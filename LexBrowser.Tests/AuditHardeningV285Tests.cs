using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LexBrowser.Core;
using LexBrowser.Core.AdBlock;
using LexBrowser.Core.AdBlock.ShieldCore;
using LexBrowser.Core.Notifications;
using LexBrowser.UI.Models;
using Xunit;

namespace LexBrowser.Tests;

[Collection("SettingsCollection")]
public class AuditHardeningV285Tests : IDisposable
{
    private readonly string _tempDir;

    public AuditHardeningV285Tests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"lex_v285_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        BookmarkManager.CustomStoragePath = null;
        BookmarkManager.Bookmarks.Clear();
        CrashLogger.CustomLogPath = null;
        ShieldLogger.Clear();
        TestSettingsIsolation.ResetToIsolatedDefault();
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    [Fact]
    public async Task PinRecovery_ReturnsNewCode_InvalidatesOld_AndPreservesSecretQuestion()
    {
        string settingsPath = Path.Combine(_tempDir, "settings_recovery.json");
        SettingsManager.CustomSettingsPath = settingsPath;
        SettingsManager.Load();

        string initialCode = PasswordManager.ConfigureMasterPassword(
            "159357",
            "SmartSession",
            "¿Nombre de mi primer colegio?",
            "San Martín");

        var res = await PasswordManager.TryRecoverAndResetPinAsync(initialCode, "246810");

        Assert.True(res.Success);
        Assert.False(res.IsLockedOut);
        Assert.NotNull(res.NewRecoveryCode);
        Assert.StartsWith("LEX-", res.NewRecoveryCode!, StringComparison.Ordinal);
        Assert.NotEqual(initialCode, res.NewRecoveryCode);

        // El nuevo PIN funciona y el anterior deja de funcionar
        Assert.True(PasswordManager.VerifyMasterPassword("246810"));
        Assert.False(PasswordManager.VerifyMasterPassword("159357"));

        // La pregunta y respuesta secreta previas permanecen intactas (no fueron pisadas por el código)
        Assert.Equal("¿Nombre de mi primer colegio?", SettingsManager.Current.SecurityQuestion);
        Assert.True(PasswordManager.VerifyRecoveryAnswerOrCode("san martín"));

        // El código viejo queda invalidado y el código nuevo sí funciona
        Assert.False(PasswordManager.VerifyRecoveryAnswerOrCode(initialCode));
        Assert.True(PasswordManager.VerifyRecoveryAnswerOrCode(res.NewRecoveryCode!));

        // Formato unificado de bloqueo
        Assert.Equal("5 s", PasswordManager.FormatLockoutWait(TimeSpan.FromSeconds(5)));
        Assert.Equal("2 min", PasswordManager.FormatLockoutWait(TimeSpan.FromSeconds(90)));

        PasswordManager.DisableMasterPassword();
    }

    [Fact]
    public void BookmarkManager_EmptyArrayStaysEmpty_AndCorruptFileQuarantinesWithoutOverwrite()
    {
        string bookmarksPath = Path.Combine(_tempDir, "bookmarks.json");
        BookmarkManager.CustomStoragePath = bookmarksPath;

        // 1. Primera ejecución real sin archivo previo: siembra los 3 marcadores por defecto
        BookmarkManager.Load();
        Assert.Equal(3, BookmarkManager.Bookmarks.Count);
        Assert.True(File.Exists(bookmarksPath));

        // 2. El usuario borra todos sus marcadores -> Save guarda [] y Load los respeta vacíos
        BookmarkManager.Bookmarks.Clear();
        BookmarkManager.Save();
        BookmarkManager.Load();
        Assert.Empty(BookmarkManager.Bookmarks);

        // 3. Si bookmarks.json se corrompe, se respalda en cuarentena y NO se sobrescribe con defaults
        const string corruptPayload = "{broken-json:::";
        File.WriteAllText(bookmarksPath, corruptPayload);
        BookmarkManager.Load();

        Assert.Empty(BookmarkManager.Bookmarks);
        Assert.NotNull(BookmarkManager.LastLoadError);
        Assert.NotNull(BookmarkManager.LastQuarantineBackupPath);
        Assert.True(File.Exists(BookmarkManager.LastQuarantineBackupPath!));
        Assert.Equal(corruptPayload, File.ReadAllText(BookmarkManager.LastQuarantineBackupPath!));
        Assert.Equal(corruptPayload, File.ReadAllText(bookmarksPath));
    }

