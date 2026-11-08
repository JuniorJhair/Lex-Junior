using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LexBrowser.Core;

public sealed class SavedCredential
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Domain { get; set; } = string.Empty;
    public string OriginHost { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public string DomainInitial => string.IsNullOrWhiteSpace(Domain) ? "🌐" : Domain.Substring(0, 1).ToUpperInvariant();
    public string UpdatedDisplay => $"Guardado el {UpdatedAtUtc.ToLocalTime():dd/MM/yyyy HH:mm}";

    [System.Text.Json.Serialization.JsonIgnore]
    public System.Windows.Media.ImageSource? FaviconImage
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Domain)) return null;
            var cached = Engine.FaviconService.GetCachedFavicon(Domain);
            if (cached != null) return cached;
            _ = Engine.FaviconService.ResolveFaviconAsync("https://" + Domain);
            return null;
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasFaviconImage => FaviconImage != null;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasNoFaviconImage => FaviconImage == null;
}

public sealed class VaultFileModel
{
    public List<SavedCredential> Credentials { get; set; } = new();
    public List<string> ExcludedDomains { get; set; } = new();
}

/// <summary>
/// Motor criptográfico y almacén en memoria O(1) para la Bóveda de Contraseñas de Lex Junior.
/// Cifrado nativo con Windows DPAPI (ProtectedData), 0% consumo de CPU en reposo.
/// Regla de oro: si el cifrado falla, NO se escribe nada en disco y se avisa (nunca se degrada a texto plano).
/// </summary>
public sealed class PasswordVaultStore
{
    private readonly object _lock = new();
    private readonly string _vaultFilePath;
    private readonly List<SavedCredential> _credentials = new();
    private readonly HashSet<string> _excludedDomains = new(StringComparer.OrdinalIgnoreCase);

    private static readonly byte[] VaultEntropy = Encoding.UTF8.GetBytes("LexJunior.Vault.DPAPI.v1");

