using System;
using Microsoft.Web.WebView2.Core;

namespace LexBrowser.Core.AdBlock.ShieldCore;

/// <summary>
/// Resultado de evaluar una petición de red a través de un motor de bloqueo.
/// </summary>
public readonly struct ShieldNetworkDecision
{
    public bool ShouldBlock { get; init; }
    public bool IsRedirectStub { get; init; }
    public bool HasException { get; init; }
    public bool IsImportant { get; init; }
    public bool IsDataUnavailable { get; init; }
    public byte[] RedirectBytes { get; init; }
    public string RedirectContentType { get; init; }
    public string? Reason { get; init; }

    public static ShieldNetworkDecision Allow => new()
    {
        ShouldBlock = false,
        IsRedirectStub = false,
        RedirectBytes = Array.Empty<byte>(),
        RedirectContentType = string.Empty
    };

    public static ShieldNetworkDecision AllowWithReason(string reason, bool hasException = false) => new()
    {
        ShouldBlock = false,
        IsRedirectStub = false,
        HasException = hasException,
        RedirectBytes = Array.Empty<byte>(),
        RedirectContentType = string.Empty,
        Reason = reason
    };

    public static ShieldNetworkDecision AllowDataUnavailable(string reason) => new()
    {
        ShouldBlock = false,
        IsRedirectStub = false,
        IsDataUnavailable = true,
        RedirectBytes = Array.Empty<byte>(),
        RedirectContentType = string.Empty,
        Reason = reason
    };

    public static ShieldNetworkDecision Block(string? reason = null, bool isImportant = false) => new()
    {
        ShouldBlock = true,
        IsRedirectStub = false,
        IsImportant = isImportant,
        RedirectBytes = Array.Empty<byte>(),
        RedirectContentType = string.Empty,
        Reason = reason
    };

    public static ShieldNetworkDecision Redirect(byte[] bytes, string contentType, string? reason = null, bool isImportant = false) => new()
    {
        ShouldBlock = true,
        IsRedirectStub = true,
        IsImportant = isImportant,
        RedirectBytes = bytes,
        RedirectContentType = contentType,
        Reason = reason
    };
}

/// <summary>
/// Contrato común de evaluación para LexShield Core.
/// </summary>
public interface ILexShieldEngine
{
    string Name { get; }

    ShieldNetworkDecision EvaluateNetwork(
        Uri uri,
        string? originHost,
        EffectiveSitePolicy policy,
        CoreWebView2WebResourceContext resourceContext,
        string? secFetchDest = null,
        string? httpMethod = null);

    bool ShouldBlockPopup(
        Uri targetUri,
        Uri? originUri,
        EffectiveSitePolicy policy,
        bool isFromSubframe,
        bool isUserInitiated,
        out string reason);

    bool IsGenericHideEnabled(Uri documentUri, string? originHost);
}
