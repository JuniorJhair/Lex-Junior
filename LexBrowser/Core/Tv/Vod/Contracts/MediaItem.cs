using System;
using System.Collections.Generic;

namespace LexBrowser.Core.Tv.Vod.Contracts;

public sealed class WatchProviderInfo
{
    public string Name { get; set; } = "";
    public string? LogoUrl { get; set; }
    public int ProviderId { get; set; }
    public string? OfficialUrl { get; set; }
    public string Type { get; set; } = "flatrate"; // "flatrate" (suscripción) | "rent" | "buy"
}

public sealed class AnimeMetaInfo
{
    public string? JapaneseTitle { get; set; }
    public string? RomajiTitle { get; set; }
    public int? TotalEpisodes { get; set; }
    public string? Studio { get; set; }
    public string? SeasonYear { get; set; }
    public string? Format { get; set; } // "TV" | "MOVIE" | "OVA"
}

public sealed class MediaEpisode
{
    public string Id { get; set; } = "";
    public int EpisodeNumber { get; set; }
    public int SeasonNumber { get; set; } = 1;
    public string Title { get; set; } = "";
    public string? Synopsis { get; set; }
    public int RuntimeMin { get; set; }
    public string? ThumbnailUrl { get; set; }
}

public sealed class MediaSeason
{
    public int SeasonNumber { get; set; }
    public string Title { get; set; } = "";
    public List<MediaEpisode> Episodes { get; set; } = new();
}

/// <summary>
/// Modelo universal de ítem multimedia (Película, Serie o Anime) para la capa de catálogo.
/// Desacoplado de la procedencia del archivo de video.
/// </summary>
public sealed class MediaItem
{
    public string Id { get; set; } = ""; // Prefijos estándar: "tmdb:123", "anilist:456", "cine:abc"
    public string Type { get; set; } = "movie"; // "movie" | "series" | "anime"
    public string Kind { get => Type; set => Type = value; }
    public string Title { get; set; } = "";
    public string? OriginalTitle { get; set; }
    public int? Year { get; set; }
    public int RuntimeMin { get; set; }
    public List<string> Genres { get; set; } = new();
    public string? Synopsis { get; set; }
    public string? PosterUrl { get; set; }
    public string? BackdropUrl { get; set; }
    public double? Rating { get; set; } // 0.0 - 10.0
    public string? License { get; set; }
    public string? SourceName { get; set; }

    /// <summary>Dónde verla oficialmente en Perú (región PE) según TMDB / JustWatch.</summary>
    public List<WatchProviderInfo> WatchProviders { get; set; } = new();

    /// <summary>Enlace oficial para abrir en pestaña normal de LexBrowser.</summary>
    public string? OfficialWatchUrl { get; set; }

    /// <summary>Atribución legal requerida (TMDB, JustWatch, etc.).</summary>
    public string? Attribution { get; set; }

    /// <summary>Metadatos específicos para Anime.</summary>
    public AnimeMetaInfo? AnimeMeta { get; set; }

    /// <summary>Temporadas y episodios si es serie o anime por capítulos.</summary>
    public List<MediaSeason> Seasons { get; set; } = new();
}
