using System;
using System.Collections.Generic;

namespace LexBrowser.Core;

/// <summary>
/// Valores exactos de las opciones que integran un perfil de privacidad y seguridad.
/// La fuente de verdad son siempre las propiedades individuales de BrowserSettings.
/// </summary>
public readonly record struct PrivacyProfileValues(
    bool EnableSmartScreen,
    bool EnableClientSidePhishingDetection,
    bool EnableLinkFaviconDownload,
    bool EnableDirectFaviconIco,
    bool EnableExternalFaviconService,
    bool EnableSearchSuggestions,
    string AutoUpdateCheckMode)
{
    public bool EqualsProfile(PrivacyProfileValues other) =>
        EnableSmartScreen == other.EnableSmartScreen &&
        EnableClientSidePhishingDetection == other.EnableClientSidePhishingDetection &&
        EnableLinkFaviconDownload == other.EnableLinkFaviconDownload &&
        EnableDirectFaviconIco == other.EnableDirectFaviconIco &&
        EnableExternalFaviconService == other.EnableExternalFaviconService &&
        EnableSearchSuggestions == other.EnableSearchSuggestions &&
        string.Equals(
            PrivacyProfiles.NormalizeAutoUpdateCheckMode(AutoUpdateCheckMode),
            PrivacyProfiles.NormalizeAutoUpdateCheckMode(other.AutoUpdateCheckMode),
            StringComparison.Ordinal);
}

public enum PrivacyOptionKind
{
    Toggle,
    InformationalAlwaysActive,
    AutoUpdateRadio
}

/// <summary>
/// Metadatos compartidos de cada fila entre la ventana inicial y Configuración -> Privacidad y seguridad.
/// Garantiza que ambas vistas muestren exactamente las mismas opciones, explicaciones y avisos.
/// </summary>
public sealed record PrivacyOptionDescriptor(
    string Id,
    string Title,
    string ShortSummary,
    string TableLabel,
    string Advantage,
    string Disadvantage,
    PrivacyOptionKind Kind,
    bool RequiresEngineRestart,
    string ValueA,
    string ValueB,
    string ValueC);

/// <summary>
/// Catálogo centralizado y funciones puras para los perfiles A (Privacidad estricta),
/// B (Equilibrado) y C (Completo), así como la deducción del estado «Personalizado».
/// </summary>
public static class PrivacyProfiles
{
    public const string ProfileKeyA = "A";
    public const string ProfileKeyB = "B";
    public const string ProfileKeyC = "C";
    public const string ProfileKeyCustom = "Personalizado";

    public const string AutoUpdateManual = "Manual";
    public const string AutoUpdateOnStartup = "OnStartup";

    public const int CurrentOnboardingVersion = 1;

    /// <summary>
    /// Constante única de la matriz para el Perfil A (Privacidad estricta).
    /// </summary>
    public static readonly PrivacyProfileValues ProfileA = new(
        EnableSmartScreen: false,
        EnableClientSidePhishingDetection: false,
        EnableLinkFaviconDownload: false,
        EnableDirectFaviconIco: false,
        EnableExternalFaviconService: false,
        EnableSearchSuggestions: false,
        AutoUpdateCheckMode: AutoUpdateManual);

    /// <summary>
    /// Constante única de la matriz para el Perfil B (Equilibrado — recomendado).
    /// </summary>
    public static readonly PrivacyProfileValues ProfileB = new(
        EnableSmartScreen: true,
        EnableClientSidePhishingDetection: false,
        EnableLinkFaviconDownload: true,
        EnableDirectFaviconIco: true,
        EnableExternalFaviconService: false,
        EnableSearchSuggestions: false,
        AutoUpdateCheckMode: AutoUpdateOnStartup);

    /// <summary>
    /// Constante única de la matriz para el Perfil C (Completo).
    /// </summary>
    public static readonly PrivacyProfileValues ProfileC = new(
        EnableSmartScreen: true,
        EnableClientSidePhishingDetection: true,
        EnableLinkFaviconDownload: true,
        EnableDirectFaviconIco: true,
        EnableExternalFaviconService: true,
        EnableSearchSuggestions: true,
        AutoUpdateCheckMode: AutoUpdateOnStartup);

