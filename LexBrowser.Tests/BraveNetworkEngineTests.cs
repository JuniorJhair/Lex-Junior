using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using LexBrowser.Core.AdBlock;
using LexBrowser.Core.AdBlock.Brave;
using Microsoft.Web.WebView2.Core;
using Xunit;

namespace LexBrowser.Tests;

public class BraveNetworkEngineTests
{
    private static EffectiveSitePolicy ActivePolicy(bool network = true, bool stubs = true, bool popups = true, bool ytAdapter = true) => new()
    {
        RegistrableDomain = "example.org",
        Mode = "Normal",
        ShieldEnabled = true,
        Network = network,
        Tracking = true,
        Stubs = stubs,
        ImaStub = stubs,
        Popups = popups,
        Overlays = true,
        Cosmetic = true,
        AntiWall = true,
        Subframes = false,
        VideoDefuser = false,
        YouTubeAdapter = ytAdapter,
        DailymotionAdapter = true
    };

    private sealed class SyntheticLexBuilder
    {
        private readonly Dictionary<ulong, uint> _pure = new();
        private readonly List<(uint Mask, string Host, string Pat, string[] IncDom, string[] ExcDom, string[] IncTo, string[] ExcTo, short Prio, string Mod)> _rules = new();

        public void AddPureHost(string host, BraveNetworkMask mask = BraveNetworkMask.FromAllTypes | BraveNetworkMask.PartyMask | BraveNetworkMask.FromSchemeMask)
        {
            _pure[BraveHash.Fnv1a64(host.ToLowerInvariant())] = (uint)mask;
        }

        public void AddPattern(
            BraveNetworkMask mask,
            string host = "",
            string pat = "",
            string[]? incDom = null,
            string[]? excDom = null,
            string[]? incTo = null,
            string[]? excTo = null,
            short prio = 0,
            string mod = "")
        {
            _rules.Add(((uint)mask, host.ToLowerInvariant(), pat.ToLowerInvariant(),
                incDom ?? Array.Empty<string>(),
                excDom ?? Array.Empty<string>(),
                incTo ?? Array.Empty<string>(),
                excTo ?? Array.Empty<string>(),
                prio,
                mod));
        }

        public BraveShieldData Build()
        {
            var blob = new List<byte>();
            var hashPool = new List<ulong>();
            var recs = new BravePatRec[_rules.Count];
            var fallback = new List<int>();

            int InternStr(string s, out int len)
            {
                if (string.IsNullOrEmpty(s)) { len = 0; return 0; }
                int off = blob.Count;
                byte[] bytes = Encoding.UTF8.GetBytes(s);
                blob.AddRange(bytes);
                len = bytes.Length;
                return off;
            }

            int InternHashes(string[] list, out ushort len)
            {
                if (list.Length == 0) { len = 0; return 0; }
                int off = hashPool.Count;
                foreach (var d in list)
                    hashPool.Add(BraveHash.Fnv1a64(d.ToLowerInvariant()));
                len = (ushort)list.Length;
                return off;
            }

            for (int i = 0; i < _rules.Count; i++)
            {
                var r = _rules[i];
                int hOff = InternStr(r.Host, out int hLen);
                int pOff = InternStr(r.Pat, out int pLen);
                int mOff = InternStr(r.Mod, out int mLen);
                int idOff = InternHashes(r.IncDom, out ushort idLen);
                int edOff = InternHashes(r.ExcDom, out ushort edLen);
                int itOff = InternHashes(r.IncTo, out ushort itLen);
                int etOff = InternHashes(r.ExcTo, out ushort etLen);

                recs[i] = new BravePatRec
                {
                    Mask = r.Mask,
                    HostOff = hOff,
                    HostLen = hLen,
                    PatOff = pOff,
                    PatLen = pLen,
                    ModOff = mOff,
                    ModLen = mLen,
                    IncDomOff = idOff,
                    ExcDomOff = edOff,
                    IncToOff = itOff,
                    ExcToOff = etOff,
                    IncDomLen = idLen,
                    ExcDomLen = edLen,
                    IncToLen = itLen,
                    ExcToLen = etLen,
                    RedirectPriority = r.Prio
                };
                fallback.Add(i);
            }

            var pureSorted = _pure.OrderBy(k => k.Key).ToArray();
            byte[] patBytes = MemoryMarshal.AsBytes(recs.AsSpan()).ToArray();
            byte[] pureHashesB = MemoryMarshal.AsBytes(pureSorted.Select(x => x.Key).ToArray().AsSpan()).ToArray();
            byte[] pureMasksB = MemoryMarshal.AsBytes(pureSorted.Select(x => x.Value).ToArray().AsSpan()).ToArray();
            byte[] hpB = MemoryMarshal.AsBytes(hashPool.ToArray().AsSpan()).ToArray();
            byte[] fbB = MemoryMarshal.AsBytes(fallback.ToArray().AsSpan()).ToArray();

            var sections = new byte[22][];
            sections[0] = Encoding.UTF8.GetBytes("{\"version\":2}");
            sections[1] = pureHashesB;
            sections[2] = pureMasksB;
            sections[3] = patBytes;
            sections[4] = blob.ToArray();
            sections[5] = hpB;
            for (int i = 6; i < 12; i++) sections[i] = Array.Empty<byte>();
            sections[12] = fbB;
            for (int i = 13; i < 22; i++) sections[i] = Array.Empty<byte>();

            using var ms = new MemoryStream();
            ms.Write(Encoding.ASCII.GetBytes("LEXSHLD2"));
            Span<byte> hdr = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(0, 4), 2);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(4, 4), 22);
            ms.Write(hdr);

