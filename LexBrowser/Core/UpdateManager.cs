using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace LexBrowser.Core;

public record UpdateReleaseInfo(
    string VersionTag,
    Version ParsedVersion,
    string ReleaseNotes,
    string DownloadUrl,
    string HtmlUrl,
    string? ExpectedSha256Hex = null,
    long ExpectedSizeBytes = 0);

public static class UpdateManager
{
    public const string GitHubOwner = "JuniorJhair";
    public const string GitHubRepo = "Lex-Junior";

    public const string LatestReleaseApiUrl = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
    public const string LatestReleaseWebUrl = $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/latest";
    public const string DirectLatestInstallerUrl = $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/latest/download/LexJuniorSetup.exe";

    private static readonly HttpClient _httpClient = CreateHttpClient();

    public static UpdateReleaseInfo? AvailableUpdate { get; private set; }
    public static event Action<UpdateReleaseInfo>? UpdateDiscovered;

    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v != null ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(1, 0, 0);
        }
    }

    public static string CurrentVersionString => $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "LexJuniorBrowser-Updater/1.0");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        return client;
    }

    public static Version? ParseVersionTag(string? rawTag)
    {
        if (string.IsNullOrWhiteSpace(rawTag)) return null;
        string cleaned = rawTag.Trim().TrimStart('v', 'V');
        int dashIdx = cleaned.IndexOf('-');
        if (dashIdx > 0) cleaned = cleaned.Substring(0, dashIdx);

        if (Version.TryParse(cleaned, out var parsed))
        {
            return new Version(
                parsed.Major,
                Math.Max(0, parsed.Minor),
                Math.Max(0, parsed.Build));
        }
        return null;
    }

    public static async Task<UpdateReleaseInfo?> CheckForUpdatesAsync(bool isUserInitiated = false)
    {
        if (!isUserInitiated &&
            string.Equals(
                PrivacyProfiles.NormalizeAutoUpdateCheckMode(SettingsManager.Current.AutoUpdateCheckMode),
                PrivacyProfiles.AutoUpdateManual,
                StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using var resp = await _httpClient.GetAsync(LatestReleaseApiUrl, HttpCompletionOption.ResponseContentRead);
            if (resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string tagName = root.TryGetProperty("tag_name", out var tagProp) ? (tagProp.GetString() ?? "") : "";
                var remoteVersion = ParseVersionTag(tagName);
                if (remoteVersion != null)
                {
                    if (remoteVersion <= CurrentVersion)
                    {
                        AvailableUpdate = null;
                        return null;
                    }

                    string notes = root.TryGetProperty("body", out var bodyProp) ? (bodyProp.GetString() ?? "") : "";
                    string htmlUrl = root.TryGetProperty("html_url", out var htmlProp) ? (htmlProp.GetString() ?? "") : "";
                    string downloadUrl = DirectLatestInstallerUrl;
                    string? expectedSha256 = null;
                    long expectedSize = 0;

                    if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var asset in assetsProp.EnumerateArray())
                        {
                            string assetName = asset.TryGetProperty("name", out var nProp) ? (nProp.GetString() ?? "") : "";
                            string browserUrl = asset.TryGetProperty("browser_download_url", out var bProp) ? (bProp.GetString() ?? "") : "";
                            if (assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(browserUrl))
                            {
                                downloadUrl = browserUrl;
                                if (asset.TryGetProperty("digest", out var dProp))
                                    expectedSha256 = ParseAssetSha256Digest(dProp.GetString());
                                if (asset.TryGetProperty("size", out var sProp) && sProp.TryGetInt64(out long sz) && sz > 0)
                                    expectedSize = sz;
                                break;
                            }
                        }
                    }

                    var info = new UpdateReleaseInfo(tagName, remoteVersion, notes, downloadUrl, htmlUrl, expectedSha256, expectedSize);
                    AvailableUpdate = info;
                    UpdateDiscovered?.Invoke(info);
                    return info;
                }
            }
        }
        catch { }

        // Fallback resistente a límites de tasa (Rate Limit 403) de la API de GitHub
        return await CheckForUpdatesViaHtmlRedirectAsync();
    }

    private static async Task<UpdateReleaseInfo?> CheckForUpdatesViaHtmlRedirectAsync()
    {
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

            using var resp = await client.GetAsync(LatestReleaseWebUrl);
            var loc = resp.Headers.Location;
            if (loc == null) return null;

            string redirectUrl = loc.ToString();
            int tagIdx = redirectUrl.LastIndexOf("/tag/", StringComparison.OrdinalIgnoreCase);
            if (tagIdx < 0) return null;

            string tagName = redirectUrl.Substring(tagIdx + 5).Trim();
            var remoteVersion = ParseVersionTag(tagName);
            if (remoteVersion == null || remoteVersion <= CurrentVersion)
            {
                AvailableUpdate = null;
                return null;
            }

            var info = new UpdateReleaseInfo(
                tagName,
                remoteVersion,
                $"Nueva versión {tagName} de Lex Junior disponible.",
                DirectLatestInstallerUrl,
                redirectUrl);

            AvailableUpdate = info;
            UpdateDiscovered?.Invoke(info);
            return info;
        }
        catch
        {
            return null;
        }
    }

    public static string? ParseAssetSha256Digest(string? rawDigest)
    {
        if (string.IsNullOrWhiteSpace(rawDigest)) return null;
        string s = rawDigest.Trim();
        if (s.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            s = s[7..].Trim();

        if (s.Length != 64) return null;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex) return null;
        }
        return s.ToLowerInvariant();
    }

    public static bool IsTrustedGitHubReleaseDownloadUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return false;

        return uri.Scheme == Uri.UriSchemeHttps &&
               string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
               uri.AbsolutePath.StartsWith($"/{GitHubOwner}/{GitHubRepo}/releases/", StringComparison.OrdinalIgnoreCase);
    }

    public static string GetUpdatesDirectory() =>
        Path.Combine(InstallerService.UserDataDir, "Updates");

    public static async Task<bool> VerifyDownloadedInstallerAsync(string filePath, string? expectedSha256Hex, long expectedSizeBytes = 0)
    {
        try
        {
            string? normalizedExpected = ParseAssetSha256Digest(expectedSha256Hex);
            if (string.IsNullOrEmpty(normalizedExpected) || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return false;

            var fi = new FileInfo(filePath);
            if (fi.Length <= 0 || (expectedSizeBytes > 0 && fi.Length != expectedSizeBytes))
                return false;

            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            byte[] hashBytes = await SHA256.HashDataAsync(fs).ConfigureAwait(false);
            string actualHex = Convert.ToHexString(hashBytes).ToLowerInvariant();

            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actualHex),
                Encoding.ASCII.GetBytes(normalizedExpected));
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(string? Sha256Hex, long SizeBytes, string? BrowserUrl)> TryFetchTagAssetIntegrityAsync(string tagName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(tagName)) return (null, 0, null);
            string tagApi = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/tags/{Uri.EscapeDataString(tagName)}";
            using var resp = await _httpClient.GetAsync(tagApi, HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return (null, 0, null);

            string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string assetName = asset.TryGetProperty("name", out var nProp) ? (nProp.GetString() ?? "") : "";
                    string browserUrl = asset.TryGetProperty("browser_download_url", out var bProp) ? (bProp.GetString() ?? "") : "";
                    if (assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(browserUrl))
                    {
                        string? sha = asset.TryGetProperty("digest", out var dProp) ? ParseAssetSha256Digest(dProp.GetString()) : null;
                        long sz = (asset.TryGetProperty("size", out var sProp) && sProp.TryGetInt64(out long sVal) && sVal > 0) ? sVal : 0;
                        return (sha, sz, browserUrl);
                    }
                }
            }
        }
        catch { }
        return (null, 0, null);
    }

    public static async Task<bool> DownloadAndApplyUpdateAsync(UpdateReleaseInfo update, IProgress<int>? progress = null)
    {
        string? partSetupPath = null;
        string? finalSetupPath = null;
        try
        {
            InstallerService.CleanOldTempInstallers();

            string downloadUrl = update.DownloadUrl;
            string? expectedSha256 = ParseAssetSha256Digest(update.ExpectedSha256Hex);
            long expectedSize = update.ExpectedSizeBytes;

            if (string.IsNullOrEmpty(expectedSha256))
            {
                var (fetchedSha, fetchedSize, fetchedUrl) = await TryFetchTagAssetIntegrityAsync(update.VersionTag);
                if (!string.IsNullOrEmpty(fetchedSha))
                    expectedSha256 = fetchedSha;
                if (expectedSize <= 0 && fetchedSize > 0)
                    expectedSize = fetchedSize;
                if (!string.IsNullOrWhiteSpace(fetchedUrl) && IsTrustedGitHubReleaseDownloadUrl(fetchedUrl))
                    downloadUrl = fetchedUrl;
            }

            if (string.IsNullOrEmpty(expectedSha256) || !IsTrustedGitHubReleaseDownloadUrl(downloadUrl))
                return false;

            string updatesDir = GetUpdatesDirectory();
            Directory.CreateDirectory(updatesDir);
            finalSetupPath = Path.Combine(updatesDir, $"LexJuniorSetup_{update.ParsedVersion}.exe");
            partSetupPath = Path.Combine(updatesDir, $"LexJuniorSetup_{update.ParsedVersion}_{Guid.NewGuid():N}.part");

            using var resp = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();

            long totalBytes = resp.Content.Headers.ContentLength ?? (expectedSize > 0 ? expectedSize : -1L);
            await using (var netStream = await resp.Content.ReadAsStreamAsync())
            await using (var fileStream = new FileStream(partSetupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                byte[] buffer = new byte[81920];
                long totalRead = 0;
                int read;
                while ((read = await netStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, read);
                    totalRead += read;
                    if (totalBytes > 0)
                    {
                        int pct = (int)Math.Clamp((totalRead * 100L) / totalBytes, 0, 100);
                        progress?.Report(pct);
                    }
                }
                await fileStream.FlushAsync();
            }

            if (!await VerifyDownloadedInstallerAsync(partSetupPath, expectedSha256, expectedSize))
            {
                try { File.Delete(partSetupPath); } catch { }
                return false;
            }

            File.Move(partSetupPath, finalSetupPath, overwrite: true);
            partSetupPath = null;
            progress?.Report(100);

            // Ejecutar el instalador descargado y verificado en modo actualización silenciosa (--silent-update)
            Process.Start(new ProcessStartInfo
            {
                FileName = finalSetupPath,
                Arguments = "--silent-update",
                UseShellExecute = true
            });

            // Cerrar la instancia actual para permitir que el actualizador reemplace el ejecutable y reinicie
            Application.Current?.Dispatcher.Invoke(() =>
            {
                Application.Current.Shutdown();
            });

            return true;
        }
        catch
        {
            if (!string.IsNullOrEmpty(partSetupPath)) { try { File.Delete(partSetupPath); } catch { } }
            if (!string.IsNullOrEmpty(finalSetupPath)) { try { File.Delete(finalSetupPath); } catch { } }
            return false;
        }
    }
}
