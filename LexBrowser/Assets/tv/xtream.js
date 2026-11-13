(() => {
  'use strict';

  const els = {
    form: document.getElementById('formXtream'),
    name: document.getElementById('xcNameInput'),
    server: document.getElementById('xcServerInput'),
    httpAlert: document.getElementById('xcHttpAlert'),
    confirmHttp: document.getElementById('chkConfirmHttp'),
    user: document.getElementById('xcUserInput'),
    pass: document.getElementById('xcPassInput'),
    saveCreds: document.getElementById('chkXcSaveCreds'),
    syncVod: document.getElementById('chkXcSyncVod'),
    legalAuth: document.getElementById('chkLegalAuth'),
    btnConnect: document.getElementById('btnConnect'),
    formError: document.getElementById('formError'),
    formInfo: document.getElementById('formInfo'),
    connectionsList: document.getElementById('connectionsList')
  };

  async function apiJson(url, opts = {}) {
    const res = await fetch(url, opts);
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      throw new Error(data.error || `Error ${res.status}: ${res.statusText}`);
    }
    return data;
  }

  function escapeHtml(str) {
    if (!str) return '';
    return String(str)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;');
  }

  function formatExpDate(exp) {
    if (!exp) return '';
    try {
      const num = parseInt(exp, 10);
      if (!isNaN(num) && num > 1000000) {
        const d = new Date(num * 1000);
        return d.toLocaleDateString();
      }
    } catch (_) {}
    return String(exp);
  }

  async function loadConnections() {
    try {
      const data = await apiJson('/api/xtream/connections');
      const list = data.connections || [];
      renderConnections(list);
    } catch (err) {
      els.connectionsList.innerHTML = `
        <div class="empty-state">
          <p class="form-error">No se pudieron cargar las conexiones: ${escapeHtml(err.message)}</p>
        </div>`;
    }
  }

  function renderConnections(list) {
    if (!list || list.length === 0) {
      els.connectionsList.innerHTML = `
        <div class="empty-state">
          <div class="empty-icon">📡</div>
          <p class="bold-white">No tienes servidores Xtream Codes conectados.</p>
          <p class="muted">Usa el formulario de la izquierda para conectar tu primer servidor personal.</p>
        </div>`;
      return;
    }

    let html = '';
    for (const c of list) {
      let hostDisplay = c.serverUrl || '';
      try {
        const u = new URL(c.serverUrl);
        hostDisplay = u.protocol + '//' + u.host;
      } catch (_) {}

      const exp = formatExpDate(c.expirationDate);
      const expBadge = exp ? `<span class="badge badge-exp">Vence: ${escapeHtml(exp)}</span>` : '';
      const statusBadge = c.serverStatus ? `<span class="badge badge-status">${escapeHtml(c.serverStatus)}</span>` : '';

      html += `
        <div class="conn-item" data-id="${escapeHtml(c.id)}">
          <div class="conn-header">
            <div>
              <h3 class="conn-title">${escapeHtml(c.name || 'Servidor Xtream')}</h3>
              <p class="conn-host mono">${escapeHtml(hostDisplay)}</p>
            </div>
            <div class="conn-badges">
              ${statusBadge}
              ${expBadge}
            </div>
          </div>

          <div class="conn-stats">
            <span class="stat-pill">📺 ${c.liveCount || 0} canales en vivo</span>
            <span class="stat-pill">🎬 ${c.vodCount || 0} películas</span>
            <span class="stat-pill">🎞️ ${c.seriesCount || 0} series</span>
          </div>

          <div class="conn-actions">
            <a href="tv.html" class="btn-xs btn-accent">📺 Ver TV en vivo</a>
            <a href="vod.html" class="btn-xs btn-secondary">🎬 Ver Cine VOD</a>
            <button type="button" class="btn-xs btn-danger btn-delete-conn" data-id="${escapeHtml(c.id)}" data-name="${escapeHtml(c.name || 'este servidor')}">
              🗑 Eliminar
            </button>
          </div>
        </div>`;
    }

    els.connectionsList.innerHTML = html;

    // Listeners para botones eliminar
    els.connectionsList.querySelectorAll('.btn-delete-conn').forEach(btn => {
      btn.addEventListener('click', async () => {
        const id = btn.getAttribute('data-id');
        const name = btn.getAttribute('data-name');
        if (!confirm(`¿Seguro que deseas eliminar la conexión "${name}" y todos sus canales y contenidos VOD asociados?`)) {
          return;
        }

        btn.disabled = true;
        btn.textContent = 'Eliminando…';
        try {
          await apiJson('/api/xtream/remove', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ id })
          });
          await loadConnections();
        } catch (err) {
          alert('Error al eliminar: ' + err.message);
          btn.disabled = false;
          btn.textContent = '🗑 Eliminar';
        }
      });
    });
  }

  function checkHttpWarning() {
    const val = (els.server.value || '').trim().toLowerCase();
    if (val.startsWith('http://') || (val.length > 0 && !val.startsWith('https://'))) {
      els.httpAlert.classList.remove('hidden');
    } else {
      els.httpAlert.classList.add('hidden');
    }
  }

  els.server.addEventListener('input', checkHttpWarning);

  els.btnConnect.addEventListener('click', async () => {
    els.formError.classList.add('hidden');
    els.formInfo.classList.add('hidden');

    const name = (els.name.value || '').trim();
    const serverUrl = (els.server.value || '').trim();
    const username = (els.user.value || '').trim();
    const password = (els.pass.value || '').trim();
    const saveCredentials = !!els.saveCreds.checked;
    const syncVod = !!els.syncVod.checked;

    if (!serverUrl || !username || !password) {
      els.formError.textContent = 'Por favor completa la URL del servidor, usuario y contraseña.';
      els.formError.classList.remove('hidden');
      return;
    }

    const isHttp = serverUrl.toLowerCase().startsWith('http://') || !serverUrl.toLowerCase().startsWith('https://');
    if (isHttp && !els.confirmHttp.checked) {
      els.formError.textContent = 'Debes aceptar la advertencia de conexión HTTP no cifrada antes de continuar.';
      els.formError.classList.remove('hidden');
      return;
    }

    if (!els.legalAuth.checked) {
      els.formError.textContent = 'Debes confirmar que cuentas con autorización para utilizar este servidor.';
      els.formError.classList.remove('hidden');
      return;
    }

    els.btnConnect.disabled = true;
    els.formInfo.textContent = 'Conectando con el servidor Xtream Codes e importando contenidos…';
    els.formInfo.classList.remove('hidden');

    try {
      const res = await apiJson('/api/xtream/connect', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          name,
          serverUrl,
          username,
          password,
          saveCredentials,
          syncVod,
          confirmedAuthorized: true
        })
      });

      els.formInfo.textContent = `¡Conexión exitosa! Sincronizados ${res.liveCount || 0} canales en vivo y ${res.vodCount || 0} películas / ${res.seriesCount || 0} series a VOD.`;
      els.pass.value = '';
      els.legalAuth.checked = false;
      await loadConnections();
    } catch (err) {
      els.formInfo.classList.add('hidden');
      els.formError.textContent = err.message || 'Error al conectar con el servidor Xtream Codes.';
      els.formError.classList.remove('hidden');
    } finally {
      els.btnConnect.disabled = false;
    }
  });

  // Inicializar
  loadConnections();
})();

