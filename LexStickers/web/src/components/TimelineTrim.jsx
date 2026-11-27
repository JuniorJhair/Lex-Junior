import React, { useEffect, useRef, useState } from 'react';

function fmtTime(sec) {
  const s = Math.max(0, Number(sec) || 0);
  const mins = Math.floor(s / 60);
  const rem = (s % 60).toFixed(1).padStart(4, '0');
  return `${mins}:${rem}`;
}

export default function TimelineTrim({
  mediaUrl,
  kind,
  totalDuration,
  maxLimit,
  onChangeMaxLimit,
  start,
  duration,
  currentTime,
  onChangeRange,
}) {
  const barRef = useRef(null);
  const dragRef = useRef(null);
  const [thumbs, setThumbs] = useState([]);

  const safeTotal = Math.max(0.5, totalDuration || 10);
  const end = Math.min(safeTotal, start + duration);

  useEffect(() => {
    if (!mediaUrl || kind !== 'video' || !safeTotal) {
      setThumbs([]);
      return;
    }
    let cancelled = false;
    const v = document.createElement('video');
    v.crossOrigin = 'anonymous';
    v.muted = true;
    v.preload = 'auto';
    v.src = mediaUrl;

    const canvas = document.createElement('canvas');
    canvas.width = 72;
    canvas.height = 54;
    const ctx = canvas.getContext('2d');

    const run = async () => {
      await new Promise((res, rej) => {
        v.onloadeddata = res;
        v.onerror = rej;
      });
      const count = 7;
      const list = [];
      for (let i = 0; i < count; i++) {
        if (cancelled) return;
        const t = ((i + 0.5) / count) * Math.min(safeTotal, v.duration || safeTotal);
        v.currentTime = t;
        await new Promise((r) => {
          const timer = setTimeout(r, 600);
          v.onseeked = () => {
            clearTimeout(timer);
            r();
          };
        });
        ctx.drawImage(v, 0, 0, canvas.width, canvas.height);
        list.push(canvas.toDataURL('image/jpeg', 0.6));
      }
      if (!cancelled) setThumbs(list);
    };

    run().catch(() => {});
    return () => {
      cancelled = true;
      v.src = '';
    };
  }, [mediaUrl, kind, safeTotal]);

  const beginDrag = (e, mode) => {
    e.stopPropagation();
    e.preventDefault();
    const rect = barRef.current?.getBoundingClientRect();
    if (!rect) return;
    dragRef.current = {
      mode,
      startX: e.clientX,
      rect,
      initStart: start,
      initEnd: end,
    };
    e.currentTarget.setPointerCapture?.(e.pointerId);
  };

  const onPointerMove = (e) => {
    const d = dragRef.current;
    if (!d) return;
    const dt = ((e.clientX - d.startX) / d.rect.width) * safeTotal;
    const minDur = 0.5;

    if (d.mode === 'move') {
      const len = d.initEnd - d.initStart;
      const ns = Math.max(0, Math.min(safeTotal - len, d.initStart + dt));
      onChangeRange(Number(ns.toFixed(1)), Number(len.toFixed(1)));
      return;
    }

    if (d.mode === 'left') {
      let ns = Math.max(0, Math.min(d.initEnd - minDur, d.initStart + dt));
      let newDur = d.initEnd - ns;
      if (newDur > maxLimit) {
        newDur = maxLimit;
        ns = d.initEnd - maxLimit;
      }
      onChangeRange(Number(ns.toFixed(1)), Number(newDur.toFixed(1)));
      return;
    }

    if (d.mode === 'right') {
      let ne = Math.min(safeTotal, Math.max(d.initStart + minDur, d.initEnd + dt));
      let newDur = ne - d.initStart;
      if (newDur > maxLimit) {
        newDur = maxLimit;
      }
      onChangeRange(Number(d.initStart.toFixed(1)), Number(newDur.toFixed(1)));
    }
  };

  const endDrag = () => {
    dragRef.current = null;
  };

  const leftPct = (start / safeTotal) * 100;
  const widthPct = ((end - start) / safeTotal) * 100;
  const playheadPct = Math.max(0, Math.min(100, (currentTime / safeTotal) * 100));

  return (
    <div>
      <div className="panel-title">
        <h2>Tramo del sticker</h2>
        <span className="pill-badge">
          {duration.toFixed(1)} s · máx. {maxLimit} s
        </span>
      </div>

      <div className="seg-switch">
        <button
          type="button"
          className={maxLimit === 5 ? 'on' : ''}
          onClick={() => onChangeMaxLimit(5)}
        >
          5 segundos
        </button>
        <button
          type="button"
          className={maxLimit === 10 ? 'on' : ''}
          onClick={() => onChangeMaxLimit(10)}
        >
          10 segundos
        </button>
      </div>

      {kind === 'image' ? (
        <div className="slider-row" style={{ marginTop: 6 }}>
          <span className="muted-text">Duración del audio</span>
          <input
            type="range"
            min="1"
            max={maxLimit}
            step="0.5"
            value={duration}
            onChange={(e) => onChangeRange(0, Number(e.target.value))}
          />
          <span className="muted-text">{duration.toFixed(1)} s</span>
        </div>
      ) : (
        <>
          <div
            className="timeline-box"
            ref={barRef}
            onPointerMove={onPointerMove}
            onPointerUp={endDrag}
            onPointerCancel={endDrag}
          >
            <div className="timeline-thumbs">
              {thumbs.map((src, idx) => (
                <img key={idx} src={src} alt="" />
              ))}
            </div>
            <div className="timeline-dim" style={{ left: 0, width: `${leftPct}%` }} />
            <div
              className="timeline-dim"
              style={{ left: `${leftPct + widthPct}%`, right: 0 }}
            />
            <div
              className="timeline-selection"
              style={{ left: `${leftPct}%`, width: `${widthPct}%` }}
              onPointerDown={(e) => beginDrag(e, 'move')}
            >
              <div className="timeline-handle left" onPointerDown={(e) => beginDrag(e, 'left')} />
              <div className="timeline-handle right" onPointerDown={(e) => beginDrag(e, 'right')} />
            </div>
            <div className="timeline-playhead" style={{ left: `${playheadPct}%` }} />
          </div>

          <div className="timeline-times">
            <span>Inicio: {fmtTime(start)}</span>
            <span>Fin: {fmtTime(end)}</span>
            <span>Total: {fmtTime(safeTotal)}</span>
          </div>

          <div className="row-wrap" style={{ marginTop: 8 }}>
            <button
              type="button"
              className="btn btn-sm"
              onClick={() => onChangeRange(Math.max(0, Number((start - 0.5).toFixed(1))), duration)}
            >
              -0.5s inicio
            </button>
            <button
              type="button"
              className="btn btn-sm"
              onClick={() =>
                onChangeRange(
                  Math.min(Math.max(0, safeTotal - duration), Number((start + 0.5).toFixed(1))),
                  duration
                )
              }
            >
              +0.5s inicio
            </button>
            <button
              type="button"
              className="btn btn-sm"
              onClick={() => onChangeRange(start, Math.max(0.5, Number((duration - 0.5).toFixed(1))))}
            >
              -0.5s largo
            </button>
            <button
              type="button"
              className="btn btn-sm"
              onClick={() =>
                onChangeRange(
                  start,
                  Math.min(maxLimit, safeTotal - start, Number((duration + 0.5).toFixed(1)))
                )
              }
            >
              +0.5s largo
            </button>
          </div>
        </>
      )}
    </div>
  );
}
