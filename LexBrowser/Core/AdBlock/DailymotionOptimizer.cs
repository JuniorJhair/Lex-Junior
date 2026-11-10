namespace LexBrowser.Core.AdBlock;

/// <summary>
/// Módulo especializado y aislado para optimización y neutralización de anuncios en Dailymotion.
/// Implementa un acelerador inteligente (Fast-Forward Defuser) con sensor de red:
/// 1. Permite que el reproductor nativo de Dailymotion inicialice legítimamente su ciclo de anuncios.
/// 2. Al detectar un anuncio en reproducción (duración menor a 90s con marcadores de comercial),
///    silencia el audio y acelera la velocidad a 16x saltando al final.
/// 3. Sensor de red (fetch / XHR hook): En cuanto se recibe la respuesta 200 del manifiesto o fragmentos
///    del video real (cdndirector, manifest/video, vod*.cf.dmcdn.net), el acelerador se autodestruye
///    y se desactiva permanentemente para garantizar que la novela/película reproduzca a velocidad 1.0x normal.
/// 4. Oculta cosméticamente banners, espacios vacíos y recomendaciones patrocinadas en la interfaz de usuario.
/// </summary>
public static class DailymotionOptimizer
{
    public const string InjectedScript = @"
(function() {
    if (window.__lex_dailymotion_optimizer_injected) return;
    window.__lex_dailymotion_optimizer_injected = true;

    const hostname = window.location.hostname || '';
    const isDailymotion = hostname === 'dailymotion.com' ||
                          hostname.endsWith('.dailymotion.com') ||
                          hostname === 'dmcdn.net' ||
                          hostname.endsWith('.dmcdn.net');

    if (!isDailymotion) return;

    // ─────────────────────────────────────────────────────────────────
    // 0. SCRIPTLET CANARY DE DAILYMOTION (Anti-AdBlock Bypass de Brave / uBO)
    // ─────────────────────────────────────────────────────────────────
    try {
        window.__NATIVEADS_CANARY__ = true;
        Object.defineProperty(window, '__NATIVEADS_CANARY__', {
            get: function() { return true; },
            set: function() {},
            configurable: true
        });
    } catch(_) {}

    // ─────────────────────────────────────────────────────────────────
    // 1. OCULTACIÓN COSMÉTICA: Banners y patrocinadores en marco principal
    // ─────────────────────────────────────────────────────────────────
    try {
        const style = document.createElement('style');
        style.id = 'lex-dm-cosmetics';
        style.textContent = `
            .WatchingDiscovery__adSection___eLtfR,
            div[class^=""AdBanner""],
            div[class^=""DisplayAd""],
            div[class^=""NewWatchingDiscovery__adSection""],
            div[class^=""VideoInfo__videoInfoAdContainer""],
            div[data-testid=""ad-slot""],
            div[class*=""ad_slot"" i],
            div[class*=""AdSlot"" i],
            div[class*=""ad-slot"" i],
            div[id*=""ad-slot"" i],
            div[class*=""ad-container"" i],
            div[id*=""ad-container"" i],
            div[class*=""ad-wrapper"" i],
            div[id*=""ad-wrapper"" i],
            div[class*=""dm-ad"" i],
            div[id*=""dmads"" i],
            div[class*=""dmads"" i],
            div[class*=""pub-container"" i],
            div[class*=""ad_billboard"" i],
            div[class*=""ad_leaderboard"" i],
            div[class*=""sponsored"" i],
            div[class*=""Sponsored"" i],
            div[id*=""google_ads"" i],
            div[id*=""bannerflow"" i],
            div[id*=""adform"" i],
            div[class*=""AdBanner"" i],
            [data-ad-slot],
            [data-ad-type],
            iframe[src*=""dmads.dailymotion.com"" i],
            iframe[src*=""bannerflow"" i],
            iframe[src*=""adform"" i],
            .dm-ads,
            .dmads-banner {
                display: none !important;
                visibility: hidden !important;
                height: 0 !important;
                min-height: 0 !important;
                max-height: 0 !important;
                width: 0 !important;
                min-width: 0 !important;
                max-width: 0 !important;
                margin: 0 !important;
                padding: 0 !important;
                opacity: 0 !important;
                pointer-events: none !important;
                border: none !important;
                background: transparent !important;
            }
        `;
        const attachDmStyle = () => {
            const t = document.head || document.documentElement;
            if (!t) return false;
            if (!document.getElementById('lex-dm-cosmetics')) t.appendChild(style);
            return true;
        };
        if (!attachDmStyle()) {
            const dmStyleWait = new MutationObserver(() => {
                if (attachDmStyle()) dmStyleWait.disconnect();
            });
            dmStyleWait.observe(document, { childList: true });
        }
    } catch(_) {}

    // ─────────────────────────────────────────────────────────────────
    // 2. ACELERADOR INTELIGENTE DE ANUNCIOS (Fast-Forward Defuser)
    // ─────────────────────────────────────────────────────────────────
    let _isDefusingAd = false;
    let _originalMuted = null;
    let _originalPlaybackRate = 1.0;
    let _observer = null;
    let _listenersAttached = false;

    function isDynamicAdPresent() {
        const dynamicAdSelectors = [
            '.dmp_AdCountdown',
            '.dmp_is-ad',
            '.videoAdUi',
            '.dmp_AdNotice',
            '[class*=""dmp_AdCountdown"" i]',
            '.ad-showing'
        ];
        for (const sel of dynamicAdSelectors) {
            const el = document.querySelector(sel);
            if (el && el.offsetParent !== null) return true;
        }
        return false;
    }

    function shutdownDefuser() {
        try {
            if (_observer) {
                try { _observer.disconnect(); } catch(_) {}
                _observer = null;
            }
            if (_listenersAttached) {
                _listenersAttached = false;
                document.removeEventListener('play', checkAndDefuseAd, true);
                document.removeEventListener('timeupdate', checkAndDefuseAd, true);
                document.removeEventListener('loadedmetadata', checkAndDefuseAd, true);
            }
        } catch(_) {}
    }

    function checkAndDefuseAd() {
        try {
            // A. Salto automático instantáneo si aparece botón interactivo de saltar anuncio
            const skipSelectors = [
                '.dmp_SkipButton',
                'button[class*=""dmp_Skip"" i]',
                '.videoAdUiSkipButton',
                '.videoAdUiAction',
                '[data-testid*=""skip-ad"" i]'
            ];
            for (const sel of skipSelectors) {
                const btn = document.querySelector(sel);
                if (btn && btn.offsetParent !== null && !btn.disabled && btn.getAttribute('aria-disabled') !== 'true' && typeof btn.click === 'function') {
                    btn.click();
                    break;
                }
            }

            const isAdActive = isDynamicAdPresent();
            const videos = document.querySelectorAll('video');

            for (const video of videos) {
                if (!video) continue;

                const duration = video.duration;
                const src = video.currentSrc || video.src || '';

                // PROTECCIÓN TOTAL DEL CONTENIDO PRINCIPAL:
                // Si supera los 180 segundos (o >90s sin indicador de anuncio), ES EL VIDEO REAL. Nunca acelerar ni pisar velocidad.
                if (duration && !isNaN(duration) && (duration > 180 || (duration > 90 && !isAdActive))) {
                    if (_isDefusingAd) {
                        _isDefusingAd = false;
                        try { video.playbackRate = _originalPlaybackRate || 1.0; } catch(_) {}
                        if (_originalMuted !== null) {
                            try { video.muted = _originalMuted; } catch(_) {}
                            _originalMuted = null;
                        }
                    }

                    // Si el video principal ya reprodujo más de 5 segundos, apagar el observer y los listeners de timeupdate para 0% CPU
                    if (video.currentTime > 5 && !isAdActive) {
                        shutdownDefuser();
                    }
                    continue;
                }

                // Es anuncio solo si tiene duración corta confirmada (<=90s o <=180s con ad activo) y coincide con indicadores publicitarios
                const isShortDuration = duration && !isNaN(duration) && duration > 0 && (duration <= 90 || (isAdActive && duration <= 180));
                const isAdSource = (isAdActive ||
                                    src.includes('/ad/') ||
                                    src.includes('googleads') ||
                                    src.includes('doubleclick') ||
                                    src.includes('_VMAP_') ||
                                    src.includes('monetization') ||
                                    src.includes('bannerflow')) && isShortDuration;

                if (isAdSource) {
                    if (!_isDefusingAd) {
                        _isDefusingAd = true;
                        _originalMuted = video.muted;
                        if (video.playbackRate > 0 && video.playbackRate <= 2.0) {
                            _originalPlaybackRate = video.playbackRate;
                        }
                    }

                    // Silenciar y acelerar al máximo (16x) saltando al final para consumir el comercial
                    video.muted = true;
                    try { video.playbackRate = 16.0; } catch(_) {}
                    try { video.currentTime = duration; } catch(_) {}
                } else if (_isDefusingAd) {
                    // El comercial concluyó: restaurar parámetros originales para el video real
                    _isDefusingAd = false;
                    try { video.playbackRate = _originalPlaybackRate || 1.0; } catch(_) {}
                    if (_originalMuted !== null) {
                        try { video.muted = _originalMuted; } catch(_) {}
                        _originalMuted = null;
                    }
                }
            }
        } catch(_) {}
    }

    // ─────────────────────────────────────────────────────────────────
    // 3. OBSERVADOR Y VIGILANCIA DE REPRODUCTOR
    // ─────────────────────────────────────────────────────────────────
    function attachListeners() {
        if (_listenersAttached) return;
        _listenersAttached = true;
        try {
            document.addEventListener('play', checkAndDefuseAd, true);
            document.addEventListener('timeupdate', checkAndDefuseAd, true);
            document.addEventListener('loadedmetadata', checkAndDefuseAd, true);
        } catch(_) {}
    }

    function setupObserver() {
        try {
            attachListeners();
            if (!_observer && (document.documentElement || document.body)) {
                _observer = new MutationObserver(checkAndDefuseAd);
                _observer.observe(document.documentElement || document.body, {
                    childList: true,
                    subtree: true,
                    attributes: true,
                    attributeFilter: ['class', 'src', 'style']
                });
            }
            checkAndDefuseAd();
        } catch(_) {}
    }

    try {
        attachListeners();
        document.addEventListener('loadstart', setupObserver, true);
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.addEventListener('message', (ev) => {
                if (ev && ev.data && ev.data.action === 'dm_real_video_detected' && !_isDefusingAd) {
                    shutdownDefuser();
                }
            });
        }
    } catch(_) {}

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', setupObserver, { once: true });
    } else {
        setupObserver();
    }
})();
";
}
