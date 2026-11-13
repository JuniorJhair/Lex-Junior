using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace LexBrowser.Core.Tv.Cine;

public sealed class TvCineProgress
{
    public string Id { get; set; } = "";
    public double PositionSec { get; set; }
    public double DurationSec { get; set; }
    public DateTime LastWatchedUtc { get; set; } = DateTime.UtcNow;

    public double Percent => DurationSec > 0 ? Math.Min(100.0, Math.Round((PositionSec / DurationSec) * 100.0, 1)) : 0;
}

public sealed class TvCineUnavailableRecord
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "dead"; // "dead" | "restricted" | "codec"
    public DateTime FailedUtc { get; set; } = DateTime.UtcNow;
    public string Reason { get; set; } = "";

    public bool IsExpired()
    {
        var age = DateTime.UtcNow - FailedUtc;
        return Kind switch
        {
            "restricted" => age > TimeSpan.FromHours(24),
            "codec" => age > TimeSpan.FromDays(7),
            _ => age > TimeSpan.FromHours(72)
        };
    }
}

public sealed class TvCineStatePayload
{
    public int Version { get; set; } = 1;
    public HashSet<string> Favorites { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Watchlist { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Watched { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, TvCineProgress> Progress { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> LastEpisodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, TvCineUnavailableRecord> Unavailable { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class TvCineStateStore
{
    private static readonly Lazy<TvCineStateStore> LazyInstance = new(() => new TvCineStateStore());
    public static TvCineStateStore Shared => LazyInstance.Value;

    private const int MaxProgressEntries = 5000;
    private const int MaxUnavailableEntries = 2000;

    private readonly object _lock = new();
    private readonly string _filePath;
    private TvCineStatePayload? _payload;
    private Timer? _debounceTimer;
    private bool _isDirty;

    public string FilePath => _filePath;

    public TvCineStateStore(string? customPath = null)
    {
        if (!string.IsNullOrEmpty(customPath))
        {
            _filePath = customPath;
        }
        else
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(appData, "LexBrowser");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "tv_cine.json");
        }
    }

    public void ReleaseMemoryCache()
    {
        Flush();
        lock (_lock)
        {
            _payload = null;
        }
    }

    public void ToggleFavorite(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (_lock)
        {
            EnsureLoadedLocked();
            if (!_payload!.Favorites.Add(id))
            {
                _payload.Favorites.Remove(id);
            }
            ScheduleSaveLocked();
        }
    }

    public bool IsFavorite(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (_lock)
        {
            EnsureLoadedLocked();
            return _payload!.Favorites.Contains(id);
        }
    }

    public void ToggleWatchlist(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (_lock)
        {
            EnsureLoadedLocked();
            if (!_payload!.Watchlist.Add(id))
            {
                _payload.Watchlist.Remove(id);
            }
            ScheduleSaveLocked();
        }
    }

    public bool IsInWatchlist(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (_lock)
        {
            EnsureLoadedLocked();
            return _payload!.Watchlist.Contains(id);
        }
    }

    public void ToggleWatched(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (_lock)
        {
            EnsureLoadedLocked();
            if (!_payload!.Watched.Add(id))
            {
                _payload.Watched.Remove(id);
            }
            ScheduleSaveLocked();
        }
    }

    public bool IsWatched(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (_lock)
        {
            EnsureLoadedLocked();
            return _payload!.Watched.Contains(id);
        }
    }

    public void UpdateProgress(string id, double positionSec, double durationSec, string? seriesName = null, string? episodeId = null)
    {
        SaveProgress(id, positionSec, durationSec);
        if (!string.IsNullOrWhiteSpace(seriesName) && !string.IsNullOrWhiteSpace(episodeId))
        {
            SaveLastEpisode(seriesName, episodeId);
        }
    }

    public void SaveProgress(string id, double positionSec, double durationSec)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (_lock)
        {
            EnsureLoadedLocked();
            _payload!.Progress[id] = new TvCineProgress
            {
                Id = id,
                PositionSec = positionSec,
                DurationSec = durationSec,
                LastWatchedUtc = DateTime.UtcNow
            };

            // Marcar como visto si supera el 92% de duración
            if (durationSec > 60 && (positionSec / durationSec) >= 0.92)
            {
                _payload.Watched.Add(id);
            }

            // Purgar progresos más antiguos si excede el tope
            if (_payload.Progress.Count > MaxProgressEntries)
            {
                var oldest = _payload.Progress
                    .OrderBy(kv => kv.Value.LastWatchedUtc)
                    .Take(_payload.Progress.Count - MaxProgressEntries)
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var k in oldest) _payload.Progress.Remove(k);
            }

            ScheduleSaveLocked();
        }
    }

    public TvCineProgress? GetProgress(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (_lock)
        {
            EnsureLoadedLocked();
            return _payload!.Progress.TryGetValue(id, out var hit) ? hit : null;
        }
    }

    public void SaveLastEpisode(string seriesName, string episodeId)
    {
        if (string.IsNullOrWhiteSpace(seriesName) || string.IsNullOrWhiteSpace(episodeId)) return;
        lock (_lock)
        {
            EnsureLoadedLocked();
            _payload!.LastEpisodes[seriesName] = episodeId;
            ScheduleSaveLocked();
        }
    }

    public string? GetLastEpisode(string seriesName)
    {
        if (string.IsNullOrWhiteSpace(seriesName)) return null;
        lock (_lock)
        {
            EnsureLoadedLocked();
            return _payload!.LastEpisodes.TryGetValue(seriesName, out var ep) ? ep : null;
        }
    }

    public void MarkUnavailable(string id, string kind, string reason)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (_lock)
        {
            EnsureLoadedLocked();
            _payload!.Unavailable[id] = new TvCineUnavailableRecord
            {
                Id = id,
                Kind = kind,
                FailedUtc = DateTime.UtcNow,
                Reason = reason
            };

            if (_payload.Unavailable.Count > MaxUnavailableEntries)
            {
                var oldest = _payload.Unavailable
                    .OrderBy(kv => kv.Value.FailedUtc)
                    .Take(_payload.Unavailable.Count - MaxUnavailableEntries)
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var k in oldest) _payload.Unavailable.Remove(k);
            }

            ScheduleSaveLocked();
        }
    }

