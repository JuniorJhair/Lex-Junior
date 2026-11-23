using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using LexJuniorPlayer.Models;

namespace LexJuniorPlayer.Services;

public static class AvdProvisioner
{
    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        IntPtr processInformation,
        int processInformationLength,
        out int returnLength);

    public static string GetAvdDirectory(SdkPaths sdk, string avdName) =>
        Path.Combine(sdk.AvdRoot, $"{avdName}.avd");

    public static string GetAvdNameFromDirectory(string avdDir) =>
        Path.GetFileNameWithoutExtension(Path.TrimEndingDirectorySeparator(avdDir));

    public static bool MatchesAvdCommandLine(string? commandLine, string avdName)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || string.IsNullOrWhiteSpace(avdName))
            return false;

        return Regex.IsMatch(
            commandLine,
            $@"(?:^|\s)(?:-avd(?:\s+|=)|@)""?{Regex.Escape(avdName)}""?(?:$|\s)",
            RegexOptions.IgnoreCase);
    }

    public static int? TryGetAvdLockPid(string avdDir)
    {
        try
        {
            var pidFile = Path.Combine(avdDir, "hardware-qemu.ini.lock", "pid");
            if (!File.Exists(pidFile)) return null;
            var raw = File.ReadAllText(pidFile).Trim();
            return int.TryParse(raw, out var pid) && pid > 0 ? pid : null;
        }
        catch
        {
            return null;
        }
    }

    public static Process? TryGetLiveAvdQemuProcess(string avdDir)
    {
        var pid = TryGetAvdLockPid(avdDir);
        if (pid.HasValue)
        {
            try
            {
                var proc = Process.GetProcessById(pid.Value);
                if (!proc.HasExited && proc.ProcessName.StartsWith("qemu-system", StringComparison.OrdinalIgnoreCase))
                {
                    return proc;
                }
                proc.Dispose();
            }
            catch { }
        }

        // Fallback when QEMU already deleted hardware-qemu.ini.lock\pid mid-shutdown (e.g. while flushing QuickBoot)
        var avdName = GetAvdNameFromDirectory(avdDir);
        if (string.IsNullOrWhiteSpace(avdName)) return null;

        return FindLiveAvdProcessByName(
            avdName,
            p => p.ProcessName.StartsWith("qemu-system", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsAnyAvdHostProcessLive(string avdDir)
    {
        using var qemu = TryGetLiveAvdQemuProcess(avdDir);
        if (qemu != null && !qemu.HasExited) return true;

        var avdName = GetAvdNameFromDirectory(avdDir);
        if (string.IsNullOrWhiteSpace(avdName)) return false;

        using var emu = FindLiveAvdProcessByName(
            avdName,
            p => string.Equals(p.ProcessName, "emulator", StringComparison.OrdinalIgnoreCase));
        return emu != null;
    }

    private static Process? FindLiveAvdProcessByName(string avdName, Func<Process, bool> predicate)
    {
        Process? matched = null;
        foreach (var proc in Process.GetProcesses())
        {
            if (matched == null)
            {
                try
                {
                    if (!proc.HasExited &&
                        predicate(proc) &&
                        MatchesAvdCommandLine(TryGetProcessCommandLine(proc), avdName))
                    {
                        matched = proc;
                        continue;
                    }
                }
                catch { }
            }
            proc.Dispose();
        }
        return matched;
    }

    private static string? TryGetProcessCommandLine(Process proc)
    {
        if (!OperatingSystem.IsWindows()) return null;
        const int ProcessCommandLineInformation = 60;
        IntPtr buf = IntPtr.Zero;
        try
        {
            int bufSize = 4096;
            buf = Marshal.AllocHGlobal(bufSize);
            int status = NtQueryInformationProcess(proc.Handle, ProcessCommandLineInformation, buf, bufSize, out int needed);
            if (status != 0 && needed > bufSize)
            {
                Marshal.FreeHGlobal(buf);
                bufSize = needed + 64;
                buf = Marshal.AllocHGlobal(bufSize);
                status = NtQueryInformationProcess(proc.Handle, ProcessCommandLineInformation, buf, bufSize, out needed);
            }
            if (status != 0) return null;

            var us = Marshal.PtrToStructure<UnicodeString>(buf);
            return us.Buffer != IntPtr.Zero && us.Length > 0
                ? Marshal.PtrToStringUni(us.Buffer, us.Length / 2)
                : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
        }
    }

    public static void CleanupDeadAvdLocks(string avdDir)
    {
        if (IsAnyAvdHostProcessLive(avdDir)) return;

        TryDeletePath(Path.Combine(avdDir, "hardware-qemu.ini.lock"));
        TryDeletePath(Path.Combine(avdDir, "multiinstance.lock"));
        TryDeletePath(Path.Combine(avdDir, "read-snapshot.txt"));
    }

    public static void InvalidateQuickBootSnapshot(SdkPaths sdk, string avdName)
    {
        var avdDir = GetAvdDirectory(sdk, avdName);
        TryDeletePath(Path.Combine(avdDir, "snapshots", "default_boot"), retries: 6);
        TryDeletePath(Path.Combine(avdDir, "quickbootChoice.ini"), retries: 2);
    }

    private static void TryDeletePath(string path, int retries = 1)
    {
        for (int attempt = 0; attempt < Math.Max(1, retries); attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                else if (File.Exists(path))
                    File.Delete(path);
                return;
            }
            catch
            {
                if (attempt + 1 < retries)
                {
                    System.Threading.Thread.Sleep(200);
                }
            }
        }
    }

    public static string GetTargetApiString(string? systemImageRelPath)
    {
        if (!string.IsNullOrWhiteSpace(systemImageRelPath))
        {
            var m = Regex.Match(systemImageRelPath, @"android-\d+", RegexOptions.IgnoreCase);
            if (m.Success)
                return m.Value.ToLowerInvariant();
        }
        return "android-30";
    }

    public static string NormalizeConfigGpuMode(string? gpuMode)
    {
        var m = (gpuMode ?? "auto").Trim().ToLowerInvariant();
        return m switch
        {
            "angle_indirect" => "angle_indirect",
            "swiftshader_indirect" => "swiftshader_indirect",
            "software" => "swiftshader_indirect",
            "auto" => "auto",
            _ => "host"
        };
    }

    public static bool EnsureCleanAvd(SdkPaths sdk, EngineSettings settings, out bool hardwareConfigChanged)
    {
        hardwareConfigChanged = false;
        if (!sdk.HasSystemImage) return false;

        Directory.CreateDirectory(sdk.AvdRoot);
        var avdIniPath = Path.Combine(sdk.AvdRoot, $"{settings.AvdName}.ini");
        var avdDir = GetAvdDirectory(sdk, settings.AvdName);
        var configPath = Path.Combine(avdDir, "config.ini");

        Directory.CreateDirectory(avdDir);

        var targetApi = GetTargetApiString(sdk.SystemImageRelPath);
        var avdIniContent = string.Join(Environment.NewLine, new[]
        {
            "avd.ini.encoding=UTF-8",
            $"path={avdDir}",
            $"path.rel=avd\\{settings.AvdName}.avd",
            $"target={targetApi}"
        }) + Environment.NewLine;

        File.WriteAllText(avdIniPath, avdIniContent, new UTF8Encoding(false));

        var newConfig = BuildConfigIni(sdk, settings);
        if (File.Exists(configPath))
        {
            var existing = File.ReadAllText(configPath);
            hardwareConfigChanged = !string.Equals(Normalize(existing), Normalize(newConfig), StringComparison.Ordinal);
        }
        else
        {
            hardwareConfigChanged = true;
        }

        File.WriteAllText(configPath, newConfig, new UTF8Encoding(false));
        return true;
    }

    public static string BuildConfigIni(SdkPaths sdk, EngineSettings settings)
    {
        var w = Math.Max(settings.ResolutionWidth, 960);
        var h = Math.Max(settings.ResolutionHeight, 540);
        var density = settings.Density > 0 ? settings.Density : 213;
        var ramMb = Math.Clamp(settings.RamMb, 1536, 8192);
        var cores = Math.Clamp(settings.CpuCores, 2, 8);
        var fastBoot = settings.QuickBootEnabled ? "yes" : "no";
        var gpuMode = NormalizeConfigGpuMode(settings.GpuMode);
        var primaryAbi = sdk.ActiveSystemImage?.PrimaryAbi ?? "x86_64";
        var tagId = sdk.ActiveSystemImage?.TagId ?? "google_apis";
        var tagDisplay = sdk.ActiveSystemImage?.TagDisplay ?? "Google APIs";
        var playStoreEnabled = string.Equals(tagId, "google_apis_playstore", StringComparison.OrdinalIgnoreCase) ? "true" : "false";

        return string.Join(Environment.NewLine, new[]
        {
            $"AvdId = {settings.AvdName}",
            $"PlayStore.enabled = {playStoreEnabled}",
            $"abi.type = {primaryAbi}",
            "avd.ini.displayname = Lex Junior Player",
            "avd.ini.encoding = UTF-8",
            "disk.dataPartition.size = 6442450944",
            "fastboot.chosenSnapshotFile = ",
            "fastboot.forceChosenSnapshotBoot = no",
            "fastboot.forceColdBoot = no",
            $"fastboot.forceFastBoot = {fastBoot}",
            "hw.accelerometer = no",
            "hw.arc = false",
            "hw.audioInput = no",
            "hw.audioOutput = yes",
            "hw.battery = yes",
            "hw.camera.back = none",
            "hw.camera.front = none",
            $"hw.cpu.arch = {primaryAbi}",
            $"hw.cpu.ncore = {cores}",
            "hw.dPad = yes",
            "hw.device.manufacturer = LexJunior",
            "hw.device.name = medium_tablet",
            "hw.gps = no",
            "hw.gpu.enabled = yes",
            $"hw.gpu.mode = {gpuMode}",
            "hw.gyroscope = no",
            "hw.initialOrientation = landscape",
            "hw.keyboard = yes",
            $"hw.lcd.density = {density}",
            $"hw.lcd.height = {h}",
            $"hw.lcd.width = {w}",
            "hw.mainKeys = no",
            $"hw.ramSize = {ramMb}",
            "hw.sdCard = yes",
            "hw.sensors.light = no",
            "hw.sensors.magnetic_field = no",
            "hw.sensors.orientation = no",
            "hw.sensors.pressure = no",
            "hw.sensors.proximity = no",
            "hw.trackBall = no",
            $"image.sysdir.1 = {sdk.SystemImageRelPath}",
            "runtime.network.latency = none",
            "runtime.network.speed = full",
            "sdcard.size = 256M",
            "showDeviceFrame = no",
            "skin.dynamic = yes",
            $"skin.name = {w}x{h}",
            "skin.path = _no_skin",
            $"tag.display = {tagDisplay}",
            $"tag.displaynames = {tagDisplay}",
            $"tag.id = {tagId}",
            $"tag.ids = {tagId}",
            "vm.heapSize = 256"
        }) + Environment.NewLine;
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Trim();
}
