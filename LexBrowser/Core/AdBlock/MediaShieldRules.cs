using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Web.WebView2.Core;

namespace LexBrowser.Core.AdBlock;

/// <summary>
/// Reglas declarativas de red y de presentación para reproductores de video en streaming (YouTube).
///
/// Origen del comportamiento: descripción pública de las listas de filtros que usa Brave
/// (EasyList / uBlock Origin / lista propia de Brave, ver <c>adblock-rust-master/data</c>).
/// La implementación es propia: no se copia código de esas listas ni de sus scriptlets.
///
/// Criterio de diseño (el mismo de la lista de referencia): se bloquean únicamente endpoints
/// publicitarios explícitos. Todo lo que el reproductor necesita para el contenido real
/// (<c>api/stats/playback|watchtime|atr|qoe</c>, <c>youtubei/v1/player</c>, <c>log_event</c>,
/// <c>videoplayback</c>…) NO se toca: bloquear la infraestructura de un anuncio que sí llega
/// deja el anuncio atascado y delata el bloqueo.
/// </summary>
public static class MediaShieldRules
{
    // ── Hosts ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Hosts del propio servicio de reproducción (páginas, API e incrustados).</summary>
    public static bool IsServiceHost(string? host)
        => PublicSuffix.HostMatchesDomain(host, "youtube.com") ||
           PublicSuffix.HostMatchesDomain(host, "youtu.be") ||
           PublicSuffix.HostMatchesDomain(host, "youtube-nocookie.com") ||
           PublicSuffix.HostMatchesDomain(host, "youtubekids.com");

    /// <summary>Hosts del servicio más su CDN de medios.</summary>
    public static bool IsServiceOrStreamHost(string? host)
        => IsServiceHost(host) || PublicSuffix.HostMatchesDomain(host, "googlevideo.com");

    private static bool IsStreamHost(string? host)
        => PublicSuffix.HostMatchesDomain(host, "googlevideo.com");

