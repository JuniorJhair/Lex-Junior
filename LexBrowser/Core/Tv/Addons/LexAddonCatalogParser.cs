using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace LexBrowser.Core.Tv.Addons;

/// <summary>
/// Constructor seguro de URLs y normalizador de respuestas /catalog y /meta
/// (compatible con protocolo Stremio v1 y extensiones Lex Addons).
/// </summary>
public static class LexAddonCatalogParser
{
    public const int MaxCatalogItems = 120;
    public const int MaxLiveChannels = 500;
    public const int MaxVideosPerMeta = 2000;
    public const int MaxStringListItems = 24;

    public static bool IsLiveChannelType(string? type)
        => string.Equals(type?.Trim(), "tv", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(type?.Trim(), "channel", StringComparison.OrdinalIgnoreCase);

    public static bool IsValidPathSegment(string? value, int maxLen = 160)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        string trimmed = value.Trim();
        if (trimmed.Length > maxLen) return false;
        if (trimmed is "." or "..") return false;
        if (trimmed.Contains("..", StringComparison.Ordinal) ||
            trimmed.Contains('/') ||
            trimmed.Contains('\\') ||
            trimmed.Contains('?') ||
            trimmed.Contains('#') ||
            trimmed.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("%5c", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (char c in trimmed)
        {
            if (char.IsControl(c)) return false;
        }

        return true;
    }

    public static bool TryBuildCatalogUrl(
        string? manifestUrl,
        string? type,
        string? catalogId,
        string? search,
        string? genre,
        int skip,
        out string endpointUrl,
        out string? error)
    {
        endpointUrl = "";
        error = null;

        if (!TryExtractManifestBase(manifestUrl, out string baseUrl, out string querySuffix, out error))
            return false;

        if (!IsValidPathSegment(type, 32) || !IsValidPathSegment(catalogId, 96))
        {
            error = "Tipo o identificador de catálogo inválido.";
            return false;
        }

        string escType = Uri.EscapeDataString(type!.Trim().ToLowerInvariant());
        string escCatalogId = Uri.EscapeDataString(catalogId!.Trim());

        var extras = new List<string>(3);
        string cleanSearch = TvUrlGuard.SanitizeHeaderValue(search, 120).Trim();
        if (!string.IsNullOrEmpty(cleanSearch))
            extras.Add("search=" + Uri.EscapeDataString(cleanSearch));

        string cleanGenre = TvUrlGuard.SanitizeHeaderValue(genre, 64).Trim();
        if (!string.IsNullOrEmpty(cleanGenre) && !cleanGenre.Equals("all", StringComparison.OrdinalIgnoreCase))
            extras.Add("genre=" + Uri.EscapeDataString(cleanGenre));

        if (skip > 0)
            extras.Add("skip=" + Math.Min(skip, 50_000).ToString(CultureInfo.InvariantCulture));

        string extraSegment = extras.Count > 0 ? "/" + string.Join("&", extras) : "";
        endpointUrl = $"{baseUrl}/catalog/{escType}/{escCatalogId}{extraSegment}.json{querySuffix}";
        return true;
    }

    public static bool TryBuildMetaUrl(
        string? manifestUrl,
        string? type,
        string? itemId,
        out string endpointUrl,
        out string? error)
    {
        endpointUrl = "";
        error = null;

        if (!TryExtractManifestBase(manifestUrl, out string baseUrl, out string querySuffix, out error))
            return false;

        if (!IsValidPathSegment(type, 32) || !IsValidPathSegment(itemId, 160))
        {
            error = "Identificador de título inválido.";
            return false;
        }

        string escType = Uri.EscapeDataString(type!.Trim().ToLowerInvariant());
        string escId = Uri.EscapeDataString(itemId!.Trim());
        endpointUrl = $"{baseUrl}/meta/{escType}/{escId}.json{querySuffix}";
        return true;
    }

    public static (bool Success, List<LexAddonMetaPreview> Items, bool HasMore, string? Error) ParseCatalog(
        string? json,
        string addonId,
        string addonName,
        string fallbackType,
        string? manifestBaseUrl = null)
    {
        var items = new List<LexAddonMetaPreview>();
        if (string.IsNullOrWhiteSpace(json))
            return (false, items, false, "El catálogo devolvió una respuesta vacía.");

        string trimmed = json.Trim();
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
            return (false, items, false, "El catálogo no devolvió un JSON válido.");

        try
        {
            using var doc = JsonDocument.Parse(trimmed, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 24
            });

            if (!TryExtractCatalogArray(doc.RootElement, out var metasArray, out bool explicitHasMore, out bool hasMoreSpecified, out string? arrayErr))
                return (false, items, false, arrayErr);

            foreach (var el in metasArray.EnumerateArray())
            {
                if (items.Count >= MaxCatalogItems) break;
                if (el.ValueKind != JsonValueKind.Object) continue;

                var preview = ParseMetaPreviewObject(el, addonId, addonName, fallbackType, manifestBaseUrl);
                if (preview != null)
                    items.Add(preview);
            }

            bool hasMore = hasMoreSpecified ? explicitHasMore : false;
            return (true, items, hasMore, null);
        }
        catch (JsonException)
        {
            return (false, items, false, "No se pudo interpretar el JSON del catálogo.");
        }
    }

    public static (bool Success, List<LexAddonIptvChannel> Channels, List<LexAddonStreamParser.ParsedStreamEntry> RememberedStreams, string? Error) ParseLiveCatalog(
        string? json,
        string addonId,
        string addonName,
        string fallbackType,
        string? manifestBaseUrl = null)
    {
        var channels = new List<LexAddonIptvChannel>();
        var rememberedStreams = new List<LexAddonStreamParser.ParsedStreamEntry>();
        if (string.IsNullOrWhiteSpace(json))
            return (false, channels, rememberedStreams, "El catálogo IPTV devolvió una respuesta vacía.");

        string trimmed = json.Trim();
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
            return (false, channels, rememberedStreams, "El catálogo IPTV no devolvió un JSON válido.");

        try
        {
            using var doc = JsonDocument.Parse(trimmed, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 24
            });

            if (!TryExtractCatalogArray(doc.RootElement, out var itemsArray, out _, out _, out string? arrayErr))
                return (false, channels, rememberedStreams, arrayErr);

            foreach (var el in itemsArray.EnumerateArray())
            {
                if (channels.Count >= MaxLiveChannels) break;
                if (el.ValueKind != JsonValueKind.Object) continue;

                var parsed = ParseLiveChannelObject(el, channels.Count + 1, addonId, addonName, fallbackType, manifestBaseUrl, rememberedStreams);
                if (parsed != null)
                    channels.Add(parsed);
            }

            return (true, channels, rememberedStreams, null);
        }
        catch (JsonException)
        {
            return (false, channels, rememberedStreams, "No se pudo interpretar el JSON de canales IPTV.");
        }
    }

