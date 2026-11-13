using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Windows;
using LexBrowser.Core.Tv.Vod.Contracts;
using LexBrowser.Core.Tv.Vod.Metadata;
using LexBrowser.Core.Tv.Vod.Providers;
using Microsoft.Web.WebView2.Core;

namespace LexBrowser.Core.Tv.Vod.Bridge;

/// <summary>
/// Controlador del puente seguro entre WebView2 y el subsistema VOD nativo.
/// Provee endpoints REST y soporte para mensajes JSON bidireccionales,
/// aislando el frontend de llamadas CORS directas y ocultando claves de API.
/// </summary>
public static class VodBridgeController
{
    public static async Task<bool> TryHandleVodApiAsync(
        CoreWebView2Environment env,
        CoreWebView2WebResourceRequestedEventArgs e,
        Uri uri,
        string path,
        string? body,
        CoreWebView2Deferral deferral,
        CancellationToken ct)
    {
        if (!path.StartsWith("api/vod/", StringComparison.OrdinalIgnoreCase))
            return false;

        var q = HttpUtility.ParseQueryString(uri.Query);

        // 1. TMDB Config
        if (path.Equals("api/vod/tmdb/config", StringComparison.OrdinalIgnoreCase))
        {
            if (e.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                string key = doc.RootElement.TryGetProperty("key", out var kProp) ? (kProp.GetString() ?? "") : "";
                TmdbMetaService.Shared.SetApiKey(key);
                e.Response = CreateJsonResponse(env, 200, "OK", new { configured = TmdbMetaService.Shared.IsConfigured });
                return true;
            }

            e.Response = CreateJsonResponse(env, 200, "OK", new { configured = TmdbMetaService.Shared.IsConfigured });
            return true;
        }

        // 2. TMDB Discover (Disney, Pixar, Marvel, etc.)
        if (path.Equals("api/vod/tmdb/discover", StringComparison.OrdinalIgnoreCase))
        {
            string studio = q["studio"] ?? "disney";
            int? year = int.TryParse(q["year"], out int y) ? y : null;
            string? genre = q["genre"];
            int page = int.TryParse(q["page"], out int p) ? p : 1;

            var items = await TmdbMetaService.Shared.DiscoverAsync("movie", studio, year, genre, page, ct).ConfigureAwait(false);
            e.Response = CreateJsonResponse(env, 200, "OK", new
            {
                items,
                configured = TmdbMetaService.Shared.IsConfigured,
                attribution = TmdbMetaService.TmdbAttribution
            });
            return true;
        }

        // 3. TMDB Movie Detail + Watch Providers PE
        if (path.Equals("api/vod/tmdb/movie", StringComparison.OrdinalIgnoreCase))
        {
            string id = q["id"] ?? "";
            var item = await TmdbMetaService.Shared.GetDetailsAsync(id, ct).ConfigureAwait(false);
            if (item == null)
            {
                e.Response = CreateJsonResponse(env, 404, "Not Found", new { error = "Película no encontrada." });
                return true;
            }

            e.Response = CreateJsonResponse(env, 200, "OK", new { item });
            return true;
        }

        // 4. AniList Discover
        if (path.Equals("api/vod/anilist/discover", StringComparison.OrdinalIgnoreCase))
        {
            string category = q["category"] ?? "trending";
            int? year = int.TryParse(q["year"], out int y) ? y : null;
            string? genre = q["genre"];
            int page = int.TryParse(q["page"], out int p) ? p : 1;

            var items = await AniListMetaService.Shared.DiscoverAsync("anime", category, year, genre, page, ct).ConfigureAwait(false);
            e.Response = CreateJsonResponse(env, 200, "OK", new { items });
            return true;
        }

        // 5. AniList Detail
        if (path.Equals("api/vod/anilist/detail", StringComparison.OrdinalIgnoreCase))
        {
            string id = q["id"] ?? "";
            var item = await AniListMetaService.Shared.GetDetailsAsync(id, ct).ConfigureAwait(false);
            if (item == null)
            {
                e.Response = CreateJsonResponse(env, 404, "Not Found", new { error = "Anime no encontrado." });
                return true;
            }

            e.Response = CreateJsonResponse(env, 200, "OK", new { item });
            return true;
        }

        // 6. Proveedores y Streams agregados (Add-on system)
        if (path.Equals("api/vod/streams", StringComparison.OrdinalIgnoreCase))
        {
            string type = q["type"] ?? "movie";
            string id = q["id"] ?? "";
            var streams = await VodProviderManager.Shared.GetAggregatedStreamsAsync(type, id, ct).ConfigureAwait(false);
            e.Response = CreateJsonResponse(env, 200, "OK", new { streams, total = streams.Count });
            return true;
        }

        // 7. Add-ons Manifests
        if (path.Equals("api/vod/addons", StringComparison.OrdinalIgnoreCase))
        {
            if (e.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                string url = doc.RootElement.TryGetProperty("url", out var uProp) ? (uProp.GetString() ?? "") : "";
                bool installed = await VodProviderManager.Shared.InstallAddonFromUrlAsync(url, ct).ConfigureAwait(false);
                e.Response = CreateJsonResponse(env, installed ? 200 : 400, installed ? "OK" : "Bad Request", new
                {
                    installed,
                    manifests = VodProviderManager.Shared.GetManifests()
                });
                return true;
            }

            e.Response = CreateJsonResponse(env, 200, "OK", new
            {
                manifests = VodProviderManager.Shared.GetManifests()
            });
            return true;
        }

        // 8. Borrar caché de metadatos VOD
        if (path.Equals("api/vod/cache/clear", StringComparison.OrdinalIgnoreCase))
        {
            TmdbMetaService.Shared.ClearCache();
            AniListMetaService.Shared.ClearCache();
            e.Response = CreateJsonResponse(env, 200, "OK", new { ok = true });
            return true;
        }

        // 9. Abrir enlace oficial en pestaña normal del navegador
        if (path.Equals("api/vod/open-official", StringComparison.OrdinalIgnoreCase))
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            string rawUrl = doc.RootElement.TryGetProperty("url", out var uProp) ? (uProp.GetString() ?? "") : "";

            if (!TvUrlGuard.IsAllowedRemoteUrl(rawUrl, out var safeUri) || safeUri == null)
            {
                e.Response = CreateJsonResponse(env, 400, "Bad Request", new { error = "URL no válida." });
                return true;
            }

            _ = Application.Current?.Dispatcher.BeginInvoke(new Action(async () =>
            {
                if (Application.Current.MainWindow is MainWindow win)
                {
                    await win.AddNewTabFromExternalAsync(safeUri.AbsoluteUri);
                }
            }));

            e.Response = CreateJsonResponse(env, 200, "OK", new { opened = true, url = safeUri.AbsoluteUri });
            return true;
        }

        return false;
    }

    private static CoreWebView2WebResourceResponse CreateJsonResponse(CoreWebView2Environment env, int code, string reason, object data)
    {
        string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
        string headers = "Content-Type: application/json; charset=utf-8\r\nAccess-Control-Allow-Origin: https://tv.lex.local\r\nCache-Control: no-cache\r\nX-Content-Type-Options: nosniff";
        return env.CreateWebResourceResponse(new MemoryStream(bytes, writable: false), code, reason, headers);
    }
}
