using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using LexBrowser.Core;
using LexBrowser.Core.AdBlock;
using LexBrowser.Core.AdBlock.ShieldCore;
using Microsoft.Web.WebView2.Core;
using Xunit;

namespace LexBrowser.Tests;

[Collection("SettingsCollection")]
public sealed class LexShieldUnificationTests
{
    private static EffectiveSitePolicy FullPolicy(string host = "anime.example")
        => new()
        {
            RegistrableDomain = host,
            Mode = SitePolicy.ModeNormal,
            ShieldEnabled = true,
            IsCompatMode = false,
            Network = true,
            Tracking = true,
            Stubs = true,
            ImaStub = true,
            Popups = true,
            Overlays = true,
            Cosmetic = true,
            AntiWall = true,
            Subframes = true,
            VideoDefuser = false,
            YouTubeAdapter = true,
            DailymotionAdapter = true
        };

    [Fact]
    public void ThreeTierExemptions_ResolveDeterministicallyAcrossDocumentPlanAndNetwork()
    {
        var full = FullPolicy("hentaila.com");

        // TotalExempt: Pagos / OAuth / CAPTCHA / YouTube / Dailymotion / Escudo apagado
        Assert.Equal(ShieldExemptionTier.TotalExempt, ShieldExemptions.ResolveExemptionTier("js.stripe.com", "shop.example", full));
        Assert.Equal(ShieldExemptionTier.TotalExempt, ShieldExemptions.ResolveExemptionTier("challenges.cloudflare.com", "site.example", full));
        Assert.Equal(ShieldExemptionTier.TotalExempt, ShieldExemptions.ResolveExemptionTier("www.youtube.com", "www.youtube.com", full));
        Assert.Equal(ShieldExemptionTier.TotalExempt, ShieldExemptions.ResolveExemptionTier("tracker.example", "site.example", full with { ShieldEnabled = false }));

        // NetworkOnly: BigSites, CompatMode o Cosmética apagada (con Red activa)
        Assert.Equal(ShieldExemptionTier.NetworkOnly, ShieldExemptions.ResolveExemptionTier("tracker.example", "miro.com", full));
        Assert.Equal(ShieldExemptionTier.NetworkOnly, ShieldExemptions.ResolveExemptionTier("tracker.example", "www.google.com", full));
        Assert.Equal(ShieldExemptionTier.NetworkOnly, ShieldExemptions.ResolveExemptionTier("tracker.example", "hentaila.com", full with { IsCompatMode = true, Cosmetic = false }));
        Assert.Equal(ShieldExemptionTier.NetworkOnly, ShieldExemptions.ResolveExemptionTier("tracker.example", "hentaila.com", full with { Cosmetic = false }));

        // Si Red=false además de BigSite/Compat -> TotalExempt
        Assert.Equal(ShieldExemptionTier.TotalExempt, ShieldExemptions.ResolveExemptionTier("tracker.example", "miro.com", full with { Network = false }));
        Assert.Equal(ShieldExemptionTier.TotalExempt, ShieldExemptions.ResolveExemptionTier("tracker.example", "hentaila.com", full with { Network = false, IsCompatMode = true }));

        // CosmeticOnly: Red apagada en sitio normal con cosmética activa
        Assert.Equal(ShieldExemptionTier.CosmeticOnly, ShieldExemptions.ResolveExemptionTier("tracker.example", "hentaila.com", full with { Network = false }));

        // FullProtection: sitio normal con todo activo
        Assert.Equal(ShieldExemptionTier.FullProtection, ShieldExemptions.ResolveExemptionTier("tracker.example", "hentaila.com", full));

        // DocumentPlan en BigSite (miro.com): sin cosmética ni overlays ni window.open decoy
        var s = SettingsManager.Current;
        string miroPlan = ShieldRouter.BuildDocumentPlanJson("https://miro.com/app/board/123/", "", true, new Uri("https://miro.com/"), false);
        using (var doc = JsonDocument.Parse(miroPlan))
        {
            var root = doc.RootElement;
            Assert.True(root.GetProperty("enabled").GetBoolean());
            Assert.False(root.GetProperty("cosmetic").GetBoolean());
            Assert.True(root.GetProperty("ghide").GetBoolean());
            Assert.False(root.TryGetProperty("ovl", out _));
            Assert.False(root.TryGetProperty("decoyPopup", out _));
        }

        // DocumentPlan en hentaila.com: cosmética + overlays + decoyPopup activos
        string hentailaPlan = ShieldRouter.BuildDocumentPlanJson("https://hentaila.com/", "", true, new Uri("https://hentaila.com/"), false);
        using (var doc = JsonDocument.Parse(hentailaPlan))
        {
            var root = doc.RootElement;
            Assert.True(root.GetProperty("enabled").GetBoolean());
            Assert.True(root.GetProperty("cosmetic").GetBoolean());
            Assert.True(root.GetProperty("decoyPopup").GetBoolean());
            Assert.True(root.GetProperty("ovl").GetBoolean());
        }
        _ = s;
    }

