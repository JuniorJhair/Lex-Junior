using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LexBrowser.Core.AdBlock;
using LexBrowser.Core.AdBlock.Brave;
using Microsoft.Web.WebView2.Core;
using Xunit;

namespace LexBrowser.Tests;

public class BraveResourceStoreTests
{
    private static EffectiveSitePolicy ActivePolicy(bool stubs = true, bool imaStub = true, bool network = true) => new()
    {
        RegistrableDomain = "anime.example",
        Mode = stubs ? "Normal" : "Compat",
        ShieldEnabled = true,
        Network = network,
        Tracking = true,
        Stubs = stubs,
        ImaStub = imaStub,
        Popups = true,
        Overlays = true,
        Cosmetic = true,
        AntiWall = true,
        YouTubeAdapter = true,
        DailymotionAdapter = true
    };

    private sealed class SyntheticRedirectLexBuilder
    {
        private readonly Dictionary<ulong, uint> _pure = new();
        private readonly List<(uint Mask, string Host, string Pat, short Prio, string Mod)> _rules = new();
        private readonly List<(string Name, string[] Aliases, string Mime, bool IsTpl, int Perm, byte[] Bytes)> _resources = new();

        public void AddPureHost(string host)
            => _pure[BraveHash.Fnv1a64(host.ToLowerInvariant())] =
               (uint)(BraveNetworkMask.FromAllTypes | BraveNetworkMask.PartyMask | BraveNetworkMask.FromSchemeMask);

        public void AddRule(BraveNetworkMask mask, string host, string pat = "", short prio = 0, string mod = "")
            => _rules.Add(((uint)mask, host.ToLowerInvariant(), pat.ToLowerInvariant(), prio, mod));

        public void AddResource(string name, string mime, string contentUtf8, string[]? aliases = null, bool isTemplate = false, int perm = 0)
            => _resources.Add((name, aliases ?? Array.Empty<string>(), mime, isTemplate, perm, Encoding.UTF8.GetBytes(contentUtf8)));

