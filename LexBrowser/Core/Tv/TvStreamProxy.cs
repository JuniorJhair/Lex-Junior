using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace LexBrowser.Core.Tv;

public sealed class TvProxyResult : IDisposable
{
    public int StatusCode { get; init; }
    public string ReasonPhrase { get; init; } = "OK";
    public string ContentType { get; init; } = "application/octet-stream";
    public Stream ContentStream { get; init; } = Stream.Null;
    public Dictionary<string, string> ExtraHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);
    private readonly IDisposable? _owner;

    public TvProxyResult(int statusCode, string reasonPhrase, string contentType, Stream stream, IDisposable? owner = null)
    {
        StatusCode = statusCode;
        ReasonPhrase = string.IsNullOrWhiteSpace(reasonPhrase) ? "OK" : reasonPhrase;
        ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
        ContentStream = stream;
        _owner = owner;
    }

    public void Dispose() => _owner?.Dispose();
}

public sealed record TvProbeResult(
    string Kind,
    int Status,
    string ContentType,
    string ErrorClass = "",
    string ErrorMessage = "");

/// <summary>
/// Proxy HTTP/HTTPS seguro para Lex TV: evita contenido mixto,
/// reescribe playlists HLS (.m3u8), rechaza streams cifrados no soportados,
/// reenvía Range/206, clasifica errores y limita conexiones por generación.
/// No propaga cabeceras de terceros ni referers externos.
/// </summary>
public static class TvStreamProxy
{
    public const int MaxConcurrentUpstream = 12;
    private const int MaxM3u8Bytes = 4 * 1024 * 1024;
    private const int MaxProbeBytes = 4096;
    private const int MaxProbeCacheEntries = 256;
    private const int MaxImageBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan ProbeTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ImageQueueTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ImageTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReadIdleTimeout = TimeSpan.FromSeconds(30);

