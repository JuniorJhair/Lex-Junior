using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using LexJuniorPlayer.Models;

namespace LexJuniorPlayer.Services;

public sealed record RemoteSdkArchive(
    string PackagePath,
    string DisplayName,
    string Revision,
    string Url,
    string Sha1Hex,
    long SizeBytes,
    string LicenseText
);

public static class PrerequisiteService
{
    public const string RepoMainXmlUrl = "https://dl.google.com/android/repository/repository2-3.xml";
    public const string RepoSysImgGoogleApisXmlUrl = "https://dl.google.com/android/repository/sys-img/google_apis/sys-img2-3.xml";
    public const string RepoBaseUrl = "https://dl.google.com/android/repository/";
    public const string SysImgBaseUrl = "https://dl.google.com/android/repository/sys-img/google_apis/";

    public static readonly Version MinPlatformToolsVersion = new(34, 0, 0);
    public static readonly Version MinEmulatorVersion = new(32, 1, 0);
    public static readonly Version MinBuildToolsVersion = new(30, 0, 0);

    public const string DefaultSystemImagePackagePath = "system-images;android-30;google_apis;x86_64";
    public const string DefaultBuildToolsPackagePath = "build-tools;34.0.0";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    public static string DefaultAppInstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LexJuniorPlayer");

    public static string DefaultInstalledExePath =>
        Path.Combine(DefaultAppInstallDir, "LexJuniorPlayer.exe");

    public static async Task<PrerequisiteReport> CheckAllAsync(
        string? customSdkPath = null,
        string? selectedSystemImageRelPath = null,
        bool includeAppBinaryCheck = false,
        CancellationToken ct = default)
    {
        var sdk = SdkLocator.Discover(customSdkPath, selectedSystemImageRelPath);
        var effectiveSdkRoot = string.IsNullOrWhiteSpace(sdk.SdkRoot)
            ? SdkLocator.GetRecommendedInstallSdkRoot(customSdkPath)
            : sdk.SdkRoot;
        bool sdkDirExists = Directory.Exists(effectiveSdkRoot);

        var items = new List<PrerequisiteCheckItem>();

        if (includeAppBinaryCheck)
        {
            items.Add(InspectInstalledAppBinary());
        }

        // 1. Platform-Tools (adb.exe)
        items.Add(InspectPlatformTools(effectiveSdkRoot));

        // 2. Android Emulator (emulator.exe)
        items.Add(InspectEmulator(effectiveSdkRoot));

        // 3. Build-Tools (aapt2.exe)
        items.Add(InspectBuildTools(effectiveSdkRoot));

        // 4. Android System Image (any compatible already installed, or default Android 11 ARM+x86_64)
        items.Add(InspectSystemImage(sdk, effectiveSdkRoot));

        // 5. Hardware Virtualization / Acceleration (WHPX / AEHD / HAXM)
        var (accelStatus, accelVer, accelSummary) = !string.IsNullOrEmpty(sdk.EmulatorExe)
            ? await CheckHardwareAccelerationAsync(sdk.EmulatorExe, ct)
            : (PrereqStatus.Warning, "Pendiente (falta emulador)", "Se verificará automáticamente al instalar el motor del emulador Android.");

        items.Add(new PrerequisiteCheckItem(
            Id: "hw_accel",
            Name: "Aceleración por hardware (WHPX / AEHD)",
            InstalledVersion: accelVer,
            RequiredVersion: "WHPX / AEHD / HAXM",
            SizeHint: "Función de Windows",
            Status: accelStatus,
            Summary: accelSummary,
            CanAutoInstall: false));

        bool needsInstall = items.Any(i => i.CanAutoInstall && (i.Status == PrereqStatus.Missing || i.Status == PrereqStatus.Outdated));

        return new PrerequisiteReport(
            DetectedSdkRoot: effectiveSdkRoot,
            SdkDirectoryExists: sdkDirExists,
            Items: items,
            HasMissingOrOutdatedAutoInstallable: needsInstall,
            HasAccelerationWarning: accelStatus == PrereqStatus.Warning,
            AccelerationDetail: accelSummary);
    }

