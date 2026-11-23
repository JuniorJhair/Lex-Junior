using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LexBrowser.Core;

public sealed record ActiveDownloadSummary(
    int ActiveCount,
    int InProgressCount,
    int PausedCount,
    int TotalProgressPercent,
    bool HasIndeterminate)
{
    public bool HasActive => ActiveCount > 0;
}

public class DownloadEntry : INotifyPropertyChanged
{
    private long _totalBytes;
    private long _bytesReceived;
    private string _state = "Completado";
    private string? _interruptReason;
    private double _speedBytesPerSec;
    private double? _estimatedSecondsRemaining;
    private bool _canResume;
    private bool _isFinalizationDelayed;

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
                NotifyDerivedProperties();
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
                NotifyDerivedProperties();
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
                NotifyDerivedProperties();
            }
        }
    }

    public string? InterruptReason
    {
        get => _interruptReason;
        set
        {
            if (_interruptReason != value)
            {
                _interruptReason = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusDisplay));
            }
        }
    }

    public DateTime StartedAt { get; set; } = DateTime.Now;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void ApplyProgressSnapshot(
        long bytesReceived,
        long totalBytes,
        string state,
        string? interruptReason,
        bool canResume,
        double speedBytesPerSec,
        double? estimatedSecondsRemaining,
        bool isFinalizationDelayed = false)
    {
        _totalBytes = totalBytes;
        _bytesReceived = bytesReceived;
        _state = state;
        _interruptReason = interruptReason;
        _canResume = canResume;
        _speedBytesPerSec = speedBytesPerSec;
        _estimatedSecondsRemaining = estimatedSecondsRemaining;
        _isFinalizationDelayed = isFinalizationDelayed;
    }

    public void NotifyProgressUpdated()
    {
        OnPropertyChanged(nameof(BytesReceived));
        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(InterruptReason));
        OnPropertyChanged(nameof(SpeedBytesPerSec));
        OnPropertyChanged(nameof(EstimatedSecondsRemaining));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(IsFinalizationDelayed));
        NotifyDerivedProperties();
    }

    private void NotifyDerivedProperties()
    {
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsInProgress));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsInterrupted));
        OnPropertyChanged(nameof(IsCanceled));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsIndeterminateProgress));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(StatusDisplay));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResumeAction));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanOpen));
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        var handler = PropertyChanged;
        if (handler == null) return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.DataBind,
                () => handler(this, new PropertyChangedEventArgs(propertyName)));
            return;
        }

        handler(this, new PropertyChangedEventArgs(propertyName));
    }

    [JsonIgnore]
    public bool IsIncognito { get; set; }

    [JsonIgnore]
    public double SpeedBytesPerSec
    {
        get => _speedBytesPerSec;
        set
        {
            if (Math.Abs(_speedBytesPerSec - value) > 1.0)
            {
                _speedBytesPerSec = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusDisplay));
            }
        }
    }

    [JsonIgnore]
    public double? EstimatedSecondsRemaining
    {
        get => _estimatedSecondsRemaining;
        set
        {
            if (_estimatedSecondsRemaining != value)
            {
                _estimatedSecondsRemaining = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusDisplay));
            }
        }
    }

    [JsonIgnore]
    public bool CanResume
    {
        get => _canResume;
        set
        {
            if (_canResume != value)
            {
                _canResume = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanResumeAction));
            }
        }
    }

    [JsonIgnore]
    public bool IsFinalizationDelayed
    {
        get => _isFinalizationDelayed;
        set
        {
            if (_isFinalizationDelayed != value)
            {
                _isFinalizationDelayed = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusDisplay));
            }
        }
    }

    [JsonIgnore]
    public Action? PauseAction { get; set; }

    [JsonIgnore]
    public Action? ResumeAction { get; set; }

    [JsonIgnore]
    public Action? CancelAction { get; set; }

    [JsonIgnore]
    public Action? RetryAction { get; set; }

    [JsonIgnore]
    public bool IsCompleted => State == "Completado";

    [JsonIgnore]
    public bool IsInProgress => State == "En progreso";

    [JsonIgnore]
    public bool IsPaused => State == "Pausado";

    [JsonIgnore]
    public bool IsInterrupted => State == "Interrumpido" || State == "Error";

    [JsonIgnore]
    public bool IsCanceled => State == "Cancelado";

    [JsonIgnore]
    public bool IsActive => IsInProgress || IsPaused;

    [JsonIgnore]
    public bool IsIndeterminateProgress => IsInProgress && TotalBytes <= 0;

    [JsonIgnore]
    public bool CanPause => IsInProgress && PauseAction != null;

    [JsonIgnore]
    public bool CanResumeAction => (IsPaused || (IsInterrupted && CanResume)) && ResumeAction != null;

    [JsonIgnore]
    public bool CanCancel => IsActive && CancelAction != null;

    [JsonIgnore]
    public bool CanRetry => (IsInterrupted || IsCanceled) && RetryAction != null && !string.IsNullOrWhiteSpace(Url);

    [JsonIgnore]
    public bool CanOpen => IsCompleted && !string.IsNullOrWhiteSpace(FilePath);

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
                string speedPart = SpeedBytesPerSec >= 1024 ? $" · {FormatSize((long)SpeedBytesPerSec)}/s" : string.Empty;
                if (TotalBytes > 0)
                {
                    if (BytesReceived >= TotalBytes)
                    {
                        return IsFinalizationDelayed
                            ? $"⏳ 100% · Esperando análisis de Windows ({FormatSize(TotalBytes)})"
                            : $"⏳ 100% · Finalizando y verificando ({FormatSize(TotalBytes)})";
                    }

                    string etaPart = EstimatedSecondsRemaining.HasValue && EstimatedSecondsRemaining.Value >= 1
                        ? $" · {FormatEta(EstimatedSecondsRemaining.Value)}"
                        : string.Empty;
                    return $"⏳ {ProgressPercent}% · {FormatSize(BytesReceived)} / {FormatSize(TotalBytes)}{speedPart}{etaPart}";
                }
                return $"⏳ Descargando... {FormatSize(BytesReceived)}{speedPart}";
            }

            if (IsPaused)
            {
                if (TotalBytes > 0)
                    return $"⏸ Pausado ({ProgressPercent}%) · {FormatSize(BytesReceived)} / {FormatSize(TotalBytes)}";
                return $"⏸ Pausado · {FormatSize(BytesReceived)}";
            }

            if (IsCompleted)
            {
                long size = TotalBytes > 0 ? TotalBytes : BytesReceived;
                string sizeStr = size > 0 ? $"{FormatSize(size)} · " : "";
                return $"✅ {sizeStr}{timeLabel}";
            }

            if (IsCanceled)
            {
                return $"⛔ Cancelado · {timeLabel}";
            }

            string reason = !string.IsNullOrWhiteSpace(InterruptReason) ? $" ({InterruptReason})" : string.Empty;
            return $"⚠️ {State}{reason} · {timeLabel}";
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

    public static string FormatEta(double seconds)
    {
        if (seconds < 60) return $"{Math.Max(1, (int)Math.Round(seconds))}s";
        int mins = (int)(seconds / 60);
        int secs = (int)(seconds % 60);
        if (mins < 60) return $"{mins}m {secs:00}s";
        int hrs = mins / 60;
        mins %= 60;
        return $"{hrs}h {mins:00}m";
    }
}

