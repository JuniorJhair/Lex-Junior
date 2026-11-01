using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LexBrowser.Core;

public class HistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public DateTime VisitedAt { get; set; } = DateTime.Now;

    // Propiedades calculadas para la interfaz gráfica
    public string DisplayTime => VisitedAt.ToString("HH:mm:ss");
    public string DisplayDate => VisitedAt.ToString("dd/MM/yyyy");
    public string DayGroup
    {
        get
        {
            var date = VisitedAt.Date;
            if (date == DateTime.Today) return "Hoy";
            if (date == DateTime.Today.AddDays(-1)) return "Ayer";
            return VisitedAt.ToString("dddd, d 'de' MMMM 'de' yyyy");
        }
    }

    public string FaviconUrl
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Domain)) return "pack://application:,,,/Assets/logo.png";
            return "pack://application:,,,/Assets/logo.png";
        }
    }
}

public class DomainVisitStat
{
    public string Domain { get; set; } = string.Empty;
    public int Count { get; set; }
    public string DisplayText => $"{Domain} ({Count})";
}

public static class HistoryManager
{
    private static readonly string HistoryDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LexBrowser"
    );

    private static readonly string HistoryFilePath = Path.Combine(HistoryDir, "history.json");
    private static readonly object _fileLock = new();
    private static readonly List<HistoryEntry> _entries = new();

    public static event Action? HistoryChanged;

    static HistoryManager()
    {
        Load();
    }

    public static void Load()
    {
        lock (_fileLock)
        {
            try
            {
                if (File.Exists(HistoryFilePath))
                {
                    string json = File.ReadAllText(HistoryFilePath);
                    var list = JsonSerializer.Deserialize<List<HistoryEntry>>(json);
                    if (list != null)
                    {
                        _entries.Clear();
                        _entries.AddRange(list);
                    }
                }
            }
            catch { }
        }
    }

    public static void Save()
    {
        lock (_fileLock)
        {
            try
            {
                if (!Directory.Exists(HistoryDir))
                    Directory.CreateDirectory(HistoryDir);

                string json = JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(HistoryFilePath, json);
            }
            catch { }
        }
        HistoryChanged?.Invoke();
    }

    public static string ExtractRootDomain(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return string.Empty;

        string host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.")) host = host.Substring(4);

        // Simplificar dominios comunes (ej. subdominios en m.youtube.com -> youtube.com)
        var parts = host.Split('.');
        if (parts.Length > 2)
        {
            // Manejar ccTLDs como .com.pe, .co.uk, .org.es
            if (parts.Length >= 3 && (parts[^2] == "com" || parts[^2] == "org" || parts[^2] == "net" || parts[^2] == "edu" || parts[^2] == "gob"))
            {
                return string.Join(".", parts.TakeLast(3));
            }
            return string.Join(".", parts.TakeLast(2));
        }

        return host;
    }

    public static void AddEntry(string url, string title)
    {
        // 1. Si el usuario desactivó el historial, NO guardar absolutamente nada (ahorro total de espacio y RAM)
        if (!SettingsManager.Current.EnableHistory) return;

        if (string.IsNullOrWhiteSpace(url)) return;
        if (url.StartsWith("about:") || url.StartsWith("data:") || url.Contains("NewTabPage")) return;

        string cleanTitle = string.IsNullOrWhiteSpace(title) ? url : title.Trim();
        string domain = ExtractRootDomain(url);
        var now = DateTime.Now;

        lock (_fileLock)
        {
            // Evitar duplicados consecutivos en menos de 5 segundos
            var last = _entries.LastOrDefault();
            if (last != null && last.Url.Equals(url, StringComparison.OrdinalIgnoreCase) && (now - last.VisitedAt).TotalSeconds < 5)
            {
                last.Title = cleanTitle;
                last.VisitedAt = now;
                Save();
                return;
            }

            _entries.Add(new HistoryEntry
            {
                Url = url,
                Title = cleanTitle,
                Domain = domain,
                VisitedAt = now
            });

            // Limitar a los últimos 50,000 registros para mantener la velocidad instantánea
            if (_entries.Count > 50000)
            {
                _entries.RemoveRange(0, _entries.Count - 50000);
            }
        }

        Save();
    }

    public static List<HistoryEntry> GetEntries(string? filterText = null, string? domain = null, DateTime? fromDate = null, DateTime? toDate = null)
    {
        lock (_fileLock)
        {
            var query = _entries.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(domain) && domain != "Todos")
            {
                query = query.Where(e => e.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(filterText))
            {
                string term = filterText.Trim();
                query = query.Where(e =>
                    e.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    e.Url.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    e.Domain.Contains(term, StringComparison.OrdinalIgnoreCase));
            }

            if (fromDate.HasValue)
            {
                query = query.Where(e => e.VisitedAt >= fromDate.Value);
            }

            if (toDate.HasValue)
            {
                query = query.Where(e => e.VisitedAt <= toDate.Value);
            }

            return query.OrderByDescending(e => e.VisitedAt).ToList();
        }
    }

    public static List<DomainVisitStat> GetDistinctDomains()
    {
        lock (_fileLock)
        {
            return _entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Domain))
                .GroupBy(e => e.Domain, StringComparer.OrdinalIgnoreCase)
                .Select(g => new DomainVisitStat { Domain = g.Key, Count = g.Count() })
                .OrderByDescending(g => g.Count)
                .ToList();
        }
    }

    public static void DeleteEntry(string id)
    {
        lock (_fileLock)
        {
            _entries.RemoveAll(e => e.Id == id);
        }
        Save();
    }

    public static void DeleteEntries(IEnumerable<string> ids)
    {
        var idSet = new HashSet<string>(ids);
        lock (_fileLock)
        {
            _entries.RemoveAll(e => idSet.Contains(e.Id));
        }
        Save();
    }

    public static void DeleteByDay(DateTime day)
    {
        var targetDate = day.Date;
        lock (_fileLock)
        {
            _entries.RemoveAll(e => e.VisitedAt.Date == targetDate);
        }
        Save();
    }

    public static void DeleteByDomain(string domain, DateTime? fromTime = null)
    {
        if (string.IsNullOrWhiteSpace(domain)) return;
        lock (_fileLock)
        {
            if (fromTime.HasValue)
            {
                _entries.RemoveAll(e => e.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase) && e.VisitedAt >= fromTime.Value);
            }
            else
            {
                _entries.RemoveAll(e => e.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase));
            }
        }
        Save();
    }

    public static void DeleteByTimeRange(DateTime fromTime, DateTime toTime, string? domain = null)
    {
        lock (_fileLock)
        {
            if (string.IsNullOrWhiteSpace(domain) || domain == "Todos")
            {
                _entries.RemoveAll(e => e.VisitedAt >= fromTime && e.VisitedAt <= toTime);
            }
            else
            {
                _entries.RemoveAll(e => e.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase) && e.VisitedAt >= fromTime && e.VisitedAt <= toTime);
            }
        }
        Save();
    }

    public static void ClearAll()
    {
        lock (_fileLock)
        {
            _entries.Clear();
        }
        Save();
    }

    public static string GetHistoryFileSizeFormatted()
    {
        if (!SettingsManager.Current.EnableHistory)
            return "0 KB (Historial desactivado)";

        try
        {
            if (File.Exists(HistoryFilePath))
            {
                long bytes = new FileInfo(HistoryFilePath).Length;
                if (bytes < 1024) return $"{bytes} B";
                if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
                return $"{bytes / (1024.0 * 1024.0):F2} MB";
            }
        }
        catch { }
        return "0 KB";
    }

    public static int GetTotalCount()
    {
        lock (_fileLock)
        {
            return _entries.Count;
        }
    }
}

