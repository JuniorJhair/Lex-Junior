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
}
