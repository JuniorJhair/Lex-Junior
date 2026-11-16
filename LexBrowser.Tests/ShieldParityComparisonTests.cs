using System;
using System.Collections.Generic;
using LexBrowser.Core;
using LexBrowser.Core.AdBlock;
using LexBrowser.Core.AdBlock.ShieldCore;
using Microsoft.Web.WebView2.Core;
using Xunit;

namespace LexBrowser.Tests;

[Collection("SettingsCollection")]
public sealed class ShieldParityComparisonTests
{
    private static EffectiveSitePolicy PolicyFor(string host, bool compat = false)
        => SitePolicy.Resolve(host, temporaryCompatMode: compat);

    [Fact]
    public void DefaultEngineIsLexShieldCoreAndNormalizeAlwaysResolvesUnifiedEngine()
    {
        var fresh = new ShieldSettings();
        Assert.Equal(ShieldSettings.EngineLexShield, fresh.Engine);
        Assert.Equal(ShieldSettings.CurrentShieldVersion, fresh.Version);
        Assert.Equal(ShieldSettings.EngineLexShield, ShieldSettings.NormalizeEngine(null));
        Assert.Equal(ShieldSettings.EngineLexShield, ShieldSettings.NormalizeEngine(ShieldSettings.LegacyEngineLegacyAlias));
        Assert.Equal(ShieldSettings.EngineLexShield, ShieldSettings.NormalizeEngine(ShieldSettings.LegacyEngineBraveAlias));
        Assert.Equal(ShieldSettings.EngineLexShield, ShieldRouter.GetConfiguredEngineName());
    }

    [Fact]
    public void DailymotionAdapter_MatchesYouTubeAdapterSymmetryWithCompatMode()
    {
        var dmNormal = PolicyFor("dailymotion.com", compat: false);
        var dmCompat = PolicyFor("dailymotion.com", compat: true);
        var ytNormal = PolicyFor("youtube.com", compat: false);
        var ytCompat = PolicyFor("youtube.com", compat: true);

        Assert.True(dmNormal.DailymotionAdapter);
        Assert.False(dmCompat.DailymotionAdapter);
        Assert.True(ytNormal.YouTubeAdapter);
        Assert.False(ytCompat.YouTubeAdapter);
    }

    [Fact]
    public void ShieldRouter_ShouldBlockUrl_EvaluatesUnifiedEngineAndPreservesIsolation()
    {
        var adUri = new Uri("https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js");
        var safeUri = new Uri("https://github.com/features");
        var policy = PolicyFor("news.example");

        var hardBlockUri = new Uri("https://stats.g.doubleclick.net/r/collect");
        Assert.True(ShieldRouter.ShouldBlockUrl(hardBlockUri, "news.example", policy, CoreWebView2WebResourceContext.XmlHttpRequest));
        Assert.False(ShieldRouter.ShouldBlockUrl(safeUri, "news.example", policy, CoreWebView2WebResourceContext.Document));

        // URL normal de página YT / DM jamás bloqueada
        var ytWatch = new Uri("https://www.youtube.com/watch?v=dQw4w9WgXcQ");
        var dmWatch = new Uri("https://www.dailymotion.com/video/x8abcde");
        Assert.False(ShieldRouter.ShouldBlockUrl(ytWatch, "youtube.com", PolicyFor("youtube.com"), CoreWebView2WebResourceContext.Document));
        Assert.False(ShieldRouter.ShouldBlockUrl(dmWatch, "dailymotion.com", PolicyFor("dailymotion.com"), CoreWebView2WebResourceContext.Document));
        _ = adUri;
    }

    [Fact]
    public void UnifiedMatrix_YouTubeDailymotionBigSitesAndAds()
    {
        var cases = new (Uri Uri, string Origin, CoreWebView2WebResourceContext Ctx, bool ExpectedBlock)[]
        {
            // YouTube aislado
            (new Uri("https://ads.youtube.com/api/stats"), "www.youtube.com", CoreWebView2WebResourceContext.XmlHttpRequest, true),
            (new Uri("https://www.youtube.com/pagead/viewthroughconversion/123"), "www.youtube.com", CoreWebView2WebResourceContext.XmlHttpRequest, true),
            (new Uri("https://www.youtube.com/watch?v=abc"), "www.youtube.com", CoreWebView2WebResourceContext.Document, false),

            // Dailymotion aislado (incluyendo canary nativeads.js stub)
            (new Uri("https://dmads.dailymotion.com/ad"), "www.dailymotion.com", CoreWebView2WebResourceContext.XmlHttpRequest, true),
            (new Uri("https://www.dailymotion.com/nativeads.js"), "www.dailymotion.com", CoreWebView2WebResourceContext.Script, true),
            (new Uri("https://www.dailymotion.com/embed/video/x999"), "animeyt.es", CoreWebView2WebResourceContext.Document, false),

            // BigSites y pagos: sin falsos positivos
            (new Uri("https://www.google.com/search?q=lex"), "www.google.com", CoreWebView2WebResourceContext.Document, false),
            (new Uri("https://github.com/login"), "github.com", CoreWebView2WebResourceContext.Document, false),
            (new Uri("https://accounts.google.com/o/oauth2/auth"), "news.example", CoreWebView2WebResourceContext.Document, false),
            (new Uri("https://www.paypal.com/checkoutnow"), "shop.example", CoreWebView2WebResourceContext.Document, false),

            // Ad networks conocidas: bloqueadas o redirigidas con stub
            (new Uri("https://securepubads.g.doubleclick.net/tag/js/gpt.js"), "news.example", CoreWebView2WebResourceContext.Script, true),
            (new Uri("https://www.google-analytics.com/analytics.js"), "news.example", CoreWebView2WebResourceContext.Script, true),
        };

        foreach (var (uri, origin, ctx, expectedBlock) in cases)
        {
            var policy = PolicyFor(origin);
            var decision = ShieldRouter.EvaluateWebResource(
                uri, origin, origin, origin, null, null, policy, ctx);

            Assert.True(decision.ShouldBlock == expectedBlock,
                $"LexShield Core fallo en {uri} ({ctx}): ShouldBlock={decision.ShouldBlock} ({decision.Reason}), esperado={expectedBlock}");
        }
    }

    [Fact]
    public void UnifiedMatrix_AnimeYtHentailaHaveScriptletsAndAllowMainDocument()
    {
        var data = LexShieldData.TryGetDefault();
        Assert.NotNull(data);
        var cos = data!.GetCosmeticEngine();
        var res = data.GetResourceStore();

        var animeCos = cos.EvaluateHost("animeyt.es", false, res);
        var hentailaCos = cos.EvaluateHost("hentaila.com", false, res);

        Assert.NotEmpty(animeCos.Scriptlets);
        Assert.NotEmpty(hentailaCos.Scriptlets);

        var animeDocPolicy = PolicyFor("animeyt.es");
        var animeNet = ShieldRouter.EvaluateWebResource(
            new Uri("https://animeyt.es/"), "animeyt.es", "animeyt.es", "animeyt.es", null, null,
            animeDocPolicy, CoreWebView2WebResourceContext.Document, secFetchDest: "document", httpMethod: "GET");
        Assert.False(animeNet.ShouldBlock);

        var hentailaNet = ShieldRouter.EvaluateWebResource(
            new Uri("https://hentaila.com/"), "hentaila.com", "hentaila.com", "hentaila.com", null, null,
            PolicyFor("hentaila.com"), CoreWebView2WebResourceContext.Document, secFetchDest: "document", httpMethod: "GET");
        Assert.False(hentailaNet.ShouldBlock);
    }
}
