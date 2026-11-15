using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using LexBrowser.Core.AdBlock.Brave;
using Microsoft.Web.WebView2.Core;

namespace LexBrowser.Core.AdBlock;

/// <summary>
/// Única fachada pública de evaluación de red y popups para Lex Shield.
/// Enruta YouTube y Dailymotion exclusivamente a sus módulos aislados; el resto del tráfico
/// usa el motor configurado en <see cref="ShieldSettings.Engine"/> (<c>Legacy</c> o <c>Brave</c>).
/// </summary>
public static class ShieldRouter
{
    private static readonly LegacyShieldEngine LegacyEngine = new();
    private static BraveNetworkEngine _braveEngine = new();

    /// <summary>
    /// Contextos seguros de WebView2 para <c>AddWebResourceRequestedFilter</c>.
    /// NOTA CRÍTICA: <see cref="CoreWebView2WebResourceContext.Websocket"/> no debe incluirse nunca aquí;
    /// en WebView2 Runtime desactiva silenciosamente el evento <c>WebResourceRequested</c> para TODOS los recursos.
    /// </summary>
    internal static readonly CoreWebView2WebResourceContext[] SupportedWebResourceContexts =
    {
        CoreWebView2WebResourceContext.Document,
        CoreWebView2WebResourceContext.Script,
        CoreWebView2WebResourceContext.XmlHttpRequest,
        CoreWebView2WebResourceContext.Fetch,
        CoreWebView2WebResourceContext.Image,
        CoreWebView2WebResourceContext.Ping,
        CoreWebView2WebResourceContext.Stylesheet,
        CoreWebView2WebResourceContext.Font,
        CoreWebView2WebResourceContext.Media,
        CoreWebView2WebResourceContext.Other
    };

    internal static string FormatNetworkDecisionLogReason(
        in ShieldNetworkDecision decision,
        Uri uri,
        string frameDesc,
        CoreWebView2WebResourceContext resourceContext,
        string engineName,
        string fallbackReason)
    {
        bool isSubframe = !string.IsNullOrEmpty(frameDesc) &&
                          frameDesc.StartsWith("Subframe", StringComparison.OrdinalIgnoreCase);
        string rule = !string.IsNullOrWhiteSpace(decision.Reason) ? decision.Reason : fallbackReason;
        return $"{rule} | Engine={engineName} | Ctx={resourceContext} | Subframe={isSubframe} | Host={uri.Host}";
    }

    public static string GetConfiguredEngineName()
        => ShieldSettings.NormalizeEngine(SettingsManager.Current?.Shield?.Engine);

    public static bool IsBraveActive
        => string.Equals(GetConfiguredEngineName(), ShieldSettings.EngineBrave, StringComparison.Ordinal);

    public static void PreloadIfConfigured()
    {
        if (IsBraveActive)
        {
            BraveShieldData.PreloadInBackground();
        }
    }

    public static IShieldEngine GetGeneralEngine(string? engineOverride = null)
    {
        string engine = string.IsNullOrWhiteSpace(engineOverride)
            ? GetConfiguredEngineName()
            : ShieldSettings.NormalizeEngine(engineOverride);

        return string.Equals(engine, ShieldSettings.EngineBrave, StringComparison.Ordinal)
            ? _braveEngine
            : LegacyEngine;
    }

    /// <summary>
    /// Permite inyectar una instancia de prueba del motor Brave (para suites unitarias).
    /// </summary>
    internal static void SetBraveEngineForTests(BraveNetworkEngine? engine)
        => _braveEngine = engine ?? new BraveNetworkEngine();

    public static ShieldNetworkDecision EvaluateWebResource(
        Uri uri,
        string? originHost,
        string? policyHost,
        string? mainTabHost,
        string? tabUrl,
        string? referer,
        EffectiveSitePolicy policy,
        CoreWebView2WebResourceContext resourceContext,
        string? secFetchDest = null,
        string? httpMethod = null,
        string? engineOverride = null)
    {
        if (uri == null || !policy.ShieldEnabled)
            return ShieldNetworkDecision.Allow;

        bool isYouTubeAdapterContext =
            MediaShieldRules.IsServiceHost(policyHost) ||
            MediaShieldRules.IsServiceOrStreamHost(uri.Host);

        bool isYouTubeIsolated =
            isYouTubeAdapterContext ||
            ShieldIsolation.IsYouTubeHost(uri.Host) ||
            ShieldIsolation.IsYouTubeHost(originHost) ||
            ShieldIsolation.IsYouTubeHost(policyHost);

        bool isDailymotionIsolated =
            ShieldIsolation.IsDailymotionContext(uri, mainTabHost, tabUrl, referer, originHost);

        if (isYouTubeIsolated || isDailymotionIsolated)
        {
            bool allowNetworkBlock = !isYouTubeAdapterContext || policy.YouTubeAdapter;
            return LegacyEngine.EvaluateIsolatedNetwork(
                uri, originHost, policy, resourceContext, allowNetworkBlock);
        }

        return GetGeneralEngine(engineOverride).EvaluateNetwork(
            uri, originHost, policy, resourceContext, secFetchDest, httpMethod);
    }

