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
        "/ telemetry",
        "/ptracking",
        "/log_event",
        "/ generate_204",
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

    /// <summary>
    /// Extrae todas las calidades limpias de un video o Short de YouTube (1080p HD, 720p HD, 480p, 360p y Solo Audio M4A)
    /// consultando los clientes InnerTube (VISIONOS / IOS / ANDROID_VR) bajo demanda.
    /// </summary>
    public static async Task<List<DetectedMediaItem>> ExtractYouTubeStreamsAsync(string pageUrl, string? fallbackTitle = null, CancellationToken ct = default)
    {
        var results = new List<DetectedMediaItem>();
        string? videoId = TryExtractYouTubeVideoId(pageUrl);
        if (string.IsNullOrWhiteSpace(videoId)) return results;

        // Intentar clientes InnerTube en orden de fiabilidad de URLs directas sin cifrado ni bloqueo 403
        // 1. VISIONOS (idéntico a Video DownloadHelper 10.6.7.2: devuelve 1080p/720p/480p/360p + Audio M4A + HLS con 200 OK inmediato)
        // 2. IOS
        // 3. ANDROID_VR
        var clients = new (string Name, string ClientNameId, string Version, string UserAgent, object ContextObj)[]
        {
            (
                "VISIONOS",
                "101",
                "1.02",
                VisionOsUserAgent,
                new
                {
                    client = new
                    {
                        clientName = "VISIONOS",
                        clientVersion = "1.02",
                        deviceMake = "Apple",
                        deviceModel = "RealityDevice17,1",
                        osName = "visionOS",
                        osVersion = "26.5.23O471",
                        hl = "es",
                        gl = "US"
                    }
                }
            ),
            (
                "IOS",
                "5",
                "20.11.6",
                IosUserAgent,
                new
                {
                    client = new
                    {
                        clientName = "IOS",
                        clientVersion = "20.11.6",
                        deviceMake = "Apple",
                        deviceModel = "iPhone16,2",
                        osName = "iPhone",
                        osVersion = "18.3.2.22D82",
                        hl = "es",
                        gl = "US"
                    }
                }
            ),
            (
                "ANDROID_VR",
                "28",
                "1.65.10",
                AndroidVrUserAgent,
                new
                {
                    client = new
                    {
                        clientName = "ANDROID_VR",
                        clientVersion = "1.65.10",
                        deviceMake = "Oculus",
                        deviceModel = "Quest 3",
                        osName = "Android",
                        osVersion = "12L",
                        androidSdkVersion = 32,
                        hl = "es",
                        gl = "US"
                    }
                }
            )
        };

        foreach (var clientInfo in clients)
        {
            try
            {
                var payload = new
                {
                    videoId = videoId,
                    context = clientInfo.ContextObj,
                    contentCheckOk = true,
                    racyCheckOk = true
                };

                using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/player?prettyPrint=false");
                req.Headers.TryAddWithoutValidation("User-Agent", clientInfo.UserAgent);
                req.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", clientInfo.ClientNameId);
                req.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", clientInfo.Version);
                req.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
                req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode) continue;

                string json = await resp.Content.ReadAsStringAsync(ct);
                var parsed = ParseYouTubePlayerResponseJson(json, pageUrl, fallbackTitle, clientInfo.UserAgent);
                if (parsed.Count > 0)
                {
                    return parsed;
                }
            }
            catch
            {
                // Intentar siguiente cliente InnerTube
            }
        }

        return results;
    }

    /// <summary>
    /// Parsea la respuesta JSON de InnerTube (/youtubei/v1/player) y construye la lista ordenada de calidades descargables.
    /// </summary>
    public static List<DetectedMediaItem> ParseYouTubePlayerResponseJson(string json, string pageUrl, string? fallbackTitle, string clientUserAgent)
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

            // 1. Buscar la mejor pista de audio M4A (itag 140 = AAC 128kbps en contenedor MP4/M4A)
            string? bestAudioM4aUrl = null;
            long bestAudioM4aBytes = 0;
            int bestAudioBitrate = 0;

            var adaptiveStreams = new List<JsonElement>();
            if (streamingData.TryGetProperty("adaptiveFormats", out var adaptiveArr) && adaptiveArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in adaptiveArr.EnumerateArray())
                {
                    adaptiveStreams.Add(el);
                    string mime = el.TryGetProperty("mimeType", out var mProp) ? (mProp.GetString() ?? "") : "";
                    string directUrl = el.TryGetProperty("url", out var uProp) ? (uProp.GetString() ?? "") : "";
                    if (string.IsNullOrWhiteSpace(directUrl)) continue;

                    if (mime.StartsWith("audio/mp4", StringComparison.OrdinalIgnoreCase))
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
            //    Se priorizan sobre "formats" porque en clientes modernos las pistas adaptativas tienen mucha mejor tasa de bits y 0 bloqueos 403
            if (!string.IsNullOrWhiteSpace(bestAudioM4aUrl))
            {
                int[] targetResolutions = { 1080, 720, 480, 360 };
                foreach (int targetRes in targetResolutions)
                {
                    if (addedHeights.Contains(targetRes)) continue;

                    // Elegir stream video/mp4 con codec avc1 (máxima compatibilidad universal en Windows/móviles)
                    JsonElement? bestMatch = null;
                    int bestBr = 0;

                    foreach (var el in adaptiveStreams)
                    {
                        string mime = el.TryGetProperty("mimeType", out var mProp) ? (mProp.GetString() ?? "") : "";
                        string directUrl = el.TryGetProperty("url", out var uProp) ? (uProp.GetString() ?? "") : "";
                        if (string.IsNullOrWhiteSpace(directUrl) || !mime.StartsWith("video/mp4", StringComparison.OrdinalIgnoreCase))
                            continue;

                        int h = el.TryGetProperty("height", out var hProp) ? hProp.GetInt32() : 0;
                        int w = el.TryGetProperty("width", out var wProp) ? wProp.GetInt32() : 0;
                        int minDim = (w > 0 && h > 0) ? Math.Min(w, h) : h;

                        string qLabel = el.TryGetProperty("qualityLabel", out var qlProp) ? (qlProp.GetString() ?? "") : "";
                        bool matchesRes = (minDim == targetRes) || qLabel.StartsWith($"{targetRes}p", StringComparison.OrdinalIgnoreCase);
                        if (!matchesRes) continue;

                        int br = el.TryGetProperty("bitrate", out var brProp) ? brProp.GetInt32() : 0;
                        bool isAvc1 = mime.Contains("avc1", StringComparison.OrdinalIgnoreCase);
                        int score = (isAvc1 ? 10_000_000 : 0) + br;

                        if (score > bestBr)
                        {
                            bestBr = score;
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

            // 4. Registrar opción de Solo Audio en alta calidad (M4A / AAC)
            if (!string.IsNullOrWhiteSpace(bestAudioM4aUrl))
            {
                results.Add(new DetectedMediaItem
                {
                    Url = bestAudioM4aUrl,
                    Referer = pageUrl,
                    CustomUserAgent = clientUserAgent,
                    IsYouTubeStream = true,
                    FileName = $"{videoTitle} (Audio HQ).m4a",
                    Category = "Audio",
                    CategoryIcon = "🎵",
                    FormatBadge = "AUDIO HQ",
                    SizeBytes = bestAudioM4aBytes,
                    VideoSizeBytes = bestAudioM4aBytes,
                    SizeDisplay = bestAudioM4aBytes > 0 ? $"{FormatBytes(bestAudioM4aBytes)} • Solo Audio (128 kbps)" : "Solo Audio M4A",
                    PriorityScore = 950
                });
            }

            return results.OrderByDescending(x => x.PriorityScore).ToList();
        }
        catch
        {
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
        if (item.IsHlsStream)
        {
            return await DownloadHlsStreamToFileAsync(
                item.Url,
                item.Referer,
                item.CustomUserAgent,
                item.CustomCookieHeader,
                item.CustomOriginHeader,
                targetPath,
                onProgress,
                ct);
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

                // Unir video fMP4 + audio fMP4 en el archivo final .mp4
                await Task.Run(() => MuxFragmentedMp4(tempVideoPath, tempAudioPath, targetPath), ct);

                var fi = new FileInfo(targetPath);
                return fi.Exists ? fi.Length : (videoReceived + audioReceived);
            }
            catch when (!string.IsNullOrWhiteSpace(item.FallbackHlsUrl) && !ct.IsCancellationRequested)
            {
                // Si por algún motivo de red fallara el stream adaptativo y hay manifiesto HLS muxado de respaldo, usarlo
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
        return await DownloadStreamFastAsync(
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

    private readonly struct Mp4BoxInfo
    {
        public readonly string Type;
        public readonly long Offset;
        public readonly long Size;
        public readonly int HeaderSize;

        public Mp4BoxInfo(string type, long offset, long size, int headerSize)
        {
            Type = type;
            Offset = offset;
            Size = size;
            HeaderSize = headerSize;
        }
    }

    /// <summary>
    /// Une un archivo de video MP4 fragmentado (DASH/YouTube) y un archivo de audio M4A fragmentado
    /// en un único contenedor .mp4 con ambas pistas (Video Track ID = 1, Audio Track ID = 2).
    /// </summary>
    public static void MuxFragmentedMp4(string videoPath, string audioPath, string outputPath)
    {
        byte[] videoBytes = File.ReadAllBytes(videoPath);
        byte[] audioBytes = File.ReadAllBytes(audioPath);

        var vBoxes = ParseBoxes(videoBytes, 0, videoBytes.Length);
        var aBoxes = ParseBoxes(audioBytes, 0, audioBytes.Length);

        var vFtyp = vBoxes.FirstOrDefault(b => b.Type == "ftyp");
        var vMoov = vBoxes.FirstOrDefault(b => b.Type == "moov");
        var aMoov = aBoxes.FirstOrDefault(b => b.Type == "moov");

        if (vMoov.Size == 0 || aMoov.Size == 0)
        {
            // Fallback si alguno no es un MP4 válido
            File.Copy(videoPath, outputPath, overwrite: true);
            return;
        }

        // Construir la caja moov combinada con ambas pistas (Video ID=1, Audio ID=2)
        byte[] mergedMoov = BuildMergedMoovBox(videoBytes, vMoov, audioBytes, aMoov);

        using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

        // 1. Escribir ftyp
        if (vFtyp.Size > 0)
        {
            outFs.Write(videoBytes, (int)vFtyp.Offset, (int)vFtyp.Size);
        }

        // 2. Escribir moov combinado
        outFs.Write(mergedMoov, 0, mergedMoov.Length);

        // 3. Recolectar los pares (moof + mdat) de video (track 1) y de audio (parcheado a track 2)
        var vFragments = ExtractMoofMdatPairs(videoBytes, vBoxes);
        var aFragments = ExtractMoofMdatPairs(audioBytes, aBoxes);

        uint seqNumber = 1;
        int vIdx = 0;
        int aIdx = 0;

        // Intercalar fragmentos de video y audio proporcionalmente para reproducción fluida instantánea
        while (vIdx < vFragments.Count || aIdx < aFragments.Count)
        {
            bool takeVideo;
            if (vIdx < vFragments.Count && aIdx < aFragments.Count)
            {
                double vProgress = (double)vIdx / vFragments.Count;
                double aProgress = (double)aIdx / aFragments.Count;
                takeVideo = vProgress <= aProgress;
            }
            else
            {
                takeVideo = vIdx < vFragments.Count;
            }

            if (takeVideo)
            {
                var (moof, mdat) = vFragments[vIdx++];
                PatchMoofSequenceAndTrack(videoBytes, moof, seqNumber++, targetTrackId: 1);
                outFs.Write(videoBytes, (int)moof.Offset, (int)moof.Size);
                outFs.Write(videoBytes, (int)mdat.Offset, (int)mdat.Size);
            }
            else
            {
                var (moof, mdat) = aFragments[aIdx++];
                PatchMoofSequenceAndTrack(audioBytes, moof, seqNumber++, targetTrackId: 2);
                outFs.Write(audioBytes, (int)moof.Offset, (int)moof.Size);
                outFs.Write(audioBytes, (int)mdat.Offset, (int)mdat.Size);
            }
        }
    }

    private static List<Mp4BoxInfo> ParseBoxes(byte[] data, long start, long end)
    {
        var list = new List<Mp4BoxInfo>();
        long pos = start;
        while (pos + 8 <= end)
        {
            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)pos, 4));
            string type = Encoding.ASCII.GetString(data, (int)pos + 4, 4);
            long boxSize = size32;
            int headerSize = 8;

            if (size32 == 1)
            {
                if (pos + 16 > end) break;
                boxSize = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan((int)pos + 8, 8));
                headerSize = 16;
            }
            else if (size32 == 0)
            {
                boxSize = end - pos;
            }

            if (boxSize < headerSize || pos + boxSize > end) break;
            list.Add(new Mp4BoxInfo(type, pos, boxSize, headerSize));
            pos += boxSize;
        }
        return list;
    }

    private static byte[] BuildMergedMoovBox(byte[] vBytes, Mp4BoxInfo vMoov, byte[] aBytes, Mp4BoxInfo aMoov)
    {
        var vChildren = ParseBoxes(vBytes, vMoov.Offset + vMoov.HeaderSize, vMoov.Offset + vMoov.Size);
        var aChildren = ParseBoxes(aBytes, aMoov.Offset + aMoov.HeaderSize, aMoov.Offset + aMoov.Size);

        var vMvhd = vChildren.FirstOrDefault(b => b.Type == "mvhd");
        var vTrak = vChildren.FirstOrDefault(b => b.Type == "trak");
        var aTrak = aChildren.FirstOrDefault(b => b.Type == "trak");
        var vMvex = vChildren.FirstOrDefault(b => b.Type == "mvex");
        var aMvex = aChildren.FirstOrDefault(b => b.Type == "mvex");

        using var ms = new MemoryStream();

        // 1. mvhd (con next_track_ID = 3)
        if (vMvhd.Size > 0)
        {
            byte[] mvhdCopy = vBytes.AsSpan((int)vMvhd.Offset, (int)vMvhd.Size).ToArray();
            if (mvhdCopy.Length >= 8)
            {
                BinaryPrimitives.WriteUInt32BigEndian(mvhdCopy.AsSpan(mvhdCopy.Length - 4, 4), 3u);
            }
            ms.Write(mvhdCopy);
        }

        // 2. Video trak (track_ID = 1)
        if (vTrak.Size > 0)
        {
            byte[] vTrakCopy = vBytes.AsSpan((int)vTrak.Offset, (int)vTrak.Size).ToArray();
            PatchTrakId(vTrakCopy, 1u);
            ms.Write(vTrakCopy);
        }

        // 3. Audio trak (parcheado a track_ID = 2)
        if (aTrak.Size > 0)
        {
            byte[] aTrakCopy = aBytes.AsSpan((int)aTrak.Offset, (int)aTrak.Size).ToArray();
            PatchTrakId(aTrakCopy, 2u);
            ms.Write(aTrakCopy);
        }

        // 4. mvex combinado (trex de video ID=1 + trex de audio ID=2)
        if (vMvex.Size > 0 && aMvex.Size > 0)
        {
            var vMvexChildren = ParseBoxes(vBytes, vMvex.Offset + vMvex.HeaderSize, vMvex.Offset + vMvex.Size);
            var aMvexChildren = ParseBoxes(aBytes, aMvex.Offset + aMvex.HeaderSize, aMvex.Offset + aMvex.Size);

            using var mvexPayload = new MemoryStream();
            foreach (var child in vMvexChildren)
            {
                byte[] copy = vBytes.AsSpan((int)child.Offset, (int)child.Size).ToArray();
                if (child.Type == "trex" && copy.Length >= 16)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(12, 4), 1u);
                }
                mvexPayload.Write(copy);
            }

            foreach (var child in aMvexChildren.Where(b => b.Type == "trex"))
            {
                byte[] copy = aBytes.AsSpan((int)child.Offset, (int)child.Size).ToArray();
                if (copy.Length >= 16)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(12, 4), 2u);
                }
                mvexPayload.Write(copy);
            }

            byte[] mvexBox = WrapMp4Box("mvex", mvexPayload.ToArray());
            ms.Write(mvexBox);
        }

        return WrapMp4Box("moov", ms.ToArray());
    }

    private static void PatchTrakId(byte[] trakBoxBytes, uint trackId)
    {
        var children = ParseBoxes(trakBoxBytes, 8, trakBoxBytes.Length);
        var tkhd = children.FirstOrDefault(b => b.Type == "tkhd");
        if (tkhd.Size == 0) return;

        int payloadOffset = (int)tkhd.Offset + tkhd.HeaderSize;
        if (payloadOffset + 24 > trakBoxBytes.Length) return;

        byte version = trakBoxBytes[payloadOffset];
        // En tkhd: version(1) + flags(3) + creation_time(4 u 8) + modification_time(4 u 8) + track_ID(4)
        int trackIdOffset = version == 1 ? payloadOffset + 4 + 16 : payloadOffset + 4 + 8;
        if (trackIdOffset + 4 <= trakBoxBytes.Length)
        {
            BinaryPrimitives.WriteUInt32BigEndian(trakBoxBytes.AsSpan(trackIdOffset, 4), trackId);
        }
    }

    private static List<(Mp4BoxInfo Moof, Mp4BoxInfo Mdat)> ExtractMoofMdatPairs(byte[] bytes, List<Mp4BoxInfo> topBoxes)
    {
        var pairs = new List<(Mp4BoxInfo, Mp4BoxInfo)>();
        for (int i = 0; i < topBoxes.Count - 1; i++)
        {
            if (topBoxes[i].Type == "moof")
            {
                for (int j = i + 1; j < topBoxes.Count; j++)
                {
                    if (topBoxes[j].Type == "mdat")
                    {
                        pairs.Add((topBoxes[i], topBoxes[j]));
                        i = j;
                        break;
                    }
                    if (topBoxes[j].Type == "moof") break;
                }
            }
        }
        return pairs;
    }

    private static void PatchMoofSequenceAndTrack(byte[] buffer, Mp4BoxInfo moofBox, uint sequenceNumber, uint targetTrackId)
    {
        var children = ParseBoxes(buffer, moofBox.Offset + moofBox.HeaderSize, moofBox.Offset + moofBox.Size);
        foreach (var child in children)
        {
            if (child.Type == "mfhd")
            {
                int seqOffset = (int)child.Offset + child.HeaderSize + 4; // version/flags (4) + sequence_number (4)
                if (seqOffset + 4 <= buffer.Length)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(seqOffset, 4), sequenceNumber);
                }
            }
            else if (child.Type == "traf")
            {
                var trafChildren = ParseBoxes(buffer, child.Offset + child.HeaderSize, child.Offset + child.Size);
                foreach (var tc in trafChildren)
                {
                    if (tc.Type == "tfhd")
                    {
                        int trackOffset = (int)tc.Offset + tc.HeaderSize + 4; // version/flags (4) + track_ID (4)
                        if (trackOffset + 4 <= buffer.Length)
                        {
                            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(trackOffset, 4), targetTrackId);
                        }
                    }
                }
            }
        }
    }

    private static byte[] WrapMp4Box(string type, byte[] payload)
    {
        byte[] result = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0, 4), (uint)result.Length);
        Encoding.ASCII.GetBytes(type, 0, 4, result, 4);
        Buffer.BlockCopy(payload, 0, result, 8, payload.Length);
        return result;
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

