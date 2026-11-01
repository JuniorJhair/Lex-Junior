using System;
using System.Collections.Generic;

namespace LexBrowser.Core.AdBlock;

public static class AdBlockFilter
{
    // ── Dominios completos bloqueados (host exacto o subdominio) ──────────
    private static readonly HashSet<string> BlockedDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        // Redes principales de publicidad y tracking
        "doubleclick.net",
        "googleadservices.com",
        "googlesyndication.com",
        "adservice.google.com",
        "pagead2.googlesyndication.com",
        "adclick.g.doubleclick.net",
        "securepubads.g.doubleclick.net",
        "analytics.google.com",
        "google-analytics.com",
        "googletagmanager.com",
        "stats.g.doubleclick.net",

        // Redes de tracking, SSPs y banners
        "criteo.com",
        "criteo.net",
        "taboola.com",
        "outbrain.com",
        "amazon-adsystem.com",
        "adnxs.com",
        "rubiconproject.com",
        "pubmatic.com",
        "openx.net",
        "scorecardresearch.com",
        "clarity.ms",
        "hotjar.com",
        "adcolony.com",
        "unityads.unity3d.com",
        "applovin.com",
        "ironsrc.com",
        "vungle.com",
        "chartbeat.com",
        "moatads.com",
        "quantserve.com",
        "zemanta.com",
        "infolinks.com",
        "chitika.com",
        "bidvertiser.com",
        "ad-maven.com",
        "admaven.com",
        "richpush.co",
        "evadav.com",
        "rollerads.com",

        // Redes rusas / Yandex ad context
        "an.yandex.ru",
        "mc.yandex.ru",
        "ymatuhin.ru",

        // Redes de Pop-unders, Pop-ups y Click-jacking (comunes en futbollibre, cuevana y streaming)
        "propellerads.com",
        "popcash.net",
        "popads.net",
        "adsterra.com",
        "monetag.com",
        "clickadu.com",
        "hilltopads.com",
        "trafficjunky.com",
        "trafficstars.com",
        "traffichaus.com",
        "trafficfactory.biz",
        "exoclick.com",
        "juicyads.com",
        "adcash.com",
        "onclickalgo.com",
        "onclickperformance.com",
        "mgid.com",
        "yadro.ru",
        "aniview.com",
        "spotxchange.com",
        "springserve.com",
        "teads.tv",
        "bidswitch.net",
        "casalemedia.com",
        "smartadserver.com",
        "revcontent.com",
        "tsyndicate.com",
        "ero-advertising.com",
        "livejasmin.com",
        "bongacams.com",
        "chaturbate.com",
        "stripchat.com",

        // Dominios ad-hoc descubiertos en auditoría forense (redes rotadoras)
        "r2ads.net",
        "asos2.net",
        "ppof.net",
        "llvpn.com",
        "streamtp10.com",
        "getadmiral.com",
        "visariomedia.com",
        "qojseyonoj.com",
        "hfryyynvqwgy.com",

