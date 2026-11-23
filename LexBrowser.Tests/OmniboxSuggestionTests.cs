using System.Collections.Generic;
using LexBrowser.Core;
using LexBrowser.UI.Models;
using Xunit;

namespace LexBrowser.Tests;

public class OmniboxSuggestionTests
{
    [Theory]
    [InlineData("https://www.google.com/search?q=juegos+battle+royale&oq=juegos", "juegos battle royale")]
    [InlineData("https://duckduckgo.com/?q=juegos+de+2+jugadores", "juegos de 2 jugadores")]
    [InlineData("https://www.youtube.com/watch?v=12345", null)]
    public void TryExtractSearchQueryFromUrl_ExtractsSearchQueriesCorrectly(string url, string? expected)
    {
        string? actual = OmniboxSuggestionService.TryExtractSearchQueryFromUrl(url);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildSuggestions_CombinesPrimarySearch_RecentSearches_LiveSuggestions_AndVisitedYouTubePages()
    {
        var history = new List<HistoryEntry>
        {
            new() { Id = "h1", Url = "https://www.google.com/search?q=juegos+battle+royale", Title = "juegos battle royale - Buscar con Google" },
            new() { Id = "h2", Url = "https://www.youtube.com/results?search_query=ahora+los+juegos", Title = "ahora los juegos se pueden jugar en el navegador - YouTube" }
        };

        var live = new List<string> { "juegos gratis para pc" };

        var suggestions = OmniboxSuggestionService.BuildSuggestions(
            rawInput: "juegos",
            historyEntries: history,
            bookmarks: null,
            liveGoogleSuggestions: live,
            searchEngineName: "Google");

        Assert.Equal(4, suggestions.Count);

        // 1. Búsqueda principal seleccionada
        Assert.Equal(OmniboxSuggestionKind.PrimarySearchOrUrl, suggestions[0].Kind);
        Assert.Equal("juegos", suggestions[0].PrimaryText);
        Assert.Equal(" - Búsqueda de Google", suggestions[0].SecondaryText);
        Assert.True(suggestions[0].IsSelected);

        // 2. Página/video ya visitado en YouTube con ranking prioritario (history-first ranking)
        Assert.Equal(OmniboxSuggestionKind.VisitedPage, suggestions[1].Kind);
        Assert.Equal("▶", suggestions[1].IconText);
        Assert.Contains("ahora los juegos", suggestions[1].PrimaryText);
        Assert.Contains("youtube.com/results", suggestions[1].SecondaryText);

        // 3. Búsqueda reciente con icono de reloj 🕒
        Assert.Equal(OmniboxSuggestionKind.RecentSearch, suggestions[2].Kind);
        Assert.Equal("juegos battle royale", suggestions[2].PrimaryText);
        Assert.Equal("🕒", suggestions[2].IconText);

        // 4. Sugerencia predictiva en vivo de Google 🔍
        Assert.Equal(OmniboxSuggestionKind.LiveSearchSuggestion, suggestions[3].Kind);
        Assert.Equal("juegos gratis para pc", suggestions[3].PrimaryText);
    }

    [Theory]
    [InlineData("you", "youtube.com")]
    [InlineData("git", "github.com")]
    [InlineData("goog", "google.com")]
    [InlineData("wha", "whatsapp.com")]
    [InlineData("web.wh", "web.whatsapp.com")]
    public void GetInlineAutocompleteMatch_MatchesPopularDomains(string input, string expected)
    {
        string? match = OmniboxSuggestionService.GetInlineAutocompleteMatch(input, null, null);
        Assert.Equal(expected, match);
    }

    [Fact]
    public void GetInlineAutocompleteMatch_PrioritizesBookmarksAndHistory()
    {
        var bookmarks = new List<BookmarkItem>
        {
            new() { Url = "https://customproject.org/app", Title = "Custom App" }
        };

        var history = new List<HistoryEntry>
        {
            new() { Url = "https://myworkstation.net/dashboard", Title = "Work Dashboard" }
        };

        // Bookmark match
        string? matchBm = OmniboxSuggestionService.GetInlineAutocompleteMatch("custom", history, bookmarks);
        Assert.Equal("customproject.org", matchBm);

        // History match
        string? matchHist = OmniboxSuggestionService.GetInlineAutocompleteMatch("mywork", history, bookmarks);
        Assert.Equal("myworkstation.net", matchHist);

        // Space at end suppresses inline autocomplete
        string? matchSpace = OmniboxSuggestionService.GetInlineAutocompleteMatch("you ", history, bookmarks);
        Assert.Null(matchSpace);
    }

    [Fact]
    public void NormalizePastedAddressBarText_PreservesAllLinesAndStripsZeroWidth()
    {
        string pasted = "  primera linea\r\n\tsegunda\u200B linea\n\ntercera\u00A0linea  ";
        string normalized = OmniboxInputResolver.NormalizePastedAddressBarText(pasted);
        Assert.Equal("primera linea segunda linea tercera linea", normalized);

        string resolved = OmniboxInputResolver.ResolveNavigationTarget(pasted, q => $"https://www.google.com/search?q={System.Uri.EscapeDataString(q)}");
        Assert.Equal("https://www.google.com/search?q=primera%20linea%20segunda%20linea%20tercera%20linea", resolved);
    }

    [Theory]
    [InlineData("3.14", false)]
    [InlineData("v2.0", false)]
    [InlineData("1.2.3", false)]
    [InlineData("github.com/dotnet/runtime", true)]
    [InlineData("localhost:3000", true)]
    [InlineData("192.168.1.1:8080", true)]
    [InlineData("intranet:9000", true)]
    public void LooksLikeUrlOrDomain_DistinguishesNumbersFromRealHosts(string input, bool expectedIsUrl)
    {
        Assert.Equal(expectedIsUrl, OmniboxSuggestionService.LooksLikeUrlOrDomain(input));
    }
}

