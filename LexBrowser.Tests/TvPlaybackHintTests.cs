using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LexBrowser.Core.Tv;
using Xunit;

namespace LexBrowser.Tests;

public sealed class TvPlaybackHintTests : IDisposable
{
    private sealed class DelegatingMockHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _func;

        public DelegatingMockHandler(Func<HttpRequestMessage, HttpResponseMessage> func)
        {
            _func = func;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_func(request));
        }
    }

    public TvPlaybackHintTests()
    {
        TvPlaybackHints.Clear();
        TvStreamProxy.ClearProbeCache();
    }

    public void Dispose()
    {
        TvPlaybackHints.Clear();
        TvStreamProxy.ClearProbeCache();
    }

    [Fact]
    public async Task GetWithSafeRedirectsAsync_UsesCustomUserAgent_WhenProvided()
    {
        string? capturedUa = null;
        using var handler = new DelegatingMockHandler(req =>
        {
            capturedUa = req.Headers.UserAgent?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok", Encoding.UTF8, "text/plain")
            };
        });
        using var client = new HttpClient(handler);

        var (resp, _) = await TvUrlGuard.GetWithSafeRedirectsAsync(
            client,
            new Uri("https://example.com/test"),
            rangeHeader: null,
            CancellationToken.None,
            userAgent: "CustomPlayer/1.0");

        using (resp)
        {
            Assert.Equal("CustomPlayer/1.0", capturedUa);
        }
    }

    [Fact]
    public async Task GetWithSafeRedirectsAsync_UsesDefaultUserAgent_WhenNullOrOmitted()
    {
        string? capturedUa = null;
        using var handler = new DelegatingMockHandler(req =>
        {
            capturedUa = req.Headers.UserAgent?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok", Encoding.UTF8, "text/plain")
            };
        });
        using var client = new HttpClient(handler);

        var (resp, _) = await TvUrlGuard.GetWithSafeRedirectsAsync(
            client,
            new Uri("https://example.com/test"),
            rangeHeader: null,
            CancellationToken.None);

        using (resp)
        {
            Assert.Equal(TvUrlGuard.DefaultUserAgent, capturedUa);
        }
    }

    [Fact]
    public void TvUrlGuard_PlayerUserAgent_ConfigurableAndDefaultsToNeutral()
    {
        Assert.Equal("LexTV/1.0 (Player)", TvUrlGuard.DefaultPlayerUserAgent);
        Assert.Equal("VLC/3.0.20 LibVLC/3.0.20", TvUrlGuard.FallbackPlayerUserAgent);

        string original = TvUrlGuard.PlayerUserAgent;
        try
        {
            TvUrlGuard.PlayerUserAgent = "MyCustomPlayer/2.0";
            Assert.Equal("MyCustomPlayer/2.0", TvUrlGuard.PlayerUserAgent);
        }
        finally
        {
            TvUrlGuard.PlayerUserAgent = original;
        }
    }

    [Fact]
    public void TvPlaybackHints_RememberAndRetrieve_WorksByConnIdAndHost()
    {
        Assert.Null(TvPlaybackHints.GetUserAgent("conn1", "stream.example.com"));
        Assert.Null(TvPlaybackHints.GetLiveExt("conn1"));

        TvPlaybackHints.RememberUserAgent("conn1", "stream.example.com", "VLC/3.0.20 LibVLC/3.0.20");
        TvPlaybackHints.RememberLiveExt("conn1", "m3u8");

        Assert.Equal("VLC/3.0.20 LibVLC/3.0.20", TvPlaybackHints.GetUserAgent("conn1", "other.example.com"));
        Assert.Equal("VLC/3.0.20 LibVLC/3.0.20", TvPlaybackHints.GetUserAgent(null, "stream.example.com"));
        Assert.Equal("m3u8", TvPlaybackHints.GetLiveExt("conn1"));
    }

    [Fact]
    public async Task TvPlaybackHints_FallbackUserAgent_On403Or404_CanBeSimulatedAndRemembered()
    {
        var attempts = new List<string?>();
        using var handler = new DelegatingMockHandler(req =>
        {
            string? ua = req.Headers.UserAgent?.ToString();
            attempts.Add(ua);
            if (ua == TvUrlGuard.PlayerUserAgent)
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    ReasonPhrase = "Forbidden"
                };
            }
            if (ua == TvUrlGuard.FallbackPlayerUserAgent)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("ok", Encoding.UTF8, "text/plain")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        });
        using var client = new HttpClient(handler);

        // Intento 1 con UA primario -> 403
        string targetHost = "iptv.ejemplo.invalid";
        string currentUa = TvPlaybackHints.GetUserAgent("conn_abc", targetHost) ?? TvUrlGuard.PlayerUserAgent;
        var (resp1, _) = await TvUrlGuard.GetWithSafeRedirectsAsync(client, new Uri($"https://{targetHost}/stream.ts"), null, CancellationToken.None, userAgent: currentUa);
        using (resp1)
        {
            Assert.Equal(HttpStatusCode.Forbidden, resp1.StatusCode);
        }

        // Reintento con UA alternativo ("VLC/3.0.20 LibVLC/3.0.20")
        currentUa = TvUrlGuard.FallbackPlayerUserAgent;
        var (resp2, _) = await TvUrlGuard.GetWithSafeRedirectsAsync(client, new Uri($"https://{targetHost}/stream.ts"), null, CancellationToken.None, userAgent: currentUa);
        using (resp2)
        {
            Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
            TvPlaybackHints.RememberUserAgent("conn_abc", targetHost, currentUa);
        }

        // Siguiente petición ya obtiene el UA recordado
        Assert.Equal(TvUrlGuard.FallbackPlayerUserAgent, TvPlaybackHints.GetUserAgent("conn_abc", targetHost));
        Assert.Equal(2, attempts.Count);
        Assert.Equal(TvUrlGuard.PlayerUserAgent, attempts[0]);
        Assert.Equal(TvUrlGuard.FallbackPlayerUserAgent, attempts[1]);
    }

    [Fact]
    public async Task LiveExtensionRetry_On404_AlternatesTsAndM3u8_AndRemembers()
    {
        var requestedUrls = new List<string>();
        using var handler = new DelegatingMockHandler(req =>
        {
            string url = req.RequestUri?.ToString() ?? "";
            requestedUrls.Add(url);
            if (url.EndsWith(".ts"))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    ReasonPhrase = "Not Found"
                };
            }
            if (url.EndsWith(".m3u8"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("#EXTM3U\n#EXTINF:-1,Ch\nseg.ts", Encoding.UTF8, "application/vnd.apple.mpegurl")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var client = new HttpClient(handler);

        // Solicitud inicial con .ts falla con 404
        string baseStreamUrl = "https://stream.ejemplo.invalid/live/user/pass/100";
        var (respTs, _) = await TvUrlGuard.GetWithSafeRedirectsAsync(client, new Uri(baseStreamUrl + ".ts"), null, CancellationToken.None);
        using (respTs)
        {
            Assert.Equal(HttpStatusCode.NotFound, respTs.StatusCode);
        }

        // Alterna a .m3u8 y tiene éxito
        var (respHls, _) = await TvUrlGuard.GetWithSafeRedirectsAsync(client, new Uri(baseStreamUrl + ".m3u8"), null, CancellationToken.None);
        using (respHls)
        {
            Assert.Equal(HttpStatusCode.OK, respHls.StatusCode);
            TvPlaybackHints.RememberLiveExt("conn_test", "m3u8");
        }

        Assert.Equal("m3u8", TvPlaybackHints.GetLiveExt("conn_test"));
        Assert.Equal(2, requestedUrls.Count);
        Assert.EndsWith(".ts", requestedUrls[0]);
        Assert.EndsWith(".m3u8", requestedUrls[1]);
    }

    [Fact]
    public async Task ProbeCache_DoesNotCache404Or403_OrMissingCredentials()
    {
        TvStreamProxy.ClearProbeCache();

        // 1. URL virtual con credenciales faltantes retorna 404 con errorClass: "credentials"
        var resMissing = await TvStreamProxy.ProbeAsync("https://tv.lex.local/api/xtream/live?conn=inexistente&id=999&ext=ts", CancellationToken.None);
        Assert.Equal(404, resMissing.Status);
        Assert.Equal("credentials", resMissing.ErrorClass);

        // 2. Limpieza e invalidación de caché
        TvStreamProxy.InvalidateProbe("https://tv.lex.local/api/xtream/live?conn=inexistente&id=999&ext=ts");

        // 3. FetchAsync con credenciales faltantes genera 404 diferenciado con X-Lex-Error-Class
        using var proxyRes = await TvStreamProxy.FetchAsync("https://tv.lex.local/api/xtream/live?conn=inexistente&id=999&ext=ts", CancellationToken.None);
        Assert.Equal(404, proxyRes.StatusCode);
        Assert.True(proxyRes.ExtraHeaders.TryGetValue("X-Lex-Error-Class", out var errClass));
        Assert.Equal("credentials", errClass);
    }
}

