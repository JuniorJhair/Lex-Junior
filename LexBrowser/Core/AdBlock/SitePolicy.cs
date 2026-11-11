using System;
using System.Collections.Generic;
using System.Text;

namespace LexBrowser.Core.AdBlock;

/// <summary>
/// Configuración global de los módulos de protección de LexShield (Versión 2).
/// </summary>
public sealed class ShieldModulesConfig
{
    public bool Network { get; set; } = true;
    public bool Tracking { get; set; } = true;
    public bool Stubs { get; set; } = true;
    public bool Popups { get; set; } = true;
    public bool Overlays { get; set; } = true;
    public bool Cosmetic { get; set; } = true;
    public bool AntiWall { get; set; } = true;
    public bool Subframes { get; set; } = false;
    public bool VideoDefuser { get; set; } = false;
}

/// <summary>
/// Configuración de adaptadores específicos por plataforma.
/// </summary>
public sealed class ShieldAdaptersConfig
{
    public bool YouTube { get; set; } = true;
    public bool Dailymotion { get; set; } = true;
    public bool ImaStub { get; set; } = true;
}

/// <summary>
/// Perfil de protección personalizado para un dominio específico.
/// Mode puede ser "Normal", "Compat" o "Off".
/// </summary>
public sealed class SiteShieldProfile
{
    public string Mode { get; set; } = "Normal";
    public List<string> Disabled { get; set; } = new();

    public bool IsModuleEnabled(string moduleName)
        => !Disabled.Contains(moduleName, StringComparer.OrdinalIgnoreCase);

    public void SetModuleEnabled(string moduleName, bool enabled)
    {
        Disabled.RemoveAll(m => string.Equals(m, moduleName, StringComparison.OrdinalIgnoreCase));
        if (!enabled)
        {
            Disabled.Add(moduleName);
        }
    }

    public bool Network { get => IsModuleEnabled(SitePolicy.ModNetwork); set => SetModuleEnabled(SitePolicy.ModNetwork, value); }
    public bool Stubs { get => IsModuleEnabled(SitePolicy.ModStubs); set => SetModuleEnabled(SitePolicy.ModStubs, value); }
    public bool Popups { get => IsModuleEnabled(SitePolicy.ModPopups); set => SetModuleEnabled(SitePolicy.ModPopups, value); }
    public bool Overlays { get => IsModuleEnabled(SitePolicy.ModOverlays); set => SetModuleEnabled(SitePolicy.ModOverlays, value); }
    public bool Cosmetic { get => IsModuleEnabled(SitePolicy.ModCosmetic); set => SetModuleEnabled(SitePolicy.ModCosmetic, value); }
    public bool CookieBanners { get => IsModuleEnabled(SitePolicy.ModCookieBanners); set => SetModuleEnabled(SitePolicy.ModCookieBanners, value); }
    public bool VideoDefuser { get => IsModuleEnabled(SitePolicy.ModVideoDefuser); set => SetModuleEnabled(SitePolicy.ModVideoDefuser, value); }
    public bool Adapter { get => IsModuleEnabled(SitePolicy.ModAdapter); set => SetModuleEnabled(SitePolicy.ModAdapter, value); }
}

