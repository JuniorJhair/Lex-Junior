using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LexBrowser.Core.Tv.Cine;
using LexBrowser.Core.Tv.Vod.Contracts;

namespace LexBrowser.Core.Tv.Vod.Providers;

/// <summary>
/// Proveedor oficial integrado que expone las 906 películas y series de dominio público
/// y licencias libres de legalFree (Wikimedia Commons e Internet Archive) a través del contrato IVodProvider.
/// </summary>
public sealed class LegalFreeVodProvider : IVodProvider
{
    public VodManifest Manifest { get; } = new()
    {
        Id = "org.lex.legalfree",
        Name = "Cine Libre y Dominio Público (Oficial)",
        Version = "1.0.0",
        Description = "Películas clásicas, documentales y animación libre de Wikimedia Commons e Internet Archive.",
        Resources = new List<string> { "catalog", "meta", "stream" },
        Types = new List<string> { "movie", "series" },
        IsBuiltIn = true,
        Catalogs = new List<VodCatalogDefinition>
        {
            new() { Type = "movie", Id = "all_movies", Name = "Todas las Películas" },
            new() { Type = "series", Id = "all_series", Name = "Todas las Series" }
        }
    };

    public async Task<List<MediaItem>> GetCatalogAsync(string type, string? catalogId, string? genre, int page, CancellationToken ct)
    {
        var cat = await TvCineService.Shared.GetCatalogAsync(false, ct).ConfigureAwait(false);
        var query = new TvCineFilterQuery
        {
            Kind = type,
            Genre = genre
        };

        var filtered = TvCineService.FilterIndex(cat.Index, query);

        int pageSize = 40;
        int skip = (Math.Max(1, page) - 1) * pageSize;
        var paged = filtered.Skip(skip).Take(pageSize).ToList();

        return paged.Select(i => new MediaItem
        {
            Id = $"legal:{i.Id}",
            Type = i.Kind,
            Title = i.Title,
            Year = i.Year,
            RuntimeMin = i.RuntimeMin,
            Genres = i.Genres.ToList(),
            PosterUrl = i.PosterUrl,
            License = i.License,
            SourceName = i.SourceName
        }).ToList();
    }

    public async Task<MediaItem?> GetDetailAsync(string type, string id, CancellationToken ct)
    {
        string rawId = id.StartsWith("legal:", StringComparison.OrdinalIgnoreCase)
            ? id.Substring(6)
            : id;

        var title = await TvCineService.Shared.GetTitleDetailAsync(rawId, ct).ConfigureAwait(false);
        if (title == null) return null;

        var seasons = new List<MediaSeason>();
        if (title.Series != null && title.Series.Seasons != null)
        {
            foreach (var s in title.Series.Seasons)
            {
                seasons.Add(new MediaSeason
                {
                    SeasonNumber = s.SeasonNumber,
                    Title = $"Temporada {s.SeasonNumber}",
                    Episodes = s.Episodes.Select(e => new MediaEpisode
                    {
                        Id = e.Id,
                        EpisodeNumber = e.EpisodeNumber,
                        SeasonNumber = s.SeasonNumber,
                        Title = e.Title,
                        RuntimeMin = e.DurationSec / 60,
                        ThumbnailUrl = e.PosterUrl
                    }).ToList()
                });
            }
        }

        return new MediaItem
        {
            Id = $"legal:{title.Id}",
            Type = title.Kind,
            Title = title.Title,
            Year = title.Year,
            RuntimeMin = title.RuntimeMin,
            Genres = title.Genres,
            Synopsis = title.Synopsis,
            PosterUrl = title.PosterUrl,
            License = title.License,
            SourceName = title.SourceName,
            OfficialWatchUrl = title.SourcePageUrl,
            Seasons = seasons
        };
    }

    public async Task<List<StreamSource>> GetStreamsAsync(string type, string id, CancellationToken ct)
    {
        string rawId = id.StartsWith("legal:", StringComparison.OrdinalIgnoreCase)
            ? id.Substring(6)
            : id;

        var title = await TvCineService.Shared.GetTitleDetailAsync(rawId, ct).ConfigureAwait(false);
        if (title == null || string.IsNullOrEmpty(title.MediaUrl)) return new List<StreamSource>();

        return new List<StreamSource>
        {
            new()
            {
                ProviderId = Manifest.Id,
                Title = $"{title.MediaKind.ToUpperInvariant()} - Servidor Oficial ({title.SourceName})",
                Url = title.MediaUrl,
                Quality = "1080p",
                Format = title.MediaKind,
                RequiresProxy = false
            }
        };
    }
}

