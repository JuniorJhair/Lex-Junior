using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace LexBrowser.Core.Tv;

/// <summary>
/// Servicio cliente de integración con la API de Xtream Codes.
/// Valida la red, resuelve DNS anti-SSRF, protege y redacta credenciales y
/// devuelve flujos seguros para TV en vivo y VOD (películas y series).
/// </summary>
public sealed class TvXtreamService : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);
    private static readonly Regex TmdbSizePathRegex = new(
        @"^/?(?:w\d+|original)/[A-Za-z0-9_-]+\.(?:jpg|jpeg|png|webp)(?:\?.*)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TmdbBareHashRegex = new(
        @"^/?[A-Za-z0-9_-]{18,}\.(?:jpg|jpeg|png|webp)(?:\?.*)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly Func<string, CancellationToken, Task<IPAddress[]>>? _customResolver;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public static TvXtreamService Shared { get; } = new();

    public TvXtreamService(Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null, HttpClient? injectedClient = null)
    {
        _customResolver = resolver;
        if (injectedClient != null)
        {
            _client = injectedClient;
            _ownsClient = false;
        }
        else
        {
            _client = TvUrlGuard.CreatePinnedHttpClient(_customResolver, DefaultTimeout);
            _ownsClient = true;
        }
    }

    private HttpClient GetClient() => _client;

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    /// <summary>
    /// Normaliza la URL base del servidor Xtream añadiendo http:// si falta el esquema
    /// y eliminando barras finales, query o fragmentos sin alterar el puerto explícito.
    /// </summary>
    public static string? NormalizeServerBaseUrl(string? serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)) return null;
        string trimmed = serverUrl.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal))
            trimmed = "http://" + trimmed;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;

        int qIdx = trimmed.IndexOfAny(['?', '#']);
        string withoutQuery = qIdx >= 0 ? trimmed[..qIdx] : trimmed;
        return withoutQuery.TrimEnd('/');
    }

    private async Task<(HttpResponseMessage Response, Uri FinalUri)> SendApiGetAsync(Uri targetUri, CancellationToken ct)
    {
        var candidates = TvUrlGuard.GetUserAgentCandidates(TvPlaybackHints.GetUserAgent(null, targetUri.Host));
        HttpResponseMessage? lastResp = null;
        Uri lastFinalUri = targetUri;

        for (int i = 0; i < candidates.Count; i++)
        {
            string ua = candidates[i];
            var (resp, finalUri) = await TvUrlGuard.GetWithSafeRedirectsAsync(GetClient(), targetUri, rangeHeader: null, ct, userAgent: ua).ConfigureAwait(false);
            lastResp = resp;
            lastFinalUri = finalUri;

            int code = (int)resp.StatusCode;
            if (resp.IsSuccessStatusCode)
            {
                TvPlaybackHints.RememberUserAgent(null, targetUri.Host, ua);
                TvPlaybackHints.RememberUserAgent(null, finalUri.Host, ua);
                return (resp, finalUri);
            }

            // 400 suele ser "Action not found" en player_api; devolver de inmediato para activar el fallback con action
            if (code == 400)
                return (resp, finalUri);

            bool shouldRetryUa = (code == 401 || code == 403 || code == 404 || code == 406) && i + 1 < candidates.Count;
            if (!shouldRetryUa)
                return (resp, finalUri);

            resp.Dispose();
        }

        return (lastResp!, lastFinalUri);
    }

    /// <summary>
    /// Valida sintaxis, protocolo y seguridad del servidor Xtream.
    /// Rechaza esquemas no HTTP/HTTPS, credenciales en la URL, y hosts locales o privados.
    /// Antepone http:// automáticamente si el usuario ingresó únicamente el host/dominio.
    /// </summary>
    public static (bool IsValid, Uri? Uri, bool IsHttpWarning, string? Error) ValidateServerUrl(string? serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
            return (false, null, false, "La URL del servidor no puede estar vacía.");

        string trimmed = serverUrl.Trim();
        if (!trimmed.Contains("://"))
        {
            trimmed = "http://" + trimmed;
        }

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
    /// Comprueba si el cuerpo de respuesta de un error HTTP 400 contiene específicamente 'Action not found'.
    /// </summary>
    public static bool IsActionNotFoundError(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody)) return false;

        if (responseBody.Contains("Action not found", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("message", out var msgEl) &&
                    msgEl.GetString()?.Equals("Action not found", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return true;
                }
            }
        }
        catch { }

        return false;
    }

    /// <summary>
    /// Determina si una respuesta JSON corresponde a una lista de categorías válida de Xtream Codes.
    /// Valida que sea un array JSON de objetos (o un array vacío representativo de cuenta sin contenido).
    /// </summary>
    public static bool IsValidCategoriesResponse(string? json, out bool isEmpty)
    {
        isEmpty = false;
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array) return false;

            int len = root.GetArrayLength();
            if (len == 0)
            {
                isEmpty = true;
                return true;
            }

            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    if (item.TryGetProperty("category_id", out _) || item.TryGetProperty("category_name", out _))
                        return true;
                }
            }

            return root[0].ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Autentica con el servidor Xtream Codes.
    /// 1. Intenta la solicitud tradicional mediante /player_api.php?username={u}&password={p} sin acción.
    /// 2. Si responde HTTP 400 específicamente con 'Action not found', ejecuta la operación de compatibilidad
    ///    consultando get_live_categories (o get_vod_categories) para paneles que exigen action obligatoria.
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

        string cleanBase = NormalizeServerBaseUrl(val.Uri.AbsoluteUri) ?? val.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        string targetUrl = $"{cleanBase}/player_api.php?username={Uri.EscapeDataString(username.Trim())}&password={Uri.EscapeDataString(password.Trim())}";

        try
        {
            var (response, _) = await SendApiGetAsync(new Uri(targetUrl), ct).ConfigureAwait(false);
            using (response)
            {
                // Flujo 1: Servidor tradicional que responde 2xx
                if (response.IsSuccessStatusCode)
                {
                    string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    return ParseAuthResponse(json, val.IsHttpWarning, username, password);
                }

                // Flujo 2: Servidor que exige action obligatoria (HTTP 400 con "Action not found")
                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    string errBody = "";
                    try
                    {
                        errBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    }
                    catch { }

                    if (IsActionNotFoundError(errBody))
                    {
                        return await AuthenticateWithActionFallbackAsync(cleanBase, username, password, val.IsHttpWarning, ct).ConfigureAwait(false);
                    }

                    return new TvXtreamAuthResult
                    {
                        Success = false,
                        ErrorMessage = "El servidor rechazó la solicitud (código 400 Bad Request)."
                    };
                }

                // Otros códigos HTTP claramente diferenciados
                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                {
                    return new TvXtreamAuthResult
                    {
                        Success = false,
                        ErrorMessage = $"Autenticación rechazada por el servidor: credenciales inválidas (código {(int)response.StatusCode})."
                    };
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return new TvXtreamAuthResult
                    {
                        Success = false,
                        ErrorMessage = "El servidor no encontró el endpoint solicitado (código 404). Verifica que la URL del servidor sea correcta."
                    };
                }

                return new TvXtreamAuthResult
                {
                    Success = false,
                    ErrorMessage = $"El servidor respondió con código {(int)response.StatusCode} {response.ReasonPhrase}."
                };
            }
        }
        catch (Exception ex)
        {
            string redacted = TvXtreamSecurity.Redact(ex.Message, username, password);
            if (ex is TimeoutException or TaskCanceledException)
            {
                return new TvXtreamAuthResult
                {
                    Success = false,
                    ErrorMessage = "Tiempo de espera agotado al conectar con el servidor Xtream."
                };
            }
            if (ex is SocketException or HttpRequestException)
            {
                return new TvXtreamAuthResult
                {
                    Success = false,
                    ErrorMessage = $"No se pudo conectar con el servidor Xtream ({redacted})."
                };
            }

            return new TvXtreamAuthResult
            {
                Success = false,
                ErrorMessage = $"Error al conectar con el servidor: {redacted}"
            };
        }
    }

    /// <summary>
    /// Operación de compatibilidad para servidores Xtream que exigen obligatoriamente el parámetro 'action'.
    /// Consulta get_live_categories (o get_vod_categories) y valida rigurosamente la respuesta JSON.
    /// </summary>
    private async Task<TvXtreamAuthResult> AuthenticateWithActionFallbackAsync(
        string cleanBase,
        string username,
        string password,
        bool isHttpWarning,
        CancellationToken ct)
    {
        // 1. Probar con get_live_categories
        string liveCatUrl = $"{cleanBase}/player_api.php?username={Uri.EscapeDataString(username.Trim())}&password={Uri.EscapeDataString(password.Trim())}&action=get_live_categories";

        try
        {
            var (resp, _) = await SendApiGetAsync(new Uri(liveCatUrl), ct).ConfigureAwait(false);
            using (resp)
            {
                if (resp.IsSuccessStatusCode)
                {
                    string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (IsValidCategoriesResponse(json, out _))
                    {
                        return CreateActionAuthResult(cleanBase, username, isHttpWarning);
                    }
                }
                else if (resp.StatusCode == HttpStatusCode.NotFound || resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                {
                    return new TvXtreamAuthResult
                    {
                        Success = false,
                        ErrorMessage = "Autenticación rechazada: usuario o contraseña incorrectos."
                    };
                }
            }
        }
        catch (Exception ex)
        {
            if (ex is TimeoutException or TaskCanceledException)
            {
                return new TvXtreamAuthResult
                {
                    Success = false,
                    ErrorMessage = "Tiempo de espera agotado al conectar con el servidor Xtream."
                };
            }
        }

        // 2. Si get_live_categories no devolvió categorías válidas, intentar con get_vod_categories
        string vodCatUrl = $"{cleanBase}/player_api.php?username={Uri.EscapeDataString(username.Trim())}&password={Uri.EscapeDataString(password.Trim())}&action=get_vod_categories";

        try
        {
            var (resp, _) = await SendApiGetAsync(new Uri(vodCatUrl), ct).ConfigureAwait(false);
            using (resp)
            {
                if (resp.IsSuccessStatusCode)
                {
                    string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (IsValidCategoriesResponse(json, out _))
                    {
                        return CreateActionAuthResult(cleanBase, username, isHttpWarning);
                    }

                    return new TvXtreamAuthResult
                    {
                        Success = false,
                        ErrorMessage = "El servidor devolvió una respuesta que no es JSON válido (respuesta malformada)."
                    };
                }
                else if (resp.StatusCode == HttpStatusCode.NotFound || resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                {
                    return new TvXtreamAuthResult
                    {
                        Success = false,
                        ErrorMessage = "Autenticación rechazada: usuario o contraseña incorrectos."
                    };
                }
            }
        }
        catch (Exception ex)
        {
            if (ex is TimeoutException or TaskCanceledException)
            {
                return new TvXtreamAuthResult
                {
                    Success = false,
                    ErrorMessage = "Tiempo de espera agotado al conectar con el servidor Xtream."
                };
            }
        }

        return new TvXtreamAuthResult
        {
            Success = false,
            ErrorMessage = "El servidor no respondió con categorías válidas ni soporta las acciones estándar de Xtream Codes."
        };
    }

    private static TvXtreamAuthResult CreateActionAuthResult(string cleanBase, string username, bool isHttpWarning)
    {
        return new TvXtreamAuthResult
        {
            Success = true,
            UserInfo = new TvXtreamUserInfo
            {
                Username = username.Trim(),
                Status = "Active",
                Auth = 1,
                ExpDate = null
            },
            ServerInfo = new TvXtreamServerInfo
            {
                Url = cleanBase
            },
            IsHttpWarning = isHttpWarning
        };
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
        string cleanBase = NormalizeServerBaseUrl(serverUrl) ?? serverUrl.Trim().TrimEnd('/');
        var categories = await FetchCategoriesAsync(cleanBase, username, password, "get_live_categories", ct).ConfigureAwait(false);
        string url = $"{cleanBase}/player_api.php?username={Uri.EscapeDataString(username)}&password={Uri.EscapeDataString(password)}&action=get_live_streams";

        string json;
        try
        {
            var (resp, _) = await SendApiGetAsync(new Uri(url), ct).ConfigureAwait(false);
            using (resp)
            {
                if (!resp.IsSuccessStatusCode) return new List<TvChannel>();
                json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
        }
        catch
        {
            return new List<TvChannel>();
        }

        return ParseLiveStreams(json, categories, connId, connName, cleanBase);
    }

    public static string? NormalizePosterUrl(string? rawIcon, string? serverBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(rawIcon)) return null;
        string icon = rawIcon.Trim().Replace("\\/", "/");
        if (string.IsNullOrWhiteSpace(icon) ||
            icon.Equals("null", StringComparison.OrdinalIgnoreCase) ||
            icon.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            icon.Equals("n/a", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (icon.StartsWith("//", StringComparison.Ordinal))
            return "https:" + icon;

        if (icon.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            icon.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return icon;
        }

        if (icon.StartsWith("/t/p/", StringComparison.OrdinalIgnoreCase) ||
            icon.StartsWith("t/p/", StringComparison.OrdinalIgnoreCase))
        {
            return "https://image.tmdb.org/" + icon.TrimStart('/');
        }

        if (TmdbSizePathRegex.IsMatch(icon))
        {
            return "https://image.tmdb.org/t/p/" + icon.TrimStart('/');
        }

        if (TmdbBareHashRegex.IsMatch(icon))
        {
            return "https://image.tmdb.org/t/p/w500/" + icon.TrimStart('/');
        }

        string? normalizedBase = NormalizeServerBaseUrl(serverBaseUrl);
        if (!string.IsNullOrEmpty(normalizedBase))
        {
            string cleanIcon = icon.TrimStart('/');
            return $"{normalizedBase}/{cleanIcon}";
        }

        return icon;
    }

    private static string? GetFlexibleString(JsonElement el, params string[] keys)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in keys)
        {
            if (!el.TryGetProperty(key, out var prop)) continue;
            string? val = prop.ValueKind switch
            {
                JsonValueKind.String => prop.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => prop.ToString(),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(val))
            {
                string trimmed = val.Trim();
                if (!trimmed.Equals("null", StringComparison.OrdinalIgnoreCase))
                    return trimmed;
            }
        }
        return null;
    }

    private static string? GetFirstStringOrArrayFirst(JsonElement el, params string[] keys)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in keys)
        {
            if (!el.TryGetProperty(key, out var prop)) continue;
            if (prop.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in prop.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.String)
                    {
                        string? s = entry.GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(s) && !s.Equals("null", StringComparison.OrdinalIgnoreCase))
                            return s;
                    }
                }
                continue;
            }

            if (prop.ValueKind == JsonValueKind.String)
            {
                string? s = prop.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(s) && !s.Equals("null", StringComparison.OrdinalIgnoreCase))
                    return s;
            }
        }
        return null;
    }

    private static int GetFlexibleInt(JsonElement el, int defaultValue, params string[] keys)
    {
        if (el.ValueKind != JsonValueKind.Object) return defaultValue;
        foreach (var key in keys)
        {
            if (!el.TryGetProperty(key, out var prop)) continue;
            if (prop.ValueKind == JsonValueKind.Number)
            {
                if (prop.TryGetInt32(out int num)) return num;
                if (prop.TryGetDouble(out double d)) return (int)d;
            }
            else if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString()?.Trim(), out int parsed))
            {
                return parsed;
            }
        }
        return defaultValue;
    }

    private static string? ExtractPosterFromElement(JsonElement item, bool preferSeriesCover = false)
    {
        string? direct = preferSeriesCover
            ? GetFirstStringOrArrayFirst(item, "cover", "cover_big", "poster", "poster_path", "stream_icon", "movie_image", "backdrop", "backdrop_path", "logo", "icon", "thumbnail", "thumb", "img", "c")
            : GetFirstStringOrArrayFirst(item, "stream_icon", "movie_image", "cover", "cover_big", "poster", "poster_path", "logo", "icon", "thumbnail", "thumb", "img", "c", "backdrop", "backdrop_path");

        if (!string.IsNullOrWhiteSpace(direct))
            return direct;

        if (item.TryGetProperty("info", out var infoEl) && infoEl.ValueKind == JsonValueKind.Object)
        {
            return GetFirstStringOrArrayFirst(infoEl, "movie_image", "cover_big", "cover", "poster", "poster_path", "stream_icon", "backdrop", "backdrop_path", "thumbnail", "thumb", "tmdb_poster");
        }

        return null;
    }

    private static string? ExtractDirectStreamUrl(JsonElement item, string? serverUrl)
    {
        string? raw = GetFirstStringOrArrayFirst(item, "direct_source", "stream_url", "url", "d", "link", "source");
        if (string.IsNullOrWhiteSpace(raw)) return null;

        string candidate = raw.Trim().Replace("\\/", "/");
        if (candidate.StartsWith("//", StringComparison.Ordinal))
            candidate = "https:" + candidate;
        else if (!candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                 !candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            string? cleanBase = NormalizeServerBaseUrl(serverUrl);
            if (string.IsNullOrEmpty(cleanBase)) return null;
            candidate = $"{cleanBase}/{candidate.TrimStart('/')}";
        }

        return TvUrlGuard.IsAllowedRemoteUrl(candidate, out _) ? candidate : null;
    }

    private static string ExtractCategoryId(JsonElement item)
    {
        string? direct = GetFlexibleString(item, "category_id", "category");
        if (!string.IsNullOrWhiteSpace(direct)) return direct;
        return GetFirstStringOrArrayFirst(item, "categories", "e", "category_ids") ?? "";
    }

    public static List<TvChannel> ParseLiveStreams(string json, Dictionary<string, string> categories, string connId, string connName, string? serverUrl = null)
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
                string streamId = GetFlexibleString(item, "stream_id", "id", "a") ?? "";
                string name = GetFlexibleString(item, "name", "title", "b") ?? "";
                if (string.IsNullOrWhiteSpace(streamId) || string.IsNullOrWhiteSpace(name)) continue;

                string catId = ExtractCategoryId(item);
                string group = !string.IsNullOrEmpty(catId) && categories.TryGetValue(catId, out var cName) ? cName : "En vivo";

                int parsedNum = GetFlexibleInt(item, 0, "num");
                int num = parsedNum > 0 ? parsedNum : autoNum++;
                if (parsedNum > 0) autoNum = Math.Max(autoNum, parsedNum + 1);

                string? logo = ExtractPosterFromElement(item, preferSeriesCover: false);
                string normalizedLogo = NormalizePosterUrl(logo, serverUrl) ?? "";
                string? directUrl = ExtractDirectStreamUrl(item, serverUrl);

                // URL segura protegida en el host virtual tv.lex.local sin exponer credenciales
                string secureStreamUrl = $"https://tv.lex.local/api/xtream/live?conn={Uri.EscapeDataString(connId)}&id={Uri.EscapeDataString(streamId)}";
                if (!string.IsNullOrWhiteSpace(directUrl))
                {
                    secureStreamUrl += $"&direct={Uri.EscapeDataString(directUrl)}";
                }

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
                    Logo = normalizedLogo,
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
        string cleanBase = NormalizeServerBaseUrl(serverUrl) ?? serverUrl.Trim().TrimEnd('/');
        var categories = await FetchCategoriesAsync(cleanBase, username, password, "get_vod_categories", ct).ConfigureAwait(false);
        string url = $"{cleanBase}/player_api.php?username={Uri.EscapeDataString(username)}&password={Uri.EscapeDataString(password)}&action=get_vod_streams";

        string json;
        try
        {
            var (resp, _) = await SendApiGetAsync(new Uri(url), ct).ConfigureAwait(false);
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

        return ParseVodStreams(json, categories, connId, cleanBase);
    }

    public static List<TvVodItem> ParseVodStreams(string json, Dictionary<string, string> categories, string connId, string? serverUrl = null)
    {
        var list = new List<TvVodItem>();
        if (string.IsNullOrWhiteSpace(json)) return list;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                string streamId = GetFlexibleString(item, "stream_id", "id", "a") ?? "";
                string name = GetFlexibleString(item, "name", "title", "b") ?? "";
                if (string.IsNullOrWhiteSpace(streamId) || string.IsNullOrWhiteSpace(name)) continue;

                string catId = ExtractCategoryId(item);
                string group = !string.IsNullOrEmpty(catId) && categories.TryGetValue(catId, out var cName) ? cName : "Películas";

                string ext = GetFlexibleString(item, "container_extension", "ext") ?? "mp4";
                string? logo = ExtractPosterFromElement(item, preferSeriesCover: false);
                string? normalizedLogo = NormalizePosterUrl(logo, serverUrl);
                string? directUrl = ExtractDirectStreamUrl(item, serverUrl);

                string secureUrl = $"https://tv.lex.local/api/xtream/vod?conn={Uri.EscapeDataString(connId)}&id={Uri.EscapeDataString(streamId)}&ext={Uri.EscapeDataString(ext)}";
                if (!string.IsNullOrWhiteSpace(directUrl))
                {
                    secureUrl += $"&direct={Uri.EscapeDataString(directUrl)}";
                }

                list.Add(new TvVodItem
                {
                    Id = $"xc_vod_{connId}_{streamId}",
                    Title = name,
                    Url = secureUrl,
                    Group = group,
                    Logo = normalizedLogo,
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
        string cleanBase = NormalizeServerBaseUrl(serverUrl) ?? serverUrl.Trim().TrimEnd('/');
        var categories = await FetchCategoriesAsync(cleanBase, username, password, "get_series_categories", ct).ConfigureAwait(false);
        string url = $"{cleanBase}/player_api.php?username={Uri.EscapeDataString(username)}&password={Uri.EscapeDataString(password)}&action=get_series";

        string json;
        try
        {
            var (resp, _) = await SendApiGetAsync(new Uri(url), ct).ConfigureAwait(false);
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

        var seriesList = ParseSeriesList(json, categories, cleanBase);
        var targets = seriesList.Take(200).ToList();
        if (targets.Count == 0) return new List<TvVodItem>();

        using var throttle = new SemaphoreSlim(6, 6);
        var tasks = targets.Select(async s =>
        {
            await throttle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (ct.IsCancellationRequested) return new List<TvVodItem>();
                string infoUrl = $"{cleanBase}/player_api.php?username={Uri.EscapeDataString(username)}&password={Uri.EscapeDataString(password)}&action=get_series_info&series_id={Uri.EscapeDataString(s.SeriesId)}";
                var (infoResp, _) = await SendApiGetAsync(new Uri(infoUrl), ct).ConfigureAwait(false);
                using (infoResp)
                {
                    if (infoResp.IsSuccessStatusCode)
                    {
                        string infoJson = await infoResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        return ParseSeriesInfoEpisodes(infoJson, s, connId, cleanBase);
                    }
                }
            }
            catch { }
            finally
            {
                throttle.Release();
            }
            return new List<TvVodItem>();
        }).ToArray();

        var perSeries = await Task.WhenAll(tasks).ConfigureAwait(false);
        var result = new List<TvVodItem>();
        foreach (var episodes in perSeries)
            result.AddRange(episodes);

        return result;
    }

    public static List<TvXtreamSeriesItem> ParseSeriesList(string json, Dictionary<string, string> categories, string? serverUrl = null)
    {
        var list = new List<TvXtreamSeriesItem>();
        if (string.IsNullOrWhiteSpace(json)) return list;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                string seriesId = GetFlexibleString(item, "series_id", "id", "a") ?? "";
                string name = GetFlexibleString(item, "name", "title", "b") ?? "";
                if (string.IsNullOrWhiteSpace(seriesId) || string.IsNullOrWhiteSpace(name)) continue;

                string catId = ExtractCategoryId(item);
                string? cover = ExtractPosterFromElement(item, preferSeriesCover: true);
                string? normalizedCover = NormalizePosterUrl(cover, serverUrl);

                list.Add(new TvXtreamSeriesItem
                {
                    SeriesId = seriesId,
                    Name = name,
                    Cover = normalizedCover,
                    CategoryId = catId
                });
            }
        }
        catch { }

        return list;
    }

    private static TvVodItem? ParseEpisodeObject(JsonElement ep, TvXtreamSeriesItem series, string connId, int defaultSeason, string? serverUrl)
    {
        if (ep.ValueKind != JsonValueKind.Object) return null;

        string epId = GetFlexibleString(ep, "id", "stream_id", "a") ?? "";
        string epTitle = GetFlexibleString(ep, "title", "name", "b") ?? "";
        if (string.IsNullOrWhiteSpace(epId)) return null;

        int seasonNum = GetFlexibleInt(ep, defaultSeason, "season", "season_num");
        if (seasonNum <= 0) seasonNum = defaultSeason;

        int epNum = GetFlexibleInt(ep, 1, "episode_num", "episode");
        if (epNum <= 0) epNum = 1;

        string ext = GetFlexibleString(ep, "container_extension", "ext") ?? "mp4";
        string? epCover = ExtractPosterFromElement(ep, preferSeriesCover: false) ?? series.Cover;
        string? normalizedCover = NormalizePosterUrl(epCover, serverUrl);
        string? directUrl = ExtractDirectStreamUrl(ep, serverUrl);

        string secureUrl = $"https://tv.lex.local/api/xtream/series?conn={Uri.EscapeDataString(connId)}&id={Uri.EscapeDataString(epId)}&ext={Uri.EscapeDataString(ext)}";
        if (!string.IsNullOrWhiteSpace(directUrl))
        {
            secureUrl += $"&direct={Uri.EscapeDataString(directUrl)}";
        }
        string displayTitle = !string.IsNullOrWhiteSpace(epTitle)
            ? $"{series.Name} S{seasonNum:D2}E{epNum:D2} - {epTitle}"
            : $"{series.Name} S{seasonNum:D2}E{epNum:D2}";

        return new TvVodItem
        {
            Id = $"xc_series_{connId}_{series.SeriesId}_{epId}",
            Title = displayTitle,
            Url = secureUrl,
            Group = series.Name,
            Logo = normalizedCover,
            IsSeries = true,
            SeriesTitle = series.Name,
            SeasonNumber = seasonNum,
            EpisodeNumber = epNum,
            EpisodeTitle = epTitle,
            AddedUtc = DateTime.UtcNow
        };
    }

    public static List<TvVodItem> ParseSeriesInfoEpisodes(string json, TvXtreamSeriesItem series, string connId, string? serverUrl = null)
    {
        var list = new List<TvVodItem>();
        if (string.IsNullOrWhiteSpace(json)) return list;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("episodes", out var epRoot))
                return list;

            if (epRoot.ValueKind == JsonValueKind.Object)
            {
                foreach (var seasonProp in epRoot.EnumerateObject())
                {
                    int seasonNum = int.TryParse(seasonProp.Name, out int sn) ? sn : 1;
                    if (seasonProp.Value.ValueKind != JsonValueKind.Array) continue;

                    foreach (var ep in seasonProp.Value.EnumerateArray())
                    {
                        var item = ParseEpisodeObject(ep, series, connId, seasonNum, serverUrl);
                        if (item != null) list.Add(item);
                    }
                }
            }
            else if (epRoot.ValueKind == JsonValueKind.Array)
            {
                int seasonIdx = 1;
                foreach (var entry in epRoot.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var ep in entry.EnumerateArray())
                        {
                            var item = ParseEpisodeObject(ep, series, connId, seasonIdx, serverUrl);
                            if (item != null) list.Add(item);
                        }
                        seasonIdx++;
                    }
                    else if (entry.ValueKind == JsonValueKind.Object)
                    {
                        var item = ParseEpisodeObject(entry, series, connId, 1, serverUrl);
                        if (item != null) list.Add(item);
                    }
                }
            }
        }
        catch { }

        return list;
    }

    private async Task<Dictionary<string, string>> FetchCategoriesAsync(string serverUrl, string username, string password, string action, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string cleanBase = NormalizeServerBaseUrl(serverUrl) ?? serverUrl.Trim().TrimEnd('/');
        string url = $"{cleanBase}/player_api.php?username={Uri.EscapeDataString(username)}&password={Uri.EscapeDataString(password)}&action={action}";

        try
        {
            var (resp, _) = await SendApiGetAsync(new Uri(url), ct).ConfigureAwait(false);
            using (resp)
            {
                if (!resp.IsSuccessStatusCode) return map;
                string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return map;

                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    string id = GetFlexibleString(el, "category_id", "id", "a") ?? "";
                    string name = GetFlexibleString(el, "category_name", "name", "title", "b") ?? "";
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

        string? serverBase = NormalizeServerBaseUrl(conn.ServerUrl);
        if (string.IsNullOrEmpty(serverBase))
            return (false, null, "La URL del servidor no cumple los requisitos de seguridad de red de Lex TV.");
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

    private static bool TryResolveConnectionAndCreds(
        TvXtreamStore store,
        string connId,
        out TvXtreamConnection? conn,
        out TvXtreamCredentials? creds,
        out string? error)
    {
        conn = store.GetConnection(connId);
        creds = conn != null ? store.GetCredentials(connId) : null;

        // Respaldo: si connId quedó huérfano por una reimportación previa, usar la primera conexión activa con credenciales.
        if (conn == null || creds == null ||
            string.IsNullOrWhiteSpace(creds.Username) ||
            string.IsNullOrWhiteSpace(creds.Password))
        {
            foreach (var candidate in store.GetSummaries())
            {
                var cCreds = store.GetCredentials(candidate.Id);
                if (cCreds != null &&
                    !string.IsNullOrWhiteSpace(cCreds.Username) &&
                    !string.IsNullOrWhiteSpace(cCreds.Password))
                {
                    conn = store.GetConnection(candidate.Id);
                    creds = cCreds;
                    break;
                }
            }
        }

        if (conn == null)
        {
            error = "Conexión Xtream no encontrada.";
            return false;
        }

        if (creds == null || string.IsNullOrWhiteSpace(creds.Username) || string.IsNullOrWhiteSpace(creds.Password))
        {
            error = "Credenciales no disponibles para esta conexión (sesión expirada o no guardadas).";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Construye la lista ordenada y deduplicada de URLs upstream candidatas para un stream Xtream interno,
    /// incluyendo URL directa (si vino en el catálogo), rutas modernas (/live, /movie, /series) y rutas heredadas.
    /// </summary>
    public static (bool Success, List<string> Candidates, string? Error) ResolveUpstreamStreamCandidates(
        TvXtreamStore store,
        string? rawInternalUrl,
        string? overrideExt = null)
    {
        var candidates = new List<string>();
        if (!TvUrlGuard.IsInternalTvStreamUrl(rawInternalUrl, out var internalUri) || internalUri == null)
            return (false, candidates, "Ruta interna de streaming no reconocida.");

        var q = HttpUtility.ParseQueryString(internalUri.Query);
        string connId = q["conn"] ?? "";
        string streamId = q["id"] ?? "";
        string requestedExt = !string.IsNullOrWhiteSpace(overrideExt) ? overrideExt : (q["ext"] ?? "");
        string directParam = q["direct"] ?? "";
        string streamType = internalUri.AbsolutePath.EndsWith("live", StringComparison.OrdinalIgnoreCase) ? "live" :
                            internalUri.AbsolutePath.EndsWith("vod", StringComparison.OrdinalIgnoreCase) ? "movie" : "series";

        if (string.IsNullOrWhiteSpace(streamId))
            return (false, candidates, "Identificadores de flujo faltantes.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        void AddIfValid(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            if (TvUrlGuard.IsAllowedRemoteUrl(url, out var uri) && uri != null && seen.Add(uri.AbsoluteUri))
            {
                candidates.Add(uri.AbsoluteUri);
            }
        }

        if (!string.IsNullOrWhiteSpace(directParam))
        {
            AddIfValid(directParam.Trim());
        }

        if (!TryResolveConnectionAndCreds(store, connId, out var conn, out var creds, out var credErr) || conn == null || creds == null)
        {
            if (candidates.Count > 0)
                return (true, candidates, null);
            return (false, candidates, credErr);
        }

        string? serverBase = NormalizeServerBaseUrl(conn.ServerUrl);
        if (string.IsNullOrEmpty(serverBase))
        {
            if (candidates.Count > 0)
                return (true, candidates, null);
            return (false, candidates, "La URL del servidor no cumple los requisitos de seguridad de red de Lex TV.");
        }

        string u = Uri.EscapeDataString(creds.Username);
        string p = Uri.EscapeDataString(creds.Password);
        string id = Uri.EscapeDataString(streamId);

        string cleanExt = requestedExt.Trim().TrimStart('.');
        if (!cleanExt.All(char.IsLetterOrDigit)) cleanExt = "";

        if (streamType == "live")
        {
            if (string.IsNullOrEmpty(cleanExt))
            {
                string? remembered = TvPlaybackHints.GetLiveExt(connId);
                cleanExt = !string.IsNullOrWhiteSpace(remembered) ? remembered.TrimStart('.') : "ts";
            }
            string altExt = string.Equals(cleanExt, "ts", StringComparison.OrdinalIgnoreCase) ? "m3u8" : "ts";

            AddIfValid($"{serverBase}/live/{u}/{p}/{id}.{cleanExt}");
            AddIfValid($"{serverBase}/live/{u}/{p}/{id}.{altExt}");
            // Rutas heredadas/compatibles de paneles Xtream y reproductores IPTV (incluyendo sin extensión ni prefijo /live)
            AddIfValid($"{serverBase}/{u}/{p}/{id}");
            AddIfValid($"{serverBase}/live/{u}/{p}/{id}");
            AddIfValid($"{serverBase}/{u}/{p}/{id}.{cleanExt}");
            AddIfValid($"{serverBase}/{u}/{p}/{id}.{altExt}");
        }
        else if (streamType == "movie")
        {
            if (string.IsNullOrEmpty(cleanExt)) cleanExt = "mp4";
            AddIfValid($"{serverBase}/movie/{u}/{p}/{id}.{cleanExt}");
            foreach (string alt in new[] { "mp4", "mkv", "m3u8", "ts", "avi" })
            {
                if (!string.Equals(alt, cleanExt, StringComparison.OrdinalIgnoreCase))
                    AddIfValid($"{serverBase}/movie/{u}/{p}/{id}.{alt}");
            }
            AddIfValid($"{serverBase}/{u}/{p}/{id}.{cleanExt}");
            AddIfValid($"{serverBase}/movie/{u}/{p}/{id}");
            AddIfValid($"{serverBase}/{u}/{p}/{id}");
        }
        else
        {
            if (string.IsNullOrEmpty(cleanExt)) cleanExt = "mp4";
            AddIfValid($"{serverBase}/series/{u}/{p}/{id}.{cleanExt}");
            foreach (string alt in new[] { "mp4", "mkv", "m3u8", "ts" })
            {
                if (!string.Equals(alt, cleanExt, StringComparison.OrdinalIgnoreCase))
                    AddIfValid($"{serverBase}/series/{u}/{p}/{id}.{alt}");
            }
            AddIfValid($"{serverBase}/{u}/{p}/{id}.{cleanExt}");
            AddIfValid($"{serverBase}/series/{u}/{p}/{id}");
        }

        if (candidates.Count == 0)
            return (false, candidates, "La URL del servidor no cumple los requisitos de seguridad de red de Lex TV.");

        return (true, candidates, null);
    }

    public static string? ParseVodInfoStreamUrl(string json, string? serverBaseUrl, string username, string password, string vodId)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string? direct = null;
            string? ext = null;

            if (root.TryGetProperty("movie_data", out var movieData) && movieData.ValueKind == JsonValueKind.Object)
            {
                direct = ExtractDirectStreamUrl(movieData, serverBaseUrl);
                ext = GetFlexibleString(movieData, "container_extension", "ext");
            }

            if (string.IsNullOrWhiteSpace(direct) && root.TryGetProperty("info", out var infoEl) && infoEl.ValueKind == JsonValueKind.Object)
            {
                direct = ExtractDirectStreamUrl(infoEl, serverBaseUrl);
                ext ??= GetFlexibleString(infoEl, "container_extension", "ext");
            }

            direct ??= ExtractDirectStreamUrl(root, serverBaseUrl);
            if (!string.IsNullOrWhiteSpace(direct))
                return direct;

            string? cleanBase = NormalizeServerBaseUrl(serverBaseUrl);
            if (!string.IsNullOrEmpty(cleanBase) && !string.IsNullOrWhiteSpace(ext))
            {
                string cleanExt = ext.Trim().TrimStart('.');
                if (!string.IsNullOrEmpty(cleanExt) && cleanExt.All(char.IsLetterOrDigit))
                {
                    string candidate = $"{cleanBase}/movie/{Uri.EscapeDataString(username)}/{Uri.EscapeDataString(password)}/{Uri.EscapeDataString(vodId)}.{cleanExt}";
                    if (TvUrlGuard.IsAllowedRemoteUrl(candidate, out _))
                        return candidate;
                }
            }
        }
        catch { }

        return null;
    }

    public async Task<string?> TryResolveVodInfoStreamUrlAsync(TvXtreamStore store, string connId, string vodId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(vodId)) return null;
        if (!TryResolveConnectionAndCreds(store, connId, out var conn, out var creds, out _) || conn == null || creds == null)
            return null;

        string? cleanBase = NormalizeServerBaseUrl(conn.ServerUrl);
        if (string.IsNullOrEmpty(cleanBase)) return null;

        string infoUrl = $"{cleanBase}/player_api.php?username={Uri.EscapeDataString(creds.Username)}&password={Uri.EscapeDataString(creds.Password)}&action=get_vod_info&vod_id={Uri.EscapeDataString(vodId)}";
        if (!TvUrlGuard.IsAllowedRemoteUrl(infoUrl, out var uri) || uri == null)
            return null;

        try
        {
            var (resp, _) = await SendApiGetAsync(uri, ct).ConfigureAwait(false);
            using (resp)
            {
                if (!resp.IsSuccessStatusCode) return null;
                string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return ParseVodInfoStreamUrl(json, cleanBase, creds.Username, creds.Password, vodId);
            }
        }
        catch
        {
            return null;
        }
    }
}