    private static bool TryExtractCatalogArray(
        JsonElement root,
        out JsonElement arrayElement,
        out bool explicitHasMore,
        out bool hasMoreSpecified,
        out string? error)
    {
        arrayElement = default;
        explicitHasMore = false;
        hasMoreSpecified = false;
        error = null;

        if (root.ValueKind == JsonValueKind.Array)
        {
            arrayElement = root;
            return true;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "Formato JSON de catálogo no compatible.";
            return false;
        }

        if (root.TryGetProperty("hasMore", out var hm) &&
            (hm.ValueKind == JsonValueKind.True || hm.ValueKind == JsonValueKind.False))
        {
            explicitHasMore = hm.ValueKind == JsonValueKind.True;
            hasMoreSpecified = true;
        }

        if (root.TryGetProperty("metas", out var mProp) && mProp.ValueKind == JsonValueKind.Array)
        {
            arrayElement = mProp;
            return true;
        }

        if (root.TryGetProperty("items", out var iProp) && iProp.ValueKind == JsonValueKind.Array)
        {
            arrayElement = iProp;
            return true;
        }

        if (root.TryGetProperty("channels", out var cProp) && cProp.ValueKind == JsonValueKind.Array)
        {
            arrayElement = cProp;
            return true;
        }

        error = "El catálogo no incluye la lista 'metas'.";
        return false;
    }

