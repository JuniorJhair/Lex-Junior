using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LexBrowser.Core;

public class DownloadEntry : INotifyPropertyChanged
{
    private long _totalBytes;
    private long _bytesReceived;
    private string _state = "Completado";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;

    public long TotalBytes
    {
        get => _totalBytes;
        set
        {
            if (_totalBytes != value)
            {
                _totalBytes = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProgressPercent));
                OnPropertyChanged(nameof(StatusDisplay));
            }
        }
    }

    public long BytesReceived
    {
        get => _bytesReceived;
        set
        {
            if (_bytesReceived != value)
            {
                _bytesReceived = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProgressPercent));
                OnPropertyChanged(nameof(StatusDisplay));
            }
        }
    }

    public string State
    {
        get => _state;
        set
        {
            if (_state != value)
            {
                _state = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCompleted));
                OnPropertyChanged(nameof(IsInProgress));
                OnPropertyChanged(nameof(ProgressPercent));
                OnPropertyChanged(nameof(StatusDisplay));
            }
        }
    }

    public DateTime StartedAt { get; set; } = DateTime.Now;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void NotifyProgressUpdated()
    {
        OnPropertyChanged(nameof(BytesReceived));
        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsInProgress));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(StatusDisplay));
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    [JsonIgnore]
    public bool IsIncognito { get; set; }

    [JsonIgnore]
    public bool IsCompleted => State == "Completado";

    [JsonIgnore]
    public bool IsInProgress => State == "En progreso";

    [JsonIgnore]
    public int ProgressPercent =>
        TotalBytes > 0
            ? (int)Math.Clamp((BytesReceived * 100L) / TotalBytes, 0, 100)
            : (IsCompleted ? 100 : 0);

    [JsonIgnore]
    public string CategoryIcon
    {
        get
        {
            string ext = Path.GetExtension(FileName).ToLowerInvariant();
            return ext switch
            {
                ".mp4" or ".webm" or ".mkv" or ".avi" or ".mov" or ".m4v" => "🎬",
                ".mp3" or ".wav" or ".ogg" or ".flac" or ".m4a" or ".aac" => "🎵",
                ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".svg" or ".avif" => "🖼️",
                ".exe" or ".msi" or ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "📦",
                ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".txt" or ".csv" => "📄",
                _ => "📁"
            };
        }
    }

    [JsonIgnore]
    public string StatusDisplay
    {
        get
        {
            string timeLabel = StartedAt.Date == DateTime.Today
                ? $"Hoy {StartedAt:HH:mm}"
                : StartedAt.ToString("dd/MM/yyyy HH:mm");

            if (IsInProgress)
            {
                if (TotalBytes > 0)
                    return $"⏳ {ProgressPercent}% · {FormatSize(BytesReceived)} / {FormatSize(TotalBytes)}";
                return $"⏳ Descargando... {FormatSize(BytesReceived)}";
            }

            if (IsCompleted)
            {
                long size = TotalBytes > 0 ? TotalBytes : BytesReceived;
                string sizeStr = size > 0 ? $"{FormatSize(size)} · " : "";
                return $"✅ {sizeStr}{timeLabel}";
            }

            return $"⚠️ {State} · {timeLabel}";
        }
    }

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):0.00} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / (1024.0 * 1024):0.0} MB";
        if (bytes >= 1024L) return $"{bytes / 1024.0:0} KB";
        return $"{bytes} B";
    }
}

public class DownloadStore
{
    public const int MaxEntries = 300;

    private readonly string _filePath;
    private readonly object _lock = new();
    private List<DownloadEntry> _entries = new();
    private bool _loaded;

    public event Action? DownloadsChanged;

    public DownloadStore(string filePath)
    {
        _filePath = filePath;
    }

