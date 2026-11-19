using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using LexBrowser.Core.Tv;
using Microsoft.Web.WebView2.Core;

namespace LexBrowser.Core.Pdf;

internal sealed record RemotePdfFetchResult(
    int StatusCode,
    string ReasonPhrase,
    string ContentType,
    byte[] Bytes,
    string? ErrorReason = null);

/// <summary>
/// Servicio de integración y motor de servidor virtual para Lex PDF Studio.
/// Permite edición nativa de documentos PDF con herramientas de dibujo a mano alzada,
/// resaltador, ajuste de grosor, paleta de colores, borrador, texto, firmas y guardado.
/// 100% offline, bajo demanda y sin dependencias externas.
/// </summary>
public static class PdfStudioService
{
    public const string HostName = "pdf.lex.local";
    public const string VirtualOrigin = "https://pdf.lex.local/";
    private const string AllowedCorsOrigin = "https://pdf.lex.local";
    private const string LocalPdfRoute = "api/load-local-pdf";
    private const string RemotePdfRoute = "api/load-remote-pdf";
    internal const int MaxRemotePdfBytes = 100 * 1024 * 1024;
    private const int MaxRemoteRedirects = 8;

    private static readonly byte[] PdfMagicPrefix = "%PDF-"u8.ToArray();

    private static readonly Lazy<HttpClient> _publicPdfHttp =
        new(() => TvUrlGuard.CreatePinnedHttpClient(timeout: TimeSpan.FromSeconds(60)));

