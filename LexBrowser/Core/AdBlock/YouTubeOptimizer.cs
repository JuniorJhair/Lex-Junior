namespace LexBrowser.Core.AdBlock;

public static class YouTubeOptimizer
{
    /// <summary>
    /// Script inyectado universal que:
    /// 1. Neutraliza publicidad, popunders y click-jacking en cualquier web (especialmente streaming como futbollibre).
    /// 2. Habilita el Picture-in-Picture nativo libre (Mini-Reproductor) en YouTube, transmisiones en vivo e iframes.
    /// 3. Remueve restricciones disablePictureInPicture y captura el evento de retorno para regresar a la pestaña.
    /// </summary>
    public const string InjectedScript = @"
(function() {
    if (window.__lex_universal_core_injected) return;
    window.__lex_universal_core_injected = true;

    const hostname = window.location.hostname || '';
    const isYouTube = hostname.includes('youtube.com');

    // ─────────────────────────────────────────────────────────────────
    // YOUTUBE PRE-HOOK: Limpiar adPlacements de ytInitialPlayerResponse
    // ─────────────────────────────────────────────────────────────────
    if (isYouTube) {
        try {
            const cleanPlayerResponse = function(data) {
                if (!data || typeof data !== 'object') return data;
                try {
                    if (data.adPlacements) data.adPlacements = [];
                    if (data.playerAds) data.playerAds = [];
                    if (data.adSlots) data.adSlots = [];
                    if (data.playbackTracking && data.playbackTracking.videostatsPlaybackUrl) {
                        data.playbackTracking.videostatsPlaybackUrl.baseUrl = '';
                    }
                } catch(_) {}
                return data;
            };

            let _ytInitialPlayerResponse = window.ytInitialPlayerResponse;
            Object.defineProperty(window, 'ytInitialPlayerResponse', {
                get: function() { return _ytInitialPlayerResponse; },
                set: function(val) { _ytInitialPlayerResponse = cleanPlayerResponse(val); },
                configurable: true
            });
        } catch(_) {}

        // Navegación SPA: tras el primer video, YouTube ya no recarga la página; pide el player por fetch o XHR.
        // Sin esto el pre-hook de arriba solo protege al primer video.
        try {
            const cleanPlayerPayload = function(data) {
                if (!data || typeof data !== 'object') return data;
                try {
                    if (Array.isArray(data.adPlacements)) data.adPlacements = [];
                    if (Array.isArray(data.playerAds)) data.playerAds = [];
                    if (Array.isArray(data.adSlots)) data.adSlots = [];
                    if (data.playerResponse) cleanPlayerPayload(data.playerResponse);
                    if (data.contents) cleanPlayerPayload(data.contents);
                    if (data.playbackTracking && data.playbackTracking.videostatsPlaybackUrl) {
                        data.playbackTracking.videostatsPlaybackUrl.baseUrl = '';
                    }
                } catch(_) {}
                return data;
            };
            const isPlayerEndpoint = (u) => typeof u === 'string' && (
                u.indexOf('/youtubei/v1/player') !== -1 ||
                u.indexOf('/youtubei/v1/reel/reel_item_watch') !== -1 ||
                u.indexOf('/youtubei/v1/next') !== -1
            );

            // 1. Interceptar fetch
            const origFetch = window.fetch;
            window.fetch = function(input, init) {
                const url = (typeof input === 'string') ? input : (input && input.url);
                const p = origFetch.apply(this, arguments);
                if (!isPlayerEndpoint(url)) return p;
                return p.then(function(res) {
                    return res.clone().json().then(function(json) {
                        const body = JSON.stringify(cleanPlayerPayload(json));
                        return new Response(body, { status: res.status, statusText: res.statusText, headers: res.headers });
                    }).catch(function() { return res; });
                });
            };

            // 2. Interceptar XMLHttpRequest (utilizado por el reproductor base de YouTube)
            const origXhrOpen = XMLHttpRequest.prototype.open;
            XMLHttpRequest.prototype.open = function(method, url) {
                this.__lex_url = url;
                return origXhrOpen.apply(this, arguments);
            };

            const origResponseTextDesc = Object.getOwnPropertyDescriptor(XMLHttpRequest.prototype, 'responseText');
            if (origResponseTextDesc && origResponseTextDesc.get) {
                Object.defineProperty(XMLHttpRequest.prototype, 'responseText', {
                    get: function() {
                        const raw = origResponseTextDesc.get.call(this);
                        if (isPlayerEndpoint(this.__lex_url) && typeof raw === 'string' && raw.includes('adPlacements')) {
                            try {
                                return JSON.stringify(cleanPlayerPayload(JSON.parse(raw)));
                            } catch(_) {}
                        }
                        return raw;
                    },
                    configurable: true
                });
            }

            // 3. Interceptar JSON.parse para atrapar respuestas de anuncios parseadas directamente
            const origParse = JSON.parse;
            JSON.parse = function() {
                const out = origParse.apply(this, arguments);
                if (out && typeof out === 'object') {
                    if (out.adPlacements || out.playerAds || out.adSlots || out.playerResponse) {
                        return cleanPlayerPayload(out);
                    }
                }
                return out;
            };
        } catch(_) {}
    }

    // ─────────────────────────────────────────────────────────────────
    // 0. SCRIPTLETS DE EMULACIÓN ANTI-ANTI-ADBLOCK & NOTIFICATION DEFUSER
    // ─────────────────────────────────────────────────────────────────
    try {
        window.canRunAds = true;
        window.isAdBlockActive = false;
        window.adblock = false;
        window.adsbygoogle = window.adsbygoogle || [];
        window.adsbygoogle.push = function() { return 0; };
        window.google_ad_client = 'ca-pub-test';

        // Emular bibliotecas de popunder para que se desactiven pacíficamente sin romper el reproductor
        window.aclib = {
            runPop: function() { return false; },
            runBanner: function() { return false; },
            runSlider: function() { return false; },
            runInterstitial: function() { return false; }
        };
        window.popns = {};
        window._pop = [];
        window._monetag = {};
        window.adcash = {};

        // Emular y neutralizar ZorDon / Zordon (FuckAdBlock / BlockAdBlock usado por Byse / JWPlayer)
        try {
            Object.defineProperty(window, 'ZorDon', {
                get: function() { return false; },
                set: function() {},
                configurable: true
            });
            window.Zordon = {
                on: function(status, cb) {
                    if (status === false && typeof cb === 'function') {
                        try { cb(); } catch(_) {}
                    }
                    return this;
                },
                check: function() { return false; },
                clearEvent: function() { return this; }
            };
            const FakeFab = function() {
                this.on = function(detected, fn) {
                    if (!detected && typeof fn === 'function') {
                        try { setTimeout(fn, 10); } catch(_) {}
                    }
                    return this;
                };
                this.check = function() { return false; };
                this.clearEvent = function() { return this; };
                this.setOption = function() { return this; };
            };
            window.BlockAdBlock = FakeFab;
            window.FuckAdBlock = FakeFab;
            window.blockAdBlock = new FakeFab();
            window.fuckAdBlock = new FakeFab();
            window.sniffAdBlock = false;
        } catch(_) {}

        // Neutralizar trampa de autodetección por PDF object en Byse / reproductores iframe
        try {
            if (navigator && navigator.plugins && typeof navigator.plugins.namedItem === 'function') {
                const origNamedItem = navigator.plugins.namedItem.bind(navigator.plugins);
                navigator.plugins.namedItem = function(name) {
                    if (name === 'Chrome PDF Viewer') return null;
                    return origNamedItem(name);
                };
            }
        } catch(_) {}

        try {
            const origSetAttr = HTMLObjectElement.prototype.setAttribute;
            HTMLObjectElement.prototype.setAttribute = function(name, val) {
                if (name === 'data' && typeof val === 'string' && val.startsWith('data:application/pdf')) {
                    const self = this;
                    setTimeout(function() {
                        if (typeof self.onload === 'function') {
                            try { self.onload(); } catch(_) {}
                        }
                    }, 10);
                    return;
                }
                return origSetAttr.apply(this, arguments);
            };
        } catch(_) {}
    } catch(e) {}

    // Defusor de secuestro de notificaciones invasivas (Push Notification Spam)
    try {
        if (typeof window.Notification !== 'undefined') {
            window.Notification.requestPermission = function() {
                return Promise.resolve('denied');
            };
        }
    } catch(e) {}

    // ─────────────────────────────────────────────────────────────────
    // 1. ESCUDO ANTI-CLICKJACKING Y ANTI-POPUNDERS (Streaming / Web)
    // ─────────────────────────────────────────────────────────────────
    const adDomainsRegex = /(propellerads|popcash|popads|adsterra|monetag|bet365|1xbet|clickadu|hilltopads|exoclick|juicyads|syndication|doubleclick|adservice|outbrain|taboola|criteo|adnxs|pubmatic|onclick|trafficstars|adcash|revcontent|smartadserver|r2ads|llvpn|streamtp|trkr|ppof\.net|visariomedia|qojseyonoj|hfryyynvqwgy)/i;

    // 1.1 Defusor de window.open para popunders no solicitados (excluyendo dominios OAuth de confianza)
    const trustedOAuthRegex = /^https:\/\/([a-z0-9-]+\.)?(google\.com|apple\.com|microsoft\.com|microsoftonline\.com|live\.com|github\.com|facebook\.com|discord\.com|x\.com|twitter\.com|paypal\.com)(\/|$)/i;
    try {
        const originalOpen = window.open;
        window.open = function(url, target, features) {
            if (url && typeof url === 'string' && !trustedOAuthRegex.test(url)) {
                const isAd = adDomainsRegex.test(url) ||
                             url.includes('/ad/') ||
                             url.includes('/ads/') ||
                             url.includes('popunder') ||
                             url.includes('direct-link') ||
                             url.includes('link?z=') ||
                             url.includes('?z=') ||
                             url.includes('&z=') ||
                             url.includes('zoneid') ||
                             url.includes('zoneId') ||
                             url.includes('trkr.') ||
                             (url.includes('click') && url.includes('track'));

                if (isAd) {
                    if (window.chrome && window.chrome.webview) {
                        window.chrome.webview.postMessage({ type: 'ad_blocked', url: url, reason: 'window_open' });
                    }
                    return null;
                }
            }
            return originalOpen.apply(this, arguments);
        };
    } catch(e) {}

    // 1.2 Defusor de clics simulados en enlaces fantasma (creados en memoria sin contenido visual)
    try {
        const originalAnchorClick = HTMLAnchorElement.prototype.click;
        HTMLAnchorElement.prototype.click = function() {
            const href = this.href || '';
            const isDetached = !document.contains(this);
            const text = (this.innerText || this.textContent || '').trim();
            if ((isDetached || !text) && (this.target === '_blank' || adDomainsRegex.test(href))) {
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage({ type: 'ad_blocked', url: href, reason: 'phantom_anchor' });
                }
                return; // Neutralizado
            }
            return originalAnchorClick.apply(this, arguments);
        };
    } catch(e) {}

    // 1.3 Verificador Heurístico de Capas Fantasma / Overlays Trampa
    const isGhostOverlay = (el) => {
        if (!el || el === document.body || el === document.documentElement) return false;
        const tag = el.tagName || '';
        if (['VIDEO', 'AUDIO', 'INPUT', 'TEXTAREA', 'SELECT', 'BUTTON', 'IFRAME', 'CANVAS', 'FORM', 'DIALOG'].includes(tag)) return false;

        // Whitelist absoluta: NUNCA tocar elementos de la estructura de la app, reproductores o containers
        if (el.id === 'root' || el.id === 'app' || el.hasAttribute('data-reactroot')) return false;
        if (el.closest('video, audio, iframe, canvas, form, dialog, #root, #app, [id*=player], [class*=player], [class*=jw8], [class*=jw-], [class*=plyr], [class*=vjs-], [class*=stage], [class*=video-], [id*=video], [class*=aniyt], [class*=mytsumi], [class*=abismo]')) return false;

        // CRÍTICO: Si el elemento CONTIENE un video, iframe o spinner dentro, ES PARTE DEL REPRODUCTOR, NUNCA borrarlo!
        if (el.querySelector('video, iframe, canvas, [class*=jw8], [class*=jw-], [class*=plyr], [class*=spinner]')) return false;

        // Si tiene id o clase explícita de anuncio trampa (ej. first-click-ad en futbollibrelive/tvf90, pop-overlay, click-ad)
        const idClass = ((el.id || '') + ' ' + (el.className || '')).toLowerCase();
        const isExplicitAdName = /(first-click-ad|click-ad|ad-overlay|pop-overlay|popup-overlay|interads)/.test(idClass);

        const style = window.getComputedStyle(el);
        const isPositioned = (style.position === 'fixed' || style.position === 'absolute');
        if (!isPositioned && !isExplicitAdName) return false;

        // Si contiene controles interactivos legítimos
        if (el.querySelector('input, button, select, textarea, a:not([target=_blank])')) return false;

        const opacity = parseFloat(style.opacity);
        const bg = style.backgroundColor || '';
        const isZeroAlpha = (!bg || bg === 'transparent' || (bg.includes('rgba(') && parseFloat(bg.split(',')[3] || '1') <= 0.08));
        const isTransparent = (opacity <= 0.08 || isZeroAlpha || style.visibility === 'hidden');

        const text = (el.innerText || el.textContent || '').trim();
        // Si tiene texto legítimo y visible
        if (text.length > 0 && style.visibility !== 'hidden' && opacity > 0.3) return false;

        if (!isTransparent && !isExplicitAdName) return false;

        const isGhostAnchor = (tag === 'A' || el.closest('a')) && isTransparent && !text;

        // Solo considerar overlay trampa si es un nombre explícito de anuncio, o un anchor trampa sin contenido
        return (isExplicitAdName || isGhostAnchor);
    };

    // 1.4 Interceptor en Fase de Captura Pre-Clic (Destrucción inmediata + Click Pass-Through)
    const handleCaptureClick = (e) => {
        if (isYouTube) return; // YouTube no usa clickjacking ni popunders; no interferir con clics de Polymer
        let target = e.target;
        if (!target) return;

        // Si el usuario hace clic dentro de un reproductor o control legítimo, permitir inmediatamente
        if (target.closest('video, audio, iframe, button, input, select, textarea, [class*=jw], [class*=plyr], [class*=player], [class*=aniyt], [class*=mytsumi]')) {
            return;
        }

        let ghost = null;
        let curr = target;
        for (let i = 0; i < 4 && curr; i++) {
            if (isGhostOverlay(curr)) {
                ghost = curr;
                break;
            }
            curr = curr.parentElement;
        }

        if (ghost) {
            e.preventDefault();
            e.stopPropagation();
            e.stopImmediatePropagation();

            const x = e.clientX;
            const y = e.clientY;

            ghost.remove();

            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage({ type: 'ad_blocked', reason: 'ghost_overlay_capture' });
            }

            // Click Pass-Through: Pasar el clic al elemento legítimo que estaba oculto debajo
            try {
                const underlying = document.elementFromPoint(x, y);
                if (underlying && underlying !== ghost) {
                    if (underlying.tagName === 'VIDEO') {
                        if (underlying.paused) underlying.play().catch(()=>{});
                    } else if (typeof underlying.click === 'function') {
                        underlying.click();
                    }
                }
            } catch(err) {}
        }
    };

