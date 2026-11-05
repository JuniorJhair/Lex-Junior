using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace LexBrowser.Core;

public record UpdateReleaseInfo(
    string VersionTag,
    Version ParsedVersion,
    string ReleaseNotes,
    string DownloadUrl,
    string HtmlUrl);

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

    public static async Task<UpdateReleaseInfo?> CheckForUpdatesAsync()
    {
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

                    if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var asset in assetsProp.EnumerateArray())
                        {
                            string assetName = asset.TryGetProperty("name", out var nProp) ? (nProp.GetString() ?? "") : "";
                            string browserUrl = asset.TryGetProperty("browser_download_url", out var bProp) ? (bProp.GetString() ?? "") : "";
                            if (assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(browserUrl))
                            {
                                downloadUrl = browserUrl;
                                break;
                            }
                        }
                    }

                    var info = new UpdateReleaseInfo(tagName, remoteVersion, notes, downloadUrl, htmlUrl);
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

    public static async Task<bool> DownloadAndApplyUpdateAsync(UpdateReleaseInfo update, IProgress<int>? progress = null)
    {
        try
        {
            string tempSetupPath = Path.Combine(Path.GetTempPath(), $"LexJuniorSetup_{update.ParsedVersion}.exe");

            using var resp = await _httpClient.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();

            long totalBytes = resp.Content.Headers.ContentLength ?? -1L;
            await using (var netStream = await resp.Content.ReadAsStreamAsync())
            await using (var fileStream = new FileStream(tempSetupPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
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
            }

            progress?.Report(100);

            // Ejecutar el instalador descargado en modo actualización silenciosa (--silent-update)
            Process.Start(new ProcessStartInfo
            {
                FileName = tempSetupPath,
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
            return false;
        }
    }
}
