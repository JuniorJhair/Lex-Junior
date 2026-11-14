using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LexBrowser.Core.Tv.Addons;

/// <summary>
/// Constructor seguro de endpoints /stream y analizador/normalizador de fuentes de reproducción
/// (HTTP, HLS, MP4, subtítulos externos y cabeceras allowlisted) provenientes de addons Stremio/Lex.
/// </summary>
public static class LexAddonStreamParser
{
    public const int MaxStreamsPerResponse = 60;
    public const int MaxSubtitlesPerStream = 24;
    public const int MaxSubtitleBytes = 2 * 1024 * 1024;

    private static readonly Regex SrtTimestampCommaRegex = new(
        @"(\d{2}:\d{2}:\d{2}),(\d{3})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public sealed record ParsedStreamEntry(
        LexAddonStreamOption Option,
        string? RequestUserAgent,
        string? RequestReferer,
        string? RequestOrigin);

    public static bool TryBuildStreamUrl(
        string? manifestUrl,
        string? type,
        string? itemId,
        out string endpointUrl,
        out string? error)
    {
        endpointUrl = "";
        error = null;

        if (!LexAddonCatalogParser.TryExtractManifestBase(manifestUrl, out string baseUrl, out string querySuffix, out error))
            return false;

        if (!LexAddonCatalogParser.IsValidPathSegment(type, 32) ||
            !LexAddonCatalogParser.IsValidPathSegment(itemId, 180))
        {
            error = "Identificador de stream inválido.";
            return false;
        }

        string escType = Uri.EscapeDataString(type!.Trim().ToLowerInvariant());
        string escId = Uri.EscapeDataString(itemId!.Trim());
        endpointUrl = $"{baseUrl}/stream/{escType}/{escId}.json{querySuffix}";
        return true;
    }

    public static bool TryBuildSubtitlesUrl(
        string? manifestUrl,
        string? type,
        string? itemId,
        string? videoHash,
        long? videoSize,
        string? filename,
        out string endpointUrl,
        out string? error)
    {
        endpointUrl = "";
        error = null;

        if (!LexAddonCatalogParser.TryExtractManifestBase(manifestUrl, out string baseUrl, out string querySuffix, out error))
            return false;

        if (!LexAddonCatalogParser.IsValidPathSegment(type, 32) ||
            !LexAddonCatalogParser.IsValidPathSegment(itemId, 180))
        {
            error = "Identificador de subtítulos inválido.";
            return false;
        }

        string escType = Uri.EscapeDataString(type!.Trim().ToLowerInvariant());
        string escId = Uri.EscapeDataString(itemId!.Trim());

        var extras = new List<string>(3);
        string cleanHash = TvUrlGuard.SanitizeHeaderValue(videoHash, 64).Trim();
        if (!string.IsNullOrEmpty(cleanHash))
            extras.Add("videoHash=" + Uri.EscapeDataString(cleanHash));

        if (videoSize is > 0 and <= 500_000_000_000L)
            extras.Add("videoSize=" + videoSize.Value.ToString(CultureInfo.InvariantCulture));

        string cleanName = SanitizeDisplayFilename(filename);
        if (!string.IsNullOrEmpty(cleanName))
            extras.Add("filename=" + Uri.EscapeDataString(cleanName));

        string extraSegment = extras.Count > 0 ? "/" + string.Join("&", extras) : "";
        endpointUrl = $"{baseUrl}/subtitles/{escType}/{escId}{extraSegment}.json{querySuffix}";
        return true;
    }