    private static readonly Regex UriAttrRegex =
        new("URI=\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Lazy<HttpClient> ProxyHttp =
        new(() => TvUrlGuard.CreatePinnedHttpClient(timeout: TimeSpan.FromSeconds(45)));

    private static readonly SemaphoreSlim UpstreamGate = new(MaxConcurrentUpstream, MaxConcurrentUpstream);
    private static readonly SemaphoreSlim ImageGate = new(12, 12);
    private static int _activeUpstreamConnections;
    private static long _nextStreamToken;
    private static readonly object ActiveStreamsLock = new();
    private static readonly Dictionary<long, (string SessionId, int Gen, long CreatedTimestamp, OwnedNetworkStream Stream)> ActiveStreams = new();

    private static readonly object ProbeLock = new();
    private static readonly Dictionary<string, (DateTime ExpiresUtc, TvProbeResult Result, LinkedListNode<string> Node)> ProbeCache =
        new(StringComparer.Ordinal);
    private static readonly LinkedList<string> ProbeLru = new();

    /// <summary>Contador diagnóstico de conexiones upstream abiertas por el proxy.</summary>
    public static int ActiveUpstreamConnections => Volatile.Read(ref _activeUpstreamConnections);

    internal static HttpClient Http => ProxyHttp.Value;

    public static int ParseGen(string? sid)
    {
        if (string.IsNullOrEmpty(sid)) return 0;
        if (sid.StartsWith("g", StringComparison.OrdinalIgnoreCase))
        {
            int underscore = sid.IndexOf('_');
            string numStr = underscore > 1 ? sid.Substring(1, underscore - 1) : sid.Substring(1);
            if (int.TryParse(numStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int g) && g > 0)
                return g;
        }
        return 0;
    }

    public static object GetStats()
    {
        int currentGen = TvSessions.CurrentGen;
        int slotsInUse = ActiveUpstreamConnections;
        var sessions = new List<object>();
        long now = Stopwatch.GetTimestamp();

        lock (ActiveStreamsLock)
        {
            var groups = ActiveStreams.Values
                .GroupBy(x => x.SessionId)
                .Select(g => new
                {
                    sid = g.Key,
                    gen = g.First().Gen,
                    open = g.Count(),
                    ageMs = (long)((now - g.Min(x => x.CreatedTimestamp)) * 1000.0 / Stopwatch.Frequency)
                });
            sessions.AddRange(groups);
        }

        return new
        {
            gen = currentGen,
            slotsInUse = slotsInUse,
            sessions = sessions
        };
    }

    public static void ClearProbeCache()
    {
        lock (ProbeLock)
        {
            ProbeCache.Clear();
            ProbeLru.Clear();
        }
    }

    /// <summary>Aborta de inmediato streams pertenecientes a una sesión anterior.</summary>
    public static int AbortSession(string? sessionId)
    {
        List<OwnedNetworkStream> targets;
        lock (ActiveStreamsLock)
        {
            if (ActiveStreams.Count == 0) return 0;
            string cleanSid = sessionId ?? "";
            targets = new List<OwnedNetworkStream>();
            foreach (var kv in ActiveStreams)
            {
                if (string.IsNullOrEmpty(cleanSid) ||
                    string.Equals(kv.Value.SessionId, cleanSid, StringComparison.Ordinal))
                {
                    targets.Add(kv.Value.Stream);
                }
            }
        }

        foreach (var s in targets)
        {
            try { s.Dispose(); } catch { }
        }
        return targets.Count;
    }

    public static Stream CreateOwnedStreamForTest(
        Stream inner,
        HttpResponseMessage owner,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        int gen = ParseGen(sessionId);
        var st = TvSessions.Enter(gen) ?? TvSessions.Enter(0)!;
        st.Slots.Wait();
        UpstreamGate.Wait();
        var lease = new UpstreamLease(st);
        var genToken = cancellationToken != default ? cancellationToken : st.Cts.Token;
        return new OwnedNetworkStream(inner, owner, lease, sessionId ?? "", gen, genToken);
    }

    public static Task<TvProxyResult> FetchAsync(string? rawUrl, CancellationToken ct)
        => FetchAsync(rawUrl, null, null, ct);

    public static Task<TvProxyResult> FetchAsync(string? rawUrl, string? rangeHeader, CancellationToken ct)
        => FetchAsync(rawUrl, rangeHeader, null, ct);

    public static async Task<TvProxyResult> FetchAsync(
        string? rawUrl,
        string? rangeHeader,
        string? sessionId,
        CancellationToken ct)
    {
        string? effectiveUrl = rawUrl;
        string? connId = null;
        string? liveStreamId = null;
        string? liveRequestedExt = null;
        string streamType = "";
        bool isInternalXtream = false;
        bool isLiveStream = false;
        List<string>? xtreamCandidates = null;

        if (TvUrlGuard.IsInternalTvStreamUrl(rawUrl, out var internalUri) && internalUri != null)
        {
            isInternalXtream = true;
            var internalQ = HttpUtility.ParseQueryString(internalUri.Query);
            connId = internalQ["conn"] ?? "";
            streamType = internalUri.AbsolutePath.EndsWith("live", StringComparison.OrdinalIgnoreCase) ? "live" :
                         internalUri.AbsolutePath.EndsWith("vod", StringComparison.OrdinalIgnoreCase) ? "movie" : "series";
            isLiveStream = string.Equals(streamType, "live", StringComparison.OrdinalIgnoreCase);
            liveStreamId = internalQ["id"] ?? "";
            liveRequestedExt = internalQ["ext"] ?? "";

            var resolved = TvXtreamService.ResolveUpstreamStreamCandidates(TvXtreamStore.Shared, rawUrl);

            if (!resolved.Success || resolved.Candidates.Count == 0)
            {
                string errCls = resolved.Error != null && (resolved.Error.Contains("Credenciales", StringComparison.OrdinalIgnoreCase) || resolved.Error.Contains("Conexión", StringComparison.OrdinalIgnoreCase) || resolved.Error.Contains("Identificadores", StringComparison.OrdinalIgnoreCase))
                    ? "credentials"
                    : "blocked";
                TvDiagLog.Write("stream-resolve", $"Sin candidatos ({errCls})", $"{rawUrl ?? "<null>"} | {resolved.Error ?? "empty"}");
                return ProxyErrorResult(404, "Not Found", errCls, resolved.Error ?? "Flujo Xtream no disponible.");
            }
            xtreamCandidates = resolved.Candidates;
            effectiveUrl = xtreamCandidates[0];
        }
        else if (TvUrlGuard.IsInternalTvHostUrl(rawUrl))
        {
            TvDiagLog.Write("stream-resolve", "Ruta interna desconocida", rawUrl ?? "<null>");
            return ProxyErrorResult(404, "Not Found", "not_found", "Ruta interna de streaming no reconocida.");
        }

        if (!TvUrlGuard.IsAllowedRemoteUrl(effectiveUrl, out var targetUri) || targetUri == null)
        {
            TvDiagLog.Write("stream-blocked", "URL bloqueada por TvUrlGuard", effectiveUrl ?? rawUrl ?? "<null>");
            return ProxyErrorResult(404, "Not Found", "blocked", "URL privada o esquema no permitido.");
        }

        int gen = ParseGen(sessionId);
        var st = TvSessions.Enter(gen);
        if (st == null || st.Cts.IsCancellationRequested)
        {
            return ProxyErrorResult(410, "Gone", "stale", "Canal cambiado");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(st.Cts.Token, ct);
        var linkedToken = linkedCts.Token;

        bool gotGenSlot = false;
        try
        {
            gotGenSlot = await st.Slots.WaitAsync(1500, linkedToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (st.Cts.IsCancellationRequested)
        {
            return ProxyErrorResult(410, "Gone", "stale", "Canal cambiado");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new TvProxyResult(499, "Cancelled", "text/plain; charset=utf-8", Stream.Null);
        }

        if (!gotGenSlot)
        {
            var err = ProxyErrorResult(429, "Too Many Requests", "upstream", "Límite de conexiones simultáneas por canal alcanzado (6).");
            err.ExtraHeaders["Retry-After"] = "1";
            return err;
        }

        bool gotGlobalSlot = false;
        try
        {
            gotGlobalSlot = await UpstreamGate.WaitAsync(3500, linkedToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (st.Cts.IsCancellationRequested)
        {
            try { st.Slots.Release(); } catch { }
            return ProxyErrorResult(410, "Gone", "stale", "Canal cambiado");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { st.Slots.Release(); } catch { }
            return new TvProxyResult(499, "Cancelled", "text/plain; charset=utf-8", Stream.Null);
        }

        if (!gotGlobalSlot)
        {
            try { st.Slots.Release(); } catch { }
            var err = ProxyErrorResult(429, "Too Many Requests", "upstream", "Límite global de conexiones simultáneas alcanzado (12).");
            err.ExtraHeaders["Retry-After"] = "1";
            return err;
        }

        var lease = new UpstreamLease(st);
        HttpResponseMessage? resp = null;
        try
        {
            string initialUa = TvPlaybackHints.GetUserAgent(connId, targetUri.Host) ?? TvUrlGuard.PlayerUserAgent;
            Uri finalUri;
            string currentUa;
            (resp, finalUri, currentUa) = await SendWithUserAgentFallbackAsync(
                Http, targetUri, rangeHeader, connId, linkedToken, useRetry: true).ConfigureAwait(false);

            int status = (int)resp.StatusCode;
            if (resp.IsSuccessStatusCode && !string.Equals(currentUa, initialUa, StringComparison.Ordinal))
            {
                InvalidateProbe(effectiveUrl);
                InvalidateProbe(rawUrl);
            }

            // Reintento de candidatos Xtream o formato de stream si da 404
            if (status == 404)
            {
                if (isInternalXtream)
                {
                    var (altResp, altFinal, altUri, altUa) = await TryXtream404FallbackAsync(
                        xtreamCandidates, streamType, connId, liveStreamId, rangeHeader, useRetry: true, linkedToken).ConfigureAwait(false);
                    if (altResp != null && altFinal != null && altUri != null)
                    {
                        resp.Dispose();
                        resp = altResp;
                        finalUri = altFinal;
                        status = (int)altResp.StatusCode;
                        targetUri = altUri;
                        currentUa = altUa;
                        InvalidateProbe(effectiveUrl);
                        InvalidateProbe(rawUrl);
                        if (isLiveStream)
                        {
                            string matchedExt = GetLiveStreamExtension(targetUri, "");
                            if (!string.IsNullOrEmpty(matchedExt))
                                TvPlaybackHints.RememberLiveExt(connId, matchedExt);
                        }
                    }
                }
                else if (isLiveStream || LooksLikeLiveStream(targetUri))
                {
                    string currentExt = GetLiveStreamExtension(targetUri, liveRequestedExt);
                    string alternateExt = string.Equals(currentExt, "ts", StringComparison.OrdinalIgnoreCase) ? "m3u8" : "ts";
                    Uri? altUri = SwapLiveExtension(targetUri, currentExt, alternateExt);
                    if (altUri != null)
                    {
                        var (altResp, altFinal, altUa) = await SendWithUserAgentFallbackAsync(
                            Http, altUri, rangeHeader, connId, linkedToken, useRetry: true).ConfigureAwait(false);
                        if (altResp.IsSuccessStatusCode)
                        {
                            resp.Dispose();
                            resp = altResp;
                            finalUri = altFinal;
                            status = (int)altResp.StatusCode;
                            targetUri = altUri;
                            currentUa = altUa;
                            InvalidateProbe(effectiveUrl);
                            InvalidateProbe(rawUrl);
                        }
                        else
                        {
                            altResp.Dispose();
                        }
                    }
                }
            }

            if (resp.IsSuccessStatusCode && isLiveStream)
            {
                string okExt = GetLiveStreamExtension(targetUri, "");
                if (!string.IsNullOrEmpty(okExt))
                    TvPlaybackHints.RememberLiveExt(connId, okExt);
            }

            string reason = resp.ReasonPhrase ?? "OK";
            string contentType = resp.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";

            // Tratamiento de HTTP 416 (Range Not Satisfiable)
            if (status == 416)
            {
                var headers416 = BuildForwardHeaders(resp);
                resp.Dispose();
                lease.Dispose();
                var res416 = new TvProxyResult(416, "Range Not Satisfiable", contentType, Stream.Null);
                foreach (var kv in headers416)
                    res416.ExtraHeaders[kv.Key] = kv.Value;
                return res416;
            }

            if (!resp.IsSuccessStatusCode)
            {
                TvDiagLog.Write("stream-upstream", $"HTTP {status} {reason}", $"ua={currentUa} target={targetUri.AbsoluteUri} final={finalUri.AbsoluteUri}");
                resp.Dispose();
                lease.Dispose();
                return UpstreamErrorResult(status, reason, $"Error remoto {status} {reason}");
            }

            if (LooksLikeHlsManifest(finalUri, contentType))
            {
                string body = await TvUrlGuard.ReadBoundedUtf8Async(resp, MaxM3u8Bytes, linkedToken).ConfigureAwait(false);
                resp.Dispose();
                lease.Dispose();

                if (HasNonNoneEncryption(body))
                {
                    TvDiagLog.Write("stream-encrypted", "Manifiesto HLS cifrado no soportado", finalUri.AbsoluteUri);
                    return ProxyErrorResult(422, "Unprocessable Entity", "encrypted",
                        "Este método de reproducción (flujo cifrado) no está soportado.");
                }

                string rewritten = body.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase)
                    ? RewriteHlsPlaylist(body, finalUri, sessionId)
                    : body;

                byte[] bytes = Encoding.UTF8.GetBytes(rewritten);
                return new TvProxyResult(
                    status,
                    reason,
                    "application/vnd.apple.mpegurl; charset=utf-8",
                    new MemoryStream(bytes, writable: false));
            }

            var extraHeaders = BuildForwardHeaders(resp);
            var networkStream = await resp.Content.ReadAsStreamAsync(linkedToken).ConfigureAwait(false);
            var wrapped = new OwnedNetworkStream(networkStream, resp, lease, sessionId ?? "", gen, st.Cts.Token);
            var result = new TvProxyResult(status, reason, contentType, wrapped, wrapped);
            foreach (var kv in extraHeaders)
                result.ExtraHeaders[kv.Key] = kv.Value;
            return result;
        }
        catch (OperationCanceledException) when (st.Cts.IsCancellationRequested)
        {
            resp?.Dispose();
            lease.Dispose();
            return ProxyErrorResult(410, "Gone", "stale", "Canal cambiado");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            resp?.Dispose();
            lease.Dispose();
            return new TvProxyResult(499, "Cancelled", "text/plain; charset=utf-8", Stream.Null);
        }
        catch (Exception ex)
        {
            resp?.Dispose();
            lease.Dispose();
            var (cls, status, msg) = TvUrlGuard.ClassifyException(ex);
            string reason = status == 504 ? "Gateway Timeout" : "Bad Gateway";
            return ProxyErrorResult(status, reason, cls, msg);
        }
    }

    /// <summary>
    /// Descarga y sirve imágenes de logotipos o portadas bajo estricto control de origen,
    /// concurrencia máx. 6, límite de 2 MB y timeout de 8 s, validando Content-Type: image/*.
    /// </summary>
    public static Task<TvProxyResult> FetchImageAsync(string? rawUrl, CancellationToken ct)
        => FetchImageAsync(rawUrl, null, ct);

    /// <summary>
    /// Descarga y sirve imágenes de logotipos o portadas bajo estricto control de origen,
    /// concurrencia máx. 6, límite de 2 MB y timeout de 8 s, validando Content-Type: image/*.
    /// </summary>
    private static readonly string[] VodFallbackExtensions = ["m3u8", "mp4", "mkv", "ts"];

    private static async Task<(HttpResponseMessage Response, Uri FinalUri, string UsedUa)> SendWithUserAgentFallbackAsync(
        HttpClient client,
        Uri targetUri,
        string? rangeHeader,
        string? connId,
        CancellationToken ct,
        bool useRetry,
        bool retryOnServerError = false)
    {
        var candidates = TvUrlGuard.GetUserAgentCandidates(TvPlaybackHints.GetUserAgent(connId, targetUri.Host));
        HttpResponseMessage? lastResp = null;
        Uri lastFinal = targetUri;
        string lastUa = candidates[0];

        for (int i = 0; i < candidates.Count; i++)
        {
            string ua = candidates[i];
            var (resp, finalUri) = useRetry && ReferenceEquals(client, Http)
                ? await SendWithRetryAsync(targetUri, rangeHeader, ct, ua).ConfigureAwait(false)
                : await TvUrlGuard.GetWithSafeRedirectsAsync(client, targetUri, rangeHeader, ct, userAgent: ua).ConfigureAwait(false);

            lastResp = resp;
            lastFinal = finalUri;
            lastUa = ua;

            if (resp.IsSuccessStatusCode)
            {
                TvPlaybackHints.RememberUserAgent(connId, targetUri.Host, ua);
                TvPlaybackHints.RememberUserAgent(connId, finalUri.Host, ua);
                return (resp, finalUri, ua);
            }

            int code = (int)resp.StatusCode;
            bool shouldRetry = code == 401 || code == 403 || code == 404 || code == 406 ||
                               (retryOnServerError && code >= 500);
            if (!shouldRetry || i + 1 >= candidates.Count)
                return (resp, finalUri, ua);

            resp.Dispose();
        }

        return (lastResp!, lastFinal, lastUa);
    }

    public static string? SniffImageContentType(ReadOnlySpan<byte> bytes, string? currentContentType, Uri? uri = null)
    {
        if (bytes.Length == 0) return null;

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";

        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
            return "image/png";

        if (bytes.Length >= 6 && bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F' &&
            bytes[3] == '8' && (bytes[4] == '7' || bytes[4] == '9') && bytes[5] == 'a')
            return "image/gif";

        if (bytes.Length >= 12 &&
            bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' &&
            bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P')
            return "image/webp";

        if (bytes.Length >= 2 && bytes[0] == 'B' && bytes[1] == 'M')
            return "image/bmp";

        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0x01 && bytes[3] == 0x00)
            return "image/x-icon";

        if (bytes.Length >= 12 && bytes[4] == 'f' && bytes[5] == 't' && bytes[6] == 'y' && bytes[7] == 'p')
        {
            string brand = Encoding.ASCII.GetString(bytes.Slice(8, 4));
            if (brand.Equals("avif", StringComparison.OrdinalIgnoreCase) || brand.Equals("avis", StringComparison.OrdinalIgnoreCase))
                return "image/avif";
            if (brand.Equals("heic", StringComparison.OrdinalIgnoreCase) || brand.Equals("heix", StringComparison.OrdinalIgnoreCase))
                return "image/heic";
        }

        // Recortar BOM/espacios UTF-8 iniciales para detectar SVG o rechazar HTML disfrazado
        int headLen = Math.Min(bytes.Length, 512);
        string head = Encoding.UTF8.GetString(bytes[..headLen]).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (head.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
            head.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
            head.StartsWith("<head", StringComparison.OrdinalIgnoreCase) ||
            head.StartsWith("<body", StringComparison.OrdinalIgnoreCase) ||
            head.StartsWith("<script", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) ||
            (head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) &&
             head.Contains("<svg", StringComparison.OrdinalIgnoreCase)))
        {
            return "image/svg+xml";
        }

        if (!string.IsNullOrWhiteSpace(currentContentType))
        {
            string mediaType = currentContentType.Split(';', 2)[0].Trim();
            if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return mediaType;
        }

        return null;
    }

    private static async Task<(HttpResponseMessage? Resp, Uri? FinalUri, Uri? MatchedUri, string MatchedUa)> TryXtream404FallbackAsync(
        List<string>? candidates,
        string streamType,
        string? connId,
        string? liveStreamId,
        string? rangeHeader,
        bool useRetry,
        CancellationToken ct)
    {
        var tried = new HashSet<string>(StringComparer.Ordinal);
        if (candidates != null && candidates.Count > 0)
        {
            tried.Add(candidates[0]);
            for (int i = 1; i < candidates.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                string altUrl = candidates[i];
                if (!tried.Add(altUrl)) continue;
                if (!TvUrlGuard.IsAllowedRemoteUrl(altUrl, out var altUri) || altUri == null) continue;

                try
                {
                    var (altResp, altFinal, altUa) = await SendWithUserAgentFallbackAsync(
                        Http, altUri, rangeHeader, connId, ct, useRetry: useRetry).ConfigureAwait(false);
                    if (altResp.IsSuccessStatusCode)
                    {
                        TvDiagLog.Write("xtream-fallback", $"Candidato #{i} OK ({(int)altResp.StatusCode})", $"ua={altUa} url={altUri.AbsoluteUri}");
                        return (altResp, altFinal, altUri, altUa);
                    }
                    TvDiagLog.Write("xtream-fallback", $"Candidato #{i} HTTP {(int)altResp.StatusCode}", $"ua={altUa} url={altUri.AbsoluteUri}");
                    altResp.Dispose();
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    TvDiagLog.Write("xtream-fallback", $"Candidato #{i} error {ex.GetType().Name}", $"url={altUri.AbsoluteUri} | {ex.Message}");
                }
            }
        }

        if (string.Equals(streamType, "movie", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(liveStreamId) &&
            !ct.IsCancellationRequested)
        {
            string? vodInfoUrl = await TvXtreamService.Shared.TryResolveVodInfoStreamUrlAsync(
                TvXtreamStore.Shared, connId ?? "", liveStreamId, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(vodInfoUrl) &&
                TvUrlGuard.IsAllowedRemoteUrl(vodInfoUrl, out var vodInfoUri) &&
                vodInfoUri != null &&
                tried.Add(vodInfoUri.AbsoluteUri))
            {
                var (altResp, altFinal, altUa) = await SendWithUserAgentFallbackAsync(
                    Http, vodInfoUri, rangeHeader, connId, ct, useRetry: useRetry).ConfigureAwait(false);
                if (altResp.IsSuccessStatusCode)
                {
                    TvDiagLog.Write("xtream-fallback", $"get_vod_info OK ({(int)altResp.StatusCode})", $"ua={altUa} url={vodInfoUri.AbsoluteUri}");
                    return (altResp, altFinal, vodInfoUri, altUa);
                }
                TvDiagLog.Write("xtream-fallback", $"get_vod_info HTTP {(int)altResp.StatusCode}", $"ua={altUa} url={vodInfoUri.AbsoluteUri}");
                altResp.Dispose();
            }
        }

        return (null, null, null, "");
    }

    public static Task<TvProxyResult> FetchImageAsync(string? rawUrl, string? connId, CancellationToken ct)
        => FetchImageAsync(rawUrl, connId, null, ct);

    public static async Task<TvProxyResult> FetchImageAsync(string? rawUrl, string? connId, HttpClient? httpClient, CancellationToken ct)
    {
        string? normalizedUrl = rawUrl?.Trim();
        if (!string.IsNullOrEmpty(normalizedUrl) && normalizedUrl.StartsWith("//", StringComparison.Ordinal))
            normalizedUrl = "https:" + normalizedUrl;

        if (TvUrlGuard.IsInternalTvHostUrl(normalizedUrl))
        {
            TvDiagLog.Write("image-proxy", "Imagen interna no encontrada", normalizedUrl ?? "<null>");
            return ProxyErrorResult(404, "Not Found", "not_found", "Imagen interna no encontrada.");
        }

        if (!TvUrlGuard.IsAllowedRemoteUrl(normalizedUrl, out var targetUri) || targetUri == null)
        {
            TvDiagLog.Write("image-proxy", "URL de imagen bloqueada", normalizedUrl ?? "<null>");
            return ProxyErrorResult(400, "Bad Request", "blocked", "URL de imagen no permitida o privada.");
        }

        bool gotSlot = false;
        try
        {
            gotSlot = await ImageGate.WaitAsync(ImageQueueTimeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TvDiagLog.Write("image-proxy", "Timeout en cola de imágenes", targetUri.AbsoluteUri);
            return ProxyErrorResult(504, "Gateway Timeout", "timeout", "Tiempo de espera agotado en cola de imágenes.");
        }

        if (!gotSlot)
        {
            TvDiagLog.Write("image-proxy", "429 límite concurrencia imágenes", targetUri.AbsoluteUri);
            return ProxyErrorResult(429, "Too Many Requests", "upstream", "Límite de concurrencia de imágenes alcanzado.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ImageTimeout);
        var token = timeoutCts.Token;

        try
        {
            HttpClient activeClient = httpClient ?? Http;
            var (resp, finalUri, usedUa) = await SendWithUserAgentFallbackAsync(
                activeClient, targetUri, rangeHeader: null, connId, token, useRetry: false, retryOnServerError: true).ConfigureAwait(false);

            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                {
                    TvDiagLog.Write("image-proxy", $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}", $"ua={usedUa} url={targetUri.AbsoluteUri} final={finalUri.AbsoluteUri}");
                    return UpstreamErrorResult((int)resp.StatusCode, resp.ReasonPhrase ?? "Error", "Error al obtener imagen.");
                }

                if (resp.Content.Headers.ContentLength is long cl && cl > MaxImageBytes)
                {
                    TvDiagLog.Write("image-proxy", $"413 imagen excede 8MB ({cl} bytes)", finalUri.AbsoluteUri);
                    return ProxyErrorResult(413, "Payload Too Large", "blocked", "La imagen excede el límite de 8 MB.");
                }

                await using var inStream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var bounded = new BoundedReadStream(inStream, MaxImageBytes);
                var ms = new MemoryStream();
                await bounded.CopyToAsync(ms, token).ConfigureAwait(false);
                if (ms.Length == 0)
                {
                    ms.Dispose();
                    TvDiagLog.Write("image-proxy", "404 cuerpo de imagen vacío", finalUri.AbsoluteUri);
                    return ProxyErrorResult(404, "Not Found", "upstream", "La imagen está vacía.");
                }

                string? rawUpstreamCt = resp.Content.Headers.ContentType?.MediaType;
                string? detectedType = SniffImageContentType(
                    ms.GetBuffer().AsSpan(0, (int)ms.Length),
                    rawUpstreamCt,
                    finalUri);

                if (string.IsNullOrEmpty(detectedType))
                {
                    long byteCount = ms.Length;
                    ms.Dispose();
                    TvDiagLog.Write("image-proxy", "415 tipo no válido", $"ct={rawUpstreamCt ?? "<null>"} bytes={byteCount} final={finalUri.AbsoluteUri}");
                    return ProxyErrorResult(415, "Unsupported Media Type", "blocked", "El recurso no es una imagen válida.");
                }

                ms.Position = 0;

                var res = new TvProxyResult(200, "OK", detectedType, ms);
                res.ExtraHeaders["Cache-Control"] = "public, max-age=86400";
                return res;
            }
        }
        catch (Exception ex)
        {
            var (cls, status, msg) = TvUrlGuard.ClassifyException(ex);
            TvDiagLog.Write("image-proxy", $"Excepción {ex.GetType().Name} ({status})", $"{targetUri.AbsoluteUri} | {ex.Message}");
            return ProxyErrorResult(status, status == 504 ? "Gateway Timeout" : "Bad Gateway", cls, msg);
        }
        finally
        {
            try { ImageGate.Release(); } catch { }
        }
    }

    /// <summary>
    /// Sondea hasta 4 KB (máx. 8 s) para clasificar el formato real del stream sin descargar el video completo.
    /// Cachea el resultado durante 10 minutos en una LRU de hasta 256 entradas.
    /// No almacena en caché respuestas 404, 403 ni fallos de credenciales.
    /// </summary>
    public static async Task<TvProbeResult> ProbeAsync(string? rawUrl, CancellationToken ct)
    {
        string? targetToProbe = rawUrl;
        string? connId = null;
        string? liveStreamId = null;
        string? liveRequestedExt = null;
        string streamType = "";
        bool isInternalXtream = false;
        bool isLiveStream = false;
        List<string>? xtreamCandidates = null;

        if (TvUrlGuard.IsInternalTvStreamUrl(rawUrl, out var internalUri) && internalUri != null)
        {
            isInternalXtream = true;
            var internalQ = HttpUtility.ParseQueryString(internalUri.Query);
            connId = internalQ["conn"] ?? "";
            streamType = internalUri.AbsolutePath.EndsWith("live", StringComparison.OrdinalIgnoreCase) ? "live" :
                         internalUri.AbsolutePath.EndsWith("vod", StringComparison.OrdinalIgnoreCase) ? "movie" : "series";
            isLiveStream = string.Equals(streamType, "live", StringComparison.OrdinalIgnoreCase);
            liveStreamId = internalQ["id"] ?? "";
            liveRequestedExt = internalQ["ext"] ?? "";

            var resolved = TvXtreamService.ResolveUpstreamStreamCandidates(TvXtreamStore.Shared, rawUrl);

            if (!resolved.Success || resolved.Candidates.Count == 0)
            {
                string errCls = resolved.Error != null && (resolved.Error.Contains("Credenciales", StringComparison.OrdinalIgnoreCase) || resolved.Error.Contains("Conexión", StringComparison.OrdinalIgnoreCase) || resolved.Error.Contains("Identificadores", StringComparison.OrdinalIgnoreCase))
                    ? "credentials"
                    : "blocked";
                TvDiagLog.Write("probe-resolve", $"Sin candidatos ({errCls})", $"{rawUrl ?? "<null>"} | {resolved.Error ?? "empty"}");
                // REQUIREMENT 4: No cachear fallos de credenciales en ProbeCache
                return new TvProbeResult("unknown", 404, "", errCls, resolved.Error ?? "Flujo Xtream no disponible.");
            }
            xtreamCandidates = resolved.Candidates;
            targetToProbe = xtreamCandidates[0];
        }
        else if (TvUrlGuard.IsInternalTvHostUrl(rawUrl))
        {
            TvDiagLog.Write("probe-resolve", "Ruta interna desconocida", rawUrl ?? "<null>");
            return new TvProbeResult("unknown", 404, "", "not_found", "Ruta interna de streaming no reconocida.");
        }

        if (!TvUrlGuard.IsAllowedRemoteUrl(targetToProbe, out var targetUri) || targetUri == null)
        {
            TvDiagLog.Write("probe-blocked", "URL bloqueada por TvUrlGuard", targetToProbe ?? rawUrl ?? "<null>");
            return new TvProbeResult("unknown", 502, "", "blocked", "URL privada o no permitida.");
        }

        string cacheKey = targetUri.AbsoluteUri;
        if (TryGetCachedProbe(cacheKey, out var cached))
            return cached;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ProbeTimeout);

        try
        {
            var (resp, finalUri, _) = await SendWithUserAgentFallbackAsync(
                Http, targetUri, rangeHeader: null, connId, timeoutCts.Token, useRetry: false).ConfigureAwait(false);

            int status = (int)resp.StatusCode;

            // Reintento de candidatos Xtream o formato en 404
            if (status == 404)
            {
                if (isInternalXtream)
                {
                    var (altResp, altFinal, altUri, _) = await TryXtream404FallbackAsync(
                        xtreamCandidates, streamType, connId, liveStreamId, rangeHeader: null, useRetry: false, timeoutCts.Token).ConfigureAwait(false);
                    if (altResp != null && altFinal != null && altUri != null)
                    {
                        resp.Dispose();
                        resp = altResp;
                        finalUri = altFinal;
                        status = (int)altResp.StatusCode;
                        targetUri = altUri;
                        cacheKey = altUri.AbsoluteUri;
                        if (isLiveStream)
                        {
                            string matchedExt = GetLiveStreamExtension(targetUri, "");
                            if (!string.IsNullOrEmpty(matchedExt))
                                TvPlaybackHints.RememberLiveExt(connId, matchedExt);
                        }
                    }
                }
                else if (isLiveStream || LooksLikeLiveStream(targetUri))
                {
                    string currentExt = GetLiveStreamExtension(targetUri, liveRequestedExt);
                    string alternateExt = string.Equals(currentExt, "ts", StringComparison.OrdinalIgnoreCase) ? "m3u8" : "ts";
                    Uri? altUri = SwapLiveExtension(targetUri, currentExt, alternateExt);
                    if (altUri != null)
                    {
                        var (altResp, altFinal, _) = await SendWithUserAgentFallbackAsync(
                            Http, altUri, rangeHeader: null, connId, timeoutCts.Token, useRetry: false).ConfigureAwait(false);
                        if (altResp.IsSuccessStatusCode)
                        {
                            resp.Dispose();
                            resp = altResp;
                            finalUri = altFinal;
                            status = (int)altResp.StatusCode;
                            targetUri = altUri;
                            cacheKey = altUri.AbsoluteUri;
                        }
                        else
                        {
                            altResp.Dispose();
                        }
                    }
                }
            }

            using (resp)
            {
                string contentType = TvUrlGuard.SanitizeHeaderValue(resp.Content.Headers.ContentType?.ToString(), 120);

                if (!resp.IsSuccessStatusCode)
                {
                    TvDiagLog.Write("probe-upstream", $"HTTP {status}", $"ct={contentType} target={targetUri.AbsoluteUri} final={finalUri.AbsoluteUri}");
                    var fail = new TvProbeResult("unknown", status, contentType, "upstream", $"HTTP {status}");
                    // REQUIREMENT 4: No cachear en ProbeCache los fallos 404/403
                    if (status != 403 && status != 404)
                    {
                        StoreCachedProbe(cacheKey, fail);
                    }
                    return fail;
                }

                if (isLiveStream)
                {
                    TvPlaybackHints.RememberLiveExt(connId, GetLiveStreamExtension(targetUri, liveRequestedExt));
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
                byte[] buf = new byte[MaxProbeBytes];
                int total = 0;
                while (total < buf.Length)
                {
                    int read = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), timeoutCts.Token).ConfigureAwait(false);
                    if (read <= 0) break;
                    total += read;
                    if (total >= 1024) break;
                }

                string previewText = Encoding.UTF8.GetString(buf, 0, total);
                if (HasNonNoneEncryption(previewText))
                {
                    var encFail = new TvProbeResult("unknown", 422, contentType, "encrypted",
                        "Este método de reproducción (flujo cifrado) no está soportado.");
                    StoreCachedProbe(cacheKey, encFail);
                    return encFail;
                }

                var kind = TvStreamSniffer.Classify(buf.AsSpan(0, total));
                if (kind == TvStreamKind.Unknown && LooksLikeHlsManifest(finalUri, contentType))
                    kind = TvStreamKind.Hls;

                var result = new TvProbeResult(TvStreamSniffer.ToApiName(kind), status, contentType);
                StoreCachedProbe(cacheKey, result);
                return result;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new TvProbeResult("unknown", 499, "", "upstream", "Cancelado.");
        }
        catch (Exception ex)
        {
            var (cls, status, msg) = TvUrlGuard.ClassifyException(ex);
            TvDiagLog.Write("probe-error", $"Excepción {ex.GetType().Name} ({status})", $"{targetUri.AbsoluteUri} | {ex.Message}");
            return new TvProbeResult("unknown", status, "", cls, msg);
        }
    }

    /// <summary>
    /// Detecta si un manifiesto HLS contiene etiquetas de cifrado no nulo (#EXT-X-KEY o #EXT-X-SESSION-KEY con METHOD != NONE).
    /// </summary>
    public static bool HasNonNoneEncryption(string? manifest)
    {
        if (string.IsNullOrEmpty(manifest)) return false;
        foreach (string raw in manifest.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("#EXT-X-SESSION-KEY:", StringComparison.OrdinalIgnoreCase))
            {
                int idx = line.IndexOf("METHOD=", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    string methodPart = line.Substring(idx + 7).TrimStart();
                    int comma = methodPart.IndexOf(',');
                    string method = (comma >= 0 ? methodPart.Substring(0, comma) : methodPart).Trim().Trim('"');
                    if (!method.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                else
                {
                    return true;
                }
            }
        }
        return false;
    }

    public static Dictionary<string, string> BuildForwardHeaders(HttpResponseMessage resp)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (resp.Content.Headers.ContentLength is long len && len >= 0)
            headers["Content-Length"] = len.ToString(CultureInfo.InvariantCulture);

        if (resp.Content.Headers.ContentRange != null)
            headers["Content-Range"] = TvUrlGuard.SanitizeHeaderValue(resp.Content.Headers.ContentRange.ToString(), 120);

        if (resp.Headers.AcceptRanges.Count > 0)
        {
            headers["Accept-Ranges"] = TvUrlGuard.SanitizeHeaderValue(string.Join(", ", resp.Headers.AcceptRanges), 60);
        }
        else if (resp.StatusCode == HttpStatusCode.PartialContent || resp.Content.Headers.ContentRange != null)
        {
            headers["Accept-Ranges"] = "bytes";
        }

        return headers;
    }

    public static void InvalidateProbe(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        lock (ProbeLock)
        {
            if (ProbeCache.TryGetValue(url, out var entry))
            {
                ProbeLru.Remove(entry.Node);
                ProbeCache.Remove(url);
            }
        }
    }

    private static bool LooksLikeLiveStream(Uri uri)
    {
        string path = uri.AbsolutePath.ToLowerInvariant();
        return path.Contains("/live/") || path.EndsWith(".ts") || path.EndsWith(".m3u8");
    }

    private static string GetLiveStreamExtension(Uri uri, string? hint)
    {
        if (!string.IsNullOrWhiteSpace(hint))
        {
            string clean = hint.TrimStart('.').ToLowerInvariant();
            if (clean == "m3u8" || clean == "ts") return clean;
        }
        string path = uri.AbsolutePath.ToLowerInvariant();
        if (path.EndsWith(".m3u8")) return "m3u8";
        return "ts";
    }

    private static Uri? SwapLiveExtension(Uri uri, string currentExt, string targetExt)
    {
        try
        {
            string currentDot = "." + currentExt.TrimStart('.');
            string targetDot = "." + targetExt.TrimStart('.');
            string path = uri.AbsolutePath;
            int idx = path.LastIndexOf(currentDot, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                string newPath = path.Substring(0, idx) + targetDot + path.Substring(idx + currentDot.Length);
                var builder = new UriBuilder(uri) { Path = newPath };
                if (TvUrlGuard.IsAllowedRemoteUrl(builder.Uri.AbsoluteUri, out var safe) && safe != null)
                    return safe;
            }
        }
        catch { }
        return null;
    }

    private static async Task<(HttpResponseMessage Response, Uri FinalUri)> SendWithRetryAsync(
        Uri targetUri,
        string? rangeHeader,
        CancellationToken ct,
        string? userAgent = null)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var (resp, finalUri) = await TvUrlGuard.GetWithSafeRedirectsAsync(
                    Http, targetUri, rangeHeader, ct, userAgent: userAgent).ConfigureAwait(false);

                int code = (int)resp.StatusCode;
                if (attempt == 0 && (code == 429 || code == 503))
                {
                    int waitMs = TvUrlGuard.ParseRetryAfterMs(resp, 2000);
                    resp.Dispose();
                    await Task.Delay(waitMs, ct).ConfigureAwait(false);
                    continue;
                }

                return (resp, finalUri);
            }
            catch (Exception ex) when (attempt == 0 && !ct.IsCancellationRequested && IsPreFirstByteTransient(ex))
            {
                await Task.Delay(300, ct).ConfigureAwait(false);
            }
        }

        return await TvUrlGuard.GetWithSafeRedirectsAsync(
            Http, targetUri, rangeHeader, ct, userAgent: userAgent).ConfigureAwait(false);
    }

    private static bool IsPreFirstByteTransient(Exception ex)
    {
        for (Exception? cur = ex; cur != null; cur = cur.InnerException)
        {
            if (cur is TvProxyErrorException) return false;
            if (cur is TaskCanceledException || cur is TimeoutException) return true;
            if (cur is SocketException se &&
                (se.SocketErrorCode == SocketError.ConnectionReset ||
                 se.SocketErrorCode == SocketError.ConnectionAborted ||
                 se.SocketErrorCode == SocketError.TimedOut))
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryGetCachedProbe(string key, out TvProbeResult result)
    {
        lock (ProbeLock)
        {
            if (ProbeCache.TryGetValue(key, out var entry))
            {
                if (entry.ExpiresUtc > DateTime.UtcNow)
                {
                    ProbeLru.Remove(entry.Node);
                    ProbeLru.AddLast(entry.Node);
                    result = entry.Result;
                    return true;
                }
                ProbeLru.Remove(entry.Node);
                ProbeCache.Remove(key);
            }
        }
        result = default!;
        return false;
    }

    private static void StoreCachedProbe(string key, TvProbeResult result)
    {
        lock (ProbeLock)
        {
            DateTime expires = DateTime.UtcNow + ProbeTtl;
            if (ProbeCache.TryGetValue(key, out var existing))
            {
                ProbeLru.Remove(existing.Node);
                ProbeLru.AddLast(existing.Node);
                ProbeCache[key] = (expires, result, existing.Node);
                return;
            }

            while (ProbeCache.Count >= MaxProbeCacheEntries && ProbeLru.First != null)
            {
                string oldest = ProbeLru.First.Value;
                ProbeLru.RemoveFirst();
                ProbeCache.Remove(oldest);
            }

            var node = ProbeLru.AddLast(key);
            ProbeCache[key] = (expires, result, node);
        }
    }

    /// <summary>
    /// Convierte URLs dentro de un manifiesto .m3u8 hacia /api/proxy,
    /// descartando destinos privados o no-HTTP. No inyecta ni propaga cabeceras de terceros.
    /// </summary>
    public static string RewriteHlsPlaylist(string content, Uri baseUri, string? sessionId = null)
    {
        if (string.IsNullOrEmpty(content)) return "";
        string suffix = BuildSessionQuerySuffix(sessionId);
        var sb = new StringBuilder(content.Length + 512);

        foreach (string raw in content.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            string trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                sb.Append('\n');
                continue;
            }

            if (trimmed[0] == '#')
            {
                if (trimmed.Contains("URI=\"", StringComparison.Ordinal))
                {
                    line = UriAttrRegex.Replace(line, m =>
                    {
                        string? proxied = ToProxyPath(m.Groups[1].Value, baseUri, suffix);
                        return proxied != null ? $"URI=\"{proxied}\"" : m.Value;
                    });
                }
                sb.Append(line).Append('\n');
                continue;
            }

            string? urlProxied = ToProxyPath(trimmed, baseUri, suffix);
            sb.Append(urlProxied ?? trimmed).Append('\n');
        }

        return sb.ToString();
    }

    public static string BuildProxyPath(string targetUrl, string? sessionId = null)
        => $"/api/proxy?u={Uri.EscapeDataString(targetUrl)}{BuildSessionQuerySuffix(sessionId)}";

    private static string BuildSessionQuerySuffix(string? sessionId)
    {
        string sid = TvUrlGuard.SanitizeHeaderValue(sessionId, 24);
        return sid.Length > 0 ? $"&sid={Uri.EscapeDataString(sid)}" : "";
    }

    private static string? ToProxyPath(string rawRef, Uri baseUri, string suffix)
    {
        if (!Uri.TryCreate(baseUri, rawRef.Trim(), out var resolved)) return null;
        if (!TvUrlGuard.IsAllowedRemoteUrl(resolved.AbsoluteUri, out var safe) || safe == null) return null;
        return $"/api/proxy?u={Uri.EscapeDataString(safe.AbsoluteUri)}{suffix}";
    }

    private static bool LooksLikeHlsManifest(Uri uri, string contentType)
    {
        if (contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)) return true;
        return uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
               uri.AbsolutePath.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase);
    }

    private static TvProxyResult ProxyErrorResult(int code, string reason, string errorClass, string message)
    {
        string cleanMsg = TvUrlGuard.SanitizeHeaderValue(message, 160);
        string json = JsonSerializer.Serialize(new { cls = errorClass, msg = cleanMsg });
        var res = new TvProxyResult(code, reason, "application/json; charset=utf-8",
            new MemoryStream(Encoding.UTF8.GetBytes(json), writable: false));
        res.ExtraHeaders["X-Lex-Error-Class"] = errorClass;
        return res;
    }

    private static TvProxyResult UpstreamErrorResult(int code, string reason, string message)
    {
        var res = new TvProxyResult(code, reason, "text/plain; charset=utf-8",
            new MemoryStream(Encoding.UTF8.GetBytes(message), writable: false));
        res.ExtraHeaders["X-Lex-Upstream"] = "1";
        return res;
    }

    private sealed class UpstreamLease : IDisposable
    {
        private readonly TvSessions.GenState? _genState;
        private int _released;

        public UpstreamLease(TvSessions.GenState? genState)
        {
            _genState = genState;
            Interlocked.Increment(ref _activeUpstreamConnections);
            if (_genState != null)
                Interlocked.Increment(ref _genState.Open);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Interlocked.Decrement(ref _activeUpstreamConnections);
                if (_genState != null)
                {
                    Interlocked.Decrement(ref _genState.Open);
                    try { _genState.Slots.Release(); } catch { }
                }
                try { UpstreamGate.Release(); } catch { }
            }
        }
    }

    private sealed class OwnedNetworkStream : Stream
    {
        private readonly Stream _inner;
        private readonly HttpResponseMessage _owner;
        private readonly UpstreamLease _lease;
        private readonly long _token;
        private readonly Timer _idleTimer;
        private readonly CancellationTokenRegistration _registration;
        private int _disposed;

        public OwnedNetworkStream(
            Stream inner,
            HttpResponseMessage owner,
            UpstreamLease lease,
            string sessionId,
            int gen,
            CancellationToken genToken)
        {
            _inner = inner;
            _owner = owner;
            _lease = lease;
            _token = Interlocked.Increment(ref _nextStreamToken);

            lock (ActiveStreamsLock)
                ActiveStreams[_token] = (sessionId, gen, Stopwatch.GetTimestamp(), this);

            _idleTimer = new Timer(_ => ReleaseResources(), null, ReadIdleTimeout, Timeout.InfiniteTimeSpan);

            if (genToken.CanBeCanceled)
            {
                _registration = genToken.Register(() => ReleaseResources());
            }
        }

        ~OwnedNetworkStream() => ReleaseResources();

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            BumpIdle();
            try
            {
                int n = _inner.Read(buffer, offset, count);
                if (n == 0) ReleaseResources();
                else BumpIdle();
                return n;
            }
            catch
            {
                ReleaseResources();
                throw;
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            BumpIdle();
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idleCts.CancelAfter(ReadIdleTimeout);
            try
            {
                int n = await _inner.ReadAsync(buffer, idleCts.Token).ConfigureAwait(false);
                if (n == 0) ReleaseResources();
                else BumpIdle();
                return n;
            }
            catch
            {
                ReleaseResources();
                throw;
            }
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            BumpIdle();
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idleCts.CancelAfter(ReadIdleTimeout);
            try
            {
                int n = await _inner.ReadAsync(buffer, offset, count, idleCts.Token).ConfigureAwait(false);
                if (n == 0) ReleaseResources();
                else BumpIdle();
                return n;
            }
            catch
            {
                ReleaseResources();
                throw;
            }
        }

        private void BumpIdle()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try { _idleTimer.Change(ReadIdleTimeout, Timeout.InfiniteTimeSpan); } catch { }
        }

        private void ReleaseResources()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _registration.Dispose(); } catch { }
            lock (ActiveStreamsLock)
                ActiveStreams.Remove(_token);
            try { _idleTimer.Dispose(); } catch { }
            try { _inner.Dispose(); } catch { }
            try { _owner.Dispose(); } catch { }
            _lease.Dispose();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) GC.SuppressFinalize(this);
            ReleaseResources();
            base.Dispose(disposing);
        }
    }
}

