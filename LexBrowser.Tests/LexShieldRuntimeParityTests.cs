using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using LexBrowser.Core;
using LexBrowser.Core.AdBlock;
using LexBrowser.Core.AdBlock.ShieldCore;
using LexBrowser.UI.Models;
using Microsoft.Web.WebView2.Core;
using Xunit;

namespace LexBrowser.Tests;

[Collection("SettingsCollection")]
public sealed class LexShieldRuntimeParityTests
{
    private static EffectiveSitePolicy NormalPolicy(string host = "example.com")
        => SitePolicy.Resolve(host, temporaryCompatMode: false);

    private static (LexShieldData Data, LexShieldCoreEngine Engine) BuildSyntheticData(
        (uint Mask, string Host, string Pat, string Mod, string[]? IncDom, string[]? ExcDom)[]? cspDefs = null,
        (uint Mask, string Host, string Pat, string Mod, string[]? IncDom, string[]? ExcDom)[]? regexDefs = null)
    {
        var blob = new List<byte>();
        var pool = new List<ulong>();

        (int Off, int Len) AddStr(string s)
        {
            if (string.IsNullOrEmpty(s))
                return (0, 0);
            int off = blob.Count;
            byte[] b = Encoding.UTF8.GetBytes(s);
            blob.AddRange(b);
            return (off, b.Length);
        }

        (int Off, ushort Len) AddDoms(string[]? doms)
        {
            if (doms == null || doms.Length == 0)
                return (0, 0);
            int off = pool.Count;
            foreach (string d in doms)
                pool.Add(LexShieldHash.Fnv1a64(d.ToLowerInvariant().AsSpan()));
            return (off, (ushort)doms.Length);
        }

        LexShieldPatRec[] BuildRecs((uint Mask, string Host, string Pat, string Mod, string[]? IncDom, string[]? ExcDom)[]? defs)
        {
            if (defs == null || defs.Length == 0)
                return Array.Empty<LexShieldPatRec>();
            var arr = new LexShieldPatRec[defs.Length];
            for (int i = 0; i < defs.Length; i++)
            {
                var d = defs[i];
                var (hOff, hLen) = AddStr(d.Host);
                var (pOff, pLen) = AddStr(d.Pat);
                var (mOff, mLen) = AddStr(d.Mod);
                var (idOff, idLen) = AddDoms(d.IncDom);
                var (edOff, edLen) = AddDoms(d.ExcDom);
                arr[i] = new LexShieldPatRec
                {
                    Mask = d.Mask,
                    HostOff = hOff,
                    HostLen = hLen,
                    PatOff = pOff,
                    PatLen = pLen,
                    ModOff = mOff,
                    ModLen = mLen,
                    IncDomOff = idOff,
                    IncDomLen = idLen,
                    ExcDomOff = edOff,
                    ExcDomLen = edLen
                };
            }
            return arr;
        }

        var cspRecs = BuildRecs(cspDefs);
        var rgxRecs = BuildRecs(regexDefs);
        var data = LexShieldData.CreateForTests(
            strBlob: blob.ToArray(),
            hashPool: pool.ToArray(),
            cspRules: cspRecs,
            regexRules: rgxRecs);
        return (data, new LexShieldCoreEngine(() => data));
    }

    [Fact]
    public void RealBraveLex_CompilesAllRegexAndCspRegexRulesWithoutError()
    {
        var data = LexShieldData.TryGetDefault();
        Assert.NotNull(data);

        LexShieldPatRec[] regexRules = data!.GetRegexRules();
        Assert.NotEmpty(regexRules);

        for (int i = 0; i < regexRules.Length; i++)
        {
            ref readonly var rec = ref regexRules[i];
            string pat = rec.PatLen > 0 ? Encoding.UTF8.GetString(data.StrBlob, rec.PatOff, rec.PatLen) : string.Empty;
            bool matchCase = (rec.Mask & (uint)LexShieldMask.IsMatchCase) != 0;
            Assert.NotNull(LexShieldData.TryCompileRegex(pat, matchCase));
        }

        LexShieldPatRec[] cspRules = data.GetCspRules();
        Assert.NotEmpty(cspRules);

        for (int i = 0; i < cspRules.Length; i++)
        {
            ref readonly var rec = ref cspRules[i];
            if ((rec.Mask & (uint)LexShieldMask.IsFullRegex) != 0)
            {
                string pat = rec.PatLen > 0 ? Encoding.UTF8.GetString(data.StrBlob, rec.PatOff, rec.PatLen) : string.Empty;
                bool matchCase = (rec.Mask & (uint)LexShieldMask.IsMatchCase) != 0;
                Assert.NotNull(LexShieldData.TryCompileRegex(pat, matchCase));
            }
        }
    }

