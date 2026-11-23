using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LexBrowser.Core;

public static class LexPlayerLauncher
{
    public const string PlayerReleaseTag = "player-v1.1";

    public const string PlayerSetupDownloadUrl =
        $"https://github.com/{UpdateManager.GitHubOwner}/{UpdateManager.GitHubRepo}/releases/download/{PlayerReleaseTag}/LexJuniorPlayerSetup.exe";

    public static string InstalledPlayerDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LexJuniorPlayer");

    public static string InstalledPlayerExePath =>
        Path.Combine(InstalledPlayerDir, "LexJuniorPlayer.exe");

    public static string? FindInstalledPlayerExe()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            InstalledPlayerExePath,
            Path.Combine(baseDir, "LexJuniorPlayer.exe"),
            Path.Combine(baseDir, "LexJuniorPlayer", "LexJuniorPlayer.exe")
        };

        // Workspace / dev layout fallbacks
        try
        {
            var probe = baseDir;
            for (int i = 0; i < 6 && !string.IsNullOrEmpty(probe); i++)
            {
                candidates.Add(Path.Combine(probe, "Lex Junior Player", "LexJuniorPlayer", "LexJuniorPlayer", "bin", "Release", "net8.0-windows", "LexJuniorPlayer.exe"));
                candidates.Add(Path.Combine(probe, "Lex Junior Player", "LexJuniorPlayer", "App", "LexJuniorPlayer.exe"));
                candidates.Add(Path.Combine(probe, "LexJuniorPlayer", "App", "LexJuniorPlayer.exe"));
                probe = Path.GetDirectoryName(probe);
            }
        }
        catch { }

        foreach (var path in candidates)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                return path;
        }

        return null;
    }

    public static string? FindLocalPlayerSetupExe()
    {
        var baseDir = AppContext.BaseDirectory;
        var updatesDir = Path.Combine(InstallerService.UserDataDir, "Updates", "LexJuniorPlayerSetup.exe");
        var candidates = new List<string>
        {
            Path.Combine(baseDir, "LexJuniorPlayerSetup.exe"),
            updatesDir
        };

        try
        {
            var probe = baseDir;
            for (int i = 0; i < 6 && !string.IsNullOrEmpty(probe); i++)
            {
                candidates.Add(Path.Combine(probe, "dist", "LexJuniorPlayerSetup.exe"));
                probe = Path.GetDirectoryName(probe);
            }
        }
        catch { }

        foreach (var path in candidates)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                return path;
        }

        return null;
    }

    public static bool IsPlayerInstalled() => FindInstalledPlayerExe() != null;

    public static bool TryLaunchInstalledPlayer(out string? error)
    {
        error = null;
        var exe = FindInstalledPlayerExe();
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            error = "Lex Junior Player no está instalado todavía.";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? InstalledPlayerDir,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryLaunchLocalSetup(out string? setupPath)
    {
        setupPath = FindLocalPlayerSetupExe();
        if (string.IsNullOrWhiteSpace(setupPath) || !File.Exists(setupPath))
            return false;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = setupPath,
                Arguments = "--install",
                WorkingDirectory = Path.GetDirectoryName(setupPath) ?? InstalledPlayerDir,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<string> DownloadSetupInstallerAsync(
        IProgress<(int Percent, string Status)> progress,
        CancellationToken ct = default)
    {
        var updatesDir = Path.Combine(InstallerService.UserDataDir, "Updates");
        Directory.CreateDirectory(updatesDir);
        var finalPath = Path.Combine(updatesDir, "LexJuniorPlayerSetup.exe");
        var partPath = Path.Combine(updatesDir, $"LexJuniorPlayerSetup_{Guid.NewGuid():N}.part");

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "LexJuniorBrowser-PlayerFetch/1.0");

        progress.Report((5, "Conectando con el lanzamiento oficial en GitHub..."));
        using var resp = await client.GetAsync(PlayerSetupDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        long total = resp.Content.Headers.ContentLength ?? 0;
        await using (var net = await resp.Content.ReadAsStreamAsync(ct))
        await using (var fs = File.Create(partPath))
        {
            var buf = new byte[65536];
            long received = 0;
            int read;
            while ((read = await net.ReadAsync(buf, ct)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, read), ct);
                received += read;
                if (total > 0)
                {
                    int pct = 5 + (int)Math.Clamp(received * 90.0 / total, 0, 90);
                    progress.Report((pct, $"Descargando LexJuniorPlayerSetup.exe: {received / (1024.0 * 1024.0):F1} / {total / (1024.0 * 1024.0):F1} MB..."));
                }
            }
        }

        if (File.Exists(finalPath))
            File.Delete(finalPath);
        File.Move(partPath, finalPath);

        progress.Report((100, "Instalador descargado. Abriendo asistente inteligente..."));
        return finalPath;
    }
}
