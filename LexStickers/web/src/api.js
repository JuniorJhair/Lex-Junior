const API_BASE_STORAGE_KEY = 'lexsticker_api_base';
const CLIENT_SESSION_STORAGE_KEY = 'lexsticker_client_session';

export function getClientSessionId() {
  try {
    const existing = localStorage.getItem(CLIENT_SESSION_STORAGE_KEY);
    if (existing && /^[a-zA-Z0-9_-]{16,64}$/.test(existing)) {
      return existing;
    }
    let next = '';
    if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
      next = crypto.randomUUID();
    } else if (typeof crypto !== 'undefined' && typeof crypto.getRandomValues === 'function') {
      const bytes = new Uint8Array(16);
      crypto.getRandomValues(bytes);
      next = Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
    } else {
      next = `${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 12)}_${Math.random().toString(36).slice(2, 8)}`;
    }
    localStorage.setItem(CLIENT_SESSION_STORAGE_KEY, next);
    return next;
  } catch {
    return 'guest_session_fallback_0001';
  }
}

export function getApiBase() {
  try {
    const saved = localStorage.getItem(API_BASE_STORAGE_KEY);
    if (saved) {
      const parsed = new URL(saved.trim());
      if (parsed.protocol === 'http:' || parsed.protocol === 'https:') {
        return parsed.origin + parsed.pathname.replace(/\/+$/, '');
      }
    }
  } catch {
    // ignore invalid stored URL
  }
  const envUrl = import.meta.env?.VITE_API_URL;
  if (envUrl) return String(envUrl).trim().replace(/\/+$/, '');
  return '';
}

export function setApiBase(rawUrl) {
  const cleaned = String(rawUrl || '').trim();
  if (!cleaned) {
    localStorage.removeItem(API_BASE_STORAGE_KEY);
    return '';
  }
  const parsed = new URL(cleaned);
  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
    throw new Error('La URL del servidor debe comenzar con http:// o https://');
  }
  const normalized = parsed.origin + parsed.pathname.replace(/\/+$/, '');
  localStorage.setItem(API_BASE_STORAGE_KEY, normalized);
  return normalized;
}

export function resolveMediaUrl(relativeOrAbs) {
  if (!relativeOrAbs) return '';
  if (/^(https?:|blob:|data:)/i.test(relativeOrAbs)) return relativeOrAbs;
  return `${getApiBase()}${relativeOrAbs}`;
}

async function apiFetch(path, options = {}) {
  const base = getApiBase();
  const signal =
    options.signal ||
    (typeof AbortSignal !== 'undefined' && typeof AbortSignal.timeout === 'function'
      ? AbortSignal.timeout(path === '/api/health' ? 15000 : 120000)
      : undefined);

  const headers = new Headers(options.headers || {});
  headers.set('x-lex-session', getClientSessionId());

  const res = await fetch(`${base}${path}`, { ...options, headers, signal });
  const contentType = res.headers.get('content-type') || '';
  if (!contentType.includes('application/json')) {
    throw new Error(
      res.ok
        ? 'El servidor no respondió JSON válido.'
        : `Servidor no disponible (${res.status}).`
    );
  }
  const data = await res.json().catch(() => ({}));
  if (!res.ok) {
    throw new Error(data.error || `Error del servidor (${res.status})`);
  }
  return data;
}

export const api = {
  health: () => apiFetch('/api/health'),
  uploadMedia: (file) => {
    const fd = new FormData();
    fd.append('file', file);
    return apiFetch('/api/media/upload', { method: 'POST', body: fd });
  },
  uploadAudio: (fileOrBlob, filename = 'audio.webm') => {
    const fd = new FormData();
    fd.append('file', fileOrBlob, filename);
    return apiFetch('/api/media/audio-upload', { method: 'POST', body: fd });
  },
  buildSticker: (payload) =>
    apiFetch('/api/stickers/build', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload),
    }),
  waStatus: () => apiFetch('/api/whatsapp/status'),
  waConnect: (payload = {}) =>
    apiFetch('/api/whatsapp/connect', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload),
    }),
  waLogout: () => apiFetch('/api/whatsapp/logout', { method: 'POST' }),
  waSend: (payload) =>
    apiFetch('/api/whatsapp/send', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload),
    }),
};

