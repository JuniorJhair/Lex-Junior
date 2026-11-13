using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LexBrowser.Core.Tv;

/// <summary>
/// Servicio para procesar e importar listas M3U/M3U8 provistas exclusivamente por el usuario.
/// Lex no incluye ni sugiere listas remotas.
/// </summary>
public sealed class TvPlaylistService
{
    private static readonly Lazy<HttpClient> SharedHttp =
        new(() => TvUrlGuard.CreatePinnedHttpClient(timeout: TimeSpan.FromSeconds(45)));

    public static TvPlaylistService Shared { get; } = new();

    internal static HttpClient Http => SharedHttp.Value;

    public async Task<List<TvChannel>> ImportPlaylistFromUrlAsync(string url, string sourceName, CancellationToken ct)
        => (await ImportPlaylistDetailedFromUrlAsync(url, sourceName, ct).ConfigureAwait(false)).Channels;

    public async Task<M3uParseResult> ImportPlaylistDetailedFromUrlAsync(string url, string sourceName, CancellationToken ct)
    {
        if (TvDenylist.IsDenylisted(url, out var denyReason))
            throw new InvalidOperationException(denyReason ?? "Esta fuente fue deshabilitada tras una reclamación.");

        if (!TvUrlGuard.IsAllowedRemoteUrl(url, out var uri) || uri == null)
            throw new InvalidOperationException("URL inválida. Solo se aceptan enlaces públicos http:// o https://.");

        var (resp, finalUri) = await TvUrlGuard.GetWithSafeRedirectsAsync(Http, uri, null, ct).ConfigureAwait(false);
        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"El servidor respondió {(int)resp.StatusCode} {resp.ReasonPhrase}.");

            string text = await TvUrlGuard.ReadBoundedUtf8Async(resp, TvUrlGuard.MaxPlaylistBytes, ct).ConfigureAwait(false);
            var parsed = M3uParser.ParseDetailed(text, sourceName, finalUri.AbsoluteUri);
            if (parsed.Channels.Count == 0)
                throw new InvalidOperationException("No se encontraron canales válidos (#EXTINF + URL http/https) en la lista.");
            return parsed;
        }
    }
}
