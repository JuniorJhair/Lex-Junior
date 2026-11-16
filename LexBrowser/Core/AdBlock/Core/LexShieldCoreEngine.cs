using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;

namespace LexBrowser.Core.AdBlock.ShieldCore;

/// <summary>
/// Motor de red declarativo en C#/.NET 8 que replica la semántica <c>check_parameterised</c>
/// de <c>adblock-rust</c> (<c>$important</c> > normal > <c>@@</c>, anclajes <c>||host</c>, <c>^ </c>,
/// <c>*</c>, <c>$domain/$from/$to/$denyallow</c>, <c>$1p/$3p</c>, <c>$popup</c>, <c>$generichide</c>).
/// YouTube, Dailymotion y hosts críticos de pago/OAuth/CAPTCHA quedan estrictamente excluidos.
/// </summary>
public sealed class LexShieldCoreEngine : ILexShieldEngine
{
    private const int UrlStackLimit = 512;
    private const int MaxTokensPerUrl = 128;
    private const int MaxOriginHashes = 16;

    // Hashes FNV-1a precomputados para evitar comparaciones lineales de cadenas en cada petición
    private static readonly ulong[] IsolatedDomainHashes = BuildDomainHashes(
        "youtube.com", "youtu.be", "youtube-nocookie.com", "googlevideo.com", "ytimg.com",
        "dailymotion.com", "dmcdn.net", "dmxleo.com");

    private static readonly ulong[] PaymentOrAuthDomainHashes = BuildPaymentHashes();

    private static ulong[] BuildDomainHashes(params string[] domains)
    {
        var arr = new ulong[domains.Length];
        for (int i = 0; i < domains.Length; i++)
            arr[i] = LexShieldHash.Fnv1a64(domains[i].AsSpan());
        Array.Sort(arr);
        return arr;
    }

    private static ulong[] BuildPaymentHashes()
    {
        var src = ShieldExemptions.PaymentOrAuthHosts;
        var arr = new ulong[src.Length];
        for (int i = 0; i < src.Length; i++)
            arr[i] = LexShieldHash.Fnv1a64(src[i].ToLowerInvariant().AsSpan());
        Array.Sort(arr);
        return arr;
    }

    private readonly Func<LexShieldData?> _dataProvider;

    public string Name => ShieldSettings.EngineLexShield;

    public LexShieldCoreEngine(Func<LexShieldData?>? dataProvider = null)
    {
        _dataProvider = dataProvider ?? LexShieldData.TryGetDefault;
    }

    internal LexShieldData? GetShieldData() => _dataProvider();

    public ShieldNetworkDecision EvaluateNetwork(
        Uri uri,
        string? originHost,
        EffectiveSitePolicy policy,
        CoreWebView2WebResourceContext resourceContext,
        string? secFetchDest = null,
        string? httpMethod = null)
    {
        return EvaluateNetworkCore(
            uri,
            originHost,
            policy,
            resourceContext,
            secFetchDest,
            httpMethod,
            out _);
    }

