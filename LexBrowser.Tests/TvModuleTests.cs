using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LexBrowser.Core;
using LexBrowser.Core.AdBlock;
using LexBrowser.Core.Tv;
using Xunit;

namespace LexBrowser.Tests;

public class TvModuleTests
{
    [Theory]
    [InlineData("https://example.com/live/stream.m3u8")]
    [InlineData("http://cdn.broadcaster.org:8080/ch1/index.m3u8")]
    public void TvUrlGuard_AllowsPublicHttpAndHttps(string url)
    {
        Assert.True(TvUrlGuard.IsAllowedRemoteUrl(url, out var uri));
        Assert.NotNull(uri);
    }

    [Theory]
    [InlineData("file:///C:/secret.m3u8")]
    [InlineData("javascript:alert(1)")]
    [InlineData("rtsp://192.168.1.10/stream")]
    [InlineData("http://localhost:8080/live.m3u8")]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://192.168.1.25/live.m3u8")]
    [InlineData("http://10.0.0.5/live.m3u8")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("https://user:pass@example.com/live.m3u8")]
    [InlineData("https://tv.lex.local/api/catalog")]
    public void TvUrlGuard_RejectsPrivateLocalAndUnsafeUrls(string url)
    {
        Assert.False(TvUrlGuard.IsAllowedRemoteUrl(url, out _));
    }

    [Fact]
    public void TvUrlGuard_SanitizeHeaderValue_StripsCrlfAndControlChars()
    {
        string clean = TvUrlGuard.SanitizeHeaderValue("Canal\r\nX-Injected: evil\0Nombre");
        Assert.Equal("CanalX-Injected: evilNombre", clean);
    }

    [Fact]
    public async Task TvUrlGuard_DnsSsrfResolver_RejectsPrivateAndFiltersMixedAddresses()
    {
        // DNS rebote a IP privada debe lanzar TvProxyErrorException("blocked")
        var ex = await Assert.ThrowsAsync<TvProxyErrorException>(() =>
            TvUrlGuard.SelectPublicAddressesAsync(
                "rebind.example.com",
                (_, _) => Task.FromResult(new[]
                {
                    IPAddress.Parse("127.0.0.1"),
                    IPAddress.Parse("100.64.0.1"),
                    IPAddress.Parse("192.168.1.50")
                }),
                CancellationToken.None));
        Assert.Equal("blocked", ex.ErrorClass);

        // DNS mixto conserva solo direcciones públicas en orden
        var publicOnly = await TvUrlGuard.SelectPublicAddressesAsync(
            "mixed.example.com",
            (_, _) => Task.FromResult(new[]
            {
                IPAddress.Parse("10.0.0.1"),
                IPAddress.Parse("93.184.216.34"),
                IPAddress.Parse("169.254.169.254"),
                IPAddress.Parse("1.1.1.1")
            }),
            CancellationToken.None);
        Assert.Equal(2, publicOnly.Length);
        Assert.Equal(IPAddress.Parse("93.184.216.34"), publicOnly[0]);
        Assert.Equal(IPAddress.Parse("1.1.1.1"), publicOnly[1]);

        // DNS vacío devuelve "dns"
        var dnsEx = await Assert.ThrowsAsync<TvProxyErrorException>(() =>
            TvUrlGuard.SelectPublicAddressesAsync("missing.example.com", (_, _) => Task.FromResult(Array.Empty<IPAddress>()), CancellationToken.None));
        Assert.Equal("dns", dnsEx.ErrorClass);
    }

