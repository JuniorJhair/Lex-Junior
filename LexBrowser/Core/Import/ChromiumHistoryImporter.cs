using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace LexBrowser.Core.Import;

/// <summary>
/// Importa historial desde la base SQLite 'History' de navegadores Chromium.
/// </summary>
public static class ChromiumHistoryImporter
{
    private static readonly long MaxFileTimeUtc = DateTime.MaxValue.ToFileTimeUtc();

    /// <summary>
    /// Convierte tiempo Chromium (microsegundos desde 1601-01-01 UTC) a DateTime UTC con guarda de rango.
    /// </summary>
    public static bool TryConvertChromiumTimestamp(long microsecondsSince1601Utc, out DateTime utcDateTime)
    {
        utcDateTime = default;
        if (microsecondsSince1601Utc <= 0) return false;
        if (microsecondsSince1601Utc > MaxFileTimeUtc / 10L) return false;

        long fileTime = microsecondsSince1601Utc * 10L;
        if (fileTime <= 0 || fileTime > MaxFileTimeUtc) return false;

        utcDateTime = DateTime.FromFileTimeUtc(fileTime);
        return true;
    }

    public static (List<(string Url, string Title, DateTime VisitedAt)> Entries, int Skipped, string? Error) ReadFromDatabase(
        string historyDbPath,
        CancellationToken cancellationToken = default)
    {
        var list = new List<(string Url, string Title, DateTime VisitedAt)>();
        int skipped = 0;

        if (string.IsNullOrWhiteSpace(historyDbPath) || !File.Exists(historyDbPath))
        {
            return (list, 0, "No se encontró el archivo History en el perfil seleccionado.");
        }

        try
        {
            using var db = SqliteTempReaderHelper.OpenReadOnlyCopy(historyDbPath, cancellationToken);
            using var cmd = db.Connection.CreateCommand();
            cmd.CommandText = "SELECT url, title, last_visit_time FROM urls";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();

                string url = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                string title = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                long rawTime = reader.IsDBNull(2) ? 0L : reader.GetInt64(2);

                if (string.IsNullOrWhiteSpace(url) ||
                    !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    skipped++;
                    continue;
                }

                if (!TryConvertChromiumTimestamp(rawTime, out DateTime visitedUtc))
                {
                    skipped++;
                    continue;
                }

                list.Add((url.Trim(), title, visitedUtc));
            }

            return (list, skipped, null);
        }
        catch (OperationCanceledException)
        {
            return (list, skipped, "Importación de historial cancelada.");
        }
        catch (Exception ex)
        {
            return (list, skipped, $"Error al leer el historial SQLite: {ex.Message}");
        }
    }

    public static ImportCategoryResult ImportFromProfile(
        BrowserProfileInfo profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (!SettingsManager.Current.EnableHistory)
        {
            return new ImportCategoryResult(
                Category: "Historial",
                Imported: 0,
                Duplicates: 0,
                Skipped: 0,
                Warning: "El historial está desactivado en la configuración de Lex Junior; no se importaron visitas.");
        }

        string dbPath = Path.Combine(profile.ProfileDirectory, "History");
        var (rows, readSkipped, readError) = ReadFromDatabase(dbPath, cancellationToken);
        if (!string.IsNullOrEmpty(readError))
        {
            return new ImportCategoryResult("Historial", 0, 0, readSkipped, Error: readError);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new ImportCategoryResult("Historial", 0, 0, readSkipped, Warning: "Importación de historial cancelada.");
        }

        var (imported, duplicates, storeSkipped, warning) = HistoryManager.ImportEntries(rows);
        return new ImportCategoryResult(
            Category: "Historial",
            Imported: imported,
            Duplicates: duplicates,
            Skipped: readSkipped + storeSkipped,
            Warning: warning);
    }
}

