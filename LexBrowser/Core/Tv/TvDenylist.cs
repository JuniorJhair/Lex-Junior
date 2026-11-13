using System;
using System.Collections.Generic;

namespace LexBrowser.Core.Tv;

/// <summary>
/// Lista de denegación de fuentes deshabilitadas tras reclamaciones fundadas de derechos de autor.
/// Vacía por defecto; se actualiza con las versiones de la aplicación.
/// </summary>
public static class TvDenylist
{
    private static readonly HashSet<string> DeniedHosts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> DeniedUrls = new(StringComparer.OrdinalIgnoreCase);

    public const string DefaultDenyMessage = "Esta fuente fue deshabilitada tras una reclamación.";

    public static bool IsDenylisted(string? url, out string? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(url)) return false;

        string trimmed = url.Trim();
        if (DeniedUrls.Contains(trimmed))
        {
            reason = DefaultDenyMessage;
            return true;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            if (DeniedHosts.Contains(uri.Host))
            {
                reason = DefaultDenyMessage;
                return true;
            }
        }

        return false;
    }

    public static void AddForTesting(string hostOrUrl)
    {
        if (string.IsNullOrWhiteSpace(hostOrUrl)) return;
        string t = hostOrUrl.Trim();
        if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            DeniedUrls.Add(t);
            if (Uri.TryCreate(t, UriKind.Absolute, out var u))
                DeniedHosts.Add(u.Host);
        }
        else
        {
            DeniedHosts.Add(t);
        }
    }

    public static void ClearForTesting()
    {
        DeniedHosts.Clear();
        DeniedUrls.Clear();
    }
}
