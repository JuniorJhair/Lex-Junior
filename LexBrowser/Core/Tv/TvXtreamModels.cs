using System;
using System.Collections.Generic;

namespace LexBrowser.Core.Tv;

public sealed class TvXtreamConnection
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ServerUrl { get; set; } = "";
    public DateTime AddedUtc { get; set; }
    public bool SaveCredentials { get; set; }

    /// <summary>Usuario cifrado con Windows DPAPI (CurrentUser). Solo presente si SaveCredentials es true.</summary>
    public string? ProtectedUsername { get; set; }

    /// <summary>Contraseña cifrada con Windows DPAPI (CurrentUser). Solo presente si SaveCredentials es true.</summary>
    public string? ProtectedPassword { get; set; }

    public int LiveCount { get; set; }
    public int VodCount { get; set; }
    public int SeriesCount { get; set; }

    public string? ServerStatus { get; set; }
    public string? ExpirationDate { get; set; }
}

public sealed class TvXtreamConnectionSummary
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ServerUrl { get; set; } = "";
    public DateTime AddedUtc { get; set; }
    public bool SaveCredentials { get; set; }
    public int LiveCount { get; set; }
    public int VodCount { get; set; }
    public int SeriesCount { get; set; }
    public string? ServerStatus { get; set; }
    public string? ExpirationDate { get; set; }
}

public sealed class TvXtreamUserInfo
{
    public string Username { get; set; } = "";
    public string Status { get; set; } = "";
    public int Auth { get; set; }
    public string? ExpDate { get; set; }
    public string? ActiveCons { get; set; }
    public string? MaxCons { get; set; }
}

public sealed class TvXtreamServerInfo
{
    public string Url { get; set; } = "";
    public string Port { get; set; } = "";
    public string HttpsPort { get; set; } = "";
    public string ServerProtocol { get; set; } = "";
    public string? Timezone { get; set; }
}

public sealed class TvXtreamAuthResult
{
    public bool Success { get; set; }
    public TvXtreamUserInfo? UserInfo { get; set; }
    public TvXtreamServerInfo? ServerInfo { get; set; }
    public bool IsHttpWarning { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class TvXtreamCredentials
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class TvXtreamCategory
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class TvXtreamLiveStream
{
    public int Num { get; set; }
    public string Name { get; set; } = "";
    public string StreamId { get; set; } = "";
    public string? StreamIcon { get; set; }
    public string? CategoryId { get; set; }
}

public sealed class TvXtreamVodStream
{
    public int Num { get; set; }
    public string Name { get; set; } = "";
    public string StreamId { get; set; } = "";
    public string? StreamIcon { get; set; }
    public string? Extension { get; set; } = "mp4";
    public string? CategoryId { get; set; }
}

public sealed class TvXtreamSeriesItem
{
    public int Num { get; set; }
    public string SeriesId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Cover { get; set; }
    public string? CategoryId { get; set; }
}

public sealed class TvXtreamEpisodeItem
{
    public string Id { get; set; } = "";
    public int Season { get; set; }
    public int EpisodeNum { get; set; }
    public string Title { get; set; } = "";
    public string? Extension { get; set; } = "mp4";
    public string? Cover { get; set; }
}
