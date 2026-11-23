using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LexJuniorPlayer.Models;

namespace LexJuniorPlayer.Services;

public sealed record AdbDeviceEntry(string Serial, string State);

public sealed class AndroidEngineService
{
    private static readonly TimeSpan AdbDefaultTimeout = TimeSpan.FromSeconds(15);

    private SdkPaths _sdk;
    private readonly AppLibraryStore _store;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _backgroundCompiles = new(StringComparer.OrdinalIgnoreCase);
    private string? _cachedSerial;
    private bool _forceColdBootOnNextStart;
    private bool _accelChecked;

    public string? CachedSerial => _cachedSerial;
    public string? LastAccelerationNotice { get; private set; }
    public string? LastGpuUsed { get; private set; }

    public event Action<string>? BackgroundStatusUpdated;

    public void RequestColdBootOnNextStart() => _forceColdBootOnNextStart = true;

    public void UpdateSdk(SdkPaths sdk)
    {
        _sdk = sdk;
        _accelChecked = false;
    }

    public AndroidEngineService(SdkPaths sdk, AppLibraryStore store)
    {
        _sdk = sdk;
        _store = store;
    }

    public static IReadOnlyList<string> ResolveGpuAttemptOrder(string? preferredGpuMode)
    {
        var normalized = (preferredGpuMode ?? "auto").Trim().ToLowerInvariant();
        return normalized switch
        {
            "swiftshader_indirect" or "software" => new[] { "swiftshader_indirect" },
            "angle_indirect" => new[] { "angle_indirect", "swiftshader_indirect" },
            _ => new[] { "host", "angle_indirect", "swiftshader_indirect" }
        };
    }