    /// <summary>
    /// Definición única compartida de las 8 filas de la matriz (7 configurables + 1 informativa fija).
    /// </summary>
    public static IReadOnlyList<PrivacyOptionDescriptor> OptionDescriptors { get; } = new[]
    {
        new PrivacyOptionDescriptor(
            Id: "SmartScreen",
            Title: "SmartScreen (Microsoft: páginas y descargas)",
            ShortSummary: "Habilita la protección de Microsoft SmartScreen en el motor del navegador para revisar páginas y descargas. Que el componente WebView2 realice comprobaciones en red depende de su configuración en tu equipo.",
            TableLabel: "SmartScreen (Microsoft)",
            Advantage: "Deja habilitada la opción en el motor WebView2 para revisión de páginas y descargas si el runtime la aplica.",
            Disadvantage: "Al depender de Microsoft WebView2, puede enviar consultas de URL/descargas a Microsoft; cambiarlo con el navegador ya abierto requiere reiniciar.",
            Kind: PrivacyOptionKind.Toggle,
            RequiresEngineRestart: true,
            ValueA: "Desactivado",
            ValueB: "Activado",
            ValueC: "Activado"),

        new PrivacyOptionDescriptor(
            Id: "ClientSidePhishing",
            Title: "Detección de phishing en el navegador",
            ShortSummary: "Activa la detección integrada del navegador contra páginas de suplantación (phishing). Su funcionamiento efectivo depende del motor WebView2 de Microsoft Edge instalado en tu equipo.",
            TableLabel: "Detección de phishing",
            Advantage: "Deja sin desactivar la detección de suplantación integrada de Chromium/Edge si el runtime la ejecuta.",
            Disadvantage: "El motor puede descargar modelos o enviar avisos de phishing a Google/Microsoft; cambiarlo con el navegador ya abierto requiere reiniciar.",
            Kind: PrivacyOptionKind.Toggle,
            RequiresEngineRestart: true,
            ValueA: "Desactivado",
            ValueB: "Desactivado",
            ValueC: "Activado"),

        new PrivacyOptionDescriptor(
            Id: "NativePageFaviconStream",
            Title: "Favicons propios entregados por la página (WebView2 / data:)",
            ShortSummary: "Usa únicamente la imagen de icono que la propia pestaña ya cargó dentro de su navegación normal en WebView2 o incrustada como data URI.",
            TableLabel: "Favicons nativos de la página",
            Advantage: "Muestra el icono de pestañas abiertas usando la sesión de la propia web sin peticiones extra desde C#.",
            Disadvantage: "En marcadores, historial o sugerencias aún no cargados en esta sesión, verás el icono genérico o inicial si apagas las demás fuentes.",
            Kind: PrivacyOptionKind.InformationalAlwaysActive,
            RequiresEngineRestart: false,
            ValueA: "Activo",
            ValueB: "Activo",
            ValueC: "Activo"),

        new PrivacyOptionDescriptor(
            Id: "LinkFaviconDownload",
            Title: "Favicon <link rel=\"icon\"> descargado desde C#",
            ShortSummary: "Descarga el enlace de icono declarado en el HTML de la página directamente desde su servidor público con protección anti-SSRF y límite de tamaño.",
            TableLabel: "Favicon <link> (desde C#)",
            Advantage: "Recupera iconos de alta nitidez declarados por la propia web sin pasar por terceros.",
            Disadvantage: "Realiza una petición HTTP(S) desde C# hacia el servidor del icono declarado por la página.",
            Kind: PrivacyOptionKind.Toggle,
            RequiresEngineRestart: false,
            ValueA: "Desactivado",
            ValueB: "Activado",
            ValueC: "Activado"),

        new PrivacyOptionDescriptor(
            Id: "DirectFaviconIco",
            Title: "/favicon.ico directo del sitio",
            ShortSummary: "Solicita https://{dominio}/favicon.ico directamente al servidor del sitio cuando no hay icono en caché RAM (por ejemplo en barra de marcadores).",
            TableLabel: "/favicon.ico directo",
            Advantage: "Muestra el icono de sitios visitados o marcados comunicándose únicamente con el propio dominio.",
            Disadvantage: "Cada sitio sin icono en memoria recibe una petición directa hacia /favicon.ico en tu primera consulta de la sesión.",
            Kind: PrivacyOptionKind.Toggle,
            RequiresEngineRestart: false,
            ValueA: "Desactivado",
            ValueB: "Activado",
            ValueC: "Activado"),

        new PrivacyOptionDescriptor(
            Id: "GoogleFaviconApi",
            Title: "Google Favicon API (servicio externo)",
            ShortSummary: "Si el sitio no entregó icono por los métodos anteriores, consulta el nombre de dominio en https://www.google.com/s2/favicons.",
            TableLabel: "Google Favicon API",
            Advantage: "Mayor cobertura visual de iconos incluso en páginas que bloquean o no ofrecen /favicon.ico.",
            Disadvantage: "Envía el nombre de dominio (sin ruta ni parámetros) a Google cuando falta su icono en caché RAM.",
            Kind: PrivacyOptionKind.Toggle,
            RequiresEngineRestart: false,
            ValueA: "Desactivado",
            ValueB: "Desactivado",
            ValueC: "Activado"),

        new PrivacyOptionDescriptor(
            Id: "SearchSuggestions",
            Title: "Sugerencias de búsqueda en vivo",
            ShortSummary: "Envía lo que escribes en la barra de direcciones (cuando no es una URL ni dominio) al buscador elegido para mostrar predicciones en tiempo real.",
            TableLabel: "Sugerencias de búsqueda",
            Advantage: "Autocompleta búsquedas en vivo desde la barra de direcciones; nunca se activa en Incógnito ni Invitado.",
            Disadvantage: "Lo que tecleas en búsquedas se envía tecla a tecla (tras pausa breve) al proveedor de búsqueda seleccionado.",
            Kind: PrivacyOptionKind.Toggle,
            RequiresEngineRestart: false,
            ValueA: "Desactivado",
            ValueB: "Desactivado",
            ValueC: "Activado"),

        new PrivacyOptionDescriptor(
            Id: "AutoUpdates",
            Title: "Búsqueda de actualizaciones",
            ShortSummary: "Comprueba en GitHub Releases si existe una versión nueva de Lex Junior. Incluso al iniciar, solo avisa: descargar e instalar siempre exige tu confirmación.",
            TableLabel: "Búsqueda de actualizaciones",
            Advantage: "Te avisa de nuevas versiones y parches verificados por SHA-256 sin instalar nada sin permiso.",
            Disadvantage: "En modo automático realiza una consulta a GitHub al abrir el navegador; en Manual debes pulsar «Buscar actualizaciones ahora».",
            Kind: PrivacyOptionKind.AutoUpdateRadio,
            RequiresEngineRestart: false,
            ValueA: "Manual",
            ValueB: "Al iniciar",
            ValueC: "Al iniciar")
    };

