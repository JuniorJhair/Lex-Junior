using System;
using System.Net;
using System.Text;
using LexBrowser.Core.Pdf;
using LexBrowser.Core.Tv;

namespace LexBrowser.Core;

/// <summary>
/// Resuelve el texto introducido en el Omnibox hacia una URL navegable o consulta de búsqueda.
/// Respeta esquemas explícitos, archivos PDF locales/remotos, IPs privadas/localhost con http://
/// y dominios públicos con https://.
/// </summary>
public static class OmniboxInputResolver
{
    public static string NormalizePastedAddressBarText(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        var sb = new StringBuilder(raw.Length);
        bool prevWasSpace = true; // trims leading whitespace

        foreach (char c in raw)
        {
            // Ignorar caracteres invisibles de formato (zero-width, BOM, soft hyphen)
            if (c is '\u200B' or '\u200C' or '\u200D' or '\uFEFF' or '\u00AD')
                continue;

            if (c is '\r' or '\n' or '\t' or '\u00A0' or '\u2028' or '\u2029' || char.IsWhiteSpace(c))
            {
                if (!prevWasSpace)
                {
                    sb.Append(' ');
                    prevWasSpace = true;
                }
                continue;
            }

            if (char.IsControl(c))
                continue;

            sb.Append(c);
            prevWasSpace = false;
        }

        while (sb.Length > 0 && sb[sb.Length - 1] == ' ')
            sb.Length--;

        return sb.ToString();
    }

    public static string ResolveNavigationTarget(string? rawInput, Func<string, string>? searchUrlBuilder = null)
    {
        string input = NormalizePastedAddressBarText(rawInput);
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        // 1. Acceso directo a Lex TV (IPTV bajo demanda) o Lex Class
        if (TvStudioService.IsTvShortcut(input))
        {
            return TvStudioService.HomeUrl;
        }

        if (UI.Views.LexClassComingSoonDialog.IsLexClassUrl(input))
        {
            return UI.Views.LexClassComingSoonDialog.VirtualUrl;
        }

        // 2. Esquemas explícitos ya presentes
        if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("edge://", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
        {
            return PdfStudioService.IsPdfTarget(input)
                ? PdfStudioService.GetViewerUrl(input)
                : input;
        }

        // 3. Rutas locales de Windows (C:\... o \\servidor\...)
        if ((input.Length >= 2 && char.IsLetter(input[0]) && input[1] == ':' && (input.Length == 2 || input[2] is '\\' or '/')) ||
            input.StartsWith(@"\\", StringComparison.Ordinal))
        {
            if (PdfStudioService.IsPdfTarget(input))
            {
                return PdfStudioService.GetViewerUrl(input);
            }

            try
            {
                return new Uri(input).AbsoluteUri;
            }
            catch
            {
                return input;
            }
        }

        // 4. Direcciones sin espacios: localhost, IPs privadas/locales, host:puerto o dominios públicos
        if (!input.Contains(' '))
        {
            int slashIdx = input.IndexOf('/');
            string authority = slashIdx >= 0 ? input.Substring(0, slashIdx) : input;

            string hostPart = authority;
            int port = -1;
            int colonIdx = authority.LastIndexOf(':');
            if (colonIdx > 0 && colonIdx < authority.Length - 1 && !authority.Contains('['))
            {
                string portCandidate = authority.Substring(colonIdx + 1);
                if (int.TryParse(portCandidate, out int parsedPort) && parsedPort is >= 1 and <= 65535)
                {
                    hostPart = authority.Substring(0, colonIdx);
                    port = parsedPort;
                }
            }

            string? resolved = null;
            if ((hostPart.Contains('.') || hostPart.Equals("localhost", StringComparison.OrdinalIgnoreCase) || hostPart.StartsWith('[')) &&
                IsLocalOrPrivateHost(hostPart))
            {
                string scheme = port == 443 ? "https://" : "http://";
                resolved = scheme + input;
            }
            else if (port > 0 && IsLikelyHostName(hostPart))
            {
                string scheme = port == 443 ? "https://" : "http://";
                resolved = scheme + input;
            }
            else if (hostPart.Contains('.') && IsLikelyHostName(hostPart))
            {
                resolved = "https://" + input;
            }

            if (resolved != null)
            {
                return PdfStudioService.IsPdfTarget(resolved)
                    ? PdfStudioService.GetViewerUrl(resolved)
                    : resolved;
            }
        }

        // 5. Consulta de búsqueda en el motor configurado
        var builder = searchUrlBuilder ?? SettingsManager.GetSearchUrl;
        return builder(input);
    }

    public static bool IsLocalOrPrivateHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        string h = host.Trim().ToLowerInvariant();
        if (h == "localhost" || h.EndsWith(".local", StringComparison.Ordinal) || h.EndsWith(".lan", StringComparison.Ordinal) || !h.Contains('.'))
            return true;

        if (IsStrictIpv4Address(h) || (h.StartsWith('[') && h.EndsWith(']') && IPAddress.TryParse(h, out _)))
        {
            if (IPAddress.TryParse(h, out var ip))
            {
                if (IPAddress.IsLoopback(ip))
                    return true;

                byte[] bytes = ip.GetAddressBytes();
                if (bytes.Length == 4)
                {
                    // 10.0.0.0/8, 127.0.0.0/8, 192.168.0.0/16, 172.16.0.0/12, 169.254.0.0/16
                    if (bytes[0] == 10 || bytes[0] == 127)
                        return true;
                    if (bytes[0] == 192 && bytes[1] == 168)
                        return true;
                    if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                        return true;
                    if (bytes[0] == 169 && bytes[1] == 254)
                        return true;

                    return false;
                }

                return true;
            }
        }

        return false;
    }

    internal static bool IsStrictIpv4Address(string host)
    {
        string[] parts = host.Split('.');
        if (parts.Length != 4) return false;
        foreach (string p in parts)
        {
            if (p.Length == 0 || !byte.TryParse(p, out _))
                return false;
        }
        return IPAddress.TryParse(host, out _);
    }

    internal static bool IsLikelyHostName(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.StartsWith('.') || host.EndsWith('.') || host.Contains("..", StringComparison.Ordinal))
            return false;

        foreach (char c in host)
        {
            if (!(char.IsLetterOrDigit(c) || c == '.' || c == '-'))
                return false;
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IsStrictIpv4Address(host))
            return true;

        int lastDot = host.LastIndexOf('.');
        if (lastDot < 0)
            return true; // Single-label host (solo usado cuando hay puerto explícito, ej. intranet:9000)

        string tld = host.Substring(lastDot + 1);
        if (tld.Length < 2)
            return false;

        // Evitar que números decimales o versiones (ej. 3.14, v2.0) se abran como https://3.14
        bool allAsciiLetters = true;
        foreach (char c in tld)
        {
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')))
            {
                allAsciiLetters = false;
                break;
            }
        }

        return allAsciiLetters || tld.StartsWith("xn--", StringComparison.OrdinalIgnoreCase);
    }
}

