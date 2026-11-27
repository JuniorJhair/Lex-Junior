import { spawn } from 'node:child_process';
import crypto from 'node:crypto';
import fs from 'node:fs';
import fsp from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import ffmpegStatic from 'ffmpeg-static';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const SERVER_ROOT = path.resolve(__dirname, '..');
export const DATA_DIR = path.join(SERVER_ROOT, 'data');
export const SOURCES_DIR = path.join(DATA_DIR, 'sources');
export const OUTPUTS_DIR = path.join(DATA_DIR, 'outputs');

const FFMPEG_BIN = process.env.FFMPEG_PATH || ffmpegStatic || 'ffmpeg';

const MAX_VIDEO_DURATION = 10.0;
const MIN_DURATION = 0.5;
const MAX_WEBP_ANIMATED_BYTES = 490 * 1024;
const MAX_WEBP_STATIC_BYTES = 98 * 1024;
const MAX_CONCURRENT_BUILDS = 1;

let activeBuilds = 0;
const buildWaitQueue = [];

async function withBuildSlot(taskFn) {
  if (activeBuilds >= MAX_CONCURRENT_BUILDS) {
    await new Promise((resolve) => buildWaitQueue.push(resolve));
  }
  activeBuilds += 1;
  try {
    return await taskFn();
  } finally {
    activeBuilds = Math.max(0, activeBuilds - 1);
    const next = buildWaitQueue.shift();
    if (next) next();
  }
}

export function assertValidId(value, label = 'Identificador') {
  const s = String(value || '').trim();
  if (!/^[a-zA-Z0-9_-]{8,64}$/.test(s)) {
    throw new Error(`${label} inválido.`);
  }
  return s;
}

export function resolveSafeInside(baseDir, ...segments) {
  const root = path.resolve(baseDir);
  const target = path.resolve(root, ...segments);
  if (target !== root && !target.startsWith(root + path.sep)) {
    throw new Error('Ruta fuera de alcance permitido.');
  }
  return target;
}

export async function ensureMediaDirs() {
  await Promise.all([
    fsp.mkdir(SOURCES_DIR, { recursive: true }),
    fsp.mkdir(OUTPUTS_DIR, { recursive: true }),
  ]);
}

export async function cleanupExpiredMediaDirs(ttlMs = 20 * 60 * 1000) {
  const now = Date.now();
  for (const baseDir of [SOURCES_DIR, OUTPUTS_DIR]) {
    let sessions;
    try {
      sessions = await fsp.readdir(baseDir, { withFileTypes: true });
    } catch {
      continue;
    }

    for (const sEntry of sessions) {
      if (!sEntry.isDirectory()) continue;
      const sessionDir = path.join(baseDir, sEntry.name);
      let children;
      try {
        children = await fsp.readdir(sessionDir, { withFileTypes: true });
      } catch {
        continue;
      }

      for (const child of children) {
        const childPath = path.join(sessionDir, child.name);
        try {
          const stat = await fsp.stat(childPath);
          if (now - stat.mtimeMs > ttlMs) {
            await fsp.rm(childPath, { recursive: true, force: true });
          }
        } catch {
          // ignore
        }
      }

      try {
        const remaining = await fsp.readdir(sessionDir);
        if (remaining.length === 0) {
          const sStat = await fsp.stat(sessionDir);
          if (now - sStat.mtimeMs > ttlMs) {
            await fsp.rm(sessionDir, { recursive: true, force: true });
          }
        }
      } catch {
        // ignore
      }
    }
  }
}

function runProcess(bin, args, { timeoutMs = 120_000 } = {}) {
  const effectiveArgs =
    bin === FFMPEG_BIN && args.length >= 2 && !args.includes('-threads')
      ? [...args.slice(0, -1), '-threads', '1', args[args.length - 1]]
      : args;
  return new Promise((resolve, reject) => {
    const child = spawn(bin, effectiveArgs, { windowsHide: true });
    let stdout = '';
    let stderr = '';
    const timer = setTimeout(() => {
      child.kill('SIGKILL');
      reject(new Error(`Proceso excedió ${Math.round(timeoutMs / 1000)}s`));
    }, timeoutMs);

    child.stdout.on('data', (d) => {
      stdout += d.toString();
    });
    child.stderr.on('data', (d) => {
      stderr += d.toString();
    });
    child.on('error', (err) => {
      clearTimeout(timer);
      reject(err);
    });
    child.on('close', (code) => {
      clearTimeout(timer);
      if (code === 0) resolve({ stdout, stderr });
      else reject(new Error(stderr.trim() || stdout.trim() || `Código de salida ${code}`));
    });
  });
}

