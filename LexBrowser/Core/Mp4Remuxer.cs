using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LexBrowser.Core;

/// <summary>
/// Remuxer ultra-ligero fMP4 (DASH fragmentado) -> MP4 progresivo estándar (moov al inicio + mdat único).
/// Soporta 1 pista de video (H.264 avc1) y opcionalmente 1 pista de audio (AAC mp4a). Sin dependencias externas.
/// Lee en streaming desde disco: solo las tablas de muestras se procesan en memoria (0% saturación de RAM).
/// Genera la tabla stbl completa (stts, ctts, stss, stsc, stsz, stco/co64) compatible 100% con Windows Media Player (WMP).
/// </summary>
public static class Mp4Remuxer
{
    private struct Sample
    {
        public long Offset;
        public uint Size;
        public uint Duration;
        public int Cts;
        public bool Sync;
    }

    private readonly record struct BoxRef(string Type, int Start, int Size, int Header);
    private readonly record struct FileBox(string Type, long Start, long Size, int Header);

    private sealed class ChunkPlan
    {
        public int Track;
        public int First;
        public int Count;
        public double StartSec;
        public long Rel;
    }

    private sealed class Track : IDisposable
    {
        public bool IsVideo;
        public uint OutId;
        public FileStream Src = null!;
        public uint Timescale = 1000;
        public byte[] Trak = Array.Empty<byte>();
        public uint TrexDur, TrexSize, TrexFlags;
        public List<Sample> Samples = new();
        public List<ChunkPlan> Chunks = new();
        public ulong Duration;
        public void Dispose() => Src?.Dispose();
    }

