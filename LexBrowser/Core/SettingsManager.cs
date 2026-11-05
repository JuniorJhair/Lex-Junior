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
    public bool RestorePreviousSession { get; set; } = true;

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

    private static readonly string DefaultSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LexBrowser",
        "settings.json"
    );

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LexBrowser",
        "settings.log"
    );

    public static string? CustomSettingsPath { get; set; }
    public static string SettingsPath => CustomSettingsPath ?? DefaultSettingsPath;

    public static BrowserSettings Current { get; private set; } = new();

    public static event Action? SettingsChanged;

    static SettingsManager()
    {
        Load();
    }

    public static void Load()
    {
        string path = SettingsPath;
        string tempPath = path + ".tmp";

        // 1. Intentar cargar desde el archivo principal
        if (TryLoadFromFile(path, out var loaded))
        {
            Current = loaded!;
            return;
        }

        // 2. Si el archivo principal falló o está ausente, intentar recuperar desde el .tmp
        if (File.Exists(tempPath) && TryLoadFromFile(tempPath, out var recovered))
        {
            LogSettingsEvent($"[RECOVERY] settings.json recuperado exitosamente desde {tempPath}");
            Current = recovered!;
            Save(notify: false);
            return;
        }

        Current = new BrowserSettings();
        Save(notify: false);
    }

    private static bool TryLoadFromFile(string filePath, out BrowserSettings? result)
    {
        result = null;
        try
        {
            if (File.Exists(filePath))
            {
                string json = File.ReadAllText(filePath);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var loaded = JsonSerializer.Deserialize<BrowserSettings>(json);
                    if (loaded != null)
                    {
                        result = loaded;
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogSettingsEvent($"Error al leer configuración desde {filePath}: {ex.Message}");
        }
        return false;
    }

    public static void Save(bool notify = true)
    {
        string path = SettingsPath;
        string tempPath = path + ".tmp";
        string? dir = Path.GetDirectoryName(path);

        try
        {
            lock (_saveLock)
            {
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(tempPath, json);

                bool replaced = false;
                Exception? lastEx = null;

                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            File.Replace(tempPath, path, null);
                        }
                        else
                        {
                            File.Move(tempPath, path);
                        }
                        replaced = true;
                        break;
                    }
                    catch (IOException ex)
                    {
                        lastEx = ex;
                        Thread.Sleep(25 * (attempt + 1));
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        lastEx = ex;
                        Thread.Sleep(25 * (attempt + 1));
                    }
                }

                if (!replaced)
                {
                    LogSettingsEvent($"[SAVE_FAILED] No se pudo reemplazar {path} tras 5 intentos. El estado seguro queda en {tempPath}. Detalle: {lastEx?.Message}");
                }
            }

            if (notify)
            {
                SettingsChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            LogSettingsEvent($"[CRITICAL] Error inesperado en Save(): {ex}");
        }
    }

    private static void LogSettingsEvent(string message)
    {
        try
        {
            string line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
            string? dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(LogPath, line);
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

