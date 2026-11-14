using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LexBrowser.Core.Tv;
using LexBrowser.Core.Tv.Addons;
using Xunit;

namespace LexBrowser.Tests;

[Collection("PlaybackHints")]
public sealed class LexAddonTests : IDisposable
{
    private readonly string _tempDir;

    public LexAddonTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LexAddonTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Ignorar limpieza temporal en test
        }
    }

    private sealed class MockHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public MockHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    // -------------------------------------------------------------------------
    // 1. Normalización de URL de manifiesto
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("stremio://addons.example.org/manifest.json", "https://addons.example.org/manifest.json")]
    [InlineData("stremio://https://addons.example.org/manifest.json", "https://addons.example.org/manifest.json")]
    [InlineData("stremio://http://127.0.0.1:7000/manifest.json", "http://127.0.0.1:7000/manifest.json")]
    [InlineData("stremio://127.0.0.1:7000/manifest.json", "http://127.0.0.1:7000/manifest.json")]
    [InlineData("stremio://localhost:8787/vod", "http://localhost:8787/vod/manifest.json")]
    [InlineData("addons.example.org", "https://addons.example.org/manifest.json")]
    [InlineData("addons.example.org:8443/cfg", "https://addons.example.org:8443/cfg/manifest.json")]
    [InlineData("https://addons.example.org/v1/cfg", "https://addons.example.org/v1/cfg/manifest.json")]
    [InlineData("https://addons.example.org/my/manifest.json?tier=pro#frag", "https://addons.example.org/my/manifest.json?tier=pro")]
    public void TryNormalizeManifestInput_ValidInputs_AppendsManifestAndNormalizesScheme(string raw, string expected)
    {
        bool ok = LexAddonManifestParser.TryNormalizeManifestInput(raw, out string normalized, out string? error);

        Assert.True(ok, error);
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void TryNormalizeRepoInput_AllowsStremioScheme()
    {
        bool ok = LexAddonRepoParser.TryNormalizeRepoInput("stremio://repo.example.org/addons.json", out string normalized, out string? error);

        Assert.True(ok, error);
        Assert.Equal("https://repo.example.org/addons.json", normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("file:///C:/addons/manifest.json")]
    [InlineData("ftp://addons.example.org/manifest.json")]
    [InlineData("https://user:secret@addons.example.org/manifest.json")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:application/json,{}")]
    public void TryNormalizeManifestInput_InvalidOrDangerousInputs_ReturnsFalse(string raw)
    {
        bool ok = LexAddonManifestParser.TryNormalizeManifestInput(raw, out _, out string? error);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    // -------------------------------------------------------------------------
    // 2. Redacción segura de tokens en URL para UI y logs
    // -------------------------------------------------------------------------

    [Fact]
    public void RedactManifestUrlForDisplay_MasksLongConfigTokensAndQueryString()
    {
        string raw = "https://addons.example.org/eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9_secret_config/manifest.json?apikey=123456";

        string redacted = LexAddonManifestParser.RedactManifestUrlForDisplay(raw);

        Assert.DoesNotContain("eyJhbGciOi", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("123456", redacted, StringComparison.Ordinal);
        Assert.StartsWith("https://addons.example.org/", redacted, StringComparison.Ordinal);
        Assert.Contains("manifest.json", redacted, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // 3. Parsing y validación estricta de manifiestos JSON (Stremio + Lex)
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_StremioManifestWithStringAndObjectResources_NormalizesCorrectly()
    {
        string json = @"{
          ""id"": ""org.lex.public-cinema"",
          ""name"": ""Public Cinema"",
          ""version"": ""1.4.2"",
          ""description"": ""Catálogo legal de dominio público"",
          ""logo"": ""/assets/logo.png"",
          ""types"": [""Movie"", ""Series""],
          ""resources"": [
            ""catalog"",
            { ""name"": ""stream"", ""types"": [""movie""], ""idPrefixes"": [""tt"", ""lex:""] }
          ],
          ""catalogs"": [
            {
              ""type"": ""movie"",
              ""id"": ""top_public"",
              ""name"": ""Clásicos"",
              ""extra"": [
                { ""name"": ""search"", ""isRequired"": false },
                { ""name"": ""genre"", ""options"": [""Drama"", ""Sci-Fi""] }
              ],
              ""extraSupported"": [""skip""],
              ""extraRequired"": [""genre""]
            }
          ],
          ""behaviorHints"": { ""configurable"": true, ""configurationRequired"": false }
        }";

        var (ok, manifest, error) = LexAddonManifestParser.Parse(json, "https://addons.example.org/v2");

        Assert.True(ok, error);
        Assert.NotNull(manifest);
        Assert.Equal("org.lex.public-cinema", manifest!.Id);
        Assert.Equal("Public Cinema", manifest.Name);
        Assert.Equal("1.4.2", manifest.Version);
        Assert.Equal("https://addons.example.org/v2/assets/logo.png", manifest.Logo);
        Assert.Equal(2, manifest.Resources.Count);
        Assert.Contains(manifest.Resources, r => r.Name == "catalog");
        var streamRes = manifest.Resources.Single(r => r.Name == "stream");
        Assert.Contains("movie", streamRes.Types);
        Assert.Contains("tt", streamRes.IdPrefixes);

        var cat = Assert.Single(manifest.Catalogs);
        Assert.Equal("top_public", cat.Id);
        Assert.Contains(cat.Extra, e => e.Name == "search");
        Assert.Contains(cat.Extra, e => e.Name == "skip");
        var genreExtra = cat.Extra.Single(e => e.Name == "genre");
        Assert.True(genreExtra.IsRequired);
        Assert.Equal(new[] { "Drama", "Sci-Fi" }, genreExtra.Options);
        Assert.True(manifest.BehaviorHints.Configurable);
    }

    [Fact]
    public void Parse_InvalidIdOrNoResources_RejectsManifest()
    {
        var (okBadId, _, _) = LexAddonManifestParser.Parse(@"{""id"": ""bad id!"", ""name"": ""Test"", ""resources"": [""catalog""]}");
        var (okNoRes, _, _) = LexAddonManifestParser.Parse(@"{""id"": ""org.valid.id"", ""name"": ""Empty"", ""resources"": [], ""catalogs"": []}");

        Assert.False(okBadId);
        Assert.False(okNoRes);
    }

    // -------------------------------------------------------------------------
    // 4. Seguridad SSRF, aislamiento loopback y límites HTTP en LexAddonClient
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("http://127.0.0.1:7000/manifest.json", false, false, false)]
    [InlineData("http://localhost:8080/manifest.json", false, false, false)]
    [InlineData("http://192.168.1.50/manifest.json", true, false, false)]
    [InlineData("http://169.254.169.254/latest/meta-data/manifest.json", true, false, false)]
    [InlineData("http://nas.local/manifest.json", true, false, false)]
    [InlineData("http://127.0.0.1:7000/manifest.json", true, true, true)]
    [InlineData("https://addons.example.org/manifest.json", false, true, false)]
    public void ValidateAddonEndpoint_EnforcesSsrfAndLoopbackOptIn(
        string url,
        bool allowLoopback,
        bool expectedAllowed,
        bool expectedLoopback)
    {
        bool ok = LexAddonClient.ValidateAddonEndpoint(url, allowLoopback, out _, out bool isLoopback, out _);

        Assert.Equal(expectedAllowed, ok);
        if (ok)
        {
            Assert.Equal(expectedLoopback, isLoopback);
        }
    }

    [Fact]
    public async Task FetchManifestAsync_LoopbackRedirect_IsRejected()
    {
        using var loopbackHttp = new HttpClient(new MockHttpHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Redirect);
            resp.Headers.Location = new Uri("https://169.254.169.254/latest");
            return resp;
        }));
        using var remoteHttp = new HttpClient(new MockHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = new LexAddonClient(injectedRemoteClient: remoteHttp, injectedLoopbackClient: loopbackHttp);

        var result = await client.FetchManifestAsync("http://127.0.0.1:7000/manifest.json", allowLocalLoopback: true, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("redirecciones", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchManifestAsync_OversizedOrHtmlOrHttp404_IsRejectedSafely()
    {
        // a) > 256 KB
        string oversized = new string('a', LexAddonClient.MaxManifestBytes + 128);
        using var oversizedHttp = new HttpClient(new MockHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":\"org.big\",\"name\":\"Big\",\"resources\":[\"catalog\"],\"description\":\"" + oversized + "\"}", Encoding.UTF8, "application/json")
        }));
        using (var client = new LexAddonClient(injectedRemoteClient: oversizedHttp))
        {
            var res = await client.FetchManifestAsync("https://addons.example.org/manifest.json", false, CancellationToken.None);
            Assert.False(res.Success);
            Assert.Contains("tamaño", res.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        // b) HTML en lugar de JSON
        using var htmlHttp = new HttpClient(new MockHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>Blocked</body></html>", Encoding.UTF8, "text/html")
        }));
        using (var client = new LexAddonClient(injectedRemoteClient: htmlHttp))
        {
            var res = await client.FetchManifestAsync("https://addons.example.org/manifest.json", false, CancellationToken.None);
            Assert.False(res.Success);
        }

        // c) HTTP 404
        using var notFoundHttp = new HttpClient(new MockHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        using (var client = new LexAddonClient(injectedRemoteClient: notFoundHttp))
        {
            var res = await client.FetchManifestAsync("https://addons.example.org/manifest.json", false, CancellationToken.None);
            Assert.False(res.Success);
            Assert.Contains("404", res.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
    }

    // -------------------------------------------------------------------------
    // 5. Persistencia DPAPI, ciclo de vida e integridad en LexAddonStore & Service
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ServiceLifecycle_PreviewInstallToggleRefreshIdMismatchAndRemove_WorksAndProtectsUrlWithDpapi()
    {
        string version = "1.0.0";
        string remoteId = "org.lex.cinema";
        bool simulateOffline = false;

        using var remoteHttp = new HttpClient(new MockHttpHandler(_ =>
        {
            if (simulateOffline)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

            string json = $@"{{
              ""id"": ""{remoteId}"",
              ""name"": ""Cinema Pack"",
              ""version"": ""{version}"",
              ""types"": [""movie""],
              ""resources"": [""catalog"", ""stream""],
              ""catalogs"": [{{ ""type"": ""movie"", ""id"": ""popular"", ""name"": ""Populares"" }}]
            }}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }));

        using var client = new LexAddonClient(injectedRemoteClient: remoteHttp);
        var store = new LexAddonStore(_tempDir);
        var service = new LexAddonService(store, client);

        const string secretUrl = "https://addons.example.org/supersecrettoken_0123456789abcdef/manifest.json?api_key=MY_SECRET_KEY";

        // 1) Preview no persiste nada en el registro
        var preview = await service.PreviewFromUrlAsync(secretUrl, allowLocalLoopback: false, CancellationToken.None);
        Assert.True(preview.Success, preview.ErrorMessage);
        Assert.Empty(service.ListAddons());

        // 2) Install cifra la URL real en disco (Windows DPAPI) y redacta la UI
        var installed = await service.InstallFromUrlAsync(secretUrl, allowLocalLoopback: false, CancellationToken.None);
        Assert.True(installed.Success, installed.ErrorMessage);
        Assert.False(installed.Updated);
        Assert.NotNull(installed.Addon);
        Assert.DoesNotContain("MY_SECRET_KEY", installed.Addon!.DisplayUrl, StringComparison.Ordinal);

        Assert.True(File.Exists(store.RegistryPath));
        string rawDiskJson = File.ReadAllText(store.RegistryPath);
        Assert.DoesNotContain("MY_SECRET_KEY", rawDiskJson, StringComparison.Ordinal);
        Assert.DoesNotContain("supersecrettoken_0123456789abcdef", rawDiskJson, StringComparison.Ordinal);

        // 3) Recarga desde disco tras liberar caché en memoria
        store.ReleaseMemoryCache();
        Assert.Equal(secretUrl, store.GetManifestUrl("org.lex.cinema"));

        // 4) Toggle habilitado/deshabilitado
        var disabled = service.SetEnabled("org.lex.cinema", false);
        Assert.True(disabled.Success);
        Assert.False(disabled.Addon!.Enabled);

        // 5) Refresh actualiza versión manteniendo estado Enabled=false
        version = "2.1.0";
        var refreshed = await service.RefreshAddonAsync("org.lex.cinema", CancellationToken.None);
        Assert.True(refreshed.Success, refreshed.ErrorMessage);
        Assert.Equal("2.1.0", refreshed.Addon!.Version);
        Assert.False(refreshed.Addon.Enabled);

        // 6) Refresh rechaza cambio malicioso de ID remoto y registra LastError
        remoteId = "org.evil.hijack";
        var hijack = await service.RefreshAddonAsync("org.lex.cinema", CancellationToken.None);
        Assert.False(hijack.Success);
        Assert.Contains("id", hijack.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(hijack.ErrorMessage, store.GetSummary("org.lex.cinema")!.LastError);

        // 7) Error transitorio de red registra LastError sin eliminar el addon
        remoteId = "org.lex.cinema";
        simulateOffline = true;
        var offline = await service.RefreshAddonAsync("org.lex.cinema", CancellationToken.None);
        Assert.False(offline.Success);
        Assert.NotNull(store.GetSummary("org.lex.cinema")!.LastError);

        // 8) Remove limpia definitivamente
        var removed = service.Remove("org.lex.cinema");
        Assert.True(removed.Success);
        Assert.Empty(service.ListAddons());
    }

    // -------------------------------------------------------------------------
    // 6. Fase 3: Catálogos de películas, series, anime y metadatos (/catalog y /meta)
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("tt0133093", true)]
    [InlineData("kitsu:1234", true)]
    [InlineData("anime-seasonal_2026", true)]
    [InlineData("../secret", false)]
    [InlineData("foo/bar", false)]
    [InlineData("id%2fescape", false)]
    [InlineData("id?query=1", false)]
    public void IsValidPathSegment_RejectsTraversalAndAllowsValidAddonIds(string segment, bool expected)
    {
        Assert.Equal(expected, LexAddonCatalogParser.IsValidPathSegment(segment));
    }

    [Fact]
    public void TryBuildCatalogAndMetaUrls_EncodesExtrasAndPreservesAddonBaseAndQuery()
    {
        string manifestUrl = "https://addons.example.org/cfgToken123/manifest.json?tier=plus";

        bool okCat = LexAddonCatalogParser.TryBuildCatalogUrl(
            manifestUrl, "anime", "top_anime", "fullmetal alchemist", "Action", 20,
            out string catUrl, out string? catErr);

        Assert.True(okCat, catErr);
        Assert.Equal(
            "https://addons.example.org/cfgToken123/catalog/anime/top_anime/search=fullmetal%20alchemist&genre=Action&skip=20.json?tier=plus",
            catUrl);

        bool okMeta = LexAddonCatalogParser.TryBuildMetaUrl(
            manifestUrl, "anime", "kitsu:456",
            out string metaUrl, out string? metaErr);

        Assert.True(okMeta, metaErr);
        Assert.Equal(
            "https://addons.example.org/cfgToken123/meta/anime/kitsu%3A456.json?tier=plus",
            metaUrl);
    }

    [Fact]
    public void ParseCatalog_NormalizesMoviesSeriesAnimeAndFiltersUnsafeEntries()
    {
        string json = @"{
          ""hasMore"": true,
          ""metas"": [
            {
              ""id"": ""kitsu:101"",
              ""type"": ""anime"",
              ""name"": ""Frieren"",
              ""poster"": ""/img/frieren.jpg"",
              ""year"": 2023,
              ""imdbRating"": 8.9,
              ""genres"": [""Fantasy"", ""Adventure""]
            },
            {
              ""id"": ""../evil"",
              ""name"": ""Malicious Path""
            },
            {
              ""id"": ""tt999"",
              ""name"": ""Bad Poster Scheme"",
              ""poster"": ""javascript:alert(1)""
            }
          ]
        }";

        var (ok, items, hasMore, err) = LexAddonCatalogParser.ParseCatalog(
            json, "org.anime.hub", "Anime Hub", "anime", "https://addons.example.org/v1");

        Assert.True(ok, err);
        Assert.True(hasMore);
        Assert.Equal(2, items.Count);

        Assert.Equal("kitsu:101", items[0].Id);
        Assert.Equal("https://addons.example.org/v1/img/frieren.jpg", items[0].Poster);
        Assert.Equal("2023", items[0].ReleaseInfo);
        Assert.Equal("8.9", items[0].ImdbRating);
        Assert.Contains("Fantasy", items[0].Genres);

        // El poster con javascript: es descartado (null), sin perder el título válido
        Assert.Equal("tt999", items[1].Id);
        Assert.Null(items[1].Poster);
    }

    [Fact]
    public void ParseMeta_ParsesAndSortsSeriesAndAnimeEpisodesAcrossSeasons()
    {
        string json = @"{
          ""meta"": {
            ""id"": ""tt0903747"",
            ""type"": ""series"",
            ""name"": ""Serie Demo"",
            ""poster"": ""https://cdn.example.org/poster.jpg"",
            ""description"": ""Sinopsis de prueba"",
            ""runtime"": ""48 min"",
            ""director"": ""Jane Doe"",
            ""cast"": [""Actor A"", ""Actor B""],
            ""videos"": [
              { ""id"": ""tt0903747:2:1"", ""title"": ""T2 Ep1"", ""season"": 2, ""number"": 1, ""released"": ""2024-05-01T00:00:00Z"" },
              { ""id"": ""tt0903747:1:2"", ""title"": ""Ep2"", ""season"": 1, ""episode"": 2 },
              { ""id"": ""tt0903747:1:1"", ""title"": ""Ep1"", ""episode"": 1 }
            ]
          }
        }";

        var (ok, meta, err) = LexAddonCatalogParser.ParseMeta(json, "org.meta", "MetaAddon");

        Assert.True(ok, err);
        Assert.NotNull(meta);
        Assert.Equal("Serie Demo", meta!.Name);
        Assert.Equal("Jane Doe", Assert.Single(meta.Director));
        Assert.Equal(3, meta.Videos.Count);
        Assert.Equal("tt0903747:1:1", meta.Videos[0].Id);
        Assert.Equal(1, meta.Videos[0].Season);
        Assert.Equal("tt0903747:1:2", meta.Videos[1].Id);
        Assert.Equal("tt0903747:2:1", meta.Videos[2].Id);
        Assert.Equal("2024-05-01", meta.Videos[2].Released);
    }

    [Fact]
    public async Task ServiceCatalogAndMeta_SupportsCatalogListingSearchAndCrossAddonMetaFallback()
    {
        using var remoteHttp = new HttpClient(new MockHttpHandler(req =>
        {
            string u = req.RequestUri!.AbsoluteUri;
            if (u == "https://catalog.example.org/manifest.json")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"{
                      ""id"": ""org.catalog.only"",
                      ""name"": ""Catalog Only"",
                      ""version"": ""1.0.0"",
                      ""resources"": [""catalog""],
                      ""types"": [""anime"", ""movie""],
                      ""catalogs"": [
                        { ""type"": ""anime"", ""id"": ""popular_anime"", ""name"": ""Anime Popular"", ""extra"": [{ ""name"": ""search"" }, { ""name"": ""genre"", ""options"": [""Shonen"", ""Seinen""] }, { ""name"": ""skip"" }] }
                      ]
                    }", Encoding.UTF8, "application/json")
                };
            }

            if (u == "https://meta.example.org/manifest.json")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"{
                      ""id"": ""org.kitsu.meta"",
                      ""name"": ""Anime Meta"",
                      ""version"": ""1.0.0"",
                      ""resources"": [{ ""name"": ""meta"", ""types"": [""anime""], ""idPrefixes"": [""kitsu:""] }]
                    }", Encoding.UTF8, "application/json")
                };
            }

            if (u == "https://catalog.example.org/catalog/anime/popular_anime/search=hero&genre=Shonen.json")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"{
                      ""hasMore"": true,
                      ""metas"": [
                        { ""id"": ""kitsu:77"", ""type"": ""anime"", ""name"": ""Hero Academia Demo"" }
                      ]
                    }", Encoding.UTF8, "application/json")
                };
            }

            if (u == "https://meta.example.org/meta/anime/kitsu%3A77.json")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"{
                      ""meta"": {
                        ""id"": ""kitsu:77"",
                        ""type"": ""anime"",
                        ""name"": ""Hero Academia Demo"",
                        ""description"": ""Detalle desde Meta Addon"",
                        ""videos"": [{ ""id"": ""kitsu:77:1"", ""title"": ""Inicio"", ""season"": 1, ""episode"": 1 }]
                      }
                    }", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        using var client = new LexAddonClient(injectedRemoteClient: remoteHttp);
        var store = new LexAddonStore(_tempDir);
        var service = new LexAddonService(store, client);

        Assert.True((await service.InstallFromUrlAsync("https://catalog.example.org/manifest.json", false, CancellationToken.None)).Success);
        Assert.True((await service.InstallFromUrlAsync("https://meta.example.org/manifest.json", false, CancellationToken.None)).Success);

        var descriptors = service.ListCatalogDescriptors("anime");
        var catDesc = Assert.Single(descriptors);
        Assert.True(catDesc.HasSearch);
        Assert.True(catDesc.HasSkip);
        Assert.Equal(new[] { "Shonen", "Seinen" }, catDesc.Genres);

        var catRes = await service.GetCatalogAsync("org.catalog.only", "anime", "popular_anime", "hero", "Shonen", 0, CancellationToken.None);
        Assert.True(catRes.Success, catRes.ErrorMessage);
        Assert.True(catRes.HasMore);
        var item = Assert.Single(catRes.Items!);
        Assert.Equal("kitsu:77", item.Id);

        // El addon de catálogo no tiene recurso 'meta': debe resolver automáticamente con org.kitsu.meta por prefijo 'kitsu:'
        var metaRes = await service.GetMetaAsync("org.catalog.only", "anime", "kitsu:77", CancellationToken.None);
        Assert.True(metaRes.Success, metaRes.ErrorMessage);
        Assert.Equal("Detalle desde Meta Addon", metaRes.Meta!.Description);
        Assert.Single(metaRes.Meta.Videos);

        // Si se desactiva org.catalog.only, deja de aparecer en ListCatalogDescriptors y GetCatalogAsync lo rechaza
        Assert.True(service.SetEnabled("org.catalog.only", false).Success);
        Assert.Empty(service.ListCatalogDescriptors());
        var disabledCat = await service.GetCatalogAsync("org.catalog.only", "anime", "popular_anime", null, null, 0, CancellationToken.None);
        Assert.False(disabledCat.Success);
    }

    // -------------------------------------------------------------------------
    // 7. Fase 4: Resolución multi-addon de streams, cabeceras y subtítulos (/stream)
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseStreams_ValidatesUrlsFiltersSsrfExtractsSafeHeadersAndSubtitles()
    {
        string json = @"{
          ""streams"": [
            {
              ""name"": ""LexCDN\n1080p"",
              ""title"": ""Episodio 1 [HLS]"",
              ""url"": ""https://cdn.example.org/videos/ep1.m3u8"",
              ""behaviorHints"": {
                ""bingeGroup"": ""anime-1080"",
                ""proxyHeaders"": {
                  ""request"": {
                    ""User-Agent"": ""CustomPlayer/2.0"",
                    ""Referer"": ""https://player.example.org/watch/1"",
                    ""Origin"": ""https://player.example.org/app"",
                    ""Cookie"": ""session=SHOULD_NOT_PASS""
                  }
                }
              },
              ""subtitles"": [
                { ""id"": ""es"", ""lang"": ""spa"", ""url"": ""https://subs.example.org/ep1.srt"" },
                { ""id"": ""bad"", ""lang"": ""eng"", ""url"": ""file:///C:/subs/ep1.vtt"" }
              ]
            },
            {
              ""name"": ""SSRF Trap"",
              ""url"": ""http://127.0.0.1:9000/internal.mp4""
            },
            {
              ""name"": ""P2P 4K"",
              ""infoHash"": ""0123456789abcdef0123456789abcdef01234567""
            },
            {
              ""name"": ""Web Only"",
              ""externalUrl"": ""https://watch.example.org/ep1""
            },
            {
              ""name"": ""Unsafe External"",
              ""externalUrl"": ""http://127.0.0.1:9000/admin""
            },
            {
              ""name"": ""Script External"",
              ""externalUrl"": ""javascript:alert(1)""
            }
          ]
        }";

        var (ok, entries, err) = LexAddonStreamParser.ParseStreams(json, "org.stream.provider", "Stream Provider");

        Assert.True(ok, err);
        Assert.Equal(6, entries.Count);

        var hls = entries[0];
        Assert.True(hls.Option.Playable);
        Assert.Equal("hls", hls.Option.StreamKind);
        Assert.StartsWith("/api/proxy?u=", hls.Option.ProxyUrl, StringComparison.Ordinal);
        Assert.Equal("CustomPlayer/2.0", hls.RequestUserAgent);
        Assert.Equal("https://player.example.org/watch/1", hls.RequestReferer);
        Assert.Equal("https://player.example.org", hls.RequestOrigin);
        var sub = Assert.Single(hls.Option.Subtitles);
        Assert.StartsWith("/api/addons/subtitle?u=", sub.ProxyUrl, StringComparison.Ordinal);

        Assert.False(entries[1].Option.Playable);
        Assert.Equal("blocked", entries[1].Option.StreamKind);

        Assert.False(entries[2].Option.Playable);
        Assert.Equal("p2p_unsupported", entries[2].Option.StreamKind);

        Assert.False(entries[3].Option.Playable);
        Assert.Equal("external", entries[3].Option.StreamKind);
        Assert.Equal("https://watch.example.org/ep1", entries[3].Option.ExternalUrl);

        Assert.False(entries[4].Option.Playable);
        Assert.Equal("blocked", entries[4].Option.StreamKind);
        Assert.Equal("", entries[4].Option.ExternalUrl);

        Assert.False(entries[5].Option.Playable);
        Assert.Equal("blocked", entries[5].Option.StreamKind);
        Assert.Equal("", entries[5].Option.ExternalUrl);
    }

    [Fact]
    public void NormalizeToWebVtt_ConvertsSrtAndPreservesVtt()
    {
        string srt = "1\r\n00:00:01,250 --> 00:00:04,500\r\nHola Lex TV\r\n";
        string vtt = LexAddonStreamParser.NormalizeToWebVtt(srt);
        Assert.StartsWith("WEBVTT\n\n", vtt, StringComparison.Ordinal);
        Assert.Contains("00:00:01.250 --> 00:00:04.500", vtt, StringComparison.Ordinal);

        string alreadyVtt = "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nReady";
        Assert.Equal(alreadyVtt, LexAddonStreamParser.NormalizeToWebVtt(alreadyVtt));
    }

    [Fact]
    public async Task ServiceStreamsAndSubtitles_AggregatesAcrossAddonsRegistersPlaybackHintsAndConvertsSrt()
    {
        TvPlaybackHints.Clear();
        try
        {
            using var remoteHttp = new HttpClient(new MockHttpHandler(req =>
            {
                string u = req.RequestUri!.AbsoluteUri;
                if (u == "https://a1.example.org/manifest.json")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(@"{
                          ""id"": ""org.a1"",
                          ""name"": ""Addon Uno"",
                          ""version"": ""1.0.0"",
                          ""resources"": [{ ""name"": ""stream"", ""types"": [""movie""], ""idPrefixes"": [""tt""] }]
                        }", Encoding.UTF8, "application/json")
                    };
                }

                if (u == "https://a2.example.org/manifest.json")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(@"{
                          ""id"": ""org.a2"",
                          ""name"": ""Addon Dos"",
                          ""version"": ""1.0.0"",
                          ""resources"": [{ ""name"": ""stream"", ""types"": [""movie""], ""idPrefixes"": [""tt""] }]
                        }", Encoding.UTF8, "application/json")
                    };
                }

                if (u == "https://a1.example.org/stream/movie/tt0133093.json")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(@"{
                          ""streams"": [
                            { ""infoHash"": ""aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"", ""title"": ""Torrent no web"" }
                          ]
                        }", Encoding.UTF8, "application/json")
                    };
                }

                if (u == "https://a2.example.org/stream/movie/tt0133093.json")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(@"{
                          ""streams"": [
                            {
                              ""title"": ""Direct MP4"",
                              ""url"": ""https://stream-host.example.org/matrix.mp4"",
                              ""behaviorHints"": {
                                ""proxyHeaders"": {
                                  ""request"": {
                                    ""User-Agent"": ""LexStreamClient/1.1"",
                                    ""Referer"": ""https://ref.example.org/embed""
                                  }
                                }
                              }
                            }
                          ]
                        }", Encoding.UTF8, "application/json")
                    };
                }

                if (u == "https://subs.example.org/matrix_es.srt")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("1\n00:01:00,000 --> 00:01:03,500\nBienvenido\n", Encoding.UTF8, "text/plain")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));

            using var client = new LexAddonClient(injectedRemoteClient: remoteHttp);
            var store = new LexAddonStore(_tempDir);
            var service = new LexAddonService(store, client);

            Assert.True((await service.InstallFromUrlAsync("https://a1.example.org/manifest.json", false, CancellationToken.None)).Success);
            Assert.True((await service.InstallFromUrlAsync("https://a2.example.org/manifest.json", false, CancellationToken.None)).Success);

            var streamsRes = await service.GetStreamsAsync("org.a1", "movie", "tt0133093", CancellationToken.None);
            Assert.True(streamsRes.Success, streamsRes.ErrorMessage);
            Assert.Equal(2, streamsRes.QueriedAddonsCount);
            Assert.Equal(2, streamsRes.Streams!.Count);

            // Los streams reproducibles deben quedar primero aunque vinieran del segundo addon
            Assert.True(streamsRes.Streams[0].Playable);
            Assert.Equal("org.a2", streamsRes.Streams[0].AddonId);
            Assert.False(streamsRes.Streams[1].Playable);

            var (ua, referer, _) = TvPlaybackHints.GetHostHeaders(null, "stream-host.example.org");
            Assert.Equal("LexStreamClient/1.1", ua);
            Assert.Equal("https://ref.example.org/embed", referer);

            // Descarga y conversión de subtítulo remoto .srt -> WebVTT + bloqueo SSRF en loopback
            var subOk = await service.FetchSubtitleAsWebVttAsync("https://subs.example.org/matrix_es.srt", CancellationToken.None);
            Assert.True(subOk.Success, subOk.ErrorMessage);
            Assert.StartsWith("WEBVTT\n\n", subOk.WebVtt!, StringComparison.Ordinal);
            Assert.Contains("00:01:00.000 --> 00:01:03.500", subOk.WebVtt!, StringComparison.Ordinal);

            var subBlocked = await service.FetchSubtitleAsWebVttAsync("http://127.0.0.1:8080/sub.srt", CancellationToken.None);
            Assert.False(subBlocked.Success);
            Assert.Equal(403, subBlocked.StatusCode);
        }
        finally
        {
            TvPlaybackHints.Clear();
        }
    }

    // -------------------------------------------------------------------------
    // 8. Fase 5: Repositorios de addons, SemVer, SSRF en repos y actualización masiva
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("1.10.0", "1.2.9", 1)]
    [InlineData("v2.0.0", "1.9.9", 1)]
    [InlineData("1.0.0", "1.0.0-beta", 1)]
    [InlineData("1.0.0-beta", "1.0.0", -1)]
    [InlineData("1.0", "1.0.0", 0)]
    public void CompareVersions_ComparesSemVerNumericAndPrereleaseCorrectly(string candidate, string current, int expectedSign)
    {
        int cmp = LexAddonRepoParser.CompareVersions(candidate, current);
        Assert.Equal(expectedSign, Math.Sign(cmp));
    }

    [Fact]
    public void TryNormalizeRepoInput_ValidAndDangerousUrls()
    {
        Assert.True(LexAddonRepoParser.TryNormalizeRepoInput("repos.example.org/feed.json#hash", out string normalized, out string? err), err);
        Assert.Equal("https://repos.example.org/feed.json", normalized);

        Assert.True(LexAddonRepoParser.TryNormalizeRepoInput("https://repos.example.org/custom/catalog.json?k=1", out string preserved, out string? err2), err2);
        Assert.Equal("https://repos.example.org/custom/catalog.json?k=1", preserved);

        Assert.False(LexAddonRepoParser.TryNormalizeRepoInput("file:///C:/repo.json", out _, out _));
        Assert.False(LexAddonRepoParser.TryNormalizeRepoInput("ftp://repos.example.org/repo.json", out _, out _));
        Assert.False(LexAddonRepoParser.TryNormalizeRepoInput("https://user:pass@repos.example.org/repo.json", out _, out _));
    }

    [Fact]
    public void Parse_RemoteRepoRejectsInjectedLoopbackAndPrivateManifests()
    {
        const string repoJson = @"{
          ""name"": ""Repo Remoto Mixto"",
          ""addons"": [
            { ""id"": ""org.pub"", ""name"": ""Publico"", ""version"": ""1.0.0"", ""manifestUrl"": ""https://addons.example.org/pub/manifest.json"" },
            { ""id"": ""org.loop"", ""name"": ""Loopback"", ""version"": ""1.0.0"", ""manifestUrl"": ""http://127.0.0.1:7000/manifest.json"" },
            { ""id"": ""org.priv"", ""name"": ""Privado"", ""version"": ""1.0.0"", ""manifestUrl"": ""http://192.168.1.2/manifest.json"" }
          ]
        }";

        var res = LexAddonRepoParser.Parse(repoJson, "https://repos.example.org/repo.json", repoAllowsLoopback: false);

        Assert.True(res.Success, res.Error);
        Assert.NotNull(res.Feed);
        Assert.Single(res.Feed!.Entries);
        Assert.Equal("org.pub", res.Feed.Entries[0].AddonId);
    }

    [Fact]
    public async Task ServiceRepositoriesAndBulkRefresh_SavesWithDpapiDetectsUpdatesRefreshesAllAndRemoves()
    {
        string r1Version = "1.0.0";
        bool r3Offline = false;
        bool repoOffline = false;

        using var remoteHttp = new HttpClient(new MockHttpHandler(req =>
        {
            string u = req.RequestUri!.AbsoluteUri;

            if (u == "https://r1.example.org/manifest.json")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($@"{{
                      ""id"": ""org.r1"",
                      ""name"": ""Repo Addon Uno"",
                      ""version"": ""{r1Version}"",
                      ""resources"": [""catalog""],
                      ""types"": [""movie""]
                    }}", Encoding.UTF8, "application/json")
                };
            }

            if (u == "https://r2.example.org/manifest.json")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"{
                      ""id"": ""org.r2"",
                      ""name"": ""Repo Addon Dos"",
                      ""version"": ""2.0.0"",
                      ""resources"": [""catalog""],
                      ""types"": [""movie""]
                    }", Encoding.UTF8, "application/json")
                };
            }

            if (u == "https://r3.example.org/manifest.json")
            {
                if (r3Offline)
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"{
                      ""id"": ""org.r3"",
                      ""name"": ""Repo Addon Tres"",
                      ""version"": ""1.0.0"",
                      ""resources"": [""catalog""],
                      ""types"": [""series""]
                    }", Encoding.UTF8, "application/json")
                };
            }

            if (u == "https://repos.example.org/main.json?secret=REPO_TOKEN")
            {
                if (repoOffline)
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"{
                      ""name"": ""Catalogo Oficial Test"",
                      ""description"": ""Coleccion verificada"",
                      ""addons"": [
                        {
                          ""id"": ""org.r1"",
                          ""name"": ""Repo Addon Uno"",
                          ""version"": ""1.2.0"",
                          ""manifestUrl"": ""https://r1.example.org/manifest.json""
                        },
                        {
                          ""id"": ""org.r2"",
                          ""name"": ""Repo Addon Dos"",
                          ""version"": ""2.0.0"",
                          ""manifestUrl"": ""https://r2.example.org/manifest.json""
                        }
                      ]
                    }", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        using var client = new LexAddonClient(injectedRemoteClient: remoteHttp);
        var store = new LexAddonStore(_tempDir);
        var service = new LexAddonService(store, client);

        Assert.True((await service.InstallFromUrlAsync("https://r1.example.org/manifest.json", false, CancellationToken.None)).Success);
        Assert.True((await service.InstallFromUrlAsync("https://r2.example.org/manifest.json", false, CancellationToken.None)).Success);
        Assert.True((await service.InstallFromUrlAsync("https://r3.example.org/manifest.json", false, CancellationToken.None)).Success);

        var addRes = await service.AddRepositoryAsync("https://repos.example.org/main.json?secret=REPO_TOKEN", false, CancellationToken.None);
        Assert.True(addRes.Success, addRes.ErrorMessage);
        Assert.NotNull(addRes.Repository);
        string repoId = addRes.Repository!.Id;
        Assert.Equal("Catalogo Oficial Test", addRes.Repository.Name);
        Assert.DoesNotContain("REPO_TOKEN", addRes.Repository.DisplayUrl, StringComparison.Ordinal);

        var r1Entry = Assert.Single(addRes.Entries!, e => e.AddonId == "org.r1");
        Assert.True(r1Entry.Installed);
        Assert.Equal("1.0.0", r1Entry.InstalledVersion);
        Assert.True(r1Entry.UpdateAvailable);

        var r2Entry = Assert.Single(addRes.Entries!, e => e.AddonId == "org.r2");
        Assert.True(r2Entry.Installed);
        Assert.False(r2Entry.UpdateAvailable);

        // El secreto del repositorio debe estar cifrado con DPAPI en disco y recuperarse tras limpiar caché
        string registryJson = File.ReadAllText(Path.Combine(_tempDir, "addons_registry.json"));
        Assert.DoesNotContain("REPO_TOKEN", registryJson, StringComparison.Ordinal);

        store.ReleaseMemoryCache();
        Assert.Equal("https://repos.example.org/main.json?secret=REPO_TOKEN", store.GetRepositoryUrl(repoId));

        // Inspección online y fallo offline conservando el repositorio con LastError
        var inspectOk = await service.InspectRepositoryAsync(repoId, CancellationToken.None);
        Assert.True(inspectOk.Success, inspectOk.ErrorMessage);
        Assert.Equal(2, inspectOk.Entries!.Count);

        repoOffline = true;
        var inspectFail = await service.InspectRepositoryAsync(repoId, CancellationToken.None);
        Assert.False(inspectFail.Success);
        Assert.False(string.IsNullOrWhiteSpace(inspectFail.ErrorMessage));
        var storedSummary = Assert.Single(service.ListRepositories(), r => r.Id == repoId);
        Assert.False(string.IsNullOrWhiteSpace(storedSummary.LastError));

        // Actualización masiva: org.r1 sube a 1.2.0, org.r2 queda igual, org.r3 falla por 503
        r1Version = "1.2.0";
        r3Offline = true;
        var bulkRes = await service.RefreshAllAddonsAsync(CancellationToken.None);
        Assert.Equal(3, bulkRes.TotalChecked);
        Assert.Equal(1, bulkRes.UpdatedCount);
        Assert.Equal(1, bulkRes.UpToDateCount);
        Assert.Equal(1, bulkRes.ErrorCount);
        Assert.Equal(new[] { "org.r1" }, bulkRes.UpdatedIds);
        Assert.Single(bulkRes.Errors);

        // Eliminación limpia del repositorio
        var remRes = service.RemoveRepository(repoId);
        Assert.True(remRes.Success, remRes.ErrorMessage);
        Assert.Empty(service.ListRepositories());
        Assert.Null(store.GetRepositoryUrl(repoId));
    }

    // -------------------------------------------------------------------------
    // 9. Fase 6: Compatibilidad Stremio avanzada (links, defaultVideoId, /subtitles y búsqueda global)
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseMeta_ExtractsCinemetaLinksDefaultVideoIdAndReleasedFallback()
    {
        const string json = @"{
          ""meta"": {
            ""id"": ""tt0133093"",
            ""type"": ""movie"",
            ""name"": ""The Matrix"",
            ""released"": ""1999-03-31T00:00:00Z"",
            ""behaviorHints"": { ""defaultVideoId"": ""tt0133093"" },
            ""links"": [
              { ""name"": ""Sci-Fi"", ""category"": ""Genres"" },
              { ""name"": ""Lana Wachowski"", ""category"": ""Directors"" },
              { ""name"": ""Keanu Reeves"", ""category"": ""Cast"" }
            ]
          }
        }";

        var (ok, meta, err) = LexAddonCatalogParser.ParseMeta(json, "org.cinemeta.test", "Cinemeta");

        Assert.True(ok, err);
        Assert.NotNull(meta);
        Assert.Equal("1999-03-31", meta!.ReleaseInfo);
        Assert.Equal("tt0133093", meta.DefaultVideoId);
        Assert.Contains("Sci-Fi", meta.Genres);
        Assert.Contains("Lana Wachowski", meta.Director);
        Assert.Contains("Keanu Reeves", meta.Cast);
    }

    [Fact]
    public void TryBuildSubtitlesUrl_ParseSubtitlesAndStreamHints_EnforcesExtrasAndSsrfProtection()
    {
        bool okUrl = LexAddonStreamParser.TryBuildSubtitlesUrl(
            "https://subs.example.org/token123/manifest.json?lang=es",
            "movie",
            "tt0133093",
            "deadbeef1234",
            1_800_000_000L,
            "C:\\fakepath\\matrix_1080p.mkv",
            out string subUrl,
            out string? err);

        Assert.True(okUrl, err);
        Assert.Equal(
            "https://subs.example.org/token123/subtitles/movie/tt0133093/videoHash=deadbeef1234&videoSize=1800000000&filename=matrix_1080p.mkv.json?lang=es",
            subUrl);

        const string subsJson = @"{
          ""subtitles"": [
            { ""id"": ""s1"", ""lang"": ""spa"", ""url"": ""https://subs.example.org/es.srt"" },
            { ""id"": ""sEvil"", ""lang"": ""eng"", ""url"": ""http://127.0.0.1:8080/local.srt"" }
          ]
        }";

        var parsedSubs = LexAddonStreamParser.ParseSubtitlesResponse(subsJson, "OpenSubs Demo");
        Assert.True(parsedSubs.Success, parsedSubs.Error);
        var onlySub = Assert.Single(parsedSubs.Subtitles);
        Assert.Equal("SPA · OpenSubs Demo", onlySub.Label);
        Assert.StartsWith("/api/addons/subtitle?u=", onlySub.ProxyUrl, StringComparison.Ordinal);

        const string streamsJson = @"{
          ""streams"": [
            {
              ""title"": ""1080p BluRay"",
              ""url"": ""https://cdn.example.org/movie.mkv"",
              ""behaviorHints"": {
                ""filename"": ""/tmp/path/Matrix.1999.1080p.mkv"",
                ""videoSize"": 2147483648
              }
            }
          ]
        }";

        var parsedStreams = LexAddonStreamParser.ParseStreams(streamsJson, "org.streams", "Streams");
        Assert.True(parsedStreams.Success, parsedStreams.Error);
        var s = Assert.Single(parsedStreams.Streams);
        Assert.Equal("Matrix.1999.1080p.mkv", s.Option.Filename);
        Assert.Equal(2147483648L, s.Option.VideoSize);
    }

    [Fact]
    public async Task ServiceGlobalSearchAndStandaloneSubtitles_AggregatesAcrossAddonsSafely()
    {
        using var remoteHttp = new HttpClient(new MockHttpHandler(req =>
        {
            string u = req.RequestUri!.AbsoluteUri;

            if (u == "https://movies-cat.example.org/manifest.json")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"{
                      ""id"": ""org.movies.cat"",
                      ""name"": ""Movies Cat"",
                      ""version"": ""1.0.0"",
                      ""resources"": [""catalog""],
                      ""types"": [""movie""],
                      ""catalogs"": [{ ""type"": ""movie"", ""id"": ""top"", ""name"": ""Top"", ""extra"": [{ ""name"": ""search"" }] }]
                    }", Encoding.UTF8, "application/json")
                };
            }

            if (u == "https://anime-cat.example.org/manifest.json")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"{
                      ""id"": ""org.anime.cat"",
                      ""name"": ""Anime Cat"",
                      ""version"": ""1.0.0"",
                      ""resources"": [""catalog""],
                      ""types"": [""anime""],
                      ""catalogs"": [{ ""type"": ""anime"", ""id"": ""all_anime"", ""name"": ""Anime"", ""extra"": [{ ""name"": ""search"" }] }]
                    }", Encoding.UTF8, "application/json")
                };
            }

            if (u == "https://subs-hub.example.org/manifest.json")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"{
                      ""id"": ""org.subs.hub"",
                      ""name"": ""Subtitles Hub"",
                      ""version"": ""1.0.0"",
                      ""resources"": [{ ""name"": ""subtitles"", ""types"": [""movie""], ""idPrefixes"": [""tt""] }]
                    }", Encoding.UTF8, "application/json")
                };
            }

            if (u == "https://movies-cat.example.org/catalog/movie/top/search=matrix.json")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"{
                      ""metas"": [{ ""id"": ""tt0133093"", ""type"": ""movie"", ""name"": ""The Matrix"" }]
                    }", Encoding.UTF8, "application/json")
                };
            }

            if (u == "https://anime-cat.example.org/catalog/anime/all_anime/search=matrix.json")
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            if (u == "https://subs-hub.example.org/subtitles/movie/tt0133093/videoHash=abc123&videoSize=1500000000&filename=matrix.mp4.json")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"{
                      ""subtitles"": [
                        { ""id"": ""sub-es"", ""lang"": ""es"", ""url"": ""https://subs-hub.example.org/files/tt0133093_es.srt"" },
                        { ""id"": ""sub-ssrf"", ""lang"": ""en"", ""url"": ""http://10.0.0.5/bad.srt"" }
                      ]
                    }", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        using var client = new LexAddonClient(injectedRemoteClient: remoteHttp);
        var store = new LexAddonStore(_tempDir);
        var service = new LexAddonService(store, client);

        Assert.True((await service.InstallFromUrlAsync("https://movies-cat.example.org/manifest.json", false, CancellationToken.None)).Success);
        Assert.True((await service.InstallFromUrlAsync("https://anime-cat.example.org/manifest.json", false, CancellationToken.None)).Success);
        Assert.True((await service.InstallFromUrlAsync("https://subs-hub.example.org/manifest.json", false, CancellationToken.None)).Success);

        // Búsqueda global: un addon devuelve resultado y el segundo falla con 503 sin romper la búsqueda
        var searchRes = await service.SearchCatalogsAsync("matrix", "all", CancellationToken.None);
        Assert.True(searchRes.Success, searchRes.ErrorMessage);
        Assert.Equal(2, searchRes.SearchedCatalogsCount);
        var hit = Assert.Single(searchRes.Items!);
        Assert.Equal("tt0133093", hit.Id);
        Assert.Equal("org.movies.cat", hit.AddonId);
        Assert.NotNull(searchRes.Errors);
        Assert.Single(searchRes.Errors!);

        // Subtítulos independientes: llama a org.subs.hub y filtra URL privada 10.0.0.5
        var subsRes = await service.GetSubtitlesAsync(
            "org.movies.cat", "movie", "tt0133093", "abc123", 1500000000L, "matrix.mp4", CancellationToken.None);
        Assert.True(subsRes.Success, subsRes.ErrorMessage);
        Assert.Equal(1, subsRes.QueriedAddonsCount);
        var sub = Assert.Single(subsRes.Subtitles!);
        Assert.Equal("ES · Subtitles Hub", sub.Label);
        Assert.Contains("tt0133093_es.srt", Uri.UnescapeDataString(sub.ProxyUrl), StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // 10. Fase 7: Complementos IPTV (canales tv/channel, EPG, streams embebidos y bajo demanda)
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseLiveCatalog_NormalizesChannelsEmbeddedStreamsEpgAndFiltersPrivateUrls()
    {
        const string liveJson = @"{
          ""channels"": [
            {
              ""id"": ""ch_news"",
              ""name"": ""Noticias 24h"",
              ""number"": 101,
              ""group"": ""Informativos"",
              ""language"": ""es"",
              ""country"": ""ES"",
              ""epgNow"": ""Edición Matinal"",
              ""logo"": ""/logos/news.png"",
              ""streams"": [
                {
                  ""name"": ""FHD"",
                  ""url"": ""https://live-cdn.example.org/news/index.m3u8"",
                  ""behaviorHints"": {
                    ""proxyHeaders"": { ""request"": { ""User-Agent"": ""LexLive/2.0"" } }
                  }
                },
                {
                  ""name"": ""EvilLoop"",
                  ""url"": ""http://127.0.0.1:9000/secret.m3u8""
                },
                {
                  ""name"": ""Backup"",
                  ""url"": ""https://backup-cdn.example.org/news.m3u8""
                }
              ]
            },
            {
              ""id"": ""../traversal"",
              ""name"": ""Canal inválido""
            }
          ]
        }";

        var (ok, channels, remembered, err) = LexAddonCatalogParser.ParseLiveCatalog(
            liveJson, "org.live.demo", "Live Demo", "tv", "https://iptv.example.org");

        Assert.True(ok, err);
        var ch = Assert.Single(channels);
        Assert.Equal("addon::org.live.demo::tv::ch_news", ch.Id);
        Assert.Equal(101, ch.Number);
        Assert.Equal("Informativos", ch.Group);
        Assert.Equal("Edición Matinal", ch.EpgNow);
        Assert.Equal("https://iptv.example.org/logos/news.png", ch.Logo);
        Assert.Equal("https://live-cdn.example.org/news/index.m3u8", ch.Url);
        var alt = Assert.Single(ch.Alternates);
        Assert.Equal("https://backup-cdn.example.org/news.m3u8", alt.Url);
        Assert.Equal(2, remembered.Count);
    }

    [Fact]
    public async Task ServiceIptv_ListsSourcesFiltersVodLoadsChannelsRemembersHeadersAndResolvesOnDemand()
    {
        TvPlaybackHints.Clear();
        try
        {
            using var remoteHttp = new HttpClient(new MockHttpHandler(req =>
            {
                string u = req.RequestUri!.AbsoluteUri;

                if (u == "https://vod-only.example.org/manifest.json")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(@"{
                          ""id"": ""org.vod.only"",
                          ""name"": ""Solo VOD"",
                          ""version"": ""1.0.0"",
                          ""resources"": [""catalog""],
                          ""types"": [""movie""],
                          ""catalogs"": [{ ""type"": ""movie"", ""id"": ""movies"", ""name"": ""Películas"" }]
                        }", Encoding.UTF8, "application/json")
                    };
                }

                if (u == "https://iptv.example.org/manifest.json")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(@"{
                          ""id"": ""org.live.tv"",
                          ""name"": ""Canales Abiertos"",
                          ""version"": ""1.0.0"",
                          ""resources"": [""catalog"", ""stream""],
                          ""types"": [""tv""],
                          ""catalogs"": [{ ""type"": ""tv"", ""id"": ""live_main"", ""name"": ""Tierra y Cultura"" }]
                        }", Encoding.UTF8, "application/json")
                    };
                }

                if (u == "https://iptv.example.org/catalog/tv/live_main.json")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(@"{
                          ""metas"": [
                            {
                              ""id"": ""ch_direct"",
                              ""type"": ""tv"",
                              ""name"": ""Canal Directo"",
                              ""genres"": [""Documentales""],
                              ""epgNow"": ""Naturaleza Viva"",
                              ""streams"": [
                                {
                                  ""name"": ""HD"",
                                  ""url"": ""https://live-hint.example.org/direct.m3u8"",
                                  ""behaviorHints"": {
                                    ""proxyHeaders"": { ""request"": { ""Referer"": ""https://iptv-ref.example.org/"" } }
                                  }
                                }
                              ]
                            },
                            {
                              ""id"": ""ch_ondemand"",
                              ""type"": ""tv"",
                              ""name"": ""Canal Bajo Demanda"",
                              ""genres"": [""Deportes""]
                            }
                          ]
                        }", Encoding.UTF8, "application/json")
                    };
                }

                if (u == "https://iptv.example.org/stream/tv/ch_ondemand.json")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(@"{
                          ""streams"": [
                            { ""name"": ""Principal"", ""url"": ""https://ondemand-stream.example.org/live.m3u8"" }
                          ]
                        }", Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));

            using var client = new LexAddonClient(injectedRemoteClient: remoteHttp);
            var store = new LexAddonStore(_tempDir);
            var service = new LexAddonService(store, client);

            Assert.True((await service.InstallFromUrlAsync("https://vod-only.example.org/manifest.json", false, CancellationToken.None)).Success);
            Assert.True((await service.InstallFromUrlAsync("https://iptv.example.org/manifest.json", false, CancellationToken.None)).Success);

            // Solo debe listar el catálogo 'tv', ignorando el catálogo VOD
            var sources = service.ListIptvSources();
            var src = Assert.Single(sources);
            Assert.Equal("addon::org.live.tv::tv::live_main", src.SourceKey);

            var liveRes = await service.GetIptvChannelsAsync("org.live.tv", "tv", "live_main", null, null, CancellationToken.None);
            Assert.True(liveRes.Success, liveRes.ErrorMessage);
            Assert.Equal(2, liveRes.Channels!.Count);

            var chDirect = liveRes.Channels[0];
            Assert.Equal("https://live-hint.example.org/direct.m3u8", chDirect.Url);
            Assert.Equal("Naturaleza Viva", chDirect.EpgNow);

            var (_, referer, _) = TvPlaybackHints.GetHostHeaders(null, "live-hint.example.org");
            Assert.Equal("https://iptv-ref.example.org/", referer);

            // El canal sin URL embebida queda listo para resolución bajo demanda vía GetStreamsAsync
            var chOnDemand = liveRes.Channels[1];
            Assert.Equal("", chOnDemand.Url);
            var resolved = await service.GetStreamsAsync("org.live.tv", "tv", chOnDemand.ItemId, CancellationToken.None);
            Assert.True(resolved.Success, resolved.ErrorMessage);
            Assert.Equal("https://ondemand-stream.example.org/live.m3u8", Assert.Single(resolved.Streams!).StreamUrl);
        }
        finally
        {
            TvPlaybackHints.Clear();
        }
    }
}

