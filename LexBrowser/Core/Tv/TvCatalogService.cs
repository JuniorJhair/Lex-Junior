using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LexBrowser.Core.Tv;

public sealed class TvCatalogCachePayload
{
    public int SchemaVersion { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public List<TvChannel> Channels { get; set; } = new();
    public Dictionary<string, int> NumberMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, DateTime> RetiredNumbers { get; set; } = new();
}

public sealed class TvCatalogResult
{
    public List<TvChannel> Channels { get; set; } = new();
    public DateTime UpdatedUtc { get; set; }
    public bool FromCache { get; set; }
    public bool IsStaleFallback { get; set; }
    public string Error { get; set; } = "";
}

/// <summary>
/// Carga bajo demanda el catálogo de iptv-org (blocklist -> countries -> channels -> feeds -> streams -> logos),
/// parseando en streaming con JsonDocument.ParseAsync y liberando cada documento antes del siguiente
/// para minimizar el Working Set y evitar asignar grandes strings en el LOH.
/// </summary>
public sealed class TvCatalogService
{
    private const string ApiBase = "https://iptv-org.github.io/api/";
    private const int CurrentSchemaVersion = 2;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan RetiredHoldPeriod = TimeSpan.FromDays(30);
    private static readonly string[] LatamCountryPriority =
    [
        "PE", "MX", "AR", "CO", "CL", "EC", "UY", "BO", "PY", "VE",
        "CR", "PA", "DO", "GT", "HN", "NI", "SV", "CU", "PR"
    ];
    private static readonly JsonSerializerOptions CacheJsonOptions = new() { WriteIndented = false };

    private static readonly Lazy<HttpClient> SharedHttp =
        new(() => TvUrlGuard.CreatePinnedHttpClient(timeout: TimeSpan.FromSeconds(45)));

    private static readonly Dictionary<string, string> CategoryNamesEs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["auto"] = "Motor",
        ["animation"] = "Animación",
        ["business"] = "Economía",
        ["classic"] = "Clásicos",
        ["comedy"] = "Comedia",
        ["cooking"] = "Cocina",
        ["culture"] = "Cultura",
        ["documentary"] = "Documentales",
        ["education"] = "Educación",
        ["entertainment"] = "Entretenimiento",
        ["family"] = "Familia",
        ["general"] = "Sin categoría",
        ["kids"] = "Infantil",
        ["legislative"] = "Legislativo",
        ["lifestyle"] = "Estilo de vida",
        ["movies"] = "Cine",
        ["music"] = "Música",
        ["news"] = "Noticias",
        ["outdoor"] = "Aire libre",
        ["public"] = "Público",
        ["relax"] = "Relajación",
        ["religious"] = "Religión",
        ["science"] = "Ciencia",
        ["series"] = "Series",
        ["shop"] = "Compras",
        ["sports"] = "Deportes",
        ["travel"] = "Viajes",
        ["weather"] = "Clima",
        ["undefined"] = "Sin categoría"
    };

    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly string _cachePath;
    private TvCatalogCachePayload? _memoryCache;

    public static TvCatalogService Shared { get; } = new(TvUserStateStore.GetDefaultDirectory());
    internal static HttpClient Http => SharedHttp.Value;

    public long LastLoadWorkingSetBeforeBytes { get; private set; }
    public long LastLoadWorkingSetAfterBytes { get; private set; }

    public TvCatalogService(string cacheDirectory)
    {
        _cachePath = Path.Combine(cacheDirectory, "tv_catalog_cache.json");
    }

