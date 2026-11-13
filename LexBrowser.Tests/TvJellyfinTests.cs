using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LexBrowser.Core.Tv;
using Xunit;

namespace LexBrowser.Tests;

public sealed class TvJellyfinTests
{
    [Fact]
    public void ParseJellyfinItems_ExtractsMoviesAndEpisodesCorrectly()
    {
        string json = @"{
            ""Items"": [
                {
                    ""Id"": ""movie123"",
                    ""Name"": ""Inception"",
                    ""Type"": ""Movie""
                },
                {
                    ""Id"": ""ep456"",
                    ""Name"": ""Pilot"",
                    ""Type"": ""Episode"",
                    ""SeriesName"": ""Breaking Bad"",
                    ""ParentIndexNumber"": 1,
                    ""IndexNumber"": 1
                }
            ]
        }";

        var items = TvJellyfinService.ParseJellyfinItems(json, "http://localhost:8096", "test_key");

        Assert.Equal(2, items.Count);

        var movie = items.FirstOrDefault(i => i.Id == "jellyfin_movie123");
        Assert.NotNull(movie);
        Assert.Equal("Inception", movie.Title);
        Assert.False(movie.IsSeries);
        Assert.Equal("http://localhost:8096/Videos/movie123/stream.mp4?api_key=test_key", movie.Url);

        var episode = items.FirstOrDefault(i => i.Id == "jellyfin_ep456");
        Assert.NotNull(episode);
        Assert.True(episode.IsSeries);
        Assert.Equal("Breaking Bad", episode.SeriesTitle);
        Assert.Equal(1, episode.SeasonNumber);
        Assert.Equal(1, episode.EpisodeNumber);
        Assert.Equal("Pilot", episode.EpisodeTitle);
    }

    [Fact]
    public void ParseJellyfinItems_HandlesEmptyOrInvalidJsonSafely()
    {
        var items1 = TvJellyfinService.ParseJellyfinItems("", "http://localhost:8096", null);
        Assert.Empty(items1);

        var items2 = TvJellyfinService.ParseJellyfinItems("{}", "http://localhost:8096", null);
        Assert.Empty(items2);
    }

    [Fact]
    public async Task TestConnectionAsync_ReturnsFalseForInvalidUrl()
    {
        bool res1 = await TvJellyfinService.Shared.TestConnectionAsync("", "", CancellationToken.None);
        Assert.False(res1);

        bool res2 = await TvJellyfinService.Shared.TestConnectionAsync("not-a-valid-url", "", CancellationToken.None);
        Assert.False(res2);
    }
}