export async function shareOrDownloadBlob(blob, filename, title = 'LexSticker') {
  const file = new File([blob], filename, { type: blob.type || 'application/octet-stream' });
  if (navigator.canShare && navigator.canShare({ files: [file] })) {
    try {
      await navigator.share({ files: [file], title });
      return;
    } catch (err) {
      if (err?.name === 'AbortError') return;
    }
  }

  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  document.body.append(a);
  a.click();
  setTimeout(() => {
    URL.revokeObjectURL(url);
    a.remove();
  }, 1500);
}

export async function shareOrDownloadFromUrl(fileUrl, filename) {
  const res = await fetch(resolveMediaUrl(fileUrl));
  if (!res.ok) throw new Error('No se pudo descargar el archivo generado.');
  const blob = await res.blob();
  await shareOrDownloadBlob(blob, filename);
}

export async function probeAudioDurationLocal(audioUrl, fallbackSec = 5) {
  return new Promise((resolve) => {
    const a = document.createElement('audio');
    a.preload = 'metadata';
    a.src = audioUrl;
    const done = (val) => {
      a.src = '';
      resolve(Number.isFinite(val) && val > 0 ? val : fallbackSec);
    };
    a.onloadedmetadata = () => done(a.duration);
    a.onerror = () => done(fallbackSec);
    setTimeout(() => done(fallbackSec), 2000);
  });
}

function pickRecorderMime(withAudio) {
  const candidates = withAudio
    ? ['video/mp4;codecs=avc1,mp4a.40.2', 'video/mp4', 'video/webm;codecs=vp9,opus', 'video/webm;codecs=vp8,opus', 'video/webm']
    : ['video/mp4', 'video/webm;codecs=vp9', 'video/webm;codecs=vp8', 'video/webm'];
  return candidates.find((m) => MediaRecorder.isTypeSupported(m)) || '';
}

