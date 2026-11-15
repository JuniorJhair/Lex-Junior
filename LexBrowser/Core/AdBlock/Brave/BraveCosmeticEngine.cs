using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace LexBrowser.Core.AdBlock.Brave;

internal readonly struct CosHostSlice
{
    public readonly uint Offset;
    public readonly ushort Count;
    public readonly byte Flags;
    public readonly byte Pad;

    public CosHostSlice(uint offset, ushort count, byte flags, byte pad = 0)
    {
        Offset = offset;
        Count = count;
        Flags = flags;
        Pad = pad;
    }
}

internal readonly struct CosPayload
{
    public const byte KindHideCss = 0;
    public const byte KindUnhideCss = 1;
    public const byte KindScriptlet = 2;
    public const byte KindProcedural = 3;

    public readonly string Text;
    public readonly string[]? NegHosts;
    public readonly byte Kind;
    public readonly byte Permission;

    public CosPayload(byte kind, string text, string[]? negHosts, byte permission)
    {
        Kind = kind;
        Text = text;
        NegHosts = negHosts;
        Permission = permission;
    }
}

/// <summary>
/// Resultado cosmético y de scriptlets específico de un documento.
/// </summary>
public sealed class HostCosmeticResources
{
    private static readonly IReadOnlySet<string> EmptySet = new HashSet<string>(StringComparer.Ordinal);

    public static HostCosmeticResources Empty { get; } = new(
        string.Empty,
        genericHide: false,
        uninjectAllScriptlets: false,
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<string>(),
        EmptySet);

    public HostCosmeticResources(
        string host,
        bool genericHide,
        bool uninjectAllScriptlets,
        IReadOnlyList<string> hideSelectors,
        IReadOnlyList<string> styleCssRules,
        IReadOnlyList<string> proceduralRules,
        IReadOnlyList<string> scriptlets,
        IReadOnlySet<string> unhiddenSelectors)
    {
        Host = host;
        GenericHide = genericHide;
        UninjectAllScriptlets = uninjectAllScriptlets;
        HideSelectors = hideSelectors;
        StyleCssRules = styleCssRules;
        ProceduralRules = proceduralRules;
        Scriptlets = scriptlets;
        UnhiddenSelectors = unhiddenSelectors;
    }

    public string Host { get; }
    public bool GenericHide { get; }
    public bool UninjectAllScriptlets { get; }
    public IReadOnlyList<string> HideSelectors { get; }
    public IReadOnlyList<string> StyleCssRules { get; }
    public IReadOnlyList<string> ProceduralRules { get; }
    public IReadOnlyList<string> Scriptlets { get; }
    public IReadOnlySet<string> UnhiddenSelectors { get; }

    public bool HasAnyInjection =>
        HideSelectors.Count > 0 ||
        StyleCssRules.Count > 0 ||
        ProceduralRules.Count > 0 ||
        Scriptlets.Count > 0;
}

/// <summary>
/// Motor cosmético de Brave compilado desde <c>brave-shield.lex</c> (secciones 13..19):
/// soporta selectores específicos por dominio/entidad, excepciones <c>#@#</c>,
/// <c>$generichide</c>, lookup bajo demanda de clases/IDs genéricos y reglas procedurales.
/// </summary>
public sealed class BraveCosmeticEngine
{
    private const byte HostFlagUninjectAll = 1;
    private const int MaxGenericSelectorsPerQuery = 512;

    private static readonly string[] ProceduralTokens =
    {
        ":has-text(",
        ":upward(",
        ":xpath(",
        ":matches-css",
        ":matches-attr(",
        ":matches-path(",
        ":matches-media(",
        ":min-text-length(",
        ":style(",
        ":remove(",
        ":remove-attr(",
        ":remove-class(",
        ":watch-attr(",
        ":-abp-"
    };

    public static BraveCosmeticEngine Empty { get; } = new(
        Array.Empty<ulong>(),
        Array.Empty<ulong>(),
        Array.Empty<ulong>(),
        Array.Empty<CosHostSlice>(),
        Array.Empty<int>(),
        Array.Empty<CosPayload>(),
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string[]>(StringComparer.Ordinal),
        new Dictionary<string, string[]>(StringComparer.Ordinal),
        Array.Empty<string>());

