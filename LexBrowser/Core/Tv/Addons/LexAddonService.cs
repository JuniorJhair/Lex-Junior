using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LexBrowser.Core.Tv.Addons;

/// <summary>
/// Servicio de coordinación para validar, instalar, actualizar y administrar addons en Lex TV.
/// </summary>
public sealed class LexAddonService
{
    private readonly LexAddonStore _store;
    private readonly LexAddonClient _client;

    public static LexAddonService Shared { get; } = new(LexAddonStore.Shared, LexAddonClient.Shared);

    public LexAddonService(LexAddonStore store, LexAddonClient client)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public List<LexAddonSummary> ListAddons() => _store.GetSummaries();

    public Task<LexAddonManifestResult> PreviewFromUrlAsync(
        string? rawManifestUrl,
        bool allowLocalLoopback,
        CancellationToken ct)
        => _client.FetchManifestAsync(rawManifestUrl, allowLocalLoopback, ct);

    public async Task<LexAddonActionResult> InstallFromUrlAsync(
        string? rawManifestUrl,
        bool allowLocalLoopback,
        CancellationToken ct)
    {
        var fetched = await _client.FetchManifestAsync(rawManifestUrl, allowLocalLoopback, ct).ConfigureAwait(false);
        if (!fetched.Success || fetched.Manifest == null || string.IsNullOrEmpty(fetched.CanonicalUrl))
        {
            return new LexAddonActionResult(false, ErrorMessage: fetched.ErrorMessage ?? "No se pudo validar el manifiesto del addon.");
        }

        try
        {
            var (summary, wasUpdate) = _store.InstallOrUpdate(fetched.Manifest, fetched.CanonicalUrl, allowLocalLoopback);
            TvDiagLog.Write("addon-install", $"{(wasUpdate ? "Actualizado" : "Instalado")} {summary.Id} v{summary.Version}", summary.DisplayUrl);
            return new LexAddonActionResult(true, Addon: summary, Addons: _store.GetSummaries(), Updated: wasUpdate);
        }
        catch (Exception ex)
        {
            return new LexAddonActionResult(false, ErrorMessage: TvUrlGuard.SanitizeHeaderValue(ex.Message, 180));
        }
    }

    public async Task<LexAddonActionResult> RefreshAddonAsync(string? addonId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(addonId))
            return new LexAddonActionResult(false, ErrorMessage: "Identificador de addon inválido.");

        var existing = _store.GetSummary(addonId);
        string? storedUrl = _store.GetManifestUrl(addonId);
        if (existing == null || string.IsNullOrEmpty(storedUrl))
            return new LexAddonActionResult(false, ErrorMessage: "Addon no encontrado o sin URL almacenada.");

        var fetched = await _client.FetchManifestAsync(storedUrl, existing.AllowLocalLoopback, ct).ConfigureAwait(false);
        if (!fetched.Success || fetched.Manifest == null || string.IsNullOrEmpty(fetched.CanonicalUrl))
        {
            string err = fetched.ErrorMessage ?? "No se pudo consultar el manifiesto remoto.";
            var updatedSummary = _store.RecordCheckError(addonId, err);
            return new LexAddonActionResult(false, Addon: updatedSummary, Addons: _store.GetSummaries(), ErrorMessage: err);
        }

        if (!string.Equals(fetched.Manifest.Id, existing.Id, StringComparison.OrdinalIgnoreCase))
        {
            string mismatchErr = "El manifiesto remoto cambió su 'id' y fue rechazado por seguridad.";
            var updatedSummary = _store.RecordCheckError(addonId, mismatchErr);
            return new LexAddonActionResult(false, Addon: updatedSummary, Addons: _store.GetSummaries(), ErrorMessage: mismatchErr);
        }

