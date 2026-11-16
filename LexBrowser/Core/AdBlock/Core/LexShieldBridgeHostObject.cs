using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace LexBrowser.Core.AdBlock.ShieldCore;

/// <summary>
/// Puente síncrono COM (<c>hostObjects.sync.lexShield</c>) expuesto a WebView2 para entregar
/// scriptlets, CSP, selectores cosméticos y defensa anti-overlay antes de que el HTML ejecute scripts,
/// sin bloqueos en <c>WebResourceRequested</c> ni carreras en la carga inicial ni en iframes.
/// </summary>
[ClassInterface(ClassInterfaceType.AutoDual)]
[ComVisible(true)]
public sealed class LexShieldBridgeHostObject
{
    private const string DisabledPlanJson = """{"enabled":false}""";
    private const string EmptyCosmeticsJson = """{"type":"lex_shield_cosmetic_apply","ghide":true}""";
    private const int MaxLoggedBridgeErrors = 25;

    private readonly string _bridgeToken;
    private readonly Func<Uri?> _getMainTabUri;
    private readonly Func<bool> _getTemporaryCompatMode;
    private int _loggedErrorCount;

    public LexShieldBridgeHostObject(
        string bridgeToken,
        Func<Uri?> getMainTabUri,
        Func<bool> getTemporaryCompatMode)
    {
        _bridgeToken = bridgeToken ?? throw new ArgumentNullException(nameof(bridgeToken));
        _getMainTabUri = getMainTabUri ?? throw new ArgumentNullException(nameof(getMainTabUri));
        _getTemporaryCompatMode = getTemporaryCompatMode ?? throw new ArgumentNullException(nameof(getTemporaryCompatMode));
    }

    public string BridgeToken => _bridgeToken;

    private void LogBridgeIssue(string eventType, string url, bool isTop, string reason)
    {
        if (Interlocked.Increment(ref _loggedErrorCount) <= MaxLoggedBridgeErrors || ShieldLogger.DiagnosticMode)
        {
            string origin = "unknown";
            try { origin = _getMainTabUri()?.Host ?? "unknown"; } catch { }
            ShieldLogger.Log(eventType, origin, url ?? "", isTop ? "MainFrame" : "Subframe", reason);
        }
    }

    public string GetDocumentPlanJson(string token, string href, string parentHref, bool isTop)
    {
        if (!string.Equals(token, _bridgeToken, StringComparison.Ordinal))
        {
            if (ShieldLogger.DiagnosticMode)
                LogBridgeIssue("LEXSHIELD_BRIDGE_TOKEN_MISMATCH", href, isTop, "Invalid bridge token on GetDocumentPlanJson");
            return DisabledPlanJson;
        }

        try
        {
            return ShieldRouter.BuildDocumentPlanJson(
                href,
                parentHref,
                isTop,
                _getMainTabUri(),
                _getTemporaryCompatMode());
        }
        catch (Exception ex)
        {
            LogBridgeIssue("LEXSHIELD_BRIDGE_PLAN_ERROR", href, isTop, $"{ex.GetType().Name}: {ex.Message}");
            return DisabledPlanJson;
        }
    }

    public string QueryCosmeticsJson(
        string token,
        string href,
        string parentHref,
        bool isTop,
        bool initial,
        string classesJson,
        string idsJson)
    {
        if (!string.Equals(token, _bridgeToken, StringComparison.Ordinal))
        {
            if (ShieldLogger.DiagnosticMode)
                LogBridgeIssue("LEXSHIELD_BRIDGE_TOKEN_MISMATCH", href, isTop, "Invalid bridge token on QueryCosmeticsJson");
            return EmptyCosmeticsJson;
        }

        try
        {
            return ShieldRouter.QueryCosmeticsFromBridgeJson(
                href,
                parentHref,
                isTop,
                initial,
                classesJson,
                idsJson,
                _getMainTabUri(),
                _getTemporaryCompatMode());
        }
        catch (Exception ex)
        {
            LogBridgeIssue("LEXSHIELD_BRIDGE_COSMETIC_ERROR", href, isTop, $"{ex.GetType().Name}: {ex.Message}");
            return EmptyCosmeticsJson;
        }
    }
}