    private readonly ulong[] _simpleClassHashes;
    private readonly ulong[] _simpleIdHashes;
    private readonly ulong[] _cosHostHashes;
    private readonly CosHostSlice[] _cosHostSlices;
    private readonly int[] _cosPayloadIds;
    private readonly CosPayload[] _payloads;
    private readonly Dictionary<string, string[]> _hostUninjectMap;
    private readonly Dictionary<string, string[]> _complexClasses;
    private readonly Dictionary<string, string[]> _complexIds;
    private readonly string[] _miscGenericHide;

    internal BraveCosmeticEngine(
        ulong[] simpleClassHashes,
        ulong[] simpleIdHashes,
        ulong[] cosHostHashes,
        CosHostSlice[] cosHostSlices,
        int[] cosPayloadIds,
        CosPayload[] payloads,
        Dictionary<string, string[]> hostUninjectMap,
        Dictionary<string, string[]> complexClasses,
        Dictionary<string, string[]> complexIds,
        string[] miscGenericHide)
    {
        _simpleClassHashes = simpleClassHashes;
        _simpleIdHashes = simpleIdHashes;
        _cosHostHashes = cosHostHashes;
        _cosHostSlices = cosHostSlices;
        _cosPayloadIds = cosPayloadIds;
        _payloads = payloads;
        _hostUninjectMap = hostUninjectMap;
        _complexClasses = complexClasses;
        _complexIds = complexIds;
        _miscGenericHide = miscGenericHide;
    }

    public int SimpleClassCount => _simpleClassHashes.Length;
    public int SimpleIdCount => _simpleIdHashes.Length;
    public int HostCount => _cosHostHashes.Length;
    public int PayloadCount => _payloads.Length;
    public IReadOnlyList<string> MiscGenericHide => _miscGenericHide;

    internal static BraveCosmeticEngine LoadFromSections(
        byte[] sec13SimpleClasses,
        byte[] sec14SimpleIds,
        byte[] sec15HostHashes,
        byte[] sec16HostSlices,
        byte[] sec17PayloadIds,
        byte[] sec18PayloadsJsonUtf8,
        byte[] sec19ComplexJsonUtf8)
    {
        ulong[] simpleClasses = ReadUInt64Array(sec13SimpleClasses);
        ulong[] simpleIds = ReadUInt64Array(sec14SimpleIds);
        ulong[] hostHashes = ReadUInt64Array(sec15HostHashes);
        CosHostSlice[] hostSlices = ReadHostSlices(sec16HostSlices);
        int[] payloadIds = ReadInt32Array(sec17PayloadIds);
        (CosPayload[] payloads, Dictionary<string, string[]> uiMap) = ParseSection18(sec18PayloadsJsonUtf8);
        (Dictionary<string, string[]> cc, Dictionary<string, string[]> ci, string[] mg) = ParseSection19(sec19ComplexJsonUtf8);

        return new BraveCosmeticEngine(
            simpleClasses,
            simpleIds,
            hostHashes,
            hostSlices,
            payloadIds,
            payloads,
            uiMap,
            cc,
            ci,
            mg);
    }