    [Fact]
    public void RealBraveLex_IncludesEasyListSpanishRulesForHentailaAndAnimeyt()
    {
        var data = LexShieldData.TryGetDefault();
        Assert.NotNull(data);
        var cos = data!.GetCosmeticEngine();
        var res = data.GetResourceStore();

        var hentaila = cos.EvaluateHost("hentaila.com", genericHide: false, res);
        Assert.Contains("#onhsl", hentaila.HideSelectors);
        Assert.Contains(hentaila.Scriptlets, s => s.Contains("popMagic", StringComparison.OrdinalIgnoreCase));

        // Reglas de EasyList Spanish para animeyt.es: regex de dominio 3p (>=8 alnum + .com) y ||truepush.com^$third-party
        var engine = new LexShieldCoreEngine(() => data);
        var policy = NormalPolicy("animeyt.es");
        var animeRegexBlock = engine.EvaluateNetwork(
            new Uri("https://abcdefgh12.com/pop.js"),
            "animeyt.es",
            policy,
            CoreWebView2WebResourceContext.Script);
        Assert.True(animeRegexBlock.ShouldBlock);

        var animeMediaSkip = engine.EvaluateNetwork(
            new Uri("https://abcdefgh12.com/video.mp4"),
            "animeyt.es",
            policy,
            CoreWebView2WebResourceContext.Media);
        Assert.False(animeMediaSkip.ShouldBlock);

        var truePushBlock = engine.EvaluateNetwork(
            new Uri("https://sdks.truepush.com/sdk/v2.0.4/app.js"),
            "animeyt.es",
            policy,
            CoreWebView2WebResourceContext.Script);
        Assert.True(truePushBlock.ShouldBlock);
    }

    [Fact]
    public void CspRules_MergeExceptionsImportantAndSandboxPopupEnforcement()
    {
        uint baseDoc = (uint)(LexShieldMask.FromDocument | LexShieldMask.FromSubdocument |
                              LexShieldMask.FirstParty | LexShieldMask.ThirdParty |
                              LexShieldMask.FromHttp | LexShieldMask.FromHttps |
                              LexShieldMask.IsHostnameAnchor | LexShieldMask.IsRightAnchor);

        var (data, engine) = BuildSyntheticData(cspDefs: new[]
        {
            (baseDoc, "stream.example", "", "script-src 'self'", (string[]?)null, (string[]?)null),
            (baseDoc, "stream.example", "", "sandbox allow-forms allow-same-origin allow-scripts", (string[]?)null, (string[]?)null),
            (baseDoc | (uint)LexShieldMask.IsException, "stream.example", "", "script-src 'self'", (string[]?)null, (string[]?)null),
            (baseDoc | (uint)LexShieldMask.IsImportant, "stream.example", "", "worker-src 'none'", (string[]?)null, (string[]?)null),
            (baseDoc | (uint)LexShieldMask.IsException, "allowall.example", "", "", (string[]?)null, (string[]?)null),
            (baseDoc, "allowall.example", "", "script-src 'none'", (string[]?)null, (string[]?)null),
            (baseDoc | (uint)LexShieldMask.IsImportant, "allowall.example", "", "worker-src 'none'", (string[]?)null, (string[]?)null),
        });

        var streamPolicy = NormalPolicy("stream.example");
        var streamCsp = engine.EvaluateCspPolicies(
            new Uri("https://stream.example/watch/1"),
            "stream.example",
            isSubdocument: false,
            streamPolicy,
            out bool sandboxNoPopups);

        Assert.True(sandboxNoPopups);
        Assert.Contains("worker-src 'none'", streamCsp);
        Assert.Contains("sandbox allow-forms allow-same-origin allow-scripts", streamCsp);
        Assert.DoesNotContain("script-src 'self'", streamCsp);

        // Con $csp=sandbox sin allow-popups, hasta un click del usuario que abre popup se bloquea
        bool blockedPopup = engine.ShouldBlockPopup(
            new Uri("https://unknown-ad-pop.example/go"),
            new Uri("https://stream.example/watch/1"),
            streamPolicy,
            isFromSubframe: false,
            isUserInitiated: true,
            out string popupReason);
        Assert.True(blockedPopup);
        Assert.Contains("sandbox", popupReason, StringComparison.OrdinalIgnoreCase);

        // Excepción comodín @@||allowall.example^$csp cancela reglas normales pero no $important
        var allowCsp = engine.EvaluateCspPolicies(
            new Uri("https://allowall.example/"),
            "allowall.example",
            isSubdocument: false,
            NormalPolicy("allowall.example"),
            out bool allowSandbox);
        Assert.False(allowSandbox);
        Assert.Single(allowCsp);
        Assert.Equal("worker-src 'none'", allowCsp[0]);
        _ = data;
    }