    [Fact]
    public void UnifiedPipeline_EnforcesSingleImaOwner_VastContextSafety_AndExtraRules()
    {
        var data = LexShieldData.TryGetDefault();
        Assert.NotNull(data);
        var engine = new LexShieldCoreEngine(() => data);
        var policy = FullPolicy("anime.example");

        // 1. IMA SDK: 200 stub único cuando ImaStub=true en sitio tercero, 403 cuando ImaStub=false o CompatMode, intacto en BigSite
        var imaUri = new Uri("https://imasdk.googleapis.com/js/sdkloader/ima3.js");
        var imaOk = engine.EvaluateNetwork(imaUri, "anime.example", policy, CoreWebView2WebResourceContext.Script);
        Assert.True(imaOk.ShouldBlock);
        Assert.True(imaOk.IsRedirectStub);
        Assert.Contains("google.ima", Encoding.UTF8.GetString(imaOk.RedirectBytes));

        var imaOff = engine.EvaluateNetwork(imaUri, "anime.example", policy with { ImaStub = false }, CoreWebView2WebResourceContext.Script);
        Assert.True(imaOff.ShouldBlock);
        Assert.False(imaOff.IsRedirectStub);

        var imaCompat = engine.EvaluateNetwork(imaUri, "anime.example", policy with { IsCompatMode = true, Stubs = false }, CoreWebView2WebResourceContext.Script);
        Assert.True(imaCompat.ShouldBlock);
        Assert.False(imaCompat.IsRedirectStub);

        // 2. VAST/VMAP: stub XML solo en XHR/Fetch/Other/Media, jamás en Document o Script
        var vastXhr = engine.EvaluateNetwork(new Uri("https://cdn.adprovider.example/ads?output=vast"), "anime.example", policy, CoreWebView2WebResourceContext.XmlHttpRequest);
        Assert.True(vastXhr.ShouldBlock);
        Assert.True(vastXhr.IsRedirectStub);
        Assert.Contains("<VAST", Encoding.UTF8.GetString(vastXhr.RedirectBytes));

        var adtngDoc = engine.EvaluateNetwork(new Uri("https://a.adtng.com/get/10016594"), "hentaila.com", policy, CoreWebView2WebResourceContext.Document, "iframe");
        Assert.True(adtngDoc.ShouldBlock);
        Assert.False(adtngDoc.IsRedirectStub);

        // 3. Reglas "extra" migradas desde Legacy: rotadores/trackers bloqueados en $3p, apuestas libres en navegación directa Document
        Assert.True(engine.EvaluateNetwork(new Uri("https://streamtp10.com/pop.js"), "anime.example", policy, CoreWebView2WebResourceContext.Script).ShouldBlock);
        Assert.True(engine.EvaluateNetwork(new Uri("https://tjassets.com/ad/vast.xml"), "anime.example", policy, CoreWebView2WebResourceContext.XmlHttpRequest).ShouldBlock);
        Assert.True(engine.EvaluateNetwork(new Uri("https://ads.cloud3-easiness925.workers.dev/x.js"), "anime.example", policy, CoreWebView2WebResourceContext.Script).ShouldBlock);
        Assert.True(engine.EvaluateNetwork(new Uri("https://cdn.example/banners/pr_advertising_99.jpg"), "anime.example", policy, CoreWebView2WebResourceContext.Image).ShouldBlock);

        Assert.True(engine.ShouldBlockPopup(new Uri("https://bet365.com/offer"), new Uri("https://anime.example/"), policy, isFromSubframe: false, isUserInitiated: true, out _));
        Assert.False(engine.EvaluateNetwork(new Uri("https://bet365.com/"), "bet365.com", FullPolicy("bet365.com"), CoreWebView2WebResourceContext.Document, "document").ShouldBlock);
    }

