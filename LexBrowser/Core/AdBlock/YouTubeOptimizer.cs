using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LexBrowser.Core.AdBlock;

/// <summary>
/// Adaptador de bloqueo de anuncios para YouTube basado en la estrategia de Brave
/// (<c>adblock-rust-master/data/brave/brave-main-list.txt</c> y reglas de uBlock).
///
/// Estrategia:
/// 1. Aislamiento total de dominio: fuera de YouTube termina de inmediato sin tocar nada.
/// 2. Poda de JSON publicitario (<c>adPlacements</c>, <c>playerAds</c>, <c>adSlots</c>,
///    <c>adBreakHeartbeatParams</c> y Shorts marcados con <c>isAd</c>) en
///    <c>ytInitialPlayerResponse</c>, <c>playerResponse</c>, <c>fetch</c> y <c>XMLHttpRequest</c>.
/// 3. Prevención de bypass DOM (<c>appendChild</c>/<c>insertBefore</c> en iframes auxiliares).
/// 4. Neutralización del temporizador anti-adblock de 17 s y de <c>onAbnormalityDetected</c>.
/// 5. Cero manipulación del reproductor: nunca altera <c>playbackRate</c>, volumen ni visibilidad.
///    Solo pulsa "Saltar" si un anuncio residual llega a mostrarse.
/// 6. CSS cosmético seguro (sin ocultar <c>#player-ads</c>, exceptuado en Brave).
/// </summary>
public static class YouTubeOptimizer
{
    private static readonly string SessionGuardToken = "__lex_yt_" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// Script inyectado en <c>document_start</c> para páginas de YouTube.
    /// </summary>
    public static string InjectedScript { get; } = BuildInjectedScript();

    private static string BuildInjectedScript()
    {
        string cssLiteral = JsonSerializer.Serialize(MediaShieldRules.BuildCosmeticCss());
        return ScriptTemplate
            .Replace("__GUARD_TOKEN__", SessionGuardToken)
            .Replace("__YT_COSMETIC_CSS__", cssLiteral);
    }

    /// <summary>
    /// Poda determinista en C# de cargas JSON del reproductor de YouTube: elimina
    /// <c>adPlacements</c>, <c>playerAds</c>, <c>adSlots</c>, <c>adBreakHeartbeatParams</c>
    /// (en raíz, en <c>playerResponse</c> anidado y en listas) y anuncios de Shorts.
    /// </summary>
    public static string CleanPlayerPayloadJson(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return rawJson;
        try
        {
            var node = JsonNode.Parse(rawJson);
            if (node == null) return rawJson;
            CleanJsonNode(node, 0);
            return node.ToJsonString();
        }
        catch
        {
            return rawJson;
        }
    }

    private static void CleanJsonNode(JsonNode? node, int depth)
    {
        if (node == null || depth > 4) return;

        if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is JsonObject itemObj &&
                    itemObj.TryGetPropertyValue("playerResponse", out var nestedPr) &&
                    nestedPr != null)
                {
                    CleanJsonNode(nestedPr, depth + 1);
                }
            }
            return;
        }

        if (node is not JsonObject obj) return;

        obj.Remove("adPlacements");
        obj.Remove("playerAds");
        obj.Remove("adSlots");
        obj.Remove("adBreakHeartbeatParams");

        if (obj.TryGetPropertyValue("playerResponse", out var pr) && pr != null)
        {
            CleanJsonNode(pr, depth + 1);
        }

        PruneAdEntries(obj["entries"] as JsonArray);

        if (obj.TryGetPropertyValue("reelWatchSequenceResponse", out var rwsNode) && rwsNode is JsonObject rwsObj)
        {
            PruneAdEntries(rwsObj["entries"] as JsonArray);
        }
    }

    private static void PruneAdEntries(JsonArray? entriesArr)
    {
        if (entriesArr == null) return;
        for (int i = entriesArr.Count - 1; i >= 0; i--)
        {
            if (entriesArr[i] is JsonObject entryObj)
            {
                bool isAd = entryObj["command"]?["reelWatchEndpoint"]?["adClientParams"]?["isAd"]?.GetValue<bool>() == true;
                if (isAd)
                {
                    entriesArr.RemoveAt(i);
                }
            }
        }
    }

    private const string ScriptTemplate = @"
