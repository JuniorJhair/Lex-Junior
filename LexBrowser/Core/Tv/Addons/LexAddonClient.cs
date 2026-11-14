using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LexBrowser.Core.Tv.Addons;

/// <summary>
/// Cliente HTTP aislado y endurecido contra SSRF para consultar manifiestos y endpoints JSON de addons.
/// Reutiliza DNS pinning de TvUrlGuard para URLs remotas e impone aislamiento estricto para loopback opt-in.
/// </summary>
public sealed class LexAddonClient : IDisposable
{
    public const int MaxManifestBytes = 256 * 1024;
    public const int MaxJsonBytes = 4 * 1024 * 1024;
    public const string AddonUserAgent = "LexAddons/1.0 (LexBrowser)";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _remoteClient;
    private readonly HttpClient _loopbackClient;
    private readonly bool _ownsClients;

    public static LexAddonClient Shared { get; } = new();

    public LexAddonClient(
        Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null,
        HttpClient? injectedRemoteClient = null,
        HttpClient? injectedLoopbackClient = null)
    {
        if (injectedRemoteClient != null)
        {
            _remoteClient = injectedRemoteClient;
            _loopbackClient = injectedLoopbackClient ?? injectedRemoteClient;
            _ownsClients = false;
        }
        else
        {
            _remoteClient = TvUrlGuard.CreatePinnedHttpClient(resolver, DefaultTimeout);
            _loopbackClient = CreateLoopbackOnlyHttpClient(DefaultTimeout);
            _ownsClients = true;
        }
    }

    public static bool IsLoopbackOnlyUri(string? rawUrl, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(rawUrl) || rawUrl.Length > 2048) return false;
        if (!Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.IsNullOrEmpty(u.UserInfo)) return false;

