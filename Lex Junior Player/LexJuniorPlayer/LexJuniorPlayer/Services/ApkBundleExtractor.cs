using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LexJuniorPlayer.Models;

namespace LexJuniorPlayer.Services;

/// <summary>
/// Extracts and resolves compatible APK splits from .xapk / .apks / .apkm bundles safely
/// so streaming apps packaged as split APKs can install via adb install-multiple.
/// </summary>
public static class ApkBundleExtractor
{
    private static readonly string[] KnownAbiNames =
    {
        "x86_64", "x86", "arm64-v8a", "arm64_v8a", "armeabi-v7a", "armeabi_v7a", "armeabi"
    };

    public static bool IsSupportedPackageFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path);
        return string.Equals(ext, ".apk", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".xapk", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".apks", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".apkm", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSplitBundle(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path);
        return string.Equals(ext, ".xapk", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".apks", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".apkm", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<BundleInstallPlan?> PreparePackageAsync(
        string packagePath,
        string aapt2Exe,
        IReadOnlyList<string>? guestAbis = null)
    {
        if (!File.Exists(packagePath) || !IsSupportedPackageFile(packagePath))
            return null;

        if (!IsSplitBundle(packagePath))
        {
            var singleMeta = await ApkInspector.InspectApkAsync(packagePath, aapt2Exe);
            if (singleMeta == null) return null;
            return new BundleInstallPlan(
                OriginalPath: packagePath,
                TempExtractDir: null,
                ApkPathsToInstall: new[] { packagePath },
                ObbSourceDir: null,
                PreExtractedIconPath: null,
                PrimaryMetadata: singleMeta,
                IsSplitBundle: false);
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "lex_bundle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            using (var archive = OpenZipOrFail(packagePath))
            {
                if (archive == null)
                {
                    CleanupTempDir(tempDir);
                    return null;
                }

                var fullRoot = Path.GetFullPath(tempDir) + Path.DirectorySeparatorChar;
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name))
                        continue;

                    var rel = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    var destPath = Path.GetFullPath(Path.Combine(tempDir, rel));
                    if (!destPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                        continue; // Guard against zip-slip

                    var parent = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(parent))
                        Directory.CreateDirectory(parent);

                    entry.ExtractToFile(destPath, overwrite: true);
                }
            }

            var allApks = Directory.GetFiles(tempDir, "*.apk", SearchOption.AllDirectories)
                .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (allApks.Count == 0)
            {
                CleanupTempDir(tempDir);
                return null;
            }

            var preferredAbis = (guestAbis != null && guestAbis.Count > 0)
                ? guestAbis
                : new[] { "x86_64", "x86", "arm64-v8a", "armeabi-v7a" };

            var selectedApks = FilterCompatibleBundleApks(allApks, preferredAbis);
            var baseApk = FindBaseApk(selectedApks) ?? FindBaseApk(allApks) ?? selectedApks[0];

            var meta = await ApkInspector.InspectApkAsync(baseApk, aapt2Exe);
            var manifestInfo = TryReadXapkManifest(Path.Combine(tempDir, "manifest.json"));
            if (meta != null && manifestInfo.HasValue)
            {
                var (mPkg, mName, mVerName, mVerCode) = manifestInfo.Value;
                meta = meta with
                {
                    PackageName = string.IsNullOrWhiteSpace(meta.PackageName) || meta.PackageName == Path.GetFileNameWithoutExtension(baseApk)
                        ? (string.IsNullOrWhiteSpace(mPkg) ? meta.PackageName : mPkg)
                        : meta.PackageName,
                    AppName = string.IsNullOrWhiteSpace(meta.AppName) || meta.AppName == meta.PackageName
                        ? (string.IsNullOrWhiteSpace(mName) ? meta.AppName : mName)
                        : meta.AppName,
                    VersionName = string.IsNullOrWhiteSpace(meta.VersionName) ? mVerName : meta.VersionName,
                    VersionCode = meta.VersionCode == 0 ? mVerCode : meta.VersionCode
                };
            }

            if (meta == null)
            {
                CleanupTempDir(tempDir);
                return null;
            }

            var obbCandidate = Path.Combine(tempDir, "Android", "obb");
            string? obbDir = Directory.Exists(obbCandidate) &&
                             Directory.GetFiles(obbCandidate, "*.obb", SearchOption.AllDirectories).Length > 0
                ? obbCandidate
                : null;

            var iconCandidate = Path.Combine(tempDir, "icon.png");
            string? preIcon = File.Exists(iconCandidate) ? iconCandidate : null;

            return new BundleInstallPlan(
                OriginalPath: packagePath,
                TempExtractDir: tempDir,
                ApkPathsToInstall: selectedApks,
                ObbSourceDir: obbDir,
                PreExtractedIconPath: preIcon,
                PrimaryMetadata: meta,
                IsSplitBundle: true);
        }
        catch
        {
            CleanupTempDir(tempDir);
            return null;
        }
    }

    public static IReadOnlyList<string> FilterCompatibleBundleApks(
        IReadOnlyList<string> allApkPaths,
        IReadOnlyList<string> guestAbis)
    {
        if (allApkPaths.Count <= 1)
            return allApkPaths;

        var abiSplits = new List<(string Path, string Abi)>();
        var nonAbiSplits = new List<string>();

        foreach (var path in allApkPaths)
        {
            var fileName = Path.GetFileName(path);
            var abi = DetectAbiSplitName(fileName);
            if (abi != null)
            {
                abiSplits.Add((path, abi));
            }
            else
            {
                nonAbiSplits.Add(path);
            }
        }

        if (abiSplits.Count == 0)
            return allApkPaths;

        // Pick only the single best matching ABI split supported by the active emulator guest
        string? chosenAbiPath = null;
        foreach (var candidateAbi in guestAbis)
        {
            var norm = NormalizeAbiToken(candidateAbi);
            var hit = abiSplits.FirstOrDefault(s =>
                string.Equals(NormalizeAbiToken(s.Abi), norm, StringComparison.OrdinalIgnoreCase));
            if (hit.Path != null)
            {
                chosenAbiPath = hit.Path;
                break;
            }
        }

        if (chosenAbiPath == null)
        {
            chosenAbiPath = abiSplits[0].Path;
        }

        var result = new List<string>(nonAbiSplits.Count + 1);
        result.AddRange(nonAbiSplits);
        result.Add(chosenAbiPath);

        // Keep base.apk first
        return result
            .OrderByDescending(p => IsBaseApkName(Path.GetFileName(p)))
            .ThenBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string? DetectAbiSplitName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        if (IsBaseApkName(fileName))
            return null;

        foreach (var abi in KnownAbiNames)
        {
            var normalized = abi.Replace('-', '_');
            if (stem.EndsWith("." + abi, StringComparison.Ordinal) ||
                stem.EndsWith("." + normalized, StringComparison.Ordinal) ||
                stem.EndsWith("_" + abi, StringComparison.Ordinal) ||
                stem.EndsWith("_" + normalized, StringComparison.Ordinal) ||
                stem.Contains("config." + normalized, StringComparison.Ordinal) ||
                stem.Contains("config." + abi, StringComparison.Ordinal) ||
                Regex.IsMatch(stem, $@"(?:^|[._-]){Regex.Escape(normalized)}(?:$|[._-])"))
            {
                return abi.Replace('_', '-').Replace("v7a", "v7a"); // returns canonical e.g. arm64-v8a / x86_64
            }
        }

        return null;
    }

    public static void CleanupTempDir(string? tempDir)
    {
        if (string.IsNullOrWhiteSpace(tempDir) || !Directory.Exists(tempDir))
            return;

        try { Directory.Delete(tempDir, recursive: true); } catch { }
    }

    private static string? FindBaseApk(IEnumerable<string> apkPaths)
    {
        return apkPaths.FirstOrDefault(p => IsBaseApkName(Path.GetFileName(p)));
    }

    private static bool IsBaseApkName(string fileName) =>
        string.Equals(fileName, "base.apk", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(fileName, "base-master.apk", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeAbiToken(string raw) =>
        raw.Trim().ToLowerInvariant().Replace('_', '-');

    private static ZipArchive? OpenZipOrFail(string path)
    {
        try
        {
            return ZipFile.OpenRead(path);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static (string Pkg, string Name, string VerName, long VerCode)? TryReadXapkManifest(string jsonPath)
    {
        if (!File.Exists(jsonPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var root = doc.RootElement;
            string pkg = root.TryGetProperty("package_name", out var p) ? p.GetString() ?? "" : "";
            string name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            string verName = root.TryGetProperty("version_name", out var vn) ? vn.GetString() ?? "" : "";
            long verCode = 0;
            if (root.TryGetProperty("version_code", out var vc))
            {
                if (vc.ValueKind == JsonValueKind.Number) vc.TryGetInt64(out verCode);
                else if (vc.ValueKind == JsonValueKind.String) long.TryParse(vc.GetString(), out verCode);
            }
            return (pkg, name, verName, verCode);
        }
        catch
        {
            return null;
        }
    }
}
