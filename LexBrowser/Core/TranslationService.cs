using System;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace LexBrowser.Core;

/// <summary>
/// Servicio de traducción de páginas web bajo demanda.
/// 0.0% consumo de CPU y 0 MB de RAM en reposo: no ejecuta ningún proceso ni consume
/// recursos hasta que el usuario hace clic en el botón de traducir.
/// Oculta completamente los banners invasivos y menús de tablas deformadas de Google Translate.
/// </summary>
public static class TranslationService
{
    /// <summary>
    /// Detecta el código de idioma principal de la página HTML actual (ej. "en", "ja", "fr", "es").
    /// </summary>
    public static async Task<string> DetectPageLanguageAsync(CoreWebView2? core)
    {
        if (core == null) return string.Empty;
        try
        {
            string json = await core.ExecuteScriptAsync(
                @"(function() {
                    var l = (document.documentElement.lang || document.querySelector('meta[http-equiv=""content-language""]')?.content || '').toLowerCase().trim();
                    if (l.length >= 2) return l.substring(0, 2);
                    return '';
                })()");
            if (!string.IsNullOrWhiteSpace(json) && json != "null" && json != "\"\"")
            {
                return json.Trim('"', ' ');
            }
        }
        catch { }
        return string.Empty;
    }

    /// <summary>
    /// Traduce la página web al idioma de destino especificado sin banners antiestéticos.
    /// Inyecta reglas de CSS estrictas para ocultar .goog-te-banner-frame y .goog-te-menu-frame.
    /// </summary>
    public static async Task<bool> TranslatePageAsync(CoreWebView2? core, string targetLang = "es")
    {
        if (core == null) return false;
        try
        {
            string script = $@"
(function(tl) {{
    try {{
        // 1. Configurar cookie de traducción de Google
        document.cookie = 'googtrans=/auto/' + tl + '; path=/;';
        document.cookie = 'googtrans=/auto/' + tl + '; domain=' + location.hostname + '; path=/;';

        // 2. Inyectar CSS que neutraliza el banner superior y los menús flotantes desproporcionados
        var style = document.getElementById('lex_gt_clean_style');
        if (!style) {{
            style = document.createElement('style');
            style.id = 'lex_gt_clean_style';
            style.innerHTML = '.goog-te-banner-frame, .goog-te-menu-frame, #goog-gt-tt, .skiptranslate, #goog-gt-vt {{ display: none !important; }} body {{ top: 0px !important; position: static !important; }}';
            (document.head || document.documentElement).appendChild(style);
        }}

        // 3. Inicializar TranslateElement
        if (!document.getElementById('lex_gt_script')) {{
            var el = document.getElementById('google_translate_element');
            if (!el) {{
                el = document.createElement('div');
                el.id = 'google_translate_element';
                el.style.display = 'none';
                document.body.appendChild(el);
            }}

            window.googleTranslateElementInit = function() {{
                try {{
                    new google.translate.TranslateElement({{
                        pageLanguage: 'auto',
                        autoDisplay: false
                    }}, 'google_translate_element');
                }} catch(_) {{}}
            }};

            var s = document.createElement('script');
            s.id = 'lex_gt_script';
            s.src = 'https://translate.google.com/translate_a/element.js?cb=googleTranslateElementInit';
            (document.head || document.documentElement).appendChild(s);
        }} else {{
            location.reload();
        }}
        window.__lexIsTranslated = true;
        window.__lexCurrentLang = tl;
        return true;
    }} catch(_) {{
        return false;
    }}
}})('{targetLang}');";
            string result = await core.ExecuteScriptAsync(script);
            return result == "true";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Restaura la página web a su idioma original limpiando cookies y recargando limpiamente.
    /// </summary>
    public static async Task<bool> RestoreOriginalAsync(CoreWebView2? core)
    {
        if (core == null) return false;
        try
        {
            string script = @"
(function() {
    try {
        document.cookie = 'googtrans=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/;';
        document.cookie = 'googtrans=; expires=Thu, 01 Jan 1970 00:00:00 UTC; domain=' + location.hostname + '; path=/;';
        window.__lexIsTranslated = false;
        window.__lexCurrentLang = '';
        location.reload();
        return true;
    } catch(_) {
        return false;
    }
})()";
            string result = await core.ExecuteScriptAsync(script);
            return result == "true";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Indica si la página actual se encuentra traducida.
    /// </summary>
    public static async Task<bool> IsPageTranslatedAsync(CoreWebView2? core)
    {
        if (core == null) return false;
        try
        {
            string script = @"(function() { return (window.__lexIsTranslated === true || document.cookie.indexOf('googtrans=/auto/') !== -1); })()";
            string res = await core.ExecuteScriptAsync(script);
            return res == "true";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Activa o desactiva la traducción in-place de la página web (compatibilidad hacia atrás).
    /// </summary>
    public static async Task<bool> ToggleInPageTranslateAsync(CoreWebView2? core, string targetLang = "es")
    {
        if (core == null) return false;
        bool isTranslated = await IsPageTranslatedAsync(core);
        if (isTranslated)
        {
            await RestoreOriginalAsync(core);
            return false;
        }
        else
        {
            return await TranslatePageAsync(core, targetLang);
        }
    }

    /// <summary>
    /// Genera la URL de traducción directa oficial en caso de páginas con restricciones CSP estrictas.
    /// </summary>
    public static string GetGoogleTranslateProxyUrl(string originalUrl, string targetLang = "es")
    {
        if (string.IsNullOrWhiteSpace(originalUrl)) return string.Empty;
        return $"https://translate.google.com/translate?sl=auto&tl={targetLang}&u={Uri.EscapeDataString(originalUrl)}";
    }
}
