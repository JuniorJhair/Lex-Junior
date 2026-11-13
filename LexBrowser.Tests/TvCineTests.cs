using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LexBrowser.Core.Tv;
using LexBrowser.Core.Tv.Cine;
using Xunit;

namespace LexBrowser.Tests;

public class TvCineTests
{
    [Theory]
    [InlineData("biskyt.github.io")]
    [InlineData("raw.githubusercontent.com")]
    [InlineData("BISKYT.GITHUB.IO")]
    public void TvUrlGuard_AllowsCineCatalogHost(string host)
    {
        Assert.True(TvUrlGuard.IsAllowedCineCatalogHost(host));
    }

    [Theory]
    [InlineData("evil.com")]
    [InlineData("iptv-org.github.io")]
    [InlineData("archive.org")]
    [InlineData("localhost")]
    [InlineData(null)]
    [InlineData("")]
    public void TvUrlGuard_RejectsDisallowedCatalogHost(string? host)
    {
        Assert.False(TvUrlGuard.IsAllowedCineCatalogHost(host));
    }

    [Theory]
    [InlineData("archive.org")]
    [InlineData("ia800100.us.archive.org")]
    [InlineData("dn790001.ca.archive.org")]
    [InlineData("upload.wikimedia.org")]
    public void TvUrlGuard_AllowsCineVideoHost(string host)
    {
        Assert.True(TvUrlGuard.IsAllowedCineVideoHost(host));
    }

    [Theory]
    [InlineData("youtube.com")]
    [InlineData("vimeo.com")]
    [InlineData("biskyt.github.io")]
    [InlineData("evilarchive.org")]
    [InlineData("notarchive.org.evil.com")]
    [InlineData(null)]
    [InlineData("")]
    public void TvUrlGuard_RejectsDisallowedCineVideoHost(string? host)
    {
        Assert.False(TvUrlGuard.IsAllowedCineVideoHost(host));
    }

    [Theory]
    [InlineData("https://upload.wikimedia.org/wikipedia/commons/1/1a/video.webm")]
    [InlineData("https://archive.org/download/sample_movie/sample.mp4")]
    [InlineData("https://dn790001.ca.archive.org/0/items/sample_movie/sample.mp4")]
    public void TvUrlGuard_AllowsCineMediaUrl(string url)
    {
        Assert.True(TvUrlGuard.IsAllowedCineMediaUrl(url, out var uri));
        Assert.NotNull(uri);
    }

    [Theory]
    [InlineData("http://archive.org/download/sample/sample.mp4")] // HTTP inseguro rechazado para Cine VOD
    [InlineData("https://evil.com/video.mp4")]
    [InlineData("https://192.168.1.1/video.mp4")]
    [InlineData("https://localhost/video.mp4")]
    [InlineData("ftp://archive.org/video.mp4")]
    [InlineData("")]
    [InlineData(null)]
    public void TvUrlGuard_RejectsInvalidOrInsecureCineMediaUrl(string? url)
    {
        Assert.False(TvUrlGuard.IsAllowedCineMediaUrl(url, out _));
    }

    [Fact]
    public void TvCineService_NormalizeForSearch_StripsDiacriticsAndCasing()
    {
        Assert.Equal("el hotel electrico", TvCineService.NormalizeForSearch("El Hotel Eléctrico"));
        Assert.Equal("pelicula de accion", TvCineService.NormalizeForSearch("Película de ACCIÓN"));
        Assert.Equal("fantasio", TvCineService.NormalizeForSearch("  Fantasío  "));
        Assert.Equal("", TvCineService.NormalizeForSearch(""));
        Assert.Equal("", TvCineService.NormalizeForSearch(null));
    }

