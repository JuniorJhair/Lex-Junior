using System;
using System.IO;
using System.Text.Json;

namespace LexBrowser.Core;

public class BrowserSettings
{
    public bool ShowFloatingPageButton { get; set; } = true;
    public bool EnableAutoVideoPip { get; set; } = true;
    public bool ShowBookmarksBar { get; set; } = true;
    public bool EnableAdBlocker { get; set; } = true;
    public bool EnableHistory { get; set; } = true;
    public double FloatingPipWidth { get; set; } = 640;
    public double FloatingPipHeight { get; set; } = 390;
    public int NativePipWidth { get; set; } = 0;
    public int NativePipHeight { get; set; } = 0;
    public int NativePipShortsWidth { get; set; } = 0;
    public int NativePipShortsHeight { get; set; } = 0;
    public bool HasCustomMainWindowBounds { get; set; } = false;
    public double MainWindowWidth { get; set; } = 1280;
    public double MainWindowHeight { get; set; } = 800;
    public double MainWindowLeft { get; set; } = double.NaN;
    public double MainWindowTop { get; set; } = double.NaN;
    public string DefaultSearchEngine { get; set; } = "Google";
    public string CustomSearchUrl { get; set; } = "https://www.google.com/search?q=";

    // Guardador de Contraseñas y Bloqueo Inteligente del Navegador (0% CPU en reposo)
    public bool EnablePasswordManager { get; set; } = true;
    // Modos: "None" (Sin contraseña), "SmartSession" (Solo al prender PC o tras bloquear/suspender Windows),
    //        "AlwaysOnStartup" (Siempre al abrir el navegador), "VaultOnly" (Solo al abrir bóveda/ver claves)
    public string BrowserLockMode { get; set; } = "None";
    public string MasterPasswordHash { get; set; } = "";
    public string MasterPasswordSalt { get; set; } = "";
    public int MasterPasswordLength { get; set; } = 0;
    public string SecurityQuestion { get; set; } = "";
    public string SecurityAnswerHash { get; set; } = "";
    public string RecoveryCodeHash { get; set; } = "";
    public string RecoveryCodeHint { get; set; } = "";
    public long LastUnlockedBootId { get; set; } = 0;
    public bool HasCompletedInitialSecuritySetup { get; set; } = false;
}

public static class SettingsManager
{
    private static readonly object _saveLock = new();

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LexBrowser",
        "settings.json"
    );

    public static BrowserSettings Current { get; private set; } = new();

    public static event Action? SettingsChanged;

    static SettingsManager()
    {
        Load();
    }

    public static void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<BrowserSettings>(json);
                if (loaded != null)
                {
                    Current = loaded;
                    return;
                }
            }
        }
        catch { }

        Current = new BrowserSettings();
        Save();
    }

    public static void Save(bool notify = true)
    {
        try
        {
            lock (_saveLock)
            {
                string? dir = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                string json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
                string tempPath = SettingsPath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, SettingsPath, overwrite: true);
            }
            if (notify)
            {
                SettingsChanged?.Invoke();
            }
        }
        catch { }
    }

    public static string GetSearchUrl(string query)
    {
        string encoded = Uri.EscapeDataString(query);
        return Current.DefaultSearchEngine switch
        {
            "DuckDuckGo" => $"https://duckduckgo.com/?q={encoded}",
            "Bing" => $"https://www.bing.com/search?q={encoded}",
            "Brave" => $"https://search.brave.com/search?q={encoded}",
            "Custom" => string.IsNullOrWhiteSpace(Current.CustomSearchUrl)
                ? $"https://www.google.com/search?q={encoded}"
                : (Current.CustomSearchUrl.Contains("%s")
                    ? Current.CustomSearchUrl.Replace("%s", encoded)
                    : Current.CustomSearchUrl + encoded),
            _ => $"https://www.google.com/search?q={encoded}"
        };
    }
}

