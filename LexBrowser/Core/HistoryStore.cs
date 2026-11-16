using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
                        bool repairedAnyTitle = false;
                        _entries.Clear();
                        foreach (var item in list)
                        {
                            if (item == null || string.IsNullOrWhiteSpace(item.Url)) continue;
                            if (Tv.TvStudioService.IsTvUrl(item.Url)) continue;
                            // Filtrar o desenvolver URLs internas del visor PDF que pudieran haberse guardado antes
                            if (Pdf.PdfStudioService.TryUnwrapInternalViewerUrl(item.Url, out string? unwrappedPdf))
                            {
                                if (string.IsNullOrWhiteSpace(unwrappedPdf)) continue;
                                item.Url = unwrappedPdf;
                                item.Domain = ExtractRootDomain(unwrappedPdf);
                            }
                            string repairedTitle = RepairMojibake(item.Title);
                            if (!string.Equals(repairedTitle, item.Title, StringComparison.Ordinal))
                            {
                                item.Title = repairedTitle;
                                repairedAnyTitle = true;
                            }
                            _ = item.SearchKey;
                            _entries.Add(item);
                        }
                        _entries.Sort((a, b) => a.VisitedAt.CompareTo(b.VisitedAt));
                        RebuildSnapshotUnderLock();
                        if (repairedAnyTitle)
                        {
                            _dirty = true;
                            if (!_disposed) _saveTimer.Change(_saveDelay, Timeout.InfiniteTimeSpan);
                        }
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

        if (Tv.TvStudioService.IsTvUrl(trimmed))
        {
            return false;
        }

        normalizedUrl = trimmed;
        if (string.IsNullOrWhiteSpace(title))
        {
            cleanTitle = normalizedUrl;
        }
        else
        {
            string trimmedTitle = RepairMojibake(title.Trim());
            cleanTitle = string.IsNullOrWhiteSpace(trimmedTitle) ? normalizedUrl : trimmedTitle;
        }
        return true;
    }

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Repara secuencias UTF-8 decodificadas erróneamente como Latin-1/Windows-1252
    /// (p. ej. "Ãºltimo" → "último", "â€“" → "–") sin alterar texto UTF-8 válido.
    /// </summary>
    internal static string RepairMojibake(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        bool hasMarker = false;
        foreach (char ch in text)
        {
            if (ch == 'Ã' || ch == 'Â' || ch == 'â' || ch == 'ð' || ch == 'Ð' || ch == 'Ñ')
            {
                hasMarker = true;
                break;
            }
        }
        if (!hasMarker) return text;

        var sb = new StringBuilder(text.Length);
        Span<byte> seq = stackalloc byte[4];
        bool changed = false;

        for (int i = 0; i < text.Length; i++)
        {
            if (TryToWin1252Byte(text[i], out byte b0))
            {
                int seqLen = 0;
                if (b0 >= 0xC2 && b0 <= 0xDF) seqLen = 2;
                else if (b0 >= 0xE0 && b0 <= 0xEF) seqLen = 3;
                else if (b0 >= 0xF0 && b0 <= 0xF4) seqLen = 4;

                if (seqLen > 0 && i + seqLen <= text.Length)
                {
                    seq[0] = b0;
                    bool allCont = true;
                    for (int k = 1; k < seqLen; k++)
                    {
                        if (!TryToWin1252Byte(text[i + k], out byte bk) || (bk & 0xC0) != 0x80)
                        {
                            allCont = false;
                            break;
                        }
                        seq[k] = bk;
                    }

                    if (allCont)
                    {
                        try
                        {
                            string decoded = StrictUtf8.GetString(seq.Slice(0, seqLen));
                            if (decoded.Length > 0 && !(decoded.Length == 1 && decoded[0] >= '\u0080' && decoded[0] <= '\u009F'))
                            {
                                sb.Append(decoded);
                                i += seqLen - 1;
                                changed = true;
                                continue;
                            }
                        }
                        catch (DecoderFallbackException)
                        {
                        }
                    }
                }
            }
            sb.Append(text[i]);
        }

        return changed ? sb.ToString() : text;
    }

    private static bool TryToWin1252Byte(char ch, out byte value)
    {
        if (ch <= '\u00FF')
        {
            value = (byte)ch;
            return true;
        }

        switch (ch)
        {
            case '\u20AC': value = 0x80; return true; // €
            case '\u201A': value = 0x82; return true; // ‚
            case '\u0192': value = 0x83; return true; // ƒ
            case '\u201E': value = 0x84; return true; // „
            case '\u2026': value = 0x85; return true; // …
            case '\u2020': value = 0x86; return true; // †
            case '\u2021': value = 0x87; return true; // ‡
            case '\u02C6': value = 0x88; return true; // ˆ
            case '\u2030': value = 0x89; return true; // ‰
            case '\u0160': value = 0x8A; return true; // Š
            case '\u2039': value = 0x8B; return true; // ‹
            case '\u0152': value = 0x8C; return true; // Œ
            case '\u017D': value = 0x8E; return true; // Ž
            case '\u2018': value = 0x91; return true; // ‘
            case '\u2019': value = 0x92; return true; // ’
            case '\u201C': value = 0x93; return true; // “
            case '\u201D': value = 0x94; return true; // ”
            case '\u2022': value = 0x95; return true; // •
            case '\u2013': value = 0x96; return true; // –
            case '\u2014': value = 0x97; return true; // —
            case '\u02DC': value = 0x98; return true; // ˜
            case '\u2122': value = 0x99; return true; // ™
            case '\u0161': value = 0x9A; return true; // š
            case '\u203A': value = 0x9B; return true; // ›
            case '\u0153': value = 0x9C; return true; // œ
            case '\u017E': value = 0x9E; return true; // ž
            case '\u0178': value = 0x9F; return true; // Ÿ
            default:
                value = 0;
                return false;
        }
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

    private static long ToUtcTicks(DateTime dt) =>
        (dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : dt).Ticks;

    private static long ToUtcSecondBucket(DateTime dt) =>
        ToUtcTicks(dt) / TimeSpan.TicksPerSecond;

    private sealed class UrlSecondKeyComparer : IEqualityComparer<(string Url, long SecondBucket)>
    {
        public static readonly UrlSecondKeyComparer Instance = new();

        public bool Equals((string Url, long SecondBucket) x, (string Url, long SecondBucket) y) =>
            x.SecondBucket == y.SecondBucket && string.Equals(x.Url, y.Url, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Url, long SecondBucket) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Url ?? string.Empty), obj.SecondBucket);
    }

    public (int Imported, int Duplicates, int Skipped, string? Warning) ImportEntries(
        IEnumerable<(string Url, string Title, DateTime VisitedAt)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var staged = entries.ToList();
        if (!_isEnabled())
        {
            return (0, 0, staged.Count, "El historial está desactivado en la configuración de Lex Junior.");
        }

        if (staged.Count == 0) return (0, 0, 0, null);

        int imported = 0, duplicates = 0, skipped = 0;
        lock (_lock)
        {
            var seen = new HashSet<(string Url, long SecondBucket)>(UrlSecondKeyComparer.Instance);
            foreach (var existing in _entries)
            {
                if (!string.IsNullOrEmpty(existing.Url))
                {
                    seen.Add((existing.Url, ToUtcSecondBucket(existing.VisitedAt)));
                }
            }

            foreach (var (rawUrl, rawTitle, rawVisitedAt) in staged)
            {
                if (!TryNormalize(rawUrl, rawTitle, out string normalizedUrl, out string cleanTitle) ||
                    !Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    skipped++;
                    continue;
                }

                DateTime visitedAt = rawVisitedAt == default ? _clock() : rawVisitedAt;
                var key = (normalizedUrl, ToUtcSecondBucket(visitedAt));
                if (!seen.Add(key))
                {
                    duplicates++;
                    continue;
                }

                _entries.Add(new HistoryEntry
                {
                    Url = normalizedUrl,
                    Title = cleanTitle,
                    Domain = ExtractRootDomain(normalizedUrl),
                    VisitedAt = visitedAt
                });
                imported++;
            }

            if (imported > 0)
            {
                _entries.Sort(static (a, b) => ToUtcTicks(a.VisitedAt).CompareTo(ToUtcTicks(b.VisitedAt)));
                if (_entries.Count > _maxEntries)
                {
                    _entries.RemoveRange(0, _entries.Count - _maxEntries);
                }
                RebuildSnapshotUnderLock();
            }
        }

        if (imported > 0)
        {
            MarkDirtyNow();
        }

        return (imported, duplicates, skipped, null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _saveTimer.Dispose();
        Flush();
    }
}

