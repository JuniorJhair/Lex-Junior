using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LexBrowser.Core.Notifications;

public enum SiteNotificationMode
{
    Ask = 0,
    Allow = 1,
    Silent = 2,  // Solo centro de notificaciones, sin toasts emergentes en pantalla
    Digest = 3,  // Resumen periódico de novedades
    Block = 4    // Bloqueado
}

public sealed class SiteNotificationEntry
{
    public string Origin { get; set; } = string.Empty;
    public SiteNotificationMode Mode { get; set; } = SiteNotificationMode.Ask;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastNotifiedAt { get; set; }
    public DateTime? MutedUntil { get; set; }

    [JsonIgnore]
    public bool IsMuted
    {
        get
        {
            if (!MutedUntil.HasValue) return false;
            return DateTime.UtcNow < MutedUntil.Value;
        }
    }
}

public sealed class NotificationMetadataDocument
{
    public int Version { get; set; } = 1;
    public List<SiteNotificationEntry> Sites { get; set; } = new();
}

/// <summary>
/// Gestor centralizado de permisos y preferencias de notificación web por origen.
/// 0.0% CPU en reposo: sin hilos ni temporizadores permanentes.
/// Carga perezosa (lazy), guardado atómico mediante archivo temporal y aislamiento estricto de incógnito.
/// </summary>
public static class NotificationPermissionManager
{
    private static readonly object _syncLock = new();
    private static bool _isInitialized = false;