        var (summary, _) = _store.InstallOrUpdate(fetched.Manifest, fetched.CanonicalUrl, existing.AllowLocalLoopback);
        TvDiagLog.Write("addon-refresh", $"Comprobado {summary.Id} v{summary.Version}", summary.DisplayUrl);
        return new LexAddonActionResult(true, Addon: summary, Addons: _store.GetSummaries(), Updated: true);
    }

    public LexAddonActionResult SetEnabled(string? addonId, bool enabled)
    {
        var updated = _store.SetEnabled(addonId, enabled);
        if (updated == null)
            return new LexAddonActionResult(false, ErrorMessage: "Addon no encontrado.");

        return new LexAddonActionResult(true, Addon: updated, Addons: _store.GetSummaries());
    }

    public LexAddonActionResult Remove(string? addonId)
    {
        bool removed = _store.Remove(addonId);
        if (!removed)
            return new LexAddonActionResult(false, ErrorMessage: "Addon no encontrado.");

        TvDiagLog.Write("addon-remove", $"Desinstalado {addonId}");
        return new LexAddonActionResult(true, Addons: _store.GetSummaries());
    }

    // =========================================================================
    // Fase 3: Catálogos de películas, series y anime + Metadatos
    // =========================================================================

    public List<LexAddonCatalogDescriptor> ListCatalogDescriptors(string? typeFilter = null)
        => _store.GetCatalogDescriptors(typeFilter);

    public async Task<LexAddonCatalogResult> GetCatalogAsync(
        string? addonId,
        string? type,
        string? catalogId,
        string? search,
        string? genre,
        int skip,
        CancellationToken ct)
    {
        var prep = await FetchCatalogJsonAsync(addonId, type, catalogId, search, genre, skip, ct).ConfigureAwait(false);
        if (!prep.Success || prep.Json == null || prep.Summary == null || prep.CatalogDecl == null)
        {
            return new LexAddonCatalogResult(
                false,
                AddonId: prep.Summary?.Id,
                AddonName: prep.Summary?.Name,
                CatalogName: prep.CatalogDecl?.Name,
                ErrorMessage: prep.ErrorMessage ?? "No se pudo consultar el catálogo del addon.");
        }

        var (ok, items, explicitHasMore, parseErr) = LexAddonCatalogParser.ParseCatalog(
            prep.Json, prep.Summary.Id, prep.Summary.Name, prep.CatalogDecl.Type, prep.BaseUrl);
        if (!ok)
        {
            return new LexAddonCatalogResult(
                false,
                AddonId: prep.Summary.Id,
                AddonName: prep.Summary.Name,
                CatalogName: prep.CatalogDecl.Name,
                ErrorMessage: parseErr ?? "No se pudo procesar el catálogo.");
        }

        bool supportsSkip = prep.CatalogDecl.Extra.Any(e => e.Name.Equals("skip", StringComparison.OrdinalIgnoreCase));
        bool hasMore = explicitHasMore || (supportsSkip && items.Count >= 20);
        int safeSkip = Math.Max(0, skip);

        return new LexAddonCatalogResult(
            true,
            Items: items,
            HasMore: hasMore,
            NextSkip: safeSkip + items.Count,
            AddonId: prep.Summary.Id,
            AddonName: prep.Summary.Name,
            CatalogName: string.IsNullOrWhiteSpace(prep.CatalogDecl.Name) ? prep.CatalogDecl.Id : prep.CatalogDecl.Name);
    }

    private async Task<(bool Success, string? Json, string? BaseUrl, LexAddonSummary? Summary, LexAddonCatalogDecl? CatalogDecl, string? ErrorMessage)> FetchCatalogJsonAsync(
        string? addonId,
        string? type,
        string? catalogId,
        string? search,
        string? genre,
        int skip,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(addonId))
            return (false, null, null, null, null, "Selecciona un addon válido.");

        var summary = _store.GetSummary(addonId);
        if (summary == null)
            return (false, null, null, null, null, "El addon solicitado no está instalado.");
        if (!summary.Enabled)
            return (false, null, null, summary, null, "El addon está deshabilitado.");

        string? storedUrl = _store.GetManifestUrl(addonId);
        if (string.IsNullOrEmpty(storedUrl))
            return (false, null, null, summary, null, "No se pudo recuperar la configuración del addon.");

        var catDecl = summary.Catalogs.FirstOrDefault(c =>
            c.Id.Equals(catalogId?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            c.Type.Equals(type?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (catDecl == null)
            return (false, null, null, summary, null, "El addon no expone el catálogo solicitado.");

        if (!LexAddonCatalogParser.TryBuildCatalogUrl(storedUrl, catDecl.Type, catDecl.Id, search, genre, skip, out string endpointUrl, out string? urlErr))
            return (false, null, null, summary, catDecl, urlErr);

        var fetched = await _client.FetchJsonEndpointAsync(
            endpointUrl, summary.AllowLocalLoopback, LexAddonClient.MaxJsonBytes, ct).ConfigureAwait(false);
        if (!fetched.Success || fetched.Json == null)
            return (false, null, null, summary, catDecl, fetched.ErrorMessage ?? "No se pudo consultar el catálogo del addon.");

        string baseUrl = LexAddonManifestParser.ExtractBaseUrl(new Uri(fetched.CanonicalUrl ?? storedUrl));
        return (true, fetched.Json, baseUrl, summary, catDecl, null);
    }

    public async Task<LexAddonMetaResult> GetMetaAsync(
        string? addonId,
        string? type,
        string? itemId,
        CancellationToken ct)
    {
        if (!LexAddonCatalogParser.IsValidPathSegment(type, 32) ||
            !LexAddonCatalogParser.IsValidPathSegment(itemId, 160))
        {
            return new LexAddonMetaResult(false, ErrorMessage: "Identificador de título inválido.");
        }

        string cleanType = type!.Trim().ToLowerInvariant();
        string cleanId = itemId!.Trim();
        var candidateIds = _store.FindMetaCapableAddonIds(cleanType, cleanId, addonId);
        if (candidateIds.Count == 0)
            return new LexAddonMetaResult(false, ErrorMessage: "Ningún addon habilitado expone detalles ('meta') para este título.");

        string? lastError = null;
        foreach (string cid in candidateIds)
        {
            var summary = _store.GetSummary(cid);
            string? storedUrl = _store.GetManifestUrl(cid);
            if (summary == null || !summary.Enabled || string.IsNullOrEmpty(storedUrl))
                continue;

            if (!LexAddonCatalogParser.TryBuildMetaUrl(storedUrl, cleanType, cleanId, out string endpointUrl, out string? urlErr))
            {
                lastError = urlErr;
                continue;
            }

            var fetched = await _client.FetchJsonEndpointAsync(
                endpointUrl, summary.AllowLocalLoopback, LexAddonClient.MaxJsonBytes, ct).ConfigureAwait(false);
            if (!fetched.Success || fetched.Json == null)
            {
                lastError = fetched.ErrorMessage;
                continue;
            }

            string baseUrl = LexAddonManifestParser.ExtractBaseUrl(new Uri(fetched.CanonicalUrl ?? storedUrl));
            var (ok, meta, parseErr) = LexAddonCatalogParser.ParseMeta(fetched.Json, summary.Id, summary.Name, baseUrl);
            if (ok && meta != null)
                return new LexAddonMetaResult(true, Meta: meta);

            lastError = parseErr;
        }

        return new LexAddonMetaResult(false, ErrorMessage: lastError ?? "No se encontraron detalles para este título.");
    }

    // =========================================================================
    // Fase 4: Resolución multi-addon de streams y subtítulos (/stream)
    // =========================================================================

    public async Task<LexAddonStreamsResult> GetStreamsAsync(
        string? addonId,
        string? type,
        string? itemId,
        CancellationToken ct)
    {
        if (!LexAddonCatalogParser.IsValidPathSegment(type, 32) ||
            !LexAddonCatalogParser.IsValidPathSegment(itemId, 180))
        {
            return new LexAddonStreamsResult(false, ErrorMessage: "Identificador de stream inválido.");
        }

        string cleanType = type!.Trim().ToLowerInvariant();
        string cleanId = itemId!.Trim();
        var candidateIds = _store.FindStreamCapableAddonIds(cleanType, cleanId, addonId);
        if (candidateIds.Count == 0)
        {
            return new LexAddonStreamsResult(
                false,
                Streams: new List<LexAddonStreamOption>(),
                QueriedAddonsCount: 0,
                ErrorMessage: "Ningún addon habilitado expone fuentes ('stream') para este título.");
        }

        var tasks = candidateIds.Select(cid => FetchSingleAddonStreamsAsync(cid, cleanType, cleanId, ct)).ToArray();
        var perAddonResults = await Task.WhenAll(tasks).ConfigureAwait(false);

        var playableStreams = new List<LexAddonStreamOption>();
        var nonPlayableStreams = new List<LexAddonStreamOption>();
        var errors = new List<string>();

        foreach (var res in perAddonResults)
        {
            if (!string.IsNullOrEmpty(res.Error))
                errors.Add(res.Error);

            foreach (var entry in res.Entries)
            {
                if (entry.Option.Playable)
                    playableStreams.Add(entry.Option);
                else
                    nonPlayableStreams.Add(entry.Option);
            }
        }

        var combined = new List<LexAddonStreamOption>(playableStreams.Count + nonPlayableStreams.Count);
        combined.AddRange(playableStreams);
        combined.AddRange(nonPlayableStreams);

        if (combined.Count == 0 && errors.Count > 0)
        {
            return new LexAddonStreamsResult(
                false,
                Streams: combined,
                QueriedAddonsCount: candidateIds.Count,
                AddonErrors: errors,
                ErrorMessage: errors[0]);
        }

        return new LexAddonStreamsResult(
            true,
            Streams: combined,
            QueriedAddonsCount: candidateIds.Count,
            AddonErrors: errors.Count > 0 ? errors : null);
    }

    public async Task<(bool Success, string? WebVtt, int StatusCode, string? ErrorMessage)> FetchSubtitleAsWebVttAsync(
        string? rawSubUrl,
        CancellationToken ct)
    {
        if (!TvUrlGuard.IsAllowedRemoteUrl(rawSubUrl, out var safeUri) || safeUri == null)
            return (false, null, 403, "URL de subtítulo bloqueada por seguridad.");

        if (TvDenylist.IsDenylisted(safeUri.AbsoluteUri, out var denyReason))
            return (false, null, 403, denyReason ?? "Fuente de subtítulos deshabilitada.");

        var fetched = await _client.FetchJsonEndpointAsync(
            safeUri.AbsoluteUri,
            allowLocalLoopback: false,
            LexAddonStreamParser.MaxSubtitleBytes,
            ct).ConfigureAwait(false);

        if (!fetched.Success || fetched.Json == null)
        {
            int status = fetched.StatusCode is >= 400 and <= 599 ? fetched.StatusCode : 502;
            return (false, null, status, fetched.ErrorMessage ?? "No se pudo descargar el subtítulo.");
        }

        string vtt = LexAddonStreamParser.NormalizeToWebVtt(fetched.Json);
        return (true, vtt, 200, null);
    }

    private async Task<(List<LexAddonStreamParser.ParsedStreamEntry> Entries, string? Error)> FetchSingleAddonStreamsAsync(
        string addonId,
        string cleanType,
        string cleanId,
        CancellationToken ct)
    {
        var summary = _store.GetSummary(addonId);
        string? storedUrl = _store.GetManifestUrl(addonId);
        if (summary == null || !summary.Enabled || string.IsNullOrEmpty(storedUrl))
            return (new List<LexAddonStreamParser.ParsedStreamEntry>(), null);

        if (!LexAddonStreamParser.TryBuildStreamUrl(storedUrl, cleanType, cleanId, out string endpointUrl, out string? urlErr))
            return (new List<LexAddonStreamParser.ParsedStreamEntry>(), $"{summary.Name}: {urlErr}");

        var fetched = await _client.FetchJsonEndpointAsync(
            endpointUrl, summary.AllowLocalLoopback, LexAddonClient.MaxJsonBytes, ct).ConfigureAwait(false);
        if (!fetched.Success || fetched.Json == null)
            return (new List<LexAddonStreamParser.ParsedStreamEntry>(), $"{summary.Name}: {fetched.ErrorMessage ?? "sin respuesta"}");

        var (ok, entries, parseErr) = LexAddonStreamParser.ParseStreams(fetched.Json, summary.Id, summary.Name);
        if (!ok)
            return (new List<LexAddonStreamParser.ParsedStreamEntry>(), $"{summary.Name}: {parseErr}");

        RememberStreamPlaybackHeaders(entries);
        return (entries, null);
    }

    private static void RememberStreamPlaybackHeaders(IEnumerable<LexAddonStreamParser.ParsedStreamEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (!entry.Option.Playable || string.IsNullOrEmpty(entry.Option.StreamUrl))
                continue;

            bool hasHeaders = !string.IsNullOrWhiteSpace(entry.RequestUserAgent) ||
                              !string.IsNullOrWhiteSpace(entry.RequestReferer) ||
                              !string.IsNullOrWhiteSpace(entry.RequestOrigin);

            if (hasHeaders && Uri.TryCreate(entry.Option.StreamUrl, UriKind.Absolute, out var streamUri))
            {
                TvPlaybackHints.RememberHostPlaybackHeaders(
                    streamUri.Host,
                    entry.RequestUserAgent,
                    entry.RequestReferer,
                    entry.RequestOrigin);
                TvStreamProxy.InvalidateProbe(entry.Option.StreamUrl);
            }
        }
    }

    // =========================================================================
    // Fase 5: Repositorios de Addons y Actualizaciones Masivas
    // =========================================================================

    public async Task<LexAddonBulkUpdateResult> RefreshAllAddonsAsync(CancellationToken ct)
    {
        var current = _store.GetSummaries();
        if (current.Count == 0)
        {
            return new LexAddonBulkUpdateResult(
                0, 0, 0, 0,
                new List<string>(),
                new List<string>(),
                new List<LexAddonSummary>());
        }

        var tasks = current.Select(async addon =>
        {
            var res = await RefreshAddonAsync(addon.Id, ct).ConfigureAwait(false);
            return (Before: addon, Result: res);
        }).ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var updatedIds = new List<string>();
        var errors = new List<string>();
        int upToDateCount = 0;

        foreach (var item in results)
        {
            if (item.Result.Success && item.Result.Addon != null)
            {
                if (LexAddonRepoParser.CompareVersions(item.Result.Addon.Version, item.Before.Version) > 0)
                    updatedIds.Add(item.Result.Addon.Id);
                else
                    upToDateCount++;
            }
            else
            {
                errors.Add($"{item.Before.Name}: {item.Result.ErrorMessage ?? "error desconocido"}");
            }
        }

        return new LexAddonBulkUpdateResult(
            TotalChecked: current.Count,
            UpdatedCount: updatedIds.Count,
            UpToDateCount: upToDateCount,
            ErrorCount: errors.Count,
            UpdatedIds: updatedIds,
            Errors: errors,
            Addons: _store.GetSummaries());
    }

    public List<LexAddonRepositorySummary> ListRepositories() => _store.GetRepositories();

    public async Task<LexAddonRepoResult> AddRepositoryAsync(
        string? rawRepoUrl,
        bool allowLocalLoopback,
        CancellationToken ct)
    {
        if (!LexAddonRepoParser.TryNormalizeRepoInput(rawRepoUrl, out string normalizedUrl, out string? normErr))
            return new LexAddonRepoResult(false, ErrorMessage: normErr);

        var (ok, feed, canonicalUrl, err) = await FetchAndParseRepoAsync(normalizedUrl, allowLocalLoopback, ct).ConfigureAwait(false);
        if (!ok || feed == null || string.IsNullOrEmpty(canonicalUrl))
            return new LexAddonRepoResult(false, Repositories: _store.GetRepositories(), ErrorMessage: err ?? "No se pudo leer el repositorio.");

        try
        {
            var (summary, _) = _store.AddOrUpdateRepository(feed, canonicalUrl, allowLocalLoopback);
            AnnotateRepoEntries(feed.Entries);
            TvDiagLog.Write("addon-repo-add", $"{summary.Name} ({summary.AddonCount} addons)", summary.DisplayUrl);
            return new LexAddonRepoResult(
                true,
                Repository: summary,
                Repositories: _store.GetRepositories(),
                Entries: feed.Entries);
        }
        catch (Exception ex)
        {
            return new LexAddonRepoResult(false, Repositories: _store.GetRepositories(), ErrorMessage: TvUrlGuard.SanitizeHeaderValue(ex.Message, 180));
        }
    }

    public async Task<LexAddonRepoResult> InspectRepositoryAsync(string? repoId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoId))
            return new LexAddonRepoResult(false, ErrorMessage: "Identificador de repositorio inválido.");

        var existing = _store.GetRepositorySummary(repoId);
        string? storedUrl = _store.GetRepositoryUrl(repoId);
        if (existing == null || string.IsNullOrEmpty(storedUrl))
            return new LexAddonRepoResult(false, ErrorMessage: "Repositorio no encontrado.");

        var (ok, feed, canonicalUrl, err) = await FetchAndParseRepoAsync(storedUrl, existing.AllowLocalLoopback, ct).ConfigureAwait(false);
        if (!ok || feed == null || string.IsNullOrEmpty(canonicalUrl))
        {
            var updatedErr = _store.RecordRepositoryError(repoId, err ?? "No se pudo consultar el repositorio.");
            return new LexAddonRepoResult(
                false,
                Repository: updatedErr,
                Repositories: _store.GetRepositories(),
                ErrorMessage: err ?? "No se pudo consultar el repositorio.");
        }

        feed.Id = existing.Id;
        var (summary, _) = _store.AddOrUpdateRepository(feed, canonicalUrl, existing.AllowLocalLoopback);
        AnnotateRepoEntries(feed.Entries);
        return new LexAddonRepoResult(
            true,
            Repository: summary,
            Repositories: _store.GetRepositories(),
            Entries: feed.Entries);
    }

    public LexAddonRepoResult RemoveRepository(string? repoId)
    {
        bool removed = _store.RemoveRepository(repoId);
        if (!removed)
            return new LexAddonRepoResult(false, Repositories: _store.GetRepositories(), ErrorMessage: "Repositorio no encontrado.");

        TvDiagLog.Write("addon-repo-remove", $"Eliminado {repoId}");
        return new LexAddonRepoResult(true, Repositories: _store.GetRepositories());
    }

    private async Task<(bool Success, LexAddonRepoFeed? Feed, string? CanonicalUrl, string? Error)> FetchAndParseRepoAsync(
        string url,
        bool allowLocalLoopback,
        CancellationToken ct)
    {
        var fetched = await _client.FetchJsonEndpointAsync(
            url, allowLocalLoopback, LexAddonRepoParser.MaxRepoBytes, ct).ConfigureAwait(false);
        if (!fetched.Success || fetched.Json == null)
            return (false, null, null, fetched.ErrorMessage ?? "No se pudo descargar el repositorio.");

        string canonical = fetched.CanonicalUrl ?? url;
        var (ok, feed, parseErr) = LexAddonRepoParser.Parse(fetched.Json, canonical, allowLocalLoopback);
        if (!ok || feed == null)
            return (false, null, null, parseErr ?? "Repositorio JSON inválido.");

        return (true, feed, canonical, null);
    }

    private void AnnotateRepoEntries(List<LexAddonRepoEntry> entries)
    {
        var installedById = _store.GetSummaries()
            .ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        var installedByDisplayUrl = _store.GetSummaries()
            .GroupBy(a => a.DisplayUrl, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            LexAddonSummary? match = null;
            if (!string.IsNullOrWhiteSpace(entry.AddonId))
                installedById.TryGetValue(entry.AddonId, out match);
            if (match == null && !string.IsNullOrWhiteSpace(entry.DisplayUrl))
                installedByDisplayUrl.TryGetValue(entry.DisplayUrl, out match);

            if (match != null)
            {
                entry.Installed = true;
                entry.InstalledVersion = match.Version;
                entry.UpdateAvailable = LexAddonRepoParser.CompareVersions(entry.Version, match.Version) > 0;
            }
        }
    }

    // =========================================================================
    // Fase 6: Búsqueda Global en Catálogos y Subtítulos Dedicados (/subtitles)
    // =========================================================================

    public async Task<LexAddonGlobalSearchResult> SearchCatalogsAsync(
        string? query,
        string? typeFilter,
        CancellationToken ct)
    {
        string cleanQuery = TvUrlGuard.SanitizeHeaderValue(query, 120).Trim();
        if (string.IsNullOrWhiteSpace(cleanQuery))
            return new LexAddonGlobalSearchResult(false, ErrorMessage: "Escribe un término de búsqueda.");

        string? normalizedType = string.IsNullOrWhiteSpace(typeFilter) ||
            typeFilter.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)
            ? null
            : typeFilter.Trim();

        var targets = _store.GetCatalogDescriptors(normalizedType)
            .Where(c => c.HasSearch)
            .Take(10)
            .ToList();

        if (targets.Count == 0)
        {
            return new LexAddonGlobalSearchResult(
                false,
                Items: new List<LexAddonMetaPreview>(),
                SearchedCatalogsCount: 0,
                ErrorMessage: "Ningún catálogo habilitado soporta búsqueda por texto.");
        }

        var tasks = targets.Select(async cat =>
        {
            var res = await GetCatalogAsync(cat.AddonId, cat.Type, cat.CatalogId, cleanQuery, null, 0, ct).ConfigureAwait(false);
            return (Catalog: cat, Result: res);
        }).ToArray();

        var responses = await Task.WhenAll(tasks).ConfigureAwait(false);
        var items = new List<LexAddonMetaPreview>();
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in responses)
        {
            if (!r.Result.Success)
            {
                if (!string.IsNullOrWhiteSpace(r.Result.ErrorMessage))
                    errors.Add($"{r.Catalog.AddonName} ({r.Catalog.Name}): {r.Result.ErrorMessage}");
                continue;
            }

            if (r.Result.Items == null) continue;
            foreach (var item in r.Result.Items)
            {
                if (items.Count >= LexAddonCatalogParser.MaxCatalogItems) break;
                string key = $"{item.AddonId}::{item.Type}::{item.Id}";
                if (seen.Add(key))
                    items.Add(item);
            }
        }

        if (items.Count == 0 && errors.Count > 0 && errors.Count == targets.Count)
        {
            return new LexAddonGlobalSearchResult(
                false,
                Items: items,
                SearchedCatalogsCount: targets.Count,
                Errors: errors,
                ErrorMessage: errors[0]);
        }

        return new LexAddonGlobalSearchResult(
            true,
            Items: items,
            SearchedCatalogsCount: targets.Count,
            Errors: errors.Count > 0 ? errors : null);
    }

    public async Task<LexAddonSubtitlesResult> GetSubtitlesAsync(
        string? addonId,
        string? type,
        string? itemId,
        string? videoHash,
        long? videoSize,
        string? filename,
        CancellationToken ct)
    {
        if (!LexAddonCatalogParser.IsValidPathSegment(type, 32) ||
            !LexAddonCatalogParser.IsValidPathSegment(itemId, 180))
        {
            return new LexAddonSubtitlesResult(false, ErrorMessage: "Identificador de subtítulos inválido.");
        }

        string cleanType = type!.Trim().ToLowerInvariant();
        string cleanId = itemId!.Trim();
        var candidateIds = _store.FindSubtitleCapableAddonIds(cleanType, cleanId, addonId);
        if (candidateIds.Count == 0)
        {
            return new LexAddonSubtitlesResult(
                true,
                Subtitles: new List<LexAddonSubtitleDecl>(),
                QueriedAddonsCount: 0);
        }

        var tasks = candidateIds.Select(cid => FetchSingleAddonSubtitlesAsync(
            cid, cleanType, cleanId, videoHash, videoSize, filename, ct)).ToArray();
        var perAddon = await Task.WhenAll(tasks).ConfigureAwait(false);

        var combined = new List<LexAddonSubtitleDecl>();
        var seenProxyUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        foreach (var res in perAddon)
        {
            if (!string.IsNullOrEmpty(res.Error))
                errors.Add(res.Error);

            foreach (var sub in res.Subtitles)
            {
                if (combined.Count >= LexAddonStreamParser.MaxSubtitlesPerStream) break;
                if (seenProxyUrls.Add(sub.ProxyUrl))
                    combined.Add(sub);
            }
        }

        return new LexAddonSubtitlesResult(
            true,
            Subtitles: combined,
            QueriedAddonsCount: candidateIds.Count,
            AddonErrors: errors.Count > 0 ? errors : null);
    }

    private async Task<(List<LexAddonSubtitleDecl> Subtitles, string? Error)> FetchSingleAddonSubtitlesAsync(
        string addonId,
        string cleanType,
        string cleanId,
        string? videoHash,
        long? videoSize,
        string? filename,
        CancellationToken ct)
    {
        var summary = _store.GetSummary(addonId);
        string? storedUrl = _store.GetManifestUrl(addonId);
        if (summary == null || !summary.Enabled || string.IsNullOrEmpty(storedUrl))
            return (new List<LexAddonSubtitleDecl>(), null);

        if (!LexAddonStreamParser.TryBuildSubtitlesUrl(
            storedUrl, cleanType, cleanId, videoHash, videoSize, filename, out string endpointUrl, out string? urlErr))
        {
            return (new List<LexAddonSubtitleDecl>(), $"{summary.Name}: {urlErr}");
        }

        var fetched = await _client.FetchJsonEndpointAsync(
            endpointUrl, summary.AllowLocalLoopback, LexAddonClient.MaxJsonBytes, ct).ConfigureAwait(false);
        if (!fetched.Success || fetched.Json == null)
            return (new List<LexAddonSubtitleDecl>(), $"{summary.Name}: {fetched.ErrorMessage ?? "sin respuesta"}");

        var (ok, subs, parseErr) = LexAddonStreamParser.ParseSubtitlesResponse(fetched.Json, summary.Name);
        if (!ok)
            return (new List<LexAddonSubtitleDecl>(), $"{summary.Name}: {parseErr}");

        return (subs, null);
    }

    // =========================================================================
    // Fase 7: Complementos IPTV (Canales en vivo integrados en tv.html)
    // =========================================================================

    public List<LexAddonIptvSourceDescriptor> ListIptvSources()
    {
        return _store.GetCatalogDescriptors()
            .Where(c => LexAddonCatalogParser.IsLiveChannelType(c.Type))
            .Select(c => new LexAddonIptvSourceDescriptor
            {
                SourceKey = $"addon::{c.AddonId}::{c.Type}::{c.CatalogId}",
                AddonId = c.AddonId,
                AddonName = c.AddonName,
                Type = c.Type,
                CatalogId = c.CatalogId,
                Name = $"{c.AddonName} · {c.Name}",
                HasSearch = c.HasSearch,
                Genres = c.Genres
            })
            .ToList();
    }

    public async Task<LexAddonIptvChannelsResult> GetIptvChannelsAsync(
        string? addonId,
        string? type,
        string? catalogId,
        string? search,
        string? genre,
        CancellationToken ct)
    {
        var prep = await FetchCatalogJsonAsync(addonId, type, catalogId, search, genre, 0, ct).ConfigureAwait(false);
        if (!prep.Success || prep.Json == null || prep.Summary == null || prep.CatalogDecl == null)
        {
            return new LexAddonIptvChannelsResult(
                false,
                ErrorMessage: prep.ErrorMessage ?? "No se pudo cargar el catálogo IPTV del addon.");
        }

        if (!LexAddonCatalogParser.IsLiveChannelType(prep.CatalogDecl.Type))
        {
            return new LexAddonIptvChannelsResult(
                false,
                ErrorMessage: "El catálogo seleccionado no corresponde a canales de TV en vivo.");
        }

        var (ok, channels, rememberedStreams, parseErr) = LexAddonCatalogParser.ParseLiveCatalog(
            prep.Json, prep.Summary.Id, prep.Summary.Name, prep.CatalogDecl.Type, prep.BaseUrl);
        if (!ok)
            return new LexAddonIptvChannelsResult(false, ErrorMessage: parseErr ?? "No se pudieron leer los canales IPTV.");

        RememberStreamPlaybackHeaders(rememberedStreams);

        string sourceKey = $"addon::{prep.Summary.Id}::{prep.CatalogDecl.Type}::{prep.CatalogDecl.Id}";
        string sourceName = $"{prep.Summary.Name} · {(string.IsNullOrWhiteSpace(prep.CatalogDecl.Name) ? prep.CatalogDecl.Id : prep.CatalogDecl.Name)}";

        return new LexAddonIptvChannelsResult(
            true,
            SourceKey: sourceKey,
            SourceName: sourceName,
            Channels: channels);
    }
}