    if (!isYouTube) {
        window.addEventListener('click', handleCaptureClick, true);
        window.addEventListener('pointerdown', handleCaptureClick, true);
    }

    // 1.5 Limpieza proactiva de capas trampas en el DOM (restringido estrictamente a ads explícitos y anchors fantasma)
    const clearClickjackOverlays = () => {
        try {
            const explicitAds = document.querySelectorAll('[id*=first-click-ad], [class*=first-click-ad], [id*=click-ad], [class*=click-ad], [class*=ad-overlay], [class*=pop-overlay], [class*=popup-overlay], [class*=interads]');
            for (let i = 0; i < explicitAds.length; i++) {
                const el = explicitAds[i];
                if (isGhostOverlay(el)) {
                    el.remove();
                    if (window.chrome && window.chrome.webview) {
                        window.chrome.webview.postMessage({ type: 'ad_blocked', reason: 'ghost_overlay_explicit' });
                    }
                }
            }

            const ghostAnchors = document.querySelectorAll('a[target=_blank]');
            for (let i = 0; i < ghostAnchors.length; i++) {
                const a = ghostAnchors[i];
                if (isGhostOverlay(a)) {
                    a.remove();
                    if (window.chrome && window.chrome.webview) {
                        window.chrome.webview.postMessage({ type: 'ad_blocked', reason: 'ghost_anchor_scanned' });
                    }
                }
            }
        } catch(e) {}
    };