    [Fact]
    public void FullRegexRules_RespectMatchCasePrefilterAndPopupSafeFallback()
    {
        uint netScript = (uint)(LexShieldMask.FromScript | LexShieldMask.IsFullRegex |
                                LexShieldMask.FirstParty | LexShieldMask.ThirdParty |
                                LexShieldMask.FromHttp | LexShieldMask.FromHttps);
        uint netPopup = (uint)(LexShieldMask.FromPopup | LexShieldMask.IsFullRegex |
                               LexShieldMask.FirstParty | LexShieldMask.ThirdParty |
                               LexShieldMask.FromHttp | LexShieldMask.FromHttps);
        uint docExc = (uint)(LexShieldMask.FromDocument | LexShieldMask.FromPopup |
                             LexShieldMask.IsException | LexShieldMask.IsFullRegex |
                             LexShieldMask.FirstParty | LexShieldMask.ThirdParty |
                             LexShieldMask.FromHttp | LexShieldMask.FromHttps);

        var (_, engine) = BuildSyntheticData(regexDefs: new[]
        {
            (netScript | (uint)LexShieldMask.IsMatchCase, "sensitivescript", @"SensitiveScript/[0-9]+\.js$", "", new[] { "site.example" }, (string[]?)null),
            (netPopup, "rotpop-", @"^https?://[a-z0-9.-]+/rotpop-[0-9]+\.html$", "", (string[]?)null, (string[]?)null),
            (docExc, "trusted-pop.example", @"^https?://trusted-pop\.example/", "", (string[]?)null, (string[]?)null),
        });

        var policy = NormalPolicy("site.example");

        // $match-case: coincide mayúscula exacta, permite minúsculas
        Assert.True(engine.EvaluateNetwork(
            new Uri("https://cdn.example/SensitiveScript/123.js"),
            "site.example",
            policy,
            CoreWebView2WebResourceContext.Script).ShouldBlock);
        Assert.False(engine.EvaluateNetwork(
            new Uri("https://cdn.example/sensitivescript/123.js"),
            "site.example",
            policy,
            CoreWebView2WebResourceContext.Script).ShouldBlock);

        // $popup regex bloqueado aunque sea click del usuario
        Assert.True(engine.ShouldBlockPopup(
            new Uri("https://evil.example/rotpop-99.html"),
            new Uri("https://site.example/"),
            policy,
            isFromSubframe: false,
            isUserInitiated: true,
            out _));

        // Sin regla explícita: popup cross-origin no iniciado por usuario bloqueado por salvaguarda; click del usuario mismo permitido
        Assert.True(engine.ShouldBlockPopup(
            new Uri("https://other.example/landing"),
            new Uri("https://site.example/"),
            policy,
            isFromSubframe: false,
            isUserInitiated: false,
            out _));
        Assert.False(engine.ShouldBlockPopup(
            new Uri("https://other.example/landing"),
            new Uri("https://site.example/"),
            policy,
            isFromSubframe: false,
            isUserInitiated: true,
            out _));

        // Excepción @@...$popup/$document desactiva también la salvaguarda
        Assert.False(engine.ShouldBlockPopup(
            new Uri("https://trusted-pop.example/oauth"),
            new Uri("https://site.example/"),
            policy,
            isFromSubframe: false,
            isUserInitiated: false,
            out _));
    }

    [Fact]
    public void FormatHideCssChunks_SplitsLargeSelectorListsIntoSafeBatches()
    {
        var selectors = new List<string>(45);
        for (int i = 0; i < 45; i++)
            selectors.Add($".ad-slot-{i}");

        string css = LexShieldCosmeticEngine.FormatHideCssChunks(selectors, maxBatchCount: 20);
        string[] lines = css.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.EndsWith("{display:none!important;}", lines[0]);
        Assert.EndsWith("{display:none!important;}", lines[1]);
        Assert.EndsWith("{display:none!important;}", lines[2]);
    }

    [Fact]
    public void ClassifyWebResourceRequest_DistinguishesCrossOriginTopDocumentFromSubframe()
    {
        var hentailaUri = new Uri("https://hentaila.com/");
        var googleUri = new Uri("https://www.google.com/");

        // Clic desde Google hacia hentaila.com antes de NavigationCompleted: Sec-Fetch-Dest: document
        var topNav = ShieldRouter.ClassifyWebResourceRequest(
            hentailaUri,
            activeMainUri: googleUri,
            tabUrlFallback: "https://www.google.com/",
            referer: "https://www.google.com/",
            secFetchDest: "document",
            resourceContext: CoreWebView2WebResourceContext.Document);

        Assert.Equal("hentaila.com", topNav.MainTabHost);
        Assert.Equal("hentaila.com", topNav.OriginHost);
        Assert.Equal("hentaila.com", topNav.PolicyHost);
        Assert.Equal("Document", topNav.FrameDesc);

        // Subrecurso durante carga inicial con PendingMainNavigationUri == https://hentaila.com/
        var subReq = ShieldRouter.ClassifyWebResourceRequest(
            new Uri("https://cdn.hentaila.com/app.js"),
            activeMainUri: hentailaUri,
            tabUrlFallback: "https://www.google.com/",
            referer: "https://hentaila.com/",
            secFetchDest: null,
            resourceContext: CoreWebView2WebResourceContext.Script);

        Assert.Equal("hentaila.com", subReq.MainTabHost);
        Assert.Equal("hentaila.com", subReq.PolicyHost);

        // Iframe cross-origin embebido dentro de hentaila.com
        var iframeDoc = ShieldRouter.ClassifyWebResourceRequest(
            new Uri("https://adframe.example/banner.html"),
            activeMainUri: hentailaUri,
            tabUrlFallback: "https://hentaila.com/",
            referer: "https://hentaila.com/",
            secFetchDest: "iframe",
            resourceContext: CoreWebView2WebResourceContext.Document);

        Assert.Equal("hentaila.com", iframeDoc.PolicyHost);
        Assert.StartsWith("Subframe", iframeDoc.FrameDesc, StringComparison.Ordinal);
    }