    private static readonly Lazy<HttpClient> _privatePdfHttp =
        new(() =>
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                ConnectTimeout = TimeSpan.FromSeconds(10)
            };
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        });

    private static string? _cachedPdfFolder;

    // Registro en memoria de archivos PDF locales y URLs remotas explícitamente abiertos por el usuario en la sesión.
    // Impide que sitios web externos lean archivos del disco o utilicen el visor como proxy abierto SSRF.
    private static readonly ConcurrentDictionary<string, byte> _authorizedLocalFiles =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, byte> _authorizedRemoteUrls =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Autoriza explícitamente la lectura de un archivo PDF local en el visor interno.
    /// Solo acepta rutas válidas con extensión .pdf.
    /// </summary>
    public static bool AuthorizeLocalFile(string? localFilePath)
    {
        if (!TryNormalizeLocalPdfPath(localFilePath, out string normalizedPath))
            return false;

        _authorizedLocalFiles[normalizedPath] = 1;
        return true;
    }

    /// <summary>
    /// Verifica si un archivo PDF local fue autorizado explícitamente por el usuario.
    /// </summary>
    public static bool IsLocalFileAuthorized(string? localFilePath)
    {
        if (!TryNormalizeLocalPdfPath(localFilePath, out string normalizedPath))
            return false;

        return _authorizedLocalFiles.ContainsKey(normalizedPath);
    }

    /// <summary>
    /// Autoriza explícitamente la carga en el visor interno de una URL HTTP/HTTPS de PDF.
    /// </summary>
    public static bool AuthorizeRemoteUrl(string? remoteUrl)
    {
        if (!TryNormalizeRemotePdfUrl(remoteUrl, out string normalizedUrl, out _))
            return false;

        _authorizedRemoteUrls[normalizedUrl] = 1;
        return true;
    }

    /// <summary>
    /// Verifica si una URL remota de PDF fue autorizada explícitamente por el usuario.
    /// </summary>
    public static bool IsRemoteUrlAuthorized(string? remoteUrl)
    {
        if (!TryNormalizeRemotePdfUrl(remoteUrl, out string normalizedUrl, out _))
            return false;

        return _authorizedRemoteUrls.ContainsKey(normalizedUrl);
    }

    public static void ClearAuthorizedFiles()
    {
        _authorizedLocalFiles.Clear();
        _authorizedRemoteUrls.Clear();
    }

    internal static bool TryNormalizeRemotePdfUrl(string? rawUrl, out string normalizedUrl, out Uri? parsedUri)
    {
        normalizedUrl = string.Empty;
        parsedUri = null;
        if (string.IsNullOrWhiteSpace(rawUrl) || rawUrl.Length > 4096)
            return false;

        if (!Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var uri))
            return false;

        if (!IsSafeExternalHttpUri(uri))
            return false;

        normalizedUrl = uri.GetLeftPart(UriPartial.Query);
        parsedUri = uri;
        return true;
    }

    private static bool IsSafeExternalHttpUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        if (!string.IsNullOrEmpty(uri.UserInfo))
            return false;

        if (uri.Host.Equals(HostName, StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals(TvUrlGuard.InternalHost, StringComparison.OrdinalIgnoreCase))
            return false;

        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip))
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && ip.IsIPv6LinkLocal)
                return false;
            byte[] bytes = ip.GetAddressBytes();
            if (bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Indica si la URL pertenece estrictamente al visor interno https://pdf.lex.local.
    /// </summary>
    public static bool IsInternalViewerPage(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return false;

        return uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
               uri.Host.Equals(HostName, StringComparison.OrdinalIgnoreCase) &&
               (uri.IsDefaultPort || uri.Port == 443) &&
               string.IsNullOrEmpty(uri.UserInfo);
    }

    private static bool TryNormalizeLocalPdfPath(string? rawPath, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(rawPath))
            return false;

        try
        {
            string full = Path.GetFullPath(rawPath.Trim());
            if (!full.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                return false;

            normalizedPath = full;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Verifica que los encabezados Origin / Referer de una petición a endpoints internos de PDF
    /// provengan exclusivamente del visor interno https://pdf.lex.local y no de una web externa.
    /// </summary>
    public static bool IsValidPdfOriginOrReferer(string? originHeader, string? refererHeader)
    {
        bool hasOrigin = !string.IsNullOrWhiteSpace(originHeader);
        bool hasReferer = !string.IsNullOrWhiteSpace(refererHeader);

        // Debe existir al menos uno de los dos encabezados apuntando al visor interno
        if (!hasOrigin && !hasReferer)
            return false;

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
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Si la URL pertenece al visor interno (https://pdf.lex.local/...), intenta extraer la URL web original del PDF.
    /// Devuelve true si es una URL interna de pdf.lex.local (y asigna externalPdfWebUrl si el PDF original era http/https o local).
    /// </summary>
    public static bool TryUnwrapInternalViewerUrl(string? url, out string? externalPdfWebUrl)
    {
        externalPdfWebUrl = null;
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            !uri.Host.Equals(HostName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var query = HttpUtility.ParseQueryString(uri.Query);
            string? fileParam = query["file"];
            if (!string.IsNullOrWhiteSpace(fileParam))
            {
                if (Uri.TryCreate(fileParam.Trim(), UriKind.Absolute, out var innerUri))
                {
                    if ((innerUri.Scheme == Uri.UriSchemeHttp || innerUri.Scheme == Uri.UriSchemeHttps) &&
                        !innerUri.Host.Equals(HostName, StringComparison.OrdinalIgnoreCase))
                    {
                        externalPdfWebUrl = innerUri.AbsoluteUri;
                    }
                    else if (innerUri.Host.Equals(HostName, StringComparison.OrdinalIgnoreCase) &&
                             innerUri.AbsolutePath.Equals($"/{LocalPdfRoute}", StringComparison.OrdinalIgnoreCase))
                    {
                        var innerQuery = HttpUtility.ParseQueryString(innerUri.Query);
                        string? localPath = innerQuery["path"];
                        if (!string.IsNullOrWhiteSpace(localPath))
                        {
                            externalPdfWebUrl = localPath;
                        }
                    }
                }
            }
        }
        catch { }

        return true;
    }

    /// <summary>
    /// Localiza la carpeta que contiene viewer.html y las bibliotecas locales de PDF.js y pdf-lib.
    /// </summary>
    public static string GetPdfAssetsFolder()
    {
        if (_cachedPdfFolder != null && Directory.Exists(_cachedPdfFolder))
            return _cachedPdfFolder;

        // 1. Binario en ejecución
        string p1 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "pdf");
        if (Directory.Exists(p1) && File.Exists(Path.Combine(p1, "viewer.html")))
        {
            _cachedPdfFolder = p1;
            return p1;
        }

        // 2. Modo desarrollo (desde bin/Debug o bin/Release hacia raíz del proyecto)
        string p2 = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Assets", "pdf"));
        if (Directory.Exists(p2) && File.Exists(Path.Combine(p2, "viewer.html")))
        {
            _cachedPdfFolder = p2;
            return p2;
        }

        return p1;
    }

    /// <summary>
    /// Determina si una URL apunta a un documento PDF (excluyendo el propio visor, blob: y data:).
    /// </summary>
    public static bool IsPdfTarget(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        string trimmed = url.Trim();
        if (trimmed.StartsWith(VirtualOrigin, StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int qIdx = trimmed.IndexOfAny(new[] { '?', '#' });
        string clean = qIdx >= 0 ? trimmed.Substring(0, qIdx) : trimmed;

        return clean.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Transforma cualquier ruta de archivo local o URL web de un PDF a la dirección de Lex PDF Studio.
    /// Registra automáticamente los archivos locales y las URLs remotas en la lista blanca de la sesión.
    /// </summary>
    public static string GetViewerUrl(string pdfPathOrUrl)
    {
        if (string.IsNullOrWhiteSpace(pdfPathOrUrl)) return VirtualOrigin + "viewer.html";

        string effectiveUrl;

        // Archivo local en Windows (ej. file:///C:/... o C:\...)
        if (pdfPathOrUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var uri = new Uri(pdfPathOrUrl);
                string localPath = uri.LocalPath;
                AuthorizeLocalFile(localPath);
                effectiveUrl = $"{VirtualOrigin}{LocalPdfRoute}?path={Uri.EscapeDataString(localPath)}";
            }
            catch
            {
                effectiveUrl = pdfPathOrUrl;
            }
        }
        else if ((pdfPathOrUrl.Length >= 2 && char.IsLetter(pdfPathOrUrl[0]) && pdfPathOrUrl[1] == ':') ||
                 pdfPathOrUrl.StartsWith(@"\\"))
        {
            AuthorizeLocalFile(pdfPathOrUrl);
            effectiveUrl = $"{VirtualOrigin}{LocalPdfRoute}?path={Uri.EscapeDataString(pdfPathOrUrl)}";
        }
        else
        {
            AuthorizeRemoteUrl(pdfPathOrUrl);
            effectiveUrl = pdfPathOrUrl;
        }

        return $"{VirtualOrigin}viewer.html?file={Uri.EscapeDataString(effectiveUrl)}";
    }

    private static void ExtractOriginAndReferer(
        CoreWebView2WebResourceRequestedEventArgs e,
        out string? originHeader,
        out string? refererHeader)
    {
        originHeader = null;
        refererHeader = null;
        try
        {
            if (e.Request.Headers == null) return;
            if (e.Request.Headers.Contains("Origin"))
                originHeader = e.Request.Headers.GetHeader("Origin");
            if (e.Request.Headers.Contains("Referer"))
                refererHeader = e.Request.Headers.GetHeader("Referer");
        }
        catch { }
    }

    private static CoreWebView2WebResourceResponse CreateTextResponse(
        CoreWebView2Environment env,
        int statusCode,
        string reasonPhrase,
        string message)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message);
        string safeReason = TvUrlGuard.SanitizeHeaderValue(reasonPhrase, 80);
        if (string.IsNullOrEmpty(safeReason)) safeReason = "Error";
        string headers =
            $"Content-Type: text/plain; charset=utf-8\r\nAccess-Control-Allow-Origin: {AllowedCorsOrigin}\r\n" +
            "X-Content-Type-Options: nosniff\r\nCache-Control: no-store";
        return env.CreateWebResourceResponse(new MemoryStream(bytes, writable: false), statusCode, safeReason, headers);
    }

    /// <summary>
    /// Comprueba si los primeros 1024 bytes contienen la firma mágica "%PDF-" según ISO 32000.
    /// </summary>
    internal static bool HasPdfMagicHeader(ReadOnlySpan<byte> bytes)
    {
        int scanLen = Math.Min(bytes.Length, 1024);
        return bytes[..scanLen].IndexOf(PdfMagicPrefix) >= 0;
    }

    private static bool IsRedirectStatusCode(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static async Task<byte[]?> ReadBoundedBytesAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        byte[] buffer = new byte[81920];
        long total = 0;

        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (read <= 0) break;
            total += read;
            if (total > maxBytes)
                return null;
            ms.Write(buffer, 0, read);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Descarga un PDF remoto siguiendo redirecciones seguras, aplicando aislamiento anti-SSRF
    /// y cookies por salto, y validando la firma binaria real del documento.
    /// </summary>
    internal static async Task<RemotePdfFetchResult> FetchRemotePdfAsync(
        Uri initialUri,
        Func<Uri, Task<string?>>? cookieHeaderProvider = null,
        string? userAgent = null,
        HttpClient? httpClientOverride = null,
        CancellationToken ct = default)
    {
        if (!IsSafeExternalHttpUri(initialUri))
        {
            return new RemotePdfFetchResult(403, "Forbidden", "text/plain; charset=utf-8",
                Encoding.UTF8.GetBytes("URL remota no permitida para el visor PDF."), "URL no permitida");
        }

        bool initialIsPrivate = TvUrlGuard.IsPrivateOrLocalHost(initialUri.Host);
        HttpClient client = httpClientOverride ?? (initialIsPrivate ? _privatePdfHttp.Value : _publicPdfHttp.Value);

        string effectiveUa = TvUrlGuard.SanitizeHeaderValue(userAgent, 240);
        if (string.IsNullOrEmpty(effectiveUa))
            effectiveUa = TvUrlGuard.BrowserUserAgent;

        Uri currentUri = initialUri;
        for (int hop = 0; hop <= MaxRemoteRedirects; hop++)
        {
            if (!IsSafeExternalHttpUri(currentUri))
            {
                return new RemotePdfFetchResult(403, "Forbidden", "text/plain; charset=utf-8",
                    Encoding.UTF8.GetBytes("Redirección bloqueada hacia un destino no permitido."), "Destino bloqueado");
            }

            if (!initialIsPrivate && TvUrlGuard.IsPrivateOrLocalHost(currentUri.Host))
            {
                return new RemotePdfFetchResult(403, "Forbidden", "text/plain; charset=utf-8",
                    Encoding.UTF8.GetBytes("Redirección bloqueada hacia una dirección privada o local."), "Redirección SSRF bloqueada");
            }

            if (TvDenylist.IsDenylisted(currentUri.AbsoluteUri, out var denyReason))
            {
                return new RemotePdfFetchResult(403, "Forbidden", "text/plain; charset=utf-8",
                    Encoding.UTF8.GetBytes(denyReason ?? "Origen bloqueado."), "Origen bloqueado");
            }

            // Solicitar cookies estrictamente del salto actual para no filtrar sesiones entre dominios
            string? cookieHeader = null;
            if (cookieHeaderProvider != null)
            {
                try { cookieHeader = await cookieHeaderProvider(currentUri); }
                catch { cookieHeader = null; }
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, currentUri);
            req.Headers.TryAddWithoutValidation("User-Agent", effectiveUa);
            req.Headers.TryAddWithoutValidation("Accept", "application/pdf,application/octet-stream;q=0.9,*/*;q=0.8");
            if (!string.IsNullOrWhiteSpace(cookieHeader) && cookieHeader.IndexOfAny(new[] { '\r', '\n' }) < 0)
            {
                req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }

            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            if (IsRedirectStatusCode(resp.StatusCode))
            {
                var location = resp.Headers.Location;
                if (location == null || hop == MaxRemoteRedirects)
                {
                    return new RemotePdfFetchResult(502, "Bad Gateway", "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes("Demasiadas redirecciones al cargar el PDF remoto."), "Exceso de redirecciones");
                }

                Uri nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                if (!IsSafeExternalHttpUri(nextUri) ||
                    (!initialIsPrivate && TvUrlGuard.IsPrivateOrLocalHost(nextUri.Host)))
                {
                    return new RemotePdfFetchResult(403, "Forbidden", "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes("Redirección bloqueada hacia una dirección privada o no segura."), "Redirección bloqueada");
                }

                currentUri = nextUri;
                continue;
            }

            if (!resp.IsSuccessStatusCode)
            {
                int code = (int)resp.StatusCode;
                string errMsg = code is 401 or 403
                    ? $"El servidor requiere inicio de sesión o restringió el acceso directo (HTTP {code})."
                    : code == 404
                        ? "El documento PDF no fue encontrado en el servidor remoto (HTTP 404)."
                        : $"El servidor remoto respondió con error HTTP {code}.";
                return new RemotePdfFetchResult(code, resp.ReasonPhrase ?? "Upstream Error",
                    "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(errMsg), errMsg);
            }

            long? contentLen = resp.Content.Headers.ContentLength;
            if (contentLen.HasValue && contentLen.Value > MaxRemotePdfBytes)
            {
                string sizeMsg = "El documento PDF remoto supera el límite de 100 MB en el visor.";
                return new RemotePdfFetchResult(413, "Payload Too Large", "text/plain; charset=utf-8",
                    Encoding.UTF8.GetBytes(sizeMsg), sizeMsg);
            }

            byte[]? payload = await ReadBoundedBytesAsync(resp.Content, MaxRemotePdfBytes, ct);
            if (payload == null)
            {
                string sizeMsg = "El documento PDF remoto supera el límite de 100 MB en el visor.";
                return new RemotePdfFetchResult(413, "Payload Too Large", "text/plain; charset=utf-8",
                    Encoding.UTF8.GetBytes(sizeMsg), sizeMsg);
            }

            string mediaType = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!HasPdfMagicHeader(payload))
            {
                bool isHtml = mediaType.Contains("html", StringComparison.OrdinalIgnoreCase);
                string invalidMsg = isHtml
                    ? "El enlace devolvió una página web en lugar de un PDF (puede requerir verificación o inicio de sesión)."
                    : "El recurso remoto no contiene una estructura PDF válida.";
                return new RemotePdfFetchResult(415, "Unsupported Media Type", "text/plain; charset=utf-8",
                    Encoding.UTF8.GetBytes(invalidMsg), invalidMsg);
            }

            return new RemotePdfFetchResult(200, "OK", "application/pdf", payload);
        }

        return new RemotePdfFetchResult(502, "Bad Gateway", "text/plain; charset=utf-8",
            Encoding.UTF8.GetBytes("No se pudo resolver la descarga del PDF remoto."), "Fallo en redirecciones");
    }

    private static async Task HandleRemotePdfRequestAsync(
        CoreWebView2Environment env,
        CoreWebView2WebResourceRequestedEventArgs e,
        Uri initialUri,
        CoreWebView2? core,
        string? browserUserAgent,
        CoreWebView2Deferral deferral)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            Func<Uri, Task<string?>>? cookieProvider = core == null
                ? null
                : async hopUri =>
                {
                    try
                    {
                        var cookies = await core.CookieManager.GetCookiesAsync(hopUri.AbsoluteUri);
                        if (cookies == null || cookies.Count == 0)
                            return null;

                        var sb = new StringBuilder();
                        foreach (var cookie in cookies)
                        {
                            if (string.IsNullOrEmpty(cookie.Name)) continue;
                            if (sb.Length > 0) sb.Append("; ");
                            sb.Append(cookie.Name).Append('=').Append(cookie.Value ?? string.Empty);
                        }
                        return sb.Length > 0 ? sb.ToString() : null;
                    }
                    catch
                    {
                        return null;
                    }
                };

            var result = await FetchRemotePdfAsync(initialUri, cookieProvider, browserUserAgent, httpClientOverride: null, cts.Token);
            if (result.StatusCode == 200)
            {
                string headers =
                    $"Content-Type: application/pdf\r\nAccess-Control-Allow-Origin: {AllowedCorsOrigin}\r\n" +
                    "X-Content-Type-Options: nosniff\r\nCache-Control: no-store\r\nAccept-Ranges: none";
                e.Response = env.CreateWebResourceResponse(new MemoryStream(result.Bytes, writable: false), 200, "OK", headers);
            }
            else
            {
                e.Response = CreateTextResponse(
                    env,
                    result.StatusCode,
                    result.ReasonPhrase,
                    result.ErrorReason ?? "No se pudo cargar el documento PDF remoto.");
            }
        }
        catch (OperationCanceledException)
        {
            e.Response = CreateTextResponse(env, 504, "Gateway Timeout", "Tiempo de espera agotado al descargar el PDF remoto.");
        }
        catch (Exception ex)
        {
            var (_, status, msg) = TvUrlGuard.ClassifyException(ex);
            e.Response = CreateTextResponse(env, status, "Upstream Error", $"No se pudo cargar el PDF remoto: {msg}");
        }
        finally
        {
            try { deferral.Complete(); } catch { }
        }
    }

    /// <summary>
    /// Intercepta las solicitudes hacia https://pdf.lex.local/* y sirve los recursos y el stream del PDF.
    /// </summary>
    public static bool TryHandleWebResourceRequest(CoreWebView2Environment env, CoreWebView2WebResourceRequestedEventArgs e)
        => TryHandleWebResourceRequest(env, e, core: null);

    /// <summary>
    /// Intercepta las solicitudes hacia https://pdf.lex.local/*, propagando cookies de sesión WebView2
    /// cuando se cargan PDFs remotos autorizados a través del visor interno.
    /// </summary>
    public static bool TryHandleWebResourceRequest(
        CoreWebView2Environment env,
        CoreWebView2WebResourceRequestedEventArgs e,
        CoreWebView2? core)
    {
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)) return false;
        if (!uri.Host.Equals(HostName, StringComparison.OrdinalIgnoreCase)) return false;

        string path = uri.AbsolutePath.TrimStart('/');
        string folder = GetPdfAssetsFolder();

        // 1. Cargar archivo local mediante API (protegido contra lectura arbitraria desde orígenes externos)
        if (path.Equals(LocalPdfRoute, StringComparison.OrdinalIgnoreCase))
        {
            ExtractOriginAndReferer(e, out string? originHeader, out string? refererHeader);

            if (!IsValidPdfOriginOrReferer(originHeader, refererHeader))
            {
                e.Response = CreateTextResponse(env, 403, "Forbidden", "Acceso denegado: origen no autorizado.");
                return true;
            }

            var query = HttpUtility.ParseQueryString(uri.Query);
            string? localFilePath = query["path"];

            if (!IsLocalFileAuthorized(localFilePath) || !TryNormalizeLocalPdfPath(localFilePath, out string safeFullPath))
            {
                e.Response = CreateTextResponse(env, 403, "Forbidden", "Acceso denegado: el archivo PDF no ha sido autorizado por el usuario.");
                return true;
            }

            if (File.Exists(safeFullPath))
            {
                try
                {
                    byte[] fileBytes = File.ReadAllBytes(safeFullPath);
                    string headers = $"Content-Type: application/pdf\r\nAccess-Control-Allow-Origin: {AllowedCorsOrigin}\r\nX-Content-Type-Options: nosniff\r\nCache-Control: no-store";
                    e.Response = env.CreateWebResourceResponse(new MemoryStream(fileBytes, writable: false), 200, "OK", headers);
                    return true;
                }
                catch (Exception ex)
                {
                    e.Response = CreateTextResponse(env, 500, "Read Error", $"Error al leer PDF local: {ex.Message}");
                    return true;
                }
            }
            else
            {
                e.Response = CreateTextResponse(env, 404, "Not Found", "Archivo local no encontrado.");
                return true;
            }
        }

        // 1b. Cargar PDF virtual / remoto mediante proxy same-origin autenticado
        if (path.Equals(RemotePdfRoute, StringComparison.OrdinalIgnoreCase))
        {
            ExtractOriginAndReferer(e, out string? originHeader, out string? refererHeader);

            if (!IsValidPdfOriginOrReferer(originHeader, refererHeader))
            {
                e.Response = CreateTextResponse(env, 403, "Forbidden", "Acceso denegado: origen no autorizado.");
                return true;
            }

            var query = HttpUtility.ParseQueryString(uri.Query);
            string? rawRemoteUrl = query["url"];

            if (!TryNormalizeRemotePdfUrl(rawRemoteUrl, out string normalizedRemoteUrl, out var initialUri) ||
                initialUri == null ||
                !IsRemoteUrlAuthorized(normalizedRemoteUrl))
            {
                e.Response = CreateTextResponse(env, 403, "Forbidden", "Acceso denegado: la URL del PDF remoto no ha sido autorizada.");
                return true;
            }

            string? browserUa = null;
            try { browserUa = core?.Settings?.UserAgent; } catch { }

            var deferral = e.GetDeferral();
            _ = HandleRemotePdfRequestAsync(env, e, initialUri, core, browserUa, deferral);
            return true;
        }

        // 2. Archivos estáticos del visor (viewer.html, pdf.min.js, pdf.worker.min.js, pdf-lib.min.js)
        if (string.IsNullOrEmpty(path)) path = "viewer.html";

        // Protección contra Path Traversal (..)
        if (path.Contains("..", StringComparison.Ordinal) || path.Contains(':', StringComparison.Ordinal))
        {
            e.Response = CreateTextResponse(env, 400, "Bad Request", "Ruta inválida.");
            return true;
        }

        byte[]? bytes = null;
        try
        {
            string fullFolder = Path.GetFullPath(folder);
            string diskFile = Path.GetFullPath(Path.Combine(fullFolder, path));
            if (diskFile.StartsWith(fullFolder, StringComparison.OrdinalIgnoreCase) && File.Exists(diskFile))
            {
                bytes = File.ReadAllBytes(diskFile);
            }
        }
        catch { }

        // Respaldo de alta disponibilidad: Si no está en disco (ej. Single-file publish autónomo en otra máquina),
        // cargar directamente desde los recursos incrustados en el ensamblado binario.
        if (bytes == null)
        {
            string resName = $"LexBrowser.Assets.pdf.{path.Replace('/', '.')}";
            using var stream = typeof(PdfStudioService).Assembly.GetManifestResourceStream(resName);
            if (stream != null)
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                bytes = ms.ToArray();
            }
        }

        if (bytes != null)
        {
            try
            {
                string contentType = path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ? "text/html; charset=utf-8" :
                                     path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ? "application/javascript; charset=utf-8" :
                                     path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ? "text/css; charset=utf-8" :
                                     "application/octet-stream";
                string cacheControl = path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ? "no-cache" : "max-age=86400";

                string headers = $"Content-Type: {contentType}\r\nAccess-Control-Allow-Origin: {AllowedCorsOrigin}\r\nX-Content-Type-Options: nosniff\r\nCache-Control: {cacheControl}";
                e.Response = env.CreateWebResourceResponse(new MemoryStream(bytes, writable: false), 200, "OK", headers);
                return true;
            }
            catch { }
        }

        return false;
    }
}

