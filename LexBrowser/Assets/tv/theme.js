'use strict';

/**
 * Lex TV — Gestor de Temas Visuales
 * Implementación de nivel Senior:
 * - Detección y aplicación inmediata sin parpadeo (Zero-FOUC).
 * - Sincronización multi-pestaña mediante 'storage' event.
 * - Soporte para:
 *    - 'dark': Tema Oscuro (Medianoche / Slate) — Predeterminado
 *    - 'light': Tema Claro (Luz / White & Slate)
 *    - 'classic': Diseño Original (OLED Negro Puro & Magma Orange)
 * - Persistencia segura en localStorage ('lex_tv_theme').
 */
(function() {
  const THEME_STORAGE_KEY = 'lex_tv_theme';
  const VALID_THEMES = ['dark', 'light', 'classic'];
  const DEFAULT_THEME = 'dark';

  function getSavedTheme() {
    try {
      const saved = localStorage.getItem(THEME_STORAGE_KEY);
      if (saved && VALID_THEMES.includes(saved)) {
        return saved;
      }
    } catch (_) { }
    return DEFAULT_THEME;
  }

  function applyTheme(theme, save = true) {
    const targetTheme = VALID_THEMES.includes(theme) ? theme : DEFAULT_THEME;
    document.documentElement.setAttribute('data-theme', targetTheme);

    if (save) {
      try {
        localStorage.setItem(THEME_STORAGE_KEY, targetTheme);
      } catch (_) { }
    }

    // Actualizar todos los selectores de tema presentes en la página
    const selectors = document.querySelectorAll('.theme-selector, #themeSelect');
    selectors.forEach(sel => {
      if (sel.value !== targetTheme) {
        sel.value = targetTheme;
      }
    });
  }

  // 1. Aplicación inmediata (síncrona al cargar el script en <head> para evitar FOUC)
  applyTheme(getSavedTheme(), false);

  // 2. Sincronización entre ventanas y pestañas
  window.addEventListener('storage', (event) => {
    if (event.key === THEME_STORAGE_KEY && event.newValue) {
      applyTheme(event.newValue, false);
    }
  });

  // 3. Inicialización y enlace de eventos a selectores
  function initThemeControls() {
    const currentTheme = getSavedTheme();
    const selectors = document.querySelectorAll('.theme-selector, #themeSelect');
    selectors.forEach(sel => {
      sel.value = currentTheme;
      if (!sel.dataset.themeBound) {
        sel.dataset.themeBound = 'true';
        sel.addEventListener('change', (e) => {
          applyTheme(e.target.value, true);
        });
      }
    });
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', initThemeControls);
  } else {
    initThemeControls();
  }

  window.LexTheme = {
    get: getSavedTheme,
    set: applyTheme,
    init: initThemeControls,
    THEMES: VALID_THEMES
  };
})();

