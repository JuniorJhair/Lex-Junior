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

    public static ObservableCollection<BookmarkItem> Bookmarks { get; } = new();

    // Historial para deshacer eliminación
    public static (BookmarkItem Item, int Index)? LastRemovedBookmark { get; private set; }

    static BookmarkManager()
    {
        Load();
    }

    public static void Load()
    {
        try
        {
            if (File.Exists(StoragePath))
            {
                string json = File.ReadAllText(StoragePath);
                var items = JsonSerializer.Deserialize<BookmarkItem[]>(json);
                Bookmarks.Clear();
                if (items != null && items.Length > 0)
                {
                    foreach (var item in items)
                    {
                        Bookmarks.Add(item);
                    }
                    return;
                }
            }
        }
        catch { }

        // Marcadores predeterminados si está vacío
        if (Bookmarks.Count == 0)
        {
            Bookmarks.Add(new BookmarkItem { Title = "YouTube", Url = "https://www.youtube.com" });
            Bookmarks.Add(new BookmarkItem { Title = "Google", Url = "https://www.google.com" });
            Bookmarks.Add(new BookmarkItem { Title = "Twitch", Url = "https://www.twitch.tv" });
            Save();
        }
    }

    public static void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(StoragePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            string json = JsonSerializer.Serialize(Bookmarks.ToList(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(StoragePath, json);
        }
        catch { }
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

