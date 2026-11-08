using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace LexBrowser.Core;

/// <summary>
/// Almacén de historial con persistencia diferida.
/// - Las altas/cambios de título solo marcan "sucio" y programan un guardado (debounce) en un hilo de fondo,
///   de modo que la UI nunca espera a escribir el archivo.
/// - Los borrados explícitos del usuario se guardan al instante.
/// - La escritura es atómica (archivo temporal + reemplazo) y en JSON compacto.
/// Es instanciable (ruta, reloj y flag de habilitado inyectables) para poder probarlo sin tocar datos reales.
/// </summary>
public sealed class HistoryStore : IDisposable
{
    public const int DefaultMaxEntries = 50000;

    private readonly string _filePath;
    private readonly Func<bool> _isEnabled;
    private readonly Func<DateTime> _clock;
    private readonly TimeSpan _saveDelay;
    private readonly int _maxEntries;

    private readonly object _lock = new();
    private readonly object _writeLock = new();
    private readonly List<HistoryEntry> _entries = new();
    private volatile HistoryEntry[] _snapshot = Array.Empty<HistoryEntry>();
    private volatile List<DomainVisitStat>? _cachedDomainStats;
    private readonly Timer _saveTimer;
    private bool _dirty;
    private bool _disposed;

    public event Action? HistoryChanged;

    public HistoryStore(
        string filePath,
        Func<bool>? isEnabled = null,
        Func<DateTime>? clock = null,
        TimeSpan? saveDelay = null,
        int maxEntries = DefaultMaxEntries)
    {
        _filePath = filePath;
        _isEnabled = isEnabled ?? (() => true);
        _clock = clock ?? (() => DateTime.Now);
        _saveDelay = saveDelay ?? TimeSpan.FromSeconds(2);
        _maxEntries = maxEntries > 0 ? maxEntries : DefaultMaxEntries;
        _saveTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        Load();
    }

