using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LexBrowser.Core.Tv.Addons;

/// <summary>
/// Analizador y validador estricto de repositorios JSON de addons y comparador semántico de versiones.
/// </summary>
public static class LexAddonRepoParser
{
    public const int MaxRepoBytes = 1024 * 1024;
    public const int MaxRepoEntries = 100;

    public static bool TryNormalizeRepoInput(string? rawInput, out string normalizedUrl, out string? error)
    {
        normalizedUrl = "";
        if (!LexAddonManifestParser.TryNormalizeHttpUri(rawInput, allowStremioScheme: true, out var uri, out error) || uri == null)
            return false;

        normalizedUrl = uri.AbsoluteUri;
        return true;
    }

    public static string ComputeStableRepoId(string canonicalRepoUrl)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes((canonicalRepoUrl ?? "").Trim().ToLowerInvariant()));
        return "repo_" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>
    /// Compara versiones tipo SemVer ("1.10.2" vs "1.2.9"). Devuelve &gt; 0 si candidate &gt; current.
    /// </summary>
    public static int CompareVersions(string? candidate, string? current)
    {
        var a = ParseNumericParts(candidate);
        var b = ParseNumericParts(current);
        int len = Math.Max(a.Length, b.Length);

        for (int i = 0; i < len; i++)
        {
            int va = i < a.Length ? a[i] : 0;
            int vb = i < b.Length ? b[i] : 0;
            if (va != vb) return va.CompareTo(vb);
        }

        string preA = ExtractPreRelease(candidate);
        string preB = ExtractPreRelease(current);
        bool aHasPre = preA.Length > 0;
        bool bHasPre = preB.Length > 0;
        if (aHasPre != bHasPre)
            return aHasPre ? -1 : 1; // Release estable > pre-release

        if (aHasPre)
            return string.Compare(preA, preB, StringComparison.OrdinalIgnoreCase);

        if (a.Length == 0 && b.Length == 0)
            return string.Compare((candidate ?? "").Trim(), (current ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

        return 0;
    }

    private static string ExtractPreRelease(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "";
        string trimmed = version.Trim();
        int plus = trimmed.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0) trimmed = trimmed[..plus];
        int dash = trimmed.IndexOf('-', StringComparison.Ordinal);
        return dash >= 0 && dash + 1 < trimmed.Length ? trimmed[(dash + 1)..] : "";
    }

    public static (bool Success, LexAddonRepoFeed? Feed, string? Error) Parse(
        string? json,
        string canonicalRepoUrl,
        bool repoAllowsLoopback)
    {
        if (string.IsNullOrWhiteSpace(json))
            return (false, null, "El repositorio devolvió una respuesta vacía.");

        string trimmed = json.Trim();
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
            return (false, null, "El recurso recibido no es un repositorio JSON válido.");

        try
        {
            using var doc = JsonDocument.Parse(trimmed, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 20
            });

            Uri repoUri = new(canonicalRepoUrl);
            string baseUrl = repoUri.GetLeftPart(UriPartial.Authority);

            string feedName = repoUri.Host;
            string feedDesc = "";
            string repoId = "";
            JsonElement entriesArr;

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                entriesArr = doc.RootElement;
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var root = doc.RootElement;
                repoId = ReadCleanString(root, "id", 80);
                string parsedName = ReadCleanString(root, "name", 80);
                if (!string.IsNullOrWhiteSpace(parsedName))
                    feedName = parsedName;
                feedDesc = ReadCleanString(root, "description", 320);

                if (root.TryGetProperty("addons", out var aProp) && aProp.ValueKind == JsonValueKind.Array)
                    entriesArr = aProp;
                else if (root.TryGetProperty("items", out var iProp) && iProp.ValueKind == JsonValueKind.Array)
                    entriesArr = iProp;
                else
                    return (false, null, "El repositorio JSON no contiene una lista 'addons'.");
            }
            else
            {
                return (false, null, "Formato JSON de repositorio no compatible.");
            }

            if (string.IsNullOrWhiteSpace(repoId))
                repoId = ComputeStableRepoId(canonicalRepoUrl);

            var entries = new List<LexAddonRepoEntry>();
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in entriesArr.EnumerateArray())
            {
                if (entries.Count >= MaxRepoEntries) break;
                if (item.ValueKind != JsonValueKind.Object) continue;

                var parsedEntry = ParseRepoEntry(item, baseUrl, repoAllowsLoopback);
                if (parsedEntry == null) continue;
                if (!seenUrls.Add(parsedEntry.ManifestUrl)) continue;

                entries.Add(parsedEntry);
            }

            return (true, new LexAddonRepoFeed
            {
                Id = repoId,
                Name = feedName,
                Description = feedDesc,
                Entries = entries
            }, null);
        }
        catch (JsonException)
        {
            return (false, null, "No se pudo interpretar el JSON del repositorio.");
        }
    }

    private static LexAddonRepoEntry? ParseRepoEntry(JsonElement item, string fallbackBaseUrl, bool repoAllowsLoopback)
    {
        string rawUrl = ReadCleanString(item, "transportUrl", 2048);
        if (string.IsNullOrWhiteSpace(rawUrl))
            rawUrl = ReadCleanString(item, "manifestUrl", 2048);
        if (string.IsNullOrWhiteSpace(rawUrl))
            rawUrl = ReadCleanString(item, "url", 2048);

        if (!LexAddonManifestParser.TryNormalizeManifestInput(rawUrl, out string normalizedManifestUrl, out _))
            return null;

        // Barrera anti-SSRF crítica: un repositorio remoto NUNCA puede inyectar manifiestos hacia localhost/LAN.
        if (!LexAddonClient.ValidateAddonEndpoint(normalizedManifestUrl, repoAllowsLoopback, out var safeUri, out _, out _) || safeUri == null)
            return null;

        string canonicalManifest = safeUri.AbsoluteUri;
        string manifestBase = LexAddonManifestParser.ExtractBaseUrl(safeUri);

        LexAddonManifest? embeddedManifest = null;
        if (item.TryGetProperty("manifest", out var mEl) && mEl.ValueKind == JsonValueKind.Object)
        {
            var (_, parsedManifest, _) = LexAddonManifestParser.Parse(mEl.GetRawText(), manifestBase);
            embeddedManifest = parsedManifest;
        }

        string id = embeddedManifest?.Id ?? ReadCleanString(item, "id", 80);
        string name = embeddedManifest?.Name ?? ReadCleanString(item, "name", 80);
        if (string.IsNullOrWhiteSpace(name))
            name = !string.IsNullOrWhiteSpace(id) ? id : safeUri.Host;

        string version = embeddedManifest?.Version ?? ReadCleanString(item, "version", 32);
        if (string.IsNullOrWhiteSpace(version)) version = "1.0.0";

        string desc = embeddedManifest?.Description ?? ReadCleanString(item, "description", 320);
        string? logo = embeddedManifest?.Logo ??
            LexAddonManifestParser.NormalizeOptionalImageUrl(ReadCleanString(item, "logo", 512), manifestBase ?? fallbackBaseUrl);

        var types = embeddedManifest?.Types ?? ReadStringList(item, "types", 16, 32);
        var resources = embeddedManifest?.Resources.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            ?? ReadStringList(item, "resources", 16, 32);

        return new LexAddonRepoEntry
        {
            AddonId = id,
            Name = name,
            Version = version,
            Description = desc,
            Logo = logo,
            ManifestUrl = canonicalManifest,
            DisplayUrl = LexAddonManifestParser.RedactManifestUrlForDisplay(canonicalManifest),
            Types = types,
            ResourceNames = resources
        };
    }

    private static int[] ParseNumericParts(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return Array.Empty<int>();
        string core = version.Trim().TrimStart('v', 'V');
        int dash = core.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0) core = core[..dash];
        int plus = core.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0) core = core[..plus];

        string[] parts = core.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var nums = new int[Math.Min(parts.Length, 4)];
        for (int i = 0; i < nums.Length; i++)
        {
            int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out nums[i]);
        }
        return nums;
    }

    private static string ReadCleanString(JsonElement obj, string propName, int maxLen)
    {
        if (!obj.TryGetProperty(propName, out var prop) || prop.ValueKind != JsonValueKind.String)
            return "";
        return TvUrlGuard.SanitizeHeaderValue(prop.GetString(), maxLen);
    }

    private static List<string> ReadStringList(JsonElement obj, string propName, int maxCount, int maxLen)
    {
        var result = new List<string>();
        if (!obj.TryGetProperty(propName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in arr.EnumerateArray())
        {
            if (result.Count >= maxCount) break;
            if (item.ValueKind != JsonValueKind.String) continue;
            string clean = TvUrlGuard.SanitizeHeaderValue(item.GetString(), maxLen);
            if (!string.IsNullOrWhiteSpace(clean) && !result.Contains(clean, StringComparer.OrdinalIgnoreCase))
                result.Add(clean);
        }

        return result;
    }
}

