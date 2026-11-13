using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LexBrowser.Core.Tv.Vod.Contracts;
using LexBrowser.Core.Tv.Vod.Metadata;
using LexBrowser.Core.Tv.Vod.Providers;
using Xunit;

namespace LexBrowser.Tests;

public class TvVodModularTests
{
    [Fact]
    public void TmdbMetaService_KeyConfiguration_SavesAndMasks()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "lex_tmdb_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var svc = new TmdbMetaService(tempDir);
            string testKey = "tmdb_secret_key_1234567890abcdef";

            svc.SetApiKey(testKey);

            Assert.True(svc.IsConfigured);
            string masked = svc.GetMaskedKey();
            Assert.StartsWith("tmdb_secret_k", masked);
            Assert.EndsWith("cdef", masked);
            Assert.Contains("...", masked);

            // Limpiar clave
            svc.SetApiKey("");
            Assert.False(svc.IsConfigured);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TmdbMetaService_ParseDiscoverJson_FiltersFutureReleasesAndFormatsUrls()
    {
        string sampleJson = @"{
            ""page"": 1,
            ""results"": [
                {
                    ""id"": 12,
                    ""title"": ""Buscando a Nemo"",
                    ""overview"": ""Un pez payaso busca a su hijo en el océano."",
                    ""poster_path"": ""/nemo.jpg"",
                    ""backdrop_path"": ""/nemo_bg.jpg"",
                    ""release_date"": ""2003-05-30"",
                    ""vote_average"": 8.2,
                    ""genre_ids"": [16, 10751]
                },
                {
                    ""id"": 99999,
                    ""title"": ""Película Futurista No Estrenada"",
                    ""overview"": ""Estreno en el año 2099."",
                    ""poster_path"": ""/future.jpg"",
                    ""release_date"": ""2099-01-01"",
                    ""vote_average"": 0
                }
            ],
            ""total_results"": 2,
            ""total_pages"": 1
        }";

        var items = TmdbMetaService.ParseDiscoverResults(sampleJson, "Pixar Animation Studios");

