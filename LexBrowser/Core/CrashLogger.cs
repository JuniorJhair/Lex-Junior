using System;
using System.IO;
using System.Text;

namespace LexBrowser.Core;

/// <summary>
/// Registro persistente liviano en disco para excepciones UI / tareas en segundo plano no controladas
/// que en compilación Release pasarían inadvertidas con Debug.WriteLine.
/// </summary>
public static class CrashLogger
{
    private const long MaxLogBytes = 512 * 1024;
    private static readonly object SyncLock = new();

    public static string DefaultLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LexBrowser",
        "Logs",
        "crash.log");

    public static string? CustomLogPath { get; set; }
    public static string LogPath => string.IsNullOrWhiteSpace(CustomLogPath) ? DefaultLogPath : CustomLogPath!;

    public static void Log(string context, Exception? exception, string? note = null)
    {
        if (exception == null && string.IsNullOrWhiteSpace(note))
            return;

        var sb = new StringBuilder();
        sb.Append('[').Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ");
        sb.Append('[').Append(string.IsNullOrWhiteSpace(context) ? "APP" : context.Trim()).Append("] ");
        if (!string.IsNullOrWhiteSpace(note))
        {
            sb.Append(note.Trim()).Append(' ');
        }
        if (exception != null)
        {
            sb.Append(exception.GetType().FullName).Append(": ").Append(exception.Message);
            if (!string.IsNullOrWhiteSpace(exception.StackTrace))
            {
                sb.AppendLine().Append(exception.StackTrace);
            }
        }
        sb.AppendLine();

        WriteLineSafe(sb.ToString());
    }

    private static void WriteLineSafe(string entryText)
    {
        try
        {
            string targetPath = LogPath;
            string? dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            lock (SyncLock)
            {
                if (File.Exists(targetPath))
                {
                    var fi = new FileInfo(targetPath);
                    if (fi.Length >= MaxLogBytes)
                    {
                        string rotated = targetPath + ".1";
                        try { File.Move(targetPath, rotated, overwrite: true); } catch { }
                    }
                }

                File.AppendAllText(targetPath, entryText, Encoding.UTF8);
            }
        }
        catch { }
    }
}