    public bool IsUnavailable(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (_lock)
        {
            EnsureLoadedLocked();
            if (_payload!.Unavailable.TryGetValue(id, out var rec))
            {
                if (rec.IsExpired())
                {
                    _payload.Unavailable.Remove(id);
                    ScheduleSaveLocked();
                    return false;
                }
                return true;
            }
            return false;
        }
    }

    public void ClearUnavailable(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (_lock)
        {
            EnsureLoadedLocked();
            if (_payload!.Unavailable.Remove(id))
            {
                ScheduleSaveLocked();
            }
        }
    }

    public void ClearAllUnavailable()
    {
        lock (_lock)
        {
            EnsureLoadedLocked();
            if (_payload!.Unavailable.Count > 0)
            {
                _payload.Unavailable.Clear();
                ScheduleSaveLocked();
            }
        }
    }

    public TvCineStatePayload GetSnapshot()
    {
        lock (_lock)
        {
            EnsureLoadedLocked();
            return new TvCineStatePayload
            {
                Version = _payload!.Version,
                Favorites = new HashSet<string>(_payload.Favorites, StringComparer.OrdinalIgnoreCase),
                Watchlist = new HashSet<string>(_payload.Watchlist, StringComparer.OrdinalIgnoreCase),
                Watched = new HashSet<string>(_payload.Watched, StringComparer.OrdinalIgnoreCase),
                Progress = new Dictionary<string, TvCineProgress>(_payload.Progress, StringComparer.OrdinalIgnoreCase),
                LastEpisodes = new Dictionary<string, string>(_payload.LastEpisodes, StringComparer.OrdinalIgnoreCase),
                Unavailable = new Dictionary<string, TvCineUnavailableRecord>(_payload.Unavailable, StringComparer.OrdinalIgnoreCase)
            };
        }
    }

    public void Flush()
    {
        lock (_lock)
        {
            if (_debounceTimer != null)
            {
                _debounceTimer.Dispose();
                _debounceTimer = null;
            }
            if (_isDirty && _payload != null)
            {
                SaveToFileLocked();
            }
        }
    }

    private void EnsureLoadedLocked()
    {
        if (_payload != null) return;

        if (File.Exists(_filePath))
        {
            try
            {
                string json = File.ReadAllText(_filePath, Encoding.UTF8);
                var loaded = JsonSerializer.Deserialize<TvCineStatePayload>(json);
                if (loaded != null)
                {
                    // Purga elementos caducados al cargar
                    var expired = loaded.Unavailable.Where(kv => kv.Value.IsExpired()).Select(kv => kv.Key).ToList();
                    foreach (var k in expired) loaded.Unavailable.Remove(k);

                    _payload = loaded;
                    return;
                }
            }
            catch { }
        }

        _payload = new TvCineStatePayload();
    }

    private void ScheduleSaveLocked()
    {
        _isDirty = true;
        _debounceTimer ??= new Timer(_ =>
        {
            lock (_lock)
            {
                _debounceTimer?.Dispose();
                _debounceTimer = null;
                if (_isDirty && _payload != null)
                {
                    SaveToFileLocked();
                }
            }
        }, null, 500, Timeout.Infinite);
    }

    private void SaveToFileLocked()
    {
        try
        {
            string tmp = _filePath + ".tmp";
            string json = JsonSerializer.Serialize(_payload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, _filePath, overwrite: true);
            _isDirty = false;
        }
        catch { }
    }
}

