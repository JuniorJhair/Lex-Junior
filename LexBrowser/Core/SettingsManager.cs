using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using LexBrowser.Core.AdBlock;

namespace LexBrowser.Core;

public class BrowserSettings
{
    public int SettingsSchemaVersion { get; set; } = 1;
    public string Theme { get; set; } = "Classic"; // "Classic", "Dark", "Light"
    public bool ShowFloatingPageButton { get; set; } = true;
    public bool EnableAutoVideoPip { get; set; } = true;
    public bool ShowBookmarksBar { get; set; } = true;
    public bool EnableAdBlocker { get; set; } = true;
    public bool EnableVideoPlayerDefuser { get; set; } = false;
    public bool EnableImaSdkStub { get; set; } = true;
    public bool EnableCosmeticShield { get; set; } = true;
    public bool EnableWebShieldInSubframes { get; set; } = true;
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
    public bool AskWhereToSaveDownloads { get; set; } = true;
    public string DefaultDownloadDirectory { get; set; } = "";
    public bool EnableExternalFaviconService { get; set; } = true;
    public bool EnableSmartScreen { get; set; } = false;
    public bool EnableClientSidePhishingDetection { get; set; } = false;
    public bool EnableLinkFaviconDownload { get; set; } = true;
    public bool EnableDirectFaviconIco { get; set; } = true;
    public string AutoUpdateCheckMode { get; set; } = "OnStartup"; // "Manual" | "OnStartup"
    public int PrivacyProfileOnboardingVersion { get; set; } = 0;
    public bool FreshInstallOnboardingPending { get; set; } = false;
    public string LastAppVersion { get; set; } = string.Empty;
    public bool RestorePreviousSession { get; set; } = true;
    public bool CapturePermissionsMigrationV1Done { get; set; } = false;

    // Ahorro de recursos, avisos de cookies y lector en voz alta
    public int TabSuspensionMinutes { get; set; } = 15;
    public bool SuspendPinnedTabs { get; set; } = false;
    public bool EnableCookieBannerHiding { get; set; } = true;
    public bool EnableReadAloud { get; set; } = true;
    public int ReadAloudRate { get; set; } = 0;
    public string ReadAloudVoiceName { get; set; } = "";

    // Guardador de Contraseñas y Bloqueo Inteligente del Navegador
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

