using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using LexBrowser.Core;
using LexBrowser.Core.AdBlock;
using LexBrowser.UI.Views;
using Xunit;

namespace LexBrowser.Tests;

internal static class TestSettingsIsolation
{
    internal static readonly string DefaultTestSettingsDir = Path.Combine(
        Path.GetTempPath(),
        "LexBrowserTests",
        $"proc_{Environment.ProcessId}_{Guid.NewGuid():N}");

    internal static readonly string DefaultTestSettingsPath = Path.Combine(
        DefaultTestSettingsDir,
        "settings.json");

    [ModuleInitializer]
    internal static void Initialize()
    {
        Directory.CreateDirectory(DefaultTestSettingsDir);
        Environment.SetEnvironmentVariable("LEX_TEST_SETTINGS_PATH", DefaultTestSettingsPath);
        SettingsManager.CustomSettingsPath = DefaultTestSettingsPath;
        SettingsManager.Load();
    }

    internal static void ResetToIsolatedDefault()
    {
        Directory.CreateDirectory(DefaultTestSettingsDir);
        SettingsManager.CustomSettingsPath = DefaultTestSettingsPath;
        SettingsManager.Load();
    }
}

[Collection("SettingsCollection")]
public sealed class SettingsPersistenceIsolationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _tempPath;

    public SettingsPersistenceIsolationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"lex_shield_persist_{Guid.NewGuid():N}");
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
    public void SettingsManager_InTests_NeverTargetsRealLocalAppDataSettingsFile()
    {
        TestSettingsIsolation.ResetToIsolatedDefault();
        string realAppDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LexBrowser",
            "settings.json");

        Assert.NotEqual(
            Path.GetFullPath(realAppDataPath),
            Path.GetFullPath(SettingsManager.SettingsPath),
            StringComparer.OrdinalIgnoreCase);
        Assert.StartsWith(
            Path.GetFullPath(Path.GetTempPath()),
            Path.GetFullPath(SettingsManager.SettingsPath),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SettingsManager_Load_AcceptsCaseInsensitiveKeysAndMigratesLegacyBraveToV4AcrossDiagnosticToggle()
    {
        File.WriteAllText(_tempPath, """
        {
          "SettingsSchemaVersion": 2,
          "Shield": {
            "engine": "brave",
            "DiagnosticMode": false
          }
        }
        """);

        SettingsManager.Load();
        Assert.Equal(ShieldSettings.EngineLexShield, ShieldRouter.GetConfiguredEngineName());
        Assert.Equal(4, SettingsManager.Current.SettingsSchemaVersion);
        Assert.Equal(4, SettingsManager.Current.Shield.Version);

        // Simula activar Modo Diagnóstico + guardar
        SettingsManager.Current.Shield.DiagnosticMode = true;
        ShieldLogger.DiagnosticMode = true;
        SettingsManager.Save();

        SettingsManager.Load();
        Assert.True(ShieldLogger.DiagnosticMode);
        Assert.True(SettingsManager.Current.Shield.DiagnosticMode);
        Assert.Equal(ShieldSettings.EngineLexShield, SettingsManager.Current.Shield.Engine);
    }

    [Fact]
    public void SettingsWindow_RemovedDualEngineSelector_WhileKeepingBraveSearchOption()
    {
        Assert.Null(typeof(SettingsWindow).GetMethod("ChkUseBraveEngine_Click", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Null(typeof(SettingsWindow).GetField("ChkUseBraveEngine", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.NotNull(typeof(SettingsWindow).GetField("RbBrave", BindingFlags.Instance | BindingFlags.NonPublic));
    }

    [Fact]
    public void NewProfile_DefaultsToLexShieldCore_AlignedDefaults_AndVersion4()
    {
        File.WriteAllText(_tempPath, "");
        File.Delete(_tempPath);

        SettingsManager.Load();
        var s = SettingsManager.Current;

        Assert.Equal(ShieldSettings.CurrentShieldVersion, s.SettingsSchemaVersion);
        Assert.Equal(ShieldSettings.CurrentShieldVersion, s.Shield.Version);
        Assert.Equal(ShieldSettings.EngineLexShield, s.Shield.Engine);
        Assert.True(s.EnableWebShieldInSubframes);
        Assert.True(s.Shield.Modules.Subframes);
        Assert.False(s.Shield.DiagnosticMode);
        Assert.False(ShieldLogger.DiagnosticMode);
        Assert.False(s.EnableVideoPlayerDefuser);
        Assert.False(s.Shield.Modules.VideoDefuser);
        Assert.True(s.EnableYouTubeAdBlock);
        Assert.True(s.EnableDailymotionAdBlock);
        Assert.True(s.Shield.Adapters.YouTube);
        Assert.True(s.Shield.Adapters.Dailymotion);
    }

    [Fact]
    public void ExistingLegacyAndBraveProfiles_MigrateToV4WithoutClobberingSitePrefs()
    {
        // 1. Perfil pre-v3 en "Legacy" -> migra a v4 y habilita subframes pre-v3 preservando sitios y cosmética
        File.WriteAllText(_tempPath, """
        {
          "SettingsSchemaVersion": 2,
          "EnableCosmeticShield": false,
          "EnableWebShieldInSubframes": false,
          "Shield": {
            "Version": 2,
            "Engine": "Legacy",
            "DiagnosticMode": true,
            "Sites": {
              "example.org": { "Mode": "Off" }
            }
          }
        }
        """);

        SettingsManager.Load();
        var firstLoad = SettingsManager.Current;
        Assert.Equal(ShieldSettings.EngineLexShield, firstLoad.Shield.Engine);
        Assert.Equal(ShieldSettings.CurrentShieldVersion, firstLoad.Shield.Version);
        Assert.Equal(ShieldSettings.CurrentShieldVersion, firstLoad.SettingsSchemaVersion);
        Assert.True(firstLoad.EnableWebShieldInSubframes);
        Assert.True(firstLoad.Shield.Modules.Subframes);
        Assert.True(firstLoad.Shield.DiagnosticMode);
        Assert.False(firstLoad.EnableCosmeticShield);
        Assert.Equal(SitePolicy.ModeOff, firstLoad.Shield.Sites["example.org"].Mode);

        // 2. Perfil v3 en "Brave" con subframes=false, Theme="Dark", Search="Brave" y defuser=true -> migra a v4 intacto
        File.WriteAllText(_tempPath, """
        {
          "SettingsSchemaVersion": 3,
          "Theme": "Light",
          "DefaultSearchEngine": "Brave",
          "EnableVideoPlayerDefuser": true,
          "EnableWebShieldInSubframes": false,
          "Shield": {
            "Version": 3,
            "Engine": "Brave",
            "EngineMigratedToBrave": true,
            "DiagnosticMode": false,
            "Sites": {
              "hentaila.com": { "Mode": "Compat" }
            }
          }
        }
        """);

        SettingsManager.Load();
        var v3Load = SettingsManager.Current;
        Assert.Equal(ShieldSettings.EngineLexShield, v3Load.Shield.Engine);
        Assert.Equal(ShieldSettings.CurrentShieldVersion, v3Load.Shield.Version);
        Assert.Equal(ShieldSettings.CurrentShieldVersion, v3Load.SettingsSchemaVersion);
        Assert.Equal("Light", v3Load.Theme);
        Assert.Equal("Brave", v3Load.DefaultSearchEngine);
        Assert.True(v3Load.EnableVideoPlayerDefuser);
        Assert.False(v3Load.EnableWebShieldInSubframes);
        Assert.Equal(SitePolicy.ModeCompat, v3Load.Shield.Sites["hentaila.com"].Mode);
    }

    [Fact]
    public void UnifiedDefault_KeepsYouTubeAndDailymotionStrictlyIsolated()
    {
        SettingsManager.Load();
        Assert.Equal(ShieldSettings.EngineLexShield, ShieldRouter.GetConfiguredEngineName());

        var ytPolicy = SitePolicy.Resolve("youtube.com");
        var ytAdDecision = ShieldRouter.EvaluateWebResource(
            new Uri("https://ads.youtube.com/api/stats"),
            "www.youtube.com",
            "youtube.com",
            "youtube.com",
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            ytPolicy,
            Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.XmlHttpRequest);
        Assert.True(ytAdDecision.ShouldBlock);
        Assert.Equal("Blocked ad domain or pattern", ytAdDecision.Reason);

        var dmPolicy = SitePolicy.Resolve("dailymotion.com");
        var dmAdDecision = ShieldRouter.EvaluateWebResource(
            new Uri("https://dmads.dailymotion.com/ad/vast.xml"),
            "www.dailymotion.com",
            "dailymotion.com",
            "dailymotion.com",
            "https://www.dailymotion.com/video/x8abcde",
            "https://www.dailymotion.com/video/x8abcde",
            dmPolicy,
            Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.XmlHttpRequest);
        Assert.True(dmAdDecision.ShouldBlock);
        Assert.Equal("Blocked ad domain or pattern", dmAdDecision.Reason);

        string ytPlan = ShieldRouter.BuildDocumentPlanJson(
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "", true,
            new Uri("https://www.youtube.com/watch?v=dQw4w9WgXcQ"), temporaryCompatMode: false);
        Assert.Equal("""{"enabled":false}""", ytPlan);

        string dmPlan = ShieldRouter.BuildDocumentPlanJson(
            "https://www.dailymotion.com/video/x8abcde", "", true,
            new Uri("https://www.dailymotion.com/video/x8abcde"), temporaryCompatMode: false);
        Assert.Equal("""{"enabled":false}""", dmPlan);
    }
}