        Assert.Single(items);
        var nemo = items[0];
        Assert.Equal("tmdb:12", nemo.Id);
        Assert.Equal("Buscando a Nemo", nemo.Title);
        Assert.Equal(2003, nemo.Year);
        Assert.Equal("https://image.tmdb.org/t/p/w500/nemo.jpg", nemo.PosterUrl);
        Assert.Equal("https://image.tmdb.org/t/p/w1280/nemo_bg.jpg", nemo.BackdropUrl);
        Assert.Equal(8.2, nemo.Rating);
        Assert.Equal("Pixar Animation Studios", nemo.SourceName);
    }

    [Fact]
    public void TmdbMetaService_ParseMovieDetails_ExtractsPeruWatchProviders()
    {
        string sampleMovieJson = @"{
            ""id"": 12,
            ""title"": ""Buscando a Nemo"",
            ""overview"": ""Un pez payaso busca a su hijo."",
            ""poster_path"": ""/nemo.jpg"",
            ""release_date"": ""2003-05-30"",
            ""runtime"": 100,
            ""vote_average"": 8.2,
            ""genres"": [{ ""id"": 16, ""name"": ""Animación"" }]
        }";

        string sampleProvidersJson = @"{
            ""id"": 12,
            ""results"": {
                ""US"": {
                    ""link"": ""https://www.themoviedb.org/movie/12/watch?locale=en-US"",
                    ""flatrate"": [
                        { ""provider_id"": 337, ""provider_name"": ""Disney Plus"", ""logo_path"": ""/dp.jpg"" }
                    ]
                },
                ""PE"": {
                    ""link"": ""https://www.themoviedb.org/movie/12/watch?locale=es-PE"",
                    ""flatrate"": [
                        { ""provider_id"": 337, ""provider_name"": ""Disney Plus"", ""logo_path"": ""/dp.jpg"" }
                    ],
                    ""buy"": [
                        { ""provider_id"": 2, ""provider_name"": ""Apple TV"", ""logo_path"": ""/apple.jpg"" }
                    ]
                }
            }
        }";

        var item = TmdbMetaService.ParseMovieDetails(sampleMovieJson, sampleProvidersJson, "tmdb:12");

        Assert.NotNull(item);
        Assert.NotNull(item.WatchProviders);
        Assert.Contains(item.WatchProviders, p => p.Name == "Disney Plus" && p.Type == "flatrate");
        Assert.Contains(item.WatchProviders, p => p.Name == "Apple TV" && p.Type == "buy");
        Assert.Equal("https://www.themoviedb.org/movie/12/watch?locale=es-PE", item.OfficialWatchUrl);
        Assert.Contains("TMDB", item.Attribution);
    }

    [Fact]
    public void AniListMetaService_ParseDiscoverJson_ExtractsAnimeMetadata()
    {
        string sampleGraphql = @"{
            ""data"": {
                ""Page"": {
                    ""pageInfo"": {
                        ""total"": 500,
                        ""currentPage"": 1,
                        ""hasNextPage"": true
                    },
                    ""media"": [
                        {
                            ""id"": 16498,
                            ""title"": {
                                ""romaji"": ""Shingeki no Kyojin"",
                                ""english"": ""Attack on Titan"",
                                ""native"": ""進撃の巨人""
                            },
                            ""format"": ""TV"",
                            ""status"": ""FINISHED"",
                            ""description"": ""Several hundred years ago, humans were nearly exterminated by titans."",
                            ""seasonYear"": 2013,
                            ""episodes"": 25,
                            ""duration"": 24,
                            ""coverImage"": {
                                ""extraLarge"": ""https://s4.anilist.co/file/aot_xl.jpg"",
                                ""large"": ""https://s4.anilist.co/file/aot_l.jpg"",
                                ""medium"": ""https://s4.anilist.co/file/aot_m.jpg""
                            },
                            ""bannerImage"": ""https://s4.anilist.co/file/aot_banner.jpg"",
                            ""genres"": [""Action"", ""Drama"", ""Fantasy"", ""Mystery""],
                            ""averageScore"": 85
                        }
                    ]
                }
            }
        }";

        var items = AniListMetaService.ParseAniListResults(sampleGraphql);

        Assert.Single(items);
        var aot = items[0];
        Assert.Equal("anilist:16498", aot.Id);
        Assert.Equal("Attack on Titan", aot.Title);
        Assert.Equal("anime", aot.Type);
        Assert.Equal(2013, aot.Year);
        Assert.Equal(8.5, aot.Rating);
        Assert.Equal("https://s4.anilist.co/file/aot_xl.jpg", aot.PosterUrl);
        Assert.Equal("https://s4.anilist.co/file/aot_banner.jpg", aot.BackdropUrl);
        Assert.NotNull(aot.AnimeMeta);
        Assert.Equal("Shingeki no Kyojin", aot.AnimeMeta.RomajiTitle);
        Assert.Equal("進撃の巨人", aot.AnimeMeta.JapaneseTitle);
        Assert.Equal(25, aot.AnimeMeta.TotalEpisodes);
    }

    [Fact]
    public async Task VodProviderManager_AggregatesStreamsFromInstalledProviders()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "lex_vod_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var mgr = new VodProviderManager(tempDir);
            // El proveedor legalFree viene registrado por defecto
            var manifest = mgr.GetInstalledProviders().FirstOrDefault(p => p.Id == "org.lex.legalfree");
            Assert.NotNull(manifest);

            // Consulta de streams para un id de prueba
            var streams = await mgr.GetAggregatedStreamsAsync("series", "ia.Sherlock_S01E01");
            Assert.NotNull(streams);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ReleaseAll_ZeroesVodMemoryWithoutErrors()
    {
        VodProviderManager.Shared.ReleaseMemoryCache();
        TmdbMetaService.Shared.ReleaseMemoryCache();
        AniListMetaService.Shared.ReleaseMemoryCache();
    }
}