    internal ShieldNetworkDecision EvaluateIsolatedNetwork(
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
                    "LexShield isolated stub (200 OK)");
            }
        }

        if (allowNetworkBlock && policy.Network && AdBlockFilter.ShouldBlock(uri, originHost, resourceContext))
        {
            return ShieldNetworkDecision.Block("Blocked ad domain or pattern");
        }

        return ShieldNetworkDecision.Allow;
    }

    internal bool ShouldBlockIsolatedPopup(
        Uri targetUri,
        Uri? originUri,
        EffectiveSitePolicy policy,
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

    private ShieldNetworkDecision EvaluateNetworkCore(
        Uri uri,
        string? originHost,
        EffectiveSitePolicy policy,
        CoreWebView2WebResourceContext resourceContext,
        string? secFetchDest,
        string? httpMethod,
        out bool hadException)
    {
        hadException = false;
        if (uri is null || !uri.IsAbsoluteUri)
            return ShieldNetworkDecision.Allow;

        var tier = ShieldExemptions.ResolveExemptionTier(uri.Host, originHost, policy);
        if (tier == ShieldExemptionTier.TotalExempt ||
            tier == ShieldExemptionTier.CosmeticOnly ||
            !policy.Network)
        {
            return ShieldNetworkDecision.Allow;
        }

        Span<char> urlScratch = stackalloc char[UrlStackLimit];
        Span<ulong> tokBuf = stackalloc ulong[MaxTokensPerUrl];
        Span<ulong> orgBuf = stackalloc ulong[MaxOriginHashes];
        Span<ulong> tgtBuf = stackalloc ulong[MaxOriginHashes];

        if (!TryBuildRequestContext(
                uri, originHost, resourceContext, secFetchDest, httpMethod,
                urlScratch, tokBuf, orgBuf, tgtBuf,
                out var ctx, out char[]? rentedUrlBuf))
        {
            return ShieldNetworkDecision.Allow;
        }

        try
        {
            var data = _dataProvider();
            if (data == null)
                return ShieldNetworkDecision.AllowDataUnavailable("LexShield data == null (fail-open: shield data not loaded)");

            var redir = new RedirectCollector();
            var trace = RuleMatchTrace.Empty;

            EvaluateRules(
                data,
                in ctx,
                EvalMode.Network,
                out bool hasImportantBlock,
                out bool hasNormalBlock,
                out bool hasException,
                ref redir,
                ref trace);

            hadException = hasException;

            bool shouldBlock = hasImportantBlock || (hasNormalBlock && !hasException);
            if (!shouldBlock)
            {
                if (!hasException &&
                    policy.Stubs &&
                    !policy.IsCompatMode &&
                    (resourceContext is CoreWebView2WebResourceContext.XmlHttpRequest or
                                        CoreWebView2WebResourceContext.Fetch or
                                        CoreWebView2WebResourceContext.Other or
                                        CoreWebView2WebResourceContext.Media) &&
                    AdBlockResources.TryGetUnifiedFallbackStub(
                        uri, originHost, resourceContext, policy,
                        out byte[] unblockedStubBytes, out string unblockedStubCt, out string unblockedStubName))
                {
                    return ShieldNetworkDecision.Redirect(
                        unblockedStubBytes,
                        unblockedStubCt,
                        $"LexShield stub ({unblockedStubName})");
                }

                if (hasException && ShieldLogger.DiagnosticMode)
                {
                    string excRule = FormatMatchedRule(data, in ctx, isImportant: false, isException: true, in trace);
                    return ShieldNetworkDecision.AllowWithReason($"LexShield exception ({excRule})", hasException: true);
                }
                return ShieldNetworkDecision.Allow;
            }

            if (policy.Stubs && !redir.CancelAll && redir.CandCount > 0)
            {
                var redirectDecision = ResolveBestRedirect(data, uri, in ctx, policy, in redir, hasImportantBlock);
                if (redirectDecision.IsRedirectStub)
                    return redirectDecision;
            }

            if (policy.Stubs &&
                !redir.CancelAll &&
                AdBlockResources.TryGetUnifiedFallbackStub(
                    uri, originHost, resourceContext, policy,
                    out byte[] fallbackBytes, out string fallbackCt, out string fallbackName))
            {
                return ShieldNetworkDecision.Redirect(
                    fallbackBytes,
                    fallbackCt,
                    $"LexShield stub ({fallbackName})",
                    isImportant: hasImportantBlock);
            }

            string matchedRule = FormatMatchedRule(data, in ctx, isImportant: hasImportantBlock, isException: false, in trace);
            if (hasImportantBlock)
            {
                return ShieldNetworkDecision.Block(
                    string.IsNullOrEmpty(matchedRule) ? "LexShield network ($important)" : $"LexShield network ($important: {matchedRule})",
                    isImportant: true);
            }

            return ShieldNetworkDecision.Block(
                string.IsNullOrEmpty(matchedRule) ? "LexShield network" : $"LexShield network ({matchedRule})");
        }
        finally
        {
            if (rentedUrlBuf != null)
                ArrayPool<char>.Shared.Return(rentedUrlBuf);
        }
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
        if (targetUri is null || !policy.ShieldEnabled || !policy.Popups)
            return false;

        string? originHost = originUri?.Host;
        if (ShieldIsolation.IsIsolatedHost(targetUri.Host) ||
            ShieldIsolation.IsIsolatedHost(originHost) ||
            ShieldExemptions.IsPaymentOrAuthHost(targetUri.Host) ||
            ShieldExemptions.IsPaymentOrAuthHost(originHost))
        {
            return false;
        }

        var data = _dataProvider();
        if (data == null)
            return false;

        bool excPopup = false;
        Span<char> urlScratch = stackalloc char[UrlStackLimit];
        Span<ulong> tokBuf = stackalloc ulong[MaxTokensPerUrl];
        Span<ulong> orgBuf = stackalloc ulong[MaxOriginHashes];
        Span<ulong> tgtBuf = stackalloc ulong[MaxOriginHashes];

        // 1. Reglas declarativas $popup de LexShield Core (ej. ||popunder.net^$popup y /regex/$popup)
        if (TryBuildRequestContext(
                targetUri, originHost, CoreWebView2WebResourceContext.Document,
                secFetchDest: null, httpMethod: "GET",
                urlScratch, tokBuf, orgBuf, tgtBuf,
                out var ctx, out char[]? rentedBuf,
                overrideTypeMask: LexShieldMask.FromPopup))
        {
            try
            {
                var dummyRedir = default(RedirectCollector);
                var trace = RuleMatchTrace.Empty;
                EvaluateRules(
                    data,
                    in ctx,
                    EvalMode.Popup,
                    out bool impPopup,
                    out bool normPopup,
                    out excPopup,
                    ref dummyRedir,
                    ref trace);

                if (impPopup || (normPopup && !excPopup))
                {
                    string matched = FormatMatchedRule(data, in ctx, isImportant: impPopup, isException: false, in trace);
                    reason = impPopup
                        ? (string.IsNullOrEmpty(matched) ? "LexShield $popup ($important)" : $"LexShield $popup ($important: {matched})")
                        : (string.IsNullOrEmpty(matched) ? "LexShield $popup" : $"LexShield $popup ({matched})");
                    return true;
                }
            }
            finally
            {
                if (rentedBuf != null)
                    ArrayPool<char>.Shared.Return(rentedBuf);
            }
        }

        // 2. Reglas de bloqueo Document si el módulo Network está activo
        bool hasDocException = false;
        if (policy.Network)
        {
            var navDecision = EvaluateNetworkCore(
                targetUri,
                originHost,
                policy,
                CoreWebView2WebResourceContext.Document,
                secFetchDest: null,
                httpMethod: null,
                out hasDocException);
            if (navDecision.ShouldBlock)
            {
                reason = navDecision.Reason ?? "LexShield popup document block";
                return true;
            }
        }

        if (excPopup || hasDocException)
            return false;

        // 3. Si el documento origen tiene regla $csp=sandbox sin allow-popups, bloquear el popup
        if (originUri != null && policy.Network)
        {
            EvaluateCspPolicies(originUri, originUri.Host, isFromSubframe, policy, out bool originSandboxNoPopups);
            if (originSandboxNoPopups)
            {
                reason = "LexShield $csp=sandbox (no allow-popups)";
                return true;
            }
        }

        // 4. Salvaguardas seguras cuando no existe excepción explícita $popup/$document
        bool isSafeDest = AdBlockFilter.IsGoogleDomain(targetUri.Host) ||
                          AdBlockFilter.IsTrustedDomain(targetUri.Host);
        if (!isSafeDest)
        {
            if (!isUserInitiated &&
                originUri != null &&
                !PublicSuffix.IsSameRegistrableDomain(targetUri.Host, originUri.Host))
            {
                reason = "LexShield popup: cross-origin unsolicited";
                return true;
            }

            if ((!isUserInitiated || isFromSubframe) && AdBlockFilter.IsSuspiciousPopup(targetUri, originUri))
            {
                reason = "LexShield popup: suspicious tracking/popunder";
                return true;
            }
        }

        return false;
    }

    public IReadOnlyList<string> EvaluateCspPolicies(
        Uri documentUri,
        string? originHost,
        bool isSubdocument,
        EffectiveSitePolicy policy,
        out bool enforceSandboxNoPopups)
    {
        enforceSandboxNoPopups = false;
        if (documentUri is null || !policy.ShieldEnabled || !policy.Network)
            return Array.Empty<string>();

        var data = _dataProvider();
        if (data == null)
            return Array.Empty<string>();

        var cspRules = data.GetCspRules();
        if (cspRules.Length == 0)
            return Array.Empty<string>();

        string effOrigin = string.IsNullOrWhiteSpace(originHost) ? documentUri.Host : originHost;
        Span<char> urlScratch = stackalloc char[UrlStackLimit];
        Span<ulong> tokBuf = stackalloc ulong[MaxTokensPerUrl];
        Span<ulong> orgBuf = stackalloc ulong[MaxOriginHashes];
        Span<ulong> tgtBuf = stackalloc ulong[MaxOriginHashes];

        if (!TryBuildRequestContext(
                documentUri,
                effOrigin,
                CoreWebView2WebResourceContext.Document,
                secFetchDest: isSubdocument ? "iframe" : "document",
                httpMethod: "GET",
                urlScratch,
                tokBuf,
                orgBuf,
                tgtBuf,
                out var ctx,
                out char[]? rentedBuf,
                overrideTypeMask: isSubdocument ? LexShieldMask.FromSubdocument : LexShieldMask.FromDocument))
        {
            return Array.Empty<string>();
        }

        try
        {
            bool wildcardException = false;
            HashSet<string>? cancelledDirectives = null;
            List<string>? importantDirectives = null;
            List<string>? normalDirectives = null;

            for (int i = 0; i < cspRules.Length; i++)
            {
                ref readonly var rec = ref cspRules[i];
                var mask = (LexShieldMask)rec.Mask;

                if ((mask & ctx.TypeMask) == 0)
                    continue;
                if ((mask & ctx.PartyMask) == 0)
                    continue;
                if ((mask & LexShieldMask.FromSchemeMask) != 0 && (mask & ctx.SchemeMask) == 0)
                    continue;
                if ((mask & LexShieldMask.FromAnyMethods) != 0 && (mask & ctx.MethodMask) == 0)
                    continue;
                if (!MatchesDomainConstraints(data, in rec, ctx.OriginHashes, ctx.TargetHashes))
                    continue;

                bool matched = (mask & LexShieldMask.IsFullRegex) != 0
                    ? MatchesFullRegex(data, in rec, i, isCsp: true, in ctx)
                    : MatchesUrlPattern(data, in rec, ctx.UrlLower, ctx.ReqHost, ctx.HostStart);
                if (!matched)
                    continue;

                string mod = rec.ModLen > 0
                    ? Encoding.UTF8.GetString(data.StrBlob, rec.ModOff, rec.ModLen).Trim()
                    : string.Empty;

                bool isException = (mask & LexShieldMask.IsException) != 0;
                bool isImportant = (mask & LexShieldMask.IsImportant) != 0;

                if (isException)
                {
                    if (string.IsNullOrEmpty(mod))
                    {
                        wildcardException = true;
                    }
                    else
                    {
                        cancelledDirectives ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        cancelledDirectives.Add(mod);
                    }
                }
                else if (!string.IsNullOrEmpty(mod))
                {
                    if (isImportant)
                    {
                        importantDirectives ??= new List<string>(2);
                        importantDirectives.Add(mod);
                    }
                    else
                    {
                        normalDirectives ??= new List<string>(4);
                        normalDirectives.Add(mod);
                    }
                }
            }

            if (importantDirectives == null && (wildcardException || normalDirectives == null))
                return Array.Empty<string>();

            var result = new List<string>(4);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (importantDirectives != null)
            {
                foreach (string d in importantDirectives)
                {
                    if (seen.Add(d))
                        result.Add(d);
                }
            }

            if (!wildcardException && normalDirectives != null)
            {
                foreach (string d in normalDirectives)
                {
                    if (cancelledDirectives != null && cancelledDirectives.Contains(d))
                        continue;
                    if (seen.Add(d))
                        result.Add(d);
                }
            }

            if (result.Count == 0)
                return Array.Empty<string>();

            for (int i = 0; i < result.Count; i++)
            {
                if (HasSandboxWithoutPopups(result[i]))
                {
                    enforceSandboxNoPopups = true;
                    break;
                }
            }

            return result;
        }
        finally
        {
            if (rentedBuf != null)
                ArrayPool<char>.Shared.Return(rentedBuf);
        }
    }

    public string? EvaluateCspHeader(
        Uri documentUri,
        string? originHost,
        bool isSubdocument,
        EffectiveSitePolicy policy,
        out bool enforceSandboxNoPopups)
    {
        var policies = EvaluateCspPolicies(documentUri, originHost, isSubdocument, policy, out enforceSandboxNoPopups);
        return policies.Count > 0 ? string.Join(", ", policies) : null;
    }

    private static bool HasSandboxWithoutPopups(string cspPolicy)
    {
        if (string.IsNullOrWhiteSpace(cspPolicy))
            return false;

        int start = 0;
        while (start < cspPolicy.Length)
        {
            int semi = cspPolicy.IndexOf(';', start);
            var part = (semi >= 0 ? cspPolicy.AsSpan(start, semi - start) : cspPolicy.AsSpan(start)).Trim();
            if (part.Equals("sandbox", StringComparison.OrdinalIgnoreCase) ||
                (part.StartsWith("sandbox", StringComparison.OrdinalIgnoreCase) &&
                 part.Length > 7 &&
                 char.IsWhiteSpace(part[7])))
            {
                if (part.IndexOf("allow-popups", StringComparison.OrdinalIgnoreCase) < 0)
                    return true;
            }
            if (semi < 0)
                break;
            start = semi + 1;
        }
        return false;
    }

    public bool IsGenericHideEnabled(Uri documentUri, string? originHost)
    {
        if (documentUri is null)
            return false;

        string effOrigin = string.IsNullOrWhiteSpace(originHost) ? documentUri.Host : originHost;
        Span<char> urlScratch = stackalloc char[UrlStackLimit];
        Span<ulong> tokBuf = stackalloc ulong[MaxTokensPerUrl];
        Span<ulong> orgBuf = stackalloc ulong[MaxOriginHashes];
        Span<ulong> tgtBuf = stackalloc ulong[MaxOriginHashes];

        if (!TryBuildRequestContext(
                documentUri, effOrigin, CoreWebView2WebResourceContext.Document,
                secFetchDest: null, httpMethod: "GET",
                urlScratch, tokBuf, orgBuf, tgtBuf,
                out var ctx, out char[]? rentedBuf))
        {
            return false;
        }

        try
        {
            var data = _dataProvider();
            if (data == null)
                return false;

            var dummyRedir = default(RedirectCollector);
            var trace = RuleMatchTrace.Empty;
            EvaluateRules(data, in ctx, EvalMode.GenericHide, out bool matched, out _, out _, ref dummyRedir, ref trace);
            return matched;
        }
        finally
        {
            if (rentedBuf != null)
                ArrayPool<char>.Shared.Return(rentedBuf);
        }
    }

    private enum EvalMode
    {
        Network,
        Popup,
        GenericHide
    }

    private ref struct RequestEvalContext
    {
        public Uri? OriginalUri;
        public ReadOnlySpan<char> UrlLower;
        public ReadOnlySpan<char> ReqHost;
        public string CleanOriginStr;
        public int HostStart;
        public int HostEnd;
        public LexShieldMask TypeMask;
        public LexShieldMask PartyMask;
        public LexShieldMask SchemeMask;
        public LexShieldMask MethodMask;
        public ReadOnlySpan<ulong> TokenHashes;
        public ReadOnlySpan<ulong> OriginHashes;
        public ReadOnlySpan<ulong> TargetHashes;
    }

    [InlineArray(16)]
    private struct CandInline16 { private int _e0; }

    [InlineArray(8)]
    private struct ExcInline8 { private int _e0; }

    private struct RedirectCollector
    {
        private CandInline16 _cands;
        private ExcInline8 _excs;
        public int CandCount;
        public int ExcCount;
        public bool CancelAll;

        public readonly ReadOnlySpan<int> CandIndices => MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in _cands[0]), CandCount);
        public readonly ReadOnlySpan<int> ExcIndices => MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in _excs[0]), ExcCount);

        public void AddCandidate(int ridx)
        {
            var span = MemoryMarshal.CreateReadOnlySpan(ref _cands[0], CandCount);
            if (CandCount < 16 && span.IndexOf(ridx) < 0)
                _cands[CandCount++] = ridx;
        }

        public void AddException(int ridx, int modLen)
        {
            if (modLen <= 0)
            {
                CancelAll = true;
                return;
            }
            var span = MemoryMarshal.CreateReadOnlySpan(ref _excs[0], ExcCount);
            if (ExcCount < 8 && span.IndexOf(ridx) < 0)
                _excs[ExcCount++] = ridx;
        }
    }

    private struct RuleMatchTrace
    {
        public const int UnsetIndex = int.MinValue;
        public int PureHostPos;
        public int ImpEncodedIdx;
        public int NormEncodedIdx;
        public int ExcEncodedIdx;

        public static RuleMatchTrace Empty => new()
        {
            PureHostPos = -1,
            ImpEncodedIdx = UnsetIndex,
            NormEncodedIdx = UnsetIndex,
            ExcEncodedIdx = UnsetIndex
        };
    }

    private static string FormatMatchedRule(
        LexShieldData data,
        in RequestEvalContext ctx,
        bool isImportant,
        bool isException,
        in RuleMatchTrace trace)
    {
        if (isException)
        {
            return trace.ExcEncodedIdx != RuleMatchTrace.UnsetIndex
                ? FormatPatRecRule(data, trace.ExcEncodedIdx)
                : string.Empty;
        }

        if (isImportant)
        {
            return trace.ImpEncodedIdx != RuleMatchTrace.UnsetIndex
                ? FormatPatRecRule(data, trace.ImpEncodedIdx)
                : string.Empty;
        }

        if (trace.PureHostPos >= 0 && trace.PureHostPos < ctx.ReqHost.Length)
        {
            return $"purehost:{ctx.ReqHost.Slice(trace.PureHostPos).ToString()}";
        }

        if (trace.NormEncodedIdx != RuleMatchTrace.UnsetIndex)
        {
            return FormatPatRecRule(data, trace.NormEncodedIdx);
        }

        return string.Empty;
    }

    private static string FormatPatRecRule(LexShieldData data, int encodedIdx)
    {
        ref readonly var rec = ref GetRuleByEncodedIndex(data, encodedIdx);
        var mask = (LexShieldMask)rec.Mask;

        string host = rec.HostLen > 0 ? Encoding.UTF8.GetString(data.StrBlob, rec.HostOff, rec.HostLen) : string.Empty;
        string pat = rec.PatLen > 0 ? Encoding.UTF8.GetString(data.StrBlob, rec.PatOff, rec.PatLen) : string.Empty;

        var sb = new StringBuilder(64);
        if ((mask & LexShieldMask.IsException) != 0)
            sb.Append("@@");

        if ((mask & LexShieldMask.IsFullRegex) != 0)
        {
            sb.Append('/').Append(pat).Append('/');
        }
        else
        {
            if ((mask & LexShieldMask.IsHostnameAnchor) != 0)
                sb.Append("||");
            else if ((mask & LexShieldMask.IsLeftAnchor) != 0)
                sb.Append('|');

            if (string.IsNullOrEmpty(pat))
                sb.Append(host);
            else if (!string.IsNullOrEmpty(host) && !pat.Contains(host, StringComparison.Ordinal))
                sb.Append(host).Append(pat);
            else
                sb.Append(pat);

            if ((mask & LexShieldMask.IsRightAnchor) != 0)
                sb.Append('|');
        }

        if ((mask & LexShieldMask.IsImportant) != 0)
            sb.Append("$important");

        string res = sb.ToString();
        return res.Length > 96 ? res[..96] + "..." : res;
    }

    private static bool TryBuildRequestContext(
        Uri uri,
        string? originHost,
        CoreWebView2WebResourceContext resourceContext,
        string? secFetchDest,
        string? httpMethod,
        Span<char> urlScratch,
        Span<ulong> tokBuf,
        Span<ulong> orgBuf,
        Span<ulong> tgtBuf,
        out RequestEvalContext ctx,
        out char[]? rentedUrlBuf,
        LexShieldMask? overrideTypeMask = null)
    {
        ctx = default;
        rentedUrlBuf = null;

        if (!uri.IsAbsoluteUri)
            return false;

        string scheme = uri.Scheme;
        bool isHttp = scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                      scheme.Equals("ws", StringComparison.OrdinalIgnoreCase);
        bool isHttps = scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                       scheme.Equals("wss", StringComparison.OrdinalIgnoreCase);
        if (!isHttp && !isHttps)
            return false;

        string rawHost = uri.Host;
        if (string.IsNullOrEmpty(rawHost))
            return false;

        var rawHostSpan = rawHost.AsSpan().TrimEnd('.');
        if (rawHostSpan.IsEmpty)
            return false;

        int tgtCount = ExtractDomainAndEntityHashes(rawHostSpan, rawHost, tgtBuf, out string? reqHostStr, out string reqRegDomain);
        var tgtSlice = tgtBuf.Slice(0, tgtCount);
        if (ContainsAnySorted(tgtSlice, IsolatedDomainHashes) ||
            ContainsAnySorted(tgtSlice, PaymentOrAuthDomainHashes))
        {
            return false;
        }

        int orgCount = 0;
        string cleanOriginStr = string.Empty;
        string orgRegDomain = string.Empty;
        if (!string.IsNullOrWhiteSpace(originHost))
        {
            var orgSpanRaw = originHost.AsSpan().Trim().TrimEnd('.');
            if (!orgSpanRaw.IsEmpty)
            {
                orgCount = ExtractDomainAndEntityHashes(orgSpanRaw, originHost, orgBuf, out cleanOriginStr, out orgRegDomain);
                var orgSlice = orgBuf.Slice(0, orgCount);
                if (ContainsAnySorted(orgSlice, IsolatedDomainHashes) ||
                    ContainsAnySorted(orgSlice, PaymentOrAuthDomainHashes))
                {
                    return false;
                }
            }
        }

        // Construir URL normalizada en minúsculas sin asignaciones innecesarias
        string pathAndQuery = uri.PathAndQuery;
        int port = !uri.IsDefaultPort && uri.Port > 0 ? uri.Port : -1;
        int portDigits = port > 0 ? CountDigits(port) + 1 : 0;
        int totalLen = scheme.Length + 3 + reqHostStr!.Length + portDigits + pathAndQuery.Length;

        Span<char> urlSpan = totalLen <= urlScratch.Length
            ? urlScratch.Slice(0, totalLen)
            : (rentedUrlBuf = ArrayPool<char>.Shared.Rent(totalLen)).AsSpan(0, totalLen);

        int pos = 0;
        CopyAsciiLower(scheme.AsSpan(), urlSpan.Slice(pos)); pos += scheme.Length;
        urlSpan[pos++] = ':'; urlSpan[pos++] = '/'; urlSpan[pos++] = '/';
        int hostStart = pos;
        reqHostStr.AsSpan().CopyTo(urlSpan.Slice(pos)); pos += reqHostStr.Length;
        int hostEnd = pos;
        if (port > 0)
        {
            urlSpan[pos++] = ':';
            port.TryFormat(urlSpan.Slice(pos), out int written);
            pos += written;
        }
        CopyAsciiLower(pathAndQuery.AsSpan(), urlSpan.Slice(pos));

        bool isFirstParty = string.IsNullOrEmpty(cleanOriginStr) ||
                            string.Equals(reqRegDomain, orgRegDomain, StringComparison.OrdinalIgnoreCase);

        int tokCount = ExtractUrlTokenHashes(urlSpan, tokBuf);

        ctx = new RequestEvalContext
        {
            OriginalUri = uri,
            UrlLower = urlSpan,
            ReqHost = urlSpan.Slice(hostStart, hostEnd - hostStart),
            CleanOriginStr = cleanOriginStr,
            HostStart = hostStart,
            HostEnd = hostEnd,
            TypeMask = overrideTypeMask ?? LexShieldRequestContextMapper.ToResourceTypeMask(resourceContext, secFetchDest),
            PartyMask = isFirstParty ? LexShieldMask.FirstParty : LexShieldMask.ThirdParty,
            SchemeMask = isHttps ? LexShieldMask.FromHttps : LexShieldMask.FromHttp,
            MethodMask = LexShieldRequestContextMapper.ToMethodMask(httpMethod),
            TokenHashes = tokBuf.Slice(0, tokCount),
            OriginHashes = orgBuf.Slice(0, orgCount),
            TargetHashes = tgtSlice
        };
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ContainsAnySorted(ReadOnlySpan<ulong> candidates, ulong[] sortedTargets)
    {
        for (int i = 0; i < candidates.Length; i++)
        {
            if (Array.BinarySearch(sortedTargets, candidates[i]) >= 0)
                return true;
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountDigits(int n)
    {
        if (n < 10) return 1;
        if (n < 100) return 2;
        if (n < 1000) return 3;
        if (n < 10000) return 4;
        return 5;
    }

    private static void EvaluateRules(
        LexShieldData data,
        scoped in RequestEvalContext ctx,
        EvalMode mode,
        out bool hasImportantBlock,
        out bool hasNormalBlock,
        out bool hasException,
        scoped ref RedirectCollector redir,
        scoped ref RuleMatchTrace trace)
    {
        hasImportantBlock = false;
        hasNormalBlock = false;
        hasException = false;

        // Nivel 1: PureHosts (||exact_host^) — solo en evaluación Network
        if (mode == EvalMode.Network && data.PureHashes.Length > 0)
        {
            if (CheckPureHosts(data, in ctx, out int purePos))
            {
                hasNormalBlock = true;
                trace.PureHostPos = purePos;
            }
        }

        // Nivel 2: PatternRules indexadas por OptKeys (dominio origen único), TokKeys (token más raro) y FallbackRules
        // 1. Buckets por OptKeys (dominio de origen)
        if (data.OptKeys.Length > 0 && ctx.OriginHashes.Length > 0)
        {
            for (int i = 0; i < ctx.OriginHashes.Length; i++)
            {
                int bucketIdx = Array.BinarySearch(data.OptKeys, ctx.OriginHashes[i]);
                if (bucketIdx >= 0)
                {
                    var range = data.OptRanges[bucketIdx];
                    EvaluateBucketRange(data, in ctx, mode, data.OptVals, (int)range.Offset, (int)range.Count,
                        ref hasImportantBlock, ref hasNormalBlock, ref hasException, ref redir, ref trace);
                }
            }
        }

        // 2. Buckets por TokKeys (tokens de la URL)
        if (data.TokKeys.Length > 0)
        {
            for (int i = 0; i < ctx.TokenHashes.Length; i++)
            {
                int bucketIdx = Array.BinarySearch(data.TokKeys, ctx.TokenHashes[i]);
                if (bucketIdx >= 0)
                {
                    var range = data.TokRanges[bucketIdx];
                    EvaluateBucketRange(data, in ctx, mode, data.TokVals, (int)range.Offset, (int)range.Count,
                        ref hasImportantBlock, ref hasNormalBlock, ref hasException, ref redir, ref trace);
                }
            }
        }

        // 3. FallbackRules (reglas globales sin token seguro ni $domain limitado)
        if (data.FallbackRules.Length > 0)
        {
            EvaluateBucketRange(data, in ctx, mode, data.FallbackRules, 0, data.FallbackRules.Length,
                ref hasImportantBlock, ref hasNormalBlock, ref hasException, ref redir, ref trace);
        }

        // 4. Reglas /regex/ completas (pre-filtradas por máscara, dominio y literal obligatorio)
        if (mode == EvalMode.GenericHide && hasImportantBlock)
            return;

        var regexRules = data.GetRegexRules();
        if (regexRules.Length > 0)
        {
            EvaluateRegexRules(data, in ctx, mode, regexRules,
                ref hasImportantBlock, ref hasNormalBlock, ref hasException, ref redir, ref trace);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref readonly LexShieldPatRec GetRuleByEncodedIndex(LexShieldData data, int encodedIdx)
    {
        if (encodedIdx < 0)
            return ref data.GetRegexRules()[~encodedIdx];
        return ref data.PatternRules[encodedIdx];
    }

    private static ShieldNetworkDecision ResolveBestRedirect(
        LexShieldData data,
        Uri uri,
        in RequestEvalContext ctx,
        EffectiveSitePolicy policy,
        in RedirectCollector redir,
        bool isImportant)
    {
        var store = data.GetResourceStore();
        if (store.Count == 0)
            return ShieldNetworkDecision.Allow;

        bool isExemptBigSite = !string.IsNullOrEmpty(ctx.CleanOriginStr) &&
                               ShieldExemptions.IsBigSite(ctx.CleanOriginStr);
        bool allowIma = policy.ImaStub && !isExemptBigSite;

        System.Collections.Generic.HashSet<string>? cancelledNames = null;
        if (redir.ExcCount > 0)
        {
            cancelledNames = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var excIndices = redir.ExcIndices;
            for (int i = 0; i < excIndices.Length; i++)
            {
                ref readonly var excRec = ref GetRuleByEncodedIndex(data, excIndices[i]);
                if (excRec.ModLen <= 0)
                    continue;
                string mod = Encoding.UTF8.GetString(data.StrBlob, excRec.ModOff, excRec.ModLen);
                cancelledNames.Add(mod);
                cancelledNames.Add(store.ResolveCanonicalName(mod));
            }
        }

        LexShieldResourceEntry? bestEntry = null;
        short bestPriority = short.MinValue;
        bool hasBest = false;

        var candIndices = redir.CandIndices;
        for (int i = 0; i < candIndices.Length; i++)
        {
            ref readonly var rec = ref GetRuleByEncodedIndex(data, candIndices[i]);
            if (rec.ModLen <= 0)
                continue;

            string mod = Encoding.UTF8.GetString(data.StrBlob, rec.ModOff, rec.ModLen);
            if (!store.TryGetRedirectResource(mod, out var entry, allowPrivileged: false) || entry == null)
                continue;

            if (cancelledNames != null &&
                (cancelledNames.Contains(mod) || cancelledNames.Contains(entry.Name)))
            {
                continue;
            }

            bool isIma = LexShieldResourceStore.IsImaResource(entry.Name) ||
                         uri.AbsolutePath.EndsWith("ima3.js", StringComparison.OrdinalIgnoreCase);
            if (isIma && !allowIma)
                continue;

            if (LexShieldResourceStore.IsVastOrVmapResource(entry.Name) && isExemptBigSite)
                continue;

            if (!hasBest || rec.RedirectPriority > bestPriority)
            {
                bestEntry = entry;
                bestPriority = rec.RedirectPriority;
                hasBest = true;
            }
        }

        if (!hasBest || bestEntry == null)
            return ShieldNetworkDecision.Allow;

        return ShieldNetworkDecision.Redirect(
            bestEntry.Bytes,
            bestEntry.ContentTypeHeader,
            $"LexShield redirect ({bestEntry.Name})",
            isImportant: isImportant);
    }

    private static bool CheckPureHosts(LexShieldData data, in RequestEvalContext ctx, out int matchedPos)
    {
        var reqHost = ctx.ReqHost;
        int pos = 0;
        while (pos < reqHost.Length)
        {
            ulong h = LexShieldHash.Fnv1a64(reqHost.Slice(pos));
            int idx = Array.BinarySearch(data.PureHashes, h);
            if (idx >= 0)
            {
                var mask = (LexShieldMask)data.PureMasks[idx];
                if ((mask & ctx.TypeMask) != 0 &&
                    (mask & ctx.PartyMask) != 0 &&
                    ((mask & LexShieldMask.FromSchemeMask) == 0 || (mask & ctx.SchemeMask) != 0))
                {
                    matchedPos = pos;
                    return true;
                }
            }

            int dot = reqHost.Slice(pos).IndexOf('.');
            if (dot < 0)
                break;
            pos += dot + 1;
        }
        matchedPos = -1;
        return false;
    }

    private static void EvaluateBucketRange(
        LexShieldData data,
        scoped in RequestEvalContext ctx,
        EvalMode mode,
        int[] indices,
        int offset,
        int count,
        ref bool hasImportantBlock,
        ref bool hasNormalBlock,
        ref bool hasException,
        scoped ref RedirectCollector redir,
        scoped ref RuleMatchTrace trace)
    {
        int end = offset + count;
        for (int i = offset; i < end; i++)
        {
            int ridx = indices[i];
            ref readonly var rec = ref data.PatternRules[ridx];
            EvaluateSingleRule(
                data,
                in ctx,
                mode,
                in rec,
                encodedIdx: ridx,
                ruleIndex: ridx,
                isFullRegex: false,
                ref hasImportantBlock,
                ref hasNormalBlock,
                ref hasException,
                ref redir,
                ref trace);
            if (mode == EvalMode.GenericHide && hasImportantBlock)
                return;
        }
    }

    private static void EvaluateRegexRules(
        LexShieldData data,
        scoped in RequestEvalContext ctx,
        EvalMode mode,
        LexShieldPatRec[] regexRules,
        ref bool hasImportantBlock,
        ref bool hasNormalBlock,
        ref bool hasException,
        scoped ref RedirectCollector redir,
        scoped ref RuleMatchTrace trace)
    {
        for (int i = 0; i < regexRules.Length; i++)
        {
            ref readonly var rec = ref regexRules[i];
            EvaluateSingleRule(
                data,
                in ctx,
                mode,
                in rec,
                encodedIdx: ~i,
                ruleIndex: i,
                isFullRegex: true,
                ref hasImportantBlock,
                ref hasNormalBlock,
                ref hasException,
                ref redir,
                ref trace);
            if (mode == EvalMode.GenericHide && hasImportantBlock)
                return;
        }
    }

    private static void EvaluateSingleRule(
        LexShieldData data,
        scoped in RequestEvalContext ctx,
        EvalMode mode,
        in LexShieldPatRec rec,
        int encodedIdx,
        int ruleIndex,
        bool isFullRegex,
        ref bool hasImportantBlock,
        ref bool hasNormalBlock,
        ref bool hasException,
        scoped ref RedirectCollector redir,
        scoped ref RuleMatchTrace trace)
    {
        var mask = (LexShieldMask)rec.Mask;

        bool isException = (mask & LexShieldMask.IsException) != 0;
        bool isImportant = (mask & LexShieldMask.IsImportant) != 0;
        bool isRedirect = (mask & LexShieldMask.IsRedirect) != 0;

        switch (mode)
        {
            case EvalMode.GenericHide:
                if ((mask & LexShieldMask.IsGenericHide) == 0)
                    return;
                break;

            case EvalMode.Popup:
                if ((mask & LexShieldMask.IsGenericHide) != 0)
                    return;
                if (!isException)
                {
                    if ((mask & LexShieldMask.FromPopup) == 0)
                        return;
                }
                else
                {
                    // @@...$popup o @@...$document desactivan bloqueo de popup (no @@...$redirect)
                    if (isRedirect)
                        return;
                    if ((mask & (LexShieldMask.FromPopup | LexShieldMask.FromDocument)) == 0)
                        return;
                }
                break;

            case EvalMode.Network:
                if ((mask & LexShieldMask.IsGenericHide) != 0)
                    return;
                if ((mask & LexShieldMask.FromPopup) != 0)
                    return;
                if (!isRedirect)
                {
                    if (!isImportant && !isException && (hasNormalBlock || hasImportantBlock))
                        return;
                    if (isException && (hasException || hasImportantBlock))
                        return;
                }
                if ((mask & ctx.TypeMask) == 0)
                    return;
                break;
        }

        if ((mask & ctx.PartyMask) == 0)
            return;

        if ((mask & LexShieldMask.FromSchemeMask) != 0 && (mask & ctx.SchemeMask) == 0)
            return;

        if ((mask & LexShieldMask.FromAnyMethods) != 0 && (mask & ctx.MethodMask) == 0)
            return;

        if (!MatchesDomainConstraints(data, in rec, ctx.OriginHashes, ctx.TargetHashes))
            return;

        bool patternMatched = isFullRegex
            ? MatchesFullRegex(data, in rec, ruleIndex, isCsp: false, in ctx)
            : MatchesUrlPattern(data, in rec, ctx.UrlLower, ctx.ReqHost, ctx.HostStart);
        if (!patternMatched)
            return;

        if (mode == EvalMode.GenericHide)
        {
            hasImportantBlock = true;
            return;
        }

        if (mode == EvalMode.Network && isRedirect)
        {
            if (isException)
            {
                redir.AddException(encodedIdx, rec.ModLen);
            }
            else
            {
                redir.AddCandidate(encodedIdx);
                if ((mask & LexShieldMask.IsAlsoBlockRedirect) != 0)
                {
                    if (isImportant)
                    {
                        hasImportantBlock = true;
                        if (trace.ImpEncodedIdx == RuleMatchTrace.UnsetIndex)
                            trace.ImpEncodedIdx = encodedIdx;
                    }
                    else
                    {
                        hasNormalBlock = true;
                        if (trace.NormEncodedIdx == RuleMatchTrace.UnsetIndex)
                            trace.NormEncodedIdx = encodedIdx;
                    }
                }
            }
            return;
        }

        if (isImportant && !isException)
        {
            hasImportantBlock = true;
            if (trace.ImpEncodedIdx == RuleMatchTrace.UnsetIndex)
                trace.ImpEncodedIdx = encodedIdx;
            return;
        }

        if (isException)
        {
            hasException = true;
            if (trace.ExcEncodedIdx == RuleMatchTrace.UnsetIndex)
                trace.ExcEncodedIdx = encodedIdx;
        }
        else
        {
            hasNormalBlock = true;
            if (trace.NormEncodedIdx == RuleMatchTrace.UnsetIndex)
                trace.NormEncodedIdx = encodedIdx;
        }
    }

    private static bool MatchesFullRegex(
        LexShieldData data,
        in LexShieldPatRec rec,
        int ruleIndex,
        bool isCsp,
        scoped in RequestEvalContext ctx)
    {
        if (rec.HostLen > 0)
        {
            Span<char> preScratch = stackalloc char[128];
            ReadOnlySpan<char> prefilter = ReadBlobSlice(data.StrBlob, rec.HostOff, rec.HostLen, preScratch, out _);
            if (!prefilter.IsEmpty && ctx.UrlLower.IndexOf(prefilter, StringComparison.Ordinal) < 0)
                return false;
        }

        var regex = data.GetOrCompileRegex(isCsp, ruleIndex);
        if (regex == null)
            return false;

        try
        {
            if (((LexShieldMask)rec.Mask & LexShieldMask.IsMatchCase) != 0 && ctx.OriginalUri != null)
            {
                string pathAndQuery = ctx.OriginalUri.PathAndQuery;
                int prefixLen = ctx.HostEnd;
                int port = !ctx.OriginalUri.IsDefaultPort && ctx.OriginalUri.Port > 0 ? ctx.OriginalUri.Port : -1;
                int portDigits = port > 0 ? CountDigits(port) + 1 : 0;
                int totalLen = prefixLen + portDigits + pathAndQuery.Length;
                char[]? rented = null;
                Span<char> buf = totalLen <= UrlStackLimit
                    ? stackalloc char[UrlStackLimit]
                    : (rented = ArrayPool<char>.Shared.Rent(totalLen));
                try
                {
                    ctx.UrlLower.Slice(0, prefixLen).CopyTo(buf);
                    int pos = prefixLen;
                    if (port > 0)
                    {
                        buf[pos++] = ':';
                        port.TryFormat(buf.Slice(pos), out int written);
                        pos += written;
                    }
                    pathAndQuery.AsSpan().CopyTo(buf.Slice(pos));
                    return regex.IsMatch(buf.Slice(0, totalLen));
                }
                finally
                {
                    if (rented != null)
                        ArrayPool<char>.Shared.Return(rented);
                }
            }

            return regex.IsMatch(ctx.UrlLower);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool MatchesDomainConstraints(
        LexShieldData data,
        in LexShieldPatRec rec,
        ReadOnlySpan<ulong> originHashes,
        ReadOnlySpan<ulong> targetHashes)
    {
        if (rec.IncDomLen > 0)
        {
            if (originHashes.IsEmpty ||
                !AnyHashMatches(data.HashPool.AsSpan(rec.IncDomOff, rec.IncDomLen), originHashes))
            {
                return false;
            }
        }

        if (rec.ExcDomLen > 0 && !originHashes.IsEmpty)
        {
            if (AnyHashMatches(data.HashPool.AsSpan(rec.ExcDomOff, rec.ExcDomLen), originHashes))
                return false;
        }

        if (rec.IncToLen > 0)
        {
            if (targetHashes.IsEmpty ||
                !AnyHashMatches(data.HashPool.AsSpan(rec.IncToOff, rec.IncToLen), targetHashes))
            {
                return false;
            }
        }

        if (rec.ExcToLen > 0 && !targetHashes.IsEmpty)
        {
            if (AnyHashMatches(data.HashPool.AsSpan(rec.ExcToOff, rec.ExcToLen), targetHashes))
                return false;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool AnyHashMatches(ReadOnlySpan<ulong> ruleHashes, ReadOnlySpan<ulong> requestHashes)
    {
        for (int i = 0; i < requestHashes.Length; i++)
        {
            if (ruleHashes.IndexOf(requestHashes[i]) >= 0)
                return true;
        }
        return false;
    }

    private static bool MatchesUrlPattern(
        LexShieldData data,
        in LexShieldPatRec rec,
        ReadOnlySpan<char> urlLower,
        ReadOnlySpan<char> reqHost,
        int hostStart)
    {
        var mask = (LexShieldMask)rec.Mask;
        bool isHostnameAnchor = (mask & LexShieldMask.IsHostnameAnchor) != 0;
        bool isLeftAnchor = (mask & LexShieldMask.IsLeftAnchor) != 0;
        bool isRightAnchor = (mask & LexShieldMask.IsRightAnchor) != 0;
        bool isHostWildcard = (mask & LexShieldMask.IsHostWildcard) != 0;
        bool isRegex = (mask & LexShieldMask.IsRegex) != 0;

        Span<char> hostScratch = stackalloc char[128];
        Span<char> patScratch = stackalloc char[256];
        ReadOnlySpan<char> hostSpan = ReadBlobSlice(data.StrBlob, rec.HostOff, rec.HostLen, hostScratch, out _);
        ReadOnlySpan<char> patSpan = ReadBlobSlice(data.StrBlob, rec.PatOff, rec.PatLen, patScratch, out _);

        if (isHostnameAnchor)
        {
            int pos = 0;
            while (pos + hostSpan.Length <= reqHost.Length)
            {
                if (reqHost.Slice(pos).StartsWith(hostSpan, StringComparison.Ordinal))
                {
                    int afterHostOffset = pos + hostSpan.Length;
                    if (patSpan.IsEmpty && !isHostWildcard)
                    {
                        if (!isRightAnchor || afterHostOffset == reqHost.Length)
                            return true;
                    }
                    else
                    {
                        var remainingUrl = urlLower.Slice(hostStart + afterHostOffset);
                        if (MatchGlob(remainingUrl, patSpan, leftAnchored: true, rightAnchored: isRightAnchor, isRegex: isRegex))
                            return true;
                    }
                }

                int dot = reqHost.Slice(pos).IndexOf('.');
                if (dot < 0)
                    break;
                pos += dot + 1;
            }
            return false;
        }

        return MatchGlob(urlLower, patSpan, leftAnchored: isLeftAnchor, rightAnchored: isRightAnchor, isRegex: isRegex);
    }

    private static ReadOnlySpan<char> ReadBlobSlice(
        byte[] blob,
        int offset,
        int byteLen,
        Span<char> stackScratch,
        out string? heapStr)
    {
        heapStr = null;
        if (byteLen <= 0)
            return ReadOnlySpan<char>.Empty;

        var utf8 = blob.AsSpan(offset, byteLen);
        int maxChars = Encoding.UTF8.GetMaxCharCount(byteLen);
        if (maxChars <= stackScratch.Length)
        {
            int chars = Encoding.UTF8.GetChars(utf8, stackScratch);
            return stackScratch.Slice(0, chars);
        }

        heapStr = Encoding.UTF8.GetString(utf8);
        return heapStr.AsSpan();
    }

    internal static bool MatchGlob(
        ReadOnlySpan<char> text,
        ReadOnlySpan<char> pat,
        bool leftAnchored,
        bool rightAnchored,
        bool isRegex)
    {
        if (pat.IsEmpty)
            return !rightAnchored || !leftAnchored || text.IsEmpty;

        if (!isRegex)
        {
            if (leftAnchored && rightAnchored)
                return text.Equals(pat, StringComparison.Ordinal);
            if (leftAnchored)
                return text.StartsWith(pat, StringComparison.Ordinal);
            if (rightAnchored)
                return text.EndsWith(pat, StringComparison.Ordinal);
            return text.IndexOf(pat, StringComparison.Ordinal) >= 0;
        }

        if (leftAnchored)
            return MatchAnchored(text, pat, rightAnchored);

        char first = pat[0];
        if (first == '*')
            return MatchAnchored(text, pat, rightAnchored);

        for (int start = 0; start <= text.Length; start++)
        {
            if (first != '^')
            {
                int found = text.Slice(start).IndexOf(first);
                if (found < 0)
                    return false;
                start += found;
            }

            if (MatchAnchored(text.Slice(start), pat, rightAnchored))
                return true;
        }
        return false;
    }

    private static bool MatchAnchored(ReadOnlySpan<char> text, ReadOnlySpan<char> pat, bool rightAnchored)
    {
        int ti = 0;
        int pi = 0;
        int starPi = -1;
        int starTi = -1;

        while (true)
        {
            if (pi == pat.Length)
            {
                if (!rightAnchored || ti == text.Length)
                    return true;
                if (starPi != -1 && starTi < text.Length)
                {
                    pi = starPi + 1;
                    ti = ++starTi;
                    continue;
                }
                return false;
            }

            char pc = pat[pi];
            if (pc == '*')
            {
                starPi = pi++;
                starTi = ti;
                continue;
            }

            if (pc == '^')
            {
                if (ti < text.Length)
                {
                    if (IsSeparator(text[ti]))
                    {
                        pi++;
                        ti++;
                        continue;
                    }
                }
                else if (pi + 1 == pat.Length)
                {
                    pi++;
                    continue;
                }
            }
            else if (ti < text.Length && text[ti] == pc)
            {
                pi++;
                ti++;
                continue;
            }

            if (starPi != -1 && starTi < text.Length)
            {
                pi = starPi + 1;
                ti = ++starTi;
                continue;
            }

            return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSeparator(char c)
        => !(char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == '%');

    private static void CopyAsciiLower(ReadOnlySpan<char> src, Span<char> dst)
    {
        for (int i = 0; i < src.Length; i++)
        {
            char c = src[i];
            dst[i] = (uint)(c - 'A') <= ('Z' - 'A') ? (char)(c + 32) : char.ToLowerInvariant(c);
        }
    }

    private static int ExtractUrlTokenHashes(ReadOnlySpan<char> urlLower, Span<ulong> dest)
    {
        int count = 0;
        int i = 0;
        while (i < urlLower.Length)
        {
            while (i < urlLower.Length && !char.IsLetterOrDigit(urlLower[i]))
                i++;

            int start = i;
            while (i < urlLower.Length && char.IsLetterOrDigit(urlLower[i]))
                i++;

            int len = i - start;
            if (len > 1)
            {
                ulong h = LexShieldHash.Fnv1a64(urlLower.Slice(start, len));
                bool dup = false;
                for (int k = 0; k < count; k++)
                {
                    if (dest[k] == h)
                    {
                        dup = true;
                        break;
                    }
                }
                if (!dup && count < dest.Length)
                    dest[count++] = h;
            }
        }
        return count;
    }

    private static int ExtractDomainAndEntityHashes(
        ReadOnlySpan<char> rawHost,
        string? existingHostString,
        Span<ulong> dest,
        out string cleanHostLower,
        out string regDomain)
    {
        bool needsLower = false;
        for (int i = 0; i < rawHost.Length; i++)
        {
            if ((uint)(rawHost[i] - 'A') <= ('Z' - 'A'))
            {
                needsLower = true;
                break;
            }
        }

        if (!needsLower && existingHostString != null && rawHost.Length == existingHostString.Length)
            cleanHostLower = existingHostString;
        else if (!needsLower)
            cleanHostLower = rawHost.ToString();
        else
            cleanHostLower = rawHost.ToString().ToLowerInvariant();

        int count = 0;
        int pos = 0;
        var hostSpan = cleanHostLower.AsSpan();

        while (pos < hostSpan.Length && count < dest.Length)
        {
            dest[count++] = LexShieldHash.Fnv1a64(hostSpan.Slice(pos));
            int dot = hostSpan.Slice(pos).IndexOf('.');
            if (dot < 0)
                break;
            pos += dot + 1;
        }

        regDomain = PublicSuffix.GetRegistrableDomain(cleanHostLower);
        int regDot = regDomain.IndexOf('.');
        if (regDot > 0 && count < dest.Length)
        {
            Span<char> entityScratch = stackalloc char[128];
            if (regDot + 2 <= entityScratch.Length)
            {
                regDomain.AsSpan(0, regDot).CopyTo(entityScratch);
                entityScratch[regDot] = '.';
                entityScratch[regDot + 1] = '*';
                ulong eh = LexShieldHash.Fnv1a64(entityScratch.Slice(0, regDot + 2));
                if (dest.Slice(0, count).IndexOf(eh) < 0)
                    dest[count++] = eh;
            }
        }

        return count;
    }
}
