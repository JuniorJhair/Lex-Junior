using System.Text.Json;

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

    public static string GetInjectedScript(
        bool enableVideoDefuser,
        bool enableSubframes = false,
        bool enableCosmetic = true,
        bool enablePopups = true,
        bool enableOverlays = true,
        bool enableAntiWall = true)
    {
        string sitesJson = "{}";
        try
        {
            var sites = SettingsManager.Current?.Shield?.Sites;
            if (sites != null && sites.Count > 0)
            {
                sitesJson = JsonSerializer.Serialize(sites);
            }
        }
        catch { }

        string trustedHostsJson = "[]";
        try
        {
            trustedHostsJson = JsonSerializer.Serialize(ShieldExemptions.BigSites);
        }
        catch { }

        string script = BaseScript
            .Replace("const __LEX_ENABLE_SUBFRAME_SHIELD = true;", $"const __LEX_ENABLE_SUBFRAME_SHIELD = {(enableSubframes ? "true" : "false")};")
            .Replace("const __LEX_ENABLE_COSMETIC_SHIELD = true;", $"const __LEX_ENABLE_COSMETIC_SHIELD = {(enableCosmetic ? "true" : "false")};")
            .Replace("const __LEX_ENABLE_POPUPS_SHIELD = true;", $"const __LEX_ENABLE_POPUPS_SHIELD = {(enablePopups ? "true" : "false")};")
            .Replace("const __LEX_ENABLE_OVERLAYS_SHIELD = true;", $"const __LEX_ENABLE_OVERLAYS_SHIELD = {(enableOverlays ? "true" : "false")};")
            .Replace("const __LEX_ENABLE_ANTIWALL_SHIELD = true;", $"const __LEX_ENABLE_ANTIWALL_SHIELD = {(enableAntiWall ? "true" : "false")};")
            .Replace("const __LEX_TRUSTED_HOSTS = [];", $"const __LEX_TRUSTED_HOSTS = {trustedHostsJson};")
            .Replace("const __LEX_SITE_POLICIES = {};", $"const __LEX_SITE_POLICIES = {sitesJson};");

        string defuser = VideoPlayerDefuserScript
            .Replace("const __LEX_ENABLE_VIDEO_DEFUSER = true;", $"const __LEX_ENABLE_VIDEO_DEFUSER = {(enableVideoDefuser ? "true" : "false")};")
            .Replace("const __LEX_TRUSTED_HOSTS = [];", $"const __LEX_TRUSTED_HOSTS = {trustedHostsJson};");

        return script + defuser;
    }

    /// <summary>
    /// Escudo base universal para navegación web, sitios de streaming y páginas con publicidad invasiva.
    /// Neutraliza pop-unders, click-jacking, enlaces trampa y detectores anti-adblock sin tocar YouTube.
    /// </summary>
    public const string BaseScript = @"