export async function probeMediaWithFfmpeg(filePath) {
  // ffmpeg prints stream info to stderr even when exiting with 1 (no output specified)
  const info = await new Promise((resolve) => {
    const child = spawn(FFMPEG_BIN, ['-hide_banner', '-i', filePath], { windowsHide: true });
    let stderr = '';
    child.stderr.on('data', (d) => {
      stderr += d.toString();
    });
    child.on('close', () => resolve(stderr));
    child.on('error', () => resolve(''));
  });

  let duration = 0;
  const durMatch = info.match(/Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)/);
  if (durMatch) {
    duration = Number(durMatch[1]) * 3600 + Number(durMatch[2]) * 60 + Number(durMatch[3]);
  }

  let width = 0;
  let height = 0;
  const resMatch = info.match(/Video:.*?,\s*(\d{2,5})x(\d{2,5})/);
  if (resMatch) {
    width = Number(resMatch[1]);
    height = Number(resMatch[2]);
  }

  const hasAudio = /Stream #.*Audio:/i.test(info);
  const isImage = /\b(image2|png_pipe|mjpeg|webp_pipe)\b/i.test(info) || duration === 0;
  return { duration, width, height, hasAudio, isImage };
}

export async function findSourceFile(sessionId, sourceId, prefixHint = '') {
  const safeSid = assertValidId(sessionId, 'Sesión');
  const safeId = assertValidId(sourceId, 'Archivo');
  const dir = resolveSafeInside(SOURCES_DIR, safeSid, safeId);
  const files = await fsp.readdir(dir);
  const picked = (prefixHint && files.find((f) => f.startsWith(prefixHint))) || files[0];
  if (!picked) throw new Error('Archivo fuente no encontrado.');
  return resolveSafeInside(dir, picked);
}

function buildCropFilter(crop, srcW, srcH, targetSide) {
  const safeW = Math.max(2, srcW || 512);
  const safeH = Math.max(2, srcH || 512);
  let cw = Math.round((crop?.size ?? 1) * Math.min(safeW, safeH));
  cw = Math.max(16, Math.min(cw, safeW, safeH));
  // Ensure even dimensions for H.264 compatibility
  cw = cw % 2 === 0 ? cw : cw - 1;
  let cx = Math.round((crop?.x ?? 0) * safeW);
  let cy = Math.round((crop?.y ?? 0) * safeH);
  cx = Math.max(0, Math.min(cx, safeW - cw));
  cy = Math.max(0, Math.min(cy, safeH - cw));
  return `crop=${cw}:${cw}:${cx}:${cy},scale=${targetSide}:${targetSide}:flags=lanczos`;
}