    private static LexAddonIptvChannel? ParseLiveChannelObject(
        JsonElement obj,
        int sequentialNum,
        string addonId,
        string addonName,
        string fallbackType,
        string? manifestBaseUrl,
        List<LexAddonStreamParser.ParsedStreamEntry> rememberedStreams)
    {
        var preview = ParseMetaPreviewObject(obj, addonId, addonName, fallbackType, manifestBaseUrl);
        if (preview == null) return null;

        string? logo = LexAddonManifestParser.NormalizeOptionalImageUrl(
            ReadFlexibleString(obj, "logo", 512), manifestBaseUrl) ?? preview.Poster;

        string group = ReadFlexibleString(obj, "group", 64);
        if (string.IsNullOrWhiteSpace(group) && preview.Genres.Count > 0)
            group = preview.Genres[0];
        if (string.IsNullOrWhiteSpace(group))
            group = "Canales";

        string lang = ReadFlexibleString(obj, "language", 32);
        if (string.IsNullOrWhiteSpace(lang))
            lang = ReadFlexibleString(obj, "lang", 32);

        string country = ReadFlexibleString(obj, "country", 32);
        string quality = ReadFlexibleString(obj, "quality", 24);

        string epgNow = ReadFlexibleString(obj, "epgNow", 180);
        if (string.IsNullOrWhiteSpace(epgNow))
            epgNow = ReadFlexibleString(obj, "currentProgram", 180);
        if (string.IsNullOrWhiteSpace(epgNow))
            epgNow = ReadFlexibleString(obj, "program", 180);
        if (string.IsNullOrWhiteSpace(epgNow) && !string.IsNullOrWhiteSpace(preview.Description))
            epgNow = preview.Description.Length > 140 ? preview.Description[..140] : preview.Description;

        int number = ReadFlexibleInt(obj, "number", 0);
        if (number <= 0) number = ReadFlexibleInt(obj, "channelNumber", 0);
        if (number <= 0) number = ReadFlexibleInt(obj, "lcn", 0);
        if (number <= 0) number = sequentialNum;

        var urls = new List<LexAddonIptvChannelAlt>();
        string rawUrl = ReadFlexibleString(obj, "url", 2048);
        if (string.IsNullOrWhiteSpace(rawUrl))
            rawUrl = ReadFlexibleString(obj, "streamUrl", 2048);
        if (!string.IsNullOrWhiteSpace(rawUrl) &&
            TvUrlGuard.IsAllowedRemoteUrl(rawUrl, out var safeDirectUri) &&
            safeDirectUri != null &&
            !TvDenylist.IsDenylisted(safeDirectUri.AbsoluteUri, out _))
        {
            urls.Add(new LexAddonIptvChannelAlt { Url = safeDirectUri.AbsoluteUri, Quality = quality });
        }

        if (obj.TryGetProperty("streams", out var streamsProp) && streamsProp.ValueKind == JsonValueKind.Array)
        {
            var (_, parsedEntries, _) = LexAddonStreamParser.ParseStreams(obj.GetRawText(), addonId, addonName);
            foreach (var entry in parsedEntries)
            {
                if (!entry.Option.Playable || string.IsNullOrWhiteSpace(entry.Option.StreamUrl))
                    continue;
                rememberedStreams.Add(entry);
                if (!urls.Any(u => u.Url.Equals(entry.Option.StreamUrl, StringComparison.OrdinalIgnoreCase)))
                {
                    urls.Add(new LexAddonIptvChannelAlt
                    {
                        Url = entry.Option.StreamUrl,
                        Quality = !string.IsNullOrWhiteSpace(entry.Option.Name) ? entry.Option.Name : quality
                    });
                }
            }
        }

        string primaryUrl = urls.Count > 0 ? urls[0].Url : "";
        var alternates = urls.Count > 1 ? urls.Skip(1).ToList() : new List<LexAddonIptvChannelAlt>();

        return new LexAddonIptvChannel
        {
            Id = $"addon::{addonId}::{preview.Type}::{preview.Id}",
            Number = number,
            Name = preview.Name,
            Group = group,
            Logo = logo,
            Language = lang,
            Country = country,
            Quality = !string.IsNullOrWhiteSpace(quality) ? quality : (urls.Count > 0 ? urls[0].Quality : ""),
            Url = primaryUrl,
            Alternates = alternates,
            EpgNow = epgNow,
            AddonId = addonId,
            AddonName = addonName,
            ItemType = preview.Type,
            ItemId = preview.Id,
            IsAddonChannel = true
        };
    }

