using System;
using System.Collections.Generic;

namespace LexBrowser.Core.Tv.Vod.Contracts;

public sealed class VodCatalogDefinition
{
    public string Type { get; set; } = "movie"; // "movie" | "series" | "anime"
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Genres { get; set; } = new();
}

/// <summary>
/// Manifiesto JSON estándar para Add-ons VOD (inspirado en el protocolo Stremio Add-on).
/// Permite extender fuentes de catálogo y streams sin recompilar la aplicación.
/// </summary>
public sealed class VodManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = "";
    public List<string> Resources { get; set; } = new() { "catalog", "stream" }; // "catalog", "meta", "stream", "subtitles"
    public List<string> Types { get; set; } = new() { "movie", "series", "anime" };
    public List<VodCatalogDefinition> Catalogs { get; set; } = new();
    public string? EndpointUrl { get; set; }
    public string? IconUrl { get; set; }
    public bool IsBuiltIn { get; set; }
}

