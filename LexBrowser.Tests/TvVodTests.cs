using System;
using System.IO;
using System.Linq;
using LexBrowser.Core.Tv;
using Xunit;

namespace LexBrowser.Tests;

public sealed class TvVodTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TvVodStore _store;

    public TvVodTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LexVodTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _store = new TvVodStore(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch { }
    }

    [Theory]
    [InlineData("Breaking Bad S01E02 - Cat's in the Bag", "Breaking Bad", 1, 2, "Cat's in the Bag")]
    [InlineData("Friends 1x03 - The One with the Thumb", "Friends", 1, 3, "The One with the Thumb")]
    [InlineData("Lost Temp 1 Ep 2", "Lost", 1, 2, null)]
    [InlineData("Stranger Things Season 2 Episode 5", "Stranger Things", 2, 5, null)]
    [InlineData("Loki 2x01 - O.B.", "Loki", 2, 1, "O.B.")]
    [InlineData("Attack on Titan S03E15", "Attack on Titan", 3, 15, null)]
    public void DetectSeriesInfo_IdentifiesSeriesPatternsCorrectly(
        string rawTitle, string expectedSeries, int expectedSeason, int expectedEpisode, string? expectedEpTitle)
    {
        var (isSeries, series, season, episode, epTitle) = TvVodStore.DetectSeriesInfo(rawTitle);

        Assert.True(isSeries);
        Assert.Equal(expectedSeries, series);
        Assert.Equal(expectedSeason, season);
        Assert.Equal(expectedEpisode, episode);
        if (expectedEpTitle != null)
        {
            Assert.Equal(expectedEpTitle, epTitle);
        }
    }

    [Theory]
    [InlineData("The Matrix (1999)")]
    [InlineData("Interstellar 1080p")]
    [InlineData("Pulp Fiction")]
    [InlineData("")]
    public void DetectSeriesInfo_ReturnsFalseForMovies(string movieTitle)
    {
        var (isSeries, series, season, episode, epTitle) = TvVodStore.DetectSeriesInfo(movieTitle);
        Assert.False(isSeries);
        Assert.Null(series);
        Assert.Null(season);
        Assert.Null(episode);
    }

    [Fact]
    public void TvVodStore_ImportItemsAndDeduplicate_WorksCorrectly()
    {
        var items = new[]
        {
            new TvVodItem { Id = "1", Title = "Movie A", Url = "https://example.com/a.mp4", Group = "Películas" },
            new TvVodItem { Id = "2", Title = "Movie A Dup", Url = "https://example.com/a.mp4", Group = "Películas" },
            new TvVodItem { Id = "3", Title = "Movie B", Url = "https://example.com/b.mp4", Group = "Películas" }
        };

        int added = _store.ImportItems(items);
        Assert.Equal(2, added);

        var snapshot = _store.GetLibrarySnapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Contains(snapshot, i => i.Title == "Movie A");
        Assert.Contains(snapshot, i => i.Title == "Movie B");
    }

    [Fact]
    public void TvVodStore_ProgressTracking_ThresholdsAreRespected()
    {
        string url = "https://example.com/video.mp4";

        // 1. Menos de 30 segundos no se guarda
        _store.SaveProgress(url, 25.0, 1000.0);
        Assert.Null(_store.GetProgressForUrl(url));

        // 2. Entre 30 segundos y 95% de duración se guarda
        _store.SaveProgress(url, 120.0, 1000.0);
        var p = _store.GetProgressForUrl(url);
        Assert.NotNull(p);
        Assert.Equal(120.0, p.PositionSec);
        Assert.Equal(1000.0, p.DurationSec);

        // 3. Al llegar al 95% o más, se marca como terminado (se elimina del progreso)
        _store.SaveProgress(url, 960.0, 1000.0);
        Assert.Null(_store.GetProgressForUrl(url));
    }

    [Fact]
    public void TvVodStore_RemoveAndClear_WorksProperly()
    {
        var items = new[]
        {
            new TvVodItem { Id = "item1", Title = "Movie 1", Url = "https://example.com/1.mp4" },
            new TvVodItem { Id = "item2", Title = "Movie 2", Url = "https://example.com/2.mp4" }
        };
        _store.ImportItems(items);
        Assert.Equal(2, _store.GetLibrarySnapshot().Count);

        bool removed = _store.RemoveItem("item1");
        Assert.True(removed);
        Assert.Single(_store.GetLibrarySnapshot());

        _store.ClearLibrary();
        Assert.Empty(_store.GetLibrarySnapshot());
    }

    [Fact]
    public void TvVodService_ImportFromText_ParsesM3uAndClassifiesCorrectly()
    {
        string m3uContent = @"#EXTM3U
#EXTINF:-1 tvg-logo=""https://example.com/bb.png"" group-title=""Series"",Breaking Bad S01E01 - Pilot
https://example.com/bb_s01e01.mp4
#EXTINF:-1 tvg-logo=""https://example.com/matrix.png"" group-title=""Sci-Fi"",The Matrix (1999)
https://example.com/matrix.mp4
#EXTINF:-1,Invalid Local IP
http://192.168.1.100/video.mp4";

        var res = TvVodService.Shared.ImportFromText(m3uContent, "VOD", store: _store);

        Assert.Equal(2, res.AddedCount);
        Assert.Equal(1, res.SkippedPrivate);

        var snap = _store.GetLibrarySnapshot();
        var bb = snap.FirstOrDefault(i => i.Title.Contains("Breaking Bad"));
        Assert.NotNull(bb);
        Assert.True(bb.IsSeries);
        Assert.Equal("Breaking Bad", bb.SeriesTitle);
        Assert.Equal(1, bb.SeasonNumber);
        Assert.Equal(1, bb.EpisodeNumber);

        var matrix = snap.FirstOrDefault(i => i.Title.Contains("The Matrix"));
        Assert.NotNull(matrix);
        Assert.False(matrix.IsSeries);
    }
}

