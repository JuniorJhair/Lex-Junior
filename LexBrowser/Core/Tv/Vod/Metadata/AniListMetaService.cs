using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LexBrowser.Core.Tv.Vod.Contracts;

namespace LexBrowser.Core.Tv.Vod.Metadata;

/// <summary>
/// Proveedor de metadatos de Anime consumiendo la API GraphQL pública y gratuita de AniList.
/// No requiere clave de usuario y opera bajo demanda con caché en disco de 7 días.
/// </summary>
public sealed class AniListMetaService : IMetaService
{
    private static readonly Lazy<AniListMetaService> LazyInstance = new(() => new AniListMetaService());
    public static AniListMetaService Shared => LazyInstance.Value;

    public const string GraphQLUrl = "https://graphql.anilist.co";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);
    private readonly string _cachePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, (DateTime UpdatedUtc, string Json)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private bool _cacheLoaded;

    public string ServiceId => "anilist";
    public string ServiceName => "AniList Anime Database";
    public bool IsConfigured => true; // API pública abierta sin clave

    public AniListMetaService(string? customDirectory = null)
    {
        string dir = customDirectory ?? TvUserStateStore.GetDefaultDirectory();
        Directory.CreateDirectory(dir);
        _cachePath = Path.Combine(dir, "anilist_cache.json");
    }

    public void ReleaseMemoryCache() => ClearCache();

    public void ClearCache()
    {
        lock (_gate)
        {
            _cache.Clear();
            _cacheLoaded = true;
            if (File.Exists(_cachePath))
            {
                try { File.Delete(_cachePath); } catch { }
            }
        }
    }

    public async Task<List<MediaItem>> DiscoverAsync(
        string type,
        string? categoryOrStudio,
        int? year,
        string? genre,
        int page,
        CancellationToken ct)
    {
        string sortType = (categoryOrStudio ?? "").ToLowerInvariant() switch
        {
            "popular" => "POPULARITY_DESC",
            "score" => "SCORE_DESC",
            "trending" => "TRENDING_DESC",
            _ => "TRENDING_DESC"
        };

        string cacheKey = $"discover:{sortType}:{year}:{genre}:{page}";
        string query = @"
        query ($page: Int, $perPage: Int, $sort: [MediaSort], $seasonYear: Int, $genre: String) {
          Page(page: $page, perPage: $perPage) {
            media(type: ANIME, sort: $sort, seasonYear: $seasonYear, genre: $genre, isAdult: false) {
              id
              title {
                romaji
                english
                native
              }
              description(asHtml: false)
              seasonYear
              episodes
              genres
              averageScore
              coverImage {
                large
                extraLarge
              }
              bannerImage
              studios(isMain: true) {
                nodes {
                  name
                }
              }
              format
            }
          }
        }";

        var variables = new Dictionary<string, object?>
        {
            ["page"] = Math.Max(1, page),
            ["perPage"] = 24,
            ["sort"] = new[] { sortType }
        };

        if (year.HasValue && year > 1960) variables["seasonYear"] = year.Value;
        if (!string.IsNullOrEmpty(genre) && !genre.Equals("all", StringComparison.OrdinalIgnoreCase))
            variables["genre"] = genre;

        string? json = await ExecuteGraphQLAsync(cacheKey, query, variables, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(json)) return new List<MediaItem>();

        return ParseAniListResults(json);
    }

    public async Task<MediaItem?> GetDetailsAsync(string id, CancellationToken ct)
    {
        string rawId = id.StartsWith("anilist:", StringComparison.OrdinalIgnoreCase)
            ? id.Substring(8)
            : id;

        if (!int.TryParse(rawId, out int numericId)) return null;

        string cacheKey = $"detail:{numericId}";
        string query = @"
        query ($id: Int) {
          Media(id: $id, type: ANIME) {
            id
            title {
              romaji
              english
              native
            }
            description(asHtml: false)
            seasonYear
            episodes
            genres
            averageScore
            coverImage {
              large
              extraLarge
            }
            bannerImage
            studios(isMain: true) {
              nodes {
                name
              }
            }
            format
          }
        }";

        var variables = new Dictionary<string, object?> { ["id"] = numericId };
        string? json = await ExecuteGraphQLAsync(cacheKey, query, variables, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(json)) return null;

        var list = ParseAniListResults(json, isSingleItem: true);
        return list.Count > 0 ? list[0] : null;
    }

    public static List<MediaItem> ParseAniListResults(string json, bool isSingleItem = false)
    {
        var result = new List<MediaItem>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var dataEl)) return result;

            if (isSingleItem)
            {
                if (dataEl.TryGetProperty("Media", out var singleEl))
                {
                    var item = ParseSingleMediaElement(singleEl);
                    if (item != null) result.Add(item);
                }
                return result;
            }

            if (dataEl.TryGetProperty("Page", out var pageEl) &&
                pageEl.TryGetProperty("media", out var mediaArr) &&
                mediaArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var mEl in mediaArr.EnumerateArray())
                {
                    var item = ParseSingleMediaElement(mEl);
                    if (item != null) result.Add(item);
                }
            }
        }
        catch { }
        return result;
    }

    private static MediaItem? ParseSingleMediaElement(JsonElement el)
    {
        int id = el.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;
        if (id <= 0) return null;

        string romaji = "";
        string english = "";
        string native = "";

        if (el.TryGetProperty("title", out var tObj))
        {
            romaji = tObj.TryGetProperty("romaji", out var rProp) ? (rProp.GetString() ?? "") : "";
            english = tObj.TryGetProperty("english", out var eProp) ? (eProp.GetString() ?? "") : "";
            native = tObj.TryGetProperty("native", out var nProp) ? (nProp.GetString() ?? "") : "";
        }

        string title = !string.IsNullOrEmpty(english) ? english : romaji;
        string desc = el.TryGetProperty("description", out var dProp) ? (dProp.GetString() ?? "") : "";
        int? year = el.TryGetProperty("seasonYear", out var yProp) && yProp.TryGetInt32(out int yVal) ? yVal : null;
        int? eps = el.TryGetProperty("episodes", out var epProp) && epProp.TryGetInt32(out int epVal) ? epVal : null;
        double? score = el.TryGetProperty("averageScore", out var sProp) && sProp.TryGetInt32(out int scVal)
            ? Math.Round(scVal / 10.0, 1)
            : null;

        string? posterUrl = null;
        if (el.TryGetProperty("coverImage", out var cObj))
        {
            posterUrl = cObj.TryGetProperty("extraLarge", out var xlProp)
                ? xlProp.GetString()
                : (cObj.TryGetProperty("large", out var lProp) ? lProp.GetString() : null);
        }

        string? bannerUrl = el.TryGetProperty("bannerImage", out var bProp) ? bProp.GetString() : null;

        string studioName = "";
        if (el.TryGetProperty("studios", out var stObj) &&
            stObj.TryGetProperty("nodes", out var nArr) &&
            nArr.ValueKind == JsonValueKind.Array &&
            nArr.GetArrayLength() > 0)
        {
            studioName = nArr[0].TryGetProperty("name", out var snProp) ? (snProp.GetString() ?? "") : "";
        }

        string format = el.TryGetProperty("format", out var fProp) ? (fProp.GetString() ?? "TV") : "TV";

        var genres = new List<string>();
        if (el.TryGetProperty("genres", out var gArr) && gArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in gArr.EnumerateArray())
            {
                string s = g.GetString() ?? "";
                if (!string.IsNullOrEmpty(s)) genres.Add(s);
            }
        }

        return new MediaItem
        {
            Id = $"anilist:{id}",
            Type = "anime",
            Title = title,
            OriginalTitle = romaji,
            Year = year,
            Genres = genres,
            Synopsis = desc,
            PosterUrl = posterUrl,
            BackdropUrl = bannerUrl,
            Rating = score,
            SourceName = "AniList",
            Attribution = "Data provided by AniList.co",
            AnimeMeta = new AnimeMetaInfo
            {
                JapaneseTitle = native,
                RomajiTitle = romaji,
                TotalEpisodes = eps,
                Studio = studioName,
                Format = format,
                SeasonYear = year?.ToString()
            }
        };
    }

    private async Task<string?> ExecuteGraphQLAsync(string cacheKey, string query, object variables, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureCacheLoaded();

            if (_cache.TryGetValue(cacheKey, out var hit) && (DateTime.UtcNow - hit.UpdatedUtc) < CacheTtl)
            {
                return hit.Json;
            }

            var payload = new { query, variables };
            string bodyJson = JsonSerializer.Serialize(payload);

            using var req = new HttpRequestMessage(HttpMethod.Post, GraphQLUrl)
            {
                Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
            };

            using var resp = await TvCatalogService.Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _cache[cacheKey] = (DateTime.UtcNow, json);
            SaveCacheToDisk();
            return json;
        }
        catch
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureCacheLoaded()
    {
        if (_cacheLoaded) return;
        if (File.Exists(_cachePath))
        {
            try
            {
                string json = File.ReadAllText(_cachePath, Encoding.UTF8);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json);
                if (loaded != null)
                {
                    foreach (var kv in loaded)
                    {
                        if ((DateTime.UtcNow - kv.Value.UpdatedUtc) < CacheTtl)
                        {
                            _cache[kv.Key] = (kv.Value.UpdatedUtc, kv.Value.Json);
                        }
                    }
                }
            }
            catch { }
        }
        _cacheLoaded = true;
    }

    private void SaveCacheToDisk()
    {
        try
        {
            var export = _cache.ToDictionary(k => k.Key, v => new CacheEntry { UpdatedUtc = v.Value.UpdatedUtc, Json = v.Value.Json });
            string tmp = _cachePath + ".tmp";
            string json = JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, _cachePath, overwrite: true);
        }
        catch { }
    }

    private sealed class CacheEntry
    {
        public DateTime UpdatedUtc { get; set; }
        public string Json { get; set; } = "";
    }
}
