using System;
using System.Collections.Generic;
using System.Text;

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

    public override string ToString()
    {
        return $"[{Timestamp:HH:mm:ss.fff}] [{EventType}] [{Frame}] Origin: {SourceDomain} | Url: {Url} | Reason: {Reason}";
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
    /// Registra un evento de seguridad / bloqueo en el buffer circular.
    /// </summary>
    public static void Log(string eventType, string sourceDomain, string url, string frame, string reason)
    {
        var entry = new ShieldLogEntry
        {
            EventType = eventType ?? "UNKNOWN",
            SourceDomain = string.IsNullOrWhiteSpace(sourceDomain) ? "about:blank" : sourceDomain,
            Url = url ?? string.Empty,
            Frame = string.IsNullOrWhiteSpace(frame) ? "MainFrame" : frame,
            Reason = reason ?? string.Empty
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
    /// Vuelca todo el buffer de eventos a una cadena de texto estructurada.
    /// </summary>
    public static string DumpLog()
    {
        ShieldLogEntry[] snapshot;
        lock (SyncLock)
        {
            snapshot = Entries.ToArray();
        }

        var sb = new StringBuilder();
        sb.AppendLine($"=== LEX JUNIOR - SHIELD LOGGER DUMP ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) ===");
        sb.AppendLine($"Total entries: {snapshot.Length} (Buffer limit: {MaxEntries})");
        sb.AppendLine(new string('=', 90));

        foreach (var entry in snapshot)
        {
            sb.AppendLine(entry.ToString());
        }

        sb.AppendLine(new string('=', 90));
        sb.AppendLine("=== END OF SHIELD LOGGER DUMP ===");
        return sb.ToString();
    }
}

