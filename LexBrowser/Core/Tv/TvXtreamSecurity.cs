using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LexBrowser.Core.Tv;

/// <summary>
/// Protección criptográfica local de credenciales mediante Windows DPAPI (CurrentUser)
/// y redacción estricta de credenciales en logs, trazas, URLs y mensajes de excepción.
/// </summary>
public static class TvXtreamSecurity
{
    // Entropía local adicional para el aislamiento de credenciales en el perfil de Lex
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LexTv.Xtream.DPAPI.v1.LocalEntropy");

    /// <summary>
    /// Cifra una cadena de texto plano con Windows DPAPI (DataProtectionScope.CurrentUser).
    /// Solo el usuario de Windows actual en este equipo puede descifrarla.
    /// </summary>
    public static string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;

        try
        {
            byte[] plainBytes = Encoding.UTF8.GetBytes(plaintext);
            byte[] cipherBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(cipherBytes);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("No se pudieron proteger las credenciales con Windows DPAPI.", ex);
        }
    }

    /// <summary>
    /// Descifra una cadena previamente cifrada con Windows DPAPI (CurrentUser).
    /// </summary>
    public static string Unprotect(string? cipherBase64)
    {
        if (string.IsNullOrEmpty(cipherBase64)) return string.Empty;

        try
        {
            byte[] cipherBytes = Convert.FromBase64String(cipherBase64);
            byte[] plainBytes = ProtectedData.Unprotect(cipherBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("No se pudieron descifrar las credenciales con Windows DPAPI.", ex);
        }
    }

    /// <summary>
    /// Redacta de forma exhaustiva credenciales de usuario y contraseña en cualquier texto,
    /// log, mensaje de error o traza de excepción para evitar filtraciones involuntarias.
    /// </summary>
    public static string Redact(string? text, string? username = null, string? password = null)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        string result = text;

        if (!string.IsNullOrEmpty(password) && password.Length >= 2)
            result = result.Replace(password, "[REDACTED]", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(username) && username.Length >= 2)
            result = result.Replace(username, "[REDACTED]", StringComparison.OrdinalIgnoreCase);

        // Limpiar parámetros de query comunes: password=..., pass=..., username=..., user=...
        result = Regex.Replace(result, @"(?i)([\?&](?:password|pass|p)=)([^&""'\s]+)", "$1[REDACTED]");
        result = Regex.Replace(result, @"(?i)([\?&](?:username|user|u)=)([^&""'\s]+)", "$1[REDACTED]");

        // Limpiar rutas típicas de streams Xtream: /live/user/pass/..., /movie/user/pass/..., /series/user/pass/...
        result = Regex.Replace(result, @"(?i)/(live|movie|series)/([^/]+)/([^/]+)/", "/$1/[USER]/[PASS]/");

        // Limpiar credenciales embedded en URI: http://user:pass@host
        result = Regex.Replace(result, @"(?i)://([^:]+):([^@]+)@", "://[USER]:[PASS]@");

        return result;
    }

    /// <summary>
    /// Sobrecarga que rediseña una excepción de modo que ni su mensaje ni su ToString contengan credenciales.
    /// </summary>
    public static Exception RedactException(Exception ex, string? username = null, string? password = null)
    {
        string cleanMsg = Redact(ex.Message, username, password);
        return new InvalidOperationException(cleanMsg);
    }
}