    [Fact]
    public void GoogleLensAndSearch_RemainAllowed_WhileTelemetryAndThirdPartyPageAdBlock()
    {
        var data = LexShieldData.TryGetDefault();
        Assert.NotNull(data);
        var engine = new LexShieldCoreEngine(() => data);
        var googlePolicy = FullPolicy("google.com");

        // Endpoints funcionales de Google Search / Lens / GSI permitidos
        Assert.False(engine.EvaluateNetwork(new Uri("https://lens.google.com/v3/upload"), "www.google.com", googlePolicy, CoreWebView2WebResourceContext.XmlHttpRequest).ShouldBlock);
        Assert.False(engine.EvaluateNetwork(new Uri("https://www.google.com/complete/search?q=lex"), "www.google.com", googlePolicy, CoreWebView2WebResourceContext.XmlHttpRequest).ShouldBlock);
        Assert.False(engine.EvaluateNetwork(new Uri("https://www.google.com/async/fol?ei=1"), "www.google.com", googlePolicy, CoreWebView2WebResourceContext.Fetch).ShouldBlock);
        Assert.False(engine.EvaluateNetwork(new Uri("https://accounts.google.com/gsi/log"), "news.example", FullPolicy("news.example"), CoreWebView2WebResourceContext.XmlHttpRequest).ShouldBlock);

        // Telemetría Google silenciada por reglas declarativas (en MainWindow.Tabs.cs responde HTTP 204 sin error)
        Assert.True(engine.EvaluateNetwork(new Uri("https://www.google.com/gen_204?s=web&t=1"), "www.google.com", googlePolicy, CoreWebView2WebResourceContext.Ping).ShouldBlock);
        Assert.True(engine.EvaluateNetwork(new Uri("https://www.google.com/log?format=json"), "www.google.com", googlePolicy, CoreWebView2WebResourceContext.XmlHttpRequest).ShouldBlock);

        // PageAd en origen tercero bloqueado vía regla "extra" ||google.com/pagead/viewthroughconversion/
        Assert.True(engine.EvaluateNetwork(new Uri("https://www.google.com/pagead/viewthroughconversion/999/"), "shop.example", FullPolicy("shop.example"), CoreWebView2WebResourceContext.Script).ShouldBlock);
    }

    [Fact]
    public void NodeVm_UnifiedBootstrap_SingleWindowOpenHook_NoDoubleCounting_AndCanvasProtected()
    {
        string bootJs = LexShieldScriptletEngine.BuildBootstrapScript("unif-tok");
        string defuserJs = WebShield.GetVideoDefuserScript(
            enableVideoDefuser: true,
            new Dictionary<string, SiteShieldProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["paused.example"] = new SiteShieldProfile { Mode = SitePolicy.ModeOff },
                ["compat.example"] = new SiteShieldProfile { Mode = SitePolicy.ModeCompat }
            });