    // ─────────────────────────────────────────────────────────────────
    // 2. YOUTUBE: Anti-anuncios, cortina visual fantasma y salto instantáneo
    // ─────────────────────────────────────────────────────────────────
    const closePolymerDialog = (dialog) => {
        if (!dialog) return;
        try { if (typeof dialog.cancel === 'function') dialog.cancel(); } catch(_) {}
        try { if (typeof dialog.close === 'function') dialog.close(); } catch(_) {}
        try {
            dialog.removeAttribute('opened');
            dialog.setAttribute('aria-hidden', 'true');
            dialog.style.display = 'none';
            dialog.style.pointerEvents = 'none';
        } catch(_) {}
    };

    const clearYouTubePopups = () => {
        if (!isYouTube) return;
        const enforcement = document.querySelector('ytd-enforcement-message-view-model');
        if (enforcement) {
            const dialog = enforcement.closest('tp-yt-paper-dialog, ytd-modal-with-backdrop-renderer');
            if (dialog) closePolymerDialog(dialog);
            enforcement.remove();
            document.querySelectorAll('tp-yt-iron-overlay-backdrop').forEach(b => {
                b.classList.remove('opened');
                b.removeAttribute('opened');
                b.style.pointerEvents = 'none';
                b.style.display = 'none';
            });
            const player = document.querySelector('#movie_player') || document.querySelector('.html5-video-player');
            if (player && typeof player.playVideo === 'function') player.playVideo();
        }
        const feedback = document.querySelector('tp-yt-paper-dialog #feedback');
        if (feedback) {
            const dialog = feedback.closest('tp-yt-paper-dialog');
            if (dialog) {
                closePolymerDialog(dialog);
                feedback.remove();
            }
        }
        // Cerrar cualquier tp-yt-paper-dialog que haya quedado abierto pero vacío (huérfano tras limpiar su contenido)
        const openDialogs = document.querySelectorAll('tp-yt-paper-dialog:not([aria-hidden=""true""]), tp-yt-paper-dialog[opened]');
        for (let i = 0; i < openDialogs.length; i++) {
            const d = openDialogs[i];
            if (d.children.length === 0 || d.querySelector('ytd-enforcement-message-view-model')) {
                closePolymerDialog(d);
            }
        }
        // Neutralizar backdrops huérfanos cuando no exista un diálogo legítimo visible en pantalla
        const openBackdrops = document.querySelectorAll('tp-yt-iron-overlay-backdrop.opened, tp-yt-iron-overlay-backdrop[opened]');
        for (let i = 0; i < openBackdrops.length; i++) {
            const b = openBackdrops[i];
            const validDialog = document.querySelector('tp-yt-paper-dialog[opened]:not([aria-hidden=""true""]), ytd-modal-with-backdrop-renderer[opened]');
            const isLegitOpen = validDialog && validDialog.children.length > 0 && validDialog.offsetWidth > 0 && !validDialog.querySelector('ytd-enforcement-message-view-model');
            if (!isLegitOpen) {
                b.classList.remove('opened');
                b.removeAttribute('opened');
                b.style.display = 'none';
                b.style.pointerEvents = 'none';
            }
        }
    };

    let wasMutedByAdBlock = false;
    let previousUserPlaybackRate = 1.0;
    let isHandlingAd = false;
    let wasAdReported = false;
    let lastAdCheckTime = 0;
    let adWatchdog = null;

