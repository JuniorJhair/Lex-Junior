using System;
using System.Collections.Generic;

namespace LexBrowser.Core.Tv;

/// <summary>
/// Memoria volátil (solo en RAM) de qué User-Agent y qué extensión de live (.ts/.m3u8) funcionó
/// por conexión Xtream. Para URLs sin conexión Xtream se indexa por host ("host:nombre").
/// No almacena credenciales ni URLs completas.
/// </summary>
public static class TvPlaybackHints
{
    private const int MaxEntries = 256;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Hint> Map = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Hint
    {
        public string? UserAgent;
        public string? LiveExt;
        public string? Referer;
        public string? Origin;
    }

    private static string? HostKey(string? host)
        => string.IsNullOrWhiteSpace(host) ? null : "host:" + host.Trim().ToLowerInvariant();

    public static string? GetUserAgent(string? connId, string? host)
    {
        lock (Gate)
        {
            if (!string.IsNullOrWhiteSpace(connId) &&
                Map.TryGetValue(connId.Trim(), out var byConn) && byConn.UserAgent != null)
                return byConn.UserAgent;

            string? hk = HostKey(host);
            if (hk != null && Map.TryGetValue(hk, out var byHost) && byHost.UserAgent != null)
                return byHost.UserAgent;
        }
        return null;
    }

    /// <summary>Recuerda el UA que funcionó para la conexión y, además, para su host (segmentos HLS, imágenes).</summary>
    public static void RememberUserAgent(string? connId, string? host, string userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return;
        lock (Gate)
        {
            if (!string.IsNullOrWhiteSpace(connId)) GetOrAdd(connId.Trim()).UserAgent = userAgent;
            string? hk = HostKey(host);
            if (hk != null) GetOrAdd(hk).UserAgent = userAgent;
        }
    }

    public static string? GetLiveExt(string? connId)
    {
        if (string.IsNullOrWhiteSpace(connId)) return null;
        lock (Gate)
        {
            return Map.TryGetValue(connId.Trim(), out var h) ? h.LiveExt : null;
        }
    }

    public static void RememberLiveExt(string? connId, string ext)
    {
        if (string.IsNullOrWhiteSpace(connId) || string.IsNullOrWhiteSpace(ext)) return;
        lock (Gate)
        {
            GetOrAdd(connId.Trim()).LiveExt = ext.TrimStart('.').ToLowerInvariant();
        }
    }

    public static void RememberHostPlaybackHeaders(string? host, string? userAgent, string? referer, string? origin)
    {
        string? hk = HostKey(host);
        if (hk == null) return;

        string cleanUa = TvUrlGuard.SanitizeHeaderValue(userAgent, 160);
        string? cleanRef = SanitizeHttpUrlHeader(referer);
        string? cleanOrig = SanitizeHttpUrlHeader(origin);

        lock (Gate)
        {
            var h = GetOrAdd(hk);
            if (!string.IsNullOrWhiteSpace(cleanUa)) h.UserAgent = cleanUa;
            if (!string.IsNullOrWhiteSpace(cleanRef)) h.Referer = cleanRef;
            if (!string.IsNullOrWhiteSpace(cleanOrig)) h.Origin = cleanOrig;
        }
    }

    public static (string? UserAgent, string? Referer, string? Origin) GetHostHeaders(string? connId, string? host)
    {
        string? ua = GetUserAgent(connId, host);
        string? hk = HostKey(host);
        string? referer = null;
        string? origin = null;

        if (hk != null)
        {
            lock (Gate)
            {
                if (Map.TryGetValue(hk, out var byHost))
                {
                    referer = byHost.Referer;
                    origin = byHost.Origin;
                }
            }
        }

        return (ua, referer, origin);
    }

    private static string? SanitizeHttpUrlHeader(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string clean = TvUrlGuard.SanitizeHeaderValue(raw, 512);
        if (!Uri.TryCreate(clean, UriKind.Absolute, out var u)) return null;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;
        if (!string.IsNullOrEmpty(u.UserInfo)) return null;
        return u.AbsoluteUri;
    }

    public static void Clear()
    {
        lock (Gate) Map.Clear();
    }

    private static Hint GetOrAdd(string key)
    {
        if (!Map.TryGetValue(key, out var h))
        {
            if (Map.Count >= MaxEntries) Map.Clear();
            h = new Hint();
            Map[key] = h;
        }
        return h;
    }
}

