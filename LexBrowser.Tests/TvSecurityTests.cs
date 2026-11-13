using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LexBrowser.Core.Tv;
using Xunit;

namespace LexBrowser.Tests;

public class TvSecurityTests
{
    [Theory]
    [InlineData("192.0.0.1", true)]        // IETF Protocol Assignments (192.0.0.0/24)
    [InlineData("192.0.2.1", true)]        // TEST-NET-1 (192.0.2.0/24)
    [InlineData("198.18.0.5", true)]       // Benchmarking (198.18.0.0/15)
    [InlineData("198.19.255.1", true)]     // Benchmarking (198.18.0.0/15)
    [InlineData("198.51.100.1", true)]     // TEST-NET-2 (198.51.100.0/24)
    [InlineData("203.0.113.1", true)]      // TEST-NET-3 (203.0.113.0/24)
    [InlineData("127.0.0.1", true)]        // Loopback
    [InlineData("10.1.2.3", true)]         // RFC 1918 Private
    [InlineData("172.16.0.1", true)]       // RFC 1918 Private
    [InlineData("192.168.1.1", true)]      // RFC 1918 Private
    [InlineData("169.254.1.1", true)]      // Link-Local
    [InlineData("100.64.0.1", true)]       // CGNAT
    [InlineData("224.0.0.1", true)]        // Multicast
    [InlineData("8.8.8.8", false)]         // Public DNS
    [InlineData("1.1.1.1", false)]         // Public DNS
    [InlineData("93.184.216.34", false)]   // example.com
    public void TvUrlGuard_IsPrivateIp_IPv4Ranges(string ipStr, bool expectedPrivate)
    {
        var ip = IPAddress.Parse(ipStr);
        Assert.Equal(expectedPrivate, TvUrlGuard.IsPrivateIp(ip));
    }

    [Theory]
    [InlineData("::1", true)]                       // Loopback
    [InlineData("::", true)]                        // Any
    [InlineData("fe80::1", true)]                   // Link-local
    [InlineData("fc00::1", true)]                   // Unique Local
    [InlineData("fd12:3456:789a::1", true)]         // Unique Local
    [InlineData("ff02::1", true)]                   // Multicast
    [InlineData("64:ff9b::192.0.2.1", true)]        // 64:ff9b::/96
    [InlineData("2002:c000:0201::", true)]          // 2002::/16 (6to4)
    [InlineData("2001::1", true)]                   // 2001::/32 (Teredo)
    [InlineData("2001:db8::1", true)]               // 2001:db8::/32 (Doc)
    [InlineData("2606:4700:4700::1111", false)]     // Cloudflare DNS Public
    [InlineData("2001:4860:4860::8888", false)]     // Google DNS Public
    public void TvUrlGuard_IsPrivateIp_IPv6Ranges(string ipStr, bool expectedPrivate)
    {
        var ip = IPAddress.Parse(ipStr);
        Assert.Equal(expectedPrivate, TvUrlGuard.IsPrivateIp(ip));
    }

    [Fact]
    public void TvUrlGuard_IsPrivateIp_IPv4MappedToIPv6_HandledAsIPv4()
    {
        var mappedPrivate = IPAddress.Parse("127.0.0.1").MapToIPv6();
        Assert.True(TvUrlGuard.IsPrivateIp(mappedPrivate));

        var mappedPublic = IPAddress.Parse("8.8.8.8").MapToIPv6();
        Assert.False(TvUrlGuard.IsPrivateIp(mappedPublic));
    }

    [Fact]
    public void TvStreamProxy_HasNonNoneEncryption_DetectsEncryptedStreams()
    {
        string aes128 = "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"\n#EXTINF:4.0,\nseg1.ts";
        Assert.True(TvStreamProxy.HasNonNoneEncryption(aes128));

        string sampleAes = "#EXTM3U\n#EXT-X-SESSION-KEY:METHOD=SAMPLE-AES,URI=\"key.bin\"\n#EXTINF:4.0,\nseg1.ts";
        Assert.True(TvStreamProxy.HasNonNoneEncryption(sampleAes));

        string implicitAes = "#EXTM3U\n#EXT-X-KEY:URI=\"key.bin\"\n#EXTINF:4.0,\nseg1.ts";
        Assert.True(TvStreamProxy.HasNonNoneEncryption(implicitAes));

        string none = "#EXTM3U\n#EXT-X-KEY:METHOD=NONE\n#EXTINF:4.0,\nseg1.ts";
        Assert.False(TvStreamProxy.HasNonNoneEncryption(none));

        string unencrypted = "#EXTM3U\n#EXTINF:4.0,\nseg1.ts";
        Assert.False(TvStreamProxy.HasNonNoneEncryption(unencrypted));
    }