    const skipYouTubeAds = () => {
        if (!isYouTube) return;

        const moviePlayer = document.querySelector('#movie_player') || document.querySelector('.html5-video-player');
        const video = document.querySelector('video');
        if (!video || !moviePlayer) {
            if (adWatchdog) { clearInterval(adWatchdog); adWatchdog = null; }
            return;
        }

        // Detección oficial estricta: YouTube únicamente asigna 'ad-showing' o 'ad-interrupting' cuando un anuncio está activo
        const isAdShowing = moviePlayer.classList.contains('ad-showing') ||
                            moviePlayer.classList.contains('ad-interrupting');

        // Vigilante de vida corta: existe SOLO mientras hay un anuncio en pantalla
        if (isAdShowing && !adWatchdog) adWatchdog = setInterval(skipYouTubeAds, 250);
        else if (!isAdShowing && adWatchdog) { clearInterval(adWatchdog); adWatchdog = null; }

        const now = Date.now();
        // Solo limitar tasa si NO hay anuncio activo (para no saturar en reproducción normal)
        if (!isAdShowing && (now - lastAdCheckTime < 150)) return;
        lastAdCheckTime = now;

        if (isAdShowing) {
            if (isHandlingAd) return;
            isHandlingAd = true;

            try {
                // 1. Silencio inmediato en tiempo cero
                if (!video.muted) {
                    video.muted = true;
                    wasMutedByAdBlock = true;
                }

                // Guardar la velocidad del usuario antes del anuncio
                if (video.playbackRate <= 2.0 && video.playbackRate > 0) {
                    previousUserPlaybackRate = video.playbackRate;
                }

                // 2. Aceleración máxima (16x)
                video.playbackRate = 16.0;

                // 3. Forzar reproducción si YouTube intentó pausar
                if (video.paused) {
                    try { video.play().catch(() => {}); } catch(_) {}
                }

                // 4. Salto hacia el final del segmento de anuncio para disparar el evento ended
                if (!isNaN(video.duration) && video.duration > 0 && isFinite(video.duration)) {
                    const targetTime = Math.max(0, video.duration - 0.1);
                    if (video.currentTime < targetTime) {
                        try { video.currentTime = targetTime; } catch(_) {}
                    }
                }

                // 5. Llamada directa al método nativo de la API de YouTube Player
                if (typeof moviePlayer.skipAd === 'function') {
                    try { moviePlayer.skipAd(); } catch(_) {}
                }

                // 6. Clic en botón de skip si está presente
                const skipBtn = document.querySelector('.ytp-ad-skip-button, .ytp-ad-skip-button-modern, .ytp-skip-ad-button, .ytp-ad-skip-button-slot button, button[class*=""skip""], [class*=""skip-button""]');
                if (skipBtn) {
                    try {
                        const opts = { bubbles: true, cancelable: true, view: window };
                        skipBtn.dispatchEvent(new MouseEvent('click', opts));
                        skipBtn.click();
                    } catch(_) {}
                }

                // 7. Notificar a C# EXACTAMENTE UNA VEZ por anuncio (cero saturación de hilos)
                if (!wasAdReported) {
                    wasAdReported = true;
                    if (window.chrome && window.chrome.webview) {
                        window.chrome.webview.postMessage({
                            type: 'ad_blocked',
                            url: window.location.href
                        });
                    }
                }
            } finally {
                isHandlingAd = false;
            }
        } else {
            // El anuncio ha terminado o es reproducción normal: restaurar velocidad y audio del usuario
            if (video.playbackRate > 2.0) {
                video.playbackRate = previousUserPlaybackRate || 1.0;
            }

            if (wasMutedByAdBlock) {
                video.muted = false;
                wasMutedByAdBlock = false;
            }

            wasAdReported = false;
        }
    };

    if (isYouTube) {
        ['play', 'loadedmetadata', 'durationchange'].forEach(evt =>
            document.addEventListener(evt, () => skipYouTubeAds(), true));
        ['yt-navigate-finish', 'yt-page-data-updated'].forEach(evt =>
            document.addEventListener(evt, () => {
                skipYouTubeAds();
                clearYouTubePopups();
                armYouTubeObservers();
            }, true));
    }

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
        if (!isYouTube) return;

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

