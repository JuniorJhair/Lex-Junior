using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LexBrowser.Core.Tv.Vod.Contracts;

namespace LexBrowser.Core.Tv.Vod.Providers;

/// <summary>
/// Gestor modular de proveedores VOD (Add-ons).
/// Permite añadir, listar y consultar fuentes externas con paralelismo y resiliencia a timeouts.
/// </summary>
public sealed class VodProviderManager
{
    private static readonly Lazy<VodProviderManager> LazyInstance =
        new(() => new VodProviderManager(TvUserStateStore.GetDefaultDirectory()));
    public static VodProviderManager Shared => LazyInstance.Value;

    private readonly string _addonsConfigPath;
    private readonly object _lock = new();
    private readonly Dictionary<string, IVodProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;

    public VodProviderManager(string? configDir = null)
    {
        string dir = configDir ?? TvUserStateStore.GetDefaultDirectory();
        Directory.CreateDirectory(dir);
        _addonsConfigPath = Path.Combine(dir, "vod_addons.json");
    }

    public List<VodManifest> GetInstalledProviders() => GetManifests();

    public void ReleaseMemoryCache()
    {
        lock (_lock)
        {
            _providers.Clear();
            _initialized = false;
        }
    }

    public void EnsureInitialized()
    {
        lock (_lock)
        {
            if (_initialized) return;

            // Registrar proveedor oficial integrado de legalFree
            var legalFree = new LegalFreeVodProvider();
            _providers[legalFree.Manifest.Id] = legalFree;

            // Cargar add-ons guardados por el usuario
            if (File.Exists(_addonsConfigPath))
            {
                try
                {
                    string json = File.ReadAllText(_addonsConfigPath, Encoding.UTF8);
                    var savedList = JsonSerializer.Deserialize<List<VodManifest>>(json);
                    if (savedList != null)
                    {
                        foreach (var m in savedList)
                        {
                            if (!string.IsNullOrEmpty(m.Id) && !_providers.ContainsKey(m.Id))
                            {
                                _providers[m.Id] = new ManifestVodProvider(m);
                            }
                        }
                    }
                }
                catch { }
            }

            _initialized = true;
        }
    }

    public List<VodManifest> GetManifests()
    {
        EnsureInitialized();
        lock (_lock)
        {
            return _providers.Values.Select(p => p.Manifest).ToList();
        }
    }

    public async Task<bool> InstallAddonFromUrlAsync(string manifestUrl, CancellationToken ct = default)
    {
        EnsureInitialized();
        var provider = await ManifestVodProvider.LoadFromUrlAsync(manifestUrl, null, ct).ConfigureAwait(false);
        if (provider == null) return false;

        lock (_lock)
        {
            _providers[provider.Manifest.Id] = provider;
            SaveCustomAddonsLocked();
        }
        return true;
    }

    public bool RemoveAddon(string providerId)
    {
        EnsureInitialized();
        lock (_lock)
        {
            if (_providers.TryGetValue(providerId, out var p))
            {
                if (p.Manifest.IsBuiltIn) return false; // No eliminar proveedores del sistema
                _providers.Remove(providerId);
                SaveCustomAddonsLocked();
                return true;
            }
        }
        return false;
    }

    public async Task<List<StreamSource>> GetAggregatedStreamsAsync(string type, string id, CancellationToken ct = default)
    {
        EnsureInitialized();
        List<IVodProvider> active;
        lock (_lock)
        {
            active = _providers.Values.ToList();
        }

        var tasks = active.Select(async p =>
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(6));
            try
            {
                return await p.GetStreamsAsync(type, id, timeoutCts.Token).ConfigureAwait(false);
            }
            catch
            {
                return new List<StreamSource>();
            }
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var combined = new List<StreamSource>();
        foreach (var r in results)
        {
            if (r != null) combined.AddRange(r);
        }

        return combined;
    }

    private void SaveCustomAddonsLocked()
    {
        try
        {
            var custom = _providers.Values
                .Where(p => !p.Manifest.IsBuiltIn)
                .Select(p => p.Manifest)
                .ToList();

            string json = JsonSerializer.Serialize(custom, new JsonSerializerOptions { WriteIndented = true });
            string tmp = _addonsConfigPath + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, _addonsConfigPath, overwrite: true);
        }
        catch { }
    }
}
