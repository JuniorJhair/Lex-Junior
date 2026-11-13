using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LexBrowser.Core.Tv.Vod.Contracts;

namespace LexBrowser.Core.Tv.Vod.Metadata;

public sealed class TmdbMetaService : IMetaService
{
    private static readonly Lazy<TmdbMetaService> LazyInstance = new(() => new TmdbMetaService());
    public static TmdbMetaService Shared => LazyInstance.Value;

    public const string TmdbApiBase = "https://api.themoviedb.org/3";
    public const string TmdbImageBase = "https://image.tmdb.org/t/p/w500";
    public const string TmdbBackdropBase = "https://image.tmdb.org/t/p/w1280";

    public const string TmdbAttribution = "This product uses the TMDB API but is not endorsed or certified by TMDB. Streaming provider data powered by JustWatch.";

    // Identificadores de compañías de producción oficiales verificados en TMDB
    public static readonly Dictionary<string, (int Id, string DisplayName)> ProductionCompanies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["disney"] = (2, "Walt Disney Pictures"),
        ["disney_anim"] = (6125, "Walt Disney Animation Studios"),
        ["pixar"] = (3, "Pixar Animation Studios"),
        ["marvel"] = (420, "Marvel Studios"),
        ["lucasfilm"] = (1, "Lucasfilm Ltd.")
    };

    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);
    private readonly string _cachePath;
    private readonly string _keyPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _apiKey;
    private Dictionary<string, (DateTime UpdatedUtc, string Json)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private bool _cacheLoaded;

    public string ServiceId => "tmdb";
    public string ServiceName => "The Movie Database (Disney / Pixar / Marvel)";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(GetApiKey());

    public TmdbMetaService(string? customDirectory = null)
    {
        string dir = customDirectory ?? TvUserStateStore.GetDefaultDirectory();
        Directory.CreateDirectory(dir);
        _cachePath = Path.Combine(dir, "tmdb_cache.json");
        _keyPath = Path.Combine(dir, "tmdb_key.dat");
    }

    public string? GetApiKey()
    {
        if (_apiKey != null) return _apiKey;
        if (File.Exists(_keyPath))
        {
            try
            {
                byte[] encrypted = File.ReadAllBytes(_keyPath);
                byte[] decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                _apiKey = Encoding.UTF8.GetString(decrypted).Trim();
                return _apiKey;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TmdbMetaService] Error al descifrar DPAPI: {ex.Message}");
            }
        }
        return null;
    }

    public void SetApiKey(string? key)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                _apiKey = null;
                if (File.Exists(_keyPath)) File.Delete(_keyPath);
                return;
            }

            string clean = key.Trim();
            _apiKey = clean;
            try
            {
                byte[] raw = Encoding.UTF8.GetBytes(clean);
                byte[] encrypted = ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(_keyPath, encrypted);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TmdbMetaService] Error al guardar DPAPI: {ex.Message}");
                // Fallback en texto local si DPAPI no está soportado en el entorno
                File.WriteAllText(_keyPath + ".plain", clean, Encoding.UTF8);
            }
        }
    }

    public string GetMaskedKey()
    {
        string? k = GetApiKey();
        if (string.IsNullOrWhiteSpace(k)) return "";
        if (k.Length <= 8) return new string('*', k.Length);
        return k.Substring(0, Math.Min(13, k.Length - 4)) + "..." + k.Substring(k.Length - 4);
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
        string? key = GetApiKey();
        if (string.IsNullOrEmpty(key)) return new List<MediaItem>();

        string studioKey = categoryOrStudio ?? "disney";
        int companyId = 2; // Default Disney
        string companyName = "Disney";
        if (ProductionCompanies.TryGetValue(studioKey, out var comp))
        {
            companyId = comp.Id;
            companyName = comp.DisplayName;
        }
        else if (int.TryParse(studioKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numId))
        {
            companyId = numId;
            var match = ProductionCompanies.Values.FirstOrDefault(v => v.Id == numId);
            companyName = string.IsNullOrEmpty(match.DisplayName) ? "Estudio" : match.DisplayName;
        }

        string today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var queryParams = new List<string>
        {
            $"api_key={key}",
            "language=es-MX",
            "sort_by=primary_release_date.desc",
            $"primary_release_date.lte={today}",
            $"with_companies={companyId}",
            $"page={Math.Max(1, page)}"
        };

        if (year.HasValue && year > 1900)
        {
            queryParams.Add($"primary_release_year={year.Value}");
        }

        string url = $"{TmdbApiBase}/discover/movie?{string.Join("&", queryParams)}";
        string cacheKey = $"discover:{studioKey}:{year}:{genre}:{page}";

        string? json = await GetCachedOrFetchJsonAsync(cacheKey, url, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(json)) return new List<MediaItem>();

        return ParseDiscoverResults(json, companyName);
    }

    public async Task<MediaItem?> GetDetailsAsync(string id, CancellationToken ct)
    {
        string? key = GetApiKey();
        if (string.IsNullOrEmpty(key)) return null;

        string tmdbId = id.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase)
            ? id.Substring(5)
            : id;

        if (!int.TryParse(tmdbId, out int numericId)) return null;

        // 1. Datos principales
        string movieUrl = $"{TmdbApiBase}/movie/{numericId}?api_key={key}&language=es-MX";
        string movieCacheKey = $"movie:{numericId}";
        string? movieJson = await GetCachedOrFetchJsonAsync(movieCacheKey, movieUrl, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(movieJson)) return null;

        // 2. Proveedores para Perú (Watch Providers PE)
        string provUrl = $"{TmdbApiBase}/movie/{numericId}/watch/providers?api_key={key}";
        string provCacheKey = $"providers:{numericId}";
        string? provJson = await GetCachedOrFetchJsonAsync(provCacheKey, provUrl, ct).ConfigureAwait(false);

        return ParseMovieDetails(movieJson, provJson, tmdbId);
    }

    public static List<MediaItem> ParseDiscoverResults(string json, string sourceName)
    {
        var result = new List<MediaItem>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var resArray) || resArray.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var el in resArray.EnumerateArray())
            {
                int id = el.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;
                if (id <= 0) continue;

                string title = el.TryGetProperty("title", out var tProp) ? (tProp.GetString() ?? "") : "";
                string origTitle = el.TryGetProperty("original_title", out var otProp) ? (otProp.GetString() ?? "") : "";
                string overview = el.TryGetProperty("overview", out var ovProp) ? (ovProp.GetString() ?? "") : "";
                string posterPath = el.TryGetProperty("poster_path", out var pProp) ? (pProp.GetString() ?? "") : "";
                string releaseDate = el.TryGetProperty("release_date", out var rdProp) ? (rdProp.GetString() ?? "") : "";
                if (!string.IsNullOrEmpty(releaseDate) && DateTime.TryParse(releaseDate, out var rDate) && rDate.Date > DateTime.UtcNow.Date)
                {
                    continue; // Omitir estrenos futuros aún no disponibles
                }

                string backdropPath = el.TryGetProperty("backdrop_path", out var bgProp) ? (bgProp.GetString() ?? "") : "";
                double rating = el.TryGetProperty("vote_average", out var vProp) ? Math.Round(vProp.GetDouble(), 1) : 0.0;

                int? year = null;
                if (!string.IsNullOrEmpty(releaseDate) && releaseDate.Length >= 4 && int.TryParse(releaseDate.Substring(0, 4), out int yVal))
                {
                    year = yVal;
                }

                result.Add(new MediaItem
                {
                    Id = $"tmdb:{id}",
                    Type = "movie",
                    Title = title,
                    OriginalTitle = origTitle,
                    Year = year,
                    Synopsis = overview,
                    PosterUrl = !string.IsNullOrEmpty(posterPath) ? $"{TmdbImageBase}{posterPath}" : null,
                    BackdropUrl = !string.IsNullOrEmpty(backdropPath) ? $"{TmdbBackdropBase}{backdropPath}" : null,
                    Rating = rating,
                    SourceName = sourceName,
                    Attribution = TmdbAttribution
                });
            }
        }
        catch { }
        return result;
    }

    public static MediaItem? ParseMovieDetails(string movieJson, string? provJson, string id)
    {
        try
        {
            using var doc = JsonDocument.Parse(movieJson);
            var root = doc.RootElement;

            string title = root.TryGetProperty("title", out var tProp) ? (tProp.GetString() ?? "") : "";
            string origTitle = root.TryGetProperty("original_title", out var otProp) ? (otProp.GetString() ?? "") : "";
            string overview = root.TryGetProperty("overview", out var ovProp) ? (ovProp.GetString() ?? "") : "";
            string posterPath = root.TryGetProperty("poster_path", out var pProp) ? (pProp.GetString() ?? "") : "";
            string backdropPath = root.TryGetProperty("backdrop_path", out var bProp) ? (bProp.GetString() ?? "") : "";
            string releaseDate = root.TryGetProperty("release_date", out var rdProp) ? (rdProp.GetString() ?? "") : "";
            int runtime = root.TryGetProperty("runtime", out var rProp) && rProp.TryGetInt32(out int rVal) ? rVal : 0;
            double rating = root.TryGetProperty("vote_average", out var vProp) ? Math.Round(vProp.GetDouble(), 1) : 0.0;

            int? year = null;
            if (!string.IsNullOrEmpty(releaseDate) && releaseDate.Length >= 4 && int.TryParse(releaseDate.Substring(0, 4), out int yVal))
            {
                year = yVal;
            }

            var genres = new List<string>();
            if (root.TryGetProperty("genres", out var gArray) && gArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var gEl in gArray.EnumerateArray())
                {
                    if (gEl.TryGetProperty("name", out var gnProp))
                    {
                        string gName = gnProp.GetString() ?? "";
                        if (!string.IsNullOrEmpty(gName)) genres.Add(gName);
                    }
                }
            }

            // Parsear Watch Providers para Perú (results.PE)
            var providersList = new List<WatchProviderInfo>();
            string? officialWatchUrl = null;

            if (!string.IsNullOrEmpty(provJson))
            {
                try
                {
                    using var pDoc = JsonDocument.Parse(provJson);
                    if (pDoc.RootElement.TryGetProperty("results", out var rObj) &&
                        rObj.TryGetProperty("PE", out var peObj))
                    {
                        if (peObj.TryGetProperty("link", out var linkProp))
                        {
                            officialWatchUrl = linkProp.GetString();
                        }

                        void ExtractProviders(string typeKey)
                        {
                            if (peObj.TryGetProperty(typeKey, out var arr) && arr.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var pEl in arr.EnumerateArray())
                                {
                                    string pName = pEl.TryGetProperty("provider_name", out var pnProp) ? (pnProp.GetString() ?? "") : "";
                                    string pLogo = pEl.TryGetProperty("logo_path", out var plProp) ? (plProp.GetString() ?? "") : "";
                                    int pId = pEl.TryGetProperty("provider_id", out var piProp) ? piProp.GetInt32() : 0;

                                    providersList.Add(new WatchProviderInfo
                                    {
                                        Name = pName,
                                        ProviderId = pId,
                                        LogoUrl = !string.IsNullOrEmpty(pLogo) ? $"{TmdbImageBase}{pLogo}" : null,
                                        Type = typeKey,
                                        OfficialUrl = officialWatchUrl
                                    });
                                }
                            }
                        }

                        ExtractProviders("flatrate");
                        ExtractProviders("rent");
                        ExtractProviders("buy");
                    }
                }
                catch { }
            }

            return new MediaItem
            {
                Id = $"tmdb:{id}",
                Type = "movie",
                Title = title,
                OriginalTitle = origTitle,
                Year = year,
                RuntimeMin = runtime,
                Genres = genres,
                Synopsis = overview,
                PosterUrl = !string.IsNullOrEmpty(posterPath) ? $"{TmdbImageBase}{posterPath}" : null,
                BackdropUrl = !string.IsNullOrEmpty(backdropPath) ? $"{TmdbBackdropBase}{backdropPath}" : null,
                Rating = rating,
                WatchProviders = providersList,
                OfficialWatchUrl = officialWatchUrl,
                Attribution = TmdbAttribution
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> GetCachedOrFetchJsonAsync(string cacheKey, string url, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureCacheLoaded();

            if (_cache.TryGetValue(cacheKey, out var hit) && (DateTime.UtcNow - hit.UpdatedUtc) < CacheTtl)
            {
                return hit.Json;
            }

            // Descarga condicional
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
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
