namespace LexBrowser.Core.AdBlock;

public static class WebShield
{
    /// <summary>
    /// Escudo universal para navegación web, sitios de streaming y páginas con publicidad invasiva.
    /// Neutraliza pop-unders, click-jacking, enlaces trampa y detectores anti-adblock sin tocar YouTube.
    /// </summary>
    public const string InjectedScript = @"
(function() {
    if (window.__lex_web_shield_injected) return;
    window.__lex_web_shield_injected = true;

    const hostname = window.location.hostname || '';
    const isYouTube = hostname === 'youtube.com' ||
                      hostname === 'www.youtube.com' ||
                      hostname.endsWith('.youtube.com') ||
                      hostname === 'youtu.be' ||
                      hostname.endsWith('.youtube-nocookie.com');

    // YouTube cuenta con su propio optimizador aislado y no debe ser intervenido por este escudo genérico
    if (isYouTube) return;

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

    // 1.1 Defusor de window.open para popunders con señuelo Proxy (closed: false)
    const trustedOAuthRegex = /^https:\/\/([a-z0-9-]+\.)?(google\.com|apple\.com|microsoft\.com|microsoftonline\.com|live\.com|github\.com|facebook\.com|discord\.com|x\.com|twitter\.com|paypal\.com)(\/|$)/i;

    const isAdOrSuspiciousUrl = (u) => {
        if (!u || typeof u !== 'string') return false;
        if (trustedOAuthRegex.test(u)) return false;
        const lower = u.toLowerCase();
        return adDomainsRegex.test(lower) ||
               lower.includes('/ad/') ||
               lower.includes('/ads/') ||
               lower.includes('popunder') ||
               lower.includes('direct-link') ||
               lower.includes('link?z=') ||
               lower.includes('?z=') ||
               lower.includes('&z=') ||
               lower.includes('zoneid') ||
               lower.includes('zoneId') ||
               lower.includes('trkr.') ||
               (lower.includes('click') && lower.includes('track'));
    };

    // Señuelo inteligente de ventana (Proxy) para que reproductores y scripts invasores
    // crean que el popup se abrió correctamente (.closed === false) y no detengan la reproducción.
    const createDecoyPopup = () => {
        const noop = () => {};
        const noopObj = { write: noop, open: noop, close: noop, createElement: () => ({ style: {} }) };
        const noopLoc = { href: 'about:blank', replace: noop, assign: noop, reload: noop };
        try {
            return new Proxy({ closed: false }, {
                get: (target, prop) => {
                    if (prop === 'closed') return false;
                    if (prop === 'document') return noopObj;
                    if (prop === 'location') return noopLoc;
                    if (prop === 'opener') return window;
                    if (prop === 'self' || prop === 'window') return target;
                    return noop;
                },
                set: () => true
            });
        } catch(_) {
            return {
                closed: false,
                focus: noop,
                blur: noop,
                close: noop,
                postMessage: noop,
                addEventListener: noop,
                removeEventListener: noop,
                document: noopObj,
                location: noopLoc,
                opener: window
            };
        }
    };

    try {
        const originalOpen = window.open;
        window.open = function(url, target, features) {
            const urlStr = (typeof url === 'string') ? url : (url && url.toString ? url.toString() : '');
            const isInsideFrame = (window.self !== window.top);
            const isSuspicious = isAdOrSuspiciousUrl(urlStr);

            // 1. Si es detectado como anuncio explícito
            if (isSuspicious) {
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage({ type: 'ad_blocked', url: urlStr, reason: 'window_open_ad_pattern' });
                }
                return createDecoyPopup();
            }

            // 2. Si proviene de un iframe (común en anime/streaming) y navega hacia un dominio cruzado no OAuth
            if (isInsideFrame) {
                const isCrossDomain = urlStr && !urlStr.startsWith(window.location.origin) && !trustedOAuthRegex.test(urlStr);
                const isBlankPop = !urlStr || urlStr === 'about:blank';
                if (isCrossDomain || isBlankPop) {
                    if (window.chrome && window.chrome.webview) {
                        window.chrome.webview.postMessage({ type: 'ad_blocked', url: urlStr || 'about:blank', reason: 'window_open_iframe_popunder' });
                    }
                    return createDecoyPopup();
                }
            }

            // 3. Ejecución normal. Si el navegador cancela la apertura (e.Handled = true), devolver señuelo para no romper el reproductor
            try {
                const win = originalOpen.apply(this, arguments);
                return win || createDecoyPopup();
            } catch(err) {
                return createDecoyPopup();
            }
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

    window.addEventListener('click', handleCaptureClick, true);
    window.addEventListener('pointerdown', handleCaptureClick, true);

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

    const onLoaded = (fn) => {
        if (document.readyState === 'complete') fn();
        else window.addEventListener('load', fn, { once: true });
    };

    const runClear = () => { if (!document.hidden) clearClickjackOverlays(); };
    onLoaded(() => [600, 3000, 8000].forEach(ms => setTimeout(runClear, ms)));
    document.addEventListener('visibilitychange', runClear);
})();
";
}

