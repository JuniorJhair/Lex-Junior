using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using LexBrowser.UI.Models;

namespace LexBrowser.Core;

public static class BookmarkManager
{
    private static readonly string StoragePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LexBrowser",
        "bookmarks.json"
    );

    internal static string? CustomStoragePath { get; set; }
    private static string EffectiveStoragePath => string.IsNullOrWhiteSpace(CustomStoragePath) ? StoragePath : CustomStoragePath!;

    private static readonly JsonSerializerOptions BookmarkJsonOptions = new() { WriteIndented = true };

    public static ObservableCollection<BookmarkItem> Bookmarks { get; } = new();

    // Historial para deshacer eliminación
    public static (BookmarkItem Item, int Index)? LastRemovedBookmark { get; private set; }
    public static string? LastLoadError { get; private set; }
    public static string? LastQuarantineBackupPath { get; private set; }

    static BookmarkManager()
    {
        Load();
    }

    public static void Load()
    {
        string targetPath = EffectiveStoragePath;
        LastLoadError = null;
        LastQuarantineBackupPath = null;

        if (!File.Exists(targetPath))
        {
            Bookmarks.Clear();
            Bookmarks.Add(new BookmarkItem { Title = "YouTube", Url = "https://www.youtube.com" });
            Bookmarks.Add(new BookmarkItem { Title = "Google", Url = "https://www.google.com" });
            Bookmarks.Add(new BookmarkItem { Title = "Twitch", Url = "https://www.twitch.tv" });
            Save();
            return;
        }

        try
        {
            string json = File.ReadAllText(targetPath);
            var items = JsonSerializer.Deserialize<BookmarkItem[]>(json);
            if (items == null)
                throw new InvalidDataException("El archivo bookmarks.json deserializó como nulo.");

            Bookmarks.Clear();
            foreach (var item in items)
            {
                if (item != null)
                {
                    Bookmarks.Add(item);
                }
            }
        }
        catch (Exception ex)
        {
            LastLoadError = ex.Message;
            LastQuarantineBackupPath = AtomicFile.QuarantineCorruptFile(targetPath, ".corrupt-");
            CrashLogger.Log("BookmarkManager.Load", ex, $"Path={targetPath}; Quarantine={LastQuarantineBackupPath}");
            // No sobrescribir el archivo dañado ni reinyectar marcadores por defecto encima.
            Bookmarks.Clear();
        }
    }

    public static void Save()
    {
        try
        {
            string targetPath = EffectiveStoragePath;
            var snapshot = Bookmarks.ToList();
            string json = JsonSerializer.Serialize(snapshot, BookmarkJsonOptions);
            AtomicFile.WriteAllTextAtomic(targetPath, json);
        }
        catch (Exception ex)
        {
            CrashLogger.Log("BookmarkManager.Save", ex);
        }
    }

    public static (int Imported, int Duplicates, int Skipped) ImportBookmarks(IEnumerable<BookmarkItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var staged = items.ToList();
        if (staged.Count == 0) return (0, 0, 0);

        (int Imported, int Duplicates, int Skipped) ExecuteCore()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var existing in Bookmarks)
            {
                if (!string.IsNullOrWhiteSpace(existing.Url))
                {
                    seen.Add(existing.Url.Trim().TrimEnd('/'));
                }
            }

            int imported = 0, duplicates = 0, skipped = 0;
            foreach (var item in staged)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Url))
                {
                    skipped++;
                    continue;
                }

                string trimmedUrl = item.Url.Trim();
                if (!Uri.TryCreate(trimmedUrl, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    skipped++;
                    continue;
                }

                string dedupKey = trimmedUrl.TrimEnd('/');
                if (!seen.Add(dedupKey))
                {
                    duplicates++;
                    continue;
                }

                Bookmarks.Add(new BookmarkItem
                {
                    Title = string.IsNullOrWhiteSpace(item.Title) ? uri.Host : item.Title.Trim(),
                    Url = trimmedUrl,
                    FaviconUrl = item.FaviconUrl ?? string.Empty
                });
                imported++;
            }

            if (imported > 0)
            {
                Save();
            }

            return (imported, duplicates, skipped);
        }

        var app = System.Windows.Application.Current;
        if (app != null && !app.Dispatcher.CheckAccess())
        {
            return app.Dispatcher.Invoke(ExecuteCore);
        }

        return ExecuteCore();
    }

    public static bool IsBookmarked(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        string clean = url.TrimEnd('/');
        return Bookmarks.Any(b => b.Url.TrimEnd('/').Equals(clean, StringComparison.OrdinalIgnoreCase));
    }

    public static bool ToggleBookmark(string title, string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.StartsWith("data:") || url == "about:blank")
            return false;

        string clean = url.TrimEnd('/');
        var existing = Bookmarks.FirstOrDefault(b => b.Url.TrimEnd('/').Equals(clean, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            int index = Bookmarks.IndexOf(existing);
            LastRemovedBookmark = (existing, index);
            Bookmarks.Remove(existing);
            Save();
            return false; // Ahora no está anclada
        }
        else
        {
            var newItem = new BookmarkItem
            {
                Title = string.IsNullOrWhiteSpace(title) ? clean : title,
                Url = url
            };
            Bookmarks.Add(newItem);
            Save();
            return true; // Ahora está anclada
        }
    }

    public static void Remove(BookmarkItem item)
    {
        int index = Bookmarks.IndexOf(item);
        if (index >= 0)
        {
            LastRemovedBookmark = (item, index);
            Bookmarks.RemoveAt(index);
            Save();
        }
    }

    public static BookmarkItem? UndoRemove()
    {
        if (LastRemovedBookmark.HasValue)
        {
            var (item, index) = LastRemovedBookmark.Value;
            if (index >= 0 && index <= Bookmarks.Count)
            {
                Bookmarks.Insert(index, item);
            }
            else
            {
                Bookmarks.Add(item);
            }
            Save();
            LastRemovedBookmark = null;
            return item;
        }
        return null;
    }
}

