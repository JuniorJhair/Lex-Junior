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
    'lex-class': 'Lex Junior Class — Academia Interactiva en Lex Junior',
    'documentacion': 'Documentación Oficial y Manual Técnico — Lex Junior Browser v2.8.5',
    'faq': 'Preguntas y Respuestas (FAQ) — Lex Junior Browser v2.8.5',
    'novedades': 'Novedades y Changelog v2.8.5 — Lex Junior Browser'
  };

  const normalizeRoute = (rawHash) => {
    const clean = (rawHash || '').replace(/^#\/?/, '').trim().toLowerCase();
    if (!clean || clean === 'inicio' || clean === 'preview') return 'inicio';
    if (clean === 'funciones' || clean === 'features' || clean === 'telemetry') return 'funciones';
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
      title: 'Lex Junior Class — Disponible en la próxima actualización',
      url: 'https://class.lex.local/index.html',
      ads: 'Próxima actualización'
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
});
