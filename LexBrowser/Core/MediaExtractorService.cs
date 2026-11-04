using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LexBrowser.UI.Models;

namespace LexBrowser.Core;

/// <summary>
/// Motor de extracción multimedia bajo demanda (0% CPU y 0 MB RAM cuando está apagado).
/// Incluye:
/// 1. Extractor directo para YouTube y YouTube Shorts (1080p HD, 720p HD, 480p, 360p MP4 con audio + Solo Audio M4A).
/// 2. Muxer nativo ISO-BMFF (fMP4) en C# puro para unir pistas adaptativas de video HD + audio sin depender de FFmpeg externo.
/// 3. Analizador y ensamblador de streams HLS (.m3u8) para sitios de películas, anime y redes sociales.
/// 4. Filtro estricto anti-basura para descartar segmentos sueltos, miniaturas, avatares, sonidos de UI y telemetría.
/// </summary>
public static class MediaExtractorService
{
    private const string VisionOsUserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 15_7_3) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15";
    private const string IosUserAgent = "com.google.ios.youtube/20.11.6 (iPhone16,2; U; CPU iOS 18_3_2 like Mac OS X;)";
    private const string AndroidVrUserAgent = "com.google.android.apps.youtube.vr.oculus/1.65.10 (Linux; U; Android 12L; eureka-user Build/SQ3A.220605.009.A1) gzip";
    private const string AndroidUserAgent = "com.google.android.youtube/19.44.38 (Linux; U; Android 14) gzip";
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    private static readonly HttpClient _httpClient = new(new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        AllowAutoRedirect = true,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = 16,
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, _, _, _) => true
        }
    })
    {
        Timeout = TimeSpan.FromMinutes(30)
    };

    private static readonly string[] GarbageHostKeywords =
    {
        "ytimg.com",
        "ggpht.com",
        "googleusercontent.com",
        "gstatic.com",
        "googlesyndication.com",
        "doubleclick.net",
        "google-analytics.com",
        "googletagmanager.com",
        "adservice.google",
        "play.google.com",
        "accounts.google.com",
        "jnn-pa.googleapis.com",
        "youtube.com/api/stats",
        "youtube.com/ptracking",
        "youtube.com/pagead",
        "youtube.com/youtubei/",
        "sentry.io",
        "clarity.ms",
        "hotjar.com",
        "facebook.com/tr",
        "connect.facebook.net",
        "analytics.tiktok.com",
        "ads-twitter.com"
    };

    private static readonly string[] GarbagePathKeywords =
    {
        "/favicon",
        "/sprite",
        "/avatar",
        "/profile_images/",
        "/emoji/",
        "/badges/",
        "/icons/",
        "/logo",
        "/banner",
        "/placeholder",
        "/spinner",
        "/loader",
        "/pixel",
        "/blank.",
        "/silent.",
        "/notification",
        "/telemetry",
        "/ptracking",
        "/log_event",
        "/generate_204",
        "init.mp4",
        "init.m4s",
        "seg-",
        "chunk-",
        "frag-",
        "/segment"
    };

    private static readonly Regex YouTubeIdRegex = new(
        @"(?:youtube\.com\/(?:watch\?.*v=|shorts\/|embed\/|v\/)|youtu\.be\/)([a-zA-Z0-9_-]{11})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Extrae el ID de 11 caracteres de cualquier URL de YouTube (watch, shorts, youtu.be, embed).
    /// </summary>
    public static string? TryExtractYouTubeVideoId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var match = YouTubeIdRegex.Match(url);
        if (match.Success && match.Groups.Count > 1)
        {
            return match.Groups[1].Value;
        }
        return null;
    }

    /// <summary>
    /// Determina si una URL es un recurso basura (segmentos sueltos de reproductor, miniaturas, avatares, trackers o archivos diminutos).
    /// </summary>
    public static bool IsGarbageMediaUrl(string? url, string? contentType = null, long contentLength = 0, string? categoryHint = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return true;
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return true;

        string fullLower = url.ToLowerInvariant();
        string hostLower = uri.Host.ToLowerInvariant();
        string pathLower = uri.AbsolutePath.ToLowerInvariant();
        string queryLower = uri.Query.ToLowerInvariant();
        string ctLower = (contentType ?? "").ToLowerInvariant();

        // 1. Bloquear fragmentos crudos de YouTube videoplayback (son buffers MSE/SABR truncados sin cabeceras completas;
        //    los videos de YouTube se extraen limpios mediante ExtractYouTubeStreamsAsync)
        if (hostLower.Contains("googlevideo.com") && pathLower.Contains("/videoplayback"))
        {
            if (queryLower.Contains("range=") || queryLower.Contains("ump=") || queryLower.Contains("sabr=") || contentLength == 0)
                return true;
        }

        // 2. Bloquear dominios de miniaturas, avatares, telemetría y publicidad
        foreach (var badHost in GarbageHostKeywords)
        {
            if (fullLower.Contains(badHost, StringComparison.Ordinal))
                return true;
        }

        // 3. Bloquear rutas de iconos, avatares, segmentos sueltos o inicializadores DASH/HLS
        foreach (var badPath in GarbagePathKeywords)
        {
            if (pathLower.Contains(badPath, StringComparison.Ordinal))
                return true;
        }

        // 4. Bloquear extensiones de segmentos individuales o código web
        if (pathLower.EndsWith(".ts") ||
            pathLower.EndsWith(".m4s") ||
            pathLower.EndsWith(".m4f") ||
            pathLower.EndsWith(".cmfv") ||
            pathLower.EndsWith(".cmfa") ||
            pathLower.EndsWith(".js") ||
            pathLower.EndsWith(".mjs") ||
            pathLower.EndsWith(".css") ||
            pathLower.EndsWith(".json") ||
            pathLower.EndsWith(".xml") ||
            pathLower.EndsWith(".woff") ||
            pathLower.EndsWith(".woff2") ||
            pathLower.EndsWith(".ttf") ||
            pathLower.EndsWith(".ico") ||
            pathLower.EndsWith(".svg") ||
            pathLower.EndsWith(".cur"))
        {
            return true;
        }

        // 5. Bloquear parámetros de fragmentos por rango en query string
        if (queryLower.Contains("bytestart=") ||
            queryLower.Contains("byteend=") ||
            queryLower.Contains("&range=") ||
            queryLower.Contains("?range=") ||
            queryLower.Contains("&sq="))
        {
            return true;
        }

        // 6. Bloquear sub-manifiestos de un solo segmento o audio/video demasiado pequeño (efectos de sonido de UI, anuncios cortos)
        bool isHlsManifest = pathLower.EndsWith(".m3u8") || ctLower.Contains("mpegurl");
        if (!isHlsManifest)
        {
            if ((ctLower.StartsWith("video/") || categoryHint == "Video") && contentLength > 0 && contentLength < 300_000)
                return true; // Menor a 300 KB no es un video real

            if ((ctLower.StartsWith("audio/") || categoryHint == "Audio") && contentLength > 0 && contentLength < 80_000)
                return true; // Menor a 80 KB suele ser un sonido de notificación

            if ((ctLower.StartsWith("image/") || categoryHint == "Imagen") && contentLength > 0 && contentLength < 200_000)
                return true; // Ignorar miniaturas e imágenes pequeñas
        }

        // 7. Si Content-Type es HTML, texto plano JSON o binario genérico sin extensión válida, descartar
        if (ctLower.Contains("text/html") || ctLower.Contains("application/json") || ctLower.Contains("text/javascript"))
            return true;

        return false;
    }

    private static string? _cachedVisitorData;
    private static DateTime _cachedVisitorDataExpiry = DateTime.MinValue;
    private static readonly object _logLock = new();

    private static string DiagnosticLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LexBrowser",
        "media_downloader.log");

    /// <summary>
    /// Registra eventos y errores del motor de extracción/descarga multimedia en %LOCALAPPDATA%\LexBrowser\media_downloader.log.
    /// </summary>
    public static void LogMediaDiagnostic(string message)
    {
        try
        {
            lock (_logLock)
            {
                string path = DiagnosticLogPath;
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 512 * 1024)
                {
                    File.WriteAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [LOG ROTATED]{Environment.NewLine}");
                }

                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch { }
    }

    /// <summary>
    /// Obtiene el token VISITOR_DATA de YouTube necesario para que el cliente VISIONOS no reciba LOGIN_REQUIRED en videos musicales.
    /// </summary>
    public static async Task<string?> FetchYouTubeVisitorDataAsync(string videoId, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(_cachedVisitorData) && DateTime.UtcNow < _cachedVisitorDataExpiry)
        {
            return _cachedVisitorData;
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://www.youtube.com/watch?v={videoId}&hl=es");
            req.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            req.Headers.TryAddWithoutValidation("Accept-Language", "es-ES,es;q=0.9,en;q=0.8");

            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return _cachedVisitorData;

            string html = await resp.Content.ReadAsStringAsync(ct);
            var m = Regex.Match(html, "\"VISITOR_DATA\"\\s*:\\s*\"([^\"]+)\"");
            if (m.Success && !string.IsNullOrWhiteSpace(m.Groups[1].Value))
            {
                _cachedVisitorData = m.Groups[1].Value;
                _cachedVisitorDataExpiry = DateTime.UtcNow.AddMinutes(15);
                LogMediaDiagnostic($"[YouTube] VISITOR_DATA obtenido para videoId={videoId} (len={_cachedVisitorData.Length})");
                return _cachedVisitorData;
            }
        }
        catch (Exception ex)
        {
            LogMediaDiagnostic($"[YouTube] Aviso al obtener VISITOR_DATA: {ex.Message}");
        }

        return _cachedVisitorData;
    }

    /// <summary>
    /// Extrae todas las calidades limpias de un video o Short de YouTube (1080p HD, 720p HD, 480p, 360p y Solo Audio MP3/M4A)
    /// consultando VISIONOS con VISITOR_DATA (idéntico al flujo interno de Video DownloadHelper 10.6.7.2).
    /// </summary>
    public static async Task<List<DetectedMediaItem>> ExtractYouTubeStreamsAsync(
        string pageUrl,
        string? fallbackTitle = null,
        string? pageVisitorData = null,
        string? pageCookieHeader = null,
        CancellationToken ct = default)
    {
        var results = new List<DetectedMediaItem>();
        string? videoId = TryExtractYouTubeVideoId(pageUrl);
        if (string.IsNullOrWhiteSpace(videoId)) return results;

        string? visitorData = !string.IsNullOrWhiteSpace(pageVisitorData)
            ? pageVisitorData
            : await FetchYouTubeVisitorDataAsync(videoId, ct);

        if (!string.IsNullOrWhiteSpace(pageVisitorData))
        {
            _cachedVisitorData = pageVisitorData;
            _cachedVisitorDataExpiry = DateTime.UtcNow.AddMinutes(15);
        }

        // 1. Intentar VISIONOS (sin cookies primero + con VISITOR_DATA, y luego con cookies si el video requiere cuenta/edad)
        var visionAttempts = new List<string?> { null };
        if (!string.IsNullOrWhiteSpace(pageCookieHeader))
        {
            visionAttempts.Add(pageCookieHeader);
        }

        foreach (var cookieAttempt in visionAttempts)
        {
            try
            {
                var payload = new
                {
                    videoId = videoId,
                    context = new
                    {
                        client = new
                        {
                            clientName = "VISIONOS",
                            clientVersion = "1.02",
                            deviceMake = "Apple",
                            deviceModel = "RealityDevice17,1",
                            osName = "visionOS",
                            osVersion = "26.5.23O471",
                            visitorData = visitorData,
                            hl = "es",
                            gl = "US"
                        }
                    },
                    contentCheckOk = true,
                    racyCheckOk = true
                };

                using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/player?prettyPrint=false");
                req.Headers.TryAddWithoutValidation("User-Agent", VisionOsUserAgent);
                req.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", "101");
                req.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", "1.02");
                req.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
                if (!string.IsNullOrWhiteSpace(visitorData))
                {
                    req.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", visitorData);
                }
                if (!string.IsNullOrWhiteSpace(cookieAttempt))
                {
                    req.Headers.TryAddWithoutValidation("Cookie", cookieAttempt);
                }

                req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode) continue;

                string json = await resp.Content.ReadAsStringAsync(ct);
                var parsed = ParseYouTubePlayerResponseJson(json, pageUrl, fallbackTitle, VisionOsUserAgent, allowAdaptiveFormats: true);
                if (parsed.Count > 0)
                {
                    LogMediaDiagnostic($"[YouTube] VISIONOS extrajo {parsed.Count} calidades limpias para videoId={videoId}");
                    return parsed;
                }
            }
            catch (Exception ex)
            {
                LogMediaDiagnostic($"[YouTube] Error en cliente VISIONOS para {videoId}: {ex.Message}");
            }
        }

        // 2. Respaldo IOS únicamente vía HLS Manifest (en IOS adaptiveFormats da 403 sin poToken de hardware, igual que en VDH)
        try
        {
            var iosPayload = new
            {
                videoId = videoId,
                context = new
                {
                    client = new
                    {
                        clientName = "IOS",
                        clientVersion = "20.11.6",
                        deviceMake = "Apple",
                        deviceModel = "iPhone16,2",
                        osName = "iPhone",
                        osVersion = "18.3.2.22D82",
                        visitorData = visitorData,
                        hl = "es",
                        gl = "US"
                    }
                },
                contentCheckOk = true,
                racyCheckOk = true
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/player?prettyPrint=false");
            req.Headers.TryAddWithoutValidation("User-Agent", IosUserAgent);
            req.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", "5");
            req.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", "20.11.6");
            req.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
            if (!string.IsNullOrWhiteSpace(visitorData))
            {
                req.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", visitorData);
            }
            req.Content = new StringContent(JsonSerializer.Serialize(iosPayload), Encoding.UTF8, "application/json");

            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("streamingData", out var sd) &&
                    sd.TryGetProperty("hlsManifestUrl", out var hlsEl) &&
                    !string.IsNullOrWhiteSpace(hlsEl.GetString()))
                {
                    string title = fallbackTitle ?? "Video de YouTube";
                    if (doc.RootElement.TryGetProperty("videoDetails", out var vd) &&
                        vd.TryGetProperty("title", out var tEl) &&
                        !string.IsNullOrWhiteSpace(tEl.GetString()))
                    {
                        title = tEl.GetString()!;
                    }
                    var hlsItems = await ExpandHlsPlaylistAsync(hlsEl.GetString()!, pageUrl, title, ct);
                    if (hlsItems.Count > 0)
                    {
                        LogMediaDiagnostic($"[YouTube] IOS HLS extrajo {hlsItems.Count} variantes para videoId={videoId}");
                        return hlsItems;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogMediaDiagnostic($"[YouTube] Error en respaldo IOS HLS para {videoId}: {ex.Message}");
        }

        return results;
    }

    /// <summary>
    /// Parsea la respuesta JSON de InnerTube (/youtubei/v1/player) y construye la lista ordenada de calidades descargables,
    /// separando explícitamente las opciones de Video (MP4 1080p, 720p, 480p, 360p) y las opciones de Audio (MP3 / M4A).
    /// </summary>
    public static List<DetectedMediaItem> ParseYouTubePlayerResponseJson(
        string json,
        string pageUrl,
        string? fallbackTitle,
        string clientUserAgent,
        bool allowAdaptiveFormats = true)
    {
        var results = new List<DetectedMediaItem>();
        if (string.IsNullOrWhiteSpace(json)) return results;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("playabilityStatus", out var statusEl) &&
                statusEl.TryGetProperty("status", out var statusVal) &&
                statusVal.GetString() != "OK")
            {
                string reason = statusEl.TryGetProperty("reason", out var rProp) ? (rProp.GetString() ?? "") : "";
                LogMediaDiagnostic($"[YouTube] playabilityStatus={statusVal.GetString()} ({reason})");
                return results;
            }

            string videoTitle = fallbackTitle ?? "Video de YouTube";
            long durationSec = 0;

            if (root.TryGetProperty("videoDetails", out var details))
            {
                if (details.TryGetProperty("title", out var titleEl) && !string.IsNullOrWhiteSpace(titleEl.GetString()))
                {
                    videoTitle = titleEl.GetString()!;
                }
                if (details.TryGetProperty("lengthSeconds", out var lenEl) && long.TryParse(lenEl.GetString(), out long d))
                {
                    durationSec = d;
                }
            }

            videoTitle = SanitizeTitle(videoTitle.Replace(" - YouTube", "", StringComparison.OrdinalIgnoreCase).Trim());
            if (string.IsNullOrWhiteSpace(videoTitle)) videoTitle = "Video_YouTube";

            if (!root.TryGetProperty("streamingData", out var streamingData))
                return results;

            string? hlsManifestUrl = streamingData.TryGetProperty("hlsManifestUrl", out var hlsProp)
                ? hlsProp.GetString()
                : null;

            // 1. Buscar las pistas de audio M4A (itag 140 = AAC 128kbps HQ, itag 139 = AAC 48kbps Ligero)
            string? bestAudioM4aUrl = null;
            long bestAudioM4aBytes = 0;
            int bestAudioBitrate = 0;

            var adaptiveStreams = new List<JsonElement>();
            if (allowAdaptiveFormats &&
                streamingData.TryGetProperty("adaptiveFormats", out var adaptiveArr) &&
                adaptiveArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in adaptiveArr.EnumerateArray())
                {
                    adaptiveStreams.Add(el);
                    string mime = el.TryGetProperty("mimeType", out var mProp) ? (mProp.GetString() ?? "") : "";
                    string directUrl = el.TryGetProperty("url", out var uProp) ? (uProp.GetString() ?? "") : "";
                    if (string.IsNullOrWhiteSpace(directUrl)) continue;

                    if (mime.StartsWith("audio/mp4", StringComparison.OrdinalIgnoreCase) &&
                        mime.Contains("mp4a", StringComparison.OrdinalIgnoreCase))
                    {
                        int br = el.TryGetProperty("bitrate", out var brProp) ? brProp.GetInt32() : 0;
                        long cl = el.TryGetProperty("contentLength", out var clProp) && long.TryParse(clProp.GetString(), out long parsedCl) ? parsedCl : 0;
                        if (br > bestAudioBitrate || bestAudioM4aUrl == null)
                        {
                            bestAudioBitrate = br;
                            bestAudioM4aUrl = directUrl;
                            bestAudioM4aBytes = cl;
                        }
                    }
                }
            }

            var addedHeights = new HashSet<int>();

            // 2. Registrar formatos adaptativos MP4 en Alta Definición (1080p HD, 720p HD, 480p, 360p) unidos con el audio M4A
            if (!string.IsNullOrWhiteSpace(bestAudioM4aUrl))
            {
                int[] targetResolutions = { 1080, 720, 480, 360 };
                foreach (int targetRes in targetResolutions)
                {
                    if (addedHeights.Contains(targetRes)) continue;

                    // Elegir stream video/mp4 con codec avc1 (H.264) indispensable para reproducción nativa en Windows Media Player sin errores
                    JsonElement? bestMatch = null;
                    int bestBr = 0;

                    foreach (var el in adaptiveStreams)
                    {
                        string mime = el.TryGetProperty("mimeType", out var mProp) ? (mProp.GetString() ?? "") : "";
                        string directUrl = el.TryGetProperty("url", out var uProp) ? (uProp.GetString() ?? "") : "";
                        if (string.IsNullOrWhiteSpace(directUrl) ||
                            !mime.StartsWith("video/mp4", StringComparison.OrdinalIgnoreCase) ||
                            !mime.Contains("avc1", StringComparison.OrdinalIgnoreCase))
                            continue;

                        int h = el.TryGetProperty("height", out var hProp) ? hProp.GetInt32() : 0;
                        int w = el.TryGetProperty("width", out var wProp) ? wProp.GetInt32() : 0;
                        int minDim = (w > 0 && h > 0) ? Math.Min(w, h) : h;

                        string qLabel = el.TryGetProperty("qualityLabel", out var qlProp) ? (qlProp.GetString() ?? "") : "";
                        bool matchesRes = (minDim == targetRes) || qLabel.StartsWith($"{targetRes}p", StringComparison.OrdinalIgnoreCase);
                        if (!matchesRes) continue;

                        int br = el.TryGetProperty("bitrate", out var brProp) ? brProp.GetInt32() : 0;
                        if (br > bestBr)
                        {
                            bestBr = br;
                            bestMatch = el;
                        }
                    }

                    if (bestMatch.HasValue)
                    {
                        var el = bestMatch.Value;
                        string videoUrl = el.GetProperty("url").GetString()!;
                        long videoCl = el.TryGetProperty("contentLength", out var clProp) && long.TryParse(clProp.GetString(), out long parsedCl) ? parsedCl : 0;
                        long totalBytes = (videoCl > 0 && bestAudioM4aBytes > 0) ? (videoCl + bestAudioM4aBytes) : videoCl;

                        string qualityLabel = targetRes >= 720 ? $"{targetRes}p HD" : $"{targetRes}p";
                        addedHeights.Add(targetRes);

                        results.Add(new DetectedMediaItem
                        {
                            Url = videoUrl,
                            AudioPairUrl = bestAudioM4aUrl,
                            FallbackHlsUrl = hlsManifestUrl,
                            Referer = pageUrl,
                            CustomUserAgent = clientUserAgent,
                            IsYouTubeStream = true,
                            FileName = $"{videoTitle} ({qualityLabel}).mp4",
                            Category = "Video",
                            CategoryIcon = "🎬",
                            FormatBadge = $"MP4 {qualityLabel}",
                            SizeBytes = totalBytes,
                            VideoSizeBytes = videoCl,
                            AudioSizeBytes = bestAudioM4aBytes,
                            SizeDisplay = totalBytes > 0 ? $"{FormatBytes(totalBytes)} • Video + Audio" : $"Video {qualityLabel} + Audio",
                            PriorityScore = 1000 + targetRes
                        });
                    }
                }
            }

            // 3. Registrar formatos progresivos combinados si aún no se cubrió esa resolución
            if (streamingData.TryGetProperty("formats", out var formatsArr) && formatsArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in formatsArr.EnumerateArray())
                {
                    string mime = el.TryGetProperty("mimeType", out var mProp) ? (mProp.GetString() ?? "") : "";
                    string directUrl = el.TryGetProperty("url", out var uProp) ? (uProp.GetString() ?? "") : "";
                    if (string.IsNullOrWhiteSpace(directUrl) || !mime.StartsWith("video/mp4", StringComparison.OrdinalIgnoreCase))
                        continue;

                    int height = el.TryGetProperty("height", out var hProp) ? hProp.GetInt32() : 360;
                    int width = el.TryGetProperty("width", out var wProp) ? wProp.GetInt32() : 640;
                    int effectiveRes = Math.Min(width, height);
                    if (effectiveRes <= 0) effectiveRes = height;
                    if (addedHeights.Contains(effectiveRes)) continue;

                    long cl = el.TryGetProperty("contentLength", out var clProp) && long.TryParse(clProp.GetString(), out long parsedCl) ? parsedCl : 0;
                    if (cl <= 0 && durationSec > 0 && el.TryGetProperty("bitrate", out var brProp))
                    {
                        cl = (long)(brProp.GetInt64() / 8.0 * durationSec);
                    }

                    string qualityLabel = effectiveRes >= 720 ? $"{effectiveRes}p HD" : $"{effectiveRes}p";
                    addedHeights.Add(effectiveRes);

                    results.Add(new DetectedMediaItem
                    {
                        Url = directUrl,
                        FallbackHlsUrl = hlsManifestUrl,
                        Referer = pageUrl,
                        CustomUserAgent = clientUserAgent,
                        IsYouTubeStream = true,
                        FileName = $"{videoTitle} ({qualityLabel}).mp4",
                        Category = "Video",
                        CategoryIcon = "🎬",
                        FormatBadge = $"MP4 {qualityLabel}",
                        SizeBytes = cl,
                        VideoSizeBytes = cl,
                        SizeDisplay = cl > 0 ? $"{FormatBytes(cl)} • Video + Audio (Directo)" : "Video + Audio (Directo)",
                        PriorityScore = 1000 + effectiveRes
                    });
                }
            }

            // 4. Registrar opciones separadas de Solo Audio en alta calidad (MP3 y M4A Original)
            if (!string.IsNullOrWhiteSpace(bestAudioM4aUrl))
            {
                results.Add(new DetectedMediaItem
                {
                    Url = bestAudioM4aUrl,
                    Referer = pageUrl,
                    CustomUserAgent = clientUserAgent,
                    IsYouTubeStream = true,
                    FileName = $"{videoTitle}.mp3",
                    Category = "Audio",
                    CategoryIcon = "🎵",
                    FormatBadge = "AUDIO MP3",
                    SizeBytes = bestAudioM4aBytes,
                    VideoSizeBytes = bestAudioM4aBytes,
                    SizeDisplay = bestAudioM4aBytes > 0 ? $"{FormatBytes(bestAudioM4aBytes)} • Solo Audio (Alta Calidad)" : "Solo Audio MP3",
                    PriorityScore = 960
                });

                results.Add(new DetectedMediaItem
                {
                    Url = bestAudioM4aUrl,
                    Referer = pageUrl,
                    CustomUserAgent = clientUserAgent,
                    IsYouTubeStream = true,
                    FileName = $"{videoTitle} (Audio HQ).m4a",
                    Category = "Audio",
                    CategoryIcon = "🎵",
                    FormatBadge = "AUDIO M4A",
                    SizeBytes = bestAudioM4aBytes,
                    VideoSizeBytes = bestAudioM4aBytes,
                    SizeDisplay = bestAudioM4aBytes > 0 ? $"{FormatBytes(bestAudioM4aBytes)} • Audio Original AAC (128 kbps)" : "Solo Audio M4A",
                    PriorityScore = 950
                });
            }

            return results.OrderByDescending(x => x.PriorityScore).ToList();
        }
        catch (Exception ex)
        {
            LogMediaDiagnostic($"[YouTube] Error parseando respuesta InnerTube: {ex.Message}");
            return results;
        }
    }

    /// <summary>
    /// Analiza un manifiesto maestro HLS (.m3u8) y devuelve sus variantes de resolución (1080p, 720p, 480p)
    /// o el propio stream si ya es una lista directa de segmentos.
    /// </summary>
    public static async Task<List<DetectedMediaItem>> ExpandHlsPlaylistAsync(string m3u8Url, string pageUrl, string pageTitle, CancellationToken ct = default)
    {
        var items = new List<DetectedMediaItem>();
        if (string.IsNullOrWhiteSpace(m3u8Url) || !Uri.TryCreate(m3u8Url, UriKind.Absolute, out var baseUri))
            return items;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, baseUri);
            req.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            if (!string.IsNullOrWhiteSpace(pageUrl))
            {
                req.Headers.TryAddWithoutValidation("Referer", pageUrl);
            }

            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return items;

            string content = await resp.Content.ReadAsStringAsync(ct);
            return ParseHlsManifestContent(content, baseUri, pageUrl, pageTitle);
        }
        catch
        {
            return items;
        }
    }

    /// <summary>
    /// Parsea el texto de un archivo .m3u8 (Master Playlist o Media Playlist) para extraer las calidades disponibles.
    /// </summary>
    public static List<DetectedMediaItem> ParseHlsManifestContent(string m3u8Content, Uri manifestUri, string pageUrl, string pageTitle)
    {
        var items = new List<DetectedMediaItem>();
        if (string.IsNullOrWhiteSpace(m3u8Content) || !m3u8Content.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            return items;

        string cleanTitle = SanitizeTitle(string.IsNullOrWhiteSpace(pageTitle) ? manifestUri.Host : pageTitle);
        if (string.IsNullOrWhiteSpace(cleanTitle)) cleanTitle = "Video_Stream";

        var lines = m3u8Content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        // Verificar si es un Master Playlist con variantes #EXT-X-STREAM-INF
        bool hasVariants = lines.Any(l => l.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase));
        if (hasVariants)
        {
            var seenRes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < lines.Length - 1; i++)
            {
                string line = lines[i].Trim();
                if (!line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Ignorar líneas de comentario intermedias hasta encontrar la URL de la variante
                string? variantRaw = null;
                for (int j = i + 1; j < lines.Length; j++)
                {
                    string candidate = lines[j].Trim();
                    if (!candidate.StartsWith("#"))
                    {
                        variantRaw = candidate;
                        i = j;
                        break;
                    }
                }

                if (string.IsNullOrWhiteSpace(variantRaw)) continue;
                if (!Uri.TryCreate(manifestUri, variantRaw, out var variantUri)) continue;

                int height = 0;
                var resMatch = Regex.Match(line, @"RESOLUTION=\d+x(\d+)", RegexOptions.IgnoreCase);
                if (resMatch.Success && int.TryParse(resMatch.Groups[1].Value, out int parsedH))
                {
                    height = parsedH;
                }

                string qualityTag = height > 0 ? (height >= 720 ? $"{height}p HD" : $"{height}p") : "Calidad Auto";
                if (!seenRes.Add(qualityTag)) continue;

                items.Add(new DetectedMediaItem
                {
                    Url = variantUri.AbsoluteUri,
                    Referer = pageUrl,
                    IsHlsStream = true,
                    FileName = $"{cleanTitle} ({qualityTag}).mp4",
                    Category = "Video",
                    CategoryIcon = "🎬",
                    FormatBadge = height > 0 ? $"HLS {height}p" : "HLS MP4",
                    SizeBytes = 0,
                    SizeDisplay = $"Stream completo ({qualityTag}) • Se une automáticamente",
                    PriorityScore = 800 + height
                });
            }

            return items.OrderByDescending(x => x.PriorityScore).ToList();
        }

        // Si es una lista directa de segmentos (#EXTINF:), registrarla solo si tiene al menos 2 segmentos (evitar micro-anuncios)
        int segmentCount = lines.Count(l => l.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase));
        if (segmentCount >= 2)
        {
            items.Add(new DetectedMediaItem
            {
                Url = manifestUri.AbsoluteUri,
                Referer = pageUrl,
                IsHlsStream = true,
                FileName = $"{cleanTitle}.mp4",
                Category = "Video",
                CategoryIcon = "🎬",
                FormatBadge = "HLS MP4",
                SizeBytes = 0,
                SizeDisplay = $"Stream completo ({segmentCount} segmentos)",
                PriorityScore = 750
            });
        }

        return items;
    }

    /// <summary>
    /// Descarga un elemento multimedia detectado al archivo destino, reportando progreso en tiempo real.
    /// Soporta:
    /// 1. Videos adaptativos de YouTube (descarga por bloques rápidos &amp;range= de 10 MB + muxing nativo fMP4 de Video HD + Audio M4A).
    /// 2. Streams directos de YouTube por bloques de 10 MB (evita throttling de velocidad).
    /// 3. Streams HLS (.m3u8) descargando y uniendo todos los segmentos en un solo archivo.
    /// 4. Descarga HTTP directa estándar con cabeceras de sesión (Cookie/Referer/Origin) y reintento automático.
    /// </summary>
    public static async Task<long> DownloadMediaItemAsync(
        DetectedMediaItem item,
        string targetPath,
        Action<long, long>? onProgress = null,
        CancellationToken ct = default)
    {
        LogMediaDiagnostic($"[DownloadStart] File={item.FileName} | Badge={item.FormatBadge} | YT={item.IsYouTubeStream} | HLS={item.IsHlsStream} | HasAudioPair={!string.IsNullOrEmpty(item.AudioPairUrl)} | Size={item.SizeBytes}");
        try
        {
            if (item.IsHlsStream)
            {
                long hlsBytes = await DownloadHlsStreamToFileAsync(
                    item.Url,
                    item.Referer,
                    item.CustomUserAgent,
                    item.CustomCookieHeader,
                    item.CustomOriginHeader,
                    targetPath,
                    onProgress,
                    ct);
                LogMediaDiagnostic($"[DownloadSuccess] HLS File={item.FileName} | Written={hlsBytes} bytes");
                return hlsBytes;
            }

            string userAgent = !string.IsNullOrWhiteSpace(item.CustomUserAgent) ? item.CustomUserAgent : BrowserUserAgent;

            // Caso YouTube HD con pista de video y pista de audio separadas: descargar ambas y unirlas en un solo .mp4 con sonido
            if (!string.IsNullOrWhiteSpace(item.AudioPairUrl))
            {
                string tempVideoPath = targetPath + ".v_tmp";
                string tempAudioPath = targetPath + ".a_tmp";

                try
                {
                    long videoReceived = 0;
                    long audioReceived = 0;
                    long expectedTotal = item.SizeBytes;

                    await DownloadStreamFastAsync(
                        item.Url,
                        item.Referer,
                        userAgent,
                        item.CustomCookieHeader,
                        item.CustomOriginHeader,
                        item.CustomAuthorizationHeader,
                        tempVideoPath,
                        item.IsYouTubeStream,
                        item.VideoSizeBytes,
                        (rec, tot) =>
                        {
                            videoReceived = rec;
                            if (expectedTotal <= 0 && tot > 0) expectedTotal = tot + item.AudioSizeBytes;
                            onProgress?.Invoke(videoReceived + audioReceived, expectedTotal);
                        },
                        ct);

                    await DownloadStreamFastAsync(
                        item.AudioPairUrl,
                        item.Referer,
                        userAgent,
                        item.CustomCookieHeader,
                        item.CustomOriginHeader,
                        item.CustomAuthorizationHeader,
                        tempAudioPath,
                        item.IsYouTubeStream,
                        item.AudioSizeBytes,
                        (rec, tot) =>
                        {
                            audioReceived = rec;
                            onProgress?.Invoke(videoReceived + audioReceived, Math.Max(expectedTotal, videoReceived + tot));
                        },
                        ct);

                    // Unir video fMP4 + audio fMP4 en el archivo final .mp4 progresivo estándar compatible con Windows Media Player
                    await Task.Run(() => Mp4Remuxer.Mux(tempVideoPath, tempAudioPath, targetPath), ct);

                    var fi = new FileInfo(targetPath);
                    long finalLen = fi.Exists ? fi.Length : (videoReceived + audioReceived);
                    LogMediaDiagnostic($"[DownloadSuccess] Muxed MP4 File={item.FileName} | FinalSize={finalLen} bytes (V={videoReceived}, A={audioReceived})");
                    return finalLen;
                }
                catch (Exception ex) when (!string.IsNullOrWhiteSpace(item.FallbackHlsUrl) && !ct.IsCancellationRequested)
                {
                    LogMediaDiagnostic($"[DownloadFallback] Adaptive stream falló ({ex.Message}), usando FallbackHlsUrl para {item.FileName}");
                    return await DownloadHlsStreamToFileAsync(
                        item.FallbackHlsUrl!,
                        item.Referer,
                        userAgent,
                        item.CustomCookieHeader,
                        item.CustomOriginHeader,
                        targetPath,
                        onProgress,
                        ct);
                }
                finally
                {
                    TryDeleteFile(tempVideoPath);
                    TryDeleteFile(tempAudioPath);
                }
            }

            // Caso descarga de un solo flujo (MP4 combinado, M4A audio, MP3, documento o imagen)
            long singleKnownSize = item.VideoSizeBytes > 0 ? item.VideoSizeBytes : item.SizeBytes;
            long singleBytes = await DownloadStreamFastAsync(
                item.Url,
                item.Referer,
                userAgent,
                item.CustomCookieHeader,
                item.CustomOriginHeader,
                item.CustomAuthorizationHeader,
                targetPath,
                item.IsYouTubeStream,
                singleKnownSize,
                onProgress,
                ct);
            LogMediaDiagnostic($"[DownloadSuccess] Single Stream File={item.FileName} | Written={singleBytes} bytes");
            return singleBytes;
        }
        catch (Exception ex)
        {
            LogMediaDiagnostic($"[DownloadError] File={item.FileName} | Error={ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Remueve cualquier parámetro range=... previo de una URL de googlevideo.com para poder anexar bloques limpios.
    /// </summary>
    private static string StripRangeQueryParam(string url)
    {
        return Regex.Replace(url, @"([?&])range=\d*-\d*&?", m =>
        {
            return m.Groups[1].Value == "?" ? "?" : "&";
        }, RegexOptions.IgnoreCase).TrimEnd('&', '?');
    }

    /// <summary>
    /// Descarga un flujo HTTP a disco.
    /// - Si es de googlevideo.com, utiliza el parámetro &amp;range={inicio}-{fin} en bloques de 10 MB (igual que Video DownloadHelper)
    ///   para máxima velocidad sin throttling ni errores 403.
    /// - Si es de otro sitio web, envía las cabeceras de sesión capturadas (Cookie, Referer, Origin) y reintenta automáticamente
    ///   si el servidor rechaza algún Referer externo.
    /// </summary>
    private static async Task<long> DownloadStreamFastAsync(
        string url,
        string? referer,
        string userAgent,
        string? cookieHeader,
        string? originHeader,
        string? authorizationHeader,
        string destinationPath,
        bool isYouTubeStream,
        long knownTotalSize,
        Action<long, long>? onProgress,
        CancellationToken ct)
    {
        bool isGoogleVideo = isYouTubeStream || url.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase);

        if (isGoogleVideo)
        {
            string cleanUrl = StripRangeQueryParam(url);
            long totalSize = knownTotalSize;
            if (totalSize <= 0)
            {
                var clMatch = Regex.Match(cleanUrl, @"[?&]clen=(\d+)", RegexOptions.IgnoreCase);
                if (clMatch.Success && long.TryParse(clMatch.Groups[1].Value, out long parsedClen) && parsedClen > 0)
                {
                    totalSize = parsedClen;
                }
            }

            if (totalSize > 0)
            {
                const long chunkSize = 10 * 1024 * 1024; // 10 MB por bloque (idéntico a download_worker/main.js de VDH)
                long downloaded = 0;

                await using var outStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                byte[] buffer = new byte[65536];

                while (downloaded < totalSize)
                {
                    long end = Math.Min(downloaded + chunkSize - 1, totalSize - 1);
                    string sep = cleanUrl.Contains('?') ? "&" : "?";
                    string chunkUrl = $"{cleanUrl}{sep}range={downloaded}-{end}";

                    long chunkBytesRead = 0;
                    Exception? lastChunkEx = null;

                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        try
                        {
                            using var req = new HttpRequestMessage(HttpMethod.Get, chunkUrl);
                            req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                            req.Headers.TryAddWithoutValidation("Accept", "*/*");

                            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                            resp.EnsureSuccessStatusCode();

                            await using var netStream = await resp.Content.ReadAsStreamAsync(ct);
                            int read;
                            while ((read = await netStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                            {
                                await outStream.WriteAsync(buffer.AsMemory(0, read), ct);
                                downloaded += read;
                                chunkBytesRead += read;
                                onProgress?.Invoke(downloaded, totalSize);
                            }

                            lastChunkEx = null;
                            break;
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            lastChunkEx = ex;
                            if (chunkBytesRead > 0)
                            {
                                // Si ya avanzó parcialmente este bloque, continuar desde el nuevo offset 'downloaded'
                                lastChunkEx = null;
                                break;
                            }
                            await Task.Delay(250 * (attempt + 1), ct);
                        }
                    }

                    if (lastChunkEx != null)
                        throw lastChunkEx;

                    // Evitar bucle infinito si el servidor cerró el flujo al llegar al final real del archivo
                    if (chunkBytesRead == 0)
                        break;
                }

                return downloaded;
            }
        }

        // Descarga directa estándar (con reintento automático de cabeceras si un CDN bloquea Referer/Origin)
        var attempts = new (bool SendRefererAndOrigin, bool SendCustomUa)[]
        {
            (true, true),
            (false, true),
            (false, false)
        };

        Exception? lastError = null;
        foreach (var attempt in attempts)
        {
            try
            {
                using var stdReq = new HttpRequestMessage(HttpMethod.Get, url);
                stdReq.Headers.TryAddWithoutValidation("User-Agent", attempt.SendCustomUa ? userAgent : BrowserUserAgent);
                stdReq.Headers.TryAddWithoutValidation("Accept", "*/*");

                if (!isGoogleVideo)
                {
                    if (!string.IsNullOrWhiteSpace(cookieHeader))
                        stdReq.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
                    if (!string.IsNullOrWhiteSpace(authorizationHeader))
                        stdReq.Headers.TryAddWithoutValidation("Authorization", authorizationHeader);

                    if (attempt.SendRefererAndOrigin)
                    {
                        if (!string.IsNullOrWhiteSpace(referer))
                            stdReq.Headers.TryAddWithoutValidation("Referer", referer);
                        if (!string.IsNullOrWhiteSpace(originHeader))
                            stdReq.Headers.TryAddWithoutValidation("Origin", originHeader);
                    }
                }

                using var stdResp = await _httpClient.SendAsync(stdReq, HttpCompletionOption.ResponseHeadersRead, ct);
                stdResp.EnsureSuccessStatusCode();

                long totalLen = stdResp.Content.Headers.ContentLength ?? knownTotalSize;
                long received = 0;

                await using var stream = await stdResp.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                byte[] buf = new byte[65536];
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0)
                {
                    await fileStream.WriteAsync(buf.AsMemory(0, bytesRead), ct);
                    received += bytesRead;
                    onProgress?.Invoke(received, totalLen);
                }

                return received;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw lastError ?? new InvalidOperationException("No se pudo descargar el recurso multimedia.");
    }

    /// <summary>
    /// Descarga todos los segmentos de una lista HLS (.m3u8) y los ensambla en orden en un único archivo de video.
    /// </summary>
    private static async Task<long> DownloadHlsStreamToFileAsync(
        string m3u8Url,
        string? referer,
        string? customUserAgent,
        string? cookieHeader,
        string? originHeader,
        string destinationPath,
        Action<long, long>? onProgress,
        CancellationToken ct)
    {
        if (!Uri.TryCreate(m3u8Url, UriKind.Absolute, out var manifestUri))
            throw new InvalidOperationException("URL de stream HLS inválida.");

        string ua = !string.IsNullOrWhiteSpace(customUserAgent) ? customUserAgent : BrowserUserAgent;

        using var req = new HttpRequestMessage(HttpMethod.Get, manifestUri);
        req.Headers.TryAddWithoutValidation("User-Agent", ua);
        if (!string.IsNullOrWhiteSpace(referer))
            req.Headers.TryAddWithoutValidation("Referer", referer);
        if (!string.IsNullOrWhiteSpace(cookieHeader))
            req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        if (!string.IsNullOrWhiteSpace(originHeader))
            req.Headers.TryAddWithoutValidation("Origin", originHeader);

        using var resp = await _httpClient.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        string text = await resp.Content.ReadAsStringAsync(ct);

        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        // Si por alguna razón aún es un Master Playlist, tomar la primera variante de mayor resolución
        if (lines.Any(l => l.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase)))
        {
            var variants = ParseHlsManifestContent(text, manifestUri, referer ?? "", "Video");
            if (variants.Count > 0 && variants[0].Url != m3u8Url)
            {
                return await DownloadHlsStreamToFileAsync(
                    variants[0].Url,
                    referer,
                    ua,
                    cookieHeader,
                    originHeader,
                    destinationPath,
                    onProgress,
                    ct);
            }
        }

        string? initMapUrl = null;
        var segmentUrls = new List<string>();

        foreach (var rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase))
            {
                var uriMatch = Regex.Match(line, "URI=\"([^\"]+)\"", RegexOptions.IgnoreCase);
                if (uriMatch.Success && Uri.TryCreate(manifestUri, uriMatch.Groups[1].Value, out var mapUri))
                {
                    initMapUrl = mapUri.AbsoluteUri;
                }
            }
            else if (!line.StartsWith("#"))
            {
                if (Uri.TryCreate(manifestUri, line, out var segUri))
                {
                    segmentUrls.Add(segUri.AbsoluteUri);
                }
            }
        }

        if (segmentUrls.Count == 0)
            throw new InvalidOperationException("El manifiesto HLS no contiene segmentos descargables.");

        long totalWritten = 0;
        await using var outStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        if (!string.IsNullOrWhiteSpace(initMapUrl))
        {
            byte[] initBytes = await FetchSegmentBytesAsync(initMapUrl, referer, ua, cookieHeader, originHeader, ct);
            await outStream.WriteAsync(initBytes, ct);
            totalWritten += initBytes.Length;
        }

        // Descargar en lotes de 4 segmentos en paralelo manteniendo el orden exacto
        const int batchSize = 4;
        for (int i = 0; i < segmentUrls.Count; i += batchSize)
        {
            var batch = segmentUrls.Skip(i).Take(batchSize).ToArray();
            var tasks = batch.Select(u => FetchSegmentBytesAsync(u, referer, ua, cookieHeader, originHeader, ct)).ToArray();
            byte[][] results = await Task.WhenAll(tasks);

            foreach (var segBytes in results)
            {
                await outStream.WriteAsync(segBytes, ct);
                totalWritten += segBytes.Length;
            }

            long estimatedTotal = (long)((double)totalWritten / Math.Min(i + batch.Length, segmentUrls.Count) * segmentUrls.Count);
            onProgress?.Invoke(totalWritten, Math.Max(totalWritten, estimatedTotal));
        }

        return totalWritten;
    }

    private static async Task<byte[]> FetchSegmentBytesAsync(
        string url,
        string? referer,
        string userAgent,
        string? cookieHeader,
        string? originHeader,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        if (!string.IsNullOrWhiteSpace(referer))
            req.Headers.TryAddWithoutValidation("Referer", referer);
        if (!string.IsNullOrWhiteSpace(cookieHeader))
            req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        if (!string.IsNullOrWhiteSpace(originHeader))
            req.Headers.TryAddWithoutValidation("Origin", originHeader);

        using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    // ─────────────────────────────────────────────────────────────────────
    // MUXER NATIVO ISO-BMFF (fMP4) EN C# PURO (VIDEO HD + AUDIO M4A)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Remuxea flujos fragmentados a un MP4 progresivo estándar con moov al inicio compatible 100% con Windows Media Player.
    /// </summary>
    public static void MuxFragmentedMp4(string videoPath, string audioPath, string outputPath)
    {
        Mp4Remuxer.Mux(videoPath, audioPath, outputPath);
    }

    // ─────────────────────────────────────────────────────────────────────
    // MOTOR DE RESOLUCIÓN DE EMBEDS E IFRAMES OFUSCADOS (AnimeYT, Streamwish, etc.)
    // ─────────────────────────────────────────────────────────────────────

    private static readonly Regex PackedRegex = new(
        @"\}\('((?:[^'\\]|\\.)*)',\s*(\d+),\s*(\d+),\s*'((?:[^'\\]|\\.)*)'\.split\('\|'\)",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex AbsMediaRegex = new(
        @"https?://[^\s""'<>\\]+?\.(?:m3u8|mp4)(?:\?[^\s""'<>\\]*)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RelMediaRegex = new(
        @"[""'](/[^""'\s<>\\]+?\.(?:m3u8|mp4)(?:\?[^""'\s<>\\]*)?)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] NonPlayerEmbedHosts =
    {
        "google.", "youtube.", "facebook.", "twitter.", "disqus.", "recaptcha", "doubleclick", "gstatic.", "adservice"
    };

    /// <summary>Script de inspección ligera ejecutado bajo demanda en frames DOM.</summary>
    public const string ScanJs = """
    (() => {
      const urls = new Set(), frames = new Set();
      const add = (u) => { try { if (!u || typeof u !== 'string') return; if (/^(blob|data|javascript|about):/i.test(u)) return; urls.add(new URL(u, location.href).href); } catch (e) {} };
      const media = /\.(m3u8|mp4|webm|mkv|m4v|mp3|m4a|aac|mov)(\?|#|$)/i;
      try { document.querySelectorAll('video,audio,source').forEach(e => { add(e.currentSrc); add(e.getAttribute('src')); add(e.getAttribute('data-src')); }); } catch (e) {}
      try { document.querySelectorAll('iframe').forEach(f => { const s = f.getAttribute('src') || f.getAttribute('data-src'); if (s && !/^(about|javascript|data):/i.test(s)) frames.add(new URL(s, location.href).href); }); } catch (e) {}
      try { performance.getEntriesByType('resource').forEach(r => { if (media.test(r.name)) add(r.name); }); } catch (e) {}
      try { if (window.jwplayer) { const p = jwplayer(); const pl = (p.getPlaylist && p.getPlaylist()) || []; pl.forEach(it => { add(it.file); (it.sources || []).forEach(s => add(s.file)); }); } } catch (e) {}
      try { if (window.videojs && videojs.getPlayers) Object.values(videojs.getPlayers()).forEach(p => { if (p && p.currentSrc) add(p.currentSrc()); }); } catch (e) {}
      try { for (const k of ['hls', 'player', 'plyr', 'flowplayer']) { const o = window[k]; if (o) { add(o.url); add(o.src); if (o.config) add(o.config.url); } } } catch (e) {}
      try {
        const rx = /(?:https?:)?\/\/[^\s"'<>\\]+?\.(?:m3u8|mp4)(?:\?[^\s"'<>\\]*)?/gi;
        document.querySelectorAll('script:not([src])').forEach(s => {
          const t = (s.textContent || '').replace(/\\\//g, '/');
          if (t.length > 2000000) return;
          (t.match(rx) || []).forEach(add);
        });
      } catch (e) {}
      return JSON.stringify({ page: location.href, ua: navigator.userAgent, title: document.title, urls: [...urls], frames: [...frames] });
    })()
    """;

    public static bool IsHlsUrl(string url, string? contentType)
    {
        try { if (new Uri(url).AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)) return true; } catch { }
        return (contentType ?? "").Contains("mpegurl", StringComparison.OrdinalIgnoreCase);
    }

    public static string? CategorizeResource(string url, string? contentType)
    {
        string ext;
        try { ext = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant(); } catch { return null; }
        string ct = (contentType ?? "").ToLowerInvariant();

        if (ext == ".m3u8" || ct.Contains("mpegurl")) return "Video";
        if (ext is ".mp4" or ".webm" or ".mkv" or ".mov" or ".m4v" or ".avi" or ".flv" || ct.StartsWith("video/")) return "Video";
        if (ext is ".mp3" or ".m4a" or ".aac" or ".ogg" or ".opus" or ".wav" or ".flac" || ct.StartsWith("audio/")) return "Audio";
        if (ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".avif" || ct.StartsWith("image/")) return "Imagen";
        if (ext is ".pdf" or ".zip" or ".rar" or ".7z" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".epub" or ".csv"
            || ct is "application/pdf" or "application/zip" or "application/x-rar-compressed" or "application/x-7z-compressed"
            || ct.Contains("officedocument") || ct.Contains("msword") || ct.Contains("ms-excel") || ct.Contains("ms-powerpoint"))
            return "Documento";
        return null;
    }

    public static string DedupeKey(string url)
    {
        try { var u = new Uri(url); return u.Host + u.AbsolutePath; } catch { return url; }
    }

    public static string OriginOf(string url)
    {
        try { return new Uri(url).GetLeftPart(UriPartial.Authority); } catch { return ""; }
    }

    public static bool LooksLikePlayerEmbed(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https")) return false;
        string h = u.Host.ToLowerInvariant();
        return !NonPlayerEmbedHosts.Any(k => h.Contains(k));
    }

    public static DetectedMediaItem BuildDirectItem(string url, string? contentType, long size, string category, string? referer, string? pageTitle)
    {
        string ext = "";
        string rawName = "";
        try
        {
            var u = new Uri(url);
            ext = Path.GetExtension(u.AbsolutePath).ToLowerInvariant();
            rawName = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(u.AbsolutePath));
        }
        catch { }

        if (string.IsNullOrEmpty(ext))
        {
            string ct = (contentType ?? "").ToLowerInvariant();
            ext = ct switch
            {
                "video/webm" => ".webm",
                "audio/mpeg" => ".mp3",
                "audio/mp4" => ".m4a",
                "application/pdf" => ".pdf",
                "image/png" => ".png",
                "image/webp" => ".webp",
                "image/jpeg" => ".jpg",
                _ => category == "Video" ? ".mp4" : category == "Audio" ? ".m4a" : ".bin"
            };
        }

        string baseName = (category == "Video" || category == "Audio") && !string.IsNullOrWhiteSpace(pageTitle)
            ? SanitizeTitle(pageTitle!)
            : SanitizeTitle(rawName);
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "archivo";

        string icon = category switch { "Video" => "🎬", "Audio" => "🎵", "Imagen" => "🖼️", _ => "📄" };

        return new DetectedMediaItem
        {
            Url = url,
            Referer = referer ?? "",
            FileName = baseName + ext,
            Category = category,
            CategoryIcon = icon,
            FormatBadge = ext.TrimStart('.').ToUpperInvariant(),
            SizeBytes = size,
            SizeDisplay = size > 0 ? FormatBytes(size) : "Tamaño desconocido",
            PriorityScore = category == "Video" ? 900 : category == "Audio" ? 700 : 500
        };
    }

    /// <summary>Expande HLS playlist pasando Origin/UA/Cookie reales de la pestaña para CDNs con anti-hotlink.</summary>
    public static async Task<List<DetectedMediaItem>> ExpandHlsPlaylistWithHeadersAsync(
        string m3u8Url, string refererUrl, string pageTitle,
        string? origin, string? userAgent, string? cookie, CancellationToken ct = default)
    {
        var items = new List<DetectedMediaItem>();
        if (!Uri.TryCreate(m3u8Url, UriKind.Absolute, out var baseUri)) return items;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(12));
            using var req = new HttpRequestMessage(HttpMethod.Get, baseUri);
            req.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrWhiteSpace(userAgent) ? BrowserUserAgent : userAgent);
            if (!string.IsNullOrWhiteSpace(refererUrl)) req.Headers.TryAddWithoutValidation("Referer", refererUrl);
            if (!string.IsNullOrWhiteSpace(origin)) req.Headers.TryAddWithoutValidation("Origin", origin);
            if (!string.IsNullOrWhiteSpace(cookie)) req.Headers.TryAddWithoutValidation("Cookie", cookie);

            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode) return items;
            string content = await resp.Content.ReadAsStringAsync(cts.Token);
            return ParseHlsManifestContent(content, baseUri, refererUrl, pageTitle);
        }
        catch (Exception ex)
        {
            LogMediaDiagnostic($"[HLS] No se pudo leer {m3u8Url}: {ex.Message}");
            return items;
        }
    }

    /// <summary>
    /// Resuelve una página embed (Streamwish, Vidhide, Mp4upload, Filemoon...) SIN ejecutar su JS:
    /// descarga el HTML con Referer, desempaqueta eval(function(p,a,c,k,e,d)...) y extrae .m3u8/.mp4.
    /// </summary>
    public static async Task<List<string>> ResolveEmbedPageAsync(string embedUrl, string referer, CancellationToken ct = default)
    {
        var found = new List<string>();
        if (!Uri.TryCreate(embedUrl, UriKind.Absolute, out var baseUri)) return found;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            using var req = new HttpRequestMessage(HttpMethod.Get, baseUri);
            req.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,*/*");
            if (!string.IsNullOrWhiteSpace(referer)) req.Headers.TryAddWithoutValidation("Referer", referer);

            using var resp = await _httpClient.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return found;
            string html = await resp.Content.ReadAsStringAsync(cts.Token);
            if (html.Length > 3_000_000) html = html[..3_000_000];

            var texts = new List<string> { html };
            foreach (Match m in PackedRegex.Matches(html))
            {
                try
                {
                    texts.Add(UnpackPacker(m.Groups[1].Value, int.Parse(m.Groups[2].Value),
                                           int.Parse(m.Groups[3].Value), m.Groups[4].Value.Split('|')));
                }
                catch { }
            }

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in texts)
            {
                string t = raw.Replace("\\/", "/").Replace("\\u0026", "&").Replace("&amp;", "&").Replace("\\'", "'");
                foreach (Match m in AbsMediaRegex.Matches(t)) set.Add(m.Value);
                foreach (Match m in RelMediaRegex.Matches(t))
                {
                    if (Uri.TryCreate(baseUri, m.Groups[1].Value, out var abs)) set.Add(abs.AbsoluteUri);
                }
            }
            found.AddRange(set);
        }
        catch (Exception ex)
        {
            LogMediaDiagnostic($"[Embed] {embedUrl}: {ex.Message}");
        }
        return found;
    }

    /// <summary>Desempaquetador nativo Dean Edwards p.a.c.k.e.r.</summary>
    private static string UnpackPacker(string payload, int radix, int count, string[] dict)
    {
        string ToBase(int n)
        {
            const string digits = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
            if (n == 0) return "0";
            var sb = new StringBuilder();
            while (n > 0) { sb.Insert(0, digits[n % radix]); n /= radix; }
            return sb.ToString();
        }

        var map = new Dictionary<string, string>();
        while (count-- > 0)
        {
            string key = ToBase(count);
            map[key] = count < dict.Length && dict[count].Length > 0 ? dict[count] : key;
        }
        return Regex.Replace(payload.Replace("\\'", "'"), @"\b\w+\b", m => map.TryGetValue(m.Value, out var v) ? v : m.Value);
    }

    public static string SanitizeTitle(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string clean = raw.Trim();
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            clean = clean.Replace(c, '_');
        }
        clean = Regex.Replace(clean, @"\s+", " ").Trim();
        if (clean.Length > 75) clean = clean.Substring(0, 75).Trim();
        return clean;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        if (bytes >= 1024L) return $"{bytes / 1024.0:F0} KB";
        return $"{bytes} B";
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
}

