using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LexBrowser.Core.Tv;

namespace LexBrowser.Core.Engine;

/// <summary>
/// Servicio centralizado de favicons y logotipos de sitios web.
/// Protege contra SSRF y DNS rebinding (resolución única, validación de todas las IP,
/// conexión directa a la IP validada, UseProxy=false, redirecciones validadas manualmente),
/// impone límite de 512 KB, decodificación acotada a 64x64 máx., compuerta de arranque y caché RAM.
/// </summary>
public static class FaviconService
{
    private const int MaxCacheEntries = 500;
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromHours(24);

    public const int MaxFaviconBytes = 512 * 1024;
    public const int MaxDataUriChars = 750_000;
    public const int MaxFaviconDecodePixels = 64;
    public const int MaxFaviconRedirects = 5;

    private static readonly ConcurrentDictionary<string, ImageSource> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Task<ImageSource?>> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DateTime> _failedUntilUtc = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentQueue<string> _cacheOrder = new();

    private static volatile bool _isStartupNetworkGateOpen = true;

    public static bool IsStartupNetworkGateOpen => _isStartupNetworkGateOpen;

    /// <summary>
    /// Abre o cierra la compuerta de resoluciones de red en el arranque (p. ej. mientras
    /// la pantalla de bloqueo o el selector inicial de perfiles están pendientes).
    /// </summary>
    public static void SetStartupNetworkGate(bool isOpen)
    {
        _isStartupNetworkGateOpen = isOpen;
    }

    private static readonly HttpClient _httpClient = CreatePinnedFaviconHttpClient();