    /// <summary>
    /// Comprueba en tiempo casi nulo si el host tiene reglas cosméticas o scriptlets específicos
    /// (se usa en <c>WebResourceRequested(Document)</c> para decidir si tomar deferral).
    /// </summary>
    public bool HasHostRules(string? host)
    {
        string normalized = NormalizeHost(host);
        if (string.IsNullOrEmpty(normalized) || ShieldIsolation.IsIsolatedHost(normalized))
            return false;

        if (_cosHostHashes.Length == 0)
            return false;

        Span<int> sliceIndices = stackalloc int[16];
        int matchCount = CollectHostSliceIndices(normalized, out string? entityKey, sliceIndices);
        for (int i = 0; i < matchCount; i++)
        {
            var sl = _cosHostSlices[sliceIndices[i]];
            if (sl.Count == 0 || (sl.Flags & HostFlagUninjectAll) != 0)
                continue;

            int end = (int)sl.Offset + sl.Count;
            if ((uint)end > (uint)_cosPayloadIds.Length)
                continue;

            for (int pIdx = (int)sl.Offset; pIdx < end; pIdx++)
            {
                int pid = _cosPayloadIds[pIdx];
                if ((uint)pid >= (uint)_payloads.Length)
                    continue;
                ref readonly CosPayload p = ref _payloads[pid];
                if (p.Kind != CosPayload.KindUnhideCss && !MatchesAnyNegHost(normalized, entityKey, p.NegHosts))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resuelve todas las reglas cosméticas específicas, estilos, procedurales y scriptlets del documento.
    /// </summary>
    public HostCosmeticResources EvaluateHost(
        string? host,
        bool genericHide,
        BraveResourceStore? resources = null,
        bool allowPrivileged = false)
    {
        string normalized = NormalizeHost(host);
        if (string.IsNullOrEmpty(normalized) || ShieldIsolation.IsIsolatedHost(normalized))
            return HostCosmeticResources.Empty;

        Span<int> sliceIndices = stackalloc int[16];
        int matchCount = CollectHostSliceIndices(normalized, out string? entityKey, sliceIndices);

        bool uninjectAll = false;
        var unhidden = new HashSet<string>(StringComparer.Ordinal);
        List<string>? rawUninjectRules = CollectHostUninjectRules(normalized, entityKey);

        for (int i = 0; i < matchCount; i++)
        {
            var sl = _cosHostSlices[sliceIndices[i]];
            if ((sl.Flags & HostFlagUninjectAll) != 0)
                uninjectAll = true;

            int end = (int)sl.Offset + sl.Count;
            if ((uint)end > (uint)_cosPayloadIds.Length)
                continue;

            for (int pIdx = (int)sl.Offset; pIdx < end; pIdx++)
            {
                int pid = _cosPayloadIds[pIdx];
                if ((uint)pid >= (uint)_payloads.Length)
                    continue;
                ref readonly CosPayload p = ref _payloads[pid];
                if (p.Kind == CosPayload.KindUnhideCss)
                    unhidden.Add(p.Text);
            }
        }

        if (matchCount == 0)
        {
            return unhidden.Count == 0 && !genericHide && !uninjectAll
                ? HostCosmeticResources.Empty
                : new HostCosmeticResources(
                    normalized, genericHide, uninjectAll,
                    Array.Empty<string>(), Array.Empty<string>(),
                    Array.Empty<string>(), Array.Empty<string>(),
                    unhidden);
        }

        bool unhideEverything = unhidden.Contains("*");
        var hideSeen = new HashSet<string>(StringComparer.Ordinal);
        var hideSelectors = new List<string>();
        var styleSeen = new HashSet<string>(StringComparer.Ordinal);
        var styleCssRules = new List<string>();
        var procSeen = new HashSet<string>(StringComparer.Ordinal);
        var proceduralRules = new List<string>();
        var scriptletSeen = new HashSet<string>(StringComparer.Ordinal);
        var scriptlets = new List<string>();

        for (int i = 0; i < matchCount; i++)
        {
            var sl = _cosHostSlices[sliceIndices[i]];
            int end = (int)sl.Offset + sl.Count;
            if ((uint)end > (uint)_cosPayloadIds.Length)
                continue;

            for (int pIdx = (int)sl.Offset; pIdx < end; pIdx++)
            {
                int pid = _cosPayloadIds[pIdx];
                if ((uint)pid >= (uint)_payloads.Length)
                    continue;
                ref readonly CosPayload p = ref _payloads[pid];
                if (p.Kind == CosPayload.KindUnhideCss)
                    continue;

                if (MatchesAnyNegHost(normalized, entityKey, p.NegHosts))
                    continue;

                if (p.Kind == CosPayload.KindHideCss)
                {
                    if (!unhideEverything && !unhidden.Contains(p.Text) && hideSeen.Add(p.Text))
                        hideSelectors.Add(p.Text);
                }
                else if (p.Kind == CosPayload.KindScriptlet)
                {
                    if (uninjectAll)
                        continue;
                    if (p.Permission != 0 && !allowPrivileged)
                        continue;
                    if (IsScriptletUninjected(p.Text, rawUninjectRules, resources))
                        continue;
                    if (scriptletSeen.Add(p.Text))
                        scriptlets.Add(p.Text);
                }
                else if (p.Kind == CosPayload.KindProcedural)
                {
                    if (unhideEverything || unhidden.Contains(p.Text))
                        continue;

                    if (TrySplitPlainCssStyleRule(p.Text, out string cssRule))
                    {
                        if (styleSeen.Add(cssRule))
                            styleCssRules.Add(cssRule);
                    }
                    else if (procSeen.Add(p.Text))
                    {
                        proceduralRules.Add(p.Text);
                    }
                }
            }
        }

        return new HostCosmeticResources(
            normalized,
            genericHide,
            uninjectAll,
            hideSelectors,
            styleCssRules,
            proceduralRules,
            scriptlets,
            unhidden);
    }

    /// <summary>
    /// Consulta bajo demanda los selectores genéricos (.class / #id) observados en el DOM actual,
    /// aplicando <c>$generichide</c> y las excepciones <c>#@#</c> específicas del host.
    /// </summary>
    public string QueryGenericCss(
        string? host,
        IReadOnlyList<string>? classes,
        IReadOnlyList<string>? ids,
        bool genericHide,
        EffectiveSitePolicy policy)
    {
        if (!policy.ShieldEnabled || !policy.Cosmetic || genericHide)
            return string.Empty;

        string normalized = NormalizeHost(host);
        if (string.IsNullOrEmpty(normalized) || ShieldIsolation.IsIsolatedHost(normalized))
            return string.Empty;

        bool hasClasses = classes != null && classes.Count > 0;
        bool hasIds = ids != null && ids.Count > 0;
        if (!hasClasses && !hasIds)
            return string.Empty;

        HashSet<string>? unhidden = CollectUnhiddenOnly(normalized);
        if (unhidden != null && unhidden.Contains("*"))
            return string.Empty;

        var matched = new List<string>();
        var dedup = new HashSet<string>(StringComparer.Ordinal);

        if (hasClasses)
        {
            for (int i = 0; i < classes!.Count && matched.Count < MaxGenericSelectorsPerQuery; i++)
            {
                string cls = classes[i];
                if (string.IsNullOrWhiteSpace(cls) || cls.Length > 128)
                    continue;

                if (_simpleClassHashes.Length > 0 && IsSafeCssToken(cls))
                {
                    ulong h = BraveHash.Fnv1a64(cls.AsSpan());
                    if (Array.BinarySearch(_simpleClassHashes, h) >= 0)
                    {
                        string sel = "." + EscapeCssToken(cls);
                        if ((unhidden == null || !unhidden.Contains(sel)) && dedup.Add(sel))
                            matched.Add(sel);
                    }
                }

                if (_complexClasses.TryGetValue(cls, out string[]? complexSels))
                {
                    foreach (string cs in complexSels)
                    {
                        if (matched.Count >= MaxGenericSelectorsPerQuery)
                            break;
                        if ((unhidden == null || !unhidden.Contains(cs)) && dedup.Add(cs))
                            matched.Add(cs);
                    }
                }
            }
        }

        if (hasIds)
        {
            for (int i = 0; i < ids!.Count && matched.Count < MaxGenericSelectorsPerQuery; i++)
            {
                string id = ids[i];
                if (string.IsNullOrWhiteSpace(id) || id.Length > 128)
                    continue;

                if (_simpleIdHashes.Length > 0 && IsSafeCssToken(id))
                {
                    ulong h = BraveHash.Fnv1a64(id.AsSpan());
                    if (Array.BinarySearch(_simpleIdHashes, h) >= 0)
                    {
                        string sel = "#" + EscapeCssToken(id);
                        if ((unhidden == null || !unhidden.Contains(sel)) && dedup.Add(sel))
                            matched.Add(sel);
                    }
                }

                if (_complexIds.TryGetValue(id, out string[]? complexSels))
                {
                    foreach (string cs in complexSels)
                    {
                        if (matched.Count >= MaxGenericSelectorsPerQuery)
                            break;
                        if ((unhidden == null || !unhidden.Contains(cs)) && dedup.Add(cs))
                            matched.Add(cs);
                    }
                }
            }
        }

        if (matched.Count == 0)
            return string.Empty;

        return string.Join(",", matched) + "{display:none!important;}";
    }

    internal static bool TrySplitPlainCssStyleRule(string rule, out string cssRule)
    {
        cssRule = string.Empty;
        if (string.IsNullOrWhiteSpace(rule))
            return false;

        int styleIdx = rule.IndexOf(":style(", StringComparison.Ordinal);
        if (styleIdx <= 0 || !rule.EndsWith(")", StringComparison.Ordinal))
            return false;

        // Verificar que los paréntesis de :style(...) cierren justo al final y que no haya un segundo :style(
        int bodyStart = styleIdx + ":style(".Length;
        int depth = 1;
        int closeIdx = -1;
        for (int i = bodyStart; i < rule.Length; i++)
        {
            char c = rule[i];
            if (c == '(') depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    closeIdx = i;
                    break;
                }
            }
        }

        if (closeIdx != rule.Length - 1)
            return false;

        string lhs = rule.Substring(0, styleIdx).Trim();
        string styleBody = rule.Substring(bodyStart, closeIdx - bodyStart).Trim();
        if (lhs.Length == 0 || styleBody.Length == 0)
            return false;

        if (lhs.IndexOf('{') >= 0 || lhs.IndexOf('}') >= 0 || lhs.IndexOf(';') >= 0)
            return false;
        if (styleBody.IndexOf('{') >= 0 || styleBody.IndexOf('}') >= 0 ||
            styleBody.Contains("</style", StringComparison.OrdinalIgnoreCase))
            return false;

        for (int i = 0; i < ProceduralTokens.Length; i++)
        {
            if (lhs.Contains(ProceduralTokens[i], StringComparison.Ordinal))
                return false;
        }

        cssRule = $"{lhs}{{{styleBody}}}";
        return true;
    }

    private HashSet<string>? CollectUnhiddenOnly(string normalizedHost)
    {
        if (_cosHostHashes.Length == 0)
            return null;

        Span<int> sliceIndices = stackalloc int[16];
        int matchCount = CollectHostSliceIndices(normalizedHost, out _, sliceIndices);
        if (matchCount == 0)
            return null;

        HashSet<string>? unhidden = null;
        for (int i = 0; i < matchCount; i++)
        {
            var sl = _cosHostSlices[sliceIndices[i]];
            int end = (int)sl.Offset + sl.Count;
            if ((uint)end > (uint)_cosPayloadIds.Length)
                continue;

            for (int pIdx = (int)sl.Offset; pIdx < end; pIdx++)
            {
                int pid = _cosPayloadIds[pIdx];
                if ((uint)pid >= (uint)_payloads.Length)
                    continue;
                ref readonly CosPayload p = ref _payloads[pid];
                if (p.Kind == CosPayload.KindUnhideCss)
                {
                    unhidden ??= new HashSet<string>(StringComparer.Ordinal);
                    unhidden.Add(p.Text);
                }
            }
        }

        return unhidden;
    }

    private int CollectHostSliceIndices(string normalizedHost, out string? entityKey, Span<int> dest)
    {
        int count = 0;
        entityKey = null;

        if (_cosHostHashes.Length == 0)
            return 0;

        int pos = 0;
        while (pos < normalizedHost.Length && count < dest.Length)
        {
            ReadOnlySpan<char> suffix = normalizedHost.AsSpan(pos);
            ulong h = BraveHash.Fnv1a64(suffix);
            int idx = Array.BinarySearch(_cosHostHashes, h);
            if (idx >= 0)
                dest[count++] = idx;

            int dot = suffix.IndexOf('.');
            if (dot < 0 || pos + dot + 1 >= normalizedHost.Length)
                break;

            ReadOnlySpan<char> afterDot = normalizedHost.AsSpan(pos + dot + 1);
            if (afterDot.IndexOf('.') < 0)
                break;

            pos += dot + 1;
        }

        string reg = PublicSuffix.GetRegistrableDomain(normalizedHost);
        if (!string.IsNullOrEmpty(reg))
        {
            int regDot = reg.IndexOf('.');
            if (regDot > 0)
            {
                entityKey = string.Concat(reg.AsSpan(0, regDot), ".*");
                if (count < dest.Length)
                {
                    ulong eh = BraveHash.Fnv1a64(entityKey.AsSpan());
                    int eIdx = Array.BinarySearch(_cosHostHashes, eh);
                    if (eIdx >= 0 && !ContainsInt(dest.Slice(0, count), eIdx))
                        dest[count++] = eIdx;
                }
            }
        }

        return count;
    }

    private List<string>? CollectHostUninjectRules(string normalizedHost, string? entityKey)
    {
        if (_hostUninjectMap.Count == 0)
            return null;

        List<string>? collected = null;
        int pos = 0;
        while (pos < normalizedHost.Length)
        {
            string suffix = pos == 0 ? normalizedHost : normalizedHost.Substring(pos);
            if (_hostUninjectMap.TryGetValue(suffix, out string[]? rules))
            {
                collected ??= new List<string>();
                collected.AddRange(rules);
            }

            int dot = normalizedHost.IndexOf('.', pos);
            if (dot < 0 || dot + 1 >= normalizedHost.Length)
                break;
            int nextDot = normalizedHost.IndexOf('.', dot + 1);
            if (nextDot < 0)
                break;

            pos = dot + 1;
        }

        if (!string.IsNullOrEmpty(entityKey) && _hostUninjectMap.TryGetValue(entityKey, out string[]? entRules))
        {
            collected ??= new List<string>();
            collected.AddRange(entRules);
        }

        return collected;
    }

    private static bool IsScriptletUninjected(
        string scriptletInner,
        List<string>? uninjectRules,
        BraveResourceStore? resources)
    {
        if (uninjectRules == null || uninjectRules.Count == 0)
            return false;

        List<string> targetArgs = BraveScriptletEngine.ParseScriptletArgs(scriptletInner);
        if (targetArgs.Count == 0)
            return false;

        string targetName = ResolveScriptletIdentity(targetArgs[0], resources);

        for (int i = 0; i < uninjectRules.Count; i++)
        {
            string rule = uninjectRules[i];
            if (string.Equals(rule, scriptletInner, StringComparison.Ordinal))
                return true;

            List<string> excArgs = BraveScriptletEngine.ParseScriptletArgs(rule);
            if (excArgs.Count == 0)
                continue;

            string excName = ResolveScriptletIdentity(excArgs[0], resources);
            if (!string.Equals(targetName, excName, StringComparison.OrdinalIgnoreCase))
                continue;

            // `#@#+js(nombre)` sin argumentos desinyecta todas las invocaciones de ese scriptlet en el host
            if (excArgs.Count == 1)
                return true;

            if (excArgs.Count == targetArgs.Count)
            {
                bool allMatch = true;
                for (int a = 1; a < excArgs.Count; a++)
                {
                    if (!string.Equals(excArgs[a], targetArgs[a], StringComparison.Ordinal))
                    {
                        allMatch = false;
                        break;
                    }
                }
                if (allMatch)
                    return true;
            }
        }

        return false;
    }

    private static string ResolveScriptletIdentity(string rawName, BraveResourceStore? resources)
    {
        string trimmed = rawName.Trim();
        if (resources != null)
        {
            string canonical = resources.ResolveCanonicalName(trimmed);
            if (!string.Equals(canonical, trimmed, StringComparison.OrdinalIgnoreCase) ||
                canonical.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            {
                return canonical;
            }

            if (!trimmed.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                return resources.ResolveCanonicalName(trimmed + ".js");
        }

        return trimmed.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
            ? trimmed.Substring(0, trimmed.Length - 3)
            : trimmed;
    }

    private static bool MatchesAnyNegHost(string normalizedHost, string? entityKey, string[]? negHosts)
    {
        if (negHosts == null || negHosts.Length == 0)
            return false;

        for (int i = 0; i < negHosts.Length; i++)
        {
            string nh = negHosts[i];
            if (nh.EndsWith(".*", StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(entityKey) && string.Equals(entityKey, nh, StringComparison.Ordinal))
                    return true;
            }
            else if (PublicSuffix.HostMatchesDomain(normalizedHost, nh))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsInt(ReadOnlySpan<int> span, int value)
    {
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] == value)
                return true;
        }
        return false;
    }

    private static string NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return string.Empty;
        return host.Trim().TrimEnd('.').ToLowerInvariant();
    }

    internal static bool IsSafeCssToken(string token)
    {
        if (string.IsNullOrEmpty(token))
            return false;

        for (int i = 0; i < token.Length; i++)
        {
            char c = token[i];
            bool ok = (c >= 'a' && c <= 'z') ||
                      (c >= 'A' && c <= 'Z') ||
                      (c >= '0' && c <= '9') ||
                      c == '-' ||
                      c == '_';
            if (!ok)
                return false;
        }
        return true;
    }

    internal static string EscapeCssToken(string token)
    {
        // Si empieza con dígito o con guion seguido de dígito, escapamos el primer dígito según CSS Syntax Level 3
        if (token[0] >= '0' && token[0] <= '9')
            return $"\\3{token[0]} {token.Substring(1)}";
        if (token.Length >= 2 && token[0] == '-' && token[1] >= '0' && token[1] <= '9')
            return $"-\\3{token[1]} {token.Substring(2)}";
        return token;
    }

    private static ulong[] ReadUInt64Array(byte[] raw)
    {
        int count = raw.Length / 8;
        var arr = new ulong[count];
        for (int i = 0; i < count; i++)
            arr[i] = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(i * 8, 8));
        return arr;
    }