    internal static PrerequisiteCheckItem InspectInstalledAppBinary()
    {
        var targetExe = DefaultInstalledExePath;
        var currentVer = typeof(PrerequisiteService).Assembly.GetName().Version ?? new Version(1, 0, 0, 0);
        var currentStr = $"v{currentVer.Major}.{currentVer.Minor}.{Math.Max(0, currentVer.Build)}";

        if (!File.Exists(targetExe))
        {
            return new PrerequisiteCheckItem(
                Id: "app_binary",
                Name: "Aplicación Lex Junior Player",
                InstalledVersion: "No instalada",
                RequiredVersion: currentStr,
                SizeHint: "~1 MB",
                Status: PrereqStatus.Missing,
                Summary: "Se copiará en AppData\\Local\\Programs\\LexJuniorPlayer.",
                CanAutoInstall: true);
        }

        Version? installedVer = null;
        try
        {
            var fvi = FileVersionInfo.GetVersionInfo(targetExe);
            if (!string.IsNullOrWhiteSpace(fvi.FileVersion) && Version.TryParse(fvi.FileVersion.Split('+')[0], out var parsed))
                installedVer = parsed;
        }
        catch { }

        if (installedVer != null && installedVer < currentVer)
        {
            return new PrerequisiteCheckItem(
                Id: "app_binary",
                Name: "Aplicación Lex Junior Player",
                InstalledVersion: $"v{installedVer.Major}.{installedVer.Minor}.{Math.Max(0, installedVer.Build)}",
                RequiredVersion: currentStr,
                SizeHint: "~1 MB",
                Status: PrereqStatus.Outdated,
                Summary: $"Versión anterior instalada; se actualizará a {currentStr} conservando tus APKs.",
                CanAutoInstall: true);
        }

        return new PrerequisiteCheckItem(
            Id: "app_binary",
            Name: "Aplicación Lex Junior Player",
            InstalledVersion: installedVer != null ? $"v{installedVer.Major}.{installedVer.Minor}.{Math.Max(0, installedVer.Build)}" : currentStr,
            RequiredVersion: currentStr,
            SizeHint: "Al día",
            Status: PrereqStatus.Ready,
            Summary: "Ya instalada y al día (no se sobrescribe sin cambios).",
            CanAutoInstall: true);
    }

    internal static PrerequisiteCheckItem InspectPlatformTools(string sdkRoot)
    {
        var dir = Path.Combine(sdkRoot, "platform-tools");
        var adb = Path.Combine(dir, "adb.exe");
        var rev = SdkLocator.ReadPackageRevision(dir);
        var ver = SdkLocator.ParseVersionSafe(rev);

        if (!File.Exists(adb))
        {
            return new PrerequisiteCheckItem(
                Id: "platform-tools",
                Name: "Android Platform-Tools (adb.exe)",
                InstalledVersion: "No encontrado",
                RequiredVersion: $"v{MinPlatformToolsVersion}",
                SizeHint: "~8 MB (ZIP)",
                Status: PrereqStatus.Missing,
                Summary: "Falta adb.exe para instalar y controlar APKs de streaming.",
                CanAutoInstall: true,
                SdkPackagePath: "platform-tools",
                SdkTargetRelDir: "platform-tools");
        }

        if (ver != new Version(0, 0) && ver < MinPlatformToolsVersion)
        {
            return new PrerequisiteCheckItem(
                Id: "platform-tools",
                Name: "Android Platform-Tools (adb.exe)",
                InstalledVersion: $"v{rev}",
                RequiredVersion: $"v{MinPlatformToolsVersion}",
                SizeHint: "~8 MB (ZIP)",
                Status: PrereqStatus.Outdated,
                Summary: $"Versión {rev} desactualizada (mínima {MinPlatformToolsVersion}); se recomienda actualizar.",
                CanAutoInstall: true,
                SdkPackagePath: "platform-tools",
                SdkTargetRelDir: "platform-tools");
        }

        return new PrerequisiteCheckItem(
            Id: "platform-tools",
            Name: "Android Platform-Tools (adb.exe)",
            InstalledVersion: string.IsNullOrEmpty(rev) ? "Detectado" : $"v{rev}",
            RequiredVersion: $"v{MinPlatformToolsVersion}+",
            SizeHint: "Listo",
            Status: PrereqStatus.Ready,
            Summary: "Ya instalado y compatible. No se vuelve a instalar.",
            CanAutoInstall: true,
            SdkPackagePath: "platform-tools",
            SdkTargetRelDir: "platform-tools");
    }

