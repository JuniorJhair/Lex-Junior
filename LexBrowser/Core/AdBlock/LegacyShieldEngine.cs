using System;
using LexBrowser.Core.AdBlock.Brave;
using Microsoft.Web.WebView2.Core;

namespace LexBrowser.Core.AdBlock;

/// <summary>
/// Adaptador del motor clásico (<see cref="AdBlockFilter"/> y <see cref="AdBlockResources"/>)
/// bajo la interfaz unificada <see cref="IShieldEngine"/>.
/// También respalda la ruta aislada de YouTube y Dailymotion.
/// </summary>
public sealed class LegacyShieldEngine : IShieldEngine
{
    public string Name => ShieldSettings.EngineLegacy;

    public ShieldNetworkDecision EvaluateNetwork(
        Uri uri,
        string? originHost,
        EffectiveSitePolicy policy,
        CoreWebView2WebResourceContext resourceContext,
        string? secFetchDest = null,
        string? httpMethod = null)
        => EvaluateInternal(uri, originHost, policy, resourceContext, allowNetworkBlock: true);

    internal ShieldNetworkDecision EvaluateIsolatedNetwork(
        Uri uri,
        string? originHost,
        EffectiveSitePolicy policy,
        CoreWebView2WebResourceContext resourceContext,
        bool allowNetworkBlock)
        => EvaluateInternal(uri, originHost, policy, resourceContext, allowNetworkBlock);

    private static ShieldNetworkDecision EvaluateInternal(
        Uri uri,
        string? originHost,
        EffectiveSitePolicy policy,
        CoreWebView2WebResourceContext resourceContext,
        bool allowNetworkBlock)
    {
        if (uri == null || !policy.ShieldEnabled)
            return ShieldNetworkDecision.Allow;

        if (policy.Stubs)
        {
            bool allowThisStub = policy.ImaStub ||
                                 !uri.AbsolutePath.EndsWith("ima3.js", StringComparison.OrdinalIgnoreCase);
            if (allowThisStub &&
                AdBlockResources.TryGetRedirectStub(uri, out byte[] stubBytes, out string stubContentType, originHost, resourceContext))
            {
                return ShieldNetworkDecision.Redirect(
                    stubBytes,
                    stubContentType,
                    "AdBlockResources redirect stub (200 OK)");
            }
        }

        if (allowNetworkBlock && policy.Network && AdBlockFilter.ShouldBlock(uri, originHost, resourceContext))
        {
            return ShieldNetworkDecision.Block("Blocked ad domain or pattern");
        }

        return ShieldNetworkDecision.Allow;
    }

    public bool ShouldBlockPopup(
        Uri targetUri,
        Uri? originUri,
        EffectiveSitePolicy policy,
        bool isFromSubframe,
        bool isUserInitiated,
        out string reason)
    {
        reason = string.Empty;
        if (targetUri == null || !policy.ShieldEnabled || !policy.Popups)
            return false;

        if ((policy.Network && AdBlockFilter.ShouldBlock(targetUri, originUri?.Host, CoreWebView2WebResourceContext.Document)) ||
            AdBlockFilter.IsSuspiciousPopup(targetUri, originUri))
        {
            reason = "AdBlockFilter domain or suspicious pattern";
            return true;
        }

        return false;
    }

    public bool IsGenericHideEnabled(Uri documentUri, string? originHost)
        => false;
}