    [Fact]
    public void BridgeHostObject_EnforcesTokenAboutBlankInheritanceAndYtDmIsolation()
    {
        var s = SettingsManager.Current;
        string origEngine = s.Shield.Engine;
        bool origAdBlocker = s.EnableAdBlocker;
        bool origShieldEnabled = s.Shield.Enabled;
        bool origCosmetic = s.EnableCosmeticShield;
        bool origModCosmetic = s.Shield.Modules.Cosmetic;
        bool origSubframes = s.EnableWebShieldInSubframes;
        bool origModSubframes = s.Shield.Modules.Subframes;
        s.Shield.Engine = ShieldSettings.EngineLexShield;
        s.EnableAdBlocker = true;
        s.Shield.Enabled = true;
        s.EnableCosmeticShield = true;
        s.Shield.Modules.Cosmetic = true;
        s.EnableWebShieldInSubframes = true;
        s.Shield.Modules.Subframes = true;

        try
        {
            Uri? activeMain = new Uri("https://hentaila.com/ver/1");
            var bridge = new LexShieldBridgeHostObject(
                "secret-tok",
                () => activeMain,
                () => false);

            // Token inválido rechazado
            using (var badDoc = JsonDocument.Parse(bridge.GetDocumentPlanJson("wrong", "https://hentaila.com/", "", true)))
            {
                Assert.False(badDoc.RootElement.GetProperty("enabled").GetBoolean());
            }

            // Documento principal hentaila.com recibe scriptlets + #onhsl
            using (var planDoc = JsonDocument.Parse(bridge.GetDocumentPlanJson("secret-tok", "https://hentaila.com/ver/1", "", true)))
            {
                var root = planDoc.RootElement;
                Assert.True(root.GetProperty("enabled").GetBoolean());
                Assert.Equal("hentaila.com", root.GetProperty("host").GetString());
                Assert.Contains("popMagic", root.GetProperty("scriptletCalls").GetRawText(), StringComparison.OrdinalIgnoreCase);
                Assert.Contains("#onhsl", root.GetProperty("hostCss").GetString() ?? "", StringComparison.Ordinal);
            }

            // Iframe about:blank dentro de hentaila.com hereda parentHref INCLUSO cuando EnableWebShieldInSubframes == false
            s.EnableWebShieldInSubframes = false;
            s.Shield.Modules.Subframes = false;
            using (var blankFrameDefaultSub = JsonDocument.Parse(bridge.GetDocumentPlanJson("secret-tok", "about:blank", "https://hentaila.com/ver/1", false)))
            {
                var root = blankFrameDefaultSub.RootElement;
                Assert.True(root.GetProperty("enabled").GetBoolean());
                Assert.Equal("hentaila.com", root.GetProperty("host").GetString());
                Assert.Contains("popMagic", root.GetProperty("scriptletCalls").GetRawText(), StringComparison.OrdinalIgnoreCase);
            }

            // En Modo Compatible sí se omiten subframes en Brave
            var compatBridge = new LexShieldBridgeHostObject("secret-tok", () => activeMain, () => true);
            using (var blankFrameCompat = JsonDocument.Parse(compatBridge.GetDocumentPlanJson("secret-tok", "about:blank", "https://hentaila.com/ver/1", false)))
            {
                Assert.False(blankFrameCompat.RootElement.GetProperty("enabled").GetBoolean());
            }

            // Fallo en delegado no se traga en silencio: registra LEXSHIELD_BRIDGE_PLAN_ERROR en ShieldLogger
            ShieldLogger.Clear();
            var brokenBridge = new LexShieldBridgeHostObject("secret-tok", () => throw new InvalidOperationException("boom_uri"), () => false);
            using (var brokenDoc = JsonDocument.Parse(brokenBridge.GetDocumentPlanJson("secret-tok", "https://hentaila.com/", "", true)))
            {
                Assert.False(brokenDoc.RootElement.GetProperty("enabled").GetBoolean());
            }
            Assert.Contains("LEXSHIELD_BRIDGE_PLAN_ERROR", ShieldLogger.DumpLog(), StringComparison.Ordinal);

            // Aislamiento estricto: YouTube, Dailymotion y about:blank bajo Dailymotion jamás reciben Brave
            activeMain = new Uri("https://www.dailymotion.com/video/x123");
            using (var dmFrame = JsonDocument.Parse(bridge.GetDocumentPlanJson("secret-tok", "about:blank", "https://www.dailymotion.com/video/x123", false)))
            {
                Assert.False(dmFrame.RootElement.GetProperty("enabled").GetBoolean());
            }
            activeMain = new Uri("https://www.youtube.com/watch?v=1");
            using (var ytDoc = JsonDocument.Parse(bridge.GetDocumentPlanJson("secret-tok", "https://www.youtube.com/watch?v=1", "", true)))
            {
                Assert.False(ytDoc.RootElement.GetProperty("enabled").GetBoolean());
            }
        }
        finally
        {
            s.Shield.Engine = origEngine;
            s.EnableAdBlocker = origAdBlocker;
            s.Shield.Enabled = origShieldEnabled;
            s.EnableCosmeticShield = origCosmetic;
            s.Shield.Modules.Cosmetic = origModCosmetic;
            s.EnableWebShieldInSubframes = origSubframes;
            s.Shield.Modules.Subframes = origModSubframes;
        }
    }

