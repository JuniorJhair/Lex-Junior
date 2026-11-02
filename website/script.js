// ═══════════════════════════════════════════════════════════════════════════
// LEX JUNIOR BROWSER — OFFICIAL WEBSITE INTERACTIVE LOGIC
// ═══════════════════════════════════════════════════════════════════════════

document.addEventListener('DOMContentLoaded', () => {
  // 1. Animaciones al hacer scroll (IntersectionObserver)
  const revealItems = document.querySelectorAll('.reveal');
  const observer = new IntersectionObserver(
    (entries) => {
      entries.forEach((entry) => {
        if (entry.isIntersecting) {
          entry.target.classList.add('visible');
          observer.unobserve(entry.target);
        }
      });
    },
    { threshold: 0.12 }
  );
  revealItems.forEach((el) => observer.observe(el));

  // 2. Menú móvil adaptable
  const mobileBtn = document.getElementById('mobile-menu-btn');
  const navLinks = document.getElementById('nav-links');
  if (mobileBtn && navLinks) {
    mobileBtn.addEventListener('click', () => {
      navLinks.classList.toggle('open');
    });
    navLinks.querySelectorAll('a').forEach((link) => {
      link.addEventListener('click', () => navLinks.classList.remove('open'));
    });
  }

  // 3. Reloj en vivo dentro de la vista "Nueva Pestaña"
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

  // 4. Selector interactivo de las 5 vistas del navegador
  const viewMeta = {
    'view-newtab': {
      icon: '⚡',
      title: 'Nueva pestaña',
      url: 'lex://nueva-pestana',
      ads: '0 bloqueados'
    },
    'view-youtube': {
      icon: '▶',
      title: 'YouTube — Concierto en Vivo 4K',
      url: 'https://www.youtube.com/watch?v=LexJunior4K',
      ads: '34 bloqueados'
    },
    'view-sniffer': {
      icon: '📥',
      title: 'Streaming HD — Episodio 5',
      url: 'https://streaming-hd.example/episodio-05',
      ads: '29 bloqueados'
    },
    'view-pip': {
      icon: '🗖',
      title: 'Transmisión en Vivo · Página Flotante',
      url: 'https://twitch.tv/lexjunior_live',
      ads: '18 bloqueados'
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

  // Botones dentro de la barra del navegador simulado también cambian la vista
  const mockBtnPip = document.getElementById('mock-btn-pip');
  const mockBtnVol = document.getElementById('mock-btn-vol');
  const mockBtnMedia = document.getElementById('mock-btn-media');
  if (mockBtnPip) mockBtnPip.addEventListener('click', () => activateView('view-pip'));
  if (mockBtnVol) mockBtnVol.addEventListener('click', () => activateView('view-youtube'));
  if (mockBtnMedia) mockBtnMedia.addEventListener('click', () => activateView('view-sniffer'));

  // 5. Slider interactivo del Amplificador de Volumen 300%
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

  // 6. Sincronización automática con la última Release de GitHub
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
          metaLabel.textContent = `Instalador Oficial (${version} · ${sizeMb} MB) · Gratis`;
        }
      }
    })
    .catch(() => {
      // Silencioso si el usuario navega sin conexión
    });
});

