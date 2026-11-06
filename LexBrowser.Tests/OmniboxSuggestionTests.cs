using System.Collections.Generic;
using LexBrowser.Core;
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
}