    public static string NormalizeAutoUpdateCheckMode(string? mode)
    {
        if (string.Equals(mode?.Trim(), AutoUpdateManual, StringComparison.OrdinalIgnoreCase))
            return AutoUpdateManual;

        return AutoUpdateOnStartup;
    }

    public static PrivacyProfileValues FromSettings(BrowserSettings? settings)
    {
        settings ??= new BrowserSettings();
        return new PrivacyProfileValues(
            EnableSmartScreen: settings.EnableSmartScreen,
            EnableClientSidePhishingDetection: settings.EnableClientSidePhishingDetection,
            EnableLinkFaviconDownload: settings.EnableLinkFaviconDownload,
            EnableDirectFaviconIco: settings.EnableDirectFaviconIco,
            EnableExternalFaviconService: settings.EnableExternalFaviconService,
            EnableSearchSuggestions: settings.EnableSearchSuggestions,
            AutoUpdateCheckMode: NormalizeAutoUpdateCheckMode(settings.AutoUpdateCheckMode));
    }

    public static PrivacyProfileValues GetValuesForProfileKey(string? profileKey) => profileKey switch
    {
        ProfileKeyA => ProfileA,
        ProfileKeyB => ProfileB,
        ProfileKeyC => ProfileC,
        _ => ProfileB
    };

