using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LexBrowser.Core;
using LexBrowser.Core.Engine;
using LexBrowser.UI.Views;
using Xunit;

namespace LexBrowser.Tests;

[Collection("SettingsCollection")]
public sealed class PrivacyProfilesTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _tempPath;

    public PrivacyProfilesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"lex_privacy_profiles_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _tempPath = Path.Combine(_tempDir, "settings.json");
        SettingsManager.CustomSettingsPath = _tempPath;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
        finally
        {
            TestSettingsIsolation.ResetToIsolatedDefault();
        }
    }

    [Fact]
    public void FreshInstall_WithoutSettingsJson_DetectsFreshInstallAndPreservesV26ClassDefaults()
    {
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
        if (File.Exists(_tempPath + ".tmp")) File.Delete(_tempPath + ".tmp");

        SettingsManager.Load();
        var s = SettingsManager.Current;

        Assert.True(SettingsManager.IsFreshInstall);
        Assert.Equal(0, s.PrivacyProfileOnboardingVersion);
        // Defaults de la clase deben ser v2.6.0, NO los del Perfil B
        Assert.False(s.EnableSmartScreen);
        Assert.False(s.EnableClientSidePhishingDetection);
        Assert.True(s.EnableLinkFaviconDownload);
        Assert.True(s.EnableDirectFaviconIco);
        Assert.True(s.EnableExternalFaviconService);
        Assert.True(s.EnableSearchSuggestions);
        Assert.Equal(PrivacyProfiles.AutoUpdateOnStartup, s.AutoUpdateCheckMode);
    }

    [Fact]
    public void UpgradeFromLegacyJson_DoesNotClobberExistingPrefs_AndAssignsLegacyDefaults()
    {
        File.WriteAllText(_tempPath, """
        {
          "SettingsSchemaVersion": 4,
          "Theme": "Dark",
          "DefaultSearchEngine": "DuckDuckGo",
          "EnableSearchSuggestions": false,
          "EnableExternalFaviconService": false,
          "EnableAdBlocker": true,
          "RestorePreviousSession": false
        }
        """);

        SettingsManager.Load();
        var s = SettingsManager.Current;

        Assert.False(SettingsManager.IsFreshInstall);
        Assert.Equal(0, s.PrivacyProfileOnboardingVersion);
        // Preferencias existentes intactas
        Assert.Equal("Dark", s.Theme);
        Assert.Equal("DuckDuckGo", s.DefaultSearchEngine);
        Assert.False(s.EnableSearchSuggestions);
        Assert.False(s.EnableExternalFaviconService);
        Assert.True(s.EnableAdBlocker);
        Assert.False(s.RestorePreviousSession);
        // Propiedades nuevas faltantes toman defaults v2.6.0
        Assert.False(s.EnableSmartScreen);
        Assert.False(s.EnableClientSidePhishingDetection);
        Assert.True(s.EnableLinkFaviconDownload);
        Assert.True(s.EnableDirectFaviconIco);
        Assert.Equal(PrivacyProfiles.AutoUpdateOnStartup, s.AutoUpdateCheckMode);

        // Combinación antigua (SmartScreen=false, link/ico=true, google/suggest=false, OnStartup) no coincide con A, B ni C -> Personalizado
        Assert.Equal(PrivacyProfiles.ProfileKeyCustom, PrivacyProfiles.ResolveActiveProfile(s));
    }

    [Fact]
    public void ResolveActiveProfile_MatchesABC_AndCustomOnSingleChange_WithoutMutatingOtherOptions()
    {
        var s = new BrowserSettings();

        PrivacyProfiles.ApplyPredefinedProfile(s, PrivacyProfiles.ProfileKeyA);
        Assert.Equal(PrivacyProfiles.ProfileKeyA, PrivacyProfiles.ResolveActiveProfile(s));
        Assert.False(s.EnableSmartScreen);
        Assert.False(s.EnableClientSidePhishingDetection);
        Assert.False(s.EnableLinkFaviconDownload);
        Assert.False(s.EnableDirectFaviconIco);
        Assert.False(s.EnableExternalFaviconService);
        Assert.False(s.EnableSearchSuggestions);
        Assert.Equal(PrivacyProfiles.AutoUpdateManual, s.AutoUpdateCheckMode);

        PrivacyProfiles.ApplyPredefinedProfile(s, PrivacyProfiles.ProfileKeyB);
        Assert.Equal(PrivacyProfiles.ProfileKeyB, PrivacyProfiles.ResolveActiveProfile(s));
        Assert.True(s.EnableSmartScreen);
        Assert.False(s.EnableClientSidePhishingDetection);
        Assert.True(s.EnableLinkFaviconDownload);
        Assert.True(s.EnableDirectFaviconIco);
        Assert.False(s.EnableExternalFaviconService);
        Assert.False(s.EnableSearchSuggestions);
        Assert.Equal(PrivacyProfiles.AutoUpdateOnStartup, s.AutoUpdateCheckMode);

        PrivacyProfiles.ApplyPredefinedProfile(s, PrivacyProfiles.ProfileKeyC);
        Assert.Equal(PrivacyProfiles.ProfileKeyC, PrivacyProfiles.ResolveActiveProfile(s));
        Assert.True(s.EnableSmartScreen);
        Assert.True(s.EnableClientSidePhishingDetection);
        Assert.True(s.EnableLinkFaviconDownload);
        Assert.True(s.EnableDirectFaviconIco);
        Assert.True(s.EnableExternalFaviconService);
        Assert.True(s.EnableSearchSuggestions);
        Assert.Equal(PrivacyProfiles.AutoUpdateOnStartup, s.AutoUpdateCheckMode);

        // Cambiar una sola opción pasa a Personalizado sin alterar las demás ni cambiar tema/buscador/escudo
        s.Theme = "Light";
        s.DefaultSearchEngine = "Brave";
        s.EnableSearchSuggestions = false;
        Assert.Equal(PrivacyProfiles.ProfileKeyCustom, PrivacyProfiles.ResolveActiveProfile(s));
        Assert.True(s.EnableSmartScreen);
        Assert.True(s.EnableClientSidePhishingDetection);
        Assert.True(s.EnableExternalFaviconService);
        Assert.Equal("Light", s.Theme);
        Assert.Equal("Brave", s.DefaultSearchEngine);

        // Replicar manualmente la combinación del Perfil B desde cero debe deducirse como "B"
        var manualB = PrivacyProfiles.FromSettings(s);
        manualB = manualB with
        {
            EnableSmartScreen = true,
            EnableClientSidePhishingDetection = false,
            EnableLinkFaviconDownload = true,
            EnableDirectFaviconIco = true,
            EnableExternalFaviconService = false,
            EnableSearchSuggestions = false,
            AutoUpdateCheckMode = PrivacyProfiles.AutoUpdateOnStartup
        };
        Assert.Equal(PrivacyProfiles.ProfileKeyB, PrivacyProfiles.ResolveActiveProfile(manualB));
    }

    [Fact]
    public void CustomCombinationSavedToDisk_ReloadsAsPersonalizado_AndIdenticalToB_ReloadsAsB()
    {
        File.WriteAllText(_tempPath, "{}");
        SettingsManager.Load();

        // Combinación arbitraria elegida en tarjeta Personalizado
        var customValues = new PrivacyProfileValues(
            EnableSmartScreen: false,
            EnableClientSidePhishingDetection: true,
            EnableLinkFaviconDownload: true,
            EnableDirectFaviconIco: false,
            EnableExternalFaviconService: false,
            EnableSearchSuggestions: true,
            AutoUpdateCheckMode: PrivacyProfiles.AutoUpdateManual);

        PrivacyProfiles.ConfirmOnboardingSelection(SettingsManager.Current, customValues);
        SettingsManager.Save();

        SettingsManager.Load();
        Assert.Equal(1, SettingsManager.Current.PrivacyProfileOnboardingVersion);
        Assert.Equal(PrivacyProfiles.ProfileKeyCustom, PrivacyProfiles.ResolveActiveProfile(SettingsManager.Current));

        // Combinación construida en Personalizado pero idéntica a B -> deduce "B"
        PrivacyProfiles.ConfirmOnboardingSelection(SettingsManager.Current, PrivacyProfiles.ProfileB);
        SettingsManager.Save();

        SettingsManager.Load();
        Assert.Equal(PrivacyProfiles.ProfileKeyB, PrivacyProfiles.ResolveActiveProfile(SettingsManager.Current));
    }

    [Fact]
    public void CloseWithoutChoosing_FreshAppliesB_UpgradePreservesExistingWithoutOverwrite()
    {
        // 1. Instalación nueva cerrada sin elegir -> aplica B y marca onboarding = 1
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
        SettingsManager.Load();
        Assert.True(SettingsManager.IsFreshInstall);

        bool applied = PrivacyProfiles.ResolveClosedWithoutChoice(SettingsManager.Current, SettingsManager.IsFreshInstall);
        Assert.True(applied);
        Assert.Equal(1, SettingsManager.Current.PrivacyProfileOnboardingVersion);
        Assert.Equal(PrivacyProfiles.ProfileKeyB, PrivacyProfiles.ResolveActiveProfile(SettingsManager.Current));

        // 2. Usuario que actualiza y cierra sin elegir (con preferencias suyas previas) -> conserva todo igual
        File.WriteAllText(_tempPath, """
        {
          "Theme": "Light",
          "DefaultSearchEngine": "Brave",
          "EnableSearchSuggestions": true,
          "EnableExternalFaviconService": true,
          "EnableSmartScreen": false,
          "EnableClientSidePhishingDetection": false,
          "EnableLinkFaviconDownload": false,
          "EnableDirectFaviconIco": true,
          "AutoUpdateCheckMode": "Manual",
          "PrivacyProfileOnboardingVersion": 0
        }
        """);

        SettingsManager.Load();
        Assert.False(SettingsManager.IsFreshInstall);
        var before = PrivacyProfiles.FromSettings(SettingsManager.Current);

        bool modified = PrivacyProfiles.ResolveClosedWithoutChoice(SettingsManager.Current, SettingsManager.IsFreshInstall);
        Assert.False(modified);
        Assert.Equal(1, SettingsManager.Current.PrivacyProfileOnboardingVersion);
        Assert.Equal(before, PrivacyProfiles.FromSettings(SettingsManager.Current));
        Assert.Equal("Light", SettingsManager.Current.Theme);
        Assert.Equal("Brave", SettingsManager.Current.DefaultSearchEngine);
    }

    [Fact]
    public void OptionDescriptors_CoverAll8MatrixRows_AndCoherenceNoticesWork()
    {
        Assert.Equal(8, PrivacyProfiles.OptionDescriptors.Count);

        var onlyNativeAndManual = PrivacyProfiles.ProfileA;
        var notices = PrivacyProfiles.GetCoherenceNotices(onlyNativeAndManual);
        Assert.Equal(2, notices.Count);

        var balancedNotices = PrivacyProfiles.GetCoherenceNotices(PrivacyProfiles.ProfileB);
        Assert.Empty(balancedNotices);
    }

    [Theory]
    [InlineData("settings.json.tmp")]
    [InlineData("bookmarks.json")]
    [InlineData("vault.dat")]
    [InlineData("history.json")]
    [InlineData(".security_setup_done")]
    public void PriorUsageSignals_SinglePreExistingFile_PreventsFreshInstallDetection(string existingFileName)
    {
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
        if (File.Exists(_tempPath + ".tmp")) File.Delete(_tempPath + ".tmp");

        File.WriteAllText(Path.Combine(_tempDir, existingFileName), "marker");

        SettingsManager.Load();
        Assert.False(SettingsManager.IsFreshInstall);
        Assert.Equal(0, SettingsManager.Current.PrivacyProfileOnboardingVersion);
    }

    [Fact]
    public void PriorUsageSignals_NonEmptyUserDataDirectory_PreventsFreshInstallDetection()
    {
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
        string udDir = Path.Combine(_tempDir, "UserData");
        Directory.CreateDirectory(udDir);
        File.WriteAllText(Path.Combine(udDir, "placeholder.dat"), "1");

        SettingsManager.Load();
        Assert.False(SettingsManager.IsFreshInstall);
    }

    [Fact]
    public void UnreadableSettings_TruncatedJson_CreatesCorruptBackup_RescuesPinAndTheme_NeverOverwrites()
    {
        string truncated = """
        {
          "Theme": "Light",
          "BrowserLockMode": "AlwaysOnStartup",
          "MasterPasswordHash": "PBKDF2$100000$salt$hashvalue",
          "MasterPasswordSalt": "salt123",
          "MasterPasswordLength": 6,
          "HasCompletedInitialSecuritySetup": true,
          "DefaultSearchEngine": "Brave",
          "TabSuspensionMi
        """;
        File.WriteAllText(_tempPath, truncated);

        SettingsManager.Load();

        Assert.False(SettingsManager.IsFreshInstall);
        Assert.True(SettingsManager.HadUnreadableSettingsFile);
        Assert.True(SettingsManager.AutoSavesSuspended);
        Assert.NotNull(SettingsManager.CorruptSettingsBackupPath);
        Assert.True(File.Exists(SettingsManager.CorruptSettingsBackupPath));
        Assert.Equal(truncated, File.ReadAllText(SettingsManager.CorruptSettingsBackupPath!));
        // Archivo original NO sobrescrito
        Assert.Equal(truncated, File.ReadAllText(_tempPath));

        // Propiedades recuperadas
        Assert.Equal("Light", SettingsManager.Current.Theme);
        Assert.Equal("AlwaysOnStartup", SettingsManager.Current.BrowserLockMode);
        Assert.Equal("PBKDF2$100000$salt$hashvalue", SettingsManager.Current.MasterPasswordHash);
        Assert.Equal("salt123", SettingsManager.Current.MasterPasswordSalt);
        Assert.Equal(6, SettingsManager.Current.MasterPasswordLength);
        Assert.Equal("Brave", SettingsManager.Current.DefaultSearchEngine);
        Assert.True(PasswordManager.HasMasterPasswordConfigured());
        Assert.True(PasswordManager.ShouldRequireUnlockOnStartup());

        // Save bloqueado mientras no se resuelva el estado
        SettingsManager.Current.Theme = "Dark";
        SettingsManager.Save();
        SettingsManager.SaveDeferred(delayMs: 50);
        Assert.Equal(truncated, File.ReadAllText(_tempPath));
    }

    [Fact]
    public void UnreadableSettings_NullBytesAndWrongTypeProperty_CreatesBackupAndRecoversValidFields()
    {
        string malformed = "{\0\n" +
            "  \"Theme\": \"Dark\",\0\n" +
            "  \"TabSuspensionMinutes\": \"no-es-numero\",\n" +
            "  \"EnableSearchSuggestions\": false,\n" +
            "  \"BrowserLockMode\": \"SmartSession\",\n" +
            "  \"MasterPasswordHash\": \"PBKDF2$test\",\n" +
            "  \"MasterPasswordSalt\": \"mysalt\"\n" +
            "}";
        File.WriteAllBytes(_tempPath, Encoding.UTF8.GetBytes(malformed));

        SettingsManager.Load();

        Assert.False(SettingsManager.IsFreshInstall);
        Assert.True(SettingsManager.HadUnreadableSettingsFile);
        Assert.True(SettingsManager.AutoSavesSuspended);
        Assert.True(File.Exists(SettingsManager.CorruptSettingsBackupPath));
        Assert.Equal("Dark", SettingsManager.Current.Theme);
        Assert.False(SettingsManager.Current.EnableSearchSuggestions);
        Assert.Equal("SmartSession", SettingsManager.Current.BrowserLockMode);
        Assert.Equal("PBKDF2$test", SettingsManager.Current.MasterPasswordHash);
        Assert.Equal(15, SettingsManager.Current.TabSuspensionMinutes); // default porque la propiedad tenía tipo incorrecto
        Assert.Equal(malformed, File.ReadAllText(_tempPath));
    }

    [Fact]
    public void UnreadableSettings_EmptyFileWithVaultDat_ActivatesConservativeVaultLockAndNeverOverwrites()
    {
        File.WriteAllText(_tempPath, string.Empty);
        File.WriteAllBytes(Path.Combine(_tempDir, "vault.dat"), new byte[] { 1, 2, 3, 4 });

        SettingsManager.Load();

        Assert.False(SettingsManager.IsFreshInstall);
        Assert.True(SettingsManager.HadUnreadableSettingsFile);
        Assert.True(SettingsManager.AutoSavesSuspended);
        Assert.True(SettingsManager.LockSettingsPotentiallyLost);
        Assert.True(PasswordManager.IsVaultLockedByRecovery);
        Assert.True(PasswordManager.ShouldRequireUnlockForVaultAction());
        Assert.NotNull(SettingsManager.SettingsRecoveryWarningMessage);
        Assert.True(File.Exists(SettingsManager.CorruptSettingsBackupPath));
        Assert.Equal(string.Empty, File.ReadAllText(_tempPath));
    }

    [Fact]
    public void UnreadableSettings_ExclusivelyLockedFile_DoesNotCrashOrOverwriteOrMarkFreshInstall()
    {
        File.WriteAllText(_tempPath, """{"Theme":"Dark","EnableSearchSuggestions":false}""");
        using var exclusiveLock = new FileStream(_tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        SettingsManager.Load();

        Assert.False(SettingsManager.IsFreshInstall);
        Assert.True(SettingsManager.HadUnreadableSettingsFile);
        Assert.True(SettingsManager.AutoSavesSuspended);
        Assert.False(SettingsManager.ResolveRecoveryAndAllowSaves()); // Sin backup válido si sigue bloqueado/sin copia
    }

    [Fact]
    public void BrowserEngine_BuildEngineFlags_TogglesSmartScreenAndPhishingOnly_PreservesMediaAndAutoplay()
    {
        var a = new BrowserSettings();
        PrivacyProfiles.ApplyPredefinedProfile(a, PrivacyProfiles.ProfileKeyA);
        string flagsA = BrowserEngine.BuildEngineFlags(a);
        Assert.Contains("--disable-client-side-phishing-detection", flagsA);
        Assert.Contains("msSmartScreenProtection", flagsA);
        Assert.Contains("HardwareMediaKeyHandling", flagsA);
        Assert.Contains("--autoplay-policy=no-user-gesture-required", flagsA);

        var b = new BrowserSettings();
        PrivacyProfiles.ApplyPredefinedProfile(b, PrivacyProfiles.ProfileKeyB);
        string flagsB = BrowserEngine.BuildEngineFlags(b);
        Assert.Contains("--disable-client-side-phishing-detection", flagsB);
        Assert.DoesNotContain("msSmartScreenProtection", flagsB);
        Assert.Contains("HardwareMediaKeyHandling", flagsB);
        Assert.Contains("--autoplay-policy=no-user-gesture-required", flagsB);

        var c = new BrowserSettings();
        PrivacyProfiles.ApplyPredefinedProfile(c, PrivacyProfiles.ProfileKeyC);
        string flagsC = BrowserEngine.BuildEngineFlags(c);
        Assert.DoesNotContain("--disable-client-side-phishing-detection", flagsC);
        Assert.DoesNotContain("msSmartScreenProtection", flagsC);
        Assert.Contains("HardwareMediaKeyHandling", flagsC);
        Assert.Contains("--autoplay-policy=no-user-gesture-required", flagsC);
    }

    [Theory]
    [InlineData("http://127.0.0.1/favicon.ico")]
    [InlineData("http://[::1]/favicon.ico")]
    [InlineData("http://[::ffff:127.0.0.1]/favicon.ico")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://0.0.0.0/favicon.ico")]
    [InlineData("http://[::]/favicon.ico")]
    [InlineData("http://[fe80::1]/favicon.ico")]
    [InlineData("http://[fc00::1]/favicon.ico")]
    [InlineData("http://100.64.0.1/favicon.ico")]
    [InlineData("http://2130706433/favicon.ico")]
    [InlineData("http://0x7f000001/favicon.ico")]
    [InlineData("http://0177.0.0.1/favicon.ico")]
    [InlineData("http://127.1/favicon.ico")]
    [InlineData("https://example.com:8080/favicon.ico")]
    [InlineData("http://example.com:22/favicon.ico")]
    [InlineData("file:///C:/Windows/win.ini")]
    public void FaviconService_IsAllowedFaviconUrl_BlocksAllSsrfNumericAndUnusualPortTargets(string blockedUrl)
    {
        Assert.False(FaviconService.IsAllowedFaviconUrl(blockedUrl, out _));
    }

    [Fact]
    public async Task FaviconService_DnsRebindingAndPinnedSocketAndRedirects_EnforcedStrictly()
    {
        // 1. Dominio aparentemente público cuya resolución DNS devuelve 127.0.0.1 o lista mixta [pública, privada] -> bloqueado ANTES de abrir socket
        int tcpAttempts = 0;
        using var strictBlockedClient = FaviconService.CreatePinnedFaviconHttpClient(
            dnsResolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34"), IPAddress.Loopback }),
            onConnectedEndpoint: _ => tcpAttempts++);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await FaviconService.DownloadRemoteFaviconBytesAsync("https://rebind-mixed.example.com/favicon.ico", strictBlockedClient));
        Assert.Equal(0, tcpAttempts);

        // 2. Servidor HTTP real en loopback con puerto efímero usando AcceptTcpClientAsync(ct)
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var serverCts = new CancellationTokenSource();

        byte[] tinyPng = CreateSolidPngBytes(16, 16);
        string? lastSeenHostHeader = null;
        var serverTask = Task.Run(async () =>
        {
            while (!serverCts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(serverCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    break;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        using (client)
                        await using (var stream = client.GetStream())
                        using (var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true))
                        {
                            string? reqLine = await reader.ReadLineAsync();
                            string? line;
                            string hostHeader = "";
                            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                            {
                                if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                                    hostHeader = line;
                            }
                            lastSeenHostHeader = hostHeader;

                            byte[] payload;
                            string headers;
                            if (reqLine != null && reqLine.Contains("/ok", StringComparison.Ordinal))
                            {
                                payload = tinyPng;
                                headers = $"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
                            }
                            else if (reqLine != null && reqLine.Contains("/redir-private", StringComparison.Ordinal))
                            {
                                payload = Array.Empty<byte>();
                                headers = $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{port}/ok\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                            }
                            else if (reqLine != null && reqLine.Contains("/redir-loop", StringComparison.Ordinal))
                            {
                                payload = Array.Empty<byte>();
                                headers = $"HTTP/1.1 302 Found\r\nLocation: http://pinned-test.example.invalid:{port}/redir-loop\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                            }
                            else if (reqLine != null && reqLine.Contains("/huge", StringComparison.Ordinal))
                            {
                                payload = new byte[FaviconService.MaxFaviconBytes + 4096];
                                headers = $"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
                            }
                            else
                            {
                                payload = Array.Empty<byte>();
                                headers = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                            }

                            byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
                            await stream.WriteAsync(headerBytes);
                            if (payload.Length > 0) await stream.WriteAsync(payload);
                            await stream.FlushAsync();
                        }
                    }
                    catch { }
                });
            }
        });

        try
        {
            int phase = 0;
            IPEndPoint? effectiveEndpoint = null;
            using var harnessClient = FaviconService.CreatePinnedFaviconHttpClient(
                dnsResolver: (host, _) =>
                {
                    Assert.Equal("pinned-test.example.invalid", host);
                    return Task.FromResult(new[] { IPAddress.Loopback });
                },
                allowedIpPredicate: _ => phase == 0,
                onConnectedEndpoint: ep => effectiveEndpoint = ep,
                timeout: TimeSpan.FromSeconds(5));

            // 2a. Conexión real al hostname sintético no resoluble por OS -> demuestra conexión directa vía IP validada en ConnectCallback + Host original
            byte[]? firstBytes = await FaviconService.DownloadRemoteFaviconBytesAsync(
                $"http://pinned-test.example.invalid:{port}/ok",
                customHttpClient: harnessClient,
                allowLoopbackHostForLocalTestHarness: true,
                allowCustomPortForLocalTestHarness: true);

            Assert.NotNull(firstBytes);
            Assert.Equal(tinyPng, firstBytes);
            Assert.NotNull(effectiveEndpoint);
            Assert.Equal(IPAddress.Loopback, effectiveEndpoint!.Address);
            Assert.Equal(port, effectiveEndpoint.Port);
            Assert.Contains("pinned-test.example.invalid", lastSeenHostHeader ?? "", StringComparison.OrdinalIgnoreCase);

            // 2b. Simula DNS rebinding (en la siguiente conexión el DNS resuelve a IP privada) -> falla antes de conectar
            phase = 1;
            effectiveEndpoint = null;
            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await FaviconService.DownloadRemoteFaviconBytesAsync(
                    $"http://pinned-test.example.invalid:{port}/ok",
                    customHttpClient: harnessClient,
                    allowLoopbackHostForLocalTestHarness: true,
                    allowCustomPortForLocalTestHarness: true));
            Assert.Null(effectiveEndpoint);

            // 2c. Redirección hacia IP privada, cadena larga (>5 saltos) y payload >512KB -> rechazados
            phase = 0;
            Assert.Null(await FaviconService.DownloadRemoteFaviconBytesAsync(
                $"http://pinned-test.example.invalid:{port}/redir-private",
                customHttpClient: harnessClient,
                allowLoopbackHostForLocalTestHarness: true,
                allowCustomPortForLocalTestHarness: true));

            Assert.Null(await FaviconService.DownloadRemoteFaviconBytesAsync(
                $"http://pinned-test.example.invalid:{port}/redir-loop",
                customHttpClient: harnessClient,
                allowLoopbackHostForLocalTestHarness: true,
                allowCustomPortForLocalTestHarness: true));

            Assert.Null(await FaviconService.DownloadRemoteFaviconBytesAsync(
                $"http://pinned-test.example.invalid:{port}/huge",
                customHttpClient: harnessClient,
                allowLoopbackHostForLocalTestHarness: true,
                allowCustomPortForLocalTestHarness: true));
        }
        finally
        {
            serverCts.Cancel();
            listener.Stop();
            await Task.WhenAny(serverTask, Task.Delay(500));
        }
    }

    [Fact]
    public async Task FaviconService_BoundsDecodePixelsTo64_AndStreamMaxBytes_AcrossAllPaths()
    {
        byte[] largePng = CreateSolidPngBytes(2048, 2048);
        Assert.True(largePng.Length <= FaviconService.MaxFaviconBytes);

        // 1. CreateFrozenBitmapFromMemory
        using (var ms = new MemoryStream(largePng))
        {
            BitmapImage? bmp = FaviconService.CreateFrozenBitmapFromMemory(ms);
            Assert.NotNull(bmp);
            Assert.True(bmp!.PixelWidth <= FaviconService.MaxFaviconDecodePixels);
            Assert.True(bmp.PixelHeight <= FaviconService.MaxFaviconDecodePixels);
        }

        // 2. data: URI
        string dataUri = "data:image/png;base64," + Convert.ToBase64String(largePng);
        BitmapImage? dataBmp = FaviconService.CreateBitmapFromDataUri(dataUri);
        Assert.NotNull(dataBmp);
        Assert.True(dataBmp!.PixelWidth <= FaviconService.MaxFaviconDecodePixels);
        Assert.True(dataBmp.PixelHeight <= FaviconService.MaxFaviconDecodePixels);

        // 3. CreateFrozenBitmapFromStreamAsync (normal & oversized webViewStream)
        using (var okStream = new MemoryStream(largePng))
        {
            BitmapImage? streamBmp = await FaviconService.CreateFrozenBitmapFromStreamAsync(okStream);
            Assert.NotNull(streamBmp);
            Assert.True(streamBmp!.PixelWidth <= FaviconService.MaxFaviconDecodePixels);
            Assert.True(streamBmp.PixelHeight <= FaviconService.MaxFaviconDecodePixels);
        }

        using var oversizedStream = new MemoryStream(new byte[FaviconService.MaxFaviconBytes + 1024]);
        Assert.Null(await FaviconService.CreateFrozenBitmapFromStreamAsync(oversizedStream));
    }

    [Fact]
    public async Task StartupGate_VaultNoNetwork_AndManualUpdateCheck_Enforced()
    {
        // 1. Compuerta cerrada impide resolución sin caché y permite data: URI local
        FaviconService.ClearCache();
        FaviconService.SetStartupNetworkGate(false);
        try
        {
            Assert.Null(await FaviconService.ResolveFaviconAsync("https://example.org/page"));

            byte[] tinyPng = CreateSolidPngBytes(16, 16);
            string tinyData = "data:image/png;base64," + Convert.ToBase64String(tinyPng);
            var localFromData = await FaviconService.ResolveFaviconAsync("https://local-data-test.example/page", null, tinyData);
            Assert.NotNull(localFromData);
        }
        finally
        {
            FaviconService.SetStartupNetworkGate(true);
            FaviconService.ClearCache();
        }

        // 2. Bóveda nunca dispara red desde SavedCredential.FaviconImage
        var cred = new SavedCredential { Domain = "noncached-vault-domain.example", Username = "u", Password = "p" };
        Assert.Null(cred.FaviconImage);
        Assert.Null(FaviconService.GetCachedFavicon("noncached-vault-domain.example"));
        string? repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string vaultWindowCs = File.ReadAllText(Path.Combine(repoRoot, "LexBrowser", "UI", "Views", "PasswordVaultWindow.xaml.cs"));
        Assert.DoesNotContain("LoadFaviconForCredentialAsync", vaultWindowCs, StringComparison.Ordinal);

        // 3. AutoUpdateCheckMode Manual devuelve null al instante en chequeo no manual
        File.WriteAllText(_tempPath, """{"AutoUpdateCheckMode":"Manual"}""");
        SettingsManager.Load();
        Assert.Null(await UpdateManager.CheckForUpdatesAsync(isUserInitiated: false));
    }

    [Fact]
    public void StartupStage_AndOnboardingDecision_CoversAllFlowsAndGuardrails()
    {
        // Modo Invitado jamás muestra onboarding ni entra en PIN de propietario
        Assert.Equal(
            MainWindow.StartupExecutionStage.GuestSession,
            MainWindow.DetermineStartupStage(
                startInGuest: true,
                hasInitialAttachedTab: false,
                hasCompletedSecuritySetup: false,
                hasMasterPasswordConfigured: false,
                lockSettingsPotentiallyLost: false,
                shouldRequireUnlockOnStartup: true,
                tabsCount: 0,
                privacyOnboardingVersion: 0));
        Assert.False(MainWindow.ShouldShowPrivacyOnboardingWindow(
            new BrowserSettings { PrivacyProfileOnboardingVersion = 0 },
            isGuestSession: true,
            hasInitialAttachedTab: false));

        // Pestaña arrastrada a nueva ventana salta onboarding/PIN y se adjunta directo
        Assert.Equal(
            MainWindow.StartupExecutionStage.AttachExistingTab,
            MainWindow.DetermineStartupStage(
                startInGuest: false,
                hasInitialAttachedTab: true,
                hasCompletedSecuritySetup: true,
                hasMasterPasswordConfigured: true,
                lockSettingsPotentiallyLost: false,
                shouldRequireUnlockOnStartup: true,
                tabsCount: 0,
                privacyOnboardingVersion: 0));
        Assert.False(MainWindow.ShouldShowPrivacyOnboardingWindow(
            new BrowserSettings { PrivacyProfileOnboardingVersion = 0 },
            isGuestSession: false,
            hasInitialAttachedTab: true));

        // Primer arranque absoluto sin setup de PIN: primero overlay de creación de PIN
        Assert.Equal(
            MainWindow.StartupExecutionStage.FirstTimePinSetupOverlay,
            MainWindow.DetermineStartupStage(
                startInGuest: false,
                hasInitialAttachedTab: false,
                hasCompletedSecuritySetup: false,
                hasMasterPasswordConfigured: false,
                lockSettingsPotentiallyLost: false,
                shouldRequireUnlockOnStartup: false,
                tabsCount: 0,
                privacyOnboardingVersion: 0));

        // LockSettingsPotentiallyLost (settings corrupto + vault/.security_setup_done): NO muestra creación de PIN desde cero
        Assert.Equal(
            MainWindow.StartupExecutionStage.DirectTabs,
            MainWindow.DetermineStartupStage(
                startInGuest: false,
                hasInitialAttachedTab: false,
                hasCompletedSecuritySetup: false,
                hasMasterPasswordConfigured: false,
                lockSettingsPotentiallyLost: true,
                shouldRequireUnlockOnStartup: false,
                tabsCount: 0,
                privacyOnboardingVersion: 1));

        // AlwaysOnStartup / SmartSession bloqueado: overlay de desbloqueo va ANTES de onboarding/pestañas
        Assert.Equal(
            MainWindow.StartupExecutionStage.PinUnlockOverlay,
            MainWindow.DetermineStartupStage(
                startInGuest: false,
                hasInitialAttachedTab: false,
                hasCompletedSecuritySetup: true,
                hasMasterPasswordConfigured: true,
                lockSettingsPotentiallyLost: false,
                shouldRequireUnlockOnStartup: true,
                tabsCount: 0,
                privacyOnboardingVersion: 0));

        // Propietario ya desbloqueado (o VaultOnly/sin bloqueo) con onboarding pendiente: abre OnboardingAndTabs
        Assert.Equal(
            MainWindow.StartupExecutionStage.OnboardingAndTabs,
            MainWindow.DetermineStartupStage(
                startInGuest: false,
                hasInitialAttachedTab: false,
                hasCompletedSecuritySetup: true,
                hasMasterPasswordConfigured: true,
                lockSettingsPotentiallyLost: false,
                shouldRequireUnlockOnStartup: false,
                tabsCount: 0,
                privacyOnboardingVersion: 0));
        Assert.True(MainWindow.ShouldShowPrivacyOnboardingWindow(
            new BrowserSettings { PrivacyProfileOnboardingVersion = 0 },
            isGuestSession: false,
            hasInitialAttachedTab: false));

        // Propietario con onboarding completado: abre pestañas directo sin mostrar modal
        Assert.Equal(
            MainWindow.StartupExecutionStage.DirectTabs,
            MainWindow.DetermineStartupStage(
                startInGuest: false,
                hasInitialAttachedTab: false,
                hasCompletedSecuritySetup: true,
                hasMasterPasswordConfigured: true,
                lockSettingsPotentiallyLost: false,
                shouldRequireUnlockOnStartup: false,
                tabsCount: 0,
                privacyOnboardingVersion: PrivacyProfiles.CurrentOnboardingVersion));
        Assert.False(MainWindow.ShouldShowPrivacyOnboardingWindow(
            new BrowserSettings { PrivacyProfileOnboardingVersion = PrivacyProfiles.CurrentOnboardingVersion },
            isGuestSession: false,
            hasInitialAttachedTab: false));

        // En proceso limpio sin WebView2 inicializado todavía no exige reinicio pendiente
        Assert.False(BrowserEngine.IsEnvironmentCreated);
        Assert.False(BrowserEngine.RequiresRestartForCurrentSettings());
    }

    [Fact]
    public void NewPrivacyViews_UseOnlyThemeDynamicResources_NoHardcodedColors()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string[] xamlFiles =
        {
            Path.Combine(repoRoot, "LexBrowser", "UI", "Views", "PrivacyProfileSetupWindow.xaml"),
            Path.Combine(repoRoot, "LexBrowser", "UI", "Controls", "PrivacyOptionsPanelControl.xaml"),
            Path.Combine(repoRoot, "LexBrowser", "UI", "Views", "SettingsRecoveryDialog.xaml"),
            Path.Combine(repoRoot, "LexBrowser", "UI", "Views", "VersionDowngradeDialog.xaml")
        };

        string combinedXaml = string.Join(Environment.NewLine, xamlFiles.Select(File.ReadAllText));

        // Prohibido color hexadecimal fijo (#RGB / #RRGGBB / #AARRGGBB) o StaticResource de color en vistas nuevas
        string withoutComments = Regex.Replace(combinedXaml, @"<!--.*?-->", string.Empty, RegexOptions.Singleline);
        Assert.DoesNotMatch(@"#[0-9A-Fa-f]{3,8}\b", withoutComments);

        var referencedKeys = Regex.Matches(combinedXaml, @"\{DynamicResource\s+([A-Za-z0-9_]+)\}")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(referencedKeys);

        foreach (string theme in new[] { BrowserThemeManager.Light, BrowserThemeManager.Dark, BrowserThemeManager.Classic })
        {
            var dict = new ResourceDictionary();
            BrowserThemeManager.ApplyThemePalette(dict, theme);
            foreach (string key in referencedKeys)
            {
                Assert.True(dict.Contains(key), $"Falta clave DynamicResource '{key}' para tema '{theme}' en las vistas nuevas de privacidad.");
            }
        }
    }

    [Fact]
    public void HonestCopy_AuditAcrossAppFiles_NoLegacyZeroTelemetryOrDiskCacheClaims()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string[] targetFiles =
        {
            Path.Combine(repoRoot, "LexBrowser", "MainWindow.xaml"),
            Path.Combine(repoRoot, "LexBrowser", "UI", "Views", "SettingsWindow.xaml"),
            Path.Combine(repoRoot, "LexBrowser", "UI", "Views", "PrivacyProfileSetupWindow.xaml"),
            Path.Combine(repoRoot, "LexBrowser", "UI", "Views", "PrivacyProfileSetupWindow.xaml.cs"),
            Path.Combine(repoRoot, "LexBrowser", "UI", "Controls", "PrivacyOptionsPanelControl.xaml"),
            Path.Combine(repoRoot, "LexBrowser", "UI", "Controls", "PrivacyOptionsPanelControl.xaml.cs"),
            Path.Combine(repoRoot, "LexBrowser", "UI", "Views", "SettingsRecoveryDialog.xaml"),
            Path.Combine(repoRoot, "LexBrowser", "UI", "Views", "SettingsRecoveryDialog.xaml.cs"),
            Path.Combine(repoRoot, "LexBrowser", "UI", "Views", "VersionDowngradeDialog.xaml"),
            Path.Combine(repoRoot, "LexBrowser", "UI", "Views", "VersionDowngradeDialog.xaml.cs"),
            Path.Combine(repoRoot, "LexBrowser", "UI", "Views", "InstallerWindow.xaml"),
            Path.Combine(repoRoot, "LexBrowser", "Core", "InstallerService.cs"),
            Path.Combine(repoRoot, "LexBrowser", "Core", "NewTabPage.cs"),
            Path.Combine(repoRoot, "LexBrowser", "Assets", "tv", "index.html"),
            Path.Combine(repoRoot, "LexBrowser", "Assets", "tv", "como-funciona.html"),
            Path.Combine(repoRoot, "LexBrowser", "Assets", "tv", "legal.html"),
            Path.Combine(repoRoot, "README.md")
        };

        foreach (string file in targetFiles)
        {
            string raw = File.ReadAllText(file);
            string nonComment = Regex.Replace(raw, @"<!--.*?-->", string.Empty, RegexOptions.Singleline);
            Assert.DoesNotContain("caché local en disco", nonComment, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("0 telemetría", nonComment, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cero telemetría", nonComment, StringComparison.OrdinalIgnoreCase);
        }

        string mainXaml = File.ReadAllText(Path.Combine(repoRoot, "LexBrowser", "MainWindow.xaml"));
        Assert.Contains("Text=\"Sin telemetría propia\"", mainXaml, StringComparison.Ordinal);

        string settingsXaml = File.ReadAllText(Path.Combine(repoRoot, "LexBrowser", "UI", "Views", "SettingsWindow.xaml"));
        Assert.Contains("Sin telemetría propia · Menos anuncios · Privado", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("🛡️ Privacidad y seguridad", settingsXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void VaultLockRecoveryMarker_SurvivesOnboardingResolveSavesAndRestart_OnlyClearedByPinOrEmergencyReset()
    {
        File.WriteAllBytes(Path.Combine(_tempDir, "vault.dat"), new byte[] { 10, 20, 30, 40 });
        File.WriteAllText(_tempPath, """{"Theme":"Dark",""" ); // truncado sin campos de PIN

        SettingsManager.Load();
        Assert.True(SettingsManager.LockSettingsPotentiallyLost);
        Assert.True(SettingsManager.HasPendingVaultLockRecoveryMarker());
        Assert.True(File.Exists(SettingsManager.VaultLockRecoveryMarkerPath));
        Assert.True(PasswordManager.IsVaultLockedByRecovery);
        Assert.True(PasswordManager.ShouldRequireUnlockForVaultAction());

        // Elegir Perfil B, resolver guardados y guardar nuevo settings.json válido (con BrowserLockMode = "None")
        PrivacyProfiles.ConfirmOnboardingSelection(SettingsManager.Current, PrivacyProfiles.ProfileB);
        Assert.True(SettingsManager.ResolveRecoveryAndAllowSaves());
        SettingsManager.Save();

        // El marcador persistente NO debe haberse borrado al elegir perfil ni al resolver guardados
        Assert.True(File.Exists(SettingsManager.VaultLockRecoveryMarkerPath));
        Assert.True(PasswordManager.IsVaultLockedByRecovery);

        // Simular reinicio completo llamando de nuevo a Load()
        SettingsManager.Load();
        Assert.False(SettingsManager.HadUnreadableSettingsFile);
        Assert.False(SettingsManager.LockSettingsPotentiallyLost); // flag en memoria se reinició
        Assert.True(SettingsManager.HasPendingVaultLockRecoveryMarker()); // pero el marcador en disco persiste
        Assert.True(PasswordManager.IsVaultLockedByRecovery);
        Assert.True(PasswordManager.ShouldRequireUnlockForVaultAction());
        Assert.NotEqual(
            MainWindow.StartupExecutionStage.FirstTimePinSetupOverlay,
            MainWindow.DetermineStartupStage(
                startInGuest: false,
                hasInitialAttachedTab: false,
                hasCompletedSecuritySetup: SettingsManager.Current.HasCompletedInitialSecuritySetup,
                hasMasterPasswordConfigured: PasswordManager.HasMasterPasswordConfigured(),
                lockSettingsPotentiallyLost: SettingsManager.IsVaultRecoveryLockActive,
                shouldRequireUnlockOnStartup: PasswordManager.ShouldRequireUnlockOnStartup(),
                tabsCount: 0,
                privacyOnboardingVersion: SettingsManager.Current.PrivacyProfileOnboardingVersion));

        // 1. Solo ConfigureMasterPassword o EmergencyResetLockAndClearVault pueden limpiar el marcador
        PasswordManager.ConfigureMasterPassword("159357", "AlwaysOnStartup", null, null);
        Assert.False(File.Exists(SettingsManager.VaultLockRecoveryMarkerPath));
        Assert.False(PasswordManager.IsVaultLockedByRecovery);

        // 2. Repetir escenario y validar limpieza vía EmergencyResetLockAndClearVault
        File.WriteAllBytes(Path.Combine(_tempDir, "vault.dat"), new byte[] { 10, 20, 30, 40 });
        File.WriteAllText(_tempPath, "{bad");
        SettingsManager.Load();
        Assert.True(File.Exists(SettingsManager.VaultLockRecoveryMarkerPath));

        PasswordManager.EmergencyResetLockAndClearVault();
        Assert.False(File.Exists(SettingsManager.VaultLockRecoveryMarkerPath));
        Assert.False(PasswordManager.IsVaultLockedByRecovery);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TruncatedPinBoundaries_SetMarkerUnlessCompletePinOrExplicitNone_AndSurviveSaveAndReload(bool useVaultDatSignal)
    {
        if (useVaultDatSignal)
        {
            File.WriteAllBytes(Path.Combine(_tempDir, "vault.dat"), new byte[] { 1, 2, 3, 4 });
        }
        else
        {
            File.WriteAllText(Path.Combine(_tempDir, ".security_setup_done"), "1");
        }

        // 1. Truncado justo después de BrowserLockMode != "None" (falta Hash y Salt) -> DEBE crear marcador y sobrevivir guardar+recargar
        string cutAfterLockMode = """
        {
          "Theme": "Dark",
          "BrowserLockMode": "AlwaysOnStartup",
          "MasterPasswordHa
        """;
        File.WriteAllText(_tempPath, cutAfterLockMode);
        SettingsManager.Load();

        Assert.False(PasswordManager.HasMasterPasswordConfigured());
        Assert.True(SettingsManager.LockSettingsPotentiallyLost);
        Assert.True(SettingsManager.IsVaultRecoveryLockActive);
        Assert.True(File.Exists(SettingsManager.VaultLockRecoveryMarkerPath));

        PrivacyProfiles.ConfirmOnboardingSelection(SettingsManager.Current, PrivacyProfiles.ProfileB);
        Assert.True(SettingsManager.ResolveRecoveryAndAllowSaves());
        SettingsManager.Save();
        SettingsManager.Load();

        Assert.False(PasswordManager.HasMasterPasswordConfigured());
        Assert.False(SettingsManager.LockSettingsPotentiallyLost);
        Assert.True(SettingsManager.IsVaultRecoveryLockActive);
        Assert.True(PasswordManager.IsVaultLockedByRecovery);
        Assert.True(File.Exists(SettingsManager.VaultLockRecoveryMarkerPath));

        // Limpiar marcador para siguiente subcaso
        File.Delete(SettingsManager.VaultLockRecoveryMarkerPath);

        // 2. Truncado justo después de MasterPasswordHash (con BrowserLockMode = "SmartSession", falta MasterPasswordSalt) -> DEBE crear marcador y sobrevivir guardar+recargar
        string cutAfterHash = """
        {
          "BrowserLockMode": "SmartSession",
          "MasterPasswordHash": "v2$210000$AAAA$BBBB",
          "MasterPasswordSa
        """;
        File.WriteAllText(_tempPath, cutAfterHash);
        SettingsManager.Load();

        Assert.False(PasswordManager.HasMasterPasswordConfigured());
        Assert.True(SettingsManager.LockSettingsPotentiallyLost);
        Assert.True(SettingsManager.IsVaultRecoveryLockActive);
        Assert.True(File.Exists(SettingsManager.VaultLockRecoveryMarkerPath));

        Assert.True(SettingsManager.ResolveRecoveryAndAllowSaves());
        SettingsManager.Save();
        SettingsManager.Load();

        Assert.False(PasswordManager.HasMasterPasswordConfigured());
        Assert.True(SettingsManager.IsVaultRecoveryLockActive);
        Assert.True(PasswordManager.IsVaultLockedByRecovery);
        Assert.True(File.Exists(SettingsManager.VaultLockRecoveryMarkerPath));

        File.Delete(SettingsManager.VaultLockRecoveryMarkerPath);

        // 3. Truncado justo después de MasterPasswordSalt (BrowserLockMode + Hash + Salt completos) -> PIN activo, SIN marcador
        string cutAfterSalt = """
        {
          "BrowserLockMode": "AlwaysOnStartup",
          "MasterPasswordHash": "v2$210000$AAAA$BBBB",
          "MasterPasswordSalt": "salt123",
          "MasterPasswordLen
        """;
        File.WriteAllText(_tempPath, cutAfterSalt);
        SettingsManager.Load();

        Assert.True(PasswordManager.HasMasterPasswordConfigured());
        Assert.False(SettingsManager.LockSettingsPotentiallyLost);
        Assert.False(SettingsManager.IsVaultRecoveryLockActive);
        Assert.False(File.Exists(SettingsManager.VaultLockRecoveryMarkerPath));

        // 4. Truncado después de BrowserLockMode = "None" explícito -> usuario había elegido sin PIN, SIN marcador
        string cutAfterExplicitNone = """
        {
          "Theme": "Light",
          "BrowserLockMode": "None",
          "HasCompletedInitialSecuritySetup": true,
          "TabSusp
        """;
        File.WriteAllText(_tempPath, cutAfterExplicitNone);
        SettingsManager.Load();

        Assert.False(PasswordManager.HasMasterPasswordConfigured());
        Assert.False(SettingsManager.LockSettingsPotentiallyLost);
        Assert.False(SettingsManager.IsVaultRecoveryLockActive);
        Assert.False(File.Exists(SettingsManager.VaultLockRecoveryMarkerPath));
    }

    [Fact]
    public void VersionDowngradeGuard_UpgradesCleanly_BlocksOlderAppWithoutOverwriting_OnJsonAndMarkerSignals()
    {
        // 1. Instalación/uso normal en v2.6.0 registra versión en LastAppVersion y .last_app_version
        File.WriteAllText(_tempPath, """{"Theme":"Dark","PrivacyProfileOnboardingVersion":1,"EnableSearchSuggestions":false}""");
        SettingsManager.Load(new Version(2, 6, 0));

        Assert.False(SettingsManager.DowngradeDetected);
        Assert.False(SettingsManager.AutoSavesSuspended);
        Assert.Equal("2.6.0", File.ReadAllText(SettingsManager.LastAppVersionMarkerPath).Trim());

        // 2. Actualización legítima a v2.7.0 actualiza marcador y LastAppVersion sin perder preferencias
        SettingsManager.Load(new Version(2, 7, 0));
        Assert.False(SettingsManager.DowngradeDetected);
        Assert.False(SettingsManager.AutoSavesSuspended);
        Assert.Equal("Dark", SettingsManager.Current.Theme);
        Assert.False(SettingsManager.Current.EnableSearchSuggestions);
        Assert.Equal("2.7.0", SettingsManager.Current.LastAppVersion);
        Assert.Equal("2.7.0", File.ReadAllText(SettingsManager.LastAppVersionMarkerPath).Trim());

        SettingsManager.Save();
        string savedV270 = File.ReadAllText(_tempPath);
        Assert.Contains("\"LastAppVersion\": \"2.7.0\"", savedV270, StringComparison.Ordinal);

        // 3. Reapertura accidental con v2.6.0 (downgrade): bloquea, suspende guardados y NO toca settings.json ni marcador
        SettingsManager.Load(new Version(2, 6, 0));
        Assert.True(SettingsManager.DowngradeDetected);
        Assert.True(SettingsManager.AutoSavesSuspended);
        Assert.Equal("2.7.0", SettingsManager.DetectedNewerProfileVersion);
        Assert.NotNull(SettingsManager.DowngradeWarningMessage);
        Assert.False(SettingsManager.ResolveRecoveryAndAllowSaves());

        SettingsManager.Current.Theme = "Light";
        SettingsManager.Save();
        Assert.Equal(savedV270, File.ReadAllText(_tempPath));
        Assert.Equal("2.7.0", File.ReadAllText(SettingsManager.LastAppVersionMarkerPath).Trim());

        // 4. Incluso borrando .last_app_version, el campo LastAppVersion en settings.json detecta el downgrade
        File.Delete(SettingsManager.LastAppVersionMarkerPath);
        SettingsManager.Load(new Version(2, 6, 0));
        Assert.True(SettingsManager.DowngradeDetected);
        Assert.True(SettingsManager.AutoSavesSuspended);
        Assert.Equal("2.7.0", SettingsManager.DetectedNewerProfileVersion);

        // 5. Incluso borrando settings.json pero conservando .last_app_version, bloquea antes de recrear perfil anterior
        File.WriteAllText(SettingsManager.LastAppVersionMarkerPath, "2.7.0");
        File.Delete(_tempPath);
        SettingsManager.Load(new Version(2, 6, 0));
        Assert.True(SettingsManager.DowngradeDetected);
        Assert.False(SettingsManager.IsFreshInstall);
        Assert.True(SettingsManager.AutoSavesSuspended);
        Assert.False(File.Exists(_tempPath));
    }

    [Fact]
    public void BlockedSaves_OnboardingAlreadyCompleted_DeduplicatesIdenticalCorruptBackups_AndHonorsExplicitResolve()
    {
        string truncatedAfterOnboarding = """
        {
          "Theme": "Light",
          "PrivacyProfileOnboardingVersion": 1,
          "NotificationGlobalPolicy": "BlockAll",
          "BrowserLockMo
        """;
        File.WriteAllText(_tempPath, truncatedAfterOnboarding);

        SettingsManager.Load();
        Assert.True(SettingsManager.AutoSavesSuspended);
        Assert.False(MainWindow.ShouldShowPrivacyOnboardingWindow(SettingsManager.Current, isGuestSession: false, hasInitialAttachedTab: false));

        string[] backupsAfterRun1 = Directory.GetFiles(_tempDir, "settings.json.corrupt-*");
        Assert.Single(backupsAfterRun1);

        // Simular otro arranque idéntico ("Dejar como está") -> NO debe duplicar .corrupt-*
        SettingsManager.Load();
        SettingsManager.Load();
        string[] backupsAfterRun3 = Directory.GetFiles(_tempDir, "settings.json.corrupt-*");
        Assert.Single(backupsAfterRun3);
        Assert.True(SettingsManager.AutoSavesSuspended);

        // Guardar sin resolver sigue bloqueado
        SettingsManager.Current.Theme = "Dark";
        SettingsManager.Save();
        Assert.Equal(truncatedAfterOnboarding, File.ReadAllText(_tempPath));

        // Si el contenido corrupto cambia, sí se crea un segundo respaldo nuevo
        File.WriteAllText(_tempPath, truncatedAfterOnboarding + "x");
        SettingsManager.Load();
        Assert.Equal(2, Directory.GetFiles(_tempDir, "settings.json.corrupt-*").Length);

        // Resolución explícita ("Continuar con lo recuperado") habilita Save y guarda el archivo válido
        Assert.True(SettingsManager.ResolveRecoveryAndAllowSaves());
        Assert.False(SettingsManager.AutoSavesSuspended);
        SettingsManager.Save();
        SettingsManager.Load();
        Assert.False(SettingsManager.HadUnreadableSettingsFile);
        Assert.False(SettingsManager.AutoSavesSuspended);
        Assert.Equal("Light", SettingsManager.Current.Theme);
        Assert.Equal("BlockAll", SettingsManager.Current.NotificationGlobalPolicy);
    }

    [Fact]
    public async Task SafeRestart_WaitPid_InvalidStateRetry_AndDownloadPromptBypassWork()
    {
        Assert.Equal(54321, App.ParseWaitPidArgument(new[] { "--foo", "--wait-pid=54321" }));
        Assert.Equal(9876, App.ParseWaitPidArgument(new[] { "/wait-pid=9876" }));
        Assert.Null(App.ParseWaitPidArgument(new[] { $"--wait-pid={Environment.ProcessId}" }));
        Assert.Null(App.ParseWaitPidArgument(new[] { "--wait-pid=abc" }));

        // Proceso hijo real que espera ~250ms antes de salir
        Process? child = null;
        try
        {
            child = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -Command \"Start-Sleep -Milliseconds 250\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            Assert.NotNull(child);

            await App.WaitForParentProcessExitAsync(child!.Id, timeoutMs: 5000);
            Assert.True(child.HasExited);
        }
        finally
        {
            try { if (child != null && !child.HasExited) child.Kill(true); } catch { }
            child?.Dispose();
        }

        // Reintento ante 0x8007139F
        int attempts = 0;
        string created = await BrowserEngine.CreateWithInvalidStateRetryAsync(_ =>
        {
            attempts++;
            if (attempts < 4)
                throw new COMException("Different flags on active UserData", BrowserEngine.HResultInvalidState);
            return Task.FromResult("env-ok");
        }, retryDelaysMs: new[] { 10, 15, 20, 25 });
        Assert.Equal("env-ok", created);
        Assert.Equal(4, attempts);

        // Otro error distinto de 0x8007139F no debe reintentarse
        int otherAttempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BrowserEngine.CreateWithInvalidStateRetryAsync<string>(_ =>
            {
                otherAttempts++;
                throw new InvalidOperationException("Other failure");
            }, retryDelaysMs: new[] { 10, 10 }));
        Assert.Equal(1, otherAttempts);

        // Durante reinicio controlado no debe volver a preguntar OnClosing por descargas activas
        Assert.False(MainWindow.ShouldPromptActiveDownloadsOnClose(isRestartingProcess: true, hasActiveDownloads: true));
        Assert.True(MainWindow.ShouldPromptActiveDownloadsOnClose(isRestartingProcess: false, hasActiveDownloads: true));
    }

    [Fact]
    public void FreshInstall_InterruptedBeforeChoice_RemainsFreshOnRestart_AndLegacyJsonRemainsUpgrade()
    {
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
        if (File.Exists(_tempPath + ".tmp")) File.Delete(_tempPath + ".tmp");

        // Primer arranque limpio crea settings.json con FreshInstallOnboardingPending = true
        SettingsManager.Load();
        Assert.True(SettingsManager.IsFreshInstall);
        Assert.True(SettingsManager.Current.FreshInstallOnboardingPending);
        Assert.True(File.Exists(_tempPath));

        // Segundo arranque tras cierre antes de confirmar la ventana inicial (o tras crear solo PIN)
        SettingsManager.Load();
        Assert.True(SettingsManager.IsFreshInstall);
        Assert.True(SettingsManager.Current.FreshInstallOnboardingPending);
        Assert.True(PrivacyProfiles.ResolveClosedWithoutChoice(SettingsManager.Current, SettingsManager.IsFreshInstall));
        Assert.Equal(PrivacyProfiles.ProfileKeyB, PrivacyProfiles.ResolveActiveProfile(SettingsManager.Current));
        Assert.False(SettingsManager.Current.FreshInstallOnboardingPending);
        Assert.Equal(1, SettingsManager.Current.PrivacyProfileOnboardingVersion);
        SettingsManager.Save();

        // Tercer arranque ya completado el onboarding
        SettingsManager.Load();
        Assert.False(SettingsManager.IsFreshInstall);
        Assert.False(SettingsManager.Current.FreshInstallOnboardingPending);

        // Archivo legacy v2.6.0 sin FreshInstallOnboardingPending sigue tratándose como actualización
        File.WriteAllText(_tempPath, """{"Theme":"Classic","EnableExternalFaviconService":true}""");
        SettingsManager.Load();
        Assert.False(SettingsManager.IsFreshInstall);
        Assert.False(SettingsManager.Current.FreshInstallOnboardingPending);
    }

    [Fact]
    public async Task MinorFixes_NoFlagNamesInShortSummary_HttpsDowngradeBlocked_Ipv4CompatibleBlocked_AndGenericRecovery()
    {
        // 1. Ningún texto visible de OptionDescriptors debe citar nombres técnicos de flags
        foreach (var desc in PrivacyProfiles.OptionDescriptors)
        {
            string combined = $"{desc.Title}\n{desc.ShortSummary}\n{desc.TableLabel}\n{desc.Advantage}\n{desc.Disadvantage}";
            Assert.DoesNotContain("msSmartScreenProtection", combined, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("--disable-", combined, StringComparison.OrdinalIgnoreCase);
        }

        // 2. Bloqueo de redirección https -> http
        int requestedCount = 0;
        using var downgradeClient = new HttpClient(new StubRedirectHandler(_ =>
        {
            requestedCount++;
            return new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri("http://downgraded.example.com/favicon.ico") }
            };
        }));
        Assert.Null(await FaviconService.DownloadRemoteFaviconBytesAsync("https://secure.example.com/favicon.ico", downgradeClient));
        Assert.Equal(1, requestedCount);

        // 3. IPv6 ::a.b.c.d (IPv4-compatible) privado bloqueado en IsPrivateIp e IsAllowedFaviconUrl
        Assert.True(LexBrowser.Core.Tv.TvUrlGuard.IsPrivateIp(IPAddress.Parse("::127.0.0.1")));
        Assert.True(LexBrowser.Core.Tv.TvUrlGuard.IsPrivateIp(IPAddress.Parse("::10.0.0.1")));
        Assert.True(LexBrowser.Core.Tv.TvUrlGuard.IsPrivateIp(IPAddress.Parse("::192.168.1.50")));
        Assert.True(LexBrowser.Core.Tv.TvUrlGuard.IsPrivateIp(IPAddress.Parse("::169.254.169.254")));
        Assert.True(LexBrowser.Core.Tv.TvUrlGuard.IsPrivateIp(IPAddress.Parse("::100.64.0.1")));
        Assert.False(LexBrowser.Core.Tv.TvUrlGuard.IsPrivateIp(IPAddress.Parse("::8.8.8.8")));
        Assert.False(FaviconService.IsAllowedFaviconUrl("http://[::127.0.0.1]/favicon.ico", out _));

        // 4. Recuperación parcial genérica rescata notificaciones, geometría/PiP, lector en voz alta y navegador predeterminado
        string truncatedRich = """
        {
          "Theme": "Light",
          "FloatingPipWidth": 720,
          "FloatingPipHeight": 420,
          "HasCustomMainWindowBounds": true,
          "MainWindowWidth": 1440,
          "MainWindowHeight": 900,
          "MainWindowLeft": 120,
          "MainWindowTop": 80,
          "EnableReadAloud": false,
          "ReadAloudRate": 3,
          "ReadAloudVoiceName": "Microsoft Sabina",
          "UserDecidedDefaultBrowser": true,
          "WasPreviouslyDefaultBrowser": true,
          "CapturePermissionsMigrationV1Done": true,
          "NotificationGlobalPolicy": "BlockAll",
          "NotificationDndShortcut": "Ctrl+Alt+N",
          "NotificationGlobalDailyLimit": 20,
          "NotificationDefaultSiteDailyLimit": 3,
          "NotificationDiscardOlderThan24Hours": false,
          "NotificationDndOnFullscreen": false,
          "CutOffField": "incomplete
        """;
        File.WriteAllText(_tempPath, truncatedRich);
        SettingsManager.Load();

        var s = SettingsManager.Current;
        Assert.Equal("Light", s.Theme);
        Assert.Equal(720, s.FloatingPipWidth);
        Assert.Equal(420, s.FloatingPipHeight);
        Assert.True(s.HasCustomMainWindowBounds);
        Assert.Equal(1440, s.MainWindowWidth);
        Assert.Equal(900, s.MainWindowHeight);
        Assert.Equal(120, s.MainWindowLeft);
        Assert.Equal(80, s.MainWindowTop);
        Assert.False(s.EnableReadAloud);
        Assert.Equal(3, s.ReadAloudRate);
        Assert.Equal("Microsoft Sabina", s.ReadAloudVoiceName);
        Assert.True(s.UserDecidedDefaultBrowser);
        Assert.True(s.WasPreviouslyDefaultBrowser);
        Assert.True(s.CapturePermissionsMigrationV1Done);
        Assert.Equal("BlockAll", s.NotificationGlobalPolicy);
        Assert.Equal("Ctrl+Alt+N", s.NotificationDndShortcut);
        Assert.Equal(20, s.NotificationGlobalDailyLimit);
        Assert.Equal(3, s.NotificationDefaultSiteDailyLimit);
        Assert.False(s.NotificationDiscardOlderThan24Hours);
        Assert.False(s.NotificationDndOnFullscreen);
    }

    private sealed class StubRedirectHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubRedirectHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }

    private static byte[] CreateSolidPngBytes(int width, int height)
    {
        int stride = width * 4;
        byte[] allPixels = new byte[height * stride];
        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, allPixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
