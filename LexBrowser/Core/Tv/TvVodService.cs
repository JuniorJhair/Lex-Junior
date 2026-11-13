using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LexBrowser.Core.Tv;

public sealed class TvVodImportResult
{
    public int AddedCount { get; set; }
    public int TotalInFile { get; set; }
    public int SkippedUnsupported { get; set; }
    public int SkippedPrivate { get; set; }
    public int SkippedInvalid { get; set; }
    public int SkippedDuplicate { get; set; }
}

public sealed class TvVodService
{
    private static readonly Lazy<HttpClient> SharedHttp =
        new(() => TvUrlGuard.CreatePinnedHttpClient(timeout: TimeSpan.FromSeconds(45)));

    public static TvVodService Shared { get; } = new();

    public async Task<TvVodImportResult> ImportFromUrlAsync(string url, string defaultGroup, CancellationToken ct, TvVodStore? store = null)
    {
        if (TvDenylist.IsDenylisted(url, out var denyReason))
            throw new InvalidOperationException(denyReason ?? "Esta fuente fue deshabilitada tras una reclamación.");

        if (!TvUrlGuard.IsAllowedRemoteUrl(url, out var uri) || uri == null)
            throw new InvalidOperationException("URL inválida. Solo se aceptan enlaces públicos http:// o https://.");

        var (resp, finalUri) = await TvUrlGuard.GetWithSafeRedirectsAsync(SharedHttp.Value, uri, null, ct).ConfigureAwait(false);
        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"El servidor respondió {(int)resp.StatusCode} {resp.ReasonPhrase}.");

            string text = await TvUrlGuard.ReadBoundedUtf8Async(resp, TvUrlGuard.MaxPlaylistBytes, ct).ConfigureAwait(false);
            return ImportFromText(text, defaultGroup, finalUri.AbsoluteUri, store);
        }
    }

    public TvVodImportResult ImportFromText(string text, string defaultGroup, string? baseUri = null, TvVodStore? store = null)
    {
        var targetStore = store ?? TvVodStore.Shared;
        var parsed = M3uParser.ParseDetailed(text, defaultGroup, baseUri);
        var vodItems = new List<TvVodItem>();

        foreach (var ch in parsed.Channels)
        {
            if (string.IsNullOrWhiteSpace(ch.Url)) continue;

            string group = string.IsNullOrWhiteSpace(ch.Group) ? (defaultGroup ?? "Películas") : ch.Group;
            var (isSeries, seriesTitle, season, episode, epTitle) = TvVodStore.DetectSeriesInfo(ch.Name, group);

            vodItems.Add(new TvVodItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = ch.Name,
                Url = ch.Url,
                Group = group,
                Logo = ch.Logo,
                IsSeries = isSeries,
                SeriesTitle = seriesTitle,
                SeasonNumber = season,
                EpisodeNumber = episode,
                EpisodeTitle = epTitle,
                AddedUtc = DateTime.UtcNow
            });
        }

        int added = targetStore.ImportItems(vodItems);

        return new TvVodImportResult
        {
            AddedCount = added,
            TotalInFile = parsed.Channels.Count,
            SkippedUnsupported = parsed.SkippedUnsupportedScheme,
            SkippedPrivate = parsed.SkippedPrivate,
            SkippedInvalid = parsed.SkippedInvalid,
            SkippedDuplicate = parsed.Channels.Count - added
        };
    }
}

