using System;
using System.IO;
using System.Text;
using System.Text.Json;
using LexBrowser.Core;
using Xunit;

namespace LexBrowser.Tests;

[Collection("SettingsCollection")]
public class PasswordManagerTests : IDisposable
{
    private readonly string _tempVaultPath;
    private readonly string _tempSettingsPath;

    public PasswordManagerTests()
    {
        _tempVaultPath = Path.Combine(Path.GetTempPath(), $"lex_vault_test_{Guid.NewGuid():N}.dat");
        _tempSettingsPath = Path.Combine(Path.GetTempPath(), $"lex_settings_test_{Guid.NewGuid():N}.json");
        SettingsManager.CustomSettingsPath = _tempSettingsPath;
        SettingsManager.Load();
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_tempVaultPath))
                File.Delete(_tempVaultPath);
            DeleteVaultSidecarFiles();
            if (File.Exists(_tempSettingsPath))
                File.Delete(_tempSettingsPath);
            if (File.Exists(_tempSettingsPath + ".tmp"))
                File.Delete(_tempSettingsPath + ".tmp");
            if (File.Exists(SettingsManager.SecuritySetupMarkerPath))
                File.Delete(SettingsManager.SecuritySetupMarkerPath);
        }
        catch { }
        finally
        {
            SettingsManager.CustomSettingsPath = null;
            SettingsManager.Load();
        }
    }

    /// <summary>Borra .tmp, .log y .unreadable-* que la bóveda de prueba pudo crear junto al archivo.</summary>
    private void DeleteVaultSidecarFiles()
    {
        string? dir = Path.GetDirectoryName(_tempVaultPath);
        if (string.IsNullOrEmpty(dir)) return;
        foreach (string f in Directory.GetFiles(dir, Path.GetFileName(_tempVaultPath) + ".*"))
        {
            try { File.Delete(f); } catch { }
        }
    }

    private string[] GetUnreadableBackups()
    {
        string dir = Path.GetDirectoryName(_tempVaultPath)!;
        return Directory.GetFiles(dir, Path.GetFileName(_tempVaultPath) + ".unreadable-*");
    }

    [Theory]
    [InlineData("https://accounts.google.com/signin/v2/identifier?hl=es", "google.com")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "youtube.com")]
    [InlineData("https://m.facebook.com/login.php?next=https://facebook.com/home", "facebook.com")]
    [InlineData("https://banca.viabcp.com.pe/login/auth", "viabcp.com.pe")]
    [InlineData("https://e-menu.sunat.gob.pe/cl-ti-itmenu/MenuInternet.htm", "sunat.gob.pe")]
    [InlineData("https://www.amazon.co.uk/ap/signin", "amazon.co.uk")]
    [InlineData("netflix.com", "netflix.com")]
    [InlineData("http://localhost:3000/login", "localhost")]
    public void ExtractBaseDomain_NormalizesSubdomainsRoutesAndCompoundTlds(string inputUrl, string expectedDomain)
    {
        string result = PasswordVaultStore.ExtractBaseDomain(inputUrl);
        Assert.Equal(expectedDomain, result);
    }

    [Fact]
    public void IsDomainMatch_DetectsSameSiteDifferentRoutes_AndBlocksPhishingOrHttp()
    {
        Assert.True(PasswordVaultStore.IsDomainMatch(
            "https://www.facebook.com/marketplace/item/123",
            "https://m.facebook.com/login",
            requireHttpsForAutofill: true));

        Assert.True(PasswordVaultStore.IsDomainMatch(
            "https://zonasegura.viabcp.com.pe/dashboard",
            "viabcp.com.pe",
            requireHttpsForAutofill: true));

        // Rechazar sitio phishing con dominio distinto
        Assert.False(PasswordVaultStore.IsDomainMatch(
            "https://facebook-security-login.com/login",
            "facebook.com",
            requireHttpsForAutofill: true));

        // Rechazar autocompletado en HTTP inseguro (excepto localhost)
        Assert.False(PasswordVaultStore.IsDomainMatch(
            "http://facebook.com/login",
            "facebook.com",
            requireHttpsForAutofill: true));
    }

    [Fact]
    public void PasswordVaultStore_EncryptsPersistsUpdatesAndReloadsCredentials()
    {
        var store = new PasswordVaultStore(_tempVaultPath);
        store.SaveOrUpdateCredential("https://accounts.google.com/ServiceLogin", "junior@gmail.com", "123456");

        Assert.Equal(1, store.GetTotalCount());
        var matches = store.GetCredentialsForUrl("https://mail.google.com/mail/u/0/#inbox");
        Assert.Single(matches);
        Assert.Equal("google.com", matches[0].Domain);
        Assert.Equal("junior@gmail.com", matches[0].Username);
        Assert.Equal("123456", matches[0].Password);

        // Actualizar contraseña para el mismo usuario y dominio
        store.SaveOrUpdateCredential("https://www.google.com/account", "junior@gmail.com", "nuevaClave99");
        Assert.Equal(1, store.GetTotalCount());

        // Recargar desde disco cifrado con DPAPI
        var reloaded = new PasswordVaultStore(_tempVaultPath);
        var loadedMatches = reloaded.GetCredentialsForUrl("https://drive.google.com/drive/my-drive");
        Assert.Single(loadedMatches);
        Assert.Equal("nuevaClave99", loadedMatches[0].Password);
    }

    [Fact]
    public void PasswordVaultStore_NeverWritesPlainTextToDisk()
    {
        var store = new PasswordVaultStore(_tempVaultPath);
        store.SaveOrUpdateCredential("https://example.com/login", "ana@example.com", "miClaveSecreta-XYZ");

        Assert.Null(store.LastError);
        string onDisk = Encoding.UTF8.GetString(File.ReadAllBytes(_tempVaultPath));
        Assert.DoesNotContain("miClaveSecreta-XYZ", onDisk);
        Assert.DoesNotContain("ana@example.com", onDisk);
    }

    [Fact]
    public void PasswordVaultStore_MigratesLegacyPlainTextVaultToEncrypted()
    {
        // Simula un vault.dat que una versión anterior dejó en texto plano
        var model = new VaultFileModel();
        model.Credentials.Add(new SavedCredential
        {
            Domain = "example.com",
            OriginHost = "example.com",
            Username = "ana",
            Password = "claveEnClaro123"
        });
        File.WriteAllText(_tempVaultPath, JsonSerializer.Serialize(model));

        var store = new PasswordVaultStore(_tempVaultPath);

        Assert.Equal(1, store.GetTotalCount());
        Assert.Null(store.LastError);

        // Tras cargar, el archivo ya no contiene la clave en claro
        string onDisk = Encoding.UTF8.GetString(File.ReadAllBytes(_tempVaultPath));
        Assert.DoesNotContain("claveEnClaro123", onDisk);

        // Y sigue pudiéndose abrir (ahora cifrado con DPAPI)
        var reloaded = new PasswordVaultStore(_tempVaultPath);
        var matches = reloaded.GetCredentialsForUrl("https://example.com/login");
        Assert.Single(matches);
        Assert.Equal("claveEnClaro123", matches[0].Password);
    }

    [Fact]
    public void PasswordVaultStore_UnreadableVault_IsBackedUpAndReported_NotSilentlyIgnored()
    {
        byte[] garbage = { 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        File.WriteAllBytes(_tempVaultPath, garbage);

        var store = new PasswordVaultStore(_tempVaultPath);

        Assert.Equal(0, store.GetTotalCount());
        Assert.NotNull(store.LastError);

        string[] backups = GetUnreadableBackups();
        Assert.Single(backups);
        Assert.Equal(garbage, File.ReadAllBytes(backups[0]));

        // Reabrir otra vez no acumula copias idénticas
        _ = new PasswordVaultStore(_tempVaultPath);
        Assert.Single(GetUnreadableBackups());
    }

    [Fact]
    public void PasswordVaultStore_ManagesExcludedDomains()
    {
        var store = new PasswordVaultStore(_tempVaultPath);
        store.AddExcludedDomain("https://login.banco.com.pe/auth");

        Assert.True(store.IsDomainExcluded("https://www.banco.com.pe/inicio"));
        store.RemoveExcludedDomain("banco.com.pe");
        Assert.False(store.IsDomainExcluded("https://www.banco.com.pe/inicio"));
    }

    [Fact]
    public void PasswordManager_SupportsSimplePinAndSecretRecoveryWithoutDataLoss()
    {
        string recoveryCode = PasswordManager.ConfigureMasterPassword(
            plainPassword: "159357",
            lockMode: "SmartSession",
            securityQuestion: "¿Nombre de mi mascota?",
            securityAnswer: "Firulais");

        Assert.True(PasswordManager.HasMasterPasswordConfigured());
        Assert.True(PasswordManager.VerifyMasterPassword("159357"));
        Assert.False(PasswordManager.VerifyMasterPassword("000000"));

        // Verificar recuperación por respuesta secreta (insensible a mayúsculas/espacios) y por código LEX-XXXX-XXXX
        Assert.True(PasswordManager.VerifyRecoveryAnswerOrCode("  firulais "));
        Assert.True(PasswordManager.VerifyRecoveryAnswerOrCode(recoveryCode));
        Assert.False(PasswordManager.VerifyRecoveryAnswerOrCode("respuesta_incorrecta"));

        // Limpiar estado de prueba
        PasswordManager.DisableMasterPassword();
        Assert.False(PasswordManager.HasMasterPasswordConfigured());
    }

    [Fact]
    public void ConfigureMasterPassword_StoresPbkdf2Hashes_NotPlainSha256()
    {
        string recoveryCode = PasswordManager.ConfigureMasterPassword("159357", "SmartSession", "¿Mascota?", "Firulais");
        var s = SettingsManager.Current;

        Assert.StartsWith("v2$", s.MasterPasswordHash);
        Assert.StartsWith("v2$", s.SecurityAnswerHash);
        Assert.StartsWith("v2$", s.RecoveryCodeHash);

        Assert.True(PasswordManager.VerifyMasterPassword("159357"));
        Assert.True(PasswordManager.VerifyRecoveryAnswerOrCode("firulais"));
        Assert.True(PasswordManager.VerifyRecoveryAnswerOrCode(recoveryCode));

        PasswordManager.DisableMasterPassword();
    }

    [Fact]
    public void Pbkdf2Hash_UsesRandomSalt_SoSamePinGivesDifferentHashes()
    {
        string a = PasswordManager.ComputePbkdf2Hash("159357");
        string b = PasswordManager.ComputePbkdf2Hash("159357");
        Assert.NotEqual(a, b);
        Assert.True(PasswordManager.IsPbkdf2Hash(a));
    }

    [Fact]
    public void LegacySha256Hashes_StillWork_AndUpgradeToPbkdf2OnSuccess()
    {
        var s = SettingsManager.Current;
        const string salt = "salLegacyDePrueba";
        s.BrowserLockMode = "SmartSession";
        s.MasterPasswordSalt = salt;
        s.MasterPasswordHash = PasswordManager.ComputeSaltedHash("159357", salt);
        s.SecurityAnswerHash = PasswordManager.ComputeSaltedHash("firulais", salt);
        s.RecoveryCodeHash = PasswordManager.ComputeSaltedHash("lex-1234-5678", salt);

        // PIN antiguo: un fallo no rompe nada y el acierto migra a PBKDF2
        Assert.False(PasswordManager.VerifyMasterPassword("000000"));
        Assert.True(PasswordManager.VerifyMasterPassword("159357"));
        Assert.StartsWith("v2$", s.MasterPasswordHash);
        Assert.True(PasswordManager.VerifyMasterPassword("159357")); // ya con el hash nuevo

        // Recuperación antigua: migra solo el hash que coincidió
        Assert.True(PasswordManager.VerifyRecoveryAnswerOrCode("  Firulais "));
        Assert.StartsWith("v2$", s.SecurityAnswerHash);
        Assert.False(PasswordManager.IsPbkdf2Hash(s.RecoveryCodeHash)); // el código en claro aún no se conoce

        Assert.True(PasswordManager.VerifyRecoveryAnswerOrCode("LEX-1234-5678"));
        Assert.StartsWith("v2$", s.RecoveryCodeHash);

        PasswordManager.DisableMasterPassword();
    }

    [Fact]
    public void LockoutDelaySchedule_IsEscalatingAndCapped()
    {
        Assert.Equal(TimeSpan.Zero, PasswordManager.GetLockoutDelayForAttempts(0));
        Assert.Equal(TimeSpan.Zero, PasswordManager.GetLockoutDelayForAttempts(1));
        Assert.Equal(TimeSpan.Zero, PasswordManager.GetLockoutDelayForAttempts(2));
        Assert.Equal(TimeSpan.FromSeconds(5), PasswordManager.GetLockoutDelayForAttempts(3));
        Assert.Equal(TimeSpan.FromSeconds(15), PasswordManager.GetLockoutDelayForAttempts(4));
        Assert.Equal(TimeSpan.FromSeconds(30), PasswordManager.GetLockoutDelayForAttempts(5));
        Assert.Equal(TimeSpan.FromSeconds(60), PasswordManager.GetLockoutDelayForAttempts(6));
        Assert.Equal(TimeSpan.FromMinutes(5), PasswordManager.GetLockoutDelayForAttempts(7));
        Assert.Equal(TimeSpan.FromMinutes(5), PasswordManager.GetLockoutDelayForAttempts(500));
    }

    [Fact]
    public void PinLockout_KicksInAfterThreeFailures_PersistsAcrossRestart_AndResetsOnSuccess()
    {
        PasswordManager.ConfigureMasterPassword("159357", "SmartSession", null, null);

        Assert.False(PasswordManager.VerifyMasterPassword("000001"));
        Assert.False(PasswordManager.VerifyMasterPassword("000002"));
        Assert.Equal(TimeSpan.Zero, PasswordManager.GetPinLockoutRemaining()); // 2 fallos: aún sin espera

        Assert.False(PasswordManager.VerifyMasterPassword("000003"));
        Assert.True(PasswordManager.GetPinLockoutRemaining() > TimeSpan.Zero);
        Assert.True(PasswordManager.IsPinLockedOut);

        // Durante el bloqueo ni el PIN correcto entra, y no suma más intentos
        Assert.False(PasswordManager.VerifyMasterPassword("159357"));
        Assert.Equal(3, SettingsManager.Current.PinFailedAttempts);

        // Simular cerrar y abrir el navegador: el bloqueo vive en disco, no en memoria
        SettingsManager.Load();
        Assert.Equal(3, SettingsManager.Current.PinFailedAttempts);
        Assert.True(PasswordManager.IsPinLockedOut);
        Assert.False(PasswordManager.VerifyMasterPassword("159357"));

        // Pasado el tiempo de espera, el PIN correcto entra y el contador se reinicia
        SettingsManager.Current.PinLockoutUntilUtcTicks = DateTime.UtcNow.AddSeconds(-1).Ticks;
        Assert.True(PasswordManager.VerifyMasterPassword("159357"));
        Assert.Equal(0, SettingsManager.Current.PinFailedAttempts);
        Assert.Equal(TimeSpan.Zero, PasswordManager.GetPinLockoutRemaining());

        PasswordManager.DisableMasterPassword();
    }

    [Fact]
    public void PinLockout_NeverExceedsMaximum_EvenWithTamperedFutureTimestamp()
    {
        PasswordManager.ConfigureMasterPassword("159357", "SmartSession", null, null);
        SettingsManager.Current.PinLockoutUntilUtcTicks = DateTime.UtcNow.AddDays(365).Ticks;

        Assert.True(PasswordManager.GetPinLockoutRemaining() <= TimeSpan.FromMinutes(5));

        PasswordManager.DisableMasterPassword();
    }

    [Fact]
    public void RecoveryAttempts_ShareTheSameLockoutAsThePin()
    {
        string recoveryCode = PasswordManager.ConfigureMasterPassword("159357", "SmartSession", "¿Mascota?", "Firulais");

        Assert.False(PasswordManager.VerifyRecoveryAnswerOrCode("mala1"));
        Assert.False(PasswordManager.VerifyRecoveryAnswerOrCode("mala2"));
        Assert.False(PasswordManager.VerifyRecoveryAnswerOrCode("mala3"));
        Assert.True(PasswordManager.IsPinLockedOut);

        // Con el bloqueo activo, ni el código de recuperación correcto se acepta
        Assert.False(PasswordManager.VerifyRecoveryAnswerOrCode(recoveryCode));

        PasswordManager.DisableMasterPassword();
    }

    [Fact]
    public void InitialSecuritySetup_UserDecisionPersistsWithoutRePrompting()
    {
        // Simular que el usuario eligió "No me gustaría usar contraseña" en primer inicio
        SettingsManager.Current.BrowserLockMode = "None";
        SettingsManager.Current.HasCompletedInitialSecuritySetup = true;
        SettingsManager.Save();

        Assert.True(File.Exists(SettingsManager.SecuritySetupMarkerPath));
        Assert.True(SettingsManager.Current.HasCompletedInitialSecuritySetup);
        Assert.False(PasswordManager.HasMasterPasswordConfigured());
        Assert.False(PasswordManager.ShouldRequireUnlockOnStartup());

        // Simular reinicio completo del navegador (Load desde disco)
        SettingsManager.Load();
        Assert.True(SettingsManager.Current.HasCompletedInitialSecuritySetup);
        Assert.False(PasswordManager.HasMasterPasswordConfigured());
        Assert.False(PasswordManager.ShouldRequireUnlockOnStartup());
    }
}
