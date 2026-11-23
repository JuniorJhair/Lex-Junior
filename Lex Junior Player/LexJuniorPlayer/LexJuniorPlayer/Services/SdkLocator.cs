using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LexJuniorPlayer.Models;

namespace LexJuniorPlayer.Services;

public static class SdkLocator
{
    public const string PreferredImageRelPath = @"system-images\android-30\google_apis\x86_64";

    public static SdkPaths Discover(string? customSdkPath = null, string? preferredSystemImageRelPath = null)
    {
        var sdkRoot = FindSdkRoot(customSdkPath);
        var adb = string.IsNullOrEmpty(sdkRoot) ? string.Empty : Path.Combine(sdkRoot, "platform-tools", "adb.exe");
        var emulator = string.IsNullOrEmpty(sdkRoot) ? string.Empty : Path.Combine(sdkRoot, "emulator", "emulator.exe");
        var aapt2 = FindBestAapt2(sdkRoot);

        var images = string.IsNullOrEmpty(sdkRoot)
            ? Array.Empty<SystemImageInfo>()
            : DiscoverSystemImages(sdkRoot);

        var (relImage, activeImg, hasImage) = ResolveSelectedSystemImage(sdkRoot, images, preferredSystemImageRelPath);

        var avdRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".android",
            "avd"
        );

