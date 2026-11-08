using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LexBrowser.UI.Models;

namespace LexBrowser.Core;

public enum OmniboxSuggestionKind
{
    PrimarySearchOrUrl,
    RecentSearch,
    LiveSearchSuggestion,
    VisitedPage,
    BookmarkPage
}

public sealed class OmniboxSuggestionItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private System.Windows.Media.ImageSource? _faviconImage;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public OmniboxSuggestionKind Kind { get; init; }
    public string PrimaryText { get; init; } = string.Empty;
    public string SecondaryText { get; init; } = string.Empty;
    public string TargetInputOrUrl { get; init; } = string.Empty;
    public string IconText { get; init; } = "🔍";
    public string IconBgHex { get; init; } = "#272736";
    public string IconFgHex { get; init; } = "#A1A1AA";
    public string? HistoryEntryId { get; init; }
    public bool CanDelete => !string.IsNullOrEmpty(HistoryEntryId);
    public bool HasSecondaryText => !string.IsNullOrWhiteSpace(SecondaryText);

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public System.Windows.Media.ImageSource? FaviconImage
    {
        get => _faviconImage;
        set
        {
            if (ReferenceEquals(_faviconImage, value)) return;
            _faviconImage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasFaviconImage));
            OnPropertyChanged(nameof(HasNoFaviconImage));
        }
    }

    public bool HasFaviconImage => _faviconImage != null;
    public bool HasNoFaviconImage => _faviconImage == null;
}

/// <summary>
/// Motor de sugerencias para la barra de direcciones (Omnibox).
/// Combina de forma asíncrona y sin bloquear el hilo de interfaz:
///  1. Búsqueda directa en el motor activo o navegación a URL
///  2. Búsquedas recientes realizadas por el usuario (extraídas del historial con icono 🕒)
///  3. Autocompletado predictivo en vivo del buscador seleccionado (con caché LRU en RAM y cancelación inmediata)
///  4. Páginas y videos ya navegados con anterioridad (con icono y URL formateada)
/// Sin consumo de CPU o red cuando el usuario no está escribiendo en la barra.
/// </summary>
public static class OmniboxSuggestionService
{
    public const int MaxHistoryScanEntries = 2500;

    private static readonly HttpClient _suggestClient = new()
    {
        Timeout = TimeSpan.FromMilliseconds(1600)
    };

    private static readonly object _cacheLock = new();
    private static readonly Dictionary<string, List<string>> _liveSuggestCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> _cacheLruOrder = new();
    private const int MaxCacheEntries = 40;

    static OmniboxSuggestionService()
    {
        try
        {
            _suggestClient.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        }
        catch { }
    }

    /// <summary>
    /// Extrae el término de búsqueda si una URL del historial corresponde a una búsqueda en Google, Bing, DuckDuckGo o Brave.
    /// </summary>
    public static string? TryExtractSearchQueryFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        string host = uri.Host.ToLowerInvariant();
        bool isSearchHost =
            (host.Contains("google.") && uri.AbsolutePath.StartsWith("/search", StringComparison.OrdinalIgnoreCase)) ||
            (host.Contains("bing.com") && uri.AbsolutePath.StartsWith("/search", StringComparison.OrdinalIgnoreCase)) ||
            host.Contains("duckduckgo.com") ||
            (host.Contains("search.brave.com") && uri.AbsolutePath.StartsWith("/search", StringComparison.OrdinalIgnoreCase));

        if (!isSearchHost) return null;

        string query = uri.Query;
        if (string.IsNullOrWhiteSpace(query)) return null;

        string trimmed = query.TrimStart('?');
        foreach (string part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string key = part.Substring(0, eq);
            if (string.Equals(key, "q", StringComparison.OrdinalIgnoreCase))
            {
                string rawVal = part.Substring(eq + 1).Replace('+', ' ');
                try
                {
                    string decoded = Uri.UnescapeDataString(rawVal).Trim();
                    if (!string.IsNullOrWhiteSpace(decoded) && decoded.Length <= 120)
                        return decoded;
                }
                catch { }
            }
        }

