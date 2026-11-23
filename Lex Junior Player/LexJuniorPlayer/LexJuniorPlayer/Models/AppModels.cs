using System;
using System.Collections.Generic;

namespace LexJuniorPlayer.Models;

public sealed record ApkMetadata(
    string FilePath,
    string PackageName,
    string AppName,
    string VersionName,
    long VersionCode,
    string LaunchActivity,
    string TvActivity,
    string IconZipPath,
    string SupportedAbis
);

public sealed record BundleInstallPlan(
    string OriginalPath,
    string? TempExtractDir,
    IReadOnlyList<string> ApkPathsToInstall,
    string? ObbSourceDir,
    string? PreExtractedIconPath,
    ApkMetadata PrimaryMetadata,
    bool IsSplitBundle
);

public sealed record SystemImageInfo(
    string RelativePath,
    int ApiLevel,
    string TagId,
    string TagDisplay,
    string PrimaryAbi,
    IReadOnlyList<string> SupportedAbis,
    string DisplayName,
    string Revision,
    bool SupportsArm32,
    bool SupportsArm64
)
{
    public string SelectorLabel
    {
        get
        {
            var armText = SupportsArm32 && SupportsArm64
                ? "ARM 32/64 + x86_64"
                : (SupportsArm64 ? "ARM64 + x86_64 (sin 32-bit)" : "Solo x86_64");
            var tagText = string.IsNullOrWhiteSpace(TagDisplay) ? TagId : TagDisplay;
            return $"Android API {ApiLevel} · {tagText} · {armText}";
        }
    }
}

public sealed class InstalledAppInfo
{
    public string PackageName { get; set; } = string.Empty;
    public string AppName { get; set; } = string.Empty;
    public string VersionName { get; set; } = string.Empty;
    public long VersionCode { get; set; }
    public string LaunchActivity { get; set; } = string.Empty;
    public string TvActivity { get; set; } = string.Empty;
    public string IconPath { get; set; } = string.Empty;
    public string SupportedAbis { get; set; } = string.Empty;
    public DateTime? LastLaunchedUtc { get; set; }

    public string VersionLabel =>
        string.IsNullOrWhiteSpace(VersionName)
            ? (VersionCode > 0 ? $"v#{VersionCode}" : "v?")
            : $"v{VersionName}";
}

public sealed class EngineSettings
{
    public string AvdName { get; set; } = "LexJuniorPlayer";
    public string CustomSdkPath { get; set; } = string.Empty;
    public string SelectedSystemImageRelPath { get; set; } = string.Empty;
    public string GpuMode { get; set; } = "auto";
    public int ResolutionWidth { get; set; } = 1280;
    public int ResolutionHeight { get; set; } = 720;
    public int Density { get; set; } = 213;
    public int RamMb { get; set; } = 2048;
    public int CpuCores { get; set; } = 4;
    public bool PreferTvActivity { get; set; } = false;
    public bool ShutdownEngineOnExit { get; set; } = true;
    public bool QuickBootEnabled { get; set; } = true;
    public string AutoLaunchPackage { get; set; } = string.Empty;
    public bool HideEmulatorToolbar { get; set; } = true;
    public bool StartFullscreenOnLaunch { get; set; } = false;
    public int PerformanceTunedVersion { get; set; } = 0;
    public string AspectPreset { get; set; } = "auto";
}

public sealed record SdkPaths(
    string SdkRoot,
    string AdbExe,
    string EmulatorExe,
    string Aapt2Exe,
    string SystemImageRelPath,
    bool HasSystemImage,
    string AvdRoot,
    IReadOnlyList<SystemImageInfo>? AvailableSystemImages = null,
    SystemImageInfo? ActiveSystemImage = null
);

public enum EngineState
{
    Stopped,
    Starting,
    Running,
    Busy,
    Error
}

public enum PrereqStatus
{
    Ready,
    Outdated,
    Missing,
    Warning
}

public sealed record PrerequisiteCheckItem(
    string Id,
    string Name,
    string InstalledVersion,
    string RequiredVersion,
    string SizeHint,
    PrereqStatus Status,
    string Summary,
    bool CanAutoInstall,
    string SdkPackagePath = "",
    string SdkTargetRelDir = ""
);

public sealed record PrerequisiteReport(
    string DetectedSdkRoot,
    bool SdkDirectoryExists,
    IReadOnlyList<PrerequisiteCheckItem> Items,
    bool HasMissingOrOutdatedAutoInstallable,
    bool HasAccelerationWarning,
    string AccelerationDetail
);
