using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using LexBrowser.Core.AdBlock.ShieldCore;
using Microsoft.Web.WebView2.Core;

namespace LexBrowser.Core.AdBlock;

/// <summary>
/// Única fachada pública de evaluación de red, popups, cosmética y exenciones para <c>LexShield Core</c>.
/// Enruta YouTube y Dailymotion exclusivamente a sus módulos aislados; el resto del tráfico
/// pasa por el pipeline unificado <see cref="LexShieldCoreEngine"/>.
/// </summary>
public static class ShieldRouter
{
    private static LexShieldCoreEngine _coreEngine = new();

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

    internal static bool TryFormatAllowedNetworkLog(
        in ShieldNetworkDecision decision,
        Uri uri,
        string? policyHost,
        string? currentHost,
        string frameDesc,
        CoreWebView2WebResourceContext resourceContext,
        string engineName,
        bool diagnosticMode,
        out string reasonText,
        out bool bypassDiagnosticFilter)
    {
        if (decision.IsDataUnavailable)
        {
            bypassDiagnosticFilter = true;
            reasonText = FormatNetworkDecisionLogReason(
                in decision,
                uri,
                frameDesc,
                resourceContext,
                engineName,
                "LexShield data == null (fail-open: shield data not loaded)");
            return true;
        }

        bypassDiagnosticFilter = false;
        if (!diagnosticMode)
        {
            reasonText = string.Empty;
            return false;
        }

        string reqHost = uri.Host;
        string? effectiveMainHost = !string.IsNullOrEmpty(currentHost)
            ? currentHost
            : (!string.IsNullOrEmpty(policyHost) ? policyHost : null);
        bool isSubframeReq = frameDesc.StartsWith("Subframe", StringComparison.OrdinalIgnoreCase);

        string? allowKind = null;
        if (isSubframeReq)
            allowKind = "Subframe network request";
        else if (resourceContext == CoreWebView2WebResourceContext.Document)
            allowKind = "Document navigation request";
        else if (decision.HasException)
            allowKind = "Exception rule matched";
        else if (!string.IsNullOrEmpty(effectiveMainHost) &&
                 !PublicSuffix.HostMatchesDomain(reqHost, effectiveMainHost) &&
                 !AdBlockFilter.IsTrustedDomain(reqHost))
            allowKind = "External third-party resource";

        if (allowKind == null)
        {
            reasonText = string.Empty;
            return false;
        }

        reasonText = FormatNetworkDecisionLogReason(
            in decision, uri, frameDesc, resourceContext, engineName, $"{allowKind} (no match)");
        return true;
    }

    public static string GetConfiguredEngineName() => ShieldSettings.EngineLexShield;

    public static void PreloadIfConfigured()
    {
        LexShieldData.PreloadInBackground();
    }

    public static ILexShieldEngine GetGeneralEngine(string? engineOverride = null)
        => _coreEngine;

