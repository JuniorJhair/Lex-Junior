using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LexBrowser.Core.Tv;
using Xunit;

namespace LexBrowser.Tests;

public sealed class TvXtreamTests
{
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

    // 1. Conexión correcta con credenciales válidas (API simulada)
    [Fact]
    public async Task AuthenticateAsync_ValidCredentials_ReturnsSuccessWithUserInfo()
    {
        string mockJson = @"{
            ""user_info"": {
                ""username"": ""usuario_test"",
                ""password"": ""clave_secreta"",
                ""auth"": 1,
                ""status"": ""Active"",
                ""exp_date"": ""1794500000"",
                ""is_trial"": ""0"",
                ""active_cons"": ""1"",
                ""max_connections"": ""2""
            },
            ""server_info"": {
                ""url"": ""stream.ejemplo.invalid"",
                ""port"": ""8080"",
                ""https_port"": ""8443"",
                ""server_protocol"": ""https"",
                ""timezone"": ""UTC""
            }
        }";

        using var handler = new MockHttpHandler(req =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(mockJson, Encoding.UTF8, "application/json")
            };
        });
        using var client = new HttpClient(handler);
        var service = new TvXtreamService(injectedClient: client);

        var result = await service.AuthenticateAsync("https://stream.ejemplo.invalid", "usuario_test", "clave_secreta", CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.UserInfo);
        Assert.Equal("usuario_test", result.UserInfo.Username);
        Assert.Equal("Active", result.UserInfo.Status);
        Assert.Equal(1, result.UserInfo.Auth);
        Assert.NotNull(result.ServerInfo);
        Assert.Equal("8080", result.ServerInfo.Port);
    }

    // 2. Rechazo de credenciales incorrectas (auth = 0 o estado inválido)
    [Theory]
    [InlineData(@"{""user_info"":{""username"":""u"",""auth"":0,""status"":""Active""}}")]
    [InlineData(@"{""user_info"":{""username"":""u"",""auth"":1,""status"":""Disabled""}}")]
    [InlineData(@"{""user_info"":{""username"":""u"",""auth"":1,""status"":""Banned""}}")]
    [InlineData(@"{""user_info"":{""username"":""u"",""auth"":1,""status"":""Expired""}}")]
    public void ParseAuthResponse_InvalidAuthOrStatus_RejectsAuthentication(string json)
    {
        var result = TvXtreamService.ParseAuthResponse(json, isHttpWarning: false);
        Assert.False(result.Success);
        Assert.Contains("rechazada", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // 3. Servidor no disponible / error HTTP
    [Fact]
    public async Task AuthenticateAsync_ServerReturns503_ReturnsGracefulError()
    {
        using var handler = new MockHttpHandler(req =>
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                ReasonPhrase = "Service Unavailable"
            };
        });
        using var client = new HttpClient(handler);
        var service = new TvXtreamService(injectedClient: client);

        var result = await service.AuthenticateAsync("https://stream.ejemplo.invalid", "usuario", "password", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("503", result.ErrorMessage);
    }

    // 4. Respuesta API malformada (JSON corrupto o inesperado)
    [Theory]
    [InlineData("<!DOCTYPE html><html><body>Error 404</body></html>")]
    [InlineData("{ this is not valid json }")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData(@"{""other_key"": 123}")]
    public void ParseAuthResponse_MalformedOrUnexpectedJson_HandledSafely(string badResponse)
    {
        var result = TvXtreamService.ParseAuthResponse(badResponse, isHttpWarning: false);
        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    // 5. Servidor sin categorías VOD o series (manejo limpio devolviendo lista vacía sin excepción)
    [Fact]
    public void ParseVodAndSeries_EmptyOrMissing_ReturnsEmptyListWithoutCrashing()
    {
        var categories = new Dictionary<string, string>();

        var emptyVod = TvXtreamService.ParseVodStreams("[]", categories, "conn1");
        Assert.Empty(emptyVod);

        var nullVod = TvXtreamService.ParseVodStreams("", categories, "conn1");
        Assert.Empty(nullVod);

        var emptySeries = TvXtreamService.ParseSeriesList("[]", categories);
        Assert.Empty(emptySeries);

        var emptyEpisodes = TvXtreamService.ParseSeriesInfoEpisodes("{}", new TvXtreamSeriesItem { SeriesId = "1" }, "conn1");
        Assert.Empty(emptyEpisodes);
    }

    // 6. Redirecciones hacia IP privadas bloqueadas por TvUrlGuard
    [Theory]
    [InlineData("http://192.168.1.1/stream.m3u8")]
    [InlineData("http://10.0.0.1/stream.m3u8")]
    [InlineData("http://172.16.5.1/stream.m3u8")]
    [InlineData("http://127.0.0.1:8080/stream.m3u8")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    public void TvUrlGuard_IsAllowedRemoteUrl_BlocksPrivateTargetsInRedirectionOrDirect(string targetUrl)
    {
        bool allowed = TvUrlGuard.IsAllowedRemoteUrl(targetUrl, out var safeUri);
        Assert.False(allowed);
        Assert.Null(safeUri);
    }

    // 7. DNS que devuelve IP pública y privada simultáneamente (SelectPublicAddressesAsync)
    [Fact]
    public async Task SelectPublicAddressesAsync_MixedPublicAndPrivate_FiltersOutPrivateAndKeepsPublic()
    {
        var mixedIps = new IPAddress[]
        {
            IPAddress.Parse("127.0.0.1"),        // Loopback (privada)
            IPAddress.Parse("192.168.1.100"),    // RFC1918 (privada)
            IPAddress.Parse("93.184.216.34"),    // Public IPv4 (example.com)
            IPAddress.Parse("169.254.1.1"),      // Link-local (privada)
            IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946") // Public IPv6
        };

        Task<IPAddress[]> CustomResolver(string host, CancellationToken ct) => Task.FromResult(mixedIps);

        var selected = await TvUrlGuard.SelectPublicAddressesAsync("stream.ejemplo.invalid", CustomResolver, CancellationToken.None);

        Assert.Equal(2, selected.Length);
        Assert.DoesNotContain(selected, ip => TvUrlGuard.IsPrivateIp(ip));
        Assert.Contains(selected, ip => ip.ToString() == "93.184.216.34");
    }

    // 8. Intentos de acceso a loopback y metadata endpoints bloqueados en ValidateServerUrl
    [Theory]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://localhost")]
    [InlineData("http://169.254.169.254")]
    [InlineData("http://[::1]")]
    [InlineData("http://10.1.2.3:8000")]
    [InlineData("http://192.168.0.1:8080")]
    public void ValidateServerUrl_LocalAndPrivateEndpoints_Rejected(string serverUrl)
    {
        var (isValid, uri, _, error) = TvXtreamService.ValidateServerUrl(serverUrl);
        Assert.False(isValid);
        Assert.Null(uri);
        Assert.NotNull(error);
        Assert.Contains("bloqueado", error, StringComparison.OrdinalIgnoreCase);
    }

    // 9. Verificar que las credenciales no aparecen en logs ni excepciones
    [Fact]
    public void TvXtreamSecurity_Redact_ComprehensivelyRedactsPasswordsAndUsernames()
    {
        string rawError = "Failed connecting to https://server.invalid/player_api.php?username=mi_usuario&password=SuperClaveSecreta123";
        string redacted = TvXtreamSecurity.Redact(rawError, "mi_usuario", "SuperClaveSecreta123");

        Assert.DoesNotContain("SuperClaveSecreta123", redacted);
        Assert.DoesNotContain("mi_usuario", redacted);
        Assert.Contains("[REDACTED]", redacted);

        string streamPath = "Error on http://server.invalid/live/mi_usuario/SuperClaveSecreta123/456.ts";
        string redactedPath = TvXtreamSecurity.Redact(streamPath, "mi_usuario", "SuperClaveSecreta123");

        Assert.DoesNotContain("SuperClaveSecreta123", redactedPath);
        Assert.DoesNotContain("mi_usuario", redactedPath);

        var ex = new HttpRequestException("Socket error at https://user:secret123@server.invalid/live");
        var cleanEx = TvXtreamSecurity.RedactException(ex, "user", "secret123");
        Assert.DoesNotContain("secret123", cleanEx.Message);
    }

    // 10. Eliminación de credenciales almacenadas (memoria y disco limpios)
    [Fact]
    public void TvXtreamStore_AddAndRemoveConnection_CleansMemoryAndDisk()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "LexTv_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new TvXtreamStore(tempDir);
            var conn = new TvXtreamConnection
            {
                Id = "xc_test_123",
                Name = "Mi IPTV",
                ServerUrl = "https://stream.ejemplo.invalid",
                SaveCredentials = true
            };

            store.AddOrUpdateConnection(conn, "test_user", "test_pass");

            // Verificar que en disco no se guarda en texto claro
            string storeFile = Path.Combine(tempDir, "tv_xtream.json");
            Assert.True(File.Exists(storeFile));
            string jsonOnDisk = File.ReadAllText(storeFile);
            Assert.DoesNotContain("test_pass", jsonOnDisk);

            // Verificar que se descifra en memoria
            var creds = store.GetCredentials("xc_test_123");
            Assert.NotNull(creds);
            Assert.Equal("test_user", creds.Username);
            Assert.Equal("test_pass", creds.Password);

            // Eliminar conexión
            bool removed = store.RemoveConnection("xc_test_123");
            Assert.True(removed);

            // Credenciales y conexión deben ser null
            Assert.Null(store.GetConnection("xc_test_123"));
            Assert.Null(store.GetCredentials("xc_test_123"));

            string jsonAfterRemove = File.ReadAllText(storeFile);
            Assert.DoesNotContain("xc_test_123", jsonAfterRemove);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    // 11. Rechazo de solicitudes a hosts no autorizados o denylisted
    [Fact]
    public void ValidateServerUrl_DenylistedOrInvalidHosts_Rejected()
    {
        // 1. Host denylisted (si existe en TvDenylist)
        TvDenylist.AddForTesting("piratestream.invalid");
        try
        {
            var (isValid, _, _, error) = TvXtreamService.ValidateServerUrl("https://piratestream.invalid");
            Assert.False(isValid);
            Assert.Contains("deshabilitada", error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TvDenylist.ClearForTesting();
        }

        // 2. Esquema no HTTP/HTTPS (ftp://, file://, etc.)
        var (validFtp, _, _, errorFtp) = TvXtreamService.ValidateServerUrl("ftp://servidor.invalid/iptv");
        Assert.False(validFtp);
        Assert.Contains("protocolos HTTP", errorFtp);

        // 3. UserInfo en la URL
        var (validUserInfo, _, _, errorUserInfo) = TvXtreamService.ValidateServerUrl("https://user:pass@servidor.invalid");
        Assert.False(validUserInfo);
        Assert.Contains("No incluyas usuario", errorUserInfo);
    }

    // 12. Fallback de compatibilidad: servidor que responde 400 Action not found y valida con get_live_categories
    [Fact]
    public async Task AuthenticateAsync_ServerReturns400ActionNotFound_FallsBackToGetLiveCategoriesSuccessfully()
    {
        string categoriesJson = @"[
            {""category_id"": 10, ""category_name"": ""Noticias Internacionales"", ""parent_id"": 0},
            {""category_id"": 20, ""category_name"": ""Deportes en Vivo"", ""parent_id"": 0}
        ]";

        using var handler = new MockHttpHandler(req =>
        {
            string q = req.RequestUri?.Query ?? "";
            if (!q.Contains("action="))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(@"{""message"":""Action not found""}", Encoding.UTF8, "application/json")
                };
            }

            if (q.Contains("action=get_live_categories"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(categoriesJson, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new HttpClient(handler);
        var service = new TvXtreamService(injectedClient: client);

        var result = await service.AuthenticateAsync("https://stream.ejemplo.invalid", "usuario_test", "clave_test", CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.UserInfo);
        Assert.Equal("usuario_test", result.UserInfo.Username);
        Assert.Equal("Active", result.UserInfo.Status);
        Assert.Equal(1, result.UserInfo.Auth);
    }

    // 13. Fallback de compatibilidad: credenciales incorrectas en fallback rechazan autenticación
    [Fact]
    public async Task AuthenticateAsync_Fallback_InvalidCredentials_RejectsAuthentication()
    {
        using var handler = new MockHttpHandler(req =>
        {
            string q = req.RequestUri?.Query ?? "";
            if (!q.Contains("action="))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(@"{""message"":""Action not found""}", Encoding.UTF8, "application/json")
                };
            }

            // Credenciales incorrectas: responde 404 (o 401)
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new HttpClient(handler);
        var service = new TvXtreamService(injectedClient: client);

        var result = await service.AuthenticateAsync("https://stream.ejemplo.invalid", "usuario_malo", "clave_mala", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("rechazada", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // 14. Fallback de compatibilidad: HTTP 200 con JSON malformado es rechazado con error claro
    [Fact]
    public async Task AuthenticateAsync_Fallback_Http200WithMalformedJson_ReturnsError()
    {
        using var handler = new MockHttpHandler(req =>
        {
            string q = req.RequestUri?.Query ?? "";
            if (!q.Contains("action="))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(@"{""message"":""Action not found""}", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body>502 Bad Gateway Nginx</body></html>", Encoding.UTF8, "text/html")
            };
        });

        using var client = new HttpClient(handler);
        var service = new TvXtreamService(injectedClient: client);

        var result = await service.AuthenticateAsync("https://stream.ejemplo.invalid", "usuario_test", "clave_test", CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    // 15. Fallback de compatibilidad: cuenta válida sin categorías (array vacío [])
    [Fact]
    public async Task AuthenticateAsync_Fallback_EmptyCategoriesArray_AuthenticatesSuccessfully()
    {
        using var handler = new MockHttpHandler(req =>
        {
            string q = req.RequestUri?.Query ?? "";
            if (!q.Contains("action="))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(@"{""message"":""Action not found""}", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            };
        });

        using var client = new HttpClient(handler);
        var service = new TvXtreamService(injectedClient: client);

        var result = await service.AuthenticateAsync("https://stream.ejemplo.invalid", "usuario_test", "clave_test", CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.UserInfo);
        Assert.Equal("Active", result.UserInfo.Status);
    }

    // 16. Fallback a get_vod_categories si el servidor solo soporta VOD y responde Action not found a live
    [Fact]
    public async Task AuthenticateAsync_Fallback_LiveNotSupportedButVodSupported_AuthenticatesSuccessfully()
    {
        string vodCategoriesJson = @"[{""category_id"": 5, ""category_name"": ""Acción""}]";

        using var handler = new MockHttpHandler(req =>
        {
            string q = req.RequestUri?.Query ?? "";
            if (!q.Contains("action="))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(@"{""message"":""Action not found""}", Encoding.UTF8, "application/json")
                };
            }

            if (q.Contains("action=get_live_categories"))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(@"{""message"":""Action not found""}", Encoding.UTF8, "application/json")
                };
            }

            if (q.Contains("action=get_vod_categories"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(vodCategoriesJson, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new HttpClient(handler);
        var service = new TvXtreamService(injectedClient: client);

        var result = await service.AuthenticateAsync("https://stream.ejemplo.invalid", "usuario_test", "clave_test", CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.UserInfo);
        Assert.Equal("Active", result.UserInfo.Status);
    }

    // 17. Aislamiento de catálogos: TV en vivo disponible pero VOD no disponible
    [Fact]
    public async Task FetchCatalogs_LiveAvailableVodUnavailable_LoadsLiveChannelsGracefully()
    {
        string liveStreamsJson = @"[{""num"": 1, ""name"": ""Canal Test"", ""stream_id"": ""100"", ""category_id"": ""1""}]";

        using var handler = new MockHttpHandler(req =>
        {
            string q = req.RequestUri?.Query ?? "";
            if (q.Contains("action=get_live_streams"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(liveStreamsJson, Encoding.UTF8, "application/json")
                };
            }

            if (q.Contains("action=get_vod_streams"))
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            };
        });

        using var client = new HttpClient(handler);
        var service = new TvXtreamService(injectedClient: client);

        var liveChannels = await service.FetchLiveChannelsAsync("https://stream.ejemplo.invalid", "u", "p", "c1", "MiTV", CancellationToken.None);
        var vodMovies = await service.FetchVodMoviesAsync("https://stream.ejemplo.invalid", "u", "p", "c1", CancellationToken.None);

        Assert.Single(liveChannels);
        Assert.Equal("Canal Test", liveChannels[0].Name);
        Assert.Empty(vodMovies);
    }

    // 18. Aislamiento de catálogos: VOD disponible pero TV en vivo no disponible
    [Fact]
    public async Task FetchCatalogs_VodAvailableLiveUnavailable_LoadsVodGracefully()
    {
        string vodStreamsJson = @"[{""name"": ""Pelicula Test"", ""stream_id"": ""500"", ""container_extension"": ""mp4"", ""category_id"": ""1""}]";

        using var handler = new MockHttpHandler(req =>
        {
            string q = req.RequestUri?.Query ?? "";
            if (q.Contains("action=get_live_streams"))
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            if (q.Contains("action=get_vod_streams"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(vodStreamsJson, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            };
        });

        using var client = new HttpClient(handler);
        var service = new TvXtreamService(injectedClient: client);

        var liveChannels = await service.FetchLiveChannelsAsync("https://stream.ejemplo.invalid", "u", "p", "c1", "MiTV", CancellationToken.None);
        var vodMovies = await service.FetchVodMoviesAsync("https://stream.ejemplo.invalid", "u", "p", "c1", CancellationToken.None);

        Assert.Empty(liveChannels);
        Assert.Single(vodMovies);
        Assert.Equal("Pelicula Test", vodMovies[0].Title);
    }

    // 19. Manejo de timeout al conectar
    [Fact]
    public async Task AuthenticateAsync_Timeout_ReturnsGracefulTimeoutError()
    {
        using var handler = new MockHttpHandler(req =>
        {
            throw new TaskCanceledException("The operation was canceled due to timeout.");
        });

        using var client = new HttpClient(handler);
        var service = new TvXtreamService(injectedClient: client);

        var result = await service.AuthenticateAsync("https://stream.ejemplo.invalid", "u", "p", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Tiempo de espera agotado", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // 20. Manejo de servidor desconectado
    [Fact]
    public async Task AuthenticateAsync_ServerDisconnected_ReturnsGracefulConnectionError()
    {
        using var handler = new MockHttpHandler(req =>
        {
            throw new HttpRequestException("Connection refused", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused));
        });

        using var client = new HttpClient(handler);
        var service = new TvXtreamService(injectedClient: client);

        var result = await service.AuthenticateAsync("https://stream.ejemplo.invalid", "u", "p", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("No se pudo conectar", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // 21. Normalización de URL sin esquema http/https
    [Theory]
    [InlineData("xtream.ejemplo.invalid", "http://xtream.ejemplo.invalid/")]
    [InlineData("servidor.ejemplo.invalid:8080", "http://servidor.ejemplo.invalid:8080/")]
    [InlineData("https://seguro.ejemplo.invalid", "https://seguro.ejemplo.invalid/")]
    public void ValidateServerUrl_AutoAddsHttpSchemeIfMissing(string input, string expected)
    {
        var (isValid, uri, _, error) = TvXtreamService.ValidateServerUrl(input);
        Assert.True(isValid);
        Assert.Null(error);
        Assert.NotNull(uri);
        Assert.Equal(expected, uri.AbsoluteUri);
    }

    // 22. Reconocimiento exclusivo de tv.lex.local como origen interno seguro
    [Fact]
    public void InternalTvOrigin_RecognizesOnlyExactTvLexLocalHttps()
    {
        Assert.True(TvUrlGuard.IsInternalTvOrigin(new Uri("https://tv.lex.local")));
        Assert.True(TvUrlGuard.IsInternalTvOrigin(new Uri("https://tv.lex.local/api/xtream/live")));

        // HTTP debe ser rechazado
        Assert.False(TvUrlGuard.IsInternalTvOrigin(new Uri("http://tv.lex.local/api/xtream/live")));
        // Puertos no estándar deben ser rechazados
        Assert.False(TvUrlGuard.IsInternalTvOrigin(new Uri("https://tv.lex.local:8443/api/xtream/live")));
        // UserInfo debe ser rechazado
        Assert.False(TvUrlGuard.IsInternalTvOrigin(new Uri("https://admin:123@tv.lex.local/api/xtream/live")));
        // Otros dominios .local deben ser rechazados
        Assert.False(TvUrlGuard.IsInternalTvOrigin(new Uri("https://otro.local/api/xtream/live")));
        Assert.False(TvUrlGuard.IsInternalTvOrigin(new Uri("https://router.local/")));
    }

    // 23. Validación de rutas virtuales internas de streaming
    [Theory]
    [InlineData("https://tv.lex.local/api/xtream/live?conn=c1&id=10", true)]
    [InlineData("https://tv.lex.local/api/xtream/vod?conn=c1&id=20&ext=mp4", true)]
    [InlineData("https://tv.lex.local/api/xtream/series?conn=c1&id=30&ext=mkv", true)]
    [InlineData("https://tv.lex.local/api/other/path", false)]
    [InlineData("https://otro.local/api/xtream/live?conn=c1&id=10", false)]
    [InlineData("http://tv.lex.local/api/xtream/live?conn=c1&id=10", false)]
    [InlineData("http://evil.com/fake", false)]
    public void IsInternalTvStreamUrl_ValidatesCorrectEndpoints(string url, bool expectedValid)
    {
        bool valid = TvUrlGuard.IsInternalTvStreamUrl(url, out var uri);
        Assert.Equal(expectedValid, valid);
        if (expectedValid)
        {
            Assert.NotNull(uri);
            Assert.Equal("tv.lex.local", uri.Host);
        }
    }

    // 24. Todos los demás dominios .local siguen bloqueados para URLs remotas
    [Theory]
    [InlineData("http://printer.local/stream.m3u8")]
    [InlineData("https://device.local:8080/live")]
    [InlineData("http://sub.domain.local/vod")]
    [InlineData("https://tv.lex.local/api/xtream/live")] // No permitido como destino remoto externo
    public void IsAllowedRemoteUrl_BlocksAllLocalDomains(string url)
    {
        bool allowed = TvUrlGuard.IsAllowedRemoteUrl(url, out _);
        Assert.False(allowed);
    }

    // 25. Redirecciones remotas hacia dominios .local o IPs privadas se bloquean estrictamente
    [Fact]
    public async Task GetWithSafeRedirectsAsync_RedirectToPrivateOrLocal_ThrowsBlockedException()
    {
        using var handler = new MockHttpHandler(req =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Redirect);
            // Intentar redirigir hacia IP privada o tv.lex.local
            resp.Headers.Location = new Uri("http://192.168.1.1/movie.mp4");
            return resp;
        });

        using var client = new HttpClient(handler);
        var ex = await Assert.ThrowsAsync<TvProxyErrorException>(async () =>
        {
            await TvUrlGuard.GetWithSafeRedirectsAsync(client, new Uri("https://stream.ejemplo.invalid/test"), null, CancellationToken.None);
        });

        Assert.Equal("blocked", ex.ErrorClass);
    }

    // 26. Normalización de rutas relativas de pósteres e imágenes VOD
    [Theory]
    [InlineData("http://servidor.com/images/poster.jpg", "http://base.com", "http://servidor.com/images/poster.jpg")]
    [InlineData("https://tmdb.org/t/p/w500/abc.jpg", "http://base.com", "https://tmdb.org/t/p/w500/abc.jpg")]
    [InlineData("/images/poster.jpg", "http://xtream.ejemplo.invalid:80", "http://xtream.ejemplo.invalid:80/images/poster.jpg")]
    [InlineData("images/poster.jpg", "http://xtream.ejemplo.invalid", "http://xtream.ejemplo.invalid/images/poster.jpg")]
    [InlineData("poster.png", "https://servidor.com:8080/", "https://servidor.com:8080/poster.png")]
    public void NormalizePosterUrl_ResolvesCorrectly(string raw, string serverBase, string expected)
    {
        string? resolved = TvXtreamService.NormalizePosterUrl(raw, serverBase);
        Assert.Equal(expected, resolved);
    }

    // 27. ParseVodStreams aplica la normalización de pósteres con serverUrl
    [Fact]
    public void ParseVodStreams_WithServerUrl_ResolvesRelativePosters()
    {
        string json = @"[{
            ""stream_id"": ""42"",
            ""name"": ""Godzilla Test"",
            ""container_extension"": ""mp4"",
            ""stream_icon"": ""/images/godzilla.jpg""
        }]";

        var categories = new Dictionary<string, string>();
        var items = TvXtreamService.ParseVodStreams(json, categories, "conn1", "http://xtream.ejemplo.invalid:80");

        Assert.Single(items);
        Assert.Equal("http://xtream.ejemplo.invalid:80/images/godzilla.jpg", items[0].Logo);
        Assert.StartsWith("https://tv.lex.local/api/xtream/vod", items[0].Url);
    }

    // 28. Preservación y propagación de encabezados Range y 206 Partial Content
    [Fact]
    public void BuildForwardHeaders_PartialContent_PreservesRangeAndAcceptRanges()
    {
        var resp = new HttpResponseMessage(HttpStatusCode.PartialContent);
        resp.Content = new ByteArrayContent(new byte[1024]);
        resp.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(0, 1023, 5000000);

        var headers = TvStreamProxy.BuildForwardHeaders(resp);

        Assert.True(headers.ContainsKey("Content-Range"));
        Assert.Equal("bytes 0-1023/5000000", headers["Content-Range"]);
        Assert.True(headers.ContainsKey("Content-Length"));
        Assert.Equal("1024", headers["Content-Length"]);
        Assert.True(headers.ContainsKey("Accept-Ranges"));
        Assert.Equal("bytes", headers["Accept-Ranges"]);
    }

    // 29. Respuesta HTTP 200 sin soporte Range preserva Content-Length sin inyectar Content-Range
    [Fact]
    public void BuildForwardHeaders_OkResponse_PreservesContentLength()
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK);
        resp.Content = new ByteArrayContent(new byte[2048]);

        var headers = TvStreamProxy.BuildForwardHeaders(resp);

        Assert.False(headers.ContainsKey("Content-Range"));
        Assert.True(headers.ContainsKey("Content-Length"));
        Assert.Equal("2048", headers["Content-Length"]);
    }

    // 30. Las URLs virtuales de streaming no exponen credenciales en texto claro
    [Fact]
    public void ParseLiveAndVod_DoNotExposeCredentialsInUrl()
    {
        string liveJson = @"[{""stream_id"": ""99"", ""name"": ""Canal Test""}]";
        string vodJson = @"[{""stream_id"": ""88"", ""name"": ""Peli Test"", ""container_extension"": ""mp4""}]";

        var live = TvXtreamService.ParseLiveStreams(liveJson, new(), "conn_secret", "Canales", "http://servidor.com");
        var vod = TvXtreamService.ParseVodStreams(vodJson, new(), "conn_secret", "http://servidor.com");

        Assert.DoesNotContain("usuario", live[0].Url);
        Assert.DoesNotContain("clave", live[0].Url);
        Assert.DoesNotContain("usuario", vod[0].Url);
        Assert.DoesNotContain("clave", vod[0].Url);
        Assert.StartsWith("https://tv.lex.local/api/xtream/live", live[0].Url);
        Assert.StartsWith("https://tv.lex.local/api/xtream/vod", vod[0].Url);
    }

    // 31. Normalización de pósteres con esquema relativo (//), barras escapadas, nulos y base sin esquema
    [Theory]
    [InlineData("//cdn.ejemplo.invalid/posters/p1.jpg", "https://panel.ejemplo.invalid", "https://cdn.ejemplo.invalid/posters/p1.jpg")]
    [InlineData("/img/cover.png", "panel.ejemplo.invalid:8080", "http://panel.ejemplo.invalid:8080/img/cover.png")]
    [InlineData(@"http:\/\/cdn.ejemplo.invalid\/a\/b.jpg", "panel.ejemplo.invalid", "http://cdn.ejemplo.invalid/a/b.jpg")]
    [InlineData("null", "https://panel.ejemplo.invalid", null)]
    [InlineData("N/A", "https://panel.ejemplo.invalid", null)]
    public void NormalizePosterUrl_HandlesExtendedVariants(string raw, string serverBase, string? expected)
    {
        Assert.Equal(expected, TvXtreamService.NormalizePosterUrl(raw, serverBase));
    }

    // 32. ParseVodStreams y ParseSeriesInfoEpisodes soportan campos alternos de portada, extensión vacía y episodes como array
    [Fact]
    public void ParseVodAndSeries_SupportAlternatePosterFieldsEmptyExtAndArrayEpisodes()
    {
        string vodJson = @"[{
            ""stream_id"": 77,
            ""name"": ""Cine Ejemplo"",
            ""container_extension"": """",
            ""movie_image"": ""/posters/cine77.jpg""
        }]";

        var vods = TvXtreamService.ParseVodStreams(vodJson, new(), "c_vod", "panel.ejemplo.invalid");
        Assert.Single(vods);
        Assert.Equal("http://panel.ejemplo.invalid/posters/cine77.jpg", vods[0].Logo);
        Assert.EndsWith("&ext=mp4", vods[0].Url);

        string seriesJson = @"{
            ""episodes"": [
                {
                    ""id"": ""901"",
                    ""title"": ""Piloto"",
                    ""season"": ""2"",
                    ""episode_num"": ""1"",
                    ""container_extension"": """",
                    ""info"": { ""cover_big"": ""//img.ejemplo.invalid/ep1.jpg"" }
                }
            ]
        }";

        var series = new TvXtreamSeriesItem { SeriesId = "s1", Name = "Serie Demo", Cover = null };
        var eps = TvXtreamService.ParseSeriesInfoEpisodes(seriesJson, series, "c_vod", "panel.ejemplo.invalid");
        Assert.Single(eps);
        Assert.Equal(2, eps[0].SeasonNumber);
        Assert.Equal(1, eps[0].EpisodeNumber);
        Assert.Equal("https://img.ejemplo.invalid/ep1.jpg", eps[0].Logo);
        Assert.EndsWith("&ext=mp4", eps[0].Url);
    }

    // 33. ResolveUpstreamStreamUrl normaliza conexiones heredadas cuyo ServerUrl carecía de http://
    [Fact]
    public void ResolveUpstreamStreamUrl_NormalizesSchemelessServerUrl()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "LexTv_Resolve_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new TvXtreamStore(tempDir);
            store.AddOrUpdateConnection(new TvXtreamConnection
            {
                Id = "xc_no_scheme",
                Name = "Demo",
                ServerUrl = "stream.ejemplo.invalid:8080",
                SaveCredentials = true
            }, "user_demo", "pass_demo");

            var resolved = TvXtreamService.ResolveUpstreamStreamUrl(store, "xc_no_scheme", "live", "101", null);
            Assert.True(resolved.Success);
            Assert.Equal("http://stream.ejemplo.invalid:8080/live/user_demo/pass_demo/101.ts", resolved.UpstreamUrl);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // 34. Detección de tipo MIME de imagen por magic bytes y rechazo de HTML disfrazado
    [Fact]
    public void SniffImageContentType_DetectsMagicBytesAndRejectsHtml()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00];
        byte[] webp = Encoding.ASCII.GetBytes("RIFF1234WEBP");
        byte[] html = Encoding.UTF8.GetBytes("  <!DOCTYPE html><html><body>403</body></html>");

        Assert.Equal("image/jpeg", TvStreamProxy.SniffImageContentType(jpeg, "application/octet-stream"));
        Assert.Equal("image/png", TvStreamProxy.SniffImageContentType(png, "application/octet-stream"));
        Assert.Equal("image/webp", TvStreamProxy.SniffImageContentType(webp, null));
        Assert.Null(TvStreamProxy.SniffImageContentType(html, "image/jpeg"));
    }

    // 35. FetchImageAsync acepta octet-stream JPEG tras rotación a Magma Player/10
    [Fact]
    public async Task FetchImageAsync_OctetStreamJpeg_RetriesWithMagmaUaAndSucceeds()
    {
        TvPlaybackHints.Clear();
        var triedUas = new List<string>();
        byte[] jpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46];

        using var handler = new MockHttpHandler(req =>
        {
            string ua = req.Headers.UserAgent?.ToString() ?? "";
            triedUas.Add(ua);
            if (!string.Equals(ua, TvUrlGuard.MagmaPlayerUserAgent, StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }

            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(jpegBytes)
            };
            resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            return resp;
        });

        using var client = new HttpClient(handler);
        try
        {
            using var res = await TvStreamProxy.FetchImageAsync(
                "https://cdn.ejemplo.invalid/posters/cover.bin",
                "conn_img_magma",
                client,
                CancellationToken.None);

            Assert.Equal(200, res.StatusCode);
            Assert.Equal("image/jpeg", res.ContentType);
            Assert.Contains(TvUrlGuard.MagmaPlayerUserAgent, triedUas);
            Assert.Equal(TvUrlGuard.MagmaPlayerUserAgent, TvPlaybackHints.GetUserAgent("conn_img_magma", "cdn.ejemplo.invalid"));
        }
        finally
        {
            TvPlaybackHints.Clear();
        }
    }

    // 36. URLs internas relativas se aceptan y rutas desconocidas en tv.lex.local no devuelven blocked
    [Fact]
    public async Task InternalTvUrls_AcceptRelative_AndUnknownInternalDoesNotReportBlocked()
    {
        Assert.True(TvUrlGuard.IsInternalTvStreamUrl("/api/xtream/live?conn=c1&id=1", out var relUri));
        Assert.NotNull(relUri);
        Assert.True(TvUrlGuard.IsInternalTvHostUrl("http://tv.lex.local/api/xtream/vod?conn=c1&id=2&ext=mp4"));

        var probe = await TvStreamProxy.ProbeAsync("https://tv.lex.local/api/unknown-route", CancellationToken.None);
        Assert.Equal(404, probe.Status);
        Assert.Equal("not_found", probe.ErrorClass);
        Assert.NotEqual("blocked", probe.ErrorClass);
    }

    // 37. Parseo de claves compactas estilo Magma, URLs directas (&direct=) y rutas relativas TMDB
    [Fact]
    public void ParseStreams_SupportsCompactMagmaKeysDirectUrlsAndTmdbRelativePosters()
    {
        var cats = new Dictionary<string, string> { ["10"] = "Entretenimiento" };
        string liveJson = @"[
            {
                ""a"": ""501"",
                ""b"": ""Canal Compacto HD"",
                ""c"": ""/t/p/w300/abc123logo.png"",
                ""d"": ""https://cdn.ejemplo.invalid/live/ch501/index.m3u8"",
                ""e"": [""10""]
            }
        ]";

        var channels = TvXtreamService.ParseLiveStreams(liveJson, cats, "c_magma", "Demo", "https://panel.ejemplo.invalid");
        Assert.Single(channels);
        Assert.Equal("Canal Compacto HD", channels[0].Name);
        Assert.Equal("Entretenimiento", channels[0].Group);
        Assert.Equal("https://image.tmdb.org/t/p/w300/abc123logo.png", channels[0].Logo);
        Assert.Contains("&direct=https%3A%2F%2Fcdn.ejemplo.invalid%2Flive%2Fch501%2Findex.m3u8", channels[0].Url);

        string vodJson = @"[
            {
                ""stream_id"": ""801"",
                ""name"": ""Pelicula TMDB Hash"",
                ""poster"": ""/9xjZS2rlVxm8SFx8kPC3aIGCOYQ.jpg"",
                ""container_extension"": ""mkv""
            },
            {
                ""stream_id"": ""802"",
                ""name"": ""Pelicula TMDB Size"",
                ""backdrop"": ""/w500/xyz12345back.jpg"",
                ""direct_source"": ""http://127.0.0.1/blocked.mp4""
            }
        ]";

        var vods = TvXtreamService.ParseVodStreams(vodJson, new Dictionary<string, string>(), "c_magma", "https://panel.ejemplo.invalid");
        Assert.Equal(2, vods.Count);
        Assert.Equal("https://image.tmdb.org/t/p/w500/9xjZS2rlVxm8SFx8kPC3aIGCOYQ.jpg", vods[0].Logo);
        Assert.Equal("https://image.tmdb.org/t/p/w500/xyz12345back.jpg", vods[1].Logo);
        // La URL privada 127.0.0.1 no debe adjuntarse como &direct=
        Assert.DoesNotContain("&direct=", vods[1].Url);
    }

    // 38. ResolveUpstreamStreamCandidates incluye URL directa, rutas modernas, rutas heredadas y fallback get_vod_info
    [Fact]
    public void ResolveUpstreamStreamCandidates_IncludesDirectLegacyAndVodInfoPaths()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "LexTv_Cand_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new TvXtreamStore(tempDir);
            store.AddOrUpdateConnection(new TvXtreamConnection
            {
                Id = "xc_cand",
                Name = "Demo",
                ServerUrl = "https://stream.ejemplo.invalid",
                SaveCredentials = true
            }, "usr1", "pwd1");

            string liveInternal = "https://tv.lex.local/api/xtream/live?conn=xc_cand&id=42&direct=" +
                                  Uri.EscapeDataString("https://edge.ejemplo.invalid/live/42.m3u8");
            var liveRes = TvXtreamService.ResolveUpstreamStreamCandidates(store, liveInternal);
            Assert.True(liveRes.Success);
            Assert.Equal("https://edge.ejemplo.invalid/live/42.m3u8", liveRes.Candidates[0]);
            Assert.Contains("https://stream.ejemplo.invalid/live/usr1/pwd1/42.ts", liveRes.Candidates);
            Assert.Contains("https://stream.ejemplo.invalid/live/usr1/pwd1/42.m3u8", liveRes.Candidates);
            Assert.Contains("https://stream.ejemplo.invalid/usr1/pwd1/42", liveRes.Candidates);

            // Conexión huérfana de biblioteca antigua debe usar la conexión activa disponible
            string orphanVod = "https://tv.lex.local/api/xtream/vod?conn=xc_old_orphan&id=99&ext=mp4";
            var vodRes = TvXtreamService.ResolveUpstreamStreamCandidates(store, orphanVod);
            Assert.True(vodRes.Success);
            Assert.Equal("https://stream.ejemplo.invalid/movie/usr1/pwd1/99.mp4", vodRes.Candidates[0]);
            Assert.Contains("https://stream.ejemplo.invalid/movie/usr1/pwd1/99.mkv", vodRes.Candidates);
            Assert.Contains("https://stream.ejemplo.invalid/usr1/pwd1/99.mp4", vodRes.Candidates);

            string vodInfoJson = @"{ ""movie_data"": { ""container_extension"": ""mkv"" } }";
            string? infoUrl = TvXtreamService.ParseVodInfoStreamUrl(vodInfoJson, "https://stream.ejemplo.invalid", "usr1", "pwd1", "99");
            Assert.Equal("https://stream.ejemplo.invalid/movie/usr1/pwd1/99.mkv", infoUrl);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // 39. TvVodStore.ImportItems actualiza logos y URLs existentes al resincronizar
    [Fact]
    public void TvVodStore_ImportItems_UpdatesExistingLogoAndUrlInPlace()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "LexTv_VodUpd_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new TvVodStore(tempDir);
            int firstAdded = store.ImportItems(new[]
            {
                new TvVodItem
                {
                    Id = "xc_vod_oldconn_55",
                    Title = "Pelicula Prueba",
                    Url = "https://tv.lex.local/api/xtream/vod?conn=oldconn&id=55&ext=mp4",
                    Group = "Acción",
                    Logo = ""
                }
            });
            Assert.Equal(1, firstAdded);

            int secondAdded = store.ImportItems(new[]
            {
                new TvVodItem
                {
                    Id = "xc_vod_newconn_55",
                    Title = "Pelicula Prueba",
                    Url = "https://tv.lex.local/api/xtream/vod?conn=newconn&id=55&ext=mp4&direct=https%3A%2F%2Fcdn.ejemplo.invalid%2F55.mp4",
                    Group = "Acción HD",
                    Logo = "https://image.tmdb.org/t/p/w500/poster55.jpg"
                }
            });
            Assert.Equal(0, secondAdded);

            var lib = store.GetLibrarySnapshot();
            Assert.Single(lib);
            Assert.Equal("https://image.tmdb.org/t/p/w500/poster55.jpg", lib[0].Logo);
            Assert.Contains("conn=newconn", lib[0].Url);
            Assert.Equal("Acción HD", lib[0].Group);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // 40. TvDiagLog redacta credenciales y respeta límite de buffer y archivo en disco
    [Fact]
    public void TvDiagLog_RedactsSecrets_AndPersistsBoundedEntries()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "LexTv_Diag_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string r1 = TvDiagLog.RedactUrl("https://stream.ejemplo.invalid/live/miUser/miClave/635.ts?token=abc123");
            Assert.DoesNotContain("miUser", r1);
            Assert.DoesNotContain("miClave", r1);
            Assert.DoesNotContain("abc123", r1);
            Assert.Contains("/live/***/***/635.ts", r1);

            string r2 = TvDiagLog.RedactUrl("https://stream.ejemplo.invalid/miUser/miClave/14838.mp4");
            Assert.DoesNotContain("miUser", r2);
            Assert.DoesNotContain("miClave", r2);
            Assert.Contains("/***/***/14838.mp4", r2);

            string r3 = TvDiagLog.RedactUrl("https://miUser:miClave@stream.ejemplo.invalid/player_api.php?username=miUser&password=miClave");
            Assert.DoesNotContain("miUser", r3);
            Assert.DoesNotContain("miClave", r3);

            var log = new TvDiagLog(tempDir);
            log.Record("probe", "HTTP 404", "ua=Test/1 url=https://stream.ejemplo.invalid/movie/u1/p1/99.mp4");
            log.Record("fallback", "HTTP 404", "ua=Test/1 url=https://stream.ejemplo.invalid/u1/p1/99");

            var snap = log.GetSnapshot();
            Assert.Equal(2, snap.Count);
            Assert.DoesNotContain("u1", snap[0].Detail);
            Assert.DoesNotContain("p1", snap[0].Detail);
            Assert.DoesNotContain("u1", snap[1].Detail);
            Assert.DoesNotContain("p1", snap[1].Detail);
            Assert.Contains("/***/***/99", snap[1].Detail);
            Assert.True(File.Exists(log.LogPath));
            string onDisk = File.ReadAllText(log.LogPath);
            Assert.DoesNotContain("u1", onDisk);
            Assert.DoesNotContain("p1", onDisk);

            log.Clear();
            Assert.Empty(log.GetSnapshot());
            Assert.False(File.Exists(log.LogPath));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // 41. TvStudioService.ResolveImageTargetUrl prioriza rutas TMDB sobre ServerUrl del proveedor
    [Fact]
    public void ResolveImageTargetUrl_NormalizesTmdbPaths_EvenWhenXtreamServerExists()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "LexTv_ImgRes_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new TvXtreamStore(tempDir);
            store.AddOrUpdateConnection(new TvXtreamConnection
            {
                Id = "xc_img",
                Name = "Demo",
                ServerUrl = "https://stream.ejemplo.invalid",
                SaveCredentials = true
            }, "u1", "p1");

            Assert.Equal(
                "https://image.tmdb.org/t/p/w500/6ItqVHcsb2swkE7ajS6EVCH2mpd.jpg",
                TvStudioService.ResolveImageTargetUrl("/6ItqVHcsb2swkE7ajS6EVCH2mpd.jpg", "xc_img", store));

            Assert.Equal(
                "https://image.tmdb.org/t/p/w500/6ItqVHcsb2swkE7ajS6EVCH2mpd.jpg",
                TvStudioService.ResolveImageTargetUrl("6ItqVHcsb2swkE7ajS6EVCH2mpd.jpg", "xc_img", store));

            Assert.Equal(
                "https://image.tmdb.org/t/p/original/6ItqVHcsb2swkE7ajS6EVCH2mpd.jpg",
                TvStudioService.ResolveImageTargetUrl("/t/p/original/6ItqVHcsb2swkE7ajS6EVCH2mpd.jpg", "xc_img", store));

            Assert.Equal(
                "https://stream.ejemplo.invalid/logos/ch1.png",
                TvStudioService.ResolveImageTargetUrl("/logos/ch1.png", "xc_img", store));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // 42. TvVodStore normaliza portadas TMDB relativas guardadas previamente en disco al cargar
    [Fact]
    public void TvVodStore_Load_NormalizesPreExistingRelativeTmdbLogos()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "LexTv_VodLoad_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string libPath = Path.Combine(tempDir, "vod_library.json");
            File.WriteAllText(libPath, """
                [
                  {
                    "Id": "xc_vod_1",
                    "Title": "Pelicula Guardada",
                    "Url": "https://tv.lex.local/api/xtream/vod?conn=xc_old&id=1&ext=mp4",
                    "Logo": "/6ItqVHcsb2swkE7ajS6EVCH2mpd.jpg",
                    "Group": "Películas"
                  },
                  {
                    "Id": "xc_vod_2",
                    "Title": "Protocolo Relativo",
                    "Url": "https://tv.lex.local/api/xtream/vod?conn=xc_old&id=2&ext=mp4",
                    "Logo": "//cdn.ejemplo.invalid/poster2.jpg",
                    "Group": "Películas"
                  }
                ]
                """);

            var store = new TvVodStore(tempDir);
            var lib = store.GetLibrarySnapshot();
            Assert.Equal(2, lib.Count);
            Assert.Equal("https://image.tmdb.org/t/p/w500/6ItqVHcsb2swkE7ajS6EVCH2mpd.jpg", lib[0].Logo);
            Assert.Equal("https://cdn.ejemplo.invalid/poster2.jpg", lib[1].Logo);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
