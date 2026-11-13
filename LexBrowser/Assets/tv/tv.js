'use strict';

const ROW_HEIGHT = 62;
const MAX_LOCAL_FILE_BYTES = 10 * 1024 * 1024;
const STARTUP_TIMEOUT_MS = 10000;
const STALL_TIMEOUT_MS = 12000;
const DIAG_PER_CHANNEL_MS = 10000;
const ZAP_BUFFER_MS = 1200;

const NO_RETRY_HTTP_STATUSES = new Set([401, 403, 404, 410, 451, 502]);

function classifyFailure(ctx) {
  const c = ctx || {};
  const status = Number(c.status || 0);
  const errorClass = String(c.errorClass || '').toLowerCase();
  const details = String(c.details || '');
  const kind = String(c.kind || '').toLowerCase();
  const message = String(c.message || '');
  const mediaErrorCode = Number(c.mediaErrorCode || 0);
  const codecs = c.codecs || {};

  if (errorClass === 'stale') {
    return {
      category: 'stale',
      title: '',
      detail: '',
      persistKind: 'transient',
      hint: '',
      retry: false
    };
  }

  if (status === 404 || status === 410) {
    return {
      category: 'offline',
      title: `Canal fuera de línea (HTTP ${status})`,
      detail: 'El servidor respondió que el stream no existe o fue retirado.',
      persistKind: 'dead',
      hint: 'Prueba otra fuente alternativa o marca como caído.'
    };
  }

  if (status === 401 || status === 403 || status === 451) {
    return {
      category: 'restricted',
      title: `Acceso restringido o geobloqueado (HTTP ${status})`,
      detail: 'Este canal exige autorización, región específica o token temporal.',
      persistKind: 'restricted',
      hint: 'Puedes intentar otra fuente alternativa o abrir el enlace en VLC.'
    };
  }

  if (status === 429 || status === 503) {
    return {
      category: 'transient',
      title: `Servidor ocupado temporalmente (HTTP ${status})`,
      detail: 'El servidor limitó las peticiones o está saturado en este momento.',
      persistKind: 'transient',
      hint: 'Espera unos segundos y pulsa Reintentar.',
      retry: true
    };
  }

  if (errorClass === 'dns') {
    return {
      category: 'dns',
      title: 'Dominio inaccesible o inexistente (DNS)',
      detail: message || 'No se pudo resolver la dirección del canal.',
      persistKind: 'dead',
      hint: 'El dominio puede haber expirado; prueba otra fuente si existe.'
    };
  }

  if (errorClass === 'tls') {
    return {
      category: 'tls',
      title: 'Error de certificado o conexión segura (TLS)',
      detail: message || 'El certificado SSL/TLS del origen es inválido o incompatible.',
      persistKind: 'dead',
      hint: 'Puedes intentar abrirlo en VLC bajo tu propio criterio.'
    };
  }

  if (errorClass === 'blocked') {
    return {
      category: 'blocked',
      title: 'Destino bloqueado por seguridad',
      detail: message || 'La URL o su resolución DNS apunta a una red privada o esquema bloqueado.',
      persistKind: 'restricted',
      hint: 'Revisa que la URL sea pública http:// o https://.'
    };
  }

  if (
    errorClass === 'timeout' ||
    errorClass === 'refused' ||
    status === 504 ||
    /TimeOut/i.test(details)
  ) {
    return {
      category: 'timeout',
      title: errorClass === 'refused' ? 'Conexión rechazada por el origen' : 'Tiempo de espera agotado',
      detail: message || 'El servidor del canal tardó demasiado en responder o rechazó la conexión.',
      persistKind: 'transient',
      hint: 'Pulsa Reintentar o prueba una fuente alternativa.'
    };
  }

  if (
    details === 'bufferAddCodecError' ||
    c.categoryHint === 'codec' ||
    mediaErrorCode === 3 ||
    (mediaErrorCode === 4 && status < 400) ||
    /codec|hevc|ac-3|ec-3|not supported/i.test(message)
  ) {
    const missingAudio = codecs && codecs.ac3 === false ? ' (AC-3/HEVC no soportado nativamente)' : '';
    return {
      category: 'codec',
      title: 'Códec incompatible con WebView2',
      detail: `El navegador no puede decodificar el formato de video o audio${missingAudio}.`,
      persistKind: 'codec',
      hint: 'Usa "Abrir en VLC" para reproducir este stream directamente.'
    };
  }

  if (
    details === 'manifestParsingError' ||
    details === 'levelParsingError' ||
    kind === 'html'
  ) {
    return {
      category: 'parsing',
      title: 'Manifiesto inválido o página web en lugar de video',
      detail: kind === 'html'
        ? 'El origen devolvió una página HTML (error/portal) en vez de un stream de video.'
        : 'La lista HLS (.m3u8) tiene formato inválido o está incompleta.',
      persistKind: 'dead',
      hint: 'Prueba otra fuente alternativa o ábrelo en VLC.'
    };
  }

  if (errorClass === 'upstream' || status >= 500 || status === 400 || status === 0) {
    const codeLabel = status > 0 ? ` (HTTP ${status})` : '';
    return {
      category: status === 0 ? 'offline' : 'upstream',
      title: `No se pudo cargar el stream${codeLabel}`,
      detail: message || 'El origen falló al entregar los datos del video.',
      persistKind: status >= 500 ? 'transient' : 'dead',
      hint: 'Pulsa Reintentar, prueba otra fuente o abre en VLC.'
    };
  }

  return {
    category: 'offline',
    title: 'Error al reproducir el canal',
    detail: message || details || 'El stream no respondió correctamente.',
    persistKind: 'dead',
    hint: 'Prueba otra fuente o abrir en VLC.'
  };
}

function pickEngineKind(url, probeKind) {
  const k = String(probeKind || '').toLowerCase();
  if (k === 'hls') return 'hls';
  if (k === 'mpegts' || k === 'flv') return 'mpegts';
  if (k === 'mp4' || k === 'webm' || k === 'ogg' || k === 'mp3' || k === 'aac') return 'native';

  const cleanUrl = String(url || '').split('?')[0].split('#')[0].toLowerCase();
  if (cleanUrl.endsWith('.m3u8') || cleanUrl.endsWith('.m3u')) return 'hls';
  if (cleanUrl.endsWith('.ts') || cleanUrl.endsWith('.flv')) return 'mpegts';
  if (
    cleanUrl.endsWith('.mp4') ||
    cleanUrl.endsWith('.webm') ||
    cleanUrl.endsWith('.ogg') ||
    cleanUrl.endsWith('.mp3') ||
    cleanUrl.endsWith('.aac')
  ) {
    return 'native';
  }
  return 'probe';
}

function buildHlsConfig(HlsCtor) {
  const shouldRetry = (_retryCfg, _maxRetry, context) => {
    const code = context?.response?.code || 0;
    return !NO_RETRY_HTTP_STATUSES.has(code);
  };

  const base = {
    enableWorker: false,
    lowLatencyMode: false,
    maxBufferLength: 12,
    maxMaxBufferLength: 24,
    backBufferLength: 10,
    maxBufferSize: 20 * 1000 * 1000,
    startLevel: -1,
    testBandwidth: false,
    abrEwmaDefaultEstimate: 1500000,
    startFragPrefetch: true,
    liveSyncDurationCount: 3,
    manifestLoadingMaxRetry: 2,
    levelLoadingMaxRetry: 2,
    fragLoadingMaxRetry: 3,
    capLevelToPlayerSize: true
  };

  const ver = String(HlsCtor?.version || '1.0.0');
  const parts = ver.split('.');
  const major = parseInt(parts[0], 10) || 1;
  const minor = parseInt(parts[1], 10) || 0;
  if (major > 1 || (major === 1 && minor >= 5)) {
    base.manifestLoadPolicy = {
      default: {
        maxTimeToFirstByteMs: 10000,
        maxLoadTimeMs: 15000,
        timeoutRetry: { maxNumRetry: 2, retryDelayMs: 400, maxRetryDelayMs: 1500 },
        errorRetry: { maxNumRetry: 2, retryDelayMs: 400, maxRetryDelayMs: 1500, shouldRetry }
      }
    };
    base.fragLoadPolicy = {
      default: {
        maxTimeToFirstByteMs: 8000,
        maxLoadTimeMs: 15000,
        timeoutRetry: { maxNumRetry: 3, retryDelayMs: 300, maxRetryDelayMs: 1200 },
        errorRetry: { maxNumRetry: 3, retryDelayMs: 300, maxRetryDelayMs: 1200, shouldRetry }
      }
    };
  } else if (major >= 1) {
    base.manifestLoadPolicy = {
      default: {
        maxTimeToFirstByteMs: 12000,
        maxLoadTimeMs: 20000,
        timeoutRetry: { maxNumRetry: 2, retryDelayMs: 500, maxRetryDelayMs: 2000 },
        errorRetry: { maxNumRetry: 1, retryDelayMs: 500, maxRetryDelayMs: 2000, shouldRetry }
      }
    };
    base.fragLoadPolicy = {
      default: {
        maxTimeToFirstByteMs: 10000,
        maxLoadTimeMs: 20000,
        timeoutRetry: { maxNumRetry: 2, retryDelayMs: 500, maxRetryDelayMs: 2000 },
        errorRetry: { maxNumRetry: 2, retryDelayMs: 500, maxRetryDelayMs: 2000, shouldRetry }
      }
    };
  }
  return base;
}

