using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using LexBrowser.Core;
using LexBrowser.Core.AdBlock;
using Microsoft.Web.WebView2.Core;
using Xunit;

namespace LexBrowser.Tests;

public class YouTubeOptimizerTests
{
    [Fact]
    public void InjectedScript_ContainsRequiredEndpoints_AndPreservesSafetyInvariants()
    {
        string script = YouTubeOptimizer.InjectedScript;

        // Endpoints cubiertos por la estrategia de Brave / uBlock Origin
        Assert.Contains("'/youtubei/v1/player'", script);
        Assert.Contains("'/youtubei/v1/get_watch'", script);
        Assert.Contains("'/youtubei/v1/reel/reel_watch_sequence'", script);
        Assert.Contains("'/youtubei/v1/reel/reel_item_watch'", script);
        Assert.Contains("u.indexOf('/watch?')", script);
        Assert.Contains("u.indexOf('/playlist?')", script);

        // Dominios oficiales de YouTube soportados
        Assert.Contains("hostname === 'youtube-nocookie.com'", script);
        Assert.Contains("hostname.endsWith('.youtube-nocookie.com')", script);
        Assert.Contains("hostname === 'youtubekids.com'", script);
        Assert.Contains("hostname.endsWith('.youtubekids.com')", script);

        // Invariantes arquitectónicos de seguridad y rendimiento
        Assert.Contains("attachYtStyle", script);
        Assert.Contains("readyState === 4", script);
        Assert.Contains("findSkipButton", script);
        Assert.Contains("onAbnormalityDetected", script);
        Assert.DoesNotContain("'/youtubei/v1/next'", script);
        Assert.DoesNotContain("(document.head || document.documentElement).appendChild(style)", script);
    }

    [Fact]
    public void InjectedScript_DoesNotContainForbiddenOrDangerousConstructs()
    {
        string script = YouTubeOptimizer.InjectedScript;

        Assert.DoesNotContain("window.__lexYtDiag", script);
        Assert.DoesNotContain("opacity: 0 !important", script);
        Assert.DoesNotContain("location.replace", script);
        Assert.DoesNotContain("__lex_youtube_optimizer_injected", script);
        Assert.DoesNotContain("playbackRate = 16", script);

        // Guardia de sesión no enumerable
        Assert.Contains("Object.defineProperty", script);
        Assert.Contains("enumerable: false", script);

        // El script no debe ocultar #player-ads (excepción explícita de Brave)
        Assert.DoesNotContain("#player-ads", script);
    }

