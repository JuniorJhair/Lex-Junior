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
    private static readonly ConcurrentDictionary<string, ImageSource> _cache = new(StringComparer.OrdinalIgnoreCase);
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
    /// Limpia la caché en memoria de favicons.
    /// </summary>
    public static void ClearCache() => _cache.Clear();

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
    /// Resuelve el favicon oficial de una página web usando primero el stream de WebView2,
    /// luego la URI del favicon, y finalmente el favicon directo del servidor.
    /// En modo incógnito no almacena en la caché compartida ni realiza peticiones a servicios de terceros.
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

        // 2. Intentar decodificar el stream directo devuelto por WebView2
        if (webViewStream != null)
        {
            try
            {
                var bmp = await CreateFrozenBitmapFromStreamAsync(webViewStream);
                if (bmp != null)
                {
                    if (!isIncognito) _cache[domain] = bmp;
                    return bmp;
                }
            }
            catch { }
        }

        // En modo incógnito, nunca hacer peticiones HTTP fuera del WebView2 InPrivate ni filtrar dominios
        if (isIncognito)
        {
            if (!string.IsNullOrWhiteSpace(faviconUri) && faviconUri.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            {
                try { return await DownloadAndCreateBitmapAsync(faviconUri); } catch { }
            }
            return null;
        }

        // 3. Intentar descargar desde la FaviconUri informada por el motor
        if (!string.IsNullOrWhiteSpace(faviconUri))
        {
            try
            {
                var bmp = await DownloadAndCreateBitmapAsync(faviconUri);
                if (bmp != null)
                {
                    _cache[domain] = bmp;
                    return bmp;
                }
            }
            catch { }
        }

        // 4. Fallback directo al propio servidor de origen (sin terceros)
        try
        {
            string directIconUrl = $"https://{domain}/favicon.ico";
            var bmp = await DownloadAndCreateBitmapAsync(directIconUrl);
            if (bmp != null)
            {
                _cache[domain] = bmp;
                return bmp;
            }
        }
        catch { }

        return null;
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

