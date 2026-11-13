/**
 * Lex TV Cine — Películas y Series VOD bajo demanda.
 * Reproducción directa con soporte de búsqueda (HTTP 206 Range),
 * filtrado instantáneo, historial de progreso y favoritos.
 */
(() => {
  "use strict";

  // Constantes y límites
  const PAGE_SIZE = 60;
  const PROGRESS_AUTO_SAVE_INTERVAL_MS = 10000;
  const WATCHED_THRESHOLD_PERCENT = 92.0;

  // Estado en memoria de la UI
  const state = {
    catalog: [],           // Índice compacto del catálogo
    disneyItems: [],       // Títulos de Disney/Marvel/Pixar desde TMDb
    animeItems: [],        // Títulos de Anime desde AniList
    addonManifests: [],    // Manifiestos de add-ons modulares
    tmdbConfigured: false, // Indica si el usuario ya ingresó clave de TMDb
    stats: null,           // Estadísticas de descarte
    userState: {           // Preferencias e historial del usuario
      favorites: new Set(),
      watchlist: new Set(),
      watched: new Set(),
      progress: {},
      lastEpisodes: {},
      unavailable: {}
    },
    activeView: "catalog", // "catalog" | "disney" | "anime" | "addons" | "continue" | "watchlist" | "favorites" | "watched"
    filter: {
      query: "",
      kind: "all",
      genre: "all",
      decade: "all",
      lang: "all",
      wikimediaOnly: false,
      hideUnavailable: true
    },
    filteredItems: [],
    currentPage: 1,
    currentDetail: null,   // TvTitle completo cargado bajo demanda
    playback: null,        // Sesión activa de reproducción
    saveProgressTimer: null,
    nextCountdownTimer: null,
    nextCountdownSec: 5
  };

  // Elementos DOM
  const dom = {};

  function cacheDom() {
    dom.catalogStatsPill = document.getElementById("catalogStatsPill");
    dom.searchInput = document.getElementById("searchInput");
    dom.kindSelect = document.getElementById("kindSelect");
    dom.genreSelect = document.getElementById("genreSelect");
    dom.decadeSelect = document.getElementById("decadeSelect");
    dom.langSelect = document.getElementById("langSelect");
    dom.wikimediaOnlyChk = document.getElementById("wikimediaOnlyChk");
    dom.hideUnavailableChk = document.getElementById("hideUnavailableChk");
    dom.btnRefresh = document.getElementById("btnRefresh");
    dom.btnCredits = document.getElementById("btnCredits");

    dom.tabCatalog = document.getElementById("tabCatalog");
    dom.tabDisney = document.getElementById("tabDisney");
    dom.tabAnime = document.getElementById("tabAnime");
    dom.tabAddons = document.getElementById("tabAddons");
    dom.tabContinue = document.getElementById("tabContinue");
    dom.tabWatchlist = document.getElementById("tabWatchlist");
    dom.tabFavorites = document.getElementById("tabFavorites");
    dom.tabWatched = document.getElementById("tabWatched");

    dom.disneyStudioSelect = document.getElementById("disneyStudioSelect");
    dom.btnTmdbKey = document.getElementById("btnTmdbKey");
    dom.btnAddAddon = document.getElementById("btnAddAddon");

    dom.btnOfficialWatch = document.getElementById("btnOfficialWatch");
    dom.watchProvidersBox = document.getElementById("watchProvidersBox");
    dom.watchProvidersList = document.getElementById("watchProvidersList");
    dom.tmdbAttributionText = document.getElementById("tmdbAttributionText");

    dom.tmdbKeyModal = document.getElementById("tmdbKeyModal");
    dom.tmdbKeyInput = document.getElementById("tmdbKeyInput");
    dom.tmdbKeyStatus = document.getElementById("tmdbKeyStatus");
    dom.btnSaveTmdbKey = document.getElementById("btnSaveTmdbKey");
    dom.btnRemoveTmdbKey = document.getElementById("btnRemoveTmdbKey");
    dom.btnCancelTmdbKey = document.getElementById("btnCancelTmdbKey");

    dom.addonModal = document.getElementById("addonModal");
    dom.addonUrlInput = document.getElementById("addonUrlInput");
    dom.addonModalError = document.getElementById("addonModalError");
    dom.btnSubmitAddon = document.getElementById("btnSubmitAddon");
    dom.btnCancelAddon = document.getElementById("btnCancelAddon");

    dom.continueSection = document.getElementById("continueSection");
    dom.continueGrid = document.getElementById("continueGrid");
    dom.resultsCount = document.getElementById("resultsCount");
    dom.activeFilterBadge = document.getElementById("activeFilterBadge");

    dom.btnPrevPage = document.getElementById("btnPrevPage");
    dom.btnNextPage = document.getElementById("btnNextPage");
    dom.pageIndicator = document.getElementById("pageIndicator");
    dom.btnPrevPageBottom = document.getElementById("btnPrevPageBottom");
    dom.btnNextPageBottom = document.getElementById("btnNextPageBottom");
    dom.pageIndicatorBottom = document.getElementById("pageIndicatorBottom");

    dom.cardsGrid = document.getElementById("cardsGrid");
    dom.emptyMessage = document.getElementById("emptyMessage");
    dom.emptyTitle = document.getElementById("emptyTitle");
    dom.emptySub = document.getElementById("emptySub");
    dom.btnResetFilters = document.getElementById("btnResetFilters");

    // Modal Detalle
    dom.detailModal = document.getElementById("detailModal");
    dom.btnCloseDetail = document.getElementById("btnCloseDetail");
    dom.detailPoster = document.getElementById("detailPoster");
    dom.detailBadge = document.getElementById("detailBadge");
    dom.detailTitle = document.getElementById("detailTitle");
    dom.detailYear = document.getElementById("detailYear");
    dom.detailRuntime = document.getElementById("detailRuntime");
    dom.detailLanguages = document.getElementById("detailLanguages");
    dom.detailLicense = document.getElementById("detailLicense");
    dom.detailGenres = document.getElementById("detailGenres");
    dom.detailSynopsis = document.getElementById("detailSynopsis");
    dom.btnPlayTitle = document.getElementById("btnPlayTitle");
    dom.btnWatchlistToggle = document.getElementById("btnWatchlistToggle");
    dom.btnFavoriteToggle = document.getElementById("btnFavoriteToggle");
    dom.btnWatchedToggle = document.getElementById("btnWatchedToggle");
    dom.btnOpenExternal = document.getElementById("btnOpenExternal");
    dom.btnReportTitle = document.getElementById("btnReportTitle");

    dom.seriesSection = document.getElementById("seriesSection");
    dom.seasonTabs = document.getElementById("seasonTabs");
    dom.episodeList = document.getElementById("episodeList");

    dom.detailSourceName = document.getElementById("detailSourceName");
    dom.detailSourceLink = document.getElementById("detailSourceLink");
    dom.detailTmdbLink = document.getElementById("detailTmdbLink");
    dom.detailImdbLink = document.getElementById("detailImdbLink");

    // Player Overlay
    dom.playerOverlay = document.getElementById("playerOverlay");
    dom.btnClosePlayer = document.getElementById("btnClosePlayer");
    dom.playerTitle = document.getElementById("playerTitle");
    dom.playerSub = document.getElementById("playerSub");
    dom.playerHostPill = document.getElementById("playerHostPill");
    dom.btnPlayerVlc = document.getElementById("btnPlayerVlc");
    dom.videoPlayer = document.getElementById("videoPlayer");
    dom.playerErrorOverlay = document.getElementById("playerErrorOverlay");
    dom.playerErrorTitle = document.getElementById("playerErrorTitle");
    dom.playerErrorMsg = document.getElementById("playerErrorMsg");
    dom.btnRetryPlay = document.getElementById("btnRetryPlay");
    dom.btnErrorVlc = document.getElementById("btnErrorVlc");
    dom.btnMarkUnavailable = document.getElementById("btnMarkUnavailable");

    dom.nextEpisodePrompt = document.getElementById("nextEpisodePrompt");
    dom.nextEpisodeTitle = document.getElementById("nextEpisodeTitle");
    dom.nextCountdownSec = document.getElementById("nextCountdownSec");
    dom.btnPlayNextNow = document.getElementById("btnPlayNextNow");
    dom.btnCancelNext = document.getElementById("btnCancelNext");

    // Créditos
    dom.creditsModal = document.getElementById("creditsModal");
    dom.btnCloseCredits = document.getElementById("btnCloseCredits");
    dom.discardStatsDetails = document.getElementById("discardStatsDetails");
  }

  // --- Normalización para búsqueda sin acentos ---
  function normalizeText(text) {
    if (!text) return "";
    return text.toString()
      .normalize("NFD")
      .replace(/[\u0300-\u036f]/g, "")
      .toLowerCase()
      .trim();
  }

  function formatTime(seconds) {
    if (!seconds || isNaN(seconds) || seconds < 0) return "0:00";
    const total = Math.floor(seconds);
    const m = Math.floor(total / 60);
    const s = total % 60;
    if (m >= 60) {
      const h = Math.floor(m / 60);
      const remM = m % 60;
      return `${h}:${remM.toString().padStart(2, "0")}:${s.toString().padStart(2, "0")}`;
    }
    return `${m}:${s.toString().padStart(2, "0")}`;
  }

  function formatDurationMinutes(minutes) {
    if (!minutes || minutes <= 0) return "";
    if (minutes < 60) return `${minutes} min`;
    const h = Math.floor(minutes / 60);
    const m = minutes % 60;
    return m > 0 ? `${h}h ${m}m` : `${h}h`;
  }

  function extractHost(url) {
    try {
      return new URL(url).hostname;
    } catch {
      return "";
    }
  }

  // --- Inicialización y Carga de API ---
  async function init() {
    cacheDom();
    bindEvents();
    await loadCatalog(false);
  }

  async function loadCatalog(forceRefresh) {
    dom.catalogStatsPill.textContent = "Cargando catálogo…";
    try {
      const url = forceRefresh ? "/api/cine/catalog?refresh=1" : "/api/cine/catalog";
      const resp = await fetch(url, { headers: { "Accept": "application/json" } });
      if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
      const data = await resp.json();

      state.catalog = data.items || [];
      state.stats = data.stats || null;

      // Unificar estado de usuario
      if (data.state) {
        state.userState.favorites = new Set(data.state.favorites || []);
        state.userState.watchlist = new Set(data.state.watchlist || []);
        state.userState.watched = new Set(data.state.watched || []);
        state.userState.progress = data.state.progress || {};
        state.userState.lastEpisodes = data.state.lastEpisodes || {};
        state.userState.unavailable = data.state.unavailable || {};
      }

      dom.catalogStatsPill.textContent = `${state.catalog.length} títulos disponibles`;
      populateFilters();
      applyFilters();
      renderContinueWatching();
      renderDiscardStats();
    } catch (err) {
      console.error("[Cine] Error al cargar catálogo:", err);
      dom.catalogStatsPill.textContent = "Error al cargar catálogo";
    }
  }

  function populateFilters() {
    const genresSet = new Set();
    const langSet = new Set();

    for (const item of state.catalog) {
      if (Array.isArray(item.genres)) {
        for (const g of item.genres) if (g && g.trim()) genresSet.add(g.trim());
      }
      if (Array.isArray(item.languages)) {
        for (const l of item.languages) if (l && l.trim()) langSet.add(l.trim());
      }
    }

    // Llenar géneros
    const sortedGenres = Array.from(genresSet).sort((a, b) => a.localeCompare(b, "es"));
    dom.genreSelect.innerHTML = `<option value="all">Género: Todos</option>`;
    for (const g of sortedGenres) {
      const opt = document.createElement("option");
      opt.value = g;
      opt.textContent = g;
      dom.genreSelect.appendChild(opt);
    }

    // Llenar idiomas
    const sortedLangs = Array.from(langSet).sort((a, b) => a.localeCompare(b, "es"));
    dom.langSelect.innerHTML = `<option value="all">Idioma: Todos</option>`;
    for (const l of sortedLangs) {
      const opt = document.createElement("option");
      opt.value = l;
      opt.textContent = l;
      dom.langSelect.appendChild(opt);
    }
  }

  // --- Filtrado y Búsqueda ---
  function applyFilters() {
    let list = state.catalog;

    // Filtrar según la pestaña activa
    if (state.activeView === "disney") {
      list = state.disneyItems || [];
    } else if (state.activeView === "anime") {
      list = state.animeItems || [];
    } else if (state.activeView === "continue") {
      list = list.filter(item => {
        const prog = state.userState.progress[item.id];
        return prog && prog.positionSec > 10 && (prog.percent || 0) < WATCHED_THRESHOLD_PERCENT;
      });
    } else if (state.activeView === "watchlist") {
      list = list.filter(item => state.userState.watchlist.has(item.id));
    } else if (state.activeView === "favorites") {
      list = list.filter(item => state.userState.favorites.has(item.id));
    } else if (state.activeView === "watched") {
      list = list.filter(item => state.userState.watched.has(item.id));
    }

    const q = normalizeText(state.filter.query);
    const kind = state.filter.kind;
    const genre = state.filter.genre;
    const decade = state.filter.decade;
    const lang = state.filter.lang;
    const wikiOnly = state.filter.wikimediaOnly;
    const hideUnav = state.filter.hideUnavailable;

    state.filteredItems = list.filter(item => {
      // Ocultar no disponibles
      if (hideUnav && state.userState.unavailable[item.id]) {
        return false;
      }

      // Solo Wikimedia
      if (wikiOnly && (!item.mediaUrl || !item.mediaUrl.includes("upload.wikimedia.org"))) {
        return false;
      }

      // Tipo (movie / series)
      if (kind !== "all" && item.kind !== kind) {
        return false;
      }

      // Género
      if (genre !== "all") {
        if (!item.genres || !item.genres.some(g => g.toLowerCase() === genre.toLowerCase())) {
          return false;
        }
      }

      // Idioma
      if (lang !== "all") {
        if (!item.languages || !item.languages.some(l => l.toLowerCase() === lang.toLowerCase())) {
          return false;
        }
      }

      // Década
      if (decade !== "all" && item.year > 0) {
        const y = item.year;
        if (decade === "1900s" && (y < 1900 || y > 1909)) return false;
        if (decade === "1910s" && (y < 1910 || y > 1919)) return false;
        if (decade === "1920s" && (y < 1920 || y > 1929)) return false;
        if (decade === "1930s" && (y < 1930 || y > 1939)) return false;
        if (decade === "1940s" && (y < 1940 || y > 1949)) return false;
        if (decade === "1950s" && (y < 1950 || y > 1959)) return false;
        if (decade === "1960s" && (y < 1960 || y > 1969)) return false;
        if (decade === "1970s" && y < 1970) return false;
      }

      // Búsqueda por texto (título, géneros)
      if (q.length > 0) {
        const normTitle = normalizeText(item.title);
        if (normTitle.includes(q)) return true;
        if (item.genres && item.genres.some(g => normalizeText(g).includes(q))) return true;
        return false;
      }

      return true;
    });

    state.currentPage = 1;
    renderGrid();
    renderPagination();
  }

  // --- Renderizado de Grid y Paginación ---
  function renderGrid() {
    dom.cardsGrid.innerHTML = "";
    const total = state.filteredItems.length;
    dom.resultsCount.textContent = `${total} título${total === 1 ? "" : "s"} encontrado${total === 1 ? "" : "s"}`;

    if (total === 0) {
      dom.emptyMessage.classList.remove("hidden");
      if (state.activeView === "continue") {
        dom.emptyTitle.textContent = "No tienes reproducciones pendientes";
        dom.emptySub.textContent = "Los títulos que empieces a ver aparecerán aquí para que puedas retomarlos fácilmente.";
      } else if (state.activeView === "watchlist") {
        dom.emptyTitle.textContent = "Tu lista está vacía";
        dom.emptySub.textContent = "Haz clic en '+ Mi lista' en la ficha de cualquier película o serie para guardarla.";
      } else if (state.activeView === "favorites") {
        dom.emptyTitle.textContent = "No tienes favoritos";
        dom.emptySub.textContent = "Marca títulos con la estrella para encontrarlos rápidamente.";
      } else if (state.activeView === "watched") {
        dom.emptyTitle.textContent = "Aún no has marcado títulos como vistos";
        dom.emptySub.textContent = "Al terminar un video se marcará automáticamente, o puedes marcarlo a mano.";
      } else {
        dom.emptyTitle.textContent = "No se encontraron títulos";
        dom.emptySub.textContent = "Prueba con otros términos de búsqueda o restablece los filtros.";
      }
      return;
    }

    dom.emptyMessage.classList.add("hidden");

    const startIdx = (state.currentPage - 1) * PAGE_SIZE;
    const endIdx = Math.min(startIdx + PAGE_SIZE, total);
    const pageItems = state.filteredItems.slice(startIdx, endIdx);

    const frag = document.createDocumentFragment();
    for (const item of pageItems) {
      const card = createCardElement(item);
      frag.appendChild(card);
    }
    dom.cardsGrid.appendChild(frag);
  }

  function createCardElement(item) {
    const card = document.createElement("div");
    card.className = "cine-card";
    card.tabIndex = 0;
    card.setAttribute("role", "button");
    card.setAttribute("aria-label", `${item.title} (${item.year || "Año desconocido"})`);

    const posterWrap = document.createElement("div");
    posterWrap.className = "card-poster-wrap";

    const posterImg = document.createElement("img");
    posterImg.className = "card-poster";
    posterImg.loading = "lazy";
    posterImg.alt = item.title;
    posterImg.src = item.posterUrl || "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='200' height='300' viewBox='0 0 200 300'%3E%3Crect width='200' height='300' fill='%23182132'/%3E%3Ctext x='50%25' y='50%25' dominant-baseline='middle' text-anchor='middle' fill='%238ea0bf' font-family='sans-serif' font-size='14'%3ESin carátula%3C/text%3E%3C/svg%3E";

    // Si la imagen falla, muestra carátula genérica
    posterImg.onerror = () => {
      posterImg.onerror = null;
      posterImg.src = "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='200' height='300' viewBox='0 0 200 300'%3E%3Crect width='200' height='300' fill='%23182132'/%3E%3Ctext x='50%25' y='50%25' dominant-baseline='middle' text-anchor='middle' fill='%238ea0bf' font-family='sans-serif' font-size='14'%3ECarátula no disponible%3C/text%3E%3C/svg%3E";
    };

    const kindBadge = document.createElement("span");
    kindBadge.className = "card-badge";
    kindBadge.textContent = item.kind === "series" ? "Serie" : "Película";

    // Botón rápido de lista / favorito
    const bookmarkBtn = document.createElement("button");
    bookmarkBtn.type = "button";
    bookmarkBtn.className = "card-bookmark-btn";
    const isWatch = state.userState.watchlist.has(item.id);
    if (isWatch) bookmarkBtn.classList.add("active");
    bookmarkBtn.title = isWatch ? "Quitar de Mi lista" : "Guardar en Mi lista";
    bookmarkBtn.textContent = isWatch ? "★" : "☆";
    bookmarkBtn.onclick = (e) => {
      e.stopPropagation();
      toggleUserAction("watchlist", item.id);
    };

    posterWrap.appendChild(posterImg);
    posterWrap.appendChild(kindBadge);
    posterWrap.appendChild(bookmarkBtn);

    const info = document.createElement("div");
    info.className = "card-info";

    const title = document.createElement("h3");
    title.className = "card-title";
    title.textContent = item.title;

    const metaRow = document.createElement("div");
    metaRow.className = "card-meta-row";

    const yearSpan = document.createElement("span");
    yearSpan.textContent = item.year > 0 ? item.year.toString() : "";

    const licenseSpan = document.createElement("span");
    licenseSpan.className = "card-license";
    licenseSpan.textContent = item.license || "Libre";

    metaRow.appendChild(yearSpan);
    metaRow.appendChild(licenseSpan);

    info.appendChild(title);
    info.appendChild(metaRow);

    // Barra de progreso si está empezada
    const prog = state.userState.progress[item.id];
    if (prog && prog.positionSec > 0 && prog.durationSec > 0) {
      const pWrap = document.createElement("div");
      pWrap.className = "progress-bar-wrap";
      const pFill = document.createElement("div");
      pFill.className = "progress-bar-fill";
      pFill.style.width = `${Math.min(100, prog.percent || 0)}%`;
      pWrap.appendChild(pFill);
      info.appendChild(pWrap);
    }

    card.appendChild(posterWrap);
    card.appendChild(info);

    card.onclick = () => openDetailModal(item.id);
    card.onkeydown = (e) => {
      if (e.key === "Enter" || e.key === " ") {
        e.preventDefault();
        openDetailModal(item.id);
      }
    };

    return card;
  }

  function renderPagination() {
    const total = state.filteredItems.length;
    const maxPage = Math.max(1, Math.ceil(total / PAGE_SIZE));
    const isFirst = state.currentPage <= 1;
    const isLast = state.currentPage >= maxPage;

    const text = `Página ${state.currentPage} de ${maxPage}`;
    dom.pageIndicator.textContent = text;
    dom.pageIndicatorBottom.textContent = text;

    dom.btnPrevPage.disabled = isFirst;
    dom.btnPrevPageBottom.disabled = isFirst;
    dom.btnNextPage.disabled = isLast;
    dom.btnNextPageBottom.disabled = isLast;
  }

  // --- Continuar viendo (Sección Banner) ---
  function renderContinueWatching() {
    if (state.activeView !== "catalog") {
      dom.continueSection.classList.add("hidden");
      return;
    }

    const inProgress = [];
    for (const [id, prog] of Object.entries(state.userState.progress)) {
      if (prog.positionSec > 10 && (prog.percent || 0) < WATCHED_THRESHOLD_PERCENT) {
        const item = state.catalog.find(i => i.id === id);
        if (item) {
          inProgress.push({ item, prog });
        }
      }
    }

    if (inProgress.length === 0) {
      dom.continueSection.classList.add("hidden");
      return;
    }

    // Ordenar más recientes primero
    inProgress.sort((a, b) => new Date(b.prog.lastWatchedUtc) - new Date(a.prog.lastWatchedUtc));
    dom.continueGrid.innerHTML = "";

    const slice = inProgress.slice(0, 6);
    for (const entry of slice) {
      const cCard = document.createElement("div");
      cCard.className = "continue-card";
      cCard.tabIndex = 0;
      cCard.setAttribute("role", "button");

      const img = document.createElement("img");
      img.className = "continue-poster-thumb";
      img.loading = "lazy";
      img.alt = entry.item.title;
      img.src = entry.item.posterUrl || "";

      const info = document.createElement("div");
      info.className = "continue-info";

      const title = document.createElement("div");
      title.className = "continue-title";
      title.textContent = entry.item.title;

      const sub = document.createElement("div");
      sub.className = "continue-sub";
      sub.textContent = `En ${formatTime(entry.prog.positionSec)} (${entry.prog.percent}%)`;

      const pWrap = document.createElement("div");
      pWrap.className = "progress-bar-wrap";
      const pFill = document.createElement("div");
      pFill.className = "progress-bar-fill";
      pFill.style.width = `${Math.min(100, entry.prog.percent || 0)}%`;
      pWrap.appendChild(pFill);

      info.appendChild(title);
      info.appendChild(sub);
      info.appendChild(pWrap);

      cCard.appendChild(img);
      cCard.appendChild(info);

      cCard.onclick = () => openDetailModal(entry.item.id);
      dom.continueGrid.appendChild(cCard);
    }

    dom.continueSection.classList.remove("hidden");
  }

  // --- Modal Ficha de Detalle ---
  async function openDetailModal(id) {
    dom.detailModal.classList.remove("hidden");
    dom.detailTitle.textContent = "Cargando detalles…";
    dom.detailSynopsis.textContent = "";
    dom.detailGenres.innerHTML = "";
    dom.seriesSection.classList.add("hidden");
    dom.watchProvidersBox.classList.add("hidden");
    dom.btnOfficialWatch.classList.add("hidden");
    dom.btnPlayTitle.classList.remove("hidden");
    dom.btnOpenExternal.classList.remove("hidden");

    if (id.startsWith("tmdb:")) {
      try {
        const resp = await fetch(`/api/vod/tmdb/movie?id=${encodeURIComponent(id)}`, { headers: { "Accept": "application/json" } });
        if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
        const data = await resp.json();
        renderDetailTmdb(data.item);
      } catch (err) {
        dom.detailTitle.textContent = "Error al cargar película de TMDb";
        dom.detailSynopsis.textContent = err.message;
      }
      return;
    }

    if (id.startsWith("anilist:")) {
      try {
        const resp = await fetch(`/api/vod/anilist/detail?id=${encodeURIComponent(id)}`, { headers: { "Accept": "application/json" } });
        if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
        const data = await resp.json();
        renderDetailAniList(data.item);
      } catch (err) {
        dom.detailTitle.textContent = "Error al cargar anime de AniList";
        dom.detailSynopsis.textContent = err.message;
      }
      return;
    }

    try {
      const resp = await fetch(`/api/cine/title?id=${encodeURIComponent(id)}`, { headers: { "Accept": "application/json" } });
      if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
      const data = await resp.json();
      state.currentDetail = data.title;
      renderDetail(data.title);
    } catch (err) {
      console.error("[Cine] Error al obtener título:", err);
      dom.detailTitle.textContent = "Error al cargar la ficha del título";
    }
  }

  function renderDetailTmdb(item) {
    dom.detailTitle.textContent = item.title;
    dom.detailPoster.src = item.posterUrl || "";
    dom.detailBadge.textContent = "TMDb / Oficial";

    dom.detailYear.textContent = item.year ? item.year.toString() : "";
    dom.detailRuntime.textContent = formatDurationMinutes(item.runtimeMin);
    dom.detailLanguages.textContent = "Español / Original";
    dom.detailLicense.textContent = "Copyright Oficial (Disney / Marvel / Pixar)";

    dom.detailGenres.innerHTML = "";
    if (item.genres && item.genres.length) {
      for (const g of item.genres) {
        const tag = document.createElement("span");
        tag.className = "genre-tag";
        tag.textContent = g;
        dom.detailGenres.appendChild(tag);
      }
    }

    dom.detailSynopsis.textContent = item.synopsis || "Sin sinopsis disponible.";

    // PROHIBIDO reproducir contenido de Disney dentro de la app:
    dom.btnPlayTitle.classList.add("hidden");
    dom.btnOpenExternal.classList.add("hidden");
    dom.btnOfficialWatch.classList.remove("hidden");
    dom.btnOfficialWatch.textContent = "🌐 Ver en servicio oficial";
    dom.btnOfficialWatch.onclick = () => {
      const targetUrl = item.officialWatchUrl || (item.watchProviders && item.watchProviders[0] && item.watchProviders[0].officialUrl) || "https://www.disneyplus.com/";
      fetch("/api/vod/open-official", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ url: targetUrl })
      }).catch(() => {});
    };

    // Dónde verla en Perú (Watch Providers)
    dom.watchProvidersBox.classList.remove("hidden");
    dom.watchProvidersList.innerHTML = "";
    if (item.watchProviders && item.watchProviders.length > 0) {
      for (const wp of item.watchProviders) {
        const pItem = document.createElement("div");
        pItem.className = "watch-provider-item";

        if (wp.logoUrl) {
          const logo = document.createElement("img");
          logo.className = "watch-provider-logo";
          logo.src = wp.logoUrl;
          logo.alt = wp.name;
          pItem.appendChild(logo);
        }

        const nameSpan = document.createElement("span");
        const typeLabel = wp.type === "flatrate" ? "Suscripción" : (wp.type === "rent" ? "Alquiler" : "Compra");
        nameSpan.textContent = `${wp.name} (${typeLabel})`;
        pItem.appendChild(nameSpan);

        if (wp.officialUrl) {
          pItem.style.cursor = "pointer";
          pItem.title = `Abrir en ${wp.name}`;
          pItem.onclick = () => {
            fetch("/api/vod/open-official", {
              method: "POST",
              headers: { "Content-Type": "application/json" },
              body: JSON.stringify({ url: wp.officialUrl })
            }).catch(() => {});
          };
        }

        dom.watchProvidersList.appendChild(pItem);
      }
    } else {
      dom.watchProvidersList.innerHTML = `<span class="muted" style="font-size:12px;">No se encontraron plataformas de suscripción directa para Perú en este momento. Consulta en Disney+.</span>`;
    }

    dom.tmdbAttributionText.textContent = item.attribution || "This product uses the TMDB API but is not endorsed or certified by TMDB. Streaming provider data powered by JustWatch.";
    dom.detailSourceName.textContent = "The Movie Database (TMDB)";
    dom.detailSourceLink.classList.add("hidden");
    dom.detailTmdbLink.classList.remove("hidden");
    dom.detailTmdbLink.href = `https://www.themoviedb.org/movie/${item.id.replace("tmdb:", "")}`;
    dom.detailImdbLink.classList.add("hidden");
    dom.seriesSection.classList.add("hidden");
  }

  async function renderDetailAniList(item) {
    dom.detailTitle.textContent = item.title;
    dom.detailPoster.src = item.posterUrl || "";
    dom.detailBadge.textContent = "AniList / Anime";

    dom.detailYear.textContent = item.year ? item.year.toString() : "";
    dom.detailRuntime.textContent = formatDurationMinutes(item.runtimeMin);
    dom.detailLanguages.textContent = "Japonés (Audio) / Subtítulos";
    dom.detailLicense.textContent = "AniList Public Metadata";

    dom.detailGenres.innerHTML = "";
    if (item.genres && item.genres.length) {
      for (const g of item.genres) {
        const tag = document.createElement("span");
        tag.className = "genre-tag";
        tag.textContent = g;
        dom.detailGenres.appendChild(tag);
      }
    }

    let extraText = "";
    if (item.animeMeta) {
      const parts = [];
      if (item.animeMeta.romajiTitle) parts.push(`Romaji: ${item.animeMeta.romajiTitle}`);
      if (item.animeMeta.japaneseTitle) parts.push(`Japonés: ${item.animeMeta.japaneseTitle}`);
      if (item.animeMeta.totalEpisodes) parts.push(`Episodios: ${item.animeMeta.totalEpisodes}`);
      if (item.animeMeta.studio) parts.push(`Estudio: ${item.animeMeta.studio}`);
      if (item.animeMeta.format) parts.push(`Formato: ${item.animeMeta.format}`);
      if (parts.length) extraText = parts.join(" · ") + "\n\n";
    }

    dom.detailSynopsis.textContent = extraText + (item.synopsis ? item.synopsis.replace(/<[^>]+>/g, "") : "Sin sinopsis disponible.");

    dom.detailSourceName.textContent = "AniList GraphQL";
    dom.detailSourceLink.classList.remove("hidden");
    dom.detailSourceLink.href = `https://anilist.co/anime/${item.id.replace("anilist:", "")}`;
    dom.detailTmdbLink.classList.add("hidden");
    dom.detailImdbLink.classList.add("hidden");
    dom.seriesSection.classList.add("hidden");
    dom.watchProvidersBox.classList.add("hidden");

    // Consultar si algún add-on provee streams para este anime
    try {
      const sResp = await fetch(`/api/vod/streams?type=series&id=${encodeURIComponent(item.id)}`, { headers: { "Accept": "application/json" } });
      const sData = await sResp.json();
      if (sData.streams && sData.streams.length > 0) {
        const stream = sData.streams[0];
        dom.btnPlayTitle.classList.remove("hidden");
        dom.btnPlayTitle.textContent = `▶ Reproducir (${stream.name || "Add-on"})`;
        dom.btnPlayTitle.onclick = () => {
          startPlaybackFromStream(item, stream);
        };
        dom.btnOfficialWatch.classList.add("hidden");
      } else {
        dom.btnPlayTitle.classList.add("hidden");
        dom.btnOfficialWatch.classList.remove("hidden");
        dom.btnOfficialWatch.textContent = "🌐 Ver en AniList";
        dom.btnOfficialWatch.onclick = () => {
          fetch("/api/vod/open-official", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ url: `https://anilist.co/anime/${item.id.replace("anilist:", "")}` })
          }).catch(() => {});
        };
      }
    } catch {
      dom.btnPlayTitle.classList.add("hidden");
      dom.btnOfficialWatch.classList.remove("hidden");
      dom.btnOfficialWatch.textContent = "🌐 Ver en AniList";
      dom.btnOfficialWatch.onclick = () => {
        fetch("/api/vod/open-official", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ url: `https://anilist.co/anime/${item.id.replace("anilist:", "")}` })
        }).catch(() => {});
      };
    }
  }

  function startPlaybackFromStream(item, stream) {
    closeDetailModal();
    const titleObj = {
      id: item.id,
      title: item.title,
      year: item.year || 0,
      mediaUrl: stream.url,
      mediaKind: stream.url.includes(".m3u8") ? "hls" : "mp4",
      sourceName: stream.name || "Add-on Stream"
    };
    startPlayback(titleObj);
  }

  function renderDetail(title) {
    dom.detailTitle.textContent = title.title;
    dom.detailPoster.src = title.posterUrl || "";
    dom.detailBadge.textContent = title.kind === "series" ? "Serie VOD" : "Película VOD";

    dom.detailYear.textContent = title.year > 0 ? title.year.toString() : "";
    dom.detailRuntime.textContent = formatDurationMinutes(title.runtimeMin);
    dom.detailLanguages.textContent = title.languages && title.languages.length ? title.languages.join(", ") : "";
    dom.detailLicense.textContent = title.license || "Licencia Libre";

    // Géneros
    dom.detailGenres.innerHTML = "";
    if (title.genres && title.genres.length) {
      for (const g of title.genres) {
        const tag = document.createElement("span");
        tag.className = "genre-tag";
        tag.textContent = g;
        dom.detailGenres.appendChild(tag);
      }
    }

    // Sinopsis
    dom.detailSynopsis.textContent = title.synopsis || "Sin sinopsis disponible.";

    // Botones de acción
    updateDetailActionButtons(title.id);

    // Enlaces de origen
    dom.detailSourceName.textContent = title.sourceName || extractHost(title.sourcePageUrl || title.mediaUrl);
    if (title.sourcePageUrl) {
      dom.detailSourceLink.href = title.sourcePageUrl;
      dom.detailSourceLink.classList.remove("hidden");
    } else {
      dom.detailSourceLink.classList.add("hidden");
    }

    if (title.tmdbId > 0) {
      dom.detailTmdbLink.href = `https://www.themoviedb.org/movie/${title.tmdbId}`;
      dom.detailTmdbLink.classList.remove("hidden");
    } else {
      dom.detailTmdbLink.classList.add("hidden");
    }

    if (title.imdbId) {
      dom.detailImdbLink.href = `https://www.imdb.com/title/${title.imdbId}/`;
      dom.detailImdbLink.classList.remove("hidden");
    } else {
      dom.detailImdbLink.classList.add("hidden");
    }

    // Series: Temporadas y Episodios
    if (title.kind === "series" && title.series && title.series.seasons && title.series.seasons.length) {
      renderSeriesSection(title);
    } else {
      dom.seriesSection.classList.add("hidden");
    }

    // Botón reproducir: muestra tiempo si ya se empezó
    const prog = state.userState.progress[title.id];
    if (prog && prog.positionSec > 10 && (prog.percent || 0) < WATCHED_THRESHOLD_PERCENT) {
      dom.btnPlayTitle.textContent = `▶ Reanudar en ${formatTime(prog.positionSec)}`;
    } else {
      dom.btnPlayTitle.textContent = "▶ Reproducir";
    }

    dom.btnPlayTitle.onclick = () => {
      startPlayback(title);
    };

    dom.btnOpenExternal.onclick = () => {
      openExternalVlc(title.mediaUrl);
    };

    dom.btnReportTitle.onclick = () => {
      if (confirm(`¿Deseas reportar "${title.title}" como no disponible o con fallo?`)) {
        toggleUserAction("unavailable", title.id, "reported_by_user");
        closeDetailModal();
      }
    };
  }

  function updateDetailActionButtons(id) {
    const isFav = state.userState.favorites.has(id);
    const isWatch = state.userState.watchlist.has(id);
    const isWatched = state.userState.watched.has(id);

    dom.btnFavoriteToggle.textContent = isFav ? "★ En favoritos" : "☆ Favorito";
    dom.btnFavoriteToggle.onclick = () => toggleUserAction("favorite", id);

    dom.btnWatchlistToggle.textContent = isWatch ? "✓ En mi lista" : "+ Mi lista";
    dom.btnWatchlistToggle.onclick = () => toggleUserAction("watchlist", id);

    dom.btnWatchedToggle.textContent = isWatched ? "✓ Visto" : "Marcar visto";
    dom.btnWatchedToggle.onclick = () => toggleUserAction("watched", id);
  }

  function renderSeriesSection(title) {
    dom.seriesSection.classList.remove("hidden");
    dom.seasonTabs.innerHTML = "";
    dom.episodeList.innerHTML = "";

    const seasons = title.series.seasons;
    let selectedSeasonIndex = 0;

    seasons.forEach((season, idx) => {
      const tab = document.createElement("button");
      tab.type = "button";
      tab.className = `season-tab ${idx === 0 ? "active" : ""}`;
      tab.textContent = `Temporada ${season.seasonNumber || (idx + 1)}`;
      tab.onclick = () => {
        dom.seasonTabs.querySelectorAll(".season-tab").forEach(t => t.classList.remove("active"));
        tab.classList.add("active");
        renderEpisodesForSeason(title, season);
      };
      dom.seasonTabs.appendChild(tab);
    });

    renderEpisodesForSeason(title, seasons[0]);
  }

  function renderEpisodesForSeason(title, season) {
    dom.episodeList.innerHTML = "";
    if (!season.episodes || season.episodes.length === 0) {
      dom.episodeList.innerHTML = `<span class="muted">No hay episodios registrados en esta temporada.</span>`;
      return;
    }

    season.episodes.forEach((ep, epIdx) => {
      const item = document.createElement("div");
      item.className = "episode-item";
      item.tabIndex = 0;

      const group = document.createElement("div");
      group.className = "episode-title-group";

      const num = document.createElement("span");
      num.className = "episode-num";
      num.textContent = ep.episodeNumber ? `E${ep.episodeNumber}` : `#${epIdx + 1}`;

      const t = document.createElement("span");
      t.textContent = ep.title || `Episodio ${epIdx + 1}`;

      group.appendChild(num);
      group.appendChild(t);

      const dur = document.createElement("span");
      dur.className = "muted";
      dur.textContent = formatDurationMinutes(ep.runtimeMin);

      item.appendChild(group);
      item.appendChild(dur);

      item.onclick = () => {
        startEpisodePlayback(title, season, ep, epIdx);
      };

      dom.episodeList.appendChild(item);
    });
  }

  function closeDetailModal() {
    dom.detailModal.classList.add("hidden");
    state.currentDetail = null;
  }

  // --- Reproductor de Video Directo con Seeking (Range 206) ---
  function startPlayback(title) {
    closeDetailModal();
    const mediaUrl = title.mediaUrl;
    if (!mediaUrl) {
      alert("No hay URL de video asociada a este título.");
      return;
    }

    state.playback = {
      titleId: title.id,
      titleName: title.title,
      subName: title.year > 0 ? title.year.toString() : "",
      mediaUrl: mediaUrl,
      isSeries: title.kind === "series",
      series: title.series || null,
      currentSeason: null,
      currentEpisode: null,
      currentEpisodeIndex: -1
    };

    openPlayerUI();
  }

  function startEpisodePlayback(title, season, episode, epIdx) {
    closeDetailModal();
    const mediaUrl = episode.mediaUrl || title.mediaUrl;
    if (!mediaUrl) {
      alert("No hay URL de video asociada a este episodio.");
      return;
    }

    state.playback = {
      titleId: title.id,
      titleName: title.title,
      subName: `T${season.seasonNumber || 1}:E${episode.episodeNumber || (epIdx + 1)} — ${episode.title || "Episodio"}`,
      mediaUrl: mediaUrl,
      isSeries: true,
      series: title.series,
      currentSeason: season,
      currentEpisode: episode,
      currentEpisodeIndex: epIdx
    };

    openPlayerUI();
  }

  function openPlayerUI() {
    const pb = state.playback;
    dom.playerOverlay.classList.remove("hidden");
    dom.playerErrorOverlay.classList.add("hidden");
    dom.nextEpisodePrompt.classList.add("hidden");

    dom.playerTitle.textContent = pb.titleName;
    dom.playerSub.textContent = pb.subName;
    dom.playerHostPill.textContent = extractHost(pb.mediaUrl);

    dom.btnPlayerVlc.onclick = () => openExternalVlc(pb.mediaUrl);

    // Cargar y reproducir en <video>
    const video = dom.videoPlayer;
    video.pause();
    video.removeAttribute("src");
    video.load();

    video.src = pb.mediaUrl;
    video.focus();

    // Reanudar en progreso guardado si existe
    const saved = state.userState.progress[pb.titleId];
    if (saved && saved.positionSec > 5 && (saved.percent || 0) < WATCHED_THRESHOLD_PERCENT) {
      const handleLoadedMetadata = () => {
        video.removeEventListener("loadedmetadata", handleLoadedMetadata);
        if (saved.positionSec < video.duration) {
          video.currentTime = saved.positionSec;
        }
      };
      video.addEventListener("loadedmetadata", handleLoadedMetadata);
    }

    video.play().catch(err => {
      console.warn("[Cine] Auto-play no permitido o error inicial:", err);
    });

    startProgressAutoSave();
  }

  function closePlayer() {
    stopProgressAutoSave();
    cancelNextEpisodeCountdown();

    const video = dom.videoPlayer;
    if (!video.paused) {
      saveCurrentProgressNow();
      video.pause();
    }
    video.removeAttribute("src");
    video.load();

    dom.playerOverlay.classList.add("hidden");
    dom.playerErrorOverlay.classList.add("hidden");
    state.playback = null;

    // Refrescar tarjetas de la UI para mostrar progreso
    renderGrid();
    renderContinueWatching();
  }

  function handleVideoError() {
    const video = dom.videoPlayer;
    const err = video.error;
    let title = "Error de reproducción";
    let msg = "No se pudo reproducir este archivo multimedia.";

    if (err) {
      if (err.code === MediaError.MEDIA_ERR_NETWORK) {
        msg = "Fallo de red al conectar con el servidor de video (Internet Archive o Wikimedia).";
      } else if (err.code === MediaError.MEDIA_ERR_DECODE) {
        msg = "El formato de códec de este video no es soportado nativamente por WebView2.";
      } else if (err.code === MediaError.MEDIA_ERR_SRC_NOT_SUPPORTED) {
        msg = "El archivo no se encuentra o el enlace fue revocado por el servidor de origen.";
      }
    }

    dom.playerErrorTitle.textContent = title;
    dom.playerErrorMsg.textContent = `${msg} Puedes abrirlo en VLC para reproducir códecs avanzados.`;
    dom.playerErrorOverlay.classList.remove("hidden");

    dom.btnRetryPlay.onclick = () => {
      dom.playerErrorOverlay.classList.add("hidden");
      video.load();
      video.play().catch(() => {});
    };

    dom.btnErrorVlc.onclick = () => {
      if (state.playback) openExternalVlc(state.playback.mediaUrl);
    };

    dom.btnMarkUnavailable.onclick = () => {
      if (state.playback) {
        toggleUserAction("unavailable", state.playback.titleId, msg);
        closePlayer();
      }
    };
  }

  function handleVideoEnded() {
    saveCurrentProgressNow();

    const pb = state.playback;
    if (!pb || !pb.isSeries || !pb.currentSeason || pb.currentEpisodeIndex < 0) {
      return; // Película terminada, no hay auto-advance
    }

    // Verificar si hay siguiente episodio en la temporada
    const nextEpIdx = pb.currentEpisodeIndex + 1;
    const season = pb.currentSeason;
    if (nextEpIdx < season.episodes.length) {
      const nextEp = season.episodes[nextEpIdx];
      startNextEpisodeCountdown(pb.titleName, season, nextEp, nextEpIdx);
    }
  }

  function startNextEpisodeCountdown(seriesTitle, season, nextEp, nextEpIdx) {
    state.nextCountdownSec = 5;
    dom.nextCountdownSec.textContent = state.nextCountdownSec;
    dom.nextEpisodeTitle.textContent = nextEp.title || `Episodio ${nextEpIdx + 1}`;
    dom.nextEpisodePrompt.classList.remove("hidden");

    state.nextCountdownTimer = setInterval(() => {
      state.nextCountdownSec -= 1;
      dom.nextCountdownSec.textContent = state.nextCountdownSec;
      if (state.nextCountdownSec <= 0) {
        cancelNextEpisodeCountdown();
        const pb = state.playback;
        if (pb && pb.series) {
          startEpisodePlayback({ id: pb.titleId, title: seriesTitle, kind: "series", series: pb.series }, season, nextEp, nextEpIdx);
        }
      }
    }, 1000);

    dom.btnPlayNextNow.onclick = () => {
      cancelNextEpisodeCountdown();
      const pb = state.playback;
      if (pb && pb.series) {
        startEpisodePlayback({ id: pb.titleId, title: seriesTitle, kind: "series", series: pb.series }, season, nextEp, nextEpIdx);
      }
    };

    dom.btnCancelNext.onclick = () => {
      cancelNextEpisodeCountdown();
    };
  }

  function cancelNextEpisodeCountdown() {
    if (state.nextCountdownTimer) {
      clearInterval(state.nextCountdownTimer);
      state.nextCountdownTimer = null;
    }
    dom.nextEpisodePrompt.classList.add("hidden");
  }

  // --- Guardado de Progreso ---
  function startProgressAutoSave() {
    stopProgressAutoSave();
    state.saveProgressTimer = setInterval(() => {
      saveCurrentProgressNow();
    }, PROGRESS_AUTO_SAVE_INTERVAL_MS);
  }

  function stopProgressAutoSave() {
    if (state.saveProgressTimer) {
      clearInterval(state.saveProgressTimer);
      state.saveProgressTimer = null;
    }
  }

  function saveCurrentProgressNow() {
    const pb = state.playback;
    const video = dom.videoPlayer;
    if (!pb || !video || isNaN(video.duration) || video.duration <= 0) return;

    const pos = video.currentTime;
    const dur = video.duration;
    const percent = Math.min(100.0, Math.round((pos / dur) * 1000) / 10);

    // Guardar en estado local
    state.userState.progress[pb.titleId] = {
      id: pb.titleId,
      positionSec: pos,
      durationSec: dur,
      percent: percent,
      lastWatchedUtc: new Date().toISOString()
    };

    if (dur > 60 && percent >= WATCHED_THRESHOLD_PERCENT) {
      state.userState.watched.add(pb.titleId);
    }

    // Enviar a backend
    fetch("/api/cine/progress", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        id: pb.titleId,
        positionSec: pos,
        durationSec: dur,
        seriesName: pb.isSeries ? pb.titleName : null,
        episodeId: pb.currentEpisode ? pb.currentEpisode.title : null
      })
    }).catch(() => {});
  }

  // --- Acciones de Usuario (Favorito, Lista, Visto, No disponible) ---
  async function toggleUserAction(action, id, reason) {
    if (!id) return;
    try {
      const resp = await fetch("/api/cine/action", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ action, id, reason: reason || "" })
      });
      if (resp.ok) {
        const data = await resp.json();
        if (data.state) {
          state.userState.favorites = new Set(data.state.favorites || []);
          state.userState.watchlist = new Set(data.state.watchlist || []);
          state.userState.watched = new Set(data.state.watched || []);
          state.userState.unavailable = data.state.unavailable || {};
        }

        // Actualizar UI
        if (state.currentDetail && state.currentDetail.id === id) {
          updateDetailActionButtons(id);
        }
        applyFilters();
      }
    } catch (err) {
      console.error("[Cine] Error al ejecutar acción:", err);
    }
  }

  async function openExternalVlc(mediaUrl) {
    if (!mediaUrl) return;
    try {
      const resp = await fetch("/api/open-external", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ url: mediaUrl })
      });
      const data = await resp.json();
      if (!data.launched) {
        alert("No se encontró el ejecutable de VLC en el sistema. Asegúrate de tener VLC instalado.");
      }
    } catch (err) {
      alert("Error al intentar abrir el reproductor externo: " + err.message);
    }
  }

  // --- Auditoría y Estadísticas de Descarte ---
  function renderDiscardStats() {
    if (!state.stats) return;
    dom.discardStatsDetails.innerHTML = "";
    const items = [
      { label: "Total analizados", val: state.stats.totalParsed },
      { label: "Válidos aceptados", val: state.stats.accepted },
      { label: "Host no permitido", val: state.stats.discardedHostNotAllowed },
      { label: "Esquema no HTTPS", val: state.stats.discardedSchemeNotHttps },
      { label: "Sin título", val: state.stats.discardedMissingTitle },
      { label: "URL inválida", val: state.stats.discardedInvalidUrl },
      { label: "En directo / Live descartados", val: state.stats.discardedLiveStream }
    ];

    for (const stat of items) {
      const row = document.createElement("div");
      row.innerHTML = `<strong>${stat.label}:</strong> <span class="mono">${stat.val}</span>`;
      dom.discardStatsDetails.appendChild(row);
    }
  }

  // --- Vistas Modulares VOD (Disney, Anime, Add-ons) ---
  function updateToolbarForView(view) {
    const isDisney = view === "disney";
    const isAnime = view === "anime";
    const isAddons = view === "addons";
    const isStandard = !isDisney && !isAnime && !isAddons;

    dom.disneyStudioSelect.classList.toggle("hidden", !isDisney);
    dom.btnTmdbKey.classList.toggle("hidden", !isDisney);
    dom.btnAddAddon.classList.toggle("hidden", !isAddons);

    dom.kindSelect.classList.toggle("hidden", isAddons || isDisney || isAnime);
    dom.genreSelect.classList.toggle("hidden", isAddons || isDisney || isAnime);
    dom.decadeSelect.classList.toggle("hidden", isAddons || isDisney || isAnime);
    dom.langSelect.classList.toggle("hidden", isAddons || isDisney || isAnime);
    dom.wikimediaOnlyChk.closest("label").classList.toggle("hidden", !isStandard);
    dom.hideUnavailableChk.closest("label").classList.toggle("hidden", !isStandard);
  }

  async function loadDisneyView() {
    dom.cardsGrid.innerHTML = "";
    dom.resultsCount.textContent = "Cargando títulos…";
    dom.emptyMessage.classList.add("hidden");
    dom.continueSection.classList.add("hidden");

    try {
      const cfgResp = await fetch("/api/vod/tmdb/config", { headers: { "Accept": "application/json" } });
      const cfg = await cfgResp.json();
      state.tmdbConfigured = !!cfg.configured;

      if (!state.tmdbConfigured) {
        state.disneyItems = [];
        state.filteredItems = [];
        dom.resultsCount.textContent = "0 títulos";
        dom.emptyMessage.classList.remove("hidden");
        dom.emptyTitle.textContent = "Clave de TMDb requerida";
        dom.emptySub.textContent = "El catálogo Disney, Pixar y Marvel utiliza metadatos oficiales de TheMovieDB. Pulsa el botón 'Clave TMDb' arriba para ingresar tu clave gratuita.";
        return;
      }

      const studioVal = dom.disneyStudioSelect.value || "disney";
      const resp = await fetch(`/api/vod/tmdb/discover?studio=${encodeURIComponent(studioVal)}`, { headers: { "Accept": "application/json" } });
      if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
      const data = await resp.json();
      state.disneyItems = (data.items || []).map(m => ({
        id: m.id,
        kind: m.kind || "movie",
        title: m.title,
        year: m.year || 0,
        genres: m.genres || [],
        posterUrl: m.posterUrl || "",
        mediaUrl: "",
        runtimeMin: m.runtimeMin || 0,
        officialWatchUrl: m.officialWatchUrl,
        attribution: data.attribution
      }));
      applyFilters();
    } catch (err) {
      console.error("[Cine] Error al cargar catálogo Disney:", err);
      dom.emptyMessage.classList.remove("hidden");
      dom.emptyTitle.textContent = "Error al consultar TMDb";
      dom.emptySub.textContent = err.message || "No se pudo conectar con el servicio de metadatos.";
    }
  }

  async function loadAnimeView() {
    dom.cardsGrid.innerHTML = "";
    dom.resultsCount.textContent = "Consultando catálogo de anime…";
    dom.emptyMessage.classList.add("hidden");
    dom.continueSection.classList.add("hidden");

    try {
      const resp = await fetch("/api/vod/anilist/discover?category=trending", { headers: { "Accept": "application/json" } });
      if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
      const data = await resp.json();
      state.animeItems = (data.items || []).map(m => ({
        id: m.id,
        kind: m.kind || "series",
        title: m.title,
        year: m.year || 0,
        genres: m.genres || [],
        posterUrl: m.posterUrl || "",
        mediaUrl: "",
        runtimeMin: m.runtimeMin || 0,
        animeMeta: m.animeMeta
      }));
      applyFilters();
    } catch (err) {
      console.error("[Cine] Error al cargar anime:", err);
      dom.emptyMessage.classList.remove("hidden");
      dom.emptyTitle.textContent = "Error al consultar AniList";
      dom.emptySub.textContent = err.message || "No se pudo conectar con AniList.";
    }
  }

  async function loadAddonsView() {
    dom.cardsGrid.innerHTML = "";
    dom.resultsCount.textContent = "Cargando add-ons instalados…";
    dom.emptyMessage.classList.add("hidden");
    dom.continueSection.classList.add("hidden");

    try {
      const resp = await fetch("/api/vod/addons", { headers: { "Accept": "application/json" } });
      if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
      const data = await resp.json();
      state.addonManifests = data.manifests || [];
      renderAddonsList(state.addonManifests);
    } catch (err) {
      console.error("[Cine] Error al cargar add-ons:", err);
      dom.emptyMessage.classList.remove("hidden");
      dom.emptyTitle.textContent = "Error al cargar add-ons";
      dom.emptySub.textContent = err.message;
    }
  }

  function renderAddonsList(manifests) {
    dom.cardsGrid.innerHTML = "";
    dom.resultsCount.textContent = `${manifests.length} add-on${manifests.length === 1 ? "" : "s"} instalado${manifests.length === 1 ? "" : "s"}`;

    if (manifests.length === 0) {
      dom.emptyMessage.classList.remove("hidden");
      dom.emptyTitle.textContent = "No hay add-ons instalados";
      dom.emptySub.textContent = "Puedes instalar proveedores VOD externos compatibles con el protocolo Stremio pulsando '+ Agregar Add-on'.";
      return;
    }

    dom.emptyMessage.classList.add("hidden");
    const frag = document.createDocumentFragment();
    for (const m of manifests) {
      const card = document.createElement("div");
      card.className = "cine-card";
      card.style.cursor = "default";

      const wrap = document.createElement("div");
      wrap.className = "card-poster-wrap";
      wrap.style.display = "flex";
      wrap.style.flexDirection = "column";
      wrap.style.justifyContent = "center";
      wrap.style.alignItems = "center";
      wrap.style.padding = "20px";
      wrap.style.background = "#131b2e";
      wrap.style.textAlign = "center";

      const icon = document.createElement("div");
      icon.style.fontSize = "36px";
      icon.style.marginBottom = "8px";
      icon.textContent = "🧩";

      const v = document.createElement("span");
      v.className = "card-badge";
      v.textContent = `v${m.version || "1.0.0"}`;

      wrap.appendChild(icon);
      wrap.appendChild(v);

      const info = document.createElement("div");
      info.className = "card-info";
      info.style.padding = "12px";

      const h = document.createElement("h3");
      h.className = "card-title";
      h.textContent = m.name || m.id;

      const desc = document.createElement("p");
      desc.className = "muted";
      desc.style.fontSize = "12px";
      desc.style.margin = "6px 0";
      desc.textContent = m.description || "Proveedor modular VOD.";

      info.appendChild(h);
      info.appendChild(desc);

      card.appendChild(wrap);
      card.appendChild(info);
      frag.appendChild(card);
    }
    dom.cardsGrid.appendChild(frag);
  }

  // --- Manejo de Eventos y Teclado ---
  function bindEvents() {
    // Pestañas principales
    const tabs = [
      { btn: dom.tabCatalog, view: "catalog" },
      { btn: dom.tabDisney, view: "disney" },
      { btn: dom.tabAnime, view: "anime" },
      { btn: dom.tabAddons, view: "addons" },
      { btn: dom.tabContinue, view: "continue" },
      { btn: dom.tabWatchlist, view: "watchlist" },
      { btn: dom.tabFavorites, view: "favorites" },
      { btn: dom.tabWatched, view: "watched" }
    ];

    tabs.forEach(t => {
      t.btn.onclick = () => {
        tabs.forEach(o => {
          o.btn.classList.remove("active");
          o.btn.setAttribute("aria-selected", "false");
        });
        t.btn.classList.add("active");
        t.btn.setAttribute("aria-selected", "true");
        state.activeView = t.view;
        updateToolbarForView(t.view);
        if (t.view === "disney") {
          loadDisneyView();
        } else if (t.view === "anime") {
          loadAnimeView();
        } else if (t.view === "addons") {
          loadAddonsView();
        } else {
          renderContinueWatching();
          applyFilters();
        }
      };
    });

    dom.disneyStudioSelect.onchange = () => {
      loadDisneyView();
    };

    dom.btnTmdbKey.onclick = async () => {
      dom.tmdbKeyModal.classList.remove("hidden");
      dom.tmdbKeyInput.value = "";
      try {
        const resp = await fetch("/api/vod/tmdb/config", { headers: { "Accept": "application/json" } });
        const data = await resp.json();
        dom.tmdbKeyStatus.textContent = data.configured
          ? "Estado: Clave configurada y protegida con DPAPI en Windows."
          : "Estado: Sin configurar. Ingresa tu clave para habilitar Disney/Marvel/Pixar.";
      } catch {
        dom.tmdbKeyStatus.textContent = "Estado: No disponible.";
      }
    };

    dom.btnCancelTmdbKey.onclick = () => {
      dom.tmdbKeyModal.classList.add("hidden");
    };

    dom.btnSaveTmdbKey.onclick = async () => {
      const k = dom.tmdbKeyInput.value.trim();
      if (!k) return;
      try {
        await fetch("/api/vod/tmdb/config", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ key: k })
        });
        dom.tmdbKeyModal.classList.add("hidden");
        state.tmdbConfigured = true;
        if (state.activeView === "disney") loadDisneyView();
      } catch (err) {
        alert("Error al guardar la clave: " + err.message);
      }
    };

    dom.btnRemoveTmdbKey.onclick = async () => {
      if (confirm("¿Deseas eliminar la clave guardada de TMDb?")) {
        try {
          await fetch("/api/vod/tmdb/config", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ key: "" })
          });
          dom.tmdbKeyModal.classList.add("hidden");
          state.tmdbConfigured = false;
          if (state.activeView === "disney") loadDisneyView();
        } catch (err) {
          alert("Error al borrar la clave: " + err.message);
        }
      }
    };

    dom.btnAddAddon.onclick = () => {
      dom.addonModal.classList.remove("hidden");
      dom.addonUrlInput.value = "";
      dom.addonModalError.classList.add("hidden");
    };

    dom.btnCancelAddon.onclick = () => {
      dom.addonModal.classList.add("hidden");
    };

    dom.btnSubmitAddon.onclick = async () => {
      const url = dom.addonUrlInput.value.trim();
      if (!url || !url.startsWith("https://")) {
        dom.addonModalError.textContent = "Ingresa una URL HTTPS válida que apunte al manifest.json.";
        dom.addonModalError.classList.remove("hidden");
        return;
      }
      dom.btnSubmitAddon.disabled = true;
      dom.btnSubmitAddon.textContent = "Instalando…";
      try {
        const resp = await fetch("/api/vod/addons", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ url: url })
        });
        const data = await resp.json();
        if (data.installed) {
          dom.addonModal.classList.add("hidden");
          loadAddonsView();
        } else {
          dom.addonModalError.textContent = "El manifiesto no es válido o no respondió correctamente.";
          dom.addonModalError.classList.remove("hidden");
        }
      } catch (err) {
        dom.addonModalError.textContent = "Error al conectar: " + err.message;
        dom.addonModalError.classList.remove("hidden");
      } finally {
        dom.btnSubmitAddon.disabled = false;
        dom.btnSubmitAddon.textContent = "Instalar";
      }
    };

    // Filtros
    let searchDebounce = null;
    dom.searchInput.oninput = () => {
      clearTimeout(searchDebounce);
      searchDebounce = setTimeout(() => {
        state.filter.query = dom.searchInput.value;
        applyFilters();
      }, 200);
    };

    dom.kindSelect.onchange = () => {
      state.filter.kind = dom.kindSelect.value;
      applyFilters();
    };

    dom.genreSelect.onchange = () => {
      state.filter.genre = dom.genreSelect.value;
      applyFilters();
    };

    dom.decadeSelect.onchange = () => {
      state.filter.decade = dom.decadeSelect.value;
      applyFilters();
    };

    dom.langSelect.onchange = () => {
      state.filter.lang = dom.langSelect.value;
      applyFilters();
    };

    dom.wikimediaOnlyChk.onchange = () => {
      state.filter.wikimediaOnly = dom.wikimediaOnlyChk.checked;
      applyFilters();
    };

    dom.hideUnavailableChk.onchange = () => {
      state.filter.hideUnavailable = dom.hideUnavailableChk.checked;
      applyFilters();
    };

    dom.btnResetFilters.onclick = () => {
      dom.searchInput.value = "";
      dom.kindSelect.value = "all";
      dom.genreSelect.value = "all";
      dom.decadeSelect.value = "all";
      dom.langSelect.value = "all";
      dom.wikimediaOnlyChk.checked = false;
      dom.hideUnavailableChk.checked = true;

      state.filter = {
        query: "",
        kind: "all",
        genre: "all",
        decade: "all",
        lang: "all",
        wikimediaOnly: false,
        hideUnavailable: true
      };
      applyFilters();
    };

    dom.btnRefresh.onclick = () => {
      loadCatalog(true);
    };

    // Paginación
    const goPrev = () => {
      if (state.currentPage > 1) {
        state.currentPage -= 1;
        renderGrid();
        renderPagination();
        dom.cardsGrid.scrollIntoView({ behavior: "smooth", block: "start" });
      }
    };

    const goNext = () => {
      const maxPage = Math.max(1, Math.ceil(state.filteredItems.length / PAGE_SIZE));
      if (state.currentPage < maxPage) {
        state.currentPage += 1;
        renderGrid();
        renderPagination();
        dom.cardsGrid.scrollIntoView({ behavior: "smooth", block: "start" });
      }
    };

    dom.btnPrevPage.onclick = goPrev;
    dom.btnPrevPageBottom.onclick = goPrev;
    dom.btnNextPage.onclick = goNext;
    dom.btnNextPageBottom.onclick = goNext;

    // Modales
    dom.btnCloseDetail.onclick = closeDetailModal;
    dom.detailModal.onclick = (e) => {
      if (e.target === dom.detailModal) closeDetailModal();
    };

    dom.btnCredits.onclick = () => {
      dom.creditsModal.classList.remove("hidden");
    };
    dom.btnCloseCredits.onclick = () => {
      dom.creditsModal.classList.add("hidden");
    };
    dom.creditsModal.onclick = (e) => {
      if (e.target === dom.creditsModal) dom.creditsModal.classList.add("hidden");
    };

    // Reproductor
    dom.btnClosePlayer.onclick = closePlayer;
    dom.videoPlayer.onerror = handleVideoError;
    dom.videoPlayer.onended = handleVideoEnded;

    // Guardar progreso al salir o cambiar de pestaña
    window.addEventListener("beforeunload", () => {
      if (state.playback) saveCurrentProgressNow();
    });

    document.addEventListener("visibilitychange", () => {
      if (document.hidden && state.playback) {
        saveCurrentProgressNow();
      }
    });

    // Atajos de Teclado
    window.addEventListener("keydown", (e) => {
      // Si el reproductor está abierto
      if (!dom.playerOverlay.classList.contains("hidden")) {
        const video = dom.videoPlayer;
        if (e.key === "Escape") {
          e.preventDefault();
          closePlayer();
          return;
        }
        if (e.key === " " || e.key === "k" || e.key === "K") {
          e.preventDefault();
          if (video.paused) video.play().catch(() => {});
          else video.pause();
          return;
        }
        if (e.key === "ArrowLeft") {
          e.preventDefault();
          video.currentTime = Math.max(0, video.currentTime - 10);
          return;
        }
        if (e.key === "ArrowRight") {
          e.preventDefault();
          video.currentTime = Math.min(video.duration || 0, video.currentTime + 10);
          return;
        }
        if (e.key === "ArrowUp") {
          e.preventDefault();
          video.volume = Math.min(1.0, video.volume + 0.05);
          return;
        }
        if (e.key === "ArrowDown") {
          e.preventDefault();
          video.volume = Math.max(0.0, video.volume - 0.05);
          return;
        }
        if (e.key === "f" || e.key === "F") {
          e.preventDefault();
          if (!document.fullscreenElement) {
            dom.playerOverlay.requestFullscreen().catch(() => {});
          } else {
            document.exitFullscreen().catch(() => {});
          }
          return;
        }
        if (e.key === "m" || e.key === "M") {
          e.preventDefault();
          video.muted = !video.muted;
          return;
        }
        if (e.key === "n" || e.key === "N") {
          e.preventDefault();
          handleVideoEnded();
          return;
        }
        return;
      }

      // Si el modal de detalle está abierto
      if (!dom.detailModal.classList.contains("hidden")) {
        if (e.key === "Escape") {
          e.preventDefault();
          closeDetailModal();
        }
        return;
      }

      // Si el modal de créditos está abierto
      if (!dom.creditsModal.classList.contains("hidden")) {
        if (e.key === "Escape") {
          e.preventDefault();
          dom.creditsModal.classList.add("hidden");
        }
        return;
      }

      // Teclas globales
      if (e.key === "/" && document.activeElement !== dom.searchInput) {
        e.preventDefault();
        dom.searchInput.focus();
        dom.searchInput.select();
      }
    });
  }

  // Arrancar cuando el DOM esté listo
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
})();