    [Theory]
    [InlineData("https://ads.youtube.com/api/v1/ad", "www.youtube.com", CoreWebView2WebResourceContext.XmlHttpRequest, true)]
    [InlineData("https://www.youtube.com/pagead/viewthroughconversion", "www.youtube.com", CoreWebView2WebResourceContext.XmlHttpRequest, true)]
    [InlineData("https://www.youtube.com/pagead/adview", "www.youtube.com", CoreWebView2WebResourceContext.Ping, true)]
    [InlineData("https://www.youtube.com/youtubei/v1/player/ad_break", "www.youtube.com", CoreWebView2WebResourceContext.Fetch, true)]
    [InlineData("https://www.youtube.com/get_midroll_info", "www.youtube.com", CoreWebView2WebResourceContext.XmlHttpRequest, true)]
    [InlineData("https://www.youtube.com/api/stats/ads?break_type=1", "www.youtube.com", CoreWebView2WebResourceContext.Ping, true)]
    [InlineData("https://youtubekids.com/api/stats/ads?ver=2", "youtubekids.com", CoreWebView2WebResourceContext.Ping, true)]
    [InlineData("https://www.youtube.com/get_video_info?video_id=123&adunit=1", "www.youtube.com", CoreWebView2WebResourceContext.XmlHttpRequest, true)]
    [InlineData("https://m.youtube.com/yts/jsbin/ad.js", "m.youtube.com", CoreWebView2WebResourceContext.Script, true)]
    [InlineData("https://rr1---sn-xxx.googlevideo.com/initplayback?source=youtube&c=TVHTML5&oad=1", "www.youtube.com", CoreWebView2WebResourceContext.XmlHttpRequest, true)]
    [InlineData("https://rr1---sn-xxx.googlevideo.com/videoplayback?id=123_ad_456", "www.youtube.com", CoreWebView2WebResourceContext.Media, true)]
    // Permitidos (criterio Brave: nunca romper reproducción, estadísticas legítimas ni atr/log_event)
    [InlineData("https://rr1---sn-xxx.googlevideo.com/videoplayback?id=123", "www.youtube.com", CoreWebView2WebResourceContext.Media, false)]
    [InlineData("https://www.youtube.com/youtubei/v1/player", "www.youtube.com", CoreWebView2WebResourceContext.Fetch, false)]
    [InlineData("https://www.youtube.com/watch?v=33CtMX77KNc", "www.youtube.com", CoreWebView2WebResourceContext.Document, false)]
    [InlineData("https://www.youtube.com/playlist?list=PL123", "www.youtube.com", CoreWebView2WebResourceContext.Fetch, false)]
    [InlineData("https://www.youtube.com/api/stats/watchtime?ns=yt&docid=123", "www.youtube.com", CoreWebView2WebResourceContext.XmlHttpRequest, false)]
    [InlineData("https://www.youtube.com/api/stats/playback?ns=yt&docid=123", "www.youtube.com", CoreWebView2WebResourceContext.XmlHttpRequest, false)]
    [InlineData("https://www.youtube.com/api/stats/playback?el=adunit&adformat=15_8", "www.youtube.com", CoreWebView2WebResourceContext.XmlHttpRequest, false)]
    [InlineData("https://www.youtube.com/api/stats/atr?el=adunit", "www.youtube.com", CoreWebView2WebResourceContext.Ping, false)]
    [InlineData("https://www.youtube.com/api/stats/qoe?docid=123", "www.youtube.com", CoreWebView2WebResourceContext.XmlHttpRequest, false)]
    [InlineData("https://www.youtube.com/youtubei/v1/log_event?alt=json", "www.youtube.com", CoreWebView2WebResourceContext.Fetch, false)]
    [InlineData("https://www.youtube.com/youtubei/v1/next", "www.youtube.com", CoreWebView2WebResourceContext.Fetch, false)]
    [InlineData("https://example.com/pagead/test.js", "example.com", CoreWebView2WebResourceContext.Script, false)]
    public void MediaShieldRules_EvaluatesBraveNetworkRulesCorrectly(
        string url,
        string originHost,
        CoreWebView2WebResourceContext context,
        bool expectedBlocked)
    {
        var uri = new Uri(url);
        Assert.Equal(expectedBlocked, MediaShieldRules.IsAdRequest(uri, originHost, context));
    }

    [Fact]
    public void MediaShieldRules_WantsEmptyJson_MatchesOnlyAdBreakAndMidroll()
    {
        Assert.True(MediaShieldRules.WantsEmptyJson(new Uri("https://www.youtube.com/youtubei/v1/player/ad_break")));
        Assert.True(MediaShieldRules.WantsEmptyJson(new Uri("https://www.youtube.com/get_midroll_info?x=1")));

        Assert.False(MediaShieldRules.WantsEmptyJson(new Uri("https://www.youtube.com/youtubei/v1/player")));
        Assert.False(MediaShieldRules.WantsEmptyJson(new Uri("https://www.youtube.com/api/stats/ads?ver=2")));
        Assert.False(MediaShieldRules.WantsEmptyJson(new Uri("https://example.com/get_midroll_info")));
    }

