import crypto from 'node:crypto';
import fs from 'node:fs';
import fsp from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import cors from 'cors';
import express from 'express';
import multer from 'multer';
import {
  assertValidId,
  buildStickerPackage,
  cleanupExpiredMediaDirs,
  ensureMediaDirs,
  OUTPUTS_DIR,
  probeMediaWithFfmpeg,
  resolveSafeInside,
  SOURCES_DIR,
} from './media.js';
import { whatsappPool } from './whatsapp.js';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const SERVER_ROOT = path.resolve(__dirname, '..');
const WORKSPACE_ROOT = path.resolve(SERVER_ROOT, '..');
const WEB_DIST_DIR = path.join(WORKSPACE_ROOT, 'web', 'dist');

const PORT = Number(process.env.PORT || 3000);

function extractSessionId(req) {
  const raw = req.get('x-lex-session') || req.query?.s || '';
  return assertValidId(raw, 'Sesión');
}

function withSessionId(req, res, next) {
  try {
    req.lexSessionId = extractSessionId(req);
    next();
  } catch (err) {
    res.status(400).json({ error: err.message });
  }
}

const upload = multer({
  storage: multer.diskStorage({
    destination: async (req, _file, cb) => {
      try {
        await ensureMediaDirs();
        const sid = req.lexSessionId || extractSessionId(req);
        const id = req.uploadId || (req.uploadId = crypto.randomUUID());
        const dir = resolveSafeInside(SOURCES_DIR, sid, id);
        await fsp.mkdir(dir, { recursive: true });
        cb(null, dir);
      } catch (err) {
        cb(err);
      }
    },
    filename: (_req, file, cb) => {
      const ext = path.extname(file.originalname || '').toLowerCase().replace(/[^a-z0-9.]/g, '') || '.bin';
      cb(null, `upload${ext}`);
    },
  }),
  limits: { fileSize: 50 * 1024 * 1024 },
});

