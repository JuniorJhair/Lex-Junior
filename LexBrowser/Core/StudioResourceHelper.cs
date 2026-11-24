using System;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Web.WebView2.Core;

namespace LexBrowser.Core;

/// <summary>
/// Helpers compartidos para lectura segura de recursos estáticos locales y respuestas HTTP de WebView2
/// usados por Lex Media (TvStudioService), Lex PDF Studio y Lex Class Studio.
/// </summary>
internal static class StudioResourceHelper
{
    public static CoreWebView2WebResourceResponse CreateTextResponse(
        CoreWebView2Environment env,
        int code,
        string reason,
        string text,
        string allowedCorsOrigin,
        bool denyFraming = false)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
        string frameHeader = denyFraming ? "\r\nReferrer-Policy: same-origin\r\nX-Frame-Options: DENY" : string.Empty;
        string headers =
            $"Content-Type: text/plain; charset=utf-8\r\n" +
            $"Access-Control-Allow-Origin: {allowedCorsOrigin}\r\n" +
            $"X-Content-Type-Options: nosniff" +
            frameHeader +
            "\r\nCache-Control: no-store";

        return env.CreateWebResourceResponse(new MemoryStream(bytes, writable: false), code, reason, headers);
    }

    public static bool TryReadSafeFile(string baseFolder, string relativePath, out byte[]? bytes)
    {
        bytes = null;
        if (string.IsNullOrWhiteSpace(baseFolder) || string.IsNullOrWhiteSpace(relativePath))
            return false;

        string cleanPath = relativePath.Trim().TrimStart('/', '\\').Replace('\\', '/');
        if (cleanPath.Contains("..", StringComparison.Ordinal) || cleanPath.Contains(':', StringComparison.Ordinal))
            return false;

        try
        {
            if (!Directory.Exists(baseFolder))
                return false;

            string fullFolder = Path.GetFullPath(baseFolder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string diskFile = Path.GetFullPath(Path.Combine(fullFolder, cleanPath.Replace('/', Path.DirectorySeparatorChar)));
            if (diskFile.StartsWith(fullFolder, StringComparison.OrdinalIgnoreCase) && File.Exists(diskFile))
            {
                bytes = File.ReadAllBytes(diskFile);
                return true;
            }
        }
        catch { }

        return false;
    }

    public static bool TryReadEmbeddedResource(Assembly assembly, string resourceName, out byte[]? bytes)
    {
        bytes = null;
        try
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) return false;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            bytes = ms.ToArray();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
