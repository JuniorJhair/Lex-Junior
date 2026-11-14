using System;
using System.Collections.Generic;

namespace LexBrowser.Core.Tv.Addons;

/// <summary>
/// Declaración normalizada de un catálogo expuesto por un addon (compatible con Lex Addons y protocolo Stremio).
/// </summary>
public sealed class LexAddonCatalogDecl
{
    public string Type { get; set; } = "";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<LexAddonExtraDecl> Extra { get; set; } = new();
}

/// <summary>
/// Parámetro extra soportado por un catálogo (ej. search, genre, skip).
/// </summary>
public sealed class LexAddonExtraDecl
{
    public string Name { get; set; } = "";
    public bool IsRequired { get; set; }
    public List<string> Options { get; set; } = new();
}

/// <summary>
/// Declaración de recurso soportado por el addon (catalog, meta, stream, subtitles).
/// </summary>
public sealed class LexAddonResourceDecl
{
    public string Name { get; set; } = "";
    public List<string> Types { get; set; } = new();
    public List<string> IdPrefixes { get; set; } = new();
}

/// <summary>
/// Indicaciones de comportamiento del manifiesto.
/// </summary>
public sealed class LexAddonBehaviorHints
{
    public bool Configurable { get; set; }
    public bool ConfigurationRequired { get; set; }
    public bool Adult { get; set; }
}

/// <summary>
/// Manifiesto validado de un addon HTTP/JSON (compatible con Lex v1 y protocolo Stremio).
/// </summary>
public sealed class LexAddonManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = "";
    public string? Logo { get; set; }
    public string? Background { get; set; }
    public string Protocol { get; set; } = "stremio-v1";
    public List<string> Types { get; set; } = new();
    public List<LexAddonResourceDecl> Resources { get; set; } = new();
    public List<LexAddonCatalogDecl> Catalogs { get; set; } = new();
    public List<string> IdPrefixes { get; set; } = new();
    public LexAddonBehaviorHints BehaviorHints { get; set; } = new();
}

/// <summary>
/// Registro persistido en disco de un addon instalado.
/// La URL real del manifiesto se cifra con Windows DPAPI para proteger tokens embebidos.
/// </summary>
public sealed class LexInstalledAddon
{
    public string Id { get; set; } = "";
    public LexAddonManifest Manifest { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public bool AllowLocalLoopback { get; set; }
    public string? ProtectedManifestUrl { get; set; }
    public string DisplayUrl { get; set; } = "";
    public DateTime InstalledUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? LastCheckUtc { get; set; }
    public string? LastError { get; set; }
}

/// <summary>
/// Contenedor persistido en addons_registry.json.
/// </summary>
public sealed class LexAddonRegistryData
{
    public int SchemaVersion { get; set; } = 1;
    public List<LexInstalledAddon> Addons { get; set; } = new();
    public List<LexAddonRepositoryRecord> Repositories { get; set; } = new();
}

/// <summary>
/// Vista segura para interfaz web (sin URLs protegidas ni secretos).
/// </summary>
public sealed class LexAddonSummary
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Logo { get; set; }
    public string Protocol { get; set; } = "";
    public List<string> Types { get; set; } = new();
    public List<string> ResourceNames { get; set; } = new();
    public int CatalogCount { get; set; }
    public List<LexAddonCatalogDecl> Catalogs { get; set; } = new();
    public bool Enabled { get; set; }
    public bool AllowLocalLoopback { get; set; }
    public bool ConfigurationRequired { get; set; }
    public bool Configurable { get; set; }
    public string DisplayUrl { get; set; } = "";
    public DateTime InstalledUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? LastCheckUtc { get; set; }
    public string? LastError { get; set; }
}

public sealed record LexAddonManifestResult(
    bool Success,
    LexAddonManifest? Manifest = null,
    string? CanonicalUrl = null,
    string? DisplayUrl = null,
    string? ErrorMessage = null);

public sealed record LexAddonActionResult(
    bool Success,
    LexAddonSummary? Addon = null,
    List<LexAddonSummary>? Addons = null,
    bool Updated = false,
    string? ErrorMessage = null);

// =========================================================================
// Fase 3: Contratos de Catálogos y Metadatos (Películas, Series y Anime)
// =========================================================================

public sealed class LexAddonCatalogDescriptor
{
    public string AddonId { get; set; } = "";
    public string AddonName { get; set; } = "";
    public string CatalogId { get; set; } = "";
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public bool HasSearch { get; set; }
    public bool SearchRequired { get; set; }
    public bool HasSkip { get; set; }
    public bool SupportsMeta { get; set; }
    public List<string> Genres { get; set; } = new();
}

public sealed class LexAddonMetaPreview
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Poster { get; set; }
    public string PosterShape { get; set; } = "poster";
    public string? Background { get; set; }
    public string Description { get; set; } = "";
    public string ReleaseInfo { get; set; } = "";
    public string ImdbRating { get; set; } = "";
    public List<string> Genres { get; set; } = new();
    public string AddonId { get; set; } = "";
    public string AddonName { get; set; } = "";
}

public sealed class LexAddonVideoItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public int Season { get; set; } = 1;
    public int Episode { get; set; }
    public string Released { get; set; } = "";
    public string? Thumbnail { get; set; }
    public string Overview { get; set; } = "";
}

public sealed class LexAddonMetaDetail
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Poster { get; set; }
    public string? Background { get; set; }
    public string Description { get; set; } = "";
    public string ReleaseInfo { get; set; } = "";
    public string ImdbRating { get; set; } = "";
    public string Runtime { get; set; } = "";
    public List<string> Genres { get; set; } = new();
    public List<string> Director { get; set; } = new();
    public List<string> Cast { get; set; } = new();
    public string DefaultVideoId { get; set; } = "";
    public List<LexAddonVideoItem> Videos { get; set; } = new();
    public string AddonId { get; set; } = "";
    public string AddonName { get; set; } = "";
}