if (typeof window !== 'undefined' && typeof document !== 'undefined') {
  const isDiagMode = typeof window !== 'undefined' && (new URLSearchParams(window.location.search).get('diag') === '1');

  const state = {
    catalog: [],
    playlists: [],
    loadedPlaylists: new Map(),
    favorites: new Set(),
    deadStreams: Object.create(null),
    activeSourceId: 'catalog',
    filtered: [],
    activeChannel: null,
    activeSourceIndex: 0,
    activeSources: [],
    activeEngineName: '',
    codecs: { h264: false, hevc: false, aac: false, ac3: false, ec3: false, mp3: false, mse: false },
    hlsInstance: null,
    mpegtsInstance: null,
    playbackSessionId: 0,
    gen: 0,
    switchTimer: null,
    retry429Count: 0,
    perfTimes: null,
    activeProxySid: '',
    diagSidSeq: 0,
    startupTimer: null,
    stallTimer: null,
    hasStartedPlayback: false,
    fallbackUsed: false,
    zapDigits: '',
    zapTimer: null,
    animLang: 'es',
    animCountry: 'all',
    animeOnly: false,
    diagRunning: false,
    diagStopFlag: false,
    diagResults: []
  };

  const scriptPromises = new Map();
  const els = {};

  function $(id) {
    return document.getElementById(id);
  }

  function loadScript(src) {
    if (scriptPromises.has(src)) return scriptPromises.get(src);
    const p = new Promise((resolve, reject) => {
      const s = document.createElement('script');
      s.src = src;
      s.async = true;
      s.onload = () => resolve();
      s.onerror = () => {
        scriptPromises.delete(src);
        reject(new Error(`No se pudo cargar ${src}`));
      };
      document.head.appendChild(s);
    });
    scriptPromises.set(src, p);
    return p;
  }

  function detectCodecSupport() {
    const v = document.createElement('video');
    const ms = typeof window.MediaSource !== 'undefined' ? window.MediaSource : null;
    const can = (mime) => {
      if (ms && typeof ms.isTypeSupported === 'function' && ms.isTypeSupported(mime)) return true;
      const r = v.canPlayType(mime);
      return r === 'probably' || r === 'maybe';
    };
    state.codecs = {
      mse: !!ms,
      h264: can('video/mp4; codecs="avc1.42E01E"'),
      hevc: can('video/mp4; codecs="hvc1.1.6.L93.B0"'),
      aac: can('audio/mp4; codecs="mp4a.40.2"'),
      ac3: can('audio/mp4; codecs="ac-3"'),
      ec3: can('audio/mp4; codecs="ec-3"'),
      mp3: can('audio/mpeg')
    };
    const mark = (ok) => (ok ? '✓' : '✗');
    els.codecPill.textContent =
      `Codecs: H.264 ${mark(state.codecs.h264)} · AAC ${mark(state.codecs.aac)} · HEVC ${mark(state.codecs.hevc)} · AC-3 ${mark(state.codecs.ac3)}`;
  }

  function buildProxyUrl(url, referrer, userAgent, sid) {
    let q = `/api/proxy?u=${encodeURIComponent(url)}`;
    if (referrer) q += `&r=${encodeURIComponent(referrer)}`;
    if (userAgent) q += `&ua=${encodeURIComponent(userAgent)}`;
    if (sid) q += `&sid=${encodeURIComponent(String(sid))}`;
    return q;
  }

  function abortProxySession(sid) {
    if (!sid) return;
    try {
      fetch(`/api/proxy/abort?sid=${encodeURIComponent(String(sid))}`, { method: 'POST', keepalive: true }).catch(() => { });
    } catch { }
  }

  async function apiJson(url, options) {
    const resp = await fetch(url, options);
    const text = await resp.text();
    let data = {};
    try { data = text ? JSON.parse(text) : {}; } catch { }
    if (!resp.ok) {
      throw new Error(data.error || data.msg || `HTTP ${resp.status}`);
    }
    return data;
  }

  async function probeStream(url, referrer, userAgent) {
    let q = `/api/probe?u=${encodeURIComponent(url)}`;
    if (referrer) q += `&r=${encodeURIComponent(referrer)}`;
    if (userAgent) q += `&ua=${encodeURIComponent(userAgent)}`;
    try {
      const resp = await fetch(q);
      if (!resp.ok) return { kind: 'unknown', status: resp.status, errorClass: '', errorMessage: '' };
      return await resp.json();
    } catch {
      return { kind: 'unknown', status: 0, errorClass: '', errorMessage: '' };
    }
  }

  function getChannelSources(ch) {
    const list = [{
      url: ch.url,
      quality: ch.quality || '',
      referrer: ch.referrer || '',
      userAgent: ch.userAgent || ''
    }];
    if (Array.isArray(ch.alternates)) {
      for (const a of ch.alternates) {
        if (a && a.url) {
          list.push({
            url: a.url,
            quality: a.quality || '',
            referrer: a.referrer || '',
            userAgent: a.userAgent || ''
          });
        }
      }
    }
    return list;
  }

  function isUrlMarkedDead(url) {
    const m = state.deadStreams[url];
    if (!m) return false;
    if (m.expiresUtc) {
      const exp = Date.parse(m.expiresUtc);
      if (!Number.isNaN(exp) && exp <= Date.now()) {
        delete state.deadStreams[url];
        return false;
      }
    }
    return true;
  }

  function isChannelAllDead(ch) {
    const sources = getChannelSources(ch);
    return sources.length > 0 && sources.every((s) => isUrlMarkedDead(s.url));
  }

  function clearWatchdogs() {
    if (state.startupTimer) {
      clearTimeout(state.startupTimer);
      state.startupTimer = null;
    }
    if (state.stallTimer) {
      clearTimeout(state.stallTimer);
      state.stallTimer = null;
    }
  }

  function destroyActivePlayer() {
    clearWatchdogs();
    const prevSid = state.activeProxySid;
    state.activeProxySid = '';
    abortProxySession(prevSid);
    if (state.hlsInstance) {
      try { state.hlsInstance.destroy(); } catch { }
      state.hlsInstance = null;
    }
    if (state.mpegtsInstance) {
      try {
        state.mpegtsInstance.pause();
        state.mpegtsInstance.unload();
        state.mpegtsInstance.detachMediaElement();
        state.mpegtsInstance.destroy();
      } catch { }
      state.mpegtsInstance = null;
    }
    try {
      els.videoEl.pause();
      els.videoEl.removeAttribute('src');
      els.videoEl.load();
    } catch { }
    state.hasStartedPlayback = false;
    els.codecWarn.classList.add('hidden');
  }

  function showOverlay(icon, title, detail, showActions) {
    els.statusIcon.textContent = icon;
    els.statusTitle.textContent = title;
    els.statusDetail.textContent = detail || '';
    els.statusActions.classList.toggle('hidden', !showActions);
    els.btnNextAlt.classList.toggle('hidden', !(state.activeSources.length > 1));
    els.statusOverlay.classList.remove('hidden');
  }

  function hideOverlay() {
    els.statusOverlay.classList.add('hidden');
  }

  function updateEngineBadge(name) {
    state.activeEngineName = name;
    if (!name) {
      els.engineBadge.classList.add('hidden');
      return;
    }
    els.engineBadge.textContent = name;
    els.engineBadge.classList.remove('hidden');
  }

  function updateNowPlayingHeader(ch, srcIndex) {
    els.nowNumber.textContent = ch.number > 0 ? `#${ch.number}` : '--';
    els.nowTitle.textContent = ch.name;
    const parts = [];
    if (ch.countryName || ch.country) parts.push(ch.countryName || ch.country);
    if (ch.group) parts.push(ch.group);
    if (ch.language) parts.push(ch.language);
    const activeSrc = state.activeSources[srcIndex];
    if (activeSrc && activeSrc.quality) parts.push(activeSrc.quality);
    if (ch.tags) parts.push(ch.tags);
    els.nowSub.textContent = parts.join(' · ');

    if (state.activeSources.length > 1) {
      els.btnAltSource.textContent = `Fuente ${srcIndex + 1}/${state.activeSources.length}`;
      els.btnAltSource.classList.remove('hidden');
    } else {
      els.btnAltSource.classList.add('hidden');
    }

    const isFav = state.favorites.has(ch.id);
    els.btnFavCurrent.textContent = isFav ? '★ Favorito' : '☆ Favorito';
    els.btnFavCurrent.classList.remove('hidden');
    els.btnVlcTop.classList.remove('hidden');
  }

  async function reportFailureMark(url, failure) {
    if (!url || !failure || failure.persistKind === 'transient') return;
    state.deadStreams[url] = {
      kind: failure.persistKind,
      failedUtc: new Date().toISOString(),
      reason: failure.title
    };
    renderVirtualList();
    try {
      await apiJson('/api/dead/mark', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          url,
          kind: failure.persistKind,
          reason: failure.title
        })
      });
    } catch { }
  }

  async function clearDeadMark(url) {
    if (!url || !state.deadStreams[url]) return;
    delete state.deadStreams[url];
    renderVirtualList();
    try {
      await apiJson('/api/dead/clear', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ url })
      });
    } catch { }
  }

  async function extractProxyErrorFromXhr(xhr) {
    if (!xhr) return { status: 0, errorClass: '', message: '' };
    const status = xhr.status || 0;
    const errorClass = (xhr.getResponseHeader && xhr.getResponseHeader('X-Lex-Error-Class')) || '';
    let message = '';
    try {
      if (xhr.responseText && xhr.responseText.trim().startsWith('{')) {
        const parsed = JSON.parse(xhr.responseText);
        if (parsed.msg) message = parsed.msg;
      }
    } catch { }
    return { status, errorClass, message };
  }

  function selectChannel(ch) {
    if (!ch) return;
    if (state.switchTimer) {
      clearTimeout(state.switchTimer);
      state.switchTimer = null;
    }
    state.activeChannel = ch;
    updateNowPlayingHeader(ch, 0);
    renderVirtualList();

    state.switchTimer = setTimeout(() => {
      state.switchTimer = null;
      startChannel(ch, false);
    }, 200);
  }

  function playChannel(ch, forceAllSources = false, explicitSourceIdx = -1) {
    return startChannel(ch, forceAllSources, explicitSourceIdx);
  }

  async function startChannel(ch, forceAllSources = false, explicitSourceIdx = -1) {
    if (!ch) return;
    if (state.switchTimer) {
      clearTimeout(state.switchTimer);
      state.switchTimer = null;
    }
    const my = ++state.gen;
    destroyActivePlayer();

    state.activeChannel = ch;

    if (ch.kind === 'external' || ch.isYouTube) {
      state.activeSources = [{ url: ch.externalUrl || ch.url }];
      updateNowPlayingHeader(ch, 0);
      updateEngineBadge('YouTube Externo');
      showOverlay('▶', ch.name, 'Esta transmisión se emite a través de YouTube.', true);
      els.btnRetry.textContent = 'Abrir en YouTube';
      els.btnRetry.onclick = () => {
        fetch('/api/vod/open-official', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ url: ch.externalUrl || ch.url })
        }).catch(() => { });
      };
      return;
    }

    const allSources = getChannelSources(ch);
    let orderedSources;

    if (explicitSourceIdx >= 0 && explicitSourceIdx < allSources.length) {
      orderedSources = allSources;
    } else if (forceAllSources) {
      orderedSources = allSources;
    } else {
      const healthy = allSources.filter((s) => !isUrlMarkedDead(s.url));
      const dead = allSources.filter((s) => isUrlMarkedDead(s.url));
      orderedSources = healthy.concat(dead);
    }

    state.activeSources = orderedSources;
    const startIdx = explicitSourceIdx >= 0 ? explicitSourceIdx : 0;
    await tryPlaySourceIndex(ch, startIdx, forceAllSources, my);
  }

  async function tryPlaySourceIndex(ch, idx, forceManual, my) {
    if (my !== state.gen) return;
    destroyActivePlayer();
    state.activeSourceIndex = idx;
    state.fallbackUsed = false;
    state.retry429Count = 0;
    els.btnRetry.textContent = 'Reintentar';
    els.btnRetry.onclick = () => playChannel(state.activeChannel, true);
    updateNowPlayingHeader(ch, idx);
    renderVirtualList();

    const src = state.activeSources[idx];
    if (!src) return;

    state.perfTimes = {
      tClick: performance.now(),
      tManifest: 0,
      tFirstFrag: 0,
      tPlaying: 0,
      channel: ch
    };

    const total = state.activeSources.length;
    const srcLabel = total > 1 ? ` (fuente ${idx + 1}/${total})` : '';
    showOverlay('⏳', `Conectando${srcLabel}…`, ch.name, false);

    let engine = pickEngineKind(src.url, '');
    let probeInfo = null;
    if (engine === 'probe') {
      probeInfo = await probeStream(src.url, src.referrer, src.userAgent);
      if (my !== state.gen) return;

      if (probeInfo.errorClass || probeInfo.status >= 400 || probeInfo.kind === 'html') {
        const failure = classifyFailure({
          status: probeInfo.status,
          errorClass: probeInfo.errorClass,
          kind: probeInfo.kind,
          message: probeInfo.errorMessage,
          codecs: state.codecs
        });
        await handlePlaybackFailure(ch, idx, src, failure, my);
        return;
      }

      engine = pickEngineKind(src.url, probeInfo.kind);
      if (engine === 'probe') engine = 'hls';
    }

    if (my !== state.gen) return;
    await startEngine(ch, idx, src, engine, my, probeInfo);
  }

  async function startEngine(ch, idx, src, engine, my, probeInfo) {
    if (my !== state.gen) return;
    const sid = `g${my}_${idx}`;
    state.activeProxySid = sid;
    const proxyUrl = buildProxyUrl(src.url, src.referrer, src.userAgent, sid);

    clearWatchdogs();
    state.startupTimer = setTimeout(async () => {
      if (my !== state.gen || state.hasStartedPlayback) return;
      const failure = classifyFailure({
        errorClass: 'timeout',
        status: 504,
        message: 'El stream no envió cuadros de video en 10 segundos.'
      });
      await handlePlaybackFailure(ch, idx, src, failure, my);
    }, STARTUP_TIMEOUT_MS);

    if (engine === 'hls') {
      updateEngineBadge('HLS.js');
      try {
        await loadScript('./vendor/hls.light.min.js');
      } catch (err) {
        if (my !== state.gen) return;
        await handlePlaybackFailure(ch, idx, src, classifyFailure({ message: err.message }), my);
        return;
      }
      if (my !== state.gen) return;

      if (window.Hls && window.Hls.isSupported()) {
        const hls = new window.Hls(buildHlsConfig(window.Hls));
        state.hlsInstance = hls;

        hls.on(window.Hls.Events.MANIFEST_PARSED, (_ev, data) => {
          if (my !== state.gen) return;
          if (state.perfTimes && !state.perfTimes.tManifest) {
            state.perfTimes.tManifest = performance.now();
          }
          if (data && Array.isArray(data.audioTracks) && data.audioTracks.length === 0 && !state.codecs.ac3) {
            // Verificar tras iniciar video si hay pistas de audio reales
          }
          els.videoEl.play().catch(() => { });
        });

        hls.on(window.Hls.Events.FRAG_LOADED, () => {
          if (my !== state.gen) return;
          if (state.perfTimes && !state.perfTimes.tFirstFrag) {
            state.perfTimes.tFirstFrag = performance.now();
          }
        });

        hls.on(window.Hls.Events.ERROR, async (_ev, data) => {
          if (my !== state.gen || !data || !data.fatal) return;

          let netInfo = await extractProxyErrorFromXhr(data.networkDetails);
          if (my !== state.gen) return;
          if (netInfo.errorClass === 'stale') return;

          const status = Number(data.response?.code || netInfo.status || 0);

          if (status === 429 && state.retry429Count < 2) {
            state.retry429Count++;
            const delay = state.retry429Count === 1 ? 400 : 900;
            setTimeout(() => {
              if (my === state.gen) {
                try {
                  hls.loadSource(proxyUrl);
                  hls.attachMedia(els.videoEl);
                } catch { }
              }
            }, delay);
            return;
          }

          if (data.type === window.Hls.ErrorTypes.MEDIA_ERROR && !state.fallbackUsed) {
            state.fallbackUsed = true;
            try { hls.recoverMediaError(); return; } catch { }
          }

          if ((status === 0 || (!netInfo.errorClass && status >= 400)) && data.details === 'manifestLoadError') {
            const p = await probeStream(src.url, src.referrer, src.userAgent);
            if (my !== state.gen) return;

            if (!state.fallbackUsed && (p.kind === 'mpegts' || p.kind === 'flv')) {
              state.fallbackUsed = true;
              destroyActivePlayer();
              await startEngine(ch, idx, src, 'mpegts', my, p);
              return;
            }
            if (!state.fallbackUsed && (p.kind === 'mp4' || p.kind === 'webm' || p.kind === 'ogg')) {
              state.fallbackUsed = true;
              destroyActivePlayer();
              await startEngine(ch, idx, src, 'native', my, p);
              return;
            }
            netInfo = {
              status: p.status || status,
              errorClass: p.errorClass || netInfo.errorClass,
              message: p.errorMessage || netInfo.message,
              kind: p.kind
            };
          }

          const failure = classifyFailure({
            status: netInfo.status || status,
            errorClass: netInfo.errorClass,
            message: netInfo.message || data.reason || '',
            details: data.details,
            kind: netInfo.kind || probeInfo?.kind || '',
            codecs: state.codecs
          });
          await handlePlaybackFailure(ch, idx, src, failure, my);
        });

        hls.loadSource(proxyUrl);
        hls.attachMedia(els.videoEl);
        return;
      }

      if (els.videoEl.canPlayType('application/vnd.apple.mpegurl')) {
        updateEngineBadge('HLS Nativo');
        els.videoEl.src = proxyUrl;
        els.videoEl.play().catch(() => { });
        return;
      }

      await handlePlaybackFailure(ch, idx, src, classifyFailure({ categoryHint: 'codec' }), my);
      return;
    }

    if (engine === 'mpegts') {
      updateEngineBadge('MPEG-TS');
      try {
        await loadScript('./vendor/mpegts.js');
      } catch (err) {
        if (my !== state.gen) return;
        await handlePlaybackFailure(ch, idx, src, classifyFailure({ message: err.message }), my);
        return;
      }
      if (my !== state.gen) return;

      if (window.mpegts && window.mpegts.isSupported()) {
        const player = window.mpegts.createPlayer(
          { type: probeInfo?.kind === 'flv' ? 'flv' : 'mse', isLive: true, url: proxyUrl },
          {
            enableWorker: false,
            lazyLoad: false,
            liveBufferLatencyChasing: true,
            autoCleanupSourceBuffer: true,
            autoCleanupMaxBackwardDuration: 20,
            autoCleanupMinBackwardDuration: 10
          }
        );
        state.mpegtsInstance = player;

        player.on(window.mpegts.Events.ERROR, async (type, detail, info) => {
          if (my !== state.gen) return;
          const status = Number(info?.code || 0);
          const p = await probeStream(src.url, src.referrer, src.userAgent);
          if (my !== state.gen) return;

          if (!state.fallbackUsed && p.kind === 'hls') {
            state.fallbackUsed = true;
            destroyActivePlayer();
            await startEngine(ch, idx, src, 'hls', my, p);
            return;
          }

          const failure = classifyFailure({
            status: p.status || status,
            errorClass: p.errorClass,
            kind: p.kind,
            message: p.errorMessage || info?.msg || String(detail || ''),
            categoryHint: /codec|audio|video/i.test(String(detail || '')) ? 'codec' : '',
            codecs: state.codecs
          });
          await handlePlaybackFailure(ch, idx, src, failure, my);
        });

        player.attachMediaElement(els.videoEl);
        player.load();
        els.videoEl.play().catch(() => { });
        return;
      }

      await handlePlaybackFailure(ch, idx, src, classifyFailure({ categoryHint: 'codec' }), my);
      return;
    }

    updateEngineBadge('Nativo <video>');
    els.videoEl.src = proxyUrl;
    els.videoEl.play().catch(() => { });
  }

  async function handlePlaybackFailure(ch, idx, src, failure, my) {
    if (my !== state.gen) return;
    if (failure.category === 'stale') return;

    if (failure.category === 'transient' && failure.title.includes('429') && state.retry429Count < 2) {
      state.retry429Count++;
      const delay = state.retry429Count === 1 ? 400 : 900;
      setTimeout(() => {
        if (my === state.gen) {
          tryPlaySourceIndex(ch, idx, false, my);
        }
      }, delay);
      return;
    }

    destroyActivePlayer();

    if (failure.persistKind && failure.persistKind !== 'transient') {
      await reportFailureMark(src.url, failure);
    }
    if (my !== state.gen) return;

    const nextIdx = idx + 1;
    if (nextIdx < state.activeSources.length && failure.category !== 'transient') {
      showOverlay(
        '↻',
        `Fuente ${idx + 1} falló (${failure.category})`,
        `Probando fuente ${nextIdx + 1}/${state.activeSources.length}…`,
        false
      );
      setTimeout(() => {
        if (my === state.gen) {
          tryPlaySourceIndex(ch, nextIdx, false, my);
        }
      }, 250);
      return;
    }

    showOverlay(
      '⚠',
      failure.title,
      `${failure.detail}\n${failure.hint}`,
      true
    );
  }

  function wireVideoEvents() {
    els.videoEl.addEventListener('playing', () => {
      state.hasStartedPlayback = true;
      clearWatchdogs();
      hideOverlay();
      const activeSrc = state.activeSources[state.activeSourceIndex];
      if (activeSrc) clearDeadMark(activeSrc.url);

      if (state.perfTimes && state.activeChannel) {
        state.perfTimes.tPlaying = performance.now();
        if (isDiagMode) {
          const tManifest = state.perfTimes.tManifest ? Math.round(state.perfTimes.tManifest - state.perfTimes.tClick) : -1;
          const tFirstFrag = state.perfTimes.tFirstFrag ? Math.round(state.perfTimes.tFirstFrag - state.perfTimes.tClick) : -1;
          const tPlaying = Math.round(state.perfTimes.tPlaying - state.perfTimes.tClick);
          console.log(`[LexTV Diag] #${state.activeChannel.number} ${state.activeChannel.name}: tClick=0ms, tManifest=${tManifest}ms, tFirstFrag=${tFirstFrag}ms, tPlaying=${tPlaying}ms`);
        }
      }

      setTimeout(() => {
        if (!state.activeChannel || !state.hasStartedPlayback) return;
        const tracks = els.videoEl.audioTracks;
        if (els.videoEl.videoWidth > 0 && tracks && tracks.length === 0) {
          els.codecWarnText.textContent = 'Video activo sin pista de audio compatible (posible AC-3/EAC-3). Prueba en VLC.';
          els.codecWarn.classList.remove('hidden');
        }
      }, 1500);
    });

    els.videoEl.addEventListener('waiting', () => {
      if (!state.hasStartedPlayback || els.videoEl.paused) return;
      if (state.stallTimer) clearTimeout(state.stallTimer);
      const my = state.gen;
      state.stallTimer = setTimeout(() => {
        if (my !== state.gen || els.videoEl.paused) return;
        if (state.hlsInstance) {
          try { state.hlsInstance.startLoad(); } catch { }
        }
        const failure = classifyFailure({
          errorClass: 'timeout',
          status: 504,
          message: 'El stream se congeló más de 12 segundos sin nuevos fragmentos.'
        });
        const src = state.activeSources[state.activeSourceIndex];
        if (state.activeChannel && src) {
          handlePlaybackFailure(state.activeChannel, state.activeSourceIndex, src, failure, my);
        }
      }, STALL_TIMEOUT_MS);
    });

    els.videoEl.addEventListener('error', async () => {
      if (!state.activeChannel || state.hlsInstance || state.mpegtsInstance) return;
      if (!els.videoEl.getAttribute('src') && !els.videoEl.currentSrc) return;
      const err = els.videoEl.error;
      const my = state.gen;
      const src = state.activeSources[state.activeSourceIndex];
      if (!src) return;

      const p = await probeStream(src.url, src.referrer, src.userAgent);
      if (my !== state.gen) return;

      const failure = classifyFailure({
        status: p.status,
        errorClass: p.errorClass,
        kind: p.kind,
        message: p.errorMessage || err?.message || '',
        mediaErrorCode: err?.code || 0,
        codecs: state.codecs
      });
      await handlePlaybackFailure(state.activeChannel, state.activeSourceIndex, src, failure, my);
    });
  }

  function scoreAnime(ch) {
    if (!ch) return 0;
    let score = 0;
    const name = (ch.name || '').toLowerCase();
    const group = (ch.group || '').toLowerCase();
    const lang = (ch.language || '').toLowerCase();
    const tags = (ch.tags || '').toLowerCase();

    const animeKeywords = ['anime', 'manga', 'otaku', 'toei', 'crunchyroll', 'animax', 'funimation', 'retro anime', 'aniverse'];
    for (const kw of animeKeywords) {
      if (name.includes(kw)) score += 5;
      if (group.includes(kw) || tags.includes(kw)) score += 3;
    }

    if (lang === 'jpn' || lang === 'ja' || (ch.country || '').toLowerCase() === 'jp') {
      if (group.includes('animat') || group.includes('animac') || group.includes('kids')) score += 4;
      else score += 2;
    }

    const franchises = [
      'dragon ball', 'naruto', 'one piece', 'pokemon', 'pokémon', 'digimon',
      'sailor moon', 'doraemon', 'shin chan', 'yugioh', 'yu-gi-oh', 'bleach',
      'inuyasha', 'ranma', 'saint seiya', 'caballeros del zodiaco', 'capitan tsubasa',
      'supercampeones', 'death note', 'evangelion', 'ghibli', 'conan'
    ];
    for (const f of franchises) {
      if (name.includes(f)) score += 6;
    }

    return score;
  }

  function applyFilters() {
    const query = (els.searchInput.value || '').trim().toLowerCase();
    const lang = els.langSelect.value;
    const cat = els.categorySelect.value;
    const hideDead = els.hideDeadChk.checked;

    const isAnimCategory = cat !== 'all' && (cat.toLowerCase().includes('animat') || cat.toLowerCase().includes('animac'));
    if (els.animSubfilters) {
      els.animSubfilters.classList.toggle('hidden', !isAnimCategory);
    }

    let baseList;
    if (state.activeSourceId === 'catalog') {
      baseList = state.catalog;
    } else if (state.activeSourceId === 'favorites') {
      const plChannels = Array.from(state.loadedPlaylists.values()).flatMap((p) => p.channels || []);
      const all = state.catalog.concat(plChannels);
      const byId = new Set();
      baseList = all.filter((c) => {
        if (!state.favorites.has(c.id) || byId.has(c.id)) return false;
        byId.add(c.id);
        return true;
      });
    } else {
      const pl = state.loadedPlaylists.get(state.activeSourceId);
      baseList = pl ? (pl.channels || []) : [];
    }

    if (isAnimCategory && els.animCountrySelect && els.animCountrySelect.options.length <= 1) {
      const countries = new Set();
      for (const ch of baseList) {
        if (ch.group === cat && ch.country) countries.add(ch.country);
      }
      const sortedCountries = Array.from(countries).sort();
      els.animCountrySelect.innerHTML = '<option value="all">País: Todos</option>';
      for (const c of sortedCountries) {
        const opt = document.createElement('option');
        opt.value = c;
        opt.textContent = c;
        els.animCountrySelect.appendChild(opt);
      }
    }

    state.filtered = baseList.filter((ch) => {
      if (hideDead && isChannelAllDead(ch)) return false;

      if (isAnimCategory) {
        if (state.animeOnly && scoreAnime(ch) < 3) return false;
        if (state.animCountry && state.animCountry !== 'all' && (ch.country || '').toLowerCase() !== state.animCountry.toLowerCase()) return false;
        if (state.animLang === 'es' && !String(ch.language || '').toLowerCase().includes('español')) return false;
        if (state.animLang === 'jp' && !String(ch.language || '').toLowerCase().includes('japonés') && String(ch.language || '').toLowerCase() !== 'jpn') return false;
        if (state.animLang === 'en' && !String(ch.language || '').toLowerCase().includes('inglés') && String(ch.language || '').toLowerCase() !== 'eng') return false;
      } else {
        if (lang === 'es' && !String(ch.language || '').toLowerCase().includes('español')) return false;
        if (lang === 'en' && !String(ch.language || '').toLowerCase().includes('inglés')) return false;
      }

      if (cat !== 'all' && ch.group !== cat) return false;
      if (query) {
        const qNum = query.startsWith('#') ? query.slice(1) : query;
        const matchNum = /^\d+$/.test(qNum) && String(ch.number) === qNum;
        if (!matchNum) {
          const hay = `${ch.name} ${ch.countryName || ''} ${ch.country || ''} ${ch.group || ''}`.toLowerCase();
          if (!hay.includes(query)) return false;
        }
      }
      return true;
    });

    els.channelCountLabel.textContent = `${state.filtered.length.toLocaleString('es')} canales`;
    els.channelViewport.scrollTop = 0;
    renderVirtualList();
  }

  function renderCategorySelect(channels) {
    const cur = els.categorySelect.value;
    const cats = new Set();
    for (const c of channels) {
      if (c.group) cats.add(c.group);
    }
    const sorted = Array.from(cats).sort((a, b) => a.localeCompare(b, 'es'));
    els.categorySelect.innerHTML = '<option value="all">Categoría: Todas</option>';
    for (const g of sorted) {
      const opt = document.createElement('option');
      opt.value = g;
      opt.textContent = g;
      els.categorySelect.appendChild(opt);
    }
    els.categorySelect.value = cats.has(cur) ? cur : 'all';
  }

  function renderSourceSelect() {
    const cur = state.activeSourceId;
    els.sourceSelect.innerHTML = '';

    const optCat = document.createElement('option');
    optCat.value = 'catalog';
    optCat.textContent = `Catálogo iptv-org (${state.catalog.length})`;
    els.sourceSelect.appendChild(optCat);

    const optFav = document.createElement('option');
    optFav.value = 'favorites';
    optFav.textContent = `★ Favoritos (${state.favorites.size})`;
    els.sourceSelect.appendChild(optFav);

    for (const pl of state.playlists) {
      const opt = document.createElement('option');
      opt.value = pl.id;
      opt.textContent = `📁 ${pl.name} (${pl.channelCount})`;
      els.sourceSelect.appendChild(opt);
    }

    const exists = Array.from(els.sourceSelect.options).some((o) => o.value === cur);
    state.activeSourceId = exists ? cur : 'catalog';
    els.sourceSelect.value = state.activeSourceId;
    els.btnRemoveList.classList.toggle('hidden', state.activeSourceId === 'catalog' || state.activeSourceId === 'favorites');
  }

  let virtualRaf = 0;
  function renderVirtualList() {
    if (virtualRaf) return;
    virtualRaf = requestAnimationFrame(() => {
      virtualRaf = 0;
      const total = state.filtered.length;
      els.channelSpacer.style.height = `${total * ROW_HEIGHT}px`;

      const vpHeight = els.channelViewport.clientHeight || 600;
      const scrollTop = els.channelViewport.scrollTop || 0;
      const start = Math.max(0, Math.floor(scrollTop / ROW_HEIGHT) - 4);
      const end = Math.min(total, Math.ceil((scrollTop + vpHeight) / ROW_HEIGHT) + 5);

      els.channelItems.style.transform = `translateY(${start * ROW_HEIGHT}px)`;
      const frag = document.createDocumentFragment();

      for (let i = start; i < end; i++) {
        const ch = state.filtered[i];
        const row = document.createElement('div');
        const allDead = isChannelAllDead(ch);
        row.className = 'ch-row' +
          (state.activeChannel && state.activeChannel.id === ch.id && state.activeChannel.url === ch.url ? ' active' : '') +
          (allDead ? ' is-failed' : '');

        const num = document.createElement('span');
        num.className = 'ch-num';
        num.textContent = ch.number > 0 ? String(ch.number) : '—';
        row.appendChild(num);

        const logoBox = document.createElement('div');
        logoBox.className = 'ch-logo-box';
        if (ch.logo && ch.logo.startsWith('https://')) {
          const img = document.createElement('img');
          img.className = 'ch-logo';
          img.loading = 'lazy';
          img.decoding = 'async';
          img.referrerPolicy = 'no-referrer';
          img.alt = '';
          img.src = ch.logo;
          img.addEventListener('error', () => {
            logoBox.textContent = (ch.name || 'TV').slice(0, 2).toUpperCase();
          });
          logoBox.appendChild(img);
        } else {
          logoBox.textContent = (ch.name || 'TV').slice(0, 2).toUpperCase();
        }
        row.appendChild(logoBox);

        const info = document.createElement('div');
        info.className = 'ch-info';

        const nameRow = document.createElement('div');
        nameRow.className = 'ch-name';
        const titleSpan = document.createElement('span');
        titleSpan.textContent = ch.name;
        nameRow.appendChild(titleSpan);

        if (ch.quality) {
          const qb = document.createElement('span');
          qb.className = 'ch-badge';
          qb.textContent = ch.quality;
          nameRow.appendChild(qb);
        }

        if (ch.tier === 1) {
          const tb = document.createElement('span');
          tb.className = 'ch-badge badge-tier1';
          tb.textContent = 'Oficial';
          tb.title = ch.provenance || 'Oficial verificado (legalFree)';
          nameRow.appendChild(tb);
        } else if (ch.tier === 2) {
          const tb = document.createElement('span');
          tb.className = 'ch-badge badge-tier2';
          tb.textContent = 'Curado';
          tb.title = ch.provenance || 'Curado (Free-TV)';
          nameRow.appendChild(tb);
        } else if (ch.tier === 3) {
          const tb = document.createElement('span');
          tb.className = 'ch-badge badge-tier3';
          tb.textContent = 'Comunitario';
          tb.title = ch.provenance || 'Comunitario (iptv-org)';
          nameRow.appendChild(tb);
        }

        if (ch.isGeoblocked) {
          const gb = document.createElement('span');
          gb.className = 'ch-badge badge-geo';
          gb.textContent = 'Ⓖ';
          gb.title = 'Transmisión con posible restricción geográfica';
          nameRow.appendChild(gb);
        }
        if (ch.isSd) {
          const sdb = document.createElement('span');
          sdb.className = 'ch-badge badge-sd';
          sdb.textContent = 'Ⓢ';
          sdb.title = 'Definición estándar (SD)';
          nameRow.appendChild(sdb);
        }
        if (ch.isYouTube || ch.kind === 'external') {
          const ytb = document.createElement('span');
          ytb.className = 'ch-badge badge-yt';
          ytb.textContent = 'Ⓨ';
          ytb.title = 'Transmisión de YouTube';
          nameRow.appendChild(ytb);
        }

        if (scoreAnime(ch) >= 3) {
          const ab = document.createElement('span');
          ab.className = 'ch-badge badge-anime';
          ab.textContent = 'Anime';
          ab.title = 'Canal de animación o anime';
          nameRow.appendChild(ab);
        }

        if (Array.isArray(ch.alternates) && ch.alternates.length > 0) {
          const ab = document.createElement('span');
          ab.className = 'ch-badge';
          ab.textContent = `${ch.alternates.length + 1}f`;
          nameRow.appendChild(ab);
        }
        if (allDead) {
          const fb = document.createElement('span');
          fb.className = 'ch-badge fail';
          fb.textContent = '!';
          nameRow.appendChild(fb);
        }
        info.appendChild(nameRow);

        const sub = document.createElement('div');
        sub.className = 'ch-sub';
        sub.textContent = [ch.country || ch.countryName, ch.group, ch.language].filter(Boolean).join(' · ');
        info.appendChild(sub);
        row.appendChild(info);

        const fav = document.createElement('button');
        const isFav = state.favorites.has(ch.id);
        fav.className = 'ch-fav-btn' + (isFav ? ' on' : '');
        fav.type = 'button';
        fav.textContent = isFav ? '★' : '☆';
        fav.title = 'Favorito';
        fav.addEventListener('click', (ev) => {
          ev.stopPropagation();
          toggleFavorite(ch.id);
        });
        row.appendChild(fav);

        row.addEventListener('click', () => selectChannel(ch));
        frag.appendChild(row);
      }

      els.channelItems.innerHTML = '';
      els.channelItems.appendChild(frag);
    });
  }

  async function toggleFavorite(key) {
    if (!key) return;
    try {
      const data = await apiJson('/api/favorites/toggle', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ key })
      });
      state.favorites = new Set(data.favorites || []);
      renderSourceSelect();
      if (state.activeChannel) updateNowPlayingHeader(state.activeChannel, state.activeSourceIndex);
      if (state.activeSourceId === 'favorites') applyFilters();
      else renderVirtualList();
    } catch { }
  }

  async function openInVlc(url) {
    if (!url) return;
    try {
      const res = await apiJson('/api/open-external', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ url })
      });
      if (!res.launched) {
        if (navigator.clipboard && navigator.clipboard.writeText) {
          await navigator.clipboard.writeText(url);
        }
        els.statusDetail.textContent = `${els.statusDetail.textContent}\n\nVLC no detectado; enlace copiado al portapapeles.`;
      }
    } catch {
      try { await navigator.clipboard.writeText(url); } catch { }
    }
  }

  async function loadCatalog(refresh = false) {
    els.channelCountLabel.textContent = refresh ? 'Actualizando iptv-org…' : 'Cargando catálogo…';
    try {
      const data = await apiJson(`/api/catalog?multi=1${refresh ? '&refresh=1' : ''}`);
      state.catalog = data.channels || [];
      state.playlists = data.playlists || [];
      state.favorites = new Set(data.prefs?.favorites || []);
      state.deadStreams = data.prefs?.deadStreams || Object.create(null);

      if (data.updatedUtc) {
        const dt = new Date(data.updatedUtc);
        els.catalogAgeLabel.textContent = `${data.fromCache ? 'Caché' : 'Actualizado'} ${dt.toLocaleTimeString('es', { hour: '2-digit', minute: '2-digit' })}`;
      }
      renderSourceSelect();
      renderCategorySelect(state.catalog);
      applyFilters();
    } catch (err) {
      els.channelCountLabel.textContent = `Error: ${err.message}`;
    }
  }

  async function onSourceChange() {
    state.activeSourceId = els.sourceSelect.value;
    els.btnRemoveList.classList.toggle('hidden', state.activeSourceId === 'catalog' || state.activeSourceId === 'favorites');
    if (state.activeSourceId !== 'catalog' && state.activeSourceId !== 'favorites') {
      if (!state.loadedPlaylists.has(state.activeSourceId)) {
        els.channelCountLabel.textContent = 'Cargando lista…';
        try {
          const data = await apiJson(`/api/playlist?id=${encodeURIComponent(state.activeSourceId)}`);
          if (data.playlist) state.loadedPlaylists.set(state.activeSourceId, data.playlist);
        } catch (err) {
          els.channelCountLabel.textContent = `Error: ${err.message}`;
          return;
        }
      }
      const pl = state.loadedPlaylists.get(state.activeSourceId);
      renderCategorySelect(pl?.channels || []);
    } else {
      renderCategorySelect(state.catalog);
    }
    applyFilters();
  }

  function stepChannel(delta) {
    if (state.filtered.length === 0) return;
    let idx = state.activeChannel
      ? state.filtered.findIndex((c) => c.id === state.activeChannel.id && c.url === state.activeChannel.url)
      : -1;
    idx = idx < 0 ? 0 : (idx + delta + state.filtered.length) % state.filtered.length;
    const next = state.filtered[idx];
    selectChannel(next);
    const targetTop = idx * ROW_HEIGHT;
    if (targetTop < els.channelViewport.scrollTop || targetTop + ROW_HEIGHT > els.channelViewport.scrollTop + els.channelViewport.clientHeight) {
      els.channelViewport.scrollTop = Math.max(0, targetTop - els.channelViewport.clientHeight / 2);
    }
  }

  function pushZapDigit(d) {
    state.zapDigits = (state.zapDigits + d).slice(0, 5);
    els.zapOsd.textContent = `Canal ${state.zapDigits}`;
    els.zapOsd.classList.remove('hidden');
    if (state.zapTimer) clearTimeout(state.zapTimer);
    state.zapTimer = setTimeout(commitZapDigits, ZAP_BUFFER_MS);
  }

  function commitZapDigits(immediate = false) {
    if (state.zapTimer) {
      clearTimeout(state.zapTimer);
      state.zapTimer = null;
    }
    const num = parseInt(state.zapDigits, 10);
    state.zapDigits = '';
    els.zapOsd.classList.add('hidden');
    if (!Number.isFinite(num) || num <= 0) return;
    const match = state.filtered.find((c) => c.number === num);
    if (match) {
      if (immediate) {
        if (state.switchTimer) {
          clearTimeout(state.switchTimer);
          state.switchTimer = null;
        }
        state.activeChannel = match;
        updateNowPlayingHeader(match, 0);
        renderVirtualList();
        startChannel(match, false);
      } else {
        selectChannel(match);
      }
    }
  }

  function wireKeyboard() {
    document.addEventListener('keydown', (ev) => {
      if (ev.ctrlKey && ev.shiftKey && ev.key.toLowerCase() === 'd') {
        ev.preventDefault();
        openDiagModal();
        return;
      }

      const tag = (document.activeElement?.tagName || '').toUpperCase();
      const inInput = tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || document.activeElement?.isContentEditable;
      if (inInput) {
        if (ev.key === 'Escape') {
          document.activeElement.blur();
          closeAddModal();
          closeDiagModal();
        }
        return;
      }

      if (ev.key === 'Escape') {
        closeAddModal();
        closeDiagModal();
        return;
      }

      if (!els.addModal.classList.contains('hidden') || !els.diagModal.classList.contains('hidden')) return;

      if (ev.key >= '0' && ev.key <= '9') {
        ev.preventDefault();
        pushZapDigit(ev.key);
      } else if (ev.key === 'Enter' && state.zapDigits.length > 0) {
        ev.preventDefault();
        commitZapDigits(true);
      } else if (ev.key === 'ArrowUp' || ev.key === 'PageUp') {
        ev.preventDefault();
        stepChannel(-1);
      } else if (ev.key === 'ArrowDown' || ev.key === 'PageDown') {
        ev.preventDefault();
        stepChannel(1);
      } else if (ev.key === ' ' || ev.key.toLowerCase() === 'k') {
        ev.preventDefault();
        if (els.videoEl.paused) els.videoEl.play().catch(() => { });
        else els.videoEl.pause();
      } else if (ev.key.toLowerCase() === 'm') {
        ev.preventDefault();
        els.videoEl.muted = !els.videoEl.muted;
      } else if (ev.key.toLowerCase() === 'f') {
        ev.preventDefault();
        if (document.fullscreenElement) document.exitFullscreen().catch(() => { });
        else els.videoWrap.requestFullscreen?.().catch(() => { });
      }
    });
  }

  function openAddModal() {
    els.addModalError.classList.add('hidden');
    els.addModalInfo.classList.add('hidden');
    els.addModal.classList.remove('hidden');
  }

  function closeAddModal() {
    els.addModal.classList.add('hidden');
  }

  function formatSkippedSummary(res) {
    const parts = [];
    if (res.skippedUnsupported > 0) parts.push(`${res.skippedUnsupported} esquema no HTTP`);
    if (res.skippedPrivate > 0) parts.push(`${res.skippedPrivate} privados`);
    if (res.skippedInvalid > 0) parts.push(`${res.skippedInvalid} inválidos`);
    if (res.skippedDuplicate > 0) parts.push(`${res.skippedDuplicate} duplicados`);
    return parts.length ? ` (omitidos: ${parts.join(', ')})` : '';
  }

  async function submitAddPlaylist() {
    els.addModalError.classList.add('hidden');
    els.addModalInfo.classList.add('hidden');
    const name = (els.plNameInput.value || '').trim();
    const url = (els.plUrlInput.value || '').trim();
    const content = (els.plTextInput.value || '').trim();

    if (!url && !content) {
      els.addModalError.textContent = 'Ingresa una URL pública http/https o pega/sube un archivo M3U.';
      els.addModalError.classList.remove('hidden');
      return;
    }

    els.btnSubmitAdd.disabled = true;
    try {
      const res = url
        ? await apiJson('/api/playlists/add-url', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ name, url })
        })
        : await apiJson('/api/playlists/add-text', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ name, content })
        });

      state.playlists = res.playlists || [];
      if (res.playlist) {
        state.loadedPlaylists.set(res.playlist.id, res.playlist);
        state.activeSourceId = res.playlist.id;
      }
      renderSourceSelect();
      await onSourceChange();
      els.addModalInfo.textContent = `Importados ${res.playlist?.channels?.length || 0} canales${formatSkippedSummary(res)}.`;
      els.addModalInfo.classList.remove('hidden');
      setTimeout(closeAddModal, 900);
    } catch (err) {
      els.addModalError.textContent = err.message;
      els.addModalError.classList.remove('hidden');
    } finally {
      els.btnSubmitAdd.disabled = false;
    }
  }

  function openDiagModal() {
    els.diagSaveMsg.classList.add('hidden');
    els.diagModal.classList.remove('hidden');
  }

  function closeDiagModal() {
    state.diagStopFlag = true;
    els.diagModal.classList.add('hidden');
  }

  async function testOneChannelForDiag(ch) {
    const start = performance.now();
    const src = getChannelSources(ch)[0];
    if (!src) return { number: ch.number, name: ch.name, url: ch.url, status: 'fail', category: 'offline', persistKind: 'dead', engine: '-', ms: 0 };

    const p = await probeStream(src.url, src.referrer, src.userAgent);
    if (state.diagStopFlag) return null;

    if (p.errorClass || p.status >= 400 || p.kind === 'html') {
      const fail = classifyFailure({ status: p.status, errorClass: p.errorClass, kind: p.kind, message: p.errorMessage, codecs: state.codecs });
      return {
        number: ch.number,
        name: ch.name,
        url: src.url,
        status: 'fail',
        category: fail.category,
        persistKind: fail.persistKind,
        engine: 'probe',
        ms: Math.round(performance.now() - start)
      };
    }

    let engine = pickEngineKind(src.url, p.kind);
    if (engine === 'probe') engine = 'hls';
    const diagSid = `diag_${++state.diagSidSeq}`;
    const proxyUrl = buildProxyUrl(src.url, src.referrer, src.userAgent, diagSid);

    return await new Promise((resolve) => {
      const v = document.createElement('video');
      v.muted = true;
      v.playsInline = true;
      v.style.display = 'none';
      document.body.appendChild(v);

      let tmpHls = null;
      let tmpTs = null;
      let done = false;

      const finish = (outcome) => {
        if (done) return;
        done = true;
        clearTimeout(timer);
        abortProxySession(diagSid);
        if (tmpHls) { try { tmpHls.destroy(); } catch { } }
        if (tmpTs) { try { tmpTs.destroy(); } catch { } }
        try { v.pause(); v.removeAttribute('src'); v.load(); v.remove(); } catch { }
        resolve({
          number: ch.number,
          name: ch.name,
          url: src.url,
          status: outcome.ok ? 'ok' : 'fail',
          category: outcome.category || (outcome.ok ? 'ok' : 'timeout'),
          persistKind: outcome.persistKind || (outcome.ok ? '' : 'dead'),
          engine,
          ms: Math.round(performance.now() - start)
        });
      };

      const timer = setTimeout(() => {
        finish({ ok: false, category: 'timeout', persistKind: 'transient' });
      }, DIAG_PER_CHANNEL_MS);

      v.addEventListener('loadeddata', () => finish({ ok: true }), { once: true });
      v.addEventListener('error', () => {
        const fail = classifyFailure({ mediaErrorCode: v.error?.code || 0, codecs: state.codecs });
        finish({ ok: false, category: fail.category, persistKind: fail.persistKind });
      }, { once: true });

      (async () => {
        try {
          if (engine === 'hls') {
            await loadScript('./vendor/hls.light.min.js');
            if (done || state.diagStopFlag) return finish({ ok: false, category: 'timeout', persistKind: 'transient' });
            tmpHls = new window.Hls(buildHlsConfig(window.Hls));
            tmpHls.on(window.Hls.Events.ERROR, async (_e, data) => {
              if (!data || !data.fatal || done) return;
              const net = await extractProxyErrorFromXhr(data.networkDetails);
              const fail = classifyFailure({
                status: data.response?.code || net.status,
                errorClass: net.errorClass,
                details: data.details,
                message: net.message,
                codecs: state.codecs
              });
              finish({ ok: false, category: fail.category, persistKind: fail.persistKind });
            });
            tmpHls.loadSource(proxyUrl);
            tmpHls.attachMedia(v);
            v.play().catch(() => { });
          } else if (engine === 'mpegts') {
            await loadScript('./vendor/mpegts.js');
            if (done || state.diagStopFlag) return finish({ ok: false, category: 'timeout', persistKind: 'transient' });
            tmpTs = window.mpegts.createPlayer({ type: 'mse', isLive: true, url: proxyUrl }, { enableWorker: false });
            tmpTs.on(window.mpegts.Events.ERROR, (_t, d, info) => {
              const fail = classifyFailure({ status: info?.code || 0, message: String(d || ''), codecs: state.codecs });
              finish({ ok: false, category: fail.category, persistKind: fail.persistKind });
            });
            tmpTs.attachMediaElement(v);
            tmpTs.load();
            v.play().catch(() => { });
          } else {
            v.src = proxyUrl;
            v.play().catch(() => { });
          }
        } catch {
          finish({ ok: false, category: 'offline', persistKind: 'dead' });
        }
      })();
    });
  }

  async function runDiagnostic() {
    if (state.diagRunning) return;
    const count = Math.max(1, Math.min(300, parseInt(els.diagCountInput.value, 10) || 100));
    els.diagCountInput.value = String(count);

    let pool;
    const scope = els.diagScopeSelect.value;
    if (scope === 'filtered') pool = state.filtered;
    else if (scope === 'es') pool = state.catalog.filter((c) => String(c.language || '').includes('Español'));
    else pool = state.catalog;

    const sample = pool.slice(0, count);
    state.diagRunning = true;
    state.diagStopFlag = false;
    state.diagResults = [];
    els.diagTableBody.innerHTML = '';
    els.btnDiagRun.classList.add('hidden');
    els.btnDiagStop.classList.remove('hidden');
    els.btnDiagJson.disabled = true;
    els.btnDiagCsv.disabled = true;
    els.btnDiagSave.disabled = true;
    els.btnDiagApply.disabled = true;

    let ok = 0; let auth = 0; let dead = 0; let codec = 0; let other = 0;
    const updateCounts = (i) => {
      els.diagProgressText.textContent = `Probados ${i}/${sample.length}…`;
      els.diagOk.textContent = String(ok);
      els.diagAuth.textContent = String(auth);
      els.diagDead.textContent = String(dead);
      els.diagCodec.textContent = String(codec);
      els.diagOther.textContent = String(other);
    };
    updateCounts(0);

    for (let i = 0; i < sample.length; i++) {
      if (state.diagStopFlag) break;
      const r = await testOneChannelForDiag(sample[i]);
      if (!r || state.diagStopFlag) break;
      state.diagResults.push(r);

      if (r.status === 'ok') ok++;
      else if (r.category === 'restricted' || r.category === 'blocked') auth++;
      else if (r.category === 'codec') codec++;
      else if (r.category === 'offline' || r.category === 'dns' || r.category === 'tls' || r.category === 'parsing') dead++;
      else other++;

      const tr = document.createElement('tr');
      for (const val of [r.number, r.name, r.status.toUpperCase(), r.category, r.engine, r.ms]) {
        const td = document.createElement('td');
        td.textContent = String(val);
        tr.appendChild(td);
      }
      els.diagTableBody.appendChild(tr);
      updateCounts(i + 1);
    }

    state.diagRunning = false;
    els.btnDiagRun.classList.remove('hidden');
    els.btnDiagStop.classList.add('hidden');
    const hasResults = state.diagResults.length > 0;
    els.btnDiagJson.disabled = !hasResults;
    els.btnDiagCsv.disabled = !hasResults;
    els.btnDiagSave.disabled = !hasResults;
    els.btnDiagApply.disabled = !hasResults;
    els.diagProgressText.textContent = state.diagStopFlag
      ? `Detenido tras ${state.diagResults.length}/${sample.length}.`
      : `Completado: ${state.diagResults.length} canales evaluados.`;
  }

  function downloadBlob(name, content, mime) {
    const blob = new Blob([content], { type: mime });
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = name;
    a.click();
    setTimeout(() => URL.revokeObjectURL(a.href), 1500);
  }

  function initApp() {
    [
      'codecPill', 'sourceSelect', 'searchInput', 'langSelect', 'categorySelect', 'hideDeadChk',
      'btnAddList', 'btnRemoveList', 'btnRefresh', 'btnDiag', 'btnClearCache',
      'channelCountLabel', 'catalogAgeLabel', 'channelViewport', 'channelSpacer', 'channelItems',
      'nowNumber', 'nowTitle', 'nowSub', 'engineBadge', 'btnAltSource', 'btnFavCurrent', 'btnVlcTop',
      'videoWrap', 'videoEl', 'zapOsd', 'codecWarn', 'codecWarnText', 'btnWarnVlc',
      'statusOverlay', 'statusIcon', 'statusTitle', 'statusDetail', 'statusActions',
      'btnRetry', 'btnNextAlt', 'btnOverlayVlc',
      'addModal', 'btnCloseAddModal', 'btnCancelAdd', 'btnSubmitAdd', 'plNameInput', 'plUrlInput',
      'plFileInput', 'plFileInfo', 'plTextInput', 'addModalError', 'addModalInfo',
      'diagModal', 'btnCloseDiag', 'diagCountInput', 'diagScopeSelect', 'btnDiagRun', 'btnDiagStop',
      'diagProgressText', 'diagOk', 'diagAuth', 'diagDead', 'diagCodec', 'diagOther',
      'diagTableBody', 'diagSaveMsg', 'btnDiagJson', 'btnDiagCsv', 'btnDiagSave', 'btnDiagApply',
      'btnSources', 'sourcesModal', 'btnCloseSources', 'btnCloseSourcesFoot', 'btnRefreshSources',
      'srcTier1Count', 'srcTier2Count', 'srcTier3Count',
      'animSubfilters', 'btnAnimLangEs', 'btnAnimLangJp', 'btnAnimLangEn', 'btnAnimLangAll',
      'chkAnimeOnly', 'animCountrySelect'
    ].forEach((id) => { els[id] = $(id); });

    detectCodecSupport();
    wireVideoEvents();
    wireKeyboard();

    els.channelViewport.addEventListener('scroll', renderVirtualList);
    window.addEventListener('resize', renderVirtualList);
    els.sourceSelect.addEventListener('change', onSourceChange);
    els.searchInput.addEventListener('input', applyFilters);
    els.langSelect.addEventListener('change', applyFilters);
    els.categorySelect.addEventListener('change', applyFilters);
    els.hideDeadChk.addEventListener('change', applyFilters);

    els.btnRefresh.addEventListener('click', () => loadCatalog(true));
    els.btnClearCache.addEventListener('click', async () => {
      await apiJson('/api/cache/clear', { method: 'POST' });
      await loadCatalog(true);
    });

    els.btnRemoveList.addEventListener('click', async () => {
      const id = state.activeSourceId;
      if (id === 'catalog' || id === 'favorites') return;
      const res = await apiJson('/api/playlists/remove', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ id })
      });
      state.loadedPlaylists.delete(id);
      state.playlists = res.playlists || [];
      state.activeSourceId = 'catalog';
      renderSourceSelect();
      await onSourceChange();
    });

    els.btnFavCurrent.addEventListener('click', () => {
      if (state.activeChannel) toggleFavorite(state.activeChannel.id);
    });
    els.btnAltSource.addEventListener('click', () => {
      if (!state.activeChannel || state.activeSources.length <= 1) return;
      const next = (state.activeSourceIndex + 1) % state.activeSources.length;
      tryPlaySourceIndex(state.activeChannel, next, true);
    });

    const getVlcUrl = () => state.activeSources[state.activeSourceIndex]?.url || state.activeChannel?.url || '';
    els.btnVlcTop.addEventListener('click', () => openInVlc(getVlcUrl()));
    els.btnOverlayVlc.addEventListener('click', () => openInVlc(getVlcUrl()));
    els.btnWarnVlc.addEventListener('click', () => openInVlc(getVlcUrl()));

    els.btnRetry.addEventListener('click', () => {
      if (state.activeChannel) playChannel(state.activeChannel, true);
    });
    els.btnNextAlt.addEventListener('click', () => {
      if (!state.activeChannel || state.activeSources.length <= 1) return;
      const next = (state.activeSourceIndex + 1) % state.activeSources.length;
      tryPlaySourceIndex(state.activeChannel, next, true);
    });

    els.btnAddList.addEventListener('click', openAddModal);
    els.btnCloseAddModal.addEventListener('click', closeAddModal);
    els.btnCancelAdd.addEventListener('click', closeAddModal);
    els.btnSubmitAdd.addEventListener('click', submitAddPlaylist);

    els.plFileInput.addEventListener('change', async () => {
      els.addModalError.classList.add('hidden');
      const f = els.plFileInput.files?.[0];
      if (!f) return;
      if (f.size > MAX_LOCAL_FILE_BYTES) {
        els.addModalError.textContent = `El archivo supera el máximo permitido de 10 MB (${(f.size / (1024 * 1024)).toFixed(1)} MB).`;
        els.addModalError.classList.remove('hidden');
        els.plFileInput.value = '';
        return;
      }
      els.plFileInfo.textContent = `${f.name} (${Math.round(f.size / 1024)} KB)`;
      if (!els.plNameInput.value.trim()) {
        els.plNameInput.value = f.name.replace(/\.(m3u8?|txt)$/i, '');
      }
      els.plTextInput.value = await f.text();
    });

    els.btnDiag.addEventListener('click', openDiagModal);
    els.btnCloseDiag.addEventListener('click', closeDiagModal);
    els.btnDiagRun.addEventListener('click', runDiagnostic);
    els.btnDiagStop.addEventListener('click', () => { state.diagStopFlag = true; });

    els.btnDiagJson.addEventListener('click', () => {
      downloadBlob(`lex_tv_diag_${Date.now()}.json`, JSON.stringify(state.diagResults, null, 2), 'application/json');
    });
    els.btnDiagCsv.addEventListener('click', () => {
      const header = 'number,name,status,category,engine,ms,url\n';
      const rows = state.diagResults.map((r) =>
        [r.number, `"${String(r.name).replace(/"/g, '""')}"`, r.status, r.category, r.engine, r.ms, `"${r.url}"`].join(',')
      );
      downloadBlob(`lex_tv_diag_${Date.now()}.csv`, header + rows.join('\n'), 'text/csv;charset=utf-8');
    });
    els.btnDiagSave.addEventListener('click', async () => {
      try {
        const res = await apiJson('/api/diag/save', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ results: state.diagResults })
        });
        els.diagSaveMsg.textContent = `Reporte guardado: ${res.path}`;
        els.diagSaveMsg.classList.remove('hidden');
      } catch (err) {
        els.diagSaveMsg.textContent = `Error: ${err.message}`;
        els.diagSaveMsg.classList.remove('hidden');
      }
    });
    els.btnDiagApply.addEventListener('click', async () => {
      let applied = 0;
      for (const r of state.diagResults) {
        if (r.status === 'fail' && r.persistKind && r.persistKind !== 'transient') {
          await reportFailureMark(r.url, { persistKind: r.persistKind, title: `Diag: ${r.category}` });
          applied++;
        }
      }
      els.diagSaveMsg.textContent = `Se aplicaron ${applied} marcas al caché.`;
      els.diagSaveMsg.classList.remove('hidden');
    });

    async function openSourcesModal() {
      if (els.sourcesModal) els.sourcesModal.classList.remove('hidden');
      try {
        const data = await apiJson('/api/sources');
        if (data) {
          if (els.srcTier1Count) els.srcTier1Count.textContent = `${data.tier1Count || 0} canales`;
          if (els.srcTier2Count) els.srcTier2Count.textContent = `${data.tier2Count || 0} canales`;
          if (els.srcTier3Count) els.srcTier3Count.textContent = `${data.tier3Count || 0} canales`;
        }
      } catch { }
    }

    function closeSourcesModal() {
      if (els.sourcesModal) els.sourcesModal.classList.add('hidden');
    }

    if (els.btnSources) els.btnSources.addEventListener('click', openSourcesModal);
    if (els.btnCloseSources) els.btnCloseSources.addEventListener('click', closeSourcesModal);
    if (els.btnCloseSourcesFoot) els.btnCloseSourcesFoot.addEventListener('click', closeSourcesModal);
    if (els.btnRefreshSources) {
      els.btnRefreshSources.addEventListener('click', async () => {
        closeSourcesModal();
        try { await apiJson('/api/sources/refresh', { method: 'POST' }); } catch { }
        await loadCatalog(true);
      });
    }

    const animPills = [
      { el: els.btnAnimLangEs, lang: 'es' },
      { el: els.btnAnimLangJp, lang: 'jp' },
      { el: els.btnAnimLangEn, lang: 'en' },
      { el: els.btnAnimLangAll, lang: 'all' }
    ];

    animPills.forEach(({ el, lang }) => {
      if (el) {
        el.addEventListener('click', () => {
          state.animLang = lang;
          animPills.forEach(p => p.el?.classList.toggle('active', p.lang === lang));
          applyFilters();
        });
      }
    });

    if (els.chkAnimeOnly) {
      els.chkAnimeOnly.addEventListener('change', () => {
        state.animeOnly = !!els.chkAnimeOnly.checked;
        applyFilters();
      });
    }

    if (els.animCountrySelect) {
      els.animCountrySelect.addEventListener('change', () => {
        state.animCountry = els.animCountrySelect.value;
        applyFilters();
      });
    }

    window.addEventListener('beforeunload', destroyActivePlayer);

    loadCatalog(false);
    if (new URLSearchParams(window.location.search).get('diag') === '1') {
      openDiagModal();
    }
  }

  window.addEventListener('DOMContentLoaded', initApp);
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = { classifyFailure, pickEngineKind, buildHlsConfig };
}