    internal static PrerequisiteCheckItem InspectEmulator(string sdkRoot)
    {
        var dir = Path.Combine(sdkRoot, "emulator");
        var exe = Path.Combine(dir, "emulator.exe");
        var rev = SdkLocator.ReadPackageRevision(dir);
        var ver = SdkLocator.ParseVersionSafe(rev);

        if (!File.Exists(exe))
        {
            return new PrerequisiteCheckItem(
                Id: "emulator",
                Name: "Android Emulator (x86_64 + GPU)",
                InstalledVersion: "No encontrado",
                RequiredVersion: $"v{MinEmulatorVersion}",
                SizeHint: "~320 MB (ZIP)",
                Status: PrereqStatus.Missing,
                Summary: "Falta emulator.exe para ejecutar el motor Android.",
                CanAutoInstall: true,
                SdkPackagePath: "emulator",
                SdkTargetRelDir: "emulator");
        }

        if (ver != new Version(0, 0) && ver < MinEmulatorVersion)
        {
            return new PrerequisiteCheckItem(
                Id: "emulator",
                Name: "Android Emulator (x86_64 + GPU)",
                InstalledVersion: $"v{rev}",
                RequiredVersion: $"v{MinEmulatorVersion}",
                SizeHint: "~320 MB (ZIP)",
                Status: PrereqStatus.Outdated,
                Summary: $"Versión {rev} antigua para GPU/WHPX moderno (mínima {MinEmulatorVersion}).",
                CanAutoInstall: true,
                SdkPackagePath: "emulator",
                SdkTargetRelDir: "emulator");
        }

        return new PrerequisiteCheckItem(
            Id: "emulator",
            Name: "Android Emulator (x86_64 + GPU)",
            InstalledVersion: string.IsNullOrEmpty(rev) ? "Detectado" : $"v{rev}",
            RequiredVersion: $"v{MinEmulatorVersion}+",
            SizeHint: "Listo",
            Status: PrereqStatus.Ready,
            Summary: "Ya instalado y actualizado. No se vuelve a instalar.",
            CanAutoInstall: true,
            SdkPackagePath: "emulator",
            SdkTargetRelDir: "emulator");
    }

