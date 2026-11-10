namespace LexBrowser.Core;

/// <summary>
/// Runtime universal y ligero para utilidades multimedia y atajos de teclado en pestañas web:
/// 1. Mini-Reproductor Nativo (Picture-in-Picture libre con retorno a pestaña).
/// 2. Amplificador de Volumen Hi-Fi hasta 300% (AudioContext + DynamicsCompressor bajo demanda).
/// 3. Detector multimedia bajo demanda (0% consumo cuando el usuario no lo activa).
/// 4. Atajos de teclado globales (Ctrl+T, Ctrl+W, Ctrl+H, Ctrl+N, Ctrl+Shift+N, Ctrl+Shift+T).
/// Cumple la regla v2.0.0: NO engancha prototipos ni rastrea Shadow DOM en páginas sin video/audio.
/// </summary>
public static class MediaRuntime
{
    public const string InjectedScript = @"
(function() {
    if (window.__lex_media_runtime_injected) return;
    window.__lex_media_runtime_injected = true;

    const hostname = (window.location.hostname || '').toLowerCase();
    const isYouTube = hostname === 'youtube.com' ||
                      hostname.endsWith('.youtube.com') ||
                      hostname === 'youtu.be' ||
                      hostname === 'youtube-nocookie.com' ||
                      hostname.endsWith('.youtube-nocookie.com');

    // ─────────────────────────────────────────────────────────────────
    // 1. MINI-REPRODUCTOR NATIVO CON RETORNO A PESTAÑA
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

    // ─────────────────────────────────────────────────────────────────
    // 2. AMPLIFICADOR DE VOLUMEN UNIVERSAL HASTA 300% (Bajo Demanda)
    // ─────────────────────────────────────────────────────────────────
    window.__lexVolumePercent = 100;

    const ensureAudioContextRunning = () => {
        try { const c = window.__lexAudioCtx; if (c && c.state === 'suspended') c.resume().catch(() => {}); } catch(_) {}
    };

    const lexClamp = (v) => { const n = Number(v); return Number.isFinite(n) ? Math.max(0, Math.min(300, n)) : 100; };

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

    const lexSeen = new WeakSet();
    const lexRefs = [];
    const lexShadowRoots = [];
    const lexListened = new WeakSet();
    let mediaRuntimeArmed = false;

    const listenMedia = (el) => {
        if (lexListened.has(el)) return;
        lexListened.add(el);
        ['play', 'pause', 'ended', 'loadedmetadata', 'durationchange', 'emptied'].forEach(evt =>
            el.addEventListener(evt, () => { if (window.__lexScheduleSync) window.__lexScheduleSync(); }));
    };

    const pruneWeakRefs = (arr, maxLen) => {
        if (arr.length < maxLen) return;
        for (let i = arr.length - 1; i >= 0; i--) {
            if (!arr[i].deref()) arr.splice(i, 1);
        }
        if (arr.length >= maxLen) arr.splice(0, arr.length - Math.floor(maxLen / 2));
    };

    const trackMedia = (el) => {
        if (!el || lexSeen.has(el)) return;
        lexSeen.add(el);
        pruneWeakRefs(lexRefs, 128);
        lexRefs.push(new WeakRef(el));
        listenMedia(el);
    };

    const armTitleObserver = () => {
        const t = document.querySelector('title');
        if (!t || t.__lexTitleObs) return;
        t.__lexTitleObs = true;
        new MutationObserver(() => { if (window.__lexScheduleSync) window.__lexScheduleSync(); }).observe(t, { childList: true });
    };

    const armMediaPrototypes = () => {
        if (mediaRuntimeArmed) return;
        mediaRuntimeArmed = true;

        try {
            ['pointerdown', 'click', 'keydown', 'play'].forEach(evt => {
                window.addEventListener(evt, ensureAudioContextRunning, { capture: true, passive: true });
            });
        } catch(_) {}

        try {
            const origPlay = HTMLMediaElement.prototype.play;
            HTMLMediaElement.prototype.play = function() {
                trackMedia(this);
                if (window.__lexVolumePercent !== 100) {
                    const self = this;
                    queueMicrotask(() => applyToElement(self, window.__lexVolumePercent));
                }
                return origPlay.apply(this, arguments);
            };
        } catch(_) {}

        try {
            const origAttach = Element.prototype.attachShadow;
            Element.prototype.attachShadow = function(init) {
                const root = origAttach.call(this, init);
                try {
                    pruneWeakRefs(lexShadowRoots, 256);
                    lexShadowRoots.push(new WeakRef(root));
                } catch(_) {}
                return root;
            };
        } catch(_) {}

        try { armTitleObserver(); } catch(_) {}
    };

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

    const classify = (el) => {
        if (el.mediaKeys) return 'drm';
        if (el.srcObject) return 'safe';
        const src = el.currentSrc || '';
        if (!src) return 'pending';
        if (/^(blob|data):/i.test(src)) return 'safe';
        try { if (new URL(src, location.href).origin === location.origin) return 'safe'; } catch(_) { return 'pending'; }
        if (el.crossOrigin === 'anonymous' || el.crossOrigin === 'use-credentials') return 'safe';
        return 'cross';
    };

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

    const reloadWithCors = (el) => new Promise((resolve) => {
        const t = el.currentTime, wasPlaying = !el.paused, rate = el.playbackRate;
        let done = false;
        const finish = (ok) => {
            if (done) return; done = true;
            clearTimeout(timer);
            el.removeEventListener('loadedmetadata', onOk);
            el.removeEventListener('error', onErr);
            if (!ok) { el.removeAttribute('crossorigin'); try { el.load(); } catch(_) {} }
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

    const pendingUnlock = new Map();
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
            let ok = await probeCorsWithThrowaway(src, 6000);
            if (!ok && await requestHostUnlock(src)) ok = await probeCorsWithThrowaway(src, 8000);
            if (ok && el.currentSrc === src && await reloadWithCors(el) && routeElement(el)) return true;
            markFailed(el, src, 'cors_blocked');
            return false;
        } catch(_) { markFailed(el, src, 'cors_blocked'); return false; }
        finally { el.__lexBusy = false; }
    };

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
            gain.connect(ctx.destination);
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
        if (s && lexFailedSrc.get(el) === s) return false;
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
            const cur = window.__lexVolumePercent;
            if (ok && cur > 100) { ensureAudioContextRunning(); setChain(el, true); setGain(el, cur / 100.0); }
        } catch(_) {}
    };

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
        armMediaPrototypes();
        const p = lexClamp(percent);
        window.__lexVolumePercent = p;
        if (p === 0) window.__lexMutedByZeroVol = true;
        updateObserverState(p);
        collectAllMedia().forEach(el => applyToElement(el, p));
        document.querySelectorAll('iframe').forEach(ifr => {
            try { if (ifr.contentWindow) ifr.contentWindow.postMessage({ __lex_cmd: 'set_volume_boost', percent: p }, '*'); } catch(_) {}
        });
    };

