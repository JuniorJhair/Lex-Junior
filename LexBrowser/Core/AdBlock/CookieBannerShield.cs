using System;
using System.Collections.Generic;
using System.Text.Json;

namespace LexBrowser.Core.AdBlock;

/// <summary>
/// Oculta avisos y banners de cookies mediante CSS inmediato y dos comprobaciones puntuales tras DOMContentLoaded.
/// Respeta exclusiones sensibles (bancos, pagos, OAuth, Google/YouTube consent) y política por sitio (Off/Compat/módulo).
/// Nunca pulsa Aceptar ni Rechazar de forma automática ni escribe cookies de consentimiento.
/// </summary>
public static class CookieBannerShield
{
    public static string InjectedScript => GetInjectedScript();

    public static string GetInjectedScript()
        => GetInjectedScript(SettingsManager.Current?.Shield?.Sites);

    public static string GetInjectedScript(IDictionary<string, SiteShieldProfile>? sitePolicies)
    {
        string sitesJson = "{}";
        try
        {
            if (sitePolicies != null && sitePolicies.Count > 0)
            {
                sitesJson = JsonSerializer.Serialize(sitePolicies);
            }
        }
        catch { }

        string paymentHostsJson = "[]";
        try
        {
            paymentHostsJson = JsonSerializer.Serialize(ShieldExemptions.PaymentOrAuthHosts);
        }
        catch { }

        return BaseScript
            .Replace("const __LEX_SITE_POLICIES = {};", $"const __LEX_SITE_POLICIES = {sitesJson};")
            .Replace("const __LEX_PAYMENT_AUTH_HOSTS = [];", $"const __LEX_PAYMENT_AUTH_HOSTS = {paymentHostsJson};");
    }