    public static List<AdbDeviceEntry> ParseAdbEmulatorDevices(string adbDevicesOutput)
    {
        var list = new List<AdbDeviceEntry>();
        foreach (var rawLine in adbDevicesOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                list.Add(new AdbDeviceEntry(parts[0], parts[1]));
            }
        }
        return list;
    }

    public static bool ShouldTriggerQuickBootRecovery(
        bool attemptUsedQuickBoot,
        bool emuProcessExited,
        bool sawOfflineDevice,
        TimeSpan elapsed,
        int offlineRecoverySeconds = 15,
        int quickBootAttachTimeoutSeconds = 22)
    {
        if (!attemptUsedQuickBoot)
            return false;

        if (emuProcessExited)
            return true;

        if (sawOfflineDevice && elapsed.TotalSeconds >= offlineRecoverySeconds)
            return true;

        return elapsed.TotalSeconds >= quickBootAttachTimeoutSeconds;
    }

    public async Task<string?> FindRunningAvdSerialAsync(string avdName, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_sdk.AdbExe)) return null;

        var res = await RunCommandAsync(_sdk.AdbExe, "devices", ct);
        if (res.ExitCode != 0) return null;

        var onlineSerials = ParseAdbEmulatorDevices(res.StdOut)
            .Where(d => string.Equals(d.State, "device", StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Serial)
            .ToList();

        foreach (var serial in onlineSerials)
        {
            var avdCheck = await RunCommandAsync(_sdk.AdbExe, $"-s {serial} emu avd name", ct);
            var firstLine = avdCheck.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .FirstOrDefault(s => !string.Equals(s, "OK", StringComparison.OrdinalIgnoreCase));
            if (string.Equals(firstLine, avdName, StringComparison.OrdinalIgnoreCase))
            {
                return serial;
            }
        }

        return null;
    }

    public async Task<string> EnsureEngineRunningAsync(
        EngineSettings settings,
        Action<string> reportProgress,
        bool forceColdBoot = false,
        CancellationToken ct = default)
    {
        var effectiveColdBoot = forceColdBoot || _forceColdBootOnNextStart;
        if (!effectiveColdBoot && _cachedSerial is { } cached)
        {
            var r = await RunCommandAsync(_sdk.AdbExe, $"-s {cached} shell getprop sys.boot_completed", ct, TimeSpan.FromSeconds(5));
            if (r.ExitCode == 0 && r.StdOut.Trim() == "1") return cached;
        }
        _cachedSerial = null;
        var serial = await EnsureEngineRunningCoreAsync(settings, reportProgress, effectiveColdBoot, ct);
        _forceColdBootOnNextStart = false;
        _cachedSerial = serial;
        return serial;
    }

    private async Task EnsureAccelCheckedOnceAsync(Action<string> reportProgress, CancellationToken ct)
    {
        if (_accelChecked || string.IsNullOrEmpty(_sdk.EmulatorExe))
            return;

        _accelChecked = true;
        var (status, _, summary) = await PrerequisiteService.CheckHardwareAccelerationAsync(_sdk.EmulatorExe, ct);
        if (status == PrereqStatus.Warning)
        {
            LastAccelerationNotice = summary;
            reportProgress($"⚠ {summary}");
        }
        else
        {
            LastAccelerationNotice = null;
        }
    }

    private async Task<string> EnsureEngineRunningCoreAsync(
        EngineSettings settings,
        Action<string> reportProgress,
        bool forceColdBoot = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_sdk.EmulatorExe) || string.IsNullOrEmpty(_sdk.AdbExe))
            throw new InvalidOperationException("No se encontró emulator.exe o adb.exe en el Android SDK.");

        if (!AvdProvisioner.EnsureCleanAvd(_sdk, settings, out var hardwareChanged))
            throw new InvalidOperationException("No hay ninguna imagen del sistema Android instalada en el SDK.");

        await EnsureAccelCheckedOnceAsync(reportProgress, ct);

        var avdDir = AvdProvisioner.GetAvdDirectory(_sdk, settings.AvdName);
        var existing = await FindRunningAvdSerialAsync(settings.AvdName, ct);
        bool existingUnhealthy = false;
        if (existing != null)
        {
            if (await WaitForBootAsync(existing, reportProgress, allowQuickBootFailureReturn: true, ct))
            {
                return existing;
            }
            existingUnhealthy = true;
            TerminateAvdProcesses(null, avdDir);
            AvdProvisioner.CleanupDeadAvdLocks(avdDir);
            AvdProvisioner.InvalidateQuickBootSnapshot(_sdk, settings.AvdName);
        }

        var recoveredFromStuckInstance = existingUnhealthy
            ? (OnlineSerial: (string?)null, ShouldForceColdBoot: true)
            : await WaitOrClearExistingAvdInstanceAsync(avdDir, settings, reportProgress, ct);
        if (recoveredFromStuckInstance.OnlineSerial != null)
        {
            if (await WaitForBootAsync(recoveredFromStuckInstance.OnlineSerial, reportProgress, allowQuickBootFailureReturn: true, ct))
            {
                return recoveredFromStuckInstance.OnlineSerial;
            }
            TerminateAvdProcesses(null, avdDir);
            AvdProvisioner.CleanupDeadAvdLocks(avdDir);
            AvdProvisioner.InvalidateQuickBootSnapshot(_sdk, settings.AvdName);
            recoveredFromStuckInstance = (null, true);
        }

        bool useColdBoot = forceColdBoot || hardwareChanged || !settings.QuickBootEnabled || recoveredFromStuckInstance.ShouldForceColdBoot;
        var gpuCandidates = ResolveGpuAttemptOrder(settings.GpuMode);
        Exception? lastFailure = null;

        foreach (var gpuMode in gpuCandidates)
        {
            bool gpuIsFallback = !string.Equals(gpuMode, gpuCandidates[0], StringComparison.OrdinalIgnoreCase);
            bool attemptColdBoot = useColdBoot || gpuIsFallback;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                AvdProvisioner.CleanupDeadAvdLocks(avdDir);
                var tryQuickBoot = !attemptColdBoot && attempt == 0;

                if (gpuIsFallback)
                {
                    reportProgress($"GPU anterior no compatible; reintentando con -gpu {gpuMode}...");
                }
                else
                {
                    reportProgress(tryQuickBoot
                        ? $"Iniciando motor Lex Junior Player (-gpu {gpuMode})..."
                        : $"Iniciando motor limpio (arranque completo, -gpu {gpuMode})...");
                }

                var args = new StringBuilder($"-avd {settings.AvdName} -gpu {gpuMode} -no-boot-anim -no-metrics");
                if (!tryQuickBoot)
                {
                    args.Append(" -no-snapshot-load");
                }

                var psi = new ProcessStartInfo(_sdk.EmulatorExe, args.ToString())
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(_sdk.EmulatorExe) ?? string.Empty
                };

                using var emuProc = Process.Start(psi)
                    ?? throw new InvalidOperationException("No se pudo iniciar emulator.exe.");

                var startedUtc = DateTime.UtcNow;
                var hardDeadline = startedUtc.AddSeconds(120);
                string? serial = null;
                var sawOffline = false;
                var sentReconnect = false;
                var killedHungAdb = false;
                var needsQuickBootRecovery = false;
                var gpuCrashDetected = false;

                while (DateTime.UtcNow < hardDeadline)
                {
                    ct.ThrowIfCancellationRequested();

                    var devicesRes = await RunCommandAsync(_sdk.AdbExe, "devices", ct);
                    if (devicesRes.ExitCode == -1 && !killedHungAdb)
                    {
                        killedHungAdb = true;
                        _ = await RunCommandAsync(_sdk.AdbExe, "kill-server", ct, TimeSpan.FromSeconds(10));
                        continue;
                    }
                    var emuDevices = ParseAdbEmulatorDevices(devicesRes.StdOut);
                    if (emuDevices.Any(d => string.Equals(d.State, "offline", StringComparison.OrdinalIgnoreCase)))
                    {
                        sawOffline = true;
                    }

                    if (emuDevices.Any(d => string.Equals(d.State, "device", StringComparison.OrdinalIgnoreCase)))
                    {
                        serial = await FindRunningAvdSerialAsync(settings.AvdName, ct);
                        if (serial != null) break;
                    }

                    var elapsed = DateTime.UtcNow - startedUtc;
                    if (sawOffline && !sentReconnect && elapsed.TotalSeconds >= 6)
                    {
                        sentReconnect = true;
                        _ = await RunCommandAsync(_sdk.AdbExe, "reconnect offline", ct);
                    }

                    bool exitedEarly = emuProc.HasExited && !HasAnyLiveAvdProcess(avdDir);
                    if (ShouldTriggerQuickBootRecovery(tryQuickBoot, exitedEarly, sawOffline, elapsed))
                    {
                        needsQuickBootRecovery = true;
                        break;
                    }

                    if (!tryQuickBoot && exitedEarly && elapsed.TotalSeconds >= 1)
                    {
                        gpuCrashDetected = true;
                        break;
                    }

                    await Task.Delay(900, ct);
                }

                if (serial != null)
                {
                    var bootedHealthy = await WaitForBootAsync(
                        serial,
                        reportProgress,
                        allowQuickBootFailureReturn: tryQuickBoot && attempt == 0,
                        ct);
                    if (bootedHealthy)
                    {
                        LastGpuUsed = gpuMode;
                        await ApplyLightweightTweaksAsync(serial, ct);
                        await EnsureMaxMediaVolumeAsync(serial, ct);
                        return serial;
                    }
                    needsQuickBootRecovery = true;
                }

                if (needsQuickBootRecovery && attempt == 0 && !attemptColdBoot)
                {
                    reportProgress("Snapshot anterior trabado; recuperando con arranque limpio...");
                    TerminateAvdProcesses(emuProc, avdDir);
                    AvdProvisioner.CleanupDeadAvdLocks(avdDir);
                    AvdProvisioner.InvalidateQuickBootSnapshot(_sdk, settings.AvdName);
                    attemptColdBoot = true;
                    continue;
                }

                TerminateAvdProcesses(emuProc, avdDir);
                lastFailure = gpuCrashDetected
                    ? new InvalidOperationException($"El motor Android se cerró con -gpu {gpuMode}.")
                    : new TimeoutException($"El motor Android tardó demasiado en responder por ADB (-gpu {gpuMode}).");
                break; // try next GPU fallback if available
            }
        }

        throw lastFailure ?? new TimeoutException("El motor Android no pudo iniciar con ningún modo de GPU.");
    }

    private async Task<(string? OnlineSerial, bool ShouldForceColdBoot)> WaitOrClearExistingAvdInstanceAsync(
        string avdDir,
        EngineSettings settings,
        Action<string> reportProgress,
        CancellationToken ct)
    {
        using var liveQemu = AvdProvisioner.TryGetLiveAvdQemuProcess(avdDir);
        if (liveQemu == null)
            return (null, false);

        reportProgress("Sincronizando con instancia previa del motor...");
        var deadline = DateTime.UtcNow.AddSeconds(16);
        var sentReconnect = false;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (liveQemu.HasExited)
                return (null, false);

            var online = await FindRunningAvdSerialAsync(settings.AvdName, ct);
            if (online != null)
                return (online, false);

            if (!sentReconnect)
            {
                var devRes = await RunCommandAsync(_sdk.AdbExe, "devices", ct);
                if (ParseAdbEmulatorDevices(devRes.StdOut).Any(d => string.Equals(d.State, "offline", StringComparison.OrdinalIgnoreCase)))
                {
                    sentReconnect = true;
                    _ = await RunCommandAsync(_sdk.AdbExe, "reconnect offline", ct);
                }
            }

            await Task.Delay(700, ct);
        }

        reportProgress("Reiniciando motor bloqueado con arranque limpio...");
        liveQemu.Dispose();
        TerminateAvdProcesses(null, avdDir);
        AvdProvisioner.CleanupDeadAvdLocks(avdDir);
        AvdProvisioner.InvalidateQuickBootSnapshot(_sdk, settings.AvdName);
        return (null, true);
    }

    public static bool HasAnyLiveAvdProcess(string avdDir) =>
        AvdProvisioner.IsAnyAvdHostProcessLive(avdDir);

    private static void TerminateAvdProcesses(Process? launcherProc, string avdDir)
    {
        try
        {
            using var qemuProc = AvdProvisioner.TryGetLiveAvdQemuProcess(avdDir);
            if (qemuProc != null && !qemuProc.HasExited)
            {
                qemuProc.Kill(entireProcessTree: true);
                qemuProc.WaitForExit(5000);
            }
        }
        catch { }

        try
        {
            if (launcherProc != null && !launcherProc.HasExited)
            {
                launcherProc.Kill(entireProcessTree: true);
                launcherProc.WaitForExit(3000);
            }
        }
        catch { }

        var waitDeadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < waitDeadline && HasAnyLiveAvdProcess(avdDir))
        {
            Thread.Sleep(150);
        }
    }

    public Task<InstalledAppInfo> InstallOrUpdateApkAsync(
        ApkMetadata meta,
        EngineSettings settings,
        Action<string> reportProgress,
        CancellationToken ct = default)
    {
        var singlePlan = new BundleInstallPlan(
            OriginalPath: meta.FilePath,
            TempExtractDir: null,
            ApkPathsToInstall: new[] { meta.FilePath },
            ObbSourceDir: null,
            PreExtractedIconPath: null,
            PrimaryMetadata: meta,
            IsSplitBundle: false);
        return InstallPackageBundleAsync(singlePlan, settings, reportProgress, ct);
    }

    public async Task<InstalledAppInfo> InstallPackageBundleAsync(
        BundleInstallPlan plan,
        EngineSettings settings,
        Action<string> reportProgress,
        CancellationToken ct = default)
    {
        try
        {
            var meta = plan.PrimaryMetadata;
            var serial = await EnsureEngineRunningAsync(settings, reportProgress, ct: ct);

            reportProgress(plan.IsSplitBundle
                ? $"Instalando paquete dividido {meta.AppName} ({meta.VersionName})..."
                : $"Instalando {meta.AppName} ({meta.VersionName})...");

            var adbArgs = new List<string> { "-s", serial };
            if (plan.ApkPathsToInstall.Count > 1)
            {
                adbArgs.Add("install-multiple");
            }
            else
            {
                adbArgs.Add("install");
            }
            adbArgs.Add("-r");
            adbArgs.Add("-d");
            adbArgs.Add("-g");
            foreach (var apkPath in plan.ApkPathsToInstall)
            {
                adbArgs.Add(apkPath);
            }

            var res = await RunCommandAsync(_sdk.AdbExe, adbArgs, ct, TimeSpan.FromSeconds(210));
            if (res.ExitCode != 0 && !res.StdOut.Contains("Success", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Falló la instalación ADB: {res.StdOut} {res.StdErr}".Trim());
            }

            if (!string.IsNullOrWhiteSpace(plan.ObbSourceDir) && Directory.Exists(plan.ObbSourceDir))
            {
                reportProgress($"Copiando datos OBB de {meta.AppName}...");
                try
                {
                    foreach (var obbPkgDir in Directory.GetDirectories(plan.ObbSourceDir))
                    {
                        await RunCommandAsync(
                            _sdk.AdbExe,
                            new[] { "-s", serial, "push", obbPkgDir, "/sdcard/Android/obb/" },
                            ct,
                            TimeSpan.FromSeconds(180));
                    }
                }
                catch { }
            }

            var iconPath = ApkInspector.ExtractBestIconPng(meta, _store.IconsDirectory);
            if (string.IsNullOrEmpty(iconPath) &&
                !string.IsNullOrWhiteSpace(plan.PreExtractedIconPath) &&
                File.Exists(plan.PreExtractedIconPath))
            {
                try
                {
                    Directory.CreateDirectory(_store.IconsDirectory);
                    var destIcon = Path.Combine(_store.IconsDirectory, $"{meta.PackageName}.png");
                    File.Copy(plan.PreExtractedIconPath, destIcon, overwrite: true);
                    iconPath = destIcon;
                }
                catch { }
            }

            var apps = _store.LoadApps();
            apps.RemoveAll(a => string.Equals(a.PackageName, meta.PackageName, StringComparison.OrdinalIgnoreCase));

            var info = new InstalledAppInfo
            {
                PackageName = meta.PackageName,
                AppName = meta.AppName,
                VersionName = meta.VersionName,
                VersionCode = meta.VersionCode,
                LaunchActivity = meta.LaunchActivity,
                TvActivity = meta.TvActivity,
                IconPath = iconPath,
                SupportedAbis = meta.SupportedAbis
            };
            apps.Insert(0, info);
            _store.SaveApps(apps);

            // Compile NATIVELY in the background so UI buttons are never locked waiting up to 5 min
            ScheduleBackgroundCompile(serial, meta.PackageName, meta.AppName);

            return info;
        }
        finally
        {
            ApkBundleExtractor.CleanupTempDir(plan.TempExtractDir);
        }
    }

    public void CancelAllBackgroundCompiles()
    {
        foreach (var kv in _backgroundCompiles.ToArray())
        {
            if (_backgroundCompiles.TryRemove(kv.Key, out var cts))
            {
                try { cts.Cancel(); cts.Dispose(); } catch { }
            }
        }
    }

    private void ScheduleBackgroundCompile(string serial, string packageName, string appName)
    {
        if (string.IsNullOrEmpty(_sdk.AdbExe) || string.IsNullOrWhiteSpace(packageName))
            return;

        if (_backgroundCompiles.TryRemove(packageName, out var oldCts))
        {
            try { oldCts.Cancel(); oldCts.Dispose(); } catch { }
        }

        var cts = new CancellationTokenSource();
        _backgroundCompiles[packageName] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                BackgroundStatusUpdated?.Invoke($"Optimizando {appName} en segundo plano...");
                await RunCommandAsync(
                    _sdk.AdbExe,
                    new[] { "-s", serial, "shell", "cmd", "package", "compile", "-m", "speed", packageName },
                    cts.Token,
                    TimeSpan.FromSeconds(240)).ConfigureAwait(false);
                BackgroundStatusUpdated?.Invoke($"✓ {appName} optimizada para streaming fluido.");
            }
            catch
            {
                // Non-critical or cancelled on exit/uninstall
            }
            finally
            {
                if (_backgroundCompiles.TryRemove(packageName, out var removed))
                {
                    try { removed.Dispose(); } catch { }
                }
            }
        });
    }

    public async Task LaunchAppAsync(
        InstalledAppInfo app,
        EngineSettings settings,
        Action<string> reportProgress,
        CancellationToken ct = default)
    {
        var serial = await EnsureEngineRunningAsync(settings, reportProgress, ct: ct);

        reportProgress($"Abriendo {app.AppName}...");
        string? targetComponent = null;

        if (settings.PreferTvActivity)
        {
            if (!string.IsNullOrWhiteSpace(app.TvActivity))
            {
                targetComponent = FormatComponent(app.PackageName, app.TvActivity);
            }
            else
            {
                targetComponent = await ResolveActivityViaAdbAsync(serial, app.PackageName, "android.intent.category.LEANBACK_LAUNCHER", ct);
            }
        }

        if (string.IsNullOrWhiteSpace(targetComponent))
        {
            if (!string.IsNullOrWhiteSpace(app.LaunchActivity))
            {
                targetComponent = FormatComponent(app.PackageName, app.LaunchActivity);
            }
            else
            {
                targetComponent = await ResolveActivityViaAdbAsync(serial, app.PackageName, "android.intent.category.LAUNCHER", ct);
            }
        }

        if (!string.IsNullOrWhiteSpace(targetComponent))
        {
            await RunCommandAsync(_sdk.AdbExe, $"-s {serial} shell am start -W -n {targetComponent}", ct);
        }
        else
        {
            await RunCommandAsync(_sdk.AdbExe, $"-s {serial} shell monkey -p {app.PackageName} -c android.intent.category.LAUNCHER 1", ct);
        }

        var apps = _store.LoadApps();
        var item = apps.FirstOrDefault(a => string.Equals(a.PackageName, app.PackageName, StringComparison.OrdinalIgnoreCase));
        if (item != null)
        {
            item.LastLaunchedUtc = DateTime.UtcNow;
            _store.SaveApps(apps);
        }
    }

    public Task SendKeyEventAsync(string serial, int keyCode, CancellationToken ct = default) =>
        RunCommandAsync(_sdk.AdbExe, $"-s {serial} shell input keyevent {keyCode}", ct);

    public static string EscapeForAdbInputText(string text, out bool lossy)
    {
        lossy = false;
        var sb = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            if (c == ' ') sb.Append("%s");
            else if (c == '\'') sb.Append("'\\''");
            else if (c == '\r' || c == '\n') { }
            else if (c < 0x20 || c > 0x7E) lossy = true;
            else sb.Append(c);
        }
        return sb.ToString();
    }

    public async Task<bool> PasteTextAsync(string serial, string text, CancellationToken ct = default)
    {
        bool lossy = false;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            for (int p = 0; p < lines[i].Length; p += 100)
            {
                var chunk = lines[i].Substring(p, Math.Min(100, lines[i].Length - p));
                var esc = EscapeForAdbInputText(chunk, out var l);
                lossy |= l;
                if (esc.Length == 0) continue;
                await RunCommandAsync(_sdk.AdbExe, new[] { "-s", serial, "shell", $"input text '{esc}'" }, ct, TimeSpan.FromSeconds(10));
            }
            if (i < lines.Length - 1)
                await RunCommandAsync(_sdk.AdbExe, new[] { "-s", serial, "shell", "input keyevent 66" }, ct, TimeSpan.FromSeconds(5));
        }
        return !lossy;
    }

    public async Task EnsureMaxMediaVolumeAsync(string serial, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_sdk.AdbExe) || string.IsNullOrWhiteSpace(serial))
            return;

        int maxVol = 15;
        try
        {
            var audioDump = await RunCommandAsync(
                _sdk.AdbExe,
                $"-s {serial} shell \"dumpsys audio | grep -A 8 'STREAM_MUSIC:'\"",
                ct);
            maxVol = ParseMaxMusicVolume(audioDump.StdOut, 15);
        }
        catch
        {
            maxVol = 15;
        }

        await RunCommandAsync(
            _sdk.AdbExe,
            $"-s {serial} shell \"cmd media_session volume --stream 3 --set {maxVol} && settings put system volume_music {maxVol} && settings put system volume_music_speaker {maxVol}\"",
            ct);
    }

    public static int ParseMaxMusicVolume(string dumpsysAudioOutput, int fallback = 15)
    {
        if (string.IsNullOrWhiteSpace(dumpsysAudioOutput))
            return fallback;

        var streamSection = dumpsysAudioOutput;
        var idx = dumpsysAudioOutput.IndexOf("STREAM_MUSIC", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            streamSection = dumpsysAudioOutput.Substring(idx, Math.Min(400, dumpsysAudioOutput.Length - idx));
        }

        var m = Regex.Match(streamSection, @"Max:\s*(\d+)", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var parsed) && parsed >= 5 && parsed <= 100)
        {
            return parsed;
        }

        return fallback;
    }

    public async Task UninstallAppAsync(
        string packageName,
        EngineSettings settings,
        Action<string> reportProgress,
        CancellationToken ct = default)
    {
        if (_backgroundCompiles.TryRemove(packageName, out var compileCts))
        {
            try { compileCts.Cancel(); compileCts.Dispose(); } catch { }
        }

        var serial = await EnsureEngineRunningAsync(settings, reportProgress, ct: ct);
        reportProgress($"Desinstalando {packageName}...");

        var res = await RunCommandAsync(_sdk.AdbExe, $"-s {serial} uninstall {packageName}", ct);
        if (res.ExitCode != 0 && !res.StdOut.Contains("Success", StringComparison.OrdinalIgnoreCase))
        {
            var checkPkg = await RunCommandAsync(_sdk.AdbExe, $"-s {serial} shell pm path {packageName}", ct);
            var serviceUnavailable = checkPkg.ExitCode != 0 ||
                                     checkPkg.StdErr.Contains("Can't find service", StringComparison.OrdinalIgnoreCase) ||
                                     checkPkg.StdOut.Contains("Can't find service", StringComparison.OrdinalIgnoreCase);
            if (serviceUnavailable || checkPkg.StdOut.Contains("package:", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"No se pudo desinstalar {packageName}: {res.StdOut} {res.StdErr}".Trim());
            }
        }

        var apps = _store.LoadApps();
        var target = apps.FirstOrDefault(a => string.Equals(a.PackageName, packageName, StringComparison.OrdinalIgnoreCase));
        if (target != null && !string.IsNullOrEmpty(target.IconPath) && File.Exists(target.IconPath))
        {
            try { File.Delete(target.IconPath); } catch { }
        }
        apps.RemoveAll(a => string.Equals(a.PackageName, packageName, StringComparison.OrdinalIgnoreCase));
        _store.SaveApps(apps);
    }

    public async Task<List<InstalledAppInfo>> SyncInstalledAppsWithRunningEngineAsync(
        string serial,
        CancellationToken ct = default)
    {
        var pmRes = await RunCommandAsync(_sdk.AdbExe, $"-s {serial} shell pm list packages -3 --show-versioncode", ct);
        if (pmRes.ExitCode != 0) return _store.LoadApps();

        var parsed = ParseThirdPartyPackages(pmRes.StdOut);
        var existing = _store.LoadApps();
        var updated = new List<InstalledAppInfo>();

        foreach (var (pkg, verCode) in parsed)
        {
            var found = existing.FirstOrDefault(a => string.Equals(a.PackageName, pkg, StringComparison.OrdinalIgnoreCase));
            if (found != null)
            {
                if (verCode > 0) found.VersionCode = verCode;
                updated.Add(found);
                continue;
            }

            var versionName = await QueryVersionNameAsync(serial, pkg, ct);
            updated.Add(new InstalledAppInfo
            {
                PackageName = pkg,
                AppName = PrettifyPackageName(pkg),
                VersionName = versionName,
                VersionCode = verCode
            });
        }

        updated = updated.OrderByDescending(a => a.LastLaunchedUtc ?? DateTime.MinValue)
                         .ThenBy(a => a.AppName)
                         .ToList();
        _store.SaveApps(updated);
        return updated;
    }

    public static List<(string PackageName, long VersionCode)> ParseThirdPartyPackages(string stdout)
    {
        var list = new List<(string, long)>();
        foreach (var rawLine in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("package:", StringComparison.OrdinalIgnoreCase)) continue;
            var body = line.Substring("package:".Length).Trim();
            var m = Regex.Match(body, @"^([^\s]+)(?:\s+versionCode:(\d+))?");
            if (!m.Success) continue;
            var pkg = m.Groups[1].Value;
            long.TryParse(m.Groups[2].Value, out var vc);
            list.Add((pkg, vc));
        }
        return list;
    }

    public async Task StopEngineAsync(string avdName, CancellationToken ct = default)
    {
        CancelAllBackgroundCompiles();
        _cachedSerial = null;
        var avdDir = AvdProvisioner.GetAvdDirectory(_sdk, avdName);
        using var trackedQemu = AvdProvisioner.TryGetLiveAvdQemuProcess(avdDir);
        var serial = await FindRunningAvdSerialAsync(avdName, ct);
        var gracefulExitTimeoutSec = 2;
        if (serial != null)
        {
            await RunCommandAsync(_sdk.AdbExe, $"-s {serial} emu kill", ct, TimeSpan.FromSeconds(10));
            gracefulExitTimeoutSec = 45;
        }

        var deadline = DateTime.UtcNow.AddSeconds(gracefulExitTimeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            bool trackedStillRunning = false;
            try { trackedStillRunning = trackedQemu != null && !trackedQemu.HasExited; } catch { }
            if (!trackedStillRunning && !HasAnyLiveAvdProcess(avdDir))
            {
                AvdProvisioner.CleanupDeadAvdLocks(avdDir);
                return;
            }
            await Task.Delay(250, ct);
        }

        try
        {
            if (trackedQemu != null && !trackedQemu.HasExited)
            {
                trackedQemu.Kill(entireProcessTree: true);
                trackedQemu.WaitForExit(5000);
            }
        }
        catch { }
        TerminateAvdProcesses(null, avdDir);
        AvdProvisioner.CleanupDeadAvdLocks(avdDir);
    }

    public static bool IsPackageServiceReadyOutput(int exitCode, string stdOut, string stdErr) =>
        exitCode == 0 &&
        stdOut.Contains("package:", StringComparison.OrdinalIgnoreCase) &&
        !stdOut.Contains("Can't find service", StringComparison.OrdinalIgnoreCase) &&
        !stdErr.Contains("Can't find service", StringComparison.OrdinalIgnoreCase);

    internal static string BuildInGuestBootWaitShellScript(int maxSeconds = 110) =>
        $"i=0; while [ $i -lt {Math.Max(10, maxSeconds)} ]; do " +
        "if [ \"$(getprop sys.boot_completed)\" = \"1\" ] && pm path android 2>/dev/null | grep -q \"package:\"; then " +
        "input keyevent 82 >/dev/null 2>&1; echo LEX_BOOT_READY; exit 0; " +
        "fi; sleep 1; i=$((i+1)); done; exit 1";

    private async Task<bool> WaitForBootAsync(
        string serial,
        Action<string> reportProgress,
        bool allowQuickBootFailureReturn = false,
        CancellationToken ct = default)
    {
        reportProgress("Esperando servicios del sistema Android limpio...");
        var timeoutSec = allowQuickBootFailureReturn ? 22 : 115;

        // Single adb shell loop avoids spawning ~100 separate adb.exe processes while booting
        var waitRes = await RunCommandAsync(
            _sdk.AdbExe,
            new[] { "-s", serial, "shell", BuildInGuestBootWaitShellScript(timeoutSec) },
            ct,
            TimeSpan.FromSeconds(timeoutSec + 5));

        if (waitRes.ExitCode == 0 && waitRes.StdOut.Contains("LEX_BOOT_READY", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (allowQuickBootFailureReturn)
        {
            return false;
        }

        throw new TimeoutException("Android no completó el arranque a tiempo.");
    }

    private async Task ApplyLightweightTweaksAsync(string serial, CancellationToken ct)
    {
        var tunedRes = await RunCommandAsync(_sdk.AdbExe, $"-s {serial} shell settings get global lex_junior_tuned_v2", ct, TimeSpan.FromSeconds(5));
        if (tunedRes.StdOut.Trim() == "1")
        {
            await RunCommandAsync(_sdk.AdbExe, $"-s {serial} shell input keyevent 82", ct);
            return;
        }

        var shellCmds = new List<string>
        {
            "settings put global window_animation_scale 0",
            "settings put global transition_animation_scale 0",
            "settings put global animator_duration_scale 0.25",
            "settings put global stay_on_while_plugged_in 3",
            "settings put secure immersive_mode_confirmations confirmed",
            "settings put system accelerometer_rotation 0",
            "settings put system user_rotation 0",
            "svc bluetooth disable",
            "svc nfc disable",
            "cmd media_session volume --stream 3 --set 15"
        };

        var bloatPackages = new[]
        {
            "com.android.vending",
            "com.android.dialer",
            "com.android.contacts",
            "com.android.camera2",
            "com.android.egg",
            "com.android.providers.calendar",
            "com.android.printspooler",
            "com.android.bips",
            "com.android.wallpaper.livepicker",
            "com.android.dreams.basic",
            "com.android.dreams.phototable",
            "com.android.cellbroadcastreceiver",
            "com.android.mms.service",
            "com.android.calllogbackup",
            "com.google.android.googlequicksearchbox",
            "com.google.android.ims",
            "com.google.android.tts",
            "com.google.android.feedback",
            "com.google.android.partnersetup",
            "com.google.android.backuptransport",
            "com.google.android.apps.turbo",
            "com.google.android.syncadapters.calendar",
            "com.google.android.syncadapters.contacts",
            "com.google.android.apps.messaging",
            "com.google.android.calendar",
            "com.google.android.deskclock",
            "com.google.android.apps.docs",
            "com.google.android.apps.maps",
            "com.google.android.apps.photos",
            "com.google.android.apps.wellbeing",
            "com.google.android.apps.youtube.music",
            "com.google.android.gm",
            "com.google.android.videos",
            "com.google.android.youtube",
            "com.google.android.projection.gearhead"
        };
        foreach (var pkg in bloatPackages)
        {
            shellCmds.Add($"pm disable-user --user 0 {pkg}");
        }

        await RunCommandAsync(
            _sdk.AdbExe,
            $"-s {serial} shell \"{string.Join("; ", shellCmds)}\"",
            ct,
            TimeSpan.FromSeconds(90));

        await RunCommandAsync(_sdk.AdbExe, $"-s {serial} shell settings put global lex_junior_tuned_v2 1", ct);
    }

    private async Task<string?> ResolveActivityViaAdbAsync(string serial, string packageName, string category, CancellationToken ct)
    {
        var res = await RunCommandAsync(
            _sdk.AdbExe,
            $"-s {serial} shell cmd package resolve-activity --brief -c {category} {packageName}",
            ct
        );
        if (res.ExitCode != 0) return null;
        var lines = res.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Contains('/'))
            .ToList();
        return lines.LastOrDefault();
    }

    private async Task<string> QueryVersionNameAsync(string serial, string packageName, CancellationToken ct)
    {
        var res = await RunCommandAsync(_sdk.AdbExe, $"-s {serial} shell dumpsys package {packageName}", ct);
        var m = Regex.Match(res.StdOut, @"versionName=([^\s\r\n]+)");
        return m.Success ? m.Groups[1].Value : string.Empty;
    }

    private static string FormatComponent(string packageName, string activityName)
    {
        if (activityName.Contains('/')) return activityName;
        return activityName.StartsWith('.') ? $"{packageName}/{activityName}" : $"{packageName}/{activityName}";
    }

    private static string PrettifyPackageName(string pkg)
    {
        var last = pkg.Split('.').LastOrDefault() ?? pkg;
        if (last.Length == 0) return pkg;
        return char.ToUpperInvariant(last[0]) + last.Substring(1);
    }

    internal static Task<(int ExitCode, string StdOut, string StdErr)> RunCommandAsync(
        string exe,
        string args,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        return RunProcessAsync(psi, ct, timeout);
    }

    internal static Task<(int ExitCode, string StdOut, string StdErr)> RunCommandAsync(
        string exe,
        IEnumerable<string> args,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        return RunProcessAsync(psi, ct, timeout);
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(
        ProcessStartInfo psi,
        CancellationToken ct,
        TimeSpan? timeout)
    {
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"No se pudo iniciar {psi.FileName}");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? AdbDefaultTimeout);
        var outTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
        var errTask = proc.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return (proc.ExitCode, await outTask.ConfigureAwait(false), await errTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            try { await Task.WhenAll(outTask, errTask).ConfigureAwait(false); } catch { }
            return (-1, string.Empty, "timeout");
        }
    }
}
