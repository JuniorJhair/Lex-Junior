using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LexBrowser.Core.Tv.Vod.Contracts;

namespace LexBrowser.Core.Tv.Vod.Metadata;

public interface IMetaService
{
    string ServiceId { get; }
    string ServiceName { get; }
    bool IsConfigured { get; }

    Task<List<MediaItem>> DiscoverAsync(string type, string? categoryOrStudio, int? year, string? genre, int page, CancellationToken ct);

    Task<MediaItem?> GetDetailsAsync(string id, CancellationToken ct);
}

