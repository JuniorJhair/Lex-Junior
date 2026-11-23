using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using LexJuniorPlayer.Models;

namespace LexJuniorPlayer.Services;

public sealed class AppLibraryStore
{
    public const int CurrentTuningVersion = 3;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly object IoLock = new();

    public string DataDirectory { get; }
    public string IconsDirectory { get; }
    public string SettingsPath { get; }
    public string LibraryPath { get; }
    public string UncleanFlagPath { get; }

    public AppLibraryStore(string? customDataDir = null)
    {
        DataDirectory = customDataDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LexJuniorPlayer"
        );
        IconsDirectory = Path.Combine(DataDirectory, "icons");
        SettingsPath = Path.Combine(DataDirectory, "settings.json");
        LibraryPath = Path.Combine(DataDirectory, "library.json");
        UncleanFlagPath = Path.Combine(DataDirectory, "unclean.flag");
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(IconsDirectory);
    }

    public EngineSettings LoadSettings() =>
        ReadWithBackup<EngineSettings>(SettingsPath, JsonOpts) ?? new EngineSettings();

    public void SaveSettings(EngineSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOpts);
        WriteAtomic(SettingsPath, json);
    }

    public static bool ApplyOneTimeHostTuning(EngineSettings s, long totalHostMemoryBytes, int logicalProcessors)
    {
        if (s.PerformanceTunedVersion >= CurrentTuningVersion) return false;
        s.RamMb = totalHostMemoryBytes >= 7_000_000_000L ? 2048 : 1536;
        s.CpuCores = logicalProcessors >= 6 ? 4 : 2;
        s.PerformanceTunedVersion = CurrentTuningVersion;
        return true;
    }

    public List<InstalledAppInfo> LoadApps() =>
        ReadWithBackup<List<InstalledAppInfo>>(LibraryPath, JsonOpts) ?? new List<InstalledAppInfo>();

    public void SaveApps(IReadOnlyCollection<InstalledAppInfo> apps)
    {
        var json = JsonSerializer.Serialize(apps, JsonOpts);
        WriteAtomic(LibraryPath, json);
    }

    private static void WriteAtomic(string path, string content)
    {
        lock (IoLock)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
            else File.Move(tmp, path);
        }
    }

    private static T? ReadWithBackup<T>(string path, JsonSerializerOptions opts) where T : class
    {
        lock (IoLock)
        {
            foreach (var candidate in new[] { path, path + ".bak" })
            {
                try
                {
                    if (!File.Exists(candidate)) continue;
                    var v = JsonSerializer.Deserialize<T>(File.ReadAllText(candidate), opts);
                    if (v != null) return v;
                }
                catch { }
            }
            if (File.Exists(path))
            {
                try { File.Copy(path, $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true); } catch { }
            }
            return null;
        }
    }
}
