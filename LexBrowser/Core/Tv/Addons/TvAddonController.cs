using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Web.WebView2.Core;

namespace LexBrowser.Core.Tv.Addons;

/// <summary>
/// Enrutador modular de endpoints /api/addons/* invocado desde TvStudioService.
/// </summary>
internal static class TvAddonController
{
    public delegate CoreWebView2WebResourceResponse JsonResponseFactory(
        CoreWebView2Environment env,
        int code,
        string reason,
        object payload,
        string? errorClass = null);

    // Importante: no usar ConfigureAwait(false) en TryHandleAsync porque env y e son objetos COM STA de WebView2.
    public static async Task<bool> TryHandleAsync(
        CoreWebView2Environment env,
        CoreWebView2WebResourceRequestedEventArgs e,
        string path,
        string? body,
        JsonResponseFactory createJson,
        CancellationToken ct)
    {
        if (path.Equals("api/addons", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("api/addons/list", StringComparison.OrdinalIgnoreCase))
        {
            var addons = LexAddonService.Shared.ListAddons();
            e.Response = createJson(env, 200, "OK", new
            {
                addons,
                count = addons.Count,
                maxAddons = LexAddonStore.MaxInstalledAddons
            });
            return true;
        }

        if (path.Equals("api/addons/preview", StringComparison.OrdinalIgnoreCase))
        {
            var (url, allowLocal) = ParseUrlBody(body);
            var preview = await LexAddonService.Shared.PreviewFromUrlAsync(url, allowLocal, ct);
            if (!preview.Success || preview.Manifest == null)
            {
                e.Response = createJson(env, 400, "Bad Request", new
                {
                    ok = false,
                    error = preview.ErrorMessage ?? "No se pudo leer el manifiesto."
                });
                return true;
            }

            e.Response = createJson(env, 200, "OK", new
            {
                ok = true,
                displayUrl = preview.DisplayUrl ?? "",
                manifest = preview.Manifest
            });
            return true;
        }

        if (path.Equals("api/addons/install", StringComparison.OrdinalIgnoreCase))
        {
            var (url, allowLocal) = ParseUrlBody(body);
            var res = await LexAddonService.Shared.InstallFromUrlAsync(url, allowLocal, ct);
            if (!res.Success || res.Addon == null)
            {
                e.Response = createJson(env, 400, "Bad Request", new
                {
                    ok = false,
                    error = res.ErrorMessage ?? "No se pudo instalar el addon."
                });
                return true;
            }

            e.Response = createJson(env, 200, "OK", new
            {
                ok = true,
                updated = res.Updated,
                addon = res.Addon,
                addons = res.Addons
            });
            return true;
        }

        if (path.Equals("api/addons/toggle", StringComparison.OrdinalIgnoreCase))
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            string id = doc.RootElement.TryGetProperty("id", out var iEl) ? (iEl.GetString() ?? "") : "";
            bool enabled = !doc.RootElement.TryGetProperty("enabled", out var eEl) || eEl.ValueKind != JsonValueKind.False;

            var res = LexAddonService.Shared.SetEnabled(id, enabled);
            if (!res.Success)
            {
                e.Response = createJson(env, 404, "Not Found", new { ok = false, error = res.ErrorMessage });
                return true;
            }

            e.Response = createJson(env, 200, "OK", new { ok = true, addon = res.Addon, addons = res.Addons });
            return true;
        }

        if (path.Equals("api/addons/refresh", StringComparison.OrdinalIgnoreCase))
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            string id = doc.RootElement.TryGetProperty("id", out var iEl) ? (iEl.GetString() ?? "") : "";

            var res = await LexAddonService.Shared.RefreshAddonAsync(id, ct);
            if (!res.Success)
            {
                e.Response = createJson(env, 400, "Bad Request", new
                {
                    ok = false,
                    error = res.ErrorMessage ?? "No se pudo actualizar el addon.",
                    addons = res.Addons
                });
                return true;
            }

            e.Response = createJson(env, 200, "OK", new { ok = true, addon = res.Addon, addons = res.Addons });
            return true;
        }