export async function buildStickerPackage({
  sessionId,
  sourceId,
  start = 0,
  duration = 5,
  maxDuration = MAX_VIDEO_DURATION,
  crop = { x: 0, y: 0, size: 1 },
  audioMode = 'none',
  audioId = null,
  audioStart = 0,
}) {
  return withBuildSlot(async () => {
    const safeSid = assertValidId(sessionId, 'Sesión');
    await ensureMediaDirs();
    const sourcePath = await findSourceFile(safeSid, sourceId);
    const srcMeta = await probeMediaWithFfmpeg(sourcePath);

    const safeStart = Math.max(0, Number(start) || 0);
    const capDur = Math.min(MAX_VIDEO_DURATION, Math.max(MIN_DURATION, Number(maxDuration) || MAX_VIDEO_DURATION));
    let safeDur = Math.max(MIN_DURATION, Math.min(capDur, Number(duration) || 5));
    if (!srcMeta.isImage && srcMeta.duration > 0) {
      safeDur = Math.max(MIN_DURATION, Math.min(safeDur, Math.max(MIN_DURATION, srcMeta.duration - safeStart)));
    }

    const jobId = crypto.randomUUID();
    const outDir = resolveSafeInside(OUTPUTS_DIR, safeSid, jobId);
    await fsp.mkdir(outDir, { recursive: true });

    const webpPath = path.join(outDir, 'sticker.webp');
    const oggPath = path.join(outDir, 'audio.ogg');
    const mp4Path = path.join(outDir, 'clip.mp4');

    const isAnimated = !srcMeta.isImage || /\.gif$/i.test(sourcePath);

    // 1. Build WebP sticker staying strictly within WhatsApp size limits
    let finalSide = 512;
    let finalFps = isAnimated ? 15 : 1;

    if (!isAnimated) {
      const qualities = [82, 68, 52, 38];
      for (const q of qualities) {
        const vf = buildCropFilter(crop, srcMeta.width, srcMeta.height, 512);
        await runProcess(FFMPEG_BIN, [
          '-y',
          '-i',
          sourcePath,
          '-vf',
          vf,
          '-vcodec',
          'libwebp',
          '-lossless',
          '0',
          '-q:v',
          String(q),
          '-frames:v',
          '1',
          webpPath,
        ]);
        const stat = await fsp.stat(webpPath);
        if (stat.size <= MAX_WEBP_STATIC_BYTES) break;
      }
    } else {
      // Adaptive compression ladder to guarantee animated WebP <= 490 KB
      const attempts = [
        { side: 512, fps: 15, q: 65 },
        { side: 512, fps: 12, q: 52 },
        { side: 512, fps: 10, q: 40 },
        { side: 384, fps: 12, q: 48 },
        { side: 384, fps: 10, q: 36 },
        { side: 320, fps: 10, q: 32 },
        { side: 256, fps: 8, q: 28 },
      ];
      for (const step of attempts) {
        const vf = `${buildCropFilter(crop, srcMeta.width, srcMeta.height, step.side)},fps=${step.fps},format=yuva420p`;
        await runProcess(FFMPEG_BIN, [
          '-y',
          '-ss',
          safeStart.toFixed(3),
          '-t',
          safeDur.toFixed(3),
          '-i',
          sourcePath,
          '-an',
          '-vf',
          vf,
          '-vcodec',
          'libwebp',
          '-loop',
          '0',
          '-preset',
          'default',
          '-compression_level',
          '6',
          '-q:v',
          String(step.q),
          webpPath,
        ]);
        finalSide = step.side;
        finalFps = step.fps;
        const stat = await fsp.stat(webpPath);
        if (stat.size <= MAX_WEBP_ANIMATED_BYTES) break;
      }
    }

    // 2. Build Opus OGG audio track if requested
    let hasOutputAudio = false;
    let audioBytes = 0;
    if (audioMode !== 'none') {
      let audioInputPath = null;
      let effectiveAudioStart = Math.max(0, Number(audioStart) || 0);

      if (audioMode === 'original') {
        if (!srcMeta.hasAudio) {
          throw new Error('El video seleccionado no tiene pista de audio original. Elige "Subir audio", "Grabar voz" o "Sin sonido".');
        }
        audioInputPath = sourcePath;
        effectiveAudioStart = Number.isFinite(Number(audioStart)) ? Math.max(0, Number(audioStart)) : safeStart;
      } else if (audioId) {
        audioInputPath = await findSourceFile(safeSid, audioId);
      }

      if (audioInputPath) {
        await runProcess(FFMPEG_BIN, [
          '-y',
          '-ss',
          effectiveAudioStart.toFixed(3),
          '-t',
          safeDur.toFixed(3),
          '-i',
          audioInputPath,
          '-vn',
          '-ac',
          '1',
          '-ar',
          '48000',
          '-c:a',
          'libopus',
          '-b:a',
          '24k',
          '-vbr',
          'on',
          oggPath,
        ]);
        const aStat = await fsp.stat(oggPath);
        hasOutputAudio = aStat.size > 0;
        audioBytes = aStat.size;
      }
    }

    // 3. Build square 512x512 MP4 (ready for WhatsApp's native Create Sticker editor or direct share)
    const mp4Vf = `${buildCropFilter(crop, srcMeta.width, srcMeta.height, 512)},format=yuv420p`;
    if (!isAnimated) {
      const mp4Inputs = hasOutputAudio
        ? ['-y', '-loop', '1', '-t', safeDur.toFixed(3), '-i', sourcePath, '-i', oggPath]
        : ['-y', '-loop', '1', '-t', safeDur.toFixed(3), '-i', sourcePath];
      const mp4Args = hasOutputAudio
        ? [...mp4Inputs, '-vf', mp4Vf, '-r', '15', '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '24', '-c:a', 'aac', '-b:a', '96k', '-shortest', '-movflags', '+faststart', mp4Path]
        : [...mp4Inputs, '-vf', mp4Vf, '-r', '15', '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '24', '-an', '-movflags', '+faststart', mp4Path];
      await runProcess(FFMPEG_BIN, mp4Args);
    } else {
      const mp4Args = hasOutputAudio
        ? [
            '-y',
            '-ss',
            safeStart.toFixed(3),
            '-t',
            safeDur.toFixed(3),
            '-i',
            sourcePath,
            '-i',
            oggPath,
            '-map',
            '0:v:0',
            '-map',
            '1:a:0',
            '-vf',
            mp4Vf,
            '-c:v',
            'libx264',
            '-preset',
            'veryfast',
            '-crf',
            '24',
            '-c:a',
            'aac',
            '-b:a',
            '96k',
            '-shortest',
            '-movflags',
            '+faststart',
            mp4Path,
          ]
        : [
            '-y',
            '-ss',
            safeStart.toFixed(3),
            '-t',
            safeDur.toFixed(3),
            '-i',
            sourcePath,
            '-vf',
            mp4Vf,
            '-c:v',
            'libx264',
            '-preset',
            'veryfast',
            '-crf',
            '24',
            '-an',
            '-movflags',
            '+faststart',
            mp4Path,
          ];
      await runProcess(FFMPEG_BIN, mp4Args);
    }

    const webpStat = await fsp.stat(webpPath);
    const mp4Stat = await fsp.stat(mp4Path);

    return {
      jobId,
      isAnimated,
      duration: Number(safeDur.toFixed(2)),
      side: finalSide,
      fps: finalFps,
      webpBytes: webpStat.size,
      audioBytes,
      mp4Bytes: mp4Stat.size,
      hasAudio: hasOutputAudio,
      files: {
        webp: `/api/stickers/file/${safeSid}/${jobId}/sticker.webp`,
        ogg: hasOutputAudio ? `/api/stickers/file/${safeSid}/${jobId}/audio.ogg` : null,
        mp4: `/api/stickers/file/${safeSid}/${jobId}/clip.mp4`,
      },
    };
  });
}
