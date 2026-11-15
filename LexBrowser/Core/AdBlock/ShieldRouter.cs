using System;
using System.Collections.Generic;
using System.Text.Json;
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

        var data = BraveShieldData.TryGetDefault();
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

        var data = BraveShieldData.TryGetDefault();
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

    private static bool IsEngineBrave(string? engineOverride)
    {
        string engine = string.IsNullOrWhiteSpace(engineOverride)
            ? GetConfiguredEngineName()
            : ShieldSettings.NormalizeEngine(engineOverride);
        return string.Equals(engine, ShieldSettings.EngineBrave, StringComparison.Ordinal);
    }
}
