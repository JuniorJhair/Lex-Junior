using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Web;
using Microsoft.Web.WebView2.Core;

namespace LexBrowser.Core.Pdf;

/// <summary>
/// Servicio de integración y motor de servidor virtual para Lex PDF Studio.
/// Permite edición nativa de documentos PDF con herramientas de dibujo a mano alzada,
/// resaltador, ajuste de grosor, paleta de colores, borrador, texto, firmas y guardado.
/// 100% offline, 0% CPU en reposo y sin dependencias externas.
/// </summary>
public static class PdfStudioService
{
    public const string HostName = "pdf.lex.local";
    public const string VirtualOrigin = "https://pdf.lex.local/";
    private const string AllowedCorsOrigin = "https://pdf.lex.local";

    private static string? _cachedPdfFolder;

    // Registro en memoria de archivos PDF locales explícitamente abiertos por el usuario en la sesión.
    // Impide que sitios web externos lean archivos arbitrarios del disco mediante api/load-local-pdf.
    private static readonly ConcurrentDictionary<string, byte> _authorizedLocalFiles =
        new(StringComparer.OrdinalIgnoreCase);

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

    public static void ClearAuthorizedFiles()
    {
        _authorizedLocalFiles.Clear();
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
    /// Verifica que los encabezados Origin / Referer de una petición a api/load-local-pdf
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
    /// Devuelve true si es una URL interna de pdf.lex.local (y asigna unlockedWebUrl si el PDF original era http/https).
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
                             innerUri.AbsolutePath.Equals("/api/load-local-pdf", StringComparison.OrdinalIgnoreCase))
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
    /// Determina si una URL apunta a un documento PDF (excluyendo el propio visor para evitar bucles).
    /// </summary>
    public static bool IsPdfTarget(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (url.StartsWith(VirtualOrigin, StringComparison.OrdinalIgnoreCase)) return false;

        int qIdx = url.IndexOfAny(new[] { '?', '#' });
        string clean = qIdx >= 0 ? url.Substring(0, qIdx) : url;

        return clean.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Transforma cualquier ruta de archivo local o URL web de un PDF a la dirección de Lex PDF Studio.
    /// Registra automáticamente los archivos locales en la lista blanca de la sesión.
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
                effectiveUrl = $"{VirtualOrigin}api/load-local-pdf?path={Uri.EscapeDataString(localPath)}";
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
            effectiveUrl = $"{VirtualOrigin}api/load-local-pdf?path={Uri.EscapeDataString(pdfPathOrUrl)}";
        }
        else
        {
            effectiveUrl = pdfPathOrUrl;
        }

        return $"{VirtualOrigin}viewer.html?file={Uri.EscapeDataString(effectiveUrl)}";
    }

    /// <summary>
    /// Intercepta las solicitudes hacia https://pdf.lex.local/* y sirve los recursos y el stream del PDF.
    /// </summary>
    public static bool TryHandleWebResourceRequest(CoreWebView2Environment env, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)) return false;
        if (!uri.Host.Equals(HostName, StringComparison.OrdinalIgnoreCase)) return false;

        string path = uri.AbsolutePath.TrimStart('/');
        string folder = GetPdfAssetsFolder();

        // 1. Cargar archivo local mediante API (protegido contra lectura arbitraria desde orígenes externos)
        if (path.Equals("api/load-local-pdf", StringComparison.OrdinalIgnoreCase))
        {
            string? originHeader = null;
            string? refererHeader = null;
            try
            {
                if (e.Request.Headers != null)
                {
                    if (e.Request.Headers.Contains("Origin"))
                        originHeader = e.Request.Headers.GetHeader("Origin");
                    if (e.Request.Headers.Contains("Referer"))
                        refererHeader = e.Request.Headers.GetHeader("Referer");
                }
            }
            catch { }

            if (!IsValidPdfOriginOrReferer(originHeader, refererHeader))
            {
                byte[] forbiddenOriginBytes = Encoding.UTF8.GetBytes("Acceso denegado: origen no autorizado.");
                e.Response = env.CreateWebResourceResponse(new MemoryStream(forbiddenOriginBytes), 403, "Forbidden", "Content-Type: text/plain; charset=utf-8\r\nX-Content-Type-Options: nosniff");
                return true;
            }

            var query = HttpUtility.ParseQueryString(uri.Query);
            string? localFilePath = query["path"];

            if (!IsLocalFileAuthorized(localFilePath) || !TryNormalizeLocalPdfPath(localFilePath, out string safeFullPath))
            {
                byte[] forbiddenFileBytes = Encoding.UTF8.GetBytes("Acceso denegado: el archivo PDF no ha sido autorizado por el usuario.");
                e.Response = env.CreateWebResourceResponse(new MemoryStream(forbiddenFileBytes), 403, "Forbidden", "Content-Type: text/plain; charset=utf-8\r\nX-Content-Type-Options: nosniff");
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
                    byte[] errBytes = Encoding.UTF8.GetBytes($"Error al leer PDF local: {ex.Message}");
                    e.Response = env.CreateWebResourceResponse(new MemoryStream(errBytes), 500, "Read Error", "Content-Type: text/plain; charset=utf-8\r\nX-Content-Type-Options: nosniff");
                    return true;
                }
            }
            else
            {
                byte[] notFoundBytes = Encoding.UTF8.GetBytes("Archivo local no encontrado.");
                e.Response = env.CreateWebResourceResponse(new MemoryStream(notFoundBytes), 404, "Not Found", "Content-Type: text/plain; charset=utf-8\r\nX-Content-Type-Options: nosniff");
                return true;
            }
        }

        // 2. Archivos estáticos del visor (viewer.html, pdf.min.js, pdf.worker.min.js, pdf-lib.min.js)
        if (string.IsNullOrEmpty(path)) path = "viewer.html";

        // Protección contra Path Traversal (..)
        if (path.Contains("..", StringComparison.Ordinal) || path.Contains(':', StringComparison.Ordinal))
        {
            byte[] badReq = Encoding.UTF8.GetBytes("Ruta inválida.");
            e.Response = env.CreateWebResourceResponse(new MemoryStream(badReq), 400, "Bad Request", "Content-Type: text/plain; charset=utf-8");
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

                string headers = $"Content-Type: {contentType}\r\nAccess-Control-Allow-Origin: {AllowedCorsOrigin}\r\nX-Content-Type-Options: nosniff\r\nCache-Control: max-age=3600";
                e.Response = env.CreateWebResourceResponse(new MemoryStream(bytes, writable: false), 200, "OK", headers);
                return true;
            }
            catch { }
        }

        return false;
    }
}

