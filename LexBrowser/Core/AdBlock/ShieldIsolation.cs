using System;

namespace LexBrowser.Core.AdBlock;

/// <summary>
/// Frontera estricta de aislamiento para YouTube y Dailymotion.
/// Ninguna petición ni contexto de estos dos sitios debe evaluarse contra reglas generales de Brave.
/// </summary>
public static class ShieldIsolation
{
    public static bool IsYouTubeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        return MediaShieldRules.IsServiceOrStreamHost(host) ||
               PublicSuffix.HostMatchesDomain(host, "ytimg.com");
    }

    public static bool IsYouTubeContext(string? policyHost, string? requestHost)
        => MediaShieldRules.IsServiceHost(policyHost) ||
           MediaShieldRules.IsServiceOrStreamHost(requestHost) ||
           PublicSuffix.HostMatchesDomain(policyHost, "ytimg.com") ||
           PublicSuffix.HostMatchesDomain(requestHost, "ytimg.com");

    public static bool IsDailymotionHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        return DailymotionRules.IsDailymotionDomain(host) ||
               PublicSuffix.HostMatchesDomain(host, "dmxleo.com");
    }

    public static bool IsDailymotionContext(
        Uri? uri,
        string? mainTabHost,
        string? tabUrl,
        string? referer,
        string? originHost)
        => DailymotionRules.IsDailymotionContext(uri, mainTabHost, tabUrl, referer, originHost) ||
           PublicSuffix.HostMatchesDomain(uri?.Host, "dmxleo.com") ||
           PublicSuffix.HostMatchesDomain(originHost, "dmxleo.com") ||
           PublicSuffix.HostMatchesDomain(mainTabHost, "dmxleo.com");

    public static bool IsIsolatedHost(string? host)
        => IsYouTubeHost(host) || IsDailymotionHost(host);

    public static bool IsGoogleOrYouTubeContent(string? host, string? originHost)
    {
        return PublicSuffix.HostMatchesDomain(host, "youtube.com") ||
               PublicSuffix.HostMatchesDomain(host, "youtu.be") ||
               PublicSuffix.HostMatchesDomain(host, "googlevideo.com") ||
               PublicSuffix.HostMatchesDomain(host, "google.com") ||
               PublicSuffix.HostMatchesDomain(host, "gstatic.com") ||
               (!string.IsNullOrEmpty(originHost) &&
                (PublicSuffix.HostMatchesDomain(originHost, "youtube.com") ||
                 PublicSuffix.HostMatchesDomain(originHost, "youtu.be") ||
                 PublicSuffix.HostMatchesDomain(originHost, "google.com")));
    }
}