    private static int[] ReadInt32Array(byte[] raw)
    {
        int count = raw.Length / 4;
        var arr = new int[count];
        for (int i = 0; i < count; i++)
            arr[i] = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(i * 4, 4));
        return arr;
    }

    private static CosHostSlice[] ReadHostSlices(byte[] raw)
    {
        int count = raw.Length / 8;
        var arr = new CosHostSlice[count];
        for (int i = 0; i < count; i++)
        {
            int off = i * 8;
            uint sliceOff = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(off, 4));
            ushort sliceCount = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(off + 4, 2));
            byte flags = raw[off + 6];
            byte pad = raw[off + 7];
            arr[i] = new CosHostSlice(sliceOff, sliceCount, flags, pad);
        }
        return arr;
    }

    private static (CosPayload[], Dictionary<string, string[]>) ParseSection18(byte[] utf8Json)
    {
        if (utf8Json.Length == 0)
            return (Array.Empty<CosPayload>(), new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase));

        using var doc = JsonDocument.Parse(utf8Json);
        var root = doc.RootElement;

        CosPayload[] payloads = Array.Empty<CosPayload>();
        if (root.TryGetProperty("p", out var pArr) && pArr.ValueKind == JsonValueKind.Array)
        {
            payloads = new CosPayload[pArr.GetArrayLength()];
            int idx = 0;
            foreach (var item in pArr.EnumerateArray())
            {
                byte kind = item.TryGetProperty("k", out var kEl) ? (byte)kEl.GetInt32() : (byte)0;
                string text = item.TryGetProperty("t", out var tEl) ? (tEl.GetString() ?? string.Empty) : string.Empty;
                byte perm = item.TryGetProperty("p", out var pEl) ? (byte)pEl.GetInt32() : (byte)0;

                string[]? negHosts = null;
                if (item.TryGetProperty("nh", out var nhEl) && nhEl.ValueKind == JsonValueKind.Array)
                {
                    int nhLen = nhEl.GetArrayLength();
                    if (nhLen > 0)
                    {
                        negHosts = new string[nhLen];
                        int nIdx = 0;
                        foreach (var nItem in nhEl.EnumerateArray())
                            negHosts[nIdx++] = nItem.GetString() ?? string.Empty;
                    }
                }

                payloads[idx++] = new CosPayload(kind, text, negHosts, perm);
            }
        }

        var uiMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("ui", out var uiObj) && uiObj.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in uiObj.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Array)
                    continue;
                int len = prop.Value.GetArrayLength();
                var list = new string[len];
                int idx = 0;
                foreach (var entry in prop.Value.EnumerateArray())
                    list[idx++] = entry.GetString() ?? string.Empty;
                uiMap[prop.Name] = list;
            }
        }

        return (payloads, uiMap);
    }

    private static (Dictionary<string, string[]>, Dictionary<string, string[]>, string[]) ParseSection19(byte[] utf8Json)
    {
        var cc = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var ci = new Dictionary<string, string[]>(StringComparer.Ordinal);
        string[] mg = Array.Empty<string>();

        if (utf8Json.Length == 0)
            return (cc, ci, mg);

        using var doc = JsonDocument.Parse(utf8Json);
        var root = doc.RootElement;

        if (root.TryGetProperty("cc", out var ccEl) && ccEl.ValueKind == JsonValueKind.Object)
            ReadStringArrayMap(ccEl, cc);

        if (root.TryGetProperty("ci", out var ciEl) && ciEl.ValueKind == JsonValueKind.Object)
            ReadStringArrayMap(ciEl, ci);

        if (root.TryGetProperty("mg", out var mgEl) && mgEl.ValueKind == JsonValueKind.Array)
        {
            mg = new string[mgEl.GetArrayLength()];
            int idx = 0;
            foreach (var item in mgEl.EnumerateArray())
                mg[idx++] = item.GetString() ?? string.Empty;
        }

        return (cc, ci, mg);
    }

    private static void ReadStringArrayMap(JsonElement obj, Dictionary<string, string[]> target)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Array)
                continue;
            var arr = new string[prop.Value.GetArrayLength()];
            int idx = 0;
            foreach (var item in prop.Value.EnumerateArray())
                arr[idx++] = item.GetString() ?? string.Empty;
            target[prop.Name] = arr;
        }
    }
}
