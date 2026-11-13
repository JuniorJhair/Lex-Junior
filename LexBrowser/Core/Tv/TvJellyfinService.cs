using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LexBrowser.Core.Tv;

public sealed class TvJellyfinConfig
{
    public string ServerUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string UserId { get; set; } = "";
    public bool Enabled { get; set; }
}

public sealed class TvJellyfinService
{
    private static readonly Lazy<HttpClient> SharedHttp = new(() => new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(15)
    });

    public static TvJellyfinService Shared { get; } = new();

    public async Task<bool> TestConnectionAsync(string serverUrl, string apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)) return false;
        if (!Uri.TryCreate(serverUrl.Trim().TrimEnd('/'), UriKind.Absolute, out var baseUri))
            return false;

        string target = $"{baseUri.AbsoluteUri.TrimEnd('/')}/System/Info/Public";
        using var req = new HttpRequestMessage(HttpMethod.Get, target);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            req.Headers.TryAddWithoutValidation("X-Emby-Token", apiKey.Trim());
        }

        try
        {
            using var resp = await SharedHttp.Value.SendAsync(req, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<TvVodItem>> FetchItemsAsync(string serverUrl, string apiKey, string? userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)) return new List<TvVodItem>();
        if (!Uri.TryCreate(serverUrl.Trim().TrimEnd('/'), UriKind.Absolute, out var baseUri))
            return new List<TvVodItem>();

        string cleanBase = baseUri.AbsoluteUri.TrimEnd('/');
        string endpoint = string.IsNullOrWhiteSpace(userId)
            ? $"{cleanBase}/Items?Recursive=true&IncludeItemTypes=Movie,Episode&Fields=Overview,Path"
            : $"{cleanBase}/Users/{userId.Trim()}/Items?Recursive=true&IncludeItemTypes=Movie,Episode&Fields=Overview,Path";

        using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            req.Headers.TryAddWithoutValidation("X-Emby-Token", apiKey.Trim());
        }

        using var resp = await SharedHttp.Value.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Jellyfin respondió {(int)resp.StatusCode} {resp.ReasonPhrase}.");

        string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseJellyfinItems(json, cleanBase, apiKey);
    }

    public static List<TvVodItem> ParseJellyfinItems(string json, string serverBaseUrl, string? apiKey)
    {
        var items = new List<TvVodItem>();
        if (string.IsNullOrWhiteSpace(json)) return items;

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("Items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array)
            return items;

        string tokenQuery = string.IsNullOrWhiteSpace(apiKey) ? "" : $"?api_key={apiKey.Trim()}";

        foreach (var el in itemsEl.EnumerateArray())
        {
            string id = el.TryGetProperty("Id", out var idEl) ? (idEl.GetString() ?? "") : "";
            string name = el.TryGetProperty("Name", out var nEl) ? (nEl.GetString() ?? "") : "";
            string type = el.TryGetProperty("Type", out var tEl) ? (tEl.GetString() ?? "") : "";

            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;

            string streamUrl = $"{serverBaseUrl}/Videos/{id}/stream.mp4{tokenQuery}";
            string logoUrl = $"{serverBaseUrl}/Items/{id}/Images/Primary{tokenQuery}";

            bool isEpisode = string.Equals(type, "Episode", StringComparison.OrdinalIgnoreCase);
            string? seriesName = null;
            int? season = null;
            int? episode = null;

            if (isEpisode)
            {
                if (el.TryGetProperty("SeriesName", out var snEl)) seriesName = snEl.GetString();
                if (el.TryGetProperty("ParentIndexNumber", out var sNumEl) && sNumEl.TryGetInt32(out int sVal)) season = sVal;
                if (el.TryGetProperty("IndexNumber", out var eNumEl) && eNumEl.TryGetInt32(out int eVal)) episode = eVal;
            }

            items.Add(new TvVodItem
            {
                Id = $"jellyfin_{id}",
                Title = isEpisode && !string.IsNullOrWhiteSpace(seriesName) ? $"{seriesName} S{season ?? 1:D2}E{episode ?? 1:D2} - {name}" : name,
                Url = streamUrl,
                Group = isEpisode ? (seriesName ?? "Series") : "Películas",
                Logo = logoUrl,
                IsSeries = isEpisode,
                SeriesTitle = seriesName,
                SeasonNumber = season,
                EpisodeNumber = episode,
                EpisodeTitle = isEpisode ? name : null,
                AddedUtc = DateTime.UtcNow
            });
        }

        return items;
    }
}