    private void RebuildSnapshotUnderLock()
    {
        _snapshot = _entries.ToArray();
        _cachedDomainStats = null;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Carga / guardado
    // ─────────────────────────────────────────────────────────────────────
    public void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    string json = File.ReadAllText(_filePath);
                    var list = JsonSerializer.Deserialize<List<HistoryEntry>>(json);
                    if (list != null)
                    {
                        _entries.Clear();
                        foreach (var item in list)
                        {
                            if (item == null || string.IsNullOrWhiteSpace(item.Url)) continue;
                            // Filtrar o desenvolver URLs internas del visor PDF que pudieran haberse guardado antes
                            if (Pdf.PdfStudioService.TryUnwrapInternalViewerUrl(item.Url, out string? unwrappedPdf))
                            {
                                if (string.IsNullOrWhiteSpace(unwrappedPdf)) continue;
                                item.Url = unwrappedPdf;
                                item.Domain = ExtractRootDomain(unwrappedPdf);
                            }
                            _ = item.SearchKey;
                            _entries.Add(item);
                        }
                        _entries.Sort((a, b) => a.VisitedAt.CompareTo(b.VisitedAt));
                        RebuildSnapshotUnderLock();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HistoryStore] Load falló: {ex.Message}");
            }
        }
    }

    /// <summary>Escribe a disco ahora mismo si hay cambios pendientes. Seguro de llamar desde cualquier hilo.</summary>
    public void Flush()
    {
        HistoryEntry[] snapshot;
        lock (_lock)
        {
            if (!_dirty) return;
            _dirty = false;
            snapshot = _snapshot;
        }

        lock (_writeLock)
        {
            try
            {
                string? dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                string tmp = _filePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot));
                File.Move(tmp, _filePath, overwrite: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HistoryStore] Flush falló: {ex.Message}");
                lock (_lock) { _dirty = true; }
            }
        }
    }

    private void MarkDirtyDeferred()
    {
        lock (_lock) { _dirty = true; }
        if (!_disposed) _saveTimer.Change(_saveDelay, Timeout.InfiniteTimeSpan);
        HistoryChanged?.Invoke();
    }

    private void MarkDirtyNow()
    {
        lock (_lock) { _dirty = true; }
        Flush();
        HistoryChanged?.Invoke();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Altas
    // ─────────────────────────────────────────────────────────────────────
    private static bool TryNormalize(string url, string title, out string normalizedUrl, out string cleanTitle)
    {
        normalizedUrl = string.Empty;
        cleanTitle = string.Empty;
        if (string.IsNullOrWhiteSpace(url)) return false;

        string trimmed = url.Trim();
        if (trimmed.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("NewTabPage", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // F5: No guardar URLs internas https://pdf.lex.local/ en el historial; si envuelve un PDF web externo, guardar la URL real
        if (Pdf.PdfStudioService.TryUnwrapInternalViewerUrl(trimmed, out string? externalPdfWebUrl))
        {
            if (string.IsNullOrWhiteSpace(externalPdfWebUrl))
                return false;
            trimmed = externalPdfWebUrl;
        }

        normalizedUrl = trimmed;
        cleanTitle = string.IsNullOrWhiteSpace(title) ? normalizedUrl : title.Trim();
        return true;
    }

    /// <summary>Registra una visita. Visitas consecutivas a la misma URL en menos de 5 s se fusionan.</summary>
    public void AddEntry(string url, string title)
    {
        if (!_isEnabled()) return;
        if (!TryNormalize(url, title, out string normalizedUrl, out string cleanTitle)) return;

        string domain = ExtractRootDomain(normalizedUrl);
        var now = _clock();

        lock (_lock)
        {
            var last = _entries.Count > 0 ? _entries[^1] : null;
            if (last != null && last.Url.Equals(normalizedUrl, StringComparison.OrdinalIgnoreCase) && (now - last.VisitedAt).TotalSeconds < 5)
            {
                last.Title = cleanTitle;
                last.VisitedAt = now;
                _ = last.SearchKey;
            }
            else
            {
                var entry = new HistoryEntry { Url = normalizedUrl, Title = cleanTitle, Domain = domain, VisitedAt = now };
                _ = entry.SearchKey;
                _entries.Add(entry);
                if (_entries.Count > _maxEntries)
                    _entries.RemoveRange(0, _entries.Count - _maxEntries);
                RebuildSnapshotUnderLock();
            }
        }

        MarkDirtyDeferred();
    }

    /// <summary>
    /// Actualiza el título de la última visita si es de la misma URL (sin crear duplicado).
    /// Si la URL es distinta (navegación SPA sin evento de carga), registra una visita nueva.
    /// </summary>
    public void UpdateTitle(string url, string title)
    {
        if (!_isEnabled()) return;
        if (!TryNormalize(url, title, out string normalizedUrl, out string cleanTitle)) return;

        bool updated = false;
        lock (_lock)
        {
            var last = _entries.Count > 0 ? _entries[^1] : null;
            if (last != null && last.Url.Equals(normalizedUrl, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(title))
                {
                    last.Title = cleanTitle;
                    _ = last.SearchKey;
                }
                updated = true;
            }
        }

        if (updated) MarkDirtyDeferred();
        else AddEntry(normalizedUrl, title);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Consultas (Sin bloqueo en el hilo lector gracias a _snapshot inmutable)
    // ─────────────────────────────────────────────────────────────────────
    public IReadOnlyList<HistoryEntry> GetRecentSnapshot(int maxCount = 2500)
    {
        var snap = _snapshot;
        if (snap.Length == 0 || maxCount <= 0)
            return Array.Empty<HistoryEntry>();

        int take = Math.Min(maxCount, snap.Length);
        var result = new HistoryEntry[take];
        for (int i = 0; i < take; i++)
        {
            result[i] = snap[snap.Length - 1 - i];
        }
        return result;
    }

    public List<HistoryEntry> GetEntries(string? filterText = null, string? domain = null, DateTime? fromDate = null, DateTime? toDate = null)
    {
        var snap = _snapshot;
        if (snap.Length == 0)
            return new List<HistoryEntry>();

        bool hasDomain = !string.IsNullOrWhiteSpace(domain) && !domain.Equals("Todos", StringComparison.OrdinalIgnoreCase);
        string? termLower = string.IsNullOrWhiteSpace(filterText) ? null : filterText.Trim().ToLowerInvariant();
        bool hasFrom = fromDate.HasValue;
        bool hasTo = toDate.HasValue;
        DateTime fromVal = fromDate ?? DateTime.MinValue;
        DateTime toVal = toDate ?? DateTime.MaxValue;

        var results = new List<HistoryEntry>(Math.Min(snap.Length, 256));

        // Recorrido inverso: _snapshot ya está ordenado cronológicamente de más antiguo a más reciente,
        // por lo que recorrer de atrás hacia adelante entrega orden descendente en O(N) sin OrderByDescending.
        for (int i = snap.Length - 1; i >= 0; i--)
        {
            var e = snap[i];
            if (hasFrom && e.VisitedAt < fromVal) continue;
            if (hasTo && e.VisitedAt > toVal) continue;
            if (hasDomain && !e.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase)) continue;
            if (termLower != null && !e.SearchKey.Contains(termLower, StringComparison.Ordinal)) continue;

            results.Add(e);
        }

        return results;
    }

    public List<DomainVisitStat> GetDistinctDomains()
    {
        var cached = _cachedDomainStats;
        if (cached != null)
            return cached.ToList();

        var snap = _snapshot;
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < snap.Length; i++)
        {
            string dom = snap[i].Domain;
            if (string.IsNullOrWhiteSpace(dom)) continue;
            counts.TryGetValue(dom, out int c);
            counts[dom] = c + 1;
        }

        var computed = counts
            .Select(kvp => new DomainVisitStat { Domain = kvp.Key, Count = kvp.Value })
            .OrderByDescending(g => g.Count)
            .ToList();

        _cachedDomainStats = computed;
        return computed.ToList();
    }

    public int GetTotalCount() => _snapshot.Length;

    public string GetFileSizeFormatted()
    {
        if (!_isEnabled()) return "0 KB (Historial desactivado)";

        try
        {
            if (File.Exists(_filePath))
            {
                long bytes = new FileInfo(_filePath).Length;
                if (bytes < 1024) return $"{bytes} B";
                if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
                return $"{bytes / (1024.0 * 1024.0):F2} MB";
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HistoryStore] Tamaño falló: {ex.Message}");
        }
        return "0 KB";
    }

    // ─────────────────────────────────────────────────────────────────────
    // Borrados (acciones explícitas del usuario → guardado inmediato)
    // ─────────────────────────────────────────────────────────────────────
    public void DeleteEntry(string id)
    {
        lock (_lock)
        {
            _entries.RemoveAll(e => e.Id == id);
            RebuildSnapshotUnderLock();
        }
        MarkDirtyNow();
    }

    public void DeleteEntries(IEnumerable<string> ids)
    {
        var idSet = new HashSet<string>(ids);
        lock (_lock)
        {
            _entries.RemoveAll(e => idSet.Contains(e.Id));
            RebuildSnapshotUnderLock();
        }
        MarkDirtyNow();
    }

    public void DeleteByDay(DateTime day)
    {
        var target = day.Date;
        lock (_lock)
        {
            _entries.RemoveAll(e => e.VisitedAt.Date == target);
            RebuildSnapshotUnderLock();
        }
        MarkDirtyNow();
    }

    public void DeleteByDomain(string domain, DateTime? fromTime = null)
    {
        if (string.IsNullOrWhiteSpace(domain)) return;
        lock (_lock)
        {
            if (fromTime.HasValue)
                _entries.RemoveAll(e => e.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase) && e.VisitedAt >= fromTime.Value);
            else
                _entries.RemoveAll(e => e.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase));
            RebuildSnapshotUnderLock();
        }
        MarkDirtyNow();
    }

    public void DeleteByTimeRange(DateTime fromTime, DateTime toTime, string? domain = null)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(domain) || domain == "Todos")
                _entries.RemoveAll(e => e.VisitedAt >= fromTime && e.VisitedAt <= toTime);
            else
                _entries.RemoveAll(e => e.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase) && e.VisitedAt >= fromTime && e.VisitedAt <= toTime);
            RebuildSnapshotUnderLock();
        }
        MarkDirtyNow();
    }

    public void ClearAll()
    {
        lock (_lock)
        {
            _entries.Clear();
            RebuildSnapshotUnderLock();
        }
        MarkDirtyNow();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Utilidades
    // ─────────────────────────────────────────────────────────────────────
    public static string ExtractRootDomain(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return string.Empty;

        string host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.")) host = host.Substring(4);

        // IPs (v4/v6) y hosts locales: se devuelven tal cual
        if (uri.HostNameType != UriHostNameType.Dns) return host;

        var parts = host.Split('.');
        if (parts.Length > 2)
        {
            // ccTLDs de dos niveles: .com.pe, .co.uk, .org.es, .gob.pe ...
            bool twoLevelCc = parts[^1].Length == 2 &&
                (parts[^2] is "com" or "org" or "net" or "edu" or "gob" or "gov" or "co" or "ac");
            return string.Join(".", parts.TakeLast(twoLevelCc ? 3 : 2));
        }

        return host;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _saveTimer.Dispose();
        Flush();
    }
}

