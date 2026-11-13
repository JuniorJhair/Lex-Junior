using System;
using System.Collections.Generic;

namespace LexBrowser.Core.Tv.Cine;

public sealed class TvEpisodeInfo
{
    public string Id { get; set; } = "";
    public int EpisodeNumber { get; set; }
    public string Title { get; set; } = "";
    public string MediaUrl { get; set; } = "";
    public int DurationSec { get; set; }
    public string PosterUrl { get; set; } = "";
}

public sealed class TvSeasonInfo
{
    public int SeasonNumber { get; set; }
    public List<TvEpisodeInfo> Episodes { get; set; } = new();
}

public sealed class TvSeriesInfo
{
    public string Name { get; set; } = "";
    public List<TvSeasonInfo> Seasons { get; set; } = new();
}

public sealed class TvTitle
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "movie"; // "movie" | "series"
    public string Title { get; set; } = "";
    public int? Year { get; set; }
    public List<string> Genres { get; set; } = new();
    public List<string> Languages { get; set; } = new();
    public List<string> Countries { get; set; } = new();
    public int RuntimeMin { get; set; }
    public string Synopsis { get; set; } = "";
    public string PosterUrl { get; set; } = "";
    public string TmdbId { get; set; } = "";
    public string ImdbId { get; set; } = "";
    public string License { get; set; } = "";
    public string SourceName { get; set; } = "";
    public string SourcePageUrl { get; set; } = "";
    public string MediaUrl { get; set; } = "";
    public string MediaKind { get; set; } = "mp4"; // "mp4" | "webm" | "ogv" | "m3u8"
    public TvSeriesInfo? Series { get; set; }
}

public sealed class TvCineIndexItem
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "movie";
    public string Title { get; set; } = "";
    public string TitleNormalized { get; set; } = "";
    public int? Year { get; set; }
    public int RuntimeMin { get; set; }
    public string Group { get; set; } = "";
    public string PosterUrl { get; set; } = "";
    public string[] Genres { get; set; } = Array.Empty<string>();
    public string[] Languages { get; set; } = Array.Empty<string>();
    public string SourceName { get; set; } = "";
    public string License { get; set; } = "";
    public string MediaUrl { get; set; } = "";
    public string MediaKind { get; set; } = "mp4";
    public string? SeriesName { get; set; }
}

public sealed class TvCineDiscardStats
{
    public int TotalProcessed { get; set; }
    public int TotalAccepted { get; set; }
    public int SkippedNonHttps { get; set; }
    public int SkippedDisallowedHost { get; set; }
    public int SkippedInvalidUrl { get; set; }
    public int SkippedNoTitle { get; set; }
    public int SkippedLiveStream { get; set; }
}

