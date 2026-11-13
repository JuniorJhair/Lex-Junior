using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LexBrowser.Core.Tv.Vod.Contracts;

namespace LexBrowser.Core.Tv.Vod.Providers;

/// <summary>
/// Proveedor de VOD modular que se comunica con un Add-on externo siguiendo
/// la especificación estándar de manifiestos y endpoints (Stremio Add-on Protocol).
/// </summary>
public sealed class ManifestVodProvider : IVodProvider
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private VodManifest _manifest;

    public VodManifest Manifest => _manifest;

    public ManifestVodProvider(VodManifest manifest, HttpClient? httpClient = null)
    {
        _manifest = manifest;
        _baseUrl = (manifest.EndpointUrl ?? "").TrimEnd('/');
        _http = httpClient ?? TvCatalogService.Http;
    }

    public static async Task<ManifestVodProvider?> LoadFromUrlAsync(string manifestUrl, HttpClient? httpClient = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl)) return null;
        var client = httpClient ?? TvCatalogService.Http;

        string cleanUrl = manifestUrl.Trim();
        if (!cleanUrl.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            cleanUrl = cleanUrl.TrimEnd('/') + "/manifest.json";
        }

        if (!TvUrlGuard.IsAllowedRemoteUrl(cleanUrl, out var safeUri) || safeUri == null)
            return null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(8));

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, safeUri);
            using var resp = await client.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            string json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize<VodManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (manifest == null || string.IsNullOrEmpty(manifest.Id)) return null;

            manifest.EndpointUrl = safeUri.GetLeftPart(UriPartial.Path).Replace("/manifest.json", "");
            return new ManifestVodProvider(manifest, client);
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<MediaItem>> GetCatalogAsync(string type, string? catalogId, string? genre, int page, CancellationToken ct)
    {
        var result = new List<MediaItem>();
        if (string.IsNullOrEmpty(_baseUrl)) return result;

        string catId = catalogId ?? "top";
        string url = $"{_baseUrl}/catalog/{type}/{catId}.json";
        if (!string.IsNullOrEmpty(genre) && !genre.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            url += $"?genre={Uri.EscapeDataString(genre)}";
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return result;

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("metas", out var metasArr) && metasArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in metasArr.EnumerateArray())
                {
                    string id = el.TryGetProperty("id", out var idProp) ? (idProp.GetString() ?? "") : "";
                    string title = el.TryGetProperty("name", out var nProp) ? (nProp.GetString() ?? "") : "";
                    string poster = el.TryGetProperty("poster", out var pProp) ? (pProp.GetString() ?? "") : "";
                    string desc = el.TryGetProperty("description", out var dProp) ? (dProp.GetString() ?? "") : "";

                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(title))
                    {
                        result.Add(new MediaItem
                        {
                            Id = id,
                            Type = type,
                            Title = title,
                            PosterUrl = poster,
                            Synopsis = desc,
                            SourceName = _manifest.Name
                        });
                    }
                }
            }
        }
        catch { }

        return result;
    }

    public async Task<MediaItem?> GetDetailAsync(string type, string id, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_baseUrl)) return null;

        string url = $"{_baseUrl}/meta/{type}/{Uri.EscapeDataString(id)}.json";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("meta", out var metaEl))
            {
                string title = metaEl.TryGetProperty("name", out var nProp) ? (nProp.GetString() ?? "") : "";
                string poster = metaEl.TryGetProperty("poster", out var pProp) ? (pProp.GetString() ?? "") : "";
                string desc = metaEl.TryGetProperty("description", out var dProp) ? (dProp.GetString() ?? "") : "";

                return new MediaItem
                {
                    Id = id,
                    Type = type,
                    Title = title,
                    PosterUrl = poster,
                    Synopsis = desc,
                    SourceName = _manifest.Name
                };
            }
        }
        catch { }

        return null;
    }

    public async Task<List<StreamSource>> GetStreamsAsync(string type, string id, CancellationToken ct)
    {
        var result = new List<StreamSource>();
        if (string.IsNullOrEmpty(_baseUrl)) return result;

        string url = $"{_baseUrl}/stream/{type}/{Uri.EscapeDataString(id)}.json";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return result;

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("streams", out var strArr) && strArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in strArr.EnumerateArray())
                {
                    string streamUrl = el.TryGetProperty("url", out var uProp) ? (uProp.GetString() ?? "") : "";
                    string streamTitle = el.TryGetProperty("title", out var tProp) ? (tProp.GetString() ?? "Stream") : "Stream";

                    if (!string.IsNullOrEmpty(streamUrl) && TvUrlGuard.IsAllowedRemoteUrl(streamUrl, out var safeUri) && safeUri != null)
                    {
                        string format = streamUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ? "hls" : "mp4";
                        result.Add(new StreamSource
                        {
                            ProviderId = _manifest.Id,
                            Title = $"{streamTitle} [{_manifest.Name}]",
                            Url = safeUri.AbsoluteUri,
                            Format = format,
                            Quality = streamTitle.Contains("1080") ? "1080p" : streamTitle.Contains("720") ? "720p" : "auto"
                        });
                    }
                }
            }
        }
        catch { }

        return result;
    }
}

