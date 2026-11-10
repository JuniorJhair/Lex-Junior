namespace LexBrowser.Core.AdBlock;

/// <summary>
/// Módulo especializado y estrictamente aislado para YouTube (youtube.com, youtu.be, youtube-nocookie.com).
/// Se desactiva en la primera línea en cualquier otro sitio (0% CPU/RAM fuera de YouTube).
/// Incluye:
/// 1. Limpieza temprana de payloads publicitarios en ytInitialPlayerResponse, fetch y XHR (con caché readyState===4).
/// 2. Salto instantáneo de anuncios (incluyendo anuncios largos hasta 180s), silencio 0ms, 16x y clic preciso en el botón Skip.
/// 3. Cortina visual fantasma por opacidad sin colapsar la geometría del reproductor ni la detección de anuncios.
/// 4. Inyección segura de CSS aunque el script corra antes de existir document.head/documentElement.
/// </summary>
public static class YouTubeOptimizer
{
    public const string InjectedScript = @"
(function() {
    if (window.__lex_youtube_optimizer_injected) return;
    window.__lex_youtube_optimizer_injected = true;

    const hostname = (window.location.hostname || '').toLowerCase();
    const isYouTube = hostname === 'youtube.com' ||
                      hostname.endsWith('.youtube.com') ||
                      hostname === 'youtu.be' ||
                      hostname === 'youtube-nocookie.com' ||
                      hostname.endsWith('.youtube-nocookie.com');

    // Exención total fuera de YouTube: 0% sobrecarga en Miro, Google, Canva, Dailymotion, etc.
    if (!isYouTube) return;

    // ─────────────────────────────────────────────────────────────────
    // 1. PRE-HOOK DE PAYLOADS PUBLICITARIOS (Initial + SPA fetch/XHR)
    // ─────────────────────────────────────────────────────────────────
    try {
        const cleanPlayerPayload = function(data) {
            if (!data || typeof data !== 'object') return data;
            try {
                if (Array.isArray(data.adPlacements)) data.adPlacements = [];
                if (Array.isArray(data.playerAds)) data.playerAds = [];
                if (Array.isArray(data.adSlots)) data.adSlots = [];
                if (data.playerResponse) cleanPlayerPayload(data.playerResponse);
                if (data.contents) cleanPlayerPayload(data.contents);
            } catch(_) {}
            return data;
        };

        let _ytInitialPlayerResponse = window.ytInitialPlayerResponse;
        Object.defineProperty(window, 'ytInitialPlayerResponse', {
            get: function() { return _ytInitialPlayerResponse; },
            set: function(val) { _ytInitialPlayerResponse = cleanPlayerPayload(val); },
            configurable: true
        });

        // Solo endpoints de reproductor real (excluido /youtubei/v1/next para evitar reparsear JSON gigante de comentarios/sidebar)
        const isPlayerEndpoint = (u) => typeof u === 'string' && (
            u.indexOf('/youtubei/v1/player') !== -1 ||
            u.indexOf('/youtubei/v1/reel/reel_item_watch') !== -1
        );

        const origFetch = window.fetch;
        window.fetch = function(input, init) {
            const url = (typeof input === 'string') ? input : (input && input.url);
            const p = origFetch.apply(this, arguments);
            if (!isPlayerEndpoint(url)) return p;
            return p.then(function(res) {
                return res.clone().json().then(function(json) {
                    const body = JSON.stringify(cleanPlayerPayload(json));
                    const synthetic = new Response(body, { status: res.status, statusText: res.statusText, headers: res.headers });
                    try {
                        Object.defineProperty(synthetic, 'url', { value: res.url, configurable: true });
                        Object.defineProperty(synthetic, 'redirected', { value: res.redirected, configurable: true });
                        Object.defineProperty(synthetic, 'type', { value: res.type, configurable: true });
                    } catch(_) {}
                    return synthetic;
                }).catch(function() { return res; });
            });
        };

        const origXhrOpen = XMLHttpRequest.prototype.open;
        XMLHttpRequest.prototype.open = function(method, url) {
            this.__lex_url = url;
            this.__lex_clean_text = undefined;
            this.__lex_clean_obj = undefined;
            return origXhrOpen.apply(this, arguments);
        };

        const hasAdFields = (str) => typeof str === 'string' &&
            (str.includes('adPlacements') || str.includes('playerAds') || str.includes('adSlots'));

        const origResponseTextDesc = Object.getOwnPropertyDescriptor(XMLHttpRequest.prototype, 'responseText');
        if (origResponseTextDesc && origResponseTextDesc.get) {
            Object.defineProperty(XMLHttpRequest.prototype, 'responseText', {
                get: function() {
                    if (this.readyState === 4 && this.__lex_clean_text !== undefined) {
                        return this.__lex_clean_text;
                    }
                    const raw = origResponseTextDesc.get.call(this);
                    if (isPlayerEndpoint(this.__lex_url) && hasAdFields(raw)) {
                        try {
                            const cleaned = JSON.stringify(cleanPlayerPayload(JSON.parse(raw)));
                            if (this.readyState === 4) this.__lex_clean_text = cleaned;
                            return cleaned;
                        } catch(_) {}
                    }
                    return raw;
                },
                configurable: true
            });
        }

        const origResponseDesc = Object.getOwnPropertyDescriptor(XMLHttpRequest.prototype, 'response');
        if (origResponseDesc && origResponseDesc.get) {
            Object.defineProperty(XMLHttpRequest.prototype, 'response', {
                get: function() {
                    if (this.readyState === 4 && this.__lex_clean_obj !== undefined) {
                        return this.__lex_clean_obj;
                    }
                    const raw = origResponseDesc.get.call(this);
                    if (!isPlayerEndpoint(this.__lex_url) || !raw) return raw;
                    try {
                        if (typeof raw === 'string' && hasAdFields(raw)) {
                            const cleaned = JSON.stringify(cleanPlayerPayload(JSON.parse(raw)));
                            if (this.readyState === 4) this.__lex_clean_obj = cleaned;
                            return cleaned;
                        }
                        if (typeof raw === 'object') {
                            const cleanedObj = cleanPlayerPayload(raw);
                            if (this.readyState === 4) this.__lex_clean_obj = cleanedObj;
                            return cleanedObj;
                        }
                    } catch(_) {}
                    return raw;
                },
                configurable: true
            });
        }
    } catch(_) {}

    // ─────────────────────────────────────────────────────────────────
    // 2. ANTI-ENFORCEMENT & LIMPIEZA DE DIÁLOGOS ANTI-ADBLOCK
    // ─────────────────────────────────────────────────────────────────
    const closePolymerDialog = (dialog) => {
        if (!dialog) return;
        try { if (typeof dialog.cancel === 'function') dialog.cancel(); } catch(_) {}
        try { if (typeof dialog.close === 'function') dialog.close(); } catch(_) {}
        try {
            dialog.removeAttribute('opened');
            dialog.setAttribute('aria-hidden', 'true');
        } catch(_) {}
    };

    const clearYouTubePopups = () => {
        const enforcement = document.querySelector('ytd-enforcement-message-view-model');
        if (!enforcement) return;

        const dialog = enforcement.closest('tp-yt-paper-dialog, ytd-modal-with-backdrop-renderer');
        if (dialog) closePolymerDialog(dialog);
        enforcement.remove();

        document.querySelectorAll('tp-yt-iron-overlay-backdrop').forEach(b => {
            b.classList.remove('opened');
            b.removeAttribute('opened');
            b.style.display = 'none';
        });

        const player = document.querySelector('#movie_player') || document.querySelector('.html5-video-player');
        if (player && typeof player.playVideo === 'function') {
            try { player.playVideo(); } catch(_) {}
        }
    };

    // ─────────────────────────────────────────────────────────────────
    // 3. SALTO INSTANTÁNEO DE ANUNCIOS (Cortos, Largos hasta 180s y Skip Real)
    // ─────────────────────────────────────────────────────────────────
    let wasMutedByAdBlock = false;
    let previousUserPlaybackRate = 1.0;
    let isHandlingAd = false;
    let wasAdReported = false;
    let wasSkipLogged = false;
    let lastAdCheckTime = 0;
    let adWatchdog = null;

    const MAX_AD_SECONDS = 180;
    const ACTIVE_AD_CHILD_SELECTORS =
        '.ytp-ad-player-overlay, .ytp-ad-player-overlay-layout, .ytp-ad-text, .ytp-ad-preview-container, ' +
        '.ytp-ad-overlay-container, .ytp-ad-skip-button, .ytp-ad-skip-button-modern, .ytp-skip-ad-button, ' +
        '.ytp-ad-simple-ad-badge, .video-ads > *, .ytp-ad-module > *';

    const SKIP_SELECTORS =
        'button.ytp-skip-ad-button, button.ytp-ad-skip-button-modern, button.ytp-ad-skip-button, ' +
        '.ytp-ad-skip-button-slot button, .ytp-skip-ad-button[role=""button""], [class*=""skip-button""] button';

    const findSkipButton = (player) => {
        try {
            const list = player.querySelectorAll(SKIP_SELECTORS);
            for (let i = 0; i < list.length; i++) {
                const b = list[i];
                if (!b) continue;
                const tag = b.tagName || '';
                const role = b.getAttribute ? (b.getAttribute('role') || '') : '';
                if (tag !== 'BUTTON' && role !== 'button') continue;
                if (!b.disabled && b.getAttribute('aria-disabled') !== 'true') {
                    return b;
                }
            }
        } catch(_) {}
        return null;
    };

    const skipYouTubeAds = () => {
        const moviePlayer = document.querySelector('#movie_player') || document.querySelector('.html5-video-player');
        const video = document.querySelector('video');
        if (!video || !moviePlayer) {
            if (adWatchdog) { clearInterval(adWatchdog); adWatchdog = null; }
            return;
        }

        const hasAdOverlay = !!moviePlayer.querySelector(ACTIVE_AD_CHILD_SELECTORS);
        const isAdShowing = (moviePlayer.classList.contains('ad-showing') || moviePlayer.classList.contains('ad-interrupting')) &&
                            (hasAdOverlay || isNaN(video.duration) || video.duration <= 90);

        // Vigilante de vida corta: existe SOLO mientras hay un anuncio en pantalla (0% CPU en video normal)
        if (isAdShowing && !adWatchdog) adWatchdog = setInterval(skipYouTubeAds, 200);
        else if (!isAdShowing && adWatchdog) { clearInterval(adWatchdog); adWatchdog = null; }

        const now = Date.now();
        if (!isAdShowing && (now - lastAdCheckTime < 150)) return;
        lastAdCheckTime = now;

        if (isAdShowing) {
            if (isHandlingAd) return;
            isHandlingAd = true;

            try {
                if (!video.muted) {
                    video.muted = true;
                    wasMutedByAdBlock = true;
                }

                if (video.playbackRate <= 2.0 && video.playbackRate > 0) {
                    previousUserPlaybackRate = video.playbackRate;
                }

                try { video.playbackRate = 16.0; } catch(_) {}

                if (video.paused) {
                    try { video.play().catch(() => {}); } catch(_) {}
                }

                // Salto al final para anuncios cortos (<=90s) y largos reales (<=180s con marcadores activos)
                if (!isNaN(video.duration) && video.duration > 0 &&
                    (video.duration <= 90 || (video.duration <= MAX_AD_SECONDS && hasAdOverlay))) {
                    const targetTime = Math.max(0, video.duration - 0.1);
                    if (video.currentTime < targetTime) {
                        try { video.currentTime = targetTime; } catch(_) {}
                    }
                }

                if (typeof moviePlayer.skipAd === 'function') {
                    try { moviePlayer.skipAd(); } catch(_) {}
                }

                // Clic preciso únicamente sobre el <button> real habilitado (nunca sobre el contenedor padre)
                const skipBtn = findSkipButton(moviePlayer);
                if (skipBtn) {
                    try { skipBtn.click(); } catch(_) {}
                    if (!wasSkipLogged && window.chrome && window.chrome.webview) {
                        wasSkipLogged = true;
                        window.chrome.webview.postMessage({
                            type: 'ad_blocked',
                            category: 'adapter',
                            reason: 'skip_clicked -> ' + skipBtn.tagName + '.' + (skipBtn.className || '')
                        });
                    }
                }

                if (!wasAdReported) {
                    wasAdReported = true;
                    if (window.chrome && window.chrome.webview) {
                        window.chrome.webview.postMessage({
                            type: 'ad_blocked',
                            category: 'adapter',
                            url: window.location.href,
                            reason: 'youtube_ad_skipped'
                        });
                    }
                }
            } finally {
                isHandlingAd = false;
            }
        } else {
            if (video.playbackRate > 2.0) {
                video.playbackRate = previousUserPlaybackRate || 1.0;
            }

            if (wasMutedByAdBlock) {
                video.muted = false;
                wasMutedByAdBlock = false;
            }

            wasSkipLogged = false;
            wasAdReported = false;
        }
    };

    // ─────────────────────────────────────────────────────────────────
    // 4. GUARDIÁN CONTRA PAUSA ARTIFICIAL Y OBSERVADORES DE NAVEGACIÓN SPA
    // ─────────────────────────────────────────────────────────────────
    let lastUserInteractionTime = 0;
    let lastYtNavigationTime = Date.now();

    ['click', 'keydown', 'pointerdown'].forEach(evt =>
        document.addEventListener(evt, () => { lastUserInteractionTime = Date.now(); }, true));

    document.addEventListener('pause', () => {
        const now = Date.now();
        if ((now - lastYtNavigationTime < 4500) && (now - lastUserInteractionTime > 800)) {
            const moviePlayer = document.querySelector('#movie_player') || document.querySelector('.html5-video-player');
            const video = document.querySelector('video');
            if (video && !moviePlayer?.classList?.contains('ad-showing')) {
                setTimeout(() => {
                    if (video.paused && (Date.now() - lastUserInteractionTime > 800)) {
                        try {
                            if (moviePlayer && typeof moviePlayer.playVideo === 'function') moviePlayer.playVideo();
                            else video.play().catch(() => {});
                        } catch(_) {}
                    }
                }, 120);
            }
        }
    }, true);

    ['play', 'loadedmetadata', 'durationchange'].forEach(evt =>
        document.addEventListener(evt, () => skipYouTubeAds(), true));

    const whenElement = (selector, cb, timeoutMs, onTimeout) => {
        const found = document.querySelector(selector);
        if (found) { cb(found); return; }
        if (!document.documentElement) { if (onTimeout) onTimeout(); return; }
        let tid = null;
        const obs = new MutationObserver(() => {
            const el = document.querySelector(selector);
            if (el) { obs.disconnect(); clearTimeout(tid); cb(el); }
        });
        tid = setTimeout(() => { obs.disconnect(); if (onTimeout) onTimeout(); }, timeoutMs);
        obs.observe(document.documentElement, { childList: true, subtree: true });
    };

    const ytWatch = {
        player: { el: null, obs: null, arming: false },
        popup:  { el: null, obs: null, arming: false }
    };

    const armWatch = (slot, selector, setup) => {
        const w = ytWatch[slot];
        if (w.el && w.el.isConnected) return;
        if (w.obs) { w.obs.disconnect(); w.obs = null; }
        w.el = null;
        if (w.arming) return;
        w.arming = true;
        whenElement(selector, (el) => { w.arming = false; w.el = el; w.obs = setup(el); }, 8000, () => { w.arming = false; });
    };

    const armYouTubeObservers = () => {
        armWatch('player', '#movie_player, .html5-video-player', (player) => {
            const obs = new MutationObserver(() => { skipYouTubeAds(); clearYouTubePopups(); });
            obs.observe(player, { attributes: true, attributeFilter: ['class'] });
            skipYouTubeAds();
            clearYouTubePopups();
            return obs;
        });

        armWatch('popup', 'ytd-popup-container', (container) => {
            let scheduled = false;
            const obs = new MutationObserver(() => {
                if (scheduled) return;
                scheduled = true;
                requestAnimationFrame(() => { scheduled = false; clearYouTubePopups(); });
            });
            obs.observe(container, { childList: true, subtree: true, attributes: true, attributeFilter: ['opened', 'aria-hidden'] });
            clearYouTubePopups();
            return obs;
        });
    };

    ['yt-navigate-finish', 'yt-page-data-updated'].forEach(evt =>
        document.addEventListener(evt, () => {
            lastYtNavigationTime = Date.now();
            lastUserInteractionTime = 0;
            skipYouTubeAds();
            clearYouTubePopups();
            armYouTubeObservers();
        }, true));

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', armYouTubeObservers, { once: true });
    } else {
        armYouTubeObservers();
    }

    // ─────────────────────────────────────────────────────────────────
    // 5. CSS COSMÉTICO Y CORTINA VISUAL NO DESTRUCTIVA (Preserva geometría del botón Skip)
    // ─────────────────────────────────────────────────────────────────
    try {
        const style = document.createElement('style');
        style.id = 'lex-adblock-styles';
        style.textContent = `
            /* Cortina visual por opacidad únicamente (evita ocultar visibilidad para no pausar temporizadores de YouTube) */
            .html5-video-player.ad-showing video,
            .html5-video-player.ad-interrupting video,
            #movie_player.ad-showing video,
            #movie_player.ad-interrupting video {
                opacity: 0 !important;
            }

            /* Mantener botón de saltar anuncio interactivo y con geometría válida */
            .ytp-skip-ad-button,
            .ytp-ad-skip-button,
            .ytp-ad-skip-button-modern {
                opacity: 1 !important;
                pointer-events: auto !important;
            }

            /* Ocultar textos, preview y banners publicitarios (sin colapsar .video-ads ni .ytp-ad-module) */
            .ytp-ad-preview-container,
            .ytp-ad-text,
            .ytp-ad-duration-remaining,
            .ytp-ad-hover-text-container,
            .ytp-ad-action-interstitial,
            .ytp-ad-image-overlay,
            .ytp-ad-overlay-container,
            .ytp-ad-message-container,
            .ytp-ad-survey-container,
            ytd-action-companion-ad-renderer,
            ytd-display-ad-renderer,
            ytd-video-masthead-ad-v3-renderer,
            ytd-primetime-promo-renderer,
            ytd-promoted-sparkles-web-renderer,
            ytd-statement-banner-renderer,
            ytd-in-feed-ad-layout-renderer,
            ytd-ad-slot-renderer,
            #player-ads,
            #masthead-ad,
            ytd-enforcement-message-view-model,
            tp-yt-paper-dialog:has(ytd-enforcement-message-view-model),
            ytd-popup-container:has(ytd-enforcement-message-view-model) tp-yt-iron-overlay-backdrop,
            .ytd-merch-shelf-renderer,
            ytd-banner-promo-renderer {
                display: none !important;
                visibility: hidden !important;
                opacity: 0 !important;
                height: 0 !important;
                pointer-events: none !important;
            }

            tp-yt-iron-overlay-backdrop {
                pointer-events: none !important;
            }

            /* YouTube Shorts: Garantizar visibilidad de barra de tiempo y controles */
            ytd-reel-player-progress-bar-view-model,
            .ytd-reel-player-progress-bar-view-model,
            ytd-shorts [class*=""progress-bar""],
            ytd-shorts #progress-bar,
            ytd-shorts .yt-progress-bar,
            ytd-shorts [class*=""time-display""],
            desktop-shorts-player-controls,
            .desktopShortsPlayerControlsHost,
            .desktopShortsPlayerControlsHost * {
                visibility: visible !important;
                opacity: 1 !important;
                z-index: 2147483640 !important;
            }

            #experiment-overlay {
                overflow: visible !important;
            }

            desktop-shorts-player-controls,
            .desktopShortsPlayerControlsHost {
                transform: none !important;
                bottom: 2px !important;
            }
        `;

        const attachYtStyle = () => {
            const t = document.head || document.documentElement;
            if (!t) return false;
            if (!document.getElementById('lex-adblock-styles')) t.appendChild(style);
            return true;
        };
        if (!attachYtStyle()) {
            const styleWait = new MutationObserver(() => {
                if (attachYtStyle()) styleWait.disconnect();
            });
            styleWait.observe(document, { childList: true });
        }
    } catch(_) {}
})();
";
}