/// <summary>
/// Contenedor versionado de ajustes de LexShield persistido en settings.json.
/// </summary>
public sealed class ShieldSettings
{
    public int Version { get; set; } = 2;
    public bool Enabled { get; set; } = true;
    public bool DiagnosticMode { get; set; } = false;
    public ShieldModulesConfig Modules { get; set; } = new();
    public ShieldAdaptersConfig Adapters { get; set; } = new();
    public Dictionary<string, SiteShieldProfile> Sites { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Política efectiva calculada para un sitio y pestaña determinados.
/// </summary>
public readonly struct EffectiveSitePolicy
{
    public string RegistrableDomain { get; init; }
    public string Mode { get; init; }
    public bool ShieldEnabled { get; init; }
    public bool IsCompatMode { get; init; }
    public bool Network { get; init; }
    public bool Tracking { get; init; }
    public bool Stubs { get; init; }
    public bool ImaStub { get; init; }
    public bool Popups { get; init; }
    public bool Overlays { get; init; }
    public bool Cosmetic { get; init; }
    public bool AntiWall { get; init; }
    public bool Subframes { get; init; }
    public bool VideoDefuser { get; init; }
    public bool YouTubeAdapter { get; init; }
    public bool DailymotionAdapter { get; init; }

    public bool Adapter => YouTubeAdapter || DailymotionAdapter;
}

/// <summary>
/// Resuelve la política de bloqueo por sitio (Normal, Modo Compatible o Desactivado)
/// y gestiona las excepciones por módulo y limpieza segura de parámetros de rastreo.
/// </summary>
public static class SitePolicy
{
    public const string ModeNormal = "Normal";
    public const string ModeCompat = "Compat";
    public const string ModeOff = "Off";

    public const string ModNetwork = "Network";
    public const string ModTracking = "Tracking";
    public const string ModStubs = "Stubs";
    public const string ModPopups = "Popups";
    public const string ModOverlays = "Overlays";
    public const string ModCosmetic = "Cosmetic";
    public const string ModCookieBanners = "CookieBanners";
    public const string ModAntiWall = "AntiWall";
    public const string ModSubframes = "Subframes";
    public const string ModVideoDefuser = "VideoDefuser";
    public const string ModAdapter = "Adapter";

    private static readonly HashSet<string> TrackingQueryParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content", "utm_id",
        "fbclid", "gclid", "dclid", "msclkid", "twclid", "yclid",
        "mc_eid", "_hsenc", "_hsmi", "mkt_tok", "igshid"
    };

    private static readonly HashSet<string> SensitiveAuthOrSignedParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "code", "state", "id_token", "access_token", "refresh_token", "token",
        "sig", "signature", "x-amz-signature", "x-goog-signature", "samlrequest", "samlresponse"
    };

    /// <summary>
    /// Calcula la política efectiva aplicable a una URI o host, combinando la configuración
    /// global, el perfil guardado del dominio y el estado temporal de modo compatible de la pestaña.
    /// </summary>
    public static EffectiveSitePolicy Resolve(Uri? uri, bool temporaryCompatMode = false)
        => Resolve(uri?.Host, temporaryCompatMode);