    // ── Red ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Devuelve true si la petición es un endpoint publicitario explícito del servicio.
    /// Cada rama cita la regla de la lista de Brave de la que procede (líneas de
    /// <c>brave-main-list.txt</c> en el repositorio de referencia).
    /// </summary>
    public static bool IsAdRequest(Uri? uri, string? originHost, CoreWebView2WebResourceContext? context)
    {
        if (uri == null) return false;

        string host = uri.Host;
        string path = uri.AbsolutePath;

        if (IsStreamHost(host))
            return IsAdStreamRequest(uri, originHost, context);

        if (!IsServiceHost(host))
            return false;

        // ||ads.youtube.com/  (l. 17884, 48309): servidor de anuncios dedicado.
        if (PublicSuffix.HostMatchesDomain(host, "ads.youtube.com"))
            return true;

        // ||youtube.com/pagead/  (l. 122535): conversiones, adview, viewthroughconversion…
        if (path.StartsWith("/pagead/", StringComparison.OrdinalIgnoreCase))
            return true;

        // ||youtube.com/youtubei/v1/player/ad_break  (l. 122536)
        if (path.StartsWith("/youtubei/v1/player/ad_break", StringComparison.OrdinalIgnoreCase))
            return true;

        // ||www.youtube.com/get_midroll_  y  ||m.youtube.com/get_midroll_  (l. 122611-122613)
        if (path.StartsWith("/get_midroll_", StringComparison.OrdinalIgnoreCase))
            return true;

        // ||youtube.com/api/stats/ads?  y  ||youtubekids.com/api/stats/ads?  (l. 155703, 155716)
        // Solo el endpoint /ads. Las estadísticas de reproducción (playback, watchtime, atr, qoe)
        // NO se bloquean: la lista de referencia desactiva incluso la regla genérica de atr.
        if (path.StartsWith("/api/stats/ads", StringComparison.OrdinalIgnoreCase))
            return true;

        // ||youtube.com/get_video_info?*adunit  (l. 122614)
        if (path.StartsWith("/get_video_info", StringComparison.OrdinalIgnoreCase) &&
            uri.Query.Contains("adunit", StringComparison.OrdinalIgnoreCase))
            return true;

        // ||m.youtube.com/*/ad.js$script  (l. 16232)
        if (path.EndsWith("/ad.js", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(host, "m.youtube.com", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool IsAdStreamRequest(Uri uri, string? originHost, CoreWebView2WebResourceContext? context)
    {
        // Ambas reglas de la lista están acotadas a páginas del servicio ($domain=youtube.com).
        if (!IsServiceHost(originHost))
            return false;

        // ||googlevideo.com/initplayback?source=youtube&*c=TVHTML5&*oad=$xhr  (l. 35)
        if (uri.AbsolutePath.Equals("/initplayback", StringComparison.OrdinalIgnoreCase))
        {
            string q = uri.Query;
            if (q.Contains("source=youtube", StringComparison.OrdinalIgnoreCase) &&
                q.Contains("c=TVHTML5", StringComparison.Ordinal) &&
                q.Contains("oad=", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // *_ad_$media,domain=youtube.com,3p  (l. 12)
        if (context == CoreWebView2WebResourceContext.Media &&
            uri.AbsoluteUri.Contains("_ad_", StringComparison.Ordinal))
            return true;

        return false;
    }

    /// <summary>
    /// True si la petición bloqueada debe responderse con <c>200 {}</c> en lugar de 204/403:
    /// el reproductor espera un JSON en estas llamadas y un error de red lo desconcierta.
    /// </summary>
    public static bool WantsEmptyJson(Uri? uri)
    {
        if (uri == null || !IsServiceHost(uri.Host)) return false;
        string path = uri.AbsolutePath;
        return path.StartsWith("/youtubei/v1/player/ad_break", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/get_midroll_", StringComparison.OrdinalIgnoreCase);
    }

    // ── Presentación (filtros cosméticos) ────────────────────────────────────────────────────

    /// <summary>
    /// Selectores de huecos publicitarios. Solo anuncios: no se ocultan estanterías de
    /// merchandising, tienda, pegatinas ni sugerencias, que son contenido o funciones de la página.
    /// <para>
    /// <c>#player-ads</c> queda FUERA a propósito: la lista de referencia lo exceptúa
    /// explícitamente (<c>youtube.com#@##player-ads</c>, l. 51913) porque ocultarlo daña el reproductor.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> CosmeticSelectors = new[]
    {
        // uBlock Origin (filters.txt l. 96-106)
        ".masthead-ad-control",
        ".ad-div",
        ".pyv-afc-ads-container",
        "#promotion-shelf",
        "#masthead-ad",
        ".ytp-ad-progress",

        // Brave (brave-main-list.txt l. 132018-132038)
        "#contents > ytd-rich-item-renderer:has(> ytd-ad-slot-renderer)",
        "ytd-rich-item-renderer:has(> #content > ytd-ad-slot-renderer)",
        "#shorts-inner-container > .ytd-shorts:has(> .ytd-reel-video-renderer > ytd-ad-slot-renderer)",
        ".ytd-section-list-renderer > .ytd-item-section-renderer > ytd-search-pyv-renderer.ytd-item-section-renderer",
        ".ytd-two-column-browse-results-renderer > ytd-rich-grid-renderer > #masthead-ad.ytd-rich-grid-renderer",
        ".ytd-watch-flexy > .ytd-watch-next-secondary-results-renderer > ytd-ad-slot-renderer.ytd-watch-next-secondary-results-renderer",
        ".grid.ytd-browse > #primary > .style-scope > .ytd-rich-grid-renderer > .ytd-rich-grid-renderer > .ytd-ad-slot-renderer",
        ".ytd-rich-item-renderer.style-scope > .ytd-rich-item-renderer > .ytd-ad-slot-renderer.style-scope",
        "ytd-item-section-renderer > .ytd-item-section-renderer > ytd-ad-slot-renderer.style-scope",

        // Versión móvil (m.youtube.com)
        "lazy-list > ad-slot-renderer",
        "ytm-companion-slot[data-content-type] > ytm-companion-ad-renderer",
        "ytm-rich-item-renderer > ad-slot-renderer",
    };

    /// <summary>
    /// Una regla CSS por selector: si un selector no lo soporta el motor, solo se pierde esa regla
    /// (en una lista separada por comas, un selector inválido invalidaría toda la regla).
    /// </summary>
    public static string BuildCosmeticCss()
    {
        var sb = new StringBuilder();
        foreach (string selector in CosmeticSelectors)
        {
            sb.Append(selector).Append(" { display: none !important; }\n");
        }
        return sb.ToString();
    }
}

