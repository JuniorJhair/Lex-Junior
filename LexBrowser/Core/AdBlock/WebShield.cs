namespace LexBrowser.Core.AdBlock;

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
    /// Devuelve el script de escudo a inyectar en el documento según las preferencias actuales.
    /// </summary>
    public static string InjectedScript => GetInjectedScript(
        EnableVideoPlayerDefuser,
        SettingsManager.Current?.EnableWebShieldInSubframes ?? false,
        SettingsManager.Current?.EnableCosmeticShield ?? true
    );

    public static string GetInjectedScript(bool enableVideoDefuser, bool enableSubframes = false, bool enableCosmetic = true)
    {
        string script = BaseScript
            .Replace("const __LEX_ENABLE_SUBFRAME_SHIELD = false;", $"const __LEX_ENABLE_SUBFRAME_SHIELD = {(enableSubframes ? "true" : "false")};")
            .Replace("const __LEX_ENABLE_COSMETIC_SHIELD = true;", $"const __LEX_ENABLE_COSMETIC_SHIELD = {(enableCosmetic ? "true" : "false")};");

        return enableVideoDefuser ? script + VideoPlayerDefuserScript : script;
    }

    /// <summary>
    /// Escudo base universal para navegación web, sitios de streaming y páginas con publicidad invasiva.
    /// Neutraliza pop-unders, click-jacking, enlaces trampa y detectores anti-adblock sin tocar YouTube.
    /// </summary>
    public const string BaseScript = @"
