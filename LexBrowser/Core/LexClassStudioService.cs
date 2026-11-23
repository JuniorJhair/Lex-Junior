using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LexBrowser.Core;

public sealed record LexClassWorldManifestEntry(
    string Id,
    string Version,
    string Status,
    string? DownloadUrl,
    string? Sha256,
    long? SizeBytes,
    string? SizeLabel,
    string? Note);

public sealed record LexClassWorldLocalPack(
    string Id,
    string Version,
    string InstalledUtc);

public sealed record LexClassWorldUiState(
    string Status,
    string? LocalVersion,
    string? RemoteVersion,
    string? Note,
    string? SizeLabel,
    int? Progress = null);

public sealed record LexClassPendingWorldInfo(
    string Id,
    string? LocalVersion,
    string RemoteVersion,
    string? Note,
    string? SizeLabel,
    bool IsUpdate);

/// <summary>
/// Motor local de Lex Junior Class dentro de LexBrowser:
/// - Sirve los recursos web compilados offline en <c>https://class.lex.local/*</c> con CSP estricta.
/// - Mantiene el estado modular de mundos (instalados, actualizaciones disponibles y mundos nuevos).
/// - Nunca registra historial ni sesiones del navegador.
/// </summary>
public static class LexClassStudioService
{
    public const string Host = "class.lex.local";
    public const string HomeUrl = "https://class.lex.local/index.html#universo";
    public const string WorldsManifestUrl = $"https://github.com/{UpdateManager.GitHubOwner}/{UpdateManager.GitHubRepo}/releases/latest/download/lexclass-worlds.json";

    public const long MaxManifestBytes = 2 * 1024 * 1024;
    public const long MaxWorldZipBytes = 250 * 1024 * 1024;
    public const long MaxExtractedWorldBytes = 500 * 1024 * 1024;
    public const int MaxExtractedFiles = 5000;

    private const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob:; " +
        "media-src 'self' data: blob:; " +
        "font-src 'self' data:; " +
        "connect-src 'self' data: blob:; " +
        "worker-src 'self' blob:; " +
        "object-src 'none'; " +
        "frame-ancestors 'none'";

    private static readonly Regex SafeWorldIdRegex = new("^[a-z0-9][a-z0-9_-]{0,48}$", RegexOptions.Compiled);
    private static readonly string[] BuiltInReadyWorldIds = { "matematicas", "idiomas" };

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    private static readonly object _stateLock = new();
    private static readonly ConcurrentDictionary<string, string> _busyWorldIds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, int> _busyWorldProgress = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<Dictionary<string, string>> _embeddedResourceLookup =
        new(BuildEmbeddedResourceLookup, LazyThreadSafetyMode.ExecutionAndPublication);

    private static List<LexClassWorldManifestEntry> _cachedManifest = new();
    private static bool _onlineCheckPerformed = false;

    static LexClassStudioService()
    {
        _http.DefaultRequestHeaders.UserAgent.TryParseAdd($"LexJuniorBrowser/{UpdateManager.CurrentVersionString}");
    }

    public static string LexClassDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LexBrowser", "LexClass");

    public static string PacksRootDir => Path.Combine(LexClassDataDir, "packs");

    public static string StateFilePath => Path.Combine(LexClassDataDir, "worlds-state.json");