        string host = u.Host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            uri = u;
            return true;
        }

        if (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip))
        {
            uri = u;
            return true;
        }

        return false;
    }

    public static bool ValidateAddonEndpoint(string? rawUrl, bool allowLocalLoopback, out Uri? safeUri, out bool isLoopback, out string? error)
    {
        safeUri = null;
        isLoopback = false;
        error = null;

        if (IsLoopbackOnlyUri(rawUrl, out var localUri) && localUri != null)
        {
            if (!allowLocalLoopback)
            {
                error = "El acceso a localhost / 127.0.0.1 está bloqueado por seguridad. Activa 'Permitir addon local' solo si ejecutas un servidor tuyo en este equipo.";
                return false;
            }

            safeUri = localUri;
            isLoopback = true;
            return true;
        }

        if (!TvUrlGuard.IsAllowedRemoteUrl(rawUrl, out var remoteUri) || remoteUri == null)
        {
            error = "URL de addon bloqueada por seguridad (solo URLs públicas http:// o https://).";
            return false;
        }

        safeUri = remoteUri;
        return true;
    }

    public async Task<LexAddonManifestResult> FetchManifestAsync(
        string? rawUrl,
        bool allowLocalLoopback,
        CancellationToken ct)
    {
        if (!LexAddonManifestParser.TryNormalizeManifestInput(rawUrl, out string normalizedUrl, out string? normErr))
        {
            return new LexAddonManifestResult(false, ErrorMessage: normErr);
        }

        var raw = await FetchBoundedJsonAsync(normalizedUrl, allowLocalLoopback, MaxManifestBytes, ct).ConfigureAwait(false);
        if (!raw.Success || raw.Json == null || string.IsNullOrEmpty(raw.CanonicalUrl))
        {
            return new LexAddonManifestResult(false, DisplayUrl: raw.DisplayUrl, ErrorMessage: raw.ErrorMessage);
        }

        string baseUrl = LexAddonManifestParser.ExtractBaseUrl(new Uri(raw.CanonicalUrl));
        var (ok, manifest, parseErr) = LexAddonManifestParser.Parse(raw.Json, baseUrl);
        if (!ok || manifest == null)
        {
            TvDiagLog.Write("addon-parse", parseErr ?? "Manifiesto inválido", raw.DisplayUrl);
            return new LexAddonManifestResult(false, DisplayUrl: raw.DisplayUrl, ErrorMessage: parseErr ?? "Manifiesto inválido.");
        }

        return new LexAddonManifestResult(
            true,
            Manifest: manifest,
            CanonicalUrl: raw.CanonicalUrl,
            DisplayUrl: raw.DisplayUrl);
    }

    /// <summary>
    /// Consulta un endpoint JSON acotado del addon (catalog, meta, stream) con la misma protección SSRF y DNS pinning.
    /// </summary>
    public Task<(bool Success, string? Json, string? CanonicalUrl, string? DisplayUrl, int StatusCode, string? ErrorMessage)> FetchJsonEndpointAsync(
        string? endpointUrl,
        bool allowLocalLoopback,
        int maxBytes,
        CancellationToken ct)
        => FetchBoundedJsonAsync(endpointUrl, allowLocalLoopback, Math.Clamp(maxBytes, 1024, MaxJsonBytes), ct);

    private async Task<(bool Success, string? Json, string? CanonicalUrl, string? DisplayUrl, int StatusCode, string? ErrorMessage)> FetchBoundedJsonAsync(
        string? rawUrl,
        bool allowLocalLoopback,
        int maxBytes,
        CancellationToken ct)
    {
        string safeDisplay = LexAddonManifestParser.RedactManifestUrlForDisplay(rawUrl);
        if (!ValidateAddonEndpoint(rawUrl, allowLocalLoopback, out var targetUri, out bool isLoopback, out string? secErr) || targetUri == null)
        {
            TvDiagLog.Write("addon-blocked", secErr ?? "URL bloqueada", safeDisplay);
            return (false, null, null, safeDisplay, 0, secErr);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(DefaultTimeout);

        try
        {
            HttpResponseMessage resp;
            Uri finalUri;

            if (isLoopback)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, targetUri);
                req.Headers.TryAddWithoutValidation("User-Agent", AddonUserAgent);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                resp = await _loopbackClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
                finalUri = targetUri;

                if ((int)resp.StatusCode is >= 300 and < 400)
                {
                    int redirectCode = (int)resp.StatusCode;
                    resp.Dispose();
                    TvDiagLog.Write("addon-blocked", "Redirección bloqueada en addon local", safeDisplay);
                    return (false, null, null, safeDisplay, redirectCode, "No se permiten redirecciones en addons locales.");
                }
            }
            else
            {
                (resp, finalUri) = await TvUrlGuard.GetWithSafeRedirectsAsync(
                    _remoteClient,
                    targetUri,
                    rangeHeader: null,
                    timeoutCts.Token,
                    maxRedirects: 5,
                    userAgent: AddonUserAgent).ConfigureAwait(false);
            }

            using (resp)
            {
                int statusCode = (int)resp.StatusCode;
                if (!resp.IsSuccessStatusCode)
                {
                    string msg = $"El servidor del addon respondió HTTP {statusCode}.";
                    TvDiagLog.Write("addon-fetch", $"HTTP {statusCode}", safeDisplay);
                    return (false, null, null, safeDisplay, statusCode, msg);
                }

                string json = await TvUrlGuard.ReadBoundedUtf8Async(resp, maxBytes, timeoutCts.Token).ConfigureAwait(false);
                return (
                    true,
                    json,
                    finalUri.AbsoluteUri,
                    LexAddonManifestParser.RedactManifestUrlForDisplay(finalUri.AbsoluteUri),
                    statusCode,
                    null);
            }
        }
        catch (TvProxyErrorException pex)
        {
            TvDiagLog.Write("addon-error", $"{pex.ErrorClass}: {pex.Message}", safeDisplay);
            return (false, null, null, safeDisplay, pex.SuggestedStatus, pex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (false, null, null, safeDisplay, 0, "Operación cancelada.");
        }
        catch (Exception ex)
        {
            var (_, _, msg) = TvUrlGuard.ClassifyException(ex);
            TvDiagLog.Write("addon-error", $"{ex.GetType().Name}: {msg}", safeDisplay);
            return (false, null, null, safeDisplay, 0, msg);
        }
    }

    private static HttpClient CreateLoopbackOnlyHttpClient(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectCallback = async (context, ct) =>
            {
                string host = context.DnsEndPoint.Host.Trim('[', ']');
                IPAddress[] addrs = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                    ? new[] { IPAddress.Loopback, IPAddress.IPv6Loopback }
                    : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);

                foreach (var ip in addrs)
                {
                    if (!IPAddress.IsLoopback(ip)) continue;
                    var socket = new System.Net.Sockets.Socket(ip.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp)
                    {
                        NoDelay = true
                    };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(ip, context.DnsEndPoint.Port), ct).ConfigureAwait(false);
                        return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                    }
                }
                throw new TvProxyErrorException("blocked", 502, "El addon local solo puede conectar a direcciones loopback (127.0.0.1).");
            }
        };

        return new HttpClient(handler) { Timeout = timeout };
    }

    public void Dispose()
    {
        if (_ownsClients)
        {
            _remoteClient.Dispose();
            _loopbackClient.Dispose();
        }
    }
}