    public static EffectiveSitePolicy Resolve(string? host, bool temporaryCompatMode = false)
    {
        var appSettings = SettingsManager.Current;
        var shield = appSettings?.Shield ?? new ShieldSettings();
        bool masterEnabled = (appSettings?.EnableAdBlocker ?? true) && shield.Enabled;

        string regDomain = PublicSuffix.GetRegistrableDomain(host);
        SiteShieldProfile? siteProfile = null;
        if (!string.IsNullOrEmpty(regDomain) && shield.Sites != null)
        {
            shield.Sites.TryGetValue(regDomain, out siteProfile);
        }

        string mode = siteProfile?.Mode ?? ModeNormal;
        if (temporaryCompatMode && string.Equals(mode, ModeNormal, StringComparison.OrdinalIgnoreCase))
        {
            mode = ModeCompat;
        }

        if (!masterEnabled || string.Equals(mode, ModeOff, StringComparison.OrdinalIgnoreCase))
        {
            return new EffectiveSitePolicy
            {
                RegistrableDomain = regDomain,
                Mode = masterEnabled ? ModeOff : ModeNormal,
                ShieldEnabled = false,
                IsCompatMode = false,
                Network = false,
                Tracking = false,
                Stubs = false,
                ImaStub = false,
                Popups = false,
                Overlays = false,
                Cosmetic = false,
                AntiWall = false,
                Subframes = false,
                VideoDefuser = false,
                YouTubeAdapter = false,
                DailymotionAdapter = false
            };
        }

        bool isCompat = string.Equals(mode, ModeCompat, StringComparison.OrdinalIgnoreCase);
        bool IsModDisabled(string mod) =>
            siteProfile?.Disabled != null &&
            siteProfile.Disabled.Exists(d => string.Equals(d, mod, StringComparison.OrdinalIgnoreCase));

        bool network = shield.Modules.Network && !IsModDisabled(ModNetwork);
        bool tracking = shield.Modules.Tracking && !IsModDisabled(ModTracking);
        bool popups = shield.Modules.Popups && !IsModDisabled(ModPopups);

        // En Modo Compatible se mantiene bloqueo de red, rastreadores y popups maliciosos,
        // pero se apagan los reemplazos IMA/VAST, interceptores de clics, CSS cosmético e inyección en subframes.
        bool stubs = !isCompat && shield.Modules.Stubs && !IsModDisabled(ModStubs);
        bool imaStub = !isCompat && stubs && (appSettings?.EnableImaSdkStub ?? true) && shield.Adapters.ImaStub;
        bool overlays = !isCompat && shield.Modules.Overlays && !IsModDisabled(ModOverlays);
        bool cosmetic = !isCompat && (appSettings?.EnableCosmeticShield ?? true) && shield.Modules.Cosmetic && !IsModDisabled(ModCosmetic);
        bool antiWall = !isCompat && shield.Modules.AntiWall && !IsModDisabled(ModAntiWall);
        bool subframes = !isCompat && (appSettings?.EnableWebShieldInSubframes ?? false) && shield.Modules.Subframes && !IsModDisabled(ModSubframes);
        bool videoDefuser = !isCompat && (appSettings?.EnableVideoPlayerDefuser ?? false) && shield.Modules.VideoDefuser && !IsModDisabled(ModVideoDefuser);

        bool adapterEnabled = !isCompat && !IsModDisabled(ModAdapter);
        bool ytAdapter = adapterEnabled && (appSettings?.EnableYouTubeAdBlock ?? true) && shield.Adapters.YouTube;
        bool dmAdapter = adapterEnabled && (appSettings?.EnableDailymotionAdBlock ?? true) && shield.Adapters.Dailymotion;

        return new EffectiveSitePolicy
        {
            RegistrableDomain = regDomain,
            Mode = isCompat ? ModeCompat : ModeNormal,
            ShieldEnabled = true,
            IsCompatMode = isCompat,
            Network = network,
            Tracking = tracking,
            Stubs = stubs,
            ImaStub = imaStub,
            Popups = popups,
            Overlays = overlays,
            Cosmetic = cosmetic,
            AntiWall = antiWall,
            Subframes = subframes,
            VideoDefuser = videoDefuser,
            YouTubeAdapter = ytAdapter,
            DailymotionAdapter = dmAdapter
        };
    }

    public static SiteShieldProfile GetOrCreateSiteProfile(string? host)
    {
        string regDomain = PublicSuffix.GetRegistrableDomain(host);
        if (string.IsNullOrEmpty(regDomain))
            return new SiteShieldProfile();

        var shield = SettingsManager.Current.Shield;
        shield.Sites ??= new Dictionary<string, SiteShieldProfile>(StringComparer.OrdinalIgnoreCase);

        if (!shield.Sites.TryGetValue(regDomain, out var profile) || profile == null)
        {
            profile = new SiteShieldProfile();
            shield.Sites[regDomain] = profile;
        }
        profile.Disabled ??= new List<string>();
        return profile;
    }

    public static void SetSiteMode(string? host, string mode, bool save = true)
    {
        string regDomain = PublicSuffix.GetRegistrableDomain(host);
        if (string.IsNullOrEmpty(regDomain))
            return;

        var profile = GetOrCreateSiteProfile(regDomain);
        profile.Mode = mode;

        CleanupDefaultProfileIfNeeded(regDomain, profile);
        if (save) SettingsManager.Save();
    }

