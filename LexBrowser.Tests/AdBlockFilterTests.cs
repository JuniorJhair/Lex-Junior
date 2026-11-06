using LexBrowser.Core.AdBlock;
using Xunit;

namespace LexBrowser.Tests;

public class AdBlockFilterTests
{
    private static bool Blocked(string url) => AdBlockFilter.ShouldBlock(new Uri(url));
    private static bool Popup(string target, string? current) =>
        AdBlockFilter.IsSuspiciousPopup(new Uri(target), current == null ? null : new Uri(current));

    // ── Debe bloquear ─────────────────────────────────────────────────────
    [Theory]
    [InlineData("https://securepubads.g.doubleclick.net/gampad/ads?x=1")]
    [InlineData("https://doubleclick.net/")]
    [InlineData("https://a.b.c.googlesyndication.com/pagead/js/x.js")]
    [InlineData("https://www.youtube.com/api/stats/ads?ver=2")]
    [InlineData("https://www.youtube.com/pagead/adview?ai=1")]
    [InlineData("https://www.youtube.com/youtubei/v1/player/ad_break")]
    [InlineData("https://www.youtube.com/get_midroll_info?x=1")]
    [InlineData("https://onclickads.net/x")]
    [InlineData("https://ads.exoclick.com/x")]
    [InlineData("https://s.magsrv.com/splash.php?type=3&idzone=123")]
    [InlineData("https://a.realsrv.com/slider.php?zone=456")]
    [InlineData("https://ads.trafficjunky.net/video?zone_id=789")]
    [InlineData("https://engine.adglare.net/?456")]
    [InlineData("https://a.adtng.com/get/10016594?time=1769790006402")]
    [InlineData("https://a.pemsrv.com/ad-provider.js")]
    [InlineData("https://s.opoxv.com/v1/link.php")]
    [InlineData("https://shop.example.com/static/ads.js")]
    [InlineData("https://connect.facebook.net/en_US/fbevents.js")]
    [InlineData("https://www.facebook.net/tr?id=1")]
    public void Blocks_known_ad_and_tracking_urls(string url) => Assert.True(Blocked(url));

    // ── NO debe bloquear (falsos positivos detectados en la auditoría) ───
    [Theory]
    [InlineData("https://rr3---sn-abc.googlevideo.com/videoplayback?expire=1&id=o-AB&itag=18")]
    [InlineData("https://www.youtube.com/youtubei/v1/player?prettyPrint=false")]
    [InlineData("https://www.youtube.com/youtubei/v1/att/get")]
    [InlineData("https://www.youtube.com/youtubei/v1/log_event?alt=json")]
    [InlineData("https://accounts.google.com/o/oauth2/auth?client_id=1&onclick_redirect=1")]
    [InlineData("https://docs.example.com/guide/onclick-handlers.html")]
    [InlineData("https://github.com/user/repo/blob/main/vast.xml")]
    [InlineData("https://example.com/api?campaign_id=77")]
    [InlineData("https://i.ytimg.com/vi/abc/hqdefault.jpg")]
    [InlineData("https://fonts.gstatic.com/s/roboto/v30/x.woff2")]
    [InlineData("https://www.facebook.com/")]
    [InlineData("https://notdoubleclick.net.example.com/")]
    [InlineData("https://mydoubleclick.net/")]
    public void Does_not_block_legitimate_urls(string url) => Assert.False(Blocked(url));

    [Fact]
    public void Null_uri_is_not_blocked() => Assert.False(AdBlockFilter.ShouldBlock(null));

    // ── Popups ────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("https://accounts.google.com/signin?redirect_uri=x", "https://miweb.com/")]
    [InlineData("http://otro-sitio-normal.org/pagina", "https://miweb.com/")]
    [InlineData("https://es.wikipedia.org/wiki/X", "https://miweb.com/")]
    [InlineData("https://promo.tienda.com/oferta", "https://miweb.com/")]
    [InlineData("https://www.google.com/maps?q=lima&z=15", "https://miweb.com/")]
    [InlineData("https://login.live.com/oauth?redirect_uri=https%3A%2F%2Fapp", "https://miweb.com/")]
    [InlineData("https://miweb.com/login", "https://miweb.com/")]
    [InlineData("https://sub.miweb.com/x", "https://miweb.com/")]
    public void Legitimate_popups_are_allowed(string target, string current) => Assert.False(Popup(target, current));

    [Theory]
    [InlineData("https://securepubads.g.doubleclick.net/x", "https://miweb.com/")]
    [InlineData("https://trkr.example-track.com/click", "https://miweb.com/")]
    [InlineData("https://something.xyz/?zoneid=123", "https://miweb.com/")]
    [InlineData("https://win-prize.top/", "https://miweb.com/")]
    [InlineData("https://popunder-network.com/x", "https://miweb.com/")]
    [InlineData("https://unknown-host.net/go?url=https://other.com/landing", "https://miweb.com/")]
    public void Ad_popups_are_blocked(string target, string current) => Assert.True(Popup(target, current));

    // ── IsSameSiteOrDomain ────────────────────────────────────────────────
    [Theory]
    [InlineData("https://video.portal.com/stream123", "https://video.portal.com/", true)]
    [InlineData("https://sub.portal.com/tag", "https://portal.com/home", true)]
    [InlineData("https://media.server.org/media/ep1", "https://media.server.org/", true)]
    [InlineData("https://ad-delivery.net/click", "https://portal.com/", false)]
    [InlineData("https://magsrv.com/splash.php", "https://portal.com/", false)]
    public void Identifies_same_site_or_cross_domain(string urlA, string urlB, bool expected)
    {
        bool result = AdBlockFilter.IsSameSiteOrDomain(new Uri(urlA), new Uri(urlB));
        Assert.Equal(expected, result);
    }

    // ── AdBlockResources VAST stubs ────────────────────────────────────────
    [Theory]
    [InlineData("https://s.magsrv.com/splash.php?type=3&idzone=999")]
    [InlineData("https://syndication.exoclick.com/splash.php?type=3")]
    [InlineData("https://ads.trafficjunky.net/video?zone_id=123")]
    [InlineData("https://a.adtng.com/vast?id=10016594")]
    [InlineData("https://a.adtng.com/get/10016589?time=1769787459769")]
    [InlineData("https://example.com/api/ad?format=vast")]
    [InlineData("https://streaming.net/vast.xml")]
    public void VAST_requests_receive_200_ok_empty_vast_stub(string url)
    {
        bool isStub = AdBlockResources.TryGetRedirectStub(new Uri(url), out var bytes, out var contentType);
        Assert.True(isStub);
        Assert.Contains("text/xml", contentType);
        string xml = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Contains("<VAST", xml);
    }
}