    /// <summary>
    /// Crea un HttpClient con SocketsHttpHandler endurecido anti-DNS-rebinding:
    /// - UseProxy = false para impedir que un proxy evada ConnectCallback.
    /// - AllowAutoRedirect = false para revalidar host e IP en cada salto manualmente.
    /// - ConnectCallback resuelve DNS una sola vez, valida TODAS las IPs resultantes (si una es privada/local rechaza todo)
    ///   y abre el socket TCP directamente a la IP validada conservando Host y SNI originales para TLS.
    /// </summary>
    internal static HttpClient CreatePinnedFaviconHttpClient(
        Func<string, CancellationToken, Task<IPAddress[]>>? dnsResolver = null,
        Func<IPAddress, bool>? allowedIpPredicate = null,
        Action<IPEndPoint>? onConnectedEndpoint = null,
        TimeSpan? timeout = null)
    {
        var ipAllowed = allowedIpPredicate ?? (static ip => !TvUrlGuard.IsPrivateIp(ip));

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            Proxy = null,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectCallback = async (context, ct) =>
            {
                string host = context.DnsEndPoint.Host;
                IPAddress[] resolved = await ResolveAndValidateAllAddressesAsync(host, dnsResolver, ipAllowed, ct).ConfigureAwait(false);

                Exception? lastEx = null;
                foreach (var validatedIp in resolved)
                {
                    var remoteEp = new IPEndPoint(validatedIp, context.DnsEndPoint.Port);
                    var socket = new Socket(validatedIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                    {
                        NoDelay = true
                    };
                    try
                    {
                        await socket.ConnectAsync(remoteEp, ct).ConfigureAwait(false);
                        onConnectedEndpoint?.Invoke(remoteEp);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex)
                    {
                        lastEx = ex;
                        socket.Dispose();
                    }
                }

                if (lastEx is SocketException se)
                    throw se;
                throw lastEx ?? new SocketException((int)SocketError.HostUnreachable);
            }
        };

        var client = new HttpClient(handler)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(3)
        };
        try
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }
        catch { }

        return client;
    }

    internal static async Task<IPAddress[]> ResolveAndValidateAllAddressesAsync(
        string host,
        Func<string, CancellationToken, Task<IPAddress[]>>? dnsResolver,
        Func<IPAddress, bool> ipAllowed,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new TvProxyErrorException("blocked", 502, "Host vacío bloqueado.");

        IPAddress[] raw;
        string trimmed = host.Trim().Trim('[', ']');
        if (IPAddress.TryParse(trimmed, out var literal))
        {
            raw = new[] { literal };
        }
        else
        {
            raw = dnsResolver != null
                ? await dnsResolver(trimmed, ct).ConfigureAwait(false)
                : await Dns.GetHostAddressesAsync(trimmed, ct).ConfigureAwait(false);
        }

        if (raw == null || raw.Length == 0)
            throw new TvProxyErrorException("dns", 502, "El host no devolvió direcciones IP.");

        // Validar TODAS las IPs resultantes: si CUALQUIERA es privada/no permitida, rechazar la solicitud completa.
        var validated = new List<IPAddress>(raw.Length);
        foreach (var ip in raw)
        {
            if (ip == null || !ipAllowed(ip))
            {
                throw new TvProxyErrorException("blocked", 502, "Resolución DNS contiene una dirección IP privada o reservada.");
            }
            validated.Add(ip);
        }

        return validated.ToArray();
    }

    /// <summary>
    /// Valida estrictamente la URL de destino para favicons:
    /// - Solo http:// o https:// sin credenciales (UserInfo)
    /// - Solo puertos estándar (80/443) salvo override en arnés de prueba local
    /// - Rechaza hosts locales/privados/especiales y representaciones IP numéricas alternativas (decimal, octal, hex)
    /// - Delega comprobación de IPv4/IPv6 privadas y denylist a TvUrlGuard
    /// </summary>
    internal static bool IsAllowedFaviconUrl(
        string? rawUrl,
        out Uri? safeUri,
        bool allowLoopbackHostForLocalTestHarness = false,
        bool allowCustomPortForLocalTestHarness = false)
    {
        safeUri = null;
        if (string.IsNullOrWhiteSpace(rawUrl) || rawUrl.Length > 2048)
            return false;

        string trimmed = rawUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var u))
            return false;

        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)
            return false;

        if (!string.IsNullOrEmpty(u.UserInfo))
            return false;

        if (!allowCustomPortForLocalTestHarness)
        {
            int expectedPort = u.Scheme == Uri.UriSchemeHttps ? 443 : 80;
            if (!u.IsDefaultPort || u.Port != expectedPort)
                return false;
        }

        // Inspeccionar también la autoridad textual antes de normalización por Uri para atrapar IP decimal/octal/hex
        string? authorityHost = ExtractRawAuthorityHost(trimmed);
        if (!string.IsNullOrEmpty(authorityHost) && LooksLikeAlternativeNumericIpv4Host(authorityHost))
            return false;

        string cleanHost = u.Host.Trim().Trim('[', ']');
        if (LooksLikeAlternativeNumericIpv4Host(cleanHost))
            return false;

        if (allowLoopbackHostForLocalTestHarness &&
            !TvUrlGuard.IsPrivateOrLocalHost(cleanHost))
        {
            // Permite dominios sintéticos en pruebas (ej. pinned.example.test:puerto) sin saltar validación en ConnectCallback
            safeUri = u;
            return true;
        }

        if (!TvUrlGuard.IsAllowedRemoteUrl(trimmed, out var tvSafe) || tvSafe == null)
            return false;

        safeUri = tvSafe;
        return true;
    }

    private static string? ExtractRawAuthorityHost(string url)
    {
        int schemeSep = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeSep < 0) return null;
        int start = schemeSep + 3;
        int end = url.IndexOfAny(new[] { '/', '?', '#' }, start);
        string authority = end >= 0 ? url[start..end] : url[start..];

        int atIdx = authority.LastIndexOf('@');
        if (atIdx >= 0) authority = authority[(atIdx + 1)..];

        if (authority.StartsWith("[", StringComparison.Ordinal))
        {
            int close = authority.IndexOf(']');
            return close > 0 ? authority[..(close + 1)] : authority;
        }

        int colonIdx = authority.IndexOf(':');
        return colonIdx >= 0 ? authority[..colonIdx] : authority;
    }

    /// <summary>
    /// Detecta representaciones IPv4 no estándar (decimal entero 2130706433, hexadecimal 0x7f000001,
    /// octal con ceros a la izquierda 0177.0.0.1 o abreviada tipo 127.1).
    /// </summary>
    internal static bool LooksLikeAlternativeNumericIpv4Host(string host)
    {
        string h = host.Trim().Trim('[', ']');
        if (string.IsNullOrEmpty(h)) return true;

        // Cualquier host puramente hexadecimal/decimal sin puntos (ej. 2130706433, 0x7f000001, 0177)
        if (!h.Contains('.'))
        {
            if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return ulong.TryParse(h[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _);
            return ulong.TryParse(h, NumberStyles.None, CultureInfo.InvariantCulture, out _);
        }

        string[] parts = h.Split('.');
        if (parts.Length <= 4)
        {
            bool allNumericLabels = true;
            bool hasNonCanonicalNumeric = false;
            foreach (string part in parts)
            {
                if (string.IsNullOrEmpty(part))
                {
                    allNumericLabels = false;
                    break;
                }

                if (part.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    if (ulong.TryParse(part[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _))
                    {
                        hasNonCanonicalNumeric = true;
                        continue;
                    }
                    allNumericLabels = false;
                    break;
                }

                if (ulong.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    if (part.Length > 1 && part[0] == '0')
                        hasNonCanonicalNumeric = true;
                    continue;
                }

                allNumericLabels = false;
                break;
            }

            if (allNumericLabels && (hasNonCanonicalNumeric || parts.Length < 4))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Limpia la caché en memoria de favicons y fallos recientes (caché negativa de 24h).
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
    /// Obtiene de forma síncrona el icono si ya está en caché RAM, o null si no se ha descargado.
    /// </summary>
    public static ImageSource? GetCachedFavicon(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return null;
        _cache.TryGetValue(NormalizeDomain(domain), out var cached);
        return cached;
    }

    /// <summary>
    /// Resuelve el favicon de una página respetando:
    /// (1) Modo Incógnito / Invitado, (2) compuerta de arranque, (3) toggles granulares de red,
    /// (4) protección anti-SSRF / anti-DNS-rebinding, límite de 512 KB y decodificación acotada.
    /// </summary>
    public static async Task<ImageSource?> ResolveFaviconAsync(string pageUrl, Stream? webViewStream = null, string? faviconUri = null, bool isIncognito = false)
    {
        if (string.IsNullOrWhiteSpace(pageUrl)) return null;

        string domain = ExtractDomain(pageUrl);
        if (string.IsNullOrEmpty(domain)) return null;

        // Favicon nativo de alta resolución para Lex TV (tv.lex.local)
        if (string.Equals(domain, "tv.lex.local", StringComparison.OrdinalIgnoreCase))
        {
            if (_cache.TryGetValue("tv.lex.local", out var tvCached)) return tvCached;
            try
            {
                string tvIconPath = Path.Combine(Tv.TvStudioService.GetTvAssetsFolder(), "favicon.png");
                if (File.Exists(tvIconPath))
                {
                    byte[] tvBytes = File.ReadAllBytes(tvIconPath);
                    if (tvBytes.Length > 0 && tvBytes.Length <= MaxFaviconBytes)
                    {
                        using var ms = new MemoryStream(tvBytes);
                        var img = CreateFrozenBitmapFromMemory(ms);
                        if (img != null)
                        {
                            StoreInCache("tv.lex.local", img);
                            return img;
                        }
                    }
                }
            }
            catch { }
        }

        // 1. Revisar caché en memoria (solo si no es incógnito)
        if (!isIncognito && _cache.TryGetValue(domain, out var cached))
        {
            return cached;
        }

        bool networkAllowed = _isStartupNetworkGateOpen;
        bool hasDataUri = !string.IsNullOrWhiteSpace(faviconUri) && faviconUri.StartsWith("data:image", StringComparison.OrdinalIgnoreCase);

        if (!networkAllowed && webViewStream == null && !hasDataUri)
        {
            return null;
        }

        bool hasExplicitTabIcon = webViewStream != null || !string.IsNullOrWhiteSpace(faviconUri);

        // Si ya falló recientemente y no trae un stream/URI explícito nuevo, evitar tormenta de red
        if (!isIncognito && !hasExplicitTabIcon && networkAllowed &&
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
        bool networkAllowed = _isStartupNetworkGateOpen;
        var settings = SettingsManager.Current;

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

        bool isDataUri = !string.IsNullOrWhiteSpace(resolvedFaviconUri) &&
                         resolvedFaviconUri.StartsWith("data:image", StringComparison.OrdinalIgnoreCase);

        if (isDataUri)
        {
            try
            {
                candidate = CreateBitmapFromDataUri(resolvedFaviconUri!);
            }
            catch { }
        }

        // En modo incógnito, nunca consultar servicios externos ni descargar vía HttpClient C#
        if (isIncognito)
        {
            if (candidate != null) return candidate;
            if (webViewStream != null)
            {
                try
                {
                    var img = await CreateFrozenBitmapFromStreamAsync(webViewStream).ConfigureAwait(false);
                    if (img != null) return img;
                }
                catch { }
            }
            return null;
        }

        // 2. Descarga controlada de <link rel="icon"> desde C#
        if (candidate == null &&
            networkAllowed &&
            settings.EnableLinkFaviconDownload &&
            !string.IsNullOrWhiteSpace(resolvedFaviconUri) &&
            !isDataUri &&
            !resolvedFaviconUri.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                candidate = await DownloadAndCreateBitmapAsync(resolvedFaviconUri).ConfigureAwait(false);
            }
            catch { }
        }

        // 3. Servicio externo Google Favicon API
        if (candidate == null &&
            networkAllowed &&
            settings.EnableExternalFaviconService &&
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
        if (candidate == null &&
            networkAllowed &&
            settings.EnableDirectFaviconIco &&
            !OmniboxInputResolver.IsLocalOrPrivateHost(domain))
        {
            try
            {
                string directIconUrl = $"https://{domain}/favicon.ico";
                candidate = await DownloadAndCreateBitmapAsync(directIconUrl).ConfigureAwait(false);
            }
            catch { }
        }

        // 5. Stream directo devuelto por WebView2 (funciona siempre, con límite de bytes y decodificación acotada)
        if (candidate == null && webViewStream != null)
        {
            try
            {
                candidate = await CreateFrozenBitmapFromStreamAsync(webViewStream).ConfigureAwait(false);
            }
            catch { }
        }

        if (candidate != null)
        {
            StoreInCache(domain, candidate);
        }
        else if (networkAllowed && (settings.EnableLinkFaviconDownload || settings.EnableDirectFaviconIco || settings.EnableExternalFaviconService))
        {
            _failedUntilUtc[domain] = DateTime.UtcNow.Add(NegativeCacheTtl);
        }

        return candidate;
    }

    internal static BitmapImage? CreateBitmapFromDataUri(string uriString)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(uriString) ||
                uriString.Length > MaxDataUriChars ||
                !uriString.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            int commaIdx = uriString.IndexOf(',');
            if (commaIdx <= 0 || commaIdx >= uriString.Length - 1)
                return null;

            string header = uriString[..commaIdx];
            if (!header.Contains(";base64", StringComparison.OrdinalIgnoreCase))
                return null;

            string base64 = uriString[(commaIdx + 1)..];
            byte[] bytes = Convert.FromBase64String(base64);
            if (bytes.Length == 0 || bytes.Length > MaxFaviconBytes)
                return null;

            using var ms = new MemoryStream(bytes);
            return CreateFrozenBitmapFromMemory(ms);
        }
        catch
        {
            return null;
        }
    }

    internal static BitmapImage? CreateFrozenBitmapFromMemory(MemoryStream ms)
    {
        try
        {
            if (ms == null || ms.Length == 0 || ms.Length > MaxFaviconBytes)
                return null;

            int decodeW = MaxFaviconDecodePixels;
            int decodeH = MaxFaviconDecodePixels;

            try
            {
                ms.Position = 0;
                var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                if (decoder.Frames.Count > 0)
                {
                    int srcW = decoder.Frames[0].PixelWidth;
                    int srcH = decoder.Frames[0].PixelHeight;
                    if (srcW > 0 && srcH > 0)
                    {
                        if (srcW <= MaxFaviconDecodePixels && srcH <= MaxFaviconDecodePixels)
                        {
                            decodeW = srcW;
                            decodeH = srcH;
                        }
                        else
                        {
                            double scale = Math.Min((double)MaxFaviconDecodePixels / srcW, (double)MaxFaviconDecodePixels / srcH);
                            decodeW = Math.Clamp((int)Math.Round(srcW * scale), 1, MaxFaviconDecodePixels);
                            decodeH = Math.Clamp((int)Math.Round(srcH * scale), 1, MaxFaviconDecodePixels);
                        }
                    }
                }
            }
            catch
            {
                decodeW = MaxFaviconDecodePixels;
                decodeH = MaxFaviconDecodePixels;
            }

            ms.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = decodeW;
            bitmap.DecodePixelHeight = decodeH;
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
    /// Crea un BitmapImage congelado a partir de cualquier Stream imponiendo el tope de 512 KB y 64x64 px.
    /// </summary>
    internal static async Task<BitmapImage?> CreateFrozenBitmapFromStreamAsync(Stream sourceStream)
    {
        try
        {
            if (sourceStream == null) return null;
            using var bounded = new BoundedReadStream(sourceStream, MaxFaviconBytes);
            using var ms = new MemoryStream();
            await bounded.CopyToAsync(ms).ConfigureAwait(false);
            if (ms.Length == 0 || ms.Length > MaxFaviconBytes) return null;

            ms.Position = 0;
            return CreateFrozenBitmapFromMemory(ms);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Descarga los bytes de una URL pública remota validando anti-SSRF y anti-DNS-rebinding en URL inicial y en cada redirección,
    /// imponiendo límite de 512 KB en streaming y tope de saltos de redirección.
    /// </summary>
    internal static async Task<byte[]?> DownloadRemoteFaviconBytesAsync(
        string uriString,
        HttpClient? customHttpClient = null,
        bool allowLoopbackHostForLocalTestHarness = false,
        bool allowCustomPortForLocalTestHarness = false,
        CancellationToken ct = default)
    {
        if (!IsAllowedFaviconUrl(uriString, out var currentUri, allowLoopbackHostForLocalTestHarness, allowCustomPortForLocalTestHarness) || currentUri == null)
            return null;

        HttpClient client = customHttpClient ?? _httpClient;

        for (int hop = 0; hop <= MaxFaviconRedirects; hop++)
        {
            if (!IsAllowedFaviconUrl(currentUri.AbsoluteUri, out var verifiedUri, allowLoopbackHostForLocalTestHarness, allowCustomPortForLocalTestHarness) || verifiedUri == null)
                return null;

            using var req = new HttpRequestMessage(HttpMethod.Get, verifiedUri);
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            int code = (int)resp.StatusCode;
            if (code is 301 or 302 or 303 or 307 or 308 && resp.Headers.Location != null)
            {
                Uri next = resp.Headers.Location.IsAbsoluteUri
                    ? resp.Headers.Location
                    : new Uri(verifiedUri, resp.Headers.Location);

                // Bloquear degradación de esquema (https -> http) en redirecciones
                if (string.Equals(verifiedUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(next.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    return null;

                // Al salir en redirección, exigir validación estándar estricta (nunca saltar a privados/loopback)
                if (!IsAllowedFaviconUrl(next.AbsoluteUri, out var safeNext, allowLoopbackHostForLocalTestHarness: false, allowCustomPortForLocalTestHarness: allowCustomPortForLocalTestHarness) || safeNext == null)
                    return null;

                currentUri = safeNext;
                continue;
            }

            if (!resp.IsSuccessStatusCode)
                return null;

            if (resp.Content.Headers.ContentLength is long contentLength && contentLength > MaxFaviconBytes)
                return null;

            await using var netStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var bounded = new BoundedReadStream(netStream, MaxFaviconBytes);
            using var ms = new MemoryStream();
            await bounded.CopyToAsync(ms, 16384, ct).ConfigureAwait(false);

            if (ms.Length == 0 || ms.Length > MaxFaviconBytes)
                return null;

            return ms.ToArray();
        }

        return null;
    }

    /// <summary>
    /// Descarga los bytes de una URI de imagen remota (o Data URI) validando seguridad SSRF, tamaño y resolución.
    /// </summary>
    public static async Task<BitmapImage?> DownloadAndCreateBitmapAsync(string uriString)
    {
        if (string.IsNullOrWhiteSpace(uriString)) return null;

        try
        {
            if (uriString.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            {
                return CreateBitmapFromDataUri(uriString);
            }

            byte[]? data = await DownloadRemoteFaviconBytesAsync(uriString).ConfigureAwait(false);
            if (data == null || data.Length == 0 || data.Length > MaxFaviconBytes) return null;

            using var memStream = new MemoryStream(data);
            return CreateFrozenBitmapFromMemory(memStream);
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
