using System;
using System.IO;
using System.Linq;
using LexBrowser.Core;
using LexBrowser.Core.AdBlock;
using LexBrowser.UI.Models;
using Microsoft.Web.WebView2.Core;
using Xunit;

namespace LexBrowser.Tests;

public class ShieldArchitectureV2Tests
{
    [Fact]
    public void ShieldExemptions_big_sites_are_single_source_of_truth_in_adblock_filter()
    {
        Assert.NotEmpty(ShieldExemptions.BigSites);
        Assert.Contains("miro.com", ShieldExemptions.BigSites);
        Assert.Contains("figma.com", ShieldExemptions.BigSites);
        Assert.Contains("canva.com", ShieldExemptions.BigSites);
        Assert.Contains("dailymotion.com", ShieldExemptions.BigSites);
        Assert.Contains("youtube.com", ShieldExemptions.BigSites);

        foreach (string site in ShieldExemptions.BigSites)
        {
            Assert.True(ShieldExemptions.IsBigSite(site), $"Expected IsBigSite({site}) == true");
            Assert.True(ShieldExemptions.IsBigSite($"app.{site}"), $"Expected IsBigSite(app.{site}) == true");
            Assert.True(AdBlockFilter.IsTrustedDomain(site), $"Expected IsTrustedDomain({site}) == true");
            Assert.True(AdBlockFilter.IsTrustedDomain($"cdn.{site}"), $"Expected IsTrustedDomain(cdn.{site}) == true");
        }

        Assert.False(ShieldExemptions.IsBigSite("evil-miro.com"));
        Assert.False(ShieldExemptions.IsBigSite("streamwish.to"));
    }

    [Fact]
    public void AdBlockFilter_exempts_miro_figma_canva_and_dailymotion_streams()
    {
        Assert.False(AdBlockFilter.ShouldBlock(new Uri("https://analytics.miro.com/pixel.js"), "miro.com", CoreWebView2WebResourceContext.Script));
        Assert.False(AdBlockFilter.ShouldBlock(new Uri("https://assets.figma.com/tracker-ui/bundle.js"), "figma.com", CoreWebView2WebResourceContext.Script));
        Assert.False(AdBlockFilter.ShouldBlock(new Uri("https://static.canva.com/beacon/v1/init.js"), "canva.com", CoreWebView2WebResourceContext.Script));

        Assert.False(AdBlockFilter.ShouldBlock(new Uri("https://graphql.api.dailymotion.com/"), "dailymotion.com", CoreWebView2WebResourceContext.XmlHttpRequest));
        Assert.False(AdBlockFilter.ShouldBlock(new Uri("https://proxy-01.dmcdn.net/video/123.m3u8"), "dailymotion.com", CoreWebView2WebResourceContext.XmlHttpRequest));
        Assert.True(AdBlockFilter.ShouldBlock(new Uri("https://dmads.dailymotion.com/ad/vast.xml"), "dailymotion.com", CoreWebView2WebResourceContext.XmlHttpRequest));
    }

