(() => {
  'use strict';

  const els = {
    form: document.getElementById('addonInstallForm'),
    urlInput: document.getElementById('addonUrlInput'),
    chkLocal: document.getElementById('chkAllowLocal'),
    btnPreview: document.getElementById('btnPreviewAddon'),
    btnInstall: document.getElementById('btnInstallAddon'),
    btnReload: document.getElementById('btnReloadList'),
    btnCheckUpdates: document.getElementById('btnCheckAllUpdates'),
    statusMsg: document.getElementById('addonStatusMsg'),
    previewBox: document.getElementById('addonPreviewBox'),
    pvName: document.getElementById('pvName'),
    pvMeta: document.getElementById('pvMeta'),
    pvProtocol: document.getElementById('pvProtocol'),
    pvDesc: document.getElementById('pvDesc'),
    pvCaps: document.getElementById('pvCaps'),
    addonsCountSub: document.getElementById('addonsCountSub'),
    addonsEmpty: document.getElementById('addonsEmpty'),
    addonsList: document.getElementById('addonsList'),
    repoForm: document.getElementById('repoAddForm'),
    repoUrlInput: document.getElementById('repoUrlInput'),
    repoChkLocal: document.getElementById('chkRepoAllowLocal'),
    btnAddRepo: document.getElementById('btnAddRepo'),
    repoStatusMsg: document.getElementById('repoStatusMsg'),
    repoEmpty: document.getElementById('repoEmpty'),
    repoList: document.getElementById('repoList'),
    repoExplorerBox: document.getElementById('repoExplorerBox'),
    repoExplorerTitle: document.getElementById('repoExplorerTitle'),
    repoExplorerSub: document.getElementById('repoExplorerSub'),
    repoEntriesList: document.getElementById('repoEntriesList'),
    btnCloseRepoExplorer: document.getElementById('btnCloseRepoExplorer')
  };

  let activeRepoMeta = null;

  function setBoxStatus(box, text, isError) {
    if (!box) return;
    if (!text) {
      box.className = 'addon-status hidden';
      box.textContent = '';
      return;
    }
    box.textContent = text;
    box.className = `addon-status ${isError ? 'err' : 'ok'}`;
  }

  function setStatus(text, isError) {
    setBoxStatus(els.statusMsg, text, isError);
  }

  function setRepoStatus(text, isError) {
    setBoxStatus(els.repoStatusMsg, text, isError);
  }

  function normalizeStremioSchemeInput(raw) {
    const trimmed = String(raw || '').trim();
    if (!/^stremio:\/\//i.test(trimmed)) return trimmed;
    const rest = trimmed.slice('stremio://'.length).trimStart();
    if (/^https?:\/\//i.test(rest)) return rest;
    if (/^(localhost|127\.0\.0\.1|\[::1\])(?:[:/?#]|$)/i.test(rest)) return `http://${rest}`;
    return `https://${rest}`;
  }

  function bindStremioInputNormalizer(inputEl) {
    if (!inputEl) return;
    const apply = () => {
      const normalized = normalizeStremioSchemeInput(inputEl.value);
      if (normalized !== inputEl.value) {
        inputEl.value = normalized;
      }
    };
    inputEl.addEventListener('paste', () => setTimeout(apply, 0));
    inputEl.addEventListener('blur', apply);
  }

  function makeBadge(text, extraClass = '') {
    const span = document.createElement('span');
    span.className = extraClass ? `addon-badge ${extraClass}` : 'addon-badge';
    span.textContent = text;
    return span;
  }

  async function apiPost(url, payload) {
    const resp = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload || {})
    });
    const data = await resp.json().catch(() => ({}));
    return { ok: resp.ok && data.ok !== false, status: resp.status, data };
  }

  function renderPreview(manifest, displayUrl) {
    if (!manifest) {
      els.previewBox.classList.add('hidden');
      return;
    }

    els.pvName.textContent = `${manifest.name} (v${manifest.version || '1.0.0'})`;
    els.pvMeta.textContent = `${manifest.id} · ${displayUrl || ''}`;
    els.pvProtocol.textContent = manifest.protocol || 'stremio-v1';
    els.pvDesc.textContent = manifest.description || 'Sin descripción declarada en el manifiesto.';

    els.pvCaps.replaceChildren();
    (manifest.types || []).forEach((t) => els.pvCaps.appendChild(makeBadge(`tipo:${t}`)));
    (manifest.resources || []).forEach((r) => {
      const rName = typeof r === 'string' ? r : r.name;
      if (rName) els.pvCaps.appendChild(makeBadge(`recurso:${rName}`));
    });
    if (Array.isArray(manifest.catalogs) && manifest.catalogs.length > 0) {
      els.pvCaps.appendChild(makeBadge(`${manifest.catalogs.length} catálogo(s)`));
    }
    if (manifest.behaviorHints?.configurationRequired) {
      els.pvCaps.appendChild(makeBadge('Requiere configuración previa', 'local'));
    }

    els.previewBox.classList.remove('hidden');
  }

  function createLogoNode(addon) {
    if (addon.logo) {
      const img = document.createElement('img');
      img.className = 'addon-logo';
      img.alt = addon.name || 'Logo';
      img.loading = 'lazy';
      img.src = `/api/image?u=${encodeURIComponent(addon.logo)}`;
      img.addEventListener('error', () => {
        const fb = document.createElement('div');
        fb.className = 'addon-logo-fallback';
        fb.textContent = '🧩';
        img.replaceWith(fb);
      });
      return img;
    }
    const fb = document.createElement('div');
    fb.className = 'addon-logo-fallback';
    fb.textContent = '🧩';
    return fb;
  }

  function renderList(addons) {
    const list = Array.isArray(addons) ? addons : [];
    els.addonsCountSub.textContent = `${list.length} complemento(s) instalado(s)`;
    els.addonsList.replaceChildren();
    els.addonsEmpty.classList.toggle('hidden', list.length > 0);

    for (const addon of list) {
      const card = document.createElement('article');
      card.className = addon.enabled ? 'addon-item' : 'addon-item disabled';

      const top = document.createElement('div');
      top.className = 'addon-item-top';

      const idWrap = document.createElement('div');
      idWrap.className = 'addon-item-idwrap';
      idWrap.appendChild(createLogoNode(addon));

      const meta = document.createElement('div');
      const title = document.createElement('div');
      title.className = 'addon-item-title';
      title.textContent = `${addon.name} v${addon.version || '1.0.0'}`;

      const sub = document.createElement('div');
      sub.className = 'addon-item-sub mono';
      sub.textContent = `${addon.id} · ${addon.displayUrl || ''}`;

      meta.appendChild(title);
      meta.appendChild(sub);
      idWrap.appendChild(meta);

      const badges = document.createElement('div');
      badges.className = 'addon-pills';
      badges.appendChild(makeBadge(addon.protocol || 'stremio-v1'));
      if (addon.allowLocalLoopback) badges.appendChild(makeBadge('Localhost', 'local'));
      if (addon.configurationRequired) badges.appendChild(makeBadge('Requiere config.', 'local'));
      else if (addon.configurable) badges.appendChild(makeBadge('Configurable'));
      if (!addon.enabled) badges.appendChild(makeBadge('Desactivado', 'off'));

      top.appendChild(idWrap);
      top.appendChild(badges);
      card.appendChild(top);

      if (addon.description) {
        const desc = document.createElement('div');
        desc.className = 'addon-item-sub';
        desc.textContent = addon.description;
        card.appendChild(desc);
      }

      const caps = document.createElement('div');
      caps.className = 'addon-pills';
      (addon.types || []).forEach((t) => caps.appendChild(makeBadge(t)));
      (addon.resourceNames || []).forEach((r) => caps.appendChild(makeBadge(r)));
      if ((addon.catalogCount || 0) > 0) {
        caps.appendChild(makeBadge(`${addon.catalogCount} catálogo(s)`));
      }
      card.appendChild(caps);

      if (addon.lastError) {
        const err = document.createElement('div');
        err.className = 'addon-item-err';
        err.textContent = `Último aviso: ${addon.lastError}`;
        card.appendChild(err);
      }

      const actions = document.createElement('div');
      actions.className = 'addon-item-actions';

      const btnToggle = document.createElement('button');
      btnToggle.type = 'button';
      btnToggle.className = 'btn btn-sm';
      btnToggle.textContent = addon.enabled ? 'Desactivar' : 'Activar';
      btnToggle.addEventListener('click', async () => {
        const res = await apiPost('/api/addons/toggle', { id: addon.id, enabled: !addon.enabled });
        if (!res.ok) {
          setStatus(res.data?.error || 'No se pudo cambiar el estado del addon.', true);
          return;
        }
        renderList(res.data.addons);
      });

      const btnRefresh = document.createElement('button');
      btnRefresh.type = 'button';
      btnRefresh.className = 'btn btn-sm';
      btnRefresh.textContent = 'Actualizar manifiesto';
      btnRefresh.addEventListener('click', async () => {
        btnRefresh.disabled = true;
        const res = await apiPost('/api/addons/refresh', { id: addon.id });
        btnRefresh.disabled = false;
        if (!res.ok) {
          setStatus(res.data?.error || 'Error al comprobar actualización del addon.', true);
          if (res.data?.addons) renderList(res.data.addons);
          return;
        }
        setStatus(`Manifiesto de ${addon.name} actualizado correctamente.`, false);
        renderList(res.data.addons);
      });

      const btnRemove = document.createElement('button');
      btnRemove.type = 'button';
      btnRemove.className = 'btn btn-sm btn-danger';
      btnRemove.textContent = 'Desinstalar';
      btnRemove.addEventListener('click', async () => {
        const res = await apiPost('/api/addons/remove', { id: addon.id });
        if (!res.ok) {
          setStatus(res.data?.error || 'No se pudo desinstalar el addon.', true);
          return;
        }
        setStatus(`Addon ${addon.name} desinstalado.`, false);
        renderList(res.data.addons);
      });

      actions.appendChild(btnToggle);
      actions.appendChild(btnRefresh);
      const catalogs = Array.isArray(addon.catalogs) ? addon.catalogs : [];
      const hasLiveCatalog = catalogs.some((c) => c.type === 'tv' || c.type === 'channel');
      const hasVodCatalog = catalogs.some((c) => c.type !== 'tv' && c.type !== 'channel');

      if (addon.enabled && hasVodCatalog) {
        const exploreLink = document.createElement('a');
        exploreLink.href = 'vod.html#addons';
        exploreLink.className = 'btn btn-sm btn-primary';
        exploreLink.textContent = 'Explorar catálogos →';
        actions.appendChild(exploreLink);
      }
      if (addon.enabled && hasLiveCatalog) {
        const liveLink = document.createElement('a');
        liveLink.href = 'tv.html';
        liveLink.className = 'btn btn-sm btn-primary';
        liveLink.textContent = 'Ver TV en vivo →';
        actions.appendChild(liveLink);
      }
      actions.appendChild(btnRemove);
      card.appendChild(actions);

      els.addonsList.appendChild(card);
    }
  }

  async function loadAddons() {
    try {
      const r = await fetch('/api/addons');
      const data = await r.json();
      renderList(data.addons || []);
    } catch {
      setStatus('No se pudo cargar la lista de addons.', true);
    }
  }

  async function setBusy(busy) {
    els.btnPreview.disabled = busy;
    els.btnInstall.disabled = busy;
  }

  els.btnPreview?.addEventListener('click', async () => {
    const manifestUrl = normalizeStremioSchemeInput(els.urlInput.value);
    if (els.urlInput) els.urlInput.value = manifestUrl;
    if (!manifestUrl) {
      setStatus('Ingresa la URL del manifest.json primero.', true);
      return;
    }

    setBusy(true);
    setStatus('Consultando y validando manifiesto...', false);
    const res = await apiPost('/api/addons/preview', {
      manifestUrl,
      allowLocalLoopback: !!els.chkLocal.checked
    });
    setBusy(false);

    if (!res.ok) {
      renderPreview(null);
      setStatus(res.data?.error || 'No se pudo validar el manifiesto.', true);
      return;
    }

    setStatus('Manifiesto válido. Puedes proceder con la instalación.', false);
    renderPreview(res.data.manifest, res.data.displayUrl);
  });

  els.form?.addEventListener('submit', async (ev) => {
    ev.preventDefault();
    const manifestUrl = normalizeStremioSchemeInput(els.urlInput.value);
    if (els.urlInput) els.urlInput.value = manifestUrl;
    if (!manifestUrl) {
      setStatus('Ingresa la URL del manifest.json primero.', true);
      return;
    }

    setBusy(true);
    setStatus('Instalando addon...', false);
    const res = await apiPost('/api/addons/install', {
      manifestUrl,
      allowLocalLoopback: !!els.chkLocal.checked
    });
    setBusy(false);

    if (!res.ok) {
      setStatus(res.data?.error || 'No se pudo instalar el addon.', true);
      return;
    }

    setStatus(
      res.data.updated
        ? `Addon "${res.data.addon.name}" actualizado (v${res.data.addon.version}).`
        : `Addon "${res.data.addon.name}" instalado correctamente.`,
      false
    );
    els.urlInput.value = '';
    renderPreview(null);
    renderList(res.data.addons || []);
  });

  els.btnReload?.addEventListener('click', loadAddons);

  els.btnCheckUpdates?.addEventListener('click', async () => {
    if (els.btnCheckUpdates) els.btnCheckUpdates.disabled = true;
    setStatus('Comprobando actualizaciones de todos los addons instalados...', false);
    const res = await apiPost('/api/addons/refresh-all', {});
    if (els.btnCheckUpdates) els.btnCheckUpdates.disabled = false;

    if (!res.ok) {
      setStatus(res.data?.error || 'Error al comprobar actualizaciones.', true);
      return;
    }

    const d = res.data || {};
    renderList(d.addons || []);
    const summary = `Comprobados: ${d.totalChecked || 0} · Actualizados: ${d.updatedCount || 0} · Al día: ${d.upToDateCount || 0}` +
      ((d.errorCount || 0) > 0 ? ` · Avisos: ${d.errorCount}` : '');
    setStatus(summary, (d.errorCount || 0) > 0 && (d.updatedCount || 0) === 0);
  });

  function renderRepositories(repos) {
    if (!els.repoList) return;
    const list = Array.isArray(repos) ? repos : [];
    els.repoList.replaceChildren();
    if (els.repoEmpty) els.repoEmpty.classList.toggle('hidden', list.length > 0);

    for (const repo of list) {
      const card = document.createElement('article');
      card.className = 'addon-item';

      const top = document.createElement('div');
      top.className = 'addon-item-top';

      const info = document.createElement('div');
      const title = document.createElement('div');
      title.className = 'addon-item-title';
      title.textContent = repo.name || 'Repositorio';

      const sub = document.createElement('div');
      sub.className = 'addon-item-sub mono';
      sub.textContent = `${repo.addonCount || 0} addon(s) · ${repo.displayUrl || ''}`;

      info.appendChild(title);
      info.appendChild(sub);
      top.appendChild(info);

      if (repo.allowLocalLoopback) {
        top.appendChild(makeBadge('Localhost', 'local'));
      }
      card.appendChild(top);

      if (repo.description) {
        const desc = document.createElement('div');
        desc.className = 'addon-item-sub';
        desc.textContent = repo.description;
        card.appendChild(desc);
      }

      if (repo.lastError) {
        const err = document.createElement('div');
        err.className = 'addon-item-err';
        err.textContent = `Último aviso: ${repo.lastError}`;
        card.appendChild(err);
      }

      const actions = document.createElement('div');
      actions.className = 'addon-item-actions';

      const btnInspect = document.createElement('button');
      btnInspect.type = 'button';
      btnInspect.className = 'btn btn-sm btn-primary';
      btnInspect.textContent = 'Explorar';
      btnInspect.addEventListener('click', () => inspectRepository(repo.id));

      const btnRemove = document.createElement('button');
      btnRemove.type = 'button';
      btnRemove.className = 'btn btn-sm btn-danger';
      btnRemove.textContent = 'Eliminar';
      btnRemove.addEventListener('click', async () => {
        const r = await apiPost('/api/addons/repos/remove', { id: repo.id });
        if (!r.ok) {
          setRepoStatus(r.data?.error || 'No se pudo eliminar el repositorio.', true);
          return;
        }
        renderRepositories(r.data.repositories || []);
        if (activeRepoMeta && activeRepoMeta.id === repo.id) {
          els.repoExplorerBox?.classList.add('hidden');
          activeRepoMeta = null;
        }
        setRepoStatus(`Repositorio "${repo.name}" eliminado.`, false);
      });

      actions.appendChild(btnInspect);
      actions.appendChild(btnRemove);
      card.appendChild(actions);
      els.repoList.appendChild(card);
    }
  }

  function renderRepoExplorer(repo, entries) {
    if (!els.repoExplorerBox || !els.repoEntriesList) return;
    activeRepoMeta = repo;
    els.repoExplorerTitle.textContent = repo?.name || 'Contenido del repositorio';
    els.repoExplorerSub.textContent = `${(entries || []).length} complemento(s) disponibles`;
    els.repoEntriesList.replaceChildren();

    for (const entry of (entries || [])) {
      const card = document.createElement('article');
      card.className = 'addon-item';

      const top = document.createElement('div');
      top.className = 'addon-item-top';

      const idWrap = document.createElement('div');
      idWrap.className = 'addon-item-idwrap';
      idWrap.appendChild(createLogoNode({ name: entry.name, logo: entry.logo }));

      const meta = document.createElement('div');
      const title = document.createElement('div');
      title.className = 'addon-item-title';
      title.textContent = `${entry.name} v${entry.version || '1.0.0'}`;

      const sub = document.createElement('div');
      sub.className = 'addon-item-sub mono';
      sub.textContent = entry.displayUrl || '';

      meta.appendChild(title);
      meta.appendChild(sub);
      idWrap.appendChild(meta);
      top.appendChild(idWrap);

      const badges = document.createElement('div');
      badges.className = 'addon-pills';
      if (entry.updateAvailable) {
        badges.appendChild(makeBadge(`Actualización (${entry.installedVersion} → ${entry.version})`, 'update'));
      } else if (entry.installed) {
        badges.appendChild(makeBadge(`Instalado v${entry.installedVersion || entry.version}`));
      }
      top.appendChild(badges);
      card.appendChild(top);

      if (entry.description) {
        const desc = document.createElement('div');
        desc.className = 'addon-item-sub';
        desc.textContent = entry.description;
        card.appendChild(desc);
      }

      if ((entry.types || []).length > 0 || (entry.resourceNames || []).length > 0) {
        const pills = document.createElement('div');
        pills.className = 'addon-pills';
        (entry.types || []).forEach((t) => pills.appendChild(makeBadge(t)));
        (entry.resourceNames || []).forEach((r) => pills.appendChild(makeBadge(r)));
        card.appendChild(pills);
      }

      const actions = document.createElement('div');
      actions.className = 'addon-item-actions';

      const btnInstallEntry = document.createElement('button');
      btnInstallEntry.type = 'button';
      btnInstallEntry.className = 'btn btn-sm btn-primary';
      btnInstallEntry.textContent = entry.updateAvailable
        ? `Actualizar a v${entry.version}`
        : (entry.installed ? 'Reinstalar' : 'Instalar');

      btnInstallEntry.addEventListener('click', async () => {
        btnInstallEntry.disabled = true;
        setRepoStatus(`Instalando "${entry.name}"...`, false);
        const installRes = await apiPost('/api/addons/install', {
          manifestUrl: entry.manifestUrl,
          allowLocalLoopback: Boolean(repo && repo.allowLocalLoopback)
        });
        btnInstallEntry.disabled = false;

        if (!installRes.ok) {
          setRepoStatus(installRes.data?.error || `No se pudo instalar "${entry.name}".`, true);
          return;
        }

        renderList(installRes.data.addons || []);
        setRepoStatus(`Addon "${installRes.data.addon.name}" v${installRes.data.addon.version} listo.`, false);
        if (repo && repo.id) {
          await inspectRepository(repo.id);
        }
      });

      actions.appendChild(btnInstallEntry);
      card.appendChild(actions);
      els.repoEntriesList.appendChild(card);
    }

    els.repoExplorerBox.classList.remove('hidden');
  }

  async function loadRepositories() {
    try {
      const r = await fetch('/api/addons/repos');
      const data = await r.json();
      renderRepositories(data.repositories || []);
    } catch { }
  }

  async function inspectRepository(repoId) {
    setRepoStatus('Consultando catálogo del repositorio...', false);
    const res = await apiPost('/api/addons/repos/inspect', { id: repoId });
    if (!res.ok) {
      if (res.data?.repositories) renderRepositories(res.data.repositories);
      setRepoStatus(res.data?.error || 'No se pudo explorar el repositorio.', true);
      return;
    }

    setRepoStatus('', false);
    renderRepositories(res.data.repositories || []);
    renderRepoExplorer(res.data.repository, res.data.entries || []);
  }

  els.repoForm?.addEventListener('submit', async (ev) => {
    ev.preventDefault();
    const repoUrl = normalizeStremioSchemeInput(els.repoUrlInput?.value || '');
    if (els.repoUrlInput) els.repoUrlInput.value = repoUrl;
    if (!repoUrl) {
      setRepoStatus('Ingresa la URL de un repositorio JSON.', true);
      return;
    }

    if (els.btnAddRepo) els.btnAddRepo.disabled = true;
    setRepoStatus('Validando y descargando repositorio...', false);
    const res = await apiPost('/api/addons/repos/add', {
      repoUrl,
      allowLocalLoopback: !!els.repoChkLocal?.checked
    });
    if (els.btnAddRepo) els.btnAddRepo.disabled = false;

    if (!res.ok) {
      setRepoStatus(res.data?.error || 'No se pudo cargar el repositorio.', true);
      return;
    }

    if (els.repoUrlInput) els.repoUrlInput.value = '';
    setRepoStatus(`Repositorio "${res.data.repository?.name}" guardado correctamente.`, false);
    renderRepositories(res.data.repositories || []);
    renderRepoExplorer(res.data.repository, res.data.entries || []);
  });

  els.btnCloseRepoExplorer?.addEventListener('click', () => {
    els.repoExplorerBox?.classList.add('hidden');
    activeRepoMeta = null;
  });

  bindStremioInputNormalizer(els.urlInput);
  bindStremioInputNormalizer(els.repoUrlInput);
  loadAddons();
  loadRepositories();
})();

