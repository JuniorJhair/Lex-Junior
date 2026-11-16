using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using LexBrowser.Core;
using LexBrowser.Core.AdBlock;
using LexBrowser.Core.AdBlock.Brave;
using LexBrowser.UI.Models;
using Xunit;

namespace LexBrowser.Tests;

[Collection("SettingsCollection")]
public sealed class ShieldModuleRegistryAndPopupTests : IDisposable
{
    public ShieldModuleRegistryAndPopupTests()
    {
        TestSettingsIsolation.ResetToIsolatedDefault();
        ShieldModuleRegistry.ClearCustomModules();
    }

    public void Dispose()
    {
        ShieldModuleRegistry.ClearCustomModules();
        TestSettingsIsolation.ResetToIsolatedDefault();
    }

    [Fact]
    public void SiteToggleOff_PreservesSavedModuleStates_AndReEnablingOneModuleReactivatesSite()
    {
        var s = SettingsManager.Current;
        string host = "example.org";

        // Desactivar solo un módulo inicialmente
        SitePolicy.SetSiteModuleEnabled(host, SitePolicy.ModPopups, false, save: true);
        var beforeOff = SitePolicy.Resolve(host);
        Assert.True(beforeOff.ShieldEnabled);
        Assert.True(beforeOff.Network);
        Assert.False(beforeOff.Popups);

        // Pausar "Activo" del sitio (ModeOff): NO borra disabledModules ni bloquea los controles del popup
        SitePolicy.SetSiteMode(host, SitePolicy.ModeOff, save: true);
        var pausedPolicy = SitePolicy.Resolve(host);
        Assert.False(pausedPolicy.ShieldEnabled);
        Assert.False(pausedPolicy.Network);

        var itemsWhenPaused = ShieldModuleRegistry.BuildPopupModuleItems(host, pausedPolicy, s, hasSite: true);
        var netItem = itemsWhenPaused.Single(x => x.Id == SitePolicy.ModNetwork);
        var popItem = itemsWhenPaused.Single(x => x.Id == SitePolicy.ModPopups);
        Assert.True(netItem.IsControlEnabled);
        Assert.True(popItem.IsControlEnabled);
        Assert.True(netItem.IsChecked);
        Assert.False(popItem.IsChecked);

        // Reactivar "Activo": recupera el estado previo (network=true, popups=false)
        SitePolicy.SetSiteMode(host, SitePolicy.ModeNormal, save: true);
        var restored = SitePolicy.Resolve(host);
        Assert.True(restored.ShieldEnabled);
        Assert.True(restored.Network);
        Assert.False(restored.Popups);

        // Volver a pausar y ahora reactivar DIRECTAMENTE el módulo de popups: debe reactivar el sitio entero
        SitePolicy.SetSiteMode(host, SitePolicy.ModeOff, save: true);
        SitePolicy.SetSiteModuleEnabled(host, SitePolicy.ModCosmetic, false, save: true);
        SitePolicy.SetSiteModuleEnabled(host, SitePolicy.ModPopups, true, save: true);

        var reactivated = SitePolicy.Resolve(host);
        Assert.Equal(SitePolicy.ModeNormal, reactivated.Mode);
        Assert.True(reactivated.ShieldEnabled);
        Assert.True(reactivated.Popups);
        Assert.False(reactivated.Cosmetic);
    }

