import { randomBytes } from 'node:crypto';
import fs from 'node:fs';
import fsp from 'node:fs/promises';
import path from 'node:path';
import makeWASocket, {
  DisconnectReason,
  fetchLatestBaileysVersion,
  generateMessageIDV2,
  jidNormalizedUser,
  prepareWAMessageMedia,
  proto,
  useMultiFileAuthState,
} from '@whiskeysockets/baileys';
import pino from 'pino';
import QRCode from 'qrcode';
import { assertValidId, DATA_DIR, OUTPUTS_DIR, resolveSafeInside } from './media.js';

const AUTH_SESSIONS_DIR = path.join(DATA_DIR, 'auth_sessions');
const MAX_ACTIVE_SESSIONS = 8;
const DISCONNECTED_IDLE_TTL_MS = 8 * 60 * 1000;
const CONNECTED_IDLE_TTL_MS = 45 * 60 * 1000;
const MAX_CHATS_PER_SESSION = 20;

function wrapAudioStickerPayload(msg) {
  if (!msg || typeof msg !== 'object') return msg;
  if (msg.stickerMessage?.__wrapAudioSticker) {
    return {
      audioStickerMessage: {
        message: {
          stickerMessage: msg.stickerMessage,
        },
      },
      ...(msg.messageContextInfo ? { messageContextInfo: msg.messageContextInfo } : {}),
    };
  }
  return msg;
}

// Patch Baileys WAProto in memory to support WhatsApp Sound Sticker wire tags:
// - Message.audioStickerMessage = 134 (FutureProofMessage, wireType 2 -> 1074)
// - StickerMessage.emojis = 25 (string, wireType 2 -> 202)
// - StickerMessage.audioMessage = 26 (AudioMessage, wireType 2 -> 210)
function ensureSoundStickerProtoPatch() {
  if (proto.Message.__stickersLexPatched) return;

  const origMsgEncode = proto.Message.encode.bind(proto.Message);
  proto.Message.encode = function patchedMessageEncode(m, w) {
    let target = m;
    if (target?.deviceSentMessage?.message?.stickerMessage?.__wrapAudioSticker) {
      target = {
        ...target,
        deviceSentMessage: {
          ...target.deviceSentMessage,
          message: wrapAudioStickerPayload(target.deviceSentMessage.message),
        },
      };
    }
    const writer = origMsgEncode(target, w);
    if (target?.audioStickerMessage != null && Object.hasOwnProperty.call(target, 'audioStickerMessage')) {
      proto.Message.FutureProofMessage.encode(
        target.audioStickerMessage,
        writer.uint32(1074).fork()
      ).ldelim();
    }
    return writer;
  };

  const origStickerEncode = proto.Message.StickerMessage.encode.bind(proto.Message.StickerMessage);
  proto.Message.StickerMessage.encode = function patchedStickerEncode(m, w) {
    const writer = origStickerEncode(m, w);
    if (m?.emojis != null && Object.hasOwnProperty.call(m, 'emojis')) {
      writer.uint32(202).string(String(m.emojis));
    }
    if (m?.audioMessage != null && Object.hasOwnProperty.call(m, 'audioMessage')) {
      proto.Message.AudioMessage.encode(
        m.audioMessage,
        writer.uint32(210).fork()
      ).ldelim();
    }
    return writer;
  };

  proto.Message.__stickersLexPatched = true;
}

ensureSoundStickerProtoPatch();

class WhatsAppSession {
  constructor(sessionId) {
    this.sessionId = sessionId;
    this.authDir = resolveSafeInside(AUTH_SESSIONS_DIR, sessionId);
    this.sock = null;
    this.status = 'disconnected'; // disconnected | connecting | qr | connected
    this.qrDataUrl = null;
    this.pairingCode = null;
    this.lastError = null;
    this.me = null;
    this.chats = new Map();
    this.connectingPromise = null;
    this.reconnectTimer = null;
    this.lastActivityAt = Date.now();
    this.logger = pino({ level: 'warn' });
  }

  touch() {
    this.lastActivityAt = Date.now();
  }

  hasSavedSession() {
    return fs.existsSync(path.join(this.authDir, 'creds.json'));
  }

