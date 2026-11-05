using System;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace LexBrowser.Core;

/// <summary>
/// Servicio de traducción de páginas web bajo demanda.
/// 0.0% consumo de CPU y 0 MB de RAM en reposo: no ejecuta ningún proceso ni consume
/// recursos hasta que el usuario hace clic en el botón de traducir.
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
    /// Activa o desactiva la traducción in-place de la página web al idioma de destino.
    /// Devuelve true si la página quedó traducida, o false si se restauró el original.
    /// </summary>
    public static async Task<bool> ToggleInPageTranslateAsync(CoreWebView2? core, string targetLang = "es")
    {
        if (core == null) return false;
        try
        {
            string script = $@"
(function(tl) {{
    try {{
        if (window.__lexIsTranslated) {{
            document.cookie = 'googtrans=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/;';
            document.cookie = 'googtrans=; expires=Thu, 01 Jan 1970 00:00:00 UTC; domain=' + location.hostname + '; path=/;';
            window.__lexIsTranslated = false;
            location.reload();
            return false;
        }}
        document.cookie = 'googtrans=/auto/' + tl + '; path=/;';
        document.cookie = 'googtrans=/auto/' + tl + '; domain=' + location.hostname + '; path=/;';

        if (!document.getElementById('lex_gt_script')) {{
            var el = document.createElement('div');
            el.id = 'google_translate_element';
            el.style.display = 'none';
            document.body.appendChild(el);

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
            document.head.appendChild(s);
        }} else {{
            location.reload();
        }}
        window.__lexIsTranslated = true;
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
    /// Genera la URL de traducción directa oficial en caso de páginas con restricciones CSP estrictas.
    /// </summary>
    public static string GetGoogleTranslateProxyUrl(string originalUrl, string targetLang = "es")
    {
        if (string.IsNullOrWhiteSpace(originalUrl)) return string.Empty;
        return $"https://translate.google.com/translate?sl=auto&tl={targetLang}&u={Uri.EscapeDataString(originalUrl)}";
    }
}