    public static bool IsClassUrl(string? rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput)) return false;
        string trimmed = rawInput.Trim();

        if (trimmed.Equals("lex://class", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("lex:class", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("lex://class/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            if ((uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
                 uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)) &&
                uri.Host.Equals(Host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsValidWorldId(string? worldId) =>
        !string.IsNullOrWhiteSpace(worldId) && SafeWorldIdRegex.IsMatch(worldId.Trim());

    public static string GetLexClassAssetsFolder()
    {
        string p1 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "lexclass");
        if (Directory.Exists(p1)) return p1;

        string p2 = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Assets", "lexclass"));
        if (Directory.Exists(p2)) return p2;

        return p1;
    }

    public static bool TryResolveSafeRelativeAssetPath(string? rawPath, out string cleanPath)
    {
        cleanPath = "index.html";
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return true;
        }

        string candidate = rawPath.Trim();
        int qIdx = candidate.IndexOfAny(new[] { '?', '#' });
        if (qIdx >= 0) candidate = candidate[..qIdx];

        candidate = Uri.UnescapeDataString(candidate).Trim().TrimStart('/', '\\').Replace('\\', '/');
        if (string.IsNullOrEmpty(candidate))
        {
            cleanPath = "index.html";
            return true;
        }

        if (candidate.Contains("..", StringComparison.Ordinal) ||
            candidate.Contains(':', StringComparison.Ordinal) ||
            candidate.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0)
        {
            return false;
        }

        var segments = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (string seg in segments)
        {
            if (seg == "." || seg == "..") return false;
        }

        cleanPath = string.Join("/", segments);
        return cleanPath.Length > 0;
    }

    public static byte[]? LoadStaticAsset(string? rawRelativePath, out string resolvedRelativePath)
    {
        resolvedRelativePath = "index.html";
        if (!TryResolveSafeRelativeAssetPath(rawRelativePath, out string cleanPath))
            return null;

        resolvedRelativePath = cleanPath;

        // 1. Verificar overrides de paquetes descargados localmente
        if (TryLoadFromInstalledPackOverrides(cleanPath, out var overrideBytes))
            return overrideBytes;

        // 2. Carpeta física junto al ejecutable
        if (TryLoadFromDirectory(GetLexClassAssetsFolder(), cleanPath, out var diskBytes))
            return diskBytes;

        // 3. Recurso incrustado en el binario (Single-File EXE)
        if (TryLoadFromEmbedded(cleanPath, out var embedBytes))
            return embedBytes;

        // 4. Fallback SPA si la ruta no apunta a un archivo con extensión
        if (!Path.HasExtension(cleanPath))
        {
            resolvedRelativePath = "index.html";
            if (TryLoadFromInstalledPackOverrides("index.html", out overrideBytes)) return overrideBytes;
            if (TryLoadFromDirectory(GetLexClassAssetsFolder(), "index.html", out diskBytes)) return diskBytes;
            if (TryLoadFromEmbedded("index.html", out embedBytes)) return embedBytes;
        }

        return null;
    }

    private static bool TryLoadFromInstalledPackOverrides(string cleanPath, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        try
        {
            if (!Directory.Exists(PacksRootDir)) return false;

            var localPacks = LoadInstalledPacksInternal();
            foreach (var pack in localPacks.OrderByDescending(p => p.InstalledUtc, StringComparer.Ordinal))
            {
                if (!IsValidWorldId(pack.Id)) continue;
                string packDir = Path.Combine(PacksRootDir, pack.Id, "web");
                if (Directory.Exists(packDir) && TryLoadFromDirectory(packDir, cleanPath, out bytes))
                    return true;
            }
        }
        catch { }

        return false;
    }

    private static bool TryLoadFromDirectory(string baseDir, string cleanPath, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        try
        {
            if (!Directory.Exists(baseDir)) return false;
            string fullRoot = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                              + Path.DirectorySeparatorChar;
            string diskPath = Path.GetFullPath(Path.Combine(fullRoot, cleanPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!diskPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(diskPath))
                return false;

            bytes = File.ReadAllBytes(diskPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, string> BuildEmbeddedResourceLookup()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var asm = typeof(LexClassStudioService).Assembly;
            foreach (string name in asm.GetManifestResourceNames())
            {
                if (name.StartsWith("LexClassEmbed/", StringComparison.OrdinalIgnoreCase))
                {
                    string rel = name["LexClassEmbed/".Length..].Replace('\\', '/').TrimStart('/');
                    dict[rel] = name;
                }
                else if (name.StartsWith("LexClassEmbed\\", StringComparison.OrdinalIgnoreCase))
                {
                    string rel = name["LexClassEmbed\\".Length..].Replace('\\', '/').TrimStart('/');
                    dict[rel] = name;
                }
                else if (name.StartsWith("LexBrowser.Assets.lexclass.", StringComparison.OrdinalIgnoreCase))
                {
                    dict[name] = name;
                }
            }
        }
        catch { }
        return dict;
    }

    internal static bool HasEmbeddedAsset(string cleanPath)
    {
        try
        {
            if (!TryResolveSafeRelativeAssetPath(cleanPath, out string resolved))
                return false;
            return _embeddedResourceLookup.Value.ContainsKey(resolved);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryLoadFromEmbedded(string cleanPath, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        try
        {
            var lookup = _embeddedResourceLookup.Value;
            if (lookup.TryGetValue(cleanPath, out string? resName))
            {
                using var stream = typeof(LexClassStudioService).Assembly.GetManifestResourceStream(resName);
                if (stream != null)
                {
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    bytes = ms.ToArray();
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    public static string GetMimeType(string relativePath)
    {
        string ext = Path.GetExtension(relativePath).ToLowerInvariant();
        return ext switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".js" or ".mjs" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" or ".map" => "application/json; charset=utf-8",
            ".webp" => "image/webp",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            ".woff2" => "font/woff2",
            ".woff" => "font/woff",
            ".ttf" => "font/ttf",
            ".wav" => "audio/wav",
            ".mp3" => "audio/mpeg",
            ".ogg" => "audio/ogg",
            ".wasm" => "application/wasm",
            _ => "application/octet-stream"
        };
    }

    public static string BuildResponseHeaders(string mimeType) =>
        $"Content-Type: {mimeType}\r\n" +
        $"Cache-Control: no-store\r\n" +
        $"Content-Security-Policy: {ContentSecurityPolicy}\r\n" +
        $"X-Content-Type-Options: nosniff\r\n" +
        $"Access-Control-Allow-Origin: https://{Host}";

    public static IReadOnlyList<LexClassWorldManifestEntry> ParseManifestJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<LexClassWorldManifestEntry>();

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("worlds", out var worldsArr) || worldsArr.ValueKind != JsonValueKind.Array)
            return Array.Empty<LexClassWorldManifestEntry>();

        var result = new List<LexClassWorldManifestEntry>();
        foreach (var el in worldsArr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            string id = el.TryGetProperty("id", out var idP) ? (idP.GetString() ?? "").Trim() : "";
            if (!IsValidWorldId(id)) continue;

            string version = el.TryGetProperty("version", out var vP) ? (vP.GetString() ?? "1.0.0").Trim() : "1.0.0";
            string status = el.TryGetProperty("status", out var sP) ? (sP.GetString() ?? "ready").Trim() : "ready";
            string? url = el.TryGetProperty("downloadUrl", out var uP) ? uP.GetString()?.Trim() : null;
            string? sha = el.TryGetProperty("sha256", out var shP) ? shP.GetString()?.Trim().ToLowerInvariant() : null;
            long? sz = el.TryGetProperty("sizeBytes", out var szP) && szP.TryGetInt64(out long sb) ? sb : null;
            string? lbl = el.TryGetProperty("sizeLabel", out var lbP) ? lbP.GetString()?.Trim() : null;
            string? note = el.TryGetProperty("note", out var nP) ? nP.GetString()?.Trim() : null;

            result.Add(new LexClassWorldManifestEntry(id, version, status, url, sha, sz, lbl, note));
        }

        return result;
    }

    public static Dictionary<string, LexClassWorldUiState> BuildWorldsStateMap()
    {
        var installed = LoadInstalledPacksInternal().ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        List<LexClassWorldManifestEntry> manifest;
        lock (_stateLock)
        {
            manifest = new List<LexClassWorldManifestEntry>(_cachedManifest);
        }

        var map = new Dictionary<string, LexClassWorldUiState>(StringComparer.OrdinalIgnoreCase);
        string builtInVer = UpdateManager.CurrentVersionString;

        foreach (string worldId in BuiltInReadyWorldIds)
        {
            installed.TryGetValue(worldId, out var pack);
            string localVer = pack?.Version ?? builtInVer;
            map[worldId] = new LexClassWorldUiState("installed", localVer, null, "Listo offline en este equipo", null);
        }

        foreach (var entry in manifest)
        {
            if (_busyWorldIds.ContainsKey(entry.Id))
            {
                int? livePct = _busyWorldProgress.TryGetValue(entry.Id, out int p) ? p : null;
                map[entry.Id] = new LexClassWorldUiState("installing", null, entry.Version, "Descargando paquete...", entry.SizeLabel, livePct);
                continue;
            }

            bool isBuiltIn = BuiltInReadyWorldIds.Contains(entry.Id, StringComparer.OrdinalIgnoreCase);
            installed.TryGetValue(entry.Id, out var pack);
            string? currentLocalVer = pack?.Version ?? (isBuiltIn ? builtInVer : null);

            if (!string.IsNullOrEmpty(currentLocalVer))
            {
                bool hasNewer = IsNewerSemanticVersion(entry.Version, currentLocalVer);
                map[entry.Id] = hasNewer
                    ? new LexClassWorldUiState("update", currentLocalVer, entry.Version, entry.Note ?? $"Nueva versión {entry.Version}", entry.SizeLabel)
                    : new LexClassWorldUiState("installed", currentLocalVer, entry.Version, entry.Note ?? "Actualizado offline", entry.SizeLabel);
            }
            else if (string.Equals(entry.Status, "ready", StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrWhiteSpace(entry.DownloadUrl) &&
                     UpdateManager.IsTrustedGitHubReleaseDownloadUrl(entry.DownloadUrl))
            {
                map[entry.Id] = new LexClassWorldUiState("download", null, entry.Version, entry.Note ?? "Disponible para descargar offline", entry.SizeLabel);
            }
        }

        return map;
    }

    public static string GetEffectiveDisplayVersion()
    {
        string best = UpdateManager.CurrentVersionString;
        foreach (var pack in LoadInstalledPacksInternal())
        {
            if (!string.IsNullOrWhiteSpace(pack.Version) && IsNewerSemanticVersion(pack.Version, best))
            {
                best = pack.Version.Trim().TrimStart('v', 'V');
            }
        }
        return best;
    }

    public static IReadOnlyList<LexClassPendingWorldInfo> GetPendingWorldActions()
    {
        var map = BuildWorldsStateMap();
        var list = new List<LexClassPendingWorldInfo>();
        foreach (var kv in map)
        {
            bool isUpdate = string.Equals(kv.Value.Status, "update", StringComparison.OrdinalIgnoreCase);
            bool isDownload = string.Equals(kv.Value.Status, "download", StringComparison.OrdinalIgnoreCase);
            if (!isUpdate && !isDownload) continue;

            list.Add(new LexClassPendingWorldInfo(
                Id: kv.Key,
                LocalVersion: kv.Value.LocalVersion,
                RemoteVersion: kv.Value.RemoteVersion ?? UpdateManager.CurrentVersionString,
                Note: kv.Value.Note,
                SizeLabel: kv.Value.SizeLabel,
                IsUpdate: isUpdate));
        }
        return list;
    }

    public static (int UpdatesAvailable, int NewWorldsAvailable) CountAvailableWorldUpdates()
    {
        var map = BuildWorldsStateMap();
        int updates = 0;
        int downloads = 0;
        foreach (var kv in map.Values)
        {
            if (string.Equals(kv.Status, "update", StringComparison.OrdinalIgnoreCase)) updates++;
            else if (string.Equals(kv.Status, "download", StringComparison.OrdinalIgnoreCase)) downloads++;
        }
        return (updates, downloads);
    }

    public static string BuildInjectStateScriptJson()
    {
        var payload = new
        {
            worlds = BuildWorldsStateMap(),
            engineVersion = GetEffectiveDisplayVersion()
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    public static async Task<bool> RefreshRemoteCatalogAsync(bool forceOnline = false, CancellationToken ct = default)
    {
        try
        {
            if (!forceOnline && _onlineCheckPerformed) return _cachedManifest.Count > 0;
            if (!forceOnline && string.Equals(SettingsManager.Current?.AutoUpdateCheckMode, PrivacyProfiles.AutoUpdateManual, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            _onlineCheckPerformed = true;
            using var catalogCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            catalogCts.CancelAfter(TimeSpan.FromSeconds(15));
            using var resp = await _http.GetAsync(WorldsManifestUrl, HttpCompletionOption.ResponseContentRead, catalogCts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;

            if (resp.Content.Headers.ContentLength is long len && len > MaxManifestBytes)
                return false;

            string json = await resp.Content.ReadAsStringAsync(catalogCts.Token).ConfigureAwait(false);
            if (Encoding.UTF8.GetByteCount(json) > MaxManifestBytes) return false;

            var parsed = ParseManifestJson(json);
            lock (_stateLock)
            {
                _cachedManifest = parsed.ToList();
            }
            return parsed.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    public static Task<(bool Ok, string Message)> InstallOrUpdateWorldAsync(string worldId, CancellationToken ct = default)
        => InstallOrUpdateWorldAsync(worldId, progress: null, ct);

    public static async Task<(bool Ok, string Message)> InstallOrUpdateWorldAsync(
        string worldId,
        IProgress<(int Percent, string Stage)>? progress,
        CancellationToken ct = default)
    {
        if (!IsValidWorldId(worldId))
            return (false, "Identificador de mundo no válido.");

        if (!_busyWorldIds.TryAdd(worldId, worldId))
            return (false, "Ya hay una descarga en curso para este mundo.");

        _busyWorldProgress[worldId] = 0;
        progress?.Report((0, "Consultando catálogo en GitHub..."));

        string tempZip = Path.Combine(Path.GetTempPath(), $"lexclass_{worldId}_{Guid.NewGuid():N}.zip");
        try
        {
            await RefreshRemoteCatalogAsync(forceOnline: true, ct).ConfigureAwait(false);

            LexClassWorldManifestEntry? entry;
            lock (_stateLock)
            {
                entry = _cachedManifest.FirstOrDefault(w => string.Equals(w.Id, worldId, StringComparison.OrdinalIgnoreCase));
            }

            if (entry == null || string.IsNullOrWhiteSpace(entry.DownloadUrl) || string.IsNullOrWhiteSpace(entry.Sha256))
            {
                return (false, "No hay paquete descargable publicado todavía o no hay conexión con GitHub. Puedes seguir usando todo el contenido local offline.");
            }

            if (!UpdateManager.IsTrustedGitHubReleaseDownloadUrl(entry.DownloadUrl))
            {
                return (false, "La URL del paquete no pertenece a GitHub Releases oficial de Lex Junior.");
            }

            using (var resp = await _http.GetAsync(entry.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                long expectedBytes = resp.Content.Headers.ContentLength ?? entry.SizeBytes ?? 0;
                if (expectedBytes > MaxWorldZipBytes)
                    return (false, "El paquete supera el límite de tamaño permitido.");

                await using var netStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None);
                byte[] buffer = new byte[64 * 1024];
                long totalRead = 0;
                int lastReported = -1;
                while (true)
                {
                    int read = await netStream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                    if (read <= 0) break;
                    totalRead += read;
                    if (totalRead > MaxWorldZipBytes)
                        return (false, "Descarga abortada: tamaño inesperado.");
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);

                    int pct = expectedBytes > 0
                        ? Math.Clamp((int)((totalRead * 90L) / expectedBytes), 1, 90)
                        : Math.Min(85, 10 + (int)(totalRead / (256 * 1024)));
                    if (pct != lastReported)
                    {
                        lastReported = pct;
                        _busyWorldProgress[worldId] = pct;
                        progress?.Report((pct, expectedBytes > 0
                            ? $"Descargando ({DownloadEntry.FormatSize(totalRead)} / {DownloadEntry.FormatSize(expectedBytes)})..."
                            : $"Descargando ({DownloadEntry.FormatSize(totalRead)})..."));
                    }
                }
            }

            _busyWorldProgress[worldId] = 92;
            progress?.Report((92, "Verificando integridad criptográfica SHA-256..."));

            string actualSha = await ComputeFileSha256HexAsync(tempZip, ct).ConfigureAwait(false);
            if (!string.Equals(actualSha, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return (false, "Verificación SHA-256 fallida: paquete rechazado por seguridad.");
            }

            _busyWorldProgress[worldId] = 96;
            progress?.Report((96, "Extrayendo e instalando para uso 100 % offline..."));

            string worldRoot = Path.Combine(PacksRootDir, worldId);
            string stagingDir = Path.Combine(PacksRootDir, $"{worldId}.staging_{Guid.NewGuid():N}");
            string webTargetDir = Path.Combine(stagingDir, "web");
            Directory.CreateDirectory(webTargetDir);

            ExtractVerifiedZipSafely(tempZip, webTargetDir);

            if (Directory.Exists(worldRoot))
            {
                string backup = worldRoot + ".bak";
                try { if (Directory.Exists(backup)) Directory.Delete(backup, true); } catch { }
                Directory.Move(worldRoot, backup);
                try
                {
                    Directory.Move(stagingDir, worldRoot);
                    Directory.Delete(backup, true);
                }
                catch
                {
                    try { Directory.Move(backup, worldRoot); } catch { }
                    throw;
                }
            }
            else
            {
                Directory.Move(stagingDir, worldRoot);
            }

            SaveInstalledPackRecord(new LexClassWorldLocalPack(worldId, entry.Version, DateTime.UtcNow.ToString("O")));
            _busyWorldProgress[worldId] = 100;
            progress?.Report((100, $"¡Actualizado a v{entry.Version}!"));
            return (true, $"Mundo '{worldId}' actualizado a v{entry.Version} y guardado offline.");
        }
        catch (Exception ex)
        {
            return (false, $"No se pudo completar la descarga ({ex.Message}).");
        }
        finally
        {
            _busyWorldIds.TryRemove(worldId, out _);
            _busyWorldProgress.TryRemove(worldId, out _);
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    public static void ExtractVerifiedZipSafely(string zipPath, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        string fullDestRoot = Path.GetFullPath(destinationDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                              + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(zipPath);
        int fileCount = 0;
        long totalBytes = 0;

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue; // directorio

            if (++fileCount > MaxExtractedFiles)
                throw new InvalidDataException("Demasiados archivos dentro del paquete de mundo.");

            totalBytes += entry.Length;
            if (totalBytes > MaxExtractedWorldBytes)
                throw new InvalidDataException("El contenido descomprimido supera el límite permitido.");

            string cleanEntry = entry.FullName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            string destFile = Path.GetFullPath(Path.Combine(fullDestRoot, cleanEntry));

            if (!destFile.StartsWith(fullDestRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Entrada ZIP insegura bloqueada (ZipSlip): {entry.FullName}");

            string? parent = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            entry.ExtractToFile(destFile, overwrite: true);
        }
    }

    internal static bool IsNewerSemanticVersion(string remoteVer, string localVer)
    {
        if (Version.TryParse(remoteVer.TrimStart('v', 'V'), out var r) &&
            Version.TryParse(localVer.TrimStart('v', 'V'), out var l))
        {
            return r > l;
        }
        return !string.Equals(remoteVer.Trim(), localVer.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ComputeFileSha256HexAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = SHA256.Create();
        byte[] hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static List<LexClassWorldLocalPack> LoadInstalledPacksInternal()
    {
        lock (_stateLock)
        {
            try
            {
                if (!File.Exists(StateFilePath)) return new List<LexClassWorldLocalPack>();
                string json = File.ReadAllText(StateFilePath);
                return JsonSerializer.Deserialize<List<LexClassWorldLocalPack>>(json) ?? new List<LexClassWorldLocalPack>();
            }
            catch
            {
                return new List<LexClassWorldLocalPack>();
            }
        }
    }

    private static void SaveInstalledPackRecord(LexClassWorldLocalPack record)
    {
        lock (_stateLock)
        {
            var list = LoadInstalledPacksInternal()
                .Where(p => !string.Equals(p.Id, record.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();
            list.Add(record);

            Directory.CreateDirectory(LexClassDataDir);
            string tmp = StateFilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, StateFilePath, overwrite: true);
        }
    }
}
