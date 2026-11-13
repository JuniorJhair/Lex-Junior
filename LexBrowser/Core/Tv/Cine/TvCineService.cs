using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LexBrowser.Core.Tv.Cine;

public sealed class TvCineCatalogPayload
{
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public string ETag { get; set; } = "";
    public string LastModified { get; set; } = "";
    public List<TvCineIndexItem> Index { get; set; } = new();
    public TvCineDiscardStats Stats { get; set; } = new();
}

public sealed class TvCineCatalogResult
{
    public bool FromCache { get; init; }
    public bool IsStaleFallback { get; init; }
    public string? Error { get; init; }
    public DateTime UpdatedUtc { get; init; }
    public IReadOnlyList<TvCineIndexItem> Index { get; init; } = Array.Empty<TvCineIndexItem>();
    public TvCineDiscardStats Stats { get; init; } = new();
}

public sealed class TvCineFilterQuery
{
    public string? Query { get; set; }
    public string? Kind { get; set; } // "all" | "movie" | "series"
    public string? Genre { get; set; }
    public string? Decade { get; set; } // "1900s", "1910s", etc.
    public string? Language { get; set; }
    public string? Source { get; set; }
    public string? License { get; set; }
    public bool OnlyWikimedia { get; set; }
}

public sealed class TvCineService
{
    private static readonly Lazy<TvCineService> LazyInstance = new(() => new TvCineService());
    public static TvCineService Shared => LazyInstance.Value;

    public const string CatalogUrl = "https://biskyt.github.io/legalFreeIPTVplaylist/catalog.json";
    public const string VodM3uUrl = "https://biskyt.github.io/legalFreeIPTVplaylist/vod.m3u";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    private readonly SemaphoreSlim _gate = new(1, 1);

    private List<TvCineIndexItem>? _memoryIndex;
    private TvCineDiscardStats _memoryStats = new();
    private DateTime _memoryUpdatedUtc;
    private readonly Dictionary<string, TvTitle> _detailCache = new(StringComparer.OrdinalIgnoreCase);

    public TvCineDiscardStats DiscardStats => _memoryStats;
    public bool HasIndexLoaded => _memoryIndex != null;

    internal TvCineService() { }

    public void ReleaseMemoryCache()
    {
        lock (_gate)
        {
            _memoryIndex = null;
            _memoryStats = new TvCineDiscardStats();
            _detailCache.Clear();
        }
    }

    public List<TvCineIndexItem> Filter(TvCineFilterQuery query)
    {
        lock (_gate)
        {
            var src = _memoryIndex ?? (IReadOnlyList<TvCineIndexItem>)Array.Empty<TvCineIndexItem>();
            return FilterIndex(src, query);
        }
    }

    public void ClearCacheFiles()
    {
        ReleaseMemoryCache();
        try
        {
            string p1 = GetDiskCachePath();
            if (File.Exists(p1)) File.Delete(p1);
            string p2 = GetDetailsCachePath();
            if (File.Exists(p2)) File.Delete(p2);
        }
        catch { }
    }