  getSelfJid() {
    if (!this.me?.id) return null;
    return jidNormalizedUser(this.me.id);
  }

  getStatus() {
    this.touch();
    return {
      status: this.status,
      hasSavedSession: this.hasSavedSession(),
      qrDataUrl: this.qrDataUrl,
      pairingCode: this.pairingCode,
      lastError: this.lastError,
      me: this.me ? { id: this.getSelfJid(), name: this.me.name || 'Mi WhatsApp' } : null,
      chats: Array.from(this.chats.values()).slice(0, MAX_CHATS_PER_SESSION),
    };
  }

  normalizeTargetJid(rawTarget) {
    const trimmed = String(rawTarget || '').trim();
    if (!trimmed) {
      const self = this.getSelfJid();
      if (!self) throw new Error('Conecta tu WhatsApp primero para usar tu chat propio.');
      return self;
    }
    if (trimmed.includes('@')) return trimmed;
    const digits = trimmed.replace(/\D/g, '');
    if (digits.length < 7) {
      throw new Error('Ingresa un número de WhatsApp válido con código de país (ej. 51999888777).');
    }
    return `${digits}@s.whatsapp.net`;
  }

  rememberChat(c) {
    if (!c?.id) return;
    if (this.chats.size >= MAX_CHATS_PER_SESSION && !this.chats.has(c.id)) return;
    this.chats.set(c.id, {
      id: c.id,
      name: c.name || c.subject || c.id.split('@')[0],
    });
  }

  async connect({ phoneNumber = null, forceFresh = false } = {}) {
    this.touch();
    if (forceFresh) {
      await this.logout();
    }
    if (this.sock && this.status === 'connected' && !phoneNumber) {
      return this.getStatus();
    }
    if (this.connectingPromise && !phoneNumber) {
      return this.connectingPromise;
    }

    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }

    this.connectingPromise = (async () => {
      await fsp.mkdir(this.authDir, { recursive: true });
      if (this.sock) {
        try {
          this.sock.end(undefined);
        } catch {
          // ignore
        }
        this.sock = null;
      }

      this.status = 'connecting';
      this.qrDataUrl = null;
      this.pairingCode = null;
      this.lastError = null;

      const { state, saveCreds } = await useMultiFileAuthState(this.authDir);
      let version;
      try {
        ({ version } = await fetchLatestBaileysVersion());
      } catch {
        version = undefined;
      }

      const sock = makeWASocket({
        ...(version ? { version } : {}),
        auth: state,
        logger: this.logger,
        printQRInTerminal: false,
        browser: ['LexSticker', 'Chrome', '128.0.0'],
        syncFullHistory: false,
        patchMessageBeforeSending: (msg) => wrapAudioStickerPayload(msg),
      });

      this.sock = sock;
      sock.ev.on('creds.update', saveCreds);

      sock.ev.on('chats.upsert', (items) => {
        for (const c of items || []) this.rememberChat(c);
      });

      sock.ev.on('messaging-history.set', ({ chats }) => {
        for (const c of chats || []) this.rememberChat(c);
      });

      sock.ev.on('connection.update', async (update) => {
        const { connection, lastDisconnect, qr } = update;
        if (qr && !phoneNumber) {
          try {
            this.qrDataUrl = await QRCode.toDataURL(qr, { margin: 2, width: 260 });
            this.status = 'qr';
          } catch (err) {
            this.lastError = err.message;
          }
        }

        if (connection === 'open') {
          this.touch();
          this.status = 'connected';
          this.qrDataUrl = null;
          this.pairingCode = null;
          this.lastError = null;
          this.me = sock.user || null;
          const selfJid = this.getSelfJid();
          if (selfJid) {
            this.chats.set(selfJid, { id: selfJid, name: 'Mi propio chat (Tú)' });
          }
        } else if (connection === 'close') {
          const statusCode = lastDisconnect?.error?.output?.statusCode;
          const loggedOut = statusCode === DisconnectReason.loggedOut;
          this.me = null;
          if (loggedOut) {
            this.status = 'disconnected';
            this.qrDataUrl = null;
            this.pairingCode = null;
            this.chats.clear();
            await fsp.rm(this.authDir, { recursive: true, force: true }).catch(() => {});
          } else if (this.status !== 'disconnected') {
            this.lastError = lastDisconnect?.error?.message || 'Conexión cerrada. Reconectando...';
            this.status = 'disconnected';
            this.reconnectTimer = setTimeout(() => {
              if (this.hasSavedSession() && this.status === 'disconnected') {
                this.connect().catch(() => {});
              }
            }, 2500);
          }
        }
      });

      if (phoneNumber && !state.creds.registered) {
        const digits = String(phoneNumber).replace(/\D/g, '');
        if (digits.length < 8) {
          throw new Error('Ingresa el número completo con código de país (sin +, ni espacios).');
        }
        await new Promise((r) => setTimeout(r, 1500));
        const code = await sock.requestPairingCode(digits);
        this.pairingCode = code;
      }

      return this.getStatus();
    })();

