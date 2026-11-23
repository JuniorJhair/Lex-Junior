using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LexJuniorPlayer.Models;
using Microsoft.Win32;

namespace LexJuniorPlayer.Services;

public static class PlayerInstallerService
{
    private const string UninstallSubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LexJuniorPlayer";

    public static bool ShouldRunAsSetupMode(string[]? args)
    {
        if (args != null)
        {
            foreach (var a in args)
            {
                if (string.Equals(a, "--install", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "/install", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "--prereqs", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "--setup", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        var exeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty);
        return exeName.Contains("Setup", StringComparison.OrdinalIgnoreCase) ||
               exeName.Contains("Installer", StringComparison.OrdinalIgnoreCase) ||
               exeName.Contains("Instalador", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldRunAsUninstaller(string[]? args)
    {
        if (args == null) return false;
        return args.Any(a =>
            string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "/uninstall", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task InstallMissingOrOutdatedAsync(
        PrerequisiteReport report,
        bool createDesktopShortcut,
        bool createStartMenuShortcut,
        bool includeAppBinary,
        bool forceReinstallBinary,
        Action<int, string> reportProgress,
        CancellationToken ct = default)
    {
        var sdkRoot = report.DetectedSdkRoot;
        var sdkItemsNeeded = report.Items
            .Where(i => i.CanAutoInstall &&
                        !string.IsNullOrWhiteSpace(i.SdkPackagePath) &&
                        (i.Status == PrereqStatus.Missing || i.Status == PrereqStatus.Outdated))
            .ToList();

        if (sdkItemsNeeded.Count > 0)
        {
            reportProgress(2, "Consultando catálogo oficial de Google Android SDK...");
            var filter = sdkItemsNeeded.Select(i => i.SdkPackagePath).ToList();
            var remoteMap = await PrerequisiteService.FetchRemoteArchivesAsync(filter, ct);

            for (int idx = 0; idx < sdkItemsNeeded.Count; idx++)
            {
                ct.ThrowIfCancellationRequested();
                var item = sdkItemsNeeded[idx];
                if (!remoteMap.TryGetValue(item.SdkPackagePath, out var remote))
                {
                    throw new InvalidOperationException(
                        $"No se encontró el paquete '{item.SdkPackagePath}' en el repositorio oficial de Google Android SDK.");
                }

                int sliceStart = (int)(idx * 75.0 / sdkItemsNeeded.Count);
                int sliceSpan = (int)(75.0 / sdkItemsNeeded.Count);
                await PrerequisiteService.DownloadAndInstallSdkPackageAsync(
                    sdkRoot,
                    item,
                    remote,
                    (subPct, msg) =>
                    {
                        int overall = Math.Clamp(sliceStart + (subPct * sliceSpan / 100), 1, 78);
                        reportProgress(overall, msg);
                    },
                    ct);
            }
        }
        else
        {
            reportProgress(78, "Todos los componentes del Android SDK ya están instalados y al día (sin descargas duplicadas).");
        }

        string targetExe = PrerequisiteService.DefaultInstalledExePath;
        if (includeAppBinary)
        {
            var appItem = report.Items.FirstOrDefault(i => i.Id == "app_binary");
            bool needsCopy = forceReinstallBinary ||
                             appItem == null ||
                             appItem.Status == PrereqStatus.Missing ||
                             appItem.Status == PrereqStatus.Outdated ||
                             !File.Exists(targetExe);

            if (needsCopy)
            {
                reportProgress(82, "Instalando ejecutable de Lex Junior Player...");
                await Task.Run(() => CopyAppBinaryToInstallDir(targetExe), ct);
            }
            else
            {
                reportProgress(82, "Lex Junior Player ya está actualizado (no se reinstala dos veces).");
            }
        }
        else
        {
            targetExe = File.Exists(targetExe)
                ? targetExe
                : (Environment.ProcessPath ?? targetExe);
        }

        reportProgress(90, "Configurando accesos directos y registro...");
        var iconPath = EnsureIconOnDisk();
        if (createDesktopShortcut)
        {
            ShortcutHelper.TryCreateDesktopShortcut(iconPath, out _, targetExe);
        }
        if (createStartMenuShortcut)
        {
            ShortcutHelper.TryCreateStartMenuShortcut(iconPath, out _, targetExe);
        }

        if (File.Exists(PrerequisiteService.DefaultInstalledExePath))
        {
            RegisterInWindowsAddRemovePrograms(iconPath);
        }

        // Pre-configure default AVD cleanly if all SDK pieces are ready now
        var store = new AppLibraryStore();
        var settings = store.LoadSettings();
        if (Directory.Exists(sdkRoot))
        {
            settings.CustomSdkPath = sdkRoot;
            store.SaveSettings(settings);
        }
        var refreshedSdk = SdkLocator.Discover(settings.CustomSdkPath, settings.SelectedSystemImageRelPath);
        AvdProvisioner.EnsureCleanAvd(refreshedSdk, settings, out _);

        reportProgress(100, "¡Instalación y verificación completadas!");
    }

    private static void CopyAppBinaryToInstallDir(string targetExe)
    {
        var installDir = PrerequisiteService.DefaultAppInstallDir;
        Directory.CreateDirectory(installDir);
        var currentExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
        if (string.IsNullOrEmpty(currentExe) || !File.Exists(currentExe))
            throw new InvalidOperationException("No se pudo localizar el ejecutable actual de Lex Junior Player.");

        StopOtherInstances();

        if (!string.Equals(Path.GetFullPath(currentExe), Path.GetFullPath(targetExe), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(currentExe, targetExe, overwrite: true);
            var currentDir = Path.GetDirectoryName(currentExe) ?? string.Empty;
            var sidecarDll = Path.Combine(currentDir, "LexJuniorPlayer.dll");
            if (File.Exists(sidecarDll))
            {
                CopyDirectoryRecursive(currentDir, installDir, Path.GetFileName(currentExe));
            }
        }
    }

    private static void CopyDirectoryRecursive(string sourceDir, string targetDir, string? excludeFile)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var name = Path.GetFileName(file);
            if (!string.IsNullOrEmpty(excludeFile) && string.Equals(name, excludeFile, StringComparison.OrdinalIgnoreCase))
                continue;
            try { File.Copy(file, Path.Combine(targetDir, name), overwrite: true); } catch { }
        }
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectoryRecursive(dir, Path.Combine(targetDir, Path.GetFileName(dir)), null);
        }
    }

    public static string EnsureIconOnDisk()
    {
        var assetsDir = Path.Combine(PrerequisiteService.DefaultAppInstallDir, "Assets");
        Directory.CreateDirectory(assetsDir);
        var destIco = Path.Combine(assetsDir, "lex_junior_player.ico");
        if (File.Exists(destIco))
            return destIco;

        try
        {
            var streamInfo = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/lex_junior_player.ico"));
            if (streamInfo?.Stream != null)
            {
                using var s = streamInfo.Stream;
                using var fs = File.Create(destIco);
                s.CopyTo(fs);
                return destIco;
            }
        }
        catch { }

        var local = Path.Combine(AppContext.BaseDirectory, "Assets", "lex_junior_player.ico");
        if (File.Exists(local))
        {
            try { File.Copy(local, destIco, overwrite: true); } catch { }
        }
        return File.Exists(destIco) ? destIco : local;
    }

    private static void RegisterInWindowsAddRemovePrograms(string iconPath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(UninstallSubKey);
            if (key == null) return;
            var v = typeof(PlayerInstallerService).Assembly.GetName().Version;
            var verStr = v != null ? $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}" : "1.1.0";
            key.SetValue("DisplayName", "Lex Junior Player", RegistryValueKind.String);
            key.SetValue("DisplayVersion", verStr, RegistryValueKind.String);
            key.SetValue("Publisher", "Lex Junior", RegistryValueKind.String);
            key.SetValue("DisplayIcon", File.Exists(iconPath) ? iconPath : PrerequisiteService.DefaultInstalledExePath, RegistryValueKind.String);
            key.SetValue("InstallLocation", PrerequisiteService.DefaultAppInstallDir, RegistryValueKind.String);
            key.SetValue("UninstallString", $"\"{PrerequisiteService.DefaultInstalledExePath}\" --uninstall", RegistryValueKind.String);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        catch { }
    }

    private static void StopOtherInstances()
    {
        int myPid = Environment.ProcessId;
        foreach (var proc in Process.GetProcessesByName("LexJuniorPlayer"))
        {
            try
            {
                if (proc.Id != myPid && !proc.HasExited)
                {
                    proc.CloseMainWindow();
                    if (!proc.WaitForExit(5000) && !proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(2000);
                    }
                }
            }
            catch { }
            finally { proc.Dispose(); }
        }
    }

    public static async Task UninstallAsync(bool deleteLibraryData)
    {
        await Task.Run(() =>
        {
            StopOtherInstances();
            try { if (File.Exists(ShortcutHelper.DesktopShortcutPath)) File.Delete(ShortcutHelper.DesktopShortcutPath); } catch { }
            try { if (File.Exists(ShortcutHelper.StartMenuShortcutPath)) File.Delete(ShortcutHelper.StartMenuShortcutPath); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallSubKey, throwOnMissingSubKey: false); } catch { }

            if (deleteLibraryData)
            {
                var dataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LexJuniorPlayer");
                try { if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true); } catch { }
            }

            if (Directory.Exists(PrerequisiteService.DefaultAppInstallDir))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/C ping 127.0.0.1 -n 3 > nul & rmdir /S /Q \"{PrerequisiteService.DefaultAppInstallDir}\"",
                        WindowStyle = ProcessWindowStyle.Hidden,
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                }
                catch { }
            }
        });
    }
}