    // Sistema de Notificaciones Web (reactivo a eventos)
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
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private static string? GetEnvTempSettingsPath()
    {
        try
        {
            string? raw = Environment.GetEnvironmentVariable("LEX_TEST_SETTINGS_PATH");
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            string tempRoot = Path.GetFullPath(Path.GetTempPath())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string fullCandidate = Path.GetFullPath(raw);
            if (fullCandidate.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
                return fullCandidate;
        }
        catch { }
        return null;
    }

    public static string? CustomSettingsPath { get; set; } = GetEnvTempSettingsPath();
    public static string SettingsPath => CustomSettingsPath ?? DefaultSettingsPath;

    public static string SecuritySetupMarkerPath => Path.Combine(
        Path.GetDirectoryName(SettingsPath) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LexBrowser"),
        ".security_setup_done"
    );

    public static string VaultLockRecoveryMarkerPath => Path.Combine(
        Path.GetDirectoryName(SettingsPath) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LexBrowser"),
        ".vault_lock_recovery_pending"
    );

    public static string LastAppVersionMarkerPath => Path.Combine(
        Path.GetDirectoryName(SettingsPath) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LexBrowser"),
        ".last_app_version"
    );

    private static Version _activeLoadedAppVersion = GetCurrentAppVersion();
    public static Version CurrentAppVersion => _activeLoadedAppVersion;

    public static bool HasPendingVaultLockRecoveryMarker()
    {
        try
        {
            return File.Exists(VaultLockRecoveryMarkerPath);
        }
        catch
        {
            return true;
        }
    }

    public static bool IsVaultRecoveryLockActive =>
        LockSettingsPotentiallyLost || HasPendingVaultLockRecoveryMarker();

    public static BrowserSettings Current { get; private set; } = new();

    /// <summary>
    /// Indica si en el Load() más reciente NO existía ninguna señal previa de usuario
    /// (settings.json, settings.json.tmp, .security_setup_done, bookmarks.json, vault.dat, history.json, session_tabs.json ni UserData\ con contenido).
    /// </summary>
    public static bool IsFreshInstall { get; private set; }

    /// <summary>Indica que settings.json existía pero falló al deserializarse de forma estándar.</summary>
    public static bool HadUnreadableSettingsFile { get; private set; }

    /// <summary>Bloquea todo guardado automático (SaveDeferred, OnClosed, bounds, Load) mientras settings.json no esté resuelto.</summary>
    public static bool AutoSavesSuspended { get; private set; }

    /// <summary>Ruta de la copia de respaldo .corrupt-AAAAMMDD-HHmmss creada antes de cualquier escritura.</summary>
    public static string? CorruptSettingsBackupPath { get; private set; }

    /// <summary>Indica que falló la recuperación del bloqueo por PIN habiendo vault.dat o .security_setup_done.</summary>
    public static bool LockSettingsPotentiallyLost { get; private set; }

    /// <summary>Mensaje informativo para el usuario si settings.json fue ilegible o requirió recuperación parcial.</summary>
    public static string? SettingsRecoveryWarningMessage { get; private set; }

    /// <summary>Indica que el perfil en disco pertenece a una versión más reciente que la actualmente ejecutada.</summary>
    public static bool DowngradeDetected { get; private set; }

    /// <summary>Versión más reciente registrada en el perfil cuando se detecta una ejecución en versión anterior.</summary>
    public static string? DetectedNewerProfileVersion { get; private set; }

    /// <summary>Mensaje obligatorio mostrado cuando el usuario abre una versión anterior sobre un perfil más reciente.</summary>
    public static string? DowngradeWarningMessage { get; private set; }

    public static event Action? SettingsChanged;

    static SettingsManager()
    {
        Load();
    }

    internal static bool HasPriorUserDataSignals(string settingsPath, string tempPath)
    {
        try
        {
            if (File.Exists(settingsPath) || File.Exists(tempPath))
                return true;

            string? dir = Path.GetDirectoryName(settingsPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return false;

            if (File.Exists(Path.Combine(dir, ".security_setup_done")) ||
                File.Exists(Path.Combine(dir, ".vault_lock_recovery_pending")) ||
                File.Exists(Path.Combine(dir, ".last_app_version")) ||
                File.Exists(Path.Combine(dir, "bookmarks.json")) ||
                File.Exists(Path.Combine(dir, "vault.dat")) ||
                File.Exists(Path.Combine(dir, "history.json")) ||
                File.Exists(Path.Combine(dir, "history.json.tmp")) ||
                File.Exists(Path.Combine(dir, "session_tabs.json")))
            {
                return true;
            }

            string userDataDir = Path.Combine(dir, "UserData");
            if (Directory.Exists(userDataDir))
            {
                using var entries = Directory.EnumerateFileSystemEntries(userDataDir).GetEnumerator();
                if (entries.MoveNext())
                    return true;
            }
        }
        catch
        {
            // Ante duda de E/S al verificar señales previas, no asumir instalación nueva.
            return true;
        }

        return false;
    }

    private static Version GetCurrentAppVersion()
    {
        var v = typeof(SettingsManager).Assembly.GetName().Version;
        return v != null ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(1, 0, 0);
    }

    internal static Version? TryParseNormalizedVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string cleaned = raw.Trim().TrimStart('v', 'V');
        int dash = cleaned.IndexOf('-');
        if (dash > 0) cleaned = cleaned[..dash];
        if (!Version.TryParse(cleaned, out var parsed)) return null;
        return new Version(parsed.Major, Math.Max(0, parsed.Minor), Math.Max(0, parsed.Build));
    }

    private static Version NormalizeVersion(Version v) =>
        new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    private static Version? MaxVersion(Version? a, Version? b)
    {
        if (a == null) return b;
        if (b == null) return a;
        return a >= b ? a : b;
    }

    private static Version? ReadLastAppVersionMarker()
    {
        try
        {
            string markerPath = LastAppVersionMarkerPath;
            if (!File.Exists(markerPath)) return null;
            return TryParseNormalizedVersion(File.ReadAllText(markerPath));
        }
        catch
        {
            return null;
        }
    }

    private static void WriteLastAppVersionMarker(Version version)
    {
        if (DowngradeDetected || AutoSavesSuspended) return;
        try
        {
            string markerPath = LastAppVersionMarkerPath;
            string? dir = Path.GetDirectoryName(markerPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(markerPath, NormalizeVersion(version).ToString(3));
        }
        catch { }
    }

    private static void ActivateDowngradeProtection(Version recordedNewerVersion, Version runningVersion)
    {
        DowngradeDetected = true;
        AutoSavesSuspended = true;
        DetectedNewerProfileVersion = NormalizeVersion(recordedNewerVersion).ToString(3);
        DowngradeWarningMessage =
            $"Este perfil se usó anteriormente con una versión más reciente de Lex Junior (v{DetectedNewerProfileVersion}) y estás abriendo una versión anterior (v{NormalizeVersion(runningVersion).ToString(3)}).\n\n" +
            "Para evitar pérdida de ajustes o incompatibilidades, debes actualizar a una versión igual o superior antes de continuar.\n" +
            "Tu archivo settings.json permanecerá intacto sin sobrescribirse.";
        LogSettingsEvent($"[DOWNGRADE_DETECTED] {DowngradeWarningMessage}");
    }

    private static bool FinalizeLoadedSettings(BrowserSettings settings)
    {
        bool migrated = MigrateAndSyncShieldSettings(settings);
        if (settings.PrivacyProfileOnboardingVersion >= PrivacyProfiles.CurrentOnboardingVersion &&
            settings.FreshInstallOnboardingPending)
        {
            settings.FreshInstallOnboardingPending = false;
            migrated = true;
        }

        IsFreshInstall = settings.FreshInstallOnboardingPending &&
                         settings.PrivacyProfileOnboardingVersion < PrivacyProfiles.CurrentOnboardingVersion;
        return migrated;
    }

    public static void Load() => Load(appVersionOverride: null);

    internal static void Load(Version? appVersionOverride)
    {
        string path = SettingsPath;
        string tempPath = path + ".tmp";

        Version runningVersion = NormalizeVersion(appVersionOverride ?? GetCurrentAppVersion());
        _activeLoadedAppVersion = runningVersion;

        HadUnreadableSettingsFile = false;
        AutoSavesSuspended = false;
        CorruptSettingsBackupPath = null;
        LockSettingsPotentiallyLost = false;
        SettingsRecoveryWarningMessage = null;
        DowngradeDetected = false;
        DetectedNewerProfileVersion = null;
        DowngradeWarningMessage = null;

        Version? markerVersion = ReadLastAppVersionMarker();
        bool freshNoPriorSignals = !HasPriorUserDataSignals(path, tempPath);
        IsFreshInstall = freshNoPriorSignals;

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

        if (tempIsNewer && File.Exists(path) && TryLoadFromFile(path, out _) && TryLoadFromFile(tempPath, out var fromTemp))
        {
            Current = fromTemp!;
            Version? recorded = MaxVersion(markerVersion, TryParseNormalizedVersion(Current.LastAppVersion));
            if (recorded != null && recorded > runningVersion)
            {
                ActivateDowngradeProtection(recorded, runningVersion);
                return;
            }

            Current.LastAppVersion = MaxVersion(recorded, runningVersion)!.ToString(3);
            FinalizeLoadedSettings(Current);
            BumpSettingsRevision();
            SyncSecurityMarker();
            Save(notify: false);
            WriteLastAppVersionMarker(runningVersion);
            return;
        }

        // 1. Intentar cargar desde el archivo principal de forma estricta
        if (TryLoadFromFile(path, out var loaded))
        {
            if (tempIsNewer && TryLoadFromFile(tempPath, out var newerTemp))
            {
                Current = newerTemp!;
                Version? recordedTemp = MaxVersion(markerVersion, TryParseNormalizedVersion(Current.LastAppVersion));
                if (recordedTemp != null && recordedTemp > runningVersion)
                {
                    ActivateDowngradeProtection(recordedTemp, runningVersion);
                    return;
                }

                Current.LastAppVersion = MaxVersion(recordedTemp, runningVersion)!.ToString(3);
                FinalizeLoadedSettings(Current);
                BumpSettingsRevision();
                SyncSecurityMarker();
                Save(notify: false);
                WriteLastAppVersionMarker(runningVersion);
                return;
            }

            Current = loaded!;
            Version? recordedPrimary = MaxVersion(markerVersion, TryParseNormalizedVersion(Current.LastAppVersion));
            if (recordedPrimary != null && recordedPrimary > runningVersion)
            {
                ActivateDowngradeProtection(recordedPrimary, runningVersion);
                return;
            }

            Current.LastAppVersion = MaxVersion(recordedPrimary, runningVersion)!.ToString(3);
            bool migrated = FinalizeLoadedSettings(Current);
            SyncSecurityMarker();
            if (migrated) Save(notify: false);
            if (markerVersion == null || markerVersion < runningVersion)
            {
                WriteLastAppVersionMarker(runningVersion);
            }
            return;
        }

        // 2. Si settings.json EXISTÍA pero no se pudo leer (corrupto, vacío, truncado o bloqueado):
        //    JAMÁS tratar como instalación nueva ni sobrescribir settings.json en ninguna rama.
        bool mainFileExists = false;
        try { mainFileExists = File.Exists(path); } catch { }

        if (mainFileExists)
        {
            HandleUnreadablePrimarySettings(path, tempPath);
            Version? recordedCorrupt = MaxVersion(markerVersion, TryParseNormalizedVersion(Current.LastAppVersion));
            if (recordedCorrupt != null && recordedCorrupt > runningVersion)
            {
                ActivateDowngradeProtection(recordedCorrupt, runningVersion);
            }
            return;
        }

        // 3. Si el archivo principal no existía, intentar recuperar desde .tmp
        if (File.Exists(tempPath) && TryLoadFromFile(tempPath, out var recovered))
        {
            LogSettingsEvent($"[RECOVERY] settings.json recuperado exitosamente desde {tempPath}");
            Current = recovered!;
            Version? recordedRec = MaxVersion(markerVersion, TryParseNormalizedVersion(Current.LastAppVersion));
            if (recordedRec != null && recordedRec > runningVersion)
            {
                ActivateDowngradeProtection(recordedRec, runningVersion);
                return;
            }

            Current.LastAppVersion = MaxVersion(recordedRec, runningVersion)!.ToString(3);
            FinalizeLoadedSettings(Current);
            BumpSettingsRevision();
            SyncSecurityMarker();
            Save(notify: false);
            WriteLastAppVersionMarker(runningVersion);
            return;
        }

        // Si settings.json fue eliminado pero .last_app_version apunta a una versión más reciente, bloquear antes de recrearlo
        if (markerVersion != null && markerVersion > runningVersion)
        {
            Current = new BrowserSettings
            {
                SettingsSchemaVersion = ShieldSettings.CurrentShieldVersion,
                LastAppVersion = markerVersion.ToString(3)
            };
            ActivateDowngradeProtection(markerVersion, runningVersion);
            return;
        }

        // 4. Sin archivo principal ni .tmp: inicializar en memoria/disco con valores v2.6.0 (sin aplicar Perfil B)
        // y registrar FreshInstallOnboardingPending solo cuando NO existía ninguna señal previa de usuario.
        Current = new BrowserSettings
        {
            SettingsSchemaVersion = ShieldSettings.CurrentShieldVersion,
            FreshInstallOnboardingPending = freshNoPriorSignals,
            LastAppVersion = runningVersion.ToString(3)
        };
        Current.Shield.Version = ShieldSettings.CurrentShieldVersion;
        Current.Shield.Engine = ShieldSettings.EngineLexShield;
        MigrateAndSyncShieldSettings(Current);
        BumpSettingsRevision();
        SyncSecurityMarker();
        IsFreshInstall = freshNoPriorSignals;
        Save(notify: false);
        WriteLastAppVersionMarker(runningVersion);
    }

    private static void HandleUnreadablePrimarySettings(string path, string tempPath)
    {
        IsFreshInstall = false;
        HadUnreadableSettingsFile = true;
        AutoSavesSuspended = true;

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        bool backupCreated = false;
        byte[]? rawBytes = null;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            rawBytes = ms.ToArray();

            string? existingBackup = TryFindIdenticalCorruptBackup(path, rawBytes);
            if (!string.IsNullOrEmpty(existingBackup))
            {
                CorruptSettingsBackupPath = existingBackup;
            }
            else
            {
                string backupPath = BuildUniqueCorruptBackupPath(path, stamp);
                File.WriteAllBytes(backupPath, rawBytes);
                CorruptSettingsBackupPath = backupPath;
            }
            backupCreated = true;

            if (File.Exists(tempPath))
            {
                try
                {
                    byte[] tmpBytes = File.ReadAllBytes(tempPath);
                    if (string.IsNullOrEmpty(TryFindIdenticalCorruptBackup(tempPath, tmpBytes)))
                    {
                        string tmpBackup = BuildUniqueCorruptBackupPath(tempPath, stamp);
                        File.Copy(tempPath, tmpBackup, overwrite: false);
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            LogSettingsEvent($"[CORRUPT_BACKUP_FAILED] No se pudo leer/respaldar {path}: {ex.Message}");
        }

        // Intentar recuperación tolerante propiedad por propiedad desde el .tmp válido o desde los bytes del corrupto
        BrowserSettings? recoveredSettings = null;
        bool recoveredAnyProperty = false;
        bool recoveredExplicitNoneLockMode = false;

        if (File.Exists(tempPath) && TryLoadFromFile(tempPath, out var tmpLoaded) && tmpLoaded != null)
        {
            recoveredSettings = tmpLoaded;
            recoveredAnyProperty = true;
            recoveredExplicitNoneLockMode = string.Equals(tmpLoaded.BrowserLockMode, "None", StringComparison.OrdinalIgnoreCase);
        }
        else if (rawBytes != null && rawBytes.Length > 0)
        {
            recoveredSettings = TryRecoverPartialSettingsFromBytes(rawBytes, out int rescuedCount, out recoveredExplicitNoneLockMode);
            recoveredAnyProperty = rescuedCount > 0;
        }

        Current = recoveredSettings ?? new BrowserSettings
        {
            SettingsSchemaVersion = ShieldSettings.CurrentShieldVersion
        };
        Current.Shield ??= new ShieldSettings();
        MigrateAndSyncShieldSettings(Current);
        BumpSettingsRevision();

        // Verificar si hay riesgo de haber perdido bloqueo por PIN mientras existe bóveda o marca de seguridad
        string? dir = Path.GetDirectoryName(path);
        bool vaultExistsWithContent = false;
        bool securityMarkerExists = false;
        if (!string.IsNullOrEmpty(dir))
        {
            try
            {
                string vaultPath = Path.Combine(dir, "vault.dat");
                vaultExistsWithContent = File.Exists(vaultPath) && new FileInfo(vaultPath).Length > 0;
            }
            catch { }
            try
            {
                securityMarkerExists = File.Exists(Path.Combine(dir, ".security_setup_done"));
            }
            catch { }
        }

        bool hasConfiguredPinAfterRecovery =
            !string.IsNullOrWhiteSpace(Current.MasterPasswordHash) &&
            !string.IsNullOrWhiteSpace(Current.MasterPasswordSalt);

        // Solo un modo "None" explícitamente rescatado prueba ausencia intencional de PIN.
        // Cualquier otro modo (o corte antes de recuperarlo) sin hash+sal completos debe activar el marcador persistente.
        if ((vaultExistsWithContent || securityMarkerExists) &&
            !hasConfiguredPinAfterRecovery &&
            !recoveredExplicitNoneLockMode)
        {
            LockSettingsPotentiallyLost = true;
            WriteVaultLockRecoveryMarker();
        }

        var sb = new StringBuilder();
        sb.AppendLine("⚠️ No se pudo leer normalmente tu archivo settings.json (dañado, truncado, vacío o bloqueado).");
        if (backupCreated && !string.IsNullOrEmpty(CorruptSettingsBackupPath))
        {
            sb.AppendLine($"• Copia original conservada en: {CorruptSettingsBackupPath}");
        }
        else
        {
            sb.AppendLine("• El archivo original no pudo copiarse por bloqueo en disco y se dejó intacto (sin sobrescribir).");
        }

        if (recoveredAnyProperty)
        {
            sb.AppendLine("• Se recuperaron parcialmente los ajustes válidos (incluidos los campos de PIN/seguridad si estaban intactos); lo no recuperado usa valores seguros por defecto.");
        }
        else
        {
            sb.AppendLine("• No se pudieron recuperar propiedades válidas; se cargaron los valores seguros actuales en memoria sin sobrescribir el archivo.");
        }

        if (LockSettingsPotentiallyLost)
        {
            sb.AppendLine("• PROTECCIÓN ACTIVA: No se pudo verificar tu configuración de bloqueo por PIN habiendo una bóveda o configuración de seguridad previa. El acceso a contraseñas guardadas y autocompletado permanecerá restringido hasta que revises la bóveda o restablezcas el acceso.");
        }

        SettingsRecoveryWarningMessage = sb.ToString().Trim();
        LogSettingsEvent($"[UNREADABLE_SETTINGS] {SettingsRecoveryWarningMessage}");
    }

    private static string BuildUniqueCorruptBackupPath(string originalPath, string stamp)
    {
        string baseBackup = $"{originalPath}.corrupt-{stamp}";
        if (!File.Exists(baseBackup))
            return baseBackup;

        for (int i = 1; i < 100; i++)
        {
            string candidate = $"{baseBackup}_{i}";
            if (!File.Exists(candidate))
                return candidate;
        }
        return $"{baseBackup}_{Guid.NewGuid():N}";
    }

    private static string? TryFindIdenticalCorruptBackup(string originalPath, byte[] currentBytes)
    {
        try
        {
            string? dir = Path.GetDirectoryName(originalPath);
            string baseName = Path.GetFileName(originalPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir) || string.IsNullOrEmpty(baseName))
                return null;

            string prefix = baseName + ".corrupt-";
            foreach (string candidate in Directory.EnumerateFiles(dir, prefix + "*", SearchOption.TopDirectoryOnly)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var info = new FileInfo(candidate);
                    if (!info.Exists || info.Length != currentBytes.LongLength)
                        continue;

                    byte[] existingBytes = File.ReadAllBytes(candidate);
                    if (existingBytes.AsSpan().SequenceEqual(currentBytes))
                        return candidate;
                }
                catch { }
            }
        }
        catch { }

        return null;
    }

    private static void WriteVaultLockRecoveryMarker()
    {
        try
        {
            string markerPath = VaultLockRecoveryMarkerPath;
            string? dir = Path.GetDirectoryName(markerPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(markerPath, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            LogSettingsEvent($"[VAULT_MARKER_ERROR] No se pudo escribir {VaultLockRecoveryMarkerPath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Permite desbloquear los guardados solo cuando ya existe respaldo o el usuario resolvió el estado en UI.
    /// Nunca sobrescribe si settings.json no tiene copia previa cuando estaba corrupto.
    /// </summary>
    public static bool ResolveRecoveryAndAllowSaves()
    {
        if (DowngradeDetected)
            return false;

        if (HadUnreadableSettingsFile &&
            File.Exists(SettingsPath) &&
            string.IsNullOrEmpty(CorruptSettingsBackupPath))
        {
            return false;
        }

        AutoSavesSuspended = false;
        return true;
    }

    internal static void ClearRecoveryLockRestriction()
    {
        LockSettingsPotentiallyLost = false;
        try
        {
            string markerPath = VaultLockRecoveryMarkerPath;
            if (File.Exists(markerPath))
            {
                File.Delete(markerPath);
            }
        }
        catch (Exception ex)
        {
            LogSettingsEvent($"[VAULT_MARKER_CLEAR_ERROR] No se pudo eliminar {VaultLockRecoveryMarkerPath}: {ex.Message}");
        }
    }

    internal static BrowserSettings TryRecoverPartialSettingsFromBytes(
        byte[] rawBytes,
        out int rescuedPropertiesCount,
        out bool rescuedExplicitNoneLockMode)
    {
        var result = new BrowserSettings { SettingsSchemaVersion = ShieldSettings.CurrentShieldVersion };
        rescuedPropertiesCount = 0;
        rescuedExplicitNoneLockMode = false;

        try
        {
            string text = Encoding.UTF8.GetString(rawBytes)
                .TrimStart('\uFEFF')
                .Replace("\0", string.Empty)
                .Trim();

            if (string.IsNullOrWhiteSpace(text))
                return result;

            int braceStart = text.IndexOf('{');
            if (braceStart > 0) text = text[braceStart..];
            if (!text.StartsWith("{", StringComparison.Ordinal))
                return result;

            byte[] cleanUtf8 = Encoding.UTF8.GetBytes(text);
            var readerOptions = new JsonReaderOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            };

            var reader = new Utf8JsonReader(cleanUtf8, isFinalBlock: true, new JsonReaderState(readerOptions));

            try
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                    return result;

                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject)
                        break;

                    if (reader.TokenType != JsonTokenType.PropertyName)
                        continue;

                    string? propName = reader.GetString();
                    if (string.IsNullOrWhiteSpace(propName))
                        continue;

                    if (!reader.Read())
                        break;

                    if (!JsonDocument.TryParseValue(ref reader, out JsonDocument? valueDoc) || valueDoc == null)
                        break;

                    using (valueDoc)
                    {
                        if (ApplyRecoveredProperty(result, propName, valueDoc.RootElement, ref rescuedExplicitNoneLockMode))
                        {
                            rescuedPropertiesCount++;
                        }
                    }
                }
            }
            catch
            {
                // Tolerante ante truncamiento repentino: conservamos todo lo rescatado hasta el corte.
            }
        }
        catch { }

        return result;
    }

    private static readonly Dictionary<string, System.Reflection.PropertyInfo> RecoverableProperties =
        typeof(BrowserSettings)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
            .ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);

    private static bool ApplyRecoveredProperty(
        BrowserSettings target,
        string propName,
        JsonElement el,
        ref bool rescuedExplicitNoneLockMode)
    {
        if (!RecoverableProperties.TryGetValue(propName, out var prop))
            return false;

        if (el.ValueKind == JsonValueKind.Null && prop.PropertyType == typeof(string))
        {
            prop.SetValue(target, string.Empty);
            return true;
        }

        object? deserialized;
        try
        {
            deserialized = JsonSerializer.Deserialize(el.GetRawText(), prop.PropertyType, JsonOptions);
        }
        catch
        {
            return false;
        }

        if (deserialized == null)
            return false;

        if (deserialized is double dbl)
        {
            if (double.IsInfinity(dbl))
                return false;

            if ((prop.Name is nameof(BrowserSettings.FloatingPipWidth) or
                 nameof(BrowserSettings.FloatingPipHeight) or
                 nameof(BrowserSettings.MainWindowWidth) or
                 nameof(BrowserSettings.MainWindowHeight)) &&
                (double.IsNaN(dbl) || dbl <= 0))
            {
                return false;
            }
        }

        if (prop.Name.Equals(nameof(BrowserSettings.TabSuspensionMinutes), StringComparison.Ordinal) &&
            deserialized is int suspMins && suspMins < 0)
        {
            return false;
        }

        if (prop.Name.Equals(nameof(BrowserSettings.Theme), StringComparison.Ordinal) &&
            deserialized is string rawTheme)
        {
            deserialized = BrowserThemeManager.NormalizeTheme(rawTheme);
        }
        else if (prop.Name.Equals(nameof(BrowserSettings.AutoUpdateCheckMode), StringComparison.Ordinal) &&
                 deserialized is string rawUpdateMode)
        {
            deserialized = PrivacyProfiles.NormalizeAutoUpdateCheckMode(rawUpdateMode);
        }
        else if (prop.Name.Equals(nameof(BrowserSettings.BrowserLockMode), StringComparison.Ordinal) &&
                 deserialized is string lockMode)
        {
            rescuedExplicitNoneLockMode = string.Equals(lockMode.Trim(), "None", StringComparison.OrdinalIgnoreCase);
            if (rescuedExplicitNoneLockMode)
                deserialized = "None";
        }

        prop.SetValue(target, deserialized);
        return true;
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
            // Desactivar por defecto el desarmador genérico de reproductor.
            settings.EnableVideoPlayerDefuser = false;
            settings.SettingsSchemaVersion = 2;
            migrated = true;
        }

        string rawEngine = (settings.Shield.Engine ?? string.Empty).Trim();
        bool wasLegacyAlias = string.Equals(rawEngine, ShieldSettings.LegacyEngineLegacyAlias, StringComparison.OrdinalIgnoreCase);
        bool wasBraveAlias = string.Equals(rawEngine, ShieldSettings.LegacyEngineBraveAlias, StringComparison.OrdinalIgnoreCase);
        bool needsShieldV4Migration =
            settings.SettingsSchemaVersion < ShieldSettings.CurrentShieldVersion ||
            settings.Shield.Version < ShieldSettings.CurrentShieldVersion ||
            wasLegacyAlias ||
            wasBraveAlias ||
            !string.Equals(rawEngine, ShieldSettings.EngineLexShield, StringComparison.Ordinal);

        if (needsShieldV4Migration)
        {
            if (settings.SettingsSchemaVersion < 3 && wasLegacyAlias)
            {
                settings.EnableWebShieldInSubframes = true;
            }

            settings.Shield.Engine = ShieldSettings.EngineLexShield;
            settings.Shield.Version = Math.Max(ShieldSettings.CurrentShieldVersion, settings.Shield.Version);
            settings.SettingsSchemaVersion = Math.Max(ShieldSettings.CurrentShieldVersion, settings.SettingsSchemaVersion);
            migrated = true;
        }

        string normalizedTheme = BrowserThemeManager.NormalizeTheme(settings.Theme);
        if (!string.Equals(settings.Theme, normalizedTheme, StringComparison.Ordinal))
        {
            settings.Theme = normalizedTheme;
            migrated = true;
        }

        string normalizedUpdateMode = PrivacyProfiles.NormalizeAutoUpdateCheckMode(settings.AutoUpdateCheckMode);
        if (!string.Equals(settings.AutoUpdateCheckMode, normalizedUpdateMode, StringComparison.Ordinal))
        {
            settings.AutoUpdateCheckMode = normalizedUpdateMode;
            migrated = true;
        }

        settings.Shield.Enabled = settings.EnableAdBlocker;
        settings.Shield.Engine = ShieldSettings.NormalizeEngine(settings.Shield.Engine);
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
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                string json = reader.ReadToEnd();
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
    /// El temporizador es de un solo disparo y permanece inactivo el resto del tiempo.
    /// </summary>
    public static void SaveDeferred(int delayMs = 600, bool notify = false)
    {
        if (AutoSavesSuspended) return;
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
        if (AutoSavesSuspended)
        {
            LogSettingsEvent("[SAVE_BLOCKED] Guardado bloqueado porque settings.json se encuentra en estado no recuperado.");
            return;
        }

        string path = SettingsPath;
        string tempPath = path + ".tmp";
        string? dir = Path.GetDirectoryName(path);

        try
        {
            lock (_saveLock)
            {
                if (AutoSavesSuspended)
                    return;
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

                Current.SettingsSchemaVersion = Math.Max(3, Current.SettingsSchemaVersion);
                Version effectiveVersion = MaxVersion(TryParseNormalizedVersion(Current.LastAppVersion), _activeLoadedAppVersion)!;
                Current.LastAppVersion = effectiveVersion.ToString(3);
        if (Current.Shield != null)
        {
            Current.Shield.Version = Math.Max(3, Current.Shield.Version);
        }
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
            string effectiveLogPath = !string.IsNullOrEmpty(CustomSettingsPath)
                ? Path.Combine(Path.GetDirectoryName(CustomSettingsPath) ?? Path.GetTempPath(), "settings.log")
                : LogPath;
            string? dir = Path.GetDirectoryName(effectiveLogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(effectiveLogPath, line);
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

    public static string GetEffectiveDownloadDirectory(string? configuredDir = null)
    {
        string? candidate = configuredDir ?? Current.DefaultDownloadDirectory;
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            try
            {
                string full = Path.GetFullPath(candidate.Trim());
                if (Directory.Exists(full))
                {
                    return full;
                }
            }
            catch { }
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            string defaultDownloads = Path.Combine(userProfile, "Downloads");
            try
            {
                if (!Directory.Exists(defaultDownloads))
                {
                    Directory.CreateDirectory(defaultDownloads);
                }
                if (Directory.Exists(defaultDownloads))
                {
                    return defaultDownloads;
                }
            }
            catch { }
        }

        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktop) && Directory.Exists(desktop))
        {
            return desktop;
        }

        return Path.GetTempPath();
    }
}

