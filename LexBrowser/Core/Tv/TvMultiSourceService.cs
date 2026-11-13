using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LexBrowser.Core.Tv;

public sealed class TvSourceStatus
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Tier { get; set; }
    public string Url { get; set; } = "";
    public DateTime? UpdatedUtc { get; set; }
    public int ChannelCount { get; set; }
    public bool FromCache { get; set; }
    public string? LastError { get; set; }
}

public sealed class TvMultiSourceCachePayload
{
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public string ETag { get; set; } = "";
    public string LastModified { get; set; } = "";
    public List<TvChannel> Channels { get; set; } = new();
}

/// <summary>
/// Servicio de gestión y fusión multi-nivel de fuentes de canales de TV en vivo:
/// Nivel 1: Oficial verificado (legalFree live.m3u)
/// Nivel 2: Curado gratuito (Free-TV playlist.m3u8)
/// Nivel 3: Comunitario (iptv-org API)
/// Nivel 4: Listas propias del usuario (tv_playlists.json)
/// </summary>
public sealed class TvMultiSourceService
{
    private static readonly Lazy<TvMultiSourceService> LazyInstance =
        new(() => new TvMultiSourceService(TvUserStateStore.GetDefaultDirectory()));
    public static TvMultiSourceService Shared => LazyInstance.Value;

    public const string LegalFreeLiveUrl = "https://biskyt.github.io/legalFreeIPTVplaylist/live.m3u";
    public const string FreeTvPlaylistUrl = "https://raw.githubusercontent.com/Free-TV/IPTV/master/playlist.m3u8";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    private readonly string _cacheDir;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private List<TvChannel>? _memoryTier1;
    private List<TvChannel>? _memoryTier2;
    private DateTime? _tier1UpdatedUtc;
    private DateTime? _tier2UpdatedUtc;
    private string? _tier1Error;
    private string? _tier2Error;

    public TvMultiSourceService(string cacheDirectory)
    {
        _cacheDir = cacheDirectory;
    }

    public bool HasMemoryLoaded => _memoryTier1 != null || _memoryTier2 != null;

    public void ReleaseMemoryCache()
    {
        lock (_gate)
        {
            _memoryTier1 = null;
            _memoryTier2 = null;
        }
    }

    public void ClearAllCache()
    {
        ReleaseMemoryCache();
        try
        {
            string p1 = Path.Combine(_cacheDir, "tv_source_legalfree_cache.json");
            if (File.Exists(p1)) File.Delete(p1);
            string p2 = Path.Combine(_cacheDir, "tv_source_freetv_cache.json");
            if (File.Exists(p2)) File.Delete(p2);
        }
        catch { }
    }

