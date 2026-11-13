(function () {
  'use strict';

  const termsOverlay = document.getElementById('termsOverlay');
  const chkAcceptTerms = document.getElementById('chkAcceptTerms');
  const btnAcceptTerms = document.getElementById('btnAcceptTerms');
  const cardIptv = document.getElementById('cardIptv');
  const cardVod = document.getElementById('cardVod');

  let hasAcceptedTerms = false;

  async function checkTermsStatus() {
    try {
      const resp = await fetch('/api/terms/status');
      if (resp.ok) {
        const data = await resp.json();
        hasAcceptedTerms = Boolean(data.accepted);
        if (!hasAcceptedTerms && termsOverlay) {
          termsOverlay.classList.remove('hidden');
        }
      }
    } catch {
      // Si la API falla o está desconectada, mantenemos el modal visible por seguridad
      if (termsOverlay) termsOverlay.classList.remove('hidden');
    }
  }

  if (chkAcceptTerms && btnAcceptTerms) {
    chkAcceptTerms.addEventListener('change', () => {
      btnAcceptTerms.disabled = !chkAcceptTerms.checked;
    });

    btnAcceptTerms.addEventListener('click', async () => {
      if (!chkAcceptTerms.checked) return;
      btnAcceptTerms.disabled = true;
      btnAcceptTerms.textContent = 'Guardando…';

      try {
        const resp = await fetch('/api/terms/accept', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ version: 1 })
        });

        if (resp.ok) {
          hasAcceptedTerms = true;
          if (termsOverlay) termsOverlay.classList.add('hidden');
        } else {
          alert('No se pudo guardar la aceptación de términos. Inténtalo de nuevo.');
          btnAcceptTerms.disabled = false;
          btnAcceptTerms.textContent = 'Comenzar';
        }
      } catch {
        alert('Error de conexión al guardar los términos.');
        btnAcceptTerms.disabled = false;
        btnAcceptTerms.textContent = 'Comenzar';
      }
    });
  }

  function handleCardClick(e, targetUrl) {
    if (!hasAcceptedTerms) {
      e.preventDefault();
      if (termsOverlay) termsOverlay.classList.remove('hidden');
      return;
    }
    window.location.href = targetUrl;
  }

  if (cardIptv) {
    cardIptv.addEventListener('click', (e) => handleCardClick(e, 'tv.html'));
  }

  if (cardVod) {
    cardVod.addEventListener('click', (e) => handleCardClick(e, 'vod.html'));
  }

  checkTermsStatus();
})();