    [Fact]
    public void TvCineService_ParseCatalogJson_AccuratelyExtractsAndDiscards()
    {
        string json = @"
        {
            ""generated"": ""2026-11-12T00:00:00Z"",
            ""items"": [
                {
                    ""id"": ""ia.ValidMovie1"",
                    ""kind"": ""movie"",
                    ""title"": ""El Hotel Eléctrico"",
                    ""year"": 1908,
                    ""duration"": 540,
                    ""group"": ""Película: Ciencia Ficción, Fantasía"",
                    ""languages"": [""Español""],
                    ""url"": ""https://archive.org/download/hotel/hotel.mp4"",
                    ""license"": ""Public Domain"",
                    ""source"": ""Internet Archive""
                },
                {
                    ""id"": ""wiki.ValidMovie2"",
                    ""kind"": ""movie"",
                    ""title"": ""Big Buck Bunny"",
                    ""year"": 2008,
                    ""duration"": 596,
                    ""group"": ""Película: Animación, Comedia"",
                    ""languages"": [""English""],
                    ""url"": ""https://upload.wikimedia.org/wikipedia/commons/bbb.webm"",
                    ""license"": ""CC-BY 3.0"",
                    ""source"": ""Wikimedia Commons""
                },
                {
                    ""id"": ""wiki.Series1_Ep1"",
                    ""kind"": ""series"",
                    ""title"": ""Episodio 1: El Escándalo"",
                    ""series-name"": ""Sherlock Holmes"",
                    ""season"": ""1"",
                    ""episode"": ""1"",
                    ""year"": 1954,
                    ""duration"": 1500,
                    ""url"": ""https://upload.wikimedia.org/wikipedia/commons/sh_s1e1.webm""
                },
                {
                    ""id"": ""wiki.Series1_Ep2"",
                    ""kind"": ""series"",
                    ""title"": ""Episodio 2: La Liga de los Pelirrojos"",
                    ""series-name"": ""Sherlock Holmes"",
                    ""season"": ""1"",
                    ""episode"": ""2"",
                    ""year"": 1954,
                    ""duration"": 1500,
                    ""url"": ""https://upload.wikimedia.org/wikipedia/commons/sh_s1e2.webm""
                },
                {
                    ""id"": ""ia.DiscardHttp"",
                    ""kind"": ""movie"",
                    ""title"": ""Insecure Movie"",
                    ""url"": ""http://archive.org/download/insecure/insecure.mp4""
                },
                {
                    ""id"": ""ia.DiscardHost"",
                    ""kind"": ""movie"",
                    ""title"": ""Evil Host Movie"",
                    ""url"": ""https://evil-storage.com/movie.mp4""
                },
                {
                    ""id"": ""ia.DiscardNoTitle"",
                    ""kind"": ""movie"",
                    ""title"": ""   "",
                    ""url"": ""https://archive.org/download/notitle/notitle.mp4""
                },
                {
                    ""id"": ""ia.DiscardLive"",
                    ""kind"": ""live"",
                    ""title"": ""Live TV Stream"",
                    ""url"": ""https://archive.org/download/live/live.m3u8""
                }
            ]
        }";

        var (index, details, stats) = TvCineService.ParseCatalogJson(json);

        // 2 películas + 1 serie agregada = 3 items en el índice principal
        Assert.Equal(3, index.Count);
        Assert.Equal(8, stats.TotalProcessed);
        Assert.Equal(4, stats.TotalAccepted); // 2 películas + 2 episodios válidos
        Assert.Equal(1, stats.SkippedNonHttps);
        Assert.Equal(1, stats.SkippedDisallowedHost);
        Assert.Equal(1, stats.SkippedNoTitle);
        Assert.Equal(1, stats.SkippedLiveStream);

        // Validar agrupación de series
        var seriesItem = index.FirstOrDefault(i => i.Kind == "series");
        Assert.NotNull(seriesItem);
        Assert.Equal("Sherlock Holmes", seriesItem.Title);

        var seriesDetail = details[seriesItem.Id];
        Assert.NotNull(seriesDetail.Series);
        Assert.Single(seriesDetail.Series.Seasons);

        var s1 = seriesDetail.Series.Seasons[0];
        Assert.Equal(1, s1.SeasonNumber);
        Assert.Equal(2, s1.Episodes.Count);
        Assert.Equal(1, s1.Episodes[0].EpisodeNumber);
        Assert.Equal(2, s1.Episodes[1].EpisodeNumber);
    }

    [Fact]
    public void TvCineService_FilterIndex_CombinableFiltersWork()
    {
        var items = new List<TvCineIndexItem>
        {
            new() { Id = "1", Title = "El Hotel Eléctrico", TitleNormalized = "el hotel electrico", Kind = "movie", Year = 1908, Genres = new[] { "Ciencia Ficción", "Fantasía" }, Languages = new[] { "Español" }, MediaUrl = "https://archive.org/1.mp4" },
            new() { Id = "2", Title = "Night of the Living Dead", TitleNormalized = "night of the living dead", Kind = "movie", Year = 1968, Genres = new[] { "Terror" }, Languages = new[] { "English" }, MediaUrl = "https://archive.org/2.mp4" },
            new() { Id = "3", Title = "Big Buck Bunny", TitleNormalized = "big buck bunny", Kind = "movie", Year = 2008, Genres = new[] { "Animación" }, Languages = new[] { "English" }, MediaUrl = "https://upload.wikimedia.org/3.webm" }
        };

        // Búsqueda sin acentos
        var res1 = TvCineService.FilterIndex(items, new TvCineFilterQuery { Query = "electrico" });
        Assert.Single(res1);
        Assert.Equal("1", res1[0].Id);

        // Búsqueda con acentos
        var res2 = TvCineService.FilterIndex(items, new TvCineFilterQuery { Query = "eléctrico" });
        Assert.Single(res2);
        Assert.Equal("1", res2[0].Id);

        // Solo Wikimedia
        var res3 = TvCineService.FilterIndex(items, new TvCineFilterQuery { OnlyWikimedia = true });
        Assert.Single(res3);
        Assert.Equal("3", res3[0].Id);

        // Por década 1960s
        var res4 = TvCineService.FilterIndex(items, new TvCineFilterQuery { Decade = "1960s" });
        Assert.Single(res4);
        Assert.Equal("2", res4[0].Id);

        // Por género Fantasía
        var res5 = TvCineService.FilterIndex(items, new TvCineFilterQuery { Genre = "Fantasía" });
        Assert.Single(res5);
        Assert.Equal("1", res5[0].Id);
    }

