using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LexBrowser.Core.Tv;

public sealed class TvProxyErrorException : Exception
{
    public string ErrorClass { get; }
    public int SuggestedStatus { get; }

    public TvProxyErrorException(string errorClass, int suggestedStatus, string message, Exception? inner = null)
        : base(message, inner)
    {
        ErrorClass = errorClass;
        SuggestedStatus = suggestedStatus;
    }
}

/// <summary>
/// Validaciones de seguridad para URLs, DNS SSRF (ConnectCallback), redirecciones y límites de descarga.
/// Protege la red interna bloqueando todos los rangos privados y locales IPv4 e IPv6.
/// </summary>
public static class TvUrlGuard
{
    public const int MaxPlaylistBytes = 20 * 1024 * 1024;
    public const string DefaultUserAgent = "LexBrowser/2.1 (Windows NT 10.0; Win64; x64) LexTv/1.0";

    /// <summary>User-Agent neutro por defecto para streams e imágenes (reproductor).</summary>
    public const string DefaultPlayerUserAgent = "LexTV/1.0 (Player)";
    /// <summary>User-Agent alternativo usado cuando el primario recibe rechazo o 403/404.</summary>
    public const string FallbackPlayerUserAgent = "VLC/3.0.20 LibVLC/3.0.20";
    /// <summary>User-Agent compatible con paneles Xtream/Magma.</summary>
    public const string MagmaPlayerUserAgent = "Magma Player/10";
    /// <summary>User-Agent estándar de navegador para portadas o CDN con bloqueo de reproductores.</summary>
    public const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
    public const string PlayerUserAgentEnvVar = "LEXTV_PLAYER_UA";
    public const string PlayerUserAgentFileName = "tv_player_ua.txt";

    /// <summary>
    /// Devuelve la lista ordenada y sin duplicados de User-Agents candidatos para streams, APIs e imágenes.
    /// </summary>
    public static IReadOnlyList<string> GetUserAgentCandidates(string? preferredUa = null)
    {
        var list = new List<string>(5);
        void AddUnique(string? ua)
        {
            string clean = SanitizeHeaderValue(ua, 140);
            if (clean.Length > 0 && !list.Contains(clean, StringComparer.Ordinal))
                list.Add(clean);
        }

        AddUnique(preferredUa);
        AddUnique(PlayerUserAgent);
        AddUnique(FallbackPlayerUserAgent);
        AddUnique(MagmaPlayerUserAgent);
        AddUnique(BrowserUserAgent);
        return list;
    }

    private static string? _playerUserAgent;

    /// <summary>
    /// ÚNICO campo de configuración del User-Agent de reproductor (streams e imágenes).
    /// Se resuelve (sin recompilar) desde la variable de entorno LEXTV_PLAYER_UA o, si no existe,
    /// desde el archivo tv_player_ua.txt (primera línea) en la carpeta de datos de Lex; si no, el valor neutro.
    /// </summary>
    public static string PlayerUserAgent
    {
        get => _playerUserAgent ??= LoadPlayerUserAgent();
        set
        {
            string clean = SanitizeHeaderValue(value, 120);
            _playerUserAgent = clean.Length > 0 ? clean : DefaultPlayerUserAgent;
        }
    }

    private static string LoadPlayerUserAgent()
    {
        try
        {
            string env = SanitizeHeaderValue(Environment.GetEnvironmentVariable(PlayerUserAgentEnvVar), 120);
            if (env.Length > 0) return env;

            string file = Path.Combine(TvUserStateStore.GetDefaultDirectory(), PlayerUserAgentFileName);
            if (File.Exists(file))
            {
                using var reader = new StreamReader(file);
                string fromFile = SanitizeHeaderValue(reader.ReadLine(), 120);
                if (fromFile.Length > 0) return fromFile;
            }
        }
        catch { }
        return DefaultPlayerUserAgent;
    }

    /// <summary>Host interno exacto reservado para el WebView2 local de Lex TV.</summary>
    public const string InternalHost = "tv.lex.local";
    /// <summary>Origen HTTPS interno exacto de Lex TV.</summary>
    public const string InternalOrigin = "https://tv.lex.local";

