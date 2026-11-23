using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using LexJuniorPlayer.Models;
using LexJuniorPlayer.Services;

void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("ASSERTION FAILED: " + message);
}

// 1. Test aapt2 badging parser with Magma-like output (both MAIN and LEANBACK TV activity + ABIs)
var sampleBadging = """
package: name='com.magmaplayer' versionCode='10' versionName='1.0.9' platformBuildVersionName='14'
application-label:'Magma Player'
application-label-es:'Magma Player'
application-icon-160:'res/mipmap-mdpi-v4/icon_launcher.png'
application-icon-480:'res/mipmap-xxhdpi-v4/icon_launcher.png'
application: label='Magma Player' icon='res/mipmap-xxhdpi-v4/icon_launcher.png'
launchable-activity: name='com.magmaplayer.MainActivity' label='' icon=''
leanback-launchable-activity: name='com.magmaplayer.TVActivity' label='' icon=''
native-code: 'armeabi-v7a' 'arm64-v8a' 'x86_64'
""";

var meta = ApkInspector.ParseAapt2Badging(sampleBadging, @"C:\fake\Magma.apk");
Assert(meta.PackageName == "com.magmaplayer", "PackageName");
Assert(meta.VersionCode == 10, "VersionCode");
Assert(meta.VersionName == "1.0.9", "VersionName");
Assert(meta.AppName == "Magma Player", "AppName");
Assert(meta.LaunchActivity == "com.magmaplayer.MainActivity", "LaunchActivity");
Assert(meta.TvActivity == "com.magmaplayer.TVActivity", "TvActivity");
Assert(meta.IconZipPath == "res/mipmap-xxhdpi-v4/icon_launcher.png", "IconZipPath highest DPI");
Assert(meta.SupportedAbis.Contains("x86_64") && meta.SupportedAbis.Contains("arm64-v8a"), "ABIs");
Console.WriteLine("PASS: ParseAapt2Badging extracts package, version, TV activity, icon, and ABIs");