        if (path.Equals("api/addons/remove", StringComparison.OrdinalIgnoreCase))
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            string id = doc.RootElement.TryGetProperty("id", out var iEl) ? (iEl.GetString() ?? "") : "";

            var res = LexAddonService.Shared.Remove(id);
            if (!res.Success)
            {
                e.Response = createJson(env, 404, "Not Found", new { ok = false, error = res.ErrorMessage });
                return true;
            }

            e.Response = createJson(env, 200, "OK", new { ok = true, addons = res.Addons });
            return true;
        }

        if (path.Equals("api/addons/catalogs", StringComparison.OrdinalIgnoreCase))
        {
            string? typeFilter = null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("type", out var tEl) && tEl.ValueKind == JsonValueKind.String)
                    typeFilter = tEl.GetString();
            }

            var catalogs = LexAddonService.Shared.ListCatalogDescriptors(typeFilter);
            var types = new System.Collections.Generic.List<string>();
            foreach (var c in catalogs)
            {
                if (!types.Exists(t => t.Equals(c.Type, StringComparison.OrdinalIgnoreCase)))
                    types.Add(c.Type);
            }

            e.Response = createJson(env, 200, "OK", new
            {
                ok = true,
                catalogs,
                types,
                count = catalogs.Count
            });
            return true;
        }

        if (path.Equals("api/addons/catalog", StringComparison.OrdinalIgnoreCase))
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = doc.RootElement;
            string addonId = ReadString(root, "addonId");
            string type = ReadString(root, "type");
            string catalogId = ReadString(root, "catalogId");
            string search = ReadString(root, "search");
            string genre = ReadString(root, "genre");
            int skip = root.TryGetProperty("skip", out var sEl) && sEl.TryGetInt32(out int sVal) ? Math.Max(0, sVal) : 0;

            var res = await LexAddonService.Shared.GetCatalogAsync(addonId, type, catalogId, search, genre, skip, ct);
            if (!res.Success)
            {
                e.Response = createJson(env, 400, "Bad Request", new
                {
                    ok = false,
                    error = res.ErrorMessage ?? "No se pudo cargar el catálogo."
                });
                return true;
            }

            e.Response = createJson(env, 200, "OK", new
            {
                ok = true,
                addonId = res.AddonId,
                addonName = res.AddonName,
                catalogName = res.CatalogName,
                items = res.Items,
                hasMore = res.HasMore,
                nextSkip = res.NextSkip
            });
            return true;
        }

        if (path.Equals("api/addons/meta", StringComparison.OrdinalIgnoreCase))
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = doc.RootElement;
            string addonId = ReadString(root, "addonId");
            string type = ReadString(root, "type");
            string id = ReadString(root, "id");

            var res = await LexAddonService.Shared.GetMetaAsync(addonId, type, id, ct);
            if (!res.Success || res.Meta == null)
            {
                e.Response = createJson(env, 404, "Not Found", new
                {
                    ok = false,
                    error = res.ErrorMessage ?? "No se encontraron metadatos para este título."
                });
                return true;
            }

            e.Response = createJson(env, 200, "OK", new
            {
                ok = true,
                meta = res.Meta
            });
            return true;
        }

        if (path.Equals("api/addons/streams", StringComparison.OrdinalIgnoreCase))
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = doc.RootElement;
            string addonId = ReadString(root, "addonId");
            string type = ReadString(root, "type");
            string id = ReadString(root, "id");

            var res = await LexAddonService.Shared.GetStreamsAsync(addonId, type, id, ct);
            if (!res.Success && (res.Streams == null || res.Streams.Count == 0))
            {
                e.Response = createJson(env, 400, "Bad Request", new
                {
                    ok = false,
                    queriedAddons = res.QueriedAddonsCount,
                    errors = res.AddonErrors,
                    error = res.ErrorMessage ?? "No se encontraron streams disponibles."
                });
                return true;
            }

            e.Response = createJson(env, 200, "OK", new
            {
                ok = true,
                streams = res.Streams,
                queriedAddons = res.QueriedAddonsCount,
                errors = res.AddonErrors
            });
            return true;
        }

        if (path.Equals("api/addons/subtitle", StringComparison.OrdinalIgnoreCase))
        {
            var q = HttpUtility.ParseQueryString(new Uri(e.Request.Uri).Query);
            string? rawSubUrl = q["u"];
            var res = await LexAddonService.Shared.FetchSubtitleAsWebVttAsync(rawSubUrl, ct);
            if (!res.Success || res.WebVtt == null)
            {
                e.Response = createJson(env, res.StatusCode >= 400 ? res.StatusCode : 400, "Error", new
                {
                    ok = false,
                    error = res.ErrorMessage ?? "Subtítulo no disponible."
                });
                return true;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(res.WebVtt);
            var ms = new MemoryStream(bytes, writable: false);
            string headers =
                "Content-Type: text/vtt; charset=utf-8\r\n" +
                $"Access-Control-Allow-Origin: {TvStudioService.AllowedCorsOrigin}\r\n" +
                "X-Content-Type-Options: nosniff\r\n" +
                "Cache-Control: private, max-age=600";
            e.Response = env.CreateWebResourceResponse(ms, 200, "OK", headers);
            return true;
        }

        if (path.Equals("api/addons/refresh-all", StringComparison.OrdinalIgnoreCase))
        {
            var res = await LexAddonService.Shared.RefreshAllAddonsAsync(ct);
            e.Response = createJson(env, 200, "OK", new
            {
                ok = true,
                totalChecked = res.TotalChecked,
                updatedCount = res.UpdatedCount,
                upToDateCount = res.UpToDateCount,
                errorCount = res.ErrorCount,
                updatedIds = res.UpdatedIds,
                errors = res.Errors,
                addons = res.Addons
            });
            return true;
        }

        if (path.Equals("api/addons/repos", StringComparison.OrdinalIgnoreCase))
        {
            var repos = LexAddonService.Shared.ListRepositories();
            e.Response = createJson(env, 200, "OK", new
            {
                ok = true,
                repositories = repos,
                count = repos.Count,
                maxRepositories = LexAddonStore.MaxRepositories
            });
            return true;
        }

        if (path.Equals("api/addons/repos/add", StringComparison.OrdinalIgnoreCase))
        {
            var (repoUrl, allowLocal) = ParseUrlBody(body, "repoUrl", "manifestUrl", "url");
            var res = await LexAddonService.Shared.AddRepositoryAsync(repoUrl, allowLocal, ct);
            if (!res.Success)
            {
                e.Response = createJson(env, 400, "Bad Request", new
                {
                    ok = false,
                    repositories = res.Repositories,
                    error = res.ErrorMessage ?? "No se pudo agregar el repositorio."
                });
                return true;
            }

            e.Response = createJson(env, 200, "OK", new
            {
                ok = true,
                repository = res.Repository,
                repositories = res.Repositories,
                entries = res.Entries
            });
            return true;
        }

        if (path.Equals("api/addons/repos/inspect", StringComparison.OrdinalIgnoreCase))
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            string id = ReadString(doc.RootElement, "id");
            var res = await LexAddonService.Shared.InspectRepositoryAsync(id, ct);
            if (!res.Success)
            {
                e.Response = createJson(env, 400, "Bad Request", new
                {
                    ok = false,
                    repository = res.Repository,
                    repositories = res.Repositories,
                    error = res.ErrorMessage ?? "No se pudo consultar el repositorio."
                });
                return true;
            }

            e.Response = createJson(env, 200, "OK", new
            {
                ok = true,
                repository = res.Repository,
                repositories = res.Repositories,
                entries = res.Entries
            });
            return true;
        }

        if (path.Equals("api/addons/repos/remove", StringComparison.OrdinalIgnoreCase))
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            string id = ReadString(doc.RootElement, "id");
            var res = LexAddonService.Shared.RemoveRepository(id);
            if (!res.Success)
            {
                e.Response = createJson(env, 404, "Not Found", new { ok = false, error = res.ErrorMessage });
                return true;
            }

            e.Response = createJson(env, 200, "OK", new
            {
                ok = true,
                repositories = res.Repositories
            });
            return true;
        }

        if (path.Equals("api/addons/search", StringComparison.OrdinalIgnoreCase))
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = doc.RootElement;
            string query = ReadString(root, "search");
            if (string.IsNullOrWhiteSpace(query))
                query = ReadString(root, "query");
            string type = ReadString(root, "type");

            var res = await LexAddonService.Shared.SearchCatalogsAsync(query, type, ct);
            if (!res.Success)
            {
                e.Response = createJson(env, 400, "Bad Request", new
                {
                    ok = false,
                    searchedCatalogs = res.SearchedCatalogsCount,
                    errors = res.Errors,
                    error = res.ErrorMessage ?? "No se pudo completar la búsqueda."
                });
                return true;
            }

            e.Response = createJson(env, 200, "OK", new
            {
                ok = true,
                items = res.Items,
                searchedCatalogs = res.SearchedCatalogsCount,
                errors = res.Errors
            });
            return true;
        }

        if (path.Equals("api/addons/subtitles", StringComparison.OrdinalIgnoreCase))
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = doc.RootElement;
            string addonId = ReadString(root, "addonId");
            string type = ReadString(root, "type");
            string id = ReadString(root, "id");
            string videoHash = ReadString(root, "videoHash");
            string filename = ReadString(root, "filename");
            long? videoSize = root.TryGetProperty("videoSize", out var vsEl) && vsEl.TryGetInt64(out long vsVal) && vsVal > 0
                ? vsVal
                : null;

            var res = await LexAddonService.Shared.GetSubtitlesAsync(
                addonId, type, id, videoHash, videoSize, filename, ct);
            if (!res.Success)
            {
                e.Response = createJson(env, 400, "Bad Request", new
                {
                    ok = false,
                    error = res.ErrorMessage ?? "No se pudieron consultar los subtítulos."
                });
                return true;
            }

            e.Response = createJson(env, 200, "OK", new
            {
                ok = true,
                subtitles = res.Subtitles,
                queriedAddons = res.QueriedAddonsCount,
                errors = res.AddonErrors
            });
            return true;
        }

        if (path.Equals("api/addons/iptv/sources", StringComparison.OrdinalIgnoreCase))
        {
            var sources = LexAddonService.Shared.ListIptvSources();
            e.Response = createJson(env, 200, "OK", new
            {
                ok = true,
                sources,
                count = sources.Count
            });
            return true;
        }

        if (path.Equals("api/addons/iptv/channels", StringComparison.OrdinalIgnoreCase))
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = doc.RootElement;
            string addonId = ReadString(root, "addonId");
            string type = ReadString(root, "type");
            string catalogId = ReadString(root, "catalogId");
            string search = ReadString(root, "search");
            string genre = ReadString(root, "genre");

            var res = await LexAddonService.Shared.GetIptvChannelsAsync(
                addonId, type, catalogId, search, genre, ct);
            if (!res.Success)
            {
                e.Response = createJson(env, 400, "Bad Request", new
                {
                    ok = false,
                    error = res.ErrorMessage ?? "No se pudieron cargar los canales IPTV del addon."
                });
                return true;
            }

            e.Response = createJson(env, 200, "OK", new
            {
                ok = true,
                sourceKey = res.SourceKey,
                sourceName = res.SourceName,
                channels = res.Channels
            });
            return true;
        }

        return false;
    }

    private static string ReadString(JsonElement root, string prop)
        => root.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String ? (el.GetString() ?? "") : "";

    private static (string Url, bool AllowLocalLoopback) ParseUrlBody(string? body, params string[] urlKeys)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        var keys = urlKeys.Length > 0 ? urlKeys : new[] { "manifestUrl", "url" };
        string url = "";
        foreach (string key in keys)
        {
            string val = ReadString(doc.RootElement, key);
            if (!string.IsNullOrWhiteSpace(val))
            {
                url = val;
                break;
            }
        }
        bool allowLocal = doc.RootElement.TryGetProperty("allowLocalLoopback", out var lEl) && lEl.ValueKind == JsonValueKind.True;
        return (url, allowLocal);
    }
}