(function() {
    if (window.__lex_web_shield_injected) return;
    window.__lex_web_shield_injected = true;

    const __LEX_ENABLE_SUBFRAME_SHIELD = true;
    const __LEX_ENABLE_COSMETIC_SHIELD = true;
    const __LEX_ENABLE_POPUPS_SHIELD = true;
    const __LEX_ENABLE_OVERLAYS_SHIELD = true;
    const __LEX_ENABLE_ANTIWALL_SHIELD = true;
    const __LEX_TRUSTED_HOSTS = [];
    const __LEX_SITE_POLICIES = {};

    const hostname = (window.location.hostname || '').toLowerCase();
    const isTopWindow = (window.top === window.self);
    const ends = (d) => hostname === d || hostname.endsWith('.' + d);

    const isYouTube = ends('youtube.com') ||
                      ends('youtu.be') ||
                      ends('youtube-nocookie.com');

    // YouTube cuenta con su propio optimizador aislado y no debe ser intervenido por este escudo genérico
    if (isYouTube) return;
    if (!hostname || hostname === 'tv.lex.local' || hostname === 'pdf.lex.local') return;

    const isGoogle = ends('google.com') ||
                     ends('google.com.pe') ||
                     ends('google.es') ||
                     ends('google.com.mx') ||
                     ends('google.com.ar') ||
                     ends('google.com.co') ||
                     ends('google.cl') ||
                     ends('gstatic.com') ||
                     ends('googleapis.com') ||
                     ends('googleusercontent.com');

    const isBing = ends('bing.com');

    const isTrustedSite = (__LEX_TRUSTED_HOSTS && __LEX_TRUSTED_HOSTS.some(ends)) || isGoogle || isBing;

    // Sitios dedicados, motores de búsqueda y plataformas legítimas (Miro, Figma, Canva, Dailymotion, Twitch, etc.)
    if (isTrustedSite) return;

    // Jamás intervenir en iframes sensibles (pagos, bancos, OAuth/SSO o CAPTCHAs)
    const isSensitiveSubframe =
        /(paypal|stripe|mercadopago|adyen|braintreepayments|checkout\.com|payu\.com|niubiz|izipay|visa\.com|mastercard\.com|recaptcha|hcaptcha|turnstile|challenges\.cloudflare|arkoselabs|funcaptcha|auth0|okta|accounts\.google|appleid\.apple|login\.microsoftonline|login\.live)/i.test(hostname) ||
        /(\/oauth|\/auth\/|\/login|\/signin|\/checkout|\/3ds|captcha|turnstile)/i.test(window.location.pathname || '');

    if (!isTopWindow && isSensitiveSubframe) return;

    // Activar automáticamente WebShield dentro de iframes de reproductores embebidos (Streamwish, Vidhide, Dood, AnimeYT, HentaiLA...)
    const isVideoEmbedContext = !isTopWindow && (
        /(streamwish|wishfast|wishembed|vidhide|streamtape|dood|d000d|ds2play|filelions|lulustream|luluvdo|mp4upload|uqload|voe\.sx|mixdrop|fastream|hglink|dhcplay|fembed|upstream|vidoza|wolfstream|megacloud|rabbitstream|vidplay|yourupload)/i.test(hostname) ||
        /^\/(e|v|embed|player)(\/|$|-)/i.test(window.location.pathname || '') ||
        /\/(videoembed|video_ext)/i.test(window.location.pathname || '')
    );

    if (!isTopWindow && !__LEX_ENABLE_SUBFRAME_SHIELD && !isVideoEmbedContext) return;

    // Resolver política por sitio en tiempo de ejecución
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

    if (siteMode.toLowerCase() === 'off') return;
    const isCompatMode = siteMode.toLowerCase() === 'compat';
    const isModDisabled = (m) => siteDisabled.includes(m.toLowerCase());

    const activePopups = __LEX_ENABLE_POPUPS_SHIELD && !isModDisabled('popups');
    const activeCosmetic = !isCompatMode && __LEX_ENABLE_COSMETIC_SHIELD && !isModDisabled('cosmetic');
    const activeOverlays = !isCompatMode && __LEX_ENABLE_OVERLAYS_SHIELD && !isModDisabled('overlays');
    const activeAntiWall = !isCompatMode && __LEX_ENABLE_ANTIWALL_SHIELD && !isModDisabled('antiwall');

    // ─────────────────────────────────────────────────────────────────
    // 0. SCRIPTLETS DE EMULACIÓN ANTI-ANTI-ADBLOCK & NOTIFICATION DEFUSER
    // ─────────────────────────────────────────────────────────────────
    if (activeAntiWall) {
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
        } catch(e) {}
    }


    // ─────────────────────────────────────────────────────────────────
    // 0.1 ESCUDO COSMÉTICO (Ocultamiento de banners, iframes y sliders publicitarios)
    // ─────────────────────────────────────────────────────────────────
    try {
        const applyCosmeticStyles = function() {
            try {
                if (!activeCosmetic) return;
                if (document.getElementById('__lex_cosmetic_shield')) return;
                const target = document.head || document.documentElement;
                if (!target) return;
                const style = document.createElement('style');
                style.id = '__lex_cosmetic_shield';

                // Selectores de anuncios por 'palabra': el token debe empezar la clase o ir tras espacio, '-' o '_'.
                // Así 'ad-box' coincide con 'ad-box' y 'top-ad-box', pero NO con 'download-box' ni 'upload-zone'.
                const PLAYER_SAFE = `:not(:has(video)):not([class*='player']):not([id*='player']):not(canvas):not([role='application']):not([contenteditable='true'])`;
                const PLAYER_SAFE_LOOSE = `:not(:has(video)):not([class*='player']):not(canvas):not([role='application']):not([contenteditable='true'])`;
                const adWord = (token, suffix) => {
                    const out = [];
                    for (const attr of ['class', 'id']) {
                        out.push(`div[${attr}^='${token}']${suffix}`);
                        for (const sep of [' ', '-', '_']) {
                            out.push(`div[${attr}*='${sep}${token}']${suffix}`);
                        }
                    }
                    return out.join(',\n');
                };
                const generatedAdSelectors = [
                    ['ad-slot', ''], ['ad-zone', ''], ['ad-box', ''], ['ad_box', ''],
                    ['ads-holder', ''], ['ad-block', ''], ['ad-placement', ''],
                    ['ad-banner', PLAYER_SAFE_LOOSE], ['ad_banner', PLAYER_SAFE_LOOSE],
                    ['ads-', PLAYER_SAFE], ['ad-', PLAYER_SAFE], ['adv-', PLAYER_SAFE]
                ].map(function(p) { return adWord(p[0], p[1]); }).join(',\n');

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
                    .banner-wrapper,
                    div[class*='banner-container'],
                    div[class*='banner-wrapper'],
                    ${generatedAdSelectors},
                    div[class*='exo-slider'],
                    div[id*='exo-slider'],
                    div[class*='pr_advertising_'],
                    div[id*='pr_advertising_'],
                    .adsbytrafficjunky,
                    .adsbyexoclick,
                    .adsbyjuicyads,
                    .ad-container:not(#player):not([class*='player']):not(:has(video)) {
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

    if (activePopups) {
        try {
            const originalOpen = window.open;
            window.open = function(url, target, features) {
                const urlStr = (typeof url === 'string') ? url : (url && url.toString ? url.toString() : '');
                const isInsideFrame = (window.self !== window.top);
                const isSuspicious = isAdOrSuspiciousUrl(urlStr);

                // 1. Si es detectado como anuncio explícito
                if (isSuspicious) {
                    if (window.chrome && window.chrome.webview) {
                        window.chrome.webview.postMessage({ type: 'ad_blocked', category: 'popup', url: urlStr, reason: 'window_open_ad_pattern' });
                    }
                    return createDecoyPopup();
                }

                // 2. Si proviene de un iframe sin gesto directo del usuario, hacia about:blank, o popup cross-origin desde un embed de video
                if (isInsideFrame) {
                    const hasUserGesture = !!(navigator.userActivation && navigator.userActivation.isActive);
                    const isCrossDomain = urlStr && !urlStr.startsWith(window.location.origin) && !trustedOAuthRegex.test(urlStr);
                    const isBlankPop = !urlStr || urlStr === 'about:blank';
                    if (isBlankPop || (isCrossDomain && (!hasUserGesture || isVideoEmbedContext))) {
                        if (window.chrome && window.chrome.webview) {
                            window.chrome.webview.postMessage({ type: 'ad_blocked', category: 'popup', url: urlStr || 'about:blank', reason: isVideoEmbedContext ? 'window_open_embed_popunder' : 'window_open_iframe_popunder' });
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
    }

    if (!activeOverlays) return;

    // 1.2 Defusor de clics simulados en enlaces fantasma (creados en memoria sin contenido visual)
    try {
        const originalAnchorClick = HTMLAnchorElement.prototype.click;
        HTMLAnchorElement.prototype.click = function() {
            const href = this.href || '';
            const isDetached = !document.contains(this);
            const text = (this.innerText || this.textContent || '').trim();
            if ((isDetached || !text) && (this.target === '_blank' || adDomainsRegex.test(href))) {
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage({ type: 'ad_blocked', category: 'overlay', url: href, reason: 'phantom_anchor' });
                }
                return; // Neutralizado
            }
            return originalAnchorClick.apply(this, arguments);
        };
    } catch(e) {}

    // Elementos que JAMÁS son una capa publicitaria trampa: lienzos (canvas), gráficos vectoriales,
    // objetos embebidos y áreas de edición/aplicación (Miro, Figma, Excalidraw, Google Maps, editores...).
    const NEVER_OVERLAY_TAGS = ['CANVAS', 'SVG', 'OBJECT', 'EMBED'];
    const isNeverOverlay = (el) => {
        try {
            if (!el) return false;
            const tag = String(el.tagName || '').toUpperCase();
            if (NEVER_OVERLAY_TAGS.includes(tag)) return true;
            if (el.isContentEditable) return true;
            const role = el.getAttribute ? (el.getAttribute('role') || '').toLowerCase() : '';
            if (role === 'application') return true;
            if (el.querySelector && el.querySelector('canvas')) return true;
        } catch(_) {}
        return false;
    };

    // 1.3 Verificador de Controles Legítimos y Elementos con Significado Visual
    const isControlOrMeaningful = (el) => {
        if (!el) return false;
        const tag = String(el.tagName || '').toUpperCase();
        if (['BUTTON', 'INPUT', 'SELECT', 'TEXTAREA', 'LABEL', 'VIDEO', 'AUDIO'].includes(tag)) return true;
        if (tag === 'SVG' || el.closest('svg')) return true;
        if (isNeverOverlay(el)) return true;

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

    // Evidencia de capa trampa: debajo de la capa (en el mismo punto) debe haber un reproductor,
    // un video o un iframe. Un lienzo o panel transparente sin nada de eso debajo NO es una trampa publicitaria.
    const PLAYER_HINT_REGEX = /(player|video|plyr|jwplayer|jw-|vjs|video-js|fluid|embed|stream)/i;
    const hasTrapTargetBeneath = (el, px, py) => {
        try {
            let x = px;
            let y = py;
            if (typeof x !== 'number' || typeof y !== 'number') {
                const r = el.getBoundingClientRect();
                x = r.left + r.width / 2;
                y = r.top + r.height / 2;
            }
            x = Math.min(Math.max(x, 0), Math.max(0, window.innerWidth - 1));
            y = Math.min(Math.max(y, 0), Math.max(0, window.innerHeight - 1));

            const stack = document.elementsFromPoint(x, y);
            const startIdx = stack.indexOf(el) + 1;
            for (let i = startIdx; i < stack.length; i++) {
                const s = stack[i];
                if (!s || s === el || s === document.body || s === document.documentElement) continue;
                if (s.contains(el)) continue; // ancestros de la capa: no cuentan como 'debajo'
                const t = String(s.tagName || '').toUpperCase();
                if (t === 'VIDEO' || t === 'IFRAME') return true;
                const idClass = ((s.id || '') + ' ' + (typeof s.className === 'string' ? s.className : '')).toLowerCase();
                if (idClass && PLAYER_HINT_REGEX.test(idClass)) return true;
            }
        } catch(_) {}
        return false;
    };

    // 1.4 Clasificador Heurístico Fino de Capas Fantasma / Overlays Trampa sobre Video
    // Devuelve: null (no es trampa) | 'hard' (anuncio explícito o enlace fantasma: ocultar y eliminar)
    //           | 'soft' (capa transparente sobre un reproductor: solo pointer-events:none, sin borrar el nodo)
    const classifyGhost = (el, px, py) => {
        if (!el || el === document.body || el === document.documentElement) return null;
        const tag = String(el.tagName || '').toUpperCase();
        if (['VIDEO', 'AUDIO', 'BUTTON', 'INPUT', 'SELECT', 'TEXTAREA', 'FORM', 'DIALOG'].includes(tag)) return null;

        // Lienzos, SVG, objetos embebidos y áreas de edición/aplicación: nunca son trampas
        if (isNeverOverlay(el)) return null;

        // NUNCA borrar el elemento raíz de frameworks SPA
        if (el.id === 'root' || el.id === 'app' || el.id === '__next' || el.id === '__nuxt' || el.hasAttribute('data-reactroot')) return null;

        // CRÍTICO: Si el elemento CONTIENE un video o iframe dentro de su árbol, NO borrarlo (es el contenedor del reproductor)
        if (el.querySelector('video, audio, iframe')) return null;

        // Si contiene controles interactivos legítimos (botones, sliders, svgs, etc.)
        if (el.querySelector('button, input, select, textarea, svg, [class*=control], [class*=btn]')) return null;

        // Si tiene texto visible legítimo
        const text = (el.innerText || el.textContent || '').trim();
        const style = window.getComputedStyle(el);
        const opacity = parseFloat(style.opacity);
        if (text.length > 0 && style.visibility !== 'hidden' && opacity > 0.3) return null;

        // Verificar estilo visual de trampa (posicionado absoluto/fijo y transparente)
        const isPositioned = (style.position === 'fixed' || style.position === 'absolute');
        const bg = style.backgroundColor || '';
        const isZeroAlpha = (!bg || bg === 'transparent' || (bg.includes('rgba(') && parseFloat(bg.split(',')[3] || '1') <= 0.08));
        const isTransparent = (opacity <= 0.08 || isZeroAlpha || style.visibility === 'hidden');

        // Nombre explícito de anuncio trampa
        const idClass = ((el.id || '') + ' ' + (el.className || '')).toLowerCase();
        const isExplicitAdName = /(first-click-ad|click-ad|ad-overlay|pop-overlay|popup-overlay|interads|pop-under|banner-overlay)/.test(idClass);

        if (!isPositioned && !isExplicitAdName) return null;
        if (!isTransparent && !isExplicitAdName) return null;

        // Caso 1: Enlace fantasma (<a> transparente o con target=_blank o apuntando a ad/cross-origin)
        const anchor = (tag === 'A') ? el : el.querySelector('a');
        if (anchor) {
            const href = (anchor.href || '').toLowerCase();
            const isAdLink = adDomainsRegex.test(href) || isAdOrSuspiciousUrl(href) || anchor.target === '_blank';
            if (isAdLink || !text) return 'hard';
        }

        // Anuncio con nombre explícito
        if (isExplicitAdName) return 'hard';

        // Caso 2: Capa transparente grande con z-index alto SOBRE un reproductor/video/iframe.
        // Se exige evidencia de que hay algo debajo que la capa está tapando.
        const zIndex = parseInt(style.zIndex, 10);
        const rect = el.getBoundingClientRect();
        const isLargeCoverage = (rect.width > 200 && rect.height > 150);

        if (isLargeCoverage && (zIndex >= 10 || isNaN(zIndex)) && isTransparent && !text) {
            if (hasTrapTargetBeneath(el, px, py)) return 'soft';
        }

        return null;
    };

    const isGhostOverlay = (el, px, py) => classifyGhost(el, px, py) !== null;

    const neutralizeOverlayElement = (el, mode) => {
        if (!el) return;
        try {
            // Siempre: dejar de capturar clics para que lleguen al elemento de debajo
            el.style.setProperty('pointer-events', 'none', 'important');
            // Modo suave: no ocultar ni borrar el nodo (reversible y sin riesgo en falsos positivos)
            if (mode === 'soft') return;

            el.style.setProperty('display', 'none', 'important');
            el.style.setProperty('visibility', 'hidden', 'important');
            if (el.tagName === 'A' || el.children.length === 0) {
                el.remove();
            }
        } catch(_) {}
    };

    // 1.5 Interceptor en Fase de Captura Pre-Clic (Neutralización inmediata + Click Pass-Through)
    const handleCaptureClick = (e) => {
        let target = e.target;
        if (!target) return;

        // Si es un control legítimo, un lienzo o contiene texto/iconos reales, permitir inmediatamente
        if (isControlOrMeaningful(target)) {
            return;
        }

        let ghost = null;
        let ghostMode = null;
        let curr = target;
        for (let i = 0; i < 5 && curr && curr !== document.body; i++) {
            const mode = classifyGhost(curr, e.clientX, e.clientY);
            if (mode) {
                ghost = curr;
                ghostMode = mode;
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
            const elDesc = `${tag}${id}${cls} [${Math.round(r.width)}x${Math.round(r.height)}, z:${zi}, ${ghostMode}]`;

            neutralizeOverlayElement(ghost, ghostMode);

            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage({
                    type: 'ad_blocked',
                    category: 'overlay',
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

    const isKnownMediaSite = ends('dailymotion.com') ||
                             ends('youtube.com') ||
                             ends('twitch.tv') ||
                             ends('vimeo.com');

    if (!isKnownMediaSite) {
        window.addEventListener('click', handleCaptureClick, true);
        window.addEventListener('pointerdown', handleCaptureClick, true);
    }

    // 1.6 Limpieza proactiva de capas trampas en el DOM (restringido estrictamente a ads explícitos, anchors fantasma y hermanos sobre videos)
    const clearClickjackOverlays = () => {
        if (isKnownMediaSite) return;
        try {
            // 1. Elementos con nombres explícitos de anuncio
            const explicitAds = document.querySelectorAll('[id*=first-click-ad], [class*=first-click-ad], [id*=click-ad], [class*=click-ad], [class*=ad-overlay], [class*=pop-overlay], [class*=popup-overlay], [class*=interads], [id*=popunder], [class*=popunder]');
            for (let i = 0; i < explicitAds.length; i++) {
                const el = explicitAds[i];
                const mode = classifyGhost(el);
                if (mode) {
                    const r = el.getBoundingClientRect ? el.getBoundingClientRect() : { width: 0, height: 0 };
                    const tag = el.tagName || '';
                    const id = el.id ? ('#' + el.id) : '';
                    const cls = (el.className && typeof el.className === 'string') ? ('.' + el.className.trim().split(/\s+/).join('.')) : '';
                    neutralizeOverlayElement(el, mode);
                    if (window.chrome && window.chrome.webview) {
                        window.chrome.webview.postMessage({
                            type: 'ad_blocked',
                            category: 'overlay',
                            reason: `ghost_overlay_explicit -> ${tag}${id}${cls} [${Math.round(r.width)}x${Math.round(r.height)}]`
                        });
                    }
                }
            }

            // 2. Enlaces transparentes con target=_blank
            const ghostAnchors = document.querySelectorAll('a[target=_blank]');
            for (let i = 0; i < ghostAnchors.length; i++) {
                const a = ghostAnchors[i];
                const mode = classifyGhost(a);
                if (mode) {
                    const r = a.getBoundingClientRect ? a.getBoundingClientRect() : { width: 0, height: 0 };
                    neutralizeOverlayElement(a, mode);
                    if (window.chrome && window.chrome.webview) {
                        window.chrome.webview.postMessage({
                            type: 'ad_blocked',
                            category: 'overlay',
                            reason: `ghost_anchor_scanned -> A [${Math.round(r.width)}x${Math.round(r.height)}]`
                        });
                    }
                }
            }

            // 3. Capas trampas colocadas como hermanas sobre contenedores de video o iframes
            if (!isKnownMediaSite) {
                const players = document.querySelectorAll('[class*=player], [id*=player], iframe, video');
                for (let i = 0; i < players.length; i++) {
                    const p = players[i];
                    const siblings = p.parentElement ? p.parentElement.children : [];
                    for (let j = 0; j < siblings.length; j++) {
                        const sib = siblings[j];
                        if (sib === p) continue;
                        const mode = classifyGhost(sib);
                        if (mode) {
                            const r = sib.getBoundingClientRect ? sib.getBoundingClientRect() : { width: 0, height: 0 };
                            neutralizeOverlayElement(sib, mode);
                            if (window.chrome && window.chrome.webview) {
                                window.chrome.webview.postMessage({
                                    type: 'ad_blocked',
                                    category: 'overlay',
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

    let lastClearAt = 0;
    const runClear = () => {
        if (document.hidden) return;
        const now = Date.now();
        if (now - lastClearAt < 500) return;
        lastClearAt = now;
        clearClickjackOverlays();
    };
    onLoaded(() => {
        setTimeout(runClear, 600);
        if (isVideoEmbedContext || document.querySelector('video, iframe')) {
            setTimeout(runClear, 3000);
            setTimeout(runClear, 8000);
        }
    });
    document.addEventListener('visibilitychange', () => {
        if (!document.hidden && (isVideoEmbedContext || document.querySelector('video, iframe'))) {
            runClear();
        }
    });
})();
";

    /// <summary>
    /// Desarmador pasivo y acotado para reproductores de video genéricos (JW Player / Fluid Player / Video.js).
    /// Excluye estrictamente YouTube, Google y Dailymotion, actúa únicamente dentro del contenedor del reproductor
    /// cuando un anuncio está activo, y restaura la velocidad y el audio originales al finalizar el anuncio.
    /// Solo activo si EnableVideoPlayerDefuser es true.
    /// </summary>
    public const string VideoPlayerDefuserScript = @"
(function() {
    if (window.__lex_video_defuser_injected) return;
    window.__lex_video_defuser_injected = true;

    const __LEX_ENABLE_VIDEO_DEFUSER = true;
    const __LEX_TRUSTED_HOSTS = [];

    const h = (location.hostname || '').toLowerCase();
    const isTopWindow = (window.top === window.self);
    const ends = (d) => h === d || h.endsWith('.' + d);
    const isEmbedFrame = !isTopWindow && (
        /(streamwish|wishfast|wishembed|vidhide|streamtape|dood|d000d|ds2play|filelions|lulustream|luluvdo|mp4upload|uqload|voe\.sx|mixdrop|fastream|hglink|dhcplay|fembed|upstream|vidoza|wolfstream|megacloud|rabbitstream|vidplay|yourupload)/i.test(h) ||
        /^\/(e|v|embed|player)(\/|$|-)/i.test(window.location.pathname || '') ||
        /\/(videoembed|video_ext)/i.test(window.location.pathname || '')
    );

    if (!__LEX_ENABLE_VIDEO_DEFUSER && !isEmbedFrame) return;

    if (ends('youtube.com') || ends('youtu.be') || ends('youtube-nocookie.com') ||
        ends('google.com') || ends('gstatic.com') || ends('googleusercontent.com') ||
        (__LEX_TRUSTED_HOSTS && __LEX_TRUSTED_HOSTS.some(ends))) {
        return;
    }

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
