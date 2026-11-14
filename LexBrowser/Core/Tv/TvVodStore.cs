using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LexBrowser.Core.Tv;

public sealed class TvVodItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Group { get; set; } = "";
    public string? Logo { get; set; }
    public bool IsSeries { get; set; }
    public string? SeriesTitle { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public string? EpisodeTitle { get; set; }
    public DateTime AddedUtc { get; set; }
}

public sealed class TvVodProgress
{
    public string Url { get; set; } = "";
    public double PositionSec { get; set; }
    public double DurationSec { get; set; }
    public DateTime LastWatchedUtc { get; set; }
}

public sealed class TvVodStore
{
    public const int MaxVodItems = 50_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private static readonly Regex SeasonEpisodeRegex = new(
        @"(?i)\b(?:s(?<season>\d{1,2})\s*e(?<episode>\d{1,3})|(?<season>\d{1,2})x(?<episode>\d{1,3})|(?:temp(?:orada)?|season)\s*(?<season>\d{1,2})\s*(?:ep(?:isodio)?|episode|cap(?:itulo)?)\s*(?<episode>\d{1,3}))\b",
        RegexOptions.Compiled);

    private readonly object _lock = new();
    private readonly string _libraryPath;
    private readonly string _progressPath;

    private List<TvVodItem>? _libraryCache;
    private Dictionary<string, TvVodProgress>? _progressCache;

    public static TvVodStore Shared { get; } = new(TvUserStateStore.GetDefaultDirectory());

    public TvVodStore(string directory)
    {
        _libraryPath = Path.Combine(directory, "vod_library.json");
        _progressPath = Path.Combine(directory, "vod_progress.json");
    }

    public static (bool IsSeries, string? SeriesTitle, int? Season, int? Episode, string? EpisodeTitle) DetectSeriesInfo(string title, string? group = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            return (false, null, null, null, null);

        var match = SeasonEpisodeRegex.Match(title);
        if (!match.Success)
            return (false, null, null, null, null);

        int season = int.Parse(match.Groups["season"].Value);
        int episode = int.Parse(match.Groups["episode"].Value);

        string seriesPart = title[..match.Index].TrimEnd(' ', '-', '_', ':', '.', '(', '[', '{').TrimStart();
        string episodePart = title[(match.Index + match.Length)..].TrimStart(' ', '-', '_', ':', '.', ')', ']', '}').TrimEnd();

        if (string.IsNullOrWhiteSpace(seriesPart) && !string.IsNullOrWhiteSpace(group))
            seriesPart = group.Trim();

        if (string.IsNullOrWhiteSpace(seriesPart))
            seriesPart = "Serie";

        string? epTitle = string.IsNullOrWhiteSpace(episodePart) ? null : episodePart;
        return (true, seriesPart, season, episode, epTitle);
    }

    public List<TvVodItem> GetLibrarySnapshot()
    {
        lock (_lock)
        {
            var lib = EnsureLibraryLoaded();
            return new List<TvVodItem>(lib);
        }
    }

    public List<TvVodProgress> GetProgressSnapshot()
    {
        lock (_lock)
        {
            var prog = EnsureProgressLoaded();
            return prog.Values.OrderByDescending(p => p.LastWatchedUtc).ToList();
        }
    }

