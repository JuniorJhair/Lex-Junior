using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
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
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LexJuniorBrowser");

    public static string DesktopShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Lex Junior Browser.lnk");

    public static string StartMenuShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Lex Junior Browser.lnk");

    public static bool IsAlreadyInstalled() => File.Exists(InstalledExePath);

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
        IProgress<(int Percent, string Status)>? progress = null)
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

        progress?.Report((80, "Creando accesos directos y registrando navegador en Windows..."));
        await Task.Run(() =>
        {
            if (createDesktopShortcut)
            {
                CreateWindowsShortcut(DesktopShortcutPath, InstalledExePath, InstallDir, iconPath, "Navegador ultraligero con bloqueo nativo y 0 telemetría");
            }
            if (createStartMenuShortcut)
            {
                CreateWindowsShortcut(StartMenuShortcutPath, InstalledExePath, InstallDir, iconPath, "Navegador ultraligero con bloqueo nativo y 0 telemetría");
            }

            RegisterInWindowsAddRemovePrograms(iconPath);
            RegisterAsDefaultBrowser(InstalledExePath, iconPath);
        });

        progress?.Report((100, "¡Instalación completada con éxito!"));
    }

    public static async Task PerformSilentUpdateAndRestartAsync()
    {
        await InstallAsync(createDesktopShortcut: File.Exists(DesktopShortcutPath), createStartMenuShortcut: true);
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
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallRegistrySubKey, throwOnMissingSubKey: false); } catch { }
            UnregisterAsDefaultBrowser();
        });

        if (deleteUserData)
        {
            progress?.Report((75, "Limpiando datos de navegación, caché y configuración..."));
            await Task.Run(() =>
            {
                try
                {
                    if (Directory.Exists(UserDataDir))
                    {
                        Directory.Delete(UserDataDir, recursive: true);
                    }
                }
                catch { }
            });
        }

        progress?.Report((95, "Programando limpieza final de carpeta de programa..."));
        ScheduleSelfDeletionOfInstallDir();
        progress?.Report((100, "Desinstalación completada."));
    }

    /// <summary>
    /// Comprueba si Lex Junior está actualmente configurado como navegador predeterminado para HTTP/HTTPS.
    /// </summary>
    public static bool IsDefaultBrowser()
    {
        try
        {
            using var httpsKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            var progId = httpsKey?.GetValue("ProgId") as string;
            if (string.Equals(progId, BrowserProgId, StringComparison.OrdinalIgnoreCase)) return true;

            using var httpKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice");
            var httpProgId = httpKey?.GetValue("ProgId") as string;
            return string.Equals(httpProgId, BrowserProgId, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
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
                        cap.SetValue("ApplicationDescription", "Lex Junior - Navegador ultraligero con bloqueo nativo de anuncios y 0 telemetría.", RegistryValueKind.String);
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
                if (proc.Id != currentPid)
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(3000);
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

    private static void ExtractEmbeddedIconToDisk(string targetIconPath)
    {
        try
        {
            var streamInfo = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (streamInfo?.Stream != null)
            {
                using var s = streamInfo.Stream;
                using var fs = new FileStream(targetIconPath, FileMode.Create, FileAccess.Write);
                s.CopyTo(fs);
            }
        }
        catch { }
    }

    private static void CreateWindowsShortcut(string lnkPath, string targetExe, string workingDir, string iconPath, string description)
    {
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
            if (File.Exists(iconPath))
            {
                shortcut.IconLocation = $"{iconPath},0";
            }
            else
            {
                shortcut.IconLocation = $"{targetExe},0";
            }
            shortcut.Save();
        }
        catch { }
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
