using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace LexBrowser.Core.Tv;

/// <summary>
/// Servidor virtual https://tv.lex.local/ para Lex TV (reproductor bajo demanda de listas del propio usuario).
/// Aplica CSP unificada estricta, aislamiento de origen y 0% de costo en reposo.
/// </summary>
public static class TvStudioService
{
    public const string HostName = "tv.lex.local";
    public const string VirtualOrigin = "https://tv.lex.local/";
    public const string HomeUrl = "https://tv.lex.local/index.html";

    public const string HtmlContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; media-src 'self' blob:; connect-src 'self'; object-src 'none'; frame-ancestors 'none';";

    private const string AllowedCorsOrigin = "https://tv.lex.local";
    private const int MaxJsonBodyBytes = 24 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOutOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static string? _cachedTvFolder;

    public static void ReleaseAll()
    {
        TvSessions.AbortAll();
        TvStreamProxy.AbortSession(null);
        TvUserStateStore.Shared.ReleaseMemoryCache();
        TvVodStore.Shared.ReleaseMemoryCache();
        TvXtreamStore.Shared.ReleaseMemoryCache();
        TvStreamProxy.ClearProbeCache();
    }

    public static bool IsTvUrl(string? url)
        => !string.IsNullOrWhiteSpace(url) &&
           Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) &&
           uri.Host.Equals(HostName, StringComparison.OrdinalIgnoreCase);

    public static bool IsTvShortcut(string? rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput)) return false;
        string s = rawInput.Trim();
        return s.Equals("lex://tv", StringComparison.OrdinalIgnoreCase) ||
               s.Equals("lex:tv", StringComparison.OrdinalIgnoreCase) ||
               s.Equals(HostName, StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith(VirtualOrigin, StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("http://" + HostName, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsValidTvOriginOrReferer(string? originHeader, string? refererHeader)
    {
        bool hasOrigin = !string.IsNullOrWhiteSpace(originHeader);
        bool hasReferer = !string.IsNullOrWhiteSpace(refererHeader);
        if (!hasOrigin && !hasReferer) return false;

        if (hasOrigin)
        {
            string cleanOrigin = originHeader!.Trim().TrimEnd('/');
            if (!cleanOrigin.Equals(AllowedCorsOrigin, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (hasReferer)
        {
            string cleanReferer = refererHeader!.Trim();
            if (!cleanReferer.StartsWith(VirtualOrigin, StringComparison.OrdinalIgnoreCase) &&
                !cleanReferer.Equals(AllowedCorsOrigin, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    public static string GetTvAssetsFolder()
    {
        if (_cachedTvFolder != null && Directory.Exists(_cachedTvFolder))
            return _cachedTvFolder;

        string p1 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "tv");
        if (Directory.Exists(p1) && File.Exists(Path.Combine(p1, "index.html")))
        {
            _cachedTvFolder = p1;
            return p1;
        }

        string p2 = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Assets", "tv"));
        if (Directory.Exists(p2) && File.Exists(Path.Combine(p2, "index.html")))
        {
            _cachedTvFolder = p2;
            return p2;
        }

        return p1;
    }

    public static bool TryHandleWebResourceRequest(CoreWebView2Environment env, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)) return false;
        if (!uri.Host.Equals(HostName, StringComparison.OrdinalIgnoreCase)) return false;

        string path = uri.AbsolutePath.TrimStart('/');

        // 1. Endpoints API y proxy (requieren origen interno tv.lex.local)
        if (path.StartsWith("api/", StringComparison.OrdinalIgnoreCase))
        {
            string? originHeader = null;
            string? refererHeader = null;
            string? rangeHeader = null;
            try
            {
                if (e.Request.Headers != null)
                {
                    if (e.Request.Headers.Contains("Origin")) originHeader = e.Request.Headers.GetHeader("Origin");
                    if (e.Request.Headers.Contains("Referer")) refererHeader = e.Request.Headers.GetHeader("Referer");
                    if (e.Request.Headers.Contains("Range")) rangeHeader = e.Request.Headers.GetHeader("Range");
                }
            }
            catch { }

            if (!IsValidTvOriginOrReferer(originHeader, refererHeader))
            {
                e.Response = CreateJsonResponse(env, 403, "Forbidden", new { error = "Origen no autorizado." });
                return true;
            }

            string? requestBody = null;
            if (e.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) && e.Request.Content != null)
            {
                try
                {
                    requestBody = ReadBoundedRequestBody(e.Request.Content);
                }
                catch (InvalidOperationException ex)
                {
                    e.Response = CreateJsonResponse(env, 413, "Payload Too Large", new { error = ex.Message });
                    return true;
                }
            }

            var deferral = e.GetDeferral();
            _ = HandleApiAsync(env, e, uri, path, requestBody, rangeHeader, deferral);
            return true;
        }

        // 2. Recursos estáticos (index.html, tv.css, tv.js, legal.html, vendor/*)
        if (string.IsNullOrEmpty(path)) path = "index.html";
        if (path.Contains("..", StringComparison.Ordinal) || path.Contains(':', StringComparison.Ordinal))
        {
            e.Response = CreateTextResponse(env, 400, "Bad Request", "Ruta inválida.");
            return true;
        }

        byte[]? bytes = LoadStaticAsset(path);
        if (bytes == null)
        {
            e.Response = CreateTextResponse(env, 404, "Not Found", "Recurso no encontrado.");
            return true;
        }

        string contentType = path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ? "text/html; charset=utf-8" :
                             path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ? "application/javascript; charset=utf-8" :
                             path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ? "text/css; charset=utf-8" :
                             path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? "image/svg+xml" :
                             path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" :
                             path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) ? "image/x-icon" :
                             "application/octet-stream";

        bool isVendor = path.StartsWith("vendor/", StringComparison.OrdinalIgnoreCase);
        string cacheControl = isVendor ? "max-age=86400" : "no-cache";

        var hdr = new StringBuilder();
        hdr.Append("Content-Type: ").Append(contentType).Append("\r\n");
        hdr.Append("Access-Control-Allow-Origin: ").Append(AllowedCorsOrigin).Append("\r\n");
        hdr.Append("X-Content-Type-Options: nosniff\r\n");
        hdr.Append("Referrer-Policy: same-origin\r\n");
        hdr.Append("X-Frame-Options: DENY\r\n");
        hdr.Append("Cache-Control: ").Append(cacheControl);
        if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            hdr.Append("\r\nContent-Security-Policy: ").Append(HtmlContentSecurityPolicy);

        e.Response = env.CreateWebResourceResponse(new MemoryStream(bytes, writable: false), 200, "OK", hdr.ToString());
        return true;
    }

    private static async Task HandleApiAsync(
        CoreWebView2Environment env,
        CoreWebView2WebResourceRequestedEventArgs e,
        Uri uri,
        string path,
        string? body,
        string? rangeHeader,
        CoreWebView2Deferral deferral)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var q = HttpUtility.ParseQueryString(uri.Query);

            if (path.Equals("api/proxy", StringComparison.OrdinalIgnoreCase))
            {
                var proxyRes = await TvStreamProxy.FetchAsync(q["u"], rangeHeader, q["sid"], cts.Token);
                var hdrSb = new StringBuilder();
                hdrSb.Append("Content-Type: ").Append(proxyRes.ContentType).Append("\r\n");
                hdrSb.Append("Access-Control-Allow-Origin: ").Append(AllowedCorsOrigin).Append("\r\n");
                hdrSb.Append("Access-Control-Expose-Headers: X-Lex-Error-Class, X-Lex-Upstream, Content-Range, Accept-Ranges\r\n");
                hdrSb.Append("X-Content-Type-Options: nosniff\r\n");
                hdrSb.Append("Cache-Control: no-store");
                foreach (var kv in proxyRes.ExtraHeaders)
                {
                    hdrSb.Append("\r\n").Append(kv.Key).Append(": ").Append(kv.Value);
                }
                e.Response = env.CreateWebResourceResponse(proxyRes.ContentStream, proxyRes.StatusCode, proxyRes.ReasonPhrase, hdrSb.ToString());
                return;
            }

            if (path.Equals("api/xtream/live", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("api/xtream/vod", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("api/xtream/series", StringComparison.OrdinalIgnoreCase))
            {
                string streamType = path.EndsWith("live", StringComparison.OrdinalIgnoreCase) ? "live" :
                                    path.EndsWith("vod", StringComparison.OrdinalIgnoreCase) ? "movie" : "series";
                string connId = q["conn"] ?? "";
                string streamId = q["id"] ?? "";
                string ext = q["ext"] ?? "";

                var resolve = TvXtreamService.ResolveUpstreamStreamUrl(TvXtreamStore.Shared, connId, streamType, streamId, ext);
                if (!resolve.Success || string.IsNullOrEmpty(resolve.UpstreamUrl))
                {
                    e.Response = CreateJsonResponse(env, 404, "Not Found", new { error = resolve.Error ?? "Flujo Xtream no disponible." });
                    return;
                }

                var proxyRes = await TvStreamProxy.FetchAsync(resolve.UpstreamUrl, rangeHeader, q["sid"], cts.Token);
                var hdrSb = new StringBuilder();
                hdrSb.Append("Content-Type: ").Append(proxyRes.ContentType).Append("\r\n");
                hdrSb.Append("Access-Control-Allow-Origin: ").Append(AllowedCorsOrigin).Append("\r\n");
                hdrSb.Append("Access-Control-Expose-Headers: X-Lex-Error-Class, X-Lex-Upstream, Content-Range, Accept-Ranges\r\n");
                hdrSb.Append("X-Content-Type-Options: nosniff\r\n");
                hdrSb.Append("Cache-Control: no-store");
                foreach (var kv in proxyRes.ExtraHeaders)
                {
                    hdrSb.Append("\r\n").Append(kv.Key).Append(": ").Append(kv.Value);
                }
                e.Response = env.CreateWebResourceResponse(proxyRes.ContentStream, proxyRes.StatusCode, proxyRes.ReasonPhrase, hdrSb.ToString());
                return;
            }

            if (path.Equals("api/image", StringComparison.OrdinalIgnoreCase))
            {
                var imgRes = await TvStreamProxy.FetchImageAsync(q["u"], cts.Token);
                var hdrSb = new StringBuilder();
                hdrSb.Append("Content-Type: ").Append(imgRes.ContentType).Append("\r\n");
                hdrSb.Append("Access-Control-Allow-Origin: ").Append(AllowedCorsOrigin).Append("\r\n");
                hdrSb.Append("X-Content-Type-Options: nosniff\r\n");
                hdrSb.Append("Cache-Control: public, max-age=86400");
                foreach (var kv in imgRes.ExtraHeaders)
                {
                    hdrSb.Append("\r\n").Append(kv.Key).Append(": ").Append(kv.Value);
                }
                e.Response = env.CreateWebResourceResponse(imgRes.ContentStream, imgRes.StatusCode, imgRes.ReasonPhrase, hdrSb.ToString());
                return;
            }

            if (path.Equals("api/proxy/abort", StringComparison.OrdinalIgnoreCase))
            {
                int aborted = TvStreamProxy.AbortSession(q["sid"]);
                e.Response = CreateJsonResponse(env, 200, "OK", new { aborted });
                return;
            }

            if (path.Equals("api/proxy/stats", StringComparison.OrdinalIgnoreCase))
            {
                var stats = TvStreamProxy.GetStats();
                e.Response = CreateJsonResponse(env, 200, "OK", stats);
                return;
            }

            if (path.Equals("api/probe", StringComparison.OrdinalIgnoreCase))
            {
                var probe = await TvStreamProxy.ProbeAsync(q["u"], cts.Token);
                e.Response = CreateJsonResponse(env, 200, "OK", new
                {
                    kind = probe.Kind,
                    status = probe.Status,
                    contentType = probe.ContentType,
                    errorClass = probe.ErrorClass,
                    errorMessage = probe.ErrorMessage
                });
                return;
            }

            if (path.Equals("api/catalog", StringComparison.OrdinalIgnoreCase))
            {
                var prefs = TvUserStateStore.Shared.GetPrefsSnapshot();
                var summaries = TvUserStateStore.Shared.GetPlaylistSummaries();
                var xtreamSummaries = TvXtreamStore.Shared.GetSummaries();

                e.Response = CreateJsonResponse(env, 200, "OK", new
                {
                    channels = Array.Empty<TvChannel>(),
                    playlists = summaries,
                    xtreamConnections = xtreamSummaries,
                    prefs = new
                    {
                        favorites = prefs.Favorites,
                        deadStreams = prefs.DeadStreams,
                        acceptedTermsVersion = prefs.AcceptedTermsVersion,
                        acceptedTermsUtc = prefs.AcceptedTermsUtc
                    }
                });
                return;
            }

            if (path.Equals("api/playlist", StringComparison.OrdinalIgnoreCase))
            {
                string id = q["id"] ?? "";
                var playlist = TvUserStateStore.Shared.GetPlaylistById(id);
                if (playlist == null)
                {
                    e.Response = CreateJsonResponse(env, 404, "Not Found", new { error = "Lista no encontrada." });
                    return;
                }
                e.Response = CreateJsonResponse(env, 200, "OK", new { playlist });
                return;
            }

            if (path.Equals("api/favorites/toggle", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                string key = doc.RootElement.TryGetProperty("key", out var kEl) ? (kEl.GetString() ?? "") : "";
                var favorites = TvUserStateStore.Shared.ToggleFavorite(key);
                e.Response = CreateJsonResponse(env, 200, "OK", new { favorites });
                return;
            }

            if (path.Equals("api/playlists/add-url", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                string name = doc.RootElement.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? "") : "";
                string url = doc.RootElement.TryGetProperty("url", out var uEl) ? (uEl.GetString() ?? "") : "";
                string effectiveName = string.IsNullOrWhiteSpace(name) ? "Mi lista IPTV" : name.Trim();
                var parsed = await TvPlaylistService.Shared.ImportPlaylistDetailedFromUrlAsync(url, effectiveName, cts.Token);
                var added = TvUserStateStore.Shared.AddOrUpdatePlaylist(effectiveName, url.Trim(), parsed.Channels);
                e.Response = CreateJsonResponse(env, 200, "OK", new
                {
                    playlist = added,
                    playlists = TvUserStateStore.Shared.GetPlaylistSummaries(),
                    skippedUnsupported = parsed.SkippedUnsupportedScheme,
                    skippedPrivate = parsed.SkippedPrivate,
                    skippedInvalid = parsed.SkippedInvalid,
                    skippedDuplicate = parsed.SkippedDuplicate,
                    isStandaloneHls = parsed.IsStandaloneHlsManifest
                });
                return;
            }

            if (path.Equals("api/playlists/add-text", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                string name = doc.RootElement.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? "") : "";
                string content = doc.RootElement.TryGetProperty("content", out var cEl) ? (cEl.GetString() ?? "") : "";
                string effectiveName = string.IsNullOrWhiteSpace(name) ? "Archivo M3U" : name.Trim();
                var parsed = M3uParser.ParseDetailed(content, effectiveName);
                if (parsed.Channels.Count == 0)
                {
                    e.Response = CreateJsonResponse(env, 400, "Bad Request", new
                    {
                        error = "No se encontraron canales válidos (#EXTINF + URL http/https) en el archivo.",
                        skippedUnsupported = parsed.SkippedUnsupportedScheme,
                        skippedPrivate = parsed.SkippedPrivate,
                        skippedInvalid = parsed.SkippedInvalid
                    });
                    return;
                }
                var added = TvUserStateStore.Shared.AddOrUpdatePlaylist(effectiveName, "", parsed.Channels);
                e.Response = CreateJsonResponse(env, 200, "OK", new
                {
                    playlist = added,
                    playlists = TvUserStateStore.Shared.GetPlaylistSummaries(),
                    skippedUnsupported = parsed.SkippedUnsupportedScheme,
                    skippedPrivate = parsed.SkippedPrivate,
                    skippedInvalid = parsed.SkippedInvalid,
                    skippedDuplicate = parsed.SkippedDuplicate
                });
                return;
            }

            if (path.Equals("api/playlists/remove", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                string id = doc.RootElement.TryGetProperty("id", out var idEl) ? (idEl.GetString() ?? "") : "";
                TvUserStateStore.Shared.RemovePlaylist(id);
                e.Response = CreateJsonResponse(env, 200, "OK", new { playlists = TvUserStateStore.Shared.GetPlaylistSummaries() });
                return;
            }

            if (path.Equals("api/vod/library", StringComparison.OrdinalIgnoreCase))
            {
                var items = TvVodStore.Shared.GetLibrarySnapshot();
                var progress = TvVodStore.Shared.GetProgressSnapshot();
                e.Response = CreateJsonResponse(env, 200, "OK", new { items, progress });
                return;
            }

            if (path.Equals("api/vod/import-url", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                string url = doc.RootElement.TryGetProperty("url", out var uEl) ? (uEl.GetString() ?? "") : "";
                string group = doc.RootElement.TryGetProperty("group", out var gEl) ? (gEl.GetString() ?? "Películas") : "Películas";
                var res = await TvVodService.Shared.ImportFromUrlAsync(url, group, cts.Token);
                e.Response = CreateJsonResponse(env, 200, "OK", new
                {
                    result = res,
                    libraryCount = TvVodStore.Shared.GetLibrarySnapshot().Count
                });
                return;
            }

            if (path.Equals("api/vod/import-text", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                string content = doc.RootElement.TryGetProperty("content", out var cEl) ? (cEl.GetString() ?? "") : "";
                string group = doc.RootElement.TryGetProperty("group", out var gEl) ? (gEl.GetString() ?? "Películas") : "Películas";
                var res = TvVodService.Shared.ImportFromText(content, group);
                e.Response = CreateJsonResponse(env, 200, "OK", new
                {
                    result = res,
                    libraryCount = TvVodStore.Shared.GetLibrarySnapshot().Count
                });
                return;
            }

            if (path.Equals("api/vod/progress", StringComparison.OrdinalIgnoreCase))
            {
                if (e.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                    string url = doc.RootElement.TryGetProperty("url", out var uEl) ? (uEl.GetString() ?? "") : "";
                    double pos = doc.RootElement.TryGetProperty("positionSec", out var pEl) ? pEl.GetDouble() : 0.0;
                    double dur = doc.RootElement.TryGetProperty("durationSec", out var dEl) ? dEl.GetDouble() : 0.0;
                    TvVodStore.Shared.SaveProgress(url, pos, dur);
                    e.Response = CreateJsonResponse(env, 200, "OK", new { ok = true });
                    return;
                }
                else
                {
                    string url = q["url"] ?? "";
                    var prog = TvVodStore.Shared.GetProgressForUrl(url);
                    e.Response = CreateJsonResponse(env, 200, "OK", new { progress = prog });
                    return;
                }
            }

            if (path.Equals("api/vod/progress/clear", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                string url = doc.RootElement.TryGetProperty("url", out var uEl) ? (uEl.GetString() ?? "") : "";
                TvVodStore.Shared.ClearProgress(url);
                e.Response = CreateJsonResponse(env, 200, "OK", new { ok = true });
                return;
            }

            if (path.Equals("api/vod/remove", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                string id = doc.RootElement.TryGetProperty("id", out var idEl) ? (idEl.GetString() ?? "") : "";
                bool removed = TvVodStore.Shared.RemoveItem(id);
                e.Response = CreateJsonResponse(env, 200, "OK", new
                {
                    ok = removed,
                    libraryCount = TvVodStore.Shared.GetLibrarySnapshot().Count
                });
                return;
            }

            if (path.Equals("api/vod/clear", StringComparison.OrdinalIgnoreCase))
            {
                TvVodStore.Shared.ClearLibrary();
                e.Response = CreateJsonResponse(env, 200, "OK", new { ok = true, libraryCount = 0 });
                return;
            }

            if (path.Equals("api/jellyfin/test", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                string serverUrl = doc.RootElement.TryGetProperty("serverUrl", out var sEl) ? (sEl.GetString() ?? "") : "";
                string apiKey = doc.RootElement.TryGetProperty("apiKey", out var aEl) ? (aEl.GetString() ?? "") : "";
                bool ok = await TvJellyfinService.Shared.TestConnectionAsync(serverUrl, apiKey, cts.Token);
                e.Response = CreateJsonResponse(env, 200, "OK", new { ok });
                return;
            }

            if (path.Equals("api/jellyfin/import", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                string serverUrl = doc.RootElement.TryGetProperty("serverUrl", out var sEl) ? (sEl.GetString() ?? "") : "";
                string apiKey = doc.RootElement.TryGetProperty("apiKey", out var aEl) ? (aEl.GetString() ?? "") : "";
                string userId = doc.RootElement.TryGetProperty("userId", out var uEl) ? (uEl.GetString() ?? "") : "";
                var items = await TvJellyfinService.Shared.FetchItemsAsync(serverUrl, apiKey, userId, cts.Token);
                int added = TvVodStore.Shared.ImportItems(items);
                e.Response = CreateJsonResponse(env, 200, "OK", new
                {
                    addedCount = added,
                    totalFound = items.Count,
                    libraryCount = TvVodStore.Shared.GetLibrarySnapshot().Count
                });
                return;
            }

            if (path.Equals("api/xtream/test", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                string serverUrl = doc.RootElement.TryGetProperty("serverUrl", out var sEl) ? (sEl.GetString() ?? "") : "";
                string username = doc.RootElement.TryGetProperty("username", out var uEl) ? (uEl.GetString() ?? "") : "";
                string password = doc.RootElement.TryGetProperty("password", out var pEl) ? (pEl.GetString() ?? "") : "";

                var auth = await TvXtreamService.Shared.AuthenticateAsync(serverUrl, username, password, cts.Token);
                e.Response = CreateJsonResponse(env, 200, "OK", new
                {
                    success = auth.Success,
                    isHttpWarning = auth.IsHttpWarning,
                    errorMessage = auth.ErrorMessage,
                    userInfo = auth.UserInfo != null ? new
                    {
                        status = auth.UserInfo.Status,
                        expDate = auth.UserInfo.ExpDate,
                        activeCons = auth.UserInfo.ActiveCons,
                        maxCons = auth.UserInfo.MaxCons
                    } : null,
                    serverInfo = auth.ServerInfo != null ? new
                    {
                        url = auth.ServerInfo.Url,
                        protocol = auth.ServerInfo.ServerProtocol,
                        timezone = auth.ServerInfo.Timezone
                    } : null
                });
                return;
            }

            if (path.Equals("api/xtream/connect", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                bool confirmedAuth = doc.RootElement.TryGetProperty("confirmedAuthorized", out var caEl) && caEl.GetBoolean();
                if (!confirmedAuth)
                {
                    e.Response = CreateJsonResponse(env, 400, "Bad Request", new
                    {
                        error = "Debes confirmar que tienes autorización para acceder a los contenidos de esta fuente."
                    });
                    return;
                }

                string name = doc.RootElement.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? "") : "";
                string serverUrl = doc.RootElement.TryGetProperty("serverUrl", out var sEl) ? (sEl.GetString() ?? "") : "";
                string username = doc.RootElement.TryGetProperty("username", out var uEl) ? (uEl.GetString() ?? "") : "";
                string password = doc.RootElement.TryGetProperty("password", out var pEl) ? (pEl.GetString() ?? "") : "";
                bool saveCreds = doc.RootElement.TryGetProperty("saveCredentials", out var scEl) && scEl.GetBoolean();
                bool syncVod = doc.RootElement.TryGetProperty("syncVod", out var svEl) && svEl.GetBoolean();

                var auth = await TvXtreamService.Shared.AuthenticateAsync(serverUrl, username, password, cts.Token);
                if (!auth.Success)
                {
                    e.Response = CreateJsonResponse(env, 400, "Bad Request", new
                    {
                        error = auth.ErrorMessage ?? "No se pudo autenticar con el servidor Xtream."
                    });
                    return;
                }

                string connId = "xc_" + Guid.NewGuid().ToString("N")[..10];
                string cleanName = string.IsNullOrWhiteSpace(name) ? "Mi IPTV Xtream" : name.Trim();

                // 1. Obtener canales en vivo
                var channels = await TvXtreamService.Shared.FetchLiveChannelsAsync(serverUrl, username, password, connId, cleanName, cts.Token);

                // 2. Registrar en TvUserStateStore como playlist para que aparezca en el selector
                TvUserStateStore.Shared.AddOrUpdatePlaylist(cleanName, $"xtream://{connId}", channels);

                // 3. Importar VOD y Series si se solicitó
                int vodCount = 0;
                int seriesCount = 0;
                if (syncVod)
                {
                    var movies = await TvXtreamService.Shared.FetchVodMoviesAsync(serverUrl, username, password, connId, cts.Token);
                    vodCount = movies.Count;
                    if (movies.Count > 0) TvVodStore.Shared.ImportItems(movies);

                    var series = await TvXtreamService.Shared.FetchSeriesAsync(serverUrl, username, password, connId, cts.Token);
                    seriesCount = series.Count;
                    if (series.Count > 0) TvVodStore.Shared.ImportItems(series);
                }

                // 4. Guardar conexión en TvXtreamStore
                var conn = new TvXtreamConnection
                {
                    Id = connId,
                    Name = cleanName,
                    ServerUrl = serverUrl.Trim().TrimEnd('/'),
                    AddedUtc = DateTime.UtcNow,
                    SaveCredentials = saveCreds,
                    LiveCount = channels.Count,
                    VodCount = vodCount,
                    SeriesCount = seriesCount,
                    ServerStatus = auth.UserInfo?.Status,
                    ExpirationDate = auth.UserInfo?.ExpDate
                };
                TvXtreamStore.Shared.AddOrUpdateConnection(conn, username.Trim(), password.Trim());

                e.Response = CreateJsonResponse(env, 200, "OK", new
                {
                    ok = true,
                    connectionId = connId,
                    name = cleanName,
                    liveCount = channels.Count,
                    vodCount,
                    seriesCount,
                    saveCredentials = saveCreds
                });
                return;
            }

            if (path.Equals("api/xtream/connections", StringComparison.OrdinalIgnoreCase))
            {
                var summaries = TvXtreamStore.Shared.GetSummaries();
                e.Response = CreateJsonResponse(env, 200, "OK", new { connections = summaries });
                return;
            }

            if (path.Equals("api/xtream/remove", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                string id = doc.RootElement.TryGetProperty("id", out var iEl) ? (iEl.GetString() ?? "") : "";

                bool removed = TvXtreamStore.Shared.RemoveConnection(id);
                if (removed)
                {
                    var playlists = TvUserStateStore.Shared.GetPlaylistSummaries();
                    var match = playlists.FirstOrDefault(p => string.Equals(p.SourceUrl, $"xtream://{id}", StringComparison.OrdinalIgnoreCase));
                    if (match != null) TvUserStateStore.Shared.RemovePlaylist(match.Id);

                    var vodSnap = TvVodStore.Shared.GetLibrarySnapshot();
                    foreach (var v in vodSnap)
                    {
                        if (v.Id.StartsWith($"xc_vod_{id}_") || v.Id.StartsWith($"xc_series_{id}_"))
                            TvVodStore.Shared.RemoveItem(v.Id);
                    }
                }

                e.Response = CreateJsonResponse(env, 200, "OK", new { ok = removed });
                return;
            }

            if (path.Equals("api/dead/mark", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                string url = doc.RootElement.TryGetProperty("url", out var uEl) ? (uEl.GetString() ?? "") : "";
                string reason = doc.RootElement.TryGetProperty("reason", out var rEl) ? (rEl.GetString() ?? "") : "";
                string kind = doc.RootElement.TryGetProperty("kind", out var kEl) ? (kEl.GetString() ?? "dead") : "dead";
                TvUserStateStore.Shared.MarkFailure(url, kind, reason);
                e.Response = CreateJsonResponse(env, 200, "OK", new { deadStreams = TvUserStateStore.Shared.GetPrefsSnapshot().DeadStreams });
                return;
            }

            if (path.Equals("api/dead/clear", StringComparison.OrdinalIgnoreCase))
            {
                string url = "";
                if (!string.IsNullOrWhiteSpace(body))
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("url", out var uEl)) url = uEl.GetString() ?? "";
                }
                if (url.Length > 0) TvUserStateStore.Shared.UnmarkDead(url);
                else TvUserStateStore.Shared.ClearDead();

                e.Response = CreateJsonResponse(env, 200, "OK", new { deadStreams = TvUserStateStore.Shared.GetPrefsSnapshot().DeadStreams });
                return;
            }

            if (path.Equals("api/terms/status", StringComparison.OrdinalIgnoreCase))
            {
                bool accepted = TvUserStateStore.Shared.HasAcceptedTerms(1);
                var prefs = TvUserStateStore.Shared.GetPrefsSnapshot();
                e.Response = CreateJsonResponse(env, 200, "OK", new
                {
                    accepted,
                    version = prefs.AcceptedTermsVersion,
                    acceptedUtc = prefs.AcceptedTermsUtc
                });
                return;
            }

            if (path.Equals("api/terms/accept", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                int version = doc.RootElement.TryGetProperty("version", out var vEl) ? vEl.GetInt32() : 1;
                TvUserStateStore.Shared.AcceptTerms(version);
                e.Response = CreateJsonResponse(env, 200, "OK", new { ok = true, version });
                return;
            }

            if (path.Equals("api/cache/clear", StringComparison.OrdinalIgnoreCase))
            {
                TvStreamProxy.ClearProbeCache();
                TvUserStateStore.Shared.ClearDead();
                e.Response = CreateJsonResponse(env, 200, "OK", new { ok = true });
                return;
            }

            if (path.Equals("api/open-external", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                string rawUrl = doc.RootElement.TryGetProperty("url", out var uEl) ? (uEl.GetString() ?? "") : "";
                if (!TvUrlGuard.IsAllowedRemoteUrl(rawUrl, out var safeUri) || safeUri == null)
                {
                    e.Response = CreateJsonResponse(env, 400, "Bad Request", new { error = "URL no permitida para apertura externa." });
                    return;
                }

                bool launched = TryLaunchVlc(safeUri);
                e.Response = CreateJsonResponse(env, 200, "OK", new { launched, url = safeUri.AbsoluteUri });
                return;
            }

            if (path.Equals("api/diag/save", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(body) || Encoding.UTF8.GetByteCount(body) > 2 * 1024 * 1024)
                {
                    e.Response = CreateJsonResponse(env, 400, "Bad Request", new { error = "Reporte de diagnóstico inválido o mayor a 2 MB." });
                    return;
                }

                string dir = TvUserStateStore.GetDefaultDirectory();
                Directory.CreateDirectory(dir);
                string fileName = $"tv_diag_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json";
                string fullPath = Path.Combine(dir, fileName);
                File.WriteAllText(fullPath, body);
                e.Response = CreateJsonResponse(env, 200, "OK", new { path = fullPath });
                return;
            }

            e.Response = CreateJsonResponse(env, 404, "Not Found", new { error = "Endpoint no encontrado." });
        }
        catch (Exception ex)
        {
            e.Response = CreateJsonResponse(env, 400, "Bad Request", new { error = TvUrlGuard.SanitizeHeaderValue(ex.Message, 200) });
        }
        finally
        {
            try { deferral.Complete(); } catch { }
        }
    }

    private static string ReadBoundedRequestBody(Stream content)
    {
        using var ms = new MemoryStream();
        byte[] buf = new byte[16384];
        int total = 0;
        while (true)
        {
            int read = content.Read(buf, 0, buf.Length);
            if (read <= 0) break;
            total += read;
            if (total > MaxJsonBodyBytes)
                throw new InvalidOperationException("El cuerpo de la petición excede el tamaño máximo.");
            ms.Write(buf, 0, read);
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    public static byte[]? LoadStaticAsset(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string cleanPath = path.Trim().TrimStart('/', '\\').Replace('\\', '/');
        if (cleanPath.Contains("..", StringComparison.Ordinal) || cleanPath.Contains(':', StringComparison.Ordinal))
            return null;

        try
        {
            string fullFolder = Path.GetFullPath(GetTvAssetsFolder())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string diskFile = Path.GetFullPath(Path.Combine(fullFolder, cleanPath.Replace('/', Path.DirectorySeparatorChar)));
            if (diskFile.StartsWith(fullFolder, StringComparison.OrdinalIgnoreCase) && File.Exists(diskFile))
                return File.ReadAllBytes(diskFile);
        }
        catch { }

        string resName = $"LexBrowser.Assets.tv.{cleanPath.Replace('/', '.')}";
        using var stream = typeof(TvStudioService).Assembly.GetManifestResourceStream(resName);
        if (stream == null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static bool TryLaunchVlc(Uri safeUri)
    {
        try
        {
            string[] candidates =
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VideoLAN", "VLC", "vlc.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VideoLAN", "VLC", "vlc.exe")
            ];

            foreach (string exe in candidates)
            {
                if (File.Exists(exe))
                {
                    var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
                    psi.ArgumentList.Add(safeUri.AbsoluteUri);
                    Process.Start(psi);
                    return true;
                }
            }

            using var vlcScheme = Registry.ClassesRoot.OpenSubKey("vlc\\shell\\open\\command");
            if (vlcScheme != null)
            {
                Process.Start(new ProcessStartInfo($"vlc://{safeUri.AbsoluteUri}") { UseShellExecute = true });
                return true;
            }
        }
        catch { }
        return false;
    }

    private static CoreWebView2WebResourceResponse CreateJsonResponse(CoreWebView2Environment env, int code, string reason, object payload)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOutOptions);
        string headers =
            $"Content-Type: application/json; charset=utf-8\r\n" +
            $"Access-Control-Allow-Origin: {AllowedCorsOrigin}\r\n" +
            $"X-Content-Type-Options: nosniff\r\n" +
            $"Referrer-Policy: same-origin\r\n" +
            $"X-Frame-Options: DENY\r\n" +
            $"Cache-Control: no-store";
        return env.CreateWebResourceResponse(new MemoryStream(bytes, writable: false), code, reason, headers);
    }

    private static CoreWebView2WebResourceResponse CreateTextResponse(CoreWebView2Environment env, int code, string reason, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        string headers =
            $"Content-Type: text/plain; charset=utf-8\r\n" +
            $"Access-Control-Allow-Origin: {AllowedCorsOrigin}\r\n" +
            $"X-Content-Type-Options: nosniff\r\n" +
            $"Referrer-Policy: same-origin\r\n" +
            $"X-Frame-Options: DENY\r\n" +
            $"Cache-Control: no-store";
        return env.CreateWebResourceResponse(new MemoryStream(bytes, writable: false), code, reason, headers);
    }
}