    public TvVodProgress? GetProgressForUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        lock (_lock)
        {
            var prog = EnsureProgressLoaded();
            return prog.TryGetValue(url.Trim(), out var p) ? p : null;
        }
    }

    public void SaveProgress(string url, double positionSec, double durationSec)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        string cleanUrl = url.Trim();

        lock (_lock)
        {
            var prog = EnsureProgressLoaded();

            // Guardar solo si vio >= 30s y < 95% de la duracion
            bool isValidToKeep = positionSec >= 30.0 &&
                                 (durationSec <= 0.0 || positionSec < (durationSec * 0.95));

            if (!isValidToKeep)
            {
                // Si vio >= 95%, se considera terminado y se remueve de continuar viendo
                if (prog.Remove(cleanUrl))
                {
                    SaveProgressFileAtomic(prog);
                }
                return;
            }

            prog[cleanUrl] = new TvVodProgress
            {
                Url = cleanUrl,
                PositionSec = Math.Round(positionSec, 1),
                DurationSec = Math.Round(durationSec, 1),
                LastWatchedUtc = DateTime.UtcNow
            };

            SaveProgressFileAtomic(prog);
        }
    }

    public void ClearProgress(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        lock (_lock)
        {
            var prog = EnsureProgressLoaded();
            if (prog.Remove(url.Trim()))
            {
                SaveProgressFileAtomic(prog);
            }
        }
    }

    private static string NormalizeVodMatchKey(TvVodItem item)
    {
        string rawUrl = (item.Url ?? "").Trim();
        int directIdx = rawUrl.IndexOf("&direct=", StringComparison.OrdinalIgnoreCase);
        if (directIdx > 0)
        {
            rawUrl = rawUrl[..directIdx];
        }
        return rawUrl;
    }

    private static string? TryBuildXtreamFallbackKey(TvVodItem item)
    {
        string rawUrl = (item.Url ?? "").Trim();
        if (!rawUrl.Contains("/api/xtream/", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
            return null;

        string path = uri.AbsolutePath.Trim('/').ToLowerInvariant();
        string q = uri.Query;
        string? streamId = null;
        foreach (string part in q.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && part[..eq].Equals("id", StringComparison.OrdinalIgnoreCase))
            {
                streamId = Uri.UnescapeDataString(part[(eq + 1)..]).Trim();
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(streamId))
            return null;

        string title = (item.Title ?? "").Trim().ToLowerInvariant();
        return $"{path}|{streamId}|{title}";
    }

    public int ImportItems(IEnumerable<TvVodItem> newItems)
    {
        lock (_lock)
        {
            var lib = EnsureLibraryLoaded();
            var byBaseUrl = new Dictionary<string, TvVodItem>(StringComparer.OrdinalIgnoreCase);
            var byId = new Dictionary<string, TvVodItem>(StringComparer.OrdinalIgnoreCase);
            var byXtreamKey = new Dictionary<string, TvVodItem>(StringComparer.OrdinalIgnoreCase);

            foreach (var existing in lib)
            {
                string baseUrl = NormalizeVodMatchKey(existing);
                if (!string.IsNullOrEmpty(baseUrl) && !byBaseUrl.ContainsKey(baseUrl))
                    byBaseUrl[baseUrl] = existing;
                if (!string.IsNullOrWhiteSpace(existing.Id) && !byId.ContainsKey(existing.Id))
                    byId[existing.Id] = existing;
                string? xKey = TryBuildXtreamFallbackKey(existing);
                if (!string.IsNullOrEmpty(xKey) && !byXtreamKey.ContainsKey(xKey))
                    byXtreamKey[xKey] = existing;
            }

            int addedCount = 0;
            bool dirty = false;

            foreach (var item in newItems)
            {
                if (string.IsNullOrWhiteSpace(item.Url)) continue;
                string cleanUrl = item.Url.Trim();
                string baseUrl = NormalizeVodMatchKey(item);
                string? xKey = TryBuildXtreamFallbackKey(item);

                TvVodItem? match = null;
                if (!string.IsNullOrEmpty(baseUrl)) byBaseUrl.TryGetValue(baseUrl, out match);
                if (match == null && !string.IsNullOrWhiteSpace(item.Id)) byId.TryGetValue(item.Id, out match);
                if (match == null && !string.IsNullOrEmpty(xKey)) byXtreamKey.TryGetValue(xKey, out match);

                if (match != null)
                {
                    if (!string.Equals(match.Url, cleanUrl, StringComparison.Ordinal))
                    {
                        match.Url = cleanUrl;
                        dirty = true;
                    }
                    if (!string.IsNullOrWhiteSpace(item.Logo) && !string.Equals(match.Logo, item.Logo, StringComparison.Ordinal))
                    {
                        match.Logo = item.Logo;
                        dirty = true;
                    }
                    if (string.IsNullOrWhiteSpace(match.Title) && !string.IsNullOrWhiteSpace(item.Title))
                    {
                        match.Title = item.Title;
                        dirty = true;
                    }
                    if (!string.IsNullOrEmpty(xKey) && !string.IsNullOrWhiteSpace(item.Group) && !string.Equals(match.Group, item.Group, StringComparison.Ordinal))
                    {
                        match.Group = item.Group;
                        dirty = true;
                    }
                    continue;
                }

                if (lib.Count >= MaxVodItems) break;

                item.Url = cleanUrl;
                lib.Add(item);
                addedCount++;
                dirty = true;

                if (!string.IsNullOrEmpty(baseUrl)) byBaseUrl[baseUrl] = item;
                if (!string.IsNullOrWhiteSpace(item.Id)) byId[item.Id] = item;
                if (!string.IsNullOrEmpty(xKey)) byXtreamKey[xKey] = item;
            }

            if (dirty)
            {
                SaveLibraryFileAtomic(lib);
            }

            return addedCount;
        }
    }

    public bool RemoveItem(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (_lock)
        {
            var lib = EnsureLibraryLoaded();
            int removed = lib.RemoveAll(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                SaveLibraryFileAtomic(lib);
                return true;
            }
            return false;
        }
    }

    public void ClearLibrary()
    {
        lock (_lock)
        {
            var lib = EnsureLibraryLoaded();
            lib.Clear();
            SaveLibraryFileAtomic(lib);
        }
    }

    public void ReleaseMemoryCache()
    {
        lock (_lock)
        {
            _libraryCache = null;
            _progressCache = null;
        }
    }

    private List<TvVodItem> EnsureLibraryLoaded()
    {
        if (_libraryCache != null) return _libraryCache;

        if (!File.Exists(_libraryPath))
        {
            _libraryCache = new List<TvVodItem>();
            return _libraryCache;
        }

        try
        {
            string json = File.ReadAllText(_libraryPath);
            _libraryCache = JsonSerializer.Deserialize<List<TvVodItem>>(json, JsonOptions) ?? new List<TvVodItem>();
            foreach (var item in _libraryCache)
            {
                if (string.IsNullOrWhiteSpace(item.Logo)) continue;
                if (item.Logo.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    item.Logo.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? normalizedLogo = TvXtreamService.NormalizePosterUrl(item.Logo, null);
                if (!string.IsNullOrEmpty(normalizedLogo) &&
                    (normalizedLogo.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     normalizedLogo.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                {
                    item.Logo = normalizedLogo;
                }
            }
        }
        catch
        {
            _libraryCache = new List<TvVodItem>();
        }

        return _libraryCache;
    }

    private Dictionary<string, TvVodProgress> EnsureProgressLoaded()
    {
        if (_progressCache != null) return _progressCache;

        if (!File.Exists(_progressPath))
        {
            _progressCache = new Dictionary<string, TvVodProgress>(StringComparer.OrdinalIgnoreCase);
            return _progressCache;
        }

        try
        {
            string json = File.ReadAllText(_progressPath);
            var list = JsonSerializer.Deserialize<List<TvVodProgress>>(json, JsonOptions) ?? new List<TvVodProgress>();
            _progressCache = list.ToDictionary(p => p.Url, p => p, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            _progressCache = new Dictionary<string, TvVodProgress>(StringComparer.OrdinalIgnoreCase);
        }

        return _progressCache;
    }

    private void SaveLibraryFileAtomic(List<TvVodItem> items)
    {
        string dir = Path.GetDirectoryName(_libraryPath)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        string tmp = _libraryPath + ".tmp";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(items, JsonOptions);
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, _libraryPath, overwrite: true);
    }

    private void SaveProgressFileAtomic(Dictionary<string, TvVodProgress> dict)
    {
        string dir = Path.GetDirectoryName(_progressPath)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        string tmp = _progressPath + ".tmp";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(dict.Values.ToList(), JsonOptions);
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, _progressPath, overwrite: true);
    }
}

