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
    public void SettingsManager_Load_AcceptsCaseInsensitiveKeysAndPreservesBraveAcrossDiagnosticToggle()
    {
        File.WriteAllText(_tempPath, """
        {
          "SettingsSchemaVersion": 2,
          "Shield": {
            "engine": "brave",
            "DiagnosticMode": false,
          },
        }
        """);

        SettingsManager.Load();
        Assert.True(ShieldRouter.IsBraveActive);
        Assert.Equal(ShieldSettings.EngineBrave, ShieldRouter.GetConfiguredEngineName());

        // Simula activar Modo Diagnóstico + guardar sin pasar por el selector de motor
        SettingsManager.Current.Shield.DiagnosticMode = true;
        ShieldLogger.DiagnosticMode = true;
        SettingsManager.Save();

        SettingsManager.Load();
        Assert.True(ShieldLogger.DiagnosticMode);
        Assert.True(SettingsManager.Current.Shield.DiagnosticMode);
        Assert.True(ShieldRouter.IsBraveActive);
        Assert.Equal(ShieldSettings.EngineBrave, SettingsManager.Current.Shield.Engine);
    }

    [Fact]
    public void SettingsWindow_ExposesDedicatedChkUseBraveEngineClickHandler_DecoupledFromSaveSettings()
    {
        var braveHandler = typeof(SettingsWindow).GetMethod(
            "ChkUseBraveEngine_Click",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(braveHandler);

        var s = new BrowserSettings();
        s.Shield.DiagnosticMode = true;

        SettingsWindow.ApplyShieldEngineSelection(s, useBraveEngine: true);
        Assert.Equal(ShieldSettings.EngineBrave, s.Shield.Engine);
        Assert.True(s.Shield.DiagnosticMode);

        SettingsWindow.ApplyShieldEngineSelection(s, useBraveEngine: false);
        Assert.Equal(ShieldSettings.EngineLegacy, s.Shield.Engine);
        Assert.True(s.Shield.DiagnosticMode);
    }

    [Fact]
    public void NewProfile_DefaultsToBraveEngine_AlignedDefaults_AndVersion3()
    {
        File.WriteAllText(_tempPath, "");
        File.Delete(_tempPath);

        SettingsManager.Load();
        var s = SettingsManager.Current;

        Assert.Equal(3, s.SettingsSchemaVersion);
        Assert.Equal(3, s.Shield.Version);
        Assert.Equal(ShieldSettings.EngineBrave, s.Shield.Engine);
        Assert.True(s.Shield.EngineMigratedToBrave);
        Assert.True(ShieldRouter.IsBraveActive);
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
    public void ExistingLegacyProfile_MigratesOnceToBraveWithoutClobberingPrefs_AndSubsequentLegacyChoicePersists()
    {
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

        // 1. Primera carga migra Legacy -> Brave una sola vez sin pisar otras preferencias
        SettingsManager.Load();
        var firstLoad = SettingsManager.Current;
        Assert.Equal(ShieldSettings.EngineBrave, firstLoad.Shield.Engine);
        Assert.True(firstLoad.Shield.EngineMigratedToBrave);
        Assert.Equal(3, firstLoad.Shield.Version);
        Assert.Equal(3, firstLoad.SettingsSchemaVersion);
        Assert.True(firstLoad.EnableWebShieldInSubframes);
        Assert.True(firstLoad.Shield.Modules.Subframes);
        Assert.True(firstLoad.Shield.DiagnosticMode);
        Assert.False(firstLoad.EnableCosmeticShield);
        Assert.Equal(SitePolicy.ModeOff, firstLoad.Shield.Sites["example.org"].Mode);

        // 2. Usuario elige volver a Legacy manualmente y guarda
        SettingsWindow.ApplyShieldEngineSelection(firstLoad, useBraveEngine: false);
        SettingsManager.Save();

        // 3. Reapertura posterior respeta Legacy y no vuelve a migrar ni al cambiar otros flags
        SettingsManager.Load();
        Assert.Equal(ShieldSettings.EngineLegacy, SettingsManager.Current.Shield.Engine);
        Assert.False(ShieldRouter.IsBraveActive);

        SettingsManager.Current.Shield.DiagnosticMode = false;
        SettingsManager.Current.EnableWebShieldInSubframes = true;
        SettingsManager.Save();
        SettingsManager.Load();
        Assert.Equal(ShieldSettings.EngineLegacy, SettingsManager.Current.Shield.Engine);
        Assert.False(SettingsManager.Current.Shield.DiagnosticMode);
        Assert.True(SettingsManager.Current.EnableWebShieldInSubframes);
    }

    [Fact]
    public void BraveDefault_KeepsYouTubeAndDailymotionStrictlyOnIsolatedLegacy()
    {
        SettingsManager.Load();
        Assert.True(ShieldRouter.IsBraveActive);

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