    [Fact]
    public void AdBlockResources_exempts_big_sites_from_ima_stub_without_breaking_embed_origin()
    {
        var imaUri = new Uri("https://imasdk.googleapis.com/js/sdkloader/ima3.js");

        Assert.False(AdBlockResources.TryGetRedirectStub(imaUri, out _, out _, "miro.com", CoreWebView2WebResourceContext.Script));
        Assert.False(AdBlockResources.TryGetRedirectStub(imaUri, out _, out _, "youtube.com", CoreWebView2WebResourceContext.Script));
        Assert.False(AdBlockResources.TryGetRedirectStub(imaUri, out _, out _, "dailymotion.com", CoreWebView2WebResourceContext.Script));
        Assert.False(AdBlockResources.TryGetRedirectStub(imaUri, out _, out _, "figma.com", CoreWebView2WebResourceContext.Script));

        // El stub de IMA debe seguir protegiendo reproductores embebidos genéricos
        Assert.True(AdBlockResources.TryGetRedirectStub(imaUri, out byte[] stubBytes, out string mime, "animeflv.net", CoreWebView2WebResourceContext.Script));
        Assert.NotEmpty(stubBytes);
        Assert.Contains("javascript", mime, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShieldExemptions_distinguishes_payment_oauth_from_video_embeds()
    {
        Assert.True(ShieldExemptions.IsPaymentOrAuthHost("paypal.com"));
        Assert.True(ShieldExemptions.IsPaymentOrAuthHost("checkout.stripe.com"));
        Assert.True(ShieldExemptions.IsPaymentOrAuthHost("challenges.cloudflare.com"));
        Assert.True(ShieldExemptions.IsPaymentOrAuthHost("accounts.google.com"));

        Assert.False(ShieldExemptions.IsPaymentOrAuthHost("streamwish.to"));
        Assert.False(ShieldExemptions.IsPaymentOrAuthHost("vidhidepro.com"));

        Assert.True(ShieldExemptions.IsVideoEmbedUrl(new Uri("https://streamwish.to/e/abc123")));
        Assert.True(ShieldExemptions.IsVideoEmbedUrl(new Uri("https://vidhidefast.com/v/xyz")));
        Assert.True(ShieldExemptions.IsVideoEmbedUrl(new Uri("https://example-player.test/embed/99")));

        Assert.False(ShieldExemptions.IsVideoEmbedUrl(new Uri("https://paypal.com/checkoutnow")));
        Assert.False(ShieldExemptions.IsVideoEmbedUrl(new Uri("https://accounts.google.com/o/oauth2/auth")));
    }

    [Fact]
    public void BrowserTab_ResolveCachedPolicy_invalidates_on_temp_compat_and_settings_revision()
    {
        const string testDomain = "cache-v2-test.example";
        var sites = SettingsManager.Current.Shield.Sites;
        bool hadExisting = sites.TryGetValue(testDomain, out var previousProfile);

        try
        {
            sites.Remove(testDomain);
            SettingsManager.BumpSettingsRevision();

            var tab = new BrowserTab();
            var p1 = tab.ResolveCachedPolicy(testDomain);
            var p2 = tab.ResolveCachedPolicy(testDomain);

            Assert.True(p1.ShieldEnabled);
            Assert.False(p1.IsCompatMode);
            Assert.Equal(p1.Mode, p2.Mode);

            tab.TemporaryCompatMode = true;
            var pCompat = tab.ResolveCachedPolicy(testDomain);
            Assert.True(pCompat.IsCompatMode);
            Assert.Equal("Compat", pCompat.Mode);

            tab.TemporaryCompatMode = false;
            sites[testDomain] = new SiteShieldProfile { Mode = "Off" };
            SettingsManager.BumpSettingsRevision();

            var pOff = tab.ResolveCachedPolicy(testDomain);
            Assert.False(pOff.ShieldEnabled);
            Assert.Equal("Off", pOff.Mode);

            var otherHostPolicy = tab.ResolveCachedPolicy("other-site.example");
            Assert.True(otherHostPolicy.ShieldEnabled);
        }
        finally
        {
            if (hadExisting && previousProfile != null)
                sites[testDomain] = previousProfile;
            else
                sites.Remove(testDomain);

            SettingsManager.BumpSettingsRevision();
        }
    }

    [Fact]
    public void Injected_scripts_preserve_v2_architecture_and_safety_invariants()
    {
        // MediaRuntime bajo demanda (separado de YouTubeOptimizer)
        Assert.Contains("__lex_media_runtime_injected", MediaRuntime.InjectedScript);
        Assert.Contains("armMediaPrototypes", MediaRuntime.InjectedScript);
        Assert.Contains("armTitleObserver", MediaRuntime.InjectedScript);
        Assert.Contains("__lexSetVolumeBoostGlobal", MediaRuntime.InjectedScript);
        Assert.Contains("__lexEnterPiP", MediaRuntime.InjectedScript);
        Assert.Contains("__lexExitPiP", MediaRuntime.InjectedScript);

        // YouTubeOptimizer aislado y endurecido (Y1-Y6 + D3)
        Assert.Contains("attachYtStyle", YouTubeOptimizer.InjectedScript);
        Assert.Contains("readyState === 4", YouTubeOptimizer.InjectedScript);
        Assert.Contains("findSkipButton", YouTubeOptimizer.InjectedScript);
        Assert.Contains("hostname === 'youtube-nocookie.com'", YouTubeOptimizer.InjectedScript);
        Assert.Contains("hostname.endsWith('.youtube-nocookie.com')", YouTubeOptimizer.InjectedScript);
        Assert.DoesNotContain("'/youtubei/v1/next'", YouTubeOptimizer.InjectedScript);
        Assert.Contains("'/youtubei/v1/player'", YouTubeOptimizer.InjectedScript);
        Assert.DoesNotContain("(document.head || document.documentElement).appendChild(style)", YouTubeOptimizer.InjectedScript);

        // DailymotionOptimizer seguro en document_start y botones deshabilitados
        Assert.Contains("attachDmStyle", DailymotionOptimizer.InjectedScript);
        Assert.Contains("!btn.disabled", DailymotionOptimizer.InjectedScript);
        Assert.DoesNotContain("(document.head || document.documentElement).appendChild(style)", DailymotionOptimizer.InjectedScript);

        // WebShield sincroniza BigSites y video defuser dinámico
        string enabledScript = WebShield.GetInjectedScript(enableVideoDefuser: true);
        string disabledScript = WebShield.GetInjectedScript(enableVideoDefuser: false);
        Assert.Contains("\"miro.com\"", enabledScript);
        Assert.Contains("\"dailymotion.com\"", enabledScript);
        Assert.Contains("const __LEX_ENABLE_VIDEO_DEFUSER = true;", enabledScript);
        Assert.Contains("const __LEX_ENABLE_VIDEO_DEFUSER = false;", disabledScript);
        Assert.Contains("trySkipPreRoll", disabledScript);
        Assert.Contains("videoAdUiSkipButton", disabledScript);
    }

    [Fact]
    public void UpdateManager_ParseVersionTag_recognizes_v2_above_v1_2_26()
    {
        var v126 = UpdateManager.ParseVersionTag("v1.2.26");
        var v200 = UpdateManager.ParseVersionTag("v2.0.0");

        Assert.NotNull(v126);
        Assert.NotNull(v200);
        Assert.True(v200! > v126!);
        Assert.Equal(new Version(2, 0, 0), v200);
    }
}