    /// <summary>
    /// Comprueba si la URI corresponde exclusivamente al origen interno seguro de Lex TV (https://tv.lex.local).
    /// No admite otros dominios .local, puertos no estándar ni esquemas no seguros (solo HTTPS).
    /// </summary>
    public static bool IsInternalTvOrigin(Uri? uri)
    {
        if (uri == null || !uri.IsAbsoluteUri) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(uri.Host, InternalHost, StringComparison.OrdinalIgnoreCase)) return false;
        if (!uri.IsDefaultPort && uri.Port != 443) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        return true;
    }

    /// <summary>
    /// Comprueba si la URL corresponde a una ruta virtual interna autorizada de streaming de Lex TV
    /// (/api/xtream/live, /api/xtream/vod, /api/xtream/series).
    /// Acepta también rutas relativas (/api/xtream/...) normalizándolas a https://tv.lex.local.
    /// </summary>
    public static bool IsInternalTvStreamUrl(string? url, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048) return false;
        string trimmed = url.Trim();
        if (trimmed.StartsWith("/api/xtream/", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = InternalOrigin + trimmed;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var u)) return false;
        if (!IsInternalTvOrigin(u)) return false;

        string path = u.AbsolutePath.Trim().ToLowerInvariant();
        if (path.StartsWith("/api/xtream/live", StringComparison.Ordinal) ||
            path.StartsWith("/api/xtream/vod", StringComparison.Ordinal) ||
            path.StartsWith("/api/xtream/series", StringComparison.Ordinal))
        {
            uri = u;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Indica si la URL apunta al host virtual interno tv.lex.local (http o https) o es ruta relativa /api/.
    /// Útil para impedir que rutas internas fallen con "URL privada o no permitida".
    /// </summary>
    public static bool IsInternalTvHostUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        string trimmed = url.Trim();
        if (trimmed.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) return true;
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var u))
            return string.Equals(u.Host, InternalHost, StringComparison.OrdinalIgnoreCase);
        return false;
    }

    public static bool IsAllowedRemoteUrl(string? url, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.IsNullOrEmpty(u.UserInfo)) return false;
        if (IsPrivateOrLocalHost(u.Host)) return false;
        if (TvDenylist.IsDenylisted(u.AbsoluteUri, out _)) return false;
        uri = u;
        return true;
    }

    public static bool IsPrivateOrLocalHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;
        string h = host.Trim().Trim('[', ']').ToLowerInvariant();
        if (h == "localhost" || h.EndsWith(".localhost", StringComparison.Ordinal) ||
            h.EndsWith(".local", StringComparison.Ordinal) || h.EndsWith(".lan", StringComparison.Ordinal) ||
            h.EndsWith(".internal", StringComparison.Ordinal))
            return true;

        if (IPAddress.TryParse(h, out var ip)) return IsPrivateIp(ip);
        return !h.Contains('.');
    }

    public static bool IsPrivateIp(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return true;
            if (ip.Equals(IPAddress.IPv6Any)) return true;

            byte[] b6 = ip.GetAddressBytes();
            if ((b6[0] & 0xFE) == 0xFC) return true; // Unique Local fc00::/7

            // 64:ff9b::/96 (IPv4/IPv6 translation)
            if (b6[0] == 0x00 && b6[1] == 0x64 && b6[2] == 0xff && b6[3] == 0x9b &&
                b6[4] == 0 && b6[5] == 0 && b6[6] == 0 && b6[7] == 0 &&
                b6[8] == 0 && b6[9] == 0 && b6[10] == 0 && b6[11] == 0)
                return true;

            // 2002::/16 (6to4)
            if (b6[0] == 0x20 && b6[1] == 0x02) return true;

            // 2001::/32 (Teredo)
            if (b6[0] == 0x20 && b6[1] == 0x01 && b6[2] == 0x00 && b6[3] == 0x00) return true;

            // 2001:db8::/32 (Documentation)
            if (b6[0] == 0x20 && b6[1] == 0x01 && b6[2] == 0x0d && b6[3] == 0xb8) return true;

            return false;
        }

        byte[] b = ip.GetAddressBytes();
        if (b.Length != 4) return true;

        return b[0] == 0 ||                                              // 0.0.0.0/8
               b[0] == 10 ||                                             // 10.0.0.0/8
               b[0] == 127 ||                                            // 127.0.0.0/8
               (b[0] == 169 && b[1] == 254) ||                           // 169.254.0.0/16
               (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||              // 172.16.0.0/12
               (b[0] == 192 && b[1] == 168) ||                           // 192.168.0.0/16
               (b[0] == 100 && b[1] >= 64 && b[1] <= 127) ||             // 100.64.0.0/10
               (b[0] == 192 && b[1] == 0 && b[2] == 0) ||                // 192.0.0.0/24
               (b[0] == 192 && b[1] == 0 && b[2] == 2) ||                // 192.0.2.0/24
               (b[0] == 198 && (b[1] == 18 || b[1] == 19)) ||            // 198.18.0.0/15
               (b[0] == 198 && b[1] == 51 && b[2] == 100) ||             // 198.51.100.0/24
               (b[0] == 203 && b[1] == 0 && b[2] == 113) ||              // 203.0.113.0/24
               b[0] >= 224;                                              // Multicast & Reserved
    }

    /// <summary>Quita caracteres de control (evita inyección CRLF en cabeceras).</summary>
    public static string SanitizeHeaderValue(string? value, int maxLen = 300)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(Math.Min(value.Length, maxLen));
        foreach (char c in value)
        {
            if (c < 0x20 || c == 0x7F) continue;
            sb.Append(c);
            if (sb.Length >= maxLen) break;
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Resuelve un host y filtra cualquier IP privada/loopback/link-local/CGNAT/multicast.
    /// Falla con TvProxyErrorException("blocked") si todas las IPs resueltas son privadas, o "dns" si no resuelve.
    /// </summary>
    public static async Task<IPAddress[]> SelectPublicAddressesAsync(
        string host,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolver,
        CancellationToken ct)
    {
        if (IsPrivateOrLocalHost(host))
            throw new TvProxyErrorException("blocked", 502, "Host privado o local bloqueado.");

        IPAddress[] raw;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
        {
            raw = new[] { literal };
        }
        else
        {
            try
            {
                raw = resolver != null
                    ? await resolver(host, ct).ConfigureAwait(false)
                    : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                throw new TvProxyErrorException("dns", 502, $"No se pudo resolver el dominio ({SanitizeHeaderValue(ex.Message, 90)}).", ex);
            }
        }

        if (raw == null || raw.Length == 0)
            throw new TvProxyErrorException("dns", 502, "El dominio no devolvió direcciones IP.");

        var allowed = new List<IPAddress>(raw.Length);
        foreach (var ip in raw)
        {
            if (!IsPrivateIp(ip))
                allowed.Add(ip);
        }

        if (allowed.Count == 0)
            throw new TvProxyErrorException("blocked", 502, "Resolución DNS apuntó a una dirección privada bloqueada.");

        return allowed.ToArray();
    }

    /// <summary>
    /// Crea un HttpClient con ConnectCallback anti-SSRF por DNS y validación TLS estándar intacta.
    /// </summary>
    public static HttpClient CreatePinnedHttpClient(
        Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null,
        TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = async (context, ct) =>
            {
                var ips = await SelectPublicAddressesAsync(context.DnsEndPoint.Host, resolver, ct).ConfigureAwait(false);
                Exception? lastEx = null;
                foreach (var ip in ips)
                {
                    var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                    {
                        NoDelay = true
                    };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(ip, context.DnsEndPoint.Port), ct).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex)
                    {
                        lastEx = ex;
                        socket.Dispose();
                    }
                }

                if (lastEx is SocketException se)
                    throw se;
                throw lastEx ?? new SocketException((int)SocketError.HostUnreachable);
            }
        };

        return new HttpClient(handler)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(45)
        };
    }

    public static (string ErrorClass, int StatusCode, string Message) ClassifyException(Exception ex)
    {
        for (Exception? cur = ex; cur != null; cur = cur.InnerException)
        {
            if (cur is TvProxyErrorException pe)
                return (pe.ErrorClass, pe.SuggestedStatus, SanitizeHeaderValue(pe.Message, 160));
            if (cur is AuthenticationException)
                return ("tls", 502, "Error de certificado o negociación TLS del servidor.");
            if (cur is TimeoutException)
                return ("timeout", 504, "Tiempo de espera agotado al conectar con el servidor.");
            if (cur is TaskCanceledException)
                return ("timeout", 504, "Tiempo de espera agotado en la petición al canal.");
            if (cur is SocketException se)
            {
                return se.SocketErrorCode switch
                {
                    SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain =>
                        ("dns", 502, "No se encontró el servidor DNS del canal."),
                    SocketError.ConnectionRefused =>
                        ("refused", 502, "El servidor rechazó la conexión."),
                    SocketError.TimedOut =>
                        ("timeout", 504, "Tiempo de espera agotado al conectar."),
                    _ => ("upstream", 502, $"Error de red ({se.SocketErrorCode}).")
                };
            }
        }

        return ("upstream", 502, SanitizeHeaderValue(ex.Message, 160));
    }

    /// <summary>
    /// Ejecuta GET validando cada salto de redirección (hasta 5) para impedir redirecciones a hosts locales/privados,
    /// usando el User-Agent indicado (por defecto el estándar de Lex TV) y reenviando Range si aplica.
    /// </summary>
    public static async Task<(HttpResponseMessage Response, Uri FinalUri)> GetWithSafeRedirectsAsync(
        HttpClient client,
        Uri initialUri,
        string? rangeHeader,
        CancellationToken ct,
        int maxRedirects = 8,
        string? userAgent = null)
    {
        string ua = string.IsNullOrWhiteSpace(userAgent) ? DefaultUserAgent : SanitizeHeaderValue(userAgent, 120);
        Uri current = initialUri;
        for (int hop = 0; hop <= maxRedirects; hop++)
        {
            if (TvDenylist.IsDenylisted(current.AbsoluteUri, out var denyReason))
                throw new TvProxyErrorException("blocked", 403, denyReason ?? "Fuente deshabilitada tras una reclamación.");

            using var req = new HttpRequestMessage(HttpMethod.Get, current);
            req.Headers.TryAddWithoutValidation("User-Agent", ua);

            string cleanRange = SanitizeHeaderValue(rangeHeader, 80);
            if (cleanRange.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                req.Headers.TryAddWithoutValidation("Range", cleanRange);

            var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            int code = (int)resp.StatusCode;
            if (code is 301 or 302 or 303 or 307 or 308 && resp.Headers.Location != null)
            {
                Uri next = resp.Headers.Location.IsAbsoluteUri
                    ? resp.Headers.Location
                    : new Uri(current, resp.Headers.Location);
                resp.Dispose();

                if (!IsAllowedRemoteUrl(next.AbsoluteUri, out var safeNext) || safeNext == null)
                    throw new TvProxyErrorException("blocked", 502, "Redirección hacia host no permitido bloqueada.");

                current = safeNext;
                continue;
            }

            return (resp, current);
        }

        throw new TvProxyErrorException("upstream", 502, "Demasiadas redirecciones en el stream.");
    }

    public static int ParseRetryAfterMs(HttpResponseMessage resp, int maxMs = 2000)
    {
        var ra = resp.Headers.RetryAfter;
        if (ra == null) return 300;
        if (ra.Delta.HasValue)
        {
            int ms = (int)Math.Clamp(ra.Delta.Value.TotalMilliseconds, 100, maxMs);
            return ms;
        }
        if (ra.Date.HasValue)
        {
            int ms = (int)Math.Clamp((ra.Date.Value - DateTimeOffset.UtcNow).TotalMilliseconds, 100, maxMs);
            return ms;
        }
        return 300;
    }

    /// <summary>Lee texto UTF-8 desde un stream HTTP imponiendo un límite máximo de bytes.</summary>
    public static async Task<string> ReadBoundedUtf8Async(HttpResponseMessage resp, int maxBytes, CancellationToken ct)
    {
        if (resp.Content.Headers.ContentLength is long len && len > maxBytes)
            throw new TvProxyErrorException("upstream", 502, "Archivo excede el tamaño máximo permitido.");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var bounded = new BoundedReadStream(stream, maxBytes);
        using var ms = new MemoryStream();
        await bounded.CopyToAsync(ms, 16384, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }
}

/// <summary>Stream de solo lectura que impide leer más de MaxBytes directamente desde el stream HTTP sin bufferizar string completo.</summary>
public sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _maxBytes;
    private long _totalRead;

    public BoundedReadStream(Stream inner, long maxBytes)
    {
        _inner = inner;
        _maxBytes = maxBytes;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = _inner.Read(buffer, offset, count);
        Account(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Account(read);
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        int read = await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        Account(read);
        return read;
    }

    private void Account(int read)
    {
        if (read <= 0) return;
        _totalRead += read;
        if (_totalRead > _maxBytes)
            throw new TvProxyErrorException("upstream", 502, "La descarga excedió el límite máximo de bytes.");
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