export function createApp() {
  const app = express();
  app.use(
    cors({
      origin: true,
      methods: ['GET', 'POST', 'OPTIONS'],
      allowedHeaders: ['Content-Type', 'x-lex-session'],
    })
  );
  app.use((_req, res, next) => {
    res.setHeader('Cross-Origin-Resource-Policy', 'cross-origin');
    next();
  });
  app.use(express.json({ limit: '4mb' }));

  app.get('/api/health', (req, res) => {
    let waStatus;
    try {
      const sid = extractSessionId(req);
      waStatus = whatsappPool.getStatus(sid);
    } catch {
      waStatus = {
        status: 'disconnected',
        hasSavedSession: false,
        qrDataUrl: null,
        pairingCode: null,
        lastError: null,
        me: null,
        chats: [],
      };
    }

    res.json({
      ok: true,
      mode: 'multi-user',
      port: PORT,
      whatsapp: waStatus,
    });
  });

  // Upload local video, GIF or image scoped to visitor session
  app.post('/api/media/upload', withSessionId, upload.single('file'), async (req, res) => {
    try {
      if (!req.file) return res.status(400).json({ error: 'No se recibió ningún archivo.' });
      const meta = await probeMediaWithFfmpeg(req.file.path);
      const isGif = /\.gif$/i.test(req.file.originalname || req.file.filename);
      const isImage = !isGif && (meta.isImage || req.file.mimetype?.startsWith('image/'));
      res.json({
        sourceId: req.uploadId,
        mediaUrl: `/api/media/source/${req.lexSessionId}/${req.uploadId}/${encodeURIComponent(req.file.filename)}`,
        kind: isImage ? 'image' : 'video',
        isGif,
        duration: isImage ? 5 : meta.duration || 5,
        width: meta.width || 512,
        height: meta.height || 512,
        hasAudio: meta.hasAudio,
      });
    } catch (err) {
      res.status(400).json({ error: err.message || 'Error al procesar el archivo.' });
    }
  });

  // Upload custom audio file or microphone recording scoped to visitor session
  app.post('/api/media/audio-upload', withSessionId, upload.single('file'), async (req, res) => {
    try {
      if (!req.file) return res.status(400).json({ error: 'No se recibió archivo de audio.' });
      const meta = await probeMediaWithFfmpeg(req.file.path);
      res.json({
        audioId: req.uploadId,
        audioUrl: `/api/media/source/${req.lexSessionId}/${req.uploadId}/${encodeURIComponent(req.file.filename)}`,
        duration: meta.duration || 5,
      });
    } catch (err) {
      res.status(400).json({ error: err.message || 'No se pudo leer el audio.' });
    }
  });

  // Stream uploaded/downloaded source files strictly within session folder
  app.get('/api/media/source/:sid/:id/:filename', (req, res) => {
    try {
      const sid = assertValidId(req.params.sid, 'Sesión');
      const id = assertValidId(req.params.id, 'Archivo');
      const safeName = path.basename(req.params.filename || '');
      if (!safeName) return res.status(400).end();
      const filePath = resolveSafeInside(SOURCES_DIR, sid, id, safeName);
      if (!fs.existsSync(filePath)) return res.status(404).end();
      res.sendFile(filePath);
    } catch {
      res.status(400).end();
    }
  });

  // Build sticker assets (WebP + Opus OGG + square MP4)
  app.post('/api/stickers/build', withSessionId, async (req, res) => {
    try {
      const result = await buildStickerPackage({
        ...(req.body || {}),
        sessionId: req.lexSessionId,
      });
      res.json(result);
    } catch (err) {
      res.status(400).json({ error: err.message || 'Error generando el sticker.' });
    }
  });

  // Serve generated sticker artifacts strictly within session folder
  app.get('/api/stickers/file/:sid/:jobId/:filename', (req, res) => {
    try {
      const sid = assertValidId(req.params.sid, 'Sesión');
      const jobId = assertValidId(req.params.jobId, 'Sticker');
      const filename = String(req.params.filename || '');
      const allowed = new Set(['sticker.webp', 'audio.ogg', 'clip.mp4']);
      if (!allowed.has(filename)) return res.status(404).end();
      const filePath = resolveSafeInside(OUTPUTS_DIR, sid, jobId, filename);
      if (!fs.existsSync(filePath)) return res.status(404).end();
      if (req.query.download === '1') {
        res.download(filePath, `lexsticker-${jobId.slice(0, 6)}-${filename}`);
      } else {
        res.sendFile(filePath);
      }
    } catch {
      res.status(400).end();
    }
  });

  // WhatsApp connection and direct sending endpoints scoped strictly per session
  app.get('/api/whatsapp/status', withSessionId, (req, res) => {
    res.json(whatsappPool.getStatus(req.lexSessionId));
  });

  app.post('/api/whatsapp/connect', withSessionId, async (req, res) => {
    try {
      const { phoneNumber, forceFresh } = req.body || {};
      const status = await whatsappPool.connect(req.lexSessionId, { phoneNumber, forceFresh });
      res.json(status);
    } catch (err) {
      res.status(400).json({ error: err.message || 'No se pudo iniciar la sesión de WhatsApp.' });
    }
  });

  app.post('/api/whatsapp/logout', withSessionId, async (req, res) => {
    const status = await whatsappPool.logout(req.lexSessionId);
    res.json(status);
  });

  app.post('/api/whatsapp/send', withSessionId, async (req, res) => {
    try {
      const { jobId, target, sendMode, isAnimated, duration } = req.body || {};
      const result = await whatsappPool.sendJobToWhatsApp(req.lexSessionId, {
        jobId,
        target,
        sendMode,
        isAnimated,
        duration,
      });
      console.log(`[WhatsApp][${req.lexSessionId.slice(0, 8)}] Enviado (${result.mode}) a ${result.jid} (msgId=${result.messageId})`);
      res.json(result);
    } catch (err) {
      console.error('[WhatsApp Send Error]', err.message);
      res.status(400).json({ error: err.message || 'No se pudo enviar por WhatsApp.' });
    }
  });

  // Serve built web app
  if (fs.existsSync(WEB_DIST_DIR)) {
    app.use(express.static(WEB_DIST_DIR));
    app.get('*', (req, res, next) => {
      if (req.path.startsWith('/api/')) return next();
      res.sendFile(path.join(WEB_DIST_DIR, 'index.html'));
    });
  }

  return app;
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  await ensureMediaDirs();
  await whatsappPool.ensureDirsAndCleanStartup();
  await cleanupExpiredMediaDirs();

  setInterval(() => {
    cleanupExpiredMediaDirs().catch(() => {});
    whatsappPool.cleanupIdleSessions().catch(() => {});
  }, 5 * 60 * 1000).unref();

  const app = createApp();
  app.listen(PORT, '0.0.0.0', () => {
    console.log(`LexSticker multi-usuario escuchando en puerto ${PORT}`);
  });
}
