using System;
using System.Collections.Generic;
using System.Linq;

namespace LexBrowser.Core;

/// <summary>
/// Lógica pura (sin UI) del mini-reproductor: decide qué URL carga la ventana flotante
/// para continuar la reproducción desde el segundo actual de la pestaña origen.
/// </summary>
public static class MiniPlayerUrl
{
    /// <summary>True si la URL es un video de YouTube reproducible (watch, shorts, live, youtu.be).</summary>
    public static bool IsYouTubeVideo(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        string host = uri.Host.ToLowerInvariant();
        if (host == "youtu.be") return uri.AbsolutePath.Length > 1;
        if (host != "youtube.com" && !host.EndsWith(".youtube.com")) return false;
        if (host == "music.youtube.com") return false;

        string path = uri.AbsolutePath;
        return path == "/watch"
            || path.StartsWith("/shorts/", StringComparison.Ordinal)
            || path.StartsWith("/live/", StringComparison.Ordinal);
    }

    public static bool IsShort(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.AbsolutePath.StartsWith("/shorts/", StringComparison.Ordinal);

    /// <summary>
    /// Devuelve la URL a cargar. En YouTube /watch y youtu.be añade/actualiza el parámetro t=Ns
    /// para reanudar donde iba; en el resto devuelve la URL original sin tocar.
    /// </summary>
    public static string Build(string url, double currentSeconds)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        if (!IsYouTubeVideo(url)) return url;
        if (IsShort(url)) return url; // Shorts no admiten t=
        if (double.IsNaN(currentSeconds) || currentSeconds < 1) return url;

        int seconds = (int)Math.Floor(currentSeconds);

        var pairs = new List<string>();
        string query = uri.Query.TrimStart('?');
        if (query.Length > 0)
        {
            pairs.AddRange(query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(p => !p.StartsWith("t=", StringComparison.OrdinalIgnoreCase) &&
                            !p.StartsWith("start=", StringComparison.OrdinalIgnoreCase)));
        }
        pairs.Add($"t={seconds}s");

        var builder = new UriBuilder(uri) { Query = string.Join("&", pairs) };
        return builder.Uri.AbsoluteUri;
    }
}