    public async Task<TvCatalogResult> GetCatalogAsync(bool forceRefresh, CancellationToken ct)
    {
        await _loadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            TvCatalogCachePayload? cached = _memoryCache ?? TryReadCache();
            bool hasCurrentSchemaCache = cached != null && cached.SchemaVersion >= CurrentSchemaVersion && cached.Channels.Count > 0;
            if (!forceRefresh && hasCurrentSchemaCache &&
                (DateTime.UtcNow - cached!.UpdatedUtc) < CacheTtl)
            {
                _memoryCache = cached;
                return new TvCatalogResult
                {
                    Channels = cached.Channels,
                    UpdatedUtc = cached.UpdatedUtc,
                    FromCache = true
                };
            }

            try
            {
                LastLoadWorkingSetBeforeBytes = Environment.WorkingSet;
                var fresh = await FetchAndBuildCatalogAsync(ct).ConfigureAwait(false);
                var prevMap = hasCurrentSchemaCache ? cached!.NumberMap : null;
                var prevRetired = hasCurrentSchemaCache ? cached!.RetiredNumbers : null;
                AssignStableNumbers(fresh, prevMap, prevRetired, out var updatedMap, out var updatedRetired);

                var payload = new TvCatalogCachePayload
                {
                    SchemaVersion = CurrentSchemaVersion,
                    UpdatedUtc = DateTime.UtcNow,
                    Channels = fresh,
                    NumberMap = updatedMap,
                    RetiredNumbers = updatedRetired
                };
                _memoryCache = payload;
                TryWriteCache(payload);
                LastLoadWorkingSetAfterBytes = Environment.WorkingSet;
                Debug.WriteLine(
                    $"[LexTV] Catalog loaded ({fresh.Count} channels). WorkingSet: " +
                    $"{LastLoadWorkingSetBeforeBytes / (1024 * 1024)} MB -> {LastLoadWorkingSetAfterBytes / (1024 * 1024)} MB");

                return new TvCatalogResult
                {
                    Channels = payload.Channels,
                    UpdatedUtc = payload.UpdatedUtc,
                    FromCache = false
                };
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (cached != null && cached.Channels.Count > 0)
                {
                    _memoryCache = cached;
                    return new TvCatalogResult
                    {
                        Channels = cached.Channels,
                        UpdatedUtc = cached.UpdatedUtc,
                        FromCache = true,
                        IsStaleFallback = true,
                        Error = ex.Message
                    };
                }

                return new TvCatalogResult
                {
                    Error = ex.Message
                };
            }
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>Libera el caché en RAM cuando no quedan pestañas de TV abiertas si se desea reducir memoria.</summary>
    public void ReleaseMemoryCache()
    {
        _memoryCache = null;
    }

    public void ClearAllCache()
    {
        _memoryCache = null;
        try
        {
            if (File.Exists(_cachePath))
                File.Delete(_cachePath);
        }
        catch { }
    }

    public async Task<List<TvChannel>> ImportPlaylistFromUrlAsync(string url, string sourceName, CancellationToken ct)
        => (await ImportPlaylistDetailedFromUrlAsync(url, sourceName, ct).ConfigureAwait(false)).Channels;

    public async Task<M3uParseResult> ImportPlaylistDetailedFromUrlAsync(string url, string sourceName, CancellationToken ct)
    {
        if (!TvUrlGuard.IsAllowedRemoteUrl(url, out var uri) || uri == null)
            throw new InvalidOperationException("URL inválida. Solo se aceptan enlaces públicos http:// o https://.");

        var (resp, finalUri) = await TvUrlGuard.GetWithSafeRedirectsAsync(Http, uri, null, null, ct).ConfigureAwait(false);
        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"El servidor respondió {(int)resp.StatusCode} {resp.ReasonPhrase}.");

            string text = await TvUrlGuard.ReadBoundedUtf8Async(resp, TvUrlGuard.MaxPlaylistBytes, ct).ConfigureAwait(false);
            var parsed = M3uParser.ParseDetailed(text, sourceName, finalUri.AbsoluteUri);
            if (parsed.Channels.Count == 0)
                throw new InvalidOperationException("No se encontraron canales válidos (#EXTINF + URL http/https) en la lista.");
            return parsed;
        }
    }

    private static async Task<List<TvChannel>> FetchAndBuildCatalogAsync(CancellationToken ct)
    {
        HashSet<string> blocked;
        using (var doc = await DownloadApiJsonAsync("blocklist.json", ct).ConfigureAwait(false))
            blocked = ParseBlocklistDoc(doc);

        Dictionary<string, CountryMeta> countries;
        using (var doc = await DownloadApiJsonAsync("countries.json", ct).ConfigureAwait(false))
            countries = ParseCountriesDoc(doc);

        Dictionary<string, ChannelMeta> channels;
        using (var doc = await DownloadApiJsonAsync("channels.json", ct).ConfigureAwait(false))
            channels = ParseChannelsDoc(doc, blocked);
        blocked.Clear();

        Dictionary<string, FeedMeta> feedsByKey;
        Dictionary<string, FeedMeta> mainFeedByChannel;
        using (var doc = await DownloadApiJsonAsync("feeds.json", ct).ConfigureAwait(false))
            (feedsByKey, mainFeedByChannel) = ParseFeedsDoc(doc, channels);

        List<TvChannel> result;
        using (var doc = await DownloadApiJsonAsync("streams.json", ct).ConfigureAwait(false))
            result = BuildFromStreamsDoc(doc, channels, feedsByKey, mainFeedByChannel, countries);

        // Liberar índices intermedios antes de cargar logos
        channels.Clear();
        feedsByKey.Clear();
        mainFeedByChannel.Clear();
        countries.Clear();

        if (result.Count > 0)
        {
            using var doc = await DownloadApiJsonAsync("logos.json", ct).ConfigureAwait(false);
            ApplyLogosDoc(doc, result);
        }

        return result;
    }

    private static async Task<JsonDocument> DownloadApiJsonAsync(string fileName, CancellationToken ct)
    {
        var uri = new Uri(ApiBase + fileName);
        var (resp, _) = await TvUrlGuard.GetWithSafeRedirectsAsync(Http, uri, null, null, ct).ConfigureAwait(false);
        using (resp)
        {
            resp.EnsureSuccessStatusCode();
            if (resp.Content.Headers.ContentLength is long len && len > TvUrlGuard.MaxCatalogBytes)
                throw new TvProxyErrorException("upstream", 502, $"{fileName} excede el tamaño máximo permitido.");

            await using var rawStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var bounded = new BoundedReadStream(rawStream, TvUrlGuard.MaxCatalogBytes);
            return await JsonDocument.ParseAsync(bounded, default, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Une channels + feeds + streams + blocklist + countries + logos, excluye NSFW/cerrados/bloqueados
    /// y conserva solo canales en Español o Inglés con numeración determinista.
    /// </summary>
    public static List<TvChannel> BuildCatalogFromJson(
        string channelsJson,
        string streamsJson,
        string feedsJson,
        string blocklistJson,
        string countriesJson,
        string? logosJson = null)
    {
        HashSet<string> blocked;
        if (string.IsNullOrWhiteSpace(blocklistJson))
            blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        else
            using (var doc = JsonDocument.Parse(blocklistJson))
                blocked = ParseBlocklistDoc(doc);

        Dictionary<string, CountryMeta> countries;
        if (string.IsNullOrWhiteSpace(countriesJson))
            countries = new Dictionary<string, CountryMeta>(StringComparer.OrdinalIgnoreCase);
        else
            using (var doc = JsonDocument.Parse(countriesJson))
                countries = ParseCountriesDoc(doc);

        Dictionary<string, ChannelMeta> channels;
        using (var doc = JsonDocument.Parse(channelsJson))
            channels = ParseChannelsDoc(doc, blocked);

        Dictionary<string, FeedMeta> feedsByKey;
        Dictionary<string, FeedMeta> mainFeedByChannel;
        using (var doc = JsonDocument.Parse(feedsJson))
            (feedsByKey, mainFeedByChannel) = ParseFeedsDoc(doc, channels);

        List<TvChannel> result;
        using (var doc = JsonDocument.Parse(streamsJson))
            result = BuildFromStreamsDoc(doc, channels, feedsByKey, mainFeedByChannel, countries);

        if (!string.IsNullOrWhiteSpace(logosJson) && result.Count > 0)
        {
            using var doc = JsonDocument.Parse(logosJson);
            ApplyLogosDoc(doc, result);
        }

        SortAndNumberCatalog(result);
        return result;
    }

    /// <summary>
    /// Construye el catálogo directamente desde streams UTF-8 secuenciales (tests de Working Set y bajo consumo sin string).
    /// </summary>
    public static async Task<List<TvChannel>> BuildCatalogFromStreamsAsync(
        Stream channelsStream,
        Stream streamsStream,
        Stream feedsStream,
        Stream? blocklistStream,
        Stream? countriesStream,
        Stream? logosStream = null,
        CancellationToken ct = default)
    {
        HashSet<string> blocked;
        if (blocklistStream == null)
            blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        else
            using (var doc = await JsonDocument.ParseAsync(blocklistStream, default, ct).ConfigureAwait(false))
                blocked = ParseBlocklistDoc(doc);

        Dictionary<string, CountryMeta> countries;
        if (countriesStream == null)
            countries = new Dictionary<string, CountryMeta>(StringComparer.OrdinalIgnoreCase);
        else
            using (var doc = await JsonDocument.ParseAsync(countriesStream, default, ct).ConfigureAwait(false))
                countries = ParseCountriesDoc(doc);

        Dictionary<string, ChannelMeta> channels;
        using (var doc = await JsonDocument.ParseAsync(channelsStream, default, ct).ConfigureAwait(false))
            channels = ParseChannelsDoc(doc, blocked);
        blocked.Clear();

        Dictionary<string, FeedMeta> feedsByKey;
        Dictionary<string, FeedMeta> mainFeedByChannel;
        using (var doc = await JsonDocument.ParseAsync(feedsStream, default, ct).ConfigureAwait(false))
            (feedsByKey, mainFeedByChannel) = ParseFeedsDoc(doc, channels);

        List<TvChannel> result;
        using (var doc = await JsonDocument.ParseAsync(streamsStream, default, ct).ConfigureAwait(false))
            result = BuildFromStreamsDoc(doc, channels, feedsByKey, mainFeedByChannel, countries);

        channels.Clear();
        feedsByKey.Clear();
        mainFeedByChannel.Clear();
        countries.Clear();

        if (logosStream != null && result.Count > 0)
        {
            using var doc = await JsonDocument.ParseAsync(logosStream, default, ct).ConfigureAwait(false);
            ApplyLogosDoc(doc, result);
        }

        SortAndNumberCatalog(result);
        return result;
    }

    private sealed class StreamCandidate
    {
        public string Id { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string Url { get; init; } = "";
        public string Group { get; init; } = "";
        public string Language { get; init; } = "";
        public string Country { get; init; } = "";
        public string CountryName { get; init; } = "";
        public string Quality { get; init; } = "";
        public int QualityScore { get; init; }
        public bool IsHttps { get; init; }
        public string Referrer { get; init; } = "";
        public string UserAgent { get; init; } = "";
        public string Tags { get; init; } = "";
        public int AppearanceOrder { get; init; }
    }

    private static List<TvChannel> BuildFromStreamsDoc(
        JsonDocument streamsDoc,
        Dictionary<string, ChannelMeta> channels,
        Dictionary<string, FeedMeta> feedsByKey,
        Dictionary<string, FeedMeta> mainFeedByChannel,
        Dictionary<string, CountryMeta> countries)
    {
        var grouped = new Dictionary<string, List<StreamCandidate>>(StringComparer.OrdinalIgnoreCase);
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int order = 0;

        foreach (var el in streamsDoc.RootElement.EnumerateArray())
        {
            string? chId = GetString(el, "channel");
            if (string.IsNullOrWhiteSpace(chId)) continue;
            if (!channels.TryGetValue(chId, out var chMeta)) continue;

            string? rawUrl = GetString(el, "url");
            if (!TvUrlGuard.IsAllowedRemoteUrl(rawUrl, out var streamUri) || streamUri == null) continue;
            if (!seenUrls.Add(streamUri.AbsoluteUri)) continue;

            string? feedId = GetString(el, "feed");
            FeedMeta? feed = null;
            if (!string.IsNullOrWhiteSpace(feedId))
                feedsByKey.TryGetValue(chId + "|" + feedId, out feed);
            feed ??= mainFeedByChannel.GetValueOrDefault(chId);

            var langs = feed?.Languages;
            if ((langs == null || langs.Count == 0) && countries.TryGetValue(chMeta.Country, out var cInfo))
                langs = cInfo.Languages;

            if (langs == null) continue;
            bool hasSpa = langs.Contains("spa");
            bool hasEng = langs.Contains("eng");
            if (!hasSpa && !hasEng) continue;

            string langLabel = hasSpa && hasEng ? "Español / Inglés" : (hasSpa ? "Español" : "Inglés");

            string quality = TvUrlGuard.SanitizeHeaderValue(GetString(el, "quality"), 16);
            if (quality.Length == 0 && feed != null)
                quality = TvUrlGuard.SanitizeHeaderValue(feed.Format, 16);

            string title = TvUrlGuard.SanitizeHeaderValue(GetString(el, "title"), 120);
            string displayName = title.Length > 0 ? title : chMeta.Name;
            if (feed != null && !feed.IsMain && !string.IsNullOrWhiteSpace(feed.Name) &&
                !displayName.Contains(feed.Name, StringComparison.OrdinalIgnoreCase) &&
                !feed.Name.Equals("SD", StringComparison.OrdinalIgnoreCase))
            {
                displayName = $"{displayName} ({feed.Name})";
            }

            string tags = ReadLabels(el);
            string countryDisplay = countries.TryGetValue(chMeta.Country, out var cm)
                ? cm.Name
                : chMeta.Country;

            string canonicalId = chId + (string.IsNullOrWhiteSpace(feedId) ? "" : "@" + feedId);
            if (!grouped.TryGetValue(canonicalId, out var list))
            {
                list = new List<StreamCandidate>(2);
                grouped[canonicalId] = list;
            }

            list.Add(new StreamCandidate
            {
                Id = canonicalId,
                DisplayName = displayName,
                Url = streamUri.AbsoluteUri,
                Group = MapCategoryName(chMeta.Category),
                Language = langLabel,
                Country = chMeta.Country,
                CountryName = countryDisplay,
                Quality = quality,
                QualityScore = ParseQualityScore(quality),
                IsHttps = streamUri.Scheme == Uri.UriSchemeHttps,
                Referrer = TvUrlGuard.SanitizeHeaderValue(GetString(el, "referrer"), 400),
                UserAgent = TvUrlGuard.SanitizeHeaderValue(GetString(el, "user_agent"), 240),
                Tags = tags,
                AppearanceOrder = order++
            });
        }

        var result = new List<TvChannel>(grouped.Count);
        foreach (var group in grouped.Values)
        {
            if (group.Count > 1)
            {
                group.Sort((a, b) =>
                {
                    int q = b.QualityScore.CompareTo(a.QualityScore);
                    if (q != 0) return q;
                    int h = b.IsHttps.CompareTo(a.IsHttps);
                    if (h != 0) return h;
                    return a.AppearanceOrder.CompareTo(b.AppearanceOrder);
                });
            }

            var primary = group[0];
            var ch = new TvChannel
            {
                Id = primary.Id,
                Name = primary.DisplayName,
                Url = primary.Url,
                Group = primary.Group,
                Language = primary.Language,
                Country = primary.Country,
                CountryName = primary.CountryName,
                Logo = "",
                Quality = primary.Quality,
                Referrer = primary.Referrer,
                UserAgent = primary.UserAgent,
                Tags = primary.Tags,
                Source = "iptv-org"
            };

            if (group.Count > 1)
            {
                ch.Alternates = new List<TvStreamAlternate>(group.Count - 1);
                for (int i = 1; i < group.Count; i++)
                {
                    var alt = group[i];
                    ch.Alternates.Add(new TvStreamAlternate
                    {
                        Url = alt.Url,
                        Quality = alt.Quality,
                        Referrer = alt.Referrer,
                        UserAgent = alt.UserAgent
                    });
                }
            }

            result.Add(ch);
        }

        return result;
    }

    internal static int ParseQualityScore(string? quality)
    {
        if (string.IsNullOrWhiteSpace(quality)) return 0;
        if (quality.Contains("4k", StringComparison.OrdinalIgnoreCase) ||
            quality.Contains("uhd", StringComparison.OrdinalIgnoreCase))
            return 2160;

        int val = 0;
        foreach (char c in quality)
        {
            if (c >= '0' && c <= '9')
                val = Math.Min(10000, val * 10 + (c - '0'));
            else if (val > 0)
                break;
        }
        return val;
    }

    public static void AssignStableNumbers(
        List<TvChannel> channels,
        Dictionary<string, int>? previousMap,
        Dictionary<int, DateTime>? previousRetired,
        out Dictionary<string, int> updatedMap,
        out Dictionary<int, DateTime> updatedRetired,
        DateTime? nowUtc = null)
    {
        DateTime now = nowUtc ?? DateTime.UtcNow;
        updatedMap = new Dictionary<string, int>(channels.Count, StringComparer.OrdinalIgnoreCase);
        updatedRetired = new Dictionary<int, DateTime>();

        if (previousRetired != null)
        {
            foreach (var kv in previousRetired)
            {
                if (kv.Key > 0 && (now - kv.Value) < RetiredHoldPeriod)
                    updatedRetired[kv.Key] = kv.Value;
            }
        }

        var usedNumbers = new HashSet<int>();
        var unassigned = new List<TvChannel>();

        foreach (var ch in channels)
        {
            if (previousMap != null &&
                !string.IsNullOrWhiteSpace(ch.Id) &&
                previousMap.TryGetValue(ch.Id, out int existingNum) &&
                existingNum > 0 &&
                usedNumbers.Add(existingNum))
            {
                ch.Number = existingNum;
                updatedMap[ch.Id] = existingNum;
                updatedRetired.Remove(existingNum);
            }
            else
            {
                unassigned.Add(ch);
            }
        }

        if (previousMap != null)
        {
            foreach (var kv in previousMap)
            {
                if (kv.Value > 0 && !usedNumbers.Contains(kv.Value) && !updatedRetired.ContainsKey(kv.Value))
                    updatedRetired[kv.Value] = now;
            }
        }

        unassigned.Sort((a, b) =>
        {
            var (sTierA, tierA, subA) = GetInitialSortKey(a);
            var (sTierB, tierB, subB) = GetInitialSortKey(b);
            int cmp = sTierA.CompareTo(sTierB);
            if (cmp != 0) return cmp;
            cmp = tierA.CompareTo(tierB);
            if (cmp != 0) return cmp;
            cmp = subA.CompareTo(subB);
            if (cmp != 0) return cmp;
            cmp = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            if (cmp != 0) return cmp;
            return string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
        });

        int candidate = 1;
        foreach (var ch in unassigned)
        {
            while (usedNumbers.Contains(candidate) || updatedRetired.ContainsKey(candidate))
                candidate++;

            ch.Number = candidate;
            usedNumbers.Add(candidate);
            if (!string.IsNullOrWhiteSpace(ch.Id))
                updatedMap[ch.Id] = candidate;
            candidate++;
        }

        channels.Sort((a, b) => a.Number.CompareTo(b.Number));
    }

    private static (int SourceTier, int LangTier, int Sub) GetInitialSortKey(TvChannel ch)
    {
        int sourceTier = ch.Tier > 0 ? ch.Tier : 3;
        bool hasSpanish = ch.Language.StartsWith("Español", StringComparison.OrdinalIgnoreCase);
        string country = ch.Country ?? "";

        if (hasSpanish)
        {
            int latamIdx = Array.IndexOf(LatamCountryPriority, country.ToUpperInvariant());
            if (latamIdx >= 0) return (sourceTier, 0, latamIdx);
            if (country.Equals("ES", StringComparison.OrdinalIgnoreCase)) return (sourceTier, 1, 0);
            return (sourceTier, 2, 0);
        }

        if (country.Equals("US", StringComparison.OrdinalIgnoreCase)) return (sourceTier, 3, 0);
        if (country.Equals("GB", StringComparison.OrdinalIgnoreCase)) return (sourceTier, 3, 1);
        return (sourceTier, 4, 0);
    }

    private static void SortAndNumberCatalog(List<TvChannel> result)
        => AssignStableNumbers(result, null, null, out _, out _);

    private static string MapCategoryName(string? categoryId)
    {
        if (string.IsNullOrWhiteSpace(categoryId)) return "Sin categoría";
        if (CategoryNamesEs.TryGetValue(categoryId, out var mapped)) return mapped;
        return char.ToUpperInvariant(categoryId[0]) + categoryId[1..];
    }

    private static HashSet<string> ParseBlocklistDoc(JsonDocument doc)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            string? id = GetString(el, "channel");
            if (!string.IsNullOrWhiteSpace(id)) set.Add(id);
        }
        return set;
    }

    private sealed record CountryMeta(string Name, HashSet<string> Languages);

    private static Dictionary<string, CountryMeta> ParseCountriesDoc(JsonDocument doc)
    {
        var map = new Dictionary<string, CountryMeta>(StringComparer.OrdinalIgnoreCase);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            string? code = GetString(el, "code");
            string? name = GetString(el, "name");
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name)) continue;

            var langs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (el.TryGetProperty("languages", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var l in arr.EnumerateArray())
                {
                    if (l.ValueKind == JsonValueKind.String && l.GetString() is string ls)
                        langs.Add(ls);
                }
            }
            map[code] = new CountryMeta(name, langs);
        }
        return map;
    }

    private sealed record ChannelMeta(string Name, string Country, string Category);

    private static Dictionary<string, ChannelMeta> ParseChannelsDoc(JsonDocument doc, HashSet<string> blocked)
    {
        var map = new Dictionary<string, ChannelMeta>(StringComparer.OrdinalIgnoreCase);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            string? id = GetString(el, "id");
            if (string.IsNullOrWhiteSpace(id) || blocked.Contains(id)) continue;
            if (el.TryGetProperty("is_nsfw", out var nsfw) && nsfw.ValueKind == JsonValueKind.True) continue;
            if (!string.IsNullOrWhiteSpace(GetString(el, "closed"))) continue;

            string name = TvUrlGuard.SanitizeHeaderValue(GetString(el, "name"), 120);
            string country = TvUrlGuard.SanitizeHeaderValue(GetString(el, "country"), 16);
            string category = "";
            if (el.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in cats.EnumerateArray())
                {
                    string? cs = c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                    if (string.IsNullOrWhiteSpace(cs)) continue;
                    category = cs;
                    if (!cs.Equals("general", StringComparison.OrdinalIgnoreCase)) break;
                }
            }

            map[id] = new ChannelMeta(name.Length > 0 ? name : id, country, category);
        }
        return map;
    }

    private sealed record FeedMeta(string Name, bool IsMain, HashSet<string> Languages, string Format);

    private static (Dictionary<string, FeedMeta> ByKey, Dictionary<string, FeedMeta> MainByChannel) ParseFeedsDoc(
        JsonDocument doc,
        Dictionary<string, ChannelMeta> channels)
    {
        var byKey = new Dictionary<string, FeedMeta>(StringComparer.OrdinalIgnoreCase);
        var mainByChannel = new Dictionary<string, FeedMeta>(StringComparer.OrdinalIgnoreCase);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            string? ch = GetString(el, "channel");
            string? id = GetString(el, "id");
            if (string.IsNullOrWhiteSpace(ch) || string.IsNullOrWhiteSpace(id)) continue;
            if (!channels.ContainsKey(ch)) continue;

            bool isMain = el.TryGetProperty("is_main", out var m) && m.ValueKind == JsonValueKind.True;
            var langs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (el.TryGetProperty("languages", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var l in arr.EnumerateArray())
                {
                    if (l.ValueKind == JsonValueKind.String && l.GetString() is string ls)
                        langs.Add(ls);
                }
            }

            var meta = new FeedMeta(
                TvUrlGuard.SanitizeHeaderValue(GetString(el, "name"), 60),
                isMain,
                langs,
                TvUrlGuard.SanitizeHeaderValue(GetString(el, "format"), 16));

            byKey[ch + "|" + id] = meta;
            if (isMain || !mainByChannel.ContainsKey(ch))
                mainByChannel[ch] = meta;
        }
        return (byKey, mainByChannel);
    }

    private static void ApplyLogosDoc(JsonDocument logosDoc, List<TvChannel> channels)
    {
        var neededIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ch in channels)
        {
            int at = ch.Id.IndexOf('@');
            if (at > 0)
            {
                neededIds.Add(ch.Id[..at]);
                neededIds.Add(ch.Id[..at] + "|" + ch.Id[(at + 1)..]);
            }
            else
            {
                neededIds.Add(ch.Id);
            }
        }

        var logoMap = new Dictionary<string, (string Url, bool IsNonSvg)>(StringComparer.OrdinalIgnoreCase);
        foreach (var el in logosDoc.RootElement.EnumerateArray())
        {
            if (el.TryGetProperty("in_use", out var iu) && iu.ValueKind == JsonValueKind.False) continue;
            string? ch = GetString(el, "channel");
            if (string.IsNullOrWhiteSpace(ch)) continue;

            string? feed = GetString(el, "feed");
            string key = string.IsNullOrWhiteSpace(feed) ? ch : (ch + "|" + feed);
            if (!neededIds.Contains(key)) continue;

            string? rawUrl = GetString(el, "url");
            if (!TvUrlGuard.IsAllowedRemoteUrl(rawUrl, out var lu) || lu == null || lu.Scheme != Uri.UriSchemeHttps) continue;

            string format = GetString(el, "format") ?? "";
            bool isNonSvg = !format.Equals("SVG", StringComparison.OrdinalIgnoreCase);

            if (!logoMap.TryGetValue(key, out var existing) || (isNonSvg && !existing.IsNonSvg))
                logoMap[key] = (lu.AbsoluteUri, isNonSvg);
        }

        foreach (var ch in channels)
        {
            int at = ch.Id.IndexOf('@');
            if (at > 0)
            {
                string feedKey = ch.Id[..at] + "|" + ch.Id[(at + 1)..];
                if (logoMap.TryGetValue(feedKey, out var feedLogo))
                {
                    ch.Logo = feedLogo.Url;
                    continue;
                }
                if (logoMap.TryGetValue(ch.Id[..at], out var baseLogo))
                    ch.Logo = baseLogo.Url;
            }
            else if (logoMap.TryGetValue(ch.Id, out var logo))
            {
                ch.Logo = logo.Url;
            }
        }
    }

    private static string ReadLabels(JsonElement el)
    {
        if (!el.TryGetProperty("labels", out var arr) || arr.ValueKind != JsonValueKind.Array) return "";
        var list = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is string s && !string.IsNullOrWhiteSpace(s))
                list.Add(TvUrlGuard.SanitizeHeaderValue(s, 30));
        }
        return list.Count > 0 ? string.Join(", ", list) : "";
    }

    private static string? GetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String ? val.GetString() : null;

    private TvCatalogCachePayload? TryReadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return null;
            using var fs = File.OpenRead(_cachePath);
            var parsed = JsonSerializer.Deserialize<TvCatalogCachePayload>(fs);
            if (parsed?.Channels != null) return parsed;
        }
        catch { }
        return null;
    }

    private void TryWriteCache(TvCatalogCachePayload payload)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = _cachePath + ".tmp";
            using (var fs = File.Create(tmp))
                JsonSerializer.Serialize(fs, payload, CacheJsonOptions);
            File.Move(tmp, _cachePath, overwrite: true);
        }
        catch { }
    }
}
