using System;
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

    private static string? _cachedPdfFolder;

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

        // 1. Cargar archivo local mediante API
        if (path.Equals("api/load-local-pdf", StringComparison.OrdinalIgnoreCase))
        {
            var query = HttpUtility.ParseQueryString(uri.Query);
            string? localFilePath = query["path"];

            if (!string.IsNullOrWhiteSpace(localFilePath) && File.Exists(localFilePath))
            {
                try
                {
                    byte[] fileBytes = File.ReadAllBytes(localFilePath);
                    string headers = "Content-Type: application/pdf\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: no-cache";
                    e.Response = env.CreateWebResourceResponse(new MemoryStream(fileBytes, writable: false), 200, "OK", headers);
                    return true;
                }
                catch (Exception ex)
                {
                    byte[] errBytes = Encoding.UTF8.GetBytes($"Error al leer PDF local: {ex.Message}");
                    e.Response = env.CreateWebResourceResponse(new MemoryStream(errBytes), 500, "Read Error", "Content-Type: text/plain");
                    return true;
                }
            }
            else
            {
                byte[] notFoundBytes = Encoding.UTF8.GetBytes("Archivo local no encontrado.");
                e.Response = env.CreateWebResourceResponse(new MemoryStream(notFoundBytes), 404, "Not Found", "Content-Type: text/plain");
                return true;
            }
        }

        // 2. Archivos estáticos del visor (viewer.html, pdf.min.js, pdf.worker.min.js, pdf-lib.min.js)
        if (string.IsNullOrEmpty(path)) path = "viewer.html";

        string diskFile = Path.Combine(folder, path);
        if (File.Exists(diskFile))
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(diskFile);
                string contentType = path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ? "text/html; charset=utf-8" :
                                     path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ? "application/javascript; charset=utf-8" :
                                     path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ? "text/css; charset=utf-8" :
                                     "application/octet-stream";

                string headers = $"Content-Type: {contentType}\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: max-age=3600";
                e.Response = env.CreateWebResourceResponse(new MemoryStream(bytes, writable: false), 200, "OK", headers);
                return true;
            }
            catch { }
        }

        return false;
    }
}