    /// <summary>
    /// Permite inyectar una instancia de prueba de <see cref="LexShieldCoreEngine"/> (para suites unitarias).
    /// </summary>
    internal static void SetCoreEngineForTests(LexShieldCoreEngine? engine)
        => _coreEngine = engine ?? new LexShieldCoreEngine();

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
            return _coreEngine.EvaluateIsolatedNetwork(
                uri, originHost, policy, resourceContext, allowNetworkBlock);
        }

        return _coreEngine.EvaluateNetwork(
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
            return _coreEngine.ShouldBlockIsolatedPopup(
                targetUri, originUri, originPolicy, out reason);
        }

        return _coreEngine.ShouldBlockPopup(
            targetUri, originUri, originPolicy, isFromSubframe, isUserInitiated, out reason);
    }

    public static bool IsGenericHideEnabled(Uri? documentUri, string? originHost, string? engineOverride = null)
    {
        if (documentUri == null)
            return false;

        if (ShieldIsolation.IsIsolatedHost(documentUri.Host) ||
            ShieldIsolation.IsIsolatedHost(originHost) ||
            ShieldExemptions.IsBigSite(documentUri.Host) ||
            ShieldExemptions.IsBigSite(originHost) ||
            ShieldExemptions.IsPaymentOrAuthHost(documentUri.Host) ||
            ShieldExemptions.IsPaymentOrAuthHost(originHost))
        {
            return false;
        }

        return _coreEngine.IsGenericHideEnabled(documentUri, originHost);
    }

    /// <summary>
    /// Determina de forma rápida si la respuesta Document debe diferirse brevemente para
    /// registrar scriptlets / reglas cosméticas específicas del dominio antes del parsing HTML.
    /// </summary>
    public static bool ShouldDeferDocumentForScriptlets(
        Uri? documentUri,
        string? originHost,
        EffectiveSitePolicy policy,
        bool isSubframe,
        string? engineOverride = null)
    {
        if (documentUri == null)
            return false;

        if (!policy.ShieldEnabled || !policy.Cosmetic)
            return false;

        if (isSubframe && !policy.Subframes)
            return false;

        var tier = ShieldExemptions.ResolveExemptionTier(documentUri.Host, originHost, policy);
        if (tier != ShieldExemptionTier.FullProtection)
            return false;

        var data = LexShieldData.TryGetDefault();
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
        if (documentUri == null || !policy.ShieldEnabled || !policy.Network)
            return Array.Empty<string>();

        if (ShieldIsolation.IsIsolatedHost(documentUri.Host) ||
            ShieldIsolation.IsIsolatedHost(originHost) ||
            ShieldExemptions.IsPaymentOrAuthHost(documentUri.Host) ||
            ShieldExemptions.IsPaymentOrAuthHost(originHost))
        {
            return Array.Empty<string>();
        }

        return _coreEngine.EvaluateCspPolicies(
            documentUri,
            originHost,
            isSubdocument,
            policy,
            out enforceSandboxNoPopups);
    }

    /// <summary>
    /// Resuelve de forma síncrona el plan completo por documento/iframe (scriptlets, CSP, cosmética host/genérica y defensa anti-overlay)
    /// evitando carreras de carga inicial y respetando aislamiento YT/DM, exenciones de 3 niveles y políticas por sitio.
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
                href, parentHref, isTop, mainTabUri, temporaryCompatMode,
                out Uri? effectiveUri, out string? originHost, out EffectiveSitePolicy policy))
        {
            return DisabledPlanJson;
        }

        var tier = ShieldExemptions.ResolveExemptionTier(effectiveUri!.Host, originHost, policy);
        if (tier == ShieldExemptionTier.TotalExempt)
            return DisabledPlanJson;

        bool isBigSite = ShieldExemptions.IsBigSite(effectiveUri.Host) ||
                         (!string.IsNullOrEmpty(originHost) && ShieldExemptions.IsBigSite(originHost));
        bool allowHostCosmetics = policy.Cosmetic && !isBigSite;

        LexShieldData? data = _coreEngine.GetShieldData();
        bool noPopup = false;
        IReadOnlyList<string> cspPolicies = policy.Network
            ? EvaluateCspPolicies(effectiveUri, originHost, isSubdocument: !isTop, policy, out noPopup, engineOverride)
            : Array.Empty<string>();
        if (!policy.Popups)
            noPopup = false;

        bool ghide = !allowHostCosmetics || _coreEngine.IsGenericHideEnabled(effectiveUri, originHost);
        HostCosmeticResources? cos = null;
        IReadOnlyList<LexShieldScriptletCall>? scriptletCalls = null;
        string? hostCss = null;
        string? styleCss = null;
        string? mgCss = null;
        IReadOnlyList<string>? proc = null;

        if (data != null && allowHostCosmetics)
        {
            var cosEngine = data.GetCosmeticEngine();
            var resources = data.GetResourceStore();
            cos = cosEngine.EvaluateHost(effectiveUri.Host, ghide, resources);

            var calls = LexShieldScriptletEngine.BuildScriptletCalls(cos, resources);
            if (calls.Count > 0)
                scriptletCalls = calls;

            string builtHostCss = LexShieldScriptletEngine.BuildHostHideCss(cos);
            if (!string.IsNullOrEmpty(builtHostCss))
                hostCss = builtHostCss;

            string builtStyleCss = LexShieldScriptletEngine.BuildHostStyleCss(cos);
            if (!string.IsNullOrEmpty(builtStyleCss))
                styleCss = builtStyleCss;

            if (!ghide)
            {
                string builtMg = LexShieldScriptletEngine.BuildMiscGenericCss(cosEngine, cos);
                if (!string.IsNullOrEmpty(builtMg))
                    mgCss = builtMg;
            }

            if (cos.ProceduralRules.Count > 0)
                proc = cos.ProceduralRules;
        }

        bool ovl = policy.Overlays && !ghide && !isBigSite && (cos == null || !cos.UnhiddenSelectors.Contains("*"));
        bool decoyPopup = policy.Popups && !noPopup && !policy.IsCompatMode && !isBigSite;
        string normalizedHost = effectiveUri.Host.Trim().TrimEnd('.').ToLowerInvariant();

        return JsonSerializer.Serialize(new
        {
            enabled = true,
            host = normalizedHost,
            cosmetic = allowHostCosmetics,
            ghide,
            scriptletCalls,
            hostCss,
            styleCss,
            mgCss,
            proc,
            csp = cspPolicies.Count > 0 ? cspPolicies : null,
            noPopup = noPopup ? (bool?)true : null,
            decoyPopup = decoyPopup ? (bool?)true : null,
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
                href, parentHref, isTop, mainTabUri, temporaryCompatMode,
                out Uri? effectiveUri, out _, out EffectiveSitePolicy policy))
        {
            return """{"type":"lex_shield_cosmetic_apply","ghide":true}""";
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
        out Uri? effectiveUri,
        out string? originHost,
        out EffectiveSitePolicy policy)
    {
        effectiveUri = null;
        originHost = null;
        policy = default;

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

        policy = SitePolicy.Resolve(policyHost, temporaryCompatMode);
        if (ShieldExemptions.ResolveExemptionTier(docUri.Host, originHost, policy) == ShieldExemptionTier.TotalExempt ||
            ShieldExemptions.ResolveExemptionTier(docUri.Host, policyHost, policy) == ShieldExemptionTier.TotalExempt)
        {
            return false;
        }

        // En LexShield Core, proteger iframes/about:blank en Modo Normal salvo en Modo Compatible
        // o si el usuario excluyó subframes explícitamente en este sitio concreto.
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
        if (documentUri == null)
            return null;

        var tier = ShieldExemptions.ResolveExemptionTier(documentUri.Host, originHost, policy);
        if (tier != ShieldExemptionTier.FullProtection && tier != ShieldExemptionTier.CosmeticOnly)
            return null;

        var data = _coreEngine.GetShieldData() ?? LexShieldData.TryGetDefault();
        if (data == null)
            return null;

        bool ghide = IsGenericHideEnabled(documentUri, originHost);
        var resources = data.GetResourceStore();
        var cos = data.GetCosmeticEngine().EvaluateHost(documentUri.Host, ghide, resources);
        return LexShieldScriptletEngine.BuildHostInjectionScript(documentUri.Host, cos, resources, policy);
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
        if (string.IsNullOrEmpty(normalizedHost) ||
            ShieldExemptions.ResolveExemptionTier(normalizedHost, tabUri?.Host ?? normalizedHost, policy) != ShieldExemptionTier.FullProtection &&
            ShieldExemptions.ResolveExemptionTier(normalizedHost, tabUri?.Host ?? normalizedHost, policy) != ShieldExemptionTier.CosmeticOnly)
        {
            return JsonSerializer.Serialize(new
            {
                type = "lex_shield_cosmetic_apply",
                host = normalizedHost,
                ghide = true
            });
        }

        Uri? evalUri = tabUri;
        if (evalUri == null)
            Uri.TryCreate("https://" + normalizedHost + "/", UriKind.Absolute, out evalUri);

        bool genericHide = IsGenericHideEnabled(evalUri, normalizedHost);
        if (genericHide)
        {
            return JsonSerializer.Serialize(new
            {
                type = "lex_shield_cosmetic_apply",
                host = normalizedHost,
                ghide = true
            });
        }

        var data = _coreEngine.GetShieldData() ?? LexShieldData.TryGetDefault();
        if (data == null)
        {
            return JsonSerializer.Serialize(new
            {
                type = "lex_shield_cosmetic_apply",
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
            mgCss = LexShieldScriptletEngine.BuildMiscGenericCss(cosEngine, hostCos);
        }

        return JsonSerializer.Serialize(new
        {
            type = "lex_shield_cosmetic_apply",
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
        CoreWebView2WebResourceContext resourceContext,
        string? secFetchSite = null,
        Func<string, bool>? isKnownSubframeHost = null)
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
        else if (originHost != "unknown" &&
                 !PublicSuffix.IsSameRegistrableDomain(uri.Host, originHost) &&
                 ((string.Equals(secFetchSite, "same-origin", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(secFetchSite, "same-site", StringComparison.OrdinalIgnoreCase)) ||
                  (isKnownSubframeHost != null && isKnownSubframeHost(uri.Host))))
        {
            // Subframe cross-origin con Referrer-Policy: no-referrer: su petición same-origin/known-subframe
            // pertenece al iframe de origen (p. ej. cdn.dotanborito.net), no al host superior.
            originHost = uri.Host;
            frameDesc = $"Subframe:{uri.Host}|{resourceContext}";
        }

        string policyHost = (mainTabHost != "unknown" && !string.IsNullOrEmpty(mainTabHost))
            ? mainTabHost
            : originHost;
        return (mainTabHost, originHost, policyHost, frameDesc);
    }

    private static bool IsEngineLexShield => true;
}
