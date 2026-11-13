using System.Collections.Generic;

namespace LexBrowser.Core.Tv;

/// <summary>Fuente alternativa de stream para failover automático.</summary>
public sealed class TvStreamAlternate
{
    public string Url { get; set; } = "";
    public string Quality { get; set; } = "";
    public string Referrer { get; set; } = "";
    public string UserAgent { get; set; } = "";
    public int Tier { get; set; } = 3;
    public string Provenance { get; set; } = "iptv-org";
}

/// <summary>Canal de TV en vivo normalizado (API de iptv-org o lista M3U del usuario).</summary>
public sealed class TvChannel
{
    public int Number { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Group { get; set; } = "";
    public string Language { get; set; } = "";
    public string Country { get; set; } = "";
    public string CountryName { get; set; } = "";
    public string Logo { get; set; } = "";
    public string Quality { get; set; } = "";
    public string Referrer { get; set; } = "";
    public string UserAgent { get; set; } = "";
    public string Tags { get; set; } = "";
    public string Source { get; set; } = "";
    public int Tier { get; set; } = 3;
    public string Provenance { get; set; } = "iptv-org";
    public string Kind { get; set; } = "stream"; // "stream" | "external"
    public string ExternalUrl { get; set; } = "";
    public bool IsGeoblocked { get; set; }
    public bool IsSd { get; set; }
    public bool IsYouTube { get; set; }
    public List<TvStreamAlternate> Alternates { get; set; } = new();
}