public class DownloadStore
{
    public const int MaxEntries = 300;

    private sealed class SpeedTracker
    {
        public DateTime LastUtc;
        public long LastBytes;
        public double EmaBytesPerSec;
    }

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private string _filePath;
    private readonly object _lock = new();
    private List<DownloadEntry> _entries = new();
    private readonly Dictionary<string, SpeedTracker> _speedTrackers = new(StringComparer.Ordinal);
    private bool _loaded;

    public event Action? DownloadsChanged;

    public string FilePath => _filePath;

    public DownloadStore(string filePath)
    {
        _filePath = filePath;
    }

    public void RebindFilePath(string filePath, bool reload = true)
    {
        lock (_lock)
        {
            _filePath = filePath;
            _loaded = false;
            _entries.Clear();
            _speedTrackers.Clear();
        }
        if (reload)
            Load();
    }

    public static string SanitizeFileName(string? suggestedFileName, string? fallbackUrl = null)
    {
        string raw = suggestedFileName ?? string.Empty;
        raw = raw.Replace('\\', '/');
        int slash = raw.LastIndexOf('/');
        if (slash >= 0)
            raw = raw[(slash + 1)..];

        raw = raw.Trim();
        if (string.IsNullOrWhiteSpace(raw) && !string.IsNullOrWhiteSpace(fallbackUrl) &&
            Uri.TryCreate(fallbackUrl, UriKind.Absolute, out var uri))
        {
            string seg = Path.GetFileName(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(seg))
            {
                try { raw = Uri.UnescapeDataString(seg); } catch { raw = seg; }
            }
        }

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            if (char.IsControl(c) || Array.IndexOf(invalid, c) >= 0)
                sb.Append('_');
            else
                sb.Append(c);
        }

