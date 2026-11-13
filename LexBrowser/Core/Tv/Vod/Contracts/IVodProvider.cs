using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LexBrowser.Core.Tv.Vod.Contracts;

/// <summary>
/// Contrato estándar que debe implementar cualquier proveedor de VOD (interno o Add-on externo).
/// Sigue el principio Abstraction-over-Implementation de SOLID.
/// </summary>
public interface IVodProvider
{
    VodManifest Manifest { get; }

    Task<List<MediaItem>> GetCatalogAsync(string type, string? catalogId, string? genre, int page, CancellationToken ct);

    Task<MediaItem?> GetDetailAsync(string type, string id, CancellationToken ct);

    Task<List<StreamSource>> GetStreamsAsync(string type, string id, CancellationToken ct);
}