    private sealed class W
    {
        private readonly MemoryStream _m = new();
        public W U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); _m.Write(b); return this; }
        public W U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); _m.Write(b); return this; }
        public W U64(ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, v); _m.Write(b); return this; }
        public byte[] ToArray() => _m.ToArray();
    }

    // ───────────────────────── API pública ─────────────────────────

    /// <summary>
    /// Remuxea los flujos fragmentados a un MP4 progresivo estándar con moov al inicio.
    /// audioPath puede ser null (si el video ya contiene audio o es solo video).
    /// </summary>
    public static void Mux(string videoPath, string? audioPath, string outputPath)
    {
        var tracks = new List<Track>();
        string tmp = outputPath + ".mux_tmp";
        try
        {
            tracks.Add(Load(videoPath, isVideo: true, outId: 1));
            if (!string.IsNullOrEmpty(audioPath) && File.Exists(audioPath))
            {
                tracks.Add(Load(audioPath, isVideo: false, outId: 2));
            }

            foreach (var t in tracks)
            {
                if (t.Samples.Count == 0)
                    throw new InvalidDataException("El flujo no contiene muestras válidas (descarga incompleta o formato incompatible).");
            }

            for (int i = 0; i < tracks.Count; i++)
            {
                PlanChunks(tracks[i], i);
            }

            // Intercalado temporal (chunks de ~1 segundo) para reproducción fluida y seeking instantáneo en WMP
            var order = tracks.SelectMany(t => t.Chunks).OrderBy(c => c.StartSec).ThenBy(c => c.Track).ToList();
            long payload = 0;
            foreach (var c in order)
            {
                c.Rel = payload;
                var tr = tracks[c.Track];
                for (int i = c.First; i < c.First + c.Count; i++)
                {
                    payload += tr.Samples[i].Size;
                }
            }

            byte[][] stsds = tracks.Select(t => ExtractStsd(t)).ToArray();
            uint movieDurMs = (uint)Math.Min(uint.MaxValue, tracks.Max(t => t.Duration * 1000UL / Math.Max(1u, t.Timescale)));
            byte[] ftyp = BuildFtyp();
            bool co64 = payload > 0xE0000000L;
            int mdatHdr = payload + 8 > uint.MaxValue ? 16 : 8;

            byte[] moov = BuildMoov(tracks, stsds, 0, co64, movieDurMs);              // Pasada 1: calcular tamaño exacto
            long mdatStart = ftyp.Length + moov.Length + mdatHdr;
            moov = BuildMoov(tracks, stsds, mdatStart, co64, movieDurMs);              // Pasada 2: inyectar offsets reales

            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                fs.Write(ftyp);
                fs.Write(moov);
                if (mdatHdr == 8) fs.Write(new W().U32((uint)(payload + 8)).ToArray());
                else fs.Write(new W().U32(1).ToArray());
                fs.Write(Encoding.ASCII.GetBytes("mdat"));
                if (mdatHdr == 16) fs.Write(new W().U64((ulong)(payload + 16)).ToArray());

                var buf = new byte[1 << 18]; // 256 KB buffer para copia veloz
                foreach (var c in order)
                {
                    CopyChunk(tracks[c.Track], c, fs, buf);
                }
            }

            foreach (var t in tracks) t.Dispose();
            tracks.Clear();

            if (File.Exists(outputPath)) File.Delete(outputPath);
            File.Move(tmp, outputPath, overwrite: true);
        }
        catch
        {
            foreach (var t in tracks) t.Dispose();
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    // ───────────────────────── Lectura de fMP4 ─────────────────────────

    private static uint U32(byte[] d, int p) => BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p, 4));
    private static uint Rd(byte[] d, ref int p) { uint v = U32(d, p); p += 4; return v; }

    private static byte[] ReadAt(FileStream fs, long pos, int len)
    {
        var b = new byte[len];
        fs.Position = pos;
        fs.ReadExactly(b, 0, len);
        return b;
    }

    private static List<FileBox> ScanFile(FileStream fs)
    {
        var list = new List<FileBox>();
        var h = new byte[16];
        long pos = 0, len = fs.Length;
        while (pos + 8 <= len)
        {
            fs.Position = pos;
            fs.ReadExactly(h, 0, 8);
            long size = BinaryPrimitives.ReadUInt32BigEndian(h);
            string type = Encoding.ASCII.GetString(h, 4, 4);
            int hs = 8;
            if (size == 1)
            {
                if (pos + 16 > len) break;
                fs.ReadExactly(h, 8, 8);
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(h.AsSpan(8));
                hs = 16;
            }
            else if (size == 0) size = len - pos;
            if (size < hs || pos + size > len) break;
            list.Add(new FileBox(type, pos, size, hs));
            pos += size;
        }
        return list;
    }

    private static List<BoxRef> Kids(byte[] d, int start, int end)
    {
        var l = new List<BoxRef>();
        int pos = start;
        while (pos + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(pos, 4));
            int hs = 8;
            if (size == 1)
            {
                if (pos + 16 > end) break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(pos + 8, 8));
                hs = 16;
            }
            else if (size == 0) size = end - pos;
            if (size < hs || pos + size > end) break;
            l.Add(new BoxRef(Encoding.ASCII.GetString(d, pos + 4, 4), pos, (int)size, hs));
            pos += (int)size;
        }
        return l;
    }

    private static BoxRef? Find(byte[] d, params string[] path)
    {
        int s = 0, e = d.Length;
        BoxRef? cur = null;
        foreach (var name in path)
        {
            BoxRef? next = null;
            foreach (var k in Kids(d, s, e))
            {
                if (k.Type == name) { next = k; break; }
            }
            if (next == null) return null;
            cur = next;
            s = next.Value.Start + next.Value.Header;
            e = next.Value.Start + next.Value.Size;
        }
        return cur;
    }

    private static byte[] Slice(byte[] d, BoxRef r) => d.AsSpan(r.Start, r.Size).ToArray();

    private static Track Load(string path, bool isVideo, uint outId)
    {
        var t = new Track
        {
            IsVideo = isVideo,
            OutId = outId,
            Src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20)
        };
        try
        {
            var boxes = ScanFile(t.Src);
            var mb = boxes.FirstOrDefault(b => b.Type == "moov");
            if (mb.Size == 0) throw new InvalidDataException($"Falta caja 'moov' en {Path.GetFileName(path)}.");

            byte[] moov = ReadAt(t.Src, mb.Start, (int)mb.Size);
            var trak = Find(moov, "moov", "trak") ?? throw new InvalidDataException("Falta 'trak'.");
            t.Trak = Slice(moov, trak);

            var mdhd = Find(t.Trak, "trak", "mdia", "mdhd") ?? throw new InvalidDataException("Falta 'mdhd'.");
            int tsOff = mdhd.Start + 8 + 4 + (t.Trak[mdhd.Start + 8] == 1 ? 16 : 8);
            t.Timescale = Math.Max(1u, U32(t.Trak, tsOff));

            var trex = Find(moov, "moov", "mvex", "trex");
            if (trex != null)
            {
                int p = trex.Value.Start + 20; // header(8) + vf(4) + track_id(4) + desc_idx(4)
                if (p + 12 <= moov.Length)
                {
                    t.TrexDur = U32(moov, p);
                    t.TrexSize = U32(moov, p + 4);
                    t.TrexFlags = U32(moov, p + 8);
                }
            }

            foreach (var b in boxes)
            {
                if (b.Type == "moof") ParseMoof(t, b);
            }
            return t;
        }
        catch { t.Dispose(); throw; }
    }

    private static void ParseMoof(Track t, FileBox mb)
    {
        byte[] d = ReadAt(t.Src, mb.Start, (int)mb.Size);
        long fileLen = t.Src.Length;

        foreach (var traf in Kids(d, mb.Header, d.Length))
        {
            if (traf.Type != "traf") continue;
            uint defDur = t.TrexDur, defSize = t.TrexSize, defFlags = t.TrexFlags;
            long baseOff = mb.Start, runEnd = -1;

            foreach (var c in Kids(d, traf.Start + traf.Header, traf.Start + traf.Size))
            {
                int p = c.Start + c.Header;
                if (c.Type == "tfhd")
                {
                    uint fl = U32(d, p) & 0xFFFFFF; p += 8;
                    if ((fl & 0x1) != 0) { baseOff = (long)BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(p, 8)); p += 8; }
                    if ((fl & 0x2) != 0) p += 4;
                    if ((fl & 0x8) != 0) defDur = Rd(d, ref p);
                    if ((fl & 0x10) != 0) defSize = Rd(d, ref p);
                    if ((fl & 0x20) != 0) defFlags = Rd(d, ref p);
                }
                else if (c.Type == "trun")
                {
                    uint fl = U32(d, p) & 0xFFFFFF; p += 4;
                    uint n = Rd(d, ref p);
                    bool hasDo = (fl & 0x1) != 0, hasFf = (fl & 0x4) != 0;
                    long dataOff = 0;
                    if (hasDo) { dataOff = (int)Rd(d, ref p); }
                    uint firstFlags = 0;
                    if (hasFf) firstFlags = Rd(d, ref p);

                    long cur = hasDo ? baseOff + dataOff : (runEnd >= 0 ? runEnd : baseOff);
                    for (uint i = 0; i < n; i++)
                    {
                        uint dur = (fl & 0x100) != 0 ? Rd(d, ref p) : defDur;
                        uint size = (fl & 0x200) != 0 ? Rd(d, ref p) : defSize;
                        uint sf = (fl & 0x400) != 0 ? Rd(d, ref p) : defFlags;
                        int cts = (fl & 0x800) != 0 ? (int)Rd(d, ref p) : 0;
                        if (i == 0 && hasFf) sf = firstFlags;

                        if (cur + size > fileLen) return;
                        t.Samples.Add(new Sample { Offset = cur, Size = size, Duration = dur, Cts = cts, Sync = (sf & 0x10000) == 0 });
                        cur += size;
                    }
                    runEnd = cur;
                }
            }
        }
    }

    // ───────────────────────── Planificación e intercalado ─────────────────────────

    private static void PlanChunks(Track t, int ti)
    {
        long acc = 0, chunkStart = 0;
        int first = 0;
        for (int i = 0; i < t.Samples.Count; i++)
        {
            acc += t.Samples[i].Duration;
            bool last = i == t.Samples.Count - 1;
            if (acc - chunkStart >= t.Timescale || last)
            {
                t.Chunks.Add(new ChunkPlan { Track = ti, First = first, Count = i - first + 1, StartSec = (double)chunkStart / Math.Max(1u, t.Timescale) });
                first = i + 1;
                chunkStart = acc;
            }
        }
        t.Duration = (ulong)acc;
    }

    private static void CopyChunk(Track t, ChunkPlan c, Stream dst, byte[] buf)
    {
        long start = -1, len = 0;
        for (int i = c.First; i < c.First + c.Count; i++)
        {
            var s = t.Samples[i];
            if (len > 0 && s.Offset == start + len) len += s.Size;
            else { Flush(); start = s.Offset; len = s.Size; }
        }
        Flush();

        void Flush()
        {
            if (len <= 0) return;
            t.Src.Position = start;
            long left = len;
            while (left > 0)
            {
                int r = t.Src.Read(buf, 0, (int)Math.Min(buf.Length, left));
                if (r <= 0) throw new EndOfStreamException();
                dst.Write(buf, 0, r);
                left -= r;
            }
            len = 0;
        }
    }

    // ───────────────────────── Construcción de cajas MP4 estándar ─────────────────────────

    private static byte[] Box(string type, params byte[][] parts)
    {
        int len = 8;
        foreach (var p in parts) len += p.Length;
        var r = new byte[len];
        BinaryPrimitives.WriteUInt32BigEndian(r, (uint)len);
        Encoding.ASCII.GetBytes(type, 0, 4, r, 4);
        int o = 8;
        foreach (var p in parts) { Buffer.BlockCopy(p, 0, r, o, p.Length); o += p.Length; }
        return r;
    }

    private static byte[] Full(string type, uint versionFlags, W body)
        => Box(type, new W().U32(versionFlags).ToArray(), body.ToArray());

    private static byte[] BuildFtyp()
        => Box("ftyp", Encoding.ASCII.GetBytes("isom"), new W().U32(512).ToArray(),
               Encoding.ASCII.GetBytes("isom"), Encoding.ASCII.GetBytes("iso2"),
               Encoding.ASCII.GetBytes("avc1"), Encoding.ASCII.GetBytes("mp41"));

    private static byte[] ExtractStsd(Track t)
    {
        var r = Find(t.Trak, "trak", "mdia", "minf", "stbl", "stsd") ?? throw new InvalidDataException("Falta 'stsd'.");
        return Slice(t.Trak, r);
    }

    private static byte[] BuildMvhd(uint durMs)
    {
        var w = new W().U32(0)              // version/flags
            .U32(0).U32(0)                  // creation / modification
            .U32(1000).U32(durMs)           // timescale / duration
            .U32(0x00010000).U16(0x0100).U16(0).U32(0).U32(0)  // rate, volume, reserved
            .U32(0x00010000).U32(0).U32(0)
            .U32(0).U32(0x00010000).U32(0)
            .U32(0).U32(0).U32(0x40000000)  // matriz identidad
            .U32(0).U32(0).U32(0).U32(0).U32(0).U32(0) // pre_defined
            .U32(3);                        // next_track_ID
        return Box("mvhd", w.ToArray());
    }

    private static byte[] BuildMoov(List<Track> tracks, byte[][] stsds, long mdatStart, bool co64, uint movieDurMs)
    {
        var parts = new List<byte[]> { BuildMvhd(movieDurMs) };
        for (int i = 0; i < tracks.Count; i++)
        {
            parts.Add(RebuildTrak(tracks[i], BuildStbl(tracks[i], stsds[i], mdatStart, co64)));
        }
        return Box("moov", parts.ToArray());
    }

    private static byte[] BuildStbl(Track t, byte[] stsd, long mdatStart, bool co64)
    {
        int n = t.Samples.Count;
        var parts = new List<byte[]> { stsd };

        // stts: Tabla de duraciones de muestras
        var runs = new List<(uint C, uint D)>();
        foreach (var s in t.Samples)
        {
            if (runs.Count > 0 && runs[^1].D == s.Duration) runs[^1] = (runs[^1].C + 1, s.Duration);
            else runs.Add((1, s.Duration));
        }
        var w = new W().U32((uint)runs.Count);
        foreach (var r in runs) w.U32(r.C).U32(r.D);
        parts.Add(Full("stts", 0, w));

        // ctts: Desplazamientos de composición para B-frames
        if (t.Samples.Any(s => s.Cts != 0))
        {
            bool neg = t.Samples.Any(s => s.Cts < 0);
            var cr = new List<(uint C, int V)>();
            foreach (var s in t.Samples)
            {
                if (cr.Count > 0 && cr[^1].V == s.Cts) cr[^1] = (cr[^1].C + 1, s.Cts);
                else cr.Add((1, s.Cts));
            }
            w = new W().U32((uint)cr.Count);
            foreach (var r in cr) w.U32(r.C).U32(unchecked((uint)r.V));
            parts.Add(Full("ctts", neg ? 0x01000000u : 0u, w));
        }

        // stss: Keyframes de sincronización (para video)
        if (t.IsVideo && t.Samples.Any(s => !s.Sync))
        {
            var idx = new List<uint>();
            for (int i = 0; i < n; i++) if (t.Samples[i].Sync) idx.Add((uint)(i + 1));
            w = new W().U32((uint)idx.Count);
            foreach (var v in idx) w.U32(v);
            parts.Add(Full("stss", 0, w));
        }

        // stsc: Mapeo de muestras por chunk
        var sc = new List<(uint First, uint Spc)>();
        for (int i = 0; i < t.Chunks.Count; i++)
        {
            if (sc.Count == 0 || sc[^1].Spc != (uint)t.Chunks[i].Count)
                sc.Add(((uint)(i + 1), (uint)t.Chunks[i].Count));
        }
        w = new W().U32((uint)sc.Count);
        foreach (var e in sc) w.U32(e.First).U32(e.Spc).U32(1);
        parts.Add(Full("stsc", 0, w));

        // stsz: Tamaños de cada muestra individual
        w = new W().U32(0).U32((uint)n);
        foreach (var s in t.Samples) w.U32(s.Size);
        parts.Add(Full("stsz", 0, w));

        // stco / co64: Offsets absolutos de cada chunk dentro del archivo
        w = new W().U32((uint)t.Chunks.Count);
        foreach (var c in t.Chunks)
        {
            long off = mdatStart + c.Rel;
            if (co64) w.U64((ulong)off); else w.U32((uint)off);
        }
        parts.Add(Full(co64 ? "co64" : "stco", 0, w));

        return Box("stbl", parts.ToArray());
    }

    private static byte[] Container(byte[] box, Func<byte[], BoxRef, byte[]?> map)
    {
        string type = Encoding.ASCII.GetString(box, 4, 4);
        var parts = new List<byte[]>();
        foreach (var k in Kids(box, 8, box.Length))
        {
            var r = map(box, k);
            if (r != null) parts.Add(r);
        }
        return Box(type, parts.ToArray());
    }

    private static byte[] RebuildTrak(Track t, byte[] stbl)
    {
        ulong movieDur = t.Duration * 1000UL / Math.Max(1u, t.Timescale);
        return Container(t.Trak, (pb, k) => k.Type switch
        {
            "tkhd" => PatchTkhd(Slice(pb, k), t.OutId, movieDur),
            "mdia" => Container(Slice(pb, k), (mb, m) => m.Type switch
            {
                "mdhd" => PatchMdhd(Slice(mb, m), t.Duration),
                "minf" => Container(Slice(mb, m), (fb, f) => f.Type == "stbl" ? stbl : Slice(fb, f)),
                _ => Slice(mb, m)
            }),
            _ => null // Descartar edts/udta para evitar desfases de duración obsoleta
        });
    }

    private static byte[] PatchTkhd(byte[] b, uint trackId, ulong durMovie)
    {
        b[11] |= 0x03; // enabled + in_movie
        if (b[8] == 1)
        {
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(28, 4), trackId);
            BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(36, 8), durMovie);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20, 4), trackId);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(28, 4), (uint)Math.Min(durMovie, uint.MaxValue));
        }
        return b;
    }

    private static byte[] PatchMdhd(byte[] b, ulong durMedia)
    {
        if (b[8] == 1) BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(32, 8), durMedia);
        else BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(24, 4), (uint)Math.Min(durMedia, uint.MaxValue));
        return b;
    }
}
