using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LexJuniorPlayer.Models;

namespace LexJuniorPlayer.Services;

public static class ApkInspector
{
    public static async Task<ApkMetadata?> InspectApkAsync(string apkPath, string aapt2Exe)
    {
        if (!File.Exists(apkPath)) return null;

        if (!string.IsNullOrWhiteSpace(aapt2Exe) && File.Exists(aapt2Exe))
        {
            var psi = new ProcessStartInfo(aapt2Exe, $"dump badging \"{apkPath}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var stdout = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();
                if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
                {
                    return ParseAapt2Badging(stdout, apkPath);
                }
            }
        }

        return new ApkMetadata(
            FilePath: apkPath,
            PackageName: Path.GetFileNameWithoutExtension(apkPath),
            AppName: Path.GetFileNameWithoutExtension(apkPath),
            VersionName: string.Empty,
            VersionCode: 0,
            LaunchActivity: string.Empty,
            TvActivity: string.Empty,
            IconZipPath: string.Empty,
            SupportedAbis: string.Empty
        );
    }

    public static ApkMetadata ParseAapt2Badging(string output, string apkPath)
    {
        var pkgMatch = Regex.Match(output, @"package:\s*name='([^']+)'");
        var verCodeMatch = Regex.Match(output, @"versionCode='(\d*)'");
        var verNameMatch = Regex.Match(output, @"versionName='([^']*)'");

        var packageName = pkgMatch.Success ? pkgMatch.Groups[1].Value : Path.GetFileNameWithoutExtension(apkPath);
        long.TryParse(verCodeMatch.Success ? verCodeMatch.Groups[1].Value : "0", out var versionCode);
        var versionName = verNameMatch.Success ? verNameMatch.Groups[1].Value : string.Empty;

        var appName =
            MatchFirst(output, @"application-label-es:'([^']+)'") ??
            MatchFirst(output, @"application-label:'([^']+)'") ??
            MatchFirst(output, @"application:[^\r\n]*label='([^']+)'") ??
            packageName;

        var launchActivity = MatchFirst(output, @"launchable-activity:\s*name='([^']+)'") ?? string.Empty;
        var tvActivity = MatchFirst(output, @"leanback-launchable-activity:\s*name='([^']+)'") ?? string.Empty;

        var iconMatches = Regex.Matches(output, @"application-icon-(\d+):'([^']+)'")
            .Cast<Match>()
            .Select(m => new
            {
                Dpi = int.TryParse(m.Groups[1].Value, out var d) ? d : 0,
                Path = m.Groups[2].Value
            })
            .Where(x => x.Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Dpi)
            .FirstOrDefault();

        var fallbackAppIcon = MatchFirst(output, @"application:[^\r\n]*icon='([^']+)'");
        var iconZipPath = iconMatches?.Path
            ?? (fallbackAppIcon != null && fallbackAppIcon.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? fallbackAppIcon : string.Empty);

        var abisMatch = MatchFirst(output, @"native-code:\s*([^\r\n]+)");
        var supportedAbis = abisMatch == null
            ? "Universal (Java/Kotlin)"
            : string.Join(", ", Regex.Matches(abisMatch, @"'([^']+)'").Cast<Match>().Select(m => m.Groups[1].Value));

        return new ApkMetadata(
            FilePath: apkPath,
            PackageName: packageName,
            AppName: appName,
            VersionName: versionName,
            VersionCode: versionCode,
            LaunchActivity: launchActivity,
            TvActivity: tvActivity,
            IconZipPath: iconZipPath,
            SupportedAbis: supportedAbis
        );
    }

    public static string ExtractBestIconPng(ApkMetadata meta, string iconsFolder)
    {
        try
        {
            Directory.CreateDirectory(iconsFolder);
            var safeFileName = SanitizeFileName(meta.PackageName) + ".png";
            var destPath = Path.Combine(iconsFolder, safeFileName);

            using var archive = ZipFile.OpenRead(meta.FilePath);
            ZipArchiveEntry? entry = null;

            if (!string.IsNullOrWhiteSpace(meta.IconZipPath))
            {
                entry = archive.GetEntry(meta.IconZipPath.Replace('\\', '/'));
            }

            if (entry == null)
            {
                entry = archive.Entries
                    .Where(e => e.FullName.StartsWith("res/", StringComparison.OrdinalIgnoreCase)
                             && e.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                             && !e.FullName.Contains("foreground", StringComparison.OrdinalIgnoreCase)
                             && !e.FullName.Contains("background", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(e => ScoreIconEntry(e))
                    .FirstOrDefault();
            }

            if (entry == null) return string.Empty;

            using var src = entry.Open();
            using var dst = File.Create(destPath);
            src.CopyTo(dst);
            return destPath;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int ScoreIconEntry(ZipArchiveEntry e)
    {
        var name = e.FullName.ToLowerInvariant();
        var score = 0;
        if (name.Contains("ic_launcher") || name.Contains("icon_launcher")) score += 50000;
        else if (name.Contains("launcher") || name.Contains("icon")) score += 20000;
        if (name.Contains("xxxhdpi")) score += 5000;
        else if (name.Contains("xxhdpi")) score += 4000;
        else if (name.Contains("xhdpi")) score += 3000;
        else if (name.Contains("hdpi")) score += 2000;
        if (name.EndsWith("_round.png")) score -= 500;
        score += (int)Math.Min(e.Length / 16, 4000);
        return score;
    }

    private static string? MatchFirst(string input, string pattern)
    {
        var m = Regex.Match(input, pattern);
        return m.Success && m.Groups.Count > 1 && !string.IsNullOrWhiteSpace(m.Groups[1].Value)
            ? m.Groups[1].Value.Trim()
            : null;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