    [Fact]
    public void MediaShieldRules_CosmeticCss_ExcludesPlayerAds_AndPreservesContent()
    {
        string css = MediaShieldRules.BuildCosmeticCss();

        Assert.Contains("ytd-ad-slot-renderer", css);
        Assert.Contains("#masthead-ad", css);
        Assert.Contains(".ytp-ad-progress", css);
        Assert.Contains("display: none !important;", css);

        // Excepción explícita de Brave (youtube.com#@##player-ads) y estanterías de contenido legítimo
        Assert.DoesNotContain("#player-ads", css);
        Assert.DoesNotContain("ytd-merch-shelf-renderer", css);
        Assert.DoesNotContain("ytd-statement-banner-renderer", css);
    }

    [Fact]
    public void SitePolicy_YouTubeAdapter_RespectsGlobalSiteAndCompatToggles()
    {
        const string ytDomain = "youtube.com";
        var settings = SettingsManager.Current;
        var shield = settings.Shield;
        shield.Sites ??= new System.Collections.Generic.Dictionary<string, SiteShieldProfile>(StringComparer.OrdinalIgnoreCase);

        bool origEnableAdBlocker = settings.EnableAdBlocker;
        bool origShieldEnabled = shield.Enabled;
        bool origEnableYouTube = settings.EnableYouTubeAdBlock;
        bool origAdapterYouTube = shield.Adapters.YouTube;
        bool hadProfile = shield.Sites.TryGetValue(ytDomain, out var prevProfile);

        try
        {
            settings.EnableAdBlocker = true;
            shield.Enabled = true;
            settings.EnableYouTubeAdBlock = true;
            shield.Adapters.YouTube = true;
            shield.Sites.Remove(ytDomain);
            SettingsManager.BumpSettingsRevision();

            // Activo por defecto
            Assert.True(SitePolicy.Resolve("www.youtube.com", temporaryCompatMode: false).YouTubeAdapter);

            // Desactivado en SettingsWindow (EnableYouTubeAdBlock = false)
            settings.EnableYouTubeAdBlock = false;
            shield.Adapters.YouTube = false;
            SettingsManager.BumpSettingsRevision();
            Assert.False(SitePolicy.Resolve("www.youtube.com", temporaryCompatMode: false).YouTubeAdapter);

            // Reactivado globalmente, pero desactivado el módulo Adapter en el popup de escudo del sitio (ChkSiteModAdapter)
            settings.EnableYouTubeAdBlock = true;
            shield.Adapters.YouTube = true;
            var profile = SitePolicy.GetOrCreateSiteProfile(ytDomain);
            profile.Mode = SitePolicy.ModeNormal;
            profile.Disabled.Add(SitePolicy.ModAdapter);
            SettingsManager.BumpSettingsRevision();
            Assert.False(SitePolicy.Resolve("www.youtube.com", temporaryCompatMode: false).YouTubeAdapter);

            // Quitada la excepción del módulo, pero activado Modo Compatible temporal
            profile.Disabled.Clear();
            SettingsManager.BumpSettingsRevision();
            Assert.False(SitePolicy.Resolve("www.youtube.com", temporaryCompatMode: true).YouTubeAdapter);

            // Modo Compatible persistente o escudo apagado en el sitio
            profile.Mode = SitePolicy.ModeOff;
            SettingsManager.BumpSettingsRevision();
            Assert.False(SitePolicy.Resolve("www.youtube.com", temporaryCompatMode: false).YouTubeAdapter);
        }
        finally
        {
            settings.EnableAdBlocker = origEnableAdBlocker;
            shield.Enabled = origShieldEnabled;
            settings.EnableYouTubeAdBlock = origEnableYouTube;
            shield.Adapters.YouTube = origAdapterYouTube;
            if (hadProfile && prevProfile != null)
                shield.Sites[ytDomain] = prevProfile;
            else
                shield.Sites.Remove(ytDomain);

            SettingsManager.BumpSettingsRevision();
        }
    }