    internal static PrerequisiteCheckItem InspectBuildTools(string sdkRoot)
    {
        var (dir, rev) = SdkLocator.FindBestBuildToolsDir(sdkRoot);
        var ver = SdkLocator.ParseVersionSafe(rev);

        if (string.IsNullOrEmpty(dir))
        {
            return new PrerequisiteCheckItem(
                Id: "build-tools",
                Name: "Android Build-Tools (aapt2.exe)",
                InstalledVersion: "No encontrado",
                RequiredVersion: $"v{MinBuildToolsVersion}+",
                SizeHint: "~55 MB (ZIP)",
                Status: PrereqStatus.Missing,
                Summary: "Necesario para leer nombre, versión e icono de cada APK/XAPK antes de instalar.",
                CanAutoInstall: true,
                SdkPackagePath: DefaultBuildToolsPackagePath,
                SdkTargetRelDir: @"build-tools\34.0.0");
        }

        if (ver != new Version(0, 0) && ver < MinBuildToolsVersion)
        {
            return new PrerequisiteCheckItem(
                Id: "build-tools",
                Name: "Android Build-Tools (aapt2.exe)",
                InstalledVersion: $"v{rev}",
                RequiredVersion: $"v{MinBuildToolsVersion}+",
                SizeHint: "~55 MB (ZIP)",
                Status: PrereqStatus.Outdated,
                Summary: $"Versión {rev} antigua; se recomienda instalar Build-Tools compatible.",
                CanAutoInstall: true,
                SdkPackagePath: DefaultBuildToolsPackagePath,
                SdkTargetRelDir: @"build-tools\34.0.0");
        }

        return new PrerequisiteCheckItem(
            Id: "build-tools",
            Name: "Android Build-Tools (aapt2.exe)",
            InstalledVersion: $"v{rev}",
            RequiredVersion: $"v{MinBuildToolsVersion}+",
            SizeHint: "Listo",
            Status: PrereqStatus.Ready,
            Summary: "Ya instalado y compatible. No se vuelve a instalar.",
            CanAutoInstall: true,
            SdkPackagePath: DefaultBuildToolsPackagePath,
            SdkTargetRelDir: @"build-tools\34.0.0");
    }

    internal static PrerequisiteCheckItem InspectSystemImage(SdkPaths sdk, string effectiveSdkRoot)
    {
        if (sdk.HasSystemImage && sdk.ActiveSystemImage != null)
        {
            var img = sdk.ActiveSystemImage;
            var armNote = img.SupportsArm32 && img.SupportsArm64
                ? "Compatible con APKs ARM + x86_64."
                : (img.SupportsArm64 ? "Solo 64-bit ARM/x86_64 (sin APKs 32-bit)." : "Solo x86_64 (sin traducción ARM).");
            var revText = string.IsNullOrEmpty(img.Revision) ? $"API {img.ApiLevel}" : $"API {img.ApiLevel} rev {img.Revision}";

            return new PrerequisiteCheckItem(
                Id: "system-image",
                Name: "Imagen Android de Sistema",
                InstalledVersion: revText,
                RequiredVersion: "API 30+ (ARM + x86_64)",
                SizeHint: "Listo",
                Status: PrereqStatus.Ready,
                Summary: $"Detectada en {img.RelativePath}. {armNote} No se reinstala.",
                CanAutoInstall: true,
                SdkPackagePath: DefaultSystemImagePackagePath,
                SdkTargetRelDir: SdkLocator.PreferredImageRelPath);
        }

        return new PrerequisiteCheckItem(
            Id: "system-image",
            Name: "Imagen Android 11 (Google APIs ARM + x86_64)",
            InstalledVersion: "No encontrada",
            RequiredVersion: "API 30 (x86_64 + ARM)",
            SizeHint: "~1.1 GB (ZIP)",
            Status: PrereqStatus.Missing,
            Summary: $"Falta imagen de sistema limpia en {effectiveSdkRoot}. Se descargará del repositorio oficial de Google.",
            CanAutoInstall: true,
            SdkPackagePath: DefaultSystemImagePackagePath,
            SdkTargetRelDir: SdkLocator.PreferredImageRelPath);
    }

    public static async Task<(PrereqStatus Status, string VersionLabel, string Summary)> CheckHardwareAccelerationAsync(
        string emulatorExe,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(emulatorExe) || !File.Exists(emulatorExe))
        {
            return (PrereqStatus.Warning, "Desconocida", "No se pudo ejecutar -accel-check porque falta emulator.exe.");
        }