        string cleaned = sb.ToString().Trim().Trim('.');
        if (string.IsNullOrWhiteSpace(cleaned))
            cleaned = "descarga";

        string stem = Path.GetFileNameWithoutExtension(cleaned);
        string ext = Path.GetExtension(cleaned);
        if (string.IsNullOrWhiteSpace(stem))
            stem = "descarga";
        if (ReservedDeviceNames.Contains(stem))
            stem = "_" + stem;

        if (stem.Length + ext.Length > 180)
        {
            int maxStem = Math.Max(8, 180 - ext.Length);
            stem = stem[..Math.Min(stem.Length, maxStem)].TrimEnd('.', ' ');
            if (string.IsNullOrEmpty(stem))
                stem = "descarga";
        }

        return stem + ext;
    }

    public static long SafeBytesFromUlong(ulong? raw)
        => raw is ulong v && v <= (ulong)long.MaxValue ? (long)v : 0L;

    public static string SanitizeAndMakeUniquePath(
        string targetDirectory,
        string? suggestedFileName,
        string? fallbackUrl = null,
        IEnumerable<string>? activePaths = null,
        Func<string, bool>? fileExists = null)
    {
        string dirFull = Path.GetFullPath(string.IsNullOrWhiteSpace(targetDirectory)
            ? Path.GetTempPath()
            : targetDirectory);

        string safeName = SanitizeFileName(suggestedFileName, fallbackUrl);
        string stem = Path.GetFileNameWithoutExtension(safeName);
        string ext = Path.GetExtension(safeName);

        var activeSet = activePaths != null
            ? new HashSet<string>(activePaths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Func<string, bool> existsFn = fileExists ?? File.Exists;

        for (int i = 0; i < 10000; i++)
        {
            string fileName = i == 0 ? safeName : $"{stem} ({i}){ext}";
            string candidate = Path.GetFullPath(Path.Combine(dirFull, fileName));
            if (!candidate.StartsWith(dirFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Ruta de descarga inválida fuera del directorio destino.");

            if (!activeSet.Contains(candidate) && !existsFn(candidate))
                return candidate;
        }

        string uniqueFallback = $"{stem}_{Guid.NewGuid().ToString("N")[..6]}{ext}";
        return Path.GetFullPath(Path.Combine(dirFull, uniqueFallback));
    }

    public void Load()
    {
        bool sanitizedStaleEntries = false;
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
                        foreach (var e in loaded)
                        {
                            // Jamás dejar descargas de una sesión anterior bloqueadas como "En progreso" o "Pausado"
                            if (e.State == "En progreso" || e.State == "Pausado")
                            {
                                e.State = "Interrumpido";
                                e.InterruptReason = string.IsNullOrWhiteSpace(e.InterruptReason)
                                    ? "Sesión cerrada antes de completar"
                                    : e.InterruptReason;
                                sanitizedStaleEntries = true;
                            }
                        }

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

        if (sanitizedStaleEntries)
        {
            Flush();
        }
    }

    public void Flush()
    {
        List<DownloadEntry> snapshot;
        lock (_lock)
        {
            // Jamás persistir descargas de pestañas en Modo Incógnito en disco, ni guardar estados activos transitorios como "En progreso"
            snapshot = _entries
                .Where(e => !e.IsIncognito && e.State != "En progreso" && e.State != "Pausado")
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

    public DownloadEntry RegisterDownload(
        string fileName,
        string filePath,
        string url,
        long totalBytes,
        string state = "En progreso",
        bool isIncognito = false)
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
            if (state == "En progreso")
            {
                _speedTrackers[entry.Id] = new SpeedTracker
                {
                    LastUtc = DateTime.UtcNow,
                    LastBytes = 0,
                    EmaBytesPerSec = 0
                };
            }
        }

        if (!isIncognito && state != "En progreso" && state != "Pausado")
        {
            Flush();
        }

        DownloadsChanged?.Invoke();
        return entry;
    }

    public void UpdateDownload(string id, long bytesReceived, long totalBytes, string state)
        => UpdateDownloadProgress(id, bytesReceived, totalBytes, state);

    public void UpdateDownloadProgress(
        string id,
        long bytesReceived,
        long totalBytes,
        string state,
        string? interruptReason = null,
        bool canResume = false,
        double? estimatedSecondsRemaining = null,
        DateTime? nowUtc = null,
        bool isFinalizationDelayed = false)
    {
        Load();
        bool shouldFlush = false;
        DateTime utcNow = nowUtc ?? DateTime.UtcNow;
        DownloadEntry? entryToNotify = null;

        lock (_lock)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == id);
            if (entry == null) return;

            long clampedReceived = Math.Max(0, bytesReceived);
            long nextTotal = entry.TotalBytes;
            if (totalBytes > 0)
                nextTotal = totalBytes;
            else if (state == "Completado" && clampedReceived > nextTotal)
                nextTotal = clampedReceived;

            string? nextReason = entry.InterruptReason;
            if (!string.IsNullOrWhiteSpace(interruptReason) || state == "En progreso" || state == "Completado")
            {
                nextReason = (state == "Interrumpido" || state == "Error") ? interruptReason : null;
            }

            double nextSpeed;
            double? nextEta;
            bool nextCanResume;

            if (state == "En progreso")
            {
                bool justResetDueToStall = false;
                if (!_speedTrackers.TryGetValue(id, out var tracker))
                {
                    tracker = new SpeedTracker { LastUtc = utcNow, LastBytes = clampedReceived };
                    _speedTrackers[id] = tracker;
                }
                else
                {
                    double dt = (utcNow - tracker.LastUtc).TotalSeconds;
                    if (dt >= 0.15)
                    {
                        long dBytes = Math.Max(0, clampedReceived - tracker.LastBytes);
                        if (dBytes > 0)
                        {
                            double instSpeed = dBytes / dt;
                            tracker.EmaBytesPerSec = tracker.EmaBytesPerSec <= 0
                                ? instSpeed
                                : (tracker.EmaBytesPerSec * 0.65) + (instSpeed * 0.35);
                            tracker.LastUtc = utcNow;
                            tracker.LastBytes = clampedReceived;
                        }
                        else if (dt >= 1.2)
                        {
                            if (tracker.EmaBytesPerSec > 0)
                                justResetDueToStall = true;
                            tracker.EmaBytesPerSec = 0;
                            tracker.LastUtc = utcNow;
                            tracker.LastBytes = clampedReceived;
                        }
                    }
                }

                nextSpeed = tracker.EmaBytesPerSec;
                if (justResetDueToStall)
                {
                    nextEta = null;
                }
                else if (estimatedSecondsRemaining.HasValue)
                {
                    nextEta = estimatedSecondsRemaining.Value;
                }
                else if (nextTotal > clampedReceived && tracker.EmaBytesPerSec > 64)
                {
                    nextEta = (nextTotal - clampedReceived) / tracker.EmaBytesPerSec;
                }
                else
                {
                    nextEta = null;
                }
                nextCanResume = false;
            }
            else
            {
                nextSpeed = 0;
                nextEta = null;
                nextCanResume = canResume;
                if (state != "Pausado")
                {
                    _speedTrackers.Remove(id);
                }
            }

            bool stateChanged = entry.State != state;
            bool effectiveDelayed = state == "En progreso" && nextTotal > 0 && clampedReceived >= nextTotal && isFinalizationDelayed;
            entry.ApplyProgressSnapshot(clampedReceived, nextTotal, state, nextReason, nextCanResume, nextSpeed, nextEta, effectiveDelayed);
            entryToNotify = entry;

            if (stateChanged && state != "En progreso" && state != "Pausado" && !entry.IsIncognito)
            {
                shouldFlush = true;
            }
        }

        if (entryToNotify != null)
        {
            DispatchEntryProgressNotification(entryToNotify);
        }

        if (shouldFlush)
        {
            Flush();
        }

        DownloadsChanged?.Invoke();
    }

    private static void DispatchEntryProgressNotification(DownloadEntry entry)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.DataBind,
                entry.NotifyProgressUpdated);
            return;
        }

        entry.NotifyProgressUpdated();
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

    public List<string> GetActiveDestinationPaths()
    {
        Load();
        lock (_lock)
        {
            return _entries
                .Where(e => e.IsActive && !string.IsNullOrWhiteSpace(e.FilePath))
                .Select(e => e.FilePath)
                .ToList();
        }
    }

    public ActiveDownloadSummary GetActiveSummary()
    {
        Load();
        lock (_lock)
        {
            int active = 0;
            int inProgress = 0;
            int paused = 0;
            long knownReceived = 0;
            long knownTotal = 0;
            bool hasIndeterminate = false;

            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                if (!e.IsActive) continue;
                active++;
                if (e.IsInProgress) inProgress++;
                if (e.IsPaused) paused++;

                if (e.TotalBytes > 0)
                {
                    knownTotal += e.TotalBytes;
                    knownReceived += Math.Clamp(e.BytesReceived, 0, e.TotalBytes);
                }
                else
                {
                    hasIndeterminate = true;
                }
            }

            int pct = knownTotal > 0
                ? (int)Math.Clamp((knownReceived * 100L) / knownTotal, 0, 100)
                : 0;

            return new ActiveDownloadSummary(active, inProgress, paused, pct, hasIndeterminate && knownTotal == 0);
        }
    }

    public void CancelAllActive()
    {
        List<DownloadEntry> activeEntries;
        lock (_lock)
        {
            activeEntries = _entries.Where(e => e.IsActive).ToList();
        }

        foreach (var entry in activeEntries)
        {
            try
            {
                entry.CancelAction?.Invoke();
            }
            catch { }
            try
            {
                if (entry.IsActive)
                    UpdateDownloadProgress(entry.Id, entry.BytesReceived, entry.TotalBytes, "Cancelado", "Cancelada al cerrar");
            }
            catch { }
        }
    }

    public void ReconcileOrphanedActiveEntries(IReadOnlySet<string> liveActiveIds)
    {
        Load();
        List<DownloadEntry> staleEntries;
        lock (_lock)
        {
            staleEntries = _entries
                .Where(e => e.IsActive && !liveActiveIds.Contains(e.Id))
                .ToList();
        }

        foreach (var entry in staleEntries)
        {
            ReconcileSingleOrphanEntry(entry.Id);
        }
    }

    public void ReconcileSingleOrphanEntry(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        Load();
        string targetState = "Interrumpido";
        long received;
        long total;
        string? filePath;

        lock (_lock)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == id);
            if (entry == null || !entry.IsActive) return;
            received = entry.BytesReceived;
            total = entry.TotalBytes;
            filePath = entry.FilePath;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
            {
                long diskLen = new FileInfo(filePath).Length;
                if (diskLen > 0 && (total <= 0 || diskLen >= total))
                {
                    targetState = "Completado";
                    received = diskLen;
                    total = total > 0 ? total : diskLen;
                }
            }
        }
        catch { }

        UpdateDownloadProgress(
            id,
            received,
            total,
            targetState,
            targetState == "Completado" ? null : "Finalizada");
    }

    public void DeleteEntry(string id)
    {
        Load();
        lock (_lock)
        {
            _speedTrackers.Remove(id);
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
            _speedTrackers.Clear();
            _entries.Clear();
        }
        Flush();
        DownloadsChanged?.Invoke();
    }
}

