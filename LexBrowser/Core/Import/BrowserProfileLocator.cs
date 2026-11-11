using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LexBrowser.Core.Import;

public sealed record BrowserSpec(
    string BrowserName,
    string UserDataDirectory,
    bool IsRootSingleProfile = false);

public sealed record BrowserProfileInfo(
    string BrowserName,
    string ProfileId,
    string DisplayName,
    string UserDataDirectory,
    string ProfileDirectory,
    bool HasBookmarks,
    bool HasHistory,
    bool HasPasswords)
{
    public string DisplayLabel => $"{BrowserName} — {DisplayName}";
}

/// <summary>
/// Localiza instalaciones locales y perfiles de navegadores basados en Chromium soportados.
/// </summary>
public static class BrowserProfileLocator
{
    public static IReadOnlyList<BrowserSpec> GetDefaultBrowserSpecs()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        return new List<BrowserSpec>
        {
            new("Google Chrome", Path.Combine(localAppData, "Google", "Chrome", "User Data")),
            new("Microsoft Edge", Path.Combine(localAppData, "Microsoft", "Edge", "User Data")),
            new("Brave", Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data")),
            new("Vivaldi", Path.Combine(localAppData, "Vivaldi", "User Data")),
            new("Opera", Path.Combine(appData, "Opera Software", "Opera Stable"), IsRootSingleProfile: true),
            new("Opera GX", Path.Combine(appData, "Opera Software", "Opera GX Stable"), IsRootSingleProfile: true)
        };
    }

    public static IReadOnlyList<BrowserProfileInfo> DiscoverProfiles(
        IEnumerable<BrowserSpec>? specs = null,
        ICollection<string>? warnings = null)
    {
        var list = new List<BrowserProfileInfo>();
        foreach (var spec in specs ?? GetDefaultBrowserSpecs())
        {
            if (string.IsNullOrWhiteSpace(spec.UserDataDirectory) || !Directory.Exists(spec.UserDataDirectory))
            {
                continue;
            }

            if (spec.IsRootSingleProfile)
            {
                var rootProfile = InspectProfileDirectory(
                    spec.BrowserName,
                    "Predeterminado",
                    "Perfil principal",
                    spec.UserDataDirectory,
                    spec.UserDataDirectory);
                if (rootProfile != null)
                {
                    list.Add(rootProfile);
                }
                continue;
            }

            var displayNames = ReadProfileDisplayNames(Path.Combine(spec.UserDataDirectory, "Local State"), warnings);

            var candidates = new List<string>();
            string defaultDir = Path.Combine(spec.UserDataDirectory, "Default");
            if (Directory.Exists(defaultDir))
            {
                candidates.Add(defaultDir);
            }

            try
            {
                foreach (var dir in Directory.GetDirectories(spec.UserDataDirectory, "Profile *")
                             .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    candidates.Add(dir);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings?.Add($"No se pudieron enumerar los perfiles de {spec.BrowserName}: {ex.Message}");
            }

            foreach (string profileDir in candidates)
            {
                string profileId = Path.GetFileName(profileDir);
                string fallbackName = string.Equals(profileId, "Default", StringComparison.OrdinalIgnoreCase)
                    ? "Predeterminado"
                    : profileId;
                string friendly = displayNames.TryGetValue(profileId, out string? mapped) && !string.IsNullOrWhiteSpace(mapped)
                    ? mapped
                    : fallbackName;

                var info = InspectProfileDirectory(spec.BrowserName, profileId, friendly, spec.UserDataDirectory, profileDir);
                if (info != null)
                {
                    list.Add(info);
                }
            }
        }

        return list;
    }

    public static Dictionary<string, string> ReadProfileDisplayNames(
        string localStatePath,
        ICollection<string>? warnings = null)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(localStatePath) || !File.Exists(localStatePath))
        {
            return result;
        }

        try
        {
            using var fs = new FileStream(localStatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            if (!doc.RootElement.TryGetProperty("profile", out var profileElem) ||
                !profileElem.TryGetProperty("info_cache", out var cacheElem) ||
                cacheElem.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var entry in cacheElem.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                string? name = null;
                if (entry.Value.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                {
                    name = nameProp.GetString();
                }
                if (string.IsNullOrWhiteSpace(name) &&
                    entry.Value.TryGetProperty("shortcut_name", out var shortcutProp) &&
                    shortcutProp.ValueKind == JsonValueKind.String)
                {
                    name = shortcutProp.GetString();
                }
                if (!string.IsNullOrWhiteSpace(name))
                {
                    result[entry.Name] = name.Trim();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            warnings?.Add($"No se pudo leer Local State en {Path.GetDirectoryName(localStatePath)}: {ex.Message}");
        }

        return result;
    }

    private static BrowserProfileInfo? InspectProfileDirectory(
        string browserName,
        string profileId,
        string displayName,
        string userDataDir,
        string profileDir)
    {
        bool hasBookmarks = File.Exists(Path.Combine(profileDir, "Bookmarks"));
        bool hasHistory = File.Exists(Path.Combine(profileDir, "History"));
        bool hasPasswords = File.Exists(Path.Combine(profileDir, "Login Data"));

        if (!hasBookmarks && !hasHistory && !hasPasswords)
        {
            return null;
        }

        return new BrowserProfileInfo(
            BrowserName: browserName,
            ProfileId: profileId,
            DisplayName: displayName,
            UserDataDirectory: userDataDir,
            ProfileDirectory: profileDir,
            HasBookmarks: hasBookmarks,
            HasHistory: hasHistory,
            HasPasswords: hasPasswords);
    }
}