    [Fact]
    public void PasswordVaultStore_IsolatesSameUsernameAcrossSubdomains_AndImportsCleanly()
    {
        string vaultPath = Path.Combine(_tempDir, "vault_subdomains.dat");
        var store = new PasswordVaultStore(vaultPath);

        store.SaveOrUpdateCredential("https://portal.ejemplo.com/login", "ana@ejemplo.com", "ClavePortal-1");
        store.SaveOrUpdateCredential("https://correo.ejemplo.com/auth", "ana@ejemplo.com", "ClaveCorreo-2");

        Assert.Equal(2, store.GetTotalCount());
        Assert.Equal("ClavePortal-1", store.FindExistingCredential("https://portal.ejemplo.com/panel", "ana@ejemplo.com")?.Password);
        Assert.Equal("ClaveCorreo-2", store.FindExistingCredential("https://correo.ejemplo.com/", "ana@ejemplo.com")?.Password);

        var portalAutofill = store.GetAutofillCredentialsForUrl("https://portal.ejemplo.com/oauth");
        var correoAutofill = store.GetAutofillCredentialsForUrl("https://correo.ejemplo.com/inbox");
        Assert.Single(portalAutofill);
        Assert.Single(correoAutofill);
        Assert.Equal("ClavePortal-1", portalAutofill[0].Password);
        Assert.Equal("ClaveCorreo-2", correoAutofill[0].Password);

        // Mismo host normalizado (con o sin www.) sí actualiza in-place
        store.SaveOrUpdateCredential("https://www.portal.ejemplo.com/reset", "ana@ejemplo.com", "ClavePortal-Nueva");
        Assert.Equal(2, store.GetTotalCount());
        Assert.Equal("ClavePortal-Nueva", store.GetAutofillCredentialsForUrl("https://portal.ejemplo.com/login")[0].Password);

        // Importar un tercer subdominio del mismo usuario no debe colisionar
        var (imported, duplicates, skipped, err) = store.ImportCredentials(new[]
        {
            ("https://erp.ejemplo.com/login", "ana@ejemplo.com", "ClaveErp-3"),
            ("https://correo.ejemplo.com/auth", "ana@ejemplo.com", "Duplicada-Ignorar")
        });
        Assert.Null(err);
        Assert.Equal(1, imported);
        Assert.Equal(1, duplicates);
        Assert.Equal(0, skipped);
        Assert.Equal(3, store.GetTotalCount());
    }