    [Fact]
    public void NodeVm_BootstrapScript_RunsSyncPlanDefersStyleWaitForHeadAndSupportsIframes()
    {
        string js = LexShieldScriptletEngine.BuildBootstrapScript("vm-tok");

        // 1. Verificar sintaxis limpia con node --check + VM con head inicialmente null
        string tempJs = Path.Combine(Path.GetTempPath(), $"lex_boot_{Guid.NewGuid():N}.js");
        string driverJs = Path.Combine(Path.GetTempPath(), $"lex_driver_{Guid.NewGuid():N}.js");
        File.WriteAllText(tempJs, js, Encoding.UTF8);

        string driver = """
const fs = require('fs');
const vm = require('vm');
const bootCode = fs.readFileSync(process.argv[2], 'utf8');

function runScenario(opts) {
  let headEl = opts.startWithHead ? { tagName: 'HEAD', children: [], firstChild: null,
    appendChild(n){ n.parentNode=this; n.isConnected=true; this.children.push(n); },
    insertBefore(n, ref){ n.parentNode=this; n.isConnected=true; this.children.unshift(n); }
  } : null;
  let docEl = opts.startWithHead ? { tagName: 'HTML', id: '', classList: { length: 0 }, firstElementChild: null, nextElementSibling: null } : null;
  const idMap = new Map();
  const listeners = {};
  const moCbs = [];
  let timerCbs = [];
  const posted = [];
  const queries = [];

  class FakeMO {
    constructor(cb){ this.cb = cb; moCbs.push(cb); }
    observe(){}
    disconnect(){}
  }

  const doc = {
    readyState: 'loading',
    get head(){ return headEl; },
    get documentElement(){ return docEl; },
    referrer: opts.referrer || '',
    createElement(tag) {
      let _id = '';
      return {
        tagName: tag.toUpperCase(),
        nodeType: 1,
        isConnected: false,
        textContent: '',
        parentNode: null,
        get id(){ return _id; },
        set id(v){ _id = v; if (v) idMap.set(v, this); },
        remove(){ if (this.parentNode && this.parentNode.children) { const i = this.parentNode.children.indexOf(this); if (i>=0) this.parentNode.children.splice(i,1); } this.isConnected = false; }
      };
    },
    getElementById(id){ return idMap.get(id) || null; },
    querySelectorAll(sel){ return sel.includes('.banner-hit') ? [{}, {}] : []; },
    addEventListener(ev, fn){ (listeners[ev] = listeners[ev] || []).push(fn); }
  };

  const sandbox = {
    location: { href: opts.href, hostname: opts.hostname },
    document: doc,
    MutationObserver: FakeMO,
    setTimeout(fn){ timerCbs.push(fn); return timerCbs.length; },
    getComputedStyle(){ return null; },
    open(u){ return { location: { href: u } }; },
    chrome: {
      webview: opts.useHostObject ? {
        hostObjects: {
          sync: {
            lexShield: {
              GetDocumentPlanJson(tok, href, parentHref, isTop) {
                if (tok !== 'vm-tok') return '{"enabled":false}';
                return JSON.stringify({
                  enabled: true,
                  host: 'hentaila.com',
                  cosmetic: true,
                  ghide: false,
                  scriptletCalls: [{ n: 'set', a: ['__ranBeforeHead', 'true'] }],
                  hostCss: '#onhsl{display:none!important;}',
                  mgCss: '.mg-ad{display:none!important;}',
                  csp: ["script-src 'self'"],
                  noPopup: true,
                  ovl: false
                });
              },
              QueryCosmeticsJson(tok, href, parentHref, isTop, initial, clsJson, idJson) {
                queries.push({ href, parentHref, isTop, initial });
                return JSON.stringify({
                  type: 'lex_shield_cosmetic_apply',
                  host: 'hentaila.com',
                  ghide: false,
                  css: '.banner-hit{display:none!important;}\n.other-hit{display:none!important;}'
                });
              }
            }
          }
        },
        postMessage(m){ posted.push(m); }
      } : {
        addEventListener(){},
        postMessage(m){ posted.push(m); }
      }
    }
  };
  sandbox.window = sandbox;
  sandbox.self = sandbox;
  sandbox.Request = function(){};
  sandbox.Request.prototype = { clone(){ return {}; } };
  sandbox.XMLHttpRequest = function(){};
  sandbox.XMLHttpRequest.prototype = { open(){}, send(){} };
  sandbox.EventTarget = function(){};
  sandbox.EventTarget.prototype = { addEventListener(){}, removeEventListener(){} };
  sandbox.top = opts.isTop ? sandbox : { location: { href: 'https://hentaila.com/' } };
  sandbox.parent = sandbox.top;

  vm.runInNewContext(bootCode, sandbox);
  const ranBeforeHead = Boolean(sandbox.__ranBeforeHead && doc.head === null);

  if (!opts.startWithHead) {
    // Simular aparición tardía de <html> y <head> en el parser
    docEl = { tagName: 'HTML', id: '', classList: { length: 0 }, firstElementChild: {
      nodeType: 1, id: 'adWrap', classList: { length: 1, 0: 'banner-hit' }, firstElementChild: null, nextElementSibling: null
    }, nextElementSibling: null };
    headEl = { tagName: 'HEAD', children: [], firstChild: null,
      appendChild(n){ n.parentNode=this; n.isConnected=true; this.children.push(n); },
      insertBefore(n, ref){ n.parentNode=this; n.isConnected=true; this.children.unshift(n); }
    };
    for (const cb of moCbs) cb([{ type: 'childList', addedNodes: [docEl] }]);
  }
  doc.readyState = 'interactive';
  for (const fn of (listeners['DOMContentLoaded'] || [])) fn();
  for (const fn of timerCbs) fn();

  return {
    ranBeforeHead: ranBeforeHead,
    headChildren: headEl.children.map(c => ({ tag: c.tagName, id: c.id || '', httpEquiv: c.httpEquiv || '', content: c.content || '', text: c.textContent || '' })),
    windowOpenBlocked: sandbox.window.open('https://pop.example') === null,
    diag: sandbox.__lexShieldDiag || null,
    queries,
    posted
  };
}

const topRes = runScenario({ startWithHead: false, href: 'https://hentaila.com/', hostname: 'hentaila.com', isTop: true, useHostObject: true });
const subRes = runScenario({ startWithHead: true, href: 'about:blank', hostname: '', referrer: 'https://hentaila.com/', isTop: false, useHostObject: true });
const fbRes = runScenario({ startWithHead: true, href: 'https://news.example/', hostname: 'news.example', isTop: true, useHostObject: false });

console.log(JSON.stringify({ topRes, subRes, fbRes }));
""";
        File.WriteAllText(driverJs, driver, Encoding.UTF8);

        try
        {
            var psiCheck = new ProcessStartInfo("node", $"--check \"{tempJs}\"")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            using (var pCheck = Process.Start(psiCheck)!)
            {
                string err = pCheck.StandardError.ReadToEnd();
                pCheck.WaitForExit();
                Assert.True(pCheck.ExitCode == 0, $"Node syntax check failed: {err}");
            }

            var psiRun = new ProcessStartInfo("node", $"\"{driverJs}\" \"{tempJs}\"")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            using var pRun = Process.Start(psiRun)!;
            string stdout = pRun.StandardOutput.ReadToEnd();
            string stderr = pRun.StandardError.ReadToEnd();
            pRun.WaitForExit();
            Assert.True(pRun.ExitCode == 0, $"Node VM run failed ({pRun.ExitCode}): {stderr}\n{stdout}");

            using var outDoc = JsonDocument.Parse(stdout);
            var topRes = outDoc.RootElement.GetProperty("topRes");
            Assert.True(topRes.GetProperty("ranBeforeHead").GetBoolean());
            Assert.True(topRes.GetProperty("windowOpenBlocked").GetBoolean());
            var topDiag = topRes.GetProperty("diag");
            Assert.True(topDiag.GetProperty("planOk").GetBoolean());
            Assert.True(topDiag.GetProperty("scriptletsOk").GetBoolean());
            Assert.Equal(1, topDiag.GetProperty("cspCount").GetInt32());
            Assert.Empty(topDiag.GetProperty("errs").EnumerateArray());

            var headChildren = topRes.GetProperty("headChildren");
            bool hasCspMeta = false;
            bool hasHostStyle = false;
            bool hasGenStyle = false;
            foreach (var c in headChildren.EnumerateArray())
            {
                if (c.GetProperty("tag").GetString() == "META" &&
                    c.GetProperty("httpEquiv").GetString() == "Content-Security-Policy" &&
                    c.GetProperty("content").GetString() == "script-src 'self'")
                    hasCspMeta = true;
                if (c.GetProperty("id").GetString() == "lex-shield-host" &&
                    (c.GetProperty("text").GetString() ?? "").Contains("#onhsl"))
                    hasHostStyle = true;
                if (c.GetProperty("id").GetString() == "lex-shield-gen" &&
                    (c.GetProperty("text").GetString() ?? "").Contains(".banner-hit"))
                    hasGenStyle = true;
            }
            Assert.True(hasCspMeta, "Missing CSP <meta> inserted when head appeared");
            Assert.True(hasHostStyle, "Missing #lex-shield-host after null-head delay (bug fix verification)");
            Assert.True(hasGenStyle, "Missing #lex-shield-gen cosmetic CSS");

            var subRes = outDoc.RootElement.GetProperty("subRes");
            Assert.NotEmpty(subRes.GetProperty("queries").EnumerateArray());
            Assert.False(subRes.GetProperty("queries")[0].GetProperty("isTop").GetBoolean());

            var fbRes = outDoc.RootElement.GetProperty("fbRes");
            Assert.False(fbRes.GetProperty("windowOpenBlocked").GetBoolean());
            Assert.Contains(fbRes.GetProperty("posted").EnumerateArray(),
                m => m.GetProperty("type").GetString() == "lex_shield_cosmetic_query");
        }
        finally
        {
            try { File.Delete(tempJs); } catch { }
            try { File.Delete(driverJs); } catch { }
        }
    }

