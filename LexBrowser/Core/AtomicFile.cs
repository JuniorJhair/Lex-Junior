using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace LexBrowser.Core;

/// <summary>
/// Utilidad centralizada de persistencia atómica y cuarentena segura ante corrupción.
/// Escribe primero a un archivo temporal único en el mismo directorio y reemplaza el destino con File.Move(..., overwrite: true).
/// </summary>
public static class AtomicFile
{
    public static bool WriteAllTextAtomic(
        string path,
        string content,
        Encoding? encoding = null,
        int maxAttempts = 5)
    {
        byte[] bytes = (encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)).GetBytes(content ?? string.Empty);
        return WriteAllBytesAtomic(path, bytes, maxAttempts);
    }

    public static bool WriteAllBytesAtomic(string path, byte[] bytes, int maxAttempts = 5)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("La ruta de destino no puede estar vacía.", nameof(path));

        bytes ??= Array.Empty<byte>();
        int attempts = Math.Max(1, maxAttempts);
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(tempPath, bytes);
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                try
                {
                    File.Move(tempPath, path, overwrite: true);
                    return true;
                }
                catch (IOException) when (attempt + 1 < attempts)
                {
                    Thread.Sleep(20 * (attempt + 1));
                }
                catch (UnauthorizedAccessException) when (attempt + 1 < attempts)
                {
                    Thread.Sleep(20 * (attempt + 1));
                }
            }

            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch { }
        }
    }

    public static bool TryWriteJsonAtomic<T>(
        string path,
        T payload,
        JsonSerializerOptions? options = null,
        int maxAttempts = 5)
    {
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, options);
            return WriteAllBytesAtomic(path, bytes, maxAttempts);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Crea una copia de respaldo del archivo dañado antes de cualquier recuperación,
    /// evitando duplicar respaldos si ya existe uno con contenido byte a byte idéntico.
    /// </summary>
    public static string? QuarantineCorruptFile(
        string filePath,
        string suffixPrefix = ".corrupt-",
        bool deduplicateByContent = true)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return null;

            byte[] current = File.ReadAllBytes(filePath);
            string? dirName = Path.GetDirectoryName(filePath);
            string dir = string.IsNullOrEmpty(dirName) ? "." : dirName;
            string name = Path.GetFileName(filePath);

            if (deduplicateByContent)
            {
                foreach (string existing in Directory.GetFiles(dir, $"{name}{suffixPrefix}*"))
                {
                    try
                    {
                        if (File.ReadAllBytes(existing).AsSpan().SequenceEqual(current))
                            return existing;
                    }
                    catch { }
                }
            }

            string backupPath = $"{filePath}{suffixPrefix}{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            int counter = 1;
            while (File.Exists(backupPath))
            {
                backupPath = $"{filePath}{suffixPrefix}{DateTime.UtcNow:yyyyMMdd-HHmmss}-{counter++}";
            }

            File.WriteAllBytes(backupPath, current);
            return backupPath;
        }
        catch
        {
            return null;
        }
    }
}