    [Fact]
    public void TvUrlGuard_ClassifyException_MapsTlsDnsTimeoutAndRefused()
    {
        Assert.Equal(("tls", 502), (TvUrlGuard.ClassifyException(new AuthenticationException("bad cert")).ErrorClass,
                                     TvUrlGuard.ClassifyException(new AuthenticationException("bad cert")).StatusCode));
        Assert.Equal(("dns", 502), (TvUrlGuard.ClassifyException(new SocketException((int)SocketError.HostNotFound)).ErrorClass,
                                     TvUrlGuard.ClassifyException(new SocketException((int)SocketError.HostNotFound)).StatusCode));
        Assert.Equal(("refused", 502), (TvUrlGuard.ClassifyException(new SocketException((int)SocketError.ConnectionRefused)).ErrorClass,
                                         TvUrlGuard.ClassifyException(new SocketException((int)SocketError.ConnectionRefused)).StatusCode));
        Assert.Equal(("timeout", 504), (TvUrlGuard.ClassifyException(new TaskCanceledException()).ErrorClass,
                                         TvUrlGuard.ClassifyException(new TaskCanceledException()).StatusCode));
    }

    [Fact]
    public void TvStreamSniffer_ClassifiesAllSupportedContainerAndManifestSignatures()
    {
        Assert.Equal(TvStreamKind.Hls, TvStreamSniffer.Classify("\uFEFF  #EXTM3U\n#EXT-X-VERSION:3"u8));

        byte[] ts = new byte[377];
        ts[0] = 0x47; ts[188] = 0x47; ts[376] = 0x47;
        Assert.Equal(TvStreamKind.MpegTs, TvStreamSniffer.Classify(ts));

        Assert.Equal(TvStreamKind.Flv, TvStreamSniffer.Classify("FLV\x01\x05"u8));
        Assert.Equal(TvStreamKind.Mp4, TvStreamSniffer.Classify(new byte[] { 0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m' }));
        Assert.Equal(TvStreamKind.WebM, TvStreamSniffer.Classify(new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x9F }));
        Assert.Equal(TvStreamKind.Ogg, TvStreamSniffer.Classify("OggS\x00"u8));
        Assert.Equal(TvStreamKind.Mp3, TvStreamSniffer.Classify("ID3\x03\x00"u8));
        Assert.Equal(TvStreamKind.Mp3, TvStreamSniffer.Classify(new byte[] { 0xFF, 0xFB, 0x90, 0x00 }));
        Assert.Equal(TvStreamKind.AacAdts, TvStreamSniffer.Classify(new byte[] { 0xFF, 0xF1, 0x50, 0x80 }));
        Assert.Equal(TvStreamKind.Dash, TvStreamSniffer.Classify("<?xml version=\"1.0\"?><MPD xmlns=\"urn:mpeg:dash\"></MPD>"u8));
        Assert.Equal(TvStreamKind.Html, TvStreamSniffer.Classify("  <!DOCTYPE html><html></html>"u8));
        Assert.Equal(TvStreamKind.Unknown, TvStreamSniffer.Classify(new byte[] { 1, 2, 3, 4 }));
    }

    [Fact]
    public void M3uParser_ParsesAttributesOptionsAndFiltersUnsafeUrls()
    {
        string m3u = """
            #EXTM3U
            #EXTINF:-1 tvg-id="ch1.pe" tvg-chno="7" tvg-logo="https://img.example.com/7.png" tvg-language="Spanish" tvg-country="PE" group-title="Noticias, Vivo",Canal Siete HD
            #EXTVLCOPT:http-referrer=https://player.example.com/
            #EXTVLCOPT:http-user-agent=CustomTV/1.0
            https://cdn.example.com/ch7/index.m3u8
            #EXTINF:-1 group-title="Local",Canal Interno Bloqueado
            http://127.0.0.1:9000/secret.m3u8
            #EXTINF:-1,Sin Atributos
            #EXTGRP:Deportes
            http://stream.example.org/sports.m3u8
            """;

        var list = M3uParser.Parse(m3u, "Mi Lista");
        Assert.Equal(2, list.Count);

        Assert.Equal(7, list[0].Number);
        Assert.Equal("ch1.pe", list[0].Id);
        Assert.Equal("Canal Siete HD", list[0].Name);
        Assert.Equal("Noticias, Vivo", list[0].Group);
        Assert.Equal("https://img.example.com/7.png", list[0].Logo);

        Assert.Equal("Deportes", list[1].Group);
        Assert.Equal("Sin Atributos", list[1].Name);
    }

    [Fact]
    public void M3uParser_ParseDetailed_SupportsKodiPipeVlcoptBeforeAfterStatsAndStandaloneHls()
    {
        string m3u = """
            #EXTM3U url-tvg="https://epg.example.com/guide.xml"
            #EXTVLCOPT:http-referrer=https://before.example.com/
            #EXTVLCOPT:http-user-agent=BeforeUA/2
            #EXTINF:-1 tvg-id="c1" tvg-logo="http://insecure-logo.example.com/1.png",Canal Before
            https://cdn.example.com/a.m3u8
            #EXTINF:-1 tvg-id="c2",Canal Kodi Pipe
            https://cdn.example.com/b.m3u8|User-Agent=KodiAgent%2F3.0&Referer=https%3A%2F%2Fkodi.example.org%2F
            #EXTINF:-1,Dup URL
            https://cdn.example.com/a.m3u8
            #EXTINF:-1,RTSP Unsupported
            rtsp://cdn.example.com/rtsp
            #EXTINF:-1,Private IP
            http://192.168.10.5/stream.ts
            #EXTINF:-1,Invalid
            not-a-valid-url
            """;

        var parsed = M3uParser.ParseDetailed(m3u, "TestList");
        Assert.Equal(2, parsed.Channels.Count);
        Assert.Equal(1, parsed.SkippedDuplicate);
        Assert.Equal(1, parsed.SkippedUnsupportedScheme);
        Assert.Equal(1, parsed.SkippedPrivate);
        Assert.Equal(1, parsed.SkippedInvalid);

        // Logo http:// descartado por HTTPS-only (no se propagan cabeceras de terceros)
        Assert.Equal("", parsed.Channels[0].Logo);
        Assert.Equal("Canal Before", parsed.Channels[0].Name);
        Assert.Equal("Canal Kodi Pipe", parsed.Channels[1].Name);

        // HLS suelto (#EXT-X-*) sin #EXTINF tratado como canal único cuando hay sourceUrl válida
        string standalone = """
            #EXTM3U
            #EXT-X-VERSION:3
            #EXT-X-TARGETDURATION:6
            seg0.ts
            """;
        var hlsRes = M3uParser.ParseDetailed(standalone, "Canal Directo", "https://edge.example.com/live/index.m3u8");
        Assert.True(hlsRes.IsStandaloneHlsManifest);
        Assert.Single(hlsRes.Channels);
        Assert.Equal("https://edge.example.com/live/index.m3u8", hlsRes.Channels[0].Url);
    }



    [Fact]
    public void TvStreamProxy_RewriteHlsPlaylist_RewritesRelativePathsSafely()
    {
        string playlist = """
            #EXTM3U
            #EXT-X-KEY:METHOD=NONE
            #EXTINF:4.0,
            720p/seg001.ts
            #EXTINF:4.0,
            https://cdn2.example.com/seg002.ts
            """;
        var baseUri = new Uri("http://origin.example.com/live/master.m3u8");

        string rewritten = TvStreamProxy.RewriteHlsPlaylist(playlist, baseUri, "g1_0");

        Assert.Contains("/api/proxy?u=" + Uri.EscapeDataString("http://origin.example.com/live/720p/seg001.ts") + "&sid=g1_0", rewritten);
        Assert.Contains("/api/proxy?u=" + Uri.EscapeDataString("https://cdn2.example.com/seg002.ts") + "&sid=g1_0", rewritten);
        Assert.DoesNotContain("&r=", rewritten);
        Assert.DoesNotContain("&ua=", rewritten);
    }

    [Fact]
    public void TvStreamProxy_BuildForwardHeaders_PreservesRangeAndLengthHeaders()
    {
        using var resp = new HttpResponseMessage(HttpStatusCode.PartialContent);
        resp.Headers.Add("Accept-Ranges", "bytes");
        resp.Content = new ByteArrayContent(new byte[10]);
        resp.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(0, 9, 100);
        resp.Content.Headers.ContentLength = 10;

        var hdrs = TvStreamProxy.BuildForwardHeaders(resp);
        Assert.Equal("bytes 0-9/100", hdrs["Content-Range"]);
        Assert.Equal("bytes", hdrs["Accept-Ranges"]);
        Assert.Equal("10", hdrs["Content-Length"]);
    }

    [Fact]
    public void TvUserStateStore_PersistsFavoritesPlaylistsAndDeadMarks()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "lex_tv_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TvUserStateStore(tempDir);
            var favs1 = store.ToggleFavorite("TVPeru.pe");
            Assert.Single(favs1);

            var added = store.AddOrUpdatePlaylist("Mis Canales", "https://example.com/mi.m3u", new()
            {
                new TvChannel { Name = "Canal Uno", Url = "https://cdn.example.com/1.m3u8" },
                new TvChannel { Name = "Canal Dos", Url = "https://cdn.example.com/2.m3u8" }
            });
            Assert.Equal(2, added.Channels.Count);
            Assert.Equal(1, added.Channels[0].Number);
            Assert.Equal(2, added.Channels[1].Number);

            store.MarkDead("https://cdn.example.com/1.m3u8", "404");

            var reloaded = new TvUserStateStore(tempDir).GetSnapshot();
            Assert.Single(reloaded.Favorites);
            Assert.Single(reloaded.Playlists);
            Assert.True(reloaded.DeadStreams.ContainsKey("https://cdn.example.com/1.m3u8"));
            Assert.True(File.Exists(Path.Combine(tempDir, "tv_prefs.json")));
            Assert.True(File.Exists(Path.Combine(tempDir, "tv_playlists.json")));

            store.UnmarkDead("https://cdn.example.com/1.m3u8");
            Assert.Empty(store.GetSnapshot().DeadStreams);
            Assert.True(store.RemovePlaylist(added.Id));
            Assert.Empty(store.GetSnapshot().Playlists);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TvUserStateStore_MigratesLegacyFileExpiresTypedMarksAndFlushesDebounce()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "lex_tv_mig_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            string legacyPath = Path.Combine(tempDir, "tv_user_state.json");
            var legacy = new TvUserStateData
            {
                Favorites = new() { "TVPeru.pe@HD" },
                Playlists = new()
                {
                    new TvUserPlaylist
                    {
                        Id = "pl_1",
                        Name = "Legacy",
                        Channels = new() { new TvChannel { Id = "c1", Name = "One", Url = "https://a.example.com/1.m3u8", Number = 9 } }
                    }
                },
                DeadStreams = new()
                {
                    ["https://old.example.com/expired.m3u8"] = new TvDeadMark
                    {
                        FailedUtc = DateTime.UtcNow.AddDays(-5),
                        Reason = "404"
                    },
                    ["https://keep.example.com/alive-mark.m3u8"] = new TvDeadMark
                    {
                        FailedUtc = DateTime.UtcNow.AddHours(-2),
                        Reason = "500"
                    }
                }
            };
            File.WriteAllText(legacyPath, JsonSerializer.Serialize(legacy));

            var store = new TvUserStateStore(tempDir);
            var snap = store.GetSnapshot();
            Assert.True(File.Exists(legacyPath + ".bak"));
            Assert.False(File.Exists(legacyPath));
            Assert.Single(snap.Favorites);
            Assert.Single(snap.Playlists);
            Assert.Equal(9, snap.Playlists[0].Channels[0].Number);
            // La marca heredada >72h debe haber expirado en la migración
            Assert.False(snap.DeadStreams.ContainsKey("https://old.example.com/expired.m3u8"));
            Assert.True(snap.DeadStreams.ContainsKey("https://keep.example.com/alive-mark.m3u8"));

            // Marcas tipificadas: transient NO se persiste; restricted expira a 24h; dead a 72h; codec a 7d
            var t0 = DateTime.UtcNow;
            Assert.False(store.MarkFailure("https://t.example.com/t.m3u8", "transient", "503", t0));
            Assert.True(store.MarkFailure("https://g.example.com/g.m3u8", "restricted", "403", t0));
            Assert.True(store.MarkFailure("https://c.example.com/c.m3u8", "codec", "AC-3", t0));
            store.Flush();

            var prefsAfter25h = store.GetPrefsSnapshot(t0.AddHours(25));
            Assert.False(prefsAfter25h.DeadStreams.ContainsKey("https://g.example.com/g.m3u8"));
            Assert.True(prefsAfter25h.DeadStreams.ContainsKey("https://c.example.com/c.m3u8"));

            store.ReleaseMemoryCache();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TvUserStateStore_RejectsExceeding200kChannels()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "lex_tv_limit_" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TvUserStateStore(tempDir);
            var huge = new List<TvChannel>(TvUserStateStore.MaxTotalUserChannels + 1);
            for (int i = 0; i <= TvUserStateStore.MaxTotalUserChannels; i++)
                huge.Add(new TvChannel { Id = "c" + i, Name = "C", Url = "https://cdn.example.com/" + i });

            var ex = Assert.Throws<InvalidOperationException>(() =>
                store.AddOrUpdatePlaylist("Gigante", "", huge));
            Assert.Contains("200", ex.Message);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TvStudioService_OmniboxOriginAssetsAndShieldIsolation_WorkCleanly()
    {
        Assert.Equal(TvStudioService.HomeUrl, OmniboxInputResolver.ResolveNavigationTarget("lex://tv"));
        Assert.Equal(TvStudioService.HomeUrl, OmniboxInputResolver.ResolveNavigationTarget("tv.lex.local"));
        Assert.True(TvStudioService.IsTvShortcut("http://tv.lex.local"));
        Assert.True(TvStudioService.IsTvShortcut("https://tv.lex.local/index.html"));

        Assert.True(TvStudioService.IsValidTvOriginOrReferer("https://tv.lex.local", null));
        Assert.True(TvStudioService.IsValidTvOriginOrReferer(null, "https://tv.lex.local/index.html"));
        Assert.False(TvStudioService.IsValidTvOriginOrReferer(null, null));
        Assert.False(TvStudioService.IsValidTvOriginOrReferer("https://evil.example.com", null));

        string dir = TvStudioService.GetTvAssetsFolder();
        Assert.True(File.Exists(Path.Combine(dir, "index.html")));
        Assert.True(File.Exists(Path.Combine(dir, "tv.css")));
        Assert.True(File.Exists(Path.Combine(dir, "tv.js")));
        Assert.True(File.Exists(Path.Combine(dir, "theme.js")));
        Assert.True(File.Exists(Path.Combine(dir, "vendor", "hls.light.min.js")));
        Assert.True(File.Exists(Path.Combine(dir, "vendor", "mpegts.js")));

        Assert.NotNull(TvStudioService.LoadStaticAsset("theme.js"));
        Assert.NotNull(TvStudioService.LoadStaticAsset("vendor/hls.light.min.js"));
        Assert.NotNull(TvStudioService.LoadStaticAsset("vendor/mpegts.js"));
        Assert.Null(TvStudioService.LoadStaticAsset("../app.manifest"));

        // Escudos no inyectan lógica sobre tv.lex.local
        Assert.Contains("tv.lex.local", CookieBannerShield.BaseScript);
        Assert.Contains("tv.lex.local", WebShield.BaseScript);
    }

    [Fact]
    public void TvTheme_AllViews_IncludeThemeScriptAndSelector()
    {
        string dir = TvStudioService.GetTvAssetsFolder();
        string[] htmlFiles = ["index.html", "tv.html", "vod.html", "xtream.html", "como-funciona.html", "legal.html"];
        foreach (string file in htmlFiles)
        {
            string content = File.ReadAllText(Path.Combine(dir, file));
            Assert.Contains("theme.js", content);
            Assert.Contains("theme-selector", content);
        }

        byte[]? themeBytes = TvStudioService.LoadStaticAsset("theme.js");
        Assert.NotNull(themeBytes);
        string themeJs = System.Text.Encoding.UTF8.GetString(themeBytes!);
        Assert.Contains("lex_tv_theme", themeJs);
        Assert.Contains("data-theme", themeJs);
        Assert.Contains("'dark'", themeJs);
        Assert.Contains("'light'", themeJs);
        Assert.Contains("'classic'", themeJs);
    }

    [Fact]
    public void HistoryStore_DropsInternalTvUrlsOnLoadAndAdd()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "lex_tv_hist_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            string seed = JsonSerializer.Serialize(new[]
            {
                new HistoryEntry { Title = "Lex TV", Url = "https://tv.lex.local/index.html", Domain = "tv.lex.local" },
                new HistoryEntry { Title = "Example", Url = "https://example.com/", Domain = "example.com" }
            });
            File.WriteAllText(tempFile, seed);

            using var store = new HistoryStore(tempFile);
            store.AddEntry("https://tv.lex.local/index.html", "Lex TV");
            var entries = store.GetEntries();

            Assert.Single(entries);
            Assert.Equal("https://example.com/", entries[0].Url);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public async Task TvStreamProxy_OwnedStream_ReleasesOnEofWithoutDispose()
    {
        int baseline = TvStreamProxy.ActiveUpstreamConnections;
        var inner = new MemoryStream(Encoding.UTF8.GetBytes("hello-stream"));
        var owner = new HttpResponseMessage(HttpStatusCode.OK);
        var stream = TvStreamProxy.CreateOwnedStreamForTest(inner, owner, "eof-sid");

        Assert.Equal(baseline + 1, TvStreamProxy.ActiveUpstreamConnections);

        byte[] buf = new byte[64];
        int n1 = await stream.ReadAsync(buf.AsMemory(0, buf.Length));
        Assert.True(n1 > 0);
        // Lee hasta EOF (read == 0) sin llamar a Dispose() (como hace ManagedIStream de WebView2)
        int n2 = await stream.ReadAsync(buf.AsMemory(0, buf.Length));
        Assert.Equal(0, n2);

        Assert.Equal(baseline, TvStreamProxy.ActiveUpstreamConnections);
        GC.KeepAlive(stream);
    }

    [Fact]
    public void TvStreamProxy_AbortSession_ReleasesOpenNonEofStream()
    {
        int baseline = TvStreamProxy.ActiveUpstreamConnections;
        var inner = new MemoryStream(new byte[1024]);
        var owner = new HttpResponseMessage(HttpStatusCode.OK);
        var stream = TvStreamProxy.CreateOwnedStreamForTest(inner, owner, "abort-sid-1");

        Assert.Equal(baseline + 1, TvStreamProxy.ActiveUpstreamConnections);

        int aborted = TvStreamProxy.AbortSession("abort-sid-1");
        Assert.Equal(1, aborted);
        Assert.Equal(baseline, TvStreamProxy.ActiveUpstreamConnections);
        GC.KeepAlive(stream);
    }



    [Fact]
    public async Task TvSessions_EnterNewGen_CancelsStreamsOfPriorGen_Within500Ms()
    {
        TvStudioService.ReleaseAll();
        var inner = new MemoryStream(new byte[10000]);
        var resp = new HttpResponseMessage(HttpStatusCode.OK);
        var stream = TvStreamProxy.CreateOwnedStreamForTest(inner, resp, "g1_0");

        Assert.True(TvStreamProxy.ActiveUpstreamConnections > 0);

        // Al entrar la generación 2, la generación 1 se cancela en el servidor
        TvSessions.Enter(2);

        var sw = Stopwatch.StartNew();
        while (TvStreamProxy.ActiveUpstreamConnections > 0 && sw.ElapsedMilliseconds < 500)
        {
            await Task.Delay(10);
        }

        Assert.Equal(0, TvStreamProxy.ActiveUpstreamConnections);
        TvStudioService.ReleaseAll();
        GC.KeepAlive(stream);
    }

    [Fact]
    public async Task TvStreamProxy_StaleGen_Returns410WithoutUpstream()
    {
        TvStudioService.ReleaseAll();
        TvSessions.Enter(2);

        // Petición con g1_0 habiendo avanzado a gen 2 -> 410 stale sin abrir upstream
        var res = await TvStreamProxy.FetchAsync("https://example.com/live/ch1.m3u8", null, "g1_0", CancellationToken.None);

        Assert.Equal(410, res.StatusCode);
        Assert.Equal("stale", res.ExtraHeaders["X-Lex-Error-Class"]);
        Assert.Equal(0, TvStreamProxy.ActiveUpstreamConnections);
        TvStudioService.ReleaseAll();
    }

    [Fact]
    public async Task TvSessions_Rapid30Switches_NeverExceedsGenSlotCap_AndLeavesZero()
    {
        TvStudioService.ReleaseAll();
        var allStreams = new List<Stream>();

        for (int g = 1; g <= 30; g++)
        {
            TvSessions.Enter(g);
            for (int i = 0; i < 4; i++)
            {
                var ms = new MemoryStream(new byte[1024]);
                var resp = new HttpResponseMessage(HttpStatusCode.OK);
                allStreams.Add(TvStreamProxy.CreateOwnedStreamForTest(ms, resp, $"g{g}_{i}"));
            }
            var st = TvSessions.Enter(g);
            Assert.NotNull(st);
            Assert.InRange(st!.Open, 0, 6);
        }

        // Al entrar gen 31 o ReleaseAll, todo debe quedar en 0 en < 500 ms
        TvSessions.Enter(31);
        var sw = Stopwatch.StartNew();
        while (TvStreamProxy.ActiveUpstreamConnections > 0 && sw.ElapsedMilliseconds < 500)
        {
            await Task.Delay(10);
        }

        Assert.Equal(0, TvStreamProxy.ActiveUpstreamConnections);
        TvStudioService.ReleaseAll();
        GC.KeepAlive(allStreams);
    }

    [Fact]
    public void TvSessions_429OnlyWhenGenQuotaExceeded()
    {
        TvStudioService.ReleaseAll();
        var st = TvSessions.Enter(5);
        Assert.NotNull(st);

        // Agotar las 6 ranuras de la generación 5
        for (int i = 0; i < 6; i++)
        {
            Assert.True(st!.Slots.Wait(0));
        }

        // La 7ma debe fallar inmediatamente (429)
        Assert.False(st!.Slots.Wait(0));

        // Liberar una
        st.Slots.Release();
        Assert.True(st.Slots.Wait(0));

        TvStudioService.ReleaseAll();
    }

    [Fact]
    public void TvStudioService_ReleaseAll_LeavesZeroSlotsAndEmptySessions()
    {
        var ms = new MemoryStream(new byte[100]);
        var resp = new HttpResponseMessage(HttpStatusCode.OK);
        var stream = TvStreamProxy.CreateOwnedStreamForTest(ms, resp, "g9_0");

        Assert.True(TvStreamProxy.ActiveUpstreamConnections > 0);

        TvStudioService.ReleaseAll();

        string json = JsonSerializer.Serialize(TvStreamProxy.GetStats());
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("slotsInUse").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("gen").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("sessions").GetArrayLength());
        GC.KeepAlive(stream);
    }
}