    public async Task<TvCineCatalogResult> GetCatalogAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _memoryIndex != null)
            {
                return new TvCineCatalogResult
                {
                    FromCache = true,
                    UpdatedUtc = _memoryUpdatedUtc,
                    Index = _memoryIndex,
                    Stats = _memoryStats
                };
            }

            string cachePath = GetDiskCachePath();
            TvCineCatalogPayload? cachedPayload = null;

            if (!forceRefresh && File.Exists(cachePath))
            {
                try
                {
                    using var fs = File.OpenRead(cachePath);
                    cachedPayload = await JsonSerializer.DeserializeAsync<TvCineCatalogPayload>(fs, cancellationToken: ct).ConfigureAwait(false);
                    if (cachedPayload != null && cachedPayload.Index.Count > 0 &&
                        (DateTime.UtcNow - cachedPayload.UpdatedUtc) < CacheTtl)
                    {
                        _memoryIndex = cachedPayload.Index;
                        _memoryStats = cachedPayload.Stats;
                        _memoryUpdatedUtc = cachedPayload.UpdatedUtc;
                        return new TvCineCatalogResult
                        {
                            FromCache = true,
                            UpdatedUtc = cachedPayload.UpdatedUtc,
                            Index = cachedPayload.Index,
                            Stats = cachedPayload.Stats
                        };
                    }
                }
                catch { }
            }

            // Descarga condicional con ETag / If-Modified-Since
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, CatalogUrl);
                if (cachedPayload != null)
                {
                    if (!string.IsNullOrEmpty(cachedPayload.ETag))
                        req.Headers.TryAddWithoutValidation("If-None-Match", cachedPayload.ETag);
                    if (!string.IsNullOrEmpty(cachedPayload.LastModified))
                        req.Headers.TryAddWithoutValidation("If-Modified-Since", cachedPayload.LastModified);
                }

                using var resp = await TvStreamProxy.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                if (resp.StatusCode == HttpStatusCode.NotModified && cachedPayload != null)
                {
                    cachedPayload.UpdatedUtc = DateTime.UtcNow;
                    _memoryIndex = cachedPayload.Index;
                    _memoryStats = cachedPayload.Stats;
                    _memoryUpdatedUtc = cachedPayload.UpdatedUtc;
                    TryWriteDiskCache(cachedPayload);
                    return new TvCineCatalogResult
                    {
                        FromCache = true,
                        UpdatedUtc = cachedPayload.UpdatedUtc,
                        Index = cachedPayload.Index,
                        Stats = cachedPayload.Stats
                    };
                }

                if (resp.IsSuccessStatusCode)
                {
                    string etag = resp.Headers.ETag?.Tag ?? "";
                    string lastMod = resp.Content.Headers.LastModified?.ToString("R") ?? "";

                    using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    var (index, details, stats) = await ParseCatalogJsonStreamAsync(stream, ct).ConfigureAwait(false);

                    if (index.Count > 0)
                    {
                        var payload = new TvCineCatalogPayload
                        {
                            UpdatedUtc = DateTime.UtcNow,
                            ETag = etag,
                            LastModified = lastMod,
                            Index = index,
                            Stats = stats
                        };

                        _memoryIndex = index;
                        _memoryStats = stats;
                        _memoryUpdatedUtc = payload.UpdatedUtc;
                        lock (_detailCache)
                        {
                            _detailCache.Clear();
                            foreach (var kv in details) _detailCache[kv.Key] = kv.Value;
                        }

                        TryWriteDiskCache(payload);
                        TryWriteDetailsCache(details);

                        return new TvCineCatalogResult
                        {
                            FromCache = false,
                            UpdatedUtc = payload.UpdatedUtc,
                            Index = index,
                            Stats = stats
                        };
                    }
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Debug.WriteLine($"[TvCineService] Error al descargar catalog.json: {ex.Message}");
            }

            // Fallback a caché en disco local si existe
            if (cachedPayload != null && cachedPayload.Index.Count > 0)
            {
                _memoryIndex = cachedPayload.Index;
                _memoryStats = cachedPayload.Stats;
                _memoryUpdatedUtc = cachedPayload.UpdatedUtc;
                return new TvCineCatalogResult
                {
                    FromCache = true,
                    IsStaleFallback = true,
                    UpdatedUtc = cachedPayload.UpdatedUtc,
                    Index = cachedPayload.Index,
                    Stats = cachedPayload.Stats
                };
            }

            return new TvCineCatalogResult
            {
                Error = "No se pudo cargar el catálogo de Cine ni existen datos en caché.",
                Index = Array.Empty<TvCineIndexItem>(),
                Stats = new TvCineDiscardStats()
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TvTitle?> GetTitleDetailAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        lock (_detailCache)
        {
            if (_detailCache.TryGetValue(id, out var hit)) return hit;
        }

        // Intenta leer desde el archivo de detalles en disco
        string detailsPath = GetDetailsCachePath();
        if (File.Exists(detailsPath))
        {
            try
            {
                using var fs = File.OpenRead(detailsPath);
                using var doc = await JsonDocument.ParseAsync(fs, cancellationToken: ct).ConfigureAwait(false);
                if (doc.RootElement.TryGetProperty(id, out var el))
                {
                    var title = ParseTitleFromJsonElement(el);
                    if (title != null)
                    {
                        lock (_detailCache) _detailCache[id] = title;
                        return title;
                    }
                }
            }
            catch { }
        }

        return null;
    }

    public static (List<TvCineIndexItem> Index, Dictionary<string, TvTitle> Details, TvCineDiscardStats Stats)
        ParseCatalogJson(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json), writable: false);
        return ParseCatalogJsonStreamAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
    }

    public static async Task<(List<TvCineIndexItem> Index, Dictionary<string, TvTitle> Details, TvCineDiscardStats Stats)>
        ParseCatalogJsonStreamAsync(Stream stream, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;

        JsonElement itemsArray;
        if (root.ValueKind == JsonValueKind.Array)
        {
            itemsArray = root;
        }
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var it) && it.ValueKind == JsonValueKind.Array)
        {
            itemsArray = it;
        }
        else
        {
            return (new List<TvCineIndexItem>(), new Dictionary<string, TvTitle>(), new TvCineDiscardStats());
        }

        var index = new List<TvCineIndexItem>();
        var details = new Dictionary<string, TvTitle>(StringComparer.OrdinalIgnoreCase);
        var stats = new TvCineDiscardStats();

        // Agrupación de series: seriesName -> Season -> Episode
        var seriesGroupMap = new Dictionary<string, List<(int Season, int Episode, TvTitle EpisodeTitle)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var el in itemsArray.EnumerateArray())
        {
            stats.TotalProcessed++;

            string id = GetString(el, "id");
            string titleStr = GetString(el, "title");
            string url = GetString(el, "url");
            string kind = GetString(el, "kind", "movie").ToLowerInvariant();

            if (kind == "live" || kind == "livestream")
            {
                stats.SkippedLiveStream++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(titleStr))
            {
                stats.SkippedNoTitle++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            {
                stats.SkippedInvalidUrl++;
                continue;
            }

            if (uri.Scheme != Uri.UriSchemeHttps)
            {
                stats.SkippedNonHttps++;
                continue;
            }

            if (!TvUrlGuard.IsAllowedCineVideoHost(uri.Host))
            {
                stats.SkippedDisallowedHost++;
                continue;
            }

            if (string.IsNullOrEmpty(id))
            {
                id = "cine." + Math.Abs(url.GetHashCode()).ToString("X");
            }

            int? year = ParseNullableIntProperty(el, "year");
            int durationSec = ParseIntProperty(el, "duration", 0);
            int runtimeMin = durationSec > 0 ? (int)Math.Ceiling(durationSec / 60.0) : 0;

            string group = GetString(el, "group");
            string logo = GetString(el, "logo");
            string license = GetString(el, "license");
            string source = GetString(el, "source");
            string page = GetString(el, "page");
            string description = GetString(el, "description");
            string tmdb = GetString(el, "tmdb");
            string imdb = GetString(el, "imdb");

            var langs = new List<string>();
            if (el.TryGetProperty("languages", out var lProp) && lProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var l in lProp.EnumerateArray())
                {
                    string s = l.GetString() ?? "";
                    if (!string.IsNullOrEmpty(s)) langs.Add(s);
                }
            }

            var countries = new List<string>();
            if (el.TryGetProperty("countries", out var cProp) && cProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in cProp.EnumerateArray())
                {
                    string s = c.GetString() ?? "";
                    if (!string.IsNullOrEmpty(s)) countries.Add(s);
                }
            }

            var genres = ExtractGenres(group);
            string mediaKind = DetectMediaKind(url);

            // Detección de series según FORMAT.md
            string seriesName = GetString(el, "series-name");
            if (string.IsNullOrEmpty(seriesName) && kind == "series") seriesName = titleStr;

            int seasonNum = ParseIntProperty(el, "season", 1);
            int episodeNum = ParseIntProperty(el, "episode", 1);

            var fullTitle = new TvTitle
            {
                Id = id,
                Kind = kind == "series" || !string.IsNullOrEmpty(seriesName) ? "series" : "movie",
                Title = titleStr,
                Year = year,
                Genres = genres,
                Languages = langs,
                Countries = countries,
                RuntimeMin = runtimeMin,
                Synopsis = description,
                PosterUrl = logo,
                TmdbId = tmdb,
                ImdbId = imdb,
                License = license,
                SourceName = source,
                SourcePageUrl = page,
                MediaUrl = url,
                MediaKind = mediaKind
            };

            if (fullTitle.Kind == "series" && !string.IsNullOrEmpty(seriesName))
            {
                if (!seriesGroupMap.TryGetValue(seriesName, out var list))
                {
                    seriesGroupMap[seriesName] = list = new();
                }
                list.Add((seasonNum, episodeNum, fullTitle));
                details[id] = fullTitle;
                stats.TotalAccepted++;
            }
            else
            {
                var indexItem = new TvCineIndexItem
                {
                    Id = id,
                    Kind = fullTitle.Kind,
                    Title = titleStr,
                    TitleNormalized = NormalizeForSearch(titleStr),
                    Year = year,
                    RuntimeMin = runtimeMin,
                    Group = group,
                    PosterUrl = logo,
                    Genres = genres.ToArray(),
                    Languages = langs.ToArray(),
                    SourceName = source,
                    License = license,
                    MediaUrl = url,
                    MediaKind = mediaKind,
                    SeriesName = seriesName
                };

                index.Add(indexItem);
                details[id] = fullTitle;
                stats.TotalAccepted++;
            }
        }

        // Ensamblar jerarquía de series
        foreach (var kv in seriesGroupMap)
        {
            string sName = kv.Key;
            var episodes = kv.Value;

            var seasonsDict = new Dictionary<int, List<TvEpisodeInfo>>();
            foreach (var (sNum, epNum, epTitle) in episodes)
            {
                if (!seasonsDict.TryGetValue(sNum, out var epList))
                {
                    seasonsDict[sNum] = epList = new();
                }
                epList.Add(new TvEpisodeInfo
                {
                    Id = epTitle.Id,
                    EpisodeNumber = epNum,
                    Title = epTitle.Title,
                    MediaUrl = epTitle.MediaUrl,
                    DurationSec = epTitle.RuntimeMin * 60,
                    PosterUrl = epTitle.PosterUrl
                });
            }

            var seasons = seasonsDict
                .OrderBy(s => s.Key)
                .Select(s => new TvSeasonInfo
                {
                    SeasonNumber = s.Key,
                    Episodes = s.Value.OrderBy(e => e.EpisodeNumber).ToList()
                })
                .ToList();

            var seriesObj = new TvSeriesInfo
            {
                Name = sName,
                Seasons = seasons
            };

            foreach (var (_, _, epTitle) in episodes)
            {
                epTitle.Series = seriesObj;
            }

            var firstEp = episodes.OrderBy(e => e.Item1).ThenBy(e => e.Item2).First().Item3;
            string seriesId = "series." + Math.Abs(sName.GetHashCode()).ToString("X");
            var seriesIndexItem = new TvCineIndexItem
            {
                Id = seriesId,
                Kind = "series",
                Title = sName,
                TitleNormalized = NormalizeForSearch(sName),
                Year = episodes.Select(e => e.Item3.Year).FirstOrDefault(y => y.HasValue),
                RuntimeMin = firstEp.RuntimeMin,
                Group = firstEp.Genres.FirstOrDefault() ?? "",
                PosterUrl = episodes.Select(e => e.Item3.PosterUrl).FirstOrDefault(p => !string.IsNullOrEmpty(p)) ?? "",
                Genres = episodes.SelectMany(e => e.Item3.Genres).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Languages = episodes.SelectMany(e => e.Item3.Languages).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                SourceName = firstEp.SourceName,
                License = firstEp.License,
                MediaUrl = firstEp.MediaUrl,
                MediaKind = firstEp.MediaKind,
                SeriesName = sName
            };
            index.Add(seriesIndexItem);

            var seriesDetail = new TvTitle
            {
                Id = seriesId,
                Kind = "series",
                Title = sName,
                Year = seriesIndexItem.Year,
                Genres = seriesIndexItem.Genres.ToList(),
                Languages = seriesIndexItem.Languages.ToList(),
                Countries = firstEp.Countries,
                RuntimeMin = firstEp.RuntimeMin,
                Synopsis = firstEp.Synopsis,
                PosterUrl = seriesIndexItem.PosterUrl,
                License = firstEp.License,
                SourceName = firstEp.SourceName,
                SourcePageUrl = firstEp.SourcePageUrl,
                MediaUrl = firstEp.MediaUrl,
                MediaKind = firstEp.MediaKind,
                Series = seriesObj
            };
            details[seriesId] = seriesDetail;
        }

        return (index, details, stats);
    }

    public static List<TvCineIndexItem> FilterIndex(IEnumerable<TvCineIndexItem> source, TvCineFilterQuery filter)
    {
        if (source == null) return new List<TvCineIndexItem>();
        var list = source;

        if (!string.IsNullOrWhiteSpace(filter.Query))
        {
            string q = NormalizeForSearch(filter.Query);
            list = list.Where(i => i.TitleNormalized.Contains(q, StringComparison.Ordinal) ||
                                   (i.SeriesName != null && NormalizeForSearch(i.SeriesName).Contains(q, StringComparison.Ordinal)) ||
                                   i.Genres.Any(g => NormalizeForSearch(g).Contains(q, StringComparison.Ordinal)));
        }

        if (!string.IsNullOrWhiteSpace(filter.Kind) && !filter.Kind.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            string k = filter.Kind.Trim().ToLowerInvariant();
            list = list.Where(i => i.Kind.Equals(k, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(filter.Genre))
        {
            string g = filter.Genre.Trim().ToLowerInvariant();
            list = list.Where(i => i.Genres.Any(x => x.ToLowerInvariant().Contains(g)) ||
                                   i.Group.ToLowerInvariant().Contains(g));
        }

        if (!string.IsNullOrWhiteSpace(filter.Decade))
        {
            if (int.TryParse(filter.Decade.Replace("s", "").Trim(), out int decStart))
            {
                int decEnd = decStart + 9;
                list = list.Where(i => i.Year.HasValue && i.Year.Value >= decStart && i.Year.Value <= decEnd);
            }
        }

        if (!string.IsNullOrWhiteSpace(filter.Language))
        {
            string lang = filter.Language.Trim().ToLowerInvariant();
            list = list.Where(i => i.Languages.Any(l => l.ToLowerInvariant().Contains(lang)));
        }

        if (!string.IsNullOrWhiteSpace(filter.Source))
        {
            string src = filter.Source.Trim().ToLowerInvariant();
            list = list.Where(i => i.SourceName.ToLowerInvariant().Contains(src));
        }

        if (!string.IsNullOrWhiteSpace(filter.License))
        {
            string lic = filter.License.Trim().ToLowerInvariant();
            list = list.Where(i => i.License.ToLowerInvariant().Contains(lic));
        }

        if (filter.OnlyWikimedia)
        {
            list = list.Where(i => i.SourceName.Equals("wikimedia-commons", StringComparison.OrdinalIgnoreCase) ||
                                   i.MediaUrl.Contains("wikimedia.org", StringComparison.OrdinalIgnoreCase));
        }

        return list.ToList();
    }

    public static string NormalizeForSearch(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string normalized = text.Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (char c in normalized)
        {
            var uc = CharUnicodeInfo.GetUnicodeCategory(c);
            if (uc != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static List<string> ExtractGenres(string group)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(group)) return result;

        string clean = group;
        int colon = group.IndexOf(':');
        if (colon >= 0 && colon + 1 < group.Length)
        {
            clean = group.Substring(colon + 1);
        }

        foreach (var part in clean.Split(new[] { ',', '&', '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string t = part.Trim();
            if (t.Length > 0 && !result.Contains(t, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(t);
            }
        }

        if (result.Count == 0 && clean.Trim().Length > 0)
        {
            result.Add(clean.Trim());
        }

        return result;
    }

    private static string DetectMediaKind(string url)
    {
        string u = (url ?? "").ToLowerInvariant().Split('?')[0].Split('#')[0];
        if (u.EndsWith(".webm")) return "webm";
        if (u.EndsWith(".ogv") || u.EndsWith(".ogg")) return "ogv";
        if (u.EndsWith(".m3u8") || u.EndsWith(".m3u")) return "m3u8";
        return "mp4";
    }

    private static string GetString(JsonElement el, string propName, string defaultVal = "")
    {
        if (el.TryGetProperty(propName, out var p))
        {
            if (p.ValueKind == JsonValueKind.String) return p.GetString() ?? defaultVal;
            if (p.ValueKind == JsonValueKind.Number) return p.GetRawText();
        }
        return defaultVal;
    }

    private static int ParseIntProperty(JsonElement el, string propName, int defaultVal)
    {
        if (el.TryGetProperty(propName, out var p))
        {
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int n)) return n;
            if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out int s)) return s;
        }
        return defaultVal;
    }

    private static int? ParseNullableIntProperty(JsonElement el, string propName)
    {
        if (el.TryGetProperty(propName, out var p))
        {
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int n)) return n;
            if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out int s)) return s;
        }
        return null;
    }

    private static TvTitle? ParseTitleFromJsonElement(JsonElement el)
    {
        try
        {
            return JsonSerializer.Deserialize<TvTitle>(el.GetRawText());
        }
        catch
        {
            return null;
        }
    }

    private static string GetDiskCachePath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string dir = Path.Combine(appData, "LexBrowser");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "cine_catalog_cache.json");
    }

    private static string GetDetailsCachePath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string dir = Path.Combine(appData, "LexBrowser");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "cine_details_cache.json");
    }

    private static void TryWriteDiskCache(TvCineCatalogPayload payload)
    {
        try
        {
            string path = GetDiskCachePath();
            string tmp = path + ".tmp";
            string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }

    private static void TryWriteDetailsCache(Dictionary<string, TvTitle> details)
    {
        try
        {
            string path = GetDetailsCachePath();
            string tmp = path + ".tmp";
            string json = JsonSerializer.Serialize(details, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }
}

