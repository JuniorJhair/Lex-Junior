using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

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
    private const int MaxImageBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan ProbeTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ImageTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ReadIdleTimeout = TimeSpan.FromSeconds(30);

    private static readonly Regex UriAttrRegex =
        new("URI=\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Lazy<HttpClient> ProxyHttp =
        new(() => TvUrlGuard.CreatePinnedHttpClient(timeout: TimeSpan.FromSeconds(45)));

    private static readonly SemaphoreSlim UpstreamGate = new(MaxConcurrentUpstream, MaxConcurrentUpstream);
    private static readonly SemaphoreSlim ImageGate = new(6, 6);
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
        if (!TvUrlGuard.IsAllowedRemoteUrl(rawUrl, out var targetUri) || targetUri == null)
        {
            return ProxyErrorResult(502, "Bad Gateway", "blocked", "URL privada o esquema no permitido.");
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
            Uri finalUri;
            (resp, finalUri) = await SendWithRetryAsync(targetUri, rangeHeader, linkedToken).ConfigureAwait(false);

            int status = (int)resp.StatusCode;
            string reason = resp.ReasonPhrase ?? "OK";
            string contentType = resp.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";

            if (!resp.IsSuccessStatusCode)
            {
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
    public static async Task<TvProxyResult> FetchImageAsync(string? rawUrl, CancellationToken ct)
    {
        if (!TvUrlGuard.IsAllowedRemoteUrl(rawUrl, out var targetUri) || targetUri == null)
            return ProxyErrorResult(400, "Bad Request", "blocked", "URL de imagen no permitida o privada.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ImageTimeout);
        var token = timeoutCts.Token;

        bool gotSlot = false;
        try
        {
            gotSlot = await ImageGate.WaitAsync(ImageTimeout, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ProxyErrorResult(504, "Gateway Timeout", "timeout", "Tiempo de espera agotado en cola de imágenes.");
        }

        if (!gotSlot)
            return ProxyErrorResult(429, "Too Many Requests", "upstream", "Límite de concurrencia de imágenes alcanzado.");

        try
        {
            var (resp, _) = await TvUrlGuard.GetWithSafeRedirectsAsync(Http, targetUri, null, token).ConfigureAwait(false);
            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                    return UpstreamErrorResult((int)resp.StatusCode, resp.ReasonPhrase ?? "Error", "Error al obtener imagen.");

                string contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    return ProxyErrorResult(415, "Unsupported Media Type", "blocked", "El recurso no es una imagen válida.");
                }

                if (resp.Content.Headers.ContentLength is long cl && cl > MaxImageBytes)
                {
                    return ProxyErrorResult(413, "Payload Too Large", "blocked", "La imagen excede el límite de 2 MB.");
                }

                await using var inStream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var bounded = new BoundedReadStream(inStream, MaxImageBytes);
                var ms = new MemoryStream();
                await bounded.CopyToAsync(ms, token).ConfigureAwait(false);
                ms.Position = 0;

                var res = new TvProxyResult(200, "OK", contentType, ms);
                res.ExtraHeaders["Cache-Control"] = "public, max-age=86400";
                return res;
            }
        }
        catch (Exception ex)
        {
            var (cls, status, msg) = TvUrlGuard.ClassifyException(ex);
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
    /// </summary>
    public static async Task<TvProbeResult> ProbeAsync(string? rawUrl, CancellationToken ct)
    {
        if (!TvUrlGuard.IsAllowedRemoteUrl(rawUrl, out var targetUri) || targetUri == null)
        {
            return new TvProbeResult("unknown", 502, "", "blocked", "URL privada o no permitida.");
        }

        string cacheKey = targetUri.AbsoluteUri;
        if (TryGetCachedProbe(cacheKey, out var cached))
            return cached;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ProbeTimeout);

        try
        {
            var (resp, finalUri) = await TvUrlGuard.GetWithSafeRedirectsAsync(
                Http, targetUri, null, timeoutCts.Token).ConfigureAwait(false);

            using (resp)
            {
                int status = (int)resp.StatusCode;
                string contentType = TvUrlGuard.SanitizeHeaderValue(resp.Content.Headers.ContentType?.ToString(), 120);

                if (!resp.IsSuccessStatusCode)
                {
                    var fail = new TvProbeResult("unknown", status, contentType, "upstream", $"HTTP {status}");
                    StoreCachedProbe(cacheKey, fail);
                    return fail;
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
            headers["Content-Length"] = len.ToString();

        if (resp.Content.Headers.ContentRange != null)
            headers["Content-Range"] = TvUrlGuard.SanitizeHeaderValue(resp.Content.Headers.ContentRange.ToString(), 120);

        if (resp.Headers.AcceptRanges.Count > 0)
            headers["Accept-Ranges"] = TvUrlGuard.SanitizeHeaderValue(string.Join(", ", resp.Headers.AcceptRanges), 60);

        return headers;
    }

    private static async Task<(HttpResponseMessage Response, Uri FinalUri)> SendWithRetryAsync(
        Uri targetUri,
        string? rangeHeader,
        CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var (resp, finalUri) = await TvUrlGuard.GetWithSafeRedirectsAsync(
                    Http, targetUri, rangeHeader, ct).ConfigureAwait(false);

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
            Http, targetUri, rangeHeader, ct).ConfigureAwait(false);
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