    /// <summary>
    /// Deduce el perfil activo comparando los valores individuales contra las tres matrices A, B y C.
    /// Si no coincide exactamente con ninguna, devuelve «Personalizado».
    /// </summary>
    public static string ResolveActiveProfile(BrowserSettings? settings) =>
        ResolveActiveProfile(FromSettings(settings));

    public static string ResolveActiveProfile(PrivacyProfileValues values)
    {
        if (values.EqualsProfile(ProfileA)) return ProfileKeyA;
        if (values.EqualsProfile(ProfileB)) return ProfileKeyB;
        if (values.EqualsProfile(ProfileC)) return ProfileKeyC;
        return ProfileKeyCustom;
    }

    public static string GetProfileDisplayName(string? profileKey) => profileKey switch
    {
        ProfileKeyA => "Perfil A — Privacidad estricta",
        ProfileKeyB => "Perfil B — Equilibrado",
        ProfileKeyC => "Perfil C — Completo",
        _ => ProfileKeyCustom
    };

    public static void ApplyValuesToSettings(BrowserSettings settings, PrivacyProfileValues values)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.EnableSmartScreen = values.EnableSmartScreen;
        settings.EnableClientSidePhishingDetection = values.EnableClientSidePhishingDetection;
        settings.EnableLinkFaviconDownload = values.EnableLinkFaviconDownload;
        settings.EnableDirectFaviconIco = values.EnableDirectFaviconIco;
        settings.EnableExternalFaviconService = values.EnableExternalFaviconService;
        settings.EnableSearchSuggestions = values.EnableSearchSuggestions;
        settings.AutoUpdateCheckMode = NormalizeAutoUpdateCheckMode(values.AutoUpdateCheckMode);
    }

    public static void ApplyPredefinedProfile(BrowserSettings settings, string profileKey)
    {
        ApplyValuesToSettings(settings, GetValuesForProfileKey(profileKey));
    }

    /// <summary>
    /// Resuelve el cierre de la ventana inicial cuando el usuario la cierra sin confirmar elección (X, Esc, Alt+F4).
    /// - Instalación nueva: aplica Perfil B, marca resuelto, descarta cualquier borrador sin confirmar.
    /// - Usuario que actualiza: NO cambia ninguna preferencia previa, marca resuelto.
    /// </summary>
    public static bool ResolveClosedWithoutChoice(BrowserSettings settings, bool isFreshInstall)
    {
        ArgumentNullException.ThrowIfNull(settings);
        bool modifiedProfile = false;
        if (isFreshInstall)
        {
            ApplyPredefinedProfile(settings, ProfileKeyB);
            modifiedProfile = true;
        }

        settings.PrivacyProfileOnboardingVersion = Math.Max(CurrentOnboardingVersion, settings.PrivacyProfileOnboardingVersion);
        settings.FreshInstallOnboardingPending = false;
        return modifiedProfile;
    }

    /// <summary>
    /// Confirma la elección explícita de la ventana inicial y marca el onboarding como resuelto.
    /// </summary>
    public static void ConfirmOnboardingSelection(
        BrowserSettings settings,
        PrivacyProfileValues? selectedValuesToApply)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (selectedValuesToApply.HasValue)
        {
            ApplyValuesToSettings(settings, selectedValuesToApply.Value);
        }

        settings.PrivacyProfileOnboardingVersion = Math.Max(CurrentOnboardingVersion, settings.PrivacyProfileOnboardingVersion);
        settings.FreshInstallOnboardingPending = false;
    }

    public static string GetOptionValueLabel(string optionId, PrivacyProfileValues values) => optionId switch
    {
        "SmartScreen" => values.EnableSmartScreen ? "Activado" : "Desactivado",
        "ClientSidePhishing" => values.EnableClientSidePhishingDetection ? "Activado" : "Desactivado",
        "NativePageFaviconStream" => "Activo",
        "LinkFaviconDownload" => values.EnableLinkFaviconDownload ? "Activado" : "Desactivado",
        "DirectFaviconIco" => values.EnableDirectFaviconIco ? "Activado" : "Desactivado",
        "GoogleFaviconApi" => values.EnableExternalFaviconService ? "Activado" : "Desactivado",
        "SearchSuggestions" => values.EnableSearchSuggestions ? "Activado" : "Desactivado",
        "AutoUpdates" => string.Equals(
            NormalizeAutoUpdateCheckMode(values.AutoUpdateCheckMode),
            AutoUpdateManual,
            StringComparison.Ordinal) ? "Manual" : "Al iniciar",
        _ => "—"
    };

    public static bool IsOptionEnabledInValue(string optionId, PrivacyProfileValues values) => optionId switch
    {
        "SmartScreen" => values.EnableSmartScreen,
        "ClientSidePhishing" => values.EnableClientSidePhishingDetection,
        "NativePageFaviconStream" => true,
        "LinkFaviconDownload" => values.EnableLinkFaviconDownload,
        "DirectFaviconIco" => values.EnableDirectFaviconIco,
        "GoogleFaviconApi" => values.EnableExternalFaviconService,
        "SearchSuggestions" => values.EnableSearchSuggestions,
        "AutoUpdates" => !string.Equals(NormalizeAutoUpdateCheckMode(values.AutoUpdateCheckMode), AutoUpdateManual, StringComparison.Ordinal),
        _ => false
    };

    public static PrivacyProfileValues WithOptionUpdated(PrivacyProfileValues current, string optionId, bool enabled, string? autoUpdateMode = null) => optionId switch
    {
        "SmartScreen" => current with { EnableSmartScreen = enabled },
        "ClientSidePhishing" => current with { EnableClientSidePhishingDetection = enabled },
        "LinkFaviconDownload" => current with { EnableLinkFaviconDownload = enabled },
        "DirectFaviconIco" => current with { EnableDirectFaviconIco = enabled },
        "GoogleFaviconApi" => current with { EnableExternalFaviconService = enabled },
        "SearchSuggestions" => current with { EnableSearchSuggestions = enabled },
        "AutoUpdates" => current with
        {
            AutoUpdateCheckMode = autoUpdateMode != null
                ? NormalizeAutoUpdateCheckMode(autoUpdateMode)
                : (enabled ? AutoUpdateOnStartup : AutoUpdateManual)
        },
        _ => current
    };

    public static IReadOnlyList<string> GetCoherenceNotices(PrivacyProfileValues values)
    {
        var notices = new List<string>();

        if (!values.EnableLinkFaviconDownload && !values.EnableDirectFaviconIco && !values.EnableExternalFaviconService)
        {
            notices.Add("Favicon <link> y /favicon.ico desactivados con Google Favicon API desactivado: solo verás los iconos que la propia página ya entregó o la letra inicial.");
        }

        if (string.Equals(NormalizeAutoUpdateCheckMode(values.AutoUpdateCheckMode), AutoUpdateManual, StringComparison.Ordinal))
        {
            notices.Add("Búsqueda de actualizaciones en manual: usa el botón «Buscar actualizaciones ahora» en Configuración para no quedarte desactualizado.");
        }

        return notices;
    }

    public static IReadOnlyList<string> DescribeChangesBetween(PrivacyProfileValues from, PrivacyProfileValues to)
    {
        var changes = new List<string>();
        foreach (var desc in OptionDescriptors)
        {
            if (desc.Kind == PrivacyOptionKind.InformationalAlwaysActive)
                continue;

            string oldVal = GetOptionValueLabel(desc.Id, from);
            string newVal = GetOptionValueLabel(desc.Id, to);
            if (!string.Equals(oldVal, newVal, StringComparison.Ordinal))
            {
                changes.Add($"• {desc.TableLabel}: {oldVal} → {newVal}");
            }
        }
        return changes;
    }
}
