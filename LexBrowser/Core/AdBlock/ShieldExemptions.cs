using System;
using System.Collections.Generic;

namespace LexBrowser.Core.AdBlock;

/// <summary>
/// Nivel efectivo de exención resuelto por la fuente única de verdad de LexShield Core.
/// </summary>
public enum ShieldExemptionTier
{
    FullProtection,
    TotalExempt,
    NetworkOnly,
    CosmeticOnly
}

/// <summary>
/// Fuente única de verdad para exenciones globales del Escudo LexShield en 3 niveles:
/// - TotalExempt: esquemas internos, pagos, OAuth/SSO, CAPTCHA, aislamiento YT/DM o escudo pausado.
/// - NetworkOnly: plataformas grandes/productividad (BigSites: Miro, Figma, Canva, Google, ChatGPT, Notion...),
///                Modo Compatible o cosmética desactivada (bloqueo de red sí; sin stubs/overlays/cosmética genérica).
/// - CosmeticOnly: red desactivada manteniendo cosmética activa.
/// Compartida entre el filtro de red en C# y el plan por documento en WebView2.
/// </summary>
public static class ShieldExemptions
{
    private static readonly HashSet<string> ExtraTrustedNavigationDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "google.com.pe", "google.es", "reddit.com",
        "twitter.com", "x.com", "facebook.com", "instagram.com",
        "amazon.com", "discord.com", "netflix.com", "spotify.com",
        "linkedin.com", "mercadolibre.com", "whatsapp.com"
    };
    /// <summary>
    /// Dominios raíz de plataformas grandes, motores de búsqueda, medios dedicados y apps web
    /// que quedan exentos de reglas genéricas por subcadena, shims IMA/VAST y del escudo genérico WebShield.
    /// </summary>
    public static readonly string[] BigSites =
    {
        "google.com", "youtube.com", "youtu.be", "youtube-nocookie.com", "dailymotion.com", "dmcdn.net",
        "twitch.tv", "vimeo.com", "microsoft.com", "live.com", "microsoftonline.com", "office.com",
        "apple.com", "github.com", "wikipedia.org", "openai.com", "chatgpt.com", "claude.ai",
        "paypal.com", "cloudflare.com", "gstatic.com", "googleapis.com", "googleusercontent.com",
        "miro.com", "figma.com", "canva.com", "notion.so", "excalidraw.com", "tldraw.com",
        "lucid.app", "diagrams.net", "draw.io", "mural.co"
    };

    /// <summary>
    /// Dominios raíz sensibles de pagos, autenticación OAuth/SSO y verificación anti-bot (CAPTCHA).
    /// Nunca deben sufrir bloqueo de subframes ni intercepción heurística de clics.
    /// </summary>
    public static readonly string[] PaymentOrAuthHosts =
    {
        "paypal.com", "stripe.com", "mercadopago.com", "mercadopago.com.pe", "mercadopago.com.mx",
        "mercadopago.com.ar", "adyen.com", "braintreepayments.com", "checkout.com", "payu.com",
        "niubiz.com.pe", "izipay.pe", "visa.com", "mastercard.com",
        "accounts.google.com", "appleid.apple.com", "login.microsoftonline.com", "login.live.com",
        "auth0.com", "okta.com", "recaptcha.net", "hcaptcha.com", "challenges.cloudflare.com",
        "arkoselabs.com", "funcaptcha.com"
    };

    /// <summary>
    /// Hosts frecuentes de servidores embebidos de video en sitios de streaming/anime.
    /// </summary>
    public static readonly string[] VideoEmbedHosts =
    {
        "streamwish", "wishfast", "wishembed", "vidhide", "streamtape", "dood", "d000d",
        "ds2play", "filelions", "lulustream", "luluvdo", "mp4upload", "uqload", "voe.sx",
        "mixdrop", "fastream", "hglink", "dhcplay", "fembed", "upstream", "vidoza",
        "wolfstream", "megacloud", "rabbitstream", "vidplay", "yourupload", "ok.ru", "vk.com"
    };

    /// <summary>
    /// Devuelve true si el host pertenece a <see cref="BigSites"/> (coincidencia exacta o subdominio).
    /// </summary>
    public static bool IsBigSite(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        string clean = StripPort(host);
        foreach (string domain in BigSites)
        {
            if (PublicSuffix.HostMatchesDomain(clean, domain))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Devuelve true si el host corresponde a una pasarela de pagos, identidad OAuth o CAPTCHA.
    /// </summary>
    public static bool IsPaymentOrAuthHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        string clean = StripPort(host);
        foreach (string domain in PaymentOrAuthHosts)
        {
            if (PublicSuffix.HostMatchesDomain(clean, domain))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Identifica URLs de subframes que corresponden a reproductores embebidos de streaming/video
    /// (por host conocido de hosting multimedia o patrón de ruta de embed) excluyendo sitios grandes y de pagos.
    /// </summary>
    public static bool IsVideoEmbedUrl(Uri? uri)
    {
        if (uri == null || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return false;

        string host = StripPort(uri.Host);
        if (IsBigSite(host) || IsPaymentOrAuthHost(host))
            return false;

        foreach (string token in VideoEmbedHosts)
        {
            if (host.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        string path = uri.AbsolutePath;
        return path.StartsWith("/e/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/v/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/embed", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/player", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/videoembed", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/video_ext", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Devuelve true si el host es un destino legítimo de navegación global (BigSites + portales principales)
    /// que no debe bloquearse por heurísticas de popups ni patrones ambiguos.
    /// </summary>
    public static bool IsTrustedNavigationDomain(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        string clean = StripPort(host);
        if (IsBigSite(clean)) return true;
        foreach (string domain in ExtraTrustedNavigationDomains)
        {
            if (PublicSuffix.HostMatchesDomain(clean, domain))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Resuelve de forma determinista el nivel de exención aplicable al par (recurso/documento, origen).
    /// Nota: IsBigSite se evalúa sobre <paramref name="originOrDocumentHost"/> para que recursos externos
    /// bajo *.googleapis.com (p. ej. ima3.js en reproductores terceros) sigan evaluándose correctamente.
    /// </summary>
    public static ShieldExemptionTier ResolveExemptionTier(
        string? targetHost,
        string? originOrDocumentHost,
        EffectiveSitePolicy policy)
    {
        if (!policy.ShieldEnabled ||
            IsPaymentOrAuthHost(targetHost) ||
            IsPaymentOrAuthHost(originOrDocumentHost) ||
            ShieldIsolation.IsIsolatedHost(targetHost) ||
            ShieldIsolation.IsIsolatedHost(originOrDocumentHost))
        {
            return ShieldExemptionTier.TotalExempt;
        }

        bool noCosmetic = policy.IsCompatMode || IsBigSite(originOrDocumentHost) || !policy.Cosmetic;
        if (!policy.Network)
        {
            return noCosmetic ? ShieldExemptionTier.TotalExempt : ShieldExemptionTier.CosmeticOnly;
        }

        if (noCosmetic)
        {
            return ShieldExemptionTier.NetworkOnly;
        }

        return ShieldExemptionTier.FullProtection;
    }

    private static string StripPort(string host)
    {
        int colonIdx = host.IndexOf(':');
        return colonIdx >= 0 ? host.Substring(0, colonIdx).Trim() : host.Trim();
    }
}

