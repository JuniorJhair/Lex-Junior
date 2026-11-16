using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using LexBrowser.UI.Models;

namespace LexBrowser.Core.AdBlock;

/// <summary>
/// Definición declarativa y centralizada de un módulo de LexShield.
/// Sirve como única fuente de verdad para el popup de escudo, contadores, Ajustes y encabezados de diagnóstico.
/// </summary>
public sealed class ShieldModuleDefinition
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string Description { get; init; } = string.Empty;
    public string? CounterLabel { get; init; }
    public string? DiagnosticKey { get; init; }
    public bool DisabledInCompatMode { get; init; }
    public bool IsCustom { get; init; }
    public Func<EffectiveSitePolicy, BrowserSettings, SiteShieldProfile?, bool>? GetEffectiveState { get; init; }
    public Func<SiteShieldProfile?, BrowserSettings, bool>? GetSavedSiteState { get; init; }
    public Action<SiteShieldProfile, BrowserSettings, bool>? ApplySiteToggle { get; init; }
    public Func<BrowserTab, int>? GetTabCounter { get; init; }
}

/// <summary>
/// Elemento generado dinámicamente para la UI de módulos en el popup de escudo.
/// </summary>
public sealed class ShieldModulePopupItem : INotifyPropertyChanged
{
    private bool _isChecked;
    private bool _isControlEnabled;

    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked != value)
            {
                _isChecked = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsControlEnabled
    {
        get => _isControlEnabled;
        set
        {
            if (_isControlEnabled != value)
            {
                _isControlEnabled = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Fila generada dinámicamente para el desglose de contadores del popup de escudo.
/// </summary>
public sealed class ShieldCounterRow
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public int Count { get; init; }
    public bool IsSecondary { get; init; }
    public string CountText => Count.ToString();
}

/// <summary>
/// Registro central de módulos de LexShield.
/// Al registrar un módulo nuevo aquí, aparece automáticamente en el popup, contadores y diagnóstico.
/// </summary>
public static class ShieldModuleRegistry
{
    private static readonly object RegistryLock = new();
    private static readonly List<ShieldModuleDefinition> BuiltInModules = new()
    {
        new ShieldModuleDefinition
        {
            Id = SitePolicy.ModNetwork,
            DisplayName = "Filtro de red y dominios",
            Description = "Bloquea dominios y peticiones de anuncios o rastreadores antes de descargarlos.",
            CounterLabel = "• Red bloqueada:",
            DiagnosticKey = "SiteNet",
            GetEffectiveState = (p, _, _) => p.Network,
            GetSavedSiteState = (prof, s) => (s.Shield?.Modules?.Network ?? true) && (prof?.IsModuleEnabled(SitePolicy.ModNetwork) ?? true),
            GetTabCounter = tab => tab.BlockedNetworkCount
        },
        new ShieldModuleDefinition
        {
            Id = SitePolicy.ModStubs,
            DisplayName = "Stubs de anuncios de video (VAST/IMA)",
            Description = "Responde con stubs inertes a peticiones VAST/VMAP e IMA para evitar pre-rolls sin romper el reproductor.",
            CounterLabel = "• Stubs VAST / IMA:",
            DiagnosticKey = "SiteStubs",
            DisabledInCompatMode = true,
            GetEffectiveState = (p, _, _) => p.Stubs,
            GetSavedSiteState = (prof, s) => (s.Shield?.Modules?.Stubs ?? true) && (s.EnableImaSdkStub) && (prof?.IsModuleEnabled(SitePolicy.ModStubs) ?? true),
            GetTabCounter = tab => tab.BlockedStubsCount
        },
        new ShieldModuleDefinition
        {
            Id = SitePolicy.ModPopups,
            DisplayName = "Anti-popups y redirecciones",
            Description = "Bloquea ventanas emergentes, popunders y redirecciones forzadas sin interacción.",
            CounterLabel = "• Popups / redirecciones:",
            DiagnosticKey = "SitePopups",
            GetEffectiveState = (p, _, _) => p.Popups,
            GetSavedSiteState = (prof, s) => (s.Shield?.Modules?.Popups ?? true) && (prof?.IsModuleEnabled(SitePolicy.ModPopups) ?? true),
            GetTabCounter = tab => tab.BlockedPopupsCount
        },
        new ShieldModuleDefinition
        {
            Id = SitePolicy.ModOverlays,
            DisplayName = "Anti-capas invisibles",
            Description = "Neutraliza overlays transparentes de click-jacking sobre enlaces y reproductores.",
            CounterLabel = "• Capas invisibles / cosmética:",
            DiagnosticKey = "SiteOverlays",
            DisabledInCompatMode = true,
            GetEffectiveState = (p, _, _) => p.Overlays,
            GetSavedSiteState = (prof, s) => (s.Shield?.Modules?.Overlays ?? true) && (prof?.IsModuleEnabled(SitePolicy.ModOverlays) ?? true),
            GetTabCounter = tab => tab.BlockedOverlaysCount + tab.BlockedCosmeticCount + tab.BlockedAdapterCount
        },
        new ShieldModuleDefinition
        {
            Id = SitePolicy.ModCosmetic,
            DisplayName = "Ocultación cosmética de banners",
            Description = "Oculta elementos DOM publicitarios y espacios vacíos mediante selectores y scriptlets.",
            DiagnosticKey = "SiteCos",
            DisabledInCompatMode = true,
            GetEffectiveState = (p, _, _) => p.Cosmetic,
            GetSavedSiteState = (prof, s) => s.EnableCosmeticShield && (s.Shield?.Modules?.Cosmetic ?? true) && (prof?.IsModuleEnabled(SitePolicy.ModCosmetic) ?? true)
        },
        new ShieldModuleDefinition
        {
            Id = SitePolicy.ModCookieBanners,
            DisplayName = "Ocultar avisos de cookies",
            Description = "Oculta modales y barras de consentimiento de cookies mediante reglas cosméticas.",
            DiagnosticKey = "SiteCookies",
            DisabledInCompatMode = true,
            GetEffectiveState = (p, s, prof) => p.ShieldEnabled && !p.IsCompatMode && s.EnableCookieBannerHiding && (prof?.IsModuleEnabled(SitePolicy.ModCookieBanners) ?? true),
            GetSavedSiteState = (prof, s) => s.EnableCookieBannerHiding && (prof?.IsModuleEnabled(SitePolicy.ModCookieBanners) ?? true),
            ApplySiteToggle = (prof, s, enabled) =>
            {
                prof.SetModuleEnabled(SitePolicy.ModCookieBanners, enabled);
                if (enabled && !s.EnableCookieBannerHiding)
                {
                    s.EnableCookieBannerHiding = true;
                }
            }
        },
        new ShieldModuleDefinition
        {
            Id = SitePolicy.ModVideoDefuser,
            DisplayName = "Acelerador genérico (solo Motor Legacy)",
            Description = "Acelera anuncios pre-roll en reproductores externos únicamente bajo Motor Legacy (inactivo en Motor Brave).",
            DiagnosticKey = "SiteDefuser",
            DisabledInCompatMode = true,
            GetEffectiveState = (p, _, _) => p.VideoDefuser,
            GetSavedSiteState = (prof, s) =>
                string.Equals(ShieldSettings.NormalizeEngine(s.Shield?.Engine), ShieldSettings.EngineLegacy, StringComparison.Ordinal) &&
                (s.EnableVideoPlayerDefuser || (s.Shield?.Modules?.VideoDefuser ?? false)) &&
                (prof?.IsModuleEnabled(SitePolicy.ModVideoDefuser) ?? true),
            ApplySiteToggle = (prof, s, enabled) =>
            {
                prof.SetModuleEnabled(SitePolicy.ModVideoDefuser, enabled);
                if (enabled)
                {
                    s.EnableVideoPlayerDefuser = true;
                    s.Shield ??= new ShieldSettings();
                    s.Shield.Modules ??= new ShieldModulesConfig();
                    s.Shield.Modules.VideoDefuser = true;
                }
            }
        },
        new ShieldModuleDefinition
        {
            Id = SitePolicy.ModAdapter,
            DisplayName = "Adaptador específico del sitio (YT/DM)",
            Description = "Habilita los adaptadores dedicados y aislados para YouTube y Dailymotion.",
            DiagnosticKey = "SiteAdapter",
            DisabledInCompatMode = true,
            GetEffectiveState = (p, _, _) => p.Adapter,
            GetSavedSiteState = (prof, s) => (s.EnableYouTubeAdBlock || s.EnableDailymotionAdBlock) && (prof?.IsModuleEnabled(SitePolicy.ModAdapter) ?? true)
        }
    };

    private static readonly List<ShieldModuleDefinition> CustomModules = new();

    public static IReadOnlyList<ShieldModuleDefinition> Modules
    {
        get
        {
            lock (RegistryLock)
            {
                if (CustomModules.Count == 0)
                    return BuiltInModules;

                var combined = new List<ShieldModuleDefinition>(BuiltInModules.Count + CustomModules.Count);
                combined.AddRange(BuiltInModules);
                combined.AddRange(CustomModules);
                return combined;
            }
        }
    }

    public static ShieldModuleDefinition? Find(string? moduleId)
    {
        if (string.IsNullOrWhiteSpace(moduleId))
            return null;

        lock (RegistryLock)
        {
            for (int i = 0; i < BuiltInModules.Count; i++)
            {
                if (string.Equals(BuiltInModules[i].Id, moduleId, StringComparison.OrdinalIgnoreCase))
                    return BuiltInModules[i];
            }
            for (int i = 0; i < CustomModules.Count; i++)
            {
                if (string.Equals(CustomModules[i].Id, moduleId, StringComparison.OrdinalIgnoreCase))
                    return CustomModules[i];
            }
            return null;
        }
    }

    public static void RegisterModule(ShieldModuleDefinition definition)
    {
        if (definition == null || string.IsNullOrWhiteSpace(definition.Id))
            throw new ArgumentException("El identificador del módulo es obligatorio.", nameof(definition));

        lock (RegistryLock)
        {
            CustomModules.RemoveAll(m => string.Equals(m.Id, definition.Id, StringComparison.OrdinalIgnoreCase));
            CustomModules.Add(definition);
        }
    }

    public static bool UnregisterCustomModule(string moduleId)
    {
        if (string.IsNullOrWhiteSpace(moduleId))
            return false;

        lock (RegistryLock)
        {
            return CustomModules.RemoveAll(m => string.Equals(m.Id, moduleId, StringComparison.OrdinalIgnoreCase)) > 0;
        }
    }

    public static void ClearCustomModules()
    {
        lock (RegistryLock)
        {
            CustomModules.Clear();
        }
    }

    public static bool EvaluateSavedState(ShieldModuleDefinition def, SiteShieldProfile? profile, BrowserSettings settings)
    {
        if (def.GetSavedSiteState != null)
            return def.GetSavedSiteState(profile, settings);

        return profile?.IsModuleEnabled(def.Id) ?? true;
    }

    public static bool EvaluateEffectiveState(
        ShieldModuleDefinition def,
        EffectiveSitePolicy policy,
        BrowserSettings settings,
        SiteShieldProfile? profile)
    {
        if (def.GetEffectiveState != null)
            return def.GetEffectiveState(policy, settings, profile);

        if (!policy.ShieldEnabled)
            return false;
        if (policy.IsCompatMode && def.DisabledInCompatMode)
            return false;

        return EvaluateSavedState(def, profile, settings);
    }

    public static List<ShieldModulePopupItem> BuildPopupModuleItems(
        string? domain,
        EffectiveSitePolicy policy,
        BrowserSettings settings,
        bool hasSite)
    {
        SiteShieldProfile? profile = null;
        if (hasSite && !string.IsNullOrEmpty(domain) && settings.Shield?.Sites != null)
        {
            settings.Shield.Sites.TryGetValue(domain, out profile);
        }

        var list = Modules;
        var items = new List<ShieldModulePopupItem>(list.Count);
        bool controlsInteractive = hasSite && settings.EnableAdBlocker;
        bool isBraveEngine = string.Equals(ShieldSettings.NormalizeEngine(settings.Shield?.Engine), ShieldSettings.EngineBrave, StringComparison.Ordinal);

        for (int i = 0; i < list.Count; i++)
        {
            var mod = list[i];
            bool isLegacyOnlyInBrave = isBraveEngine && string.Equals(mod.Id, SitePolicy.ModVideoDefuser, StringComparison.OrdinalIgnoreCase);
            // Cuando el escudo del sitio está en Off/pausado mostramos el estado guardado para permitir edición individual;
            // cuando está en Compat/Normal reflejamos el estado efectivo (compat apaga overlays/cosmética, etc.)
            bool isChecked = isLegacyOnlyInBrave
                ? false
                : (policy.IsCompatMode
                    ? EvaluateEffectiveState(mod, policy, settings, profile)
                    : (settings.EnableAdBlocker && EvaluateSavedState(mod, profile, settings)));

            items.Add(new ShieldModulePopupItem
            {
                Id = mod.Id,
                DisplayName = mod.DisplayName,
                Description = mod.Description,
                IsChecked = isChecked,
                IsControlEnabled = controlsInteractive && !isLegacyOnlyInBrave
            });
        }

        return items;
    }

    public static List<ShieldCounterRow> BuildCounterRows(BrowserTab? tab)
    {
        var list = Modules;
        var rows = new List<ShieldCounterRow>(list.Count + 1);

        for (int i = 0; i < list.Count; i++)
        {
            var mod = list[i];
            if (!string.IsNullOrEmpty(mod.CounterLabel) && mod.GetTabCounter != null)
            {
                rows.Add(new ShieldCounterRow
                {
                    Id = mod.Id,
                    Label = mod.CounterLabel,
                    Count = tab != null ? mod.GetTabCounter(tab) : 0,
                    IsSecondary = false
                });
            }
        }

        rows.Add(new ShieldCounterRow
        {
            Id = "telemetry204",
            Label = "• Telemetría 204 (silenciada):",
            Count = tab?.BlockedTelemetry204Count ?? 0,
            IsSecondary = true
        });

        return rows;
    }

    public static string BuildDiagnosticHeaderSummary(EffectiveSitePolicy? activePolicy, BrowserSettings settings)
    {
        if (!activePolicy.HasValue)
            return string.Empty;

        var policy = activePolicy.Value;
        SiteShieldProfile? profile = null;
        if (!string.IsNullOrEmpty(policy.RegistrableDomain) && settings.Shield?.Sites != null)
        {
            settings.Shield.Sites.TryGetValue(policy.RegistrableDomain, out profile);
        }

        var sb = new StringBuilder(192);
        sb.Append($" | SiteMode={policy.Mode} | SiteShield={policy.ShieldEnabled}");

        var list = Modules;
        for (int i = 0; i < list.Count; i++)
        {
            var mod = list[i];
            string key = string.IsNullOrEmpty(mod.DiagnosticKey) ? $"Mod_{mod.Id}" : mod.DiagnosticKey;
            bool state = EvaluateEffectiveState(mod, policy, settings, profile);
            sb.Append($" | {key}={state}");
        }

        return sb.ToString();
    }
}