    [Fact]
    public void CleanPlayerPayloadJson_RemovesAdPlacementsAndPlayerAds_RecursivelyAndFromShorts()
    {
        string rawJson = """
        {
            "adPlacements": [
                { "adPlacementConfig": { "kind": "AD_PLACEMENT_KIND_START" } }
            ],
            "playerAds": [
                { "playerAdRenderer": { "title": "Sponsored ad" } }
            ],
            "adSlots": [
                { "slotId": "slot-1" }
            ],
            "adBreakHeartbeatParams": {
                "heartbeat": 1
            },
            "videoDetails": {
                "videoId": "test12345",
                "title": "Clean Video Title"
            },
            "playerResponse": {
                "adPlacements": [ { "dummy": 1 } ],
                "playerAds": [ { "dummy": 2 } ],
                "adSlots": [ { "dummy": 3 } ],
                "adBreakHeartbeatParams": { "dummy": 4 },
                "playabilityStatus": {
                    "status": "OK"
                }
            },
            "entries": [
                {
                    "command": {
                        "reelWatchEndpoint": {
                            "adClientParams": {
                                "isAd": true
                            }
                        }
                    }
                },
                {
                    "command": {
                        "reelWatchEndpoint": {
                            "adClientParams": {
                                "isAd": false
                            }
                        }
                    },
                    "title": "Valid Short Video"
                }
            ],
            "reelWatchSequenceResponse": {
                "entries": [
                    {
                        "command": {
                            "reelWatchEndpoint": {
                                "adClientParams": {
                                    "isAd": true
                                }
                            }
                        }
                    },
                    {
                        "title": "Valid Sequence Short"
                    }
                ]
            }
        }
        """;

        string cleaned = YouTubeOptimizer.CleanPlayerPayloadJson(rawJson);
        using var doc = JsonDocument.Parse(cleaned);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("adPlacements", out _));
        Assert.False(root.TryGetProperty("playerAds", out _));
        Assert.False(root.TryGetProperty("adSlots", out _));
        Assert.False(root.TryGetProperty("adBreakHeartbeatParams", out _));

        Assert.True(root.TryGetProperty("videoDetails", out var vd));
        Assert.Equal("Clean Video Title", vd.GetProperty("title").GetString());

        Assert.True(root.TryGetProperty("playerResponse", out var pr));
        Assert.False(pr.TryGetProperty("adPlacements", out _));
        Assert.False(pr.TryGetProperty("playerAds", out _));
        Assert.False(pr.TryGetProperty("adSlots", out _));
        Assert.False(pr.TryGetProperty("adBreakHeartbeatParams", out _));
        Assert.Equal("OK", pr.GetProperty("playabilityStatus").GetProperty("status").GetString());

        Assert.True(root.TryGetProperty("entries", out var entries));
        Assert.Equal(1, entries.GetArrayLength());
        Assert.Equal("Valid Short Video", entries[0].GetProperty("title").GetString());