        var normalizedRel = relImage.TrimEnd('\\', '/') + @"\";
        return new SdkPaths(
            SdkRoot: sdkRoot,
            AdbExe: File.Exists(adb) ? adb : string.Empty,
            EmulatorExe: File.Exists(emulator) ? emulator : string.Empty,
            Aapt2Exe: aapt2,
            SystemImageRelPath: normalizedRel,
            HasSystemImage: hasImage,
            AvdRoot: avdRoot,
            AvailableSystemImages: images,
            ActiveSystemImage: activeImg
        );
    }

    public static string GetRecommendedInstallSdkRoot(string? customSdkPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customSdkPath))
        {
            try { return Path.GetFullPath(customSdkPath.Trim()); } catch { }
        }

        var discovered = FindSdkRoot(customSdkPath);
        if (!string.IsNullOrWhiteSpace(discovered))
            return discovered;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Android",
            "Sdk");
    }

    public static IReadOnlyList<string> GetCandidateSdkRoots(string? customSdkPath = null)
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var raw = new[]
        {
            customSdkPath,
            Environment.GetEnvironmentVariable("ANDROID_HOME"),
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
            Path.Combine(localApp, "Android", "Sdk"),
            Path.Combine(localApp, "LexJuniorPlayer", "Sdk"),
            Path.Combine(AppContext.BaseDirectory, "Sdk")
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var candidate in raw)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try
            {
                var full = Path.GetFullPath(candidate.Trim());
                if (seen.Add(full))
                    list.Add(full);
            }
            catch { }
        }
        return list;
    }

    private static string FindSdkRoot(string? customSdkPath)
    {
        var candidates = GetCandidateSdkRoots(customSdkPath);

        // Prefer a candidate that already has SDK binaries/images inside
        foreach (var dir in candidates)
        {
            if (Directory.Exists(dir) && ScoreSdkCandidate(dir) > 0)
                return dir;
        }

        return candidates.FirstOrDefault(Directory.Exists) ?? string.Empty;
    }

    private static int ScoreSdkCandidate(string dir)
    {
        int score = 0;
        if (File.Exists(Path.Combine(dir, "platform-tools", "adb.exe"))) score += 10;
        if (File.Exists(Path.Combine(dir, "emulator", "emulator.exe"))) score += 10;
        if (Directory.Exists(Path.Combine(dir, "system-images"))) score += 5;
        if (!string.IsNullOrEmpty(FindBestAapt2(dir))) score += 4;
        return score;
    }

    public static IReadOnlyList<SystemImageInfo> DiscoverSystemImages(string sdkRoot)
    {
        var result = new List<SystemImageInfo>();
        if (string.IsNullOrWhiteSpace(sdkRoot))
            return result;

        var sysImgBase = Path.Combine(sdkRoot, "system-images");
        if (!Directory.Exists(sysImgBase))
            return result;

        try
        {
            foreach (var apiDir in Directory.GetDirectories(sysImgBase, "android-*"))
            {
                var apiSegment = Path.GetFileName(apiDir);
                int parsedApi = ParseApiLevelSegment(apiSegment);

                foreach (var tagDir in Directory.GetDirectories(apiDir))
                {
                    var tagFolder = Path.GetFileName(tagDir);
                    foreach (var abiDir in Directory.GetDirectories(tagDir))
                    {
                        if (!IsValidSystemImageDirectory(abiDir))
                            continue;

                        var relPath = Path.GetRelativePath(sdkRoot, abiDir).TrimEnd('\\', '/');
                        var info = ReadSystemImageMetadata(abiDir, relPath, parsedApi, tagFolder);
                        if (info != null)
                            result.Add(info);
                    }
                }
            }
        }
        catch { }

        return result
            .OrderByDescending(i => string.Equals(i.RelativePath, PreferredImageRelPath, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(i => i.SupportsArm32 && i.SupportsArm64)
            .ThenByDescending(i => i.SupportsArm64)
            .ThenBy(i => Math.Abs(i.ApiLevel - 30))
            .ThenByDescending(i => i.ApiLevel)
            .ToList();
    }

    public static bool IsValidSystemImageDirectory(string dir) =>
        Directory.Exists(dir) &&
        (File.Exists(Path.Combine(dir, "system.img")) || File.Exists(Path.Combine(dir, "build.prop")));

    private static (string RelPath, SystemImageInfo? Info, bool HasImage) ResolveSelectedSystemImage(
        string sdkRoot,
        IReadOnlyList<SystemImageInfo> images,
        string? preferredRelPath)
    {
        if (!string.IsNullOrWhiteSpace(preferredRelPath))
        {
            var cleanPref = preferredRelPath.Trim().TrimEnd('\\', '/');
            var matched = images.FirstOrDefault(i =>
                string.Equals(i.RelativePath, cleanPref, StringComparison.OrdinalIgnoreCase));
            if (matched != null)
                return (matched.RelativePath, matched, true);
        }

        if (images.Count > 0)
        {
            var best = images[0];
            return (best.RelativePath, best, true);
        }

        return (PreferredImageRelPath, null, false);
    }

    internal static SystemImageInfo? ReadSystemImageMetadata(string imgDir, string relPath, int fallbackApi, string fallbackTag)
    {
        var abiName = Path.GetFileName(imgDir);
        int apiLevel = fallbackApi;
        string tagId = fallbackTag;
        string tagDisplay = PrettifyTag(fallbackTag);
        string displayName = $"Android API {fallbackApi} ({tagDisplay})";
        string revision = string.Empty;
        var abis = new List<string>();

        var pkgXml = Path.Combine(imgDir, "package.xml");
        if (File.Exists(pkgXml))
        {
            try
            {
                var doc = XDocument.Load(pkgXml);
                var lp = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "localPackage");
                if (lp != null)
                {
                    var revEl = lp.Elements().FirstOrDefault(e => e.Name.LocalName == "revision");
                    revision = FormatXmlRevision(revEl);

                    var dn = lp.Elements().FirstOrDefault(e => e.Name.LocalName == "display-name")?.Value;
                    if (!string.IsNullOrWhiteSpace(dn))
                        displayName = dn.Trim();

                    var td = lp.Elements().FirstOrDefault(e => e.Name.LocalName == "type-details");
                    if (td != null)
                    {
                        var apiText = td.Elements().FirstOrDefault(e => e.Name.LocalName == "api-level")?.Value;
                        if (int.TryParse(apiText, out var parsedApi) && parsedApi > 0)
                            apiLevel = parsedApi;

                        var tagEl = td.Elements().FirstOrDefault(e => e.Name.LocalName == "tag");
                        var idText = tagEl?.Elements().FirstOrDefault(e => e.Name.LocalName == "id")?.Value;
                        var dispText = tagEl?.Elements().FirstOrDefault(e => e.Name.LocalName == "display")?.Value;
                        if (!string.IsNullOrWhiteSpace(idText)) tagId = idText.Trim();
                        if (!string.IsNullOrWhiteSpace(dispText)) tagDisplay = dispText.Trim();

                        foreach (var a in td.Elements().Where(e => e.Name.LocalName == "abis" || e.Name.LocalName == "abi"))
                        {
                            var val = a.Value.Trim();
                            if (!string.IsNullOrEmpty(val) && !abis.Contains(val, StringComparer.OrdinalIgnoreCase))
                                abis.Add(val);
                        }
                    }
                }
            }
            catch { }
        }

        var buildProp = Path.Combine(imgDir, "build.prop");
        if (File.Exists(buildProp))
        {
            try
            {
                foreach (var line in File.ReadLines(buildProp))
                {
                    if (line.StartsWith("ro.product.cpu.abilist=", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("ro.system.product.cpu.abilist=", StringComparison.OrdinalIgnoreCase))
                    {
                        var vals = line.Split('=', 2)[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        foreach (var v in vals)
                        {
                            if (!abis.Contains(v, StringComparer.OrdinalIgnoreCase))
                                abis.Add(v);
                        }
                    }
                    else if (line.StartsWith("ro.build.version.sdk=", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(line.Split('=', 2)[1].Trim(), out var sdkVer) && sdkVer > 0)
                            apiLevel = sdkVer;
                    }
                }
            }
            catch { }
        }

        if (string.IsNullOrEmpty(revision))
            revision = ReadPackageRevision(imgDir);

        if (abis.Count == 0 && !string.IsNullOrWhiteSpace(abiName))
            abis.Add(abiName);

        bool supportsArm32 = abis.Any(a => a.StartsWith("armeabi", StringComparison.OrdinalIgnoreCase));
        bool supportsArm64 = abis.Any(a => string.Equals(a, "arm64-v8a", StringComparison.OrdinalIgnoreCase));

        return new SystemImageInfo(
            RelativePath: relPath,
            ApiLevel: apiLevel,
            TagId: tagId,
            TagDisplay: tagDisplay,
            PrimaryAbi: abiName,
            SupportedAbis: abis,
            DisplayName: displayName,
            Revision: revision,
            SupportsArm32: supportsArm32,
            SupportsArm64: supportsArm64
        );
    }

    public static string ReadPackageRevision(string packageDir)
    {
        if (string.IsNullOrWhiteSpace(packageDir) || !Directory.Exists(packageDir))
            return string.Empty;

        var srcProps = Path.Combine(packageDir, "source.properties");
        if (File.Exists(srcProps))
        {
            try
            {
                foreach (var line in File.ReadLines(srcProps))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("Pkg.Revision=", StringComparison.OrdinalIgnoreCase))
                    {
                        return trimmed.Substring("Pkg.Revision=".Length).Trim();
                    }
                }
            }
            catch { }
        }

        var pkgXml = Path.Combine(packageDir, "package.xml");
        if (File.Exists(pkgXml))
        {
            try
            {
                var doc = XDocument.Load(pkgXml);
                var revEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "revision");
                var formatted = FormatXmlRevision(revEl);
                if (!string.IsNullOrEmpty(formatted))
                    return formatted;
            }
            catch { }
        }

        return string.Empty;
    }

    public static (string Dir, string Revision) FindBestBuildToolsDir(string sdkRoot)
    {
        if (string.IsNullOrEmpty(sdkRoot)) return (string.Empty, string.Empty);
        var btDir = Path.Combine(sdkRoot, "build-tools");
        if (!Directory.Exists(btDir)) return (string.Empty, string.Empty);

        var dirs = Directory.GetDirectories(btDir)
            .Select(d => new { Dir = d, Name = Path.GetFileName(d), Ver = ParseVersionSafe(Path.GetFileName(d)) })
            .OrderByDescending(x => x.Ver);

        foreach (var item in dirs)
        {
            var exe = Path.Combine(item.Dir, "aapt2.exe");
            if (File.Exists(exe))
            {
                var rev = ReadPackageRevision(item.Dir);
                return (item.Dir, string.IsNullOrEmpty(rev) ? item.Name : rev);
            }
        }
        return (string.Empty, string.Empty);
    }

    private static string FindBestAapt2(string sdkRoot)
    {
        var (dir, _) = FindBestBuildToolsDir(sdkRoot);
        return string.IsNullOrEmpty(dir) ? string.Empty : Path.Combine(dir, "aapt2.exe");
    }

    public static Version ParseVersionSafe(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new Version(0, 0);
        var clean = Regex.Match(raw.Trim(), @"^\d+(?:\.\d+){0,3}").Value;
        if (string.IsNullOrEmpty(clean)) return new Version(0, 0);
        if (!clean.Contains('.')) clean += ".0";
        return Version.TryParse(clean, out var v) ? v : new Version(0, 0);
    }

    private static int ParseApiLevelSegment(string segment)
    {
        var m = Regex.Match(segment, @"android-(\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out var api) ? api : 30;
    }

    private static string FormatXmlRevision(XElement? revEl)
    {
        if (revEl == null) return string.Empty;
        var major = revEl.Elements().FirstOrDefault(e => e.Name.LocalName == "major")?.Value;
        var minor = revEl.Elements().FirstOrDefault(e => e.Name.LocalName == "minor")?.Value;
        var micro = revEl.Elements().FirstOrDefault(e => e.Name.LocalName == "micro")?.Value;
        var parts = new[] { major, minor, micro }.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        return string.Join(".", parts);
    }

    private static string PrettifyTag(string tag) => tag switch
    {
        "google_apis" => "Google APIs",
        "google_apis_playstore" => "Google Play",
        "android-tv" => "Android TV",
        "google-tv" => "Google TV",
        "default" => "AOSP",
        _ => tag
    };
}