        string driver = $$"""
const fs = require('fs');
const vm = require('vm');
const assert = require('assert');
const bootCode = fs.readFileSync(process.argv[2], 'utf8');
const defuserCode = fs.readFileSync(process.argv[3], 'utf8');

function runBootstrapScenario(planOverrides) {
  const posted = [];
  const docListeners = {};
  const winListeners = {};
  let origOpenCalled = 0;
  let origAnchorClicks = 0;

  function makeEl(tag, opts) {
    opts = opts || {};
    const styleMap = {};
    return {
      nodeType: 1,
      tagName: tag.toUpperCase(),
      id: opts.id || '',
      className: opts.className || '',
      isConnected: opts.isConnected !== false,
      childElementCount: opts.childCount || 0,
      children: [],
      innerText: opts.text || '',
      textContent: opts.text || '',
      href: opts.href || '',
      target: opts.target || '',
      parentElement: opts.parentElement || null,
      classList: { length: 0, forEach(){}, contains(){ return false; }, remove(){} },
      attributes: [],
      isContentEditable: Boolean(opts.editable),
      getAttribute(k) { return k === 'role' ? (opts.role || '') : null; },
      hasAttribute() { return false; },
      querySelector() { return null; },
      querySelectorAll() { return []; },
      closest() { return null; },
      getBoundingClientRect() { return opts.rect || { width: 1200, height: 800, left: 0, top: 0 }; },
      remove() { this._removed = true; },
      play() { this._played = true; return Promise.resolve(); },
      paused: true,
      style: {
        setProperty(k, v) { styleMap[k] = v; },
        get display() { return styleMap.display || ''; },
        get pointerEvents() { return styleMap['pointer-events'] || ''; }
      },
      _style: Object.assign({ position: 'fixed', zIndex: '99999', opacity: '0', backgroundColor: 'transparent', visibility: 'visible', display: 'block' }, opts.compStyle || {})
    };
  }

  const videoEl = makeEl('video', { compStyle: { position: 'relative', opacity: '1', zIndex: '1' } });
  const ghostOverlay = makeEl('div', { className: 'first-click-ad' });
  const canvasEl = makeEl('canvas', { rect: { width: 1200, height: 800 } });
  const docEl = { tagName: 'HTML', id: '', classList: { length: 0 }, firstElementChild: null, nextElementSibling: null };
  const headEl = { children: [], firstChild: null, appendChild(n){ n.isConnected=true; this.children.push(n); }, insertBefore(n){ n.isConnected=true; this.children.unshift(n); } };

  function FakeAnchor() {}
  FakeAnchor.prototype.click = function() { origAnchorClicks++; };

  const doc = {
    readyState: 'interactive',
    hidden: false,
    head: headEl,
    documentElement: docEl,
    body: makeEl('body'),
    contains(el) { return el && !el._detached; },
    getElementById() { return null; },
    createElement(t) { return makeEl(t); },
    querySelector() { return videoEl; },
    querySelectorAll() { return []; },
    elementFromPoint() { return videoEl; },
    elementsFromPoint() { return [ghostOverlay, videoEl]; },
    addEventListener(ev, fn) { (docListeners[ev] = docListeners[ev] || []).push(fn); }
  };

  const sandbox = {
    location: { hostname: 'anime.example', href: 'https://anime.example/ep/1', origin: 'https://anime.example', pathname: '/ep/1', search: '' },
    navigator: { userActivation: { isActive: false } },
    document: doc,
    HTMLAnchorElement: FakeAnchor,
    MutationObserver: class { observe(){} disconnect(){} },
    getComputedStyle(el) { return el ? el._style : null; },
    addEventListener(ev, fn) { (winListeners[ev] = winListeners[ev] || []).push(fn); },
    open(u) { origOpenCalled++; return { realWin: true, url: u }; },
    setTimeout() { return 1; },
    chrome: {
      webview: {
        hostObjects: {
          sync: {
            lexShield: {
              GetDocumentPlanJson() {
                return JSON.stringify(Object.assign({
                  enabled: true,
                  host: 'anime.example',
                  cosmetic: true,
                  ghide: false,
                  noPopup: false,
                  decoyPopup: true,
                  ovl: true
                }, planOverrides || {}));
              },
              QueryCosmeticsJson() { return JSON.stringify({ type: 'lex_shield_cosmetic_apply', ghide: false }); }
            }
          }
        },
        postMessage(m) { posted.push(m); }
      }
    }
  };
  sandbox.window = sandbox;
  sandbox.self = sandbox;
  sandbox.top = sandbox;
  sandbox.parent = sandbox;

  vm.runInNewContext(bootCode, sandbox);

  // Idempotencia de window.open
  assert.strictEqual(sandbox.__lexWindowOpenWrapped, true);

  if (planOverrides && planOverrides.noPopup) {
    assert.strictEqual(sandbox.open('https://ads.adtng.com/pop'), null);
  } else {
    const decoy = sandbox.open('https://ads.adtng.com/pop');
    assert.strictEqual(decoy.closed, false);
    const oauthWin = sandbox.open('https://accounts.google.com/o/oauth2/auth');
    assert.strictEqual(oauthWin.realWin, true);
    assert.strictEqual(origOpenCalled, 1);

    // Disparar pointerdown + mousedown + click sobre la misma capa overlay -> video.play() y conteo=1
    const mkEv = (t) => ({ target: t, clientX: 50, clientY: 50, preventDefault(){}, stopPropagation(){}, stopImmediatePropagation(){} });
    for (const fn of (winListeners.pointerdown || [])) fn(mkEv(ghostOverlay));
    for (const fn of (docListeners.mousedown || [])) fn(mkEv(ghostOverlay));
    for (const fn of (winListeners.click || [])) fn(mkEv(ghostOverlay));
    assert.strictEqual(videoEl._played, true);
    assert.strictEqual(ghostOverlay.style.display, 'none');

    // Canvas nunca se oculta
    for (const fn of (winListeners.click || [])) fn(mkEv(canvasEl));
    assert.strictEqual(canvasEl.style.display, '');

    // phantom anchor llamado 3 veces sobre el mismo nodo -> bloqueado todas y contado UNA vez
    const badA = Object.assign(new FakeAnchor(), { href: 'https://adtng.com/click', target: '_blank', innerText: '', textContent: '', _detached: true });
    badA.click();
    badA.click();
    badA.click();
    assert.strictEqual(origAnchorClicks, 0);
  }

  return { posted, diag: sandbox.__lexShieldDiag };
}

const s1 = runBootstrapScenario({ noPopup: false, decoyPopup: true, ovl: true });
assert.strictEqual(s1.diag.decoyPopup, true);
assert.strictEqual(s1.diag.noPopup, false);
const ovlMsgs = s1.posted.filter(m => m.category === 'overlay');
const phantomMsgs = s1.posted.filter(m => m.reason === 'phantom_anchor');
const popupMsgs = s1.posted.filter(m => m.category === 'popup');
assert.strictEqual(ovlMsgs.length, 2, '1 ghost overlay + 1 deduplicated phantom_anchor expected, got: ' + JSON.stringify(ovlMsgs));
assert.strictEqual(phantomMsgs.length, 1);
assert.strictEqual(popupMsgs.length, 1);

const s2 = runBootstrapScenario({ noPopup: true, decoyPopup: true, ovl: false });
assert.strictEqual(s2.diag.noPopup, true);
assert.strictEqual(s2.diag.decoyPopup, false);

// Validar que VideoPlayerDefuser respeta Off y Compat
function runDefuser(host) {
  let docListeners = 0;
  const ctx = {
    location: { hostname: host, pathname: '/ep/1' },
    document: { readyState: 'complete', addEventListener(){ docListeners++; }, querySelector(){ return null; } },
    WeakMap,
    addEventListener(){},
    setTimeout() { return 1; }
  };
  ctx.window = ctx;
  ctx.self = ctx;
  ctx.top = ctx;
  vm.runInNewContext(defuserCode, ctx);
  return docListeners;
}
assert.strictEqual(runDefuser('paused.example'), 0);
assert.strictEqual(runDefuser('compat.example'), 0);
assert.strictEqual(runDefuser('www.paypal.com'), 0);
assert.strictEqual(runDefuser('anime.example'), 1);

console.log('UNIFIED_JS_OK');
""";

        string bootFile = Path.Combine(Path.GetTempPath(), $"lex_unif_boot_{Guid.NewGuid():N}.js");
        string defFile = Path.Combine(Path.GetTempPath(), $"lex_unif_def_{Guid.NewGuid():N}.js");
        string drvFile = Path.Combine(Path.GetTempPath(), $"lex_unif_drv_{Guid.NewGuid():N}.js");
        try
        {
            File.WriteAllText(bootFile, bootJs, new UTF8Encoding(false));
            File.WriteAllText(defFile, defuserJs, new UTF8Encoding(false));
            File.WriteAllText(drvFile, driver, new UTF8Encoding(false));

            var psi = new ProcessStartInfo("node", $"\"{drvFile}\" \"{bootFile}\" \"{defFile}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10_000);
            Assert.True(proc.ExitCode == 0, $"Node VM unification test failed:\n{stderr}\n{stdout}");
            Assert.Contains("UNIFIED_JS_OK", stdout);
        }
        finally
        {
            try { File.Delete(bootFile); } catch { }
            try { File.Delete(defFile); } catch { }
            try { File.Delete(drvFile); } catch { }
        }
    }
}