    // ─────────────────────────────────────────────────────────────────
    // 3. MINI-REPRODUCTOR NATIVO CON RETORNO A PESTAÑA
    // ─────────────────────────────────────────────────────────────────
    const unlockVideoPiP = (video) => {
        if (!video) return;
        if (video.hasAttribute('disablepictureinpicture')) {
            video.removeAttribute('disablepictureinpicture');
        }
        video.disablePictureInPicture = false;

        if (!video.__lex_pip_listener_attached) {
            video.__lex_pip_listener_attached = true;
            video.addEventListener('enterpictureinpicture', () => {
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage({
                        type: 'pip_entered',
                        videoWidth: video.videoWidth || 16,
                        videoHeight: video.videoHeight || 9,
                        url: window.location.href
                    });
                }
            });
            video.addEventListener('leavepictureinpicture', () => {
                const programmatic = !!window.__lexProgrammaticPiPExit;
                window.__lexProgrammaticPiPExit = false;
                if (programmatic) return;
                if (!(window.chrome && window.chrome.webview)) return;

                window.chrome.webview.postMessage({
                    type: 'pip_leaving_now',
                    url: window.location.href
                });

                setTimeout(() => {
                    const isPaused = video.paused;
                    window.chrome.webview.postMessage({
                        type: 'pip_left',
                        currentTime: video.currentTime,
                        isPaused: isPaused,
                        url: window.location.href,
                        isFullScreen: !!window.__lexWasFullscreen
                    });
                }, 120);
            });
        }
    };

    // Desbloquear videos existentes y bajo demanda al reproducir o cargar metadatos
    ['play', 'playing', 'loadedmetadata', 'canplay'].forEach(evt => {
        document.addEventListener(evt, (e) => {
            if (e.target && (e.target.tagName === 'VIDEO' || e.target.tagName === 'AUDIO')) {
                if (e.target.tagName === 'VIDEO') unlockVideoPiP(e.target);
                if (typeof window.__lexVolumePercent === 'number' && window.__lexVolumePercent !== 100) {
                    applyVolumeBoostToElement(e.target, window.__lexVolumePercent);
                }
            }
        }, true);
    });
    document.querySelectorAll('video').forEach(unlockVideoPiP);

    // ─────────────────────────────────────────────────────────────────
    // 3.1 AMPLIFICADOR DE VOLUMEN UNIVERSAL HASTA 300% (Hi-Fi Dynamics Compressor + GainNode)
    // Cero consumo en reposo: AudioContext y Compresor se inicializan bajo demanda solo al superar 100%.
    // ─────────────────────────────────────────────────────────────────
    window.__lexVolumePercent = 100;

    const ensureAudioContextRunning = () => {
        try { const c = window.__lexAudioCtx; if (c && c.state === 'suspended') c.resume().catch(() => {}); } catch(_) {}
    };
    ['pointerdown', 'click', 'keydown', 'play'].forEach(evt => {
        window.addEventListener(evt, ensureAudioContextRunning, { capture: true, passive: true });
    });

    const lexClamp = (v) => { const n = Number(v); return Number.isFinite(n) ? Math.max(0, Math.min(300, n)) : 100; };

    // ── Mensajería hacia C# (los iframes pasan por el frame superior) ──
    const toHost = (type, payload) => {
        try {
            if (window === window.top) {
                if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(Object.assign({}, payload, { type: type }));
            } else {
                window.top.postMessage({ __lex_cmd: 'to_host', type: type, payload: payload }, '*');
            }
        } catch(_) {}
    };
    const lexReported = {};
    const reportOnce = (status, src) => {
        if (lexReported[status]) return;
        lexReported[status] = true;
        toHost('audio_status', { status: status, src: String(src || '').slice(0, 200) });
    };

    // ── Rastreo de medios sin retener memoria (WeakRef) y listeners de eventos ──
    const lexSeen = new WeakSet();
    const lexRefs = [];
    const lexShadowRoots = [];
    const lexListened = new WeakSet();
    const listenMedia = (el) => {
        if (lexListened.has(el)) return;
        lexListened.add(el);
        ['play', 'pause', 'ended', 'loadedmetadata', 'durationchange', 'emptied'].forEach(evt =>
            el.addEventListener(evt, () => { if (window.__lexScheduleSync) window.__lexScheduleSync(); }));
    };
    const trackMedia = (el) => {
        if (!el || lexSeen.has(el)) return;
        lexSeen.add(el);
        lexRefs.push(new WeakRef(el));
        listenMedia(el);
    };

    try {
        const origPlay = HTMLMediaElement.prototype.play;
        HTMLMediaElement.prototype.play = function() {
            trackMedia(this);
            if (window.__lexVolumePercent !== 100) { const self = this; queueMicrotask(() => applyToElement(self, window.__lexVolumePercent)); }
            return origPlay.apply(this, arguments);
        };
    } catch(_) {}

    try {
        const origAttach = Element.prototype.attachShadow;
        Element.prototype.attachShadow = function(init) {
            const root = origAttach.call(this, init);   // captura también roots 'closed'
            try { lexShadowRoots.push(new WeakRef(root)); } catch(_) {}
            return root;
        };
    } catch(_) {}

    const collectAllMedia = () => {
        const found = new Set();
        const scan = (root) => { try { root.querySelectorAll('video, audio').forEach(e => found.add(e)); } catch(_) {} };
        scan(document);
        for (let i = lexShadowRoots.length - 1; i >= 0; i--) {
            const r = lexShadowRoots[i].deref();
            if (!r) { lexShadowRoots.splice(i, 1); continue; }
            scan(r);
        }
        for (let i = lexRefs.length - 1; i >= 0; i--) {
            const e = lexRefs[i].deref();
            if (!e) { lexRefs.splice(i, 1); continue; }
            found.add(e);
        }
        return found;
    };

    // ── Clasificación previa: NUNCA enrutar si no estamos seguros (es irreversible) ──
    const classify = (el) => {
        if (el.mediaKeys) return 'drm';                       // EME (Netflix...) => Web Audio daria silencio
        if (el.srcObject) return 'safe';                      // MediaStream
        const src = el.currentSrc || '';
        if (!src) return 'pending';                           // aun no sabemos: NO decidir
        if (/^(blob|data):/i.test(src)) return 'safe';        // MSE/HLS
        try { if (new URL(src, location.href).origin === location.origin) return 'safe'; } catch(_) { return 'pending'; }
        if (el.crossOrigin === 'anonymous' || el.crossOrigin === 'use-credentials') return 'safe';
        return 'cross';
    };

    // Fuentes que ya fallaron: NO se reintenta hasta que el elemento cambie de src.
    // (Antes, el evento 'emptied' borraba este estado y producia el bucle de recargas de 1 segundo.)
    const lexFailedSrc = new WeakMap();
    const markFailed = (el, src, why) => {
        try { lexFailedSrc.set(el, src || ''); } catch(_) {}
        reportOnce(why || 'cors_blocked', src);
    };

    const armPending = (el) => {
        if (el.__lexPendingArmed) return;
        el.__lexPendingArmed = true;
        const retry = () => { el.__lexPendingArmed = false; if (window.__lexVolumePercent > 100) applyToElement(el, window.__lexVolumePercent); };
        el.addEventListener('loadedmetadata', retry, { once: true });
        el.addEventListener('playing', retry, { once: true });
    };

    // ── Sonda con un <video> DESECHABLE: prueba si la fuente carga con crossOrigin sin tocar el reproductor real ──
    const probeCorsWithThrowaway = (src, timeoutMs) => new Promise((resolve) => {
        let v = null, done = false, timer = null;
        const finish = (ok) => {
            if (done) return; done = true;
            clearTimeout(timer);
            try { v.onloadedmetadata = null; v.onerror = null; v.removeAttribute('src'); v.load(); } catch(_) {}
            v = null;
            resolve(ok);
        };
        try {
            v = document.createElement('video');
            v.muted = true; v.preload = 'metadata'; v.crossOrigin = 'anonymous';
            v.onloadedmetadata = () => finish(true);
            v.onerror = () => finish(false);
            timer = setTimeout(() => finish(false), timeoutMs);
            v.src = src;
        } catch(_) { finish(false); }
    });

    // ── Recarga el elemento REAL con crossOrigin (solo se llama si la sonda ya paso) ──
    const reloadWithCors = (el) => new Promise((resolve) => {
        const t = el.currentTime, wasPlaying = !el.paused, rate = el.playbackRate;
        let done = false;
        const finish = (ok) => {
            if (done) return; done = true;
            clearTimeout(timer);
            el.removeEventListener('loadedmetadata', onOk);
            el.removeEventListener('error', onErr);
            if (!ok) { el.removeAttribute('crossorigin'); try { el.load(); } catch(_) {} }   // revertir
            const restore = () => { try { if (t > 0) el.currentTime = t; el.playbackRate = rate; if (wasPlaying) el.play().catch(() => {}); } catch(_) {} };
            if (el.readyState >= 1) restore(); else el.addEventListener('loadedmetadata', restore, { once: true });
            resolve(ok);
        };
        const onOk = () => finish(true);
        const onErr = () => finish(false);
        const timer = setTimeout(() => finish(false), 8000);
        el.addEventListener('loadedmetadata', onOk, { once: true });
        el.addEventListener('error', onErr, { once: true });
        try { el.crossOrigin = 'anonymous'; el.load(); } catch(_) { finish(false); }
    });

    // ── Pedir al host (C#) que inyecte Access-Control-Allow-Origin en respuestas de MEDIA ──
    const pendingUnlock = new Map();   // origin -> [resolvers]
    window.__lexCorsUnlockAck = (origin) => {
        const arr = pendingUnlock.get(origin);
        if (arr) { pendingUnlock.delete(origin); arr.forEach(r => r(true)); }
    };
    const requestHostUnlock = (src) => new Promise((resolve) => {
        let origin;
        try { origin = new URL(src, location.href).origin; } catch(_) { return resolve(false); }
        const arr = pendingUnlock.get(origin) || [];
        arr.push(resolve);
        pendingUnlock.set(origin, arr);
        setTimeout(() => {
            const a = pendingUnlock.get(origin);
            if (!a) return;
            const i = a.indexOf(resolve);
            if (i >= 0) { a.splice(i, 1); resolve(false); }
            if (a.length === 0) pendingUnlock.delete(origin);
        }, 4000);
        toHost('cors_unlock_req', { origin: origin });
    });

    const unlockCross = async (el) => {
        const src = el.currentSrc;
        el.__lexBusy = true;
        try {
            // 1) ¿El servidor ya manda CORS? 2) si no, C# inyecta ACAO y se vuelve a probar. El reproductor real NO se toca antes.
            let ok = await probeCorsWithThrowaway(src, 6000);
            if (!ok && await requestHostUnlock(src)) ok = await probeCorsWithThrowaway(src, 8000);
            if (ok && el.currentSrc === src && await reloadWithCors(el) && routeElement(el)) return true;
            markFailed(el, src, 'cors_blocked');                  // aviso honesto, sin reintentos
            return false;
        } catch(_) { markFailed(el, src, 'cors_blocked'); return false; }
        finally { el.__lexBusy = false; }
    };

    // ── Cadena de audio: el compresor SOLO esta activo por encima de 100% ──
    const routeElement = (el) => {
        try {
            const AudioCtx = window.AudioContext || window.webkitAudioContext;
            if (!AudioCtx) return false;
            if (!window.__lexAudioCtx) window.__lexAudioCtx = new AudioCtx({ latencyHint: 'playback' });
            const ctx = window.__lexAudioCtx;
            ensureAudioContextRunning();
            const src = ctx.createMediaElementSource(el);
            const gain = ctx.createGain();
            const comp = ctx.createDynamicsCompressor();
            comp.threshold.value = -6; comp.knee.value = 12; comp.ratio.value = 4;
            comp.attack.value = 0.003; comp.release.value = 0.25;
            src.connect(gain);
            gain.connect(ctx.destination);                        // 100%: ruta transparente, sin compresor
            el.__lexGainNode = gain; el.__lexComp = comp; el.__lexUseComp = false; el.__lexState = 'routed';
            return true;
        } catch(_) { markFailed(el, el.currentSrc, 'route_failed'); return false; }
    };
    const setChain = (el, useComp) => {
        if (el.__lexState !== 'routed' || el.__lexUseComp === useComp) return;
        const g = el.__lexGainNode, c = el.__lexComp, ctx = window.__lexAudioCtx;
        try {
            g.disconnect(); c.disconnect();
            if (useComp) { g.connect(c); c.connect(ctx.destination); } else { g.connect(ctx.destination); }
            el.__lexUseComp = useComp;
        } catch(_) {}
    };
    const setGain = (el, v) => {
        try { const g = el.__lexGainNode, c = window.__lexAudioCtx; if (g && c) g.gain.setTargetAtTime(v, c.currentTime, 0.015); } catch(_) {}
    };

    const ensureRouted = async (el) => {
        if (el.__lexState === 'routed') return true;
        if (el.__lexBusy || el.__lexState === 'drm') return false;
        const s = el.currentSrc || '';
        if (s && lexFailedSrc.get(el) === s) return false;        // ya fallo con ESTA fuente: no insistir
        const c = classify(el);
        if (c === 'drm') { el.__lexState = 'drm'; reportOnce('drm'); return false; }
        if (c === 'pending') { armPending(el); return false; }
        if (c === 'safe') return routeElement(el);
        return await unlockCross(el);
    };

    const applyToElement = async (el, percent) => {
        if (!el || (el.tagName !== 'VIDEO' && el.tagName !== 'AUDIO')) return;
        const p = lexClamp(percent);
        trackMedia(el);
        try {
            if (p === 0) { el.muted = true; window.__lexMutedByZeroVol = true; return; }
            if (el.muted && window.__lexMutedByZeroVol) { el.muted = false; window.__lexMutedByZeroVol = false; }
            if (p <= 100) {
                el.volume = p / 100.0;
                if (el.__lexState === 'routed') { setChain(el, false); setGain(el, 1.0); }
                return;
            }
            el.volume = 1.0;
            const ok = await ensureRouted(el);
            const cur = window.__lexVolumePercent;                // pudo cambiar durante el await
            if (ok && cur > 100) { ensureAudioContextRunning(); setChain(el, true); setGain(el, cur / 100.0); }
        } catch(_) {}
    };
    const applyVolumeBoostToElement = applyToElement;   // alias: lo usan el listener 'play' y el observer

    let _lexMediaObserver = null;
    const updateObserverState = (percent) => {
        try {
            if (percent > 100) {
                if (!_lexMediaObserver && document.documentElement) {
                    _lexMediaObserver = new MutationObserver((mutations) => {
                        for (let i = 0; i < mutations.length; i++) {
                            const added = mutations[i].addedNodes;
                            for (let j = 0; j < added.length; j++) {
                                const node = added[j];
                                if (node.nodeType === 1) {
                                    if (node.tagName === 'VIDEO' || node.tagName === 'AUDIO') {
                                        applyToElement(node, window.__lexVolumePercent);
                                    } else if (node.querySelectorAll) {
                                        node.querySelectorAll('video, audio').forEach(m => applyToElement(m, window.__lexVolumePercent));
                                    }
                                }
                            }
                        }
                    });
                    _lexMediaObserver.observe(document.documentElement, { childList: true, subtree: true });
                }
            } else {
                if (_lexMediaObserver) {
                    _lexMediaObserver.disconnect();
                    _lexMediaObserver = null;
                }
            }
        } catch (_) {}
    };

    const applyVolumeBoostAll = (percent) => {
        const p = lexClamp(percent);
        window.__lexVolumePercent = p;
        if (p === 0) window.__lexMutedByZeroVol = true;
        updateObserverState(p);
        collectAllMedia().forEach(el => applyToElement(el, p));
        document.querySelectorAll('iframe').forEach(ifr => {
            try { if (ifr.contentWindow) ifr.contentWindow.postMessage({ __lex_cmd: 'set_volume_boost', percent: p }, '*'); } catch(_) {}
        });
    };

    // Exponer función global para invocación directa desde C# en cualquier frame
    window.__lexSetVolumeBoostGlobal = function(percent) {
        applyVolumeBoostAll(percent);
    };

    // Handshake inicial para subframes (iframes de streamwish, vidhide, etc.): solicitar volumen actual al padre
    try {
        if (window.parent && window.parent !== window) {
            window.parent.postMessage({ __lex_cmd: 'req_volume_boost' }, '*');
        }
        if (window.top && window.top !== window && window.top !== window.parent) {
            window.top.postMessage({ __lex_cmd: 'req_volume_boost' }, '*');
        }
    } catch (_) {}

    // ─────────────────────────────────────────────────────────────────
    // 3.2 DETECTOR MULTIMEDIA Y DE DOCUMENTOS BAJO DEMANDA (0 consumo cuando está apagado)
    // ─────────────────────────────────────────────────────────────────
    const scanPageMediaOnDemand = () => {
        try {
            // En YouTube, la extracción principal limpia (1080p/720p/Audio) la realiza MediaExtractorService en C#
            if (isYouTube) return;

            const items = [];
            const seen = new Set();
            const addUrl = (rawUrl, category) => {
                if (!rawUrl || typeof rawUrl !== 'string') return;
                if (rawUrl.startsWith('blob:') || rawUrl.startsWith('data:') || rawUrl.startsWith('javascript:')) return;
                let abs = rawUrl;
                try { abs = new URL(rawUrl, window.location.href).href; } catch (_) { return; }
                if (!abs.startsWith('http')) return;
                if (seen.has(abs)) return;
                seen.add(abs);
                items.push({ url: abs, category: category || '' });
            };

            // 1. Videos y Audios reales en el DOM (ignorando anuncios o micro-loops decorativos sin pista)
            document.querySelectorAll('video, audio').forEach(m => {
                const isAudio = m.tagName === 'AUDIO';
                if (!isAudio) {
                    const w = m.videoWidth || m.offsetWidth || 0;
                    const h = m.videoHeight || m.offsetHeight || 0;
                    if (w > 0 && h > 0 && w < 120 && h < 120) return;
                }
                const cat = isAudio ? 'Audio' : 'Video';
                if (m.currentSrc) addUrl(m.currentSrc, cat);
                if (m.src) addUrl(m.src, cat);
                m.querySelectorAll('source').forEach(s => addUrl(s.src, cat));
            });

            // 2. Metadatos de video OpenGraph / Twitter Player / JSON-LD (Redes sociales y sitios de video)
            const videoMetas = [
                'meta[property=""og:video""]',
                'meta[property=""og:video:url""]',
                'meta[property=""og:video:secure_url""]',
                'meta[name=""twitter:player:stream""]'
            ];
            videoMetas.forEach(sel => {
                document.querySelectorAll(sel).forEach(meta => {
                    if (meta.content) addUrl(meta.content, 'Video');
                });
            });

            document.querySelectorAll('script[type=""application/ld+json""]').forEach(scr => {
                try {
                    const txt = scr.textContent || '';
                    const match = txt.match(/""contentUrl""\s*:\s*""(https?:\\?\/\\?\/[^""]+)""/i);
                    if (match && match[1]) {
                        addUrl(match[1].replace(/\\\//g, '/'), 'Video');
                    }
                } catch (_) {}
            });

            // 3. Enlaces directos a documentos, comprimidos, audios y videos reales
            const docRegex = /\.(pdf|docx?|xlsx?|pptx?|zip|rar|7z|mp4|mkv|webm|mp3|wav|flac|m4a)(\?|#|$)/i;
            document.querySelectorAll('a[href]').forEach(a => {
                const href = a.getAttribute('href') || '';
                if (docRegex.test(href)) {
                    addUrl(a.href, '');
                }
            });

            if (items.length > 0 && window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage({
                    type: 'media_detected_batch',
                    items: items.slice(0, 30)
                });
            }

            // Solicitar escaneo a iframes hijos (reproductores embebidos de anime, películas, etc.)
            document.querySelectorAll('iframe').forEach(ifr => {
                try {
                    if (ifr.contentWindow) {
                        ifr.contentWindow.postMessage({ __lex_cmd: 'scan_page_media' }, '*');
                    }
                } catch (_) {}
            });
        } catch (_) {}
    };

    // Escuchar comandos propagados entre frames (iframes de reproductores)
    window.addEventListener('message', (e) => {
        try {
            if (!e.data || typeof e.data !== 'object') return;
            if (e.data.__lex_cmd === 'set_volume_boost') {
                applyVolumeBoostAll(e.data.percent);
            } else if (e.data.__lex_cmd === 'req_volume_boost') {
                if (e.source && typeof window.__lexVolumePercent === 'number' && window.__lexVolumePercent !== 100) {
                    try {
                        e.source.postMessage({ __lex_cmd: 'set_volume_boost', percent: window.__lexVolumePercent }, '*');
                    } catch (_) {}
                }
            } else if (e.data.__lex_cmd === 'scan_page_media') {
                scanPageMediaOnDemand();
            } else if (e.data.__lex_cmd === 'to_host' && window === window.top) {
                const d = e.data;
                if ((d.type === 'audio_status' || d.type === 'cors_unlock_req') && window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage(Object.assign({}, d.payload, { type: d.type }));
                }
            }
        } catch (_) {}
    });

    const getActiveVideo = () => {
        const videos = Array.from(collectAllMedia()).filter(m => m.tagName === 'VIDEO');
        const playing = videos.find(v => !v.paused && !v.ended && v.readyState > 1);
        if (playing) return playing;

        const withDuration = videos.find(v => (v.duration > 0 || v.currentTime > 0) && v.offsetWidth > 0 && v.offsetHeight > 0);
        if (withDuration) return withDuration;

        return videos[0] || null;
    };

    window.__lexWasFullscreen = false;

    window.__lexEnterPiP = async function() {
        try {
            const video = getActiveVideo();
            if (!video) return false;
            if (video.paused || video.ended) return false;

            unlockVideoPiP(video);
            if (document.pictureInPictureElement) return true;

            const isFs = !!(document.fullscreenElement || document.webkitFullscreenElement);
            if (isFs) {
                window.__lexWasFullscreen = true;
                try {
                    if (document.exitFullscreen) await document.exitFullscreen();
                    else if (document.webkitExitFullscreen) await document.webkitExitFullscreen();
                } catch(e) {}
            }

            if (typeof video.requestPictureInPicture === 'function') {
                await video.requestPictureInPicture();
                return true;
            }
        } catch(e) {
            console.warn('[LexPiP] error:', e);
        }
        return false;
    };

    window.__lexExitPiP = async function() {
        try {
            if (document.pictureInPictureElement && typeof document.exitPictureInPicture === 'function') {
                window.__lexProgrammaticPiPExit = true;
                try { await document.exitPictureInPicture(); } catch(e) { window.__lexProgrammaticPiPExit = false; }
            }

            if (window.__lexWasFullscreen) {
                window.__lexWasFullscreen = false;
                const video = getActiveVideo();
                const playerContainer = document.querySelector('.html5-video-player') || video;
                if (playerContainer) {
                    try {
                        if (playerContainer.requestFullscreen) await playerContainer.requestFullscreen();
                        else if (playerContainer.webkitRequestFullscreen) await playerContainer.webkitRequestFullscreen();
                    } catch(e) {}
                }
            }
        } catch(e) {}
    };

    // Sincronizar estado multimedia con C# (solo cuando cambia play/pausa, título o está reproduciéndose)
    let lastMediaState = { isPlaying: false, title: '', currentTime: 0, duration: 0 };
    const syncMediaState = () => {
        const video = getActiveVideo();
        if (!video) return;
        unlockVideoPiP(video);

        let title = '';
        if (isYouTube) {
            const ytTitle = document.querySelector('h1.ytd-watch-metadata yt-formatted-string, #title h1 yt-formatted-string');
            title = ytTitle ? ytTitle.textContent.trim() : document.title.replace(' - YouTube', '').trim();
        } else {
            title = document.title.trim();
        }

        const isPlaying = !video.paused && !video.ended && video.readyState > 1;

        const stateChanged = (
            lastMediaState.isPlaying !== isPlaying ||
            lastMediaState.title !== title ||
            (isPlaying && Math.abs(lastMediaState.currentTime - video.currentTime) > 1)
        );

        if (stateChanged && window.chrome && window.chrome.webview) {
            lastMediaState = {
                isPlaying,
                title,
                currentTime: video.currentTime,
                duration: video.duration || 0,
                muted: video.muted,
                volume: video.volume,
                isFullScreen: !!(document.fullscreenElement || document.webkitFullscreenElement)
            };

            window.chrome.webview.postMessage({
                type: 'media_state',
                data: lastMediaState
            });
        }
    };

    // ─────────────────────────────────────────────────────────────────
    // 4. COMANDOS DESDE C#
    // ─────────────────────────────────────────────────────────────────
    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.addEventListener('message', async (event) => {
            const msg = event.data;
            if (!msg) return;
            const video = getActiveVideo();

            switch (msg.action) {
                case 'play':
                    if (video) video.play().catch(() => {});
                    break;
                case 'pause':
                    if (video) video.pause();
                    break;
                case 'toggle_play':
                    if (video) {
                        if (video.paused) video.play().catch(() => {}); else video.pause();
                    }
                    break;
                case 'seek':
                    if (video && typeof msg.time === 'number') video.currentTime = msg.time;
                    break;
                case 'toggle_mute':
                    if (video) video.muted = !video.muted;
                    break;
                case 'set_mute':
                    if (video && typeof msg.muted === 'boolean') video.muted = msg.muted;
                    break;
                case 'set_volume_boost':
                    applyVolumeBoostAll(msg.percent);
                    break;
                case 'scan_page_media':
                    scanPageMediaOnDemand();
                    break;
                case 'enter_pip':
                    if (window.__lexEnterPiP) await window.__lexEnterPiP();
                    break;
                case 'exit_pip':
                    if (window.__lexExitPiP) await window.__lexExitPiP();
                    break;
                case 'restore_fullscreen':
                    window.__lexWasFullscreen = false;
                    const fVideo = getActiveVideo();
                    const playerCont = document.querySelector('.html5-video-player') || fVideo;
                    if (playerCont && !document.fullscreenElement) {
                        try {
                            if (playerCont.requestFullscreen) playerCont.requestFullscreen().catch(() => {});
                            else if (playerCont.webkitRequestFullscreen) playerCont.webkitRequestFullscreen();
                        } catch(e) {}
                    }
                    break;
            }
        });
    }

    // Atajos de teclado dentro de la página web (Ctrl+N, Ctrl+T, Ctrl+Shift+N, Ctrl+Shift+T, Ctrl+W, Ctrl+H)
    try {
        window.addEventListener('keydown', function(e) {
            if (!e.ctrlKey || e.altKey) return;
            const k = (e.key || '').toLowerCase();
            if (!e.shiftKey && k === 'h') {
                e.preventDefault();
                e.stopPropagation();
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage({ type: 'open_history' });
                }
            } else if (!e.shiftKey && (k === 'n' || k === 't')) {
                e.preventDefault();
                e.stopPropagation();
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage({ type: 'shortcut_new_tab', incognito: false });
                }
            } else if (e.shiftKey && k === 'n') {
                e.preventDefault();
                e.stopPropagation();
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage({ type: 'shortcut_new_tab', incognito: true });
                }
            } else if (e.shiftKey && k === 't') {
                e.preventDefault();
                e.stopPropagation();
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage({ type: 'shortcut_reopen_tab' });
                }
            } else if (!e.shiftKey && k === 'w') {
                e.preventDefault();
                e.stopPropagation();
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage({ type: 'shortcut_close_tab' });
                }
            }
        }, true);
    } catch(_) {}

    // ── Arranque basado en eventos ──
    const onReady = (fn) => {
        if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', fn, { once: true });
        else fn();
    };
    const onLoaded = (fn) => {
        if (document.readyState === 'complete') fn();
        else window.addEventListener('load', fn, { once: true });
    };

    if (isYouTube) {
        onReady(armYouTubeObservers);
    } else {
        const runClear = () => { if (!document.hidden) clearClickjackOverlays(); };
        onLoaded(() => [600, 3000, 8000].forEach(ms => setTimeout(runClear, ms)));
        document.addEventListener('visibilitychange', runClear);
    }

    // ── Estado multimedia por eventos (0 timers en reposo) ──
    let syncTimer = null;
    const scheduleSync = () => {
        if (syncTimer) return;
        syncTimer = setTimeout(() => { syncTimer = null; syncMediaState(); }, 250);
    };
    window.__lexScheduleSync = scheduleSync;

    ['play', 'pause', 'ended', 'loadedmetadata', 'durationchange', 'emptied'].forEach(evt =>
        document.addEventListener(evt, (e) => {
            const t = e.target;
            if (t && (t.tagName === 'VIDEO' || t.tagName === 'AUDIO')) scheduleSync();
        }, true));

    // Título: las SPA lo cambian sin eventos de media
    const armTitleObserver = () => {
        const t = document.querySelector('title');
        if (!t || t.__lexTitleObs) return;
        t.__lexTitleObs = true;
        new MutationObserver(scheduleSync).observe(t, { childList: true });
    };
    onReady(armTitleObserver);
    if (isYouTube) document.addEventListener('yt-navigate-finish', () => { armTitleObserver(); scheduleSync(); }, true);

    // Progreso (currentTime): solo si C# lo pide, p. ej. mientras la ventana flotante está visible
    let progressOn = false, lastProgressSent = 0;
    const onTimeUpdate = (e) => {
        const t = e.target;
        if (!t || (t.tagName !== 'VIDEO' && t.tagName !== 'AUDIO')) return;
        const now = Date.now();
        if (now - lastProgressSent < 1000) return;
        lastProgressSent = now;
        syncMediaState();
    };
    window.__lexSetMediaProgress = (on) => {
        on = !!on;
        if (on === progressOn) return;
        progressOn = on;
        if (on) { document.addEventListener('timeupdate', onTimeUpdate, true); syncMediaState(); }
        else document.removeEventListener('timeupdate', onTimeUpdate, true);
    };

    // Inyección de estilos cosméticos y Cortina Visual Fantasma para YouTube
    if (isYouTube) {
        const style = document.createElement('style');
        style.id = 'lex-adblock-styles';
        style.textContent = `
            /* Cortina visual fantasma: Oculta el video y todo el módulo de anuncio al 100% mientras corre el anuncio */
            .html5-video-player.ad-showing video,
            .html5-video-player.ad-interrupting video,
            #movie_player.ad-showing video,
            #movie_player.ad-interrupting video {
                opacity: 0 !important;
                visibility: hidden !important;
                filter: brightness(0) !important;
            }

            /* Ocultar ABSOLUTAMENTE TODOS los contenedores de publicidad, textos, banners y overlays para no ver nada */
            .video-ads,
            .ytp-ad-module,
            .ytp-ad-player-overlay,
            .ytp-ad-player-overlay-layout,
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
            /* Desactivar el backdrop y modal anti-adblock específicamente sin alterar menús legítimos */
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

            /* Garantizar que el telón de fondo de Polymer jamás capture ni bloquee los clics del puntero */
            tp-yt-iron-overlay-backdrop {
                pointer-events: none !important;
            }

            /* YouTube Shorts: Garantizar visibilidad de barra de tiempo y controles sin alterar el layout nativo de YouTube */
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
        (document.head || document.documentElement).appendChild(style);
    }
})();
";
}