    public const string BaseScript = @"
(function() {
    if (window.top !== window.self) return;
    if (window.__lex_cookie_banner_shield_injected) return;
    window.__lex_cookie_banner_shield_injected = true;

    const __LEX_SITE_POLICIES = {};
    const __LEX_PAYMENT_AUTH_HOSTS = [];

    const hostname = (window.location.hostname || '').toLowerCase();
    if (!hostname || hostname === 'pdf.lex.local' || hostname === 'tv.lex.local') return;

    const ends = (d) => hostname === d || hostname.endsWith('.' + d);

    if (hostname === 'accounts.google.com' || hostname.startsWith('accounts.google.') ||
        hostname === 'consent.google.com' || hostname.startsWith('consent.google.') ||
        hostname === 'consent.youtube.com' || hostname.startsWith('consent.youtube.') ||
        ends('youtube.com') || ends('youtu.be') || ends('youtube-nocookie.com')) {
        return;
    }

    if (__LEX_PAYMENT_AUTH_HOSTS && __LEX_PAYMENT_AUTH_HOSTS.some(ends)) return;
    if (/(\.|^)(bank|banca|bbva|bcp|interbank|scotiabank|banbif|bancodelanacion)\b/i.test(hostname)) return;

    let siteMode = 'Normal';
    let siteDisabled = [];
    let bestMatchLen = -1;
    try {
        for (const [domain, prof] of Object.entries(__LEX_SITE_POLICIES || {})) {
            const d = (domain || '').toLowerCase();
            if (d && ends(d) && prof && d.length > bestMatchLen) {
                bestMatchLen = d.length;
                siteMode = prof.Mode || 'Normal';
                siteDisabled = Array.isArray(prof.Disabled) ? prof.Disabled.map(x => String(x).toLowerCase()) : [];
            }
        }
    } catch(_) {}

    const modeLower = String(siteMode || 'Normal').toLowerCase();
    if (modeLower === 'off' || modeLower === 'compat') return;
    if (siteDisabled.indexOf('cookiebanners') !== -1) return;

    const CMP_SELECTORS = [
        '#onetrust-consent-sdk',
        '#onetrust-banner-sdk',
        '.onetrust-pc-dark-filter',
        '#CybotCookiebotDialog',
        '#CybotCookiebotDialogBodyUnderlay',
        '#didomi-host',
        '.qc-cmp2-container',
        '#truste-consent-track',
        '#usercentrics-root',
        '.osano-cm-window',
        '.cky-consent-container',
        '.cky-overlay',
        '#cmplz-cookiebanner-container',
        '#cookie-notice',
        '#iubenda-cs-banner',
        '#BorlabsCookieBox',
        '#axeptio_overlay',
        'div[id^=\'sp_message_container\']',
        '.fc-consent-root',
        '.fc-dialog-overlay',
        '#cookiescript_injected'
    ];
    const CMP_CSS_SELECTOR = CMP_SELECTORS.join(', ');

    try {
        const style = document.createElement('style');
        style.id = '__lex_cookie_hide';
        style.textContent = CMP_CSS_SELECTOR + ' { display: none !important; visibility: hidden !important; pointer-events: none !important; }';

        const attachCookieStyle = () => {
            const target = document.head || document.documentElement;
            if (!target) return false;
            if (!document.getElementById('__lex_cookie_hide')) target.appendChild(style);
            return true;
        };

        if (!attachCookieStyle()) {
            const waitStyle = new MutationObserver(() => {
                if (attachCookieStyle()) waitStyle.disconnect();
            });
            waitStyle.observe(document, { childList: true });
        }
    } catch(_) {}

    const reportCookieHidden = () => {
        try {
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage({
                    type: 'ad_blocked',
                    category: 'cosmetic',
                    url: window.location.href,
                    reason: 'cookie_banner_hidden'
                });
            }
        } catch(_) {}
    };

    const unlockScrollIfLocked = () => {
        try {
            const html = document.documentElement;
            const body = document.body;
            if (!body) return;
            if (window.getComputedStyle(body).overflow === 'hidden') {
                body.style.setProperty('overflow', 'auto', 'important');
                body.style.setProperty('overflow-y', 'auto', 'important');
            }
            if (html && window.getComputedStyle(html).overflow === 'hidden') {
                html.style.setProperty('overflow', 'auto', 'important');
                html.style.setProperty('overflow-y', 'auto', 'important');
            }
        } catch(_) {}
    };

    const runCookieBannerPass = () => {
        try {
            let hidAny = false;

            const knownNodes = document.querySelectorAll(CMP_CSS_SELECTOR);
            for (let i = 0; i < knownNodes.length; i++) {
                const el = knownNodes[i];
                if (!el) continue;
                hidAny = true;
                if (!el.getAttribute('data-lex-cookie-hidden')) {
                    el.setAttribute('data-lex-cookie-hidden', '1');
                    reportCookieHidden();
                }
            }

            if (hidAny) {
                unlockScrollIfLocked();
            }

            const vh = window.innerHeight || 0;
            const vw = window.innerWidth || 0;
            if (vh > 0 && vw > 0) {
                const genericQuery =
                    'body > div, body > section, body > aside, body > footer, dialog, ' +
                    'body > div > div, body > div > section, body > div > aside, ' +
                    '[role=\'dialog\'], [class*=\'cookie\'], [id*=\'cookie\'], ' +
                    '[class*=\'consent\'], [id*=\'consent\'], [class*=\'gdpr\'], [id*=\'gdpr\']';
                const candidates = document.querySelectorAll(genericQuery);
                const cookieKeywordRe = /\b(cookie|cookies|consent|consentimiento)\b/i;

                for (let i = 0; i < candidates.length; i++) {
                    const el = candidates[i];
                    if (!el || el.nodeType !== 1) continue;
                    if (el.getAttribute('data-lex-cookie-hidden')) continue;

                    const tag = el.tagName;
                    if (tag === 'HTML' || tag === 'BODY' || tag === 'MAIN' || tag === 'HEADER' || tag === 'NAV' ||
                        tag === 'SCRIPT' || tag === 'STYLE' || tag === 'NOSCRIPT') {
                        continue;
                    }

                    const cs = window.getComputedStyle(el);
                    if (!cs || cs.display === 'none' || cs.visibility === 'hidden') continue;
                    if (cs.position !== 'fixed' && cs.position !== 'sticky') continue;

                    const rect = el.getBoundingClientRect();
                    if (!rect || rect.width < 150 || rect.height < 36) continue;

                    const nearEdge = rect.top <= 40 || (vh - rect.bottom) <= 40 || rect.left <= 40 || (vw - rect.right) <= 40;
                    if (!nearEdge) continue;

                    const text = (el.innerText || el.textContent || '').trim();
                    if (!text || text.length > 2800 || !cookieKeywordRe.test(text)) continue;

                    if (el.querySelector('video, canvas, main')) continue;
                    if (el.querySelector('form input[type=\'password\']') ||
                        (tag === 'FORM' && el.querySelector('input[type=\'password\']'))) {
                        continue;
                    }

                    el.style.setProperty('display', 'none', 'important');
                    el.setAttribute('data-lex-cookie-hidden', '1');
                    hidAny = true;
                    reportCookieHidden();
                }

                if (hidAny) {
                    unlockScrollIfLocked();
                }
            }
        } catch(_) {}
    };

    const schedulePasses = () => {
        setTimeout(runCookieBannerPass, 800);
        setTimeout(runCookieBannerPass, 2500);
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', schedulePasses, { once: true });
    } else {
        schedulePasses();
    }
})();
";
}

