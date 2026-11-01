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

    // 1.1 Defusor de window.open para popunders no solicitados
    try {
        const originalOpen = window.open;
        window.open = function(url, target, features) {
            if (url && typeof url === 'string') {
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
    const clearYouTubePopups = () => {
        if (!isYouTube) return;
        const enforcement = document.querySelector('ytd-enforcement-message-view-model');
        if (enforcement) {
            enforcement.remove();
            const player = document.querySelector('#movie_player') || document.querySelector('.html5-video-player');
            if (player && typeof player.playVideo === 'function') player.playVideo();
        }
        const feedback = document.getElementById('feedback');
        if (feedback) {
            const dialog = feedback.closest('tp-yt-paper-dialog');
            if (dialog) dialog.remove();
        }
        const backdrop = document.querySelector('tp-yt-iron-overlay-backdrop');
        if (backdrop) backdrop.remove();
    };

    let wasMutedByAdBlock = false;
    let previousUserPlaybackRate = 1.0;
    let isHandlingAd = false;
    let wasAdReported = false;
    let lastAdCheckTime = 0;

    const skipYouTubeAds = () => {
        if (!isYouTube) return;
        const now = Date.now();
        if (now - lastAdCheckTime < 120) return; // Límite de tasa a 120ms
        lastAdCheckTime = now;

        const moviePlayer = document.querySelector('#movie_player') || document.querySelector('.html5-video-player');
        const video = document.querySelector('video');
        if (!video || !moviePlayer) return;

        // Detección estricta oficial: YouTube siempre asigna 'ad-showing' o 'ad-interrupting'
        const isAdShowing = moviePlayer.classList.contains('ad-showing') || moviePlayer.classList.contains('ad-interrupting');

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
                const skipBtn = document.querySelector('.ytp-ad-skip-button, .ytp-ad-skip-button-modern, .ytp-skip-ad-button, .ytp-ad-skip-button-slot button, button[class*=""skip""]');
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

    // Escucha en fase de captura para eventos multimedia en YouTube (detección instantánea sin delay)
    if (isYouTube) {
        document.addEventListener('play', () => skipYouTubeAds(), true);
        document.addEventListener('timeupdate', () => skipYouTubeAds(), true);
        document.addEventListener('yt-navigate-finish', () => {
            skipYouTubeAds();
            clearYouTubePopups();
        }, true);
    }

    // Observer enfocado únicamente en la clase del player contenedor (sin bucles de DOM)
    let ytPlayerObserver = null;
    const initYouTubeObserver = () => {
        if (!isYouTube || ytPlayerObserver) return;
        const player = document.querySelector('#movie_player') || document.querySelector('.html5-video-player');
        if (player) {
            ytPlayerObserver = new MutationObserver(() => {
                skipYouTubeAds();
                clearYouTubePopups();
            });
            ytPlayerObserver.observe(player, {
                attributes: true,
                attributeFilter: ['class'],
                childList: false,
                subtree: false
            });

            // Neutralización inmediata en el primer milisegundo al encontrar el reproductor
            skipYouTubeAds();
            clearYouTubePopups();
        }
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

        // Escuchar el evento cuando el usuario presiona 'Volver a la pestaña' en el PiP nativo
        if (!video.__lex_pip_listener_attached) {
            video.__lex_pip_listener_attached = true;
            video.addEventListener('leavepictureinpicture', () => {
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage({
                        type: 'pip_return_requested',
                        currentTime: video.currentTime,
                        isPaused: video.paused,
                        url: window.location.href,
                        wasFullscreen: !!window.__lexWasFullscreen
                    });
                }
            });
        }
    };

    // Desbloquear videos existentes y bajo demanda al reproducir
    document.addEventListener('play', (e) => {
        if (e.target && e.target.tagName === 'VIDEO') {
            unlockVideoPiP(e.target);
        }
    }, true);
    document.querySelectorAll('video').forEach(unlockVideoPiP);

    const getActiveVideo = () => {
        const videos = Array.from(document.querySelectorAll('video'));
        // 1. Preferir el video que esté reproduciéndose activamente
        const playing = videos.find(v => !v.paused && !v.ended && v.readyState > 1);
        if (playing) return playing;

        // 2. Si no, cualquier video que tenga duración o tamaño visible
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
                try { await document.exitPictureInPicture(); } catch(e) {}
            }

            // Si antes de ir a PiP estaba en pantalla completa, restaurar pantalla completa automáticamente
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

    // Sincronizar estado multimedia con C#
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
            Math.abs(lastMediaState.currentTime - video.currentTime) > 1 ||
            lastMediaState.title !== title
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

                // Mini-Reproductor: activar Picture-in-Picture nativo
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

                // Mini-Reproductor libre: Estiramiento responsive 100% sin límites (shorts y videos)
                case 'set_video_only_mode':
                    if (typeof msg.enabled === 'boolean') {
                        let pipStyle = document.getElementById('lex-video-only-style');
                        if (msg.enabled) {
                            if (!pipStyle) {
                                pipStyle = document.createElement('style');
                                pipStyle.id = 'lex-video-only-style';
                                pipStyle.textContent = `
                                    html, body {
                                        overflow: hidden !important;
                                        background: #000000 !important;
                                    }
                                    video {
                                        position: fixed !important;
                                        top: 0 !important;
                                        left: 0 !important;
                                        width: 100vw !important;
                                        height: 100vh !important;
                                        max-width: none !important;
                                        max-height: none !important;
                                        object-fit: contain !important;
                                        z-index: 2147483647 !important;
                                        background: #000000 !important;
                                        cursor: pointer !important;
                                    }
                                    #masthead-container, ytd-mini-guide-renderer, #guide, #sidebar, #below, #comments, #secondary, ytd-shorts #actions, #shorts-inner-container > ytd-reel-video-renderer:not([is-active]) {
                                        display: none !important;
                                    }
                                `;
                                (document.head || document.documentElement).appendChild(pipStyle);
                            }
                            const v = getActiveVideo();
                            if (v && !v.__lex_click_toggle_attached) {
                                v.__lex_click_toggle_attached = true;
                                v.addEventListener('click', () => {
                                    if (v.paused) v.play().catch(() => {}); else v.pause();
                                });
                            }
                        } else {
                            if (pipStyle) pipStyle.remove();
                        }
                    }
                    break;
            }
        });
    }

    // Atajos de teclado dentro de la página web (Ctrl + H para abrir Historial)
    try {
        window.addEventListener('keydown', function(e) {
            if (e.ctrlKey && (e.key === 'h' || e.key === 'H' || e.keyCode === 72)) {
                e.preventDefault();
                e.stopPropagation();
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage(JSON.stringify({ type: 'open_history' }));
                }
            }
        }, true);
    } catch(_) {}

    // Monitores periódicos ligeros (sin saturación de CPU)
    if (isYouTube) {
        initYouTubeObserver();
        setInterval(() => {
            if (!ytPlayerObserver) initYouTubeObserver();
            skipYouTubeAds();
            clearYouTubePopups();
        }, 350);
    } else {
        setInterval(clearClickjackOverlays, 300);
    }
    setInterval(syncMediaState, 1000);

    // Inyección de estilos cosméticos y Cortina Visual Fantasma para YouTube
    if (isYouTube) {
        const style = document.createElement('style');
        style.id = 'lex-adblock-styles';
        style.textContent = `
            /* Cortina visual fantasma: Oculta el video mientras corre el anuncio para que nunca se vea */
            .html5-video-player.ad-showing video,
            .html5-video-player.ad-interrupting video {
                opacity: 0 !important;
                visibility: hidden !important;
            }
            /* Ocultar todos los banners, overlays, promos y botones de compra */
            .ytp-ad-overlay-container,
            .ytp-ad-message-container,
            .ytp-ad-survey-container,
            .ytp-ad-action-interstitial,
            .ytp-ad-player-overlay,
            .ytp-ad-preview-container,
            .ytp-ad-text,
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
            .ytd-merch-shelf-renderer,
            ytd-banner-promo-renderer {
                display: none !important;
                visibility: hidden !important;
                height: 0 !important;
                pointer-events: none !important;
            }

            /* YouTube Shorts: Asegurar que el reproductor y la barra de tiempo/progreso nunca se desborden ni se oculten */
            ytd-shorts {
                overflow-y: auto !important;
            }
            ytd-reel-player-progress-bar-view-model,
            .ytd-reel-player-progress-bar-view-model,
            ytd-shorts [class*=""progress-bar""],
            ytd-shorts #progress-bar,
            ytd-shorts .yt-progress-bar,
            ytd-shorts [class*=""time-display""] {
                visibility: visible !important;
                opacity: 1 !important;
                display: flex !important;
                z-index: 2147483640 !important;
            }
        `;
        (document.head || document.documentElement).appendChild(style);
    }
})();
";
}