    window.__lexSetVolumeBoostGlobal = function(percent) {
        applyVolumeBoostAll(percent);
    };

    try {
        if (window.parent && window.parent !== window) {
            window.parent.postMessage({ __lex_cmd: 'req_volume_boost' }, '*');
        }
        if (window.top && window.top !== window && window.top !== window.parent) {
            window.top.postMessage({ __lex_cmd: 'req_volume_boost' }, '*');
        }
    } catch (_) {}

    // Activación perezosa de utilidades de video: solo cuando el primer elemento multimedia arranca o se descubre
    ['play', 'playing', 'loadedmetadata', 'canplay'].forEach(evt => {
        document.addEventListener(evt, (e) => {
            if (e.target && (e.target.tagName === 'VIDEO' || e.target.tagName === 'AUDIO')) {
                armMediaPrototypes();
                trackMedia(e.target);
                if (e.target.tagName === 'VIDEO') unlockVideoPiP(e.target);
                if (typeof window.__lexVolumePercent === 'number' && window.__lexVolumePercent !== 100) {
                    applyToElement(e.target, window.__lexVolumePercent);
                }
            }
        }, true);
    });

    // ─────────────────────────────────────────────────────────────────
    // 3. DETECTOR MULTIMEDIA BAJO DEMANDA
    // ─────────────────────────────────────────────────────────────────
    const scanPageMediaOnDemand = () => {
        try {
            if (isYouTube) return;
            armMediaPrototypes();

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

            document.querySelectorAll('iframe').forEach(ifr => {
                try {
                    if (ifr.contentWindow) {
                        ifr.contentWindow.postMessage({ __lex_cmd: 'scan_page_media' }, '*');
                    }
                } catch (_) {}
            });
        } catch (_) {}
    };

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
            armMediaPrototypes();
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

    const onReady = (fn) => {
        if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', fn, { once: true });
        else fn();
    };

    onReady(() => {
        const existingVideos = document.querySelectorAll('video, audio');
        if (existingVideos.length > 0) {
            armMediaPrototypes();
            existingVideos.forEach(m => {
                trackMedia(m);
                if (m.tagName === 'VIDEO') unlockVideoPiP(m);
            });
        }
    });

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

    if (isYouTube) document.addEventListener('yt-navigate-finish', () => { armTitleObserver(); scheduleSync(); }, true);

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
        if (on) {
            armMediaPrototypes();
            document.addEventListener('timeupdate', onTimeUpdate, true);
            syncMediaState();
        } else {
            document.removeEventListener('timeupdate', onTimeUpdate, true);
        }
    };
})();
";
}