        Assert.True(root.TryGetProperty("reelWatchSequenceResponse", out var rws));
        Assert.True(rws.TryGetProperty("entries", out var rwsEntries));
        Assert.Equal(1, rwsEntries.GetArrayLength());
        Assert.Equal("Valid Sequence Short", rwsEntries[0].GetProperty("title").GetString());
    }

    [Fact]
    public void RealInjectedScript_ExecutesAndCleansInNodeJsEnvironment_WithoutCatchSuppression()
    {
        string realScript = YouTubeOptimizer.InjectedScript;

        string runnerJs = """
        const vm = require('vm');

        function run() {
            let lastPosted = null;
            const setTimeoutCalls = [];
            const mockNativeTimeoutCb = Object.assign(() => {}, {
                toString: () => 'function () { [native code] }'
            });
            // Simula Function.prototype.toString sobre función nativa en el sandbox
            const nativeFnStub = function() {};
            Object.defineProperty(nativeFnStub, 'toString', { value: () => 'function () { [native code] }' });

            const window = {
                location: { hostname: 'www.youtube.com', href: 'https://www.youtube.com/watch?v=test' },
                chrome: {
                    webview: {
                        postMessage: (m) => { lastPosted = m; }
                    }
                },
                Headers: class Headers {
                    constructor(h) { this._h = Object.assign({}, h); }
                    delete(k) { delete this._h[k.toLowerCase()]; }
                    get(k) { return this._h[k.toLowerCase()]; }
                },
                Response: class Response {
                    constructor(body, init) {
                        this._body = body;
                        this.status = init && init.status || 200;
                        this.statusText = init && init.statusText || 'OK';
                        this.headers = init && init.headers;
                        this.ok = this.status >= 200 && this.status < 300;
                    }
                    clone() {
                        return new window.Response(this._body, { status: this.status, statusText: this.statusText, headers: this.headers });
                    }
                    async text() {
                        return typeof this._body === 'string' ? this._body : JSON.stringify(this._body);
                    }
                },
                setTimeout: (fn, delay) => {
                    setTimeoutCalls.push({ fn, delay });
                    return setTimeoutCalls.length;
                },
                clearTimeout: () => {},
                addEventListener: () => {},
                removeEventListener: () => {}
            };
            window.window = window;

            const document = {
                documentElement: { style: {} },
                body: { style: {}, removeAttribute: () => {} },
                head: {},
                createElement: (tag) => ({ tagName: tag, id: '', textContent: '', appendChild: () => {} }),
                getElementById: () => null,
                querySelector: () => null,
                querySelectorAll: () => [],
                addEventListener: () => {},
                removeEventListener: () => {}
            };

            function MockXMLHttpRequest() {
                this.readyState = 1;
                this._responseText = '';
                this._response = '';
            }
            MockXMLHttpRequest.prototype.open = function(method, url) {
                this.__lex_url = String(url);
            };
            Object.defineProperty(MockXMLHttpRequest.prototype, 'responseText', {
                get: function() { return this._responseText || ''; },
                configurable: true
            });
            Object.defineProperty(MockXMLHttpRequest.prototype, 'response', {
                get: function() { return this._response || ''; },
                configurable: true
            });
            window.XMLHttpRequest = MockXMLHttpRequest;

            window.fetch = async (input) => {
                const url = typeof input === 'string' ? input : (input && input.url || '');
                if (url.includes('/player_clean')) {
                    return new window.Response(JSON.stringify({ videoDetails: { title: 'No Ads' } }));
                }
                if (url.includes('/player') || url.includes('watch?v=') || url.includes('playlist?list=') || url.includes('reel_watch_sequence')) {
                    return new window.Response(JSON.stringify({
                        adPlacements: [{ id: 1 }],
                        playerAds: [{ ad: 2 }],
                        adSlots: [{ slot: 3 }],
                        adBreakHeartbeatParams: { x: 1 },
                        videoDetails: { title: 'Test Video' },
                        playerResponse: { adPlacements: [{ x: 1 }], playabilityStatus: { status: 'OK' } },
                        entries: [
                            { command: { reelWatchEndpoint: { adClientParams: { isAd: true } } } },
                            { command: { reelWatchEndpoint: { adClientParams: { isAd: false } } }, title: 'Real Short' }
                        ]
                    }));
                }
                return new window.Response(JSON.stringify({ regular: 'data' }));
            };

            const sandbox = {
                window,
                document,
                Headers: window.Headers,
                Response: window.Response,
                XMLHttpRequest: window.XMLHttpRequest,
                setTimeout: window.setTimeout,
                clearTimeout: () => {},
                setInterval: () => 1,
                clearInterval: () => {},
                requestAnimationFrame: () => 1,
                MutationObserver: class { observe() {} disconnect() {} },
                console
            };

            const ctx = vm.createContext(sandbox);

            const fs = require('fs');
            const scriptPath = process.argv[2];
            const scriptContent = fs.readFileSync(scriptPath, 'utf8');
            vm.runInContext(scriptContent, ctx);

            // Caso 1: ytInitialPlayerResponse y playerResponse
            sandbox.window.ytInitialPlayerResponse = {
                adPlacements: [1],
                playerAds: [2],
                adSlots: [3],
                title: 'Initial'
            };
            const cleanedInit = sandbox.window.ytInitialPlayerResponse;
            if (cleanedInit.adPlacements || cleanedInit.playerAds || cleanedInit.adSlots) {
                throw new Error('FALLO: ytInitialPlayerResponse conservó claves publicitarias');
            }
            if (cleanedInit.title !== 'Initial') {
                throw new Error('FALLO: ytInitialPlayerResponse perdió propiedad legítima');
            }

            sandbox.window.playerResponse = {
                adPlacements: [1],
                reelWatchSequenceResponse: {
                    entries: [
                        { command: { reelWatchEndpoint: { adClientParams: { isAd: true } } } },
                        { title: 'Clean Seq' }
                    ]
                }
            };
            if (sandbox.window.playerResponse.adPlacements ||
                sandbox.window.playerResponse.reelWatchSequenceResponse.entries.length !== 1) {
                throw new Error('FALLO: playerResponse no limpió anuncios');
            }

            // Caso 2: Desarme de temporizador anti-adblock (17000 ms) sin romper temporizadores normales
            // En V8/Chromium una función creada con .bind() produce 'function () { [native code] }' en Function.prototype.toString.call(fn)
            setTimeoutCalls.length = 0;
            const fakeNative = vm.runInContext('(function() {}).bind(null)', ctx);
            sandbox.window.setTimeout(fakeNative, 17000);
            sandbox.window.setTimeout(() => {}, 500);
            if (setTimeoutCalls.length < 2 || setTimeoutCalls[0].delay !== 1 || setTimeoutCalls[1].delay !== 500) {
                throw new Error('FALLO: setTimeout 17000ms nativo no fue neutralizado correctamente');
            }

            // Caso 2b: Neutralización de onAbnormalityDetected en Promise.prototype.then sin afectar promesas normales
            vm.runInContext(`
                var __markNormal = 0;
                var __markAbnormal = 0;
                window.__pAbnTest = Promise.all([
                    Promise.resolve(42).then(function(v) { __markNormal = v; }),
                    Promise.resolve(1).then(function() { /* onAbnormalityDetected */ __markAbnormal++; })
                ]).then(function() {
                    if (__markNormal !== 42 || __markAbnormal !== 0) {
                        throw new Error('FALLO: Promise.then neutralizó mal onAbnormalityDetected (norm=' + __markNormal + ', abn=' + __markAbnormal + ')');
                    }
                });
            `, ctx);

            return Promise.all([
                sandbox.window.__pAbnTest,
                // Caso 3: fetch /player
                sandbox.window.fetch('/youtubei/v1/player').then(async res => {
                    const txt = await res.text();
                    const data = JSON.parse(txt);
                    if (data.adPlacements || data.playerAds || data.adSlots || data.adBreakHeartbeatParams) {
                        throw new Error('FALLO: fetch /player no eliminó claves de anuncios');
                    }
                    if (!data.videoDetails || data.videoDetails.title !== 'Test Video') {
                        throw new Error('FALLO: fetch /player no preservó videoDetails');
                    }
                    if (data.playerResponse && data.playerResponse.adPlacements) {
                        throw new Error('FALLO: fetch /player no limpió recursivamente playerResponse');
                    }
                    if (!Array.isArray(data.entries) || data.entries.length !== 1 || data.entries[0].title !== 'Real Short') {
                        throw new Error('FALLO: fetch /player no filtró anuncios de Shorts');
                    }
                }),

                // Caso 4: fetch watch?v= (SPA navegación interna)
                sandbox.window.fetch('https://www.youtube.com/watch?v=33CtMX77KNc&spf=navigate').then(async res => {
                    const txt = await res.text();
                    const data = JSON.parse(txt);
                    if (data.adPlacements || data.playerAds || data.adSlots) {
                        throw new Error('FALLO: fetch watch?v= no eliminó claves publicitarias');
                    }
                }),

                // Caso 5: fetch playlist?list=
                sandbox.window.fetch('https://www.youtube.com/playlist?list=PL12345').then(async res => {
                    const txt = await res.text();
                    const data = JSON.parse(txt);
                    if (data.adPlacements || data.playerAds || data.adSlots) {
                        throw new Error('FALLO: fetch playlist?list= no eliminó claves publicitarias');
                    }
                }),

                // Caso 6: fetch respuesta sin campos publicitarios devuelta intacta
                sandbox.window.fetch('/player_clean').then(async res => {
                    const txt = await res.text();
                    const data = JSON.parse(txt);
                    if (!data.videoDetails || data.videoDetails.title !== 'No Ads') {
                        throw new Error('FALLO: respuesta sin anuncios fue alterada');
                    }
                }),

                // Caso 7: XHR readyState === 4
                new Promise((resolve, reject) => {
                    try {
                        const xhr = new sandbox.window.XMLHttpRequest();
                        xhr.open('GET', '/youtubei/v1/player');
                        xhr.readyState = 4;
                        xhr._responseText = JSON.stringify({ adPlacements: [1], playerAds: [2], title: 'xhr_test' });
                        const cleaned = xhr.responseText;
                        const parsed = JSON.parse(cleaned);
                        if (parsed.adPlacements || parsed.playerAds) {
                            return reject(new Error('FALLO: XHR responseText no eliminó claves publicitarias'));
                        }
                        if (parsed.title !== 'xhr_test') {
                            return reject(new Error('FALLO: XHR responseText no preservó claves legítimas'));
                        }
                        resolve();
                    } catch(e) {
                        reject(e);
                    }
                }),

                // Caso 8: Exención total en dominios fuera de YouTube
                new Promise((resolve, reject) => {
                    const otherWindow = Object.assign({}, sandbox.window, {
                        location: { hostname: 'example.com' }
                    });
                    const otherSandbox = Object.assign({}, sandbox, { window: otherWindow });
                    const otherCtx = vm.createContext(otherSandbox);
                    vm.runInContext(scriptContent, otherCtx);
                    otherWindow.ytInitialPlayerResponse = { adPlacements: [1] };
                    if (!otherWindow.ytInitialPlayerResponse.adPlacements) {
                        return reject(new Error('FALLO: el script alteró un dominio externo'));
                    }
                    resolve();
                })
            ]);
        }

        run()
            .then(() => {
                console.log('NODE_SUITE_SUCCESS');
                process.exit(0);
            })
            .catch(err => {
                console.error(err);
                process.exit(1);
            });
        """;

        string tempScriptFile = Path.Combine(Path.GetTempPath(), $"LexTest_Script_{Guid.NewGuid():N}.js");
        string tempRunnerFile = Path.Combine(Path.GetTempPath(), $"LexTest_Runner_{Guid.NewGuid():N}.js");

        try
        {
            File.WriteAllText(tempScriptFile, realScript);
            File.WriteAllText(tempRunnerFile, runnerJs);

            var psi = new ProcessStartInfo("node", $"\"{tempRunnerFile}\" \"{tempScriptFile}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            Assert.NotNull(proc);
            string stdOut = proc.StandardOutput.ReadToEnd();
            string stdErr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            Assert.True(proc.ExitCode == 0, $"La suite en Node.js falló con código {proc.ExitCode}.\nError: {stdErr}\nSalida: {stdOut}");
            Assert.Contains("NODE_SUITE_SUCCESS", stdOut);
        }
        finally
        {
            if (File.Exists(tempScriptFile)) { try { File.Delete(tempScriptFile); } catch { } }
            if (File.Exists(tempRunnerFile)) { try { File.Delete(tempRunnerFile); } catch { } }
        }
    }
}