export async function exportLocalClipOffline({
  mediaUrl,
  kind,
  start,
  duration,
  crop,
  audioMode = 'none',
  customAudioUrl = null,
  audioStart = 0,
}) {
  const canvas = document.createElement('canvas');
  canvas.width = 512;
  canvas.height = 512;
  const ctx = canvas.getContext('2d');

  const wantsCustomAudio = (audioMode === 'upload' || audioMode === 'mic') && Boolean(customAudioUrl);
  const wantsOriginalVideoAudio = kind === 'video' && audioMode === 'original';

  // Static image without audio -> crisp 512x512 WebP sticker directly
  if (kind === 'image' && !wantsCustomAudio) {
    const img = new Image();
    img.crossOrigin = 'anonymous';
    await new Promise((resolve, reject) => {
      img.onload = resolve;
      img.onerror = reject;
      img.src = mediaUrl;
    });
    const minSide = Math.min(img.naturalWidth, img.naturalHeight);
    const cw = Math.round(crop.size * minSide);
    const cx = Math.round(crop.x * img.naturalWidth);
    const cy = Math.round(crop.y * img.naturalHeight);
    ctx.drawImage(img, cx, cy, cw, cw, 0, 0, 512, 512);
    const blob = await new Promise((resolve) => canvas.toBlob(resolve, 'image/webp', 0.86));
    return {
      blob,
      filename: 'lexsticker.webp',
      isImage: true,
      hasAudio: false,
      previewUrl: URL.createObjectURL(blob),
    };
  }

  const stream = canvas.captureStream(24);
  let audioCtx = null;
  let extAudioEl = null;
  let video = null;

  try {
    let imgEl = null;
    if (kind === 'image') {
      imgEl = new Image();
      imgEl.crossOrigin = 'anonymous';
      await new Promise((resolve, reject) => {
        imgEl.onload = resolve;
        imgEl.onerror = reject;
        imgEl.src = mediaUrl;
      });
    } else {
      video = document.createElement('video');
      video.src = mediaUrl;
      video.crossOrigin = 'anonymous';
      video.playsInline = true;
      await new Promise((resolve, reject) => {
        video.onloadedmetadata = resolve;
        video.onerror = reject;
      });
      video.currentTime = start;
      await new Promise((r) => {
        video.onseeked = r;
      });
    }

    const AudioCtx = window.AudioContext || window.webkitAudioContext;
    if (wantsCustomAudio && AudioCtx) {
      extAudioEl = document.createElement('audio');
      extAudioEl.crossOrigin = 'anonymous';
      extAudioEl.src = customAudioUrl;
      await new Promise((resolve, reject) => {
        extAudioEl.onloadedmetadata = resolve;
        extAudioEl.onerror = reject;
      });
      extAudioEl.currentTime = Math.max(0, Math.min(audioStart || 0, Math.max(0, (extAudioEl.duration || duration) - 0.1)));
      await new Promise((r) => {
        const done = () => r();
        extAudioEl.onseeked = done;
        setTimeout(done, 400);
      });

      audioCtx = new AudioCtx();
      const srcNode = audioCtx.createMediaElementSource(extAudioEl);
      const destNode = audioCtx.createMediaStreamDestination();
      srcNode.connect(destNode);
      destNode.stream.getAudioTracks().forEach((t) => stream.addTrack(t));
    } else if (wantsOriginalVideoAudio && video) {
      if (typeof video.captureStream === 'function') {
        const vStream = video.captureStream();
        vStream.getAudioTracks().forEach((t) => stream.addTrack(t));
      } else if (AudioCtx) {
        audioCtx = new AudioCtx();
        const srcNode = audioCtx.createMediaElementSource(video);
        const destNode = audioCtx.createMediaStreamDestination();
        srcNode.connect(destNode);
        destNode.stream.getAudioTracks().forEach((t) => stream.addTrack(t));
      }
    }

    const mime = pickRecorderMime(wantsCustomAudio || wantsOriginalVideoAudio);
    const rec = mime ? new MediaRecorder(stream, { mimeType: mime }) : new MediaRecorder(stream);
    const chunks = [];
    rec.ondataavailable = (e) => {
      if (e.data.size > 0) chunks.push(e.data);
    };

    if (video) {
      video.muted = audioMode !== 'original';
    }

    rec.start(100);
    if (video) await video.play();
    if (extAudioEl) await extAudioEl.play().catch(() => {});

    const startedAt = performance.now();
    const targetMs = Math.max(500, duration * 1000);
    const stopAt = start + duration;

    await new Promise((resolve) => {
      const draw = () => {
        const elapsedMs = performance.now() - startedAt;
        if (elapsedMs >= targetMs || (video && (video.currentTime >= stopAt || video.ended))) {
          video?.pause();
          extAudioEl?.pause();
          resolve();
          return;
        }
        if (imgEl) {
          const minSide = Math.min(imgEl.naturalWidth, imgEl.naturalHeight);
          const cw = Math.round(crop.size * minSide);
          const cx = Math.round(crop.x * imgEl.naturalWidth);
          const cy = Math.round(crop.y * imgEl.naturalHeight);
          ctx.drawImage(imgEl, cx, cy, cw, cw, 0, 0, 512, 512);
        } else if (video) {
          const minSide = Math.min(video.videoWidth, video.videoHeight);
          const cw = Math.round(crop.size * minSide);
          const cx = Math.round(crop.x * video.videoWidth);
          const cy = Math.round(crop.y * video.videoHeight);
          ctx.drawImage(video, cx, cy, cw, cw, 0, 0, 512, 512);
        }
        requestAnimationFrame(draw);
      };
      requestAnimationFrame(draw);
    });

    const outType = rec.mimeType || mime || 'video/webm';
    const blob = await new Promise((resolve) => {
      rec.onstop = () => resolve(new Blob(chunks, { type: outType }));
      rec.stop();
    });
    const ext = outType.includes('mp4') ? 'mp4' : 'webm';
    return {
      blob,
      filename: `lexsticker.${ext}`,
      isImage: false,
      hasAudio: wantsCustomAudio || wantsOriginalVideoAudio,
      previewUrl: URL.createObjectURL(blob),
    };
  } finally {
    if (audioCtx) {
      audioCtx.close().catch(() => {});
    }
  }
}