    [Fact]
    public void RegisterCustomModule_AppearsAutomaticallyInPopupCountersAndDiagnosticHeader()
    {
        var s = SettingsManager.Current;
        string customId = "custom_tracker_probe";
        bool toggledValue = true;

        ShieldModuleRegistry.RegisterModule(new ShieldModuleDefinition
        {
            Id = customId,
            DisplayName = "Probe de rastreadores",
            Description = "Módulo registrado dinámicamente para pruebas.",
            CounterLabel = "• Probe custom:",
            DiagnosticKey = "SiteProbe",
            IsCustom = true,
            GetEffectiveState = (p, _, prof) => p.ShieldEnabled && (prof?.IsModuleEnabled(customId) ?? toggledValue),
            GetSavedSiteState = (prof, _) => prof?.IsModuleEnabled(customId) ?? toggledValue,
            ApplySiteToggle = (prof, _, en) =>
            {
                toggledValue = en;
                prof.SetModuleEnabled(customId, en);
            },
            GetTabCounter = tab => tab.BlockedNetworkCount + 7
        });

        var policy = SitePolicy.Resolve("hentaila.com");
        var popupItems = ShieldModuleRegistry.BuildPopupModuleItems("hentaila.com", policy, s, hasSite: true);
        Assert.Contains(popupItems, m => m.Id == customId && m.DisplayName == "Probe de rastreadores" && m.IsChecked && m.IsControlEnabled);

        var tab = new BrowserTab();
        var counterRows = ShieldModuleRegistry.BuildCounterRows(tab);
        Assert.Contains(counterRows, r => r.Id == customId && r.Label == "• Probe custom:" && r.Count == 7);

        string diagHeader = ShieldModuleRegistry.BuildDiagnosticHeaderSummary(policy, s);
        Assert.Contains("SiteProbe=True", diagHeader, StringComparison.Ordinal);

        // Toggle via SitePolicy funciona sin tocar UI ni XAML
        SitePolicy.SetSiteModuleEnabled("hentaila.com", customId, false, save: false);
        Assert.False(toggledValue);
        string diagAfterToggle = ShieldModuleRegistry.BuildDiagnosticHeaderSummary(SitePolicy.Resolve("hentaila.com"), s);
        Assert.Contains("SiteProbe=False", diagAfterToggle, StringComparison.Ordinal);
    }

    [Fact]
    public void BraveScriptletEngine_And_DocumentPlan_ContainZeroNewFunctionOrEval()
    {
        string bootstrap = BraveScriptletEngine.BuildBootstrapScript("unit-token");
        Assert.DoesNotContain("new Function", bootstrap, StringComparison.Ordinal);
        Assert.Contains("__runStaticScriptlet", bootstrap, StringComparison.Ordinal);
        Assert.Contains("__getLexReg", bootstrap, StringComparison.Ordinal);

        // Verificar en código fuente C# que el bootstrap propio no usa new Function ni eval(
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string engineCsPath = Path.Combine(repoRoot, "LexBrowser", "Core", "AdBlock", "Brave", "BraveScriptletEngine.cs");
        if (File.Exists(engineCsPath))
        {
            string csSrc = File.ReadAllText(engineCsPath);
            Assert.DoesNotContain("new Function", csSrc, StringComparison.Ordinal);
            Assert.DoesNotContain("eval(", csSrc, StringComparison.Ordinal);
        }

        SettingsManager.Current.Shield ??= new ShieldSettings();
        SettingsManager.Current.Shield.Engine = ShieldSettings.EngineBrave;

        var bridge = new BraveBridgeHostObject(
            "tok",
            () => new Uri("https://hentaila.com/ver/1"),
            () => false);

        using var doc = JsonDocument.Parse(bridge.GetDocumentPlanJson("tok", "https://hentaila.com/ver/1", "", true));
        Assert.True(doc.RootElement.TryGetProperty("scriptletCalls", out var callsEl));
        Assert.True(callsEl.GetArrayLength() > 0);
        Assert.False(doc.RootElement.TryGetProperty("scriptlets", out _));
    }

    [Fact]
    public void BravePopupAndBundledLex_MarksVideoDefuserInactiveInBrave_AndEmbedsLexResource()
    {
        SettingsManager.Current.Shield ??= new ShieldSettings();
        SettingsManager.Current.Shield.Engine = ShieldSettings.EngineBrave;
        SettingsManager.Current.EnableAdBlocker = true;

        var braveItems = ShieldModuleRegistry.BuildPopupModuleItems(
            "hentaila.com", SitePolicy.Resolve("hentaila.com"), SettingsManager.Current, hasSite: true);
        var defuserBrave = braveItems.Single(x => x.Id == SitePolicy.ModVideoDefuser);
        Assert.False(defuserBrave.IsChecked);
        Assert.False(defuserBrave.IsControlEnabled);

        SettingsManager.Current.Shield.Engine = ShieldSettings.EngineLegacy;
        var legacyItems = ShieldModuleRegistry.BuildPopupModuleItems(
            "hentaila.com", SitePolicy.Resolve("hentaila.com"), SettingsManager.Current, hasSite: true);
        var defuserLegacy = legacyItems.Single(x => x.Id == SitePolicy.ModVideoDefuser);
        Assert.True(defuserLegacy.IsControlEnabled);

        string[] resources = typeof(BraveShieldData).Assembly.GetManifestResourceNames();
        Assert.Contains(resources, n => n.EndsWith("Assets.shield.brave-shield.lex", StringComparison.OrdinalIgnoreCase));
    }
}