    public async Task<List<TvChannel>> GetTier1ChannelsAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _memoryTier1 != null)
                return _memoryTier1;

            string cacheFile = Path.Combine(_cacheDir, "tv_source_legalfree_cache.json");
            var result = await LoadOrFetchSourceAsync(
                "legalFree",
                1,
                LegalFreeLiveUrl,
                cacheFile,
                forceRefresh,
                ct).ConfigureAwait(false);

            _memoryTier1 = result.Channels;
            _tier1UpdatedUtc = result.UpdatedUtc;
            _tier1Error = result.Error;
            return _memoryTier1;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<List<TvChannel>> GetTier2ChannelsAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _memoryTier2 != null)
                return _memoryTier2;

            string cacheFile = Path.Combine(_cacheDir, "tv_source_freetv_cache.json");
            var result = await LoadOrFetchSourceAsync(
                "Free-TV",
                2,
                FreeTvPlaylistUrl,
                cacheFile,
                forceRefresh,
                ct).ConfigureAwait(false);

            _memoryTier2 = result.Channels;
            _tier2UpdatedUtc = result.UpdatedUtc;
            _tier2Error = result.Error;
            return _memoryTier2;
        }
        finally
        {
            _gate.Release();
        }
    }

    public List<TvSourceStatus> GetSourcesStatus()
    {
        return new List<TvSourceStatus>
        {
            new()
            {
                Id = "legalFree",
                Name = "Oficial verificado (legalFree)",
                Tier = 1,
                Url = LegalFreeLiveUrl,
                UpdatedUtc = _tier1UpdatedUtc,
                ChannelCount = _memoryTier1?.Count ?? 0,
                FromCache = _tier1UpdatedUtc.HasValue,
                LastError = _tier1Error
            },
            new()
            {
                Id = "Free-TV",
                Name = "Curado gratuito (Free-TV)",
                Tier = 2,
                Url = FreeTvPlaylistUrl,
                UpdatedUtc = _tier2UpdatedUtc,
                ChannelCount = _memoryTier2?.Count ?? 0,
                FromCache = _tier2UpdatedUtc.HasValue,
                LastError = _tier2Error
            },
            new()
            {
                Id = "iptv-org",
                Name = "Comunitario (iptv-org)",
                Tier = 3,
                Url = "https://iptv-org.github.io/api/*",
                UpdatedUtc = DateTime.UtcNow,
                ChannelCount = 0, // Se complementa desde TvCatalogService
                FromCache = true,
                LastError = null
            }
        };
    }

    private async Task<(List<TvChannel> Channels, DateTime UpdatedUtc, string? Error)> LoadOrFetchSourceAsync(
        string provenance,
        int tier,
        string url,
        string cacheFile,
        bool forceRefresh,
        CancellationToken ct)
    {
        TvMultiSourceCachePayload? cachedPayload = null;

        if (!forceRefresh && File.Exists(cacheFile))
        {
            try
            {
                string json = await File.ReadAllTextAsync(cacheFile, Encoding.UTF8, ct).ConfigureAwait(false);
                cachedPayload = JsonSerializer.Deserialize<TvMultiSourceCachePayload>(json);
                if (cachedPayload != null && cachedPayload.Channels.Count > 0 &&
                    (DateTime.UtcNow - cachedPayload.UpdatedUtc) < CacheTtl)
                {
                    return (cachedPayload.Channels, cachedPayload.UpdatedUtc, null);
                }
            }
            catch { }
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (cachedPayload != null)
            {
                if (!string.IsNullOrEmpty(cachedPayload.ETag))
                    req.Headers.TryAddWithoutValidation("If-None-Match", cachedPayload.ETag);
                if (!string.IsNullOrEmpty(cachedPayload.LastModified))
                    req.Headers.TryAddWithoutValidation("If-Modified-Since", cachedPayload.LastModified);
            }

            using var resp = await TvCatalogService.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.NotModified && cachedPayload != null)
            {
                cachedPayload.UpdatedUtc = DateTime.UtcNow;
                TryWriteCache(cacheFile, cachedPayload);
                return (cachedPayload.Channels, cachedPayload.UpdatedUtc, null);
            }

            if (resp.IsSuccessStatusCode)
            {
                string etag = resp.Headers.ETag?.Tag ?? "";
                string lastMod = resp.Content.Headers.LastModified?.ToString("R") ?? "";

                string content = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var parsedChannels = ParseM3uToChannels(content, provenance, tier);

                if (parsedChannels.Count > 0)
                {
                    var payload = new TvMultiSourceCachePayload
                    {
                        UpdatedUtc = DateTime.UtcNow,
                        ETag = etag,
                        LastModified = lastMod,
                        Channels = parsedChannels
                    };

                    TryWriteCache(cacheFile, payload);
                    return (parsedChannels, payload.UpdatedUtc, null);
                }
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Debug.WriteLine($"[TvMultiSource] Error al descargar {provenance}: {ex.Message}");
            if (cachedPayload != null && cachedPayload.Channels.Count > 0)
            {
                return (cachedPayload.Channels, cachedPayload.UpdatedUtc, ex.Message);
            }
            return (new List<TvChannel>(), DateTime.MinValue, ex.Message);
        }

        if (cachedPayload != null)
            return (cachedPayload.Channels, cachedPayload.UpdatedUtc, null);

        return (new List<TvChannel>(), DateTime.MinValue, "No se pudo obtener la fuente.");
    }

    private static void TryWriteCache(string filePath, TvMultiSourceCachePayload payload)
    {
        try
        {
            string tmp = filePath + ".tmp";
            string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, filePath, overwrite: true);
        }
        catch { }
    }

    /// <summary>
    /// Parsea un contenido M3U asignando atributos de Free-TV y legalFree:
    /// Detección de YouTube/externo, geobloqueo (Ⓖ), SD (Ⓢ), YouTube (Ⓨ).
    /// </summary>
    public static List<TvChannel> ParseM3uToChannels(string content, string provenance, int tier, string defaultGroup = "General")
    {
        var result = new List<TvChannel>();
        if (string.IsNullOrWhiteSpace(content)) return result;

        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        TvChannel? current = null;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                current = new TvChannel
                {
                    Tier = tier,
                    Provenance = provenance,
                    Source = provenance
                };

                // Extraer título tras la última coma
                int comma = line.LastIndexOf(',');
                if (comma >= 0 && comma + 1 < line.Length)
                {
                    current.Name = line.Substring(comma + 1).Trim();
                }

                // Parsear atributos clave="valor"
                ParseAttributes(line, current);

                // Detección de indicadores de Free-TV en el nombre
                if (!string.IsNullOrEmpty(current.Name))
                {
                    if (current.Name.Contains("Ⓖ")) current.IsGeoblocked = true;
                    if (current.Name.Contains("Ⓢ")) current.IsSd = true;
                    if (current.Name.Contains("Ⓨ")) current.IsYouTube = true;

                    current.Name = current.Name
                        .Replace("Ⓖ", "")
                        .Replace("Ⓢ", "")
                        .Replace("Ⓨ", "")
                        .Replace("  ", " ")
                        .Trim();
                }
            }
            else if (!line.StartsWith("#", StringComparison.Ordinal) && current != null)
            {
                string rawUrl = line;
                if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
                {
                    current.Url = rawUrl;

                    // Detección de YouTube / Dailymotion externo
                    string host = uri.Host.ToLowerInvariant();
                    if (host.Contains("youtube.com") || host.Contains("youtu.be") || host.Contains("dailymotion.com"))
                    {
                        current.Kind = "external";
                        current.ExternalUrl = rawUrl;
                        current.IsYouTube = true;
                    }
                    else
                    {
                        current.Kind = "stream";
                    }

                    if (string.IsNullOrEmpty(current.Id))
                    {
                        current.Id = $"{provenance.ToLowerInvariant()}.{Math.Abs(NormalizeStreamUrl(rawUrl).GetHashCode()).ToString("X", CultureInfo.InvariantCulture)}";
                    }

                    result.Add(current);
                }
                current = null;
            }
        }

        return result;
    }

    private static void ParseAttributes(string extinf, TvChannel target)
    {
        // Parsear tvg-id, group-title, tvg-country, tvg-language, tvg-logo
        int idx = 8; // tras "#EXTINF:"
        while (idx < extinf.Length)
        {
            while (idx < extinf.Length && char.IsWhiteSpace(extinf[idx])) idx++;
            if (idx >= extinf.Length || extinf[idx] == ',') break;

            int eq = extinf.IndexOf('=', idx);
            if (eq < 0) break;

            string key = extinf.Substring(idx, eq - idx).Trim().ToLowerInvariant();
            idx = eq + 1;
            while (idx < extinf.Length && char.IsWhiteSpace(extinf[idx])) idx++;

            string val = "";
            if (idx < extinf.Length && extinf[idx] == '"')
            {
                idx++;
                int closeQ = extinf.IndexOf('"', idx);
                if (closeQ < 0) break;
                val = extinf.Substring(idx, closeQ - idx);
                idx = closeQ + 1;
            }
            else
            {
                int nextSpace = extinf.IndexOf(' ', idx);
                if (nextSpace < 0) nextSpace = extinf.IndexOf(',', idx);
                if (nextSpace < 0) nextSpace = extinf.Length;
                val = extinf.Substring(idx, nextSpace - idx).Trim();
                idx = nextSpace;
            }

            switch (key)
            {
                case "tvg-id":
                case "id":
                    target.Id = val.Trim();
                    break;
                case "group-title":
                case "group":
                    target.Group = val.Trim();
                    break;
                case "tvg-country":
                case "country":
                    target.Country = val.Trim().ToUpperInvariant();
                    break;
                case "tvg-language":
                case "language":
                    target.Language = val.Trim();
                    break;
                case "tvg-logo":
                case "logo":
                    target.Logo = val.Trim();
                    break;
            }
        }
    }

    public static string NormalizeStreamUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        try
        {
            if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            {
                return $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}".TrimEnd('/').ToLowerInvariant();
            }
        }
        catch { }
        return url.Trim().ToLowerInvariant();
    }

    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        string clean = name
            .Replace("Ⓖ", "")
            .Replace("Ⓢ", "")
            .Replace("Ⓨ", "")
            .Replace("HD", "")
            .Replace("FHD", "")
            .Replace("SD", "")
            .Trim();

        return Cine.TvCineService.NormalizeForSearch(clean);
    }

    /// <summary>
    /// Fusión Multi-Nivel sin duplicados:
    /// Nivel 1 (legalFree) > Nivel 2 (Free-TV) > Nivel 3 (iptv-org) > Nivel 4 (usuario).
    /// El canal conserva la fuente de mejor nivel como primaria y el resto como Alternates.
    /// </summary>
    public static List<TvChannel> MergeChannels(
        IEnumerable<TvChannel> tier1,
        IEnumerable<TvChannel> tier2,
        IEnumerable<TvChannel> tier3,
        IEnumerable<TvChannel>? userChannels = null)
    {
        var result = new List<TvChannel>();
        var idIndex = new Dictionary<string, TvChannel>(StringComparer.OrdinalIgnoreCase);
        var urlIndex = new Dictionary<string, TvChannel>(StringComparer.OrdinalIgnoreCase);
        var nameCountryIndex = new Dictionary<string, TvChannel>(StringComparer.OrdinalIgnoreCase);

        // Agrupar en orden de prioridad de nivel (Tier 1 primero, luego 2, luego 3, luego 4)
        var sourceQueues = new[]
        {
            (tier1 ?? Array.Empty<TvChannel>()).OrderBy(c => c.Name),
            (tier2 ?? Array.Empty<TvChannel>()).OrderBy(c => c.Name),
            (tier3 ?? Array.Empty<TvChannel>()).OrderBy(c => c.Name),
            (userChannels ?? Array.Empty<TvChannel>()).OrderBy(c => c.Name)
        };

        foreach (var queue in sourceQueues)
        {
            foreach (var ch in queue)
            {
                string normId = (ch.Id ?? "").Trim().ToLowerInvariant();
                string normUrl = NormalizeStreamUrl(ch.Url);
                string normNameKey = $"{NormalizeName(ch.Name)}:{ch.Country.ToUpperInvariant()}";

                TvChannel? existing = null;

                // 1. Clave primaria: tvg-id
                if (!string.IsNullOrEmpty(normId) && idIndex.TryGetValue(normId, out var byId))
                {
                    existing = byId;
                }
                // 2. Clave secundaria: URL normalizada
                else if (!string.IsNullOrEmpty(normUrl) && urlIndex.TryGetValue(normUrl, out var byUrl))
                {
                    existing = byUrl;
                }
                // 3. Clave terciaria: Nombre normalizado + País
                else if (normNameKey.Length > 3 && nameCountryIndex.TryGetValue(normNameKey, out var byName))
                {
                    existing = byName;
                }

                if (existing != null)
                {
                    // Fusionar stream como alterno si no existe ya
                    string existingNormUrl = NormalizeStreamUrl(existing.Url);
                    bool urlExists = existingNormUrl.Equals(normUrl, StringComparison.OrdinalIgnoreCase) ||
                                     existing.Alternates.Any(a => NormalizeStreamUrl(a.Url).Equals(normUrl, StringComparison.OrdinalIgnoreCase));

                    if (!urlExists && !string.IsNullOrEmpty(ch.Url))
                    {
                        existing.Alternates.Add(new TvStreamAlternate
                        {
                            Url = ch.Url,
                            Quality = ch.Quality,
                            Referrer = ch.Referrer,
                            UserAgent = ch.UserAgent,
                            Tier = ch.Tier,
                            Provenance = ch.Provenance
                        });
                    }

                    // Enriquecer metadatos si al primario le faltan
                    if (string.IsNullOrEmpty(existing.Logo) && !string.IsNullOrEmpty(ch.Logo))
                        existing.Logo = ch.Logo;
                    if (string.IsNullOrEmpty(existing.Country) && !string.IsNullOrEmpty(ch.Country))
                        existing.Country = ch.Country;
                    if (string.IsNullOrEmpty(existing.Language) && !string.IsNullOrEmpty(ch.Language))
                        existing.Language = ch.Language;

                    // Conservar indicadores si vienen de una fuente con ellos
                    if (ch.IsGeoblocked) existing.IsGeoblocked = true;
                    if (ch.IsSd) existing.IsSd = true;
                    if (ch.IsYouTube) existing.IsYouTube = true;
                }
                else
                {
                    // Canal nuevo
                    var copy = new TvChannel
                    {
                        Id = ch.Id ?? "",
                        Name = ch.Name,
                        Url = ch.Url,
                        Group = ch.Group,
                        Language = ch.Language,
                        Country = ch.Country,
                        CountryName = ch.CountryName,
                        Logo = ch.Logo,
                        Quality = ch.Quality,
                        Referrer = ch.Referrer,
                        UserAgent = ch.UserAgent,
                        Tags = ch.Tags,
                        Source = ch.Source,
                        Tier = ch.Tier,
                        Provenance = ch.Provenance,
                        Kind = ch.Kind,
                        ExternalUrl = ch.ExternalUrl,
                        IsGeoblocked = ch.IsGeoblocked,
                        IsSd = ch.IsSd,
                        IsYouTube = ch.IsYouTube,
                        Alternates = new List<TvStreamAlternate>(ch.Alternates)
                    };

                    result.Add(copy);

                    if (!string.IsNullOrEmpty(normId)) idIndex[normId] = copy;
                    if (!string.IsNullOrEmpty(normUrl)) urlIndex[normUrl] = copy;
                    if (normNameKey.Length > 3) nameCountryIndex[normNameKey] = copy;
                }
            }
        }

        return result;
    }
}
