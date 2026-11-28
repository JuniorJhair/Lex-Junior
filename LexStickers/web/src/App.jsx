import React, { useCallback, useEffect, useRef, useState } from 'react';
import {
  Download,
  Film,
  Mic,
  Music,
  Send,
  Share2,
  Sparkles,
  Square,
  Upload,
  Volume2,
  VolumeX,
  WifiOff,
} from 'lucide-react';
import {
  api,
  exportLocalClipOffline,
  probeAudioDurationLocal,
  resolveMediaUrl,
  shareOrDownloadBlob,
  shareOrDownloadFromUrl,
} from './api.js';
import CropCanvas from './components/CropCanvas.jsx';
import TimelineTrim from './components/TimelineTrim.jsx';
import WhatsAppPanel from './components/WhatsAppPanel.jsx';

export default function App() {
  const [tab, setTab] = useState('create'); // create | whatsapp
  const [serverInfo, setServerInfo] = useState(null);
  const [serverOnline, setServerOnline] = useState(false);
  const [serverWaking, setServerWaking] = useState(true);

  // Media source state
  const [source, setSource] = useState(null);
  const [loadingMedia, setLoadingMedia] = useState(false);

  // Crop & trim state
  const [maxLimit, setMaxLimit] = useState(10);
  const [start, setStart] = useState(0);
  const [duration, setDuration] = useState(5);
  const [currentTime, setCurrentTime] = useState(0);
  const [crop, setCrop] = useState({ x: 0, y: 0, size: 1 });

  // Audio state
  const [audioMode, setAudioMode] = useState('original'); // none | original | upload | mic
  const [audioStart, setAudioStart] = useState(0);
  const [customAudio, setCustomAudio] = useState(null);
  const [loadingAudio, setLoadingAudio] = useState(false);

  // Microphone recording
  const [recording, setRecording] = useState(false);
  const [recSeconds, setRecSeconds] = useState(0);
  const recorderRef = useRef(null);
  const recTimerRef = useRef(null);

  // Build & send state
  const [building, setBuilding] = useState(false);
  const [built, setBuilt] = useState(null);
  const [offlineResult, setOfflineResult] = useState(null);
  const [sendingWa, setSendingWa] = useState(false);
  const [targetJid, setTargetJid] = useState(() => localStorage.getItem('stickerslex_target_jid') || '');
  const [sendMode, setSendMode] = useState(
    () => localStorage.getItem('stickerslex_send_mode') || 'audioSticker'
  );
  const [sendFeedback, setSendFeedback] = useState(null);
  const [errorMsg, setErrorMsg] = useState('');
  const [okMsg, setOkMsg] = useState('');

  const updateTargetJid = (val) => {
    setTargetJid(val);
    localStorage.setItem('stickerslex_target_jid', val);
  };

  const updateSendMode = (val) => {
    setSendMode(val);
    localStorage.setItem('stickerslex_send_mode', val);
  };

  // Preview audio loop (up to 3 times like WhatsApp)
  const previewAudioRef = useRef(null);
  const [previewPlaying, setPreviewPlaying] = useState(false);
  const loopCountRef = useRef(0);

  const fileInputRef = useRef(null);
  const audioInputRef = useRef(null);

  const refreshServer = useCallback(async () => {
    try {
      const h = await api.health();
      setServerInfo(h);
      setServerOnline(true);
    } catch {
      setServerOnline(false);
    } finally {
      setServerWaking(false);
    }
  }, []);

  useEffect(() => {
    refreshServer();
  }, [refreshServer]);

  useEffect(() => {
    if (!serverOnline) return;
    const status = serverInfo?.whatsapp?.status;
    if (tab !== 'whatsapp' && status !== 'connecting' && status !== 'qr') return;
    const timer = setInterval(() => {
      refreshServer();
    }, 2500);
    return () => clearInterval(timer);
  }, [tab, serverOnline, serverInfo?.whatsapp?.status, refreshServer]);

  const applyLoadedSource = (data) => {
    const w = data.width || 512;
    const h = data.height || 512;
    const minSide = Math.min(w, h);
    const initX = Math.max(0, (w - minSide) / (2 * w));
    const initY = Math.max(0, (h - minSide) / (2 * h));

    const total = data.duration || 5;
    const initDur = data.kind === 'image' ? Math.min(5, maxLimit) : Math.min(maxLimit, Math.max(1, total));

    setSource(data);
    setCrop({ x: initX, y: initY, size: 1 });
    setStart(0);
    setDuration(Number(initDur.toFixed(1)));
    setCurrentTime(0);
    setAudioStart(0);
    setBuilt(null);
    setOfflineResult(null);
    setSendFeedback(null);
    setErrorMsg('');
    setOkMsg('');

    if (data.kind === 'image' || data.hasAudio === false) {
      setAudioMode('none');
    } else {
      setAudioMode('original');
    }
  };

  const processSelectedFile = async (file) => {
    if (!file) return;
    setLoadingMedia(true);
    setErrorMsg('');
    setOkMsg('');

    try {
      if (serverOnline) {
        const data = await api.uploadMedia(file);
        applyLoadedSource({
          ...data,
          mediaUrl: resolveMediaUrl(data.mediaUrl),
          isOffline: false,
        });
      } else {
        const objUrl = URL.createObjectURL(file);
        const isImg = file.type.startsWith('image/') && !/\.gif$/i.test(file.name);
        if (isImg) {
          const img = new Image();
          await new Promise((res, rej) => {
            img.onload = res;
            img.onerror = rej;
            img.src = objUrl;
          });
          applyLoadedSource({
            sourceId: null,
            mediaUrl: objUrl,
            kind: 'image',
            duration: 5,
            width: img.naturalWidth,
            height: img.naturalHeight,
            hasAudio: false,
            isOffline: true,
          });
        } else {
          const v = document.createElement('video');
          v.src = objUrl;
          await new Promise((res, rej) => {
            v.onloadedmetadata = res;
            v.onerror = rej;
          });
          applyLoadedSource({
            sourceId: null,
            mediaUrl: objUrl,
            kind: 'video',
            duration: v.duration || 5,
            width: v.videoWidth || 512,
            height: v.videoHeight || 512,
            hasAudio: true,
            isOffline: true,
          });
        }
      }
    } catch (err) {
      setErrorMsg(err.message || 'No se pudo cargar el archivo.');
    } finally {
      setLoadingMedia(false);
    }
  };

  const handleUploadFile = async (e) => {
    const file = e.target.files?.[0];
    e.target.value = '';
    await processSelectedFile(file);
  };

  const handleUploadCustomAudio = async (e) => {
    const file = e.target.files?.[0];
    e.target.value = '';
    if (!file) return;
    setLoadingAudio(true);
    setErrorMsg('');
    try {
      if (serverOnline) {
        const res = await api.uploadAudio(file, file.name);
        setCustomAudio({
          audioId: res.audioId,
          audioUrl: resolveMediaUrl(res.audioUrl),
          duration: res.duration,
          label: file.name,
        });
      } else {
        const objUrl = URL.createObjectURL(file);
        const dur = await probeAudioDurationLocal(objUrl, 5);
        setCustomAudio({
          audioId: null,
          audioUrl: objUrl,
          duration: dur,
          label: file.name,
        });
      }
      setAudioMode('upload');
      setAudioStart(0);
      setBuilt(null);
      setOfflineResult(null);
    } catch (err) {
      setErrorMsg(err.message);
    } finally {
      setLoadingAudio(false);
    }
  };

  const toggleRecordMic = async () => {
    if (recording) {
      recorderRef.current?.stop();
      clearInterval(recTimerRef.current);
      setRecording(false);
      return;
    }

    setErrorMsg('');
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      const rec = new MediaRecorder(stream);
      const chunks = [];
      rec.ondataavailable = (ev) => {
        if (ev.data.size > 0) chunks.push(ev.data);
      };
      rec.onstop = async () => {
        stream.getTracks().forEach((t) => t.stop());
        const blob = new Blob(chunks, { type: rec.mimeType || 'audio/webm' });
        setLoadingAudio(true);
        try {
          if (serverOnline) {
            const res = await api.uploadAudio(blob, 'grabacion-voz.webm');
            setCustomAudio({
              audioId: res.audioId,
              audioUrl: resolveMediaUrl(res.audioUrl),
              duration: res.duration,
              label: 'Grabación de voz',
            });
          } else {
            const objUrl = URL.createObjectURL(blob);
            const dur = await probeAudioDurationLocal(objUrl, Math.max(1, recSeconds || 3));
            setCustomAudio({
              audioId: null,
              audioUrl: objUrl,
              duration: dur,
              label: 'Grabación de voz',
            });
          }
          setAudioMode('mic');
          setAudioStart(0);
          setBuilt(null);
          setOfflineResult(null);
        } catch (err) {
          setErrorMsg(err.message);
        } finally {
          setLoadingAudio(false);
        }
      };

      recorderRef.current = rec;
      setRecSeconds(0);
      rec.start();
      setRecording(true);
      recTimerRef.current = setInterval(() => {
        setRecSeconds((s) => {
          if (s + 1 >= maxLimit) {
            rec.stop();
            clearInterval(recTimerRef.current);
            setRecording(false);
          }
          return s + 1;
        });
      }, 1000);
    } catch {
      setErrorMsg('No se pudo acceder al micrófono. Verifica los permisos.');
    }
  };

  const handleChangeMaxLimit = (limit) => {
    setMaxLimit(limit);
    if (duration > limit) {
      setDuration(limit);
    }
    setBuilt(null);
    setOfflineResult(null);
  };

  const handleBuildSticker = async () => {
    if (!source) return;
    setBuilding(true);
    setErrorMsg('');
    setOkMsg('');
    setSendFeedback(null);
    setBuilt(null);
    setOfflineResult(null);

    try {
      if ((audioMode === 'upload' || audioMode === 'mic') && !customAudio?.audioUrl) {
        throw new Error('Primero sube o graba el audio que deseas añadir.');
      }

      if (source.isOffline || !serverOnline) {
        const res = await exportLocalClipOffline({
          mediaUrl: source.mediaUrl,
          kind: source.kind,
          start,
          duration,
          crop,
          audioMode,
          customAudioUrl: customAudio?.audioUrl || null,
          audioStart,
        });
        setOfflineResult(res);
        setOkMsg('Sticker/clip listo desde tu navegador para descargar o compartir por WhatsApp.');
        return;
      }

      const data = await api.buildSticker({
        sourceId: source.sourceId,
        start,
        duration,
        maxDuration: maxLimit,
        crop,
        audioMode,
        audioId: customAudio?.audioId || null,
        audioStart,
      });
      setBuilt(data);
      setOkMsg('Sticker generado. Toca la vista previa para escucharlo o envíalo directo a WhatsApp.');
    } catch (err) {
      setErrorMsg(err.message);
    } finally {
      setBuilding(false);
    }
  };

  const handleSendWhatsApp = async () => {
    if (!built?.jobId) return;
    setSendingWa(true);
    setSendFeedback(null);
    setErrorMsg('');
    setOkMsg('');
    try {
      const res = await api.waSend({
        jobId: built.jobId,
        target: targetJid,
        sendMode,
        isAnimated: built.isAnimated,
        duration: built.duration,
      });
      const msg = `¡Enviado a WhatsApp (${res.jid})! Revisa tu chat para oírlo y guardarlo en Favoritos.`;
      setSendFeedback({ type: 'ok', text: msg });
      setOkMsg(msg);
    } catch (err) {
      const msg = err.message || 'No se pudo enviar por WhatsApp.';
      setSendFeedback({ type: 'err', text: msg });
      setErrorMsg(msg);
    } finally {
      setSendingWa(false);
    }
  };

  const playStickerPreviewSound = () => {
    const a = previewAudioRef.current;
    if (!a || !built?.hasAudio) return;
    if (previewPlaying) {
      a.pause();
      a.currentTime = 0;
      setPreviewPlaying(false);
      return;
    }
    loopCountRef.current = 1;
    a.currentTime = 0;
    a.play().catch(() => {});
    setPreviewPlaying(true);
  };

  const waConnected = serverInfo?.whatsapp?.status === 'connected';

  return (
    <div className="app-shell">
      <header className="topbar">
        <div className="brand">
          <img src="./logo.png" alt="LexSticker" className="brand-logo" />
          <div>
            <h1>LexSticker</h1>
            <span>Web para Crear Stickers con Audio de WhatsApp</span>
          </div>
        </div>

        <nav className="nav-pills">
          <a href="https://lexjunior.pages.dev/" className="nav-btn" style={{ textDecoration: 'none' }}>
            ← Lex Junior
          </a>
          <button
            type="button"
            className={`nav-btn ${tab === 'create' ? 'active' : ''}`}
            onClick={() => setTab('create')}
          >
            <Sparkles size={15} /> Crear Sticker
          </button>
          <button
            type="button"
            className={`nav-btn ${tab === 'whatsapp' ? (waConnected ? 'wa-active' : 'active') : ''}`}
            onClick={() => setTab('whatsapp')}
          >
            <span className={`status-dot ${waConnected ? 'ok' : 'warn'}`} />
            WhatsApp {waConnected ? 'Conectado' : 'Vincular'}
          </button>
        </nav>
      </header>

      {!serverOnline && (
        <div className="alert warn" style={{ marginBottom: 16 }}>
          <WifiOff size={15} style={{ verticalAlign: '-2px', marginRight: 6 }} />
          {serverWaking ? (
            <>
              <strong>⏳ Despertando servidor de stickers con audio y WhatsApp...</strong> (en el plan gratuito tarda ~35 s tras inactividad; mientras tanto ya puedes ir subiendo y recortando tu archivo).
            </>
          ) : (
            <>
              <strong>Servidor en reposo o desconectado:</strong> puedes seguir recortando y descargando clips localmente, o ir a <strong>WhatsApp Vincular → Actualizar</strong> para despertar el servidor de QR.
            </>
          )}
        </div>
      )}

      {tab === 'whatsapp' && (
        <WhatsAppPanel
          serverOnline={serverOnline}
          serverWaking={serverWaking}
          waStatus={serverInfo?.whatsapp}
          onRefresh={refreshServer}
          targetJid={targetJid}
          onChangeTargetJid={updateTargetJid}
          sendMode={sendMode}
          onChangeSendMode={updateSendMode}
          onGoCreate={() => setTab('create')}
        />
      )}

      {tab === 'create' && (
        <>
          <div
            className={`source-bar ${source ? 'source-bar-compact' : ''}`}
            onDragOver={(e) => e.preventDefault()}
            onDrop={(e) => {
              e.preventDefault();
              const dropped = e.dataTransfer?.files?.[0];
              if (dropped) processSelectedFile(dropped);
            }}
          >
            <div className="link-row" style={{ alignItems: 'center', justifyContent: 'space-between' }}>
              <div className="upload-lead">
                <div className="upload-icon-wrap">
                  <Film size={22} />
                </div>
                <div>
                  <div style={{ fontWeight: 700, fontSize: 15 }}>
                    {source ? 'Archivo cargado listo para editar' : 'Sube tu video, GIF o imagen'}
                  </div>
                  <div className="muted-text">
                    {source
                      ? 'Arrastra otro archivo o pulsa el botón para cambiarlo.'
                      : 'Elige un archivo de tu galería o arrástralo aquí para recortar en 512×512.'}
                  </div>
                </div>
              </div>

              <button
                type="button"
                className="btn btn-brand"
                onClick={() => fileInputRef.current?.click()}
                disabled={loadingMedia}
              >
                <Upload size={16} />
                {loadingMedia ? 'Cargando...' : source ? 'Cambiar archivo' : 'Subir video / GIF / foto'}
              </button>
              <input
                ref={fileInputRef}
                type="file"
                accept="video/*,image/*"
                hidden
                onChange={handleUploadFile}
              />
            </div>

            {!source && (
              <div className="platform-chips">
                <span>Formatos:</span>
                <span className="chip">MP4 / MOV / WEBM</span>
                <span className="chip">GIF animado</span>
                <span className="chip">JPG / PNG / WebP</span>
              </div>
            )}
          </div>

          {errorMsg && (
            <div className="alert err" style={{ marginBottom: 16 }}>
              {errorMsg}
            </div>
          )}
          {okMsg && (
            <div className="alert ok" style={{ marginBottom: 16 }}>
              {okMsg}
            </div>
          )}

          <div className="studio-grid">
            <CropCanvas
              mediaUrl={source?.mediaUrl}
              kind={source?.kind}
              naturalWidth={source?.width}
              naturalHeight={source?.height}
              start={start}
              duration={duration}
              crop={crop}
              onChangeCrop={(c) => {
                setCrop(c);
                setBuilt(null);
              }}
              onTimeUpdate={setCurrentTime}
              customAudioUrl={customAudio?.audioUrl}
              audioMode={audioMode}
              audioStart={audioStart}
            />

            <div className="panel" style={{ display: 'flex', flexDirection: 'column', gap: 18 }}>
              <TimelineTrim
                mediaUrl={source?.mediaUrl}
                kind={source?.kind}
                totalDuration={source?.duration || 10}
                maxLimit={maxLimit}
                onChangeMaxLimit={handleChangeMaxLimit}
                start={start}
                duration={duration}
                currentTime={currentTime}
                onChangeRange={(ns, nd) => {
                  setStart(ns);
                  setDuration(nd);
                  if (audioMode === 'original') setAudioStart(ns);
                  setBuilt(null);
                }}
              />

              <div>
                <div className="panel-title">
                  <h2>Sonido del sticker</h2>
                  {audioMode !== 'none' && <Volume2 size={17} color="#ff8c2a" />}
                </div>

                <div className="audio-list">
                  <button
                    type="button"
                    className={`audio-card ${audioMode === 'none' ? 'active' : ''}`}
                    onClick={() => {
                      setAudioMode('none');
                      setBuilt(null);
                    }}
                  >
                    <div className="audio-card-head">
                      <span className="audio-radio" />
                      <div>
                        <div className="audio-card-title">
                          <VolumeX size={14} style={{ verticalAlign: '-2px', marginRight: 5 }} />
                          Sin sonido
                        </div>
                        <div className="audio-card-sub">Sticker animado o estático tradicional.</div>
                      </div>
                    </div>
                  </button>

                  {source?.kind !== 'image' && source?.hasAudio !== false && (
                    <div
                      className={`audio-card ${audioMode === 'original' ? 'active' : ''}`}
                      onClick={() => {
                        setAudioMode('original');
                        setAudioStart(start);
                        setBuilt(null);
                      }}
                      role="button"
                      tabIndex={0}
                    >
                      <div className="audio-card-head">
                        <span className="audio-radio" />
                        <div>
                          <div className="audio-card-title">
                            <Volume2 size={14} style={{ verticalAlign: '-2px', marginRight: 5 }} />
                            Sonido original
                          </div>
                          <div className="audio-card-sub">
                            Audio del mismo video (puedes mover el punto de inicio).
                          </div>
                        </div>
                      </div>
                      {audioMode === 'original' && source && (
                        <div className="audio-extra" onClick={(e) => e.stopPropagation()}>
                          <div className="slider-row">
                            <span className="muted-text">Inicio audio: {audioStart.toFixed(1)} s</span>
                            <input
                              type="range"
                              min="0"
                              max={Math.max(0, (source.duration || 10) - duration)}
                              step="0.1"
                              value={audioStart}
                              onChange={(e) => {
                                setAudioStart(Number(e.target.value));
                                setBuilt(null);
                              }}
                            />
                          </div>
                        </div>
                      )}
                    </div>
                  )}

                  <div
                    className={`audio-card ${audioMode === 'upload' ? 'active' : ''}`}
                    onClick={() => setAudioMode('upload')}
                    role="button"
                    tabIndex={0}
                  >
                    <div className="audio-card-head">
                      <span className="audio-radio" />
                      <div>
                        <div className="audio-card-title">
                          <Music size={14} style={{ verticalAlign: '-2px', marginRight: 5 }} />
                          Subir audio
                        </div>
                        <div className="audio-card-sub">
                          MP3, WAV, OGG, M4A o extrae el audio de otro video.
                        </div>
                      </div>
                    </div>
                    {audioMode === 'upload' && (
                      <div className="audio-extra" onClick={(e) => e.stopPropagation()}>
                        <div className="row-wrap">
                          <button
                            type="button"
                            className="btn btn-sm"
                            onClick={() => audioInputRef.current?.click()}
                            disabled={loadingAudio}
                          >
                            <Upload size={14} /> {loadingAudio ? 'Cargando...' : 'Elegir archivo de audio'}
                          </button>
                          {customAudio && audioMode === 'upload' && (
                            <span className="muted-text">{customAudio.label}</span>
                          )}
                        </div>
                        <input
                          ref={audioInputRef}
                          type="file"
                          accept="audio/*,video/*"
                          hidden
                          onChange={handleUploadCustomAudio}
                        />
                        {customAudio && audioMode === 'upload' && (
                          <div className="slider-row">
                            <span className="muted-text">Inicio: {audioStart.toFixed(1)} s</span>
                            <input
                              type="range"
                              min="0"
                              max={Math.max(0, (customAudio.duration || 10) - 0.5)}
                              step="0.1"
                              value={audioStart}
                              onChange={(e) => setAudioStart(Number(e.target.value))}
                            />
                          </div>
                        )}
                      </div>
                    )}
                  </div>

                  <div
                    className={`audio-card ${audioMode === 'mic' ? 'active' : ''}`}
                    onClick={() => setAudioMode('mic')}
                    role="button"
                    tabIndex={0}
                  >
                    <div className="audio-card-head">
                      <span className="audio-radio" />
                      <div>
                        <div className="audio-card-title">
                          <Mic size={14} style={{ verticalAlign: '-2px', marginRight: 5 }} />
                          Grabar voz
                        </div>
                        <div className="audio-card-sub">
                          Graba tu voz o un efecto directamente con el micrófono.
                        </div>
                      </div>
                    </div>
                    {audioMode === 'mic' && (
                      <div className="audio-extra" onClick={(e) => e.stopPropagation()}>
                        <div className="row-wrap">
                          <button
                            type="button"
                            className={`btn btn-sm ${recording ? 'btn-danger' : 'btn-brand'}`}
                            onClick={toggleRecordMic}
                            disabled={loadingAudio}
                          >
                            {recording ? <Square size={14} /> : <Mic size={14} />}
                            {recording ? `Detener (${recSeconds}s)` : 'Grabar ahora'}
                          </button>
                          {customAudio && audioMode === 'mic' && (
                            <span className="muted-text">
                              Grabado ({customAudio.duration.toFixed(1)} s)
                            </span>
                          )}
                        </div>
                      </div>
                    )}
                  </div>
                </div>
              </div>

              <button
                type="button"
                className="btn btn-orange"
                style={{ width: '100%', padding: '13px 18px', fontSize: 15 }}
                onClick={handleBuildSticker}
                disabled={!source || building}
              >
                <Sparkles size={17} />
                {building ? 'Procesando sticker (WebP + Opus)...' : 'Generar Sticker'}
              </button>

              {built && (
                <div className="preview-card">
                  <div className="wa-chat-stage">
                    <div className="sticker-bubble" onClick={playStickerPreviewSound}>
                      <img src={resolveMediaUrl(built.files.webp)} alt="Sticker generado" />
                      {built.hasAudio && (
                        <span className="sound-tag">
                          <Volume2 size={13} />
                          {previewPlaying ? 'Sonando...' : 'Toca para oír'}
                        </span>
                      )}
                    </div>
                  </div>

                  {built.hasAudio && (
                    <audio
                      ref={previewAudioRef}
                      src={resolveMediaUrl(built.files.ogg)}
                      onEnded={() => {
                        if (loopCountRef.current < 3) {
                          loopCountRef.current += 1;
                          previewAudioRef.current?.play().catch(() => {});
                        } else {
                          setPreviewPlaying(false);
                        }
                      }}
                    />
                  )}

                  <div className="stats-row">
                    <span className="stat-pill">{built.side}×{built.side} px</span>
                    <span className="stat-pill">{built.duration} s</span>
                    <span className="stat-pill">WebP: {(built.webpBytes / 1024).toFixed(0)} KB</span>
                    {built.hasAudio && (
                      <span className="stat-pill">Opus: {(built.audioBytes / 1024).toFixed(0)} KB</span>
                    )}
                  </div>

                  {waConnected ? (
                    <>
                      <select
                        className="input-box"
                        style={{ width: '100%', padding: '9px 12px', fontSize: 13 }}
                        value={sendMode}
                        onChange={(e) => updateSendMode(e.target.value)}
                      >
                        <option value="audioSticker">
                          Modo: Sticker con audio nativo (audioStickerMessage)
                        </option>
                        <option value="stickerDirect">
                          Modo: Sticker directo (stickerMessage + audioMessage)
                        </option>
                        <option value="dual">
                          Modo: Sticker + Nota de voz aparte (universal)
                        </option>
                      </select>

                      <button
                        type="button"
                        className="btn btn-primary"
                        style={{ width: '100%', padding: '13px 16px', fontSize: 15 }}
                        onClick={handleSendWhatsApp}
                        disabled={sendingWa}
                      >
                        <Send size={17} />
                        {sendingWa
                          ? 'Enviando a tu WhatsApp...'
                          : `Enviar por WhatsApp ahora (${targetJid ? targetJid.split('@')[0] : 'Tu chat'})`}
                      </button>

                      {sendFeedback && (
                        <div className={`alert ${sendFeedback.type === 'ok' ? 'ok' : 'err'}`}>
                          {sendFeedback.text}
                        </div>
                      )}
                    </>
                  ) : (
                    <button
                      type="button"
                      className="btn btn-primary"
                      style={{ width: '100%', padding: '12px 16px' }}
                      onClick={() => setTab('whatsapp')}
                    >
                      <Send size={16} /> Conectar WhatsApp para enviarlo directo
                    </button>
                  )}

                  <div className="row-wrap" style={{ justifyContent: 'center' }}>
                    <button
                      type="button"
                      className="btn btn-sm"
                      onClick={() =>
                        shareOrDownloadFromUrl(built.files.mp4, `lexsticker-${built.jobId.slice(0, 6)}.mp4`)
                      }
                    >
                      <Share2 size={14} /> Compartir clip (.mp4)
                    </button>
                    <button
                      type="button"
                      className="btn btn-sm"
                      onClick={() =>
                        shareOrDownloadFromUrl(built.files.webp, `lexsticker-${built.jobId.slice(0, 6)}.webp`)
                      }
                    >
                      <Download size={14} /> Descargar .webp
                    </button>
                  </div>
                </div>
              )}

              {offlineResult && (
                <div className="preview-card">
                  <div className="wa-chat-stage">
                    <div className="sticker-bubble" style={{ width: 196, height: 196 }}>
                      {offlineResult.isImage ? (
                        <img src={offlineResult.previewUrl} alt="Sticker generado" />
                      ) : (
                        <video
                          src={offlineResult.previewUrl}
                          controls
                          loop
                          playsInline
                          style={{ width: '100%', height: '100%', objectFit: 'cover', borderRadius: 12 }}
                        />
                      )}
                    </div>
                  </div>
                  <div className="stats-row">
                    <span className="stat-pill">512×512 px</span>
                    <span className="stat-pill">{(offlineResult.blob.size / 1024).toFixed(0)} KB</span>
                    <span className="stat-pill">{offlineResult.hasAudio ? 'Con audio' : 'Sin audio'}</span>
                  </div>
                  <button
                    type="button"
                    className="btn btn-primary"
                    style={{ width: '100%', padding: '12px 16px' }}
                    onClick={() => shareOrDownloadBlob(offlineResult.blob, offlineResult.filename)}
                  >
                    <Download size={16} /> Descargar / Compartir ({offlineResult.filename})
                  </button>
                  <button
                    type="button"
                    className="btn btn-sm"
                    style={{ width: '100%' }}
                    onClick={() => setTab('whatsapp')}
                  >
                    <Send size={14} /> ¿Tienes servidor Docker? Conéctalo para envío directo por QR
                  </button>
                </div>
              )}
            </div>
          </div>
        </>
      )}
    </div>
  );
}