    [Fact]
    public void SupportedWebResourceContexts_ExcludesWebsocketToPreventWebView2SilentDrop()
    {
        var contexts = ShieldRouter.SupportedWebResourceContexts;
        Assert.DoesNotContain(CoreWebView2WebResourceContext.Websocket, contexts);
        Assert.Contains(CoreWebView2WebResourceContext.Document, contexts);
        Assert.Contains(CoreWebView2WebResourceContext.Script, contexts);
        Assert.Contains(CoreWebView2WebResourceContext.XmlHttpRequest, contexts);
        Assert.Contains(CoreWebView2WebResourceContext.Fetch, contexts);
        Assert.Contains(CoreWebView2WebResourceContext.Image, contexts);
        Assert.Contains(CoreWebView2WebResourceContext.Ping, contexts);
        Assert.Contains(CoreWebView2WebResourceContext.Stylesheet, contexts);
        Assert.Contains(CoreWebView2WebResourceContext.Font, contexts);
        Assert.Contains(CoreWebView2WebResourceContext.Media, contexts);
        Assert.Contains(CoreWebView2WebResourceContext.Other, contexts);
    }

    [Fact]
    public void BraveNetworkDecision_IncludesMatchedRuleAndFormatsDiagnosticReason()
    {
        var s = SettingsManager.Current;
        string origEngine = s.Shield.Engine;
        bool origDiag = ShieldLogger.DiagnosticMode;
        s.Shield.Engine = ShieldSettings.EngineLexShield;
        ShieldLogger.DiagnosticMode = true;

        try
        {
            var policy = NormalPolicy("hentaila.com");

            var adIframeUri = new Uri("https://a.adtng.com/get/10016594?time=1769790006402");
            var blockDecision = ShieldRouter.EvaluateWebResource(
                adIframeUri,
                originHost: "hentaila.com",
                policyHost: "hentaila.com",
                mainTabHost: "hentaila.com",
                tabUrl: "https://hentaila.com/hub",
                referer: "https://hentaila.com/",
                policy: policy,
                resourceContext: CoreWebView2WebResourceContext.Document,
                secFetchDest: "iframe",
                httpMethod: "GET",
                engineOverride: ShieldSettings.EngineLexShield);

            Assert.True(blockDecision.ShouldBlock);
            Assert.Contains("LexShield network", blockDecision.Reason ?? "", StringComparison.Ordinal);
            Assert.Contains("adtng.com", blockDecision.Reason ?? "", StringComparison.OrdinalIgnoreCase);

            string blockLogReason = ShieldRouter.FormatNetworkDecisionLogReason(
                in blockDecision,
                adIframeUri,
                "Subframe:hentaila.com|Document",
                CoreWebView2WebResourceContext.Document,
                ShieldSettings.EngineLexShield,
                "Blocked ad domain or pattern");
            Assert.Contains("Engine=LexShield Core", blockLogReason, StringComparison.Ordinal);
            Assert.Contains("Ctx=Document", blockLogReason, StringComparison.Ordinal);
            Assert.Contains("Subframe=True", blockLogReason, StringComparison.Ordinal);
            Assert.Contains("Host=a.adtng.com", blockLogReason, StringComparison.Ordinal);
            Assert.Contains("adtng.com", blockLogReason, StringComparison.OrdinalIgnoreCase);

            var playerUri = new Uri("https://cdn.dotanborito.net/play/f7962073d7e2618e19e8753e05f67182");
            var allowDecision = ShieldRouter.EvaluateWebResource(
                playerUri,
                originHost: "hentaila.com",
                policyHost: "hentaila.com",
                mainTabHost: "hentaila.com",
                tabUrl: "https://hentaila.com/media/x/6",
                referer: "https://hentaila.com/",
                policy: policy,
                resourceContext: CoreWebView2WebResourceContext.Document,
                secFetchDest: "iframe",
                httpMethod: "GET",
                engineOverride: ShieldSettings.EngineLexShield);

            Assert.False(allowDecision.ShouldBlock);
            string allowLogReason = ShieldRouter.FormatNetworkDecisionLogReason(
                in allowDecision,
                playerUri,
                "Subframe:hentaila.com|Document",
                CoreWebView2WebResourceContext.Document,
                ShieldSettings.EngineLexShield,
                "Subframe network request (no match)");
            Assert.Contains("Engine=LexShield Core", allowLogReason, StringComparison.Ordinal);
            Assert.Contains("Ctx=Document", allowLogReason, StringComparison.Ordinal);
            Assert.Contains("Subframe=True", allowLogReason, StringComparison.Ordinal);
            Assert.Contains("Host=cdn.dotanborito.net", allowLogReason, StringComparison.Ordinal);

            // Verificar atribución de excepción @@ en modo diagnóstico (asegurando que primero bloquearía sin la excepción)
            uint baseNet = (uint)(LexShieldMask.FromScript | LexShieldMask.ThirdParty |
                                  LexShieldMask.FromHttps | LexShieldMask.IsHostnameAnchor |
                                  LexShieldMask.IsRightAnchor);
            var (_, synBlockEngine) = BuildSyntheticDataWithPatterns(
                new (uint Mask, string Host, string Pat, string Mod)[]
                {
                    (baseNet, "tracker.example", "/beacon.js", "")
                });
            var preExcDecision = synBlockEngine.EvaluateNetwork(
                new Uri("https://tracker.example/beacon.js"),
                "site.example",
                NormalPolicy("site.example"),
                CoreWebView2WebResourceContext.Script);
            Assert.True(preExcDecision.ShouldBlock);
            Assert.Contains("||tracker.example/beacon.js|", preExcDecision.Reason ?? "", StringComparison.OrdinalIgnoreCase);

            var (_, synEngine) = BuildSyntheticDataWithPatterns(
                new (uint Mask, string Host, string Pat, string Mod)[]
                {
                    (baseNet, "tracker.example", "/beacon.js", ""),
                    (baseNet | (uint)LexShieldMask.IsException, "tracker.example", "/beacon.js", "")
                });
            var excDecision = synEngine.EvaluateNetwork(
                new Uri("https://tracker.example/beacon.js"),
                "site.example",
                NormalPolicy("site.example"),
                CoreWebView2WebResourceContext.Script);
            Assert.False(excDecision.ShouldBlock);
            Assert.True(excDecision.HasException);
            Assert.Contains("@@||tracker.example/beacon.js|", excDecision.Reason ?? "", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            s.Shield.Engine = origEngine;
            ShieldLogger.DiagnosticMode = origDiag;
        }
    }

    private static (LexShieldData Data, LexShieldCoreEngine Engine) BuildSyntheticDataWithPatterns(
        (uint Mask, string Host, string Pat, string Mod)[] patDefs)
    {
        var blob = new List<byte>();
        (int Off, int Len) AddStr(string str)
        {
            if (string.IsNullOrEmpty(str)) return (0, 0);
            int off = blob.Count;
            byte[] b = Encoding.UTF8.GetBytes(str);
            blob.AddRange(b);
            return (off, b.Length);
        }

        var recs = new LexShieldPatRec[patDefs.Length];
        var fallback = new int[patDefs.Length];
        for (int i = 0; i < patDefs.Length; i++)
        {
            var d = patDefs[i];
            var (hOff, hLen) = AddStr(d.Host);
            var (pOff, pLen) = AddStr(d.Pat);
            var (mOff, mLen) = AddStr(d.Mod);
            recs[i] = new LexShieldPatRec
            {
                Mask = d.Mask,
                HostOff = hOff,
                HostLen = hLen,
                PatOff = pOff,
                PatLen = pLen,
                ModOff = mOff,
                ModLen = mLen
            };
            fallback[i] = i;
        }

        var data = LexShieldData.CreateForTests(
            patternRules: recs,
            strBlob: blob.ToArray(),
            fallbackRules: fallback);
        return (data, new LexShieldCoreEngine(() => data));
    }

    [Fact]
    public void LexShieldCoreEngine_WhenDataIsNull_MarksUnavailableAndLogsRequestAllowedEvenWithoutDiagnosticMode()
    {
        bool origDiag = ShieldLogger.DiagnosticMode;
        ShieldLogger.DiagnosticMode = false;
        ShieldLogger.Clear();

        try
        {
            var nullEngine = new LexShieldCoreEngine(() => null);
            var firstPartyScript = new Uri("https://example.com/app.js");
            var decision = nullEngine.EvaluateNetwork(
                firstPartyScript,
                "example.com",
                NormalPolicy("example.com"),
                CoreWebView2WebResourceContext.Script);

            Assert.False(decision.ShouldBlock);
            Assert.True(decision.IsDataUnavailable);
            Assert.Contains("data == null", decision.Reason ?? "", StringComparison.OrdinalIgnoreCase);

            Assert.True(ShieldRouter.TryFormatAllowedNetworkLog(
                in decision,
                firstPartyScript,
                policyHost: "example.com",
                currentHost: "example.com",
                frameDesc: "Script",
                resourceContext: CoreWebView2WebResourceContext.Script,
                engineName: ShieldSettings.EngineLexShield,
                diagnosticMode: false,
                out string reasonText,
                out bool bypassDiag));

            Assert.True(bypassDiag);
            Assert.Contains("LexShield data == null", reasonText, StringComparison.Ordinal);

            var guestTab = new BrowserTab { IsGuest = true };
            ShieldLogger.LogForTab(
                guestTab,
                "REQUEST_ALLOWED",
                "example.com",
                firstPartyScript.AbsoluteUri,
                "Script",
                reasonText,
                bypassDiagnosticFilter: bypassDiag);
            Assert.Equal(0, ShieldLogger.Count);

            var normalTab = new BrowserTab { IsGuest = false, IsIncognito = false };
            ShieldLogger.LogForTab(
                normalTab,
                "REQUEST_ALLOWED",
                "example.com",
                firstPartyScript.AbsoluteUri,
                "Script",
                reasonText,
                bypassDiagnosticFilter: bypassDiag);
            Assert.Equal(1, ShieldLogger.Count);

            string dump = ShieldLogger.DumpLog();
            Assert.Contains("[REQUEST_ALLOWED]", dump, StringComparison.Ordinal);
            Assert.Contains("LexShield data == null", dump, StringComparison.Ordinal);

            var normalAllow = ShieldNetworkDecision.Allow;
            Assert.False(ShieldRouter.TryFormatAllowedNetworkLog(
                in normalAllow,
                firstPartyScript,
                policyHost: "example.com",
                currentHost: "example.com",
                frameDesc: "Script",
                resourceContext: CoreWebView2WebResourceContext.Script,
                engineName: ShieldSettings.EngineLexShield,
                diagnosticMode: false,
                out _,
                out _));
        }
        finally
        {
            ShieldLogger.DiagnosticMode = origDiag;
            ShieldLogger.Clear();
        }
    }
}
