using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LexBrowser.Core.Tv;

public sealed class TvXtreamData
{
    public List<TvXtreamConnection> Connections { get; set; } = new();
}

/// <summary>
/// Persistencia segura de conexiones Xtream Codes.
/// Las credenciales se almacenan protegidas con Windows DPAPI (CurrentUser) solo si el usuario
/// solicitó recordarlas; de lo contrario residen únicamente en memoria volátil de sesión.
/// </summary>
public sealed class TvXtreamStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly object _lock = new();
    private readonly string _storePath;
    private TvXtreamData? _cache;
    private readonly ConcurrentDictionary<string, TvXtreamCredentials> _sessionCreds = new(StringComparer.OrdinalIgnoreCase);

    public static TvXtreamStore Shared { get; } = new(TvUserStateStore.GetDefaultDirectory());

    public TvXtreamStore(string directory)
    {
        _storePath = Path.Combine(directory, "tv_xtream.json");
    }

    public List<TvXtreamConnectionSummary> GetSummaries()
    {
        lock (_lock)
        {
            var data = EnsureLoaded();
            return data.Connections.Select(c => new TvXtreamConnectionSummary
            {
                Id = c.Id,
                Name = c.Name,
                ServerUrl = c.ServerUrl,
                AddedUtc = c.AddedUtc,
                SaveCredentials = c.SaveCredentials,
                LiveCount = c.LiveCount,
                VodCount = c.VodCount,
                SeriesCount = c.SeriesCount,
                ServerStatus = c.ServerStatus,
                ExpirationDate = c.ExpirationDate
            }).ToList();
        }
    }

    public TvXtreamConnection? GetConnection(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (_lock)
        {
            var data = EnsureLoaded();
            return data.Connections.FirstOrDefault(c => string.Equals(c.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
        }
    }

    public TvXtreamConnection AddOrUpdateConnection(TvXtreamConnection conn, string username, string password)
    {
        ArgumentNullException.ThrowIfNull(conn);
        if (string.IsNullOrWhiteSpace(conn.Id))
            conn.Id = "xc_" + Guid.NewGuid().ToString("N")[..10];

        // Manejo de credenciales según la preferencia explícita del usuario
        if (conn.SaveCredentials && !string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
        {
            conn.ProtectedUsername = TvXtreamSecurity.Protect(username);
            conn.ProtectedPassword = TvXtreamSecurity.Protect(password);
        }
        else
        {
            conn.ProtectedUsername = null;
            conn.ProtectedPassword = null;
        }

        // Mantener siempre en memoria durante la sesión activa
        _sessionCreds[conn.Id] = new TvXtreamCredentials
        {
            Username = username ?? "",
            Password = password ?? ""
        };

        lock (_lock)
        {
            var data = EnsureLoaded();
            int idx = data.Connections.FindIndex(c => string.Equals(c.Id, conn.Id, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                data.Connections[idx] = conn;
            else
                data.Connections.Add(conn);

            SaveJsonAtomic(_storePath, data);
            return conn;
        }
    }

    public bool RemoveConnection(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        string cleanId = id.Trim();

        _sessionCreds.TryRemove(cleanId, out _);

        lock (_lock)
        {
            var data = EnsureLoaded();
            int removed = data.Connections.RemoveAll(c => string.Equals(c.Id, cleanId, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                SaveJsonAtomic(_storePath, data);
                return true;
            }
            return false;
        }
    }

    public TvXtreamCredentials? GetCredentials(string connId)
    {
        if (string.IsNullOrWhiteSpace(connId)) return null;
        string cleanId = connId.Trim();

        // 1. Revisar memoria de sesión
        if (_sessionCreds.TryGetValue(cleanId, out var creds) && !string.IsNullOrEmpty(creds.Username))
            return creds;

        // 2. Si no está en memoria, intentar descifrar con DPAPI
        var conn = GetConnection(cleanId);
        if (conn != null && conn.SaveCredentials &&
            !string.IsNullOrEmpty(conn.ProtectedUsername) &&
            !string.IsNullOrEmpty(conn.ProtectedPassword))
        {
            try
            {
                string u = TvXtreamSecurity.Unprotect(conn.ProtectedUsername);
                string p = TvXtreamSecurity.Unprotect(conn.ProtectedPassword);
                var loaded = new TvXtreamCredentials { Username = u, Password = p };
                _sessionCreds[cleanId] = loaded;
                return loaded;
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    public void ReleaseMemoryCache()
    {
        _sessionCreds.Clear();
        lock (_lock)
        {
            _cache = null;
        }
    }

    public void ClearAll()
    {
        _sessionCreds.Clear();
        lock (_lock)
        {
            _cache = new TvXtreamData();
            try
            {
                if (File.Exists(_storePath)) File.Delete(_storePath);
            }
            catch { }
        }
    }

    private TvXtreamData EnsureLoaded()
    {
        if (_cache != null) return _cache;

        try
        {
            if (File.Exists(_storePath))
            {
                using var fs = File.OpenRead(_storePath);
                var parsed = JsonSerializer.Deserialize<TvXtreamData>(fs);
                if (parsed != null)
                {
                    parsed.Connections ??= new();
                    _cache = parsed;
                    return _cache;
                }
            }
        }
        catch { }

        _cache = new TvXtreamData();
        return _cache;
    }

    private static void SaveJsonAtomic(string path, object data)
    {
        string dir = Path.GetDirectoryName(path) ?? "";
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        string tmp = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}
