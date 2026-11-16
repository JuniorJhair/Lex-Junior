using System;
using System.Text;
using Microsoft.Web.WebView2.Core;

namespace LexBrowser.Core.AdBlock.ShieldCore;

/// <summary>
/// Banderas de red de 32 bits alineadas exactamente con <c>tools/compile_lex_shield.py</c>.
/// </summary>
[Flags]
public enum LexShieldMask : uint
{
    None = 0,
    FromImage = 1u << 0,
    FromMedia = 1u << 1,
    FromObject = 1u << 2,
    FromOther = 1u << 3,
    FromPing = 1u << 4,
    FromScript = 1u << 5,
    FromStylesheet = 1u << 6,
    FromSubdocument = 1u << 7,
    FromWebsocket = 1u << 8,
    FromXmlHttpRequest = 1u << 9,
    FromFont = 1u << 10,
    FromHttp = 1u << 11,
    FromHttps = 1u << 12,
    IsImportant = 1u << 13,
    FromPopup = 1u << 14,
    IsMatchCase = 1u << 15,

    ThirdParty = 1u << 16,
    FirstParty = 1u << 17,
    IsRegex = 1u << 18,
    IsLeftAnchor = 1u << 19,
    IsRightAnchor = 1u << 20,
    IsHostnameAnchor = 1u << 21,
    IsException = 1u << 22,
    IsGenericHide = 1u << 23,
    IsRedirect = 1u << 24,
    IsAlsoBlockRedirect = 1u << 25,
    FromGet = 1u << 26,
    FromHead = 1u << 27,
    IsHostWildcard = 1u << 28,
    FromDocument = 1u << 29,
    FromPost = 1u << 30,
    IsFullRegex = 1u << 31,

    FromAnyMethods = FromGet | FromHead | FromPost,
    FromNetworkTypes = FromImage | FromMedia | FromObject | FromOther | FromPing |
                       FromScript | FromStylesheet | FromSubdocument | FromWebsocket |
                       FromXmlHttpRequest | FromFont,
    FromAllTypes = FromNetworkTypes | FromDocument,
    FromSchemeMask = FromHttp | FromHttps,
    PartyMask = ThirdParty | FirstParty
}

/// <summary>
/// Hash FNV-1a 64-bit determinista idéntico al generador Python offline (<c>fnv1a_64</c>).
/// </summary>
public static class LexShieldHash
{
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    public static ulong Fnv1a64(ReadOnlySpan<byte> utf8)
    {
        ulong h = FnvOffset;
        for (int i = 0; i < utf8.Length; i++)
        {
            h ^= utf8[i];
            h *= FnvPrime;
        }
        return h;
    }

    public static ulong Fnv1a64(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
            return FnvOffset;

        // Ruta directa sin asignaciones para cadenas ASCII (99.9% de hosts y tokens URL)
        ulong h = FnvOffset;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c >= 0x80)
                return Fnv1a64NonAscii(text);
            h ^= (byte)c;
            h *= FnvPrime;
        }
        return h;
    }

    private static ulong Fnv1a64NonAscii(ReadOnlySpan<char> text)
    {
        int maxBytes = Encoding.UTF8.GetMaxByteCount(text.Length);
        if (maxBytes <= 512)
        {
            Span<byte> buf = stackalloc byte[512];
            int written = Encoding.UTF8.GetBytes(text, buf);
            return Fnv1a64(buf.Slice(0, written));
        }

        byte[] heap = Encoding.UTF8.GetBytes(text.ToString());
        return Fnv1a64(heap);
    }
}

/// <summary>
/// Mapea contextos HTTP/WebView2 y encabezados <c>Sec-Fetch-Dest</c> hacia <see cref="LexShieldMask"/>.
/// </summary>
public static class LexShieldRequestContextMapper
{
    public static LexShieldMask ToResourceTypeMask(CoreWebView2WebResourceContext context, string? secFetchDest = null)
    {
        return context switch
        {
            CoreWebView2WebResourceContext.Document => IsIframeDest(secFetchDest)
                ? LexShieldMask.FromSubdocument
                : LexShieldMask.FromDocument,
            CoreWebView2WebResourceContext.Stylesheet => LexShieldMask.FromStylesheet,
            CoreWebView2WebResourceContext.Image => LexShieldMask.FromImage,
            CoreWebView2WebResourceContext.Media => LexShieldMask.FromMedia,
            CoreWebView2WebResourceContext.Font => LexShieldMask.FromFont,
            CoreWebView2WebResourceContext.Script => LexShieldMask.FromScript,
            CoreWebView2WebResourceContext.XmlHttpRequest => LexShieldMask.FromXmlHttpRequest,
            CoreWebView2WebResourceContext.Fetch => LexShieldMask.FromXmlHttpRequest,
            CoreWebView2WebResourceContext.Websocket => LexShieldMask.FromWebsocket,
            CoreWebView2WebResourceContext.Ping => LexShieldMask.FromPing,
            CoreWebView2WebResourceContext.All => LexShieldMask.FromAllTypes,
            _ => LexShieldMask.FromOther
        };
    }

    public static bool IsIframeDest(string? secFetchDest)
        => string.Equals(secFetchDest, "iframe", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(secFetchDest, "frame", StringComparison.OrdinalIgnoreCase);

    public static LexShieldMask ToMethodMask(string? httpMethod)
    {
        if (string.IsNullOrEmpty(httpMethod))
            return LexShieldMask.FromGet;

        if (string.Equals(httpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            return LexShieldMask.FromGet;
        if (string.Equals(httpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            return LexShieldMask.FromPost;
        if (string.Equals(httpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
            return LexShieldMask.FromHead;

        return LexShieldMask.None;
    }
}