public sealed record LexAddonCatalogResult(
    bool Success,
    List<LexAddonMetaPreview>? Items = null,
    bool HasMore = false,
    int NextSkip = 0,
    string? AddonId = null,
    string? AddonName = null,
    string? CatalogName = null,
    string? ErrorMessage = null);

public sealed record LexAddonMetaResult(
    bool Success,
    LexAddonMetaDetail? Meta = null,
    string? ErrorMessage = null);

// =========================================================================
// Fase 4: Contratos de Resolución de Streams (/stream/{type}/{id}.json)
// =========================================================================

public sealed class LexAddonSubtitleDecl
{
    public string Id { get; set; } = "";
    public string Lang { get; set; } = "";
    public string Label { get; set; } = "";
    public string ProxyUrl { get; set; } = "";
}

public sealed class LexAddonStreamOption
{
    public string AddonId { get; set; } = "";
    public string AddonName { get; set; } = "";
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string StreamUrl { get; set; } = "";
    public string ExternalUrl { get; set; } = "";
    public string ProxyUrl { get; set; } = "";
    public string StreamKind { get; set; } = "http";
    public bool Playable { get; set; }
    public string? DisabledReason { get; set; }
    public bool NotWebReady { get; set; }
    public string BingeGroup { get; set; } = "";
    public string Filename { get; set; } = "";
    public long? VideoSize { get; set; }
    public List<LexAddonSubtitleDecl> Subtitles { get; set; } = new();
}

public sealed record LexAddonStreamsResult(
    bool Success,
    List<LexAddonStreamOption>? Streams = null,
    int QueriedAddonsCount = 0,
    List<string>? AddonErrors = null,
    string? ErrorMessage = null);

// =========================================================================
// Fase 6: Contratos de Subtítulos Dedicados (/subtitles) y Búsqueda Global
// =========================================================================

public sealed record LexAddonSubtitlesResult(
    bool Success,
    List<LexAddonSubtitleDecl>? Subtitles = null,
    int QueriedAddonsCount = 0,
    List<string>? AddonErrors = null,
    string? ErrorMessage = null);

public sealed record LexAddonGlobalSearchResult(
    bool Success,
    List<LexAddonMetaPreview>? Items = null,
    int SearchedCatalogsCount = 0,
    List<string>? Errors = null,
    string? ErrorMessage = null);

// =========================================================================
// Fase 5: Contratos de Repositorios de Addons y Actualizaciones Masivas
// =========================================================================

public sealed class LexAddonRepositoryRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool AllowLocalLoopback { get; set; }
    public string? ProtectedRepoUrl { get; set; }
    public string DisplayUrl { get; set; } = "";
    public int AddonCount { get; set; }
    public DateTime AddedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public string? LastError { get; set; }
}

public sealed class LexAddonRepositorySummary
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool AllowLocalLoopback { get; set; }
    public string DisplayUrl { get; set; } = "";
    public int AddonCount { get; set; }
    public DateTime AddedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public string? LastError { get; set; }
}

public sealed class LexAddonRepoEntry
{
    public string AddonId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Logo { get; set; }
    public string ManifestUrl { get; set; } = "";
    public string DisplayUrl { get; set; } = "";
    public List<string> Types { get; set; } = new();
    public List<string> ResourceNames { get; set; } = new();
    public bool Installed { get; set; }
    public string? InstalledVersion { get; set; }
    public bool UpdateAvailable { get; set; }
}

public sealed class LexAddonRepoFeed
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<LexAddonRepoEntry> Entries { get; set; } = new();
}

public sealed record LexAddonRepoResult(
    bool Success,
    LexAddonRepositorySummary? Repository = null,
    List<LexAddonRepositorySummary>? Repositories = null,
    List<LexAddonRepoEntry>? Entries = null,
    string? ErrorMessage = null);

public sealed record LexAddonBulkUpdateResult(
    int TotalChecked,
    int UpdatedCount,
    int UpToDateCount,
    int ErrorCount,
    List<string> UpdatedIds,
    List<string> Errors,
    List<LexAddonSummary> Addons);

// =========================================================================
// Fase 7: Contratos de Complementos IPTV (Canales en vivo y EPG)
// =========================================================================

public sealed class LexAddonIptvSourceDescriptor
{
    public string SourceKey { get; set; } = "";
    public string AddonId { get; set; } = "";
    public string AddonName { get; set; } = "";
    public string Type { get; set; } = "tv";
    public string CatalogId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool HasSearch { get; set; }
    public List<string> Genres { get; set; } = new();
}

public sealed class LexAddonIptvChannelAlt
{
    public string Url { get; set; } = "";
    public string Quality { get; set; } = "";
}

public sealed class LexAddonIptvChannel
{
    public string Id { get; set; } = "";
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public string? Logo { get; set; }
    public string Language { get; set; } = "";
    public string Country { get; set; } = "";
    public string Quality { get; set; } = "";
    public string Url { get; set; } = "";
    public List<LexAddonIptvChannelAlt> Alternates { get; set; } = new();
    public string EpgNow { get; set; } = "";
    public string AddonId { get; set; } = "";
    public string AddonName { get; set; } = "";
    public string ItemType { get; set; } = "tv";
    public string ItemId { get; set; } = "";
    public bool IsAddonChannel { get; set; } = true;
}

public sealed record LexAddonIptvChannelsResult(
    bool Success,
    string? SourceKey = null,
    string? SourceName = null,
    List<LexAddonIptvChannel>? Channels = null,
    string? ErrorMessage = null);