    public static (bool Success, List<LexAddonSubtitleDecl> Subtitles, string? Error) ParseSubtitlesResponse(
        string? json,
        string addonName)
    {
        var list = new List<LexAddonSubtitleDecl>();
        if (string.IsNullOrWhiteSpace(json))
            return (false, list, "El addon de subtítulos devolvió una respuesta vacía.");

        string trimmed = json.Trim();
        if (!trimmed.StartsWith('{'))
            return (false, list, "La respuesta de subtítulos no es un objeto JSON válido.");

        try
        {
            using var doc = JsonDocument.Parse(trimmed, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 20
            });

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (false, list, "La raíz de subtítulos debe ser un objeto JSON.");

            if (!doc.RootElement.TryGetProperty("subtitles", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return (false, list, "El addon no devolvió la lista 'subtitles'.");

            list = ParseSubtitles(doc.RootElement, addonName);
            return (true, list, null);
        }
        catch (JsonException)
        {
            return (false, list, "No se pudo interpretar el JSON de subtítulos del addon.");
        }
    }

    public static (bool Success, List<ParsedStreamEntry> Streams, string? Error) ParseStreams(
        string? json,
        string addonId,
        string addonName)
    {
        var list = new List<ParsedStreamEntry>();
        if (string.IsNullOrWhiteSpace(json))
            return (false, list, "El addon devolvió una respuesta vacía.");

        string trimmed = json.Trim();
        if (!trimmed.StartsWith('{'))
            return (false, list, "La respuesta de streams no es un objeto JSON válido.");

        try
        {
            using var doc = JsonDocument.Parse(trimmed, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 24
            });

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (false, list, "La raíz de streams debe ser un objeto JSON.");

            if (!doc.RootElement.TryGetProperty("streams", out var streamsArr) ||
                streamsArr.ValueKind != JsonValueKind.Array)
            {
                return (false, list, "El addon no devolvió la lista 'streams'.");
            }

            foreach (var el in streamsArr.EnumerateArray())
            {
                if (list.Count >= MaxStreamsPerResponse) break;
                if (el.ValueKind != JsonValueKind.Object) continue;

                var parsed = ParseSingleStream(el, addonId, addonName);
                if (parsed != null)
                    list.Add(parsed);
            }

            return (true, list, null);
        }
        catch (JsonException)
        {
            return (false, list, "No se pudo interpretar el JSON de streams del addon.");
        }
    }

    public static string NormalizeToWebVtt(string? rawSubtitle)
    {
        if (string.IsNullOrWhiteSpace(rawSubtitle))
            return "WEBVTT\n\n";

        string normalized = rawSubtitle
            .TrimStart('\uFEFF', ' ', '\t', '\r', '\n')
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        if (normalized.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase))
            return normalized;

