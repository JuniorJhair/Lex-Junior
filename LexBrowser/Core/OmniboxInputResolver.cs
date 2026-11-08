using System;
using System.Net;
using LexBrowser.Core.Pdf;

namespace LexBrowser.Core;

/// <summary>
/// Resuelve el texto introducido en el Omnibox hacia una URL navegable o consulta de búsqueda.
/// Respeta esquemas explícitos, archivos PDF locales/remotos, IPs privadas/localhost con http://
/// y dominios públicos con https://.
/// </summary>
public static class OmniboxInputResolver
{
    public static string ResolveNavigationTarget(string? rawInput, Func<string, string>? searchUrlBuilder = null)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
            return string.Empty;

        string input = rawInput.Trim();

        // 1. Si apunta a un documento PDF local o web, abrirlo en Lex PDF Studio
        if (PdfStudioService.IsPdfTarget(input))
        {
            return PdfStudioService.GetViewerUrl(input);
        }

        // 2. Esquemas explícitos ya presentes
        if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("edge://", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
        {
            return input;
        }

        // 3. Rutas locales de Windows (C:\... o \\servidor\...)
        if ((input.Length >= 2 && char.IsLetter(input[0]) && input[1] == ':' && (input.Length == 2 || input[2] is '\\' or '/')) ||
            input.StartsWith(@"\\", StringComparison.Ordinal))
        {
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

            if ((hostPart.Contains('.') || hostPart.Equals("localhost", StringComparison.OrdinalIgnoreCase) || hostPart.StartsWith('[')) &&
                IsLocalOrPrivateHost(hostPart))
            {
                string scheme = port == 443 ? "https://" : "http://";
                return scheme + input;
            }

            if (port > 0 && IsLikelyHostName(hostPart))
            {
                string scheme = port == 443 ? "https://" : "http://";
                return scheme + input;
            }

            if (hostPart.Contains('.') && IsLikelyHostName(hostPart))
            {
                return "https://" + input;
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

        return false;
    }

    private static bool IsLikelyHostName(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.StartsWith('.') || host.EndsWith('.'))
            return false;

        foreach (char c in host)
        {
            if (!(char.IsLetterOrDigit(c) || c == '.' || c == '-'))
                return false;
        }

        return true;
    }
}

