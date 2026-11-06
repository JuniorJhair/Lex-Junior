using System;
using System.Text;

namespace LexBrowser.Core.AdBlock;

/// <summary>
/// Respuestas señuelo (stubs / mocks) con HTTP 200 OK para recursos publicitarios conocidos
/// que los reproductores de streaming (anime, series, video) esperan para iniciar la reproducción.
/// Basado en las reglas de redirección (\) de Brave Shields y uBlock Origin.
/// </summary>
public static class AdBlockResources
{
    private static readonly byte[] VastEmptyBytes = Encoding.UTF8.GetBytes("<VAST version=\"3.0\"></VAST>");
    private static readonly byte[] PopadsDummyBytes = Encoding.UTF8.GetBytes(
        "window.aclib={runPop:function(){return false;},runBanner:function(){return false;},runSlider:function(){return false;},runInterstitial:function(){return false;},runVideo:function(){return false;}};" +
        "window.popMagic={init:function(){},pop:function(){},capture:function(){},click:function(){},check:function(){return false;}};" +
        "(function(){delete window.PopAds;delete window.popns;Object.defineProperties(window,{PopAds:{value:{}},popns:{value:{}}});})();");
    private static readonly byte[] InstreamStatusBytes = Encoding.UTF8.GetBytes("window.google_ad_status = 1;");
    private static readonly byte[] GoogleImaShimBytes = Encoding.UTF8.GetBytes(GoogleImaScript);

    public static bool TryGetRedirectStub(Uri uri, out byte[] responseBytes, out string contentType)
        => TryGetRedirectStub(uri, out responseBytes, out contentType, null);