    // Sufijos compuestos de segundo nivel comunes (.com.pe, .gob.pe, .co.uk, .com.ar, .com.mx, etc.)
    private static readonly HashSet<string> CompoundSecondLevelDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "com", "org", "net", "edu", "gob", "gov", "mil", "co", "ac", "nom"
    };

    public event Action? VaultChanged;

    /// <summary>
    /// Se dispara cuando la bóveda no pudo leerse o guardarse de forma segura (p. ej. DPAPI falló).
    /// La bóveda nunca degrada a texto plano: ante un error, no escribe nada y avisa.
    /// </summary>
    public event Action<string>? VaultError;

    /// <summary>Último error de lectura/escritura de la bóveda (null si todo está bien).</summary>
    public string? LastError { get; private set; }

    public PasswordVaultStore(string vaultFilePath)
    {
        _vaultFilePath = vaultFilePath;
        Load();
    }

    /// <summary>
    /// Extrae el dominio raíz registrable (ej. "https://accounts.google.com/signin?hl=es" -> "google.com",
    /// "https://banca.viabcp.com.pe/login" -> "viabcp.com.pe") ignorando rutas, subdominios de login y parámetros.
    /// </summary>
    public static string ExtractBaseDomain(string? urlOrHost)
    {
        if (string.IsNullOrWhiteSpace(urlOrHost))
            return string.Empty;

        string raw = urlOrHost.Trim();
        string host;

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return string.Empty;
            host = uri.Host;
        }
        else if (Uri.TryCreate("https://" + raw, UriKind.Absolute, out var fallbackUri))
        {
            host = fallbackUri.Host;
        }
        else
        {
            return string.Empty;
        }

        host = host.Trim().Trim('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(host))
            return string.Empty;

        if (host == "localhost" || System.Net.IPAddress.TryParse(host, out _))
            return host;

        string[] parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 2)
            return host;

        // Si termina en un TLD de país de 2 letras (ej. .pe, .ar, .mx, .uk, .br, .es, .co)
        // y la penúltima parte es un dominio compuesto (.com, .gob, .edu, .org, .co), tomar los últimos 3 segmentos.
        string last = parts[^1];
        string secondLast = parts[^2];
        if (last.Length == 2 && CompoundSecondLevelDomains.Contains(secondLast) && parts.Length >= 3)
        {
            return $"{parts[^3]}.{secondLast}.{last}";
        }

        return $"{secondLast}.{last}";
    }

    public static string ExtractHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return uri.Host.ToLowerInvariant();
        }
        return ExtractBaseDomain(url);
    }

    /// <summary>
    /// Verifica si una URL pertenece al mismo sitio web guardado (mismo dominio raíz) y es segura para autocompletar.
    /// </summary>
    public static bool IsDomainMatch(string? pageUrl, string? savedDomain, bool requireHttpsForAutofill = false)
    {
        if (string.IsNullOrWhiteSpace(pageUrl) || string.IsNullOrWhiteSpace(savedDomain))
            return false;

        if (requireHttpsForAutofill && Uri.TryCreate(pageUrl.Trim(), UriKind.Absolute, out var uri))
        {
            bool isLocal = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                           uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase);
            if (uri.Scheme != Uri.UriSchemeHttps && !isLocal)
                return false;
        }

        string pageBase = ExtractBaseDomain(pageUrl);
        string targetBase = ExtractBaseDomain(savedDomain);
        if (string.IsNullOrEmpty(pageBase) || string.IsNullOrEmpty(targetBase))
            return false;

        return string.Equals(pageBase, targetBase, StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<SavedCredential> GetAllCredentials(string? searchFilter = null)
    {
        lock (_lock)
        {
            IEnumerable<SavedCredential> query = _credentials;
            if (!string.IsNullOrWhiteSpace(searchFilter))
            {
                string term = searchFilter.Trim();
                query = query.Where(c =>
                    c.Domain.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    c.OriginHost.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    c.Username.Contains(term, StringComparison.OrdinalIgnoreCase));
            }
            return query
                .OrderBy(c => c.Domain, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Username, StringComparer.OrdinalIgnoreCase)
                .Select(CloneCredential)
                .ToList();
        }
    }

    public IReadOnlyList<SavedCredential> GetCredentialsForUrl(string? pageUrl)
    {
        string baseDomain = ExtractBaseDomain(pageUrl);
        if (string.IsNullOrEmpty(baseDomain))
            return Array.Empty<SavedCredential>();

        string exactHost = ExtractHost(pageUrl);

        lock (_lock)
        {
            // Priorizar coincidencia exacta de host primero, luego mismo dominio base (.com / .com.pe)
            return _credentials
                .Where(c => string.Equals(c.Domain, baseDomain, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => string.Equals(c.OriginHost, exactHost, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(c => c.UpdatedAtUtc)
                .Select(CloneCredential)
                .ToList();
        }
    }

    public SavedCredential? FindExistingCredential(string? urlOrDomain, string? username)
    {
        string baseDomain = ExtractBaseDomain(urlOrDomain);
        if (string.IsNullOrEmpty(baseDomain) || string.IsNullOrWhiteSpace(username))
            return null;

        string cleanUser = username.Trim();
        lock (_lock)
        {
            var match = _credentials.FirstOrDefault(c =>
                string.Equals(c.Domain, baseDomain, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.Username, cleanUser, StringComparison.OrdinalIgnoreCase));
            return match != null ? CloneCredential(match) : null;
        }
    }

    public string? GetPasswordById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (_lock)
        {
            return _credentials.FirstOrDefault(c => c.Id == id)?.Password;
        }
    }

    public SavedCredential SaveOrUpdateCredential(string urlOrDomain, string username, string password)
    {
        string baseDomain = ExtractBaseDomain(urlOrDomain);
        if (string.IsNullOrEmpty(baseDomain))
            throw new ArgumentException("El dominio o URL no es válido.", nameof(urlOrDomain));

        string originHost = ExtractHost(urlOrDomain);
        if (string.IsNullOrEmpty(originHost)) originHost = baseDomain;

        string cleanUser = (username ?? string.Empty).Trim();
        SavedCredential result;

        lock (_lock)
        {
            var existing = _credentials.FirstOrDefault(c =>
                string.Equals(c.Domain, baseDomain, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.Username, cleanUser, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.Password = password;
                existing.OriginHost = originHost;
                existing.UpdatedAtUtc = DateTime.UtcNow;
                result = CloneCredential(existing);
            }
            else
            {
                var created = new SavedCredential
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Domain = baseDomain,
                    OriginHost = originHost,
                    Username = cleanUser,
                    Password = password,
                    UpdatedAtUtc = DateTime.UtcNow
                };
                _credentials.Add(created);
                result = CloneCredential(created);
            }

            _excludedDomains.Remove(baseDomain);
            SaveInternal();
        }

        VaultChanged?.Invoke();
        return result;
    }

    public bool DeleteCredential(string id)
    {
        bool removed;
        lock (_lock)
        {
            removed = _credentials.RemoveAll(c => c.Id == id) > 0;
            if (removed)
            {
                SaveInternal();
            }
        }
        if (removed) VaultChanged?.Invoke();
        return removed;
    }

    public void ClearAllCredentials(bool clearExcludedDomains = false)
    {
        lock (_lock)
        {
            _credentials.Clear();
            if (clearExcludedDomains)
            {
                _excludedDomains.Clear();
            }
            SaveInternal();
        }
        VaultChanged?.Invoke();
    }

    public int GetTotalCount()
    {
        lock (_lock)
        {
            return _credentials.Count;
        }
    }

    public bool IsDomainExcluded(string? urlOrDomain)
    {
        string baseDomain = ExtractBaseDomain(urlOrDomain);
        if (string.IsNullOrEmpty(baseDomain)) return false;
        lock (_lock)
        {
            return _excludedDomains.Contains(baseDomain);
        }
    }

    public void AddExcludedDomain(string? urlOrDomain)
    {
        string baseDomain = ExtractBaseDomain(urlOrDomain);
        if (string.IsNullOrEmpty(baseDomain)) return;
        lock (_lock)
        {
            if (_excludedDomains.Add(baseDomain))
            {
                SaveInternal();
            }
        }
        VaultChanged?.Invoke();
    }

    public void RemoveExcludedDomain(string? urlOrDomain)
    {
        string baseDomain = ExtractBaseDomain(urlOrDomain);
        if (string.IsNullOrEmpty(baseDomain)) return;
        lock (_lock)
        {
            if (_excludedDomains.Remove(baseDomain))
            {
                SaveInternal();
            }
        }
        VaultChanged?.Invoke();
    }

    public IReadOnlyList<string> GetExcludedDomains()
    {
        lock (_lock)
        {
            return _excludedDomains.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    private void Load()
    {
        lock (_lock)
        {
            _credentials.Clear();
            _excludedDomains.Clear();

            try
            {
                if (!File.Exists(_vaultFilePath)) return;
                byte[] encrypted = File.ReadAllBytes(_vaultFilePath);
                if (encrypted.Length == 0) return;

                byte[] plainBytes;
                bool migrateFromPlainText = false;
                try
                {
                    plainBytes = ProtectedData.Unprotect(encrypted, VaultEntropy, DataProtectionScope.CurrentUser);
                }
                catch (Exception ex)
                {
                    // Migración única: versiones anteriores podían dejar vault.dat en texto plano si DPAPI fallaba.
                    // Solo se acepta si es un JSON de bóveda válido, y se re-cifra de inmediato.
                    if (TryReadLegacyPlainTextVault(encrypted))
                    {
                        plainBytes = encrypted;
                        migrateFromPlainText = true;
                    }
                    else
                    {
                        QuarantineVaultFile("No se pudo descifrar vault.dat con Windows (DPAPI): puede pertenecer a otro usuario de Windows o estar dañado.", ex);
                        return;
                    }
                }

                string json = Encoding.UTF8.GetString(plainBytes);
                var model = JsonSerializer.Deserialize<VaultFileModel>(json);
                if (model != null)
                {
                    if (model.Credentials != null)
                    {
                        foreach (var c in model.Credentials)
                        {
                            if (!string.IsNullOrWhiteSpace(c.Domain))
                                _credentials.Add(c);
                        }
                    }
                    if (model.ExcludedDomains != null)
                    {
                        foreach (var d in model.ExcludedDomains)
                        {
                            if (!string.IsNullOrWhiteSpace(d))
                                _excludedDomains.Add(d);
                        }
                    }
                }

                // Si venía en texto plano, re-cifrar ya. Si el cifrado falla, SaveInternal no escribe nada y avisa.
                if (migrateFromPlainText)
                    SaveInternal();
            }
            catch (Exception ex)
            {
                QuarantineVaultFile("vault.dat no se pudo leer.", ex);
            }
        }
    }

    private static bool TryReadLegacyPlainTextVault(byte[] data)
    {
        try
        {
            string text = Encoding.UTF8.GetString(data).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
            if (!text.StartsWith("{", StringComparison.Ordinal)) return false;
            return JsonSerializer.Deserialize<VaultFileModel>(text) != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Conserva una copia del archivo ilegible (para no perder datos si luego se guarda una bóveda nueva) y avisa.
    /// No duplica la copia si ya existe una idéntica (evita acumular respaldos en cada arranque).
    /// </summary>
    private void QuarantineVaultFile(string reason, Exception? ex)
    {
        string message = reason;
        try
        {
            string? dirName = Path.GetDirectoryName(_vaultFilePath);
            string dir = string.IsNullOrEmpty(dirName) ? "." : dirName;
            string name = Path.GetFileName(_vaultFilePath);
            byte[] current = File.ReadAllBytes(_vaultFilePath);

            string? existing = null;
            foreach (string candidate in Directory.GetFiles(dir, name + ".unreadable-*"))
            {
                try
                {
                    if (File.ReadAllBytes(candidate).AsSpan().SequenceEqual(current))
                    {
                        existing = candidate;
                        break;
                    }
                }
                catch { }
            }

            string backup = existing ?? $"{_vaultFilePath}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}";
            if (existing == null)
                File.WriteAllBytes(backup, current);

            message = $"{reason} Se conservó una copia en: {backup}";
        }
        catch { }

        ReportVaultError(message, ex);
    }

    private void ReportVaultError(string message, Exception? ex)
    {
        LastError = message;
        try
        {
            File.AppendAllText(
                _vaultFilePath + ".log",
                $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] {message} ({ex?.GetType().Name}: {ex?.Message}){Environment.NewLine}");
        }
        catch { }
        try { VaultError?.Invoke(message); } catch { }
    }

    private bool SaveInternal()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_vaultFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var model = new VaultFileModel
            {
                Credentials = _credentials,
                ExcludedDomains = _excludedDomains.ToList()
            };

            string json = JsonSerializer.Serialize(model);
            byte[] plainBytes = Encoding.UTF8.GetBytes(json);
            byte[] encryptedBytes;
            try
            {
                encryptedBytes = ProtectedData.Protect(plainBytes, VaultEntropy, DataProtectionScope.CurrentUser);
            }
            catch (Exception ex)
            {
                // Regla de oro: si el cifrado falla, NO se escribe nada en disco y se avisa.
                ReportVaultError("No se pudo cifrar la bóveda con Windows (DPAPI). Los cambios NO se guardaron en disco.", ex);
                return false;
            }

            string tmpPath = _vaultFilePath + ".tmp";
            File.WriteAllBytes(tmpPath, encryptedBytes);
            File.Move(tmpPath, _vaultFilePath, overwrite: true);
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            ReportVaultError("No se pudo guardar la bóveda en disco.", ex);
            return false;
        }
    }

    private static SavedCredential CloneCredential(SavedCredential src) => new()
    {
        Id = src.Id,
        Domain = src.Domain,
        OriginHost = src.OriginHost,
        Username = src.Username,
        Password = src.Password,
        UpdatedAtUtc = src.UpdatedAtUtc
    };
}

public static class PasswordManager
{
    private static readonly string DefaultVaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LexBrowser",
        "vault.dat"
    );

    public static PasswordVaultStore Store { get; } = new(DefaultVaultPath);

    // Indica si el usuario ya desbloqueó la sesión en el proceso actual
    public static bool IsUnlockedInCurrentProcess { get; set; } = false;

    static PasswordManager()
    {
        try
        {
            // Escuchar eventos nativos del sistema operativo (0% consumo de CPU en reposo):
            // Si el usuario bloquea su sesión de Windows (Win+L) o suspende la PC, el Modo Inteligente vuelve a proteger el navegador.
            Microsoft.Win32.SystemEvents.SessionSwitch += (_, e) =>
            {
                if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock ||
                    e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLogoff)
                {
                    InvalidateSmartSessionUnlock();
                }
            };

            Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
            {
                if (e.Mode == Microsoft.Win32.PowerModes.Suspend)
                {
                    InvalidateSmartSessionUnlock();
                }
            };
        }
        catch { }
    }

    /// <summary>
    /// Calcula el identificador del arranque actual de Windows en O(1) (< 0.001 ms) sin leer disco ni abrir procesos.
    /// Si la PC se apaga o reinicia, este identificador cambia automáticamente.
    /// </summary>
    public static long GetCurrentWindowsBootId()
    {
        long bootEpochSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (Environment.TickCount64 / 1000L);
        // Redondear a bloques de 30 segundos para absorber micro-ajustes de reloj NTP
        return bootEpochSeconds / 30L;
    }

    public static bool HasMasterPasswordConfigured()
    {
        var s = SettingsManager.Current;
        return s.BrowserLockMode != "None" &&
               !string.IsNullOrWhiteSpace(s.MasterPasswordHash) &&
               !string.IsNullOrWhiteSpace(s.MasterPasswordSalt);
    }

    /// <summary>
    /// Determina si se debe mostrar la pantalla de bloqueo al abrir el navegador.
    /// </summary>
    public static bool ShouldRequireUnlockOnStartup()
    {
        if (!HasMasterPasswordConfigured())
            return false;

        string mode = SettingsManager.Current.BrowserLockMode;
        if (mode == "AlwaysOnStartup")
            return true;

        if (mode == "SmartSession")
        {
            long currentBootId = GetCurrentWindowsBootId();
            return SettingsManager.Current.LastUnlockedBootId != currentBootId;
        }

        // En "VaultOnly" o "None" el navegador abre directo
        return false;
    }

    /// <summary>
    /// Determina si se debe pedir el PIN antes de abrir la bóveda, ver una contraseña o desactivar el guardador.
    /// </summary>
    public static bool ShouldRequireUnlockForVaultAction()
    {
        if (!HasMasterPasswordConfigured())
            return false;

        if (IsUnlockedInCurrentProcess)
            return false;

        if (SettingsManager.Current.BrowserLockMode == "SmartSession")
        {
            long currentBootId = GetCurrentWindowsBootId();
            if (SettingsManager.Current.LastUnlockedBootId == currentBootId)
            {
                IsUnlockedInCurrentProcess = true;
                return false;
            }
        }

        return true;
    }

    public static void MarkSessionUnlocked()
    {
        IsUnlockedInCurrentProcess = true;
        if (SettingsManager.Current.BrowserLockMode == "SmartSession")
        {
            SettingsManager.Current.LastUnlockedBootId = GetCurrentWindowsBootId();
            SettingsManager.Save(notify: false);
        }
    }

    public static void InvalidateSmartSessionUnlock()
    {
        IsUnlockedInCurrentProcess = false;
        if (SettingsManager.Current.LastUnlockedBootId != 0)
        {
            SettingsManager.Current.LastUnlockedBootId = 0;
            SettingsManager.Save(notify: false);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    //  HASHES DEL PIN Y DE LA RECUPERACIÓN
    //  Formato nuevo (autodescriptivo):  v2$<iteraciones>$<sal base64>$<hash base64>   (PBKDF2-HMAC-SHA256)
    //  Formato antiguo:                  64 caracteres hex (SHA-256 con la sal MasterPasswordSalt)
    //  El formato antiguo se sigue aceptando y se migra al nuevo tras un acierto.
    // ─────────────────────────────────────────────────────────────────
    private const string Pbkdf2Prefix = "v2$";
    private const int Pbkdf2Iterations = 210_000;
    private const int Pbkdf2MaxIterations = 5_000_000; // tope por si settings.json fuera manipulado
    private const int Pbkdf2SaltBytes = 16;
    private const int Pbkdf2HashBytes = 32;

    /// <summary>Hash antiguo (SHA-256 con sal). Solo se conserva para verificar y migrar datos existentes.</summary>
    public static string ComputeSaltedHash(string input, string salt)
    {
        byte[] bytes = Encoding.UTF8.GetBytes($"{salt}::LexJunior::{input}");
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    /// <summary>Hash nuevo: PBKDF2-HMAC-SHA256 con sal aleatoria propia. Devuelve "v2$iter$sal$hash".</summary>
    public static string ComputePbkdf2Hash(string input, int iterations = Pbkdf2Iterations)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(Pbkdf2SaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(input, salt, iterations, HashAlgorithmName.SHA256, Pbkdf2HashBytes);
        return $"{Pbkdf2Prefix}{iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool IsPbkdf2Hash(string? stored)
        => stored != null && stored.StartsWith(Pbkdf2Prefix, StringComparison.Ordinal);

    private static bool VerifyPbkdf2Hash(string input, string stored)
    {
        string[] parts = stored.Split('$');
        if (parts.Length != 4) return false;
        if (!int.TryParse(parts[1], out int iterations) || iterations < 1 || iterations > Pbkdf2MaxIterations) return false;

        try
        {
            byte[] salt = Convert.FromBase64String(parts[2]);
            byte[] expected = Convert.FromBase64String(parts[3]);
            if (expected.Length == 0) return false;
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(input, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Compara un texto contra un hash guardado (nuevo o antiguo). wasLegacy indica que coincidió con el formato antiguo.</summary>
    private static bool MatchesStoredHash(string input, string? stored, string legacySalt, out bool wasLegacy)
    {
        wasLegacy = false;
        if (string.IsNullOrWhiteSpace(stored)) return false;

        if (IsPbkdf2Hash(stored))
            return VerifyPbkdf2Hash(input, stored);

        wasLegacy = true;
        if (string.IsNullOrWhiteSpace(legacySalt)) return false;
        string candidate = ComputeSaltedHash(input, legacySalt);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(candidate),
            Encoding.ASCII.GetBytes(stored));
    }

    // ─────────────────────────────────────────────────────────────────
    //  LÍMITE DE INTENTOS (persistente en settings.json: cerrar y abrir el navegador no lo reinicia)
    //  1–2 fallos: sin espera · 3: 5 s · 4: 15 s · 5: 30 s · 6: 60 s · 7 o más: 5 min
    //  Un acierto reinicia el contador. Aplica al PIN y a la recuperación (comparten contador).
    // ─────────────────────────────────────────────────────────────────
    private static readonly object LockoutSync = new();
    private static readonly TimeSpan MaxLockout = TimeSpan.FromMinutes(5);

    public static TimeSpan GetLockoutDelayForAttempts(int failedAttempts) => failedAttempts switch
    {
        < 3 => TimeSpan.Zero,
        3 => TimeSpan.FromSeconds(5),
        4 => TimeSpan.FromSeconds(15),
        5 => TimeSpan.FromSeconds(30),
        6 => TimeSpan.FromSeconds(60),
        _ => MaxLockout
    };

    /// <summary>Tiempo que falta para poder volver a intentar el PIN (TimeSpan.Zero si no hay bloqueo).</summary>
    public static TimeSpan GetPinLockoutRemaining()
    {
        long ticks = SettingsManager.Current.PinLockoutUntilUtcTicks;
        if (ticks <= DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            return TimeSpan.Zero;

        TimeSpan remaining = new DateTime(ticks, DateTimeKind.Utc) - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero) return TimeSpan.Zero;

        // Tope: si alguien adelanta el reloj o altera el valor, nunca se espera más del máximo.
        return remaining > MaxLockout ? MaxLockout : remaining;
    }

    public static bool IsPinLockedOut => GetPinLockoutRemaining() > TimeSpan.Zero;

    private static void RegisterPinFailure()
    {
        lock (LockoutSync)
        {
            var s = SettingsManager.Current;
            if (s.PinFailedAttempts < int.MaxValue) s.PinFailedAttempts++;

            TimeSpan delay = GetLockoutDelayForAttempts(s.PinFailedAttempts);
            s.PinLockoutUntilUtcTicks = delay > TimeSpan.Zero ? (DateTime.UtcNow + delay).Ticks : 0;
            SettingsManager.Save(notify: false);
        }
    }

    private static void RegisterPinSuccess()
    {
        lock (LockoutSync)
        {
            var s = SettingsManager.Current;
            if (s.PinFailedAttempts == 0 && s.PinLockoutUntilUtcTicks == 0) return;

            s.PinFailedAttempts = 0;
            s.PinLockoutUntilUtcTicks = 0;
            SettingsManager.Save(notify: false);
        }
    }

    public static string NormalizeRecoveryText(string? input)
    {
        return (input ?? string.Empty).Trim().ToLowerInvariant();
    }

    public static string GenerateRecoveryCode()
    {
        int part1 = RandomNumberGenerator.GetInt32(1000, 10000);
        int part2 = RandomNumberGenerator.GetInt32(1000, 10000);
        return $"LEX-{part1}-{part2}";
    }

    /// <summary>
    /// Configura el PIN/contraseña maestra, el modo de bloqueo, la pregunta secreta y genera un código de recuperación.
    /// Sin reglas molestas: acepta PIN de 6 dígitos, números o letras de cualquier longitud.
    /// Todos los hashes se guardan con PBKDF2.
    /// </summary>
    public static string ConfigureMasterPassword(
        string plainPassword,
        string lockMode,
        string? securityQuestion,
        string? securityAnswer)
    {
        if (string.IsNullOrEmpty(plainPassword))
            throw new ArgumentException("La contraseña o PIN no puede estar vacío.");

        // La sal global se conserva (HasMasterPasswordConfigured la exige y los hashes antiguos de
        // recuperación dependen de ella); los hashes nuevos llevan su propia sal dentro del texto.
        string salt = Guid.NewGuid().ToString("N");
        string recoveryCode = GenerateRecoveryCode();

        var s = SettingsManager.Current;
        s.MasterPasswordSalt = salt;
        s.MasterPasswordHash = ComputePbkdf2Hash(plainPassword);
        s.MasterPasswordLength = plainPassword.Length;
        s.BrowserLockMode = string.IsNullOrWhiteSpace(lockMode) || lockMode == "None" ? "SmartSession" : lockMode;

        if (!string.IsNullOrWhiteSpace(securityQuestion) && !string.IsNullOrWhiteSpace(securityAnswer))
        {
            s.SecurityQuestion = securityQuestion.Trim();
            s.SecurityAnswerHash = ComputePbkdf2Hash(NormalizeRecoveryText(securityAnswer));
        }
        else
        {
            s.SecurityQuestion = string.Empty;
            s.SecurityAnswerHash = string.Empty;
        }

        s.RecoveryCodeHash = ComputePbkdf2Hash(NormalizeRecoveryText(recoveryCode));
        s.RecoveryCodeHint = $"LEX-****-{recoveryCode[^4..]}";
        s.LastUnlockedBootId = GetCurrentWindowsBootId();
        s.HasCompletedInitialSecuritySetup = true;
        s.PinFailedAttempts = 0;
        s.PinLockoutUntilUtcTicks = 0;
        IsUnlockedInCurrentProcess = true;

        SettingsManager.Save();
        return recoveryCode;
    }

    public static void DisableMasterPassword()
    {
        var s = SettingsManager.Current;
        s.BrowserLockMode = "None";
        s.MasterPasswordHash = string.Empty;
        s.MasterPasswordSalt = string.Empty;
        s.MasterPasswordLength = 0;
        s.SecurityQuestion = string.Empty;
        s.SecurityAnswerHash = string.Empty;
        s.RecoveryCodeHash = string.Empty;
        s.RecoveryCodeHint = string.Empty;
        s.LastUnlockedBootId = 0;
        s.HasCompletedInitialSecuritySetup = true;
        s.PinFailedAttempts = 0;
        s.PinLockoutUntilUtcTicks = 0;
        IsUnlockedInCurrentProcess = false;
        SettingsManager.Save();
    }

    /// <summary>
    /// Verifica el PIN. Mientras haya un bloqueo por intentos fallidos devuelve false sin comprobar nada
    /// (ni siquiera con el PIN correcto) y sin sumar intentos; la interfaz debe consultar GetPinLockoutRemaining().
    /// </summary>
    public static bool VerifyMasterPassword(string? candidatePassword)
    {
        if (string.IsNullOrEmpty(candidatePassword)) return false;
        var s = SettingsManager.Current;
        if (string.IsNullOrWhiteSpace(s.MasterPasswordHash) || string.IsNullOrWhiteSpace(s.MasterPasswordSalt))
            return false;

        if (IsPinLockedOut) return false;

        if (!MatchesStoredHash(candidatePassword, s.MasterPasswordHash, s.MasterPasswordSalt, out bool wasLegacy))
        {
            RegisterPinFailure();
            return false;
        }

        RegisterPinSuccess();

        if (wasLegacy)
        {
            // Migración transparente: al primer acierto con el SHA-256 antiguo se guarda con PBKDF2.
            // MasterPasswordSalt NO se toca: los hashes antiguos de recuperación todavía dependen de ella.
            s.MasterPasswordHash = ComputePbkdf2Hash(candidatePassword);
            SettingsManager.Save(notify: false);
        }

        return true;
    }

    /// <summary>
    /// Verifica el PIN en un hilo de fondo (Task.Run) para que las 210.000 iteraciones de PBKDF2
    /// y la escritura en disco no congelen el hilo de la interfaz gráfica.
    /// </summary>
    public static System.Threading.Tasks.Task<bool> VerifyMasterPasswordAsync(string? candidatePassword)
        => System.Threading.Tasks.Task.Run(() => VerifyMasterPassword(candidatePassword));

    public static bool VerifyRecoveryAnswerOrCode(string? answerOrCode)
    {
        if (string.IsNullOrWhiteSpace(answerOrCode)) return false;
        var s = SettingsManager.Current;
        if (string.IsNullOrWhiteSpace(s.MasterPasswordSalt)) return false;

        if (IsPinLockedOut) return false;

        string normalized = NormalizeRecoveryText(answerOrCode);

        bool matchedAnswer = MatchesStoredHash(normalized, s.SecurityAnswerHash, s.MasterPasswordSalt, out bool answerWasLegacy);
        bool matchedCode = false;
        bool codeWasLegacy = false;
        if (!matchedAnswer)
            matchedCode = MatchesStoredHash(normalized, s.RecoveryCodeHash, s.MasterPasswordSalt, out codeWasLegacy);

        if (!matchedAnswer && !matchedCode)
        {
            RegisterPinFailure();
            return false;
        }

        RegisterPinSuccess();

        // Migrar a PBKDF2 solo el hash que acaba de coincidir (de los demás no se conoce el texto original).
        if (matchedAnswer && answerWasLegacy)
        {
            s.SecurityAnswerHash = ComputePbkdf2Hash(normalized);
            SettingsManager.Save(notify: false);
        }
        else if (matchedCode && codeWasLegacy)
        {
            s.RecoveryCodeHash = ComputePbkdf2Hash(normalized);
            SettingsManager.Save(notify: false);
        }

        return true;
    }

    public static System.Threading.Tasks.Task<bool> VerifyRecoveryAnswerOrCodeAsync(string? answerOrCode)
        => System.Threading.Tasks.Task.Run(() => VerifyRecoveryAnswerOrCode(answerOrCode));

    /// <summary>
    /// Restablecimiento de emergencia cuando el usuario olvidó su PIN y su respuesta secreta:
    /// Borra únicamente las contraseñas guardadas por seguridad y quita el bloqueo, conservando marcadores, historial y ajustes.
    /// </summary>
    public static void EmergencyResetLockAndClearVault()
    {
        Store.ClearAllCredentials(clearExcludedDomains: false);
        DisableMasterPassword();
    }

    /// <summary>
    /// Genera un script aislado (IIFE) para inyectar credenciales vía ExecuteScriptAsync.
    /// A diferencia de PostWebMessageAsJson, ExecuteScriptAsync no emite ningún evento 'message'
    /// en window.chrome.webview, impidiendo que scripts de terceros en la página intercepten la clave.
    /// </summary>
    public static string BuildSafeAutofillScript(string username, string password)
    {
        string safeUserJson = JsonSerializer.Serialize(username ?? string.Empty);
        string safePassJson = JsonSerializer.Serialize(password ?? string.Empty);

        return $@"
(function() {{
    try {{
        if (window.top !== window.self) return;
        if (location.protocol !== 'https:' && location.hostname !== 'localhost' && location.hostname !== '127.0.0.1') return;
        const u = {safeUserJson};
        const p = {safePassJson};
        if (typeof window.__lexApplyAutofill === 'function') {{
            window.__lexApplyAutofill(u, p);
        }}
    }} catch (_) {{}}
}})();";
    }

    /// <summary>
    /// Script pasivo ultraligero (0% CPU en reposo; sin timers ni MutationObserver).
    /// Solo se activa cuando el usuario enfoca un campo de login o envía un formulario con campo de contraseña.
    /// </summary>
    public const string InjectedPasswordScript = @"
(function() {
    if (window.__lexPwdGuardActive) return;
    if (window.top !== window.self) return;
    window.__lexPwdGuardActive = true;

    let lastSentKey = '';
    let lastSentTime = 0;
    let lastAutofillReqTime = 0;

    function findUsernameField(pwdInput) {
        const container = pwdInput.closest('form') || pwdInput.closest('div[role=""dialog""], main, section, body') || document.body;
        if (!container) return null;

        const inputs = Array.from(container.querySelectorAll('input:not([type=""hidden""]):not([type=""password""]):not([type=""submit""]):not([type=""button""]):not([type=""checkbox""]):not([type=""radio""])'));
        let best = null;
        for (const inp of inputs) {
            if (inp.disabled || inp.readOnly) continue;
            const rect = inp.getBoundingClientRect();
            if (rect.width < 20 || rect.height < 10) continue;

            const type = (inp.type || 'text').toLowerCase();
            const nameId = ((inp.name || '') + ' ' + (inp.id || '') + ' ' + (inp.autocomplete || '') + ' ' + (inp.placeholder || '')).toLowerCase();

            if (type === 'email' || /user|mail|login|account|correo|usuario|ident|phone|dni|doc/.test(nameId)) {
                if (inp.value && inp.value.trim().length > 0) return inp;
                if (!best) best = inp;
            } else if (type === 'text' || type === 'tel') {
                if (inp.value && inp.value.trim().length > 0 && !best) best = inp;
            }
        }
        return best;
    }

    function captureCredentialsFromNode(rootNode) {
        try {
            if (location.protocol !== 'https:' && location.hostname !== 'localhost' && location.hostname !== '127.0.0.1') return;
            const scope = (rootNode && rootNode.querySelectorAll) ? rootNode : document;
            const pwdInputs = Array.from(scope.querySelectorAll('input[type=""password""]'));
            if (pwdInputs.length === 0) return;

            let targetPwd = null;
            for (const p of pwdInputs) {
                if (p.value && p.value.length > 0) {
                    targetPwd = p;
                    break;
                }
            }
            if (!targetPwd) return;

            const userField = findUsernameField(targetPwd);
            let username = userField && userField.value ? userField.value.trim() : '';

            // En logins de 2 pasos (ej. Google/Microsoft donde el correo está en un texto visible)
            if (!username) {
                const emailBadge = document.querySelector('[data-identifier], [id*=""profileIdentifier""], [class*=""user-email""], [class*=""identity""]');
                if (emailBadge) {
                    username = (emailBadge.getAttribute('data-identifier') || emailBadge.textContent || '').trim();
                }
            }

            const password = targetPwd.value;
            if (!password) return;

            const dedupKey = location.hostname + '|' + username + '|' + password.length;
            const now = Date.now();
            if (dedupKey === lastSentKey && (now - lastSentTime) < 2500) return;
            lastSentKey = dedupKey;
            lastSentTime = now;

            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage({
                    type: 'password_submitted',
                    url: location.href,
                    username: username,
                    password: password
                });
            }
        } catch (_) {}
    }

    window.addEventListener('submit', function(e) {
        captureCredentialsFromNode(e.target);
    }, true);

    window.addEventListener('keydown', function(e) {
        if (e.key === 'Enter' && e.target && e.target.tagName === 'INPUT') {
            captureCredentialsFromNode(e.target.closest('form') || document);
        }
    }, true);

    window.addEventListener('click', function(e) {
        if (!e.target || !e.target.closest) return;
        const btn = e.target.closest('button, input[type=""submit""], [role=""button""]');
        if (btn) {
            captureCredentialsFromNode(btn.closest('form') || document);
        }
    }, true);

    // E5: Solicitar autocompletado únicamente cuando el usuario hace foco/clic en un campo de login
    window.addEventListener('focusin', function(e) {
        try {
            const el = e.target;
            if (!el || el.tagName !== 'INPUT' || el.disabled || el.readOnly) return;
            if (location.protocol !== 'https:' && location.hostname !== 'localhost' && location.hostname !== '127.0.0.1') return;

            const type = (el.type || 'text').toLowerCase();
            let isLoginInput = (type === 'password');
            if (!isLoginInput && (type === 'email' || type === 'text' || type === 'tel')) {
                const container = el.closest('form') || document;
                if (container.querySelector('input[type=""password""]')) {
                    isLoginInput = true;
                }
            }
            if (!isLoginInput) return;

            const now = Date.now();
            if (now - lastAutofillReqTime < 1200) return;
            lastAutofillReqTime = now;

            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage({
                    type: 'autofill_requested'
                });
            }
        } catch (_) {}
    }, true);

    function setNativeInputValue(input, value) {
        if (!input || input.value === value) return;
        const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value')?.set;
        if (setter) {
            setter.call(input, value);
        } else {
            input.value = value;
        }
        input.dispatchEvent(new Event('input', { bubbles: true }));
        input.dispatchEvent(new Event('change', { bubbles: true }));
    }

    window.__lexApplyAutofill = function(username, password) {
        try {
            const pwdInputs = Array.from(document.querySelectorAll('input[type=""password""]'));
            for (const pwd of pwdInputs) {
                const rect = pwd.getBoundingClientRect();
                if (rect.width < 20 || rect.height < 10 || pwd.disabled || pwd.readOnly) continue;

                const userInp = findUsernameField(pwd);
                if (userInp && username && !userInp.value) {
                    setNativeInputValue(userInp, username);
                }
                if (password && !pwd.value) {
                    setNativeInputValue(pwd, password);
                }
                break;
            }
        } catch (_) {}
    };
})();
";
}
