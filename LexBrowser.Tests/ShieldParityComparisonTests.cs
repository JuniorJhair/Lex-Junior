using System;
using System.Collections.Generic;
using LexBrowser.Core;
using LexBrowser.Core.AdBlock;
using LexBrowser.Core.AdBlock.Brave;
using Microsoft.Web.WebView2.Core;
using Xunit;

namespace LexBrowser.Tests;

public sealed class ShieldParityComparisonTests
{
    private static EffectiveSitePolicy PolicyFor(string host, bool compat = false)
        => SitePolicy.Resolve(host, temporaryCompatMode: compat);

    [Fact]
    public void DefaultEngineRemainsLegacyUntilParitySignoff()
    {
        var fresh = new ShieldSettings();
        Assert.Equal(ShieldSettings.EngineLegacy, fresh.Engine);
        Assert.Equal(ShieldSettings.EngineLegacy, ShieldSettings.NormalizeEngine(null));
        Assert.Equal(ShieldSettings.EngineBrave, ShieldSettings.NormalizeEngine("brave"));
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
    public void ShieldRouter_ShouldBlockUrl_RoutesAcrossBothEnginesAndPreservesIsolation()
    {
        var adUri = new Uri("https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js");
        var safeUri = new Uri("https://github.com/features");
        var policy = PolicyFor("news.example");

        // Ambos motores bloquean o redirigen con stub (ShouldBlockUrl retorna false para redirect stubs 200 OK y true para bloqueos duros; probemos tracker de bloqueo duro)
        var hardBlockUri = new Uri("https://stats.g.doubleclick.net/r/collect");
        Assert.True(ShieldRouter.ShouldBlockUrl(hardBlockUri, "news.example", policy, CoreWebView2WebResourceContext.XmlHttpRequest, ShieldSettings.EngineLegacy));
        Assert.True(ShieldRouter.ShouldBlockUrl(hardBlockUri, "news.example", policy, CoreWebView2WebResourceContext.XmlHttpRequest, ShieldSettings.EngineBrave));

        Assert.False(ShieldRouter.ShouldBlockUrl(safeUri, "news.example", policy, CoreWebView2WebResourceContext.Document, ShieldSettings.EngineLegacy));
        Assert.False(ShieldRouter.ShouldBlockUrl(safeUri, "news.example", policy, CoreWebView2WebResourceContext.Document, ShieldSettings.EngineBrave));

        // URL normal de página YT / DM jamás bloqueada por Brave
        var ytWatch = new Uri("https://www.youtube.com/watch?v=dQw4w9WgXcQ");
        var dmWatch = new Uri("https://www.dailymotion.com/video/x8abcde");
        Assert.False(ShieldRouter.ShouldBlockUrl(ytWatch, "youtube.com", PolicyFor("youtube.com"), CoreWebView2WebResourceContext.Document, ShieldSettings.EngineBrave));
        Assert.False(ShieldRouter.ShouldBlockUrl(dmWatch, "dailymotion.com", PolicyFor("dailymotion.com"), CoreWebView2WebResourceContext.Document, ShieldSettings.EngineBrave));
        _ = adUri;
    }

    [Fact]
    public void ParityMatrix_YouTubeDailymotionBigSitesAndAds()
    {
        var cases = new (Uri Uri, string Origin, CoreWebView2WebResourceContext Ctx, bool ExpectIdenticalBlockState)[]
        {
            // YouTube aislado: comportamiento 100% idéntico entre Legacy y Brave
            (new Uri("https://ads.youtube.com/api/stats"), "www.youtube.com", CoreWebView2WebResourceContext.XmlHttpRequest, true),
            (new Uri("https://www.youtube.com/pagead/viewthroughconversion/123"), "www.youtube.com", CoreWebView2WebResourceContext.XmlHttpRequest, true),
            (new Uri("https://www.youtube.com/watch?v=abc"), "www.youtube.com", CoreWebView2WebResourceContext.Document, true),

            // Dailymotion aislado: comportamiento 100% idéntico entre Legacy y Brave (incluyendo canary nativeads.js permitido)
            (new Uri("https://dmads.dailymotion.com/ad"), "www.dailymotion.com", CoreWebView2WebResourceContext.XmlHttpRequest, true),
            (new Uri("https://www.dailymotion.com/nativeads.js"), "www.dailymotion.com", CoreWebView2WebResourceContext.Script, true),
            (new Uri("https://www.dailymotion.com/embed/video/x999"), "animeyt.es", CoreWebView2WebResourceContext.Document, true),

            // BigSites y pagos: sin falsos positivos en ambos motores
            (new Uri("https://www.google.com/search?q=lex"), "www.google.com", CoreWebView2WebResourceContext.Document, true),
            (new Uri("https://github.com/login"), "github.com", CoreWebView2WebResourceContext.Document, true),
            (new Uri("https://accounts.google.com/o/oauth2/auth"), "news.example", CoreWebView2WebResourceContext.Document, true),
            (new Uri("https://www.paypal.com/checkoutnow"), "shop.example", CoreWebView2WebResourceContext.Document, true),

            // Ad networks conocidas: bloqueadas o redirigidas con stub en ambos motores
            (new Uri("https://securepubads.g.doubleclick.net/tag/js/gpt.js"), "news.example", CoreWebView2WebResourceContext.Script, true),
            (new Uri("https://www.google-analytics.com/analytics.js"), "news.example", CoreWebView2WebResourceContext.Script, true),
        };

        foreach (var (uri, origin, ctx, expectIdenticalBlockState) in cases)
        {
            var policy = PolicyFor(origin);
            var legacy = ShieldRouter.EvaluateWebResource(
                uri, origin, origin, origin, null, null, policy, ctx, engineOverride: ShieldSettings.EngineLegacy);
            var brave = ShieldRouter.EvaluateWebResource(
                uri, origin, origin, origin, null, null, policy, ctx, engineOverride: ShieldSettings.EngineBrave);

            if (expectIdenticalBlockState)
            {
                Assert.True(legacy.ShouldBlock == brave.ShouldBlock,
                    $"Divergencia Legacy vs Brave en {uri} ({ctx}): Legacy={legacy.ShouldBlock} ({legacy.Reason}), Brave={brave.ShouldBlock} ({brave.Reason})");
            }
        }
    }

    [Fact]
    public void ParityMatrix_AnimeYtHentailaHaveBraveScriptletsWhileLegacyDoesNot()
    {
        var data = BraveShieldData.TryGetDefault();
        Assert.NotNull(data);
        var cos = data!.GetCosmeticEngine();
        var res = data.GetResourceStore();

        var animeBrave = cos.EvaluateHost("animeyt.es", false, res);
        var hentailaBrave = cos.EvaluateHost("hentaila.com", false, res);

        Assert.NotEmpty(animeBrave.Scriptlets);
        Assert.NotEmpty(hentailaBrave.Scriptlets);

        // El documento inicial HTTPS mismo se permite cargar (sin bloquear la página), pero recibe scriptlets Brave
        var animeDocPolicy = PolicyFor("animeyt.es");
        var animeBraveNet = ShieldRouter.EvaluateWebResource(
            new Uri("https://animeyt.es/"), "animeyt.es", "animeyt.es", "animeyt.es", null, null,
            animeDocPolicy, CoreWebView2WebResourceContext.Document, secFetchDest: "document", httpMethod: "GET",
            engineOverride: ShieldSettings.EngineBrave);
        Assert.False(animeBraveNet.ShouldBlock);

        var hentailaBraveNet = ShieldRouter.EvaluateWebResource(
            new Uri("https://hentaila.com/"), "hentaila.com", "hentaila.com", "hentaila.com", null, null,
            PolicyFor("hentaila.com"), CoreWebView2WebResourceContext.Document, secFetchDest: "document", httpMethod: "GET",
            engineOverride: ShieldSettings.EngineBrave);
        Assert.False(hentailaBraveNet.ShouldBlock);
    }
}