        return null;
    }

    public static string FormatCleanDisplayUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        string clean = url.Trim();
        if (clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            clean = clean.Substring(8);
        else if (clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            clean = clean.Substring(7);

        if (clean.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            clean = clean.Substring(4);

        if (clean.EndsWith("/", StringComparison.Ordinal) && !clean.Contains('?'))
            clean = clean.TrimEnd('/');

        if (clean.Length > 76)
            clean = clean.Substring(0, 73) + "...";

        return clean;
    }

    public static bool LooksLikeUrlOrDomain(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;
        string q = query.Trim();
        if (q.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            q.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            q.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
            q.StartsWith("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return q.Contains('.') && !q.Contains(' ');
    }

    private static void AttachFaviconInBackground(OmniboxSuggestionItem item, string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var img = await Engine.FaviconService.ResolveFaviconAsync(url).ConfigureAwait(false);
                if (img != null)
                {
                    var disp = System.Windows.Application.Current?.Dispatcher;
                    if (disp != null)
                        _ = disp.BeginInvoke(() => item.FaviconImage = img);
                    else
                        item.FaviconImage = img;
                }
            }
            catch { }
        });
    }

    /// <summary>
    /// Construye la lista combinada de sugerencias locales + sugerencias en vivo del motor activo.
    /// Usa los campos precalculados de HistoryEntry y acota el recorrido a las últimas 2500 entradas.
    /// </summary>
    public static List<OmniboxSuggestionItem> BuildSuggestions(
        string? rawInput,
        IReadOnlyList<HistoryEntry> historyEntries,
        IReadOnlyList<BookmarkItem>? bookmarks = null,
        IReadOnlyList<string>? liveGoogleSuggestions = null,
        string searchEngineName = "Google",
        int maxTotalItems = 9)
    {
        var results = new List<OmniboxSuggestionItem>();
        string query = (rawInput ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(query))
            return results;

        bool looksLikeUrl = LooksLikeUrlOrDomain(query);
        string qLower = query.ToLowerInvariant();

        // 1. Primera fila: Búsqueda directa en el motor activo (o ir a la URL si escribió un dominio)
        string? primaryFavDomain = looksLikeUrl ? Engine.FaviconService.ExtractDomain(query) : null;
        var primaryFav = !string.IsNullOrEmpty(primaryFavDomain) ? Engine.FaviconService.GetCachedFavicon(primaryFavDomain) : null;
        results.Add(new OmniboxSuggestionItem
        {
            Kind = OmniboxSuggestionKind.PrimarySearchOrUrl,
            PrimaryText = query,
            SecondaryText = looksLikeUrl ? " — Abrir dirección web" : $" - Búsqueda de {searchEngineName}",
            TargetInputOrUrl = query,
            IconText = looksLikeUrl ? "🌐" : "🔍",
            IconBgHex = "#252538",
            IconFgHex = "#818CF8",
            FaviconImage = primaryFav,
            IsSelected = true
        });

        var seenSearchTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { query };
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 2. Páginas y videos ya navegados con anterioridad en el historial
        if (historyEntries != null && historyEntries.Count > 0)
        {
            string[] wordsLower = qLower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var matchingHistory = new List<(HistoryEntry entry, int score)>();
            int scanLimit = Math.Min(historyEntries.Count, MaxHistoryScanEntries);

            for (int i = 0; i < scanLimit; i++)
            {
                var entry = historyEntries[i];
                if (entry == null || string.IsNullOrWhiteSpace(entry.Url)) continue;
                if (entry.ExtractedSearchQuery != null) continue;

                string searchKey = entry.SearchKey;
                bool matchesAllWords = true;
                for (int wIdx = 0; wIdx < wordsLower.Length; wIdx++)
                {
                    if (!searchKey.Contains(wordsLower[wIdx], StringComparison.Ordinal))
                    {
                        matchesAllWords = false;
                        break;
                    }
                }
                if (!matchesAllWords) continue;

                string urlCleanLower = entry.CleanDisplayUrlLower;
                string titleLower = entry.TitleLower;

                int score = 0;
                if (urlCleanLower.StartsWith(qLower, StringComparison.Ordinal)) score += 100;
                else if (urlCleanLower.Contains(qLower, StringComparison.Ordinal)) score += 50;
                if (titleLower.StartsWith(qLower, StringComparison.Ordinal)) score += 40;
                else if (titleLower.Contains(qLower, StringComparison.Ordinal)) score += 20;

                matchingHistory.Add((entry, score));
            }

            int visitedCount = 0;
            foreach (var (entry, _) in matchingHistory.OrderByDescending(x => x.score))
            {
                if (visitedCount >= 4 || results.Count >= maxTotalItems - 2) break;
                if (!seenUrls.Add(entry.Url)) continue;

                var (icon, bg, fg) = GetSiteBadgeStyle(entry.Url, isBookmark: false);
                string cleanDisplay = entry.CleanDisplayUrl;
                string title = string.IsNullOrWhiteSpace(entry.Title) ? cleanDisplay : entry.Title;
                if (entry.Url.Contains("lexjunior.pages.dev", StringComparison.OrdinalIgnoreCase) ||
                    title.StartsWith("Lex Junior Browser", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("Ultra-Ligero", StringComparison.OrdinalIgnoreCase))
                {
                    title = "Lex Junior Browser";
                }
                else if (title.Length > 68)
                {
                    title = title.Substring(0, 65) + "...";
                }

                string domain = !string.IsNullOrWhiteSpace(entry.Domain) ? entry.Domain : Engine.FaviconService.ExtractDomain(entry.Url);
                var fav = Engine.FaviconService.GetCachedFavicon(domain);

                var item = new OmniboxSuggestionItem
                {
                    Kind = OmniboxSuggestionKind.VisitedPage,
                    PrimaryText = title,
                    SecondaryText = $" — {cleanDisplay}",
                    TargetInputOrUrl = entry.Url,
                    IconText = icon,
                    IconBgHex = bg,
                    IconFgHex = fg,
                    FaviconImage = fav,
                    HistoryEntryId = entry.Id
                };

                if (fav == null) AttachFaviconInBackground(item, entry.Url);

                results.Add(item);
                visitedCount++;
            }
        }

        // 3. Marcadores anclados que coincidan
        if (bookmarks != null)
        {
            foreach (var bk in bookmarks)
            {
                if (results.Count >= maxTotalItems) break;
                if (bk == null || string.IsNullOrWhiteSpace(bk.Url)) continue;

                if (bk.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    bk.Url.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    if (!seenUrls.Add(bk.Url)) continue;
                    var (icon, bg, fg) = GetSiteBadgeStyle(bk.Url, isBookmark: true);

                    string bdomain = Engine.FaviconService.ExtractDomain(bk.Url);
                    var bfav = Engine.FaviconService.GetCachedFavicon(bdomain);

                    string bTitle = string.IsNullOrWhiteSpace(bk.Title) ? FormatCleanDisplayUrl(bk.Url) : bk.Title;
                    if (bk.Url.Contains("lexjunior.pages.dev", StringComparison.OrdinalIgnoreCase) ||
                        bTitle.StartsWith("Lex Junior Browser", StringComparison.OrdinalIgnoreCase) ||
                        bTitle.Contains("Ultra-Ligero", StringComparison.OrdinalIgnoreCase))
                    {
                        bTitle = "Lex Junior Browser";
                    }

                    var item = new OmniboxSuggestionItem
                    {
                        Kind = OmniboxSuggestionKind.BookmarkPage,
                        PrimaryText = bTitle,
                        SecondaryText = $" — {FormatCleanDisplayUrl(bk.Url)}",
                        TargetInputOrUrl = bk.Url,
                        IconText = icon,
                        IconBgHex = bg,
                        IconFgHex = fg,
                        FaviconImage = bfav
                    };

                    if (bfav == null) AttachFaviconInBackground(item, bk.Url);

                    results.Add(item);
                }
            }
        }

        // 4. Búsquedas recientes del usuario en el historial (con icono de reloj 🕒)
        if (historyEntries != null && historyEntries.Count > 0)
        {
            int recentSearchCount = 0;
            int scanLimit = Math.Min(historyEntries.Count, MaxHistoryScanEntries);
            for (int i = 0; i < scanLimit; i++)
            {
                if (recentSearchCount >= 2 || results.Count >= maxTotalItems - 2) break;
                var entry = historyEntries[i];
                if (entry == null) continue;
                string? pastQuery = entry.ExtractedSearchQuery;
                if (string.IsNullOrWhiteSpace(pastQuery)) continue;

                if (pastQuery.Contains(query, StringComparison.OrdinalIgnoreCase) && seenSearchTerms.Add(pastQuery))
                {
                    results.Add(new OmniboxSuggestionItem
                    {
                        Kind = OmniboxSuggestionKind.RecentSearch,
                        PrimaryText = pastQuery,
                        SecondaryText = string.Empty,
                        TargetInputOrUrl = pastQuery,
                        IconText = "🕒",
                        IconBgHex = "#232332",
                        IconFgHex = "#A1A1AA",
                        HistoryEntryId = entry.Id
                    });
                    recentSearchCount++;
                }
            }
        }

        // 5. Sugerencias predictivas en vivo (🔍)
        if (liveGoogleSuggestions != null && !looksLikeUrl)
        {
            int liveCount = 0;
            foreach (string suggestion in liveGoogleSuggestions)
            {
                if (liveCount >= 3 || results.Count >= maxTotalItems) break;
                string cleanSug = suggestion.Trim();
                if (string.IsNullOrEmpty(cleanSug) || !seenSearchTerms.Add(cleanSug)) continue;

                results.Add(new OmniboxSuggestionItem
                {
                    Kind = OmniboxSuggestionKind.LiveSearchSuggestion,
                    PrimaryText = cleanSug,
                    SecondaryText = string.Empty,
                    TargetInputOrUrl = cleanSug,
                    IconText = "🔍",
                    IconBgHex = "#232332",
                    IconFgHex = "#A1A1AA"
                });
                liveCount++;
            }
        }

        return results;
    }

    private static (string Icon, string BgHex, string FgHex) GetSiteBadgeStyle(string url, bool isBookmark)
    {
        if (url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            return ("▶", "#DC2626", "#FFFFFF");
        }
        if (url.Contains("google.", StringComparison.OrdinalIgnoreCase))
        {
            return ("G", "#1E293B", "#60A5FA");
        }
        if (url.Contains("twitch.tv", StringComparison.OrdinalIgnoreCase))
        {
            return ("👾", "#2E1065", "#C4B5FD");
        }
        if (url.Contains("x.com", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("twitter.com", StringComparison.OrdinalIgnoreCase))
        {
            return ("𝕏", "#18181B", "#FFFFFF");
        }
        if (isBookmark)
        {
            return ("★", "#1E1B4B", "#818CF8");
        }
        return ("🌐", "#232332", "#93C5FD");
    }

    public static string? BuildAutocompleteEndpointUrl(string query, string? searchEngine)
    {
        string encoded = Uri.EscapeDataString(query);
        string engine = string.IsNullOrWhiteSpace(searchEngine) ? "Google" : searchEngine.Trim();

        return engine switch
        {
            "DuckDuckGo" => $"https://duckduckgo.com/ac/?q={encoded}&type=list",
            "Bing" => $"https://api.bing.com/osjson.aspx?query={encoded}",
            "Brave" => $"https://search.brave.com/api/suggest?q={encoded}",
            "Google" => $"https://suggestqueries.google.com/complete/search?client=firefox&hl=es&q={encoded}",
            _ => null
        };
    }

    public static Task<IReadOnlyList<string>> FetchGoogleAutocompleteAsync(string query, CancellationToken cancellationToken)
        => FetchSearchSuggestionsAsync(query, SettingsManager.Current.DefaultSearchEngine, cancellationToken);

    public static async Task<IReadOnlyList<string>> FetchSearchSuggestionsAsync(
        string query,
        string? searchEngine,
        CancellationToken cancellationToken)
    {
        if (!SettingsManager.Current.EnableSearchSuggestions)
            return Array.Empty<string>();

        string clean = (query ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(clean) || clean.Length < 1 || clean.Length > 100)
            return Array.Empty<string>();

        // D3: Si el usuario está escribiendo una dirección web o dominio, no enviar la consulta al buscador
        if (LooksLikeUrlOrDomain(clean))
            return Array.Empty<string>();

        string engineKey = string.IsNullOrWhiteSpace(searchEngine) ? "Google" : searchEngine.Trim();
        string? endpointUrl = BuildAutocompleteEndpointUrl(clean, engineKey);
        if (string.IsNullOrEmpty(endpointUrl))
            return Array.Empty<string>();

        string cacheKey = $"{engineKey}::{clean}";
        lock (_cacheLock)
        {
            if (_liveSuggestCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        try
        {
            using var response = await _suggestClient.GetAsync(endpointUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Array.Empty<string>();

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() < 2)
                return Array.Empty<string>();

            var suggestionsArray = doc.RootElement[1];
            if (suggestionsArray.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            var list = new List<string>();
            foreach (var item in suggestionsArray.EnumerateArray())
            {
                string? s = null;
                if (item.ValueKind == JsonValueKind.String)
                {
                    s = item.GetString();
                }
                else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("phrase", out var phraseProp))
                {
                    s = phraseProp.GetString();
                }

                if (!string.IsNullOrWhiteSpace(s))
                {
                    list.Add(s);
                    if (list.Count >= 6) break;
                }
            }

            lock (_cacheLock)
            {
                if (!_liveSuggestCache.ContainsKey(cacheKey))
                {
                    if (_cacheLruOrder.Count >= MaxCacheEntries && _cacheLruOrder.First != null)
                    {
                        _liveSuggestCache.Remove(_cacheLruOrder.First.Value);
                        _cacheLruOrder.RemoveFirst();
                    }
                    _cacheLruOrder.AddLast(cacheKey);
                }
                _liveSuggestCache[cacheKey] = list;
            }

            return list;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static readonly string[] PopularDomains =
    {
        "youtube.com", "google.com", "github.com", "facebook.com", "instagram.com",
        "twitter.com", "x.com", "reddit.com", "wikipedia.org", "tiktok.com",
        "twitch.tv", "netflix.com", "amazon.com", "dailymotion.com", "spotify.com",
        "discord.com", "chatgpt.com", "openai.com", "web.whatsapp.com", "whatsapp.com",
        "linkedin.com", "bing.com", "duckduckgo.com"
    };

    /// <summary>
    /// Devuelve la mejor coincidencia para autocompletado predictivo en línea en el Omnibox.
    /// Usa las propiedades precalculadas de HistoryEntry y acota el recorrido a 2500 entradas.
    /// </summary>
    public static string? GetInlineAutocompleteMatch(
        string? rawInput,
        IReadOnlyList<HistoryEntry>? historyEntries,
        IReadOnlyList<BookmarkItem>? bookmarks = null)
    {
        if (string.IsNullOrWhiteSpace(rawInput)) return null;
        string query = rawInput.TrimStart();
        if (query.Length < 1 || query.EndsWith(' ')) return null;

        string qLower = query.ToLowerInvariant();

        // 1. Coincidencia en Marcadores
        if (bookmarks != null)
        {
            foreach (var b in bookmarks)
            {
                if (b == null || string.IsNullOrWhiteSpace(b.Url)) continue;
                string clean = FormatCleanDisplayUrl(b.Url);
                if (clean.StartsWith(qLower, StringComparison.OrdinalIgnoreCase) && clean.Length > query.Length)
                {
                    int slashIdx = clean.IndexOf('/');
                    if (slashIdx > 0)
                    {
                        string domainOnly = clean.Substring(0, slashIdx);
                        if (domainOnly.StartsWith(qLower, StringComparison.OrdinalIgnoreCase) && domainOnly.Length > query.Length)
                            return domainOnly;
                    }
                    return clean;
                }
            }
        }

        // 2. Coincidencia en Historial de navegación (acotado y usando valores precalculados)
        if (historyEntries != null && historyEntries.Count > 0)
        {
            int scanLimit = Math.Min(historyEntries.Count, MaxHistoryScanEntries);

            // Primero buscar coincidencia en dominios/URLs del historial
            for (int i = 0; i < scanLimit; i++)
            {
                var h = historyEntries[i];
                if (h == null || string.IsNullOrWhiteSpace(h.Url)) continue;
                string clean = h.CleanDisplayUrl;
                if (h.CleanDisplayUrlLower.StartsWith(qLower, StringComparison.Ordinal) && clean.Length > query.Length)
                {
                    int slashIdx = clean.IndexOf('/');
                    if (slashIdx > 0)
                    {
                        string domainOnly = clean.Substring(0, slashIdx);
                        if (domainOnly.StartsWith(qLower, StringComparison.OrdinalIgnoreCase) && domainOnly.Length > query.Length)
                            return domainOnly;
                    }
                    return clean;
                }
            }

            // Segundo buscar en búsquedas previas del historial
            for (int i = 0; i < scanLimit; i++)
            {
                var h = historyEntries[i];
                if (h == null) continue;
                string? prevSearch = h.ExtractedSearchQuery;
                if (!string.IsNullOrWhiteSpace(prevSearch) &&
                    prevSearch.StartsWith(qLower, StringComparison.OrdinalIgnoreCase) &&
                    prevSearch.Length > query.Length)
                {
                    return prevSearch;
                }
            }
        }

        // 3. Coincidencia en Dominios Populares Globales
        foreach (string domain in PopularDomains)
        {
            if (domain.StartsWith(qLower, StringComparison.OrdinalIgnoreCase) && domain.Length > query.Length)
            {
                return domain;
            }
        }

        return null;
    }
}
