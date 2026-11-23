using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace LexBrowser.Core;

public static class InstallerService
{
    private const string UninstallRegistrySubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LexJuniorBrowser";
    private const string BrowserClientSubKey = @"Software\Clients\StartMenuInternet\LexJuniorBrowser";
    private const string BrowserProgId = "LexJuniorHTML";
    private const string BrowserAppKey = @"Software\Classes\Applications\LexBrowser.exe";

    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LexJuniorBrowser");

    public static string InstalledExePath =>
        Path.Combine(InstallDir, "LexBrowser.exe");

    public static string UserDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LexBrowser");

    public static string DesktopShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Lex Junior Browser.lnk");

    public static string StartMenuShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Lex Junior Browser.lnk");

    public static string LexClassDesktopShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Lex Junior Class.lnk");

    public static string LexClassStartMenuShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Lex Junior Class.lnk");

    public const string BrowserAppUserModelId = "LexJunior.Browser";
    public const string LexClassAppUserModelId = "LexJunior.Class";

    public static string InstalledLexClassIconPath =>
        Path.Combine(InstallDir, "lexclass.ico");

    public static bool IsAlreadyInstalled() => File.Exists(InstalledExePath);

    public static bool ShouldRunAsLexClassLauncher(string[]? args)
    {
        if (args == null) return false;
        foreach (var a in args)
        {
            if (string.Equals(a, "--lexclass", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "/lexclass", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public static bool ShouldRunAsLexClassInstaller(string[]? args)
    {
        if (args == null) return false;
        foreach (var a in args)
        {
            if (string.Equals(a, "--install-lexclass", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "/install-lexclass", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Determina si el ejecutable actual fue abierto como instalador (ej. LexJuniorSetup.exe o con --install).
    /// </summary>
    public static bool ShouldRunAsInstaller(string[] args)
    {
        foreach (var a in args)
        {
            if (string.Equals(a, "--install", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "/install", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        string exeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
        if (exeName.Contains("Setup", StringComparison.OrdinalIgnoreCase) ||
            exeName.Contains("Installer", StringComparison.OrdinalIgnoreCase) ||
            exeName.Contains("Instalador", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public static bool ShouldRunAsSilentUpdate(string[] args)
    {
        foreach (var a in args)
        {
            if (string.Equals(a, "--silent-update", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static bool ShouldRunAsUninstaller(string[] args)
    {
        foreach (var a in args)
        {
            if (string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "/uninstall", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public static async Task InstallAsync(
        bool createDesktopShortcut,
        bool createStartMenuShortcut,
        IProgress<(int Percent, string Status)>? progress = null,
        bool createLexClassDesktopShortcut = false)
    {
        progress?.Report((10, "Cerrando instancias previas de Lex Junior..."));
        await Task.Run(() => StopOtherLexProcesses());

        progress?.Report((30, "Copiando archivos del navegador..."));
        await Task.Run(() =>
        {
            Directory.CreateDirectory(InstallDir);

            string currentExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrEmpty(currentExe) || !File.Exists(currentExe))
            {
                throw new InvalidOperationException("No se pudo localizar el ejecutable origen para la instalación.");
            }

            string currentDir = Path.GetDirectoryName(currentExe) ?? "";

            // Si estamos ejecutando un paquete multi-archivo, copiar carpeta; si es Single-File (LexJuniorSetup.exe), copiar el ejecutable directamente
            if (!string.Equals(Path.GetFullPath(currentExe), Path.GetFullPath(InstalledExePath), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(currentExe, InstalledExePath, overwrite: true);

                // Si existen librerías junto al ejecutable (despliegue no single-file), copiarlas también
                string sidecarDll = Path.Combine(currentDir, "LexBrowser.dll");
                if (File.Exists(sidecarDll))
                {
                    CopyDirectoryRecursive(currentDir, InstallDir, excludeFileName: Path.GetFileName(currentExe));
                }
            }
        });

        progress?.Report((60, "Extrayendo recursos e iconos oficiales..."));
        string iconPath = Path.Combine(InstallDir, "app.ico");
        ExtractEmbeddedIconToDisk(iconPath);
        string lexClassIconPath = InstalledLexClassIconPath;
        ExtractEmbeddedLexClassIconToDisk(lexClassIconPath);

        progress?.Report((80, "Creando accesos directos y registrando navegador en Windows..."));
        await Task.Run(() =>
        {
            if (createDesktopShortcut)
            {
                CreateWindowsShortcut(DesktopShortcutPath, InstalledExePath, InstallDir, iconPath, "Navegador privado con bloqueo nativo y sin telemetría propia");
            }
            if (createStartMenuShortcut)
            {
                CreateWindowsShortcut(StartMenuShortcutPath, InstalledExePath, InstallDir, iconPath, "Navegador privado con bloqueo nativo y sin telemetría propia");
                CreateWindowsShortcut(
                    LexClassStartMenuShortcutPath,
                    InstalledExePath,
                    InstallDir,
                    lexClassIconPath,
                    "Lex Junior Class — Academia interactiva local (Prototipo)",
                    "--lexclass",
                    LexClassAppUserModelId);
            }
            if (createLexClassDesktopShortcut)
            {
                CreateWindowsShortcut(
                    LexClassDesktopShortcutPath,
                    InstalledExePath,
                    InstallDir,
                    lexClassIconPath,
                    "Lex Junior Class — Academia interactiva local (Prototipo)",
                    "--lexclass",
                    LexClassAppUserModelId);
            }

            RegisterInWindowsAddRemovePrograms(iconPath);
            RegisterAsDefaultBrowser(InstalledExePath, iconPath);
            RefreshWindowsIconCache(iconPath);
            MarkIconCacheRefreshedForVersion(UpdateManager.CurrentVersionString);
        });

        progress?.Report((100, "¡Instalación completada con éxito!"));
    }

    public static async Task InstallLexClassShortcutsAsync(
        bool createDesktopShortcut,
        bool createStartMenuShortcut,
        IProgress<(int Percent, string Status)>? progress = null)
    {
        progress?.Report((15, "Preparando carpeta de Lex Junior Class..."));
        string targetExe = File.Exists(InstalledExePath)
            ? InstalledExePath
            : (Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "");

        if (!File.Exists(InstalledExePath) && !string.IsNullOrEmpty(targetExe) && File.Exists(targetExe))
        {
            progress?.Report((35, "Instalando ejecutable base de Lex Junior para acceso independiente..."));
            await Task.Run(() =>
            {
                Directory.CreateDirectory(InstallDir);
                string currentDir = Path.GetDirectoryName(targetExe) ?? "";
                File.Copy(targetExe, InstalledExePath, overwrite: true);
                string sidecarDll = Path.Combine(currentDir, "LexBrowser.dll");
                if (File.Exists(sidecarDll))
                {
                    CopyDirectoryRecursive(currentDir, InstallDir, excludeFileName: Path.GetFileName(targetExe));
                }
            });
            targetExe = InstalledExePath;
        }

        if (string.IsNullOrEmpty(targetExe) || !File.Exists(targetExe))
        {
            throw new InvalidOperationException("No se pudo localizar el ejecutable de Lex Junior para crear el acceso directo.");
        }

        progress?.Report((65, "Extrayendo icono oficial de Lex Junior Class..."));
        string workingDir = Path.GetDirectoryName(targetExe) ?? InstallDir;
        Directory.CreateDirectory(InstallDir);
        string appIcon = Path.Combine(InstallDir, "app.ico");
        if (!File.Exists(appIcon)) ExtractEmbeddedIconToDisk(appIcon);
        string lexClassIconPath = InstalledLexClassIconPath;
        ExtractEmbeddedLexClassIconToDisk(lexClassIconPath);

        progress?.Report((85, "Creando accesos directos de Lex Junior Class..."));
        await Task.Run(() =>
        {
            if (createDesktopShortcut)
            {
                CreateWindowsShortcut(
                    LexClassDesktopShortcutPath,
                    targetExe,
                    workingDir,
                    lexClassIconPath,
                    "Lex Junior Class — Academia interactiva local (Prototipo)",
                    "--lexclass",
                    LexClassAppUserModelId);
            }
            if (createStartMenuShortcut)
            {
                CreateWindowsShortcut(
                    LexClassStartMenuShortcutPath,
                    targetExe,
                    workingDir,
                    lexClassIconPath,
                    "Lex Junior Class — Academia interactiva local (Prototipo)",
                    "--lexclass",
                    LexClassAppUserModelId);
            }

            if (File.Exists(InstalledExePath))
            {
                RegisterInWindowsAddRemovePrograms(appIcon);
            }
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST | SHCNF_FLUSH, IntPtr.Zero, IntPtr.Zero);
        });

        progress?.Report((100, "¡Lex Junior Class quedó listo en tu equipo!"));
    }

    public static void LaunchInstalledLexClass()
    {
        try
        {
            string exe = File.Exists(InstalledExePath)
                ? InstalledExePath
                : (Environment.ProcessPath ?? "");
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "--lexclass",
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? InstallDir,
                    UseShellExecute = true
                });
            }
        }
        catch { }
    }

    public static async Task PerformSilentUpdateAndRestartAsync()
    {
        await InstallAsync(
            createDesktopShortcut: File.Exists(DesktopShortcutPath),
            createStartMenuShortcut: true,
            progress: null,
            createLexClassDesktopShortcut: File.Exists(LexClassDesktopShortcutPath));
        LaunchInstalledBrowser();
    }

    public static void LaunchInstalledBrowser()
    {
        try
        {
            if (File.Exists(InstalledExePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = InstalledExePath,
                    WorkingDirectory = InstallDir,
                    UseShellExecute = true
                });
            }
        }
        catch { }
    }

    public static async Task UninstallAsync(bool deleteUserData, IProgress<(int Percent, string Status)>? progress = null)
    {
        progress?.Report((15, "Cerrando procesos activos de Lex Junior..."));
        await Task.Run(() => StopOtherLexProcesses());

        progress?.Report((45, "Eliminando accesos directos y registro de Windows..."));
        await Task.Run(() =>
        {
            try { if (File.Exists(DesktopShortcutPath)) File.Delete(DesktopShortcutPath); } catch { }
            try { if (File.Exists(StartMenuShortcutPath)) File.Delete(StartMenuShortcutPath); } catch { }
            try { if (File.Exists(LexClassDesktopShortcutPath)) File.Delete(LexClassDesktopShortcutPath); } catch { }
            try { if (File.Exists(LexClassStartMenuShortcutPath)) File.Delete(LexClassStartMenuShortcutPath); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallRegistrySubKey, throwOnMissingSubKey: false); } catch { }
            UnregisterAsDefaultBrowser();
        });

        if (deleteUserData)
        {
            progress?.Report((75, "Limpiando datos de navegación, caché y configuración..."));
            await Task.Run(() =>
            {
                string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string[] dataFolders = new[]
                {
                    Path.Combine(localApp, "LexBrowser"),
                    Path.Combine(localApp, "LexJuniorBrowser")
                };

                foreach (var folder in dataFolders)
                {
                    try
                    {
                        if (Directory.Exists(folder))
                        {
                            Directory.Delete(folder, recursive: true);
                        }
                    }
                    catch { }
                }
            });
        }

        progress?.Report((95, "Programando limpieza final de carpeta de programa..."));
        ScheduleSelfDeletionOfInstallDir();
        progress?.Report((100, "Desinstalación completada."));
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern uint AssocQueryString(
        uint flags,
        uint str,
        string pszAssoc,
        string? pszExtra,
        [Out] StringBuilder pszOut,
        ref uint pcchOut
    );

    private const uint ASSOCF_NONE = 0x00000000;
    private const uint ASSOCSTR_EXECUTABLE = 2;
    private const uint S_OK = 0;

    /// <summary>
    /// Comprueba si Lex Junior está actualmente configurado como navegador predeterminado en Windows.
    /// Utiliza primero la API nativa de Shell (AssocQueryString) de Windows 10/11 y respaldo por Registro de Usuario (UserChoice).
    /// </summary>
    public static bool IsDefaultBrowser()
    {
        // 1. Detección nativa del Shell de Windows (fuente de verdad definitiva para https/http)
        try
        {
            string? httpsExe = GetShellProtocolExecutable("https");
            if (IsMatchingBrowserExe(httpsExe)) return true;

            string? httpExe = GetShellProtocolExecutable("http");
            if (IsMatchingBrowserExe(httpExe)) return true;
        }
        catch { }

        // 2. Respaldo por Registro de Usuario (UserChoice)
        try
        {
            if (CheckUserChoiceProgId(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice") ||
                CheckUserChoiceProgId(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice") ||
                CheckUserChoiceProgId(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.html\UserChoice"))
            {
                return true;
            }
        }
        catch { }

        return false;
    }

    private static string? GetShellProtocolExecutable(string protocol)
    {
        try
        {
            uint pcchOut = 1024;
            var sb = new StringBuilder(1024);
            uint hr = AssocQueryString(ASSOCF_NONE, ASSOCSTR_EXECUTABLE, protocol, "open", sb, ref pcchOut);
            if (hr == S_OK && sb.Length > 0)
            {
                return sb.ToString().Trim('\"', ' ');
            }
        }
        catch { }
        return null;
    }

    public static bool IsMatchingBrowserExe(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;

        try
        {
            string clean = exePath.Trim('\"', ' ');

            // Si contiene argumentos o comillas intermedias, extraer solo la ruta del ejecutable
            if (clean.EndsWith(".exe\"", StringComparison.OrdinalIgnoreCase) || clean.Contains(".exe ", StringComparison.OrdinalIgnoreCase))
            {
                int exeIdx = clean.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (exeIdx > 0)
                {
                    clean = clean.Substring(0, exeIdx + 4).Trim('\"', ' ');
                }
            }

            string currentExe = Environment.ProcessPath ?? "";
            string installedExe = InstalledExePath;

            if (!string.IsNullOrEmpty(installedExe) &&
                string.Equals(Path.GetFullPath(clean), Path.GetFullPath(installedExe), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(currentExe) &&
                string.Equals(Path.GetFullPath(clean), Path.GetFullPath(currentExe), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string fileName = Path.GetFileName(clean);
            if (fileName.Contains("LexBrowser", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains("LexJunior", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (clean.Contains("LexJunior", StringComparison.OrdinalIgnoreCase) ||
                clean.Contains("LexBrowser", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch { }

        return false;
    }

    private static bool CheckUserChoiceProgId(string subKeyPath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(subKeyPath);
            var progId = key?.GetValue("ProgId") as string;
            if (string.IsNullOrEmpty(progId)) return false;

            return string.Equals(progId, BrowserProgId, StringComparison.OrdinalIgnoreCase) ||
                   progId.Contains("LexJunior", StringComparison.OrdinalIgnoreCase) ||
                   progId.Contains("LexBrowser", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Limpia instaladores previos de Lex Junior acumulados en la carpeta %TEMP%.
    /// </summary>
    public static void CleanOldTempInstallers()
    {
        try
        {
            string currentExe = Environment.ProcessPath ?? "";
            string fullCurrentExe = string.IsNullOrEmpty(currentExe) ? "" : Path.GetFullPath(currentExe);
            string[] dirs =
            {
                Path.GetTempPath(),
                Path.Combine(UserDataDir, "Updates")
            };

            foreach (string dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (string pattern in new[] { "LexJuniorSetup*.exe", "LexJuniorSetup*.part" })
                {
                    foreach (var file in Directory.GetFiles(dir, pattern))
                    {
                        try
                        {
                            if (string.IsNullOrEmpty(fullCurrentExe) ||
                                !string.Equals(Path.GetFullPath(file), fullCurrentExe, StringComparison.OrdinalIgnoreCase))
                            {
                                File.Delete(file);
                            }
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Abre la pantalla oficial de Windows 10/11 para seleccionar el navegador web predeterminado.
    /// </summary>
    public static void OpenWindowsDefaultAppsSettings()
    {
        RegisterAsDefaultBrowser();

        try
        {
            // Windows 11: abre directamente la sección de Lex Junior
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:defaultapps?registeredAppUser=LexJuniorBrowser",
                UseShellExecute = true
            });
            return;
        }
        catch { }

        try
        {
            // Windows 10: abre la pantalla de Aplicaciones Predeterminadas estándar
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:defaultapps",
                UseShellExecute = true
            });
        }
        catch { }
    }

    /// <summary>
    /// Registra formalmente a Lex Junior como navegador web en Windows (StartMenuInternet, Capabilities, ProgID).
    /// Se escribe en HKEY_CURRENT_USER por lo que NUNCA requiere permisos de Administrador ni eleva UAC.
    /// </summary>
    public static void RegisterAsDefaultBrowser(string? targetExe = null, string? targetIcon = null)
    {
        try
        {
            targetExe ??= File.Exists(InstalledExePath) ? InstalledExePath : (Environment.ProcessPath ?? "");
            if (string.IsNullOrEmpty(targetExe) || !File.Exists(targetExe)) return;

            targetIcon ??= Path.Combine(InstallDir, "app.ico");
            if (!File.Exists(targetIcon))
            {
                string localIcon = Path.Combine(Path.GetDirectoryName(targetExe) ?? "", "app.ico");
                targetIcon = File.Exists(localIcon) ? localIcon : targetExe;
            }

            // 1. HKCU\Software\Clients\StartMenuInternet\LexJuniorBrowser
            using (var clientKey = Registry.CurrentUser.CreateSubKey(BrowserClientSubKey))
            {
                if (clientKey != null)
                {
                    clientKey.SetValue("", "Lex Junior Browser", RegistryValueKind.String);

                    using (var iconKey = clientKey.CreateSubKey("DefaultIcon"))
                    {
                        iconKey.SetValue("", $"{targetIcon},0", RegistryValueKind.String);
                    }

                    using (var shellOpen = clientKey.CreateSubKey(@"shell\open\command"))
                    {
                        shellOpen.SetValue("", $"\"{targetExe}\"", RegistryValueKind.String);
                    }

                    using (var installInfo = clientKey.CreateSubKey("InstallInfo"))
                    {
                        installInfo.SetValue("ReinstallCommand", $"\"{targetExe}\" --install", RegistryValueKind.String);
                        installInfo.SetValue("HideIconsCommand", "", RegistryValueKind.String);
                        installInfo.SetValue("ShowIconsCommand", "", RegistryValueKind.String);
                        installInfo.SetValue("IconsVisible", 1, RegistryValueKind.DWord);
                    }

                    using (var cap = clientKey.CreateSubKey("Capabilities"))
                    {
                        cap.SetValue("ApplicationDescription", "Lex Junior - Navegador privado con LexShield Core y sin telemetría propia.", RegistryValueKind.String);
                        cap.SetValue("ApplicationIcon", $"{targetIcon},0", RegistryValueKind.String);
                        cap.SetValue("ApplicationName", "Lex Junior Browser", RegistryValueKind.String);

                        using (var fa = cap.CreateSubKey("FileAssociations"))
                        {
                            fa.SetValue(".htm", BrowserProgId, RegistryValueKind.String);
                            fa.SetValue(".html", BrowserProgId, RegistryValueKind.String);
                            fa.SetValue(".pdf", BrowserProgId, RegistryValueKind.String);
                            fa.SetValue(".shtml", BrowserProgId, RegistryValueKind.String);
                            fa.SetValue(".xht", BrowserProgId, RegistryValueKind.String);
                            fa.SetValue(".xhtml", BrowserProgId, RegistryValueKind.String);
                            fa.SetValue(".svg", BrowserProgId, RegistryValueKind.String);
                            fa.SetValue(".webp", BrowserProgId, RegistryValueKind.String);
                        }

                        using (var ua = cap.CreateSubKey("URLAssociations"))
                        {
                            ua.SetValue("http", BrowserProgId, RegistryValueKind.String);
                            ua.SetValue("https", BrowserProgId, RegistryValueKind.String);
                            ua.SetValue("ftp", BrowserProgId, RegistryValueKind.String);
                        }

                        using (var sm = cap.CreateSubKey("StartMenu"))
                        {
                            sm.SetValue("StartMenuInternet", "LexJuniorBrowser", RegistryValueKind.String);
                        }
                    }
                }
            }

            // 2. HKCU\Software\RegisteredApplications
            using (var regApps = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            {
                regApps?.SetValue("LexJuniorBrowser", @"Software\Clients\StartMenuInternet\LexJuniorBrowser\Capabilities", RegistryValueKind.String);
            }

            // 3. HKCU\Software\Classes\LexJuniorHTML
            using (var progIdKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{BrowserProgId}"))
            {
                if (progIdKey != null)
                {
                    progIdKey.SetValue("", "Documento HTML de Lex Junior", RegistryValueKind.String);
                    progIdKey.SetValue("AppUserModelId", "LexJunior.Browser", RegistryValueKind.String);
                    progIdKey.SetValue("FriendlyTypeName", "Documento HTML de Lex Junior", RegistryValueKind.String);

                    using (var iconKey = progIdKey.CreateSubKey("DefaultIcon"))
                    {
                        iconKey.SetValue("", $"{targetIcon},0", RegistryValueKind.String);
                    }

                    using (var shellOpen = progIdKey.CreateSubKey(@"shell\open\command"))
                    {
                        shellOpen.SetValue("", $"\"{targetExe}\" \"%1\"", RegistryValueKind.String);
                    }

                    using (var appInfo = progIdKey.CreateSubKey("Application"))
                    {
                        appInfo.SetValue("ApplicationName", "Lex Junior Browser", RegistryValueKind.String);
                        appInfo.SetValue("ApplicationIcon", $"{targetIcon},0", RegistryValueKind.String);
                        appInfo.SetValue("ApplicationDescription", "Documento web de Lex Junior", RegistryValueKind.String);
                        appInfo.SetValue("AppUserModelId", "LexJunior.Browser", RegistryValueKind.String);
                    }
                }
            }

            // 4. HKCU\Software\Classes\Applications\LexBrowser.exe
            using (var appKey = Registry.CurrentUser.CreateSubKey(BrowserAppKey))
            {
                if (appKey != null)
                {
                    appKey.SetValue("", "Lex Junior Browser", RegistryValueKind.String);
                    appKey.SetValue("FriendlyAppName", "Lex Junior Browser", RegistryValueKind.String);
                    appKey.SetValue("AppUserModelID", "LexJunior.Browser", RegistryValueKind.String);

                    using (var sup = appKey.CreateSubKey("SupportedProtocols"))
                    {
                        sup.SetValue("http", "", RegistryValueKind.String);
                        sup.SetValue("https", "", RegistryValueKind.String);
                    }

                    using (var iconKey = appKey.CreateSubKey("DefaultIcon"))
                    {
                        iconKey.SetValue("", $"{targetIcon},0", RegistryValueKind.String);
                    }

                    using (var shellOpen = appKey.CreateSubKey(@"shell\open\command"))
                    {
                        shellOpen.SetValue("", $"\"{targetExe}\" \"%1\"", RegistryValueKind.String);
                    }
                }
            }

            // 5. OpenWithProgids para que aparezca en el menú clic derecho "Abrir con..."
            string[] extensions = { ".htm", ".html", ".pdf", ".shtml", ".svg", ".webp" };
            foreach (var ext in extensions)
            {
                try
                {
                    using var extKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\OpenWithProgids");
                    extKey?.SetValue(BrowserProgId, string.Empty, RegistryValueKind.String);
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// Limpia todas las claves de registro del navegador al desinstalar.
    /// </summary>
    public static void UnregisterAsDefaultBrowser()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(BrowserClientSubKey, throwOnMissingSubKey: false);
        }
        catch { }

        try
        {
            using var regApps = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true);
            regApps?.DeleteValue("LexJuniorBrowser", throwOnMissingValue: false);
        }
        catch { }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{BrowserProgId}", throwOnMissingSubKey: false);
        }
        catch { }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(BrowserAppKey, throwOnMissingSubKey: false);
        }
        catch { }

        string[] extensions = { ".htm", ".html", ".pdf", ".shtml", ".svg", ".webp" };
        foreach (var ext in extensions)
        {
            try
            {
                using var extKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ext}\OpenWithProgids", writable: true);
                extKey?.DeleteValue(BrowserProgId, throwOnMissingValue: false);
            }
            catch { }
        }
    }

    private static void StopOtherLexProcesses()
    {
        int currentPid = Environment.ProcessId;
        foreach (var proc in Process.GetProcessesByName("LexBrowser"))
        {
            try
            {
                if (proc.Id != currentPid && !proc.HasExited)
                {
                    try
                    {
                        if (proc.MainWindowHandle != IntPtr.Zero)
                        {
                            proc.CloseMainWindow();
                        }
                    }
                    catch { }

                    bool exited = proc.WaitForExit(10000);
                    if (!exited && !proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(3000);
                    }
                }
            }
            catch { }
            finally
            {
                proc.Dispose();
            }
        }
    }

    private static void CopyDirectoryRecursive(string sourceDir, string targetDir, string? excludeFileName = null)
    {
        Directory.CreateDirectory(targetDir);
        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string name = Path.GetFileName(file);
            if (!string.IsNullOrEmpty(excludeFileName) && string.Equals(name, excludeFileName, StringComparison.OrdinalIgnoreCase))
                continue;

            string dest = Path.Combine(targetDir, name);
            try { File.Copy(file, dest, overwrite: true); } catch { }
        }

        foreach (string subDir in Directory.GetDirectories(sourceDir))
        {
            string name = Path.GetFileName(subDir);
            CopyDirectoryRecursive(subDir, Path.Combine(targetDir, name));
        }
    }

    private static void ExtractEmbeddedIconToDisk(string targetIconPath) =>
        ExtractEmbeddedPackIconToDisk("pack://application:,,,/Assets/app.ico", targetIconPath);

    public static void ExtractEmbeddedLexClassIconToDisk(string targetIconPath) =>
        ExtractEmbeddedPackIconToDisk("pack://application:,,,/Assets/lexclass.ico", targetIconPath);

    public static string EnsureLexClassIconOnDisk()
    {
        string path = InstalledLexClassIconPath;
        try
        {
            if (!File.Exists(path))
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                ExtractEmbeddedLexClassIconToDisk(path);
            }
            if (File.Exists(path)) return path;

            string fallback = Path.Combine(LexClassStudioService.LexClassDataDir, "lexclass.ico");
            if (!File.Exists(fallback))
            {
                Directory.CreateDirectory(LexClassStudioService.LexClassDataDir);
                ExtractEmbeddedLexClassIconToDisk(fallback);
            }
            return fallback;
        }
        catch
        {
            return path;
        }
    }

    private static void ExtractEmbeddedPackIconToDisk(string packUri, string targetIconPath)
    {
        try
        {
            var streamInfo = Application.GetResourceStream(new Uri(packUri));
            if (streamInfo?.Stream != null)
            {
                using var s = streamInfo.Stream;
                using var fs = new FileStream(targetIconPath, FileMode.Create, FileAccess.Write);
                s.CopyTo(fs);
            }
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", SetLastError = true)]
    public static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    public const uint SHCNE_ASSOCCHANGED = 0x08000000;
    public const uint SHCNF_IDLIST = 0x0000;
    public const uint SHCNF_FLUSH = 0x1000;

    private static string IconCacheVersionMarkerPath => Path.Combine(UserDataDir, ".icon_cache_version");

    public static void MarkIconCacheRefreshedForVersion(string version)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(version)) return;
            Directory.CreateDirectory(UserDataDir);
            File.WriteAllText(IconCacheVersionMarkerPath, version.Trim());
        }
        catch { }
    }

    public static bool RefreshIconCacheOnceIfVersionChanged(string currentVersion)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(currentVersion) || !File.Exists(InstalledExePath))
                return false;

            string target = currentVersion.Trim();
            if (File.Exists(IconCacheVersionMarkerPath))
            {
                string recorded = File.ReadAllText(IconCacheVersionMarkerPath).Trim();
                if (string.Equals(recorded, target, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            RefreshWindowsIconCache();
            MarkIconCacheRefreshedForVersion(target);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void RefreshWindowsIconCache(string? customIconPath = null)
    {
        try
        {
            string iconPath = customIconPath ?? Path.Combine(InstallDir, "app.ico");
            if (!File.Exists(iconPath))
            {
                ExtractEmbeddedIconToDisk(iconPath);
            }

            if (File.Exists(InstalledExePath))
            {
                // 1. Recrear acceso directo en el Escritorio
                if (File.Exists(DesktopShortcutPath))
                {
                    CreateWindowsShortcut(DesktopShortcutPath, InstalledExePath, InstallDir, iconPath, "Navegador privado con bloqueo nativo y sin telemetría propia");
                }

                // 2. Recrear acceso directo en el Menú Inicio
                if (File.Exists(StartMenuShortcutPath))
                {
                    CreateWindowsShortcut(StartMenuShortcutPath, InstalledExePath, InstallDir, iconPath, "Navegador privado con bloqueo nativo y sin telemetría propia");
                }

                // 2.5 Recrear accesos directos de Lex Junior Class si existen
                string lexClassIcon = InstalledLexClassIconPath;
                if (!File.Exists(lexClassIcon))
                {
                    ExtractEmbeddedLexClassIconToDisk(lexClassIcon);
                }
                if (File.Exists(LexClassDesktopShortcutPath))
                {
                    CreateWindowsShortcut(
                        LexClassDesktopShortcutPath,
                        InstalledExePath,
                        InstallDir,
                        lexClassIcon,
                        "Lex Junior Class — Academia interactiva local (Prototipo)",
                        "--lexclass",
                        LexClassAppUserModelId);
                }
                if (File.Exists(LexClassStartMenuShortcutPath))
                {
                    CreateWindowsShortcut(
                        LexClassStartMenuShortcutPath,
                        InstalledExePath,
                        InstallDir,
                        lexClassIcon,
                        "Lex Junior Class — Academia interactiva local (Prototipo)",
                        "--lexclass",
                        LexClassAppUserModelId);
                }

                // 3. Actualizar accesos directos anclados en la barra de tareas de Windows
                string taskbarDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

                if (Directory.Exists(taskbarDir))
                {
                    foreach (var file in Directory.GetFiles(taskbarDir, "*.lnk"))
                    {
                        string fileName = Path.GetFileName(file);
                        if (fileName.Contains("Lex Junior Class", StringComparison.OrdinalIgnoreCase))
                        {
                            CreateWindowsShortcut(file, InstalledExePath, InstallDir, lexClassIcon, "Lex Junior Class", "--lexclass", LexClassAppUserModelId);
                        }
                        else if (fileName.Contains("Lex", StringComparison.OrdinalIgnoreCase))
                        {
                            CreateWindowsShortcut(file, InstalledExePath, InstallDir, iconPath, "Lex Junior Browser");
                        }
                    }
                }
            }

            // 4. Notificar a Windows Shell de cambios inmediatos en caché de iconos
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST | SHCNF_FLUSH, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    private static void CreateWindowsShortcut(
        string lnkPath,
        string targetExe,
        string workingDir,
        string iconPath,
        string description,
        string? arguments = null,
        string appUserModelId = BrowserAppUserModelId)
    {
        try
        {
            if (File.Exists(lnkPath))
            {
                try { File.Delete(lnkPath); } catch { }
            }

            // Crear mediante IShellLinkW e IPropertyStore para asignar System.AppUserModel.ID
            // Esto garantiza que el icono anclado en la barra de tareas y la ventana en ejecución
            // tengan EXACTAMENTE el mismo ID, previniendo que se duplique al abrirse.
            var shellLink = (IShellLinkW)new CShellLink();
            shellLink.SetPath(targetExe);
            shellLink.SetWorkingDirectory(workingDir);
            shellLink.SetDescription(description);
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                shellLink.SetArguments(arguments);
            }

            if (File.Exists(iconPath))
            {
                shellLink.SetIconLocation(iconPath, 0);
            }
            else
            {
                shellLink.SetIconLocation(targetExe, 0);
            }

            try
            {
                var propertyStore = (IPropertyStore)shellLink;
                // PKEY_AppUserModel_ID: {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, 5
                var pkey = new PROPERTYKEY(new Guid("{9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}"), 5);
                var pv = new PROPVARIANT
                {
                    vt = 31, // VT_LPWSTR
                    pwszVal = System.Runtime.InteropServices.Marshal.StringToCoTaskMemUni(appUserModelId)
                };
                try
                {
                    propertyStore.SetValue(ref pkey, ref pv);
                    propertyStore.Commit();
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FreeCoTaskMem(pv.pwszVal);
                }
            }
            catch { }

            var persistFile = (IPersistFile)shellLink;
            persistFile.Save(lnkPath, true);

            if (File.Exists(lnkPath))
            {
                try { File.SetLastWriteTime(lnkPath, DateTime.Now); } catch { }
            }
        }
        catch
        {
            // Respaldo WScript.Shell si la API COM nativa falla
            try
            {
                Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;
                dynamic? shell = Activator.CreateInstance(shellType);
                if (shell == null) return;

                dynamic shortcut = shell.CreateShortcut(lnkPath);
                shortcut.TargetPath = targetExe;
                shortcut.WorkingDirectory = workingDir;
                shortcut.Description = description;
                if (!string.IsNullOrWhiteSpace(arguments))
                {
                    shortcut.Arguments = arguments;
                }
                shortcut.IconLocation = File.Exists(iconPath) ? $"{iconPath},0" : $"{targetExe},0";
                shortcut.Save();
            }
            catch { }
        }
    }

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    [System.Runtime.InteropServices.Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([System.Runtime.InteropServices.Out, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cchMaxPath, out IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([System.Runtime.InteropServices.Out, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cchMaxName);
        void SetDescription([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([System.Runtime.InteropServices.Out, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([System.Runtime.InteropServices.Out, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([System.Runtime.InteropServices.Out, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszFile);
    }

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    [System.Runtime.InteropServices.Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PROPERTYKEY pkey);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        void SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        void Commit();
    }

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    [System.Runtime.InteropServices.Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        void IsDirty();
        void Load([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszFileName, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([System.Runtime.InteropServices.Out, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] System.Text.StringBuilder ppszFileName);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
        public PROPERTYKEY(Guid guid, uint id) { fmtid = guid; pid = id; }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
    private struct PROPVARIANT
    {
        [System.Runtime.InteropServices.FieldOffset(0)] public ushort vt;
        [System.Runtime.InteropServices.FieldOffset(8)] public IntPtr pwszVal;
    }

    private static void RegisterInWindowsAddRemovePrograms(string iconPath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(UninstallRegistrySubKey);
            if (key == null) return;

            key.SetValue("DisplayName", "Lex Junior Browser", RegistryValueKind.String);
            key.SetValue("DisplayVersion", UpdateManager.CurrentVersionString, RegistryValueKind.String);
            key.SetValue("Publisher", "Lex Junior", RegistryValueKind.String);
            key.SetValue("DisplayIcon", File.Exists(iconPath) ? iconPath : InstalledExePath, RegistryValueKind.String);
            key.SetValue("InstallLocation", InstallDir, RegistryValueKind.String);
            key.SetValue("UninstallString", $"\"{InstalledExePath}\" --uninstall", RegistryValueKind.String);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        catch { }
    }

    private static void ScheduleSelfDeletionOfInstallDir()
    {
        try
        {
            if (!Directory.Exists(InstallDir)) return;
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/C ping 127.0.0.1 -n 3 > nul & rmdir /S /Q \"{InstallDir}\"",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(psi);
        }
        catch { }
    }
}