    public static (bool Success, LexAddonMetaDetail? Meta, string? Error) ParseMeta(
        string? json,
        string addonId,
        string addonName,
        string? manifestBaseUrl = null)
    {
        if (string.IsNullOrWhiteSpace(json))
            return (false, null, "El endpoint meta devolvió una respuesta vacía.");

        string trimmed = json.Trim();
        if (!trimmed.StartsWith('{'))
            return (false, null, "El endpoint meta no devolvió un objeto JSON válido.");

        try
        {
            using var doc = JsonDocument.Parse(trimmed, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 24
            });

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (false, null, "La raíz de meta debe ser un objeto JSON.");

            var root = doc.RootElement;
            JsonElement metaObj = root;
            if (root.TryGetProperty("meta", out var inner))
            {
                if (inner.ValueKind == JsonValueKind.Null)
                    return (false, null, "Título no encontrado en este addon.");
                if (inner.ValueKind != JsonValueKind.Object)
                    return (false, null, "El campo 'meta' no es un objeto válido.");
                metaObj = inner;
            }

            var preview = ParseMetaPreviewObject(metaObj, addonId, addonName, "movie", manifestBaseUrl);
            if (preview == null)
                return (false, null, "Los metadatos no incluyen 'id' y 'name' válidos.");

            var genres = new List<string>(preview.Genres);
            var directors = ReadStringOrArray(metaObj, "director", MaxStringListItems, 80);
            var cast = ReadStringOrArray(metaObj, "cast", MaxStringListItems, 80);
            ExtractLinksIntoMeta(metaObj, genres, directors, cast);

            string defaultVideoId = ReadFlexibleString(metaObj, "defaultVideoId", 180);
            if (string.IsNullOrWhiteSpace(defaultVideoId) &&
                metaObj.TryGetProperty("behaviorHints", out var bh) &&
                bh.ValueKind == JsonValueKind.Object)
            {
                defaultVideoId = ReadFlexibleString(bh, "defaultVideoId", 180);
            }
            if (!IsValidPathSegment(defaultVideoId, 180))
                defaultVideoId = "";

            var detail = new LexAddonMetaDetail
            {
                Id = preview.Id,
                Type = preview.Type,
                Name = preview.Name,
                Poster = preview.Poster,
                Background = preview.Background,
                Description = preview.Description,
                ReleaseInfo = preview.ReleaseInfo,
                ImdbRating = preview.ImdbRating,
                Genres = genres,
                Runtime = ReadFlexibleString(metaObj, "runtime", 48),
                Director = directors,
                Cast = cast,
                DefaultVideoId = defaultVideoId,
                Videos = ParseVideos(metaObj, manifestBaseUrl),
                AddonId = addonId,
                AddonName = addonName
            };

            return (true, detail, null);
        }
        catch (JsonException)
        {
            return (false, null, "No se pudo interpretar el JSON de metadatos.");
        }
    }

    internal static bool TryExtractManifestBase(
        string? manifestUrl,
        out string baseUrl,
        out string querySuffix,
        out string? error)
    {
        baseUrl = "";
        querySuffix = "";
        error = null;

        if (string.IsNullOrWhiteSpace(manifestUrl) || !Uri.TryCreate(manifestUrl.Trim(), UriKind.Absolute, out var uri))
        {
            error = "La URL del manifiesto del addon no es válida.";
            return false;
        }

        baseUrl = LexAddonManifestParser.ExtractBaseUrl(uri);
        querySuffix = string.IsNullOrEmpty(uri.Query) ? "" : uri.Query;
        return true;
    }

    private static LexAddonMetaPreview? ParseMetaPreviewObject(
        JsonElement obj,
        string addonId,
        string addonName,
        string fallbackType,
        string? manifestBaseUrl)
    {
        string id = ReadFlexibleString(obj, "id", 160);
        if (!IsValidPathSegment(id, 160)) return null;

        string name = ReadFlexibleString(obj, "name", 180);
        if (string.IsNullOrWhiteSpace(name))
            name = ReadFlexibleString(obj, "title", 180);
        if (string.IsNullOrWhiteSpace(name)) return null;

        string type = ReadFlexibleString(obj, "type", 32).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(type))
            type = string.IsNullOrWhiteSpace(fallbackType) ? "movie" : fallbackType.ToLowerInvariant();

        string? poster = LexAddonManifestParser.NormalizeOptionalImageUrl(
            ReadFlexibleString(obj, "poster", 512), manifestBaseUrl);
        string? background = LexAddonManifestParser.NormalizeOptionalImageUrl(
            ReadFlexibleString(obj, "background", 512), manifestBaseUrl);

        string posterShape = ReadFlexibleString(obj, "posterShape", 24).ToLowerInvariant();
        if (posterShape is not ("poster" or "square" or "landscape"))
            posterShape = "poster";

        string desc = ReadFlexibleString(obj, "description", 1200);
        if (string.IsNullOrWhiteSpace(desc))
            desc = ReadFlexibleString(obj, "overview", 1200);

        string releaseInfo = ReadFlexibleString(obj, "releaseInfo", 32);
        if (string.IsNullOrWhiteSpace(releaseInfo))
            releaseInfo = ReadFlexibleString(obj, "year", 16);
        if (string.IsNullOrWhiteSpace(releaseInfo))
        {
            string released = ReadFlexibleString(obj, "released", 32);
            if (!string.IsNullOrWhiteSpace(released))
            {
                int tIdx = released.IndexOf('T', StringComparison.Ordinal);
                releaseInfo = tIdx > 0 ? released[..tIdx] : released;
            }
        }

        string rating = ReadFlexibleString(obj, "imdbRating", 16);
        var genres = ReadStringOrArray(obj, "genres", MaxStringListItems, 40);
        if (genres.Count == 0)
            genres = ReadStringOrArray(obj, "genre", MaxStringListItems, 40);

        return new LexAddonMetaPreview
        {
            Id = id,
            Type = type,
            Name = name,
            Poster = poster,
            PosterShape = posterShape,
            Background = background,
            Description = desc,
            ReleaseInfo = releaseInfo,
            ImdbRating = rating,
            Genres = genres,
            AddonId = addonId,
            AddonName = addonName
        };
    }

    private static List<LexAddonVideoItem> ParseVideos(JsonElement metaObj, string? manifestBaseUrl)
    {
        var videos = new List<LexAddonVideoItem>();
        if (!metaObj.TryGetProperty("videos", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return videos;

        foreach (var v in arr.EnumerateArray())
        {
            if (videos.Count >= MaxVideosPerMeta) break;
            if (v.ValueKind != JsonValueKind.Object) continue;

            string vid = ReadFlexibleString(v, "id", 180);
            if (!IsValidPathSegment(vid, 180)) continue;

            string title = ReadFlexibleString(v, "title", 180);
            if (string.IsNullOrWhiteSpace(title))
                title = ReadFlexibleString(v, "name", 180);

            int episode = ReadFlexibleInt(v, "episode", -1);
            if (episode < 0)
                episode = ReadFlexibleInt(v, "number", 0);

            int season = ReadFlexibleInt(v, "season", episode > 0 ? 1 : 0);
            if (season < 0) season = 1;

            string released = ReadFlexibleString(v, "released", 32);
            if (string.IsNullOrWhiteSpace(released))
                released = ReadFlexibleString(v, "firstAired", 32);
            if (released.Length > 10 && released.Contains('T', StringComparison.Ordinal))
                released = released[..10];

            string? thumb = LexAddonManifestParser.NormalizeOptionalImageUrl(
                ReadFlexibleString(v, "thumbnail", 512), manifestBaseUrl);
            string overview = ReadFlexibleString(v, "overview", 600);

            videos.Add(new LexAddonVideoItem
            {
                Id = vid,
                Title = string.IsNullOrWhiteSpace(title) ? $"Episodio {Math.Max(1, episode)}" : title,
                Season = season,
                Episode = episode,
                Released = released,
                Thumbnail = thumb,
                Overview = overview
            });
        }

        return videos
            .OrderBy(v => v.Season == 0 ? int.MaxValue : v.Season)
            .ThenBy(v => v.Episode)
            .ToList();
    }

    private static string ReadFlexibleString(JsonElement obj, string propName, int maxLen)
    {
        if (!obj.TryGetProperty(propName, out var prop)) return "";
        return prop.ValueKind switch
        {
            JsonValueKind.String => TvUrlGuard.SanitizeHeaderValue(prop.GetString(), maxLen),
            JsonValueKind.Number => TvUrlGuard.SanitizeHeaderValue(prop.GetRawText(), maxLen),
            _ => ""
        };
    }

    private static int ReadFlexibleInt(JsonElement obj, string propName, int fallback)
    {
        if (!obj.TryGetProperty(propName, out var prop)) return fallback;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out int num))
            return num;
        if (prop.ValueKind == JsonValueKind.String &&
            int.TryParse(prop.GetString()?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            return parsed;
        }
        return fallback;
    }

    private static List<string> ReadStringOrArray(JsonElement obj, string propName, int maxCount, int maxLen)
    {
        var result = new List<string>();
        if (!obj.TryGetProperty(propName, out var prop)) return result;

        if (prop.ValueKind == JsonValueKind.String)
        {
            string val = TvUrlGuard.SanitizeHeaderValue(prop.GetString(), maxLen);
            if (!string.IsNullOrWhiteSpace(val))
                result.Add(val);
            return result;
        }

        if (prop.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in prop.EnumerateArray())
            {
                if (result.Count >= maxCount) break;
                if (item.ValueKind != JsonValueKind.String) continue;
                string val = TvUrlGuard.SanitizeHeaderValue(item.GetString(), maxLen);
                if (!string.IsNullOrWhiteSpace(val) && !result.Contains(val, StringComparer.OrdinalIgnoreCase))
                    result.Add(val);
            }
        }

        return result;
    }

    private static void ExtractLinksIntoMeta(
        JsonElement metaObj,
        List<string> genres,
        List<string> directors,
        List<string> cast)
    {
        if (!metaObj.TryGetProperty("links", out var linksProp) || linksProp.ValueKind != JsonValueKind.Array)
            return;

        foreach (var link in linksProp.EnumerateArray())
        {
            if (link.ValueKind != JsonValueKind.Object) continue;
            string name = ReadFlexibleString(link, "name", 80);
            if (string.IsNullOrWhiteSpace(name)) continue;
            string category = ReadFlexibleString(link, "category", 32).ToLowerInvariant();

            if (category is "genres" or "genre")
                AddUniqueCapped(genres, name);
            else if (category is "directors" or "director")
                AddUniqueCapped(directors, name);
            else if (category is "cast" or "actors" or "actor")
                AddUniqueCapped(cast, name);
        }
    }

    private static void AddUniqueCapped(List<string> target, string value)
    {
        if (target.Count >= MaxStringListItems) return;
        if (!target.Contains(value, StringComparer.OrdinalIgnoreCase))
            target.Add(value);
    }
}

