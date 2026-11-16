using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LexBrowser.Core.AdBlock.ShieldCore;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct LexShieldPatRec
{
    public uint Mask;
    public int HostOff;
    public int HostLen;
    public int PatOff;
    public int PatLen;
    public int ModOff;
    public int ModLen;
    public int IncDomOff;
    public int ExcDomOff;
    public int IncToOff;
    public int ExcToOff;
    public ushort IncDomLen;
    public ushort ExcDomLen;
    public ushort IncToLen;
    public ushort ExcToLen;
    public short RedirectPriority;
    public ushort Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct LexShieldBucketRange
{
    public uint Offset;
    public uint Count;
}

internal readonly struct CompressedTailSlice
{
    public CompressedTailSlice(byte[] compressed, int rawLength)
    {
        Compressed = compressed;
        RawLength = rawLength;
    }

    public byte[] Compressed { get; }
    public int RawLength { get; }
}

/// <summary>
/// Contenedor inmutable del paquete binario precompilado <c>LEXSHLD2</c>.
/// Las secciones de red (1..12) residen en arreglos planos primitivos sin punteros GC;
/// las secciones cosméticas y de recursos (13..21) permanecen comprimidas hasta su uso bajo demanda.
/// </summary>
public sealed class LexShieldData
{
    internal sealed class CompiledRegexBox
    {
        public CompiledRegexBox(Regex? value) => Value = value;
        public Regex? Value { get; }
    }

    private const string Magic = "LEXSHLD2";
    private const uint SupportedVersion = 2;
    private const int BaseSectionCount = 22;
    private const int TailStartSection = 13;
    private const int CspSectionIndex = 22;
    private const int RegexSectionIndex = 23;
    private static readonly TimeSpan RegexEvalTimeout = TimeSpan.FromMilliseconds(2);

    private static readonly object InitLock = new();
    private static LexShieldData? _cachedDefault;
    private static bool _defaultLoadAttempted;
    private static Task? _preloadTask;

    public static bool IsDefaultLoaded => Volatile.Read(ref _cachedDefault) != null;

    private readonly CompressedTailSlice[] _tailSections;
    private readonly Lazy<LexShieldResourceStore> _resourceStoreLazy;
    private readonly Lazy<LexShieldCosmeticEngine> _cosmeticEngineLazy;
    private readonly Lazy<LexShieldPatRec[]> _cspRulesLazy;
    private readonly Lazy<LexShieldPatRec[]> _regexRulesLazy;
    private readonly Lazy<CompiledRegexBox?[]> _cspRegexCacheLazy;
    private readonly Lazy<CompiledRegexBox?[]> _netRegexCacheLazy;

    public string MetaJson { get; }
    public ulong[] PureHashes { get; }
    public uint[] PureMasks { get; }
    public LexShieldPatRec[] PatternRules { get; }
    public byte[] StrBlob { get; }
    public ulong[] HashPool { get; }
    public ulong[] TokKeys { get; }
    public LexShieldBucketRange[] TokRanges { get; }
    public int[] TokVals { get; }
    public ulong[] OptKeys { get; }
    public LexShieldBucketRange[] OptRanges { get; }
    public int[] OptVals { get; }
    public int[] FallbackRules { get; }

    private LexShieldData(
        string metaJson,
        ulong[] pureHashes,
        uint[] pureMasks,
        LexShieldPatRec[] patternRules,
        byte[] strBlob,
        ulong[] hashPool,
        ulong[] tokKeys,
        LexShieldBucketRange[] tokRanges,
        int[] tokVals,
        ulong[] optKeys,
        LexShieldBucketRange[] optRanges,
        int[] optVals,
        int[] fallbackRules,
        CompressedTailSlice[] tailSections,
        LexShieldPatRec[]? cspOverride = null,
        LexShieldPatRec[]? regexOverride = null)
    {
        MetaJson = metaJson;
        PureHashes = pureHashes;
        PureMasks = pureMasks;
        PatternRules = patternRules;
        StrBlob = strBlob;
        HashPool = hashPool;
        TokKeys = tokKeys;
        TokRanges = tokRanges;
        TokVals = tokVals;
        OptKeys = optKeys;
        OptRanges = optRanges;
        OptVals = optVals;
        FallbackRules = fallbackRules;
        _tailSections = tailSections;
        _resourceStoreLazy = new Lazy<LexShieldResourceStore>(() =>
        {
            byte[] idx = DecompressTailSection(20);
            byte[] blob = DecompressTailSection(21);
            return LexShieldResourceStore.LoadFromSections(idx, blob);
        }, LazyThreadSafetyMode.ExecutionAndPublication);
        _cosmeticEngineLazy = new Lazy<LexShieldCosmeticEngine>(() =>
        {
            if (_tailSections.Length == 0 || _tailSections[0].Compressed == null || _tailSections[0].Compressed.Length == 0)
                return LexShieldCosmeticEngine.Empty;

            return LexShieldCosmeticEngine.LoadFromSections(
                DecompressTailSection(13),
                DecompressTailSection(14),
                DecompressTailSection(15),
                DecompressTailSection(16),
                DecompressTailSection(17),
                DecompressTailSection(18),
                DecompressTailSection(19));
        }, LazyThreadSafetyMode.ExecutionAndPublication);
        _cspRulesLazy = new Lazy<LexShieldPatRec[]>(() =>
        {
            if (cspOverride != null)
                return cspOverride;
            int rel = CspSectionIndex - TailStartSection;
            if (rel >= _tailSections.Length)
                return Array.Empty<LexShieldPatRec>();
            return ReadStructArray<LexShieldPatRec>(DecompressTailSection(CspSectionIndex));
        }, LazyThreadSafetyMode.ExecutionAndPublication);
        _regexRulesLazy = new Lazy<LexShieldPatRec[]>(() =>
        {
            if (regexOverride != null)
                return regexOverride;
            int rel = RegexSectionIndex - TailStartSection;
            if (rel >= _tailSections.Length)
                return Array.Empty<LexShieldPatRec>();
            return ReadStructArray<LexShieldPatRec>(DecompressTailSection(RegexSectionIndex));
        }, LazyThreadSafetyMode.ExecutionAndPublication);
        _cspRegexCacheLazy = new Lazy<CompiledRegexBox?[]>(
            () => new CompiledRegexBox?[_cspRulesLazy.Value.Length],
            LazyThreadSafetyMode.ExecutionAndPublication);
        _netRegexCacheLazy = new Lazy<CompiledRegexBox?[]>(
            () => new CompiledRegexBox?[_regexRulesLazy.Value.Length],
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public LexShieldResourceStore GetResourceStore() => _resourceStoreLazy.Value;

    public LexShieldCosmeticEngine GetCosmeticEngine() => _cosmeticEngineLazy.Value;

    public LexShieldPatRec[] GetCspRules() => _cspRulesLazy.Value;

    public LexShieldPatRec[] GetRegexRules() => _regexRulesLazy.Value;

    public static Regex? TryCompileRegex(string pattern, bool matchCase)
    {
        if (string.IsNullOrEmpty(pattern))
            return null;

        try
        {
            var opts = RegexOptions.CultureInvariant;
            if (!matchCase)
                opts |= RegexOptions.IgnoreCase;
            return new Regex(pattern, opts, RegexEvalTimeout);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    internal Regex? GetOrCompileRegex(bool isCsp, int index)
    {
        var rules = isCsp ? _cspRulesLazy.Value : _regexRulesLazy.Value;
        if ((uint)index >= (uint)rules.Length)
            return null;

        var cache = isCsp ? _cspRegexCacheLazy.Value : _netRegexCacheLazy.Value;
        var existing = Volatile.Read(ref cache[index]);
        if (existing != null)
            return existing.Value;

        ref readonly var rec = ref rules[index];
        string pat = rec.PatLen > 0 ? Encoding.UTF8.GetString(StrBlob, rec.PatOff, rec.PatLen) : string.Empty;
        bool matchCase = (rec.Mask & (uint)LexShieldMask.IsMatchCase) != 0;
        var box = new CompiledRegexBox(TryCompileRegex(pat, matchCase));
        Volatile.Write(ref cache[index], box);
        return box.Value;
    }

    public static LexShieldData CreateForTests(
        ulong[]? pureHashes = null,
        uint[]? pureMasks = null,
        LexShieldPatRec[]? patternRules = null,
        byte[]? strBlob = null,
        ulong[]? hashPool = null,
        ulong[]? tokKeys = null,
        LexShieldBucketRange[]? tokRanges = null,
        int[]? tokVals = null,
        ulong[]? optKeys = null,
        LexShieldBucketRange[]? optRanges = null,
        int[]? optVals = null,
        int[]? fallbackRules = null,
        LexShieldPatRec[]? cspRules = null,
        LexShieldPatRec[]? regexRules = null)
    {
        return new LexShieldData(
            metaJson: "{}",
            pureHashes: pureHashes ?? Array.Empty<ulong>(),
            pureMasks: pureMasks ?? Array.Empty<uint>(),
            patternRules: patternRules ?? Array.Empty<LexShieldPatRec>(),
            strBlob: strBlob ?? Array.Empty<byte>(),
            hashPool: hashPool ?? Array.Empty<ulong>(),
            tokKeys: tokKeys ?? Array.Empty<ulong>(),
            tokRanges: tokRanges ?? Array.Empty<LexShieldBucketRange>(),
            tokVals: tokVals ?? Array.Empty<int>(),
            optKeys: optKeys ?? Array.Empty<ulong>(),
            optRanges: optRanges ?? Array.Empty<LexShieldBucketRange>(),
            optVals: optVals ?? Array.Empty<int>(),
            fallbackRules: fallbackRules ?? Array.Empty<int>(),
            tailSections: new CompressedTailSlice[BaseSectionCount - TailStartSection],
            cspOverride: cspRules,
            regexOverride: regexRules);
    }

    public static LexShieldData? TryGetDefault()
    {
        var existing = Volatile.Read(ref _cachedDefault);
        if (existing != null || Volatile.Read(ref _defaultLoadAttempted))
            return existing;

        lock (InitLock)
        {
            if (_cachedDefault != null || _defaultLoadAttempted)
                return _cachedDefault;

            try
            {
                string? assetPath = ResolveDefaultAssetPath();
                if (!string.IsNullOrEmpty(assetPath) && File.Exists(assetPath))
                {
                    using var fs = new FileStream(assetPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    _cachedDefault = LoadFromStream(fs);
                    return _cachedDefault;
                }

                var asm = Assembly.GetExecutingAssembly();
                const string resourceSuffix = "Assets.shield.lex-shield.lex";
                foreach (string name in asm.GetManifestResourceNames())
                {
                    if (name.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        using var resStream = asm.GetManifestResourceStream(name);
                        if (resStream != null)
                        {
                            _cachedDefault = LoadFromStream(resStream);
                            return _cachedDefault;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ShieldLogger.Log("LEXSHIELD_LOAD_ERROR", "local", "lex-shield.lex", "Startup", ex.Message, "ShieldData");
            }
            finally
            {
                Volatile.Write(ref _defaultLoadAttempted, true);
            }

            return null;
        }
    }

    public static void PreloadInBackground()
    {
        if (Volatile.Read(ref _cachedDefault) != null || Volatile.Read(ref _defaultLoadAttempted))
            return;

        lock (InitLock)
        {
            if (_preloadTask != null)
                return;

            _preloadTask = Task.Run(() =>
            {
                _ = TryGetDefault();
            });
        }
    }

    public static string? ResolveDefaultAssetPath()
    {
        string[] roots =
        {
            AppContext.BaseDirectory,
            AppDomain.CurrentDomain.BaseDirectory,
            Environment.CurrentDirectory
        };

        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            string direct = Path.Combine(root, "Assets", "shield", "lex-shield.lex");
            if (File.Exists(direct))
                return direct;

            var dir = new DirectoryInfo(root);
            for (int depth = 0; depth < 7 && dir != null; depth++, dir = dir.Parent)
            {
                string cand1 = Path.Combine(dir.FullName, "Assets", "shield", "lex-shield.lex");
                if (File.Exists(cand1))
                    return cand1;

                string cand2 = Path.Combine(dir.FullName, "LexBrowser", "Assets", "shield", "lex-shield.lex");
                if (File.Exists(cand2))
                    return cand2;
            }
        }

        return null;
    }

    public static LexShieldData LoadFromPath(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return LoadFromStream(fs);
    }

    public static LexShieldData LoadFromStream(Stream stream)
    {
        Span<byte> header = stackalloc byte[16];
        stream.ReadExactly(header);

        if (Encoding.ASCII.GetString(header.Slice(0, 8)) != Magic)
            throw new InvalidDataException("Invalid lex-shield header magic.");

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(8, 4));
        if (version != SupportedVersion)
            throw new InvalidDataException($"Unsupported lex-shield version {version}.");

        uint sectionCount = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(12, 4));
        if (sectionCount < BaseSectionCount)
            throw new InvalidDataException($"Unexpected lex-shield section count {sectionCount}.");

        byte[][] headRaw = new byte[TailStartSection][];
        int tailCount = Math.Max((int)sectionCount, BaseSectionCount) - TailStartSection;
        CompressedTailSlice[] tail = new CompressedTailSlice[tailCount];

        Span<byte> secHdr = stackalloc byte[8];
        for (int i = 0; i < sectionCount; i++)
        {
            stream.ReadExactly(secHdr);
            int compLen = BinaryPrimitives.ReadInt32LittleEndian(secHdr.Slice(0, 4));
            int rawLen = BinaryPrimitives.ReadInt32LittleEndian(secHdr.Slice(4, 4));
            if (compLen < 0 || rawLen < 0)
                throw new InvalidDataException($"Corrupt section lengths at index {i}.");

            byte[] compBytes = new byte[compLen];
            stream.ReadExactly(compBytes);

            if (i < TailStartSection)
            {
                headRaw[i] = InflateExact(compBytes, rawLen);
            }
            else
            {
                tail[i - TailStartSection] = new CompressedTailSlice(compBytes, rawLen);
            }
        }

        string metaJson = Encoding.UTF8.GetString(headRaw[0]);
        ulong[] pureHashes = ReadStructArray<ulong>(headRaw[1]);
        uint[] pureMasks = ReadStructArray<uint>(headRaw[2]);
        LexShieldPatRec[] patternRules = ReadStructArray<LexShieldPatRec>(headRaw[3]);
        byte[] strBlob = headRaw[4];
        ulong[] hashPool = ReadStructArray<ulong>(headRaw[5]);
        ulong[] tokKeys = ReadStructArray<ulong>(headRaw[6]);
        LexShieldBucketRange[] tokRanges = ReadStructArray<LexShieldBucketRange>(headRaw[7]);
        int[] tokVals = ReadStructArray<int>(headRaw[8]);
        ulong[] optKeys = ReadStructArray<ulong>(headRaw[9]);
        LexShieldBucketRange[] optRanges = ReadStructArray<LexShieldBucketRange>(headRaw[10]);
        int[] optVals = ReadStructArray<int>(headRaw[11]);
        int[] fallbackRules = ReadStructArray<int>(headRaw[12]);

        return new LexShieldData(
            metaJson,
            pureHashes,
            pureMasks,
            patternRules,
            strBlob,
            hashPool,
            tokKeys,
            tokRanges,
            tokVals,
            optKeys,
            optRanges,
            optVals,
            fallbackRules,
            tail);
    }

    internal byte[] DecompressTailSection(int sectionIndex)
    {
        if (sectionIndex < TailStartSection || sectionIndex >= TailStartSection + _tailSections.Length)
            throw new ArgumentOutOfRangeException(nameof(sectionIndex));

        var slice = _tailSections[sectionIndex - TailStartSection];
        if (slice.Compressed == null || slice.RawLength == 0)
            return Array.Empty<byte>();

        return InflateExact(slice.Compressed, slice.RawLength);
    }

    private static byte[] InflateExact(byte[] compressed, int rawLen)
    {
        if (rawLen == 0)
            return Array.Empty<byte>();

        byte[] output = new byte[rawLen];
        using var ms = new MemoryStream(compressed, writable: false);
        using var z = new ZLibStream(ms, CompressionMode.Decompress);
        z.ReadExactly(output);
        return output;
    }

    private static T[] ReadStructArray<T>(byte[] raw) where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        if (raw.Length == 0)
            return Array.Empty<T>();

        if (raw.Length % size != 0)
            throw new InvalidDataException($"Section byte length {raw.Length} is not a multiple of {typeof(T).Name} size {size}.");

        if (!BitConverter.IsLittleEndian)
            throw new PlatformNotSupportedException("Big-endian layout is not supported.");

        int count = raw.Length / size;
        var arr = new T[count];
        MemoryMarshal.Cast<byte, T>(raw).CopyTo(arr);
        return arr;
    }
}