        // Redes de apuestas y afiliados agresivos frecuentes en popups no solicitados
        "1xbet.com",
        "1x-bet.com",
        "bet365.com",
        "melbet.com",
        "mostbet.com",
        "parimatch.com",
        "pin-up.bet",
        "vave.com",
        "stake.com",
        "bc.game"
    };

    // ── Hosts de Facebook a bloquear ──────────────────────────────────────
    private static readonly HashSet<string> FacebookBlockedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "connect.facebook.net",
        "an.facebook.com"
    };

    // ── Paths que indican tracker ─────────────────────────────────────────
    private static readonly string[] FacebookBlockedPaths = new[]
    {
        "/tr",
        "/signals",
        "/en_US/fbevents.js",
        "/en_US/sdk.js"
    };

    // ── Patrones de URL de telemetría, popunders y recursos publicitarios ─
    private static readonly string[] GenericAdUrlPatterns = new[]
    {
        "/api/stats/ads",
        "/pagead/",
        "/ptracking",
        "/youtubei/v1/player/ad_break",
        "/get_midroll_info",
        "/youtubei/v1/att/",
        "adformat=",
        "&ad_type=",
        "&adflag=1",
        "&ad_url=",
        "&ad_host=",
        "googleads.g.doubleclick.net",
        "youtubei.googleapis.com/youtubei/v1/log_event",
        "/popunder",
        "/popcash",
        "/direct-link",
        "ad-delivery",
        "track/ad",
        "click.php?",
        "servedbyadbutler",
        "onclick",
        "/adserver/",
        "vast.xml",
        "monetag",
        "zoneid=",
        "zoneId=",
        "campaign_id=",
        "/ads.js",
        "aclib.js",
        "/tag.min.js",
        "trkr.",
        "link?z=",
        "cloud3-easiness925.workers.dev",
        "/banners/pr_advertising_",
        "pr_advertising_ads_banner",
        "an-advert-banner"
    };

    // ── TLDs comúnmente dedicados casi exclusivamente a spam y popunders ───
    private static readonly string[] SuspiciousPopunderTLDs = new[]
    {
        ".top",
        ".bid",
        ".click",
        ".cfd",
        ".gdn",
        ".buzz",
        ".monster",
        ".cam",
        ".skin",
        ".quest"
    };

    // ── Dominios seguros globales (navegación legítima de usuario) ────────
    private static readonly HashSet<string> TrustedRootDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "google.com", "google.com.pe", "google.es", "bing.com", "microsoft.com",
        "youtube.com", "youtu.be", "wikipedia.org", "github.com", "reddit.com",
        "twitter.com", "x.com", "facebook.com", "instagram.com", "twitch.tv",
        "amazon.com", "discord.com", "apple.com", "netflix.com", "spotify.com"
    };

    public static bool ShouldBlock(Uri? uri)
    {
        if (uri == null) return false;

        string host = uri.Host;
        string path = uri.AbsolutePath;
        string fullUrl = uri.AbsoluteUri;

        // 1. Bloquear dominios completos (incluyendo subdominios)
        foreach (var domain in BlockedDomains)
        {
            if (host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 2. Bloquear Facebook solo en paths de tracking
        if (FacebookBlockedHosts.Contains(host) ||
            host.EndsWith(".facebook.net", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var fbPath in FacebookBlockedPaths)
            {
                if (path.StartsWith(fbPath, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            if (FacebookBlockedHosts.Contains(host))
                return true;
        }

        // 3. Patrones de anuncios, scripts de popunder y telemetría
        foreach (var pattern in GenericAdUrlPatterns)
        {
            if (fullUrl.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Determina heurísticamente si una ventana emergente (NewWindowRequested / popunder)
    /// no fue solicitada por el usuario o es un intento de redirección publicitaria.
    /// </summary>
    public static bool IsSuspiciousPopup(Uri targetUri, Uri? currentSiteUri)
    {
        if (targetUri == null) return true;

        // Si cae en la lista de bloqueados directos, descartar de inmediato
        if (ShouldBlock(targetUri)) return true;

        string targetHost = targetUri.Host.ToLowerInvariant();
        string targetUrl = targetUri.AbsoluteUri.ToLowerInvariant();

        // 1. Si el host es idéntico o subdominio del sitio actual, es una navegación legítima (ej: login, links internos)
        if (currentSiteUri != null)
        {
            string currentHost = currentSiteUri.Host.ToLowerInvariant();
            if (targetHost == currentHost || targetHost.EndsWith("." + currentHost) || currentHost.EndsWith("." + targetHost))
            {
                return false;
            }
        }

        // 2. Identificadores de trackers de afiliados y popunders (trkr.*, ?z=, zoneid)
        if (targetHost.StartsWith("trkr.") || targetHost.Contains("trkr.") || targetHost.Contains("ppof.net") ||
            targetUrl.Contains("?z=") || targetUrl.Contains("&z=") || targetUrl.Contains("link?z=") ||
            targetUrl.Contains("zoneid=") || targetUrl.Contains("zoneid"))
        {
            return true;
        }

        // 3. TLDs comúnmente utilizados para redirecciones y popunders
        foreach (var tld in SuspiciousPopunderTLDs)
        {
            if (targetHost.EndsWith(tld, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 4. Palabras clave sospechosas en el host o parámetros de redirección publicitaria
        string[] suspiciousKeywords = new[]
        {
            "onclick", "popunder", "adserver", "syndication", "tracker",
            "affiliate", "campaign", "adclick", "redirect", "rotator", "promo", "traff"
        };

        foreach (var kw in suspiciousKeywords)
        {
            if (targetHost.Contains(kw) || targetUrl.Contains("?" + kw) || targetUrl.Contains("&" + kw))
            {
                return true;
            }
        }

        // 5. Si proviene de un sitio web que no es el actual, no es un dominio seguro de confianza,
        // y tiene múltiples niveles de redirección o query params de destino sospechosos:
        if (currentSiteUri != null && !IsTrustedDomain(targetHost))
        {
            if (targetUrl.Contains("http://") || targetUrl.Contains("https://") && targetUrl.IndexOf("https://", 8) > 0)
            {
                return true; // Intento de redirección abierta (open redirect)
            }
        }

        return false;
    }

    private static bool IsTrustedDomain(string host)
    {
        foreach (var trusted in TrustedRootDomains)
        {
            if (host.Equals(trusted, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + trusted, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