    public static bool ShouldBlockUrl(
        Uri? uri,
        string? originHost = null,
        EffectiveSitePolicy? policy = null,
        CoreWebView2WebResourceContext resourceContext = CoreWebView2WebResourceContext.Other,
        string? engineOverride = null)
    {
        if (uri == null)
            return false;

        string policyHost = !string.IsNullOrWhiteSpace(originHost) ? originHost : uri.Host;
        var resolved = policy ?? SitePolicy.Resolve(policyHost);
        if (!resolved.ShieldEnabled || !resolved.Network)
            return false;

        var decision = EvaluateWebResource(
            uri,
            originHost,
            policyHost,
            originHost,
            tabUrl: null,
            referer: null,
            resolved,
            resourceContext,
            engineOverride: engineOverride);
        return decision.ShouldBlock;
    }

    public static bool ShouldBlockPopup(
        Uri targetUri,
        Uri? originUri,
        EffectiveSitePolicy originPolicy,
        bool isFromSubframe,
        bool isUserInitiated,
        out string reason,
        string? engineOverride = null)
    {
        reason = string.Empty;
        if (targetUri == null || !originPolicy.ShieldEnabled || !originPolicy.Popups)
            return false;

        string? originHost = originUri?.Host;
        bool isIsolated =
            ShieldIsolation.IsIsolatedHost(targetUri.Host) ||
            ShieldIsolation.IsIsolatedHost(originHost);

        if (isIsolated)
        {
            return LegacyEngine.ShouldBlockPopup(
                targetUri, originUri, originPolicy, isFromSubframe, isUserInitiated, out reason);
        }

        return GetGeneralEngine(engineOverride).ShouldBlockPopup(
            targetUri, originUri, originPolicy, isFromSubframe, isUserInitiated, out reason);
    }

    public static bool IsGenericHideEnabled(Uri? documentUri, string? originHost, string? engineOverride = null)
    {
        if (documentUri == null)
            return false;

        if (ShieldIsolation.IsIsolatedHost(documentUri.Host) || ShieldIsolation.IsIsolatedHost(originHost))
            return false;

        return GetGeneralEngine(engineOverride).IsGenericHideEnabled(documentUri, originHost);
    }

    /// <summary>
    /// Determina de forma rápida si la respuesta Document debe diferirse brevemente para
    /// registrar scriptlets / reglas cosméticas específicas del dominio antes del parsing HTML.
    /// </summary>
    public static bool ShouldDeferDocumentForBraveScriptlets(
        Uri? documentUri,
        string? originHost,
        EffectiveSitePolicy policy,
        bool isSubframe,
        string? engineOverride = null)
    {
        if (!IsEngineBrave(engineOverride) || documentUri == null)
            return false;

        if (!policy.ShieldEnabled || !policy.Cosmetic)
            return false;

        if (isSubframe && !policy.Subframes)
            return false;

        if (ShieldIsolation.IsIsolatedHost(documentUri.Host) || ShieldIsolation.IsIsolatedHost(originHost))
            return false;

        var data = BraveShieldData.TryGetDefault();
        if (data == null)
            return false;

        return data.GetCosmeticEngine().HasHostRules(documentUri.Host);
    }

    private static readonly JsonSerializerOptions PlanJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly string DisabledPlanJson = """{"enabled":false}""";

    public static IReadOnlyList<string> EvaluateCspPolicies(
        Uri? documentUri,
        string? originHost,
        bool isSubdocument,
        EffectiveSitePolicy policy,
        out bool enforceSandboxNoPopups,
        string? engineOverride = null)
    {
        enforceSandboxNoPopups = false;
        if (!IsEngineBrave(engineOverride) || documentUri == null || !policy.ShieldEnabled || !policy.Network)
            return Array.Empty<string>();

        if (ShieldIsolation.IsIsolatedHost(documentUri.Host) || ShieldIsolation.IsIsolatedHost(originHost))
            return Array.Empty<string>();

        return _braveEngine.EvaluateCspPolicies(
            documentUri,
            originHost,
            isSubdocument,
            policy,
            out enforceSandboxNoPopups);
    }

