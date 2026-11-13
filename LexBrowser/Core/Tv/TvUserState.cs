using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace LexBrowser.Core.Tv;

public sealed class TvUserPlaylist
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public DateTime AddedUtc { get; set; }
    public List<TvChannel> Channels { get; set; } = new();
}

public sealed class TvPlaylistSummary
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public DateTime AddedUtc { get; set; }
    public int ChannelCount { get; set; }
}

public sealed class TvDeadMark
{
    public string Kind { get; set; } = "dead";
    public DateTime FailedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class TvPrefsData
{
    public List<string> Favorites { get; set; } = new();
    public Dictionary<string, TvDeadMark> DeadStreams { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class TvPlaylistsData
{
    public List<TvUserPlaylist> Playlists { get; set; } = new();
}

public sealed class TvUserStateData
{
    public List<TvUserPlaylist> Playlists { get; set; } = new();
    public List<string> Favorites { get; set; } = new();
    public Dictionary<string, TvDeadMark> DeadStreams { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Persistencia separada bajo demanda: tv_prefs.json (favoritos + marcas tipificadas/LRU con debounce 500 ms)
/// y tv_playlists.json (listas propias hasta 200 000 canales). Migra tv_user_state.json -> .bak.
/// Sin timers persistentes ni datos en RAM tras ReleaseMemoryCache().
/// </summary>
public sealed class TvUserStateStore
{
    public const int MaxPlaylists = 32;
    public const int MaxDeadMarks = 8000;
    public const int MaxTotalUserChannels = 200_000;
    private const int DebounceMs = 500;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly object _lock = new();
    private readonly string _prefsPath;
    private readonly string _playlistsPath;
    private readonly string _legacyPath;

    private TvPrefsData? _prefsCache;
    private TvPlaylistsData? _playlistsCache;
    private bool _prefsDirty;
    private Timer? _debounceTimer;

    public static TvUserStateStore Shared { get; } = new(GetDefaultDirectory());

    public TvUserStateStore(string directory)
    {
        _prefsPath = Path.Combine(directory, "tv_prefs.json");
        _playlistsPath = Path.Combine(directory, "tv_playlists.json");
        _legacyPath = Path.Combine(directory, "tv_user_state.json");
    }

    public static string GetDefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LexBrowser");

    public TvUserStateData GetSnapshot()
    {
        lock (_lock)
        {
            var prefs = EnsurePrefsLoaded();
            var playlists = EnsurePlaylistsLoaded();
            return new TvUserStateData
            {
                Playlists = playlists.Playlists.Select(p => new TvUserPlaylist
                {
                    Id = p.Id,
                    Name = p.Name,
                    SourceUrl = p.SourceUrl,
                    AddedUtc = p.AddedUtc,
                    Channels = p.Channels.ToList()
                }).ToList(),
                Favorites = prefs.Favorites.ToList(),
                DeadStreams = new Dictionary<string, TvDeadMark>(prefs.DeadStreams, StringComparer.OrdinalIgnoreCase)
            };
        }
    }

    public TvPrefsData GetPrefsSnapshot(DateTime? nowUtc = null)
    {
        lock (_lock)
        {
            var prefs = EnsurePrefsLoaded(nowUtc);
            return new TvPrefsData
            {
                Favorites = prefs.Favorites.ToList(),
                DeadStreams = new Dictionary<string, TvDeadMark>(prefs.DeadStreams, StringComparer.OrdinalIgnoreCase)
            };
        }
    }

    public List<TvPlaylistSummary> GetPlaylistSummaries()
    {
        lock (_lock)
        {
            var playlists = EnsurePlaylistsLoaded();
            return playlists.Playlists.Select(p => new TvPlaylistSummary
            {
                Id = p.Id,
                Name = p.Name,
                SourceUrl = p.SourceUrl,
                AddedUtc = p.AddedUtc,
                ChannelCount = p.Channels.Count
            }).ToList();
        }
    }

    public TvUserPlaylist? GetPlaylistById(string? playlistId)
    {
        if (string.IsNullOrWhiteSpace(playlistId)) return null;
        lock (_lock)
        {
            var playlists = EnsurePlaylistsLoaded();
            return playlists.Playlists.FirstOrDefault(p => p.Id.Equals(playlistId.Trim(), StringComparison.OrdinalIgnoreCase));
        }
    }

    public List<string> ToggleFavorite(string channelKey)
    {
        if (string.IsNullOrWhiteSpace(channelKey)) return GetPrefsSnapshot().Favorites;
        string clean = channelKey.Trim();
        lock (_lock)
        {
            var prefs = EnsurePrefsLoaded();
            int idx = prefs.Favorites.FindIndex(f => string.Equals(f, clean, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) prefs.Favorites.RemoveAt(idx);
            else prefs.Favorites.Add(clean);
            _prefsDirty = false;
            SaveJsonAtomic(_prefsPath, prefs);
            return prefs.Favorites.ToList();
        }
    }

    public TvUserPlaylist AddOrUpdatePlaylist(string name, string? sourceUrl, List<TvChannel> channels)
    {
        string cleanName = TvUrlGuard.SanitizeHeaderValue(name, 60);
        if (cleanName.Length == 0) cleanName = "Mi lista IPTV";
        string cleanUrl = sourceUrl ?? "";

        lock (_lock)
        {
            var data = EnsurePlaylistsLoaded();
            TvUserPlaylist? existing = !string.IsNullOrEmpty(cleanUrl)
                ? data.Playlists.FirstOrDefault(p => string.Equals(p.SourceUrl, cleanUrl, StringComparison.OrdinalIgnoreCase))
                : null;

            int existingCountOther = data.Playlists
                .Where(p => !ReferenceEquals(p, existing))
                .Sum(p => p.Channels.Count);

            if (existingCountOther + channels.Count > MaxTotalUserChannels)
            {
                throw new InvalidOperationException(
                    $"Tope máximo excedido: {MaxTotalUserChannels:N0} canales totales permitidos en listas de usuario (actual: {existingCountOther:N0}, nuevos: {channels.Count:N0}).");
            }

            string playlistId = existing?.Id ?? ("pl_" + Guid.NewGuid().ToString("N")[..10]);
            var numbered = AssignPlaylistNumbers(channels, playlistId, cleanName);

            if (existing != null)
            {
                existing.Name = cleanName;
                existing.AddedUtc = DateTime.UtcNow;
                existing.Channels = numbered;
            }
            else
            {
                if (data.Playlists.Count >= MaxPlaylists)
                    data.Playlists.RemoveAt(0);

                existing = new TvUserPlaylist
                {
                    Id = playlistId,
                    Name = cleanName,
                    SourceUrl = cleanUrl,
                    AddedUtc = DateTime.UtcNow,
                    Channels = numbered
                };
                data.Playlists.Add(existing);
            }

            SaveJsonAtomic(_playlistsPath, data);
            return existing;
        }
    }

    public bool RemovePlaylist(string playlistId)
    {
        if (string.IsNullOrWhiteSpace(playlistId)) return false;
        lock (_lock)
        {
            var data = EnsurePlaylistsLoaded();
            int removed = data.Playlists.RemoveAll(p => string.Equals(p.Id, playlistId, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) SaveJsonAtomic(_playlistsPath, data);
            return removed > 0;
        }
    }

    /// <summary>
    /// Registra fallo tipificado con caducidad: dead (72h), restricted (24h), codec (7d), transient (no se persiste).
    /// Escritura atómica con debounce de 500 ms.
    /// </summary>
    public bool MarkFailure(string url, string? kind, string? reason, DateTime? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        string normalizedKind = NormalizeFailureKind(kind);
        if (normalizedKind == "transient") return false;

        string cleanUrl = url.Trim();
        DateTime now = nowUtc ?? DateTime.UtcNow;
        TimeSpan ttl = GetFailureTtl(normalizedKind);

        lock (_lock)
        {
            var prefs = EnsurePrefsLoaded(now);
            PurgeExpiredInternal(prefs, now);

            if (prefs.DeadStreams.Count >= MaxDeadMarks && !prefs.DeadStreams.ContainsKey(cleanUrl))
            {
                int removeCount = Math.Max(1, prefs.DeadStreams.Count - MaxDeadMarks + 1);
                var oldest = prefs.DeadStreams
                    .OrderBy(k => k.Value.LastSeenUtc != default ? k.Value.LastSeenUtc : k.Value.FailedUtc)
                    .Take(Math.Max(500, removeCount))
                    .Select(k => k.Key)
                    .ToList();
                foreach (var k in oldest)
                {
                    if (prefs.DeadStreams.Count < MaxDeadMarks) break;
                    prefs.DeadStreams.Remove(k);
                }
            }

            prefs.DeadStreams[cleanUrl] = new TvDeadMark
            {
                Kind = normalizedKind,
                FailedUtc = now,
                LastSeenUtc = now,
                ExpiresUtc = now + ttl,
                Reason = TvUrlGuard.SanitizeHeaderValue(reason, 80)
            };

            ScheduleDebouncedPrefsSave();
            return true;
        }
    }

    public void MarkDead(string url, string? reason)
    {
        if (MarkFailure(url, "dead", reason))
            Flush();
    }

    public void UnmarkDead(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        lock (_lock)
        {
            var prefs = EnsurePrefsLoaded();
            if (prefs.DeadStreams.Remove(url.Trim()))
            {
                _prefsDirty = false;
                SaveJsonAtomic(_prefsPath, prefs);
            }
        }
    }

    public void ClearDead()
    {
        lock (_lock)
        {
            var prefs = EnsurePrefsLoaded();
            if (prefs.DeadStreams.Count == 0) return;
            prefs.DeadStreams.Clear();
            _prefsDirty = false;
            SaveJsonAtomic(_prefsPath, prefs);
        }
    }

    public void Flush()
    {
        lock (_lock)
        {
            try { _debounceTimer?.Dispose(); } catch { }
            _debounceTimer = null;

            if (_prefsDirty && _prefsCache != null)
            {
                SaveJsonAtomic(_prefsPath, _prefsCache);
                _prefsDirty = false;
            }
        }
    }

    public void ReleaseMemoryCache()
    {
        Flush();
        lock (_lock)
        {
            _prefsCache = null;
            _playlistsCache = null;
        }
    }

    internal static string NormalizeFailureKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return "dead";
        return kind.Trim().ToLowerInvariant() switch
        {
            "transient" => "transient",
            "restricted" or "geo" or "auth" or "403" => "restricted",
            "codec" => "codec",
            _ => "dead"
        };
    }

    internal static TimeSpan GetFailureTtl(string normalizedKind) => normalizedKind switch
    {
        "restricted" => TimeSpan.FromHours(24),
        "codec" => TimeSpan.FromDays(7),
        _ => TimeSpan.FromHours(72)
    };

    private static List<TvChannel> AssignPlaylistNumbers(List<TvChannel> channels, string playlistId, string playlistName)
    {
        var taken = new HashSet<int>();
        foreach (var ch in channels)
        {
            if (ch.Number > 0) taken.Add(ch.Number);
        }

        int autoNum = 1;
        var list = new List<TvChannel>(channels.Count);
        foreach (var ch in channels)
        {
            int num = ch.Number;
            if (num <= 0)
            {
                while (taken.Contains(autoNum)) autoNum++;
                num = autoNum++;
                taken.Add(num);
            }

            list.Add(new TvChannel
            {
                Number = num,
                Id = string.IsNullOrEmpty(ch.Id) ? $"{playlistId}:{ch.Url}" : $"{playlistId}:{ch.Id}",
                Name = ch.Name,
                Url = ch.Url,
                Group = string.IsNullOrEmpty(ch.Group) ? "Sin categoría" : ch.Group,
                Language = ch.Language,
                Country = ch.Country,
                CountryName = ch.CountryName,
                Logo = ch.Logo,
                Quality = ch.Quality,
                Referrer = ch.Referrer,
                UserAgent = ch.UserAgent,
                Tags = ch.Tags,
                Source = playlistName,
                Alternates = ch.Alternates?.ToList() ?? new List<TvStreamAlternate>()
            });
        }
        return list;
    }

    private TvPrefsData EnsurePrefsLoaded(DateTime? nowUtc = null)
    {
        DateTime now = nowUtc ?? DateTime.UtcNow;
        if (_prefsCache != null)
        {
            PurgeExpiredInternal(_prefsCache, now);
            return _prefsCache;
        }

        MigrateLegacyIfNeeded();

        try
        {
            if (File.Exists(_prefsPath))
            {
                using var fs = File.OpenRead(_prefsPath);
                var parsed = JsonSerializer.Deserialize<TvPrefsData>(fs);
                if (parsed != null)
                {
                    parsed.Favorites ??= new();
                    parsed.DeadStreams = parsed.DeadStreams != null
                        ? new Dictionary<string, TvDeadMark>(parsed.DeadStreams, StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, TvDeadMark>(StringComparer.OrdinalIgnoreCase);

                    if (PurgeExpiredInternal(parsed, now) > 0)
                        SaveJsonAtomic(_prefsPath, parsed);

                    _prefsCache = parsed;
                    return _prefsCache;
                }
            }
        }
        catch { }

        _prefsCache = new TvPrefsData();
        return _prefsCache;
    }

    private TvPlaylistsData EnsurePlaylistsLoaded()
    {
        if (_playlistsCache != null) return _playlistsCache;

        MigrateLegacyIfNeeded();

        try
        {
            if (File.Exists(_playlistsPath))
            {
                using var fs = File.OpenRead(_playlistsPath);
                var parsed = JsonSerializer.Deserialize<TvPlaylistsData>(fs);
                if (parsed != null)
                {
                    parsed.Playlists ??= new();
                    _playlistsCache = parsed;
                    return _playlistsCache;
                }
            }
        }
        catch { }

        _playlistsCache = new TvPlaylistsData();
        return _playlistsCache;
    }

    private void MigrateLegacyIfNeeded()
    {
        try
        {
            if (!File.Exists(_legacyPath)) return;
            if (File.Exists(_prefsPath) && File.Exists(_playlistsPath))
            {
                File.Move(_legacyPath, _legacyPath + ".bak", overwrite: true);
                return;
            }

            TvUserStateData? legacy;
            using (var fs = File.OpenRead(_legacyPath))
                legacy = JsonSerializer.Deserialize<TvUserStateData>(fs);

            if (legacy != null)
            {
                var prefs = new TvPrefsData
                {
                    Favorites = legacy.Favorites ?? new(),
                    DeadStreams = new Dictionary<string, TvDeadMark>(StringComparer.OrdinalIgnoreCase)
                };

                DateTime now = DateTime.UtcNow;
                if (legacy.DeadStreams != null)
                {
                    foreach (var kv in legacy.DeadStreams)
                    {
                        if (kv.Value == null) continue;
                        string kind = NormalizeFailureKind(kv.Value.Kind);
                        DateTime failed = kv.Value.FailedUtc != default ? kv.Value.FailedUtc : now;
                        DateTime expires = kv.Value.ExpiresUtc != default ? kv.Value.ExpiresUtc : failed + GetFailureTtl(kind);
                        if (expires <= now) continue;

                        prefs.DeadStreams[kv.Key] = new TvDeadMark
                        {
                            Kind = kind,
                            FailedUtc = failed,
                            LastSeenUtc = kv.Value.LastSeenUtc != default ? kv.Value.LastSeenUtc : failed,
                            ExpiresUtc = expires,
                            Reason = TvUrlGuard.SanitizeHeaderValue(kv.Value.Reason, 80)
                        };
                    }
                }

                var playlists = new TvPlaylistsData
                {
                    Playlists = legacy.Playlists ?? new()
                };

                if (!File.Exists(_prefsPath)) SaveJsonAtomic(_prefsPath, prefs);
                if (!File.Exists(_playlistsPath)) SaveJsonAtomic(_playlistsPath, playlists);
            }

            File.Move(_legacyPath, _legacyPath + ".bak", overwrite: true);
        }
        catch { }
    }

    private static int PurgeExpiredInternal(TvPrefsData prefs, DateTime now)
    {
        if (prefs.DeadStreams.Count == 0) return 0;
        var expired = new List<string>();
        foreach (var kv in prefs.DeadStreams)
        {
            var m = kv.Value;
            if (m == null) { expired.Add(kv.Key); continue; }
            DateTime exp = m.ExpiresUtc != default
                ? m.ExpiresUtc
                : (m.FailedUtc != default ? m.FailedUtc + TimeSpan.FromHours(72) : DateTime.MinValue);
            if (exp <= now)
                expired.Add(kv.Key);
        }

        foreach (var k in expired)
            prefs.DeadStreams.Remove(k);
        return expired.Count;
    }

    private void ScheduleDebouncedPrefsSave()
    {
        _prefsDirty = true;
        if (_debounceTimer == null)
            _debounceTimer = new Timer(_ => Flush(), null, DebounceMs, Timeout.Infinite);
        else
            _debounceTimer.Change(DebounceMs, Timeout.Infinite);
    }

    private static void SaveJsonAtomic<T>(string path, T payload)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            using (var fs = File.Create(tmp))
                JsonSerializer.Serialize(fs, payload, JsonOptions);
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }
}
