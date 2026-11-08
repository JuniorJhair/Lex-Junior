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
        "bannerflow.net",
        "adform.net",
        "media.net",
        "1rx.io",
        "lijit.com",
        "a-mo.net",
        "smaato.net",
        "amx1.net",
        "amxrtb.com",
        "nextmillmedia.com",
        "simpli.fi",
        "bidr.io",
        "nexx360.io",
        "sharethrough.com",
        "presage.io",
        "cpmstar.com",
        "admatic.de",
        "seedtag.com",
        "zmaticoo.com",
        "kueezrtb.com",
        "marphezis.com",

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
        "onclickads.net",
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
        "camsoda.com",
        "flirt4free.com",
        "streamate.com",
        "imlive.com",
        "jerkmate.com",
        "jasmin.com",

        // Redes especializadas de video ads (VAST/reproductores/sliders) y rotadores en streaming
        "magsrv.com",
        "realsrv.com",
        "exdynsrv.com",
        "trafficjunky.net",
        "tjassets.com",
        "trafficstars.net",
        "t-stars.com",
        "ts-adserver.com",
        "trafficfactory.com",
        "tf-adserver.com",
        "adglare.net",
        "adglare.com",
        "plugrush.com",
        "plugrush.net",
        "adxvip.com",
        "xv-adserver.com",
        "ptgnadsv2.com",
        "videoplayerhub.com",
        "etahub.com",
        "wpushsdk.com",
        "creative.mycdn.me",
        "runative-syndicate.com",
        "runative.com",
        "adtng.com",
        "adtng.net",
        "pemsrv.com",
        "opoxv.com",

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
    // Solo patrones específicos de publicidad. Se descartaron los genéricos que rompían sitios
    // legítimos ("onclick", "vast.xml", "campaign_id=", "/youtubei/v1/att/", "log_event").
    private static readonly string[] GenericAdUrlPatterns = new[]
    {
        "/api/stats/ads",
        "/pagead/",
        "/youtubei/v1/player/ad_break",
        "/get_midroll_info",
        "adformat=",
        "&ad_type=",
        "&adflag=1",
        "&ad_url=",
        "&ad_host=",
        "googleads.g.doubleclick.net",
        "/popunder",
        "/popcash",
        "/direct-link",
        "ad-delivery",
        "track/ad",
        "click.php?",
        "servedbyadbutler",
        "/adserver/",
        "monetag",
        "zoneid=",
        "/ads.js",
        "aclib.js",
        "/tag.min.js",
        "trkr.",
        "link?z=",
        "cloud3-easiness925.workers.dev",
        "/banners/pr_advertising_",
        "pr_advertising_ads_banner",
        "an-advert-banner",
        "splash.php?",
        "/slider.php",
        "/ad-provider.js",
        "zone_id=",
        "idzone=",
        "ad_type=vast",
        "/vast.php"
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

    // ── Palabras que, dentro del HOST de un popup externo, indican red publicitaria ──
    private static readonly string[] SuspiciousHostKeywords = new[]
    {
        "popunder", "adserver", "adclick", "rotator", "onclickads", "syndication"
    };

    // ── Dominios seguros globales (navegación legítima de usuario) ────────
    private static readonly HashSet<string> TrustedRootDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "google.com", "google.com.pe", "google.es", "bing.com", "microsoft.com",
        "youtube.com", "youtu.be", "wikipedia.org", "github.com", "reddit.com",
        "twitter.com", "x.com", "facebook.com", "instagram.com", "twitch.tv",
        "amazon.com", "discord.com", "apple.com", "netflix.com", "spotify.com",
        "live.com", "microsoftonline.com", "paypal.com", "linkedin.com", "mercadolibre.com"
    };

    /// <summary>
    /// Devuelve true si el host, o cualquiera de sus dominios padre, está en <paramref name="set"/>.
    /// Recorre las etiquetas del host (a.b.c → a.b.c, b.c, c): coste O(etiquetas), no O(tamaño de la lista).
    /// </summary>
    private static bool HostMatchesSet(string host, HashSet<string> set)
    {
        if (string.IsNullOrEmpty(host)) return false;

        int start = 0;
        while (true)
        {
            if (set.Contains(host.Substring(start))) return true;
            int dot = host.IndexOf('.', start);
            if (dot < 0) return false;
            start = dot + 1;
        }
    }

    public static bool ShouldBlock(Uri? uri, string? originHost = null)
    {
        if (uri == null) return false;

        string host = uri.Host;
        string path = uri.AbsolutePath;
        string fullUrl = uri.AbsoluteUri;

        // Regla especial de YouTube: proteger el registro del historial del usuario (watchtime, playback, ptracking)
        // YouTube requiere s.youtube.com/api/stats/watchtime, playback, etc., para guardar el historial y sincronizar el progreso.
        if (host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("googlevideo.com", StringComparison.OrdinalIgnoreCase))
        {
            // Solo bloquear anuncios explícitos conocidos dentro de YouTube
            if (path.StartsWith("/api/stats/ads", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/youtubei/v1/player/ad_break", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/get_midroll_info", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/pagead", StringComparison.OrdinalIgnoreCase) ||
                fullUrl.Contains("googleads.g.doubleclick.net", StringComparison.OrdinalIgnoreCase) ||
                fullUrl.Contains("&adformat=", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            // Permitir todo lo demás de YouTube (watchtime, playback, log_event, player, streams de video)
            return false;
        }

        // Regla especial de Google: Proteger búsquedas, respuestas IA (Gemini / Visión general IA / Lens)
        // Cuando el origen de la navegación es Google y la petición es a Google o sus servicios de sesión/token,
        // no bloquear los tokens de integridad ni endpoints asíncronos que requiere Google Lens e IA Overviews.
        bool isGoogleServiceHost = IsGoogleDomain(host);
        bool isGoogleOrigin = !string.IsNullOrEmpty(originHost) && IsGoogleDomain(originHost);

        if (isGoogleOrigin)
        {
            if (path.StartsWith("/pagead", StringComparison.OrdinalIgnoreCase) ||
                fullUrl.Contains("googleads.g.doubleclick.net", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (isGoogleServiceHost)
            {
                return false;
            }
        }

        // 1. Bloquear dominios completos (incluyendo subdominios)
        if (HostMatchesSet(host, BlockedDomains)) return true;

        if (isGoogleServiceHost)
        {
            if (path.StartsWith("/pagead", StringComparison.OrdinalIgnoreCase) ||
                fullUrl.Contains("googleads.g.doubleclick.net", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return false;
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

        // Los dominios de confianza (Google, Microsoft, PayPal…) nunca se tratan como popunder:
        // sus URLs usan parámetros como &z= (zoom de Maps) o redirect_uri (OAuth) de forma legítima.
        if (IsTrustedDomain(targetHost)) return false;

        // 2. Identificadores de trackers de afiliados y popunders (trkr.*, ?z=, zoneid=)
        if (targetHost.Contains("trkr.") || targetHost.Contains("ppof.net") ||
            targetUrl.Contains("?z=") || targetUrl.Contains("&z=") ||
            targetUrl.Contains("zoneid="))
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

        // 4. Palabras de red publicitaria en el host
        foreach (var kw in SuspiciousHostKeywords)
        {
            if (targetHost.Contains(kw)) return true;
        }

        // 5. Redirección abierta: otra URL completa incrustada tal cual (sin codificar) en la dirección
        //    de un dominio desconocido, abierta desde otro sitio.
        if (currentSiteUri != null)
        {
            int schemeEnd = targetUrl.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd >= 0 && targetUrl.IndexOf("://", schemeEnd + 3, StringComparison.Ordinal) > 0)
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsGoogleDomain(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        return host.Equals("google.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".google.com", StringComparison.OrdinalIgnoreCase) ||
               host.StartsWith("google.", StringComparison.OrdinalIgnoreCase) ||
               host.Contains(".google.") ||
               host.Equals("googleadservices.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".googleadservices.com", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("googlesyndication.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".googlesyndication.com", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("gstatic.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".gstatic.com", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("googleapis.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".googleapis.com", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("googleusercontent.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("google-analytics.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".google-analytics.com", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("googletagmanager.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".googletagmanager.com", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("doubleclick.net", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".doubleclick.net", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTrustedDomain(string host) => HostMatchesSet(host, TrustedRootDomains);

    /// <summary>
    /// Comprueba si dos URIs pertenecen al mismo sitio o dominio raíz registrable (ej: sub.sitio.com y sitio.com).
    /// </summary>
    public static bool IsSameSiteOrDomain(Uri? a, Uri? b)
    {
        if (a == null || b == null) return false;
        string hostA = a.Host.ToLowerInvariant();
        string hostB = b.Host.ToLowerInvariant();
        if (hostA == hostB) return true;
        if (hostA.EndsWith("." + hostB) || hostB.EndsWith("." + hostA)) return true;

        string rootA = GetRegistrableDomain(hostA);
        string rootB = GetRegistrableDomain(hostB);
        return !string.IsNullOrEmpty(rootA) && string.Equals(rootA, rootB, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetRegistrableDomain(string host)
    {
        if (string.IsNullOrEmpty(host)) return string.Empty;
        var parts = host.Split('.');
        if (parts.Length <= 2) return host;
        return parts[^2] + "." + parts[^1];
    }
}

