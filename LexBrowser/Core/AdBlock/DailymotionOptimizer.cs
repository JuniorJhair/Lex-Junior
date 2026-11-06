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
    // 1. OCULTACIÓN COSMÉTICA: Banners y patrocinadores en marco principal
    // ─────────────────────────────────────────────────────────────────
    try {
        const style = document.createElement('style');
        style.id = 'lex-dm-cosmetics';
        style.textContent = `
            div[data-testid=""ad-slot""],
            div[class*=""ad_slot"" i],
            div[class*=""AdSlot"" i],
            div[class*=""sponsored"" i],
            div[class*=""Sponsored"" i],
            div[id*=""google_ads"" i],
            div[id*=""bannerflow"" i],
            div[id*=""adform"" i],
            div[class*=""AdBanner"" i],
            iframe[src*=""dmads.dailymotion.com"" i],
            iframe[src*=""bannerflow"" i],
            iframe[src*=""adform"" i],
            .dm-ads,
            .dmads-banner {
                display: none !important;
                visibility: hidden !important;
                height: 0 !important;
                width: 0 !important;
                opacity: 0 !important;
                pointer-events: none !important;
            }
        `;
        (document.head || document.documentElement).appendChild(style);
    } catch(_) {}

    // ─────────────────────────────────────────────────────────────────
    // 2. SENSOR DE RED: Reconocedor de entrega del video real (Manifiesto / VOD)
    // ─────────────────────────────────────────────────────────────────
    let _mainVideoReceived = false;
    let _isDefusingAd = false;
    let _originalMuted = null;
    let _observer = null;

    function isMainContentUrl(url) {
        if (!url || typeof url !== 'string') return false;
        return (url.includes('/cdn/manifest/video/') ||
                url.includes('cdndirector.dailymotion.com') ||
                url.includes('dmxleo.dailymotion.com/cdn/manifest/') ||
                (url.includes('dmcdn.net') && url.includes('/video/')) ||
                url.includes('vod3.cf.dmcdn.net') ||
                url.includes('.cf.dmcdn.net/sec'));
    }

    function onMainVideoReceived() {
        if (_mainVideoReceived) return;
        _mainVideoReceived = true;

        // Desactivación inmediata e irreversible del acelerador
        try {
            const videos = document.querySelectorAll('video');
            for (const v of videos) {
                try { v.playbackRate = 1.0; } catch(_) {}
                if (v.muted && _originalMuted === false) {
                    try { v.muted = false; } catch(_) {}
                }
            }
        } catch(_) {}

        if (_observer) {
            try { _observer.disconnect(); } catch(_) {}
            _observer = null;
        }
    }

    // Interceptar fetch dentro del reproductor
    try {
        const origFetch = window.fetch;
        window.fetch = function(input, init) {
            const url = (typeof input === 'string') ? input : (input && input.url);
            const p = origFetch.apply(this, arguments);
            if (isMainContentUrl(url)) {
                p.then(function(res) {
                    if (res && (res.status === 200 || res.status === 206 || res.ok)) {
                        onMainVideoReceived();
                    }
                }).catch(function() {});
            }
            return p;
        };
    } catch(_) {}

    // Interceptar XMLHttpRequest dentro del reproductor
    try {
        const origXhrOpen = XMLHttpRequest.prototype.open;
        XMLHttpRequest.prototype.open = function(method, url) {
            this.__lex_dm_url = url;
            return origXhrOpen.apply(this, arguments);
        };

        const origXhrSend = XMLHttpRequest.prototype.send;
        XMLHttpRequest.prototype.send = function() {
            if (isMainContentUrl(this.__lex_dm_url)) {
                this.addEventListener('load', function() {
                    if (this.status === 200 || this.status === 206) {
                        onMainVideoReceived();
                    }
                }, { once: true });
            }
            return origXhrSend.apply(this, arguments);
        };
    } catch(_) {}

    // ─────────────────────────────────────────────────────────────────
    // 3. ACELERADOR INTELIGENTE DE ANUNCIOS (Fast-Forward Defuser)
    // ─────────────────────────────────────────────────────────────────
    function checkAndDefuseAd() {
        if (_mainVideoReceived) return;

        try {
            // A. Salto automático si aparece botón interactivo de saltar
            const skipSelectors = [
                '.dmp_SkipButton',
                'button[class*=""skip"" i]',
                'button[class*=""Skip"" i]',
                '.videoAdUiSkipButton',
                '.videoAdUiAction',
                '[data-testid*=""skip"" i]'
            ];
            for (const sel of skipSelectors) {
                const btn = document.querySelector(sel);
                if (btn && btn.offsetParent !== null && typeof btn.click === 'function') {
                    btn.click();
                    break;
                }
            }

            // B. Detección ESTRICTA de comerciales activos
            // No usamos selectores persistentes (.ima-ad-container) que permanecen para siempre en el DOM.
            const dynamicAdSelectors = [
                '.dmp_AdCountdown',
                '.dmp_is-ad',
                '.videoAdUi',
                '.dmp_AdNotice',
                '[class*=""dmp_AdCountdown"" i]',
                '.ad-showing'
            ];
            let isAdActive = false;
            for (const sel of dynamicAdSelectors) {
                const el = document.querySelector(sel);
                if (el && el.offsetParent !== null) {
                    isAdActive = true;
                    break;
                }
            }

            // C. Inspección y aceleración de videos en reproducción
            const videos = document.querySelectorAll('video');
            for (const video of videos) {
                if (!video) continue;

                const src = video.currentSrc || video.src || '';
                const duration = video.duration;

                // PROTECCIÓN TOTAL: Si el video dura más de 90 segundos o proviene de la CDN de video,
                // ES EL CONTENIDO PRINCIPAL. Desactivar acelerador inmediatamente.
                if (isMainContentUrl(src) || (duration && !isNaN(duration) && duration > 90)) {
                    onMainVideoReceived();
                    return;
                }

                // Es anuncio solo si tiene duración corta (<= 90s) y coincide con indicadores publicitarios
                const isShortDuration = !duration || isNaN(duration) || (duration > 0 && duration <= 90);
                const isAdSource = (isAdActive ||
                                    src.includes('/ad/') ||
                                    src.includes('googleads') ||
                                    src.includes('doubleclick') ||
                                    src.includes('dmxleo') ||
                                    src.includes('bannerflow')) && isShortDuration;

                if (isAdSource) {
                    if (!_isDefusingAd) {
                        _isDefusingAd = true;
                        _originalMuted = video.muted;
                    }

                    // Silenciar y acelerar al máximo (16x) para consumir el anuncio instantáneamente
                    video.muted = true;
                    try { video.playbackRate = 16.0; } catch(_) {}
                    if (duration && !isNaN(duration) && duration > 0) {
                        try { video.currentTime = duration; } catch(_) {}
                    }
                } else if (_isDefusingAd) {
                    // El comercial concluyó: restaurar parámetros para el video real
                    _isDefusingAd = false;
                    try { video.playbackRate = 1.0; } catch(_) {}
                    if (_originalMuted !== null) {
                        video.muted = _originalMuted;
                        _originalMuted = null;
                    }
                }
            }
        } catch(_) {}
    }

    // D. Observador continuo ultra liviano
    function setupObserver() {
        if (_mainVideoReceived) return;
        try {
            _observer = new MutationObserver(checkAndDefuseAd);
            _observer.observe(document.documentElement || document.body, {
                childList: true,
                subtree: true,
                attributes: true,
                attributeFilter: ['class', 'src', 'style']
            });

            document.addEventListener('play', checkAndDefuseAd, true);
            document.addEventListener('timeupdate', checkAndDefuseAd, true);
            document.addEventListener('loadedmetadata', checkAndDefuseAd, true);

            checkAndDefuseAd();
        } catch(_) {}
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', setupObserver, { once: true });
    } else {
        setupObserver();
    }
})();
";
}