    public void Load()
    {
        lock (_lock)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (File.Exists(_filePath))
                {
                    string json = File.ReadAllText(_filePath);
                    var loaded = JsonSerializer.Deserialize<List<DownloadEntry>>(json);
                    if (loaded != null)
                    {
                        _entries = loaded
                            .OrderByDescending(e => e.StartedAt)
                            .Take(MaxEntries)
                            .ToList();
                    }
                }
            }
            catch
            {
                _entries = new List<DownloadEntry>();
            }
        }
    }

    public void Flush()
    {
        List<DownloadEntry> snapshot;
        lock (_lock)
        {
            // Jamás persistir descargas de pestañas en Modo Incógnito en disco
            snapshot = _entries
                .Where(e => !e.IsIncognito)
                .OrderByDescending(e => e.StartedAt)
                .Take(MaxEntries)
                .ToList();
        }

        try
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tmpPath = _filePath + ".tmp";
            string json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, _filePath, overwrite: true);
        }
        catch { }
    }

    public DownloadEntry RegisterDownload(string fileName, string filePath, string url, long totalBytes, string state = "En progreso", bool isIncognito = false)
    {
        Load();
        var entry = new DownloadEntry
        {
            FileName = string.IsNullOrWhiteSpace(fileName) ? Path.GetFileName(filePath) : fileName,
            FilePath = filePath ?? string.Empty,
            Url = url ?? string.Empty,
            TotalBytes = Math.Max(0, totalBytes),
            BytesReceived = state == "Completado" ? Math.Max(0, totalBytes) : 0,
            State = state,
            StartedAt = DateTime.Now,
            IsIncognito = isIncognito
        };

        lock (_lock)
        {
            _entries.Insert(0, entry);
            if (_entries.Count > MaxEntries)
            {
                _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
            }
        }

        if (!isIncognito && state != "En progreso")
        {
            Flush();
        }

        DownloadsChanged?.Invoke();
        return entry;
    }

    public void UpdateDownload(string id, long bytesReceived, long totalBytes, string state)
    {
        Load();
        bool shouldFlush = false;
        lock (_lock)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == id);
            if (entry == null) return;

            entry.BytesReceived = Math.Max(0, bytesReceived);
            if (totalBytes > 0) entry.TotalBytes = totalBytes;
            bool stateChanged = entry.State != state;
            entry.State = state;

            if (stateChanged && state != "En progreso" && !entry.IsIncognito)
            {
                shouldFlush = true;
            }
        }

        if (shouldFlush)
        {
            Flush();
        }

        DownloadsChanged?.Invoke();
    }

    public List<DownloadEntry> GetEntries(string? searchFilter = null)
    {
        Load();
        lock (_lock)
        {
            IEnumerable<DownloadEntry> query = _entries;
            if (!string.IsNullOrWhiteSpace(searchFilter))
            {
                string term = searchFilter.Trim();
                query = query.Where(e =>
                    e.FileName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    e.Url.Contains(term, StringComparison.OrdinalIgnoreCase));
            }
            return query.OrderByDescending(e => e.StartedAt).ToList();
        }
    }

    public void DeleteEntry(string id)
    {
        Load();
        lock (_lock)
        {
            _entries.RemoveAll(e => e.Id == id);
        }
        Flush();
        DownloadsChanged?.Invoke();
    }

    public void ClearAll()
    {
        Load();
        lock (_lock)
        {
            _entries.Clear();
        }
        Flush();
        DownloadsChanged?.Invoke();
    }
}

public static class DownloadManager
{
    private static readonly string DownloadsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LexBrowser",
        "downloads.json"
    );

    private static readonly DownloadStore _store = new(DownloadsFilePath);

    public static event Action? DownloadsChanged
    {
        add => _store.DownloadsChanged += value;
        remove => _store.DownloadsChanged -= value;
    }

    public static void Load() => _store.Load();
    public static void Flush() => _store.Flush();

    public static DownloadEntry RegisterDownload(string fileName, string filePath, string url, long totalBytes, string state = "En progreso", bool isIncognito = false)
        => _store.RegisterDownload(fileName, filePath, url, totalBytes, state, isIncognito);

    public static void UpdateDownload(string id, long bytesReceived, long totalBytes, string state)
        => _store.UpdateDownload(id, bytesReceived, totalBytes, state);

    public static List<DownloadEntry> GetEntries(string? searchFilter = null)
        => _store.GetEntries(searchFilter);

    public static void DeleteEntry(string id)
        => _store.DeleteEntry(id);

    public static void ClearAll()
        => _store.ClearAll();
}

