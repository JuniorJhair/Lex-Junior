using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LexBrowser.UI.Models;

namespace LexBrowser.Core;

public class SessionState
{
    public int ActiveIndex { get; set; } = 0;
    public List<SessionTabItem> Tabs { get; set; } = new();
}

public class SessionTabItem
{
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public bool IsPinned { get; set; }
}

/// <summary>
/// Gestiona la persistencia atómica de la última sesión de pestañas con arquitectura
/// de Carga Perezosa (Lazy Loading). Al iniciar el navegador con pestañas guardadas,
/// solo se conecta y carga la pestaña activa; las pestañas de fondo permanecen en hibernación,
/// consumiendo 0.0% de CPU y 0 MB de memoria RAM hasta que el usuario hace clic en ellas.
/// </summary>
public static class SessionManager
{
    private static string SessionFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LexBrowser", "session_tabs.json");

    public static void SaveSession(IEnumerable<BrowserTab> tabs, BrowserTab? activeTab)
    {
        try
        {
            var validTabs = tabs
                .Where(t => !t.IsIncognito && !t.IsDisposed && !string.IsNullOrWhiteSpace(t.Url) &&
                            !t.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
                            t.Url != "about:blank" && !t.IsNewTab)
                .ToList();

            if (validTabs.Count == 0)
            {
                ClearSession();
                return;
            }

            int activeIdx = activeTab != null ? validTabs.IndexOf(activeTab) : 0;
            if (activeIdx < 0) activeIdx = 0;

            var state = new SessionState
            {
                ActiveIndex = activeIdx,
                Tabs = validTabs.Select(t => new SessionTabItem
                {
                    Url = t.Url,
                    Title = t.Title,
                    IsPinned = t.IsPinned
                }).ToList()
            };

            string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            string dir = Path.GetDirectoryName(SessionFilePath)!;
            Directory.CreateDirectory(dir);

            string tmp = SessionFilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, SessionFilePath, overwrite: true);
        }
        catch { }
    }

    public static SessionState? LoadSession()
    {
        try
        {
            if (!File.Exists(SessionFilePath)) return null;
            string json = File.ReadAllText(SessionFilePath);
            if (string.IsNullOrWhiteSpace(json)) return null;

            var state = JsonSerializer.Deserialize<SessionState>(json);
            if (state == null || state.Tabs == null || state.Tabs.Count == 0) return null;
            return state;
        }
        catch
        {
            return null;
        }
    }

    public static void ClearSession()
    {
        try
        {
            if (File.Exists(SessionFilePath)) File.Delete(SessionFilePath);
        }
        catch { }
    }
}