// 2. Test adaptive XML icon fallback extraction from an APK zip
var tempDir = Path.Combine(Path.GetTempPath(), "lex_test_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
try
{
    var fakeApk = Path.Combine(tempDir, "test.apk");
    var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    using (var zip = ZipFile.Open(fakeApk, ZipArchiveMode.Create))
    {
        var xmlEntry = zip.CreateEntry("res/mipmap-anydpi-v26/ic_launcher.xml");
        using (var s = xmlEntry.Open()) s.Write(new byte[] { 1, 2, 3 });

        var pngEntry = zip.CreateEntry("res/mipmap-xxxhdpi-v4/ic_launcher.png");
        using (var s = pngEntry.Open()) s.Write(pngBytes);
    }

    var adaptiveMeta = new ApkMetadata(fakeApk, "com.example.xuper", "Xuper TV", "2.1.0", 21, "", "", "", "arm64-v8a");
    var extracted = ApkInspector.ExtractBestIconPng(adaptiveMeta, Path.Combine(tempDir, "icons"));
    Assert(File.Exists(extracted), "Extracted icon exists");
    Assert(File.ReadAllBytes(extracted).Length == pngBytes.Length, "Extracted PNG bytes match");
    Console.WriteLine("PASS: ExtractBestIconPng extracts PNG icon even with adaptive XML fallback");

    // 3. Test AppLibraryStore persistence and defaults
    var defaults = new EngineSettings();
    Assert(!defaults.PreferTvActivity, "Default PreferTvActivity is false so mouse/touch clicks work");
    Assert(defaults.HideEmulatorToolbar, "Default HideEmulatorToolbar is true to keep view clean");
    Assert(!defaults.StartFullscreenOnLaunch, "Default StartFullscreenOnLaunch is false unless enabled");
    var store = new AppLibraryStore(Path.Combine(tempDir, "appdata"));
    var settings = new EngineSettings { ResolutionWidth = 1920, ResolutionHeight = 1080, RamMb = 3072, HideEmulatorToolbar = false, StartFullscreenOnLaunch = true };
    store.SaveSettings(settings);
    var loadedSettings = store.LoadSettings();
    Assert(loadedSettings.ResolutionWidth == 1920 && loadedSettings.RamMb == 3072, "Settings persistence");
    Assert(!loadedSettings.HideEmulatorToolbar && loadedSettings.StartFullscreenOnLaunch, "Window preferences persistence");

    store.SaveApps(new[] { new InstalledAppInfo { PackageName = "com.example.xuper", AppName = "Xuper TV", VersionName = "2.1.0", VersionCode = 21 } });
    var loadedApps = store.LoadApps();
    Assert(loadedApps.Count == 1 && loadedApps[0].AppName == "Xuper TV", "Apps persistence");
    Console.WriteLine("PASS: AppLibraryStore persists settings and installed APK list");

    // 4. Test AvdProvisioner generates clean non-PlayStore landscape config.ini
    var fakeSdk = new SdkPaths(@"C:\Sdk", @"C:\Sdk\platform-tools\adb.exe", @"C:\Sdk\emulator\emulator.exe", "", @"system-images\android-30\google_apis\x86_64\", true, Path.Combine(tempDir, "avd"));
    var created = AvdProvisioner.EnsureCleanAvd(fakeSdk, loadedSettings, out var changed);
    Assert(created && changed, "AVD provisioned cleanly");
    var ini = File.ReadAllText(Path.Combine(fakeSdk.AvdRoot, "LexJuniorPlayer.ini"));
    Assert(ini.Contains("target=android-30"), "Target API 30 ARM+x86_64 configured");
    var cfg = File.ReadAllText(Path.Combine(fakeSdk.AvdRoot, "LexJuniorPlayer.avd", "config.ini"));
    Assert(cfg.Contains("PlayStore.enabled = false"), "PlayStore disabled");
    Assert(cfg.Contains("showDeviceFrame = no"), "No phone bezel");
    Assert(cfg.Contains("hw.initialOrientation = landscape"), "Landscape mode");
    Assert(cfg.Contains("hw.lcd.width = 1920") && cfg.Contains("hw.lcd.height = 1080"), "Resolution applied");
    Assert(cfg.Contains("hw.gpu.mode = auto") && cfg.Contains("hw.gpu.enabled = yes"), "Auto GPU acceleration enabled by default");
    Console.WriteLine("PASS: AvdProvisioner builds clean ad-free landscape config.ini");

    // 5. Test EmulatorWindowController predicates and style transforms
    Assert(EmulatorWindowController.IsMainEmulatorWindowTitle("Android Emulator - LexJuniorPlayer:5554", "LexJuniorPlayer"), "Matches main Qt emulator window");
    Assert(!EmulatorWindowController.IsMainEmulatorWindowTitle("Android Emulator - Pixel_8:5554", "LexJuniorPlayer"), "Ignores other AVDs");
    Assert(EmulatorWindowController.IsSideToolbarCandidate("Qt653QWindowToolSaveBits", 65, 515), "Matches narrow Qt right-hand toolbar");
    Assert(!EmulatorWindowController.IsSideToolbarCandidate("Qt653QWindowToolSaveBits", 661, 30), "Ignores horizontal zoom tooltip");
    long initialStyle = 0x96C4_0000L;
    long fsStyle = EmulatorWindowController.ComputeBorderlessFullscreenStyle(initialStyle);
    Assert((fsStyle & 0x00C4_0000L) == 0 && (fsStyle & 0x1000_0000L) != 0, "Strips caption and frame cleanly for borderless fullscreen");
    long restoredStyle = EmulatorWindowController.ComputeRestoredWindowStyle(fsStyle);
    Assert((restoredStyle & 0x00C5_0000L) == 0x00C5_0000L, "Restores caption, thick frame, and maximize box");
    Console.WriteLine("PASS: EmulatorWindowController predicates and Win32 styles validated");

    // 6. Test one-time host hardware tuning logic (v3)
    var legacy = new EngineSettings { RamMb = 3072, CpuCores = 6, PerformanceTunedVersion = 2 };
    Assert(AppLibraryStore.ApplyOneTimeHostTuning(legacy, 16_000_000_000L, 12), "Migrates 16GB/12-thread v2 host to v3 once");
    Assert(legacy.RamMb == 2048 && legacy.CpuCores == 4 && legacy.PerformanceTunedVersion == AppLibraryStore.CurrentTuningVersion, "Tunes RAM to 2048 and cores to 4");
    legacy.RamMb = 3072;
    Assert(!AppLibraryStore.ApplyOneTimeHostTuning(legacy, 16_000_000_000L, 12), "Does not override user choice once at v3");
    Assert(legacy.RamMb == 3072, "User RAM preserved after v3 migration");
    var smallHost = new EngineSettings { RamMb = 2048, CpuCores = 4, PerformanceTunedVersion = 0 };
    Assert(AppLibraryStore.ApplyOneTimeHostTuning(smallHost, 4_000_000_000L, 4), "Tunes small host to v3 cleanly");
    Assert(smallHost.RamMb == 1536 && smallHost.CpuCores == 2 && smallHost.PerformanceTunedVersion == AppLibraryStore.CurrentTuningVersion, "Sets conservative 1536MB / 2 cores on <7GB / <6-thread host");
    Console.WriteLine("PASS: ApplyOneTimeHostTuning v3 tunes hosts cleanly without overriding user edits");

    // 7. Test F11 anti-bounce cooldown helper
    var t0 = new DateTime(2026, 11, 14, 19, 0, 0, DateTimeKind.Utc);
    Assert(EmulatorWindowController.ShouldAcceptHotkeyToggle(t0, DateTime.MinValue), "First F11 press always accepted");
    Assert(!EmulatorWindowController.ShouldAcceptHotkeyToggle(t0.AddMilliseconds(40), t0), "Rejects 40ms bounce/repeat");
    Assert(EmulatorWindowController.ShouldAcceptHotkeyToggle(t0.AddMilliseconds(260), t0), "Accepts intentional follow-up press after 250ms");
    Console.WriteLine("PASS: ShouldAcceptHotkeyToggle blocks bounce while keeping single-press responsive");

    // 8. Test STREAM_MUSIC max volume extraction
    var sampleAudioDump = """
        - STREAM_SYSTEM:
           Min: 0
           Max: 7
        - STREAM_MUSIC:
           Muted: false
           Min: 0
           Max: 15
           streamVolume:5
           Current: 2 (speaker): 15
        """;
    Assert(AndroidEngineService.ParseMaxMusicVolume(sampleAudioDump) == 15, "Extracts STREAM_MUSIC max volume 15");
    Assert(AndroidEngineService.ParseMaxMusicVolume("Max: 25") == 25, "Extracts custom max volume");
    Assert(AndroidEngineService.ParseMaxMusicVolume("") == 15, "Falls back safely to 15 when empty");
    Console.WriteLine("PASS: ParseMaxMusicVolume parses STREAM_MUSIC max index accurately");

    // 9. Test fullscreen overlay hysteresis & RectEquals
    var monRect = new EmulatorWindowController.Rect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    var topEnterCursor = new EmulatorWindowController.PointPx { X = 960, Y = 8 };
    var pillBodyCursor = new EmulatorWindowController.PointPx { X = 960, Y = 48 };
    var belowExitCursor = new EmulatorWindowController.PointPx { X = 960, Y = 120 };
    var offMonCursor = new EmulatorWindowController.PointPx { X = 2100, Y = 8 };

    Assert(EmulatorWindowController.ComputeFullscreenOverlayTargetVisibility(false, true, false, false, topEnterCursor, monRect), "Shows overlay when cursor enters top 14px band");
    Assert(!EmulatorWindowController.ComputeFullscreenOverlayTargetVisibility(false, true, false, false, pillBodyCursor, monRect), "Does not show hidden overlay from 48px down before reaching top edge");
    Assert(EmulatorWindowController.ComputeFullscreenOverlayTargetVisibility(true, true, false, false, pillBodyCursor, monRect), "Hysteresis keeps visible overlay shown across 48px band without blinking");
    Assert(!EmulatorWindowController.ComputeFullscreenOverlayTargetVisibility(true, true, false, false, belowExitCursor, monRect), "Hides visible overlay once cursor drops past exit band");
    Assert(!EmulatorWindowController.ComputeFullscreenOverlayTargetVisibility(true, false, true, true, topEnterCursor, monRect), "Hides overlay immediately when suite loses foreground");
    Assert(EmulatorWindowController.ComputeFullscreenOverlayTargetVisibility(false, true, false, true, belowExitCursor, monRect), "Keeps overlay visible while pointer hovers pill");
    Assert(!EmulatorWindowController.ComputeFullscreenOverlayTargetVisibility(true, true, false, false, offMonCursor, monRect), "Ignores cursor on another monitor");
    Assert(EmulatorWindowController.RectEquals(monRect, new EmulatorWindowController.Rect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 }), "RectEquals compares all 4 edges");
    Assert(!EmulatorWindowController.RectEquals(monRect, new EmulatorWindowController.Rect { Left = 0, Top = 0, Right = 1920, Bottom = 1081 }), "RectEquals detects 1px difference");
    Console.WriteLine("PASS: Fullscreen overlay hysteresis prevents top-edge Show/Hide thrashing");

    // 10. Verify QuickBoot recovery triggers, ADB offline parsing, and dead AVD lock cleanup
    var adbOut = "List of devices attached\nemulator-5554\toffline\nemulator-5556 device\n192.168.1.10:5555\tdevice\n";
    var parsed = AndroidEngineService.ParseAdbEmulatorDevices(adbOut);
    Assert(parsed.Count == 2, "Should parse only emulator-* entries");
    Assert(parsed[0].Serial == "emulator-5554" && parsed[0].State == "offline", "Should parse offline emulator");
    Assert(parsed[1].Serial == "emulator-5556" && parsed[1].State == "device", "Should parse online emulator");

    Assert(!AndroidEngineService.ShouldTriggerQuickBootRecovery(false, true, true, TimeSpan.FromSeconds(60)), "Cold boot must never re-trigger QuickBoot recovery");
    Assert(AndroidEngineService.ShouldTriggerQuickBootRecovery(true, true, false, TimeSpan.FromSeconds(1)), "Early QEMU exit on QuickBoot must trigger recovery");
    Assert(!AndroidEngineService.ShouldTriggerQuickBootRecovery(true, false, true, TimeSpan.FromSeconds(10)), "Offline < 15s gives ADB time to attach");
    Assert(AndroidEngineService.ShouldTriggerQuickBootRecovery(true, false, true, TimeSpan.FromSeconds(16)), "Stuck offline >= 15s on QuickBoot must trigger recovery");
    Assert(!AndroidEngineService.ShouldTriggerQuickBootRecovery(true, false, false, TimeSpan.FromSeconds(20)), "Healthy QuickBoot boot before 22s wait");
    Assert(AndroidEngineService.ShouldTriggerQuickBootRecovery(true, false, false, TimeSpan.FromSeconds(23)), "Unresponsive QuickBoot attach >= 22s must trigger recovery");

    var deadAvdDir = Path.Combine(tempDir, "DeadLockTest.avd");
    var lockDir = Path.Combine(deadAvdDir, "hardware-qemu.ini.lock");
    Directory.CreateDirectory(lockDir);
    File.WriteAllText(Path.Combine(lockDir, "pid"), "99999999");
    File.WriteAllText(Path.Combine(deadAvdDir, "multiinstance.lock"), "1");
    File.WriteAllText(Path.Combine(deadAvdDir, "read-snapshot.txt"), "default_boot");
    Assert(AvdProvisioner.TryGetAvdLockPid(deadAvdDir) == 99999999, "TryGetAvdLockPid parses pid");
    AvdProvisioner.CleanupDeadAvdLocks(deadAvdDir);
    Assert(!Directory.Exists(lockDir), "CleanupDeadAvdLocks removes stale hardware-qemu.ini.lock");
    Assert(!File.Exists(Path.Combine(deadAvdDir, "multiinstance.lock")), "CleanupDeadAvdLocks removes stale multiinstance.lock");
    Assert(!File.Exists(Path.Combine(deadAvdDir, "read-snapshot.txt")), "CleanupDeadAvdLocks removes stale read-snapshot.txt");

    var fakeAvdDir = AvdProvisioner.GetAvdDirectory(fakeSdk, "LexJuniorPlayer");
    var fakeSnapDir = Path.Combine(fakeAvdDir, "snapshots", "default_boot");
    Directory.CreateDirectory(fakeSnapDir);
    File.WriteAllText(Path.Combine(fakeSnapDir, "ram.img"), "corrupt");
    File.WriteAllText(Path.Combine(fakeAvdDir, "quickbootChoice.ini"), "saveOnExit=1");
    AvdProvisioner.InvalidateQuickBootSnapshot(fakeSdk, "LexJuniorPlayer");
    Assert(!Directory.Exists(fakeSnapDir), "InvalidateQuickBootSnapshot deletes broken default_boot directory");
    Assert(!File.Exists(Path.Combine(fakeAvdDir, "quickbootChoice.ini")), "InvalidateQuickBootSnapshot deletes quickbootChoice.ini");

    Assert(AvdProvisioner.MatchesAvdCommandLine(@"C:\Sdk\emulator\qemu\windows-x86_64\qemu-system-x86_64.exe -netdelay none -avd LexJuniorPlayer -gpu host", "LexJuniorPlayer"), "Matches unquoted QEMU -avd arg");
    Assert(AvdProvisioner.MatchesAvdCommandLine("emulator.exe -avd \"LexJuniorPlayer\" -no-boot-anim", "LexJuniorPlayer"), "Matches quoted emulator -avd arg");
    Assert(AvdProvisioner.MatchesAvdCommandLine("emulator.exe @LexJuniorPlayer", "LexJuniorPlayer"), "Matches @AVD syntax");
    Assert(!AvdProvisioner.MatchesAvdCommandLine("qemu-system-x86_64.exe -avd LexJuniorPlayer2 -gpu host", "LexJuniorPlayer"), "Rejects AVD prefix collision");
    Assert(!AvdProvisioner.MatchesAvdCommandLine("qemu-system-x86_64.exe -avd Pixel_8", "LexJuniorPlayer"), "Rejects different AVD");
    Assert(AndroidEngineService.IsPackageServiceReadyOutput(0, "package:/system/framework/framework-res.apk\n", ""), "Accepts healthy pm path android response");
    Assert(!AndroidEngineService.IsPackageServiceReadyOutput(20, "", "cmd: Can't find service: package"), "Rejects missing package binder service");
    Assert(!AndroidEngineService.IsPackageServiceReadyOutput(0, "", "cmd: Can't find service: package"), "Rejects zero exit with missing package service stderr");
    Console.WriteLine("PASS: QuickBoot recovery policy, ADB offline detection, and dead AVD lock cleanup verified");

    // 11. Test ComputeGuestResolution for 16:9, 16:10, and other aspect ratios
    Assert(EmulatorWindowController.ComputeGuestResolution(1920, 1080) == (1280, 720), "1920x1080 resolves to 1280x720");
    Assert(EmulatorWindowController.ComputeGuestResolution(1920, 1200) == (1216, 760), "1920x1200 resolves to 1216x760");
    Assert(EmulatorWindowController.ComputeGuestResolution(0, 0) == (1280, 720), "Invalid monitor size falls back to 1280x720");
    Console.WriteLine("PASS: ComputeGuestResolution computes multiple-of-8 guest dimensions accurately");

    // 12. Test EscapeForAdbInputText (spaces, single quotes, non-ASCII)
    var escSpace = AndroidEngineService.EscapeForAdbInputText("hola mundo", out var lossySpace);
    Assert(escSpace == "hola%smundo" && !lossySpace, "Escapes space as %s without loss");
    var escQuote = AndroidEngineService.EscapeForAdbInputText("it's ok", out var lossyQuote);
    Assert(escQuote == "it'\\''s%sok" && !lossyQuote, "Escapes single quote cleanly for POSIX sh");
    var escNonAscii = AndroidEngineService.EscapeForAdbInputText("café\r\n!", out var lossyNonAscii);
    Assert(escNonAscii == "caf!" && lossyNonAscii, "Strips non-ASCII and marks lossy=true");
    Console.WriteLine("PASS: EscapeForAdbInputText escapes spaces, quotes, and flags non-ASCII");

    // 13. Test ShouldScanWindowHandles across all decision branches
    var scanNow = new DateTime(2026, 11, 15, 10, 0, 10, DateTimeKind.Utc);
    Assert(!EmulatorWindowController.ShouldScanWindowHandles(true, true, true, false, scanNow, scanNow), "Stops scanning when main and side are found");
    Assert(!EmulatorWindowController.ShouldScanWindowHandles(true, true, false, true, scanNow, scanNow), "Stops scanning when main is found and side gave up");
    Assert(EmulatorWindowController.ShouldScanWindowHandles(true, false, false, false, scanNow, scanNow), "Allows forceScan when main missing");
    Assert(EmulatorWindowController.ShouldScanWindowHandles(true, true, false, false, scanNow, scanNow), "Allows forceScan when main found and side still pending");
    Assert(!EmulatorWindowController.ShouldScanWindowHandles(false, false, false, false, scanNow, scanNow.AddMilliseconds(-1500)), "Throttles idle scan < 2000ms");
    Assert(EmulatorWindowController.ShouldScanWindowHandles(false, false, false, false, scanNow, scanNow.AddMilliseconds(-2000)), "Permits idle scan >= 2000ms");
    Assert(!EmulatorWindowController.ShouldScanWindowHandles(false, true, false, false, scanNow, scanNow.AddMilliseconds(-4500)), "Throttles side scan < 5000ms");
    Assert(EmulatorWindowController.ShouldScanWindowHandles(false, true, false, false, scanNow, scanNow.AddMilliseconds(-5000)), "Permits side scan >= 5000ms");
    Console.WriteLine("PASS: ShouldScanWindowHandles covers all scan and throttle branches");

    // 14. Test atomic write, .bak recovery, and corrupt file quarantine in AppLibraryStore
    var atomicDir = Path.Combine(tempDir, "atomic_appdata");
    var atomicStore = new AppLibraryStore(atomicDir);
    atomicStore.SaveApps(new[] { new InstalledAppInfo { PackageName = "com.v1", AppName = "App V1" } });
    atomicStore.SaveApps(new[] { new InstalledAppInfo { PackageName = "com.v2", AppName = "App V2" } });
    Assert(File.Exists(atomicStore.LibraryPath + ".bak"), "SaveApps writes .bak backup on replace");
    File.WriteAllText(atomicStore.LibraryPath, "{broken_json!!!");
    var recoveredApps = atomicStore.LoadApps();
    Assert(recoveredApps.Count == 1 && recoveredApps[0].PackageName == "com.v1", "LoadApps recovers prior valid state from .bak");
    File.WriteAllText(atomicStore.LibraryPath + ".bak", "{also_broken!!!");
    var emptyAfterDoubleCorrupt = atomicStore.LoadApps();
    Assert(emptyAfterDoubleCorrupt.Count == 0, "Returns empty list when both primary and .bak are corrupt");
    Assert(Directory.GetFiles(atomicDir, "library.json.corrupt-*").Length == 1, "Quarantines corrupt primary file before resetting");

    atomicStore.SaveSettings(new EngineSettings { RamMb = 1536, AspectPreset = "16:10" });
    atomicStore.SaveSettings(new EngineSettings { RamMb = 2048, AspectPreset = "21:9" });
    File.WriteAllText(atomicStore.SettingsPath, "corrupt_settings");
    var recoveredSettings = atomicStore.LoadSettings();
    Assert(recoveredSettings.RamMb == 1536 && recoveredSettings.AspectPreset == "16:10", "LoadSettings recovers from .bak");
    Console.WriteLine("PASS: AppLibraryStore atomic writes, .bak recovery, and corrupt quarantine verified");

    // 15. Test RunCommandAsync timeout kills hung process cleanly under 6s
    var swTimeout = Stopwatch.StartNew();
    var hungRes = AndroidEngineService.RunCommandAsync("powershell", "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"", CancellationToken.None, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
    swTimeout.Stop();
    Assert(hungRes.ExitCode == -1 && hungRes.StdErr == "timeout", "Hung command returns ExitCode -1 and timeout stderr");
    Assert(swTimeout.Elapsed < TimeSpan.FromSeconds(6), $"Timeout completed promptly in {swTimeout.Elapsed.TotalSeconds:F2}s");
    var argListRes = AndroidEngineService.RunCommandAsync("cmd", new[] { "/c", "echo", "ok" }, CancellationToken.None, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    Assert(argListRes.ExitCode == 0 && argListRes.StdOut.Trim() == "ok", "ArgumentList overload executes cleanly");
    Console.WriteLine("PASS: RunCommandAsync timeout and ArgumentList overload verified");

    // 16. Test GPU fallback sequence, dynamic AVD target/GPU mode, and single-shell boot script
    var autoGpus = AndroidEngineService.ResolveGpuAttemptOrder("auto");
    Assert(autoGpus.Count == 3 && autoGpus[0] == "host" && autoGpus[1] == "angle_indirect" && autoGpus[2] == "swiftshader_indirect", "Auto GPU fallback order");
    var angleGpus = AndroidEngineService.ResolveGpuAttemptOrder("angle_indirect");
    Assert(angleGpus.Count == 2 && angleGpus[0] == "angle_indirect" && angleGpus[1] == "swiftshader_indirect", "ANGLE falls back to SwiftShader");
    var swGpus = AndroidEngineService.ResolveGpuAttemptOrder("software");
    Assert(swGpus.Count == 1 && swGpus[0] == "swiftshader_indirect", "Software normalizes to swiftshader_indirect");

    Assert(AvdProvisioner.GetTargetApiString(@"system-images\android-34\google_apis_playstore\x86_64\") == "android-34", "Extracts target API dynamically");
    var customSdk = new SdkPaths(@"C:\Sdk", @"C:\Sdk\platform-tools\adb.exe", @"C:\Sdk\emulator\emulator.exe", "", @"system-images\android-34\google_apis\x86_64\", true, Path.Combine(tempDir, "avd_custom"),
        ActiveSystemImage: new SystemImageInfo(@"system-images\android-34\google_apis\x86_64", 34, "google_apis", "Google APIs", "x86_64", new[] { "x86_64", "arm64-v8a" }, "Android 14", "1", false, true));
    var customCfg = AvdProvisioner.BuildConfigIni(customSdk, new EngineSettings { GpuMode = "angle_indirect" });
    Assert(customCfg.Contains("hw.gpu.mode = angle_indirect"), "Writes configured GPU mode to AVD config.ini");
    Assert(AndroidEngineService.BuildInGuestBootWaitShellScript(45).Contains("LEX_BOOT_READY"), "Builds single-shell boot wait loop");
    Console.WriteLine("PASS: GPU fallback order, dynamic AVD GPU config, and single-shell boot wait verified");

    // 17. Test ApkBundleExtractor ABI filtering & .xapk extraction + cleanup
    Assert(ApkBundleExtractor.IsSupportedPackageFile("app.xapk") && ApkBundleExtractor.IsSupportedPackageFile("app.apkm") && ApkBundleExtractor.IsSupportedPackageFile("app.apks"), "Accepts bundle extensions");
    var splitSelection = ApkBundleExtractor.FilterCompatibleBundleApks(
        new[] { @"C:\tmp\base.apk", @"C:\tmp\split_config.arm64_v8a.apk", @"C:\tmp\split_config.armeabi_v7a.apk", @"C:\tmp\split_config.es.apk" },
        new[] { "x86_64", "arm64-v8a", "armeabi-v7a" });
    Assert(splitSelection.Count == 3 && splitSelection.Any(p => p.EndsWith("base.apk")) && splitSelection.Any(p => p.EndsWith("arm64_v8a.apk")) && !splitSelection.Any(p => p.EndsWith("armeabi_v7a.apk")), "Selects only compatible ABI split + base + language splits");

    var sampleXapk = Path.Combine(tempDir, "sample.xapk");
    var innerBaseApk = Path.Combine(tempDir, "inner_base.apk");
    using (var zip = ZipFile.Open(innerBaseApk, ZipArchiveMode.Create))
    {
        var ico = zip.CreateEntry("res/mipmap-xxhdpi/ic_launcher.png");
        using var s = ico.Open();
        s.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
    }
    using (var zip = ZipFile.Open(sampleXapk, ZipArchiveMode.Create))
    {
        zip.CreateEntryFromFile(innerBaseApk, "base.apk");
        var manEntry = zip.CreateEntry("manifest.json");
        using var mw = new StreamWriter(manEntry.Open());
        mw.Write("{\"package_name\":\"com.stream.demo\",\"name\":\"Stream Demo\",\"version_name\":\"3.2.0\",\"version_code\":\"32\"}");
    }
    var plan = ApkBundleExtractor.PreparePackageAsync(sampleXapk, "", new[] { "x86_64" }).GetAwaiter().GetResult();
    Assert(plan != null && plan.IsSplitBundle && plan.PrimaryMetadata.PackageName == "com.stream.demo" && plan.PrimaryMetadata.AppName == "Stream Demo" && plan.PrimaryMetadata.VersionName == "3.2.0", "Parses XAPK manifest and base.apk");
    ApkBundleExtractor.CleanupTempDir(plan?.TempExtractDir);
    Assert(plan?.TempExtractDir == null || !Directory.Exists(plan.TempExtractDir), "Cleans up temp XAPK extraction folder");
    Console.WriteLine("PASS: ApkBundleExtractor split ABI selection, XAPK extraction, and cleanup verified");

    // 18. Test SdkLocator multi-image discovery & PrerequisiteService detection (no duplicate install)
    var mockSdk = Path.Combine(tempDir, "mock_sdk");
    Directory.CreateDirectory(Path.Combine(mockSdk, "platform-tools"));
    File.WriteAllText(Path.Combine(mockSdk, "platform-tools", "adb.exe"), "");
    File.WriteAllText(Path.Combine(mockSdk, "platform-tools", "source.properties"), "Pkg.Revision=35.0.2\n");
    Directory.CreateDirectory(Path.Combine(mockSdk, "emulator"));
    File.WriteAllText(Path.Combine(mockSdk, "emulator", "emulator.exe"), "");
    File.WriteAllText(Path.Combine(mockSdk, "emulator", "source.properties"), "Pkg.Revision=30.0.0\n"); // Outdated!
    Directory.CreateDirectory(Path.Combine(mockSdk, "build-tools", "34.0.0"));
    File.WriteAllText(Path.Combine(mockSdk, "build-tools", "34.0.0", "aapt2.exe"), "");
    File.WriteAllText(Path.Combine(mockSdk, "build-tools", "34.0.0", "source.properties"), "Pkg.Revision=34.0.0\n");
    var img36 = Path.Combine(mockSdk, "system-images", "android-36", "google_apis_playstore", "x86_64");
    Directory.CreateDirectory(img36);
    File.WriteAllText(Path.Combine(img36, "system.img"), "");
    File.WriteAllText(Path.Combine(img36, "build.prop"), "ro.product.cpu.abilist=x86_64,arm64-v8a\nro.build.version.sdk=36\n");

    var mockDiscovered = SdkLocator.Discover(mockSdk);
    Assert(mockDiscovered.HasSystemImage && mockDiscovered.AvailableSystemImages?.Count == 1 && mockDiscovered.AvailableSystemImages[0].ApiLevel == 36, "Discovers non-default installed system image");
    Assert(!mockDiscovered.AvailableSystemImages![0].SupportsArm32 && mockDiscovered.AvailableSystemImages[0].SupportsArm64, "Accurately detects 64-bit-only ARM translation");

    var ptCheck = PrerequisiteService.InspectPlatformTools(mockSdk);
    var emuCheck = PrerequisiteService.InspectEmulator(mockSdk);
    var btCheck = PrerequisiteService.InspectBuildTools(mockSdk);
    Assert(ptCheck.Status == PrereqStatus.Ready, "Ready platform-tools skipped from reinstall");
    Assert(emuCheck.Status == PrereqStatus.Outdated, "Flags outdated emulator version cleanly");
    Assert(btCheck.Status == PrereqStatus.Ready, "Ready build-tools skipped from reinstall");

    var accel6 = PrerequisiteService.ParseAccelCheckOutput(6, "accel:\n6\nHyper-V detected and Windows Hypervisor Platform is available.", "");
    Assert(accel6.Status == PrereqStatus.Warning, "Explains Hyper-V/WHPX feature requirement cleanly");
    var accel0 = PrerequisiteService.ParseAccelCheckOutput(0, "accel:\n0\nHAXM version 7.8.0 is operational\naccel", "");
    Assert(accel0.Status == PrereqStatus.Ready, "Detects operational hardware acceleration");

    var sampleRepoXml = """
    <sdk:sdk-repository xmlns:sdk="http://schemas.android.com/sdk/android/repository/01">
      <license id="android-sdk-license">Apache/SDK</license>
      <remotePackage path="platform-tools">
        <revision><major>35</major><minor>0</minor><micro>2</micro></revision>
        <display-name>Android SDK Platform-Tools</display-name>
        <archives><archive><complete><size>8000000</size><checksum>abc123</checksum><url>platform-tools_r35.0.2-windows.zip</url></complete><host-os>windows</host-os></archive></archives>
      </remotePackage>
    </sdk:sdk-repository>
    """;
    var parsedRepo = PrerequisiteService.ParseRepositoryXml(sampleRepoXml, PrerequisiteService.RepoBaseUrl, new HashSet<string> { "platform-tools" });
    Assert(parsedRepo.Count == 1 && parsedRepo[0].Url == "https://dl.google.com/android/repository/platform-tools_r35.0.2-windows.zip" && parsedRepo[0].Sha1Hex == "abc123", "Parses Google SDK XML URL and SHA-1 cleanly");
    Console.WriteLine("PASS: SdkLocator multi-image discovery, PrerequisiteService version checks, and repo XML parser verified");
}
finally
{
    Directory.Delete(tempDir, true);
}
