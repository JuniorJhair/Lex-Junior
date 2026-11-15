using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LexBrowser.Core.AdBlock.Brave;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct BravePatRec
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
public struct BraveBucketRange
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
public sealed class BraveShieldData
{
    private const string Magic = "LEXSHLD2";
    private const uint SupportedVersion = 2;
    private const int ExpectedSectionCount = 22;
    private const int TailStartSection = 13;

    private static readonly object InitLock = new();
    private static BraveShieldData? _cachedDefault;
    private static bool _defaultLoadAttempted;
    private static Task? _preloadTask;

    private readonly CompressedTailSlice[] _tailSections;
    private readonly Lazy<BraveResourceStore> _resourceStoreLazy;
    private readonly Lazy<BraveCosmeticEngine> _cosmeticEngineLazy;

    public string MetaJson { get; }
    public ulong[] PureHashes { get; }
    public uint[] PureMasks { get; }
    public BravePatRec[] PatternRules { get; }
    public byte[] StrBlob { get; }
    public ulong[] HashPool { get; }
    public ulong[] TokKeys { get; }
    public BraveBucketRange[] TokRanges { get; }
    public int[] TokVals { get; }
    public ulong[] OptKeys { get; }
    public BraveBucketRange[] OptRanges { get; }
    public int[] OptVals { get; }
    public int[] FallbackRules { get; }

    private BraveShieldData(
        string metaJson,
        ulong[] pureHashes,
        uint[] pureMasks,
        BravePatRec[] patternRules,
        byte[] strBlob,
        ulong[] hashPool,
        ulong[] tokKeys,
        BraveBucketRange[] tokRanges,
        int[] tokVals,
        ulong[] optKeys,
        BraveBucketRange[] optRanges,
        int[] optVals,
        int[] fallbackRules,
        CompressedTailSlice[] tailSections)
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
        _resourceStoreLazy = new Lazy<BraveResourceStore>(() =>
        {
            byte[] idx = DecompressTailSection(20);
            byte[] blob = DecompressTailSection(21);
            return BraveResourceStore.LoadFromSections(idx, blob);
        }, LazyThreadSafetyMode.ExecutionAndPublication);
        _cosmeticEngineLazy = new Lazy<BraveCosmeticEngine>(() =>
        {
            if (_tailSections.Length == 0 || _tailSections[0].Compressed == null || _tailSections[0].Compressed.Length == 0)
                return BraveCosmeticEngine.Empty;

            return BraveCosmeticEngine.LoadFromSections(
                DecompressTailSection(13),
                DecompressTailSection(14),
                DecompressTailSection(15),
                DecompressTailSection(16),
                DecompressTailSection(17),
                DecompressTailSection(18),
                DecompressTailSection(19));
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public BraveResourceStore GetResourceStore() => _resourceStoreLazy.Value;

    public BraveCosmeticEngine GetCosmeticEngine() => _cosmeticEngineLazy.Value;

    public static BraveShieldData CreateForTests(
        ulong[]? pureHashes = null,
        uint[]? pureMasks = null,
        BravePatRec[]? patternRules = null,
        byte[]? strBlob = null,
        ulong[]? hashPool = null,
        ulong[]? tokKeys = null,
        BraveBucketRange[]? tokRanges = null,
        int[]? tokVals = null,
        ulong[]? optKeys = null,
        BraveBucketRange[]? optRanges = null,
        int[]? optVals = null,
        int[]? fallbackRules = null)
    {
        return new BraveShieldData(
            metaJson: "{}",
            pureHashes: pureHashes ?? Array.Empty<ulong>(),
            pureMasks: pureMasks ?? Array.Empty<uint>(),
            patternRules: patternRules ?? Array.Empty<BravePatRec>(),
            strBlob: strBlob ?? Array.Empty<byte>(),
            hashPool: hashPool ?? Array.Empty<ulong>(),
            tokKeys: tokKeys ?? Array.Empty<ulong>(),
            tokRanges: tokRanges ?? Array.Empty<BraveBucketRange>(),
            tokVals: tokVals ?? Array.Empty<int>(),
            optKeys: optKeys ?? Array.Empty<ulong>(),
            optRanges: optRanges ?? Array.Empty<BraveBucketRange>(),
            optVals: optVals ?? Array.Empty<int>(),
            fallbackRules: fallbackRules ?? Array.Empty<int>(),
            tailSections: new CompressedTailSlice[ExpectedSectionCount - TailStartSection]);
    }

    public static BraveShieldData? TryGetDefault()
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
                const string resourceSuffix = "Assets.shield.brave-shield.lex";
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
                ShieldLogger.Log("BRAVE_LOAD_ERROR", "local", "brave-shield.lex", "Startup", ex.Message, "ShieldData");
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

            string direct = Path.Combine(root, "Assets", "shield", "brave-shield.lex");
            if (File.Exists(direct))
                return direct;

            var dir = new DirectoryInfo(root);
            for (int depth = 0; depth < 7 && dir != null; depth++, dir = dir.Parent)
            {
                string cand1 = Path.Combine(dir.FullName, "Assets", "shield", "brave-shield.lex");
                if (File.Exists(cand1))
                    return cand1;

                string cand2 = Path.Combine(dir.FullName, "LexBrowser", "Assets", "shield", "brave-shield.lex");
                if (File.Exists(cand2))
                    return cand2;
            }
        }

        return null;
    }

    public static BraveShieldData LoadFromPath(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return LoadFromStream(fs);
    }

    public static BraveShieldData LoadFromStream(Stream stream)
    {
        Span<byte> header = stackalloc byte[16];
        stream.ReadExactly(header);

        if (Encoding.ASCII.GetString(header.Slice(0, 8)) != Magic)
            throw new InvalidDataException("Invalid brave-shield header magic.");

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(8, 4));
        if (version != SupportedVersion)
            throw new InvalidDataException($"Unsupported brave-shield version {version}.");

        uint sectionCount = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(12, 4));
        if (sectionCount < ExpectedSectionCount)
            throw new InvalidDataException($"Unexpected brave-shield section count {sectionCount}.");

        byte[][] headRaw = new byte[TailStartSection][];
        CompressedTailSlice[] tail = new CompressedTailSlice[ExpectedSectionCount - TailStartSection];

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
            else if (i < ExpectedSectionCount)
            {
                tail[i - TailStartSection] = new CompressedTailSlice(compBytes, rawLen);
            }
        }

        string metaJson = Encoding.UTF8.GetString(headRaw[0]);
        ulong[] pureHashes = ReadStructArray<ulong>(headRaw[1]);
        uint[] pureMasks = ReadStructArray<uint>(headRaw[2]);
        BravePatRec[] patternRules = ReadStructArray<BravePatRec>(headRaw[3]);
        byte[] strBlob = headRaw[4];
        ulong[] hashPool = ReadStructArray<ulong>(headRaw[5]);
        ulong[] tokKeys = ReadStructArray<ulong>(headRaw[6]);
        BraveBucketRange[] tokRanges = ReadStructArray<BraveBucketRange>(headRaw[7]);
        int[] tokVals = ReadStructArray<int>(headRaw[8]);
        ulong[] optKeys = ReadStructArray<ulong>(headRaw[9]);
        BraveBucketRange[] optRanges = ReadStructArray<BraveBucketRange>(headRaw[10]);
        int[] optVals = ReadStructArray<int>(headRaw[11]);
        int[] fallbackRules = ReadStructArray<int>(headRaw[12]);

        return new BraveShieldData(
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
        if (sectionIndex < TailStartSection || sectionIndex >= ExpectedSectionCount)
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
