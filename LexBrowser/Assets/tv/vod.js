'use strict';

function convertSrtToVtt(srtText) {
  if (!srtText) return 'WEBVTT\n\n';
  let vtt = 'WEBVTT\n\n' + srtText.trim().replace(/\r\n/g, '\n').replace(/\r/g, '\n');
  vtt = vtt.replace(/(\d{2}:\d{2}:\d{2}),(\d{3})/g, '$1.$2');
  return vtt;
}

function formatTime(seconds) {
  if (isNaN(seconds) || seconds < 0) return '00:00';
  const s = Math.floor(seconds);
  const hrs = Math.floor(s / 3600);
  const mins = Math.floor((s % 3600) / 60);
  const secs = s % 60;
  if (hrs > 0) {
    return `${hrs}:${String(mins).padStart(2, '0')}:${String(secs).padStart(2, '0')}`;
  }
  return `${String(mins).padStart(2, '0')}:${String(secs).padStart(2, '0')}`;
}

if (typeof window !== 'undefined' && typeof document !== 'undefined') {
  const state = {
    items: [],
    progressMap: new Map(),
    filtered: [],
    activeItem: null,
    activeHls: null,
    progressInterval: null,
    selectedSeries: null,
    activeSeason: 1,
    activeSid: null,
    vodSessionSeq: 0
  };

  const els = {};

  function $(id) {
    return document.getElementById(id);
  }

  function buildVodPosterUrl(logo, itemUrl) {
    if (!logo) return null;
    const trimmed = String(logo).trim();
    if (!trimmed) return null;
    if (trimmed.startsWith('/api/image')) return trimmed;

    let connParam = '';
    if (itemUrl && itemUrl.includes('conn=')) {
      try {
        const u = new URL(itemUrl, window.location.origin);
        const c = u.searchParams.get('conn');
        if (c) connParam = `&conn=${encodeURIComponent(c)}`;
      } catch (_) { }
    }

    return `/api/image?u=${encodeURIComponent(trimmed)}${connParam}`;
  }

  function showPlayerStatus(title, desc, icon = '⚠️', showRetry = true) {
    if (!els.vodPlayerStatus) return;
    if (els.vodStatusTitle) els.vodStatusTitle.textContent = title;
    if (els.vodStatusDesc) els.vodStatusDesc.textContent = desc;
    if (els.vodStatusIcon) els.vodStatusIcon.textContent = icon;
    if (els.btnRetryVod) els.btnRetryVod.classList.toggle('hidden', !showRetry);
    els.vodPlayerStatus.classList.remove('hidden');
  }

  function hidePlayerStatus() {
    if (els.vodPlayerStatus) els.vodPlayerStatus.classList.add('hidden');
  }

  async function apiJson(url, opts = {}) {
    const res = await fetch(url, opts);
    if (!res.ok) {
      let msg = `HTTP ${res.status}`;
      try {
        const data = await res.json();
        if (data && data.error) msg = data.error;
      } catch { }
      throw new Error(msg);
    }
    return res.json();
  }

  async function checkTermsAccepted() {
    try {
      const resp = await fetch('/api/terms/status');
      if (resp.ok) {
        const data = await resp.json();
        if (!data.accepted) {
          window.location.href = 'index.html';
        }
      }
    } catch { }
  }

  async function loadLibrary() {
    try {
      const data = await apiJson('/api/vod/library');
      state.items = data.items || [];
      state.progressMap.clear();
      (data.progress || []).forEach((p) => {
        if (p && p.url) state.progressMap.set(p.url, p);
      });

      renderGroupOptions();
      renderResumeSection();
      applyFilters();
    } catch (err) {
      console.error('Error al cargar biblioteca VOD:', err);
    }
  }

  function renderGroupOptions() {
    const current = els.vodGroupSelect.value;
    const groups = new Set();
    state.items.forEach((item) => {
      if (item.group) groups.add(item.group);
    });

    els.vodGroupSelect.innerHTML = '<option value="all">Categoría: Todas</option>';
    Array.from(groups).sort().forEach((g) => {
      const opt = document.createElement('option');
      opt.value = g;
      opt.textContent = g;
      els.vodGroupSelect.appendChild(opt);
    });

    if (Array.from(groups).includes(current)) {
      els.vodGroupSelect.value = current;
    }
  }

  function renderResumeSection() {
    els.resumeContainer.innerHTML = '';
    const progressItems = Array.from(state.progressMap.values());

    if (progressItems.length === 0) {
      els.resumeSection.classList.add('hidden');
      return;
    }

    const itemByUrl = new Map();
    state.items.forEach((i) => itemByUrl.set(i.url, i));

    let visibleCount = 0;
    progressItems.forEach((p) => {
      const item = itemByUrl.get(p.url);
      if (!item) return;

      visibleCount++;
      const card = document.createElement('div');
      card.className = 'resume-card';

      const thumb = document.createElement('div');
      thumb.className = 'resume-thumb';
      const safePoster = buildVodPosterUrl(item.logo, item.url);
      if (safePoster) {
        const img = document.createElement('img');
        img.loading = 'lazy';
        img.src = safePoster;
        img.alt = item.title;
        img.onerror = () => {
          img.remove();
          thumb.textContent = item.isSeries ? '📺' : '🎬';
        };
        thumb.appendChild(img);
      } else {
        thumb.textContent = item.isSeries ? '📺' : '🎬';
      }

      const pBar = document.createElement('div');
      pBar.className = 'resume-progress-bar';
      const pFill = document.createElement('div');
      pFill.className = 'resume-progress-fill';
      const pct = p.durationSec > 0 ? Math.min(100, Math.round((p.positionSec / p.durationSec) * 100)) : 0;
      pFill.style.width = `${pct}%`;
      pBar.appendChild(pFill);
      thumb.appendChild(pBar);

      const btnRemove = document.createElement('button');
      btnRemove.className = 'btn-remove-progress';
      btnRemove.type = 'button';
      btnRemove.textContent = '✕';
      btnRemove.title = 'Quitar de continuar viendo';
      btnRemove.addEventListener('click', async (e) => {
        e.stopPropagation();
        await apiJson('/api/vod/progress/clear', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ url: p.url })
        });
        state.progressMap.delete(p.url);
        renderResumeSection();
      });
      thumb.appendChild(btnRemove);

      const info = document.createElement('div');
      info.className = 'resume-info';

      const titleEl = document.createElement('div');
      titleEl.className = 'resume-title';
      titleEl.textContent = item.title;

      const metaEl = document.createElement('div');
      metaEl.className = 'resume-meta';
      const timeSpan = document.createElement('span');
      timeSpan.textContent = `${formatTime(p.positionSec)} / ${formatTime(p.durationSec)}`;
      const pctSpan = document.createElement('span');
      pctSpan.textContent = `${pct}%`;
      metaEl.appendChild(timeSpan);
      metaEl.appendChild(pctSpan);

      info.appendChild(titleEl);
      info.appendChild(metaEl);

      card.appendChild(thumb);
      card.appendChild(info);

      card.addEventListener('click', () => {
        openPlayer(item);
      });

      els.resumeContainer.appendChild(card);
    });

    els.resumeSection.classList.toggle('hidden', visibleCount === 0);
  }

  function applyFilters() {
    const query = (els.vodSearch.value || '').trim().toLowerCase();
    const typeFilter = els.vodTypeSelect.value;
    const groupFilter = els.vodGroupSelect.value;

    const filtered = state.items.filter((item) => {
      if (typeFilter === 'movies' && item.isSeries) return false;
      if (typeFilter === 'series' && !item.isSeries) return false;
      if (groupFilter !== 'all' && item.group !== groupFilter) return false;

      if (query) {
        const hay = `${item.title} ${item.seriesTitle || ''} ${item.group || ''}`.toLowerCase();
        if (!hay.includes(query)) return false;
      }
      return true;
    });

    state.filtered = filtered;
    els.libraryCountLabel.textContent = `(${filtered.length.toLocaleString('es')} títulos)`;
    renderLibraryGrid();
  }

  function renderLibraryGrid() {
    els.vodGrid.innerHTML = '';
    const hasItems = state.filtered.length > 0;
    els.emptyNotice.classList.toggle('hidden', state.items.length > 0);

    if (!hasItems) return;

    // Agrupar series para mostrar una tarjeta por serie
    const renderedSeries = new Set();

    state.filtered.forEach((item) => {
      if (item.isSeries && item.seriesTitle) {
        const sKey = item.seriesTitle.toLowerCase();
        if (renderedSeries.has(sKey)) return;
        renderedSeries.add(sKey);

        const seriesEpisodes = state.items.filter((i) => i.isSeries && (i.seriesTitle || '').toLowerCase() === sKey);
        const card = createSeriesCard(item.seriesTitle, seriesEpisodes, item.logo, item.group);
        els.vodGrid.appendChild(card);
      } else {
        const card = createMovieCard(item);
        els.vodGrid.appendChild(card);
      }
    });
  }

  function createMovieCard(item) {
    const card = document.createElement('div');
    card.className = 'vod-card';

    const poster = document.createElement('div');
    poster.className = 'vod-card-poster';
    const safePoster = buildVodPosterUrl(item.logo, item.url);
    if (safePoster) {
      const img = document.createElement('img');
      img.loading = 'lazy';
      img.src = safePoster;
      img.alt = item.title;
      img.onerror = () => {
        img.remove();
        poster.textContent = '🎬';
      };
      poster.appendChild(img);
    } else {
      poster.textContent = '🎬';
    }

    const badge = document.createElement('div');
    badge.className = 'vod-card-badge badge-movie';
    badge.textContent = 'Película';
    poster.appendChild(badge);

    const info = document.createElement('div');
    info.className = 'vod-card-info';

    const titleEl = document.createElement('div');
    titleEl.className = 'vod-card-title';
    titleEl.textContent = item.title;

    const subEl = document.createElement('div');
    subEl.className = 'vod-card-sub';
    subEl.textContent = item.group || 'Película';

    info.appendChild(titleEl);
    info.appendChild(subEl);

    card.appendChild(poster);
    card.appendChild(info);

    card.addEventListener('click', () => openPlayer(item));
    return card;
  }

  function createSeriesCard(seriesTitle, episodes, logo, group) {
    const card = document.createElement('div');
    card.className = 'vod-card';

    const poster = document.createElement('div');
    poster.className = 'vod-card-poster';
    const sampleEp = episodes && episodes.length > 0 ? episodes[0] : null;
    const safePoster = buildVodPosterUrl(logo, sampleEp ? sampleEp.url : null);
    if (safePoster) {
      const img = document.createElement('img');
      img.loading = 'lazy';
      img.src = safePoster;
      img.alt = seriesTitle;
      img.onerror = () => {
        img.remove();
        poster.textContent = '📺';
      };
      poster.appendChild(img);
    } else {
      poster.textContent = '📺';
    }

    const badge = document.createElement('div');
    badge.className = 'vod-card-badge badge-series';
    badge.textContent = `Serie · ${episodes.length} eps`;
    poster.appendChild(badge);

    const info = document.createElement('div');
    info.className = 'vod-card-info';

    const titleEl = document.createElement('div');
    titleEl.className = 'vod-card-title';
    titleEl.textContent = seriesTitle;

    const subEl = document.createElement('div');
    subEl.className = 'vod-card-sub';
    subEl.textContent = group || 'Serie';

    info.appendChild(titleEl);
    info.appendChild(subEl);

    card.appendChild(poster);
    card.appendChild(info);

    card.addEventListener('click', () => openSeriesModal(seriesTitle, episodes));
    return card;
  }

  function openSeriesModal(seriesTitle, episodes) {
    state.selectedSeries = { title: seriesTitle, episodes };
    els.seriesModalTitle.textContent = seriesTitle;

    const seasons = new Set();
    episodes.forEach((ep) => {
      seasons.add(ep.seasonNumber || 1);
    });
    const sortedSeasons = Array.from(seasons).sort((a, b) => a - b);
    state.activeSeason = sortedSeasons[0] || 1;

    renderSeasonTabs(sortedSeasons);
    renderEpisodesList();
    els.seriesModal.classList.remove('hidden');
  }

  function closeSeriesModal() {
    els.seriesModal.classList.add('hidden');
    state.selectedSeries = null;
  }

  function renderSeasonTabs(seasons) {
    els.seasonTabs.innerHTML = '';
    seasons.forEach((s) => {
      const btn = document.createElement('button');
      btn.className = `season-tab ${s === state.activeSeason ? 'active' : ''}`;
      btn.type = 'button';
      btn.textContent = `Temporada ${s}`;
      btn.addEventListener('click', () => {
        state.activeSeason = s;
        Array.from(els.seasonTabs.children).forEach((c) => c.classList.remove('active'));
        btn.classList.add('active');
        renderEpisodesList();
      });
      els.seasonTabs.appendChild(btn);
    });
  }

  function renderEpisodesList() {
    els.episodeList.innerHTML = '';
    if (!state.selectedSeries) return;

    const filtered = state.selectedSeries.episodes
      .filter((ep) => (ep.seasonNumber || 1) === state.activeSeason)
      .sort((a, b) => (a.episodeNumber || 0) - (b.episodeNumber || 0));

    filtered.forEach((ep) => {
      const row = document.createElement('div');
      row.className = 'episode-item';

      const meta = document.createElement('div');
      meta.className = 'episode-meta';

      const num = document.createElement('div');
      num.className = 'episode-num';
      num.textContent = `E${String(ep.episodeNumber || 1).padStart(2, '0')}`;

      const title = document.createElement('div');
      title.className = 'episode-title';
      title.textContent = ep.episodeTitle || ep.title;

      meta.appendChild(num);
      meta.appendChild(title);

      const btnPlay = document.createElement('button');
      btnPlay.className = 'btn btn-primary';
      btnPlay.type = 'button';
      btnPlay.textContent = '▶ Reproducir';

      row.appendChild(meta);
      row.appendChild(btnPlay);

      row.addEventListener('click', () => {
        closeSeriesModal();
        openPlayer(ep);
      });

      els.episodeList.appendChild(row);
    });
  }

  // --- Dedicated Video Player ---
  async function openPlayer(item) {
    state.activeItem = item;
    els.playerTitle.textContent = item.title;
    els.playerOverlay.classList.remove('hidden');
    hidePlayerStatus();

    destroyPlayer();

    const video = els.vodVideo;
    const sid = `vod_${++state.vodSessionSeq}`;
    state.activeSid = sid;

    let url = item.url;
    if (!url.includes('sid=')) {
      const sep = url.includes('?') ? '&' : '?';
      url = `${url}${sep}sid=${encodeURIComponent(sid)}`;
    }

    const isHls = url.toLowerCase().includes('.m3u8') || url.toLowerCase().includes('application/x-mpegurl');

    if (isHls && window.Hls && window.Hls.isSupported()) {
      showPlayerStatus('Conectando...', 'Iniciando reproducción HLS...', '⏳', false);
      const hls = new window.Hls({
        maxBufferLength: 60,
        maxMaxBufferLength: 120,
        enableWorker: true
      });
      hls.loadSource(url);
      hls.attachMedia(video);
      state.activeHls = hls;

      hls.on(window.Hls.Events.MANIFEST_PARSED, () => {
        hidePlayerStatus();
        populateSubtitlesFromHls(hls);
        video.play().catch(() => { });
      });

      hls.on(window.Hls.Events.ERROR, (_event, data) => {
        if (data && data.fatal) {
          showPlayerStatus('Error de transmisión HLS', data.details || 'Error al cargar manifiesto o segmentos.', '⚠️', true);
        }
      });
    } else {
      showPlayerStatus('Conectando...', 'Solicitando flujo de video al servidor...', '⏳', false);
      video.src = url;
      video.play().catch(() => { });
    }

    // Comprobar si hay progreso guardado
    checkAndPromptResume(item.url);

    // Iniciar temporizador de guardado de progreso cada 5s
    startProgressTimer();
  }

  function destroyPlayer() {
    if (state.activeSid) {
      try {
        fetch(`/api/proxy/abort?sid=${encodeURIComponent(state.activeSid)}`, { method: 'POST', keepalive: true }).catch(() => { });
      } catch (_) { }
      state.activeSid = null;
    }

    if (state.progressInterval) {
      clearInterval(state.progressInterval);
      state.progressInterval = null;
    }

    if (state.activeHls) {
      try {
        state.activeHls.destroy();
      } catch { }
      state.activeHls = null;
    }

    const video = els.vodVideo;
    if (video) {
      video.pause();
      video.removeAttribute('src');
      video.load();
      // Eliminar pistas de subtítulos dinámicas
      while (video.querySelector('track')) {
        video.querySelector('track').remove();
      }
    }

    hidePlayerStatus();
    els.subSelect.innerHTML = '<option value="none">Subtítulos: Desactivados</option>';
    els.resumeBanner.classList.add('hidden');
    els.btnPlayPause.textContent = '▶';
  }

  function closePlayer() {
    // Guardar progreso final antes de salir si aplica
    saveCurrentProgress();
    destroyPlayer();
    els.playerOverlay.classList.add('hidden');
    state.activeItem = null;
    renderResumeSection();
  }

  async function checkAndPromptResume(url) {
    try {
      const res = await apiJson(`/api/vod/progress?url=${encodeURIComponent(url)}`);
      const prog = res.progress;
      if (prog && prog.positionSec >= 30) {
        els.resumeBannerText.textContent = `Progreso guardado: ${formatTime(prog.positionSec)} / ${formatTime(prog.durationSec)}`;
        els.resumeBanner.classList.remove('hidden');

        els.btnResumeContinue.onclick = () => {
          els.vodVideo.currentTime = prog.positionSec;
          els.resumeBanner.classList.add('hidden');
        };

        els.btnResumeRestart.onclick = () => {
          els.vodVideo.currentTime = 0;
          els.resumeBanner.classList.add('hidden');
        };
      }
    } catch { }
  }

  function startProgressTimer() {
    if (state.progressInterval) clearInterval(state.progressInterval);
    state.progressInterval = setInterval(() => {
      saveCurrentProgress();
    }, 5000);
  }

  async function saveCurrentProgress() {
    const video = els.vodVideo;
    if (!video || !state.activeItem) return;

    const cur = video.currentTime;
    const dur = video.duration;

    if (isNaN(cur) || isNaN(dur) || dur <= 0) return;

    if (cur >= 30 && cur < dur * 0.95) {
      try {
        await apiJson('/api/vod/progress', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            url: state.activeItem.url,
            positionSec: cur,
            durationSec: dur
          })
        });
        state.progressMap.set(state.activeItem.url, {
          url: state.activeItem.url,
          positionSec: cur,
          durationSec: dur,
          lastWatchedUtc: new Date().toISOString()
        });
      } catch { }
    } else if (cur >= dur * 0.95) {
      // Completado: borrar de continuar viendo
      try {
        await apiJson('/api/vod/progress/clear', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ url: state.activeItem.url })
        });
        state.progressMap.delete(state.activeItem.url);
      } catch { }
    }
  }

  function populateSubtitlesFromHls(hls) {
    els.subSelect.innerHTML = '<option value="none">Subtítulos: Desactivados</option>';
    if (!hls.subtitleTracks) return;

    hls.subtitleTracks.forEach((track, idx) => {
      const opt = document.createElement('option');
      opt.value = String(idx);
      opt.textContent = track.name || track.lang || `Pista ${idx + 1}`;
      els.subSelect.appendChild(opt);
    });

    els.subSelect.onchange = () => {
      const val = els.subSelect.value;
      if (val === 'none') {
        hls.subtitleTrack = -1;
      } else {
        hls.subtitleTrack = parseInt(val, 10);
      }
    };
  }

  function handleLocalSubtitleFile(file) {
    if (!file) return;
    const reader = new FileReader();
    reader.onload = () => {
      let vttText = reader.result;
      if (file.name.toLowerCase().endsWith('.srt')) {
        vttText = convertSrtToVtt(vttText);
      }

      const blob = new Blob([vttText], { type: 'text/vtt' });
      const blobUrl = URL.createObjectURL(blob);

      const video = els.vodVideo;
      // Remover track local anterior
      const oldTrack = video.querySelector('track[data-local="true"]');
      if (oldTrack) oldTrack.remove();

      const track = document.createElement('track');
      track.kind = 'subtitles';
      track.label = `Local: ${file.name}`;
      track.srclang = 'es';
      track.src = blobUrl;
      track.setAttribute('data-local', 'true');
      track.default = true;

      video.appendChild(track);
      track.mode = 'showing';

      const opt = document.createElement('option');
      opt.value = 'local';
      opt.textContent = `★ Local: ${file.name}`;
      els.subSelect.appendChild(opt);
      els.subSelect.value = 'local';
    };
    reader.readAsText(file, 'utf-8');
  }

  async function handleVideoError() {
    const video = els.vodVideo;
    const err = video ? video.error : null;
    let title = 'Error de reproducción';
    let desc = 'No se pudo reproducir este video.';
    let icon = '⚠️';

    if (err) {
      if (err.code === 1) { // MEDIA_ERR_ABORTED
        title = 'Reproducción cancelada';
        desc = 'La carga del video fue cancelada.';
      } else if (err.code === 2) { // MEDIA_ERR_NETWORK
        title = 'Error de conexión';
        desc = 'Se interrumpió la conexión de red con el servidor de streaming.';
      } else if (err.code === 3) { // MEDIA_ERR_DECODE
        title = 'Formato o códec no compatible';
        desc = 'El navegador no puede decodificar este contenedor o pista de audio/video.';
      } else if (err.code === 4) { // MEDIA_ERR_SRC_NOT_SUPPORTED
        title = 'Fuente no disponible';
        desc = 'El servidor no pudo entregar el flujo o el formato no es compatible.';

        try {
          const checkUrl = video.src;
          if (checkUrl) {
            const resp = await fetch(checkUrl, { method: 'GET', headers: { 'Range': 'bytes=0-1' } });
            const errCls = resp.headers.get('X-Lex-Error-Class') || '';
            let customMsg = '';
            try {
              const errJson = await resp.json();
              if (errJson && (errJson.msg || errJson.error)) customMsg = errJson.msg || errJson.error;
            } catch (_) { }

            if (errCls === 'credentials') {
              title = 'Credenciales no disponibles';
              desc = customMsg || 'Credenciales no disponibles para esta conexión (sesión expirada o no guardadas).';
            } else if (errCls === 'blocked') {
              title = 'Flujo bloqueado por seguridad';
              desc = customMsg || 'La dirección solicitada no cumple con las políticas de seguridad de red de Lex TV.';
            } else if (errCls) {
              title = 'Error de reproducción';
              desc = customMsg || `Error (${errCls}).`;
            } else if (resp.status === 401 || resp.status === 403) {
              title = 'Acceso rechazado por el servidor';
              desc = `El proveedor rechazó la conexión (HTTP ${resp.status}). Revisa las credenciales o suscripción.`;
            } else if (resp.status === 404) {
              title = 'Fuente no disponible';
              desc = customMsg || 'El video o episodio no se encuentra en el servidor (HTTP 404).';
            } else if (resp.status === 504) {
              title = 'Tiempo de espera agotado';
              desc = 'El servidor de video tardó demasiado en responder (HTTP 504).';
            } else if (resp.status === 502) {
              title = 'Error de conexión';
              desc = customMsg || 'No se pudo establecer conexión con el servidor de video remoto.';
            }
          }
        } catch (_) { }
      }
    }
    showPlayerStatus(title, desc, icon, true);
  }

  // --- Keyboard Shortcuts & Scrubber ---
  function wirePlayerEvents() {
    const video = els.vodVideo;

    els.btnPlayPause.addEventListener('click', () => {
      if (video.paused) video.play();
      else video.pause();
    });

    video.addEventListener('play', () => {
      hidePlayerStatus();
      els.btnPlayPause.textContent = '⏸';
    });
    video.addEventListener('pause', () => { els.btnPlayPause.textContent = '▶'; });

    video.addEventListener('loadedmetadata', () => {
      hidePlayerStatus();
      const cur = video.currentTime || 0;
      const dur = video.duration || 0;
      els.timeDisplay.textContent = `${formatTime(cur)} / ${formatTime(dur)}`;
    });

    video.addEventListener('canplay', () => {
      hidePlayerStatus();
    });

    video.addEventListener('waiting', () => {
      showPlayerStatus('Almacenando búfer...', 'Cargando datos del video...', '⏳', false);
    });

    video.addEventListener('playing', () => {
      hidePlayerStatus();
      els.btnPlayPause.textContent = '⏸';
    });

    video.addEventListener('stalled', () => {
      if (!video.paused && video.readyState < 3) {
        showPlayerStatus('Conexión lenta', 'Esperando datos del servidor de streaming...', '⏳', false);
      }
    });

    video.addEventListener('error', async () => {
      await handleVideoError();
    });

    if (els.btnRetryVod) {
      els.btnRetryVod.addEventListener('click', () => {
        hidePlayerStatus();
        if (state.activeItem) openPlayer(state.activeItem);
      });
    }

    if (els.btnCloseVodStatus) {
      els.btnCloseVodStatus.addEventListener('click', () => {
        closePlayer();
      });
    }

    video.addEventListener('timeupdate', () => {
      const cur = video.currentTime || 0;
      const dur = video.duration || 0;
      els.timeDisplay.textContent = `${formatTime(cur)} / ${formatTime(dur)}`;
      if (dur > 0) {
        const pct = (cur / dur) * 100;
        els.scrubberFill.style.width = `${pct}%`;
      }
    });

    video.addEventListener('ended', () => {
      saveCurrentProgress();
      destroyPlayer();
    });

    els.scrubberContainer.addEventListener('click', (e) => {
      const rect = els.scrubberContainer.getBoundingClientRect();
      const clickX = e.clientX - rect.left;
      const pct = Math.max(0, Math.min(1, clickX / rect.width));
      if (video.duration) {
        video.currentTime = pct * video.duration;
      }
    });

    els.btnStepBack.addEventListener('click', () => {
      video.currentTime = Math.max(0, video.currentTime - 10);
    });

    els.btnStepForward.addEventListener('click', () => {
      video.currentTime = Math.min(video.duration || 0, video.currentTime + 10);
    });

    els.volumeSlider.addEventListener('input', () => {
      video.volume = parseFloat(els.volumeSlider.value);
      video.muted = false;
      els.btnMute.textContent = video.volume === 0 ? '🔇' : '🔊';
    });

    els.btnMute.addEventListener('click', () => {
      video.muted = !video.muted;
      els.btnMute.textContent = video.muted ? '🔇' : '🔊';
      if (!video.muted && video.volume === 0) {
        video.volume = 0.5;
        els.volumeSlider.value = '0.5';
      }
    });

    els.btnFullscreen.addEventListener('click', () => {
      if (document.fullscreenElement) {
        document.exitFullscreen().catch(() => { });
      } else {
        els.playerOverlay.requestFullscreen().catch(() => { });
      }
    });

    els.btnLoadLocalSub.addEventListener('click', () => {
      els.subFileInput.click();
    });

    els.subFileInput.addEventListener('change', () => {
      const file = els.subFileInput.files?.[0];
      if (file) handleLocalSubtitleFile(file);
    });

    els.btnClosePlayer.addEventListener('click', closePlayer);

    // Global keyboard handling
    window.addEventListener('keydown', (e) => {
      if (els.playerOverlay.classList.contains('hidden')) {
        if (e.key === 'Escape' && !els.seriesModal.classList.contains('hidden')) {
          closeSeriesModal();
        }
        return;
      }

      switch (e.key) {
        case ' ':
          e.preventDefault();
          if (video.paused) video.play();
          else video.pause();
          break;
        case 'ArrowLeft':
          e.preventDefault();
          video.currentTime = Math.max(0, video.currentTime - 5);
          break;
        case 'ArrowRight':
          e.preventDefault();
          video.currentTime = Math.min(video.duration || 0, video.currentTime + 5);
          break;
        case 'j':
        case 'J':
          e.preventDefault();
          video.currentTime = Math.max(0, video.currentTime - 60);
          break;
        case 'l':
        case 'L':
          e.preventDefault();
          video.currentTime = Math.min(video.duration || 0, video.currentTime + 60);
          break;
        case 'ArrowUp':
          e.preventDefault();
          video.volume = Math.min(1, video.volume + 0.1);
          els.volumeSlider.value = String(video.volume);
          break;
        case 'ArrowDown':
          e.preventDefault();
          video.volume = Math.max(0, video.volume - 0.1);
          els.volumeSlider.value = String(video.volume);
          break;
        case 'm':
        case 'M':
          video.muted = !video.muted;
          els.btnMute.textContent = video.muted ? '🔇' : '🔊';
          break;
        case 'f':
        case 'F':
          if (document.fullscreenElement) document.exitFullscreen().catch(() => { });
          else els.playerOverlay.requestFullscreen().catch(() => { });
          break;
        case 'Escape':
          closePlayer();
          break;
      }
    });
  }

  // --- Import Modal Wiring ---
  function openImportModal() {
    els.importError.classList.add('hidden');
    els.importInfo.classList.add('hidden');
    if (els.chkVodLegalAuth) els.chkVodLegalAuth.checked = false;
    els.btnSubmitImport.disabled = true;
    els.importModal.classList.remove('hidden');
  }

  function closeImportModal() {
    els.importModal.classList.add('hidden');
  }

  async function submitImport() {
    els.importError.classList.add('hidden');
    els.importInfo.classList.add('hidden');

    if (els.chkVodLegalAuth && !els.chkVodLegalAuth.checked) {
      els.importError.textContent = 'Debes confirmar que cuentas con autorización para utilizar las fuentes.';
      els.importError.classList.remove('hidden');
      return;
    }

    const group = (els.importGroupInput.value || '').trim() || 'Películas';
    const url = (els.importUrlInput.value || '').trim();
    const text = (els.importTextInput.value || '').trim();

    if (!url && !text) {
      els.importError.textContent = 'Ingresa una URL o pega/selecciona un archivo M3U.';
      els.importError.classList.remove('hidden');
      return;
    }

    els.btnSubmitImport.disabled = true;
    try {
      const res = url
        ? await apiJson('/api/vod/import-url', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ url, group })
        })
        : await apiJson('/api/vod/import-text', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ content: text, group })
        });

      els.importInfo.textContent = `¡Importación completada! Se agregaron ${res.result?.addedCount || 0} títulos.`;
      els.importInfo.classList.remove('hidden');

      setTimeout(() => {
        closeImportModal();
        loadLibrary();
      }, 1000);
    } catch (err) {
      els.importError.textContent = `Error: ${err.message}`;
      els.importError.classList.remove('hidden');
    } finally {
      els.btnSubmitImport.disabled = false;
    }
  }

  // --- Jellyfin / Emby Modal Wiring ---
  function openJfModal() {
    els.jfStatus.classList.add('hidden');
    els.jellyfinModal.classList.remove('hidden');
  }

  function closeJfModal() {
    els.jellyfinModal.classList.add('hidden');
  }

  async function testJfConnection() {
    const serverUrl = (els.jfServerUrl.value || '').trim();
    const apiKey = (els.jfApiKey.value || '').trim();
    if (!serverUrl) {
      els.jfStatus.textContent = 'Ingresa la URL de tu servidor Jellyfin/Emby.';
      els.jfStatus.className = 'text-bad';
      els.jfStatus.classList.remove('hidden');
      return;
    }

    els.btnJfTest.disabled = true;
    try {
      const res = await apiJson('/api/jellyfin/test', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ serverUrl, apiKey })
      });
      if (res.ok) {
        els.jfStatus.textContent = '✓ Conexión exitosa con el servidor.';
        els.jfStatus.className = 'text-ok';
      } else {
        els.jfStatus.textContent = '✗ No se pudo conectar al servidor. Verifica la URL y clave.';
        els.jfStatus.className = 'text-bad';
      }
      els.jfStatus.classList.remove('hidden');
    } catch (err) {
      els.jfStatus.textContent = `Error: ${err.message}`;
      els.jfStatus.className = 'text-bad';
      els.jfStatus.classList.remove('hidden');
    } finally {
      els.btnJfTest.disabled = false;
    }
  }

  async function importJfLibrary() {
    const serverUrl = (els.jfServerUrl.value || '').trim();
    const apiKey = (els.jfApiKey.value || '').trim();
    const userId = (els.jfUserId.value || '').trim();
    if (!serverUrl) {
      els.jfStatus.textContent = 'Ingresa la URL del servidor.';
      els.jfStatus.className = 'text-bad';
      els.jfStatus.classList.remove('hidden');
      return;
    }

    els.btnJfImport.disabled = true;
    try {
      const res = await apiJson('/api/jellyfin/import', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ serverUrl, apiKey, userId })
      });
      els.jfStatus.textContent = `✓ Importados ${res.addedCount || 0} títulos de ${res.totalFound || 0} encontrados.`;
      els.jfStatus.className = 'text-ok';
      els.jfStatus.classList.remove('hidden');
      setTimeout(() => {
        closeJfModal();
        loadLibrary();
      }, 1000);
    } catch (err) {
      els.jfStatus.textContent = `Error: ${err.message}`;
      els.jfStatus.style.color = 'var(--bad)';
      els.jfStatus.classList.remove('hidden');
    } finally {
      els.btnJfImport.disabled = false;
    }
  }

  function initApp() {
    [
      'vodSearch', 'vodTypeSelect', 'vodGroupSelect', 'btnOpenImport', 'btnOpenJellyfin', 'btnClearLibrary',
      'resumeSection', 'resumeContainer', 'librarySection', 'libraryCountLabel', 'vodGrid',
      'emptyNotice', 'btnEmptyImport',
      'seriesModal', 'seriesModalTitle', 'seasonTabs', 'episodeList', 'btnCloseSeriesModal', 'btnCloseSeriesFoot',
      'playerOverlay', 'playerTitle', 'btnClosePlayer', 'resumeBanner', 'resumeBannerText',
      'btnResumeContinue', 'btnResumeRestart', 'videoContainer', 'vodVideo', 'playerBar',
      'scrubberContainer', 'scrubberFill', 'btnPlayPause', 'btnStepBack', 'btnStepForward',
      'timeDisplay', 'btnMute', 'volumeSlider', 'subSelect', 'btnLoadLocalSub', 'subFileInput', 'btnFullscreen',
      'importModal', 'importTitle', 'btnCloseImport', 'importGroupInput', 'importUrlInput',
      'importFileInput', 'importTextInput', 'importError', 'importInfo', 'btnCancelImport', 'btnSubmitImport', 'chkVodLegalAuth',
      'jellyfinModal', 'btnCloseJfModal', 'jfServerUrl', 'jfApiKey', 'jfUserId', 'jfStatus', 'btnJfTest', 'btnJfImport',
      'vodPlayerStatus', 'vodStatusIcon', 'vodStatusTitle', 'vodStatusDesc', 'btnRetryVod', 'btnCloseVodStatus'
    ].forEach((id) => { els[id] = $(id); });

    wirePlayerEvents();

    els.vodSearch.addEventListener('input', applyFilters);
    els.vodTypeSelect.addEventListener('change', applyFilters);
    els.vodGroupSelect.addEventListener('change', applyFilters);

    els.btnOpenImport.addEventListener('click', openImportModal);
    els.btnEmptyImport.addEventListener('click', openImportModal);
    els.btnCloseImport.addEventListener('click', closeImportModal);
    els.btnCancelImport.addEventListener('click', closeImportModal);
    els.btnSubmitImport.addEventListener('click', submitImport);

    if (els.chkVodLegalAuth) {
      els.chkVodLegalAuth.addEventListener('change', () => {
        els.btnSubmitImport.disabled = !els.chkVodLegalAuth.checked;
      });
    }

    els.btnOpenJellyfin.addEventListener('click', openJfModal);
    els.btnCloseJfModal.addEventListener('click', closeJfModal);
    els.btnJfTest.addEventListener('click', testJfConnection);
    els.btnJfImport.addEventListener('click', importJfLibrary);

    els.btnCloseSeriesModal.addEventListener('click', closeSeriesModal);
    els.btnCloseSeriesFoot.addEventListener('click', closeSeriesModal);

    els.btnClearLibrary.addEventListener('click', async () => {
      if (confirm('¿Seguro que deseas vaciar tu biblioteca de películas y series?')) {
        await apiJson('/api/vod/clear', { method: 'POST' });
        loadLibrary();
      }
    });

    els.importFileInput.addEventListener('change', () => {
      const file = els.importFileInput.files?.[0];
      if (!file) return;
      const reader = new FileReader();
      reader.onload = () => {
        els.importTextInput.value = reader.result;
      };
      reader.readAsText(file, 'utf-8');
    });

    checkTermsAccepted();
    loadLibrary();
  }

  window.addEventListener('DOMContentLoaded', initApp);
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = { convertSrtToVtt, formatTime };
}

