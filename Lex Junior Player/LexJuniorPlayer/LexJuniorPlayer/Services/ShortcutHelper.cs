using System;
using System.IO;
using LexJuniorPlayer.Services;

namespace LexJuniorPlayer.Services;

public static class ShortcutHelper
{
    public static string DesktopShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Lex Junior Player.lnk");

    public static string StartMenuShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Lex Junior Player.lnk");

    public static bool TryCreateDesktopShortcut(string iconPath, out string shortcutPath, string? customTargetExe = null)
    {
        shortcutPath = DesktopShortcutPath;
        return TryCreateShortcut(shortcutPath, iconPath, customTargetExe);
    }

    public static bool TryCreateStartMenuShortcut(string iconPath, out string shortcutPath, string? customTargetExe = null)
    {
        shortcutPath = StartMenuShortcutPath;
        return TryCreateShortcut(shortcutPath, iconPath, customTargetExe);
    }

    public static bool TryCreateShortcut(string shortcutPath, string iconPath, string? customTargetExe = null)
    {
        try
        {
            var exePath = !string.IsNullOrWhiteSpace(customTargetExe) && File.Exists(customTargetExe)
                ? customTargetExe
                : Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return false;

            var dir = Path.GetDirectoryName(shortcutPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return false;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = exePath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
            shortcut.Description = "Lex Junior Player — Motor Android limpio optimizado solo para APKs de streaming";
            if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
            {
                shortcut.IconLocation = iconPath;
            }
            else
            {
                shortcut.IconLocation = $"{exePath},0";
            }
            shortcut.Save();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