public static class DownloadManager
{
    private static readonly string DefaultDownloadsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LexBrowser",
        "downloads.json"
    );

    private static string ResolveConfiguredPath()
    {
        string? envOverride = Environment.GetEnvironmentVariable("LEX_TEST_DOWNLOADS_PATH");
        return !string.IsNullOrWhiteSpace(envOverride) ? envOverride : DefaultDownloadsFilePath;
    }

    private static readonly DownloadStore _store = new(ResolveConfiguredPath());

    public static string DownloadsPath => _store.FilePath;

    public static string CustomDownloadsPath
    {
        get => _store.FilePath;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                _store.RebindFilePath(DefaultDownloadsFilePath, reload: false);
            else
                _store.RebindFilePath(value, reload: false);
        }
    }

    public static event Action? DownloadsChanged
    {
        add => _store.DownloadsChanged += value;
        remove => _store.DownloadsChanged -= value;
    }

    public static void Load() => _store.Load();
    public static void Flush() => _store.Flush();

    public static long SafeBytesFromUlong(ulong? raw)
        => DownloadStore.SafeBytesFromUlong(raw);

    public static string SanitizeFileName(string? suggestedFileName, string? fallbackUrl = null)
        => DownloadStore.SanitizeFileName(suggestedFileName, fallbackUrl);

    public static string SanitizeAndMakeUniquePath(
        string targetDirectory,
        string? suggestedFileName,
        string? fallbackUrl = null,
        IEnumerable<string>? activePaths = null,
        Func<string, bool>? fileExists = null)
        => DownloadStore.SanitizeAndMakeUniquePath(targetDirectory, suggestedFileName, fallbackUrl, activePaths ?? _store.GetActiveDestinationPaths(), fileExists);

    public static DownloadEntry RegisterDownload(string fileName, string filePath, string url, long totalBytes, string state = "En progreso", bool isIncognito = false)
        => _store.RegisterDownload(fileName, filePath, url, totalBytes, state, isIncognito);

    public static void UpdateDownload(string id, long bytesReceived, long totalBytes, string state)
        => _store.UpdateDownload(id, bytesReceived, totalBytes, state);

    public static void UpdateDownloadProgress(
        string id,
        long bytesReceived,
        long totalBytes,
        string state,
        string? interruptReason = null,
        bool canResume = false,
        double? estimatedSecondsRemaining = null,
        bool isFinalizationDelayed = false)
        => _store.UpdateDownloadProgress(id, bytesReceived, totalBytes, state, interruptReason, canResume, estimatedSecondsRemaining, isFinalizationDelayed: isFinalizationDelayed);

    public static List<DownloadEntry> GetEntries(string? searchFilter = null)
        => _store.GetEntries(searchFilter);

    public static List<string> GetActiveDestinationPaths()
        => _store.GetActiveDestinationPaths();

    public static ActiveDownloadSummary GetActiveSummary()
        => _store.GetActiveSummary();

    public static void CancelAllActive()
        => _store.CancelAllActive();

    public static void ReconcileOrphanedActiveEntries(IReadOnlySet<string> liveActiveIds)
        => _store.ReconcileOrphanedActiveEntries(liveActiveIds);

    public static void ReconcileSingleOrphanEntry(string id)
        => _store.ReconcileSingleOrphanEntry(id);

    public static void DeleteEntry(string id)
        => _store.DeleteEntry(id);

    public static void ClearAll()
        => _store.ClearAll();
}