    /// <summary>
    /// Comprueba si la URI coincide con un recurso que requiere un reemplazo 200 OK (stub)
    /// en lugar de un bloqueo 403, para que el reproductor no se congele.
    /// </summary>
    public static bool TryGetRedirectStub(Uri uri, out byte[] responseBytes, out string contentType, string? originHost = null)
    {
        string host = uri.Host;
        string path = uri.AbsolutePath;
        string full = uri.AbsoluteUri;

        // 1. Google IMA SDK (ima3.js) - evita la pantalla negra en reproductores con ad-bidding.
        // Controlado por el interruptor independiente EnableImaSdkStub en Configuración.
        // Excepción: Dailymotion cuenta con su propio módulo aislado (DailymotionOptimizer) que
        // requiere el SDK original para validar el ciclo de reproducción y acelerar/saltar los anuncios sin congelar el reproductor.
        bool isDailymotion = (!string.IsNullOrEmpty(originHost) &&
                             (originHost.Equals("dailymotion.com", StringComparison.OrdinalIgnoreCase) ||
                              originHost.EndsWith(".dailymotion.com", StringComparison.OrdinalIgnoreCase))) ||
                             full.Contains("dailymotion.com", StringComparison.OrdinalIgnoreCase);

        if (!isDailymotion &&
            SettingsManager.Current?.EnableImaSdkStub != false &&
            host.EndsWith("googleapis.com", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/ima3.js", StringComparison.OrdinalIgnoreCase))
        {
            responseBytes = GoogleImaShimBytes;
            contentType = "Content-Type: application/javascript; charset=utf-8";
            return true;
        }

        // 2. VAST Empty XML para reproductores de streaming (Fluid Player, VideoJS, JWPlayer, etc.)
        // Al responder '0 anuncios en VAST' con 200 OK, el reproductor inicia inmediatamente el video principal
        // sin reproducir el comercial pre-roll ni contar hacia atrás el reloj de espera.
        // Excepción: Dailymotion cuenta con su propio módulo aislado (DailymotionOptimizer).
        bool isVast = !isDailymotion &&
                      (full.Contains("ad_type=vast", StringComparison.OrdinalIgnoreCase) ||
                       full.Contains("format=vast", StringComparison.OrdinalIgnoreCase) ||
                       full.Contains("output=vast", StringComparison.OrdinalIgnoreCase) ||
                       full.Contains("type=vast", StringComparison.OrdinalIgnoreCase) ||
                       path.Contains("/vast", StringComparison.OrdinalIgnoreCase) ||
                       path.EndsWith("vast.xml", StringComparison.OrdinalIgnoreCase) ||
                       host.Contains("adtng.com", StringComparison.OrdinalIgnoreCase) ||
                       ((host.Contains("exoclick.com", StringComparison.OrdinalIgnoreCase) ||
                         host.Contains("magsrv.com", StringComparison.OrdinalIgnoreCase) ||
                         host.Contains("realsrv.com", StringComparison.OrdinalIgnoreCase) ||
                         host.Contains("pemsrv.com", StringComparison.OrdinalIgnoreCase)) && (path.Contains("splash.php", StringComparison.OrdinalIgnoreCase) || path.Contains("vast.php", StringComparison.OrdinalIgnoreCase) || path.Contains("tag.php", StringComparison.OrdinalIgnoreCase) || path.Contains("/delivery/", StringComparison.OrdinalIgnoreCase))) ||
                       ((host.Contains("trafficjunky.net", StringComparison.OrdinalIgnoreCase) ||
                         host.Contains("trafficjunky.com", StringComparison.OrdinalIgnoreCase)) && (path.Contains("/video", StringComparison.OrdinalIgnoreCase) || path.Contains("/vast", StringComparison.OrdinalIgnoreCase) || path.Contains("/delivery/", StringComparison.OrdinalIgnoreCase) || path.Contains("impression", StringComparison.OrdinalIgnoreCase))) ||
                       ((host.Contains("tsyndicate.com", StringComparison.OrdinalIgnoreCase) ||
                         host.Contains("ero-advertising.com", StringComparison.OrdinalIgnoreCase)) && (path.Contains("/vast", StringComparison.OrdinalIgnoreCase) || path.Contains("/video", StringComparison.OrdinalIgnoreCase) || path.Contains("splash", StringComparison.OrdinalIgnoreCase) || path.Contains("/delivery/", StringComparison.OrdinalIgnoreCase))));

        if (isVast)
        {
            responseBytes = VastEmptyBytes;
            contentType = "Content-Type: text/xml; charset=utf-8";
            return true;
        }

        // 3. PopAds / PopCash / Aclib / PopMagic / TSyndicate dummy
        if (host.Contains("popads.net", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/popads.js", StringComparison.OrdinalIgnoreCase) ||
            (host.Contains("popcash.net", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/pop.js", StringComparison.OrdinalIgnoreCase)) ||
            path.EndsWith("/aclib.js", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/ad-provider.js", StringComparison.OrdinalIgnoreCase) ||
            (host.Contains("tsyndicate.com", StringComparison.OrdinalIgnoreCase) && path.Contains("video.instant.message.js", StringComparison.OrdinalIgnoreCase)))
        {
            responseBytes = PopadsDummyBytes;
            contentType = "Content-Type: application/javascript; charset=utf-8";
            return true;
        }

        // 4. DoubleClick Instream Ad Status
        if (path.EndsWith("/instream/ad_status.js", StringComparison.OrdinalIgnoreCase))
        {
            responseBytes = InstreamStatusBytes;
            contentType = "Content-Type: application/javascript; charset=utf-8";
            return true;
        }

        responseBytes = Array.Empty<byte>();
        contentType = "Content-Type: text/plain";
        return false;
    }

    private const string GoogleImaScript = """
/**
 * 
 * Source below is based on Mozilla source code:
 * https://searchfox.org/mozilla-central/rev/d317e93d9a59c9e4c06ada85fbff9f6a1ceaaad1/browser/extensions/webcompat/shims/google-ima.js
 * 
 * Modifications to the original code below this comment:
 * - Avoid JS syntax not supported by older browser versions
 * - Add missing shim event
 * - Modified to avoid jshint warnings as per uBO's config
 * - Added `OmidVerificationVendor` to `ima`
 * - Have `AdError.getInnerError()` return `null`
 * - Have `AdDisplayContainer` constructor add DIV element to container
 * - Added missing event dispatcher functionality
 * - Corrected return type of `Ad.getUniversalAdIds()`
 * - Corrected typo in `UniversalAdIdInfo.getAdIdValue()` method name
 * - Corrected dispatch of LOAD event when preloading is enabled
 * - Corrected dispatch of CONTENT_PAUSE/RESUME_REQUESTED events
 * - Remove test for auto-play in requestAds(): always behave as if auto-play
 *   is disabled
 * 
 * Related issue:
 * - https://github.com/uBlockOrigin/uBlock-issues/issues/2158
 * - https://github.com/uBlockOrigin/uAssets/issues/30134
 * - https://github.com/uBlockOrigin/uAssets/issues/31018
 * 
**/

/* eslint-disable indent */

'use strict';

/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/. */

/**
 * Bug 1713690 - Shim Google Interactive Media Ads ima3.js
 *
 * Many sites use ima3.js for ad bidding and placement, often in conjunction
 * with Google Publisher Tags, Prebid.js and/or other scripts. This shim
 * provides a stubbed-out version of the API which helps work around related
 * site breakage, such as black bxoes where videos ought to be placed.
 */

if (!window.google || !window.google.ima || !window.google.ima.VERSION) {
  const VERSION = "3.764.0";
  const ima = {};

  class AdDisplayContainer {
    constructor(containerElement) {
      const divElement = document.createElement("div");
      divElement.style.setProperty("display", "none", "important");
      divElement.style.setProperty("visibility", "collapse", "important");
      containerElement.appendChild(divElement);
    }
    destroy() {}
    initialize() {}
  }

  class ImaSdkSettings {
    constructor() {
      this.c = true;
      this.f = {};
      this.i = false;
      this.l = "";
      this.p = "";
      this.r = 0;
      this.t = "";
      this.v = "";
    }
    getCompanionBackfill() {}
    getDisableCustomPlaybackForIOS10Plus() {
      return this.i;
    }
    getFeatureFlags() {
      return this.f;
    }
    getLocale() {
      return this.l;
    }
    getNumRedirects() {
      return this.r;
    }
    getPlayerType() {
      return this.t;
    }
    getPlayerVersion() {
      return this.v;
    }
    getPpid() {
      return this.p;
    }
    isCookiesEnabled() {
      return this.c;
    }
    setAutoPlayAdBreaks() {}
    setCompanionBackfill() {}
    setCookiesEnabled(c) {
      this.c = !!c;
    }
    setDisableCustomPlaybackForIOS10Plus(i) {
      this.i = !!i;
    }
    setFeatureFlags(f) {
      this.f = f;
    }
    setLocale(l) {
      this.l = l;
    }
    setNumRedirects(r) {
      this.r = r;
    }
    setPlayerType(t) {
      this.t = t;
    }
    setPlayerVersion(v) {
      this.v = v;
    }
    setPpid(p) {
      this.p = p;
    }
    setSessionId(/*s*/) {}
    setVpaidAllowed(/*a*/) {}
    setVpaidMode(/*m*/) {}

    // https://github.com/uBlockOrigin/uBlock-issues/issues/2265#issuecomment-1637094149
    getDisableFlashAds() {
    }
    setDisableFlashAds() {
    }
  }
  ImaSdkSettings.CompanionBackfillMode = {
    ALWAYS: "always",
    ON_MASTER_AD: "on_master_ad",
  };
  ImaSdkSettings.VpaidMode = {
    DISABLED: 0,
    ENABLED: 1,
    INSECURE: 2,
  };

  class EventHandler {
    constructor() {
      this.listeners = new Map();
    }

    _dispatch(e) {
      let listeners = this.listeners.get(e.type);
      listeners = listeners ? Array.from(listeners.values()) : [];
      for (const listener of listeners) {
        try {
          listener(e);
        } catch (r) {
          console.error(r);
        }
      }
    }

    addEventListener(types, c, options, context) {
      if (!Array.isArray(types)) {
        types = [types];
      }

      for (const t of types) {
        if (!this.listeners.has(t)) {
          this.listeners.set(t, new Map());
        }
        this.listeners.get(t).set(c, c.bind(context || this));
      }
    }

    removeEventListener(types, c) {
      if (!Array.isArray(types)) {
        types = [types];
      }

      for (const t of types) {
        const typeSet = this.listeners.get(t);
        if (typeSet) {
          typeSet.delete(c);
        }
      }
    }
  }

  class AdsLoader extends EventHandler {
    constructor() {
      super();
      this.settings = new ImaSdkSettings();
    }
    contentComplete() {}
    destroy() {}
    getSettings() {
      return this.settings;
    }
    getVersion() {
      return VERSION;
    }
    requestAds(_r, _c) {
      requestAnimationFrame(() => {
        const { ADS_MANAGER_LOADED } = AdsManagerLoadedEvent.Type;
        const event = new ima.AdsManagerLoadedEvent(ADS_MANAGER_LOADED, _r, _c);
        this._dispatch(event);
      });
    }
  }

  class AdsManager extends EventHandler {
    constructor() {
      super();
      this.volume = 1;
      this._enablePreloading = false;
    }
    collapse() {}
    configureAdsManager() {}
    destroy() {}
    discardAdBreak() {}
    expand() {}
    focus() {}
    getAdSkippableState() {
      return false;
    }
    getCuePoints() {
      return [0];
    }
    getCurrentAd() {
      return currentAd;
    }
    getCurrentAdCuePoints() {
      return [];
    }
    getRemainingTime() {
      return 0;
    }
    getVolume() {
      return this.volume;
    }
    init(/*w, h, m, e*/) {
      if (this._enablePreloading) {
        this._dispatch(new ima.AdEvent(AdEvent.Type.LOADED));
      }
    }
    isCustomClickTrackingUsed() {
      return false;
    }
    isCustomPlaybackUsed() {
      return false;
    }
    pause() {}
    requestNextAdBreak() {}
    resize(/*w, h, m*/) {}
    resume() {}
    setVolume(v) {
      this.volume = v;
    }
    skip() {}
    start() {
      requestAnimationFrame(() => {
        for (const type of [
          AdEvent.Type.LOADED,
          AdEvent.Type.STARTED,
          AdEvent.Type.COMPLETE,
          AdEvent.Type.ALL_ADS_COMPLETED,
          AdEvent.Type.CONTENT_RESUME_REQUESTED,
        ]) {
          try {
            this._dispatch(new ima.AdEvent(type));
          } catch (e) {
            console.error(e);
          }
        }
      });
    }
    stop() {}
    updateAdsRenderingSettings(/*s*/) {}
  }

  class AdsRenderingSettings {}

  class AdsRequest {
    setAdWillAutoPlay() {}
    setAdWillPlayMuted() {}
    setContinuousPlayback() {}
  }

  class AdPodInfo {
    getAdPosition() {
      return 1;
    }
    getIsBumper() {
      return false;
    }
    getMaxDuration() {
      return -1;
    }
    getPodIndex() {
      return 1;
    }
    getTimeOffset() {
      return 0;
    }
    getTotalAds() {
      return 1;
    }
  }

  class Ad {
    constructor() {
      this._pi = new AdPodInfo();
    }
    getAdId() {
      return "";
    }
    getAdPodInfo() {
      return this._pi;
    }
    getAdSystem() {
      return "";
    }
    getAdvertiserName() {
      return "";
    }
    getApiFramework() {
      return null;
    }
    getCompanionAds() {
      return [];
    }
    getContentType() {
      return "";
    }
    getCreativeAdId() {
      return "";
    }
    getCreativeId() {
      return "";
    }
    getDealId() {
      return "";
    }
    getDescription() {
      return "";
    }
    getDuration() {
      return 8.5;
    }
    getHeight() {
      return 0;
    }
    getMediaUrl() {
      return null;
    }
    getMinSuggestedDuration() {
      return -2;
    }
    getSkipTimeOffset() {
      return -1;
    }
    getSurveyUrl() {
      return null;
    }
    getTitle() {
      return "";
    }
    getTraffickingParameters() {
      return {};
    }
    getTraffickingParametersString() {
      return "";
    }
    getUiElements() {
      return [""];
    }
    getUniversalAdIdRegistry() {
      return "unknown";
    }
    getUniversalAdIds() {
      return [new UniversalAdIdInfo()];
    }
    getUniversalAdIdValue() {
      return "unknown";
    }
    getVastMediaBitrate() {
      return 0;
    }
    getVastMediaHeight() {
      return 0;
    }
    getVastMediaWidth() {
      return 0;
    }
    getWidth() {
      return 0;
    }
    getWrapperAdIds() {
      return [""];
    }
    getWrapperAdSystems() {
      return [""];
    }
    getWrapperCreativeIds() {
      return [""];
    }
    isLinear() {
      return true;
    }
    isSkippable() {
      return true;
    }
  }

  class CompanionAd {
    getAdSlotId() {
      return "";
    }
    getContent() {
      return "";
    }
    getContentType() {
      return "";
    }
    getHeight() {
      return 1;
    }
    getWidth() {
      return 1;
    }
  }

  class AdError {
    constructor(type, code, vast, message, request, context) {
      this.errorCode = code;
      this.message = message;
      this.type = type;
      this.adsRequest = request;
      this.userRequestContext = context;
      this.vastErrorCode = vast;
    }
    getErrorCode() {
      return this.errorCode;
    }
    getInnerError() {
        return null;
    }
    getMessage() {
      return this.message;
    }
    getType() {
      return this.type;
    }
    getVastErrorCode() {
      return this.vastErrorCode;
    }
    toString() {
      return `AdError ${this.errorCode}: ${this.message}`;
    }
  }
  AdError.ErrorCode = {};
  AdError.Type = {};

  const isEngadget = () => {
    try {
      for (const ctx of Object.values(window.vidible._getContexts())) {
        const player = ctx.getPlayer();
        if (!player) { continue;}
        const div = player.div;
        if (!div) { continue; }
        if (div.innerHTML.includes("www.engadget.com")) {
          return true;
        }
      }
    } catch {
    }
    return false;
  };

  const currentAd = isEngadget() ? undefined : new Ad();

  class AdEvent {
    constructor(type) {
      this.type = type;
    }
    getAd() {
      return currentAd;
    }
    getAdData() {
      return {};
    }
  }
  AdEvent.Type = {
    AD_BREAK_READY: "adBreakReady",
    AD_BUFFERING: "adBuffering",
    AD_CAN_PLAY: "adCanPlay",
    AD_METADATA: "adMetadata",
    AD_PROGRESS: "adProgress",
    ALL_ADS_COMPLETED: "allAdsCompleted",
    CLICK: "click",
    COMPLETE: "complete",
    CONTENT_PAUSE_REQUESTED: "contentPauseRequested",
    CONTENT_RESUME_REQUESTED: "contentResumeRequested",
    DURATION_CHANGE: "durationChange",
    EXPANDED_CHANGED: "expandedChanged",
    FIRST_QUARTILE: "firstQuartile",
    IMPRESSION: "impression",
    INTERACTION: "interaction",
    LINEAR_CHANGE: "linearChange",
    LINEAR_CHANGED: "linearChanged",
    LOADED: "loaded",
    LOG: "log",
    MIDPOINT: "midpoint",
    PAUSED: "pause",
    RESUMED: "resume",
    SKIPPABLE_STATE_CHANGED: "skippableStateChanged",
    SKIPPED: "skip",
    STARTED: "start",
    THIRD_QUARTILE: "thirdQuartile",
    USER_CLOSE: "userClose",
    VIDEO_CLICKED: "videoClicked",
    VIDEO_ICON_CLICKED: "videoIconClicked",
    VIEWABLE_IMPRESSION: "viewable_impression",
    VOLUME_CHANGED: "volumeChange",
    VOLUME_MUTED: "mute",
  };

  class AdErrorEvent {
    constructor(error) {
      this.type = "adError";
      this.error = error;
    }
    getError() {
      return this.error;
    }
    getUserRequestContext() {
      return this.error?.userRequestContext || {};
    }
  }
  AdErrorEvent.Type = {
    AD_ERROR: "adError",
  };

  const manager = new AdsManager();

  class AdsManagerLoadedEvent {
    constructor(type, request, context) {
      this.type = type;
      this.adsRequest = request;
      this.userRequestContext = context;
    }
    getAdsManager(c, settings) {
      if (settings && settings.enablePreloading) {
        manager._enablePreloading = true;
      }
      return manager;
    }
    getUserRequestContext() {
      return this.userRequestContext || {};
    }
  }
  AdsManagerLoadedEvent.Type = {
    ADS_MANAGER_LOADED: "adsManagerLoaded",
  };

  class CustomContentLoadedEvent {}
  CustomContentLoadedEvent.Type = {
    CUSTOM_CONTENT_LOADED: "deprecated-event",
  };

  class CompanionAdSelectionSettings {}
  CompanionAdSelectionSettings.CreativeType = {
    ALL: "All",
    FLASH: "Flash",
    IMAGE: "Image",
  };
  CompanionAdSelectionSettings.ResourceType = {
    ALL: "All",
    HTML: "Html",
    IFRAME: "IFrame",
    STATIC: "Static",
  };
  CompanionAdSelectionSettings.SizeCriteria = {
    IGNORE: "IgnoreSize",
    SELECT_EXACT_MATCH: "SelectExactMatch",
    SELECT_NEAR_MATCH: "SelectNearMatch",
  };

  class AdCuePoints {
    getCuePoints() {
      return [];
    }
  }

  class AdProgressData {}

  class UniversalAdIdInfo {
    getAdIdRegistry() {
      return "";
    }
    getAdIdValue() {
      return "";
    }
  }

  Object.assign(ima, {
    AdCuePoints,
    AdDisplayContainer,
    AdError,
    AdErrorEvent,
    AdEvent,
    AdPodInfo,
    AdProgressData,
    AdsLoader,
    AdsManager: manager,
    AdsManagerLoadedEvent,
    AdsRenderingSettings,
    AdsRequest,
    CompanionAd,
    CompanionAdSelectionSettings,
    CustomContentLoadedEvent,
    gptProxyInstance: {},
    ImaSdkSettings,
    OmidAccessMode: {
      DOMAIN: "domain",
      FULL: "full",
      LIMITED: "limited",
    },
    OmidVerificationVendor: {
      1: "OTHER",
      2: "GOOGLE",
      GOOGLE: 2,
      OTHER: 1
    },
    settings: new ImaSdkSettings(),
    UiElements: {
      AD_ATTRIBUTION: "adAttribution",
      COUNTDOWN: "countdown",
    },
    UniversalAdIdInfo,
    VERSION,
    ViewMode: {
      FULLSCREEN: "fullscreen",
      NORMAL: "normal",
    },
  });

  if (!window.google) {
    window.google = {};
  }

  window.google.ima = ima;
}

/*
ad.doubleclick.net bid.g.doubleclick.net ggpht.com google.co.uk google.com
googleads.g.doubleclick.net googleads4.g.doubleclick.net googleadservices.com
googlesyndication.com googleusercontent.com gstatic.com gvt1.com prod.google.com
pubads.g.doubleclick.net s0.2mdn.net static.doubleclick.net
surveys.g.doubleclick.net youtube.com ytimg.com
*/

""";
}
