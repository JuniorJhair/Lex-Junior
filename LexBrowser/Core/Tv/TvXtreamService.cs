using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LexBrowser.Core.Tv;

/// <summary>
/// Servicio cliente de integración con la API de Xtream Codes.
/// Valida la red, resuelve DNS anti-SSRF, protege y redacta credenciales y
/// devuelve flujos seguros para TV en vivo y VOD (películas y series).
/// </summary>
public sealed class TvXtreamService
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private readonly Func<string, CancellationToken, Task<IPAddress[]>>? _customResolver;
    private readonly HttpClient? _injectedClient;

    public static TvXtreamService Shared { get; } = new();

    public TvXtreamService(Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null, HttpClient? injectedClient = null)
    {
        _customResolver = resolver;
        _injectedClient = injectedClient;
    }

    private HttpClient GetClient() => _injectedClient ?? TvUrlGuard.CreatePinnedHttpClient(_customResolver, DefaultTimeout);

    /// <summary>
    /// Valida sintaxis, protocolo y seguridad del servidor Xtream.
    /// Rechaza esquemas no HTTP/HTTPS, credenciales en la URL, y hosts locales o privados.
    /// </summary>
    public static (bool IsValid, Uri? Uri, bool IsHttpWarning, string? Error) ValidateServerUrl(string? serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
            return (false, null, false, "La URL del servidor no puede estar vacía.");

        string trimmed = serverUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return (false, null, false, "URL inválida.");

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return (false, null, false, "Solo se admiten protocolos HTTP y HTTPS.");

        if (!string.IsNullOrEmpty(uri.UserInfo))
            return (false, null, false, "No incluyas usuario o contraseña en la URL del servidor; usa los campos correspondientes.");

        if (TvUrlGuard.IsPrivateOrLocalHost(uri.Host))
            return (false, null, false, "El servidor apunta a una dirección IP o host privado/local, lo cual está bloqueado.");

        if (TvDenylist.IsDenylisted(uri.AbsoluteUri, out var denyReason))
            return (false, null, false, denyReason ?? "El dominio del servidor se encuentra bloqueado.");

        bool isHttp = uri.Scheme == Uri.UriSchemeHttp;
        return (true, uri, isHttp, null);
    }

    /// <summary>
    /// Autentica con el servidor Xtream Codes mediante /player_api.php?username={u}&password={p}.
    /// Redacta credenciales de cualquier excepción para evitar filtraciones en logs o UI.
    /// </summary>
    public async Task<TvXtreamAuthResult> AuthenticateAsync(string serverUrl, string username, string password, CancellationToken ct)
    {
        var val = ValidateServerUrl(serverUrl);
        if (!val.IsValid || val.Uri == null)
        {
            return new TvXtreamAuthResult
            {
                Success = false,
                ErrorMessage = val.Error ?? "URL inválida."
            };
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return new TvXtreamAuthResult
            {
                Success = false,
                ErrorMessage = "Se requieren usuario y contraseña."
            };
        }

        string cleanBase = val.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        string targetUrl = $"{cleanBase}/player_api.php?username={Uri.EscapeDataString(username.Trim())}&password={Uri.EscapeDataString(password.Trim())}";

        try
        {
            using var client = GetClient();
            var (response, _) = await TvUrlGuard.GetWithSafeRedirectsAsync(client, new Uri(targetUrl), rangeHeader: null, ct).ConfigureAwait(false);
            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    return new TvXtreamAuthResult
                    {
                        Success = false,
                        ErrorMessage = $"El servidor respondió con código {(int)response.StatusCode} {response.ReasonPhrase}."
                    };
                }

                string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return ParseAuthResponse(json, val.IsHttpWarning, username, password);
            }
        }
        catch (Exception ex)
        {
            string redacted = TvXtreamSecurity.Redact(ex.Message, username, password);
            return new TvXtreamAuthResult
            {
                Success = false,
                ErrorMessage = $"Error al conectar con el servidor: {redacted}"
            };
        }
    }

    public static TvXtreamAuthResult ParseAuthResponse(string json, bool isHttpWarning, string? username = null, string? password = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new TvXtreamAuthResult
            {
                Success = false,
                ErrorMessage = "Respuesta vacía del servidor."
            };
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return new TvXtreamAuthResult
                {
                    Success = false,
                    ErrorMessage = "Formato de respuesta del servidor inesperado (se esperaba un objeto JSON)."
                };
            }

            if (!root.TryGetProperty("user_info", out var userEl) || userEl.ValueKind != JsonValueKind.Object)
            {
                return new TvXtreamAuthResult
                {
                    Success = false,
                    ErrorMessage = "La respuesta del servidor no contiene información de usuario (user_info)."
                };
            }

            int auth = 0;
            if (userEl.TryGetProperty("auth", out var authEl))
            {
                if (authEl.ValueKind == JsonValueKind.Number) auth = authEl.GetInt32();
                else if (authEl.ValueKind == JsonValueKind.String && int.TryParse(authEl.GetString(), out int a)) auth = a;
            }

            string status = userEl.TryGetProperty("status", out var stEl) ? (stEl.GetString() ?? "") : "";
            if (auth != 1 || string.Equals(status, "Disabled", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "Banned", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "Expired", StringComparison.OrdinalIgnoreCase))
            {
                return new TvXtreamAuthResult
                {
                    Success = false,
                    ErrorMessage = $"Autenticación rechazada por el servidor (Estado: {(string.IsNullOrEmpty(status) ? "No autorizado" : status)})."
                };
            }

            var userInfo = new TvXtreamUserInfo
            {
                Username = userEl.TryGetProperty("username", out var uEl) ? (uEl.GetString() ?? "") : "",
                Status = status,
                Auth = auth,
                ExpDate = userEl.TryGetProperty("exp_date", out var expEl) ? expEl.ToString() : null,
                ActiveCons = userEl.TryGetProperty("active_cons", out var acEl) ? acEl.ToString() : null,
                MaxCons = userEl.TryGetProperty("max_connections", out var mcEl) ? mcEl.ToString() : null
            };

            var serverInfo = new TvXtreamServerInfo();
            if (root.TryGetProperty("server_info", out var srvEl) && srvEl.ValueKind == JsonValueKind.Object)
            {
                serverInfo.Url = srvEl.TryGetProperty("url", out var suEl) ? (suEl.GetString() ?? "") : "";
                serverInfo.Port = srvEl.TryGetProperty("port", out var pEl) ? pEl.ToString() : "";
                serverInfo.HttpsPort = srvEl.TryGetProperty("https_port", out var hpEl) ? hpEl.ToString() : "";
                serverInfo.ServerProtocol = srvEl.TryGetProperty("server_protocol", out var spEl) ? (spEl.GetString() ?? "") : "";
                serverInfo.Timezone = srvEl.TryGetProperty("timezone", out var tzEl) ? tzEl.GetString() : null;
            }

            return new TvXtreamAuthResult
            {
                Success = true,
                UserInfo = userInfo,
                ServerInfo = serverInfo,
                IsHttpWarning = isHttpWarning
            };
        }
        catch (JsonException)
        {
            return new TvXtreamAuthResult
            {
                Success = false,
                ErrorMessage = "El servidor devolvió una respuesta que no es JSON válido (respuesta malformada)."
            };
        }
    }

    /// <summary>
    /// Obtiene canales de televisión en vivo organizados por categorías.
    /// Las URLs devueltas apuntan al proxy virtual interno de Lex TV (sin exponer credenciales).
    /// </summary>
    public async Task<List<TvChannel>> FetchLiveChannelsAsync(string serverUrl, string username, string password, string connId, string connName, CancellationToken ct)
    {
        var categories = await FetchCategoriesAsync(serverUrl, username, password, "get_live_categories", ct).ConfigureAwait(false);
        string url = $"{serverUrl.TrimEnd('/')}/player_api.php?username={Uri.EscapeDataString(username)}&password={Uri.EscapeDataString(password)}&action=get_live_streams";

        string json;
        try
        {
            using var client = GetClient();
            var (resp, _) = await TvUrlGuard.GetWithSafeRedirectsAsync(client, new Uri(url), null, ct).ConfigureAwait(false);
            using (resp)
            {
                if (!resp.IsSuccessStatusCode) return new List<TvChannel>();
                json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            throw TvXtreamSecurity.RedactException(ex, username, password);
        }

        return ParseLiveStreams(json, categories, connId, connName);
    }

    public static List<TvChannel> ParseLiveStreams(string json, Dictionary<string, string> categories, string connId, string connName)
    {
        var list = new List<TvChannel>();
        if (string.IsNullOrWhiteSpace(json)) return list;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

            int autoNum = 1;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                string streamId = item.TryGetProperty("stream_id", out var sidEl) ? sidEl.ToString() : "";
                string name = item.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? "") : "";
                if (string.IsNullOrWhiteSpace(streamId) || string.IsNullOrWhiteSpace(name)) continue;

                string catId = item.TryGetProperty("category_id", out var cidEl) ? cidEl.ToString() : "";
                string group = !string.IsNullOrEmpty(catId) && categories.TryGetValue(catId, out var cName) ? cName : "En vivo";

                int num = autoNum++;
                if (item.TryGetProperty("num", out var numEl) && numEl.TryGetInt32(out int parsedNum) && parsedNum > 0)
                    num = parsedNum;

                string? logo = item.TryGetProperty("stream_icon", out var iconEl) ? iconEl.GetString() : null;

                // URL segura protegida en el host virtual tv.lex.local sin exponer credenciales
                string secureStreamUrl = $"https://tv.lex.local/api/xtream/live?conn={Uri.EscapeDataString(connId)}&id={Uri.EscapeDataString(streamId)}";

                string quality = name.Contains("4K", StringComparison.OrdinalIgnoreCase) ? "4K" :
                                 name.Contains("FHD", StringComparison.OrdinalIgnoreCase) || name.Contains("1080", StringComparison.OrdinalIgnoreCase) ? "FHD" :
                                 name.Contains("HD", StringComparison.OrdinalIgnoreCase) ? "HD" : "";

                list.Add(new TvChannel
                {
                    Number = num,
                    Id = $"xc_live_{connId}_{streamId}",
                    Name = name,
                    Url = secureStreamUrl,
                    Group = string.IsNullOrWhiteSpace(group) ? "En vivo" : group,
                    Logo = logo ?? "",
                    Source = connName,
                    Quality = quality
                });
            }
        }
        catch { }

        return list;
    }

    /// <summary>
    /// Obtiene películas VOD organizadas por categorías.
    /// Las URLs devueltas apuntan al proxy virtual interno de Lex TV (sin exponer credenciales).
    /// </summary>
    public async Task<List<TvVodItem>> FetchVodMoviesAsync(string serverUrl, string username, string password, string connId, CancellationToken ct)
    {
        var categories = await FetchCategoriesAsync(serverUrl, username, password, "get_vod_categories", ct).ConfigureAwait(false);
        string url = $"{serverUrl.TrimEnd('/')}/player_api.php?username={Uri.EscapeDataString(username)}&password={Uri.EscapeDataString(password)}&action=get_vod_streams";

        string json;
        try
        {
            using var client = GetClient();
            var (resp, _) = await TvUrlGuard.GetWithSafeRedirectsAsync(client, new Uri(url), null, ct).ConfigureAwait(false);
            using (resp)
            {
                if (!resp.IsSuccessStatusCode) return new List<TvVodItem>();
                json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
        }
        catch
        {
            return new List<TvVodItem>();
        }

        return ParseVodStreams(json, categories, connId);
    }

    public static List<TvVodItem> ParseVodStreams(string json, Dictionary<string, string> categories, string connId)
    {
        var list = new List<TvVodItem>();
        if (string.IsNullOrWhiteSpace(json)) return list;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                string streamId = item.TryGetProperty("stream_id", out var sidEl) ? sidEl.ToString() : "";
                string name = item.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? "") : "";
                if (string.IsNullOrWhiteSpace(streamId) || string.IsNullOrWhiteSpace(name)) continue;

                string catId = item.TryGetProperty("category_id", out var cidEl) ? cidEl.ToString() : "";
                string group = !string.IsNullOrEmpty(catId) && categories.TryGetValue(catId, out var cName) ? cName : "Películas";

                string ext = item.TryGetProperty("container_extension", out var extEl) ? (extEl.GetString() ?? "mp4") : "mp4";
                string? logo = item.TryGetProperty("stream_icon", out var iconEl) ? iconEl.GetString() : null;

                string secureUrl = $"https://tv.lex.local/api/xtream/vod?conn={Uri.EscapeDataString(connId)}&id={Uri.EscapeDataString(streamId)}&ext={Uri.EscapeDataString(ext)}";

                list.Add(new TvVodItem
                {
                    Id = $"xc_vod_{connId}_{streamId}",
                    Title = name,
                    Url = secureUrl,
                    Group = group,
                    Logo = logo,
                    IsSeries = false,
                    AddedUtc = DateTime.UtcNow
                });
            }
        }
        catch { }

        return list;
    }

    /// <summary>
    /// Obtiene series y sus episodios cuando el servidor Xtream las ofrece.
    /// Si el servidor no soporta series, retorna lista vacía sin provocar error.
    /// </summary>
    public async Task<List<TvVodItem>> FetchSeriesAsync(string serverUrl, string username, string password, string connId, CancellationToken ct)
    {
        var categories = await FetchCategoriesAsync(serverUrl, username, password, "get_series_categories", ct).ConfigureAwait(false);
        string url = $"{serverUrl.TrimEnd('/')}/player_api.php?username={Uri.EscapeDataString(username)}&password={Uri.EscapeDataString(password)}&action=get_series";

        string json;
        try
        {
            using var client = GetClient();
            var (resp, _) = await TvUrlGuard.GetWithSafeRedirectsAsync(client, new Uri(url), null, ct).ConfigureAwait(false);
            using (resp)
            {
                if (!resp.IsSuccessStatusCode) return new List<TvVodItem>();
                json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
        }
        catch
        {
            return new List<TvVodItem>();
        }

        var seriesList = ParseSeriesList(json, categories);
        var result = new List<TvVodItem>();

        // Obtener episodios de cada serie (hasta 200 series para balancear tiempo de importación inicial)
        foreach (var s in seriesList.Take(200))
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                string infoUrl = $"{serverUrl.TrimEnd('/')}/player_api.php?username={Uri.EscapeDataString(username)}&password={Uri.EscapeDataString(password)}&action=get_series_info&series_id={Uri.EscapeDataString(s.SeriesId)}";
                using var client = GetClient();
                var (infoResp, _) = await TvUrlGuard.GetWithSafeRedirectsAsync(client, new Uri(infoUrl), null, ct).ConfigureAwait(false);
                using (infoResp)
                {
                    if (infoResp.IsSuccessStatusCode)
                    {
                        string infoJson = await infoResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        var episodes = ParseSeriesInfoEpisodes(infoJson, s, connId);
                        result.AddRange(episodes);
                    }
                }
            }
            catch { }
        }

        return result;
    }

    public static List<TvXtreamSeriesItem> ParseSeriesList(string json, Dictionary<string, string> categories)
    {
        var list = new List<TvXtreamSeriesItem>();
        if (string.IsNullOrWhiteSpace(json)) return list;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                string seriesId = item.TryGetProperty("series_id", out var sidEl) ? sidEl.ToString() : "";
                string name = item.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? "") : "";
                if (string.IsNullOrWhiteSpace(seriesId) || string.IsNullOrWhiteSpace(name)) continue;

                string catId = item.TryGetProperty("category_id", out var cidEl) ? cidEl.ToString() : "";
                string? cover = item.TryGetProperty("cover", out var covEl) ? covEl.GetString() : null;

                list.Add(new TvXtreamSeriesItem
                {
                    SeriesId = seriesId,
                    Name = name,
                    Cover = cover,
                    CategoryId = catId
                });
            }
        }
        catch { }

        return list;
    }

    public static List<TvVodItem> ParseSeriesInfoEpisodes(string json, TvXtreamSeriesItem series, string connId)
    {
        var list = new List<TvVodItem>();
        if (string.IsNullOrWhiteSpace(json)) return list;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("episodes", out var epRoot) || epRoot.ValueKind != JsonValueKind.Object)
                return list;

            foreach (var seasonProp in epRoot.EnumerateObject())
            {
                int seasonNum = int.TryParse(seasonProp.Name, out int sn) ? sn : 1;
                if (seasonProp.Value.ValueKind != JsonValueKind.Array) continue;

                foreach (var ep in seasonProp.Value.EnumerateArray())
                {
                    string epId = ep.TryGetProperty("id", out var idEl) ? idEl.ToString() : "";
                    string epTitle = ep.TryGetProperty("title", out var tEl) ? (tEl.GetString() ?? "") : "";
                    if (string.IsNullOrWhiteSpace(epId)) continue;

                    int epNum = 1;
                    if (ep.TryGetProperty("episode_num", out var enEl))
                    {
                        if (enEl.ValueKind == JsonValueKind.Number) epNum = enEl.GetInt32();
                        else if (enEl.ValueKind == JsonValueKind.String && int.TryParse(enEl.GetString(), out int parsed)) epNum = parsed;
                    }

                    string ext = ep.TryGetProperty("container_extension", out var extEl) ? (extEl.GetString() ?? "mp4") : "mp4";
                    string? epCover = ep.TryGetProperty("info", out var infoEl) && infoEl.TryGetProperty("movie_image", out var miEl)
                        ? miEl.GetString()
                        : series.Cover;

                    string secureUrl = $"https://tv.lex.local/api/xtream/series?conn={Uri.EscapeDataString(connId)}&id={Uri.EscapeDataString(epId)}&ext={Uri.EscapeDataString(ext)}";
                    string displayTitle = !string.IsNullOrWhiteSpace(epTitle)
                        ? $"{series.Name} S{seasonNum:D2}E{epNum:D2} - {epTitle}"
                        : $"{series.Name} S{seasonNum:D2}E{epNum:D2}";

                    list.Add(new TvVodItem
                    {
                        Id = $"xc_series_{connId}_{series.SeriesId}_{epId}",
                        Title = displayTitle,
                        Url = secureUrl,
                        Group = series.Name,
                        Logo = epCover,
                        IsSeries = true,
                        SeriesTitle = series.Name,
                        SeasonNumber = seasonNum,
                        EpisodeNumber = epNum,
                        EpisodeTitle = epTitle,
                        AddedUtc = DateTime.UtcNow
                    });
                }
            }
        }
        catch { }

        return list;
    }

    private async Task<Dictionary<string, string>> FetchCategoriesAsync(string serverUrl, string username, string password, string action, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string url = $"{serverUrl.TrimEnd('/')}/player_api.php?username={Uri.EscapeDataString(username)}&password={Uri.EscapeDataString(password)}&action={action}";

        try
        {
            using var client = GetClient();
            var (resp, _) = await TvUrlGuard.GetWithSafeRedirectsAsync(client, new Uri(url), null, ct).ConfigureAwait(false);
            using (resp)
            {
                if (!resp.IsSuccessStatusCode) return map;
                string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return map;

                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    string id = el.TryGetProperty("category_id", out var idEl) ? idEl.ToString() : "";
                    string name = el.TryGetProperty("category_name", out var nEl) ? (nEl.GetString() ?? "") : "";
                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                    {
                        map[id] = name;
                    }
                }
            }
        }
        catch { }

        return map;
    }

    /// <summary>
    /// Resuelve de forma segura y en memoria la URL real upstream de reproducción
    /// combinando credenciales descifradas por DPAPI y validando contra TvUrlGuard.
    /// Nunca expone las credenciales a la interfaz web ni a registros.
    /// </summary>
    public static (bool Success, string? UpstreamUrl, string? Error) ResolveUpstreamStreamUrl(
        TvXtreamStore store,
        string connId,
        string streamType,
        string streamId,
        string? extension = null)
    {
        if (string.IsNullOrWhiteSpace(connId) || string.IsNullOrWhiteSpace(streamId))
            return (false, null, "Identificadores de flujo faltantes.");

        var conn = store.GetConnection(connId);
        if (conn == null)
            return (false, null, "Conexión Xtream no encontrada.");

        var creds = store.GetCredentials(connId);
        if (creds == null || string.IsNullOrWhiteSpace(creds.Username) || string.IsNullOrWhiteSpace(creds.Password))
            return (false, null, "Credenciales no disponibles para esta conexión (sesión expirada o no guardadas).");

        string serverBase = conn.ServerUrl.TrimEnd('/');
        string cleanType = streamType.ToLowerInvariant();
        if (cleanType != "live" && cleanType != "movie" && cleanType != "series")
            return (false, null, "Tipo de stream no soportado.");

        string ext = "";
        if (!string.IsNullOrWhiteSpace(extension))
        {
            string cleanExt = extension.TrimStart('.');
            if (!string.IsNullOrEmpty(cleanExt) && cleanExt.All(char.IsLetterOrDigit))
                ext = "." + cleanExt;
        }

        string rawUpstream;
        if (cleanType == "live")
        {
            string liveExt = string.IsNullOrEmpty(ext) ? ".ts" : ext;
            rawUpstream = $"{serverBase}/live/{Uri.EscapeDataString(creds.Username)}/{Uri.EscapeDataString(creds.Password)}/{Uri.EscapeDataString(streamId)}{liveExt}";
        }
        else if (cleanType == "movie")
        {
            string movieExt = string.IsNullOrEmpty(ext) ? ".mp4" : ext;
            rawUpstream = $"{serverBase}/movie/{Uri.EscapeDataString(creds.Username)}/{Uri.EscapeDataString(creds.Password)}/{Uri.EscapeDataString(streamId)}{movieExt}";
        }
        else
        {
            string seriesExt = string.IsNullOrEmpty(ext) ? ".mp4" : ext;
            rawUpstream = $"{serverBase}/series/{Uri.EscapeDataString(creds.Username)}/{Uri.EscapeDataString(creds.Password)}/{Uri.EscapeDataString(streamId)}{seriesExt}";
        }

        if (!TvUrlGuard.IsAllowedRemoteUrl(rawUpstream, out var validatedUri) || validatedUri == null)
            return (false, null, "La URL del servidor no cumple los requisitos de seguridad de red de Lex TV.");

        return (true, rawUpstream, null);
    }
}
