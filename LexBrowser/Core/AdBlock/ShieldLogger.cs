using System;
using System.Collections.Generic;
using System.Text;
using LexBrowser.UI.Models;

namespace LexBrowser.Core.AdBlock;

/// <summary>
/// Entrada individual en el registro de actividad del escudo protector (Shield Logger).
/// </summary>
public sealed class ShieldLogEntry
{
    public DateTime Timestamp { get; } = DateTime.Now;
    public string EventType { get; set; } = string.Empty;
    public string SourceDomain { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Frame { get; set; } = "MainFrame";
    public string Reason { get; set; } = string.Empty;
    public string TabName { get; set; } = string.Empty;

    public override string ToString()
    {
        string tabPart = string.IsNullOrEmpty(TabName) ? "" : $"[{TabName}] ";
        return $"[{Timestamp:HH:mm:ss.fff}] [{EventType}] {tabPart}[{Frame}] Origin: {SourceDomain} | Url: {Url} | Reason: {Reason}";
    }
}

/// <summary>
/// Registro en memoria circular (ring buffer) de eventos de filtrado publicitario,
/// navegación y ventanas de Lex Junior. Diseñado para diagnóstico forense sin impacto en CPU.
/// </summary>
public static class ShieldLogger
{
    private const int MaxEntries = 2000;
    private static readonly Queue<ShieldLogEntry> Entries = new(MaxEntries);
    private static readonly object SyncLock = new();

    /// <summary>
    /// Cuando está desactivado (valor por defecto), omite eventos ruidosos de tráfico permitido
    /// (REQUEST_ALLOWED, NAVIGATION_STARTING) para evitar presión de memoria en navegación normal.
    /// </summary>
    public static bool DiagnosticMode { get; set; }

    /// <summary>
    /// Registra un evento asociado a una pestaña, omitiendo automáticamente cualquier registro
    /// si la pestaña pertenece a Modo Invitado o Modo Incógnito para proteger la privacidad.
    /// </summary>
    public static void LogForTab(BrowserTab? tab, string eventType, string sourceDomain, string url, string frame, string reason, string tabName = "")
    {
        if (tab != null && (tab.IsGuest || tab.IsIncognito))
            return;

        string tabLabel = !string.IsNullOrWhiteSpace(tabName)
            ? tabName
            : (tab != null ? $"Tab:{tab.TabId} ({(tab.CurrentUri != null ? tab.CurrentUri.Host : "blank")})" : string.Empty);

        Log(eventType, sourceDomain, url, frame, reason, tabLabel, isPrivateOrGuest: false);
    }

    /// <summary>
    /// Registra un evento de seguridad / bloqueo en el buffer circular.
    /// Sanitiza URLs de tipo data: y cadenas excesivamente largas para no saturar memoria ni el portapapeles.
    /// </summary>
    public static void Log(string eventType, string sourceDomain, string url, string frame, string reason, string tabName = "", bool isPrivateOrGuest = false)
    {
        if (isPrivateOrGuest)
            return;

        if (!DiagnosticMode &&
            (string.Equals(eventType, "REQUEST_ALLOWED", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(eventType, "NAVIGATION_STARTING", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(eventType, "SUBFRAME_NAVIGATION_STARTING", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        string sanitizedUrl = url ?? string.Empty;
        if (sanitizedUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int commaIdx = sanitizedUrl.IndexOf(',');
            string prefix = commaIdx > 0 && commaIdx <= 40 ? sanitizedUrl.Substring(0, commaIdx + 1) : "data:...";
            sanitizedUrl = $"{prefix}[data-uri truncated, len={sanitizedUrl.Length}]";
        }
        else if (sanitizedUrl.Length > 256)
        {
            sanitizedUrl = sanitizedUrl.Substring(0, 253) + "...";
        }

        string sanitizedReason = reason ?? string.Empty;
        if (sanitizedReason.Length > 200)
        {
            sanitizedReason = sanitizedReason.Substring(0, 197) + "...";
        }

        string sanitizedSource = string.IsNullOrWhiteSpace(sourceDomain) ? "about:blank" : sourceDomain;
        if (sanitizedSource.Length > 100)
        {
            sanitizedSource = sanitizedSource.Substring(0, 97) + "...";
        }

        var entry = new ShieldLogEntry
        {
            EventType = eventType ?? "UNKNOWN",
            SourceDomain = sanitizedSource,
            Url = sanitizedUrl,
            Frame = string.IsNullOrWhiteSpace(frame) ? "MainFrame" : frame,
            Reason = sanitizedReason,
            TabName = tabName ?? string.Empty
        };

        lock (SyncLock)
        {
            if (Entries.Count >= MaxEntries)
            {
                Entries.Dequeue();
            }
            Entries.Enqueue(entry);
        }
    }

    /// <summary>
    /// Cantidad actual de eventos en memoria.
    /// </summary>
    public static int Count
    {
        get
        {
            lock (SyncLock) return Entries.Count;
        }
    }

    /// <summary>
    /// Limpia el buffer de eventos.
    /// </summary>
    public static void Clear()
    {
        lock (SyncLock) Entries.Clear();
    }

    /// <summary>
    /// Vuelca el buffer de eventos a una cadena de texto estructurada, opcionalmente filtrando por pestaña.
    /// </summary>
    public static string DumpLog(string? tabFilter = null)
    {
        ShieldLogEntry[] snapshot;
        lock (SyncLock)
        {
            snapshot = Entries.ToArray();
        }

        var sb = new StringBuilder();
        sb.AppendLine($"=== LEX JUNIOR - SHIELD LOGGER DUMP ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) ===");
        sb.AppendLine($"Total entries: {snapshot.Length} (Buffer limit: {MaxEntries})");
        if (!string.IsNullOrEmpty(tabFilter))
        {
            sb.AppendLine($"[Filtro activo: Pestaña '{tabFilter}']");
        }
        sb.AppendLine(new string('=', 90));

        foreach (var entry in snapshot)
        {
            if (string.IsNullOrEmpty(tabFilter) || entry.TabName.Contains(tabFilter, StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine(entry.ToString());
            }
        }

        sb.AppendLine(new string('=', 90));
        sb.AppendLine("=== END OF SHIELD LOGGER DUMP ===");
        return sb.ToString();
    }
}

