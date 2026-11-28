import React, { useState } from 'react';
import { ArrowLeft, Globe, LogOut, QrCode, RefreshCw, Smartphone } from 'lucide-react';
import { api, getApiBase, setApiBase } from '../api.js';

export default function WhatsAppPanel({
  serverOnline,
  serverWaking,
  waStatus,
  onRefresh,
  targetJid,
  onChangeTargetJid,
  sendMode,
  onChangeSendMode,
  onGoCreate,
}) {
  const [phoneInput, setPhoneInput] = useState('');
  const [serverUrlInput, setServerUrlInput] = useState(() => getApiBase());
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState('');
  const [serverMsg, setServerMsg] = useState('');

  const handleSaveServerUrl = async (e) => {
    e.preventDefault();
    setErr('');
    setServerMsg('');
    try {
      const saved = setApiBase(serverUrlInput);
      setServerUrlInput(saved);
      await onRefresh();
      setServerMsg(
        saved
          ? `Servidor configurado (${saved}). Verificando conexión...`
          : 'URL limpiada. Trabajando en modo web directo.'
      );
    } catch (ex) {
      setErr(ex.message);
    }
  };

  const startQr = async () => {
    setBusy(true);
    setErr('');
    try {
      await api.waConnect({ forceFresh: false });
      await onRefresh();
    } catch (e) {
      setErr(e.message);
    } finally {
      setBusy(false);
    }
  };

  const startPairingCode = async (e) => {
    e.preventDefault();
    setBusy(true);
    setErr('');
    try {
      await api.waConnect({ phoneNumber: phoneInput, forceFresh: true });
      await onRefresh();
    } catch (e) {
      setErr(e.message);
    } finally {
      setBusy(false);
    }
  };

  const handleLogout = async () => {
    setBusy(true);
    setErr('');
    try {
      await api.waLogout();
      await onRefresh();
    } catch (e) {
      setErr(e.message);
    } finally {
      setBusy(false);
    }
  };

  const status = waStatus?.status || 'disconnected';

  return (
    <div className="panel" style={{ maxWidth: 740, margin: '0 auto' }}>
      <div className="panel-title">
        <h2>Conectar WhatsApp para envío directo</h2>
        <button type="button" className="btn btn-sm" onClick={onRefresh} disabled={busy}>
          <RefreshCw size={14} /> Actualizar
        </button>
      </div>

      <p className="muted-text" style={{ marginTop: 0 }}>
        Vincula tu WhatsApp una vez mediante QR o código de 8 dígitos para recibir tus stickers con audio nativo (<code>audioStickerMessage</code>) directamente en tu chat.
      </p>

      {!serverOnline && (
        <div className="alert warn" style={{ marginBottom: 14 }}>
          {serverWaking ? (
            <>
              <strong>⏳ Conectando y despertando servidor gratuito de WhatsApp...</strong>
              <div style={{ marginTop: 4 }}>
                En Render Free puede tardar ~35–50 segundos la primera vez si estaba dormido. En cuanto responda aparecerá tu código QR aquí mismo.
              </div>
            </>
          ) : (
            <>
              <strong>Servidor gratuito temporalmente inaccesible o despertando:</strong>
              <div style={{ marginTop: 4 }}>
                Pulsa <strong>Actualizar</strong> arriba para reintentar despertar <code>https://lexsticker.onrender.com</code>, o conecta tu propia URL Render abajo.
              </div>
            </>
          )}
        </div>
      )}

      {serverMsg && (
        <div className="alert ok" style={{ marginBottom: 14 }}>
          {serverMsg}
        </div>
      )}

      {err && (
        <div className="alert err" style={{ marginBottom: 14 }}>
          {err}
        </div>
      )}

      <form onSubmit={handleSaveServerUrl} className="link-row" style={{ alignItems: 'center', marginBottom: 16 }}>
        <input
          type="url"
          className="input-box"
          placeholder="URL del servidor Render Free (ej. https://lexsticker.onrender.com)"
          value={serverUrlInput}
          onChange={(e) => setServerUrlInput(e.target.value)}
        />
        <button type="submit" className="btn btn-sm" disabled={busy}>
          <Globe size={15} /> Conectar servidor
        </button>
      </form>

      {serverOnline && status === 'connected' ? (
        <div className="alert ok" style={{ marginBottom: 16 }}>
          <div style={{ fontWeight: 700 }}>
            Conectado como: {waStatus.me?.name} ({waStatus.me?.id})
          </div>
          <div style={{ marginTop: 4 }}>
            Todo listo para crear y enviar stickers desde la pestaña <strong>Crear Sticker</strong>.
          </div>
        </div>
      ) : status === 'qr' && waStatus?.qrDataUrl ? (
        <div style={{ marginBottom: 16 }}>
          <p style={{ fontWeight: 600, marginBottom: 10 }}>
            Escanea este código en WhatsApp → Dispositivos vinculados → Vincular dispositivo:
          </p>
          <div className="qr-box">
            <img src={waStatus.qrDataUrl} alt="QR de WhatsApp" width={230} height={230} />
          </div>
        </div>
      ) : waStatus?.pairingCode ? (
        <div className="alert ok" style={{ marginBottom: 16 }}>
          <div>Ingresa este código en WhatsApp → Dispositivos vinculados → Vincular con número de teléfono:</div>
          <div style={{ fontSize: 28, fontWeight: 800, letterSpacing: '0.16em', marginTop: 8 }}>
            {waStatus.pairingCode}
          </div>
        </div>
      ) : serverOnline ? (
        <div className="alert warn" style={{ marginBottom: 16 }}>
          Estado actual: <strong>{status === 'connecting' ? 'Conectando...' : 'Desconectado'}</strong>. Elige vincular con QR o con código de 8 dígitos.
        </div>
      ) : null}

      {!serverOnline ? (
        <div className="row-wrap" style={{ marginTop: 8 }}>
          <button type="button" className="btn btn-brand" onClick={onRefresh} disabled={busy}>
            <RefreshCw size={15} /> {serverWaking ? 'Conectando...' : 'Despertar / Reintentar conexión'}
          </button>
          {onGoCreate && (
            <button type="button" className="btn btn-primary" onClick={onGoCreate}>
              <ArrowLeft size={16} /> Volver a Crear Sticker
            </button>
          )}
        </div>
      ) : status !== 'connected' ? (
        <div style={{ display: 'grid', gap: 14, marginTop: 12 }}>
          <div className="row-wrap">
            <button type="button" className="btn btn-brand" onClick={startQr} disabled={busy}>
              <QrCode size={16} /> Generar código QR
            </button>
          </div>

          <form onSubmit={startPairingCode} className="link-row" style={{ alignItems: 'center' }}>
            <input
              type="text"
              className="input-box"
              placeholder="Vincular con número (código país + número, ej. 51999888777)"
              value={phoneInput}
              onChange={(e) => setPhoneInput(e.target.value)}
            />
            <button type="submit" className="btn" disabled={busy || !phoneInput.trim()}>
              <Smartphone size={16} /> Pedir código 8 dígitos
            </button>
          </form>
        </div>
      ) : (
        <div style={{ display: 'grid', gap: 14, marginTop: 12 }}>
          <div>
            <label style={{ display: 'block', fontSize: 13, fontWeight: 600, marginBottom: 6 }}>
              Destino por defecto al pulsar "Enviar por WhatsApp"
            </label>
            <input
              type="text"
              className="input-box"
              style={{ width: '100%' }}
              placeholder="Vacío = Tu propio chat (Mensajes contigo mismo), o número / JID"
              value={targetJid}
              onChange={(e) => onChangeTargetJid(e.target.value)}
            />
            {waStatus?.chats?.length > 0 && (
              <div className="row-wrap" style={{ marginTop: 8 }}>
                {waStatus.chats.slice(0, 6).map((c) => (
                  <button
                    key={c.id}
                    type="button"
                    className={`btn btn-sm ${targetJid === c.id ? 'btn-primary' : ''}`}
                    onClick={() => onChangeTargetJid(c.id)}
                  >
                    {c.name}
                  </button>
                ))}
              </div>
            )}
          </div>

          <div>
            <label style={{ display: 'block', fontSize: 13, fontWeight: 600, marginBottom: 6 }}>
              Formato de envío por WhatsApp
            </label>
            <select
              className="input-box"
              style={{ width: '100%' }}
              value={sendMode}
              onChange={(e) => onChangeSendMode(e.target.value)}
            >
              <option value="audioSticker">
                Sticker con audio nativo (audioStickerMessage)
              </option>
              <option value="stickerDirect">
                Sticker directo con pista embebida (stickerMessage + audioMessage)
              </option>
              <option value="dual">
                Sticker + Nota de voz aparte (universal)
              </option>
            </select>
          </div>

          <div className="row-wrap" style={{ marginTop: 4 }}>
            {onGoCreate && (
              <button type="button" className="btn btn-primary" onClick={onGoCreate}>
                <ArrowLeft size={16} /> Ir a Crear Sticker
              </button>
            )}
            <button type="button" className="btn btn-danger" onClick={handleLogout} disabled={busy}>
              <LogOut size={16} /> Cerrar sesión
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
