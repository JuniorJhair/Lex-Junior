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
/// </summary>
public static class TvUrlGuard
{
    public const int MaxPlaylistBytes = 20 * 1024 * 1024;
    public const int MaxCatalogBytes = 80 * 1024 * 1024;

    public static bool IsAllowedRemoteUrl(string? url, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.IsNullOrEmpty(u.UserInfo)) return false;
        if (IsPrivateOrLocalHost(u.Host)) return false;
        uri = u;
        return true;
    }

    public static bool IsAllowedCatalogHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        string h = host.Trim().ToLowerInvariant();
        return h == "iptv-org.github.io" || h == "biskyt.github.io" || h == "raw.githubusercontent.com";
    }

    public static bool IsAllowedCineCatalogHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        string h = host.Trim().ToLowerInvariant();
        return h == "biskyt.github.io" || h == "raw.githubusercontent.com";
    }

    public static bool IsAllowedCineVideoHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        string h = host.Trim().ToLowerInvariant();
        if (h == "archive.org" || h.EndsWith(".archive.org", StringComparison.Ordinal)) return true;
        if (h == "upload.wikimedia.org") return true;
        return false;
    }

    public static bool IsAllowedCineMediaUrl(string? url, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttps) return false; // Solo HTTPS estricto para Cine VOD
        if (!string.IsNullOrEmpty(u.UserInfo)) return false;
        if (IsPrivateOrLocalHost(u.Host)) return false;
        if (!IsAllowedCineVideoHost(u.Host)) return false;
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
            byte[] b6 = ip.GetAddressBytes();
            return (b6[0] & 0xFE) == 0xFC || ip.Equals(IPAddress.IPv6Any);
        }

        byte[] b = ip.GetAddressBytes();
        if (b.Length != 4) return true;
        return b[0] == 0 || b[0] == 10 || b[0] == 127 ||
               (b[0] == 169 && b[1] == 254) ||
               (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
               (b[0] == 192 && b[1] == 168) ||
               (b[0] == 100 && b[1] >= 64 && b[1] <= 127) ||
               b[0] >= 224;
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

    /// <summary>Sobrecarga compatible sin Range.</summary>
    public static Task<(HttpResponseMessage Response, Uri FinalUri)> GetWithSafeRedirectsAsync(
        HttpClient client,
        Uri initialUri,
        string? referrer,
        string? userAgent,
        CancellationToken ct,
        int maxRedirects = 5)
        => GetWithSafeRedirectsAsync(client, initialUri, referrer, userAgent, null, ct, maxRedirects);

    /// <summary>
    /// Ejecuta GET validando cada salto de redirección (hasta 5) para impedir redirecciones a hosts locales/privados,
    /// reenviando Range si aplica.
    /// </summary>
    public static async Task<(HttpResponseMessage Response, Uri FinalUri)> GetWithSafeRedirectsAsync(
        HttpClient client,
        Uri initialUri,
        string? referrer,
        string? userAgent,
        string? rangeHeader,
        CancellationToken ct,
        int maxRedirects = 5)
    {
        Uri current = initialUri;
        for (int hop = 0; hop <= maxRedirects; hop++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, current);
            string cleanUa = SanitizeHeaderValue(userAgent, 240);
            req.Headers.TryAddWithoutValidation("User-Agent",
                cleanUa.Length > 0 ? cleanUa : "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) LexJunior/2.1");

            string cleanRef = SanitizeHeaderValue(referrer, 500);
            if (cleanRef.Length > 0 && IsAllowedRemoteUrl(cleanRef, out var refUri) && refUri != null)
                req.Headers.Referrer = refUri;

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