    /// <summary>
    /// Resuelve de forma síncrona el plan completo por documento/iframe (scriptlets, CSP, cosmética host/genérica y defensa anti-overlay)
    /// evitando carreras de carga inicial y respetando aislamiento YT/DM, Auth y políticas por sitio.
    /// </summary>
    public static string BuildDocumentPlanJson(
        string? href,
        string? parentHref,
        bool isTop,
        Uri? mainTabUri,
        bool temporaryCompatMode,
        string? engineOverride = null)
    {
        if (!TryResolveFrameContext(
                href, parentHref, isTop, mainTabUri, temporaryCompatMode, engineOverride,
                out Uri? effectiveUri, out string? originHost, out EffectiveSitePolicy policy))
        {
            return DisabledPlanJson;
        }

        BraveShieldData? data = _braveEngine.GetShieldData();
        bool noPopup = false;
        IReadOnlyList<string> cspPolicies = policy.Network
            ? EvaluateCspPolicies(effectiveUri, originHost, isSubdocument: !isTop, policy, out noPopup, engineOverride)
            : Array.Empty<string>();
        if (!policy.Popups)
            noPopup = false;

        bool ghide = !policy.Cosmetic || _braveEngine.IsGenericHideEnabled(effectiveUri!, originHost);
        HostCosmeticResources? cos = null;
        string? scriptlets = null;
        string? hostCss = null;
        string? styleCss = null;
        string? mgCss = null;
        IReadOnlyList<string>? proc = null;

        if (data != null && policy.Cosmetic)
        {
            var cosEngine = data.GetCosmeticEngine();
            var resources = data.GetResourceStore();
            cos = cosEngine.EvaluateHost(effectiveUri!.Host, ghide, resources);

            string builtScriptlets = BraveScriptletEngine.BuildScriptletsBundle(cos, resources);
            if (!string.IsNullOrEmpty(builtScriptlets))
                scriptlets = builtScriptlets;

            string builtHostCss = BraveScriptletEngine.BuildHostHideCss(cos);
            if (!string.IsNullOrEmpty(builtHostCss))
                hostCss = builtHostCss;

            string builtStyleCss = BraveScriptletEngine.BuildHostStyleCss(cos);
            if (!string.IsNullOrEmpty(builtStyleCss))
                styleCss = builtStyleCss;

            if (!ghide)
            {
                string builtMg = BraveScriptletEngine.BuildMiscGenericCss(cosEngine, cos);
                if (!string.IsNullOrEmpty(builtMg))
                    mgCss = builtMg;
            }

            if (cos.ProceduralRules.Count > 0)
                proc = cos.ProceduralRules;
        }

        bool ovl = policy.Overlays && !ghide && (cos == null || !cos.UnhiddenSelectors.Contains("*"));
        string normalizedHost = effectiveUri!.Host.Trim().TrimEnd('.').ToLowerInvariant();

        return JsonSerializer.Serialize(new
        {
            enabled = true,
            host = normalizedHost,
            cosmetic = policy.Cosmetic,
            ghide,
            scriptlets,
            hostCss,
            styleCss,
            mgCss,
            proc,
            csp = cspPolicies.Count > 0 ? cspPolicies : null,
            noPopup = noPopup ? (bool?)true : null,
            ovl = ovl ? (bool?)true : null
        }, PlanJsonOptions);
    }

    public static string QueryCosmeticsFromBridgeJson(
        string? href,
        string? parentHref,
        bool isTop,
        bool initial,
        string? classesJson,
        string? idsJson,
        Uri? mainTabUri,
        bool temporaryCompatMode,
        string? engineOverride = null)
    {
        if (!TryResolveFrameContext(
                href, parentHref, isTop, mainTabUri, temporaryCompatMode, engineOverride,
                out Uri? effectiveUri, out _, out EffectiveSitePolicy policy))
        {
            return """{"type":"brave_cosmetic_apply","ghide":true}""";
        }

        string[]? classes = ParseJsonStringList(classesJson);
        string[]? ids = ParseJsonStringList(idsJson);

        return BuildCosmeticQueryResponseJson(
            effectiveUri!.Host,
            initial,
            classes,
            ids,
            mainTabUri ?? effectiveUri,
            policy,
            engineOverride);
    }