    [Fact]
    public void TvCineStateStore_SaveAndReload_PersistsCorrectly()
    {
        string tmpFile = Path.Combine(Path.GetTempPath(), $"cine_test_{Guid.NewGuid():N}.json");
        try
        {
            var store = new TvCineStateStore(tmpFile);
            store.ToggleFavorite("title-1");
            store.ToggleWatchlist("title-2");
            store.ToggleWatched("title-3");
            store.UpdateProgress("title-4", 120, 600, "Series A", "Ep 1");
            store.MarkUnavailable("title-5", "dead", "HTTP 404");
            store.Flush();

            // Recargar en una nueva instancia del store
            var storeReloaded = new TvCineStateStore(tmpFile);
            var snapshot = storeReloaded.GetSnapshot();

            Assert.Contains("title-1", snapshot.Favorites);
            Assert.Contains("title-2", snapshot.Watchlist);
            Assert.Contains("title-3", snapshot.Watched);
            Assert.True(snapshot.Progress.ContainsKey("title-4"));
            Assert.Equal(120, snapshot.Progress["title-4"].PositionSec);
            Assert.Equal(600, snapshot.Progress["title-4"].DurationSec);
            Assert.Equal(20.0, snapshot.Progress["title-4"].Percent);
            Assert.Equal("Ep 1", storeReloaded.GetLastEpisode("Series A"));
            Assert.True(storeReloaded.IsUnavailable("title-5"));
        }
        finally
        {
            if (File.Exists(tmpFile)) File.Delete(tmpFile);
        }
    }

    [Fact]
    public void TvCineStateStore_CapsProgressEntriesAt5000()
    {
        string tmpFile = Path.Combine(Path.GetTempPath(), $"cine_cap_{Guid.NewGuid():N}.json");
        try
        {
            var store = new TvCineStateStore(tmpFile);
            for (int i = 0; i < 5050; i++)
            {
                store.SaveProgress($"prog-{i}", i, 1000);
            }
            store.Flush();

            var snapshot = store.GetSnapshot();
            Assert.True(snapshot.Progress.Count <= 5000);
        }
        finally
        {
            if (File.Exists(tmpFile)) File.Delete(tmpFile);
        }
    }

    [Fact]
    public void TvCineStateStore_UnavailableRecordExpirationRules()
    {
        var recRestrictedExpired = new TvCineUnavailableRecord
        {
            Kind = "restricted",
            FailedUtc = DateTime.UtcNow.AddHours(-25)
        };
        Assert.True(recRestrictedExpired.IsExpired());

        var recRestrictedFresh = new TvCineUnavailableRecord
        {
            Kind = "restricted",
            FailedUtc = DateTime.UtcNow.AddHours(-10)
        };
        Assert.False(recRestrictedFresh.IsExpired());

        var recDeadExpired = new TvCineUnavailableRecord
        {
            Kind = "dead",
            FailedUtc = DateTime.UtcNow.AddHours(-75)
        };
        Assert.True(recDeadExpired.IsExpired());

        var recCodecExpired = new TvCineUnavailableRecord
        {
            Kind = "codec",
            FailedUtc = DateTime.UtcNow.AddDays(-8)
        };
        Assert.True(recCodecExpired.IsExpired());
    }

    [Fact]
    public void TvStudioService_ReleaseAll_ZeroMemoryForCine()
    {
        // Forzar carga o estado en TvCineService y TvCineStateStore
        TvCineStateStore.Shared.ToggleFavorite("dummy-fav");
        TvStudioService.ReleaseAll();

        Assert.False(TvCineService.Shared.HasIndexLoaded);
    }
}
