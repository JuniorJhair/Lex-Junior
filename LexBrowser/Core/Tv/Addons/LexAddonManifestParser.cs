using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LexBrowser.Core.Tv.Addons;

/// <summary>
/// Analizador y validador estricto de manifiestos HTTP/JSON (compatibles con Lex Addons y protocolo Stremio).
/// </summary>
public static class LexAddonManifestParser
{
    public const int MaxTypes = 16;
    public const int MaxResources = 32;
    public const int MaxCatalogs = 64;
    public const int MaxExtrasPerCatalog = 16;
    public const int MaxOptionsPerExtra = 64;
    public const int MaxIdPrefixes = 32;

    private static readonly Regex AddonIdRegex = new(
        @"^[A-Za-z0-9._-]{2,80}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TokenLikeSegmentRegex = new(
        @"^[A-Za-z0-9+/_=%.-]{24,}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ExplicitNonPortSchemeRegex = new(
        @"^[A-Za-z][A-Za-z0-9+.-]*:(?:\/\/|(?!\d+(?:[\/?#]|$)))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Normaliza la URL de un manifiesto (permite pegar tanto ".../manifest.json" como la URL base del addon).
    /// </summary>
    public static bool TryNormalizeManifestInput(string? rawInput, out string normalizedUrl, out string? error)
    {
        normalizedUrl = "";
        if (!TryNormalizeHttpUri(rawInput, allowStremioScheme: true, out var uri, out error) || uri == null)
            return false;

        var builder = new UriBuilder(uri) { Fragment = "" };
        string path = builder.Path.TrimEnd('/');
        if (!path.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase) &&
            !path.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            path = string.IsNullOrEmpty(path) ? "/manifest.json" : $"{path}/manifest.json";
        }
        else if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        builder.Path = path;
        normalizedUrl = builder.Uri.AbsoluteUri;
        return true;
    }

    internal static bool TryNormalizeHttpUri(string? rawInput, bool allowStremioScheme, out Uri? uri, out string? error)
    {
        uri = null;
        error = null;

        if (string.IsNullOrWhiteSpace(rawInput))
        {
            error = "Ingresa una URL válida.";
            return false;
        }

        string trimmed = rawInput.Trim();
        if (allowStremioScheme && trimmed.StartsWith("stremio://", StringComparison.OrdinalIgnoreCase))
        {
            string rest = trimmed.Substring("stremio://".Length).TrimStart();
            if (rest.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                rest.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = rest;
            }
            else if (IsLoopbackHostPrefix(rest))
            {
                trimmed = "http://" + rest;
            }
            else
            {
                trimmed = "https://" + rest;
            }
        }
        else if (!ExplicitNonPortSchemeRegex.IsMatch(trimmed))
        {
            trimmed = "https://" + trimmed;
        }

        if (trimmed.Length > 2048 || !Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed))
        {
            error = "La URL proporcionada no es válida.";
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            error = "Solo se permiten esquemas http:// o https://.";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            error = "No se permiten credenciales incrustadas en la URL.";
            return false;
        }

