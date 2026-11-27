// ═══════════════════════════════════════════════════════════════════════════
// LEX JUNIOR BROWSER v2.8.5 — MULTI-ROUTE WEBSITE & INTERACTIVE SIMULATOR
// ═══════════════════════════════════════════════════════════════════════════

document.addEventListener('DOMContentLoaded', () => {
  // 1. Router Cliente Multi-Ruta (#/inicio, #/funciones, #/lex-class, #/documentacion, #/faq, #/novedades)
  const routePages = document.querySelectorAll('.route-page');
  const navRouteLinks = document.querySelectorAll('.nav-route-link');
  const navLinksContainer = document.getElementById('nav-links');

  const routeTitles = {
    'inicio': 'Lex Junior Browser v2.8.5 — Navegador Privado, Rápido y Cero Telemetría',
    'funciones': 'Funciones y Perfiles de Privacidad A/B/C — Lex Junior Browser v2.8.5',
    'lex-sticker': 'LexSticker — Estudio de Stickers con Audio para WhatsApp',
    'lex-class': 'Lex Junior Class — Academia Interactiva en Lex Junior',
    'documentacion': 'Documentación Oficial y Manual Técnico — Lex Junior Browser v2.8.5',
    'faq': 'Preguntas y Respuestas (FAQ) — Lex Junior Browser v2.8.5',
    'novedades': 'Novedades y Changelog v2.8.5 — Lex Junior Browser'
  };

  const normalizeRoute = (rawHash) => {
    const clean = (rawHash || '').replace(/^#\/?/, '').trim().toLowerCase();
    if (!clean || clean === 'inicio' || clean === 'preview') return 'inicio';
    if (clean === 'funciones' || clean === 'features' || clean === 'telemetry') return 'funciones';
    if (clean === 'lex-sticker' || clean === 'lexsticker' || clean === 'sticker' || clean === 'stickers') return 'lex-sticker';
    if (clean === 'lex-class' || clean === 'lexclass' || clean === 'class') return 'lex-class';
    if (clean === 'documentacion' || clean === 'docs' || clean === 'specs') return 'documentacion';
    if (clean === 'faq' || clean === 'preguntas') return 'faq';
    if (clean === 'novedades' || clean === 'changelog') return 'novedades';
    return 'inicio';
  };

  const activateRoute = (routeName, scrollToTop = true) => {
    const targetRoute = routeTitles[routeName] ? routeName : 'inicio';

    routePages.forEach((page) => {
      const isMatch = page.dataset.routePage === targetRoute;
      page.classList.toggle('active', isMatch);
    });

    navRouteLinks.forEach((link) => {
      link.classList.toggle('active', link.dataset.route === targetRoute);
    });

    if (routeTitles[targetRoute]) {
      document.title = routeTitles[targetRoute];
    }

    if (navLinksContainer) {
      navLinksContainer.classList.remove('open');
    }

    if (scrollToTop) {
      window.scrollTo({ top: 0, behavior: 'smooth' });
    }
  };

  window.addEventListener('hashchange', () => {
    const route = normalizeRoute(window.location.hash);
    activateRoute(route, true);
  });

  document.querySelectorAll('[data-route-link]').forEach((el) => {
    el.addEventListener('click', (e) => {
      const target = el.getAttribute('data-route-link');
      if (target) {
        e.preventDefault();
        window.location.hash = `#/${target}`;
        activateRoute(target, true);
      }
    });
  });

  // Activar ruta inicial según el hash de la URL
  activateRoute(normalizeRoute(window.location.hash), false);

  // 2. Animaciones Reveal
  const revealItems = document.querySelectorAll('.reveal');
  if ('IntersectionObserver' in window) {
    const observer = new IntersectionObserver(
      (entries) => {
        entries.forEach((entry) => {
          if (entry.isIntersecting) {
            entry.target.classList.add('visible');
            observer.unobserve(entry.target);
          }
        });
      },
      { threshold: 0.1 }
    );
    revealItems.forEach((el) => observer.observe(el));
  } else {
    revealItems.forEach((el) => el.classList.add('visible'));
  }

  // 3. Menú móvil adaptable
  const mobileBtn = document.getElementById('mobile-menu-btn');
  if (mobileBtn && navLinksContainer) {
    mobileBtn.addEventListener('click', () => {
      navLinksContainer.classList.toggle('open');
    });
  }

  // 4. Reloj en vivo dentro de la vista "Nueva Pestaña"
  const clockEl = document.getElementById('live-demo-clock');
  const updateClock = () => {
    if (!clockEl) return;
    const now = new Date();
    const hh = String(now.getHours()).padStart(2, '0');
    const mm = String(now.getMinutes()).padStart(2, '0');
    clockEl.textContent = `${hh}:${mm}`;
  };
  updateClock();
  setInterval(updateClock, 15000);

  // 5. Simulador interactivo de vistas del navegador (incluyendo Lex Class y PDF Virtual)
  const viewMeta = {
    'view-newtab': {
      icon: '⚡',
      title: 'Nueva pestaña',
      url: 'lex://nueva-pestana',
      ads: '0 bloqueados'
    },
    'view-lexclass': {
      icon: '🎓',
      title: 'Lex Junior Class — Universo de Mundos',
      url: 'https://class.lex.local/index.html',
      ads: '100 % Local · 0 Telemetría'
    },
    'view-tv': {
      icon: '📺',
      title: 'Lex Media — Reproductor Multimedia Personal',
      url: 'https://tv.lex.local/index.html',
      ads: '0 Telemetría · Bajo demanda'
    },
    'view-youtube': {
      icon: '▶',
      title: 'YouTube — Concierto en Vivo 4K',
      url: 'https://www.youtube.com/watch?v=LexJunior4K',
      ads: '34 bloqueados'
    },
    'view-sniffer': {
      icon: '📥',
      title: 'Reproductor Multimedia — Episodio 5',
      url: 'https://reproductor.local/episodio-05',
      ads: '29 bloqueados'
    },
    'view-pip': {
      icon: '🗖',
      title: 'Transmisión en Vivo · Página Flotante',
      url: 'https://twitch.tv/lexjunior_live',
      ads: '18 bloqueados'
    },
    'view-pdf': {
      icon: '📑',
      title: 'Silabo_2026.pdf — Lex PDF Studio (Local y Virtual)',
      url: 'https://pdf.lex.local/viewer.html?file=https%3A%2F%2Funiversidad.edu%2FSilabo_2026.pdf',
      ads: 'Proxy Seguro · Anti-SSRF'
    },
    'view-incognito': {
      icon: '🕵️',
      title: 'Pestaña Privada (Incógnito)',
      url: 'lex://incognito-aislado',
      ads: 'Protección máxima'
    }
  };

  const tabs = document.querySelectorAll('.showcase-tab');
  const views = document.querySelectorAll('.bw-view');
  const tabIcon = document.getElementById('mock-tab-icon');
  const tabTitle = document.getElementById('mock-tab-title');
  const urlText = document.getElementById('mock-url-text');
  const adsCount = document.getElementById('mock-ads-count');

  const activateView = (viewId) => {
    tabs.forEach((t) => t.classList.toggle('active', t.dataset.view === viewId));
    views.forEach((v) => v.classList.toggle('active', v.id === viewId));
    const meta = viewMeta[viewId];
    if (meta) {
      if (tabIcon) tabIcon.textContent = meta.icon;
      if (tabTitle) tabTitle.textContent = meta.title;
      if (urlText) urlText.textContent = meta.url;
      if (adsCount) adsCount.textContent = meta.ads;
    }
  };

  tabs.forEach((tab) => {
    tab.addEventListener('click', () => activateView(tab.dataset.view));
  });

  const mockBtnTv = document.getElementById('mock-btn-tv');
  const mockBtnClass = document.getElementById('mock-btn-class');
  const mockBkClass = document.getElementById('mock-bk-class');
  const mockNtClassCard = document.getElementById('mock-nt-class-card');
  const mockBtnPip = document.getElementById('mock-btn-pip');
  const mockBtnVol = document.getElementById('mock-btn-vol');
  const mockBtnMedia = document.getElementById('mock-btn-media');

  if (mockBtnTv) mockBtnTv.addEventListener('click', () => activateView('view-tv'));
  if (mockBtnClass) mockBtnClass.addEventListener('click', () => activateView('view-lexclass'));
  if (mockBkClass) mockBkClass.addEventListener('click', () => activateView('view-lexclass'));
  if (mockNtClassCard) mockNtClassCard.addEventListener('click', () => activateView('view-lexclass'));
  if (mockBtnPip) mockBtnPip.addEventListener('click', () => activateView('view-pip'));
  if (mockBtnVol) mockBtnVol.addEventListener('click', () => activateView('view-youtube'));
  if (mockBtnMedia) mockBtnMedia.addEventListener('click', () => activateView('view-sniffer'));

  // 6. Selector de tema visual del navegador simulado
  const browserMockup = document.getElementById('browser-mockup');
  const themeButtons = document.querySelectorAll('.theme-pill');
  const setMockupTheme = (theme) => {
    if (!browserMockup) return;
    browserMockup.classList.remove('theme-classic', 'theme-dark', 'theme-light');
    const safeTheme = theme === 'dark' || theme === 'light' ? theme : 'classic';
    browserMockup.classList.add(`theme-${safeTheme}`);
    themeButtons.forEach((btn) => {
      const isActive = btn.dataset.theme === safeTheme;
      btn.classList.toggle('active', isActive);
      btn.setAttribute('aria-pressed', isActive ? 'true' : 'false');
    });
  };
  themeButtons.forEach((btn) => {
    btn.addEventListener('click', () => setMockupTheme(btn.dataset.theme));
  });
  setMockupTheme('classic');

  // 7. Slider interactivo del Amplificador de Volumen 300%
  const volSlider = document.getElementById('demo-vol-slider');
  const volLabel = document.getElementById('demo-vol-label');
  const navVolBadge = document.getElementById('mock-vol-badge');
  const presetBtns = document.querySelectorAll('.preset-btn');

  const setDemoVolume = (val) => {
    const pct = Math.max(0, Math.min(300, Number(val)));
    if (volSlider) volSlider.value = String(pct);
    if (volLabel) volLabel.textContent = `${pct}%`;
    if (navVolBadge) navVolBadge.textContent = `${pct}%`;
  };

  if (volSlider) {
    volSlider.addEventListener('input', (e) => setDemoVolume(e.target.value));
  }
  presetBtns.forEach((btn) => {
    btn.addEventListener('click', () => setDemoVolume(btn.dataset.vol));
  });

  // 8. Navegación lateral dentro de #/documentacion
  const docNavBtns = document.querySelectorAll('.doc-nav-btn');
  docNavBtns.forEach((btn) => {
    btn.addEventListener('click', () => {
      docNavBtns.forEach((b) => b.classList.remove('active'));
      btn.classList.add('active');
      const targetId = btn.dataset.docTarget;
      const targetEl = targetId ? document.getElementById(targetId) : null;
      if (targetEl) {
        targetEl.scrollIntoView({ behavior: 'smooth', block: 'start' });
      }
    });
  });

  // 9. Buscador y filtro por categorías en #/faq
  const faqSearchInput = document.getElementById('faq-search-input');
  const faqCatPills = document.querySelectorAll('.faq-cat-pill');
  const faqItems = document.querySelectorAll('#faq-list .faq-item');
  let activeFaqCat = 'all';

  const filterFaq = () => {
    const q = (faqSearchInput ? faqSearchInput.value : '').trim().toLowerCase();
    faqItems.forEach((item) => {
      const cat = item.dataset.cat || 'general';
      const matchesCat = activeFaqCat === 'all' || cat === activeFaqCat;
      const text = (item.textContent || '').toLowerCase();
      const matchesQuery = !q || text.includes(q);
      const visible = matchesCat && matchesQuery;
      item.style.display = visible ? '' : 'none';
      if (q && visible) {
        item.open = true;
      }
    });
  };

  if (faqSearchInput) {
    faqSearchInput.addEventListener('input', filterFaq);
  }

  faqCatPills.forEach((pill) => {
    pill.addEventListener('click', () => {
      faqCatPills.forEach((p) => p.classList.remove('active'));
      pill.classList.add('active');
      activeFaqCat = pill.dataset.faqCat || 'all';
      filterFaq();
    });
  });

  // 10. Sincronización automática con la última Release de GitHub
  fetch('https://api.github.com/repos/JuniorJhair/Lex-Junior/releases/latest')
    .then((res) => (res.ok ? res.json() : null))
    .then((data) => {
      if (!data || !data.tag_name) return;
      const version = data.tag_name;
      const navBadge = document.getElementById('nav-version-badge');
      const heroText = document.getElementById('hero-release-text');
      const metaLabel = document.getElementById('download-meta-label');

      if (navBadge) navBadge.textContent = version;
      if (heroText) {
        heroText.textContent = `Lanzamiento Oficial ${version} · Windows 10 y 11 (64-bit)`;
      }

      if (metaLabel && Array.isArray(data.assets) && data.assets.length > 0) {
        const setupAsset =
          data.assets.find((a) => a.name === 'LexJuniorSetup.exe') || data.assets[0];
        if (setupAsset && setupAsset.size) {
          const sizeMb = (setupAsset.size / (1024 * 1024)).toFixed(1);
          metaLabel.textContent = `Instalador Oficial Verificado SHA-256 (${version} · ${sizeMb} MB) · Gratis`;
        }
      }
    })
    .catch(() => {
      // Silencioso si el usuario navega sin conexión
    });

  // 11. LexSticker — Simulador Interactivo, Tabs de Inicio y Guía Paso a Paso
  const lsJumpSim = document.getElementById('ls-btn-jump-sim');
  const lsJumpGuide = document.getElementById('ls-btn-jump-guide');
  if (lsJumpSim) {
    lsJumpSim.addEventListener('click', () => {
      document.getElementById('ls-simulator')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    });
  }
  if (lsJumpGuide) {
    lsJumpGuide.addEventListener('click', () => {
      document.getElementById('ls-steps-guide')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    });
  }

  const lsPresets = {
    lol: {
      emoji: '😂',
      caption: 'NO PUEDE SER',
      bg: 'radial-gradient(circle at 30% 25%, rgba(255,122,24,0.38), rgba(124,58,237,0.34))',
      notes: [523.25, 659.25, 783.99, 1046.5],
      webpBase: 195,
    },
    fire: {
      emoji: '🔥',
      caption: 'ÉPICO BRO',
      bg: 'radial-gradient(circle at 30% 25%, rgba(239,68,68,0.38), rgba(245,158,11,0.32))',
      notes: [440, 554.37, 659.25, 880],
      webpBase: 230,
    },
    cool: {
      emoji: '😎',
      caption: 'LISTO PA HOY',
      bg: 'radial-gradient(circle at 30% 25%, rgba(16,185,129,0.36), rgba(59,130,246,0.34))',
      notes: [392, 493.88, 587.33, 783.99],
      webpBase: 178,
    },
  };

  const lsAudioDescriptions = {
    none: '🔇 Sticker tradicional sin pista de audio (animado o estático hasta 10 s).',
    original: '🔊 Usa el audio original del video y sincronízalo con el deslizador de inicio.',
    upload: '🎵 Sube cualquier MP3, WAV, OGG o M4A, o extrae el audio desde otro video.',
    mic: '🎤 Graba tu propia voz o un efecto rápido en vivo con el micrófono.',
  };

  const lsSendDescriptions = {
    audioSticker: 'Envía el sticker con audio nativo embebido (audioStickerMessage): suena al tocarlo y puedes guardarlo en Favoritos.',
    stickerDirect: 'Adjunta el audio dentro de stickerMessage + audioMessage de forma directa.',
    dual: 'Universal: envía primero el sticker cuadrado y justo debajo la nota de voz Opus para 100 % de compatibilidad.',
  };

  const lsState = {
    preset: 'lol',
    userFileUrl: null,
    userKind: null,
    zoom: 100,
    panX: 0,
    panY: 0,
    maxCap: 5,
    start: 0.5,
    duration: 5.0,
    sound: 'original',
    sendMode: 'audioSticker',
    playing: false,
    playTimer: null,
    audioCtx: null,
  };

  const lsCropStage = document.getElementById('ls-crop-stage');
  const lsCropVisual = document.getElementById('ls-crop-visual');
  const lsCropImg = document.getElementById('ls-crop-user-img');
  const lsCropVideo = document.getElementById('ls-crop-user-video');
  const lsCropZoom = document.getElementById('ls-crop-zoom');
  const lsCropCoords = document.getElementById('ls-crop-coords');

  const lsWaVisual = document.getElementById('ls-wa-sticker-art');
  const lsWaImg = document.getElementById('ls-wa-user-img');
  const lsWaVideo = document.getElementById('ls-wa-user-video');
  const lsWaBubble = document.getElementById('ls-wa-bubble');
  const lsWaSoundBadge = document.getElementById('ls-wa-sound-badge');
  const lsWaSoundText = document.getElementById('ls-wa-sound-text');
  const lsWaDualNote = document.getElementById('ls-wa-dual-note');
  const lsWaToast = document.getElementById('ls-wa-fav-toast');

  const lsStartSlider = document.getElementById('ls-start-slider');
  const lsDurSlider = document.getElementById('ls-dur-slider');
  const lsStartLabel = document.getElementById('ls-start-label');
  const lsDurLabel = document.getElementById('ls-dur-label');
  const lsTrimWindow = document.getElementById('ls-trim-window');
  const lsTrimText = document.getElementById('ls-trim-text');

  const lsAudioHint = document.getElementById('ls-audio-hint');
  const lsAudioPill = document.getElementById('ls-audio-pill');
  const lsSendDesc = document.getElementById('ls-send-desc');
  const lsChipDim = document.getElementById('ls-chip-dim');
  const lsChipDur = document.getElementById('ls-chip-dur');
  const lsChipWebp = document.getElementById('ls-chip-webp');
  const lsChipAudio = document.getElementById('ls-chip-audio');
  const lsResetBtn = document.getElementById('ls-sim-reset-btn');

  const stopLsPlayback = () => {
    lsState.playing = false;
    if (lsState.playTimer) {
      clearTimeout(lsState.playTimer);
      lsState.playTimer = null;
    }
    if (lsWaBubble) lsWaBubble.classList.remove('playing');
    if (lsWaVideo && lsState.userKind === 'video') {
      lsWaVideo.muted = true;
    }
    if (lsWaSoundText) {
      lsWaSoundText.textContent = lsState.sound === 'none' ? '🔇 Sin audio' : '🔊 Toca para oír';
    }
  };

  const renderLsMedia = () => {
    const preset = lsPresets[lsState.preset] || lsPresets.lol;
    const transformVal = `translate(${lsState.panX}%, ${lsState.panY}%) scale(${(lsState.zoom / 100).toFixed(2)})`;

    if (lsState.userFileUrl) {
      if (lsCropVisual) lsCropVisual.style.display = 'none';
      if (lsWaVisual) lsWaVisual.style.display = 'none';
      if (lsResetBtn) lsResetBtn.style.display = 'inline-flex';

      const isImg = lsState.userKind === 'image';
      if (lsCropImg) {
        lsCropImg.style.display = isImg ? 'block' : 'none';
        if (isImg) {
          lsCropImg.src = lsState.userFileUrl;
          lsCropImg.style.transform = transformVal;
        }
      }
      if (lsWaImg) {
        lsWaImg.style.display = isImg ? 'block' : 'none';
        if (isImg) {
          lsWaImg.src = lsState.userFileUrl;
          lsWaImg.style.transform = transformVal;
        }
      }
      if (lsCropVideo) {
        lsCropVideo.style.display = !isImg ? 'block' : 'none';
        if (!isImg) {
          if (lsCropVideo.src !== lsState.userFileUrl) lsCropVideo.src = lsState.userFileUrl;
          lsCropVideo.style.transform = transformVal;
          lsCropVideo.play().catch(() => {});
        }
      }
      if (lsWaVideo) {
        lsWaVideo.style.display = !isImg ? 'block' : 'none';
        if (!isImg) {
          if (lsWaVideo.src !== lsState.userFileUrl) lsWaVideo.src = lsState.userFileUrl;
          lsWaVideo.style.transform = transformVal;
          lsWaVideo.play().catch(() => {});
        }
      }
    } else {
      if (lsResetBtn) lsResetBtn.style.display = 'none';
      if (lsCropImg) lsCropImg.style.display = 'none';
      if (lsWaImg) lsWaImg.style.display = 'none';
      if (lsCropVideo) lsCropVideo.style.display = 'none';
      if (lsWaVideo) lsWaVideo.style.display = 'none';

      const cardHtml = `
        <div class="ls-sticker-card-demo" style="background:${preset.bg}">
          <span class="ls-sticker-emoji">${preset.emoji}</span>
          <span class="ls-sticker-caption">${preset.caption}</span>
        </div>`;

      if (lsCropVisual) {
        lsCropVisual.style.display = 'flex';
        lsCropVisual.innerHTML = cardHtml;
        lsCropVisual.style.transform = transformVal;
      }
      if (lsWaVisual) {
        lsWaVisual.style.display = 'flex';
        lsWaVisual.innerHTML = cardHtml;
        lsWaVisual.style.transform = transformVal;
      }
    }

    if (lsCropCoords) {
      lsCropCoords.textContent = `Encuadre: ${lsState.zoom}% · X:${lsState.panX >= 0 ? '+' : ''}${Math.round(lsState.panX)} Y:${lsState.panY >= 0 ? '+' : ''}${Math.round(lsState.panY)}`;
    }
  };

  const renderLsControls = () => {
    const preset = lsPresets[lsState.preset] || lsPresets.lol;
    const totalTimeline = 10;
    const end = Math.min(totalTimeline, lsState.start + lsState.duration);
    const leftPct = (lsState.start / totalTimeline) * 100;
    const widthPct = Math.max(8, ((end - lsState.start) / totalTimeline) * 100);

    if (lsTrimWindow) {
      lsTrimWindow.style.left = `${leftPct}%`;
      lsTrimWindow.style.width = `${Math.min(100 - leftPct, widthPct)}%`;
    }
    if (lsTrimText) {
      lsTrimText.textContent = `${lsState.start.toFixed(1)}s – ${end.toFixed(1)}s`;
    }
    if (lsStartLabel) lsStartLabel.textContent = `${lsState.start.toFixed(1)} s`;
    if (lsDurLabel) lsDurLabel.textContent = `${lsState.duration.toFixed(1)} s`;

    if (lsAudioHint) lsAudioHint.textContent = lsAudioDescriptions[lsState.sound] || '';
    if (lsAudioPill) {
      lsAudioPill.textContent = lsState.sound === 'none' ? 'Sin pista de audio' : 'Opus 24 kbps';
    }
    if (lsSendDesc) lsSendDesc.textContent = lsSendDescriptions[lsState.sendMode] || '';

    const hasSound = lsState.sound !== 'none';
    if (lsWaSoundBadge) {
      lsWaSoundBadge.style.borderColor = hasSound ? 'rgba(16, 185, 129, 0.55)' : 'rgba(255, 255, 255, 0.2)';
    }
    if (lsWaSoundText && !lsState.playing) {
      lsWaSoundText.textContent = hasSound ? '🔊 Toca para oír' : '🔇 Sin audio';
    }

    if (lsWaDualNote) {
      lsWaDualNote.style.display = hasSound && lsState.sendMode === 'dual' ? 'block' : 'none';
      const secInt = String(Math.max(1, Math.round(lsState.duration))).padStart(2, '0');
      lsWaDualNote.textContent = `🎤 Nota de voz aparte (0:${secInt} · Modo Universal)`;
    }

    const estWebpKb = Math.min(485, Math.round((preset.webpBase * lsState.duration) / 5));
    const estAudioKb = hasSound ? Math.max(4, Math.round(lsState.duration * 3.4)) : 0;

    if (lsChipDim) lsChipDim.textContent = `📐 512×512 px`;
    if (lsChipDur) lsChipDur.textContent = `⏱️ ${lsState.duration.toFixed(1)} s`;
    if (lsChipWebp) lsChipWebp.textContent = `🖼️ WebP ~${estWebpKb} KB`;
    if (lsChipAudio) {
      lsChipAudio.textContent = hasSound ? `🔊 Opus ~${estAudioKb} KB` : '🔇 Sin audio';
    }
  };

  const playLsSynthSequence = () => {
    try {
      const AudioCtx = window.AudioContext || window.webkitAudioContext;
      if (!AudioCtx) return false;
      if (!lsState.audioCtx) lsState.audioCtx = new AudioCtx();
      if (lsState.audioCtx.state === 'suspended') lsState.audioCtx.resume();

      const ctx = lsState.audioCtx;
      const preset = lsPresets[lsState.preset] || lsPresets.lol;
      const notes =
        lsState.sound === 'mic'
          ? [330, 440, 392, 523.25]
          : lsState.sound === 'upload'
          ? [293.66, 369.99, 440, 587.33]
          : preset.notes;

      const now = ctx.currentTime;
      const stepDur = 0.13;
      for (let loop = 0; loop < 3; loop++) {
        notes.forEach((freq, idx) => {
          const t0 = now + loop * (notes.length * stepDur + 0.18) + idx * stepDur;
          const osc = ctx.createOscillator();
          const gain = ctx.createGain();
          osc.type = lsState.sound === 'mic' ? 'triangle' : 'sine';
          osc.frequency.setValueAtTime(freq, t0);
          gain.gain.setValueAtTime(0.001, t0);
          gain.gain.exponentialRampToValueAtTime(0.11, t0 + 0.02);
          gain.gain.exponentialRampToValueAtTime(0.001, t0 + stepDur - 0.01);
          osc.connect(gain);
          gain.connect(ctx.destination);
          osc.start(t0);
          osc.stop(t0 + stepDur);
        });
      }
      return true;
    } catch {
      return false;
    }
  };

  const handlePlayWaSticker = () => {
    if (!lsWaBubble) return;
    if (lsState.sound === 'none') {
      if (lsWaToast) {
        lsWaToast.textContent = '💡 Elige Sonido original, Subir audio o Grabar voz arriba para escucharlo.';
      }
      return;
    }

    if (lsState.playing) {
      stopLsPlayback();
      return;
    }

    lsState.playing = true;
    lsWaBubble.classList.add('playing');
    if (lsWaSoundText) lsWaSoundText.textContent = '🔊 Sonando (bucle 3x)...';
    if (lsWaToast) lsWaToast.textContent = '';

    if (lsState.userFileUrl && lsState.userKind === 'video' && lsState.sound === 'original' && lsWaVideo) {
      lsWaVideo.currentTime = lsState.start;
      lsWaVideo.muted = false;
      lsWaVideo.play().catch(() => {
        playLsSynthSequence();
      });
    } else {
      playLsSynthSequence();
    }

    lsState.playTimer = setTimeout(() => {
      stopLsPlayback();
    }, 2400);
  };

  if (lsWaBubble) {
    lsWaBubble.addEventListener('click', handlePlayWaSticker);
    lsWaBubble.addEventListener('keydown', (e) => {
      if (e.key === 'Enter' || e.key === ' ') {
        e.preventDefault();
        handlePlayWaSticker();
      }
    });
  }

  document.querySelectorAll('.ls-preset-btn').forEach((btn) => {
    btn.addEventListener('click', () => {
      stopLsPlayback();
      document.querySelectorAll('.ls-preset-btn').forEach((b) => b.classList.remove('active'));
      btn.classList.add('active');
      lsState.preset = btn.dataset.lsPreset || 'lol';
      if (lsState.userFileUrl) {
        URL.revokeObjectURL(lsState.userFileUrl);
        lsState.userFileUrl = null;
        lsState.userKind = null;
      }
      renderLsMedia();
      renderLsControls();
    });
  });

  const lsSimFile = document.getElementById('ls-sim-file');
  const lsSimUploadBtn = document.getElementById('ls-sim-upload-btn');
  if (lsSimUploadBtn && lsSimFile) {
    lsSimUploadBtn.addEventListener('click', () => lsSimFile.click());
    lsSimFile.addEventListener('change', (e) => {
      const file = e.target.files?.[0];
      e.target.value = '';
      if (!file) return;
      stopLsPlayback();
      if (lsState.userFileUrl) URL.revokeObjectURL(lsState.userFileUrl);
      lsState.userFileUrl = URL.createObjectURL(file);
      lsState.userKind = file.type.startsWith('video/') ? 'video' : 'image';
      lsState.panX = 0;
      lsState.panY = 0;
      lsState.zoom = 108;
      if (lsCropZoom) lsCropZoom.value = '108';
      renderLsMedia();
      renderLsControls();
    });
  }

  if (lsResetBtn) {
    lsResetBtn.addEventListener('click', () => {
      stopLsPlayback();
      if (lsState.userFileUrl) {
        URL.revokeObjectURL(lsState.userFileUrl);
        lsState.userFileUrl = null;
        lsState.userKind = null;
      }
      lsState.panX = 0;
      lsState.panY = 0;
      lsState.zoom = 100;
      if (lsCropZoom) lsCropZoom.value = '100';
      renderLsMedia();
      renderLsControls();
    });
  }

  if (lsCropZoom) {
    lsCropZoom.addEventListener('input', (e) => {
      lsState.zoom = Number(e.target.value) || 100;
      renderLsMedia();
    });
  }

  if (lsCropStage) {
    let dragging = null;
    lsCropStage.addEventListener('pointerdown', (e) => {
      dragging = { startX: e.clientX, startY: e.clientY, initPanX: lsState.panX, initPanY: lsState.panY };
      lsCropStage.setPointerCapture?.(e.pointerId);
    });
    lsCropStage.addEventListener('pointermove', (e) => {
      if (!dragging) return;
      const dx = ((e.clientX - dragging.startX) / 220) * 35;
      const dy = ((e.clientY - dragging.startY) / 220) * 35;
      lsState.panX = Math.max(-28, Math.min(28, dragging.initPanX + dx));
      lsState.panY = Math.max(-28, Math.min(28, dragging.initPanY + dy));
      renderLsMedia();
    });
    const endDrag = () => {
      dragging = null;
    };
    lsCropStage.addEventListener('pointerup', endDrag);
    lsCropStage.addEventListener('pointercancel', endDrag);
  }

  document.querySelectorAll('.ls-cap-pill').forEach((btn) => {
    btn.addEventListener('click', () => {
      document.querySelectorAll('.ls-cap-pill').forEach((b) => b.classList.remove('active'));
      btn.classList.add('active');
      lsState.maxCap = Number(btn.dataset.lsCap) || 5;
      if (lsDurSlider) {
        lsDurSlider.max = String(lsState.maxCap);
        if (lsState.duration > lsState.maxCap) {
          lsState.duration = lsState.maxCap;
          lsDurSlider.value = String(lsState.maxCap);
        }
      }
      if (lsState.start + lsState.duration > 10) {
        lsState.start = Math.max(0, 10 - lsState.duration);
        if (lsStartSlider) lsStartSlider.value = String(lsState.start);
      }
      renderLsControls();
    });
  });

  if (lsStartSlider) {
    lsStartSlider.addEventListener('input', (e) => {
      lsState.start = Number(e.target.value) || 0;
      renderLsControls();
    });
  }

  if (lsDurSlider) {
    lsDurSlider.addEventListener('input', (e) => {
      lsState.duration = Math.min(lsState.maxCap, Math.max(1, Number(e.target.value) || 5));
      renderLsControls();
    });
  }

  document.querySelectorAll('.ls-sound-card').forEach((card) => {
    card.addEventListener('click', () => {
      stopLsPlayback();
      document.querySelectorAll('.ls-sound-card').forEach((c) => c.classList.remove('active'));
      card.classList.add('active');
      lsState.sound = card.dataset.lsSound || 'original';
      renderLsControls();
    });
  });

  document.querySelectorAll('.ls-send-pill').forEach((pill) => {
    pill.addEventListener('click', () => {
      document.querySelectorAll('.ls-send-pill').forEach((p) => p.classList.remove('active'));
      pill.classList.add('active');
      lsState.sendMode = pill.dataset.lsSend || 'audioSticker';
      renderLsControls();
    });
  });

  const lsFavBtn = document.getElementById('ls-wa-fav-btn');
  if (lsFavBtn && lsWaToast) {
    lsFavBtn.addEventListener('click', () => {
      lsWaToast.textContent = '⭐ En tu WhatsApp mantén presionado el sticker → "Añadir a favoritos".';
    });
  }

  // Launch tabs (Laptop / Celular / Docker)
  document.querySelectorAll('.ls-launch-tab').forEach((tab) => {
    tab.addEventListener('click', () => {
      const target = tab.dataset.lsLaunch;
      document.querySelectorAll('.ls-launch-tab').forEach((t) => t.classList.remove('active'));
      document.querySelectorAll('.ls-launch-panel').forEach((p) => p.classList.remove('active'));
      tab.classList.add('active');
      document.querySelector(`.ls-launch-panel[data-ls-panel="${target}"]`)?.classList.add('active');
    });
  });

  // Copy command buttons
  document.querySelectorAll('.ls-copy-btn').forEach((btn) => {
    btn.addEventListener('click', async () => {
      const txt = btn.dataset.copy || '';
      if (!txt) return;
      try {
        await navigator.clipboard.writeText(txt);
        const orig = btn.textContent;
        btn.textContent = '✅ Copiado';
        setTimeout(() => {
          btn.textContent = orig;
        }, 1600);
      } catch {
        // Fallback silencioso
      }
    });
  });

  // Interactive 5-step guide checklist
  const lsStepCards = document.querySelectorAll('.ls-step-card');
  const lsProgFill = document.getElementById('ls-guide-progress-fill');
  const lsProgText = document.getElementById('ls-guide-progress-text');

  const updateLsProgress = () => {
    const total = lsStepCards.length || 5;
    const doneCount = document.querySelectorAll('.ls-step-card.done').length;
    const pct = Math.round((doneCount / total) * 100);
    if (lsProgFill) lsProgFill.style.width = `${pct}%`;
    if (lsProgText) {
      lsProgText.textContent =
        doneCount === total
          ? '🎉 ¡5 de 5 completados — ya dominas LexSticker!'
          : `${doneCount} de ${total} pasos completados`;
    }
  };

  lsStepCards.forEach((card) => {
    card.addEventListener('click', (e) => {
      if (e.target.closest('code')) return;
      card.classList.toggle('done');
      updateLsProgress();
    });
  });

  renderLsMedia();
  renderLsControls();
  updateLsProgress();
});