    [Fact]
    public void ShieldLogger_NeverLogsIncognitoOrGuestTabEvents()
    {
        ShieldLogger.Clear();

        var incTab = new BrowserTab { IsIncognito = true, Url = "https://incognito-privado.example/secret" };
        var guestTab = new BrowserTab { IsGuest = true, Url = "https://invitado-privado.example/secret" };
        var ownerTab = new BrowserTab { IsIncognito = false, IsGuest = false, Url = "https://publico.example/page" };

        ShieldLogger.LogForTab(incTab, "NOTIFICATION_PERMISSION_REQUESTED", "incognito-privado.example", "https://incognito-privado.example/secret", "MainFrame", "Test", "Tab:inc");
        ShieldLogger.LogForTab(guestTab, "CAPTURE_PERMISSION_REQUESTED", "invitado-privado.example", "https://invitado-privado.example/secret", "MainFrame", "Test", "Tab:guest");
        ShieldLogger.Log("NOTIFICATION_DELIVERED", "incognito-privado.example", "https://incognito-privado.example/", "MainFrame", "Test", isPrivateOrGuest: true);

        Assert.Equal(0, ShieldLogger.Count);

        ShieldLogger.LogForTab(ownerTab, "NOTIFICATION_PERMISSION_REQUESTED", "publico.example", "https://publico.example/page", "MainFrame", "Test", "Tab:owner");
        Assert.Equal(1, ShieldLogger.Count);
        string dump = ShieldLogger.DumpLog();
        Assert.Contains("publico.example", dump, StringComparison.Ordinal);
        Assert.DoesNotContain("incognito-privado.example", dump, StringComparison.Ordinal);
        Assert.DoesNotContain("invitado-privado.example", dump, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingsManager_BackgroundSaveUsesAtomicFile_AndMarkerOnlyWrittenOnce()
    {
        string settingsPath = Path.Combine(_tempDir, "settings_bg.json");
        SettingsManager.CustomSettingsPath = settingsPath;
        SettingsManager.Load();
        SettingsManager.Current.HasCompletedInitialSecuritySetup = true;
        SettingsManager.Save(notify: false);

        string markerPath = SettingsManager.SecuritySetupMarkerPath;
        Assert.True(File.Exists(markerPath));
        DateTime markerStamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(markerPath, markerStamp);

        // Guardado desde hilo de fondo no lanza excepción y no reescribe innecesariamente el archivo marcador
        await Task.Run(() =>
        {
            SettingsManager.Current.CustomSearchUrl = "https://duckduckgo.com/?q={searchTerms}";
            SettingsManager.Save(notify: true);
        });

        Assert.Equal(markerStamp, File.GetLastWriteTimeUtc(markerPath));
        string savedJson = File.ReadAllText(settingsPath);
        Assert.Contains("duckduckgo.com", savedJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ClipboardShield_EncodesWinVExclusionAsFourByteDwordStreams()
    {
        Exception? staError = null;
        var thread = new Thread(() =>
        {
            try
            {
                DataObject obj = MainWindow.BuildProtectedClipboardDataObject("SecretoClipboard99!");
                Assert.Equal("SecretoClipboard99!", obj.GetText());

                var excludeMonitor = obj.GetData("ExcludeClipboardContentFromMonitorProcessing") as MemoryStream;
                var canHistory = obj.GetData("CanIncludeInClipboardHistory") as MemoryStream;
                var canCloud = obj.GetData("CanUploadToCloudClipboard") as MemoryStream;

                Assert.NotNull(excludeMonitor);
                Assert.NotNull(canHistory);
                Assert.NotNull(canCloud);

                Assert.Equal(4, excludeMonitor!.Length);
                Assert.Equal(4, canHistory!.Length);
                Assert.Equal(4, canCloud!.Length);

                Assert.Equal(1, BitConverter.ToInt32(excludeMonitor.ToArray(), 0));
                Assert.Equal(0, BitConverter.ToInt32(canHistory.ToArray(), 0));
                Assert.Equal(0, BitConverter.ToInt32(canCloud.ToArray(), 0));
            }
            catch (Exception ex)
            {
                staError = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (staError != null)
            throw staError;
    }

    [Fact]
    public void WebMessageHardening_ValidatesTokensLiveStreamsAndSsoRedirects()
    {
        // 1. Live stream con duration: null / Infinity no rompe TryParseMediaState y MediaRuntime usa Number.isFinite
        Assert.Contains("Number.isFinite(video.duration)", MediaRuntime.InjectedScript, StringComparison.Ordinal);
        string liveJson = """{"type":"media_state","token":"tok-abc","data":{"isPlaying":true,"title":"En Vivo TV","currentTime":42.5,"duration":null}}""";
        using (var liveDoc = JsonDocument.Parse(liveJson))
        {
            Assert.True(MainWindow.TryParseMediaState(liveDoc.RootElement, out bool isPlaying, out string title, out double cur, out double dur));
            Assert.True(isPlaying);
            Assert.Equal("En Vivo TV", title);
            Assert.Equal(42.5, cur);
            Assert.Equal(0, dur);

            // Token válido + origen HTTP(S) válido -> autorizado
            Assert.True(MainWindow.IsAuthorizedRuntimeWebMessage(liveDoc.RootElement, "tok-abc", messageSource: "https://www.twitch.tv/live"));
            // Token erróneo u origen vacío / about:blank -> rechazado
            Assert.False(MainWindow.IsAuthorizedRuntimeWebMessage(liveDoc.RootElement, "otro-token", messageSource: "https://www.twitch.tv/live"));
            Assert.False(MainWindow.IsAuthorizedRuntimeWebMessage(liveDoc.RootElement, "tok-abc", messageSource: "about:blank"));
        }

        // Mensaje falsificado sin token (p.ej. pip_return_requested o ad_blocked inyectado por página) -> rechazado
        using (var forgedDoc = JsonDocument.Parse("""{"type":"pip_return_requested","isFullScreen":true}"""))
        {
            Assert.False(MainWindow.IsAuthorizedRuntimeWebMessage(forgedDoc.RootElement, "tok-abc", "shield-xyz", "https://evil.example/"));
        }

        // ShieldBridge token autorizado en ad_blocked
        using (var shieldDoc = JsonDocument.Parse("""{"type":"ad_blocked","token":"shield-xyz","category":"popup","reason":"window_open"}"""))
        {
            Assert.True(MainWindow.IsAuthorizedRuntimeWebMessage(shieldDoc.RootElement, "tok-abc", "shield-xyz", "https://news.example/article"));
        }

        // 2. Redirección SSO inmediata en password_submitted
        var tab = new BrowserTab { Url = "https://idp.sso.example/login" };
        tab.Url = "https://app.destino.example/oauth/callback"; // redirigió antes del BeginInvoke
        var callbackUri = new Uri("https://app.destino.example/oauth/callback");

        Assert.True(MainWindow.ShouldAcceptPasswordSubmittedMessage(
            "https://idp.sso.example/login",
            uriAtMessageTime: callbackUri,
            liveTabUri: callbackUri,
            tab: tab));

        // Origen de iframe tercero que jamás fue navegación principal de la pestaña -> rechazado
        Assert.False(MainWindow.ShouldAcceptPasswordSubmittedMessage(
            "https://tercero-malicioso.example/embed",
            uriAtMessageTime: callbackUri,
            liveTabUri: callbackUri,
            tab: tab));
    }

    [Fact]
    public void CrashLogger_And_AtomicFile_PersistCleanlyInIsolatedDirectory()
    {
        string crashPath = Path.Combine(_tempDir, "Logs", "crash.log");
        CrashLogger.CustomLogPath = crashPath;
        CrashLogger.Log("TestContext", new InvalidOperationException("Error controlado"), "Detail");

        Assert.True(File.Exists(crashPath));
        string content = File.ReadAllText(crashPath);
        Assert.Contains("[TestContext]", content, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException: Error controlado", content, StringComparison.Ordinal);

        string atomicPath = Path.Combine(_tempDir, "sample.json");
        Assert.True(AtomicFile.WriteAllTextAtomic(atomicPath, "{\"ok\":true}"));
        Assert.Equal("{\"ok\":true}", File.ReadAllText(atomicPath));
    }
}
