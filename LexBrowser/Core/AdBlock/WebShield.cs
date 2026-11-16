using System;
using System.Collections.Generic;
using System.Text.Json;

namespace LexBrowser.Core.AdBlock;

/// <summary>
/// Capa de respaldo pasiva de <c>LexShield Core</c> para reproductores externos
/// (JW Player / Fluid Player / Video.js). La cosmética, los scriptlets y los
/// interceptores de popups/overlays viven unificados en <c>LexShieldScriptletEngine</c>.
/// </summary>
public static class WebShield
{
    /// <summary>
    /// Interruptor para activar/desactivar el desarmador pasivo de anuncios en reproductores (JW Player / Fluid Player).
    /// Controlado por la configuración del usuario en Ajustes (por defecto false para máxima estabilidad).
    /// </summary>
    public static bool EnableVideoPlayerDefuser
    {
        get => SettingsManager.Current?.EnableVideoPlayerDefuser ?? false;
        set
        {
            if (SettingsManager.Current != null)
                SettingsManager.Current.EnableVideoPlayerDefuser = value;
        }
    }

    /// <summary>
    /// Devuelve el script pasivo de reproductor configurado según las preferencias actuales.
    /// </summary>
    public static string InjectedScript => GetVideoDefuserScript(
        EnableVideoPlayerDefuser,
        SettingsManager.Current?.Shield?.Sites);

    public static string GetInjectedScript(
        bool enableVideoDefuser,
        bool enableSubframes = false,
        bool enableCosmetic = true,
        bool enablePopups = true,
        bool enableOverlays = true,
        bool enableAntiWall = true)
        => GetVideoDefuserScript(enableVideoDefuser, SettingsManager.Current?.Shield?.Sites);

    internal static string GetVideoDefuserScript(
        bool enableVideoDefuser,
        IReadOnlyDictionary<string, SiteShieldProfile>? sitePolicies = null)
    {
        string sitesJson = "{}";
        try
        {
            if (sitePolicies != null && sitePolicies.Count > 0)
            {
                sitesJson = JsonSerializer.Serialize(sitePolicies);
            }
        }
        catch { }

        string trustedHostsJson = "[]";
        try
        {
            trustedHostsJson = JsonSerializer.Serialize(ShieldExemptions.BigSites);
        }
        catch { }

        string authHostsJson = "[]";
        try
        {
            authHostsJson = JsonSerializer.Serialize(ShieldExemptions.PaymentOrAuthHosts);
        }
        catch { }

        return VideoPlayerDefuserScript
            .Replace("const __LEX_ENABLE_VIDEO_DEFUSER = true;", $"const __LEX_ENABLE_VIDEO_DEFUSER = {(enableVideoDefuser ? "true" : "false")};")
            .Replace("const __LEX_TRUSTED_HOSTS = [];", $"const __LEX_TRUSTED_HOSTS = {trustedHostsJson};")
            .Replace("const __LEX_AUTH_HOSTS = [];", $"const __LEX_AUTH_HOSTS = {authHostsJson};")
            .Replace("const __LEX_SITE_POLICIES = {};", $"const __LEX_SITE_POLICIES = {sitesJson};");
    }