(function() {
    if (window.__lex_web_shield_injected) return;
    window.__lex_web_shield_injected = true;

    const __LEX_ENABLE_SUBFRAME_SHIELD = false;
    const __LEX_ENABLE_COSMETIC_SHIELD = true;

    const hostname = window.location.hostname || '';
    const isTopWindow = (window.top === window.self);

    const isYouTube = hostname === 'youtube.com' ||
                      hostname === 'www.youtube.com' ||
                      hostname.endsWith('.youtube.com') ||
                      hostname === 'youtu.be' ||
                      hostname.endsWith('.youtube-nocookie.com');

    // YouTube cuenta con su propio optimizador aislado y no debe ser intervenido por este escudo genérico
    if (isYouTube) return;

    const isTrustedSite = hostname.endsWith('dailymotion.com') ||
                          hostname.endsWith('youtube.com') ||
                          hostname.endsWith('twitch.tv') ||
                          hostname.endsWith('vimeo.com');

    // Si es un iframe de una plataforma de video confiable o los subframes están desactivados,
    // NO inyectar ni intervenir en el marco del reproductor para garantizar su reproducción nativa pura
    if (!isTopWindow && (isTrustedSite || !__LEX_ENABLE_SUBFRAME_SHIELD)) return;

    // ─────────────────────────────────────────────────────────────────
    // 0. SCRIPTLETS DE EMULACIÓN ANTI-ANTI-ADBLOCK & NOTIFICATION DEFUSER
    // ─────────────────────────────────────────────────────────────────
    try {
        window.canRunAds = true;
        window.isAdBlockActive = false;
        window.adblock = false;
        window.google_ad_status = 1;
        window.adsbygoogle = window.adsbygoogle || [];
        window.adsbygoogle.push = function() { return 0; };
        window.google_ad_client = 'ca-pub-test';
        window.PopAds = window.PopAds || {};
        window.popns = window.popns || {};

        // Emular bibliotecas de popunder para que se desactiven pacíficamente sin romper el reproductor
        const fakeAclib = {
            runPop: function() { return false; },
            runBanner: function() { return false; },
            runSlider: function() { return false; },
            runInterstitial: function() { return false; },
            runVideo: function() { return false; }
        };
        try {
            window.aclib = fakeAclib;
            Object.defineProperty(window, 'aclib', {
                get: function() { return fakeAclib; },
                set: function() {},
                configurable: true
            });
        } catch(_) {}

        const noop = function() {};
        const fakePopMagic = {
            init: noop,
            pop: noop,
            capture: noop,
            click: noop,
            check: function() { return false; }
        };
        try {
            window.popMagic = fakePopMagic;
            Object.defineProperty(window, 'popMagic', {
                get: function() { return fakePopMagic; },
                set: function() {},
                configurable: true
            });
        } catch(_) {}

        window.popns = {};
        window._pop = [];
        window._monetag = {};
        window.adcash = {};

        // Neutralizar variables globales de inyección VAST en reproductores incrustados (Brave / uBO set-constant)
        try {
            const emptyStrDesc = { get: function() { return ''; }, set: function() {}, configurable: true };
            const emptyArrDesc = { get: function() { return []; }, set: function() {}, configurable: true };

            try { Object.defineProperty(window, 'vastUrl', emptyStrDesc); } catch(_) {}
            try { Object.defineProperty(window, 'vast_url', emptyStrDesc); } catch(_) {}
            try { Object.defineProperty(window, 'vastURL', emptyStrDesc); } catch(_) {}
            try { Object.defineProperty(window, 'vastAds', emptyArrDesc); } catch(_) {}
            try { Object.defineProperty(window, 'ad_tags', emptyArrDesc); } catch(_) {}

            // Pre-configurar timestamps en localStorage para satisfacer comprobaciones de tiempo de reproductores
            try {
                if (window.localStorage) {
                    const futureTime = (Date.now() + 86400000).toString();
                    localStorage.setItem('ads-last-seen', futureTime);
                    localStorage.setItem('ad_last_seen', futureTime);
                    localStorage.setItem('vast_last_seen', futureTime);
                }
            } catch(_) {}
        } catch(_) {}

        // Neutralizar detectores basados en elementos trampa BAB (BlockAdBlock)
        try {
            const origGetElem = document.getElementById;
            document.getElementById = function(id) {
                if (id === 'babasbmsgx' || (typeof id === 'string' && id.startsWith('bab_'))) {
                    return null;
                }
                return origGetElem.apply(this, arguments);
            };
        } catch(_) {}

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
    // 0.1 ESCUDO COSMÉTICO (Ocultamiento de banners, iframes y sliders publicitarios)
    // ─────────────────────────────────────────────────────────────────
    try {
        const applyCosmeticStyles = function() {
            try {
                if (!__LEX_ENABLE_COSMETIC_SHIELD) return;
                if (document.getElementById('__lex_cosmetic_shield')) return;
                const target = document.head || document.documentElement;
                if (!target) return;
                const style = document.createElement('style');
                style.id = '__lex_cosmetic_shield';
                style.textContent = `
                    iframe[src*='magsrv.com'],
                    iframe[src*='realsrv.com'],
                    iframe[src*='exoclick.com'],
                    iframe[src*='trafficjunky'],
                    iframe[src*='trafficstars'],
                    iframe[src*='juicyads'],
                    iframe[src*='adglare'],
                    iframe[src*='plugrush'],
                    iframe[src*='adxvip'],
                    iframe[src*='etahub'],
                    iframe[src*='tsyndicate'],
                    iframe[src*='ero-advertising'],
                    iframe[src*='adtng.com'],
                    iframe[src*='dmads.dailymotion.com'],
                    iframe[src*='bannerflow.net'],
                    iframe[src*='adform.net'],
                    div[class*='dm-ad'],
                    div[id*='dmads'],
                    a[href*='tsyndicate'],
                    a[href*='adtng.com'],
                    img[src*='adtng.com'],
                    div[class*='video-instant-message'],
                    div[id*='video-instant-message'],
                    .banner-container,
                    div[class*='banner-container'],
                    div[class*='ad-banner']:not(:has(video)):not([class*='player']),
                    div[class*='ad_banner']:not(:has(video)):not([class*='player']),
                    div[id*='ad-banner']:not(:has(video)):not([class*='player']),
                    div[class*='ad-zone'],
                    div[class*='ad-placement'],
                    div[class*='exo-slider'],
                    div[id*='exo-slider'],
                    div[class*='pr_advertising_'],
                    div[id*='pr_advertising_'],
                    [data-zone-id]:not([class*='player']),
                    .ad-container:not(#player):not([class*='player']):not(:has(video)) {
                        display: none !important;
                        visibility: hidden !important;
                        height: 0 !important;
                        min-height: 0 !important;
                        opacity: 0 !important;
                        pointer-events: none !important;
                    }
                `;
                target.appendChild(style);
            } catch(_) {}
        };
        applyCosmeticStyles();
        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', applyCosmeticStyles, { once: true });
        }
    } catch(e) {}

    // ─────────────────────────────────────────────────────────────────
    // 1. ESCUDO ANTI-CLICKJACKING Y ANTI-POPUNDERS (Streaming / Web)
    // ─────────────────────────────────────────────────────────────────
    const adDomainsRegex = /(propellerads|popcash|popads|adsterra|monetag|bet365|1xbet|clickadu|hilltopads|exoclick|juicyads|syndication|doubleclick|adservice|outbrain|taboola|criteo|adnxs|pubmatic|onclick|trafficstars|adcash|revcontent|smartadserver|r2ads|llvpn|streamtp|trkr|ppof\.net|visariomedia|qojseyonoj|hfryyynvqwgy|magsrv|realsrv|exdynsrv|trafficjunky|adglare|plugrush|adxvip|etahub|ptgnadsv2|videoplayerhub|runative|adtng|pemsrv|opoxv)/i;

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

    // 1.3 Verificador de Controles Legítimos y Elementos con Significado Visual
    const isControlOrMeaningful = (el) => {
        if (!el) return false;
        const tag = el.tagName || '';
        if (['BUTTON', 'INPUT', 'SELECT', 'TEXTAREA', 'LABEL', 'VIDEO', 'AUDIO'].includes(tag)) return true;
        if (tag === 'SVG' || el.closest('svg')) return true;

        // Si tiene texto visible legítimo
        const text = (el.innerText || el.textContent || '').trim();
        if (text.length > 0) {
            const style = window.getComputedStyle(el);
            const opacity = parseFloat(style.opacity);
            if (opacity > 0.3 && style.visibility !== 'hidden' && style.display !== 'none') {
                return true;
            }
        }

        // Si es un control explícito de la interfaz o reproductor
        const cls = ((el.className || '') + ' ' + (el.id || '')).toLowerCase();
        if (/(control|button|btn|play|pause|volume|mute|progress|slider|seek|timeline|time|setting|gear|fullscreen|quality|caption|subtitle|speed)/.test(cls)) {
            return true;
        }

        return false;
    };

    // 1.4 Verificador Heurístico Fino de Capas Fantasma / Overlays Trampa sobre Video
    const isGhostOverlay = (el) => {
        if (!el || el === document.body || el === document.documentElement) return false;
        const tag = el.tagName || '';
        if (['VIDEO', 'AUDIO', 'BUTTON', 'INPUT', 'SELECT', 'TEXTAREA', 'FORM', 'DIALOG'].includes(tag)) return false;

        // NUNCA borrar el elemento raíz de frameworks SPA
        if (el.id === 'root' || el.id === 'app' || el.hasAttribute('data-reactroot')) return false;

        // CRÍTICO: Si el elemento CONTIENE un video o iframe dentro de su árbol, NO borrarlo (es el contenedor del reproductor)
        if (el.querySelector('video, audio, iframe')) return false;

        // Si contiene controles interactivos legítimos (botones, sliders, svgs, etc.)
        if (el.querySelector('button, input, select, textarea, svg, [class*=control], [class*=btn]')) return false;

        // Si tiene texto visible legítimo
        const text = (el.innerText || el.textContent || '').trim();
        const style = window.getComputedStyle(el);
        const opacity = parseFloat(style.opacity);
        if (text.length > 0 && style.visibility !== 'hidden' && opacity > 0.3) return false;

        // Verificar estilo visual de trampa (posicionado absoluto/fijo y transparente)
        const isPositioned = (style.position === 'fixed' || style.position === 'absolute');
        const bg = style.backgroundColor || '';
        const isZeroAlpha = (!bg || bg === 'transparent' || (bg.includes('rgba(') && parseFloat(bg.split(',')[3] || '1') <= 0.08));
        const isTransparent = (opacity <= 0.08 || isZeroAlpha || style.visibility === 'hidden');

        // Nombre explícito de anuncio trampa
        const idClass = ((el.id || '') + ' ' + (el.className || '')).toLowerCase();
        const isExplicitAdName = /(first-click-ad|click-ad|ad-overlay|pop-overlay|popup-overlay|interads|pop-under|banner-overlay)/.test(idClass);

        if (!isPositioned && !isExplicitAdName) return false;
        if (!isTransparent && !isExplicitAdName) return false;

        // Caso 1: Enlace fantasma (<a> transparente o con target=_blank o apuntando a ad/cross-origin)
        const anchor = (tag === 'A') ? el : el.querySelector('a');
        if (anchor) {
            const href = (anchor.href || '').toLowerCase();
            const isAdLink = adDomainsRegex.test(href) || isAdOrSuspiciousUrl(href) || anchor.target === '_blank';
            if (isAdLink || !text) return true;
        }

        // Caso 2: Capa trampantojo sobre reproductor o ventana con z-index alto
        const zIndex = parseInt(style.zIndex, 10);
        const rect = el.getBoundingClientRect();
        const isLargeCoverage = (rect.width > 200 && rect.height > 150);

        if (isLargeCoverage && (zIndex >= 10 || isNaN(zIndex) || isExplicitAdName) && isTransparent && !text) {
            return true;
        }

        return isExplicitAdName;
    };

    // 1.5 Interceptor en Fase de Captura Pre-Clic (Destrucción inmediata + Click Pass-Through)
    const handleCaptureClick = (e) => {
        let target = e.target;
        if (!target) return;

        // Si es un control legítimo o contiene texto/iconos reales, permitir inmediatamente
        if (isControlOrMeaningful(target)) {
            return;
        }

        let ghost = null;
        let curr = target;
        for (let i = 0; i < 5 && curr && curr !== document.body; i++) {
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

            const r = ghost.getBoundingClientRect ? ghost.getBoundingClientRect() : { width: 0, height: 0 };
            const cs = window.getComputedStyle ? window.getComputedStyle(ghost) : {};
            const tag = ghost.tagName || '';
            const id = ghost.id ? ('#' + ghost.id) : '';
            const cls = (ghost.className && typeof ghost.className === 'string') ? ('.' + ghost.className.trim().split(/\s+/).join('.')) : '';
            const zi = cs.zIndex || 'auto';
            const elDesc = `${tag}${id}${cls} [${Math.round(r.width)}x${Math.round(r.height)}, z:${zi}]`;

            ghost.remove();

            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage({
                    type: 'ad_blocked',
                    reason: `ghost_overlay_capture -> ${elDesc}`
                });
            }

            // Click Pass-Through: Pasar el clic al elemento legítimo o video que estaba atrapado debajo
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

    const isTrustedSite = hostname.endsWith('dailymotion.com') ||
                          hostname.endsWith('youtube.com') ||
                          hostname.endsWith('twitch.tv') ||
                          hostname.endsWith('vimeo.com');

    if (!isTrustedSite) {
        window.addEventListener('click', handleCaptureClick, true);
        window.addEventListener('pointerdown', handleCaptureClick, true);
    }

    // 1.6 Limpieza proactiva de capas trampas en el DOM (restringido estrictamente a ads explícitos, anchors fantasma y hermanos sobre videos)
    const clearClickjackOverlays = () => {
        if (isTrustedSite) return;
        try {
            // 1. Elementos con nombres explícitos de anuncio
            const explicitAds = document.querySelectorAll('[id*=first-click-ad], [class*=first-click-ad], [id*=click-ad], [class*=click-ad], [class*=ad-overlay], [class*=pop-overlay], [class*=popup-overlay], [class*=interads], [id*=popunder], [class*=popunder]');
            for (let i = 0; i < explicitAds.length; i++) {
                const el = explicitAds[i];
                if (isGhostOverlay(el)) {
                    const r = el.getBoundingClientRect ? el.getBoundingClientRect() : { width: 0, height: 0 };
                    const tag = el.tagName || '';
                    const id = el.id ? ('#' + el.id) : '';
                    const cls = (el.className && typeof el.className === 'string') ? ('.' + el.className.trim().split(/\s+/).join('.')) : '';
                    el.remove();
                    if (window.chrome && window.chrome.webview) {
                        window.chrome.webview.postMessage({
                            type: 'ad_blocked',
                            reason: `ghost_overlay_explicit -> ${tag}${id}${cls} [${Math.round(r.width)}x${Math.round(r.height)}]`
                        });
                    }
                }
            }

            // 2. Enlaces transparentes con target=_blank
            const ghostAnchors = document.querySelectorAll('a[target=_blank]');
            for (let i = 0; i < ghostAnchors.length; i++) {
                const a = ghostAnchors[i];
                if (isGhostOverlay(a)) {
                    const r = a.getBoundingClientRect ? a.getBoundingClientRect() : { width: 0, height: 0 };
                    a.remove();
                    if (window.chrome && window.chrome.webview) {
                        window.chrome.webview.postMessage({
                            type: 'ad_blocked',
                            reason: `ghost_anchor_scanned -> A [${Math.round(r.width)}x${Math.round(r.height)}]`
                        });
                    }
                }
            }

            // 3. Capas trampas colocadas como hermanas sobre contenedores de video o iframes
            // Excluir plataformas de video legítimas de la remoción de hermanos para preservar controles nativos
            if (!hostname.endsWith('dailymotion.com') && !hostname.endsWith('youtube.com') && !hostname.endsWith('twitch.tv') && !hostname.endsWith('vimeo.com')) {
                const players = document.querySelectorAll('[class*=player], [id*=player], iframe, video');
                for (let i = 0; i < players.length; i++) {
                    const p = players[i];
                    const siblings = p.parentElement ? p.parentElement.children : [];
                    for (let j = 0; j < siblings.length; j++) {
                        const sib = siblings[j];
                        if (sib !== p && isGhostOverlay(sib)) {
                            const r = sib.getBoundingClientRect ? sib.getBoundingClientRect() : { width: 0, height: 0 };
                            sib.remove();
                            if (window.chrome && window.chrome.webview) {
                                window.chrome.webview.postMessage({
                                    type: 'ad_blocked',
                                    reason: `player_sibling_overlay_removed -> ${sib.tagName} [${Math.round(r.width)}x${Math.round(r.height)}]`
                                });
                            }
                        }
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

    /// <summary>
    /// Desarmador pasivo y no invasivo para reproductores de video (JW Player / Fluid Player).
    /// Nunca reemplaza window.jwplayer ni modifica setup() para preservar licencias y flujos HLS.
    /// Solo activo si EnableVideoPlayerDefuser es true.
    /// </summary>
    public const string VideoPlayerDefuserScript = @"
(function() {
    let attempts = 0;
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
        if (++attempts < 20) {
            setTimeout(attachSafePlayerListener, 500);
        }
    };
    if (document.readyState === 'complete') {
        attachSafePlayerListener();
    } else {
        window.addEventListener('load', attachSafePlayerListener, { once: true });
    }
})();
";
}

