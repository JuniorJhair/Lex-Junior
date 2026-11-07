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
    public bool EnableVideoPlayerDefuser { get; set; } = true;
    public bool EnableImaSdkStub { get; set; } = true;
    public bool EnableCosmeticShield { get; set; } = true;
    public bool EnableWebShieldInSubframes { get; set; } = true;
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
    public bool UserDecidedDefaultBrowser { get; set; } = false;
    public bool WasPreviouslyDefaultBrowser { get; set; } = false;

    // Sistema de Notificaciones Web (0% CPU en reposo)
    // Valores: "Ask" (Preguntar al usuario), "BlockAll" (Bloquear todas en silencio - comportamiento previo), "SilentAll" (Permitir solo en centro)
    public string NotificationGlobalPolicy { get; set; } = "Ask";
    public string NotificationDndShortcut { get; set; } = "Ctrl+Shift+D";
    public bool NotificationDndActive { get; set; } = false;
    public int NotificationGlobalDailyLimit { get; set; } = 0; // 0 = Ilimitado; 10, 20, 30
    public int NotificationDefaultSiteDailyLimit { get; set; } = 0; // 0 = Ilimitado; 1, 2, 3, 5
    public bool NotificationDiscardOlderThan24Hours { get; set; } = true;
    public bool NotificationDndOnFullscreen { get; set; } = true;
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

    public static string SecuritySetupMarkerPath => Path.Combine(
        Path.GetDirectoryName(SettingsPath) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LexBrowser"),
        ".security_setup_done"
    );

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

        // Si el archivo temporal es más reciente que el principal (por fallo en guardado previo), priorizarlo
        bool tempIsNewer = false;
        try
        {
            if (File.Exists(tempPath) && File.Exists(path))
            {
                tempIsNewer = File.GetLastWriteTimeUtc(tempPath) > File.GetLastWriteTimeUtc(path);
            }
        }
        catch { }

        if (tempIsNewer && TryLoadFromFile(tempPath, out var fromTemp))
        {
            Current = fromTemp!;
            SyncSecurityMarker();
            Save(notify: false);
            return;
        }

        // 1. Intentar cargar desde el archivo principal
        if (TryLoadFromFile(path, out var loaded))
        {
            Current = loaded!;
            SyncSecurityMarker();
            return;
        }

        // 2. Si el archivo principal falló o está ausente, intentar recuperar desde el .tmp
        if (File.Exists(tempPath) && TryLoadFromFile(tempPath, out var recovered))
        {
            LogSettingsEvent($"[RECOVERY] settings.json recuperado exitosamente desde {tempPath}");
            Current = recovered!;
            SyncSecurityMarker();
            Save(notify: false);
            return;
        }

        Current = new BrowserSettings();
        SyncSecurityMarker();
        Save(notify: false);
    }

    private static void SyncSecurityMarker()
    {
        try
        {
            if (File.Exists(SecuritySetupMarkerPath))
            {
                Current.HasCompletedInitialSecuritySetup = true;
            }
            else if (Current.HasCompletedInitialSecuritySetup)
            {
                string? dir = Path.GetDirectoryName(SecuritySetupMarkerPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(SecuritySetupMarkerPath, "1");
            }
        }
        catch { }
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

                // Sincronizar archivo centinela de seguridad para respetar siempre la decisión del usuario
                if (Current.HasCompletedInitialSecuritySetup)
                {
                    try
                    {
                        File.WriteAllText(SecuritySetupMarkerPath, "1");
                    }
                    catch { }
                }

                string json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });

                // Escritura resistente a bloqueos en Windows: escribir primero en .tmp y copiar sobrescribiendo
                bool saved = false;
                Exception? lastEx = null;

                try
                {
                    File.WriteAllText(tempPath, json);
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                }

                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        if (File.Exists(tempPath))
                        {
                            File.Copy(tempPath, path, overwrite: true);
                            saved = true;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        lastEx = ex;
                        Thread.Sleep(25 * (attempt + 1));
                    }
                }

                if (!saved)
                {
                    // Fallback directo por si File.Copy tuviera alguna restricción específica de permisos
                    try
                    {
                        File.WriteAllText(path, json);
                        saved = true;
                    }
                    catch (Exception ex)
                    {
                        lastEx = ex;
                    }
                }

                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch { }

                if (!saved)
                {
                    LogSettingsEvent($"[SAVE_FAILED] No se pudo guardar {path}: {lastEx?.Message}");
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