        var builder = new UriBuilder(parsed) { Fragment = "" };
        uri = builder.Uri;
        return true;
    }

    private static bool IsLoopbackHostPrefix(string rest)
    {
        foreach (string prefix in new[] { "localhost", "127.0.0.1", "[::1]" })
        {
            if (rest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                if (rest.Length == prefix.Length) return true;
                char next = rest[prefix.Length];
                if (next is ':' or '/' or '?' or '#') return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Devuelve una URL segura para mostrar en UI o logs, ocultando parámetros query y segmentos con tokens largos.
    /// </summary>
    public static string RedactManifestUrlForDisplay(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl) || !Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var uri))
            return "";

        string[] rawSegments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var cleanSegments = new List<string>(rawSegments.Length);
        foreach (string seg in rawSegments)
        {
            if (seg.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                cleanSegments.Add(seg);
                continue;
            }

            if (TokenLikeSegmentRegex.IsMatch(seg) || seg.Contains('='))
            {
                cleanSegments.Add("***");
            }
            else
            {
                cleanSegments.Add(TvUrlGuard.SanitizeHeaderValue(seg, 40));
            }
        }

        string hostPort = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        string pathPart = cleanSegments.Count > 0 ? "/" + string.Join('/', cleanSegments) : "/manifest.json";
        string queryPart = string.IsNullOrEmpty(uri.Query) ? "" : "?***";
        return $"{uri.Scheme}://{hostPort}{pathPart}{queryPart}";
    }

    /// <summary>
    /// Calcula la URL base del addon quitando "/manifest.json" y conservando prefijos de ruta o configuración.
    /// </summary>
    public static string ExtractBaseUrl(Uri manifestUri)
    {
        ArgumentNullException.ThrowIfNull(manifestUri);
        string path = manifestUri.AbsolutePath;
        if (path.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
            path = path[..^"/manifest.json".Length];
        path = path.TrimEnd('/');

        string hostPort = manifestUri.IsDefaultPort ? manifestUri.Host : $"{manifestUri.Host}:{manifestUri.Port}";
        return string.IsNullOrEmpty(path)
            ? $"{manifestUri.Scheme}://{hostPort}"
            : $"{manifestUri.Scheme}://{hostPort}{path}";
    }

    public static (bool Success, LexAddonManifest? Manifest, string? Error) Parse(string? json, string? manifestBaseUrl = null)
    {
        if (string.IsNullOrWhiteSpace(json))
            return (false, null, "El manifiesto del addon está vacío.");

        string trimmed = json.Trim();
        if (!trimmed.StartsWith('{'))
            return (false, null, "El recurso recibido no es un manifiesto JSON válido.");

        try
        {
            using var doc = JsonDocument.Parse(trimmed, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 16
            });

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (false, null, "La raíz del manifiesto debe ser un objeto JSON.");

            var root = doc.RootElement;
            string rawId = ReadSanitizedString(root, "id", 100);
            if (string.IsNullOrWhiteSpace(rawId) || !AddonIdRegex.IsMatch(rawId))
                return (false, null, "El manifiesto debe incluir un 'id' alfanumérico válido (2 a 80 caracteres).");

            string name = ReadSanitizedString(root, "name", 80);
            if (string.IsNullOrWhiteSpace(name))
                return (false, null, "El manifiesto debe incluir el campo 'name'.");

            string version = ReadSanitizedString(root, "version", 32);
            if (string.IsNullOrWhiteSpace(version))
                version = "1.0.0";

            string description = ReadSanitizedString(root, "description", 400);
            string? logo = NormalizeOptionalImageUrl(ReadSanitizedString(root, "logo", 512), manifestBaseUrl);
            string? background = NormalizeOptionalImageUrl(ReadSanitizedString(root, "background", 512), manifestBaseUrl);

            string protocol = DetectProtocol(root);
            var types = ReadDistinctLowerStrings(root, "types", MaxTypes, 32);
            var idPrefixes = ReadDistinctStrings(root, "idPrefixes", MaxIdPrefixes, 32);
            var resources = ParseResources(root);
            var catalogs = ParseCatalogs(root, types);
            var hints = ParseBehaviorHints(root);

            if (resources.Count == 0 && catalogs.Count == 0)
                return (false, null, "El manifiesto no declara recursos ni catálogos compatibles.");

            // Completar tipos con los declarados en catálogos si 'types' venía vacío
            foreach (var cat in catalogs)
            {
                if (!string.IsNullOrWhiteSpace(cat.Type) && !types.Contains(cat.Type, StringComparer.OrdinalIgnoreCase) && types.Count < MaxTypes)
                {
                    types.Add(cat.Type);
                }
            }

            var manifest = new LexAddonManifest
            {
                Id = rawId,
                Name = name,
                Version = version,
                Description = description,
                Logo = logo,
                Background = background,
                Protocol = protocol,
                Types = types,
                Resources = resources,
                Catalogs = catalogs,
                IdPrefixes = idPrefixes,
                BehaviorHints = hints
            };

            return (true, manifest, null);
        }
        catch (JsonException ex)
        {
            return (false, null, $"Manifiesto JSON malformado: {TvUrlGuard.SanitizeHeaderValue(ex.Message, 120)}");
        }
    }

    private static string DetectProtocol(JsonElement root)
    {
        if (root.TryGetProperty("lexProtocol", out var lexProp) && lexProp.ValueKind == JsonValueKind.String)
        {
            string val = TvUrlGuard.SanitizeHeaderValue(lexProp.GetString(), 24);
            if (!string.IsNullOrWhiteSpace(val))
                return val.ToLowerInvariant();
        }
        return "stremio-v1";
    }

    private static List<LexAddonResourceDecl> ParseResources(JsonElement root)
    {
        var list = new List<LexAddonResourceDecl>();
        if (!root.TryGetProperty("resources", out var resProp) || resProp.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in resProp.EnumerateArray())
        {
            if (list.Count >= MaxResources) break;

            if (item.ValueKind == JsonValueKind.String)
            {
                string rName = TvUrlGuard.SanitizeHeaderValue(item.GetString(), 32).ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(rName) && !list.Any(r => r.Name == rName))
                {
                    list.Add(new LexAddonResourceDecl { Name = rName });
                }
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                string rName = ReadSanitizedString(item, "name", 32).ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(rName)) continue;

                var decl = new LexAddonResourceDecl
                {
                    Name = rName,
                    Types = ReadDistinctLowerStrings(item, "types", MaxTypes, 32),
                    IdPrefixes = ReadDistinctStrings(item, "idPrefixes", MaxIdPrefixes, 32)
                };

                var existing = list.FirstOrDefault(r => r.Name == rName);
                if (existing != null)
                {
                    foreach (var t in decl.Types)
                    {
                        if (!existing.Types.Contains(t, StringComparer.OrdinalIgnoreCase) && existing.Types.Count < MaxTypes)
                            existing.Types.Add(t);
                    }
                    foreach (var p in decl.IdPrefixes)
                    {
                        if (!existing.IdPrefixes.Contains(p, StringComparer.Ordinal) && existing.IdPrefixes.Count < MaxIdPrefixes)
                            existing.IdPrefixes.Add(p);
                    }
                }
                else
                {
                    list.Add(decl);
                }
            }
        }

        return list;
    }

    private static List<LexAddonCatalogDecl> ParseCatalogs(JsonElement root, List<string> manifestTypes)
    {
        var catalogs = new List<LexAddonCatalogDecl>();
        if (!root.TryGetProperty("catalogs", out var catsProp) || catsProp.ValueKind != JsonValueKind.Array)
            return catalogs;

        foreach (var item in catsProp.EnumerateArray())
        {
            if (catalogs.Count >= MaxCatalogs) break;
            if (item.ValueKind != JsonValueKind.Object) continue;

            string id = ReadSanitizedString(item, "id", 80);
            string type = ReadSanitizedString(item, "type", 32).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(type) && manifestTypes.Count > 0)
                type = manifestTypes[0];

            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(type))
                continue;

            string name = ReadSanitizedString(item, "name", 80);
            if (string.IsNullOrWhiteSpace(name))
                name = id;

            var extras = new List<LexAddonExtraDecl>();
            if (item.TryGetProperty("extra", out var extraProp) && extraProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var exItem in extraProp.EnumerateArray())
                {
                    if (extras.Count >= MaxExtrasPerCatalog) break;
                    if (exItem.ValueKind != JsonValueKind.Object) continue;

                    string exName = ReadSanitizedString(exItem, "name", 32);
                    if (string.IsNullOrWhiteSpace(exName)) continue;

                    bool required = exItem.TryGetProperty("isRequired", out var reqProp) && reqProp.ValueKind == JsonValueKind.True;
                    var options = ReadDistinctStrings(exItem, "options", MaxOptionsPerExtra, 64);

                    extras.Add(new LexAddonExtraDecl
                    {
                        Name = exName,
                        IsRequired = required,
                        Options = options
                    });
                }
            }

            // Compatibilidad con campo shorthand 'extraSupported' / 'extraRequired' de Stremio
            AddLegacyExtraNames(item, "extraSupported", isRequired: false, extras);
            AddLegacyExtraNames(item, "extraRequired", isRequired: true, extras);

            catalogs.Add(new LexAddonCatalogDecl
            {
                Type = type,
                Id = id,
                Name = name,
                Extra = extras
            });
        }

        return catalogs;
    }

    private static void AddLegacyExtraNames(JsonElement catalogObj, string propName, bool isRequired, List<LexAddonExtraDecl> target)
    {
        if (!catalogObj.TryGetProperty(propName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return;

        foreach (var el in arr.EnumerateArray())
        {
            if (target.Count >= MaxExtrasPerCatalog) break;
            if (el.ValueKind != JsonValueKind.String) continue;

            string name = TvUrlGuard.SanitizeHeaderValue(el.GetString(), 32);
            if (string.IsNullOrWhiteSpace(name)) continue;

            var existing = target.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (isRequired) existing.IsRequired = true;
            }
            else
            {
                target.Add(new LexAddonExtraDecl { Name = name, IsRequired = isRequired });
            }
        }
    }

    private static LexAddonBehaviorHints ParseBehaviorHints(JsonElement root)
    {
        var hints = new LexAddonBehaviorHints();
        if (!root.TryGetProperty("behaviorHints", out var prop) || prop.ValueKind != JsonValueKind.Object)
            return hints;

        hints.Configurable = prop.TryGetProperty("configurable", out var cProp) && cProp.ValueKind == JsonValueKind.True;
        hints.ConfigurationRequired = prop.TryGetProperty("configurationRequired", out var crProp) && crProp.ValueKind == JsonValueKind.True;
        hints.Adult = prop.TryGetProperty("adult", out var aProp) && aProp.ValueKind == JsonValueKind.True;
        return hints;
    }

    internal static string? NormalizeOptionalImageUrl(string? raw, string? manifestBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string trimmed = raw.Trim();
        if (ExplicitNonPortSchemeRegex.IsMatch(trimmed) &&
            !trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? normalized = TvXtreamService.NormalizePosterUrl(trimmed, manifestBaseUrl);
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        return Uri.TryCreate(normalized, UriKind.Absolute, out var u) &&
               (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            ? u.AbsoluteUri
            : null;
    }

    private static string ReadSanitizedString(JsonElement obj, string propName, int maxLen)
    {
        if (!obj.TryGetProperty(propName, out var prop) || prop.ValueKind != JsonValueKind.String)
            return "";
        return TvUrlGuard.SanitizeHeaderValue(prop.GetString(), maxLen);
    }

    private static List<string> ReadDistinctLowerStrings(JsonElement obj, string propName, int maxCount, int maxLen)
    {
        var result = new List<string>();
        if (!obj.TryGetProperty(propName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in arr.EnumerateArray())
        {
            if (result.Count >= maxCount) break;
            if (item.ValueKind != JsonValueKind.String) continue;
            string clean = TvUrlGuard.SanitizeHeaderValue(item.GetString(), maxLen).ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(clean) && !result.Contains(clean, StringComparer.OrdinalIgnoreCase))
                result.Add(clean);
        }
        return result;
    }

    private static List<string> ReadDistinctStrings(JsonElement obj, string propName, int maxCount, int maxLen)
    {
        var result = new List<string>();
        if (!obj.TryGetProperty(propName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in arr.EnumerateArray())
        {
            if (result.Count >= maxCount) break;
            if (item.ValueKind != JsonValueKind.String) continue;
            string clean = TvUrlGuard.SanitizeHeaderValue(item.GetString(), maxLen);
            if (!string.IsNullOrWhiteSpace(clean) && !result.Contains(clean, StringComparer.Ordinal))
                result.Add(clean);
        }
        return result;
    }
}