    private static readonly ConcurrentDictionary<string, SiteNotificationEntry> _persistentEntries =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, SiteNotificationEntry> _incognitoEntries =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly string StoragePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LexBrowser",
        "notifications_metadata.json"
    );

    /// <summary>
    /// Normaliza una URL o encabezado de origen a su forma canónica "scheme://host[:port]".
    /// </summary>
    public static string NormalizeOrigin(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl)) return string.Empty;
        try
        {
            var uri = new Uri(rawUrl.Trim());
            string scheme = uri.Scheme.ToLowerInvariant();
            string host = uri.Host.ToLowerInvariant();
            int port = uri.Port;
            bool isDefaultPort = (scheme == "http" && port == 80) ||
                                 (scheme == "https" && port == 443);

            return isDefaultPort ? $"{scheme}://{host}" : $"{scheme}://{host}:{port}";
        }
        catch
        {
            return rawUrl.Trim().ToLowerInvariant();
        }
    }

    /// <summary>
    /// Obtiene el modo de notificación configurado para un origen.
    /// </summary>
    public static SiteNotificationMode GetSiteMode(string rawOrigin, bool isIncognito)
    {
        string origin = NormalizeOrigin(rawOrigin);
        if (string.IsNullOrEmpty(origin)) return SiteNotificationMode.Ask;

        EnsureLoaded();

        if (isIncognito && _incognitoEntries.TryGetValue(origin, out var incogEntry))
        {
            return incogEntry.Mode;
        }

        if (_persistentEntries.TryGetValue(origin, out var entry))
        {
            return entry.Mode;
        }

        return SiteNotificationMode.Ask;
    }

    /// <summary>
    /// Establece o actualiza el modo de notificación para un origen.
    /// En modo incógnito/invitado jamás escribe en disco.
    /// </summary>
    public static void SetSiteMode(string rawOrigin, SiteNotificationMode mode, bool isIncognito, DateTime? mutedUntil = null)
    {
        string origin = NormalizeOrigin(rawOrigin);
        if (string.IsNullOrEmpty(origin)) return;

        EnsureLoaded();

        if (isIncognito)
        {
            _incognitoEntries.AddOrUpdate(
                origin,
                _ => new SiteNotificationEntry
                {
                    Origin = origin,
                    Mode = mode,
                    CreatedAt = DateTime.UtcNow,
                    MutedUntil = mutedUntil
                },
                (_, existing) =>
                {
                    existing.Mode = mode;
                    if (mutedUntil.HasValue) existing.MutedUntil = mutedUntil;
                    return existing;
                });
            return;
        }

        _persistentEntries.AddOrUpdate(
            origin,
            _ => new SiteNotificationEntry
            {
                Origin = origin,
                Mode = mode,
                CreatedAt = DateTime.UtcNow,
                MutedUntil = mutedUntil
            },
            (_, existing) =>
            {
                existing.Mode = mode;
                if (mutedUntil.HasValue) existing.MutedUntil = mutedUntil;
                return existing;
            });

        SaveToDisk();
    }

    /// <summary>
    /// Registra el momento en que un origen despachó una notificación.
    /// </summary>
    public static void RecordNotificationSent(string rawOrigin, bool isIncognito)
    {
        string origin = NormalizeOrigin(rawOrigin);
        if (string.IsNullOrEmpty(origin)) return;

        EnsureLoaded();

        var dict = isIncognito ? _incognitoEntries : _persistentEntries;
        if (dict.TryGetValue(origin, out var entry))
        {
            entry.LastNotifiedAt = DateTime.UtcNow;
            if (!isIncognito)
            {
                SaveToDisk();
            }
        }
    }

    /// <summary>
    /// Elimina la configuración de un origen, devolviéndolo a estado "Preguntar".
    /// </summary>
    public static bool RemoveSite(string rawOrigin)
    {
        string origin = NormalizeOrigin(rawOrigin);
        if (string.IsNullOrEmpty(origin)) return false;

        EnsureLoaded();

        bool removed = _persistentEntries.TryRemove(origin, out _);
        _incognitoEntries.TryRemove(origin, out _);

        if (removed)
        {
            SaveToDisk();
        }
        return removed;
    }

    /// <summary>
    /// Devuelve una instantánea de todas las configuraciones guardadas por sitio.
    /// </summary>
    public static List<SiteNotificationEntry> GetAllSites()
    {
        EnsureLoaded();
        return _persistentEntries.Values.ToList();
    }

    /// <summary>
    /// Limpia las entradas temporales de pestañas de incógnito cerradas.
    /// </summary>
    public static void ClearIncognitoCache()
    {
        _incognitoEntries.Clear();
    }

    /// <summary>
    /// Comprueba de forma perezosa si los permisos no usados en N días deben caducar a "Preguntar".
    /// </summary>
    public static int ExpireInactivePermissions(int inactiveDays)
    {
        if (inactiveDays <= 0) return 0;
        EnsureLoaded();

        var cutoff = DateTime.UtcNow.AddDays(-inactiveDays);
        var toExpire = new List<string>();

        foreach (var kvp in _persistentEntries)
        {
            if (kvp.Value.Mode == SiteNotificationMode.Allow || kvp.Value.Mode == SiteNotificationMode.Silent)
            {
                DateTime lastActive = kvp.Value.LastNotifiedAt ?? kvp.Value.CreatedAt;
                if (lastActive < cutoff)
                {
                    toExpire.Add(kvp.Key);
                }
            }
        }

        int count = 0;
        foreach (var origin in toExpire)
        {
            if (_persistentEntries.TryGetValue(origin, out var entry))
            {
                entry.Mode = SiteNotificationMode.Ask;
                count++;
            }
        }

        if (count > 0)
        {
            SaveToDisk();
        }

        return count;
    }

    private static void EnsureLoaded()
    {
        if (_isInitialized) return;
        lock (_syncLock)
        {
            if (_isInitialized) return;
            LoadFromDisk();
            _isInitialized = true;
        }
    }

    private static void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(StoragePath)) return;

            string json = File.ReadAllText(StoragePath);
            if (string.IsNullOrWhiteSpace(json)) return;

            var doc = JsonSerializer.Deserialize<NotificationMetadataDocument>(json);
            if (doc?.Sites != null)
            {
                foreach (var site in doc.Sites)
                {
                    if (!string.IsNullOrWhiteSpace(site.Origin))
                    {
                        string norm = NormalizeOrigin(site.Origin);
                        site.Origin = norm;
                        _persistentEntries[norm] = site;
                    }
                }
            }
        }
        catch
        {
            // Si el archivo está corrupto, se aísla de forma segura
            try
            {
                string corruptBackup = StoragePath + ".corrupt";
                if (File.Exists(StoragePath))
                {
                    File.Move(StoragePath, corruptBackup, overwrite: true);
                }
            }
            catch { }
        }
    }

    private static void SaveToDisk()
    {
        lock (_syncLock)
        {
            try
            {
                string dir = Path.GetDirectoryName(StoragePath)!;
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var doc = new NotificationMetadataDocument
                {
                    Version = 1,
                    Sites = _persistentEntries.Values.ToList()
                };

                string tempFile = StoragePath + ".tmp";
                string json = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(tempFile, json);
                File.Move(tempFile, StoragePath, overwrite: true);
            }
            catch { }
        }
    }
}