    public static void SetSiteModuleEnabled(string? host, string moduleName, bool enabled, bool save = true)
    {
        string regDomain = PublicSuffix.GetRegistrableDomain(host);
        if (string.IsNullOrEmpty(regDomain) || string.IsNullOrWhiteSpace(moduleName))
            return;

        var profile = GetOrCreateSiteProfile(regDomain);
        profile.Disabled.RemoveAll(m => string.Equals(m, moduleName, StringComparison.OrdinalIgnoreCase));
        if (!enabled)
        {
            profile.Disabled.Add(moduleName);
        }

        // Si el usuario activa explícitamente el desarmador de video en este sitio, aseguramos que pueda ejecutarse
        if (enabled && string.Equals(moduleName, ModVideoDefuser, StringComparison.OrdinalIgnoreCase))
        {
            SettingsManager.Current.EnableVideoPlayerDefuser = true;
            SettingsManager.Current.Shield.Modules.VideoDefuser = true;
        }

        CleanupDefaultProfileIfNeeded(regDomain, profile);
        if (save) SettingsManager.Save();
    }

    public static void ResetSitePolicy(string? host, bool save = true)
    {
        string regDomain = PublicSuffix.GetRegistrableDomain(host);
        if (string.IsNullOrEmpty(regDomain))
            return;

        var shield = SettingsManager.Current.Shield;
        if (shield.Sites != null && shield.Sites.Remove(regDomain) && save)
        {
            SettingsManager.Save();
        }
    }

    private static void CleanupDefaultProfileIfNeeded(string regDomain, SiteShieldProfile profile)
    {
        if (string.Equals(profile.Mode, ModeNormal, StringComparison.OrdinalIgnoreCase) &&
            (profile.Disabled == null || profile.Disabled.Count == 0))
        {
            SettingsManager.Current.Shield.Sites?.Remove(regDomain);
        }
    }

    /// <summary>
    /// Limpia parámetros de rastreo conocidos (utm_*, fbclid, gclid, etc.) de una URL de navegación
    /// sin alterar flujos OAuth, URLs firmadas ni dominios de confianza.
    /// </summary>
    public static bool TryStripTrackingParameters(Uri? uri, out string cleanedUrl)
    {
        cleanedUrl = string.Empty;
        if (uri == null || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return false;

        string query = uri.Query;
        if (string.IsNullOrEmpty(query) || query.Length <= 1)
            return false;

        var policy = Resolve(uri.Host);
        if (!policy.ShieldEnabled || !policy.Tracking)
            return false;

        // No tocar flujos de autenticación ni dominios críticos de confianza
        string pathLower = uri.AbsolutePath.ToLowerInvariant();
        if (pathLower.Contains("/oauth") || pathLower.Contains("/auth") ||
            pathLower.Contains("/login") || pathLower.Contains("/signin") ||
            pathLower.Contains("/sso") || pathLower.Contains("/callback"))
        {
            return false;
        }

        string rawQuery = query.Substring(1);
        string[] pairs = rawQuery.Split('&', StringSplitOptions.RemoveEmptyEntries);
        if (pairs.Length == 0)
            return false;

        var keptPairs = new List<string>(pairs.Length);
        bool removedAny = false;

        foreach (string pair in pairs)
        {
            int eqIdx = pair.IndexOf('=');
            string key = eqIdx >= 0 ? pair.Substring(0, eqIdx) : pair;
            string decodedKey = Uri.UnescapeDataString(key);

            if (SensitiveAuthOrSignedParams.Contains(decodedKey))
                return false;

            if (TrackingQueryParams.Contains(decodedKey))
            {
                removedAny = true;
                continue;
            }

            keptPairs.Add(pair);
        }

        if (!removedAny)
            return false;

        var builder = new UriBuilder(uri)
        {
            Query = keptPairs.Count > 0 ? string.Join("&", keptPairs) : string.Empty
        };
        cleanedUrl = builder.Uri.AbsoluteUri;
        return !string.Equals(cleanedUrl, uri.AbsoluteUri, StringComparison.Ordinal);
    }
}
