using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LexBrowser.Core.Tv.Addons;

/// <summary>
/// Almacén persistente y seguro para los addons instalados bajo %LocalAppData%\LexBrowser\addons\.
/// Cifra la URL real del manifiesto (que puede contener tokens de configuración) con Windows DPAPI.
/// </summary>
public sealed class LexAddonStore
{
    public const int MaxInstalledAddons = 64;
    public const int MaxRepositories = 24;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly object _lock = new();
    private readonly string _registryPath;
    private readonly ConcurrentDictionary<string, string> _sessionManifestUrls = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _sessionRepoUrls = new(StringComparer.OrdinalIgnoreCase);
    private LexAddonRegistryData? _cache;

    public static LexAddonStore Shared { get; } =
        new(Path.Combine(TvUserStateStore.GetDefaultDirectory(), "addons"));

    public LexAddonStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            directory = Path.Combine(TvUserStateStore.GetDefaultDirectory(), "addons");

        _registryPath = Path.Combine(directory, "addons_registry.json");
    }

    public string RegistryPath => _registryPath;

    public List<LexAddonSummary> GetSummaries()
    {
        lock (_lock)
        {
            var data = EnsureLoaded();
            return data.Addons.Select(ToSummary).ToList();
        }
    }

    public LexAddonSummary? GetSummary(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        string cleanId = id.Trim();
        lock (_lock)
        {
            var data = EnsureLoaded();
            var match = data.Addons.FirstOrDefault(a => string.Equals(a.Id, cleanId, StringComparison.OrdinalIgnoreCase));
            return match != null ? ToSummary(match) : null;
        }
    }

    public List<LexAddonCatalogDescriptor> GetCatalogDescriptors(string? typeFilter = null)
    {
        string? cleanType = string.IsNullOrWhiteSpace(typeFilter) ? null : typeFilter.Trim().ToLowerInvariant();
        var list = new List<LexAddonCatalogDescriptor>();

        lock (_lock)
        {
            var data = EnsureLoaded();
            foreach (var addon in data.Addons)
            {
                if (!addon.Enabled || addon.Manifest == null) continue;

                // Si declara recursos explícitos y ninguno es "catalog", respetar el contrato salvo que tenga catálogos sin recurso
                bool declaresCatalogResource = addon.Manifest.Resources.Count == 0 ||
                    addon.Manifest.Resources.Any(r => r.Name.Equals("catalog", StringComparison.OrdinalIgnoreCase));
                if (!declaresCatalogResource) continue;

                foreach (var cat in addon.Manifest.Catalogs)
                {
                    if (cleanType != null && !cat.Type.Equals(cleanType, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var searchExtra = cat.Extra.FirstOrDefault(e => e.Name.Equals("search", StringComparison.OrdinalIgnoreCase));
                    var genreExtra = cat.Extra.FirstOrDefault(e => e.Name.Equals("genre", StringComparison.OrdinalIgnoreCase));
                    bool hasSkip = cat.Extra.Any(e => e.Name.Equals("skip", StringComparison.OrdinalIgnoreCase));

                    list.Add(new LexAddonCatalogDescriptor
                    {
                        AddonId = addon.Id,
                        AddonName = addon.Manifest.Name,
                        CatalogId = cat.Id,
                        Type = cat.Type,
                        Name = string.IsNullOrWhiteSpace(cat.Name) ? cat.Id : cat.Name,
                        HasSearch = searchExtra != null,
                        SearchRequired = searchExtra?.IsRequired == true,
                        HasSkip = hasSkip,
                        SupportsMeta = ManifestSupportsResource(addon.Manifest, "meta", cat.Type, itemId: null),
                        Genres = genreExtra != null ? new List<string>(genreExtra.Options) : new List<string>()
                    });
                }
            }
        }

        return list;
    }

    public List<string> FindMetaCapableAddonIds(string type, string itemId, string? preferredAddonId)
        => FindCapableAddonIds("meta", type, itemId, preferredAddonId);

    public List<string> FindStreamCapableAddonIds(string type, string itemId, string? preferredAddonId)
        => FindCapableAddonIds("stream", type, itemId, preferredAddonId);

    public List<string> FindSubtitleCapableAddonIds(string type, string itemId, string? preferredAddonId)
        => FindCapableAddonIds("subtitles", type, itemId, preferredAddonId);

    private List<string> FindCapableAddonIds(string resourceName, string type, string itemId, string? preferredAddonId)
    {
        string cleanType = (type ?? "").Trim().ToLowerInvariant();
        string cleanItemId = (itemId ?? "").Trim();
        var candidates = new List<string>();

        lock (_lock)
        {
            var data = EnsureLoaded();
            if (!string.IsNullOrWhiteSpace(preferredAddonId))
            {
                var preferred = data.Addons.FirstOrDefault(a =>
                    a.Enabled && string.Equals(a.Id, preferredAddonId.Trim(), StringComparison.OrdinalIgnoreCase));
                if (preferred?.Manifest != null &&
                    ManifestSupportsResource(preferred.Manifest, resourceName, cleanType, cleanItemId))
                {
                    candidates.Add(preferred.Id);
                }
            }

            foreach (var addon in data.Addons)
            {
                if (!addon.Enabled || addon.Manifest == null) continue;
                if (candidates.Contains(addon.Id, StringComparer.OrdinalIgnoreCase)) continue;
                if (ManifestSupportsResource(addon.Manifest, resourceName, cleanType, cleanItemId))
                    candidates.Add(addon.Id);
            }
        }

        return candidates;
    }

    private static bool ManifestSupportsResource(LexAddonManifest manifest, string resourceName, string type, string? itemId)
    {
        var matches = manifest.Resources
            .Where(r => r.Name.Equals(resourceName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0) return false;

        foreach (var res in matches)
        {
            var effectiveTypes = res.Types.Count > 0 ? res.Types : manifest.Types;
            if (effectiveTypes.Count > 0 && !effectiveTypes.Any(t => t.Equals(type, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (!string.IsNullOrEmpty(itemId))
            {
                var prefixes = res.IdPrefixes.Count > 0 ? res.IdPrefixes : manifest.IdPrefixes;
                if (prefixes.Count > 0 && !prefixes.Any(p => itemId.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    continue;
            }

            return true;
        }

        return false;
    }

    public string? GetManifestUrl(string? id)
        => UnprotectCachedUrl(_sessionManifestUrls, id, (data, cleanId) =>
            data.Addons.FirstOrDefault(a => string.Equals(a.Id, cleanId, StringComparison.OrdinalIgnoreCase))?.ProtectedManifestUrl);

    public string? GetRepositoryUrl(string? repoId)
        => UnprotectCachedUrl(_sessionRepoUrls, repoId, (data, cleanId) =>
            data.Repositories.FirstOrDefault(r => string.Equals(r.Id, cleanId, StringComparison.OrdinalIgnoreCase))?.ProtectedRepoUrl);

    private string? UnprotectCachedUrl(
        ConcurrentDictionary<string, string> sessionCache,
        string? id,
        Func<LexAddonRegistryData, string, string?> protectedSelector)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        string cleanId = id.Trim();

        if (sessionCache.TryGetValue(cleanId, out var memUrl) && !string.IsNullOrEmpty(memUrl))
            return memUrl;

        lock (_lock)
        {
            var data = EnsureLoaded();
            string? protectedVal = protectedSelector(data, cleanId);
            if (string.IsNullOrEmpty(protectedVal))
                return null;

            try
            {
                string decrypted = TvXtreamSecurity.Unprotect(protectedVal);
                if (!string.IsNullOrEmpty(decrypted))
                {
                    sessionCache[cleanId] = decrypted;
                    return decrypted;
                }
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    public (LexAddonSummary Summary, bool WasUpdate) InstallOrUpdate(
        LexAddonManifest manifest,
        string canonicalManifestUrl,
        bool allowLocalLoopback)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrWhiteSpace(manifest.Id))
            throw new ArgumentException("El manifiesto requiere un Id válido.", nameof(manifest));
        if (string.IsNullOrWhiteSpace(canonicalManifestUrl))
            throw new ArgumentException("La URL del manifiesto es obligatoria.", nameof(canonicalManifestUrl));

        DateTime now = DateTime.UtcNow;
        string protectedUrl = TvXtreamSecurity.Protect(canonicalManifestUrl.Trim());
        string displayUrl = LexAddonManifestParser.RedactManifestUrlForDisplay(canonicalManifestUrl);

        lock (_lock)
        {
            var data = EnsureLoaded();
            int idx = data.Addons.FindIndex(a => string.Equals(a.Id, manifest.Id, StringComparison.OrdinalIgnoreCase));

            if (idx >= 0)
            {
                var existing = data.Addons[idx];
                existing.Manifest = manifest;
                existing.AllowLocalLoopback = allowLocalLoopback;
                existing.ProtectedManifestUrl = protectedUrl;
                existing.DisplayUrl = displayUrl;
                existing.UpdatedUtc = now;
                existing.LastCheckUtc = now;
                existing.LastError = null;
                _sessionManifestUrls[existing.Id] = canonicalManifestUrl.Trim();
                SaveJsonAtomic(_registryPath, data);
                return (ToSummary(existing), true);
            }

            if (data.Addons.Count >= MaxInstalledAddons)
                throw new InvalidOperationException($"Se alcanzó el límite máximo de {MaxInstalledAddons} addons instalados.");

            var created = new LexInstalledAddon
            {
                Id = manifest.Id,
                Manifest = manifest,
                Enabled = true,
                AllowLocalLoopback = allowLocalLoopback,
                ProtectedManifestUrl = protectedUrl,
                DisplayUrl = displayUrl,
                InstalledUtc = now,
                UpdatedUtc = now,
                LastCheckUtc = now
            };

            data.Addons.Add(created);
            _sessionManifestUrls[created.Id] = canonicalManifestUrl.Trim();
            SaveJsonAtomic(_registryPath, data);
            return (ToSummary(created), false);
        }
    }

    public LexAddonSummary? SetEnabled(string? id, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        string cleanId = id.Trim();

        lock (_lock)
        {
            var data = EnsureLoaded();
            var match = data.Addons.FirstOrDefault(a => string.Equals(a.Id, cleanId, StringComparison.OrdinalIgnoreCase));
            if (match == null) return null;

            match.Enabled = enabled;
            match.UpdatedUtc = DateTime.UtcNow;
            SaveJsonAtomic(_registryPath, data);
            return ToSummary(match);
        }
    }

    public LexAddonSummary? RecordCheckError(string? id, string? error)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        string cleanId = id.Trim();

        lock (_lock)
        {
            var data = EnsureLoaded();
            var match = data.Addons.FirstOrDefault(a => string.Equals(a.Id, cleanId, StringComparison.OrdinalIgnoreCase));
            if (match == null) return null;

            match.LastCheckUtc = DateTime.UtcNow;
            match.LastError = string.IsNullOrWhiteSpace(error) ? null : TvUrlGuard.SanitizeHeaderValue(error, 200);
            SaveJsonAtomic(_registryPath, data);
            return ToSummary(match);
        }
    }

    public bool Remove(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        string cleanId = id.Trim();
        _sessionManifestUrls.TryRemove(cleanId, out _);

        lock (_lock)
        {
            var data = EnsureLoaded();
            int removed = data.Addons.RemoveAll(a => string.Equals(a.Id, cleanId, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                SaveJsonAtomic(_registryPath, data);
                return true;
            }
            return false;
        }
    }

    public List<LexAddonRepositorySummary> GetRepositories()
    {
        lock (_lock)
        {
            var data = EnsureLoaded();
            return data.Repositories.Select(ToRepoSummary).ToList();
        }
    }

    public LexAddonRepositorySummary? GetRepositorySummary(string? repoId)
    {
        if (string.IsNullOrWhiteSpace(repoId)) return null;
        string cleanId = repoId.Trim();
        lock (_lock)
        {
            var data = EnsureLoaded();
            var match = data.Repositories.FirstOrDefault(r => string.Equals(r.Id, cleanId, StringComparison.OrdinalIgnoreCase));
            return match != null ? ToRepoSummary(match) : null;
        }
    }

    public (LexAddonRepositorySummary Summary, bool WasUpdate) AddOrUpdateRepository(
        LexAddonRepoFeed feed,
        string canonicalRepoUrl,
        bool allowLocalLoopback)
    {
        ArgumentNullException.ThrowIfNull(feed);
        if (string.IsNullOrWhiteSpace(canonicalRepoUrl))
            throw new ArgumentException("La URL del repositorio es obligatoria.", nameof(canonicalRepoUrl));

        DateTime now = DateTime.UtcNow;
        string cleanUrl = canonicalRepoUrl.Trim();
        string protectedUrl = TvXtreamSecurity.Protect(cleanUrl);
        string displayUrl = LexAddonManifestParser.RedactManifestUrlForDisplay(cleanUrl);

        lock (_lock)
        {
            var data = EnsureLoaded();
            int idx = data.Repositories.FindIndex(r =>
                string.Equals(r.Id, feed.Id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.DisplayUrl, displayUrl, StringComparison.OrdinalIgnoreCase));

            if (idx >= 0)
            {
                var existing = data.Repositories[idx];
                existing.Name = feed.Name;
                existing.Description = feed.Description;
                existing.AllowLocalLoopback = allowLocalLoopback;
                existing.ProtectedRepoUrl = protectedUrl;
                existing.DisplayUrl = displayUrl;
                existing.AddonCount = feed.Entries.Count;
                existing.UpdatedUtc = now;
                existing.LastError = null;
                _sessionRepoUrls[existing.Id] = cleanUrl;
                SaveJsonAtomic(_registryPath, data);
                return (ToRepoSummary(existing), true);
            }

            if (data.Repositories.Count >= MaxRepositories)
                throw new InvalidOperationException($"Se alcanzó el límite máximo de {MaxRepositories} repositorios configurados.");

            var created = new LexAddonRepositoryRecord
            {
                Id = feed.Id,
                Name = feed.Name,
                Description = feed.Description,
                AllowLocalLoopback = allowLocalLoopback,
                ProtectedRepoUrl = protectedUrl,
                DisplayUrl = displayUrl,
                AddonCount = feed.Entries.Count,
                AddedUtc = now,
                UpdatedUtc = now
            };

            data.Repositories.Add(created);
            _sessionRepoUrls[created.Id] = cleanUrl;
            SaveJsonAtomic(_registryPath, data);
            return (ToRepoSummary(created), false);
        }
    }

    public LexAddonRepositorySummary? RecordRepositoryError(string? repoId, string? error)
    {
        if (string.IsNullOrWhiteSpace(repoId)) return null;
        string cleanId = repoId.Trim();

        lock (_lock)
        {
            var data = EnsureLoaded();
            var match = data.Repositories.FirstOrDefault(r => string.Equals(r.Id, cleanId, StringComparison.OrdinalIgnoreCase));
            if (match == null) return null;

            match.LastError = string.IsNullOrWhiteSpace(error) ? null : TvUrlGuard.SanitizeHeaderValue(error, 200);
            SaveJsonAtomic(_registryPath, data);
            return ToRepoSummary(match);
        }
    }

    public bool RemoveRepository(string? repoId)
    {
        if (string.IsNullOrWhiteSpace(repoId)) return false;
        string cleanId = repoId.Trim();
        _sessionRepoUrls.TryRemove(cleanId, out _);

        lock (_lock)
        {
            var data = EnsureLoaded();
            int removed = data.Repositories.RemoveAll(r => string.Equals(r.Id, cleanId, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                SaveJsonAtomic(_registryPath, data);
                return true;
            }
            return false;
        }
    }

    public void ReleaseMemoryCache()
    {
        _sessionManifestUrls.Clear();
        _sessionRepoUrls.Clear();
        lock (_lock)
        {
            _cache = null;
        }
    }

    public void ClearAll()
    {
        _sessionManifestUrls.Clear();
        _sessionRepoUrls.Clear();
        lock (_lock)
        {
            _cache = new LexAddonRegistryData();
            try
            {
                if (File.Exists(_registryPath)) File.Delete(_registryPath);
            }
            catch { }
        }
    }

    private static LexAddonSummary ToSummary(LexInstalledAddon addon)
    {
        var m = addon.Manifest ?? new LexAddonManifest();
        return new LexAddonSummary
        {
            Id = addon.Id,
            Name = m.Name,
            Version = m.Version,
            Description = m.Description,
            Logo = m.Logo,
            Protocol = m.Protocol,
            Types = m.Types.ToList(),
            ResourceNames = m.Resources.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            CatalogCount = m.Catalogs.Count,
            Catalogs = m.Catalogs.ToList(),
            Enabled = addon.Enabled,
            AllowLocalLoopback = addon.AllowLocalLoopback,
            Configurable = m.BehaviorHints.Configurable,
            ConfigurationRequired = m.BehaviorHints.ConfigurationRequired,
            DisplayUrl = addon.DisplayUrl,
            InstalledUtc = addon.InstalledUtc,
            UpdatedUtc = addon.UpdatedUtc,
            LastCheckUtc = addon.LastCheckUtc,
            LastError = addon.LastError
        };
    }

    private static LexAddonRepositorySummary ToRepoSummary(LexAddonRepositoryRecord repo) => new()
    {
        Id = repo.Id,
        Name = repo.Name,
        Description = repo.Description,
        AllowLocalLoopback = repo.AllowLocalLoopback,
        DisplayUrl = repo.DisplayUrl,
        AddonCount = repo.AddonCount,
        AddedUtc = repo.AddedUtc,
        UpdatedUtc = repo.UpdatedUtc,
        LastError = repo.LastError
    };

    private LexAddonRegistryData EnsureLoaded()
    {
        if (_cache != null) return _cache;

        try
        {
            if (File.Exists(_registryPath))
            {
                using var fs = File.OpenRead(_registryPath);
                var parsed = JsonSerializer.Deserialize<LexAddonRegistryData>(fs);
                if (parsed != null)
                {
                    parsed.Addons ??= new();
                    parsed.Repositories ??= new();
                    _cache = parsed;
                    return _cache;
                }
            }
        }
        catch
        {
        }

        _cache = new LexAddonRegistryData();
        return _cache;
    }

    private static void SaveJsonAtomic(string path, object data)
    {
        string dir = Path.GetDirectoryName(path) ?? "";
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        string tmp = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}