public static class TvSessions
{
    private static readonly object Lock = new();
    private static int _currentGen;
    private static readonly Dictionary<int, GenState> Gens = new();

    public sealed class GenState
    {
        public readonly int Gen;
        public readonly CancellationTokenSource Cts = new();
        public readonly SemaphoreSlim Slots;
        public int Open;
        public readonly long CreatedAtTimestamp = Stopwatch.GetTimestamp();

        public GenState(int gen)
        {
            Gen = gen;
            int slotCount = gen == 0 ? 12 : 6;
            Slots = new SemaphoreSlim(slotCount, slotCount);
        }
    }

    public static int CurrentGen
    {
        get
        {
            lock (Lock) return _currentGen;
        }
    }

    public static GenState? Enter(int gen)
    {
        lock (Lock)
        {
            if (gen > 0)
            {
                if (gen < _currentGen) return null;
                if (gen > _currentGen)
                {
                    _currentGen = gen;
                    foreach (var kv in Gens.Where(k => k.Key > 0 && k.Key < gen).ToList())
                    {
                        try { kv.Value.Cts.Cancel(); } catch { }
                        Gens.Remove(kv.Key);
                    }
                }
            }
            if (!Gens.TryGetValue(gen, out var st)) Gens[gen] = st = new GenState(gen);
            return st;
        }
    }

    public static void AbortAll()
    {
        lock (Lock)
        {
            foreach (var g in Gens.Values)
            {
                try { g.Cts.Cancel(); } catch { }
            }
            Gens.Clear();
            _currentGen = 0;
        }
    }
}
