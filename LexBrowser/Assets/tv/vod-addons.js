'use strict';

(function () {
  const TYPE_LABELS = {
    movie: '🎬 Películas',
    series: '📺 Series',
    anime: '🎌 Anime',
    tv: '📡 TV',
    channel: '📡 Canales'
  };

  const state = {
    mode: 'library',
    catalogs: [],
    types: [],
    activeType: 'all',
    selectedCatalogKey: '',
    nextSkip: 0,
    hasMore: false,
    loadingCatalog: false,
    items: [],
    metaCache: new Map(),
    activeMeta: null,
    activeSeason: null,
    addonSubtitles: []
  };

  function $(id) {
    return document.getElementById(id);
  }

  function safeImageProxyUrl(rawUrl) {
    if (!rawUrl || typeof rawUrl !== 'string') return null;
    let trimmed = rawUrl.trim();
    if (!trimmed) return null;
    if (trimmed.startsWith('/api/image')) return trimmed;
    if (trimmed.startsWith('//')) trimmed = 'https:' + trimmed;
    if (!trimmed.startsWith('http://') && !trimmed.startsWith('https://')) return null;
    return `/api/image?u=${encodeURIComponent(trimmed)}`;
  }

  function typeIcon(type) {
    switch ((type || '').toLowerCase()) {
      case 'series': return '📺';
      case 'anime': return '🎌';
      case 'tv':
      case 'channel': return '📡';
      default: return '🎬';
    }
  }

  function formatTypeLabel(type) {
    const key = (type || '').toLowerCase();
    return TYPE_LABELS[key] || (key ? key.toUpperCase() : 'Otro');
  }

  function catalogKey(c) {
    return `${c.addonId}::${c.type}::${c.catalogId}`;
  }

  async function apiJson(url, opts = {}) {
    const res = await fetch(url, opts);
    let data = {};
    try { data = await res.json(); } catch { }
    if (!res.ok) {
      throw new Error((data && data.error) ? data.error : `HTTP ${res.status}`);
    }
    return data;
  }

  function setMode(mode) {
    state.mode = mode;
    const isAddons = mode === 'addons';

    const btnLib = $('btnModeLibrary');
    const btnAdd = $('btnModeAddons');
    const resumeSec = $('resumeSection');
    const libSec = $('librarySection');
    const addonsSec = $('addonsCatalogSection');

    if (btnLib) {
      btnLib.classList.toggle('active', !isAddons);
      btnLib.setAttribute('aria-selected', String(!isAddons));
    }
    if (btnAdd) {
      btnAdd.classList.toggle('active', isAddons);
      btnAdd.setAttribute('aria-selected', String(isAddons));
    }

    if (libSec) libSec.classList.toggle('hidden', isAddons);
    if (addonsSec) addonsSec.classList.toggle('hidden', !isAddons);
    if (resumeSec && isAddons) {
      resumeSec.classList.add('hidden');
    } else if (resumeSec && !isAddons) {
      // Permitir que el modo Biblioteca restaure Continuar viendo si tenía tarjetas
      if (resumeSec.querySelector('.resume-card')) {
        resumeSec.classList.remove('hidden');
      }
    }

    if (isAddons) {
      loadCatalogDescriptors();
    }
  }

  function showBanner(msg, isError) {
    const box = $('addonCatalogBanner');
    if (!box) return;
    if (!msg) {
      box.classList.add('hidden');
      box.textContent = '';
      return;
    }
    box.textContent = msg;
    box.classList.remove('hidden', 'is-error', 'is-info');
    box.classList.add(isError ? 'is-error' : 'is-info');
  }

  function getFilteredCatalogs() {
    if (state.activeType === 'all') return state.catalogs;
    return state.catalogs.filter((c) => (c.type || '').toLowerCase() === state.activeType);
  }

  function getSelectedCatalog() {
    const filtered = getFilteredCatalogs();
    return filtered.find((c) => catalogKey(c) === state.selectedCatalogKey) || filtered[0] || null;
  }

  function renderTypePills() {
    const wrap = $('addonTypePills');
    if (!wrap) return;
    wrap.textContent = '';

    const counts = new Map();
    state.catalogs.forEach((c) => {
      const t = (c.type || '').toLowerCase();
      counts.set(t, (counts.get(t) || 0) + 1);
    });

    const addPill = (typeVal, label) => {
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'addon-pill' + (state.activeType === typeVal ? ' active' : '');
      btn.textContent = label;
      btn.addEventListener('click', () => {
        state.activeType = typeVal;
        renderTypePills();
        renderCatalogSelector();
        fetchCatalogPage(false);
      });
      wrap.appendChild(btn);
    };

    addPill('all', `Todos (${state.catalogs.length})`);
    Array.from(counts.keys()).forEach((t) => {
      addPill(t, `${formatTypeLabel(t)} (${counts.get(t)})`);
    });
  }

  function renderCatalogSelector() {
    const sel = $('addonCatalogSelect');
    const genreSel = $('addonGenreSelect');
    const searchInput = $('addonCatalogSearch');
    if (!sel) return;

    const filtered = getFilteredCatalogs();
    sel.textContent = '';

    if (filtered.length === 0) {
      const opt = document.createElement('option');
      opt.value = '';
      opt.textContent = 'Sin catálogos disponibles';
      sel.appendChild(opt);
      state.selectedCatalogKey = '';
      if (genreSel) genreSel.classList.add('hidden');
      return;
    }

    filtered.forEach((c) => {
      const opt = document.createElement('option');
      opt.value = catalogKey(c);
      opt.textContent = `${c.addonName} · ${c.name} (${c.type.toUpperCase()})`;
      sel.appendChild(opt);
    });

    if (!filtered.some((c) => catalogKey(c) === state.selectedCatalogKey)) {
      state.selectedCatalogKey = catalogKey(filtered[0]);
    }
    sel.value = state.selectedCatalogKey;

    const active = getSelectedCatalog();
    const btnSearch = $('btnAddonSearch');
    const btnSearchAll = $('btnAddonSearchAll');
    const anyHasSearch = state.catalogs.some((c) => c.hasSearch);
    if (genreSel) {
      genreSel.textContent = '';
      const genres = (active && Array.isArray(active.genres)) ? active.genres : [];
      if (genres.length > 0) {
        const allOpt = document.createElement('option');
        allOpt.value = 'all';
        allOpt.textContent = 'Género: Todos';
        genreSel.appendChild(allOpt);
        genres.forEach((g) => {
          const gOpt = document.createElement('option');
          gOpt.value = g;
          gOpt.textContent = g;
          genreSel.appendChild(gOpt);
        });
        genreSel.classList.remove('hidden');
      } else {
        genreSel.classList.add('hidden');
      }
    }

    const activeHasSearch = Boolean(active && active.hasSearch);
    if (btnSearch) {
      btnSearch.disabled = !activeHasSearch;
      btnSearch.classList.toggle('hidden', !activeHasSearch && anyHasSearch);
    }
    if (btnSearchAll) {
      btnSearchAll.classList.toggle('hidden', !anyHasSearch);
    }
    if (searchInput) {
      searchInput.disabled = !activeHasSearch && !anyHasSearch;
      searchInput.placeholder = activeHasSearch
        ? (active.searchRequired ? 'Búsqueda obligatoria en este catálogo…' : 'Buscar en este catálogo…')
        : (anyHasSearch ? 'Buscar en todos los catálogos compatibles…' : 'Ningún catálogo soporta búsqueda por texto');
      if (searchInput.disabled) searchInput.value = '';
    }
  }

  async function loadCatalogDescriptors() {
    try {
      const data = await apiJson('/api/addons/catalogs');
      state.catalogs = Array.isArray(data.catalogs) ? data.catalogs : [];
      state.types = Array.isArray(data.types) ? data.types : [];

      const badge = $('addonCatalogCountBadge');
      if (badge) {
        badge.textContent = String(state.catalogs.length);
        badge.classList.toggle('hidden', state.catalogs.length === 0);
      }

      const empty = $('addonEmptyNotice');
      const toolbar = $('addonCatalogToolbar');
      const hasAny = state.catalogs.length > 0;
      if (empty) empty.classList.toggle('hidden', hasAny);
      if (toolbar) toolbar.classList.toggle('hidden', !hasAny);

      if (!hasAny) {
        const grid = $('addonCatalogGrid');
        if (grid) grid.textContent = '';
        const moreWrap = $('addonLoadMoreWrap');
        if (moreWrap) moreWrap.classList.add('hidden');
        showBanner('', false);
        return;
      }

      renderTypePills();
      renderCatalogSelector();

      if (state.items.length === 0) {
        await fetchCatalogPage(false);
      }
    } catch (err) {
      showBanner(err.message || 'No se pudieron listar los catálogos de addons.', true);
    }
  }

  async function searchAllCatalogs() {
    if (state.loadingCatalog) return;
    const searchEl = $('addonCatalogSearch');
    const query = searchEl ? searchEl.value.trim() : '';
    if (!query) {
      showBanner('Escribe un término para buscar en todos los catálogos compatibles.', true);
      if (searchEl) searchEl.focus();
      return;
    }

    state.loadingCatalog = true;
    showBanner(`Buscando "${query}" en todos los addons compatibles…`, false);

    try {
      const data = await apiJson('/api/addons/search', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          search: query,
          type: state.activeType || 'all'
        })
      });

      state.items = Array.isArray(data.items) ? data.items : [];
      state.hasMore = false;
      state.nextSkip = 0;

      const searchedCount = Number.isFinite(data.searchedCatalogs) ? data.searchedCatalogs : 0;
      showBanner(
        state.items.length === 0
          ? `Sin resultados para "${query}" en ${searchedCount} catálogo(s).`
          : `Se encontraron ${state.items.length} resultado(s) buscando "${query}" en ${searchedCount} catálogo(s).`,
        false
      );
      renderCatalogGrid();
    } catch (err) {
      state.items = [];
      state.hasMore = false;
      renderCatalogGrid();
      showBanner(err.message || 'No se pudo completar la búsqueda global.', true);
    } finally {
      state.loadingCatalog = false;
    }
  }

  async function fetchCatalogPage(append) {
    if (state.loadingCatalog) return;
    const cat = getSelectedCatalog();
    if (!cat) return;

    const searchEl = $('addonCatalogSearch');
    const genreEl = $('addonGenreSelect');
    const search = searchEl && !searchEl.disabled ? searchEl.value.trim() : '';
    const genre = genreEl && !genreEl.classList.contains('hidden') ? genreEl.value : 'all';

    if (cat.searchRequired && !search) {
      state.items = [];
      state.hasMore = false;
      renderCatalogGrid();
      showBanner(`El catálogo "${cat.name}" requiere escribir un término de búsqueda para mostrar resultados.`, false);
      return;
    }

    state.loadingCatalog = true;
    const skip = append ? state.nextSkip : 0;
    if (!append) {
      showBanner(`Cargando "${cat.name}" (${cat.addonName})…`, false);
    }

    try {
      const data = await apiJson('/api/addons/catalog', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          addonId: cat.addonId,
          type: cat.type,
          catalogId: cat.catalogId,
          search,
          genre,
          skip
        })
      });

      const incoming = Array.isArray(data.items) ? data.items : [];
      state.items = append ? state.items.concat(incoming) : incoming;
      state.hasMore = Boolean(data.hasMore);
      state.nextSkip = Number.isFinite(data.nextSkip) ? data.nextSkip : state.items.length;

      showBanner(
        state.items.length === 0 ? 'Este catálogo no devolvió resultados con los filtros actuales.' : '',
        false
      );
      renderCatalogGrid();
    } catch (err) {
      if (!append) {
        state.items = [];
        state.hasMore = false;
        renderCatalogGrid();
      }
      showBanner(err.message || 'Error al cargar el catálogo del addon.', true);
    } finally {
      state.loadingCatalog = false;
    }
  }

  function createCatalogCard(item) {
    const card = document.createElement('div');
    card.className = 'vod-card';

    const poster = document.createElement('div');
    poster.className = 'vod-card-poster';

    const safePoster = safeImageProxyUrl(item.poster);
    if (safePoster) {
      const img = document.createElement('img');
      img.loading = 'lazy';
      img.decoding = 'async';
      img.src = safePoster;
      img.alt = item.name;
      img.addEventListener('error', () => {
        const fb = document.createElement('span');
        fb.className = 'vod-poster-fallback';
        fb.textContent = typeIcon(item.type);
        img.replaceWith(fb);
      });
      poster.appendChild(img);
    } else {
      const fb = document.createElement('span');
      fb.className = 'vod-poster-fallback';
      fb.textContent = typeIcon(item.type);
      poster.appendChild(fb);
    }

    const isSeriesLike = (item.type || '').toLowerCase() === 'series' || (item.type || '').toLowerCase() === 'anime';
    const badge = document.createElement('div');
    badge.className = `vod-card-badge ${isSeriesLike ? 'badge-series' : 'badge-movie'}`;
    badge.textContent = (item.type || 'vod').toUpperCase();
    poster.appendChild(badge);

    if (item.imdbRating) {
      const ratingBadge = document.createElement('div');
      ratingBadge.className = 'addon-rating-badge';
      ratingBadge.textContent = `★ ${item.imdbRating}`;
      poster.appendChild(ratingBadge);
    }

    const info = document.createElement('div');
    info.className = 'vod-card-info';

    const titleEl = document.createElement('div');
    titleEl.className = 'vod-card-title';
    titleEl.title = item.name;
    titleEl.textContent = item.name;

    const groupEl = document.createElement('div');
    groupEl.className = 'vod-card-sub';
    const metaParts = [];
    if (item.releaseInfo) metaParts.push(item.releaseInfo);
    if (item.genres && item.genres.length > 0) metaParts.push(item.genres.slice(0, 2).join(', '));
    if (metaParts.length === 0) metaParts.push(item.addonName || 'Addon');
    groupEl.textContent = metaParts.join(' · ');

    info.appendChild(titleEl);
    info.appendChild(groupEl);
    card.appendChild(poster);
    card.appendChild(info);

    card.addEventListener('click', () => openAddonMetaModal(item));
    return card;
  }

  function renderCatalogGrid() {
    const grid = $('addonCatalogGrid');
    const countLabel = $('addonCatalogCountLabel');
    const moreWrap = $('addonLoadMoreWrap');
    if (!grid) return;

    grid.textContent = '';
    const frag = document.createDocumentFragment();
    state.items.forEach((it) => frag.appendChild(createCatalogCard(it)));
    grid.appendChild(frag);

    if (countLabel) {
      countLabel.textContent = `(${state.items.length.toLocaleString('es')} títulos)`;
    }
    if (moreWrap) {
      moreWrap.classList.toggle('hidden', !state.hasMore);
    }
  }

  function closeAddonMetaModal() {
    const modal = $('addonMetaModal');
    if (modal) modal.classList.add('hidden');
    state.activeMeta = null;
  }

  function renderMetaDetail(meta, noteMessage) {
    const titleEl = $('addonMetaTitle');
    const posterWrap = $('addonMetaPoster');
    const subtitleEl = $('addonMetaSubtitle');
    const genresWrap = $('addonMetaGenres');
    const descEl = $('addonMetaDesc');
    const creditsEl = $('addonMetaCredits');
    const episodesBlock = $('addonMetaEpisodesBlock');
    const statusEl = $('addonMetaStatus');

    if (titleEl) titleEl.textContent = meta.name || 'Detalles';

    if (posterWrap) {
      posterWrap.textContent = '';
      const safePoster = safeImageProxyUrl(meta.poster);
      if (safePoster) {
        const img = document.createElement('img');
        img.src = safePoster;
        img.alt = meta.name || '';
        img.addEventListener('error', () => {
          const fb = document.createElement('span');
          fb.className = 'vod-poster-fallback';
          fb.textContent = typeIcon(meta.type);
          img.replaceWith(fb);
        });
        posterWrap.appendChild(img);
      } else {
        const fb = document.createElement('span');
        fb.className = 'vod-poster-fallback';
        fb.textContent = typeIcon(meta.type);
        posterWrap.appendChild(fb);
      }
    }

    if (subtitleEl) {
      const parts = [formatTypeLabel(meta.type)];
      if (meta.releaseInfo) parts.push(meta.releaseInfo);
      if (meta.runtime) parts.push(meta.runtime);
      if (meta.imdbRating) parts.push(`★ ${meta.imdbRating}`);
      if (meta.addonName) parts.push(`Addon: ${meta.addonName}`);
      subtitleEl.textContent = parts.join(' · ');
    }

    if (genresWrap) {
      genresWrap.textContent = '';
      (meta.genres || []).slice(0, 8).forEach((g) => {
        const chip = document.createElement('span');
        chip.className = 'addon-chip';
        chip.textContent = g;
        genresWrap.appendChild(chip);
      });
    }

    if (descEl) {
      descEl.textContent = meta.description || 'Sin sinopsis disponible para este título.';
    }

    if (creditsEl) {
      const creditLines = [];
      if (meta.director && meta.director.length > 0) creditLines.push(`Dirección: ${meta.director.slice(0, 4).join(', ')}`);
      if (meta.cast && meta.cast.length > 0) creditLines.push(`Reparto: ${meta.cast.slice(0, 6).join(', ')}`);
      creditsEl.textContent = creditLines.join(' · ');
      creditsEl.classList.toggle('hidden', creditLines.length === 0);
    }

    if (statusEl) {
      if (noteMessage) {
        statusEl.textContent = noteMessage;
        statusEl.classList.remove('hidden');
      } else {
        statusEl.textContent = '';
        statusEl.classList.add('hidden');
      }
    }

    const videos = Array.isArray(meta.videos) ? meta.videos : [];
    if (episodesBlock) {
      episodesBlock.classList.toggle('hidden', videos.length === 0);
      if (videos.length > 0) {
        renderMetaEpisodes(videos);
      }
    }
  }

  function renderMetaEpisodes(videos) {
    const seasonTabs = $('addonSeasonTabs');
    const epList = $('addonEpisodeList');
    if (!seasonTabs || !epList) return;

    const seasons = Array.from(new Set(videos.map((v) => v.season))).sort((a, b) => {
      if (a === 0) return 1;
      if (b === 0) return -1;
      return a - b;
    });

    if (!seasons.includes(state.activeSeason)) {
      state.activeSeason = seasons[0];
    }

    seasonTabs.textContent = '';
    seasons.forEach((s) => {
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'season-tab' + (s === state.activeSeason ? ' active' : '');
      btn.textContent = s === 0 ? 'Especiales' : `Temporada ${s}`;
      btn.addEventListener('click', () => {
        state.activeSeason = s;
        renderMetaEpisodes(videos);
      });
      seasonTabs.appendChild(btn);
    });

    epList.textContent = '';
    const seasonVideos = videos.filter((v) => v.season === state.activeSeason);
    seasonVideos.forEach((v) => {
      const row = document.createElement('div');
      row.className = 'episode-item';

      const left = document.createElement('div');
      left.className = 'episode-meta';

      const num = document.createElement('span');
      num.className = 'episode-num';
      const sNum = String(v.season || 1).padStart(2, '0');
      const eNum = String(v.episode || 0).padStart(2, '0');
      num.textContent = v.episode > 0 ? `S${sNum}E${eNum}` : 'EP';

      const title = document.createElement('span');
      title.className = 'episode-title';
      title.textContent = v.title || 'Episodio';

      left.appendChild(num);
      left.appendChild(title);

      const right = document.createElement('span');
      right.className = 'muted text-xs';
      right.textContent = v.released || '🔎 Ver fuentes';

      row.appendChild(left);
      row.appendChild(right);
      row.addEventListener('click', () => {
        epList.querySelectorAll('.episode-item.is-selected').forEach((el) => el.classList.remove('is-selected'));
        row.classList.add('is-selected');
        const epTag = v.episode > 0 ? `S${sNum}E${eNum} ` : '';
        const meta = state.activeMeta;
        if (meta) {
          loadStreamsForTarget(meta.addonId, meta.type, v.id, `${meta.name} · ${epTag}${v.title || ''}`.trim());
        }
      });

      epList.appendChild(row);
    });
  }

  function clearStreamsPanel() {
    const wrap = $('addonStreamsBlock');
    const list = $('addonStreamsList');
    if (list) list.textContent = '';
    if (wrap) wrap.classList.add('hidden');
  }

  function setMetaStatus(msg) {
    const statusEl = $('addonMetaStatus');
    if (!statusEl) return;
    if (msg) {
      statusEl.textContent = msg;
      statusEl.classList.remove('hidden');
    } else {
      statusEl.textContent = '';
      statusEl.classList.add('hidden');
    }
  }

  function formatFileSize(bytes) {
    if (!Number.isFinite(bytes) || bytes <= 0) return '';
    if (bytes >= 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024 * 1024)).toFixed(2)} GB`;
    if (bytes >= 1024 * 1024) return `${Math.round(bytes / (1024 * 1024))} MB`;
    return `${Math.round(bytes / 1024)} KB`;
  }

  function mergeSubtitles(inlineSubs, externalSubs) {
    const out = [];
    const seen = new Set();
    const add = (list) => {
      if (!Array.isArray(list)) return;
      for (const item of list) {
        if (!item || !item.proxyUrl) continue;
        if (seen.has(item.proxyUrl)) continue;
        seen.add(item.proxyUrl);
        out.push(item);
      }
    };
    add(inlineSubs);
    add(externalSubs);
    return out;
  }

  function renderStreamsList(streams, displayTitle, errors, addonSubtitles) {
    const wrap = $('addonStreamsBlock');
    const titleEl = $('addonStreamsTitle');
    const listEl = $('addonStreamsList');
    if (!wrap || !listEl) return;

    listEl.textContent = '';
    const items = Array.isArray(streams) ? streams : [];
    const extSubs = Array.isArray(addonSubtitles) ? addonSubtitles : [];
    if (titleEl) {
      const subSuffix = extSubs.length > 0 ? ` · ${extSubs.length} subt. externos` : '';
      titleEl.textContent = `Fuentes para "${displayTitle}" (${items.length})${subSuffix}`;
    }

    if (items.length === 0) {
      wrap.classList.add('hidden');
      const errDetail = (Array.isArray(errors) && errors.length > 0) ? ` (${errors[0]})` : '';
      setMetaStatus(`Ningún addon devolvió streams para este título${errDetail}.`);
      return;
    }

    setMetaStatus('');
    items.forEach((s) => {
      const isExternal = s.streamKind === 'external' && /^https?:\/\//i.test(s.externalUrl || '');
      const row = document.createElement('div');
      row.className = 'addon-stream-row' + ((s.playable || isExternal) ? '' : ' is-disabled');

      const meta = document.createElement('div');
      meta.className = 'addon-stream-meta';

      const mainTitle = document.createElement('div');
      mainTitle.className = 'addon-stream-title';
      mainTitle.textContent = s.title || s.name || s.addonName || 'Fuente';

      const badges = document.createElement('div');
      badges.className = 'addon-stream-badges';

      const bAddon = document.createElement('span');
      bAddon.className = 'addon-chip';
      bAddon.textContent = s.addonName || s.addonId || 'Addon';
      badges.appendChild(bAddon);

      if (s.name && s.name !== s.addonName) {
        const bName = document.createElement('span');
        bName.className = 'addon-chip';
        bName.textContent = s.name;
        badges.appendChild(bName);
      }

      const bKind = document.createElement('span');
      bKind.className = 'addon-kind-badge';
      bKind.textContent = isExternal ? 'WEB' : (s.streamKind || 'http').toUpperCase();
      badges.appendChild(bKind);

      const sizeText = formatFileSize(s.videoSize);
      if (sizeText) {
        const bSize = document.createElement('span');
        bSize.className = 'addon-size-badge';
        bSize.textContent = sizeText;
        badges.appendChild(bSize);
      }

      const mergedSubs = mergeSubtitles(s.subtitles || [], extSubs);
      if (mergedSubs.length > 0) {
        const bSub = document.createElement('span');
        bSub.className = 'addon-sub-badge';
        bSub.textContent = `${mergedSubs.length} subt.`;
        badges.appendChild(bSub);
      }

      meta.appendChild(mainTitle);
      meta.appendChild(badges);

      if (s.filename) {
        const fname = document.createElement('div');
        fname.className = 'addon-stream-filename mono';
        fname.textContent = s.filename;
        meta.appendChild(fname);
      }

      if (!s.playable && s.disabledReason) {
        const reason = document.createElement('div');
        reason.className = 'muted text-xs';
        reason.textContent = s.disabledReason;
        meta.appendChild(reason);
      }

      row.appendChild(meta);

      if (s.playable) {
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'btn btn-primary';
        btn.textContent = '▶ Reproducir';
        btn.addEventListener('click', () => {
          closeAddonMetaModal();
          if (window.LexVodPlayer && typeof window.LexVodPlayer.openPlayer === 'function') {
            window.LexVodPlayer.openPlayer({
              title: displayTitle,
              url: s.streamUrl,
              probeUrl: s.streamUrl,
              playbackUrl: s.proxyUrl,
              subtitles: mergedSubs
            });
          }
        });
        row.appendChild(btn);
      } else if (isExternal) {
        const link = document.createElement('a');
        link.className = 'btn btn-secondary';
        link.href = s.externalUrl;
        link.target = '_blank';
        link.rel = 'noopener noreferrer';
        link.textContent = '↗ Abrir en pestaña';
        row.appendChild(link);
      } else {
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'btn';
        btn.disabled = true;
        btn.textContent = 'No compatible';
        row.appendChild(btn);
      }

      listEl.appendChild(row);
    });

    wrap.classList.remove('hidden');
  }

  async function loadStreamsForTarget(addonId, type, targetId, displayTitle) {
    clearStreamsPanel();
    state.addonSubtitles = [];
    setMetaStatus(`Buscando streams y subtítulos disponibles para "${displayTitle}"…`);

    const bodyJson = JSON.stringify({
      addonId,
      type,
      id: targetId
    });

    const [streamsSettled, subsSettled] = await Promise.allSettled([
      apiJson('/api/addons/streams', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: bodyJson
      }),
      apiJson('/api/addons/subtitles', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: bodyJson
      })
    ]);

    if (subsSettled.status === 'fulfilled' && subsSettled.value && Array.isArray(subsSettled.value.subtitles)) {
      state.addonSubtitles = subsSettled.value.subtitles;
    }

    if (streamsSettled.status === 'fulfilled') {
      const data = streamsSettled.value || {};
      renderStreamsList(data.streams || [], displayTitle, data.errors || [], state.addonSubtitles);
    } else {
      const errMsg = streamsSettled.reason && streamsSettled.reason.message
        ? streamsSettled.reason.message
        : 'No se pudieron consultar streams desde los addons.';
      setMetaStatus(errMsg);
    }
  }

  function resolveMetaStreamTarget(meta) {
    if (!meta) return '';
    return meta.defaultVideoId || meta.id;
  }

  async function openAddonMetaModal(previewItem) {
    const modal = $('addonMetaModal');
    if (!modal) return;

    state.activeMeta = previewItem;
    state.activeSeason = null;
    clearStreamsPanel();
    renderMetaDetail(previewItem, 'Cargando ficha detallada y episodios desde el addon…');
    modal.classList.remove('hidden');

    const cacheKey = `${previewItem.addonId}::${previewItem.type}::${previewItem.id}`;
    if (state.metaCache.has(cacheKey)) {
      const cached = state.metaCache.get(cacheKey);
      state.activeMeta = cached;
      renderMetaDetail(cached, '');
      if (!cached.videos || cached.videos.length === 0) {
        loadStreamsForTarget(cached.addonId, cached.type, resolveMetaStreamTarget(cached), cached.name);
      }
      return;
    }

    try {
      const data = await apiJson('/api/addons/meta', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          addonId: previewItem.addonId,
          type: previewItem.type,
          id: previewItem.id
        })
      });

      if (data && data.meta) {
        state.metaCache.set(cacheKey, data.meta);
        if (state.activeMeta && state.activeMeta.id === previewItem.id) {
          state.activeMeta = data.meta;
          renderMetaDetail(data.meta, '');
          if (!data.meta.videos || data.meta.videos.length === 0) {
            loadStreamsForTarget(data.meta.addonId, data.meta.type, resolveMetaStreamTarget(data.meta), data.meta.name);
          }
        }
      }
    } catch (err) {
      if (state.activeMeta && state.activeMeta.id === previewItem.id) {
        renderMetaDetail(previewItem, err.message || 'Este título usa solo la vista previa del catálogo (el addon no devolvió ficha completa).');
        if ((previewItem.type || '').toLowerCase() === 'movie') {
          loadStreamsForTarget(previewItem.addonId, previewItem.type, previewItem.id, previewItem.name);
        }
      }
    }
  }

  function init() {
    const btnLib = $('btnModeLibrary');
    const btnAdd = $('btnModeAddons');
    if (btnLib) btnLib.addEventListener('click', () => setMode('library'));
    if (btnAdd) btnAdd.addEventListener('click', () => setMode('addons'));

    const selCat = $('addonCatalogSelect');
    if (selCat) {
      selCat.addEventListener('change', () => {
        state.selectedCatalogKey = selCat.value;
        renderCatalogSelector();
        fetchCatalogPage(false);
      });
    }

    const selGenre = $('addonGenreSelect');
    if (selGenre) {
      selGenre.addEventListener('change', () => fetchCatalogPage(false));
    }

    const btnSearch = $('btnAddonSearch');
    const btnSearchAll = $('btnAddonSearchAll');
    const searchInput = $('addonCatalogSearch');
    if (btnSearch) btnSearch.addEventListener('click', () => fetchCatalogPage(false));
    if (btnSearchAll) btnSearchAll.addEventListener('click', () => searchAllCatalogs());
    if (searchInput) {
      searchInput.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') {
          e.preventDefault();
          const active = getSelectedCatalog();
          if (active && active.hasSearch) {
            fetchCatalogPage(false);
          } else {
            searchAllCatalogs();
          }
        }
      });
    }

    const btnReload = $('btnAddonReload');
    if (btnReload) btnReload.addEventListener('click', () => fetchCatalogPage(false));

    const btnMore = $('btnAddonLoadMore');
    if (btnMore) btnMore.addEventListener('click', () => fetchCatalogPage(true));

    const btnCloseMeta = $('btnCloseAddonMeta');
    const btnCloseMetaFoot = $('btnCloseAddonMetaFoot');
    const btnLoadStreams = $('btnLoadAddonStreams');
    const metaModal = $('addonMetaModal');
    if (btnCloseMeta) btnCloseMeta.addEventListener('click', closeAddonMetaModal);
    if (btnCloseMetaFoot) btnCloseMetaFoot.addEventListener('click', closeAddonMetaModal);
    if (btnLoadStreams) {
      btnLoadStreams.addEventListener('click', () => {
        const meta = state.activeMeta;
        if (!meta) return;
        loadStreamsForTarget(meta.addonId, meta.type, resolveMetaStreamTarget(meta), meta.name || meta.id);
      });
    }
    if (metaModal) {
      metaModal.addEventListener('click', (e) => {
        if (e.target === metaModal) closeAddonMetaModal();
      });
    }

    // Actualizar insignia de catálogos en segundo plano
    apiJson('/api/addons/catalogs')
      .then((data) => {
        const cats = Array.isArray(data.catalogs) ? data.catalogs : [];
        const badge = $('addonCatalogCountBadge');
        if (badge) {
          badge.textContent = String(cats.length);
          badge.classList.toggle('hidden', cats.length === 0);
        }
      })
      .catch(() => { });

    if (window.location.hash === '#addons') {
      setMode('addons');
    }
  }

  document.addEventListener('DOMContentLoaded', init);
})();

