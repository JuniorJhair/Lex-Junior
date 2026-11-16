using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using LexBrowser.Core.AdBlock;
using LexBrowser.Core.AdBlock.ShieldCore;
using Xunit;

namespace LexBrowser.Tests;

public sealed class LexShieldCosmeticScriptletTests
{
    private static LexShieldData LoadRealShieldData()
    {
        var data = LexShieldData.TryGetDefault();
        Assert.NotNull(data);
        return data!;
    }

    private static EffectiveSitePolicy NormalPolicy(string host = "example.com")
        => new()
        {
            Mode = SitePolicy.ModeNormal,
            ShieldEnabled = true,
            IsCompatMode = false,
            Tracking = true,
            Network = true,
            Stubs = true,
            ImaStub = true,
            Popups = true,
            Overlays = true,
            Cosmetic = true,
            AntiWall = true,
            Subframes = true,
            VideoDefuser = true,
            YouTubeAdapter = false,
            DailymotionAdapter = false
        };

    [Fact]
    public void RealLexBundle_LoadsCosmeticCountsAndHostRules()
    {
        var data = LoadRealShieldData();
        var cos = data.GetCosmeticEngine();

        Assert.Equal(9196, cos.SimpleClassCount);
        Assert.Equal(4376, cos.SimpleIdCount);
        Assert.Equal(42518, cos.HostCount);
        Assert.Equal(27636, cos.PayloadCount);
        Assert.Equal(620, cos.MiscGenericHide.Count);
        Assert.Equal(230, data.GetCspRules().Length);
        Assert.Equal(456, data.GetRegexRules().Length);

        Assert.True(cos.HasHostRules("animeyt.es"));
        Assert.True(cos.HasHostRules("hentaila.com"));
        Assert.True(cos.HasHostRules("hentaila.tv"));
        Assert.True(cos.HasHostRules("www.google.com")); // entidad google.*
    }

    [Fact]
    public void Isolation_YouTubeAndDailymotionNeverReceiveGeneralCosmeticsOrScriptlets()
    {
        var data = LoadRealShieldData();
        var cos = data.GetCosmeticEngine();
        var res = data.GetResourceStore();
        var policy = NormalPolicy("youtube.com");

        foreach (string host in new[] { "youtube.com", "www.youtube.com", "m.youtube.com", "dailymotion.com", "geo.dailymotion.com", "www.dailymotion.com" })
        {
            Assert.False(cos.HasHostRules(host));
            var h = cos.EvaluateHost(host, genericHide: false, res);
            Assert.False(h.HasAnyInjection);
            Assert.Empty(cos.QueryGenericCss(host, new[] { "bannerad", "AdSense" }, new[] { "ads" }, genericHide: false, policy));
            Assert.Null(LexShieldScriptletEngine.BuildHostInjectionScript(host, h, res, policy));
            Assert.False(ShieldRouter.ShouldDeferDocumentForScriptlets(
                new Uri($"https://{host}/watch"), host, policy, isSubframe: false, engineOverride: ShieldSettings.EngineLexShield));
        }
    }

    [Fact]
    public void RealSites_AnimeYtAndHentailaResolveExpectedScriptlets()
    {
        var data = LoadRealShieldData();
        var cos = data.GetCosmeticEngine();
        var res = data.GetResourceStore();
        var policy = NormalPolicy("animeyt.es");

        var anime = cos.EvaluateHost("animeyt.es", genericHide: false, res);
        Assert.Contains(anime.Scriptlets, s => s.Contains("rmnt", StringComparison.Ordinal) && s.Contains("popundersPerIP", StringComparison.Ordinal));
        string? animeJs = LexShieldScriptletEngine.BuildHostInjectionScript("animeyt.es", anime, res, policy);
        Assert.NotNull(animeJs);
        Assert.Contains("removeNodeText", animeJs);
        Assert.Contains("replaceNodeTextFn", animeJs);

        var hentaila = cos.EvaluateHost("hentaila.com", genericHide: false, res);
        Assert.Contains(hentaila.Scriptlets, s => s.Contains("acs", StringComparison.Ordinal) && s.Contains("popMagic", StringComparison.Ordinal));
        string? hentailaJs = LexShieldScriptletEngine.BuildHostInjectionScript("hentaila.com", hentaila, res, NormalPolicy("hentaila.com"));
        Assert.NotNull(hentailaJs);
        Assert.Contains("abortCurrentScript", hentailaJs);
        Assert.Contains("abortCurrentScriptFn", hentailaJs);
    }

