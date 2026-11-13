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
    [InlineData("tv.m3uts.xyz", "http://tv.m3uts.xyz/")]
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
    [InlineData("/images/poster.jpg", "http://tv.m3uts.xyz:80", "http://tv.m3uts.xyz:80/images/poster.jpg")]
    [InlineData("images/poster.jpg", "http://tv.m3uts.xyz", "http://tv.m3uts.xyz/images/poster.jpg")]
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
        var items = TvXtreamService.ParseVodStreams(json, categories, "conn1", "http://tv.m3uts.xyz:80");

        Assert.Single(items);
        Assert.Equal("http://tv.m3uts.xyz:80/images/godzilla.jpg", items[0].Logo);
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
}