            foreach (var sec in sections)
            {
                using var compMs = new MemoryStream();
                using (var z = new ZLibStream(compMs, CompressionLevel.Fastest, leaveOpen: true))
                    z.Write(sec);
                byte[] comp = compMs.ToArray();
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(0, 4), comp.Length);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(4, 4), sec.Length);
                ms.Write(hdr);
                ms.Write(comp);
            }

            ms.Position = 0;
            return BraveShieldData.LoadFromStream(ms);
        }
    }

    [Fact]
    public void PureHost_MatchesExactAndSubdomains_RejectsEvilSuffix()
    {
        var b = new SyntheticLexBuilder();
        b.AddPureHost("tracker.example");
        var engine = new BraveNetworkEngine(() => b.Build());
        var policy = ActivePolicy();

        Assert.True(engine.EvaluateNetwork(new Uri("https://tracker.example/p.js"), "news.org", policy, CoreWebView2WebResourceContext.Script).ShouldBlock);
        Assert.True(engine.EvaluateNetwork(new Uri("https://sub.a.tracker.example/p.js"), "news.org", policy, CoreWebView2WebResourceContext.Script).ShouldBlock);
        Assert.False(engine.EvaluateNetwork(new Uri("https://tracker.example.evil.com/p.js"), "news.org", policy, CoreWebView2WebResourceContext.Script).ShouldBlock);
    }

    [Fact]
    public void CheckParameterised_ImportantOverridesException_NormalYieldsToException()
    {
        var b = new SyntheticLexBuilder();
        // Normal block + exception -> allowed
        b.AddPureHost("norm.ad");
        b.AddPattern(
            BraveNetworkMask.IsException | BraveNetworkMask.IsHostnameAnchor | BraveNetworkMask.IsRightAnchor |
            BraveNetworkMask.FromAllTypes | BraveNetworkMask.PartyMask | BraveNetworkMask.FromSchemeMask,
            host: "norm.ad");

        // $important block + exception -> blocked!
        b.AddPattern(
            BraveNetworkMask.IsImportant | BraveNetworkMask.IsHostnameAnchor | BraveNetworkMask.IsRightAnchor |
            BraveNetworkMask.FromAllTypes | BraveNetworkMask.PartyMask | BraveNetworkMask.FromSchemeMask,
            host: "imp.ad");
        b.AddPattern(
            BraveNetworkMask.IsException | BraveNetworkMask.IsHostnameAnchor | BraveNetworkMask.IsRightAnchor |
            BraveNetworkMask.FromAllTypes | BraveNetworkMask.PartyMask | BraveNetworkMask.FromSchemeMask,
            host: "imp.ad");

        var engine = new BraveNetworkEngine(() => b.Build());
        var policy = ActivePolicy();

        var dNorm = engine.EvaluateNetwork(new Uri("https://imp.ad/x.js"), "site.org", policy, CoreWebView2WebResourceContext.Script);
        Assert.True(dNorm.ShouldBlock);
        Assert.True(dNorm.IsImportant);

        var dAllowed = engine.EvaluateNetwork(new Uri("https://norm.ad/x.js"), "site.org", policy, CoreWebView2WebResourceContext.Script);
        Assert.False(dAllowed.ShouldBlock);
    }

    [Fact]
    public void GenericHideException_DoesNotWhitelistNetworkAds_SetsGenericHideFlag()
    {
        var b = new SyntheticLexBuilder();
        b.AddPureHost("ads.site.com");
        b.AddPattern(
            BraveNetworkMask.IsException | BraveNetworkMask.IsGenericHide | BraveNetworkMask.IsHostnameAnchor |
            BraveNetworkMask.IsRightAnchor | BraveNetworkMask.FromNetworkTypes | BraveNetworkMask.PartyMask | BraveNetworkMask.FromSchemeMask,
            host: "site.com");

        var engine = new BraveNetworkEngine(() => b.Build());
        var policy = ActivePolicy();

        Assert.True(engine.IsGenericHideEnabled(new Uri("https://www.site.com/article"), "www.site.com"));
        Assert.False(engine.IsGenericHideEnabled(new Uri("https://other.org/article"), "other.org"));

        // Regla $generichide NUNCA debe desbloquear subrecursos de red
        Assert.True(engine.EvaluateNetwork(new Uri("https://ads.site.com/banner.js"), "www.site.com", policy, CoreWebView2WebResourceContext.Script).ShouldBlock);
    }

    [Fact]
    public void RedirectRuleOnly_And_RedirectException_DoNotChangeNetworkBlockOnOwn()
    {
        var b = new SyntheticLexBuilder();
        b.AddPattern(
            BraveNetworkMask.IsRedirect | BraveNetworkMask.IsHostnameAnchor | BraveNetworkMask.IsRightAnchor |
            BraveNetworkMask.FromScript | BraveNetworkMask.PartyMask | BraveNetworkMask.FromSchemeMask,
            host: "onlyrule.example", mod: "noopjs");
        b.AddPureHost("blocked.example");
        b.AddPattern(
            BraveNetworkMask.IsException | BraveNetworkMask.IsRedirect | BraveNetworkMask.IsHostnameAnchor |
            BraveNetworkMask.IsRightAnchor | BraveNetworkMask.FromScript | BraveNetworkMask.PartyMask | BraveNetworkMask.FromSchemeMask,
            host: "blocked.example", mod: "noopjs");

        var engine = new BraveNetworkEngine(() => b.Build());
        var policy = ActivePolicy();

        // $redirect-rule por sí sola no bloquea
        Assert.False(engine.EvaluateNetwork(new Uri("https://onlyrule.example/a.js"), "site.org", policy, CoreWebView2WebResourceContext.Script).ShouldBlock);
        // @@...$redirect no actúa como @@ de red general
        Assert.True(engine.EvaluateNetwork(new Uri("https://blocked.example/a.js"), "site.org", policy, CoreWebView2WebResourceContext.Script).ShouldBlock);
    }

    [Fact]
    public void Anchors_Wildcards_Separator_Methods_And_Domains_MatchSpec()
    {
        var b = new SyntheticLexBuilder();
        // ||ad*.cdn.net/b^?v=*
        b.AddPattern(
            BraveNetworkMask.IsHostnameAnchor | BraveNetworkMask.IsHostWildcard | BraveNetworkMask.IsLeftAnchor |
            BraveNetworkMask.IsRegex | BraveNetworkMask.FromScript | BraveNetworkMask.ThirdParty | BraveNetworkMask.FromSchemeMask,
            host: "ad", pat: "*.cdn.net/b^v=");

        // ||beacon.org^$method=post,domain=shop.*|~sub.shop.org,denyallow=ok.beacon.org
        b.AddPattern(
            BraveNetworkMask.IsHostnameAnchor | BraveNetworkMask.IsRightAnchor | BraveNetworkMask.FromPing |
            BraveNetworkMask.FromPost | BraveNetworkMask.ThirdParty | BraveNetworkMask.FromSchemeMask,
            host: "beacon.org",
            incDom: new[] { "shop.*" },
            excDom: new[] { "sub.shop.org" },
            excTo: new[] { "ok.beacon.org" });

        // |https://strict.org/px.gif|
        b.AddPattern(
            BraveNetworkMask.IsLeftAnchor | BraveNetworkMask.IsRightAnchor | BraveNetworkMask.FromImage |
            BraveNetworkMask.PartyMask | BraveNetworkMask.FromHttps,
            pat: "https://strict.org/px.gif");

        var engine = new BraveNetworkEngine(() => b.Build());
        var policy = ActivePolicy();

        Assert.True(engine.EvaluateNetwork(new Uri("https://ad9.cdn.net/b?v=1"), "host.org", policy, CoreWebView2WebResourceContext.Script).ShouldBlock);
        Assert.False(engine.EvaluateNetwork(new Uri("https://ad9.cdn.net/bxv=1"), "host.org", policy, CoreWebView2WebResourceContext.Script).ShouldBlock);

        Assert.True(engine.EvaluateNetwork(new Uri("https://beacon.org/log"), "shop.com", policy, CoreWebView2WebResourceContext.Ping, httpMethod: "POST").ShouldBlock);
        Assert.False(engine.EvaluateNetwork(new Uri("https://beacon.org/log"), "shop.com", policy, CoreWebView2WebResourceContext.Ping, httpMethod: "GET").ShouldBlock);
        Assert.False(engine.EvaluateNetwork(new Uri("https://beacon.org/log"), "sub.shop.org", policy, CoreWebView2WebResourceContext.Ping, httpMethod: "POST").ShouldBlock);
        Assert.False(engine.EvaluateNetwork(new Uri("https://ok.beacon.org/log"), "shop.org", policy, CoreWebView2WebResourceContext.Ping, httpMethod: "POST").ShouldBlock);

        Assert.True(engine.EvaluateNetwork(new Uri("https://strict.org/px.gif"), "host.org", policy, CoreWebView2WebResourceContext.Image).ShouldBlock);
        Assert.False(engine.EvaluateNetwork(new Uri("https://strict.org/px.gif?q=1"), "host.org", policy, CoreWebView2WebResourceContext.Image).ShouldBlock);
        Assert.False(engine.EvaluateNetwork(new Uri("http://strict.org/px.gif"), "host.org", policy, CoreWebView2WebResourceContext.Image).ShouldBlock);
    }

    [Fact]
    public void FetchMapsToXhr_And_SecFetchDestDistinguishesSubdocument()
    {
        var b = new SyntheticLexBuilder();
        b.AddPureHost("xhr.ad", BraveNetworkMask.FromXmlHttpRequest | BraveNetworkMask.PartyMask | BraveNetworkMask.FromSchemeMask);
        b.AddPureHost("frame.ad", BraveNetworkMask.FromSubdocument | BraveNetworkMask.PartyMask | BraveNetworkMask.FromSchemeMask);

        var engine = new BraveNetworkEngine(() => b.Build());
        var policy = ActivePolicy();

        Assert.True(engine.EvaluateNetwork(new Uri("https://xhr.ad/api"), "site.org", policy, CoreWebView2WebResourceContext.Fetch).ShouldBlock);
        Assert.True(engine.EvaluateNetwork(new Uri("https://frame.ad/embed"), "site.org", policy, CoreWebView2WebResourceContext.Document, secFetchDest: "iframe").ShouldBlock);
        Assert.False(engine.EvaluateNetwork(new Uri("https://frame.ad/embed"), "site.org", policy, CoreWebView2WebResourceContext.Document, secFetchDest: "document").ShouldBlock);
    }

    [Fact]
    public void PopupRules_EvaluateInShouldBlockPopup_NotSubresources()
    {
        var b = new SyntheticLexBuilder();
        b.AddPattern(
            BraveNetworkMask.IsHostnameAnchor | BraveNetworkMask.IsRightAnchor | BraveNetworkMask.FromPopup |
            BraveNetworkMask.ThirdParty | BraveNetworkMask.FromSchemeMask,
            host: "pop.evil");

        var engine = new BraveNetworkEngine(() => b.Build());
        var policy = ActivePolicy();

        Assert.False(engine.EvaluateNetwork(new Uri("https://pop.evil/open"), "anime.org", policy, CoreWebView2WebResourceContext.Script).ShouldBlock);
        Assert.True(engine.ShouldBlockPopup(new Uri("https://pop.evil/open"), new Uri("https://anime.org"), policy, false, true, out _));
        Assert.False(engine.ShouldBlockPopup(new Uri("https://pop.evil/open"), new Uri("https://pop.evil"), policy, false, true, out _));
    }

    [Fact]
    public void StrictIsolation_YouTube_Dailymotion_PaymentHosts_NeverBlockedByBrave()
    {
        var b = new SyntheticLexBuilder();
        b.AddPureHost("youtube.com");
        b.AddPureHost("googlevideo.com");
        b.AddPureHost("ytimg.com");
        b.AddPureHost("dailymotion.com");
        b.AddPureHost("dmcdn.net");
        b.AddPureHost("dmxleo.com");
        b.AddPureHost("stripe.com");
        b.AddPureHost("paypal.com");

        var engine = new BraveNetworkEngine(() => b.Build());
        var policy = ActivePolicy();

        Assert.False(engine.EvaluateNetwork(new Uri("https://www.youtube.com/watch?v=1"), "www.youtube.com", policy, CoreWebView2WebResourceContext.Script).ShouldBlock);
        Assert.False(engine.EvaluateNetwork(new Uri("https://rr1.googlevideo.com/videoplayback"), "www.youtube.com", policy, CoreWebView2WebResourceContext.Fetch).ShouldBlock);
        Assert.False(engine.EvaluateNetwork(new Uri("https://i.ytimg.com/vi/1/hqdefault.jpg"), "www.youtube.com", policy, CoreWebView2WebResourceContext.Image).ShouldBlock);
        Assert.False(engine.EvaluateNetwork(new Uri("https://www.dailymotion.com/player"), "www.dailymotion.com", policy, CoreWebView2WebResourceContext.Script).ShouldBlock);
        Assert.False(engine.EvaluateNetwork(new Uri("https://vod.dmcdn.net/seg.ts"), "www.dailymotion.com", policy, CoreWebView2WebResourceContext.Fetch).ShouldBlock);
        Assert.False(engine.EvaluateNetwork(new Uri("https://static.dmxleo.com/app.js"), "www.dailymotion.com", policy, CoreWebView2WebResourceContext.Script).ShouldBlock);
        Assert.False(engine.EvaluateNetwork(new Uri("https://js.stripe.com/v3/"), "shop.com", policy, CoreWebView2WebResourceContext.Script).ShouldBlock);
        Assert.False(engine.EvaluateNetwork(new Uri("https://www.paypal.com/sdk/js"), "shop.com", policy, CoreWebView2WebResourceContext.Script).ShouldBlock);
    }

    [Fact]
    public void ShieldRouter_RoutesYouTubeAndDailymotionToIsolatedLegacyEvenInBraveMode()
    {
        var b = new SyntheticLexBuilder();
        b.AddPureHost("custom-brave-only-block.com");
        ShieldRouter.SetBraveEngineForTests(new BraveNetworkEngine(() => b.Build()));
        try
        {
            var policy = ActivePolicy();

            // Motor Brave bloquea dominio general cuando engineOverride="Brave"
            Assert.True(ShieldRouter.EvaluateWebResource(
                new Uri("https://custom-brave-only-block.com/ad.js"),
                "news.org", "news.org", "news.org", "https://news.org", "",
                policy, CoreWebView2WebResourceContext.Script, engineOverride: ShieldSettings.EngineBrave).ShouldBlock);

            // Motor Legacy no bloquea custom-brave-only-block.com
            Assert.False(ShieldRouter.EvaluateWebResource(
                new Uri("https://custom-brave-only-block.com/ad.js"),
                "news.org", "news.org", "news.org", "https://news.org", "",
                policy, CoreWebView2WebResourceContext.Script, engineOverride: ShieldSettings.EngineLegacy).ShouldBlock);

            // Dailymotion usa DailymotionRules aislado incluso en modo Brave: canary + dmads bloqueado, dmcdn permitido
            var dmCanary = ShieldRouter.EvaluateWebResource(
                new Uri("https://www.dailymotion.com/nativeads.js"),
                "www.dailymotion.com", "dailymotion.com", "www.dailymotion.com", "https://www.dailymotion.com/video/x", "",
                policy, CoreWebView2WebResourceContext.Script, engineOverride: ShieldSettings.EngineBrave);
            Assert.True(dmCanary.IsRedirectStub);
            Assert.Contains("__NATIVEADS_CANARY__", Encoding.UTF8.GetString(dmCanary.RedirectBytes));

            Assert.True(ShieldRouter.EvaluateWebResource(
                new Uri("https://dmads.dailymotion.com/banner.js"),
                "www.dailymotion.com", "dailymotion.com", "www.dailymotion.com", "https://www.dailymotion.com/video/x", "",
                policy, CoreWebView2WebResourceContext.Script, engineOverride: ShieldSettings.EngineBrave).ShouldBlock);

            Assert.False(ShieldRouter.EvaluateWebResource(
                new Uri("https://vod3.dmcdn.net/manifest.m3u8"),
                "www.dailymotion.com", "dailymotion.com", "www.dailymotion.com", "https://www.dailymotion.com/video/x", "",
                policy, CoreWebView2WebResourceContext.Fetch, engineOverride: ShieldSettings.EngineBrave).ShouldBlock);

            // YouTube: si YouTubeAdapter=false, permite tráfico aunque engineOverride="Brave"
            var ytOffPolicy = ActivePolicy(ytAdapter: false);
            Assert.False(ShieldRouter.EvaluateWebResource(
                new Uri("https://www.youtube.com/youtubei/v1/player/ad_break"),
                "www.youtube.com", "youtube.com", "www.youtube.com", "https://www.youtube.com/watch?v=1", "",
                ytOffPolicy, CoreWebView2WebResourceContext.Fetch, engineOverride: ShieldSettings.EngineBrave).ShouldBlock);
        }
        finally
        {
            ShieldRouter.SetBraveEngineForTests(null);
        }
    }

    [Fact]
    public void RealCompiledBundle_LoadsCleanly_BlocksKnownTrackers_ProtectsSafeSites()
    {
        var data = BraveShieldData.TryGetDefault();
        Assert.NotNull(data);
        Assert.True(data!.PureHashes.Length > 95_000, $"Expected >95k pure hosts, got {data.PureHashes.Length}");
        Assert.True(data.PatternRules.Length > 20_000, $"Expected >20k pattern rules, got {data.PatternRules.Length}");

        var engine = new BraveNetworkEngine(() => data);
        var policy = ActivePolicy();

        Assert.True(engine.EvaluateNetwork(new Uri("https://securepubads.g.doubleclick.net/tag/js/gpt.js"), "news.example", policy, CoreWebView2WebResourceContext.Script).ShouldBlock);
        Assert.True(engine.EvaluateNetwork(new Uri("https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js"), "blog.example", policy, CoreWebView2WebResourceContext.Script).ShouldBlock);
        Assert.True(engine.EvaluateNetwork(new Uri("https://syndication.exoclick.com/splash.php"), "stream.example", policy, CoreWebView2WebResourceContext.Script).ShouldBlock);
        Assert.True(engine.EvaluateNetwork(new Uri("https://a.exoclick.com/ads.css"), "stream.example", policy, CoreWebView2WebResourceContext.Stylesheet).ShouldBlock);

        // Sitios legítimos y aislados permanecen limpios
        Assert.False(engine.EvaluateNetwork(new Uri("https://www.google.com/search?q=lex+junior"), "www.google.com", policy, CoreWebView2WebResourceContext.Document).ShouldBlock);
        Assert.False(engine.EvaluateNetwork(new Uri("https://en.wikipedia.org/wiki/Main_Page"), "en.wikipedia.org", policy, CoreWebView2WebResourceContext.Document).ShouldBlock);
        Assert.False(engine.EvaluateNetwork(new Uri("https://github.com/trending"), "github.com", policy, CoreWebView2WebResourceContext.Document).ShouldBlock);
        Assert.False(engine.EvaluateNetwork(new Uri("https://www.youtube.com/watch?v=dQw4w9WgXcQ"), "www.youtube.com", policy, CoreWebView2WebResourceContext.Document).ShouldBlock);
        Assert.False(engine.EvaluateNetwork(new Uri("https://www.dailymotion.com/video/x8abcde"), "www.dailymotion.com", policy, CoreWebView2WebResourceContext.Document).ShouldBlock);
    }
}
