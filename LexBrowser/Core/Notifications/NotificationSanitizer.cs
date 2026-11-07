using System;
using System.Text;
using System.Text.RegularExpressions;

namespace LexBrowser.Core.Notifications;

/// <summary>
/// Sanitizador de seguridad para títulos y cuerpos de notificaciones web.
/// Neutraliza inyecciones HTML, scripts, caracteres de control y suplantaciones por inversión de texto (bidi).
/// 0.0% CPU en reposo: solo se ejecuta al recibir una notificación.
/// </summary>
public static class NotificationSanitizer
{
    private static readonly Regex ScriptTagRegex = new(@"<script[^>]*>[\s\S]*?</script>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex StyleTagRegex = new(@"<style[^>]*>[\s\S]*?</style>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HtmlTagRegex = new(@"<[^>]*>", RegexOptions.Compiled);

    /// <summary>
    /// Limpia un texto limitando su tamaño y neutralizando caracteres peligrosos.
    /// </summary>
    public static string Sanitize(string? input, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        // 1. Quitar etiquetas <script> y <style> junto con su contenido
        string text = ScriptTagRegex.Replace(input, string.Empty);
        text = StyleTagRegex.Replace(text, string.Empty);

        // 2. Quitar el resto de etiquetas HTML
        text = HtmlTagRegex.Replace(text, string.Empty);

        // 3. Limpiar caracteres de control y secuencias bidi
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            // Omitir caracteres de control (excepto espacio normal)
            if (char.IsControl(c) && c != ' ') continue;

            // Omitir caracteres bidi (inversión de texto y marcas direccionales)
            if (IsBidiOrFormatChar(c)) continue;

            sb.Append(c);
        }

        string result = sb.ToString().Trim();
        if (result.Length > maxLength && maxLength > 0)
        {
            result = result.Substring(0, maxLength).TrimEnd() + "…";
        }

        return result;
    }

    private static bool IsBidiOrFormatChar(char c)
    {
        return c switch
        {
            '\u202A' => true, // LRE
            '\u202B' => true, // RLE
            '\u202C' => true, // PDF
            '\u202D' => true, // LRO
            '\u202E' => true, // RLO
            '\u2066' => true, // LRI
            '\u2067' => true, // RLI
            '\u2068' => true, // FSI
            '\u2069' => true, // PDI
            '\u200E' => true, // LRM
            '\u200F' => true, // RLM
            '\u061C' => true, // ALM
            _ => char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format
        };
    }
}
