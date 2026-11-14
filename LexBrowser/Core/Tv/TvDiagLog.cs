using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace LexBrowser.Core.Tv;

public sealed class TvDiagEntry
{
    public string Timestamp { get; set; } = "";
    public string Category { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Detail { get; set; }
}

/// <summary>
/// Registro diagnóstico en memoria y disco para flujos, imágenes y peticiones Xtream de Lex TV.
/// Redacta credenciales y tokens antes de persistir o exponer entradas.
/// </summary>
public sealed class TvDiagLog
{
    public const int MaxMemoryEntries = 400;
    public const long MaxLogFileBytes = 512 * 1024;

    private static readonly Regex XtreamSegmentRegex = new(
        @"(/(?:live|movie|series))/[^/?#\s]+/[^/?#\s]+/",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LegacyXtreamSegmentRegex = new(
        @"(https?://[^/?#\s]+)/[^/?#\s]+/[^/?#\s]+/(\d+(?:\.[a-z0-9]{1,5})?(?=[?#\s|,;]|$))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex QuerySecretRegex = new(
        @"([?&](?:password|pass|pwd|username|user|token|secret|auth|api_key)=)[^&#\s]*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AuthorityCredRegex = new(
        @"(://)([^/@:?\s]+):([^/@?\s]+)(@)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly object _sync = new();
    private readonly List<TvDiagEntry> _entries = new();
    private readonly string _logPath;

    public static TvDiagLog Shared { get; } = new(TvUserStateStore.GetDefaultDirectory());

    public TvDiagLog(string directory)
    {
        string baseDir = string.IsNullOrWhiteSpace(directory)
            ? TvUserStateStore.GetDefaultDirectory()
            : directory;
        _logPath = Path.Combine(baseDir, "tv_runtime.log");
    }

    public string LogPath => _logPath;

    public static void Write(string category, string message, string? detail = null)
        => Shared.Record(category, message, detail);

    public static string RedactUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        string clean = TvUrlGuard.SanitizeHeaderValue(raw, 512);
        clean = AuthorityCredRegex.Replace(clean, "$1***:***$4");
        clean = XtreamSegmentRegex.Replace(clean, "$1/***/***/");
        clean = LegacyXtreamSegmentRegex.Replace(clean, "$1/***/***/$2");
        clean = QuerySecretRegex.Replace(clean, "$1***");
        return clean;
    }

    public void Record(string category, string message, string? detail = null)
    {
        string cat = string.IsNullOrWhiteSpace(category) ? "tv" : TvUrlGuard.SanitizeHeaderValue(category, 32);
        string msg = TvUrlGuard.SanitizeHeaderValue(message, 240);
        string? cleanDetail = string.IsNullOrWhiteSpace(detail)
            ? null
            : RedactUrl(TvUrlGuard.SanitizeHeaderValue(detail, 600));

        var entry = new TvDiagEntry
        {
            Timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            Category = cat,
            Message = msg,
            Detail = cleanDetail
        };

        string line = string.IsNullOrEmpty(entry.Detail)
            ? $"[{entry.Timestamp}] [{entry.Category}] {entry.Message}{Environment.NewLine}"
            : $"[{entry.Timestamp}] [{entry.Category}] {entry.Message} | {entry.Detail}{Environment.NewLine}";

        lock (_sync)
        {
            _entries.Add(entry);
            if (_entries.Count > MaxMemoryEntries)
            {
                _entries.RemoveRange(0, _entries.Count - MaxMemoryEntries);
            }

            AppendToFileNoThrow(line);
        }
    }

    public List<TvDiagEntry> GetSnapshot(int limit = 200)
    {
        int capped = Math.Clamp(limit, 1, MaxMemoryEntries);
        lock (_sync)
        {
            int skip = Math.Max(0, _entries.Count - capped);
            var list = new List<TvDiagEntry>(_entries.Count - skip);
            for (int i = skip; i < _entries.Count; i++)
            {
                var e = _entries[i];
                list.Add(new TvDiagEntry
                {
                    Timestamp = e.Timestamp,
                    Category = e.Category,
                    Message = e.Message,
                    Detail = e.Detail
                });
            }
            return list;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            try
            {
                if (File.Exists(_logPath)) File.Delete(_logPath);
                string rotated = _logPath + ".1";
                if (File.Exists(rotated)) File.Delete(rotated);
            }
            catch { }
        }
    }

    private void AppendToFileNoThrow(string line)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(_logPath))
            {
                var info = new FileInfo(_logPath);
                if (info.Length >= MaxLogFileBytes)
                {
                    string backup = _logPath + ".1";
                    File.Move(_logPath, backup, overwrite: true);
                }
            }

            File.AppendAllText(_logPath, line, Encoding.UTF8);
        }
        catch
        {
            // No interrumpir el reproductor ni las pruebas si el directorio de logs no es escribible
        }
    }
}

