import React, { useEffect, useRef, useState } from 'react';
import { Pause, Play, Volume2, VolumeX } from 'lucide-react';

export default function CropCanvas({
  mediaUrl,
  kind,
  naturalWidth,
  naturalHeight,
  start,
  duration,
  crop,
  onChangeCrop,
  onVideoRef,
  onTimeUpdate,
  customAudioUrl,
  audioMode,
  audioStart,
}) {
  const mediaBoxRef = useRef(null);
  const videoRef = useRef(null);
  const audioRef = useRef(null);
  const dragRef = useRef(null);
  const [playing, setPlaying] = useState(false);
  const [mutedPreview, setMutedPreview] = useState(false);

  useEffect(() => {
    if (onVideoRef) onVideoRef(videoRef.current);
  }, [mediaUrl, onVideoRef]);

  // Keep video seeked to start when user drags timeline
  useEffect(() => {
    const v = videoRef.current;
    if (!v || kind !== 'video') return;
    if (!playing && Math.abs(v.currentTime - start) > 0.08) {
      v.currentTime = start;
    }
  }, [start, kind, playing]);

  // Loop within [start, start + duration]
  useEffect(() => {
    const v = videoRef.current;
    if (!v || kind !== 'video') return;

    const handleTime = () => {
      if (onTimeUpdate) onTimeUpdate(v.currentTime);
      if (v.currentTime >= start + duration || v.currentTime < start - 0.15) {
        v.currentTime = start;
        if (audioRef.current && audioMode !== 'original' && audioMode !== 'none') {
          audioRef.current.currentTime = audioStart || 0;
        }
      }
    };
    v.addEventListener('timeupdate', handleTime);
    return () => v.removeEventListener('timeupdate', handleTime);
  }, [start, duration, kind, onTimeUpdate, audioMode, audioStart]);

  const togglePlay = async () => {
    if (kind === 'image') {
      const a = audioRef.current;
      if (!a) return;
      if (playing) {
        a.pause();
        setPlaying(false);
      } else {
        a.currentTime = audioStart || 0;
        await a.play().catch(() => {});
        setPlaying(true);
      }
      return;
    }

    const v = videoRef.current;
    if (!v) return;
    if (playing) {
      v.pause();
      if (audioRef.current) audioRef.current.pause();
      setPlaying(false);
    } else {
      v.currentTime = start;
      v.muted = mutedPreview || audioMode !== 'original';
      await v.play().catch(() => {});
      if (audioRef.current && audioMode !== 'original' && audioMode !== 'none' && !mutedPreview) {
        audioRef.current.currentTime = audioStart || 0;
        audioRef.current.play().catch(() => {});
      }
      setPlaying(true);
    }
  };

  // Compute displayed crop box percentage inside the media box
  const w = Math.max(1, naturalWidth || 512);
  const h = Math.max(1, naturalHeight || 512);
  const minSide = Math.min(w, h);
  const boxPx = crop.size * minSide;
  const leftPct = ((crop.x * w) / w) * 100;
  const topPct = ((crop.y * h) / h) * 100;
  const widthPct = (boxPx / w) * 100;
  const heightPct = (boxPx / h) * 100;

  const beginDrag = (e, mode) => {
    e.stopPropagation();
    e.preventDefault();
    const box = mediaBoxRef.current;
    if (!box) return;
    const rect = box.getBoundingClientRect();
    dragRef.current = {
      mode,
      startX: e.clientX,
      startY: e.clientY,
      rect,
      initCrop: { ...crop },
    };
    e.currentTarget.setPointerCapture?.(e.pointerId);
  };

  const onPointerMove = (e) => {
    const d = dragRef.current;
    if (!d) return;
    const dxNorm = (e.clientX - d.startX) / d.rect.width;
    const dyNorm = (e.clientY - d.startY) / d.rect.height;

    const maxX = Math.max(0, 1 - (d.initCrop.size * minSide) / w);
    const maxY = Math.max(0, 1 - (d.initCrop.size * minSide) / h);

    if (d.mode === 'move') {
      const nx = Math.max(0, Math.min(maxX, d.initCrop.x + dxNorm));
      const ny = Math.max(0, Math.min(maxY, d.initCrop.y + dyNorm));
      onChangeCrop({ ...d.initCrop, x: nx, y: ny });
      return;
    }

    // Resize from any corner while keeping square aspect
    let delta = dxNorm;
    if (d.mode === 'nw' || d.mode === 'sw') delta = -dxNorm;
    const newSize = Math.max(0.25, Math.min(1, d.initCrop.size + delta * (w / minSide)));
    const newBoxPx = newSize * minSide;
    const nx = Math.max(0, Math.min(1 - newBoxPx / w, d.initCrop.x));
    const ny = Math.max(0, Math.min(1 - newBoxPx / h, d.initCrop.y));
    onChangeCrop({ x: nx, y: ny, size: newSize });
  };

  const endDrag = () => {
    dragRef.current = null;
  };

  const handleSizeSlider = (val) => {
    const newSize = Number(val);
    const newBoxPx = newSize * minSide;
    const nx = Math.max(0, Math.min(1 - newBoxPx / w, crop.x));
    const ny = Math.max(0, Math.min(1 - newBoxPx / h, crop.y));
    onChangeCrop({ x: nx, y: ny, size: newSize });
  };

  return (
    <div className="panel">
      <div className="panel-title">
        <h2>Recorte cuadrado (512 × 512 px)</h2>
        {mediaUrl && (
          <div className="row-wrap">
            <button type="button" className="btn btn-sm" onClick={togglePlay}>
              {playing ? <Pause size={14} /> : <Play size={14} />}
              {playing ? 'Pausar' : 'Probar tramo'}
            </button>
            <button
              type="button"
              className="btn btn-sm"
              onClick={() => {
                const next = !mutedPreview;
                setMutedPreview(next);
                if (videoRef.current) videoRef.current.muted = next || audioMode !== 'original';
                if (audioRef.current) audioRef.current.muted = next;
              }}
              title="Silenciar vista previa"
            >
              {mutedPreview ? <VolumeX size={14} /> : <Volume2 size={14} />}
            </button>
          </div>
        )}
      </div>

      <div
        className="stage-wrap"
        onPointerMove={onPointerMove}
        onPointerUp={endDrag}
        onPointerCancel={endDrag}
      >
        {!mediaUrl ? (
          <div className="stage-empty">
            <p style={{ margin: '0 0 6px', fontWeight: 600, color: '#e9edef' }}>
              Sube un video, GIF o imagen arriba
            </p>
            <p className="muted-text" style={{ margin: 0 }}>
              Elige un archivo desde tu laptop o la galería del celular para recortarlo en 512×512.
            </p>
          </div>
        ) : (
          <div className="stage-media-box" ref={mediaBoxRef}>
            {kind === 'image' ? (
              <img src={mediaUrl} alt="Vista previa del sticker" draggable={false} />
            ) : (
              <video
                ref={videoRef}
                src={mediaUrl}
                playsInline
                muted={mutedPreview || audioMode !== 'original'}
                preload="auto"
              />
            )}

            <div className="crop-overlay">
              <div
                className="crop-box"
                style={{
                  left: `${leftPct}%`,
                  top: `${topPct}%`,
                  width: `${widthPct}%`,
                  height: `${heightPct}%`,
                }}
                onPointerDown={(e) => beginDrag(e, 'move')}
              >
                <div className="crop-corner nw" onPointerDown={(e) => beginDrag(e, 'nw')} />
                <div className="crop-corner ne" onPointerDown={(e) => beginDrag(e, 'ne')} />
                <div className="crop-corner sw" onPointerDown={(e) => beginDrag(e, 'sw')} />
                <div className="crop-corner se" onPointerDown={(e) => beginDrag(e, 'se')} />
              </div>
            </div>
          </div>
        )}
      </div>

      {customAudioUrl && (
        <audio
          ref={audioRef}
          src={customAudioUrl}
          onEnded={() => {
            if (kind === 'image') setPlaying(false);
          }}
        />
      )}

      {mediaUrl && (
        <div className="stage-controls">
          <div className="muted-text" style={{ textAlign: 'center' }}>
            Arrastra para mover · Estira las esquinas o desliza para el tamaño
          </div>
          <div className="slider-row">
            <span className="muted-text">Zoom</span>
            <input
              type="range"
              min="0.25"
              max="1"
              step="0.01"
              value={crop.size}
              onChange={(e) => handleSizeSlider(e.target.value)}
            />
            <span className="muted-text">{Math.round(crop.size * 100)}%</span>
          </div>
        </div>
      )}
    </div>
  );
}
