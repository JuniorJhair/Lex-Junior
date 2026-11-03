using System;
using System.IO;
using LexBrowser.Core;
using Xunit;

namespace LexBrowser.Tests;

public class PasswordManagerTests : IDisposable
{
    private readonly string _tempVaultPath;

    public PasswordManagerTests()
    {
        _tempVaultPath = Path.Combine(Path.GetTempPath(), $"lex_vault_test_{Guid.NewGuid():N}.dat");
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_tempVaultPath))
                File.Delete(_tempVaultPath);
        }
        catch { }
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
}