(function() {
    'use strict';

    // 1. Guardia de sesión no enumerable (sin bandera global fija detectable)
    const GUARD_KEY = '__GUARD_TOKEN__';
    try {
        if (Object.prototype.hasOwnProperty.call(window, GUARD_KEY)) return;
        Object.defineProperty(window, GUARD_KEY, {
            value: true,
            enumerable: false,
            configurable: false,
            writable: false
        });
    } catch (_) {
        return;
    }

    // 2. Exención total fuera de YouTube
    const hostname = (window.location && window.location.hostname ? window.location.hostname : '').toLowerCase();
    const isYouTube = hostname === 'youtube.com' ||
                      hostname.endsWith('.youtube.com') ||
                      hostname === 'youtu.be' ||
                      hostname === 'youtube-nocookie.com' ||
                      hostname.endsWith('.youtube-nocookie.com') ||
                      hostname === 'youtubekids.com' ||
                      hostname.endsWith('.youtubekids.com');

    if (!isYouTube) return;

    const reportBlocked = function(reason) {
        try {
            if (window.chrome && window.chrome.webview && typeof window.chrome.webview.postMessage === 'function') {
                window.chrome.webview.postMessage({
                    type: 'ad_blocked',
                    category: 'adapter',
                    url: (window.location && window.location.href) || '',
                    reason: reason
                });
            }
        } catch (_) {}
    };

    // 3. Poda de claves publicitarias y Shorts patrocinados (estrategia json-prune de Brave)
    const AD_KEYS = ['adPlacements', 'playerAds', 'adSlots', 'adBreakHeartbeatParams'];

    const isAdEntry = function(entry) {
        return !!(entry &&
                  entry.command &&
                  entry.command.reelWatchEndpoint &&
                  entry.command.reelWatchEndpoint.adClientParams &&
                  entry.command.reelWatchEndpoint.adClientParams.isAd === true);
    };

    const cleanPlayerPayload = function(data, depth) {
        if (!data || typeof data !== 'object') return data;
        depth = depth || 0;
        if (depth > 4) return data;

        try {
            if (Array.isArray(data)) {
                for (let i = 0; i < data.length; i++) {
                    const item = data[i];
                    if (item && typeof item === 'object' && item.playerResponse) {
                        cleanPlayerPayload(item.playerResponse, depth + 1);
                    }
                }
                return data;
            }

            let stripped = 0;
            for (let i = 0; i < AD_KEYS.length; i++) {
                const key = AD_KEYS[i];
                if (Object.prototype.hasOwnProperty.call(data, key)) {
                    delete data[key];
                    stripped++;
                }
            }

            if (data.playerResponse && typeof data.playerResponse === 'object') {
                cleanPlayerPayload(data.playerResponse, depth + 1);
            }

            if (Array.isArray(data.entries)) {
                const cleanEntries = data.entries.filter(function(e) { return !isAdEntry(e); });
                if (cleanEntries.length !== data.entries.length) {
                    stripped += (data.entries.length - cleanEntries.length);
                    data.entries = cleanEntries;
                }
            }

            if (data.reelWatchSequenceResponse && Array.isArray(data.reelWatchSequenceResponse.entries)) {
                const cleanSeq = data.reelWatchSequenceResponse.entries.filter(function(e) { return !isAdEntry(e); });
                if (cleanSeq.length !== data.reelWatchSequenceResponse.entries.length) {
                    stripped += (data.reelWatchSequenceResponse.entries.length - cleanSeq.length);
                    data.reelWatchSequenceResponse.entries = cleanSeq;
                }
            }

            if (stripped > 0) {
                reportBlocked('yt_json_pruned:' + stripped);
            }
        } catch (_) {}

        return data;
    };

    // 4. Trampas en ytInitialPlayerResponse y playerResponse
    const trapPlayerProperty = function(targetObj, propName) {
        if (!targetObj) return;
        try {
            const desc = Object.getOwnPropertyDescriptor(targetObj, propName);
            if (desc && !desc.configurable) return;

            let stored = targetObj[propName];
            if (stored && typeof stored === 'object') {
                cleanPlayerPayload(stored, 0);
            }

            Object.defineProperty(targetObj, propName, {
                configurable: true,
                enumerable: true,
                get: function() {
                    return stored;
                },
                set: function(nextVal) {
                    if (nextVal && typeof nextVal === 'object') {
                        stored = cleanPlayerPayload(nextVal, 0);
                    } else {
                        stored = nextVal;
                    }
                }
            });
        } catch (_) {}
    };

    trapPlayerProperty(window, 'ytInitialPlayerResponse');
    trapPlayerProperty(window, 'playerResponse');

    // Endpoints de carga de datos del reproductor (excluye /youtubei/v1/next por tamaño y rendimiento)
    const shouldInspectUrl = function(u) {
        if (typeof u !== 'string') return false;
        return u.indexOf('/youtubei/v1/player') !== -1 ||
               u.indexOf('/youtubei/v1/get_watch') !== -1 ||
               u.indexOf('/youtubei/v1/reel/reel_item_watch') !== -1 ||
               u.indexOf('/youtubei/v1/reel/reel_watch_sequence') !== -1 ||
               u.indexOf('/watch?') !== -1 ||
               u.indexOf('/playlist?') !== -1;
    };

    const hasAdFields = function(s) {
        return typeof s === 'string' && (
            s.indexOf('adPlacements') !== -1 ||
            s.indexOf('playerAds') !== -1 ||
            s.indexOf('adSlots') !== -1 ||
            s.indexOf('adBreakHeartbeatParams') !== -1 ||
            s.indexOf('adClientParams') !== -1
        );
    };

    // 5. Interceptación de fetch (solo reconstruye la respuesta cuando contiene claves publicitarias)
    const origFetch = window.fetch;
    if (typeof origFetch === 'function') {
        window.fetch = function(input) {
            const url = (typeof input === 'string')
                ? input
                : ((input && typeof input.url === 'string') ? input.url : '');
            const pending = origFetch.apply(this, arguments);
            if (!shouldInspectUrl(url)) return pending;

            return pending.then(function(res) {
                if (!res || !res.ok) return res;
                return res.clone().text().then(function(text) {
                    if (!hasAdFields(text)) return res;
                    let json;
                    try {
                        json = JSON.parse(text);
                    } catch (_) {
                        return res;
                    }

                    const cleanBody = JSON.stringify(cleanPlayerPayload(json, 0));
                    const headers = new Headers(res.headers);
                    headers.delete('content-length');
                    headers.delete('content-encoding');

                    const synthetic = new Response(cleanBody, {
                        status: res.status,
                        statusText: res.statusText,
                        headers: headers
                    });
                    try {
                        Object.defineProperty(synthetic, 'url', { value: res.url, configurable: true });
                        Object.defineProperty(synthetic, 'redirected', { value: res.redirected, configurable: true });
                        Object.defineProperty(synthetic, 'type', { value: res.type, configurable: true });
                    } catch (_) {}
                    return synthetic;
                }).catch(function() {
                    return res;
                });
            });
        };
    }

    // 6. Interceptación de XMLHttpRequest (cachea por respuesta limpia en readyState === 4)
    if (window.XMLHttpRequest && window.XMLHttpRequest.prototype) {
        const xhrProto = window.XMLHttpRequest.prototype;
        const origOpen = xhrProto.open;
        if (typeof origOpen === 'function') {
            xhrProto.open = function(method, url) {
                try {
                    this.__lex_inspect = shouldInspectUrl(typeof url === 'string' ? url : String(url || ''));
                    this.__lex_cached_raw = null;
                    this.__lex_cached_clean = null;
                } catch (_) {}
                return origOpen.apply(this, arguments);
            };
        }

        const rtDesc = Object.getOwnPropertyDescriptor(xhrProto, 'responseText');
        if (rtDesc && typeof rtDesc.get === 'function' && rtDesc.configurable) {
            Object.defineProperty(xhrProto, 'responseText', {
                configurable: true,
                enumerable: rtDesc.enumerable,
                get: function() {
                    const raw = rtDesc.get.call(this);
                    if (this.readyState === 4 && this.__lex_inspect && typeof raw === 'string' && hasAdFields(raw)) {
                        if (this.__lex_cached_raw === raw && typeof this.__lex_cached_clean === 'string') {
                            return this.__lex_cached_clean;
                        }
                        try {
                            const parsed = JSON.parse(raw);
                            const cleaned = JSON.stringify(cleanPlayerPayload(parsed, 0));
                            this.__lex_cached_raw = raw;
                            this.__lex_cached_clean = cleaned;
                            return cleaned;
                        } catch (_) {}
                    }
                    return raw;
                }
            });
        }

        const resDesc = Object.getOwnPropertyDescriptor(xhrProto, 'response');
        if (resDesc && typeof resDesc.get === 'function' && resDesc.configurable) {
            Object.defineProperty(xhrProto, 'response', {
                configurable: true,
                enumerable: resDesc.enumerable,
                get: function() {
                    const raw = resDesc.get.call(this);
                    if (this.readyState === 4 && this.__lex_inspect) {
                        if (typeof raw === 'string' && hasAdFields(raw)) {
                            if (this.__lex_cached_raw === raw && typeof this.__lex_cached_clean === 'string') {
                                return this.__lex_cached_clean;
                            }
                            try {
                                const parsed = JSON.parse(raw);
                                const cleaned = JSON.stringify(cleanPlayerPayload(parsed, 0));
                                this.__lex_cached_raw = raw;
                                this.__lex_cached_clean = cleaned;
                                return cleaned;
                            } catch (_) {}
                        } else if (raw && typeof raw === 'object' &&
                                   !(raw instanceof ArrayBuffer) &&
                                   !(typeof Blob !== 'undefined' && raw instanceof Blob)) {
                            try {
                                cleanPlayerPayload(raw, 0);
                            } catch (_) {}
                        }
                    }
                    return raw;
                }
            });
        }
    }

    // 7. Prevención de bypass DOM mediante iframes auxiliares (trusted-prevent-dom-bypass)
    const protectFrameWindow = function(node) {
        try {
            if (!node || node.nodeName !== 'IFRAME') return;
            const cw = node.contentWindow;
            if (!cw || cw === window) return;
            const src = (node.getAttribute && node.getAttribute('src')) || '';
            if (src && src !== 'about:blank' && src.indexOf('javascript:') !== 0) return;

            try { if (typeof window.fetch === 'function') cw.fetch = window.fetch; } catch (_) {}
            try { if (typeof window.Request === 'function') cw.Request = window.Request; } catch (_) {}
            try { if (typeof window.XMLHttpRequest === 'function') cw.XMLHttpRequest = window.XMLHttpRequest; } catch (_) {}
            try { trapPlayerProperty(cw, 'ytInitialPlayerResponse'); } catch (_) {}
            try { trapPlayerProperty(cw, 'playerResponse'); } catch (_) {}
        } catch (_) {}
    };

    if (typeof Node !== 'undefined' && Node.prototype) {
        const origAppendChild = Node.prototype.appendChild;
        if (typeof origAppendChild === 'function') {
            Node.prototype.appendChild = function(child) {
                const res = origAppendChild.apply(this, arguments);
                protectFrameWindow(child);
                return res;
            };
        }

        const origInsertBefore = Node.prototype.insertBefore;
        if (typeof origInsertBefore === 'function') {
            Node.prototype.insertBefore = function(newNode) {
                const res = origInsertBefore.apply(this, arguments);
                protectFrameWindow(newNode);
                return res;
            };
        }
    }

    // 8. Neutralización de temporizador anti-adblock (17 s, [native code]) y onAbnormalityDetected
    try {
        const origSetTimeout = window.setTimeout;
        if (typeof origSetTimeout === 'function') {
            window.setTimeout = function(fn, delay) {
                if (delay === 17000 && typeof fn === 'function') {
                    try {
                        if (Function.prototype.toString.call(fn).indexOf('[native code]') !== -1) {
                            const args = Array.prototype.slice.call(arguments);
                            args[1] = 1;
                            return origSetTimeout.apply(this, args);
                        }
                    } catch (_) {}
                }
                return origSetTimeout.apply(this, arguments);
            };
        }
    } catch (_) {}

    try {
        if (typeof Promise !== 'undefined' && Promise.prototype && typeof Promise.prototype.then === 'function') {
            const origThen = Promise.prototype.then;
            Promise.prototype.then = function(onFulfilled, onRejected) {
                try {
                    if (typeof onFulfilled === 'function') {
                        const fSrc = Function.prototype.toString.call(onFulfilled);
                        if (fSrc.indexOf('onAbnormalityDetected') !== -1) {
                            onFulfilled = function() {};
                        }
                    }
                    if (typeof onRejected === 'function') {
                        const rSrc = Function.prototype.toString.call(onRejected);
                        if (rSrc.indexOf('onAbnormalityDetected') !== -1) {
                            onRejected = function() {};
                        }
                    }
                } catch (_) {}
                return origThen.call(this, onFulfilled, onRejected);
            };
        }
    } catch (_) {}

    // 9. Respaldo pasivo en reproductor (solo botón Saltar y cierre de aviso anti-bloqueo; nunca toca velocidad/volumen/opacidad)
    const SKIP_SELECTORS = [
        '.ytp-skip-ad-button',
        '.ytp-ad-skip-button',
        '.ytp-ad-skip-button-modern',
        '.ytp-ad-skip-button-slot button'
    ];

    const findSkipButton = function() {
        if (!document || typeof document.querySelectorAll !== 'function') return null;
        for (let i = 0; i < SKIP_SELECTORS.length; i++) {
            const buttons = document.querySelectorAll(SKIP_SELECTORS[i]);
            for (let j = 0; j < buttons.length; j++) {
                const btn = buttons[j];
                if (!btn || btn.disabled) continue;
                try {
                    const style = typeof getComputedStyle === 'function' ? getComputedStyle(btn) : null;
                    if (style && (style.display === 'none' || style.visibility === 'hidden')) continue;
                } catch (_) {}
                if (btn.offsetParent !== null || (btn.getClientRects && btn.getClientRects().length > 0)) {
                    return btn;
                }
            }
        }
        return null;
    };

    let lastSkipClickAt = 0;
    const trySkipResidualAd = function() {
        const player = (document.getElementById && document.getElementById('movie_player')) ||
                       (document.querySelector && document.querySelector('.html5-video-player'));
        if (!player || !player.classList) return;
        if (!player.classList.contains('ad-showing') && !player.classList.contains('ad-interrupting')) return;

        const btn = findSkipButton();
        if (btn && (Date.now() - lastSkipClickAt > 400)) {
            lastSkipClickAt = Date.now();
            try {
                btn.click();
                reportBlocked('yt_skip_button_clicked');
            } catch (_) {}
        }
    };

    const clearEnforcement = function() {
        try {
            if (!document || typeof document.querySelector !== 'function') return;
            const msg = document.querySelector('ytd-enforcement-message-view-model');
            if (!msg) return;

            const closeBtn = msg.querySelector ? msg.querySelector('#dismiss-button button, button[aria-label]') : null;
            if (closeBtn) {
                try { closeBtn.click(); } catch (_) {}
            }

            const dialog = msg.closest ? msg.closest('tp-yt-paper-dialog') : null;
            if (dialog && dialog.style) {
                dialog.style.display = 'none';
                try { if (typeof dialog.close === 'function') dialog.close(); } catch (_) {}
            } else if (msg.style) {
                msg.style.display = 'none';
            }

            const backdrop = document.querySelector('tp-yt-iron-overlay-backdrop.opened');
            if (backdrop && backdrop.style) {
                backdrop.style.display = 'none';
                if (backdrop.classList) backdrop.classList.remove('opened');
            }

            reportBlocked('yt_enforcement_dismissed');
        } catch (_) {}
    };

    // 10. CSS cosmético declarativo de MediaShieldRules (sin selectores exceptuados por Brave)
    const YT_COSMETIC_CSS = __YT_COSMETIC_CSS__;
    const attachYtStyle = function() {
        try {
            if (!document || typeof document.createElement !== 'function') return;
            if (document.getElementById && document.getElementById('lex-yt-cosmetic-style')) return;

            const style = document.createElement('style');
            style.id = 'lex-yt-cosmetic-style';
            style.textContent = YT_COSMETIC_CSS;

            if (document.head) {
                document.head.appendChild(style);
            } else if (document.documentElement) {
                document.documentElement.appendChild(style);
            }
        } catch (_) {}
    };

    let adSkipTimer = null;
    const stopSkipPoll = function() {
        if (adSkipTimer !== null) {
            clearInterval(adSkipTimer);
            adSkipTimer = null;
        }
    };

    const syncAdState = function() {
        const player = (document.getElementById && document.getElementById('movie_player')) ||
                       (document.querySelector && document.querySelector('.html5-video-player'));
        if (!player || !player.classList) {
            stopSkipPoll();
            return;
        }
        if (player.classList.contains('ad-showing') || player.classList.contains('ad-interrupting')) {
            trySkipResidualAd();
            if (adSkipTimer === null) {
                adSkipTimer = setInterval(function() {
                    if (!player.isConnected ||
                        (!player.classList.contains('ad-showing') && !player.classList.contains('ad-interrupting'))) {
                        stopSkipPoll();
                        return;
                    }
                    trySkipResidualAd();
                }, 450);
            }
        } else {
            stopSkipPoll();
        }
    };

    const watchSlots = {
        player: { el: null, obs: null, arming: false },
        popup: { el: null, obs: null, arming: false }
    };

    const whenElement = function(selector, onFound, onTimeout) {
        if (!document || typeof document.querySelector !== 'function') {
            if (onTimeout) onTimeout();
            return null;
        }
        const immediate = document.querySelector(selector);
        if (immediate) {
            onFound(immediate);
            return null;
        }
        if (!document.documentElement || typeof MutationObserver !== 'function') {
            if (onTimeout) onTimeout();
            return null;
        }

        let tid = null;
        const finder = new MutationObserver(function() {
            const el = document.querySelector(selector);
            if (el) {
                finder.disconnect();
                if (tid !== null) clearTimeout(tid);
                onFound(el);
            }
        });
        tid = setTimeout(function() {
            finder.disconnect();
            if (onTimeout) onTimeout();
        }, 8000);
        finder.observe(document.documentElement, { childList: true, subtree: true });
        return finder;
    };

    const armSlot = function(slotName, selector, setup) {
        const slot = watchSlots[slotName];
        if (slot.el && slot.el.isConnected) return;
        if (slot.obs) { slot.obs.disconnect(); slot.obs = null; }
        slot.el = null;
        if (slot.arming) return;
        slot.arming = true;

        whenElement(selector, function(el) {
            slot.arming = false;
            slot.el = el;
            slot.obs = setup(el);
        }, function() {
            slot.arming = false;
        });
    };

    const armObservers = function() {
        attachYtStyle();

        armSlot('player', '#movie_player, .html5-video-player', function(player) {
            const obs = new MutationObserver(syncAdState);
            obs.observe(player, { attributes: true, attributeFilter: ['class'] });
            syncAdState();
            return obs;
        });

        armSlot('popup', 'ytd-popup-container', function(container) {
            let scheduled = false;
            const obs = new MutationObserver(function() {
                if (scheduled) return;
                scheduled = true;
                requestAnimationFrame(function() {
                    scheduled = false;
                    clearEnforcement();
                });
            });
            obs.observe(container, { childList: true, subtree: true });
            clearEnforcement();
            return obs;
        });
    };

    attachYtStyle();
    if (document.addEventListener) {
        ['yt-navigate-finish', 'yt-page-data-updated'].forEach(function(evt) {
            document.addEventListener(evt, function() {
                attachYtStyle();
                syncAdState();
                clearEnforcement();
                armObservers();
            }, true);
        });

        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', armObservers, { once: true });
        } else {
            armObservers();
        }
    }
})();
";
}
