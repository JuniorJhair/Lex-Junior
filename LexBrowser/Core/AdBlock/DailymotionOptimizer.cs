namespace LexBrowser.Core.AdBlock;

/// <summary>
/// Módulo especializado y aislado para optimización y neutralización de anuncios en Dailymotion.
/// Implementa un acelerador inteligente (Fast-Forward Defuser) que:
/// 1. Permite que el reproductor nativo de Dailymotion inicialice legítimamente su ciclo de anuncios.
/// 2. Al detectar un anuncio en reproducción, silencia el audio y acelera la velocidad a 16x saltando al final.
/// 3. El reproductor recibe las confirmaciones requeridas (start, mid, end) y entrega el video real en menos de 0.2s.
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
    // 2. ACELERADOR INTELIGENTE DE ANUNCIOS EN REPRODUCTOR (Fast-Forward)
    // ─────────────────────────────────────────────────────────────────
    let _isDefusingAd = false;
    let _originalMuted = null;

    function checkAndDefuseAd() {
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

            // B. Detección de marcadores o contenedores publicitarios activos
            const adIndicators = [
                '.dmp_AdContainer',
                '.dmp_AdCountdown',
                '.dmp_AdPlayer',
                '.dmp_VideoAd',
                '.dmp_is-ad',
                '.dmp_has-ad',
                '[class*=""dmp_Ad"" i]',
                '[class*=""AdContainer"" i]',
                '[class*=""ad-container"" i]',
                '.ima-ad-container',
                'div[id*=""ima-ad-container"" i]',
                '.ad-showing',
                '.is-ad'
            ];
            let isAdActive = false;
            for (const sel of adIndicators) {
                const el = document.querySelector(sel);
                if (el && (el.offsetParent !== null || el.classList.contains('dmp_is-ad') || el.classList.contains('is-ad'))) {
                    isAdActive = true;
                    break;
                }
            }

            // C. Inspección y aceleración de videos en reproducción
            const videos = document.querySelectorAll('video');
            for (const video of videos) {
                if (!video) continue;

                const src = video.currentSrc || video.src || '';
                const isAdSource = isAdActive ||
                                   (src && (src.includes('/ad/') ||
                                            src.includes('googleads') ||
                                            src.includes('doubleclick') ||
                                            src.includes('dmxleo') ||
                                            src.includes('bannerflow')));

                const isMainVideo = src && (src.includes('dmcdn.net') || src.includes('/manifest/video/')) && !isAdActive;

                if (isAdSource && !isMainVideo) {
                    if (!_isDefusingAd) {
                        _isDefusingAd = true;
                        _originalMuted = video.muted;
                    }

                    // Silenciar y acelerar al máximo (16x) para consumir el anuncio instantáneamente
                    video.muted = true;
                    try { video.playbackRate = 16.0; } catch(_) {}
                    if (video.duration && !isNaN(video.duration) && video.duration > 0) {
                        try { video.currentTime = video.duration; } catch(_) {}
                    }
                } else if (_isDefusingAd && (isMainVideo || !isAdActive)) {
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
        try {
            const observer = new MutationObserver(checkAndDefuseAd);
            observer.observe(document.documentElement || document.body, {
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