        try
        {
            var res = await AndroidEngineService.RunCommandAsync(emulatorExe, "-accel-check", ct, TimeSpan.FromSeconds(12));
            return ParseAccelCheckOutput(res.ExitCode, res.StdOut, res.StdErr);
        }
        catch (Exception ex)
        {
            return (PrereqStatus.Warning, "Sin verificar", $"No se pudo comprobar aceleración: {ex.Message}");
        }
    }

    public static (PrereqStatus Status, string VersionLabel, string Summary) ParseAccelCheckOutput(
        int exitCode,
        string stdOut,
        string stdErr)
    {
        var combined = $"{stdOut}\n{stdErr}".Trim();
        var lower = combined.ToLowerInvariant();

        if (lower.Contains("is operational") ||
            lower.Contains("whpx") && (lower.Contains("operational") || lower.Contains("usable") || lower.Contains("version")) && !lower.Contains("please ensure") ||
            lower.Contains("aehd") && lower.Contains("operational") ||
            (exitCode == 0 && lower.Contains("accel")))
        {
            return (PrereqStatus.Ready, "Activa", "Aceleración por hardware operativa (máxima fluidez de video).");
        }

        if (lower.Contains("hyper-v detected") || lower.Contains("windows hypervisor platform is available"))
        {
            return (
                PrereqStatus.Warning,
                "WHPX requerido",
                "Windows tiene Hyper-V activo. Asegúrate de activar la característica 'Plataforma de hipervisor de Windows' (Windows Hypervisor Platform) y reiniciar si el emulador va lento o no arranca.");
        }

        if (lower.Contains("please disable hyper-v") ||
            lower.Contains("not installed") ||
            lower.Contains("vt-x") ||
            lower.Contains("svm") ||
            exitCode != 0)
        {
            var firstLine = combined
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .FirstOrDefault(s => !string.Equals(s, "accel:", StringComparison.OrdinalIgnoreCase) && !int.TryParse(s, out _))
                ?? "Aceleración WHPX/AEHD no disponible.";

            return (
                PrereqStatus.Warning,
                "No detectada",
                $"{firstLine} Activa 'Plataforma de hipervisor de Windows' / virtualización en BIOS para evitar tirones.");
        }

        return (PrereqStatus.Ready, "Disponible", "Aceleración comprobada.");
    }

    public static async Task<IReadOnlyDictionary<string, RemoteSdkArchive>> FetchRemoteArchivesAsync(
        IEnumerable<string> packagePaths,
        CancellationToken ct = default)
    {
        var targets = new HashSet<string>(packagePaths.Where(p => !string.IsNullOrWhiteSpace(p)), StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, RemoteSdkArchive>(StringComparer.OrdinalIgnoreCase);
        if (targets.Count == 0) return result;

        var needsMain = targets.Any(t => !t.StartsWith("system-images;", StringComparison.OrdinalIgnoreCase));
        var needsSysImg = targets.Any(t => t.StartsWith("system-images;", StringComparison.OrdinalIgnoreCase));

        if (needsMain)
        {
            var xml = await Http.GetStringAsync(RepoMainXmlUrl, ct);
            foreach (var item in ParseRepositoryXml(xml, RepoBaseUrl, targets))
                result[item.PackagePath] = item;
        }

        if (needsSysImg)
        {
            var xml = await Http.GetStringAsync(RepoSysImgGoogleApisXmlUrl, ct);
            foreach (var item in ParseRepositoryXml(xml, SysImgBaseUrl, targets))
                result[item.PackagePath] = item;
        }

        return result;
    }

    public static IReadOnlyList<RemoteSdkArchive> ParseRepositoryXml(
        string xmlContent,
        string baseUrl,
        ISet<string>? filterPaths = null)
    {
        var list = new List<RemoteSdkArchive>();
        var doc = XDocument.Parse(xmlContent);
        var licenses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var lic in doc.Descendants().Where(e => e.Name.LocalName == "license"))
        {
            var id = lic.Attribute("id")?.Value;
            if (!string.IsNullOrWhiteSpace(id))
                licenses[id] = lic.Value;
        }

        // Collect best (highest revision, stable channel preferred) match per package path
        var bestByPath = new Dictionary<string, (Version Rev, RemoteSdkArchive Archive)>(StringComparer.OrdinalIgnoreCase);

        foreach (var pkg in doc.Descendants().Where(e => e.Name.LocalName == "remotePackage"))
        {
            var path = pkg.Attribute("path")?.Value;
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (filterPaths != null && !filterPaths.Contains(path)) continue;

            var channelRef = pkg.Elements().FirstOrDefault(e => e.Name.LocalName == "channelRef")?.Attribute("ref")?.Value;
            if (!string.IsNullOrEmpty(channelRef) && !string.Equals(channelRef, "channel-0", StringComparison.OrdinalIgnoreCase))
                continue; // skip canary/beta/dev channels

            var revEl = pkg.Elements().FirstOrDefault(e => e.Name.LocalName == "revision");
            var revStr = FormatRevision(revEl);
            var revVer = SdkLocator.ParseVersionSafe(revStr);

            var displayName = pkg.Elements().FirstOrDefault(e => e.Name.LocalName == "display-name")?.Value?.Trim() ?? path;
            var licRef = pkg.Elements().FirstOrDefault(e => e.Name.LocalName == "uses-license")?.Attribute("ref")?.Value;
            var licText = (licRef != null && licenses.TryGetValue(licRef, out var lt)) ? lt : string.Empty;

            var archiveEl = pkg.Descendants().Where(e => e.Name.LocalName == "archive")
                .FirstOrDefault(a =>
                {
                    var hostOs = a.Elements().FirstOrDefault(e => e.Name.LocalName == "host-os")?.Value;
                    return string.IsNullOrEmpty(hostOs) ||
                           string.Equals(hostOs.Trim(), "windows", StringComparison.OrdinalIgnoreCase);
                });

            if (archiveEl == null) continue;

            var completeEl = archiveEl.Elements().FirstOrDefault(e => e.Name.LocalName == "complete");
            var urlNode = completeEl?.Elements().FirstOrDefault(e => e.Name.LocalName == "url")?.Value?.Trim();
            var shaNode = completeEl?.Elements().FirstOrDefault(e => e.Name.LocalName == "checksum")?.Value?.Trim();
            var sizeStr = completeEl?.Elements().FirstOrDefault(e => e.Name.LocalName == "size")?.Value?.Trim();

            if (string.IsNullOrWhiteSpace(urlNode)) continue;
            long.TryParse(sizeStr, out var sizeBytes);

            var fullUrl = urlNode.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? urlNode
                : baseUrl.TrimEnd('/') + "/" + urlNode.TrimStart('/');

            var candidate = new RemoteSdkArchive(
                PackagePath: path,
                DisplayName: displayName,
                Revision: revStr,
                Url: fullUrl,
                Sha1Hex: shaNode ?? string.Empty,
                SizeBytes: sizeBytes,
                LicenseText: licText);

            if (!bestByPath.TryGetValue(path, out var existing) || revVer > existing.Rev)
            {
                bestByPath[path] = (revVer, candidate);
            }
        }

        list.AddRange(bestByPath.Values.Select(v => v.Archive));
        return list;
    }

    public static async Task DownloadAndInstallSdkPackageAsync(
        string sdkRoot,
        PrerequisiteCheckItem item,
        RemoteSdkArchive remote,
        Action<int, string> reportProgress,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(sdkRoot);
        var tempZip = Path.Combine(Path.GetTempPath(), $"lexsdk_{item.Id}_{Guid.NewGuid():N}.zip");
        var tempExtract = Path.Combine(Path.GetTempPath(), $"lexsdk_ext_{item.Id}_{Guid.NewGuid():N}");

        try
        {
            reportProgress(5, $"Descargando {item.Name} ({FormatMb(remote.SizeBytes)})...");
            using (var resp = await Http.GetAsync(remote.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? remote.SizeBytes;
                await using var netStream = await resp.Content.ReadAsStreamAsync(ct);
                await using var fileStream = File.Create(tempZip);
                var buffer = new byte[65536];
                long received = 0;
                int read;
                while ((read = await netStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    received += read;
                    if (total > 0)
                    {
                        var pct = 5 + (int)Math.Clamp(received * 65.0 / total, 0, 65);
                        reportProgress(pct, $"Descargando {item.Name}: {FormatMb(received)} / {FormatMb(total)}...");
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(remote.Sha1Hex))
            {
                reportProgress(74, $"Verificando integridad SHA-1 de {item.Name}...");
                await using var fs = File.OpenRead(tempZip);
                var hash = await SHA1.HashDataAsync(fs, ct);
                var actualHex = Convert.ToHexString(hash);
                if (!string.Equals(actualHex, remote.Sha1Hex.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Falló la verificación SHA-1 al descargar {item.Name}.");
                }
            }

            reportProgress(82, $"Extrayendo {item.Name}...");
            Directory.CreateDirectory(tempExtract);
            ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // Standard SDK archives wrap contents inside one folder matching component name (e.g. platform-tools/, emulator/, x86_64/, android-14/)
            var entries = Directory.GetFileSystemEntries(tempExtract);
            string payloadDir = (entries.Length == 1 && Directory.Exists(entries[0])) ? entries[0] : tempExtract;

            var targetDir = Path.GetFullPath(Path.Combine(sdkRoot, item.SdkTargetRelDir));
            var stageOld = targetDir + ".old_" + Guid.NewGuid().ToString("N");
            var parentDir = Path.GetDirectoryName(targetDir);
            if (!string.IsNullOrEmpty(parentDir))
                Directory.CreateDirectory(parentDir);

            reportProgress(92, $"Instalando {item.Name} en {targetDir}...");
            if (Directory.Exists(targetDir))
            {
                Directory.Move(targetDir, stageOld);
            }

            try
            {
                MoveOrCopyDirectory(payloadDir, targetDir);
                WriteFallbackPackageMetadataIfMissing(targetDir, item, remote);
                if (Directory.Exists(stageOld))
                {
                    try { Directory.Delete(stageOld, recursive: true); } catch { }
                }
            }
            catch
            {
                if (!Directory.Exists(targetDir) && Directory.Exists(stageOld))
                {
                    try { Directory.Move(stageOld, targetDir); } catch { }
                }
                throw;
            }

            reportProgress(100, $"{item.Name} instalado correctamente.");
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }
    }

    private static void WriteFallbackPackageMetadataIfMissing(string targetDir, PrerequisiteCheckItem item, RemoteSdkArchive remote)
    {
        var srcProps = Path.Combine(targetDir, "source.properties");
        if (!File.Exists(srcProps) && !string.IsNullOrWhiteSpace(remote.Revision))
        {
            try
            {
                File.WriteAllLines(srcProps, new[]
                {
                    $"Pkg.Desc={remote.DisplayName}",
                    "Pkg.UserSrc=false",
                    $"Pkg.Revision={remote.Revision}"
                });
            }
            catch { }
        }
    }

    private static void MoveOrCopyDirectory(string sourceDir, string targetDir)
    {
        try
        {
            Directory.Move(sourceDir, targetDir);
            return;
        }
        catch
        {
            // Different volume (Temp on C:, custom SDK on D: etc.) -> recursive copy
        }

        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var sub in Directory.GetDirectories(sourceDir))
        {
            MoveOrCopyDirectory(sub, Path.Combine(targetDir, Path.GetFileName(sub)));
        }
    }

    private static string FormatRevision(XElement? revEl)
    {
        if (revEl == null) return string.Empty;
        var major = revEl.Elements().FirstOrDefault(e => e.Name.LocalName == "major")?.Value;
        var minor = revEl.Elements().FirstOrDefault(e => e.Name.LocalName == "minor")?.Value;
        var micro = revEl.Elements().FirstOrDefault(e => e.Name.LocalName == "micro")?.Value;
        return string.Join(".", new[] { major, minor, micro }.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static string FormatMb(long bytes) =>
        bytes <= 0 ? "? MB" : $"{bytes / (1024.0 * 1024.0):F1} MB";
}