    /// <summary>
    /// Desarmador pasivo y acotado para reproductores de video genéricos (JW Player / Fluid Player / Video.js).
    /// Excluye estrictamente YouTube, Google, Dailymotion, destinos internos, PaymentOrAuthHosts y políticas Off/Compat.
    /// Actúa únicamente dentro del contenedor del reproductor cuando un anuncio está activo y restaura velocidad y audio al terminar.
    /// </summary>
    public const string VideoPlayerDefuserScript = @"
(function() {
    if (window.__lex_video_defuser_injected) return;
    window.__lex_video_defuser_injected = true;

    const __LEX_ENABLE_VIDEO_DEFUSER = true;
    const __LEX_TRUSTED_HOSTS = [];
    const __LEX_AUTH_HOSTS = [];
    const __LEX_SITE_POLICIES = {};

    const h = (location.hostname || '').toLowerCase().replace(/\.+$/, '');
    if (!h || h === 'tv.lex.local' || h === 'pdf.lex.local') return;

    const isTopWindow = (window.top === window.self);
    const ends = (d) => h === d || h.endsWith('.' + d);
    const isEmbedFrame = !isTopWindow && (
        /(streamwish|wishfast|wishembed|vidhide|streamtape|dood|d000d|ds2play|filelions|lulustream|luluvdo|mp4upload|uqload|voe\.sx|mixdrop|fastream|hglink|dhcplay|fembed|upstream|vidoza|wolfstream|megacloud|rabbitstream|vidplay|yourupload)/i.test(h) ||
        /^\/(e|v|embed|player)(\/|$|-)/i.test(window.location.pathname || '') ||
        /\/(videoembed|video_ext)/i.test(window.location.pathname || '')
    );

    if (!__LEX_ENABLE_VIDEO_DEFUSER && !isEmbedFrame) return;

    if (ends('youtube.com') || ends('youtu.be') || ends('youtube-nocookie.com') ||
        ends('dailymotion.com') || ends('dmcdn.net') || ends('dmxleo.com') ||
        ends('google.com') || ends('gstatic.com') || ends('googleusercontent.com') ||
        (__LEX_TRUSTED_HOSTS && __LEX_TRUSTED_HOSTS.some(ends)) ||
        (__LEX_AUTH_HOSTS && __LEX_AUTH_HOSTS.some(ends))) {
        return;
    }

    let siteMode = 'Normal';
    let siteDisabled = [];
    try {
        for (const [domain, prof] of Object.entries(__LEX_SITE_POLICIES || {})) {
            const d = (domain || '').toLowerCase();
            if (d && ends(d) && prof) {
                siteMode = prof.Mode || 'Normal';
                siteDisabled = Array.isArray(prof.Disabled) ? prof.Disabled.map(x => String(x).toLowerCase()) : [];
                break;
            }
        }
    } catch(_) {}

    const modeLower = siteMode.toLowerCase();
    if (modeLower === 'off' || modeLower === 'compat' || siteDisabled.includes('videodefuser')) return;

    const savedState = new WeakMap();
    let attempts = 0;
    let timeupdateArmed = false;

    const setTimeupdateArmed = (arm) => {
        try {
            if (arm && !timeupdateArmed) {
                document.addEventListener('timeupdate', trySkipPreRoll, true);
                timeupdateArmed = true;
            } else if (!arm && timeupdateArmed) {
                document.removeEventListener('timeupdate', trySkipPreRoll, true);
                timeupdateArmed = false;
            }
        } catch(_) {}
    };

    function trySkipPreRoll(e) {
        try {
            const targetVideo = (e && e.target && e.target.tagName === 'VIDEO') ? e.target : null;
            const videos = targetVideo ? [targetVideo] : document.querySelectorAll('video');
            let anyAdPending = false;

            for (let i = 0; i < videos.length; i++) {
                const v = videos[i];
                if (!v) continue;

                const player = v.closest('.jwplayer, .video-js, .fluid_video_wrapper');
                if (!player) continue;

                const adActive = player.matches('.jw-flag-ads, .vjs-ad-playing') ||
                                 !!player.querySelector('.videoAdUi, .fluid_ad_countdown, .vast-skip-button');

                if (adActive) {
                    anyAdPending = true;
                    if (!savedState.has(v)) {
                        savedState.set(v, { muted: v.muted, rate: v.playbackRate || 1.0 });
                    }
                    v.muted = true;
                    try { v.playbackRate = 16.0; } catch(_) {}
                    if (isFinite(v.duration) && v.duration > 0 && v.duration <= 90) {
                        try { v.currentTime = v.duration; } catch(_) {}
                    }

                    const skipBtns = player.querySelectorAll(
                        '.jw-skip, .videoAdUiSkipButton, .vast-skip-button, .vjs-skip-button, .fluid_ad_countdown'
                    );
                    for (let j = 0; j < skipBtns.length; j++) {
                        const btn = skipBtns[j];
                        if (btn && btn.offsetParent !== null && typeof btn.click === 'function') {
                            btn.click();
                            break;
                        }
                    }
                } else if (savedState.has(v)) {
                    const st = savedState.get(v);
                    savedState.delete(v);
                    v.muted = st.muted;
                    try { v.playbackRate = st.rate; } catch(_) {}
                }
            }

            if (anyAdPending) {
                setTimeupdateArmed(true);
            } else if (e && e.type === 'timeupdate') {
                setTimeupdateArmed(false);
            }
        } catch(_) {}
    }

    const attachSafePlayerListener = () => {
        try {
            if (typeof window.jwplayer === 'function') {
                const p = window.jwplayer();
                if (p && typeof p.on === 'function') {
                    p.on('adTime', function() {
                        try {
                            if (typeof p.skipAd === 'function') p.skipAd();
                        } catch(_) {}
                    });
                    p.on('adError', function() {
                        try {
                            if (typeof p.play === 'function') p.play();
                        } catch(_) {}
                    });
                    p.on('adBlock', function() {
                        try {
                            if (typeof p.play === 'function') p.play();
                        } catch(_) {}
                    });
                    return;
                }
            }
        } catch(_) {}
        if ((isEmbedFrame || document.querySelector('.jwplayer, video')) && ++attempts < 6) {
            setTimeout(attachSafePlayerListener, 600);
        }
    };

    try {
        document.addEventListener('play', trySkipPreRoll, true);
    } catch(_) {}

    if (document.readyState === 'complete') {
        attachSafePlayerListener();
    } else {
        window.addEventListener('load', attachSafePlayerListener, { once: true });
    }
})();
";
}
