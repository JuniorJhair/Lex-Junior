using System;
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
}
