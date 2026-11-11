using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace LexBrowser.Core.Import;

/// <summary>
/// Analiza e importa contraseñas exportadas en CSV estándar (Chrome/Edge/Brave/Opera: name,url,username,password).
/// </summary>
public static class CsvPasswordImporter
{
    public const string DeleteCsvAdvice =
        "Por seguridad, elimina el archivo CSV exportado una vez que verifiques tus contraseñas en la bóveda.";

    public static (List<(string Url, string Username, string Password)> Entries, int Skipped, string? Error) ParseCsv(string csvContent)
    {
        var entries = new List<(string Url, string Username, string Password)>();
        int skipped = 0;

        if (string.IsNullOrWhiteSpace(csvContent))
        {
            return (entries, 0, "El archivo CSV está vacío.");
        }

        // Quitar BOM UTF-8 si existe
        if (csvContent[0] == '\uFEFF')
        {
            csvContent = csvContent.Substring(1);
        }

        var rows = ParseRfc4180Rows(csvContent);
        if (rows.Count < 2)
        {
            return (entries, 0, "El archivo CSV no contiene filas de datos.");
        }

        var header = rows[0];
        int urlIdx = -1, userIdx = -1, passIdx = -1;
        for (int i = 0; i < header.Count; i++)
        {
            string col = header[i].Trim().ToLowerInvariant();
            if (col == "url") urlIdx = i;
            else if (col is "username" or "user") userIdx = i;
            else if (col == "password") passIdx = i;
        }

        if (urlIdx < 0 || userIdx < 0 || passIdx < 0)
        {
            return (entries, 0, "Cabecera CSV inválida: se esperan las columnas url, username y password.");
        }

        int minCols = Math.Max(urlIdx, Math.Max(userIdx, passIdx)) + 1;
        for (int r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.Count == 1 && string.IsNullOrWhiteSpace(row[0]))
            {
                continue;
            }

            if (row.Count < minCols)
            {
                skipped++;
                continue;
            }

            string url = row[urlIdx].Trim();
            string username = row[userIdx];
            string password = row[passIdx];

            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrEmpty(password))
            {
                skipped++;
                continue;
            }

            entries.Add((url, username, password));
        }

        return (entries, skipped, null);
    }

    public static ImportCategoryResult ImportFromFile(string csvPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(csvPath) || !File.Exists(csvPath))
        {
            return new ImportCategoryResult("Contraseñas (CSV)", 0, 0, 0, Error: "No se encontró el archivo CSV seleccionado.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string content = File.ReadAllText(csvPath, Encoding.UTF8);
            cancellationToken.ThrowIfCancellationRequested();

            var (entries, parseSkipped, parseError) = ParseCsv(content);
            if (!string.IsNullOrEmpty(parseError))
            {
                return new ImportCategoryResult("Contraseñas (CSV)", 0, 0, parseSkipped, Error: parseError);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var (imported, duplicates, storeSkipped, saveError) = PasswordManager.ImportCredentials(entries);
            return new ImportCategoryResult(
                Category: "Contraseñas (CSV)",
                Imported: imported,
                Duplicates: duplicates,
                Skipped: parseSkipped + storeSkipped,
                Error: saveError,
                Warning: saveError == null ? DeleteCsvAdvice : null);
        }
        catch (OperationCanceledException)
        {
            return new ImportCategoryResult("Contraseñas (CSV)", 0, 0, 0, Warning: "Importación CSV cancelada.");
        }
        catch (Exception ex)
        {
            return new ImportCategoryResult("Contraseñas (CSV)", 0, 0, 0, Error: $"Error al leer el archivo CSV: {ex.GetType().Name}.");
        }
    }

    private static List<List<string>> ParseRfc4180Rows(string text)
    {
        var rows = new List<List<string>>();
        var currentRow = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
            }
            else
            {
                if (c == '"' && field.Length == 0)
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    currentRow.Add(field.ToString());
                    field.Clear();
                }
                else if (c == '\r' || c == '\n')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }

                    currentRow.Add(field.ToString());
                    field.Clear();
                    rows.Add(currentRow);
                    currentRow = new List<string>();
                }
                else
                {
                    field.Append(c);
                }
            }
        }

        if (field.Length > 0 || currentRow.Count > 0)
        {
            currentRow.Add(field.ToString());
            rows.Add(currentRow);
        }

        return rows;
    }
}