    [Fact]
    public void GenericCssLookup_RespectsCaseSensitivityUnhideAndGenericHide()
    {
        var data = LoadRealShieldData();
        var cos = data.GetCosmeticEngine();
        var policy = NormalPolicy("example.org");

        string cssMatched = cos.QueryGenericCss(
            "example.org",
            new[] { "bannerad", "AdSense", "nonexistent-lex-class-xyz" },
            Array.Empty<string>(),
            genericHide: false,
            policy);
        Assert.Contains(".bannerad", cssMatched);
        Assert.Contains(".AdSense", cssMatched);
        Assert.DoesNotContain("nonexistent-lex-class-xyz", cssMatched);

        // Con $generichide activo no deben emitirse selectores genéricos
        string suppressed = cos.QueryGenericCss(
            "example.org",
            new[] { "bannerad", "AdSense" },
            Array.Empty<string>(),
            genericHide: true,
            policy);
        Assert.Equal(string.Empty, suppressed);

        // Si policy.Cosmetic está desactivado (p.ej. Modo Compatible), tampoco emite CSS genérico
        var compat = policy with { Cosmetic = false, IsCompatMode = true };
        Assert.Equal(string.Empty, cos.QueryGenericCss("example.org", new[] { "bannerad" }, Array.Empty<string>(), false, compat));
    }