    private static string[]? ParseJsonStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]")
            return null;
        try
        {
            return JsonSerializer.Deserialize<string[]>(json);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryResolveFrameContext(
        string? href,
        string? parentHref,
        bool isTop,
        Uri? mainTabUri,
        bool temporaryCompatMode,
        string? engineOverride,
        out Uri? effectiveUri,
        out string? originHost,
        out EffectiveSitePolicy policy)
    {
        effectiveUri = null;
        originHost = null;
        policy = default;

        if (!IsEngineBrave(engineOverride))
            return false;

        Uri? docUri = TryParseHttpUri(href);
        Uri? parentUri = TryParseHttpUri(parentHref);
        Uri? safeMainUri = mainTabUri != null && IsHttpScheme(mainTabUri) ? mainTabUri : null;

        if (docUri == null)
        {
            if (isTop)
                return false;

            docUri = parentUri ?? safeMainUri;
            if (docUri == null)
                return false;
        }

        effectiveUri = docUri;
        originHost = isTop
            ? docUri.Host
            : (!string.IsNullOrWhiteSpace(parentUri?.Host)
                ? parentUri.Host
                : (!string.IsNullOrWhiteSpace(safeMainUri?.Host) ? safeMainUri.Host : docUri.Host));

        string policyHost = !string.IsNullOrWhiteSpace(safeMainUri?.Host) ? safeMainUri.Host : originHost;

        if (ShieldIsolation.IsIsolatedHost(docUri.Host) ||
            ShieldIsolation.IsIsolatedHost(originHost) ||
            ShieldIsolation.IsIsolatedHost(policyHost) ||
            ShieldExemptions.IsPaymentOrAuthHost(docUri.Host) ||
            ShieldExemptions.IsPaymentOrAuthHost(policyHost))
        {
            return false;
        }

        policy = SitePolicy.Resolve(policyHost, temporaryCompatMode);
        if (!policy.ShieldEnabled)
            return false;

        // En motor Brave, proteger iframes/about:blank en Modo Normal aunque EnableWebShieldInSubframes
        // valga false (ese flag global desactiva solo WebShield Legacy para no romper YT/DM);
        // en Modo Compatible o si el usuario excluyó subframes en este sitio concreto sí se omiten.
        if (!isTop && (policy.IsCompatMode || SitePolicy.IsSiteModuleExplicitlyDisabled(policyHost, SitePolicy.ModSubframes)))
            return false;

        return true;
    }

    private static Uri? TryParseHttpUri(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        return Uri.TryCreate(raw, UriKind.Absolute, out Uri? u) && IsHttpScheme(u) ? u : null;
    }

    private static bool IsHttpScheme(Uri u)
        => u.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
           u.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    public static string? BuildDocumentInjectionScript(
        Uri? documentUri,
        string? originHost,
        EffectiveSitePolicy policy,
        string? engineOverride = null)
    {
        if (!IsEngineBrave(engineOverride) || documentUri == null)
            return null;

        if (!policy.ShieldEnabled || !policy.Cosmetic)
            return null;

        if (ShieldIsolation.IsIsolatedHost(documentUri.Host) || ShieldIsolation.IsIsolatedHost(originHost))
            return null;

        var data = _braveEngine.GetShieldData() ?? BraveShieldData.TryGetDefault();
        if (data == null)
            return null;

        bool ghide = IsGenericHideEnabled(documentUri, originHost, engineOverride);
        var resources = data.GetResourceStore();
        var cos = data.GetCosmeticEngine().EvaluateHost(documentUri.Host, ghide, resources);
        return BraveScriptletEngine.BuildHostInjectionScript(documentUri.Host, cos, resources, policy);
    }

    public static string BuildCosmeticQueryResponseJson(
        string? host,
        bool initial,
        IReadOnlyList<string>? classes,
        IReadOnlyList<string>? ids,
        Uri? tabUri,
        EffectiveSitePolicy policy,
        string? engineOverride = null)
    {
        string normalizedHost = (host ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        if (!IsEngineBrave(engineOverride) ||
            string.IsNullOrEmpty(normalizedHost) ||
            ShieldIsolation.IsIsolatedHost(normalizedHost) ||
            ShieldIsolation.IsIsolatedHost(tabUri?.Host) ||
            !policy.ShieldEnabled ||
            !policy.Cosmetic)
        {
            return JsonSerializer.Serialize(new
            {
                type = "brave_cosmetic_apply",
                host = normalizedHost,
                ghide = true
            });
        }

        Uri? evalUri = tabUri;
        if (evalUri == null)
            Uri.TryCreate("https://" + normalizedHost + "/", UriKind.Absolute, out evalUri);

        bool genericHide = IsGenericHideEnabled(evalUri, normalizedHost, engineOverride);
        if (genericHide)
        {
            return JsonSerializer.Serialize(new
            {
                type = "brave_cosmetic_apply",
                host = normalizedHost,
                ghide = true
            });
        }

        var data = _braveEngine.GetShieldData() ?? BraveShieldData.TryGetDefault();
        if (data == null)
        {
            return JsonSerializer.Serialize(new
            {
                type = "brave_cosmetic_apply",
                host = normalizedHost,
                ghide = false
            });
        }

        var cosEngine = data.GetCosmeticEngine();
        string css = cosEngine.QueryGenericCss(normalizedHost, classes, ids, genericHide: false, policy);
        string? mgCss = null;
        if (initial)
        {
            var hostCos = cosEngine.EvaluateHost(normalizedHost, genericHide: false, data.GetResourceStore());
            mgCss = BraveScriptletEngine.BuildMiscGenericCss(cosEngine, hostCos);
        }

        return JsonSerializer.Serialize(new
        {
            type = "brave_cosmetic_apply",
            host = normalizedHost,
            ghide = false,
            css = string.IsNullOrEmpty(css) ? null : css,
            mgCss = string.IsNullOrEmpty(mgCss) ? null : mgCss
        });
    }

    internal static (string MainTabHost, string OriginHost, string PolicyHost, string FrameDesc) ClassifyWebResourceRequest(
        Uri uri,
        Uri? activeMainUri,
        string? tabUrlFallback,
        string? referer,
        string? secFetchDest,
        CoreWebView2WebResourceContext resourceContext)
    {
        bool isExplicitSubframe =
            string.Equals(secFetchDest, "iframe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(secFetchDest, "frame", StringComparison.OrdinalIgnoreCase);

        bool isExplicitTopDocument =
            resourceContext == CoreWebView2WebResourceContext.Document &&
            !isExplicitSubframe &&
            (string.Equals(secFetchDest, "document", StringComparison.OrdinalIgnoreCase) ||
             (activeMainUri != null && string.Equals(activeMainUri.AbsoluteUri, uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase)));

        string mainTabHost = activeMainUri?.Host ?? "unknown";
        if (mainTabHost == "unknown" &&
            !string.IsNullOrEmpty(tabUrlFallback) &&
            Uri.TryCreate(tabUrlFallback, UriKind.Absolute, out var tabUrlUri))
        {
            mainTabHost = tabUrlUri.Host;
        }

        if (isExplicitTopDocument)
        {
            return (uri.Host, uri.Host, uri.Host, "Document");
        }

        string originHost = mainTabHost;
        string frameDesc = isExplicitSubframe
            ? $"Subframe:{originHost}|{resourceContext}"
            : resourceContext.ToString();

        if (!string.IsNullOrEmpty(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var refUri))
        {
            if (originHost == "unknown" || !refUri.Host.Equals(originHost, StringComparison.OrdinalIgnoreCase))
            {
                originHost = refUri.Host;
                frameDesc = $"Subframe:{refUri.Host}|{resourceContext}";
            }
        }
        else if (resourceContext == CoreWebView2WebResourceContext.Document)
        {
            if (isExplicitSubframe || (originHost != "unknown" && !uri.Host.Equals(originHost, StringComparison.OrdinalIgnoreCase)))
            {
                originHost = uri.Host;
                frameDesc = $"SubframeDoc:{uri.Host}";
            }
            else
            {
                mainTabHost = uri.Host;
                originHost = uri.Host;
            }
        }

        string policyHost = (mainTabHost != "unknown" && !string.IsNullOrEmpty(mainTabHost))
            ? mainTabHost
            : originHost;
        return (mainTabHost, originHost, policyHost, frameDesc);
    }

    private static bool IsEngineBrave(string? engineOverride)
    {
        string engine = string.IsNullOrWhiteSpace(engineOverride)
            ? GetConfiguredEngineName()
            : ShieldSettings.NormalizeEngine(engineOverride);
        return string.Equals(engine, ShieldSettings.EngineBrave, StringComparison.Ordinal);
    }
}