    try {
      return await this.connectingPromise;
    } finally {
      this.connectingPromise = null;
    }
  }

  async logout({ deleteRemote = true } = {}) {
    this.touch();
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    this.status = 'disconnected';
    this.qrDataUrl = null;
    this.pairingCode = null;
    this.me = null;
    this.chats.clear();
    if (this.sock) {
      try {
        if (deleteRemote) await this.sock.logout();
        else this.sock.end(undefined);
      } catch {
        try {
          this.sock.end(undefined);
        } catch {
          // ignore
        }
      }
      this.sock = null;
    }
    await fsp.rm(this.authDir, { recursive: true, force: true }).catch(() => {});
    return this.getStatus();
  }

  async sendJobToWhatsApp({
    jobId,
    target = '',
    sendMode = 'audioSticker',
    isAnimated = true,
    duration = 5,
  }) {
    this.touch();
    if (!this.sock || this.status !== 'connected') {
      throw new Error('Tu WhatsApp no está vinculado todavía. Escanea el QR o usa el código de emparejamiento.');
    }

    const safeJobId = assertValidId(jobId, 'Sticker jobId');
    const outDir = resolveSafeInside(OUTPUTS_DIR, this.sessionId, safeJobId);
    const webpPath = path.join(outDir, 'sticker.webp');
    const oggPath = path.join(outDir, 'audio.ogg');

    const webpBuffer = await fsp.readFile(webpPath);
    const hasAudio = fs.existsSync(oggPath);
    const oggBuffer = hasAudio ? await fsp.readFile(oggPath) : null;
    const jid = this.normalizeTargetJid(target);

    if (!oggBuffer || sendMode === 'dual') {
      const stickerRes = await this.sock.sendMessage(jid, { sticker: webpBuffer });
      if (oggBuffer && sendMode === 'dual') {
        await this.sock.sendMessage(jid, {
          audio: oggBuffer,
          mimetype: 'audio/ogg; codecs=opus',
          ptt: true,
        });
      }
      return { ok: true, mode: oggBuffer ? 'dual' : 'silent', messageId: stickerRes?.key?.id, jid };
    }

    const upload = this.sock.waUploadToServer;
    if (typeof upload !== 'function') {
      throw new Error('La conexión de WhatsApp no está lista para subir multimedia. Reconecta tu sesión.');
    }

    const stickerMedia = await prepareWAMessageMedia(
      { sticker: webpBuffer, isAnimated },
      { upload, jid, logger: this.logger }
    );
    const audioMedia = await prepareWAMessageMedia(
      {
        audio: oggBuffer,
        mimetype: 'audio/ogg; codecs=opus',
        seconds: Math.max(1, Math.round(Number(duration) || 5)),
      },
      { upload, jid, logger: this.logger }
    );

    const stickerPayload = stickerMedia.stickerMessage;
    stickerPayload.width = 512;
    stickerPayload.height = 512;
    stickerPayload.isAnimated = Boolean(isAnimated);
    stickerPayload.stickerSentTs = Date.now();
    stickerPayload.emojis = '🔊';
    stickerPayload.audioMessage = audioMedia.audioMessage;

    const messageId = generateMessageIDV2(this.sock.user?.id);
    const messageContextInfo = { messageSecret: randomBytes(32) };

    if (sendMode === 'stickerDirect') {
      await this.sock.relayMessage(
        jid,
        { stickerMessage: stickerPayload, messageContextInfo },
        { messageId }
      );
      return { ok: true, mode: 'stickerDirect', messageId, jid };
    }

    // Default: audioStickerMessage (field 134 FutureProofMessage wrapping StickerMessage + AudioMessage)
    Object.defineProperty(stickerPayload, '__wrapAudioSticker', {
      value: true,
      enumerable: false,
      configurable: true,
    });
    await this.sock.relayMessage(
      jid,
      { stickerMessage: stickerPayload, messageContextInfo },
      { messageId }
    );
    return { ok: true, mode: 'audioSticker', messageId, jid };
  }
}

