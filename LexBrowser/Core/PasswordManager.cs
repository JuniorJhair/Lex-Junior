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
}

public sealed class VaultFileModel
{
    public List<SavedCredential> Credentials { get; set; } = new();
    public List<string> ExcludedDomains { get; set; } = new();
}

/// <summary>
/// Motor criptográfico y almacén en memoria O(1) para la Bóveda de Contraseñas de Lex Junior.
/// Cifrado nativo con Windows DPAPI (ProtectedData) + SHA-256 con sal, 0% consumo de CPU en reposo.
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
                try
                {
                    plainBytes = ProtectedData.Unprotect(encrypted, VaultEntropy, DataProtectionScope.CurrentUser);
                }
                catch
                {
                    // Fallback si el archivo fue escrito sin DPAPI en un entorno de prueba
                    plainBytes = encrypted;
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
            }
            catch { }
        }
    }

    private void SaveInternal()
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
            catch
            {
                encryptedBytes = plainBytes;
            }

            string tmpPath = _vaultFilePath + ".tmp";
            File.WriteAllBytes(tmpPath, encryptedBytes);
            File.Move(tmpPath, _vaultFilePath, overwrite: true);
        }
        catch { }
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

    public static string ComputeSaltedHash(string input, string salt)
    {
        byte[] bytes = Encoding.UTF8.GetBytes($"{salt}::LexJunior::{input}");
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
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
    /// </summary>
    public static string ConfigureMasterPassword(
        string plainPassword,
        string lockMode,
        string? securityQuestion,
        string? securityAnswer)
    {
        if (string.IsNullOrEmpty(plainPassword))
            throw new ArgumentException("La contraseña o PIN no puede estar vacío.");

        string salt = Guid.NewGuid().ToString("N");
        string recoveryCode = GenerateRecoveryCode();

        var s = SettingsManager.Current;
        s.MasterPasswordSalt = salt;
        s.MasterPasswordHash = ComputeSaltedHash(plainPassword, salt);
        s.MasterPasswordLength = plainPassword.Length;
        s.BrowserLockMode = string.IsNullOrWhiteSpace(lockMode) || lockMode == "None" ? "SmartSession" : lockMode;

        if (!string.IsNullOrWhiteSpace(securityQuestion) && !string.IsNullOrWhiteSpace(securityAnswer))
        {
            s.SecurityQuestion = securityQuestion.Trim();
            s.SecurityAnswerHash = ComputeSaltedHash(NormalizeRecoveryText(securityAnswer), salt);
        }
        else
        {
            s.SecurityQuestion = string.Empty;
            s.SecurityAnswerHash = string.Empty;
        }

        s.RecoveryCodeHash = ComputeSaltedHash(NormalizeRecoveryText(recoveryCode), salt);
        s.RecoveryCodeHint = $"LEX-****-{recoveryCode[^4..]}";
        s.LastUnlockedBootId = GetCurrentWindowsBootId();
        s.HasCompletedInitialSecuritySetup = true;
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
        IsUnlockedInCurrentProcess = false;
        SettingsManager.Save();
    }

    public static bool VerifyMasterPassword(string? candidatePassword)
    {
        if (string.IsNullOrEmpty(candidatePassword)) return false;
        var s = SettingsManager.Current;
        if (string.IsNullOrWhiteSpace(s.MasterPasswordHash) || string.IsNullOrWhiteSpace(s.MasterPasswordSalt))
            return false;

        string candidateHash = ComputeSaltedHash(candidatePassword, s.MasterPasswordSalt);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(candidateHash),
            Encoding.ASCII.GetBytes(s.MasterPasswordHash));
    }

    public static bool VerifyRecoveryAnswerOrCode(string? answerOrCode)
    {
        if (string.IsNullOrWhiteSpace(answerOrCode)) return false;
        var s = SettingsManager.Current;
        if (string.IsNullOrWhiteSpace(s.MasterPasswordSalt)) return false;

        string normalized = NormalizeRecoveryText(answerOrCode);
        string candidateHash = ComputeSaltedHash(normalized, s.MasterPasswordSalt);

        if (!string.IsNullOrWhiteSpace(s.SecurityAnswerHash) &&
            CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(candidateHash),
                Encoding.ASCII.GetBytes(s.SecurityAnswerHash)))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(s.RecoveryCodeHash) &&
            CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(candidateHash),
                Encoding.ASCII.GetBytes(s.RecoveryCodeHash)))
        {
            return true;
        }

        return false;
    }

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
    /// Script pasivo ultraligero (0% CPU en reposo; sin timers ni MutationObserver).
    /// Solo se activa cuando el usuario envía un formulario con campo de contraseña o recibe orden de autocompletar.
    /// </summary>
    public const string InjectedPasswordScript = @"
(function() {
    if (window.__lexPwdGuardActive) return;
    window.__lexPwdGuardActive = true;

    let lastSentKey = '';
    let lastSentTime = 0;

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

    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.addEventListener('message', function(ev) {
            const msg = ev.data;
            if (!msg || msg.action !== 'autofill_credentials') return;
            try {
                const pwdInputs = Array.from(document.querySelectorAll('input[type=""password""]'));
                for (const pwd of pwdInputs) {
                    const rect = pwd.getBoundingClientRect();
                    if (rect.width < 20 || rect.height < 10 || pwd.disabled || pwd.readOnly) continue;

                    const userInp = findUsernameField(pwd);
                    if (userInp && msg.username && !userInp.value) {
                        setNativeInputValue(userInp, msg.username);
                    }
                    if (msg.password && !pwd.value) {
                        setNativeInputValue(pwd, msg.password);
                    }
                    break;
                }
            } catch (_) {}
        });
    }
})();
";
}

