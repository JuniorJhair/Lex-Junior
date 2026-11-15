using System;
using System.Text;
using Microsoft.Web.WebView2.Core;

namespace LexBrowser.Core.AdBlock;

/// <summary>
/// Reglas aisladas de red y stubs específicas de Dailymotion.
/// Mantiene el bloqueador funcional actual de Dailymotion completamente separado del motor general.
/// </summary>
public static class DailymotionRules
{
    private static readonly byte[] CanaryBytes =
        Encoding.UTF8.GetBytes("window.__NATIVEADS_CANARY__ = true;");

    private static readonly byte[] VmapEmptyBytes =
        Encoding.UTF8.GetBytes("<vmap:VMAP xmlns:vmap=\"http://www.iab.net/videosuite/vmap\" version=\"1.0\"></vmap:VMAP>");

    public static bool IsDailymotionDomain(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        return PublicSuffix.HostMatchesDomain(host, "dailymotion.com") ||
               PublicSuffix.HostMatchesDomain(host, "dmcdn.net");
    }

    public static bool IsDailymotionRequestOrOrigin(Uri? uri, string? originHost)
    {
        if (uri == null)
            return IsDailymotionDomain(originHost);

        return IsDailymotionDomain(uri.Host) ||
               (!string.IsNullOrEmpty(originHost) && PublicSuffix.HostMatchesDomain(originHost, "dailymotion.com")) ||
               uri.AbsoluteUri.Contains("dailymotion.com", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDailymotionContext(
        Uri? uri,
        string? mainTabHost,
        string? tabUrl,
        string? referer,
        string? originHost)
    {
        return PublicSuffix.HostMatchesDomain(mainTabHost, "dailymotion.com") ||
               (Uri.TryCreate(tabUrl, UriKind.Absolute, out var dmTabUri) &&
                PublicSuffix.HostMatchesDomain(dmTabUri.Host, "dailymotion.com")) ||
               (Uri.TryCreate(referer, UriKind.Absolute, out var dmRefUri) &&
                PublicSuffix.HostMatchesDomain(dmRefUri.Host, "dailymotion.com")) ||
               (uri != null && PublicSuffix.HostMatchesDomain(uri.Host, "dailymotion.com")) ||
               PublicSuffix.HostMatchesDomain(originHost, "dailymotion.com");
    }

    /// <summary>
    /// Evalúa una petición hacia dominios de Dailymotion/dmcdn.net.
    /// Devuelve true si la petición pertenece a Dailymotion y quedó resuelta en <paramref name="shouldBlock"/>.
    /// </summary>
    public static bool TryEvaluateNetwork(Uri? uri, out bool shouldBlock)
    {
        if (uri != null && IsDailymotionDomain(uri.Host))
        {
            shouldBlock = PublicSuffix.HostMatchesDomain(uri.Host, "dmads.dailymotion.com");
            return true;
        }

        shouldBlock = false;
        return false;
    }

    public static bool IsVmapUrl(string fullUrl, string path)
    {
        return fullUrl.Contains("format=vmap", StringComparison.OrdinalIgnoreCase) ||
               fullUrl.Contains("output=vmap", StringComparison.OrdinalIgnoreCase) ||
               fullUrl.Contains("type=vmap", StringComparison.OrdinalIgnoreCase) ||
               fullUrl.Contains("_VMAP_", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("/vmap", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/vmap/", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/vmap.", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("vmap.xml", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryGetRedirectStub(
        Uri? uri,
        out byte[] responseBytes,
        out string contentType,
        string? originHost = null,
        CoreWebView2WebResourceContext? resourceContext = null)
    {
        responseBytes = Array.Empty<byte>();
        contentType = string.Empty;

        if (uri == null)
            return false;

        string host = uri.Host;
        string path = uri.AbsolutePath;
        string full = uri.AbsoluteUri;

        // 0. Dailymotion Canary Script (Anti-AdBlock Bypass de Brave: @@||dailymotion.com/nativeads.js^)
        if ((PublicSuffix.HostMatchesDomain(host, "dailymotion.com") ||
             (!string.IsNullOrEmpty(originHost) && PublicSuffix.HostMatchesDomain(originHost, "dailymotion.com"))) &&
            (path.EndsWith("nativeads.js", StringComparison.OrdinalIgnoreCase) ||
             path.Contains("/nativeads.", StringComparison.OrdinalIgnoreCase)))
        {
            responseBytes = CanaryBytes;
            contentType = "Content-Type: application/javascript; charset=utf-8";
            return true;
        }

        if (!IsDailymotionRequestOrOrigin(uri, originHost))
            return false;

        bool xmlStubContext = resourceContext is null
            or CoreWebView2WebResourceContext.XmlHttpRequest
            or CoreWebView2WebResourceContext.Fetch
            or CoreWebView2WebResourceContext.Other
            or CoreWebView2WebResourceContext.Media;

        if (xmlStubContext &&
            !ShieldIsolation.IsGoogleOrYouTubeContent(host, originHost) &&
            (full.Contains("monetization", StringComparison.OrdinalIgnoreCase) || IsVmapUrl(full, path)))
        {
            responseBytes = VmapEmptyBytes;
            contentType = "Content-Type: text/xml; charset=utf-8";
            return true;
        }

        return false;
    }
}