        public BraveShieldData Build()
        {
            var blob = new List<byte>();
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

            for (int i = 0; i < _rules.Count; i++)
            {
                var r = _rules[i];
                recs[i] = new BravePatRec
                {
                    Mask = r.Mask,
                    HostOff = InternStr(r.Host, out int hLen),
                    HostLen = hLen,
                    PatOff = InternStr(r.Pat, out int pLen),
                    PatLen = pLen,
                    ModOff = InternStr(r.Mod, out int mLen),
                    ModLen = mLen,
                    RedirectPriority = r.Prio
                };
                fallback.Add(i);
            }

            var resBlob = new List<byte>();
            var resIndex = new List<object>();
            foreach (var r in _resources)
            {
                int off = resBlob.Count;
                resBlob.AddRange(r.Bytes);
                resIndex.Add(new
                {
                    n = r.Name,
                    a = r.Aliases,
                    m = r.Mime,
                    tpl = r.IsTpl,
                    d = Array.Empty<string>(),
                    p = r.Perm,
                    o = off,
                    l = r.Bytes.Length
                });
            }

            var pureSorted = _pure.OrderBy(k => k.Key).ToArray();
            var sections = new byte[22][];
            sections[0] = Encoding.UTF8.GetBytes("{\"version\":2}");
            sections[1] = MemoryMarshal.AsBytes(pureSorted.Select(x => x.Key).ToArray().AsSpan()).ToArray();
            sections[2] = MemoryMarshal.AsBytes(pureSorted.Select(x => x.Value).ToArray().AsSpan()).ToArray();
            sections[3] = MemoryMarshal.AsBytes(recs.AsSpan()).ToArray();
            sections[4] = blob.ToArray();
            for (int i = 5; i < 12; i++) sections[i] = Array.Empty<byte>();
            sections[12] = MemoryMarshal.AsBytes(fallback.ToArray().AsSpan()).ToArray();
            for (int i = 13; i < 20; i++) sections[i] = Array.Empty<byte>();
            sections[20] = JsonSerializer.SerializeToUtf8Bytes(resIndex);
            sections[21] = resBlob.ToArray();

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
    public void RealResourceStore_LoadsAll211Resources_And_BlocksTrustedByDefault()
    {
        var data = BraveShieldData.TryGetDefault();
        Assert.NotNull(data);

        var store = data!.GetResourceStore();
        Assert.Equal(211, store.Count);

        Assert.True(store.TryGetRedirectResource("noop.js", out var noopByName));
        Assert.True(store.TryGetRedirectResource("noopjs", out var noopByAlias));
        Assert.Same(noopByName, noopByAlias);
        Assert.Contains("application/javascript", noopByName!.ContentTypeHeader);

        Assert.True(store.TryGetRedirectResource("google-ima.js", out var ima));
        Assert.Contains("IMA", ima!.GetUtf8Text(), StringComparison.OrdinalIgnoreCase);

        Assert.True(store.TryGetRedirectResource("noop-vast3.xml", out var vast3));
        Assert.Contains("text/xml", vast3!.ContentTypeHeader);

        Assert.True(store.TryGetRedirectResource("noop-vmap1.xml", out var vmap1));
        Assert.Contains("text/xml", vmap1!.ContentTypeHeader);

        Assert.True(store.TryGetRedirectResource("1x1.gif", out var gif));
        Assert.Equal("Content-Type: image/gif", gif!.ContentTypeHeader);
        Assert.Equal((byte)'G', gif.Bytes[0]);

        // Todas las entradas de brave-resources.json tienen mime explícito; set-constant.js se resuelve como scriptlet JS (perm=0)
        Assert.True(store.TryGetResource("set-constant.js", out var setConstEntry));
        Assert.Contains("javascript", setConstEntry!.ContentTypeHeader);
        Assert.Equal(0, setConstEntry.Permission);

        // Si un recurso es plantilla (tpl=true), TryGetRedirectResource debe rechazarlo aunque perm == 0
        var tplBuilder = new SyntheticRedirectLexBuilder();
        tplBuilder.AddResource("my-tpl.js", "template", "// {{1}}", isTemplate: true, perm: 0);
        var tplStore = tplBuilder.Build().GetResourceStore();
        Assert.False(tplStore.TryGetRedirectResource("my-tpl.js", out _));
        Assert.True(tplStore.TryGetResource("my-tpl.js", out var tplLoaded));
        Assert.True(tplLoaded!.IsTemplate);

        // Recursos trusted-* requieren permiso explícito (Permission == 1 bloqueado por defecto)
        Assert.False(store.TryGetResource("trusted-set-cookie.js", out _, allowPrivileged: false));
        Assert.True(store.TryGetResource("trusted-set-cookie.js", out var privEntry, allowPrivileged: true));
        Assert.Equal(1, privEntry!.Permission);
    }

    [Fact]
    public void RedirectPriority_And_RedirectRuleOnly_And_Exceptions_ResolveAccurately()
    {
        var b = new SyntheticRedirectLexBuilder();
        b.AddResource("low-noop.js", "application/javascript", "// low", aliases: new[] { "lowjs" });
        b.AddResource("high-noop.js", "application/javascript", "// high", aliases: new[] { "highjs" });
        b.AddResource("trusted-hack.js", "application/javascript", "// trusted", perm: 1);

        var common = BraveNetworkMask.IsHostnameAnchor | BraveNetworkMask.IsRightAnchor |
                     BraveNetworkMask.FromScript | BraveNetworkMask.PartyMask | BraveNetworkMask.FromSchemeMask;

        // 1. Prioridad: 10 > 2
        b.AddRule(common | BraveNetworkMask.IsRedirect | BraveNetworkMask.IsAlsoBlockRedirect, "prio.ad", prio: 2, mod: "lowjs");
        b.AddRule(common | BraveNetworkMask.IsRedirect, "prio.ad", prio: 10, mod: "highjs");

        // 2. Cancelación por alias de redirect específico -> cae al de menor prioridad
        b.AddRule(common | BraveNetworkMask.IsRedirect | BraveNetworkMask.IsAlsoBlockRedirect, "cancelone.ad", prio: 1, mod: "lowjs");
        b.AddRule(common | BraveNetworkMask.IsRedirect, "cancelone.ad", prio: 10, mod: "highjs");
        b.AddRule(common | BraveNetworkMask.IsException | BraveNetworkMask.IsRedirect, "cancelone.ad", mod: "high-noop.js");

        // 3. Cancelación total @@...$redirect (mod="") -> bloquea sin devolver stub
        b.AddRule(common | BraveNetworkMask.IsRedirect | BraveNetworkMask.IsAlsoBlockRedirect, "cancelall.ad", prio: 5, mod: "highjs");
        b.AddRule(common | BraveNetworkMask.IsException | BraveNetworkMask.IsRedirect, "cancelall.ad", mod: "");

        // 4. $redirect-rule puro sin bloqueo -> Allow; con PureHost -> Redirect
        b.AddRule(common | BraveNetworkMask.IsRedirect, "unblocked-rule.ad", prio: 5, mod: "highjs");
        b.AddPureHost("blocked-rule.ad");
        b.AddRule(common | BraveNetworkMask.IsRedirect, "blocked-rule.ad", prio: 5, mod: "highjs");

        // 5. trusted-* nunca se sirve como stub, solo bloquea
        b.AddRule(common | BraveNetworkMask.IsRedirect | BraveNetworkMask.IsAlsoBlockRedirect, "priv.ad", prio: 20, mod: "trusted-hack.js");

        var engine = new BraveNetworkEngine(() => b.Build());
        var policy = ActivePolicy();

        var d1 = engine.EvaluateNetwork(new Uri("https://prio.ad/a.js"), "site.org", policy, CoreWebView2WebResourceContext.Script);
        Assert.True(d1.ShouldBlock);
        Assert.True(d1.IsRedirectStub);
        Assert.Equal("// high", Encoding.UTF8.GetString(d1.RedirectBytes));

        var d2 = engine.EvaluateNetwork(new Uri("https://cancelone.ad/a.js"), "site.org", policy, CoreWebView2WebResourceContext.Script);
        Assert.True(d2.ShouldBlock);
        Assert.True(d2.IsRedirectStub);
        Assert.Equal("// low", Encoding.UTF8.GetString(d2.RedirectBytes));

        var d3 = engine.EvaluateNetwork(new Uri("https://cancelall.ad/a.js"), "site.org", policy, CoreWebView2WebResourceContext.Script);
        Assert.True(d3.ShouldBlock);
        Assert.False(d3.IsRedirectStub);

        var d4 = engine.EvaluateNetwork(new Uri("https://unblocked-rule.ad/a.js"), "site.org", policy, CoreWebView2WebResourceContext.Script);
        Assert.False(d4.ShouldBlock);
        Assert.False(d4.IsRedirectStub);

        var d5 = engine.EvaluateNetwork(new Uri("https://blocked-rule.ad/a.js"), "site.org", policy, CoreWebView2WebResourceContext.Script);
        Assert.True(d5.ShouldBlock);
        Assert.True(d5.IsRedirectStub);
        Assert.Equal("// high", Encoding.UTF8.GetString(d5.RedirectBytes));

        var d6 = engine.EvaluateNetwork(new Uri("https://priv.ad/a.js"), "site.org", policy, CoreWebView2WebResourceContext.Script);
        Assert.True(d6.ShouldBlock);
        Assert.False(d6.IsRedirectStub);
    }

    [Fact]
    public void ImaStubToggle_StubsCompatToggle_And_BigSitesExemption_WorkCorrectly()
    {
        var b = new SyntheticRedirectLexBuilder();
        b.AddResource("google-ima.js", "application/javascript", "// ima stub");
        b.AddResource("noop-vast3.xml", "text/xml", "<VAST version=\"3.0\"/>");
        b.AddResource("noop.js", "application/javascript", "// noop stub");

        var mask = BraveNetworkMask.IsHostnameAnchor | BraveNetworkMask.IsRightAnchor |
                   BraveNetworkMask.FromNetworkTypes | BraveNetworkMask.PartyMask | BraveNetworkMask.FromSchemeMask |
                   BraveNetworkMask.IsRedirect | BraveNetworkMask.IsAlsoBlockRedirect;

        b.AddRule(mask, "imasdk.googleapis.com", mod: "google-ima.js");
        b.AddRule(mask, "vast.ads.example", mod: "noop-vast3.xml");
        b.AddRule(mask, "tracker.example", mod: "noop.js");

        var engine = new BraveNetworkEngine(() => b.Build());

        // Normal: sirve IMA y VAST en sitios de streaming
        var dImaNormal = engine.EvaluateNetwork(new Uri("https://imasdk.googleapis.com/js/sdkloader/ima3.js"), "animeyt.es", ActivePolicy(), CoreWebView2WebResourceContext.Script);
        Assert.True(dImaNormal.IsRedirectStub);
        Assert.Equal("// ima stub", Encoding.UTF8.GetString(dImaNormal.RedirectBytes));

        // ImaStub = false -> no sirve stub IMA (sí bloquea red)
        var dImaOff = engine.EvaluateNetwork(new Uri("https://imasdk.googleapis.com/js/sdkloader/ima3.js"), "animeyt.es", ActivePolicy(imaStub: false), CoreWebView2WebResourceContext.Script);
        Assert.True(dImaOff.ShouldBlock);
        Assert.False(dImaOff.IsRedirectStub);

        // Stubs = false (Compat) -> no sirve stubs
        var dCompat = engine.EvaluateNetwork(new Uri("https://tracker.example/x.js"), "animeyt.es", ActivePolicy(stubs: false), CoreWebView2WebResourceContext.Script);
        Assert.True(dCompat.ShouldBlock);
        Assert.False(dCompat.IsRedirectStub);

        // BigSite (vimeo.com) -> exento de IMA/VAST shims, pero sí sirve noop.js normal
        var dBigIma = engine.EvaluateNetwork(new Uri("https://imasdk.googleapis.com/js/sdkloader/ima3.js"), "vimeo.com", ActivePolicy(), CoreWebView2WebResourceContext.Script);
        Assert.False(dBigIma.IsRedirectStub);
        var dBigVast = engine.EvaluateNetwork(new Uri("https://vast.ads.example/vast.xml"), "vimeo.com", ActivePolicy(), CoreWebView2WebResourceContext.XmlHttpRequest);
        Assert.False(dBigVast.IsRedirectStub);
        var dBigNoop = engine.EvaluateNetwork(new Uri("https://tracker.example/x.js"), "vimeo.com", ActivePolicy(), CoreWebView2WebResourceContext.Script);
        Assert.True(dBigNoop.IsRedirectStub);
    }

    [Fact]
    public void RealCompiledBundle_ServesRedirectsForGoogleAnalyticsAndIma()
    {
        var data = BraveShieldData.TryGetDefault();
        Assert.NotNull(data);
        var engine = new BraveNetworkEngine(() => data);
        var policy = ActivePolicy();

        var ga = engine.EvaluateNetwork(
            new Uri("https://www.google-analytics.com/analytics.js"),
            "anime.example",
            policy,
            CoreWebView2WebResourceContext.Script);
        Assert.True(ga.ShouldBlock);
        Assert.True(ga.IsRedirectStub);
        Assert.Contains("javascript", ga.RedirectContentType);

        // En Brave, google-ima.js aplica en dominios declarados como viki.com; en otros sitios solo bloquea red si aplica
        var imaViki = engine.EvaluateNetwork(
            new Uri("https://imasdk.googleapis.com/js/sdkloader/ima3.js"),
            "viki.com",
            policy,
            CoreWebView2WebResourceContext.Script);
        Assert.True(imaViki.ShouldBlock);
        Assert.True(imaViki.IsRedirectStub);
        Assert.Contains("IMA", Encoding.UTF8.GetString(imaViki.RedirectBytes), StringComparison.OrdinalIgnoreCase);

        // Si ImaStub = false en viki.com -> no sirve stub IMA pero mantiene el bloqueo de red
        var imaVikiDisabled = engine.EvaluateNetwork(
            new Uri("https://imasdk.googleapis.com/js/sdkloader/ima3.js"),
            "viki.com",
            ActivePolicy(imaStub: false),
            CoreWebView2WebResourceContext.Script);
        Assert.True(imaVikiDisabled.ShouldBlock);
        Assert.False(imaVikiDisabled.IsRedirectStub);
    }
}
