using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Web.WebView2.Core;

namespace LexBrowser.Core.AdBlock.Brave;

/// <summary>
/// Motor de red declarativo en C#/.NET 8 que replica la semántica <c>check_parameterised</c>
/// de <c>adblock-rust</c> (<c>$important</c> > normal > <c>@@</c>, anclajes <c>||host</c>, <c>^ </c>,
/// <c>*</c>, <c>$domain/$from/$to/$denyallow</c>, <c>$1p/$3p</c>, <c>$popup</c>, <c>$generichide</c>).
/// YouTube, Dailymotion y hosts críticos de pago/OAuth/CAPTCHA quedan estrictamente excluidos.
/// </summary>
public sealed class BraveNetworkEngine : IShieldEngine
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
            arr[i] = BraveHash.Fnv1a64(domains[i].AsSpan());
        Array.Sort(arr);
        return arr;
    }

    private static ulong[] BuildPaymentHashes()
    {
        var src = ShieldExemptions.PaymentOrAuthHosts;
        var arr = new ulong[src.Length];
        for (int i = 0; i < src.Length; i++)
            arr[i] = BraveHash.Fnv1a64(src[i].ToLowerInvariant().AsSpan());
        Array.Sort(arr);
        return arr;
    }

    private readonly Func<BraveShieldData?> _dataProvider;

    public string Name => ShieldSettings.EngineBrave;

    public BraveNetworkEngine(Func<BraveShieldData?>? dataProvider = null)
    {
        _dataProvider = dataProvider ?? BraveShieldData.TryGetDefault;
    }

    public ShieldNetworkDecision EvaluateNetwork(
        Uri uri,
        string? originHost,
        EffectiveSitePolicy policy,
        CoreWebView2WebResourceContext resourceContext,
        string? secFetchDest = null,
        string? httpMethod = null)
    {
        if (uri is null || !policy.ShieldEnabled || !policy.Network)
            return ShieldNetworkDecision.Allow;

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
                return ShieldNetworkDecision.Allow;

            var redir = new RedirectCollector();

            EvaluateRules(
                data,
                in ctx,
                EvalMode.Network,
                out bool hasImportantBlock,
                out bool hasNormalBlock,
                out bool hasException,
                ref redir);

            bool shouldBlock = hasImportantBlock || (hasNormalBlock && !hasException);
            if (!shouldBlock)
                return ShieldNetworkDecision.Allow;

            if (policy.Stubs && !redir.CancelAll && redir.CandCount > 0)
            {
                var redirectDecision = ResolveBestRedirect(data, uri, in ctx, policy, in redir, hasImportantBlock);
                if (redirectDecision.IsRedirectStub)
                    return redirectDecision;
            }

            if (hasImportantBlock)
                return ShieldNetworkDecision.Block("Brave network ($important)", isImportant: true);

            return ShieldNetworkDecision.Block("Brave network");
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

        Span<char> urlScratch = stackalloc char[UrlStackLimit];
        Span<ulong> tokBuf = stackalloc ulong[MaxTokensPerUrl];
        Span<ulong> orgBuf = stackalloc ulong[MaxOriginHashes];
        Span<ulong> tgtBuf = stackalloc ulong[MaxOriginHashes];

        // 1. Reglas declarativas $popup de Brave (ej. ||popunder.net^$popup)
        if (TryBuildRequestContext(
                targetUri, originHost, CoreWebView2WebResourceContext.Document,
                secFetchDest: null, httpMethod: "GET",
                urlScratch, tokBuf, orgBuf, tgtBuf,
                out var ctx, out char[]? rentedBuf,
                overrideTypeMask: BraveNetworkMask.FromPopup))
        {
            try
            {
                var dummyRedir = default(RedirectCollector);
                EvaluateRules(
                    data,
                    in ctx,
                    EvalMode.Popup,
                    out bool impPopup,
                    out bool normPopup,
                    out bool excPopup,
                    ref dummyRedir);

                if (impPopup || (normPopup && !excPopup))
                {
                    reason = impPopup ? "Brave $popup ($important)" : "Brave $popup";
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
        if (policy.Network)
        {
            var navDecision = EvaluateNetwork(
                targetUri,
                originHost,
                policy,
                CoreWebView2WebResourceContext.Document);
            if (navDecision.ShouldBlock)
            {
                reason = navDecision.Reason ?? "Brave popup document block";
                return true;
            }
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
            EvaluateRules(data, in ctx, EvalMode.GenericHide, out bool matched, out _, out _, ref dummyRedir);
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
        public ReadOnlySpan<char> UrlLower;
        public ReadOnlySpan<char> ReqHost;
        public string CleanOriginStr;
        public int HostStart;
        public int HostEnd;
        public BraveNetworkMask TypeMask;
        public BraveNetworkMask PartyMask;
        public BraveNetworkMask SchemeMask;
        public BraveNetworkMask MethodMask;
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
        BraveNetworkMask? overrideTypeMask = null)
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
            UrlLower = urlSpan,
            ReqHost = urlSpan.Slice(hostStart, hostEnd - hostStart),
            CleanOriginStr = cleanOriginStr,
            HostStart = hostStart,
            HostEnd = hostEnd,
            TypeMask = overrideTypeMask ?? BraveRequestContextMapper.ToResourceTypeMask(resourceContext, secFetchDest),
            PartyMask = isFirstParty ? BraveNetworkMask.FirstParty : BraveNetworkMask.ThirdParty,
            SchemeMask = isHttps ? BraveNetworkMask.FromHttps : BraveNetworkMask.FromHttp,
            MethodMask = BraveRequestContextMapper.ToMethodMask(httpMethod),
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
        BraveShieldData data,
        scoped in RequestEvalContext ctx,
        EvalMode mode,
        out bool hasImportantBlock,
        out bool hasNormalBlock,
        out bool hasException,
        scoped ref RedirectCollector redir)
    {
        hasImportantBlock = false;
        hasNormalBlock = false;
        hasException = false;

        // Nivel 1: PureHosts (||exact_host^) — solo en evaluación Network
        if (mode == EvalMode.Network && data.PureHashes.Length > 0)
        {
            if (CheckPureHosts(data, in ctx))
                hasNormalBlock = true;
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
                        ref hasImportantBlock, ref hasNormalBlock, ref hasException, ref redir);
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
                        ref hasImportantBlock, ref hasNormalBlock, ref hasException, ref redir);
                }
            }
        }

        // 3. FallbackRules (reglas globales sin token seguro ni $domain limitado)
        if (data.FallbackRules.Length > 0)
        {
            EvaluateBucketRange(data, in ctx, mode, data.FallbackRules, 0, data.FallbackRules.Length,
                ref hasImportantBlock, ref hasNormalBlock, ref hasException, ref redir);
        }
    }

    private static ShieldNetworkDecision ResolveBestRedirect(
        BraveShieldData data,
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
                ref readonly var excRec = ref data.PatternRules[excIndices[i]];
                if (excRec.ModLen <= 0)
                    continue;
                string mod = Encoding.UTF8.GetString(data.StrBlob, excRec.ModOff, excRec.ModLen);
                cancelledNames.Add(mod);
                cancelledNames.Add(store.ResolveCanonicalName(mod));
            }
        }

        BraveResourceEntry? bestEntry = null;
        short bestPriority = short.MinValue;
        bool hasBest = false;

        var candIndices = redir.CandIndices;
        for (int i = 0; i < candIndices.Length; i++)
        {
            ref readonly var rec = ref data.PatternRules[candIndices[i]];
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

            bool isIma = BraveResourceStore.IsImaResource(entry.Name) ||
                         uri.AbsolutePath.EndsWith("ima3.js", StringComparison.OrdinalIgnoreCase);
            if (isIma && !allowIma)
                continue;

            if (BraveResourceStore.IsVastOrVmapResource(entry.Name) && isExemptBigSite)
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
            $"Brave redirect ({bestEntry.Name})",
            isImportant: isImportant);
    }

    private static bool CheckPureHosts(BraveShieldData data, in RequestEvalContext ctx)
    {
        var reqHost = ctx.ReqHost;
        int pos = 0;
        while (pos < reqHost.Length)
        {
            ulong h = BraveHash.Fnv1a64(reqHost.Slice(pos));
            int idx = Array.BinarySearch(data.PureHashes, h);
            if (idx >= 0)
            {
                var mask = (BraveNetworkMask)data.PureMasks[idx];
                if ((mask & ctx.TypeMask) != 0 &&
                    (mask & ctx.PartyMask) != 0 &&
                    ((mask & BraveNetworkMask.FromSchemeMask) == 0 || (mask & ctx.SchemeMask) != 0))
                {
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

    private static void EvaluateBucketRange(
        BraveShieldData data,
        scoped in RequestEvalContext ctx,
        EvalMode mode,
        int[] indices,
        int offset,
        int count,
        ref bool hasImportantBlock,
        ref bool hasNormalBlock,
        ref bool hasException,
        scoped ref RedirectCollector redir)
    {
        int end = offset + count;
        for (int i = offset; i < end; i++)
        {
            int ridx = indices[i];
            ref readonly var rec = ref data.PatternRules[ridx];
            var mask = (BraveNetworkMask)rec.Mask;

            bool isException = (mask & BraveNetworkMask.IsException) != 0;
            bool isImportant = (mask & BraveNetworkMask.IsImportant) != 0;
            bool isRedirect = (mask & BraveNetworkMask.IsRedirect) != 0;

            switch (mode)
            {
                case EvalMode.GenericHide:
                    if ((mask & BraveNetworkMask.IsGenericHide) == 0)
                        continue;
                    break;

                case EvalMode.Popup:
                    if ((mask & BraveNetworkMask.IsGenericHide) != 0)
                        continue;
                    if (!isException)
                    {
                        if ((mask & BraveNetworkMask.FromPopup) == 0)
                            continue;
                    }
                    else
                    {
                        // @@...$popup o @@...$document desactivan bloqueo de popup (no @@...$redirect)
                        if (isRedirect)
                            continue;
                        if ((mask & (BraveNetworkMask.FromPopup | BraveNetworkMask.FromDocument)) == 0)
                            continue;
                    }
                    break;

                case EvalMode.Network:
                    if ((mask & BraveNetworkMask.IsGenericHide) != 0)
                        continue;
                    if ((mask & BraveNetworkMask.FromPopup) != 0)
                        continue;
                    if (!isRedirect)
                    {
                        if (!isImportant && !isException && (hasNormalBlock || hasImportantBlock))
                            continue;
                        if (isException && (hasException || hasImportantBlock))
                            continue;
                    }
                    if ((mask & ctx.TypeMask) == 0)
                        continue;
                    break;
            }

            if ((mask & ctx.PartyMask) == 0)
                continue;

            if ((mask & BraveNetworkMask.FromSchemeMask) != 0 && (mask & ctx.SchemeMask) == 0)
                continue;

            if ((mask & BraveNetworkMask.FromAnyMethods) != 0 && (mask & ctx.MethodMask) == 0)
                continue;

            if (!MatchesDomainConstraints(data, in rec, ctx.OriginHashes, ctx.TargetHashes))
                continue;

            if (!MatchesUrlPattern(data, in rec, ctx.UrlLower, ctx.ReqHost, ctx.HostStart))
                continue;

            if (mode == EvalMode.GenericHide)
            {
                hasImportantBlock = true;
                return;
            }

            if (mode == EvalMode.Network && isRedirect)
            {
                if (isException)
                {
                    redir.AddException(ridx, rec.ModLen);
                }
                else
                {
                    redir.AddCandidate(ridx);
                    if ((mask & BraveNetworkMask.IsAlsoBlockRedirect) != 0)
                    {
                        if (isImportant)
                            hasImportantBlock = true;
                        else
                            hasNormalBlock = true;
                    }
                }
                continue;
            }

            if (isImportant && !isException)
            {
                hasImportantBlock = true;
                if (mode != EvalMode.Network)
                    return;
                continue;
            }

            if (isException)
                hasException = true;
            else
                hasNormalBlock = true;
        }
    }

    private static bool MatchesDomainConstraints(
        BraveShieldData data,
        in BravePatRec rec,
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
        BraveShieldData data,
        in BravePatRec rec,
        ReadOnlySpan<char> urlLower,
        ReadOnlySpan<char> reqHost,
        int hostStart)
    {
        var mask = (BraveNetworkMask)rec.Mask;
        bool isHostnameAnchor = (mask & BraveNetworkMask.IsHostnameAnchor) != 0;
        bool isLeftAnchor = (mask & BraveNetworkMask.IsLeftAnchor) != 0;
        bool isRightAnchor = (mask & BraveNetworkMask.IsRightAnchor) != 0;
        bool isHostWildcard = (mask & BraveNetworkMask.IsHostWildcard) != 0;
        bool isRegex = (mask & BraveNetworkMask.IsRegex) != 0;

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
                ulong h = BraveHash.Fnv1a64(urlLower.Slice(start, len));
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
            dest[count++] = BraveHash.Fnv1a64(hostSpan.Slice(pos));
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
                ulong eh = BraveHash.Fnv1a64(entityScratch.Slice(0, regDot + 2));
                if (dest.Slice(0, count).IndexOf(eh) < 0)
                    dest[count++] = eh;
            }
        }

        return count;
    }
}
