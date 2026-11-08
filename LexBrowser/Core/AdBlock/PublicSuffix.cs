using System;
using System.Collections.Generic;
using System.Net;

namespace LexBrowser.Core.AdBlock;

/// <summary>
/// Utilidad ligera de sufijos públicos (eTLD+1) y coincidencia segura de dominios con frontera de punto.
/// Evita tratar sufijos compuestos hispanoamericanos e internacionales (.com.pe, .gob.pe, .co.uk, .com.br, etc.)
/// como un mismo dominio registrable y previene falsos positivos de sufijo sin punto (p. ej. xyoutube.com).
/// </summary>
public static class PublicSuffix
{
    private static readonly HashSet<string> CompoundTwoLabelSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Perú
        "com.pe", "gob.pe", "edu.pe", "org.pe", "net.pe", "nom.pe", "mil.pe",
        // México
        "com.mx", "gob.mx", "edu.mx", "org.mx", "net.mx",
        // Argentina
        "com.ar", "gob.ar", "gov.ar", "edu.ar", "org.ar", "net.ar", "tur.ar",
        // Colombia
        "com.co", "gov.co", "gob.co", "edu.co", "org.co", "net.co", "mil.co",
        // Chile
        "com.cl", "gob.cl", "gov.cl", "co.cl",
        // Brasil
        "com.br", "gov.br", "edu.br", "org.br", "net.br", "art.br", "ind.br",
        // Ecuador
        "com.ec", "gob.ec", "gov.ec", "edu.ec", "org.ec", "net.ec", "fin.ec", "med.ec",
        // Uruguay
        "com.uy", "gub.uy", "edu.uy", "org.uy", "net.uy",
        // Venezuela
        "com.ve", "gob.ve", "gov.ve", "edu.ve", "org.ve", "net.ve", "co.ve",
        // Bolivia, Paraguay, Centroamérica y Caribe
        "com.bo", "gob.bo", "edu.bo", "org.bo",
        "com.py", "gov.py", "edu.py", "org.py",
        "com.pa", "gob.pa", "edu.pa", "org.pa",
        "co.cr", "fi.cr", "go.cr", "ac.cr", "or.cr",
        "com.gt", "gob.gt", "edu.gt", "org.gt",
        "com.sv", "gob.sv", "edu.sv", "org.sv",
        "com.hn", "gob.hn", "edu.hn", "org.hn",
        "com.ni", "gob.ni", "edu.ni", "org.ni",
        "com.do", "gob.do", "gov.do", "edu.do", "org.do",
        "com.pr", "gov.pr", "edu.pr", "org.pr",
        "com.cu", "gob.cu", "edu.cu", "org.cu",
        // España
        "com.es", "gob.es", "edu.es", "org.es", "nom.es",
        // Reino Unido
        "co.uk", "org.uk", "gov.uk", "ac.uk", "me.uk", "net.uk", "ltd.uk", "plc.uk",
        // Japón, Australia, Nueva Zelanda, India, Corea, etc.
        "co.jp", "ne.jp", "or.jp", "ac.jp", "go.jp", "ed.jp", "gr.jp",
        "com.au", "net.au", "org.au", "edu.au", "gov.au", "asn.au", "id.au",
        "co.nz", "org.nz", "net.nz", "govt.nz", "ac.nz",
        "co.in", "com.in", "net.in", "org.in", "gen.in", "ind.in", "ac.in", "gov.in",
        "co.za", "org.za", "gov.za", "ac.za", "net.za",
        "com.cn", "edu.cn", "gov.cn", "org.cn", "net.cn",
        "com.tw", "org.tw", "gov.tw", "edu.tw",
        "com.hk", "org.hk", "gov.hk", "edu.hk",
        "com.sg", "org.sg", "gov.sg", "edu.sg",
        "com.my", "org.my", "gov.my", "edu.my",
        "com.ph", "org.ph", "gov.ph", "edu.ph",
        "com.tr", "org.tr", "gov.tr", "edu.tr",
        "co.kr", "or.kr", "go.kr", "ac.kr", "ne.kr",
        "co.il", "org.il", "gov.il", "ac.il",
        "com.ua", "org.ua", "gov.ua", "edu.ua",
        "com.pl", "org.pl", "gov.pl", "edu.pl", "net.pl",
        "com.ru", "org.ru", "net.ru"
    };

    /// <summary>
    /// Comprueba si un host es exactamente igual a <paramref name="domain"/> o es un subdominio
    /// válido precedido por un punto (evitando asignaciones de cadena en memoria).
    /// </summary>
    public static bool HostMatchesDomain(string? host, string domain)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(domain))
            return false;

        ReadOnlySpan<char> h = host.AsSpan().Trim().TrimEnd('.');
        ReadOnlySpan<char> d = domain.AsSpan().Trim().TrimStart('.');

        if (h.Length < d.Length)
            return false;

        if (h.Length == d.Length)
            return h.Equals(d, StringComparison.OrdinalIgnoreCase);

        return h[h.Length - d.Length - 1] == '.' &&
               h.Slice(h.Length - d.Length).Equals(d, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extrae el dominio registrable (eTLD+1) considerando sufijos públicos de dos niveles.
    /// Ejemplo: "portal.sunat.gob.pe" -> "sunat.gob.pe", "bcp.com.pe" -> "bcp.com.pe".
    /// </summary>
    public static string GetRegistrableDomain(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return string.Empty;

        string clean = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (clean.Length == 0)
            return string.Empty;

        if (IPAddress.TryParse(clean, out _) || clean.StartsWith('['))
            return clean;

        string[] parts = clean.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return clean;

        string lastTwo = $"{parts[^2]}.{parts[^1]}";
        if (CompoundTwoLabelSuffixes.Contains(lastTwo))
        {
            if (parts.Length >= 3)
                return $"{parts[^3]}.{lastTwo}";

            return clean;
        }

        return lastTwo;
    }

    /// <summary>
    /// Indica si dos hosts comparten el mismo dominio registrable (eTLD+1).
    /// </summary>
    public static bool IsSameRegistrableDomain(string? hostA, string? hostB)
    {
        if (string.IsNullOrWhiteSpace(hostA) || string.IsNullOrWhiteSpace(hostB))
            return false;

        if (string.Equals(hostA.Trim().TrimEnd('.'), hostB.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
            return true;

        string regA = GetRegistrableDomain(hostA);
        string regB = GetRegistrableDomain(hostB);

        return regA.Length > 0 && string.Equals(regA, regB, StringComparison.OrdinalIgnoreCase);
    }
}

