using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LexBrowser.Core.Engine;

/// <summary>
/// Servicio centralizado de favicons y logotipos de sitios web.
/// Garantiza descarga asíncrona a memoria local, congelado seguro (Freeze)
/// para WPF y caché por dominio con cero excepciones de hilo o red.
/// </summary>
public static class FaviconService
{
    private const int MaxCacheEntries = 500;
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromHours(24);

    private static readonly ConcurrentDictionary<string, ImageSource> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Task<ImageSource?>> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DateTime> _failedUntilUtc = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentQueue<string> _cacheOrder = new();

    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    static FaviconService()
    {
        try
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }
        catch { }
    }

    /// <summary>
    /// Limpia la caché en memoria de favicons y fallos recientes.
    /// </summary>
    public static void ClearCache()
    {
        _cache.Clear();
        _failedUntilUtc.Clear();
        while (_cacheOrder.TryDequeue(out _)) { }
    }

    private static void StoreInCache(string domain, ImageSource icon)
    {
        if (_cache.TryAdd(domain, icon))
        {
            _cacheOrder.Enqueue(domain);
            while (_cache.Count > MaxCacheEntries && _cacheOrder.TryDequeue(out string? oldest))
            {
                _cache.TryRemove(oldest, out _);
            }
        }
        else
        {
            _cache[domain] = icon;
        }
        _failedUntilUtc.TryRemove(domain, out _);
    }

    /// <summary>
    /// Obtiene de forma síncrona el icono si ya está en caché, o null si aún no se ha descargado.
    /// </summary>
    public static ImageSource? GetCachedFavicon(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return null;
        _cache.TryGetValue(NormalizeDomain(domain), out var cached);
        return cached;
    }

    /// <summary>
    /// Resuelve el favicon oficial de una página web con deduplicación de peticiones en vuelo,
    /// caché negativa de 24h y respeto por la configuración de privacidad de servicios externos.
    /// </summary>
    public static async Task<ImageSource?> ResolveFaviconAsync(string pageUrl, Stream? webViewStream = null, string? faviconUri = null, bool isIncognito = false)
    {
        if (string.IsNullOrWhiteSpace(pageUrl)) return null;

        string domain = ExtractDomain(pageUrl);
        if (string.IsNullOrEmpty(domain)) return null;

        // 1. Revisar caché en memoria (solo si no es incógnito)
        if (!isIncognito && _cache.TryGetValue(domain, out var cached))
        {
            return cached;
        }

        bool hasExplicitTabIcon = webViewStream != null || !string.IsNullOrWhiteSpace(faviconUri);

        // Si ya falló recientemente y no trae un stream/URI explícito nuevo, evitar tormenta de red
        if (!isIncognito && !hasExplicitTabIcon &&
            _failedUntilUtc.TryGetValue(domain, out DateTime retryAfterUtc) &&
            DateTime.UtcNow < retryAfterUtc)
        {
            return null;
        }

        // En modo normal sin stream propio, deduplicar todas las solicitudes concurrentes al mismo dominio
        if (!isIncognito && webViewStream == null)
        {
            string flightKey = string.IsNullOrWhiteSpace(faviconUri) ? domain : $"{domain}|{faviconUri}";
            var task = _inFlight.GetOrAdd(flightKey, _ => ResolveFaviconCoreAsync(pageUrl, domain, null, faviconUri, isIncognito: false));
            try
            {
                return await task.ConfigureAwait(false);
            }
            finally
            {
                _inFlight.TryRemove(flightKey, out _);
            }
        }

        return await ResolveFaviconCoreAsync(pageUrl, domain, webViewStream, faviconUri, isIncognito).ConfigureAwait(false);
    }

    private static async Task<ImageSource?> ResolveFaviconCoreAsync(
        string pageUrl,
        string domain,
        Stream? webViewStream,
        string? faviconUri,
        bool isIncognito)
    {
        BitmapImage? candidate = null;

        // Resolver faviconUri relativo contra la URL base de la página si es necesario
        string? resolvedFaviconUri = faviconUri;
        if (!string.IsNullOrWhiteSpace(resolvedFaviconUri))
        {
            if (!Uri.TryCreate(resolvedFaviconUri, UriKind.Absolute, out _) && Uri.TryCreate(pageUrl, UriKind.Absolute, out var baseUri))
            {
                if (Uri.TryCreate(baseUri, resolvedFaviconUri, out var combinedUri))
                {
                    resolvedFaviconUri = combinedUri.AbsoluteUri;
                }
            }
        }

        // 2. Si el motor informó una FaviconUri explícita (<link rel="icon"> en el HTML), descargarla primero
        if (!isIncognito && !string.IsNullOrWhiteSpace(resolvedFaviconUri) && !resolvedFaviconUri.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                candidate = await DownloadAndCreateBitmapAsync(resolvedFaviconUri).ConfigureAwait(false);
            }
            catch { }
        }

        // En modo incógnito, nunca consultar servicios externos ni filtrar el dominio a terceros
        if (isIncognito)
        {
            if (candidate != null) return candidate;
            if (!string.IsNullOrWhiteSpace(resolvedFaviconUri) && resolvedFaviconUri.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            {
                try { return await DownloadAndCreateBitmapAsync(resolvedFaviconUri).ConfigureAwait(false); } catch { }
            }
            if (webViewStream != null)
            {
                try
                {
                    using var ms = new MemoryStream();
                    await webViewStream.CopyToAsync(ms).ConfigureAwait(false);
                    if (ms.Length > 0)
                    {
                        ms.Position = 0;
                        return CreateFrozenBitmapFromMemory(ms);
                    }
                }
                catch { }
            }
            return null;
        }

        // 3. Servicio externo de iconos de alta resolución (solo si está habilitado en ajustes y no es un host local/privado)
        if (candidate == null &&
            SettingsManager.Current.EnableExternalFaviconService &&
            !OmniboxInputResolver.IsLocalOrPrivateHost(domain))
        {
            try
            {
                string googleFaviconUrl = $"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(domain)}&sz=32";
                candidate = await DownloadAndCreateBitmapAsync(googleFaviconUrl).ConfigureAwait(false);
            }
            catch { }
        }

        // 4. Fallback directo al propio servidor de origen (https://domain/favicon.ico)
        if (candidate == null && !OmniboxInputResolver.IsLocalOrPrivateHost(domain))
        {
            try
            {
                string directIconUrl = $"https://{domain}/favicon.ico";
                candidate = await DownloadAndCreateBitmapAsync(directIconUrl).ConfigureAwait(false);
            }
            catch { }
        }

        // 5. Probar el stream directo devuelto por WebView2 (para intranets, localhost o sitios cerrados)
        if (candidate == null && webViewStream != null)
        {
            try
            {
                using var ms = new MemoryStream();
                await webViewStream.CopyToAsync(ms).ConfigureAwait(false);
                if (ms.Length > 0)
                {
                    ms.Position = 0;
                    candidate = CreateFrozenBitmapFromMemory(ms);
                }
            }
            catch { }
        }

        if (candidate != null)
        {
            StoreInCache(domain, candidate);
        }
        else
        {
            _failedUntilUtc[domain] = DateTime.UtcNow.Add(NegativeCacheTtl);
        }

        return candidate;
    }

    private static BitmapImage? CreateFrozenBitmapFromMemory(MemoryStream ms)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Crea un BitmapImage congelado a partir de cualquier Stream copiándolo a un MemoryStream local.
    /// </summary>
    private static async Task<BitmapImage?> CreateFrozenBitmapFromStreamAsync(Stream sourceStream)
    {
        try
        {
            using var ms = new MemoryStream();
            await sourceStream.CopyToAsync(ms);
            if (ms.Length == 0) return null;

            ms.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze(); // 100% seguro porque los bytes están en memoria local
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Descarga los bytes de una URI de imagen remota (o Data URI) y los convierte a BitmapImage congelado.
    /// </summary>
    public static async Task<BitmapImage?> DownloadAndCreateBitmapAsync(string uriString)
    {
        if (string.IsNullOrWhiteSpace(uriString)) return null;

        try
        {
            // Manejo de data URIs (ej. data:image/png;base64,...)
            if (uriString.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            {
                int commaIdx = uriString.IndexOf(',');
                if (commaIdx > 0)
                {
                    string base64 = uriString[(commaIdx + 1)..];
                    byte[] bytes = Convert.FromBase64String(base64);
                    using var ms = new MemoryStream(bytes);
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = ms;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return bitmap;
                }
            }

            if (!Uri.TryCreate(uriString, UriKind.Absolute, out var uri)) return null;

            byte[] data = await _httpClient.GetByteArrayAsync(uri);
            if (data == null || data.Length == 0) return null;

            using var memStream = new MemoryStream(data);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = memStream;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public static string ExtractDomain(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return NormalizeDomain(uri.Host);
            }
        }
        catch { }
        return string.Empty;
    }

    private static string NormalizeDomain(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return string.Empty;
        host = host.ToLowerInvariant().Trim();
        if (host.StartsWith("www.")) host = host[4..];
        return host;
    }
}

