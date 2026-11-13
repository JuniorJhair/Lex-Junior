using System;
using System.IO;
using System.Text.Json;
using LexBrowser.Core.AdBlock;

namespace LexBrowser.Core;

public class BrowserSettings
{
    public int SettingsSchemaVersion { get; set; } = 1;
    public bool ShowFloatingPageButton { get; set; } = true;
    public bool EnableAutoVideoPip { get; set; } = true;
    public bool ShowBookmarksBar { get; set; } = true;
    public bool EnableAdBlocker { get; set; } = true;
    public bool EnableVideoPlayerDefuser { get; set; } = false;
    public bool EnableImaSdkStub { get; set; } = true;
    public bool EnableCosmeticShield { get; set; } = true;
    public bool EnableWebShieldInSubframes { get; set; } = false;
    public bool EnableYouTubeAdBlock { get; set; } = true;
    public bool EnableDailymotionAdBlock { get; set; } = true;
    public ShieldSettings Shield { get; set; } = new();
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
    public bool EnableSearchSuggestions { get; set; } = true;
    public bool EnableExternalFaviconService { get; set; } = true;
    public bool RestorePreviousSession { get; set; } = true;
    public bool CapturePermissionsMigrationV1Done { get; set; } = false;

    // Ahorro de recursos, avisos de cookies y lector en voz alta
    public int TabSuspensionMinutes { get; set; } = 15;
    public bool SuspendPinnedTabs { get; set; } = false;
    public bool EnableCookieBannerHiding { get; set; } = true;
    public bool EnableReadAloud { get; set; } = true;
    public int ReadAloudRate { get; set; } = 0;
    public string ReadAloudVoiceName { get; set; } = "";

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
    // Límite de intentos del PIN (persistente: cerrar y abrir el navegador no lo reinicia)
    public int PinFailedAttempts { get; set; } = 0;
    public long PinLockoutUntilUtcTicks { get; set; } = 0;
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
    private static int _settingsRevision;
    private static System.Threading.Timer? _deferredSaveTimer;
    private static volatile bool _deferredSavePending;
    private static volatile bool _deferredSaveNotify;

    /// <summary>Revisión incremental de los ajustes en memoria; permite invalidar cachés por pestaña.</summary>
    public static int SettingsRevision => System.Threading.Volatile.Read(ref _settingsRevision);

    /// <summary>Incrementa la revisión de ajustes para invalidar cachés dependientes (como la política por sitio).</summary>
    public static void BumpSettingsRevision() => System.Threading.Interlocked.Increment(ref _settingsRevision);

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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

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
            MigrateAndSyncShieldSettings(Current);
        BumpSettingsRevision();
            SyncSecurityMarker();
            Save(notify: false);
            return;
        }

        // 1. Intentar cargar desde el archivo principal
        if (TryLoadFromFile(path, out var loaded))
        {
            Current = loaded!;
            bool migrated = MigrateAndSyncShieldSettings(Current);
            SyncSecurityMarker();
            if (migrated) Save(notify: false);
            return;
        }

        // 2. Si el archivo principal falló o está ausente, intentar recuperar desde el .tmp
        if (File.Exists(tempPath) && TryLoadFromFile(tempPath, out var recovered))
        {
            LogSettingsEvent($"[RECOVERY] settings.json recuperado exitosamente desde {tempPath}");
            Current = recovered!;
            MigrateAndSyncShieldSettings(Current);
        BumpSettingsRevision();
            SyncSecurityMarker();
            Save(notify: false);
            return;
        }

        Current = new BrowserSettings { SettingsSchemaVersion = 2 };
        MigrateAndSyncShieldSettings(Current);
        BumpSettingsRevision();
        SyncSecurityMarker();
        Save(notify: false);
    }

    public static bool MigrateAndSyncShieldSettings(BrowserSettings settings)
    {
        bool migrated = false;
        settings.Shield ??= new ShieldSettings();
        settings.Shield.Modules ??= new ShieldModulesConfig();
        settings.Shield.Adapters ??= new ShieldAdaptersConfig();
        settings.Shield.Sites ??= new System.Collections.Generic.Dictionary<string, SiteShieldProfile>(StringComparer.OrdinalIgnoreCase);

        if (settings.SettingsSchemaVersion < 2)
        {
            // Migración a esquema v2 (Auditoría LexShield):
            // Desactivar por defecto el desarmador genérico de reproductor y la inyección en subframes
            // para evitar reinicios de velocidad en YouTube/Dailymotion y roturas de iframes de terceros.
            settings.EnableVideoPlayerDefuser = false;
            settings.EnableWebShieldInSubframes = false;
            settings.SettingsSchemaVersion = 2;
            migrated = true;
        }

        settings.Shield.Enabled = settings.EnableAdBlocker;
        settings.Shield.Modules.Cosmetic = settings.EnableCosmeticShield;
        settings.Shield.Modules.Subframes = settings.EnableWebShieldInSubframes;
        settings.Shield.Modules.VideoDefuser = settings.EnableVideoPlayerDefuser;
        settings.Shield.Adapters.ImaStub = settings.EnableImaSdkStub;
        settings.Shield.Adapters.YouTube = settings.EnableYouTubeAdBlock;
        settings.Shield.Adapters.Dailymotion = settings.EnableDailymotionAdBlock;
        ShieldLogger.DiagnosticMode = settings.Shield.DiagnosticMode;
        return migrated;
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
                    var loaded = JsonSerializer.Deserialize<BrowserSettings>(json, JsonOptions);
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

    /// <summary>
    /// Programa un guardado diferido en segundo plano (por defecto 600 ms) para evitar escrituras
    /// síncronas repetitivas en el hilo de interfaz (ej. al redimensionar o mover ventanas PiP).
    /// 0% CPU en reposo: el temporizador es de un solo disparo y permanece inactivo el resto del tiempo.
    /// </summary>
    public static void SaveDeferred(int delayMs = 600, bool notify = false)
    {
        lock (_saveLock)
        {
            _deferredSavePending = true;
            if (notify) _deferredSaveNotify = true;

            if (_deferredSaveTimer == null)
            {
                _deferredSaveTimer = new System.Threading.Timer(_ =>
                {
                    bool shouldSave = false;
                    bool shouldNotify = false;
                    lock (_saveLock)
                    {
                        if (_deferredSavePending)
                        {
                            shouldSave = true;
                            shouldNotify = _deferredSaveNotify;
                            _deferredSavePending = false;
                            _deferredSaveNotify = false;
                        }
                    }

                    if (shouldSave)
                    {
                        Save(notify: shouldNotify);
                    }
                }, null, Math.Max(50, delayMs), System.Threading.Timeout.Infinite);
            }
            else
            {
                _deferredSaveTimer.Change(Math.Max(50, delayMs), System.Threading.Timeout.Infinite);
            }
        }
    }

    public static void FlushDeferredSave()
    {
        bool shouldSave = false;
        bool shouldNotify = false;
        lock (_saveLock)
        {
            if (_deferredSavePending)
            {
                shouldSave = true;
                shouldNotify = _deferredSaveNotify;
                _deferredSavePending = false;
                _deferredSaveNotify = false;
                _deferredSaveTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            }
        }

        if (shouldSave)
        {
            Save(notify: shouldNotify);
        }
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
                _deferredSavePending = false;
                _deferredSaveNotify = false;
                _deferredSaveTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

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

                Current.SettingsSchemaVersion = Math.Max(2, Current.SettingsSchemaVersion);
                MigrateAndSyncShieldSettings(Current);
        BumpSettingsRevision();

                string json = JsonSerializer.Serialize(Current, JsonOptions);

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