class WhatsAppSessionPool {
  constructor() {
    this.sessions = new Map();
  }

  async ensureDirsAndCleanStartup() {
    await fsp.mkdir(AUTH_SESSIONS_DIR, { recursive: true });
    const now = Date.now();
    let dirs;
    try {
      dirs = await fsp.readdir(AUTH_SESSIONS_DIR, { withFileTypes: true });
    } catch {
      return;
    }
    for (const d of dirs) {
      if (!d.isDirectory()) continue;
      const full = path.join(AUTH_SESSIONS_DIR, d.name);
      try {
        const st = await fsp.stat(full);
        if (now - st.mtimeMs > CONNECTED_IDLE_TTL_MS) {
          await fsp.rm(full, { recursive: true, force: true });
        }
      } catch {
        // ignore
      }
    }
  }

  getExisting(sessionId) {
    const safeSid = assertValidId(sessionId, 'Sesión');
    return this.sessions.get(safeSid) || null;
  }

  async getOrCreate(sessionId) {
    const safeSid = assertValidId(sessionId, 'Sesión');
    const existing = this.sessions.get(safeSid);
    if (existing) return existing;

    await this.cleanupIdleSessions();

    if (this.sessions.size >= MAX_ACTIVE_SESSIONS) {
      let evictCandidate = null;
      for (const s of this.sessions.values()) {
        if (s.status !== 'connected') {
          evictCandidate = s;
          break;
        }
      }
      if (!evictCandidate) {
        evictCandidate = Array.from(this.sessions.values()).sort(
          (a, b) => a.lastActivityAt - b.lastActivityAt
        )[0];
      }
      if (evictCandidate) {
        await evictCandidate.logout({ deleteRemote: false }).catch(() => {});
        this.sessions.delete(evictCandidate.sessionId);
      }
    }

    const session = new WhatsAppSession(safeSid);
    this.sessions.set(safeSid, session);
    return session;
  }

  getStatus(sessionId) {
    const existing = this.getExisting(sessionId);
    if (!existing) {
      return {
        status: 'disconnected',
        hasSavedSession: false,
        qrDataUrl: null,
        pairingCode: null,
        lastError: null,
        me: null,
        chats: [],
      };
    }
    return existing.getStatus();
  }

  async connect(sessionId, opts) {
    const session = await this.getOrCreate(sessionId);
    return session.connect(opts);
  }

  async logout(sessionId) {
    const existing = this.getExisting(sessionId);
    if (!existing) return this.getStatus(sessionId);
    const res = await existing.logout({ deleteRemote: true });
    this.sessions.delete(existing.sessionId);
    return res;
  }

  async sendJobToWhatsApp(sessionId, opts) {
    const existing = this.getExisting(sessionId);
    if (!existing) {
      throw new Error('Tu sesión de WhatsApp no está conectada. Escanea el QR primero.');
    }
    return existing.sendJobToWhatsApp(opts);
  }

  async cleanupIdleSessions() {
    const now = Date.now();
    for (const [sid, session] of this.sessions.entries()) {
      const idleMs = now - session.lastActivityAt;
      const limit =
        session.status === 'connected' ? CONNECTED_IDLE_TTL_MS : DISCONNECTED_IDLE_TTL_MS;
      if (idleMs > limit) {
        await session.logout({ deleteRemote: false }).catch(() => {});
        this.sessions.delete(sid);
      }
    }
  }
}

export const whatsappPool = new WhatsAppSessionPool();
