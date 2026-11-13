using System;
using System.Text;

namespace LexBrowser.Core.Tv;

public enum TvStreamKind
{
    Unknown,
    Hls,
    MpegTs,
    Flv,
    Mp4,
    WebM,
    Ogg,
    Mp3,
    AacAdts,
    Dash,
    Html
}

/// <summary>Clasificador puro por firma binaria/textual (primeros 4 KB del stream).</summary>
public static class TvStreamSniffer
{
    public static TvStreamKind Classify(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return TvStreamKind.Unknown;

        // Verificar firmas binarias desde byte 0 antes de recortar espacios
        if (LooksLikeMpegTs(data)) return TvStreamKind.MpegTs;
        if (data.Length >= 4 && data[0] == 0x1A && data[1] == 0x45 && data[2] == 0xDF && data[3] == 0xA3)
            return TvStreamKind.WebM;
        if (data.Length >= 4 && data[0] == (byte)'F' && data[1] == (byte)'L' && data[2] == (byte)'V' && data[3] <= 0x05)
            return TvStreamKind.Flv;
        if (data.Length >= 8 && data[4] == (byte)'f' && data[5] == (byte)'t' && data[6] == (byte)'y' && data[7] == (byte)'p')
            return TvStreamKind.Mp4;
        if (data.Length >= 4 && data[0] == (byte)'O' && data[1] == (byte)'g' && data[2] == (byte)'g' && data[3] == (byte)'S')
            return TvStreamKind.Ogg;
        if (data.Length >= 3 && data[0] == (byte)'I' && data[1] == (byte)'D' && data[2] == (byte)'3')
            return TvStreamKind.Mp3;

        // Saltar BOM UTF-8 y espacios en blanco iniciales para firmas de texto o sync de audio
        ReadOnlySpan<byte> trimmed = SkipBomAndWhitespace(data);
        if (trimmed.IsEmpty) return TvStreamKind.Unknown;

        if (StartsWithAsciiIgnoreCase(trimmed, "#EXTM3U"u8))
            return TvStreamKind.Hls;

        if (StartsWithAsciiIgnoreCase(trimmed, "<!DOCTYPE"u8) ||
            StartsWithAsciiIgnoreCase(trimmed, "<html"u8) ||
            StartsWithAsciiIgnoreCase(trimmed, "<head"u8) ||
            StartsWithAsciiIgnoreCase(trimmed, "<body"u8))
            return TvStreamKind.Html;

        if (StartsWithAsciiIgnoreCase(trimmed, "<MPD"u8))
            return TvStreamKind.Dash;

        if (StartsWithAsciiIgnoreCase(trimmed, "<?xml"u8))
        {
            int sampleLen = Math.Min(trimmed.Length, 1024);
            string head = Encoding.UTF8.GetString(trimmed[..sampleLen]);
            if (head.Contains("<MPD", StringComparison.OrdinalIgnoreCase))
                return TvStreamKind.Dash;
            if (head.Contains("<html", StringComparison.OrdinalIgnoreCase))
                return TvStreamKind.Html;
        }

        // Sync MPEG Audio / ADTS AAC sin etiqueta ID3
        if (data.Length >= 2 && data[0] == 0xFF)
        {
            if ((data[1] & 0xF6) == 0xF0) return TvStreamKind.AacAdts;
            if ((data[1] & 0xE6) == 0xE2) return TvStreamKind.Mp3;
        }

        return TvStreamKind.Unknown;
    }

    public static string ToApiName(TvStreamKind kind) => kind switch
    {
        TvStreamKind.Hls => "hls",
        TvStreamKind.MpegTs => "mpegts",
        TvStreamKind.Flv => "flv",
        TvStreamKind.Mp4 => "mp4",
        TvStreamKind.WebM => "webm",
        TvStreamKind.Ogg => "ogg",
        TvStreamKind.Mp3 => "mp3",
        TvStreamKind.AacAdts => "aacadts",
        TvStreamKind.Dash => "dash",
        TvStreamKind.Html => "html",
        _ => "unknown"
    };

    private static bool LooksLikeMpegTs(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 377)
            return data[0] == 0x47 && data[188] == 0x47 && data[376] == 0x47;
        if (data.Length >= 189)
            return data[0] == 0x47 && data[188] == 0x47;
        return false;
    }

    private static ReadOnlySpan<byte> SkipBomAndWhitespace(ReadOnlySpan<byte> data)
    {
        int i = 0;
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            i = 3;
        else if (data.Length >= 2 && ((data[0] == 0xFF && data[1] == 0xFE) || (data[0] == 0xFE && data[1] == 0xFF)))
            i = 2;

        while (i < data.Length && (data[i] == (byte)' ' || data[i] == (byte)'\t' || data[i] == (byte)'\r' || data[i] == (byte)'\n' || data[i] == 0))
            i++;

        return data[i..];
    }

    private static bool StartsWithAsciiIgnoreCase(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prefix)
    {
        if (data.Length < prefix.Length) return false;
        for (int i = 0; i < prefix.Length; i++)
        {
            byte a = data[i];
            byte b = prefix[i];
            if (a == b) continue;
            if (a >= (byte)'A' && a <= (byte)'Z') a += 32;
            if (b >= (byte)'A' && b <= (byte)'Z') b += 32;
            if (a != b) return false;
        }
        return true;
    }
}

