using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using LexBrowser.UI.Models;

namespace LexBrowser.Core.Import;

/// <summary>
/// Lee y aplana el archivo JSON Bookmarks de navegadores basados en Chromium.
/// </summary>
public static class ChromiumBookmarkImporter
{
    private static readonly string[] RootKeys = { "bookmark_bar", "other", "synced" };

    public static (List<BookmarkItem> Items, int Skipped, string? Error) ParseBookmarksJson(string json)
    {
        var items = new List<BookmarkItem>();
        int skipped = 0;

        if (string.IsNullOrWhiteSpace(json))
        {
            return (items, 0, "El archivo Bookmarks está vacío.");
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("roots", out var rootsElem) ||
                rootsElem.ValueKind != JsonValueKind.Object)
            {
                return (items, 0, "Formato de marcadores inválido: falta la sección 'roots'.");
            }

            foreach (string rootKey in RootKeys)
            {
                if (rootsElem.TryGetProperty(rootKey, out var rootNode))
                {
                    TraverseNode(rootNode, items, ref skipped);
                }
            }

            return (items, skipped, null);
        }
        catch (JsonException ex)
        {
            return (items, skipped, $"JSON de marcadores malformado: {ex.Message}");
        }
    }

    public static ImportCategoryResult ImportFromProfile(
        BrowserProfileInfo profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string path = Path.Combine(profile.ProfileDirectory, "Bookmarks");
        if (!File.Exists(path))
        {
            return new ImportCategoryResult("Marcadores", 0, 0, 0, Error: "No se encontró el archivo Bookmarks en el perfil seleccionado.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            string json = reader.ReadToEnd();
            cancellationToken.ThrowIfCancellationRequested();

            var (parsed, parseSkipped, parseError) = ParseBookmarksJson(json);
            if (!string.IsNullOrEmpty(parseError))
            {
                return new ImportCategoryResult("Marcadores", 0, 0, parseSkipped, Error: parseError);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var (imported, duplicates, storeSkipped) = BookmarkManager.ImportBookmarks(parsed);
            return new ImportCategoryResult(
                Category: "Marcadores",
                Imported: imported,
                Duplicates: duplicates,
                Skipped: parseSkipped + storeSkipped);
        }
        catch (OperationCanceledException)
        {
            return new ImportCategoryResult("Marcadores", 0, 0, 0, Warning: "Importación de marcadores cancelada.");
        }
        catch (Exception ex)
        {
            return new ImportCategoryResult("Marcadores", 0, 0, 0, Error: $"Error al leer marcadores: {ex.Message}");
        }
    }

    private static void TraverseNode(JsonElement node, List<BookmarkItem> output, ref int skipped)
    {
        if (node.ValueKind != JsonValueKind.Object) return;

        string? type = node.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String
            ? typeProp.GetString()
            : null;

        if (string.Equals(type, "url", StringComparison.OrdinalIgnoreCase))
        {
            string? url = node.TryGetProperty("url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String
                ? urlProp.GetString()
                : null;
            string? name = node.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                ? nameProp.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(url) ||
                !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                skipped++;
                return;
            }

            output.Add(new BookmarkItem
            {
                Title = string.IsNullOrWhiteSpace(name) ? uri.Host : name.Trim(),
                Url = url.Trim(),
                FaviconUrl = string.Empty
            });
            return;
        }

        if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                TraverseNode(child, output, ref skipped);
            }
        }
    }
}