    [Fact]
    public void TvStudioService_HtmlContentSecurityPolicy_IsStrictAndUnified()
    {
        string csp = TvStudioService.HtmlContentSecurityPolicy;
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("script-src 'self'", csp);
        Assert.Contains("style-src 'self'", csp);
        Assert.Contains("img-src 'self' data: blob:", csp);
        Assert.Contains("media-src 'self' blob:", csp);
        Assert.Contains("connect-src 'self'", csp);
        Assert.Contains("object-src 'none'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.DoesNotContain("https:", csp);
        Assert.DoesNotContain("iptv-org", csp);
        Assert.DoesNotContain("biskyt", csp);
    }

    [Theory]
    [InlineData("https://tv.lex.local", null, true)]
    [InlineData(null, "https://tv.lex.local/index.html", true)]
    [InlineData("https://tv.lex.local", "https://tv.lex.local/index.html", true)]
    [InlineData("https://evil.com", null, false)]
    [InlineData(null, "https://evil.com/page.html", false)]
    [InlineData(null, null, false)]
    public void TvStudioService_IsValidTvOriginOrReferer_OnlyAllowsInternalTvOrigin(string? origin, string? referer, bool expected)
    {
        Assert.Equal(expected, TvStudioService.IsValidTvOriginOrReferer(origin, referer));
    }

    [Fact]
    public void TvDenylist_BlocksConfiguredHostsAndUrls()
    {
        TvDenylist.ClearForTesting();
        try
        {
            Assert.False(TvDenylist.IsDenylisted("https://safe.example.com/live.m3u8", out _));

            TvDenylist.AddForTesting("pirate-stream.com");
            Assert.True(TvDenylist.IsDenylisted("https://pirate-stream.com/live/ch1.m3u8", out var reason1));
            Assert.Equal(TvDenylist.DefaultDenyMessage, reason1);

            TvDenylist.AddForTesting("https://specific-bad.org/stream.m3u8");
            Assert.True(TvDenylist.IsDenylisted("https://specific-bad.org/stream.m3u8", out var reason2));
            Assert.Equal(TvDenylist.DefaultDenyMessage, reason2);

            Assert.False(TvUrlGuard.IsAllowedRemoteUrl("https://pirate-stream.com/live.m3u8", out _));
        }
        finally
        {
            TvDenylist.ClearForTesting();
        }
    }

    [Fact]
    public void M3uParser_DoesNotPropagateThirdPartyHeaders()
    {
        string m3u = """
            #EXTM3U
            #EXTVLCOPT:http-referrer=https://forbidden-referrer.com
            #EXTVLCOPT:http-user-agent=CustomHackerAgent/1.0
            #EXTINF:-1 tvg-id="ch1" group-title="News",Canal Test
            https://stream.example.com/live.m3u8|Referer=https://kodi-pipe.com&User-Agent=KodiAgent
            """;

        var result = M3uParser.ParseDetailed(m3u, "UserList");
        Assert.Single(result.Channels);
        var ch = result.Channels[0];
        Assert.Equal("Canal Test", ch.Name);
        Assert.Equal("https://stream.example.com/live.m3u8", ch.Url);
    }

    [Fact]
    public void TvUserStateStore_TermsAcceptance_IsPersisted()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "lex_terms_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TvUserStateStore(tempDir);
            Assert.False(store.HasAcceptedTerms(1));

            store.AcceptTerms(1);
            Assert.True(store.HasAcceptedTerms(1));

            var reloaded = new TvUserStateStore(tempDir);
            Assert.True(reloaded.HasAcceptedTerms(1));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void LexTvLegalInfo_ContainsRequiredContactDetails()
    {
        Assert.Equal("Junior Pacherrez", LexTvLegalInfo.ResponsibleName);
        Assert.Equal("juniorpacherrez.31@gmail.com", LexTvLegalInfo.ContactEmail);
        Assert.False(string.IsNullOrWhiteSpace(LexTvLegalInfo.LegalDisclaimer));
    }
}