        string converted = SrtTimestampCommaRegex.Replace(normalized, "$1.$2");
        return "WEBVTT\n\n" + converted;
    }

    private static ParsedStreamEntry? ParseSingleStream(JsonElement obj, string addonId, string addonName)
    {
        string name = ReadSanitizedMultiline(obj, "name", 140);
        string title = ReadSanitizedMultiline(obj, "title", 280);
        if (string.IsNullOrWhiteSpace(title))
            title = ReadSanitizedMultiline(obj, "description", 280);

        string rawUrl = ReadRawString(obj, "url", 2048);
        string rawExternalUrl = ReadRawString(obj, "externalUrl", 2048);
        string infoHash = ReadRawString(obj, "infoHash", 128);

        bool notWebReady = false;
        string bingeGroup = "";
        string filename = "";
        long? videoSize = null;
        string? reqUa = null;
        string? reqRef = null;
        string? reqOrigin = null;

        if (obj.TryGetProperty("behaviorHints", out var hints) && hints.ValueKind == JsonValueKind.Object)
        {
            notWebReady = hints.TryGetProperty("notWebReady", out var nwr) && nwr.ValueKind == JsonValueKind.True;
            bingeGroup = TvUrlGuard.SanitizeHeaderValue(ReadRawString(hints, "bingeGroup", 80), 80);
            filename = SanitizeDisplayFilename(ReadRawString(hints, "filename", 200));
            if (hints.TryGetProperty("videoSize", out var vsProp) &&
                vsProp.ValueKind == JsonValueKind.Number &&
                vsProp.TryGetInt64(out long parsedSize) &&
                parsedSize > 0 &&
                parsedSize <= 500_000_000_000L)
            {
                videoSize = parsedSize;
            }
            (reqUa, reqRef, reqOrigin) = ExtractSafeProxyHeaders(hints);
        }

        var subtitles = ParseSubtitles(obj, sourceAddonName: null);

        // Caso 1: Fuente directa HTTP/HTTPS validada contra SSRF y denylist
        if (!string.IsNullOrWhiteSpace(rawUrl))
        {
            if (!TvUrlGuard.IsAllowedRemoteUrl(rawUrl, out var safeUri) || safeUri == null)
            {
                return new ParsedStreamEntry(new LexAddonStreamOption
                {
                    AddonId = addonId,
                    AddonName = addonName,
                    Name = string.IsNullOrWhiteSpace(name) ? addonName : name,
                    Title = string.IsNullOrWhiteSpace(title) ? "Fuente bloqueada" : title,
                    StreamKind = "blocked",
                    Playable = false,
                    DisabledReason = "URL privada o esquema no permitido por seguridad.",
                    NotWebReady = notWebReady,
                    BingeGroup = bingeGroup
                }, null, null, null);
            }

            if (TvDenylist.IsDenylisted(safeUri.AbsoluteUri, out var denyReason))
            {
                return new ParsedStreamEntry(new LexAddonStreamOption
                {
                    AddonId = addonId,
                    AddonName = addonName,
                    Name = string.IsNullOrWhiteSpace(name) ? addonName : name,
                    Title = string.IsNullOrWhiteSpace(title) ? "Fuente deshabilitada" : title,
                    StreamKind = "blocked",
                    Playable = false,
                    DisabledReason = denyReason ?? "Fuente deshabilitada.",
                    NotWebReady = notWebReady,
                    BingeGroup = bingeGroup
                }, null, null, null);
            }

            string cleanUrl = safeUri.AbsoluteUri;
            string kind = ClassifyStreamKind(safeUri);
            return new ParsedStreamEntry(new LexAddonStreamOption
            {
                AddonId = addonId,
                AddonName = addonName,
                Name = string.IsNullOrWhiteSpace(name) ? addonName : name,
                Title = string.IsNullOrWhiteSpace(title) ? safeUri.Host : title,
                StreamUrl = cleanUrl,
                ProxyUrl = "/api/proxy?u=" + Uri.EscapeDataString(cleanUrl),
                StreamKind = kind,
                Playable = true,
                NotWebReady = notWebReady,
                BingeGroup = bingeGroup,
                Filename = filename,
                VideoSize = videoSize,
                Subtitles = subtitles
            }, reqUa, reqRef, reqOrigin);
        }

        // Caso 2: BitTorrent / P2P (no soportado por proxy HTTP sin cliente torrent)
        if (!string.IsNullOrWhiteSpace(infoHash))
        {
            return new ParsedStreamEntry(new LexAddonStreamOption
            {
                AddonId = addonId,
                AddonName = addonName,
                Name = string.IsNullOrWhiteSpace(name) ? addonName : name,
                Title = string.IsNullOrWhiteSpace(title) ? "Fuente P2P / Torrent" : title,
                StreamKind = "p2p_unsupported",
                Playable = false,
                DisabledReason = "P2P / infoHash requiere motor torrent externo (no reproducible por proxy HTTP).",
                NotWebReady = true,
                BingeGroup = bingeGroup
            }, null, null, null);
        }

        // Caso 3: Enlace externo validado (p. ej. WatchHub hacia plataformas oficiales)
        if (!string.IsNullOrWhiteSpace(rawExternalUrl))
        {
            if (!TvUrlGuard.IsAllowedRemoteUrl(rawExternalUrl, out var safeExtUri) || safeExtUri == null)
            {
                return new ParsedStreamEntry(new LexAddonStreamOption
                {
                    AddonId = addonId,
                    AddonName = addonName,
                    Name = string.IsNullOrWhiteSpace(name) ? addonName : name,
                    Title = string.IsNullOrWhiteSpace(title) ? "Enlace externo bloqueado" : title,
                    StreamKind = "blocked",
                    Playable = false,
                    DisabledReason = "Enlace externo privado o esquema no permitido por seguridad.",
                    NotWebReady = true,
                    BingeGroup = bingeGroup
                }, null, null, null);
            }

            if (TvDenylist.IsDenylisted(safeExtUri.AbsoluteUri, out var denyExtReason))
            {
                return new ParsedStreamEntry(new LexAddonStreamOption
                {
                    AddonId = addonId,
                    AddonName = addonName,
                    Name = string.IsNullOrWhiteSpace(name) ? addonName : name,
                    Title = string.IsNullOrWhiteSpace(title) ? "Enlace externo deshabilitado" : title,
                    StreamKind = "blocked",
                    Playable = false,
                    DisabledReason = denyExtReason ?? "Enlace externo deshabilitado.",
                    NotWebReady = true,
                    BingeGroup = bingeGroup
                }, null, null, null);
            }

            return new ParsedStreamEntry(new LexAddonStreamOption
            {
                AddonId = addonId,
                AddonName = addonName,
                Name = string.IsNullOrWhiteSpace(name) ? addonName : name,
                Title = string.IsNullOrWhiteSpace(title) ? safeExtUri.Host : title,
                ExternalUrl = safeExtUri.AbsoluteUri,
                StreamKind = "external",
                Playable = false,
                NotWebReady = true,
                BingeGroup = bingeGroup
            }, null, null, null);
        }

        return null;
    }

    private static (string? UserAgent, string? Referer, string? Origin) ExtractSafeProxyHeaders(JsonElement hints)
    {
        if (!hints.TryGetProperty("proxyHeaders", out var ph) || ph.ValueKind != JsonValueKind.Object)
            return (null, null, null);
        if (!ph.TryGetProperty("request", out var req) || req.ValueKind != JsonValueKind.Object)
            return (null, null, null);

        string? ua = null;
        string? referer = null;
        string? origin = null;

        foreach (var prop in req.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.String) continue;
            string? val = prop.Value.GetString();
            if (string.IsNullOrWhiteSpace(val)) continue;

            if (prop.Name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
            {
                ua = TvUrlGuard.SanitizeHeaderValue(val, 160);
            }
            else if (prop.Name.Equals("Referer", StringComparison.OrdinalIgnoreCase))
            {
                string clean = TvUrlGuard.SanitizeHeaderValue(val, 512);
                if (TvUrlGuard.IsAllowedRemoteUrl(clean, out var rUri) && rUri != null)
                    referer = rUri.AbsoluteUri;
            }
            else if (prop.Name.Equals("Origin", StringComparison.OrdinalIgnoreCase))
            {
                string clean = TvUrlGuard.SanitizeHeaderValue(val, 256);
                if (TvUrlGuard.IsAllowedRemoteUrl(clean, out var oUri) && oUri != null)
                    origin = oUri.GetLeftPart(UriPartial.Authority);
            }
        }

        return (ua, referer, origin);
    }

    private static List<LexAddonSubtitleDecl> ParseSubtitles(JsonElement containerObj, string? sourceAddonName)
    {
        var subs = new List<LexAddonSubtitleDecl>();
        if (!containerObj.TryGetProperty("subtitles", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return subs;

        foreach (var sub in arr.EnumerateArray())
        {
            if (subs.Count >= MaxSubtitlesPerStream) break;
            if (sub.ValueKind != JsonValueKind.Object) continue;

            string rawUrl = ReadRawString(sub, "url", 2048);
            if (!TvUrlGuard.IsAllowedRemoteUrl(rawUrl, out var subUri) || subUri == null)
                continue;
            if (TvDenylist.IsDenylisted(subUri.AbsoluteUri, out _))
                continue;

            string lang = TvUrlGuard.SanitizeHeaderValue(ReadRawString(sub, "lang", 24), 24);
            string id = TvUrlGuard.SanitizeHeaderValue(ReadRawString(sub, "id", 48), 48);
            string baseLabel = !string.IsNullOrWhiteSpace(lang)
                ? lang.ToUpperInvariant()
                : (!string.IsNullOrWhiteSpace(id) ? id : $"Sub {subs.Count + 1}");
            string label = !string.IsNullOrWhiteSpace(sourceAddonName)
                ? $"{baseLabel} · {sourceAddonName}"
                : baseLabel;

            subs.Add(new LexAddonSubtitleDecl
            {
                Id = id,
                Lang = lang,
                Label = label,
                ProxyUrl = "/api/addons/subtitle?u=" + Uri.EscapeDataString(subUri.AbsoluteUri)
            });
        }

        return subs;
    }

    private static string SanitizeDisplayFilename(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string clean = TvUrlGuard.SanitizeHeaderValue(raw, 200).Trim();
        int lastSlash = clean.LastIndexOfAny(new[] { '/', '\\' });
        if (lastSlash >= 0 && lastSlash + 1 < clean.Length)
            clean = clean[(lastSlash + 1)..];
        return clean.Length > 160 ? clean[..160] : clean;
    }

    private static string ClassifyStreamKind(Uri uri)
    {
        string p = uri.AbsolutePath.ToLowerInvariant();
        if (p.EndsWith(".m3u8", StringComparison.Ordinal) || p.EndsWith(".m3u", StringComparison.Ordinal))
            return "hls";
        if (p.EndsWith(".mp4", StringComparison.Ordinal))
            return "mp4";
        if (p.EndsWith(".mkv", StringComparison.Ordinal))
            return "mkv";
        if (p.EndsWith(".webm", StringComparison.Ordinal))
            return "webm";
        if (p.EndsWith(".ts", StringComparison.Ordinal) || p.EndsWith(".flv", StringComparison.Ordinal))
            return "mpegts";
        return "http";
    }

    private static string ReadRawString(JsonElement obj, string propName, int maxLen)
    {
        if (!obj.TryGetProperty(propName, out var prop) || prop.ValueKind != JsonValueKind.String)
            return "";
        string val = prop.GetString() ?? "";
        return val.Length > maxLen ? val[..maxLen] : val;
    }

    private static string ReadSanitizedMultiline(JsonElement obj, string propName, int maxLen)
    {
        string raw = ReadRawString(obj, propName, maxLen * 2);
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string singleLine = raw.Replace("\r\n", " · ", StringComparison.Ordinal)
                               .Replace('\n', '·')
                               .Replace('\r', ' ');
        return TvUrlGuard.SanitizeHeaderValue(singleLine, maxLen);
    }
}