    [Fact]
    public void SyntheticCosmeticEngine_HandlesUnhideUninjectPrivilegedAndNegHosts()
    {
        const string host = "testsite.example";
        const string sub = "sub.testsite.example";
        ulong hostHash = LexShieldHash.Fnv1a64(host.AsSpan());
        ulong clsHash = LexShieldHash.Fnv1a64("adbox".AsSpan());

        var payloads = new[]
        {
            new CosPayload(CosPayload.KindHideCss, ".adbox", null, 0),
            new CosPayload(CosPayload.KindHideCss, ".only-root", new[] { sub }, 0),
            new CosPayload(CosPayload.KindUnhideCss, ".adbox", null, 0),
            new CosPayload(CosPayload.KindScriptlet, "set, safeFlag, false", null, 0),
            new CosPayload(CosPayload.KindScriptlet, "acs, a, b", null, 0),
            new CosPayload(CosPayload.KindScriptlet, "acs, c, d", null, 0),
            new CosPayload(CosPayload.KindScriptlet, "trusted-set-constant, x, 1", null, 1),
            new CosPayload(CosPayload.KindProcedural, "div.card:style(margin-top: 0px !important)", null, 0),
            new CosPayload(CosPayload.KindProcedural, "div.card:has-text(Promo):remove()", null, 0),
        };

        var slices = new[] { new CosHostSlice(0, 9, 0) };
        var pids = new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 };
        var uiMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [host] = new[] { "set", "acs, c, d" }
        };

        var engine = new LexShieldCosmeticEngine(
            new[] { clsHash },
            Array.Empty<ulong>(),
            new[] { hostHash },
            slices,
            pids,
            payloads,
            uiMap,
            new Dictionary<string, string[]>(StringComparer.Ordinal),
            new Dictionary<string, string[]>(StringComparer.Ordinal),
            new[] { "[href*=\"bad\"]", ".adbox" });

        var realResources = LoadRealShieldData().GetResourceStore();
        var rootRes = engine.EvaluateHost(host, genericHide: false, realResources, allowPrivileged: false);

        // .adbox fue desocultado por #@#.adbox, pero .only-root aplica en testsite.example
        Assert.DoesNotContain(".adbox", rootRes.HideSelectors);
        Assert.Contains(".only-root", rootRes.HideSelectors);
        // set (sin args -> desinyecta todos los set) y acs, c, d fueron desinyectados; trusted-* bloqueado por permiso
        Assert.Single(rootRes.Scriptlets);
        Assert.Equal("acs, a, b", rootRes.Scriptlets[0]);
        // :style puro vs procedural
        Assert.Contains("div.card{margin-top: 0px !important}", rootRes.StyleCssRules);
        Assert.Contains("div.card:has-text(Promo):remove()", rootRes.ProceduralRules);

        // QueryGenericCss tampoco debe re-ocultar .adbox porque está en Unhidden
        Assert.Equal(string.Empty, engine.QueryGenericCss(host, new[] { "adbox" }, null, false, NormalPolicy(host)));
        // MiscGenericCss también excluye .adbox
        string mg = LexShieldScriptletEngine.BuildMiscGenericCss(engine, rootRes);
        Assert.Equal("[href*=\"bad\"]{display:none!important;}", mg);

        // Subdominio excluido por nh=[sub]
        var subRes = engine.EvaluateHost(sub, genericHide: false, realResources, allowPrivileged: true);
        Assert.DoesNotContain(".only-root", subRes.HideSelectors);
        // Con allowPrivileged=true ahora sí aparece trusted-set-constant
        Assert.Contains("trusted-set-constant, x, 1", subRes.Scriptlets);
    }

    [Fact]
    public void ParseScriptletArgs_HandlesQuotesEscapedCommasAndRegex()
    {
        var a = LexShieldScriptletEngine.ParseScriptletArgs("rmnt, script, /h=decodeURIComponent|popundersPerIP/");
        Assert.Equal(new[] { "rmnt", "script", "/h=decodeURIComponent|popundersPerIP/" }, a);

        var b = LexShieldScriptletEngine.ParseScriptletArgs("set, foo.bar, 'hello, world', \"a\\\"b\"");
        Assert.Equal(new[] { "set", "foo.bar", "hello, world", "a\"b" }, b);

        var c = LexShieldScriptletEngine.ParseScriptletArgs(@"rpnt, script, a\,b, '/*start*/x("".y"")/*end*/'");
        Assert.Equal(new[] { "rpnt", "script", "a,b", "/*start*/x(\".y\")/*end*/" }, c);
    }

    [Fact]
    public void StyleRuleSplitter_DistinguishesPureCssFromProcedural()
    {
        Assert.True(LexShieldCosmeticEngine.TrySplitPlainCssStyleRule(
            "div.banner:style(transform: translateX(0px) !important; margin: 0)",
            out string css));
        Assert.Equal("div.banner{transform: translateX(0px) !important; margin: 0}", css);

        Assert.False(LexShieldCosmeticEngine.TrySplitPlainCssStyleRule(
            "div.banner:has-text(Sponsored):style(display: none !important)",
            out _));
    }

    [Fact]
    public void NodeJs_ValidatesSyntaxAndRuntimeExecutionOfBootstrapAndScriptlets()
    {
        var data = LoadRealShieldData();
        var cos = data.GetCosmeticEngine();
        var res = data.GetResourceStore();

        string? animeJs = LexShieldScriptletEngine.BuildHostInjectionScript(
            "animeyt.es",
            cos.EvaluateHost("animeyt.es", false, res),
            res,
            NormalPolicy("animeyt.es"));
        string? hentailaJs = LexShieldScriptletEngine.BuildHostInjectionScript(
            "hentaila.com",
            cos.EvaluateHost("hentaila.com", false, res),
            res,
            NormalPolicy("hentaila.com"));

        Assert.NotNull(animeJs);
        Assert.NotNull(hentailaJs);
        Assert.True(LexShieldScriptletEngine.TryBuildScriptletCall("set, lexTestFlag, false", res, out string setJs));
        Assert.True(LexShieldScriptletEngine.TryBuildScriptletCall("aeld, click, popMagic", res, out string aeldJs));
        Assert.True(LexShieldScriptletEngine.TryBuildScriptletCall("nowoif", res, out string nowoifJs));

        string harness = BuildNodeVerificationScript(
            LexShieldScriptletEngine.BootstrapScript,
            animeJs!,
            hentailaJs!,
            setJs,
            aeldJs,
            nowoifJs);

        string tempFile = Path.Combine(Path.GetTempPath(), $"lex_shield_cos_{Guid.NewGuid():N}.js");
        try
        {
            File.WriteAllText(tempFile, harness, new UTF8Encoding(false));

            var (syntaxCode, syntaxOut) = RunNode("--check", tempFile);
            Assert.True(syntaxCode == 0, $"node --check falló: {syntaxOut}");

            var (runCode, runOut) = RunNode(tempFile);
            Assert.True(runCode == 0, $"Ejecución Node falló (exit {runCode}): {runOut}");
            Assert.Contains("ALL_NODE_CHECKS_OK", runOut);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    private static (int ExitCode, string Output) RunNode(params string[] args)
    {
        var psi = new ProcessStartInfo("node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("No se pudo iniciar node.");
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(15000);
        return (proc.ExitCode, stdout + stderr);
    }

    private static string BuildNodeVerificationScript(
        string bootstrapJs,
        string animeJs,
        string hentailaJs,
        string setJs,
        string aeldJs,
        string nowoifJs)
    {
        return $$"""
const vm = require('vm');
const assert = require('assert');

function checkSyntax(label, code) {
  new vm.Script(code, { filename: label });
}

checkSyntax('bootstrap.js', {{JsonSerializer.Serialize(bootstrapJs)}});
checkSyntax('animeyt.js', {{JsonSerializer.Serialize(animeJs)}});
checkSyntax('hentaila.js', {{JsonSerializer.Serialize(hentailaJs)}});
checkSyntax('set.js', {{JsonSerializer.Serialize(setJs)}});
checkSyntax('aeld.js', {{JsonSerializer.Serialize(aeldJs)}});
checkSyntax('nowoif.js', {{JsonSerializer.Serialize(nowoifJs)}});

function makeBrowserCtx(host, extra) {
  const listeners = [];
  class FakeEventTarget {
    addEventListener(t, fn) { listeners.push({ t, fn }); }
    removeEventListener() {}
  }
  class FakeNode extends FakeEventTarget {
    constructor(text) {
      super();
      this._textContent = text || '';
    }
    get textContent() { return this._textContent; }
    set textContent(v) { this._textContent = v == null ? '' : String(v); }
  }
  class FakeElement extends FakeNode {}
  class FakeDocument extends FakeNode {}
  class FakeWindow extends FakeEventTarget {}
  class FakeHTMLScriptElement extends FakeElement {
    constructor(text, src) {
      super(text);
      this.tagName = 'SCRIPT';
      this.nodeType = 1;
      this.nodeName = 'SCRIPT';
      this.localName = 'script';
      this.src = src || '';
    }
  }
  const doc = Object.assign(new FakeDocument(''), {
    hidden: false,
    readyState: 'loading',
    currentScript: null,
    documentElement: { tagName: 'HTML', appendChild() {} },
    head: { appendChild() {} },
    getElementById() { return null; },
    createElement() { return { style: {}, textContent: '' }; },
    createTreeWalker: () => ({ nextNode: () => null }),
    querySelectorAll: () => ['ok']
  });
  const ctx = Object.assign({
    location: { hostname: host, pathname: '/home', search: '', origin: 'https://' + host, href: 'https://' + host + '/home' },
    Math, Object, Array, RegExp, Error, ReferenceError, TypeError, Proxy, Reflect, Set, Map, WeakSet, WeakMap,
    NodeFilter: { SHOW_ELEMENT: 1, SHOW_TEXT: 4 },
    Function, String, Number, Boolean, Symbol, Date, Promise,
    JSON, parseInt, parseFloat, isNaN, setTimeout, clearTimeout, setInterval, clearInterval,
    console, atob: (b) => Buffer.from(b, 'base64').toString('binary'), decodeURIComponent,
    Node: FakeNode,
    Element: FakeElement,
    Document: FakeDocument,
    Window: FakeWindow,
    HTMLScriptElement: FakeHTMLScriptElement,
    Request: class { clone() { return this; } },
    Response: class { clone() { return this; } },
    XMLHttpRequest: class {},
    fetch: () => Promise.resolve({}),
    EventTarget: FakeEventTarget,
    MutationObserver: class { observe() {} disconnect() {} },
    addEventListener: function(...args) { return FakeEventTarget.prototype.addEventListener.apply(this, args); },
    removeEventListener: function(...args) { return FakeEventTarget.prototype.removeEventListener.apply(this, args); },
    _listeners: listeners,
    document: doc
  }, extra || {});
  ctx.window = ctx;
  ctx.self = ctx;
  ctx.globalThis = ctx;
  return ctx;
}

// 1. Verificar set-constant (set, lexTestFlag, false)
{
  const ctx = makeBrowserCtx('example.com');
  vm.runInNewContext({{JsonSerializer.Serialize(setJs)}}, ctx);
  ctx.document.currentScript = new ctx.HTMLScriptElement('', '');
  assert.strictEqual(ctx.lexTestFlag, false);
}

// 2. Verificar hentaila.com (acs, document.querySelectorAll, popMagic) en VM con entorno DOM mínimo
{
  const ctx = makeBrowserCtx('hentaila.com');
  vm.runInNewContext({{JsonSerializer.Serialize(hentailaJs)}}, ctx);
  ctx.document.currentScript = new ctx.HTMLScriptElement('var x = popMagic();', '');
  let threw = false;
  try {
    ctx.document.querySelectorAll('.some-ad');
  } catch (e) {
    threw = true;
  }
  assert.strictEqual(threw, true, 'acs debía abortar document.querySelectorAll cuando popMagic está en el script');
  ctx.document.currentScript = new ctx.HTMLScriptElement('var safe = 123;', '');
  assert.deepStrictEqual(ctx.document.querySelectorAll('.safe'), ['ok']);
}

// 3. Verificar animeyt.es (rmnt) interceptando nodos script con popundersPerIP
{
  let observerCb = null;
  const ctx = makeBrowserCtx('animeyt.es', {
    MutationObserver: class {
      constructor(cb) { observerCb = cb; }
      observe() {}
      disconnect() {}
    }
  });
  vm.runInNewContext({{JsonSerializer.Serialize(animeJs)}}, ctx);
  assert.ok(observerCb, 'rmnt debía registrar un MutationObserver');
  const fakeScript = new ctx.HTMLScriptElement('h=decodeURIComponent(x); popundersPerIP = 1;', '');
  fakeScript.replaceWith = () => {};
  fakeScript.remove = () => {};
  observerCb([{ type: 'childList', addedNodes: [fakeScript] }]);
  assert.strictEqual(fakeScript.textContent, '', 'rmnt debía vaciar el texto del script coincidente');
}

// 4. Verificar addEventListener-defuser y window.open-defuser
{
  const ctx = makeBrowserCtx('example.com', {
    open: function() { return { closed: false }; }
  });
  vm.runInNewContext({{JsonSerializer.Serialize(aeldJs + "\n" + nowoifJs)}}, ctx);
  ctx.addEventListener('click', function popMagic() {});
  assert.strictEqual(ctx._listeners.length, 0, 'aeld debía bloquear listener popMagic');
  assert.doesNotThrow(() => ctx.open('https://ad.example/'));
}

// 5. Verificar motor procedural de BootstrapScript (:has-text, :remove-attr, :remove-class, :remove)
{
  function makeEl(tag, attrs, text, children) {
    const attrMap = new Map(Object.entries(attrs || {}));
    const clsSet = new Set((attrs && attrs.class ? attrs.class.split(/\s+/) : []));
    const el = {
      nodeType: 1,
      tagName: tag.toUpperCase(),
      isConnected: true,
      textContent: text || '',
      style: { setProperty(k, v) { el._styled = (el._styled || '') + k + ':' + v; } },
      children: children || [],
      parentElement: null,
      attributes: [],
      classList: {
        forEach(cb) { Array.from(clsSet).forEach(c => cb(c)); },
        remove(c) { clsSet.delete(c); },
        contains(c) { return clsSet.has(c); }
      },
      hasAttribute(k) { return attrMap.has(k); },
      getAttribute(k) { return attrMap.get(k) ?? null; },
      setAttribute(k, v) { attrMap.set(k, String(v)); el._syncAttrs(); },
      removeAttribute(k) { attrMap.delete(k); el._syncAttrs(); },
      remove() { el._removed = true; },
      matches(sel) {
        if (sel.startsWith('.')) return clsSet.has(sel.slice(1));
        if (sel === 'a[href]') return tag === 'a' && attrMap.has('href');
        return sel.toLowerCase() === tag.toLowerCase();
      },
      _syncAttrs() {
        el.attributes = Array.from(attrMap.entries()).map(([name, value]) => ({ name, value }));
      }
    };
    el._syncAttrs();
    (children || []).forEach(c => { c.parentElement = el; });
    return el;
  }

  const card = makeEl('div', { class: 'card ad-cls' }, 'Promoted content', []);
  const badLink = makeEl('a', { href: 'https://click.example' }, 'Watch', []);
  const allEls = [card, badLink];
  const ctx = {
    location: { hostname: 'news.example', pathname: '/home', search: '', href: 'https://news.example/home' },
    Math, Object, Array, RegExp, Error, Proxy, Reflect, Set, Map, WeakSet, WeakMap,
    JSON, parseInt, setTimeout, clearTimeout,
    MutationObserver: class { observe() {} disconnect() {} },
    __lexShieldPendingProcedural: [
      '.card:has-text(/Promoted/):remove-class(ad-cls)',
      'a[href]:remove-attr(href)'
    ],
    document: {
      readyState: 'loading',
      documentElement: { tagName: 'HTML', appendChild() {} },
      head: { appendChild() {} },
      getElementById() { return null; },
      createElement() { return { style: {}, textContent: '' }; },
      addEventListener() {},
      querySelectorAll(sel) {
        if (sel === '.card') return [card];
        if (sel === 'a[href]') return [badLink];
        return allEls.filter(e => e.matches(sel));
      }
    }
  };
  ctx.window = ctx;
  ctx.self = ctx;
  ctx.globalThis = ctx;
  ctx.window.top = ctx.window;
  vm.runInNewContext({{JsonSerializer.Serialize(bootstrapJs)}}, ctx);
  ctx.window.__lexShieldApplyProcedural();
  assert.strictEqual(card.classList.contains('ad-cls'), false);
  assert.strictEqual(badLink.hasAttribute('href'), false);
}

console.log('ALL_NODE_CHECKS_OK');
""";
    }
}
