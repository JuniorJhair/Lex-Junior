using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LexBrowser.Core;
using LexBrowser.Core.Pdf;
using Xunit;

namespace LexBrowser.Tests;

public class PdfStudioServiceTests
{
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }

    [Theory]
    [InlineData("file:///C:/Users/user/Downloads/Macmillan-Readers-1-Man-in-Iron-Mask.pdf")]
    [InlineData("C:\\Users\\user\\Documents\\contract.pdf")]
    [InlineData("https://example.com/books/sample.pdf")]
    [InlineData("https://example.com/books/SAMPLE.PDF")]
    [InlineData("https://docs.local/report.pdf?version=2")]
    [InlineData("http://archive.org/manual.pdf#page=5")]
    public void IsPdfTarget_ValidPdfUrls_ReturnsTrue(string url)
    {
        Assert.True(PdfStudioService.IsPdfTarget(url));
    }

    [Theory]
    [InlineData("https://pdf.lex.local/viewer.html?file=abc")]
    [InlineData("https://pdf.lex.local/viewer.html")]
    [InlineData("blob:https://example.com/abc.pdf")]
    [InlineData("data:application/pdf;base64,JVBERi0xLjQK")]
    [InlineData("https://google.com/search?q=pdf")]
    [InlineData("https://example.com/document.docx")]
    [InlineData("https://example.com/image.png")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsPdfTarget_NonPdfOrViewerUrls_ReturnsFalse(string? url)
    {
        Assert.False(PdfStudioService.IsPdfTarget(url));
    }

    [Fact]
    public void GetViewerUrl_WithLocalFilePath_FormatsLocalApiUrl()
    {
        PdfStudioService.ClearAuthorizedFiles();
        try
        {
            string localFile = @"C:\Users\test\sample.pdf";
            string viewerUrl = PdfStudioService.GetViewerUrl(localFile);

            Assert.StartsWith(PdfStudioService.VirtualOrigin + "viewer.html?file=", viewerUrl);
            string unescaped = Uri.UnescapeDataString(Uri.UnescapeDataString(viewerUrl));
            Assert.Contains("api/load-local-pdf", unescaped);
            Assert.Contains(@"C:\Users\test\sample.pdf", unescaped);
        }
        finally
        {
            PdfStudioService.ClearAuthorizedFiles();
        }
    }

    [Fact]
    public void GetViewerUrl_WithFileUri_FormatsLocalApiUrl()
    {
        PdfStudioService.ClearAuthorizedFiles();
        try
        {
            string fileUri = "file:///C:/Users/test/sample.pdf";
            string viewerUrl = PdfStudioService.GetViewerUrl(fileUri);

            Assert.StartsWith(PdfStudioService.VirtualOrigin + "viewer.html?file=", viewerUrl);
            string unescaped = Uri.UnescapeDataString(Uri.UnescapeDataString(viewerUrl));
            Assert.Contains("api/load-local-pdf", unescaped);
            Assert.Contains(@"C:\Users\test\sample.pdf", unescaped);
        }
        finally
        {
            PdfStudioService.ClearAuthorizedFiles();
        }
    }

    [Fact]
    public void GetViewerUrl_WithRemoteHttpsUrl_EncodesParamAndAuthorizesRemoteUrl()
    {
        PdfStudioService.ClearAuthorizedFiles();
        try
        {
            string remoteUrl = "https://example.org/documents/manual.pdf#page=5";
            Assert.False(PdfStudioService.IsRemoteUrlAuthorized(remoteUrl));

            string viewerUrl = PdfStudioService.GetViewerUrl(remoteUrl);

            Assert.StartsWith(PdfStudioService.VirtualOrigin + "viewer.html?file=", viewerUrl);
            Assert.Contains(Uri.EscapeDataString(remoteUrl), viewerUrl);
            Assert.True(PdfStudioService.IsRemoteUrlAuthorized(remoteUrl));
            Assert.True(PdfStudioService.IsRemoteUrlAuthorized("https://example.org/documents/manual.pdf"));
        }
        finally
        {
            PdfStudioService.ClearAuthorizedFiles();
        }
    }

    [Theory]
    [InlineData("https://user:pass@example.org/doc.pdf")]
    [InlineData("https://pdf.lex.local/secret.pdf")]
    [InlineData("https://tv.lex.local/secret.pdf")]
    [InlineData("http://169.254.169.254/metadata.pdf")]
    [InlineData("file:///C:/secret.pdf")]
    [InlineData("javascript:alert(1)")]
    public void AuthorizeRemoteUrl_RejectsUnsafeOrInternalUrls(string unsafeUrl)
    {
        PdfStudioService.ClearAuthorizedFiles();
        Assert.False(PdfStudioService.AuthorizeRemoteUrl(unsafeUrl));
        Assert.False(PdfStudioService.IsRemoteUrlAuthorized(unsafeUrl));
    }

    [Fact]
    public void OmniboxInputResolver_NormalizesDomainPdfAndIgnoresSearchQueriesWithSpaces()
    {
        PdfStudioService.ClearAuthorizedFiles();
        try
        {
            string domainOnly = OmniboxInputResolver.ResolveNavigationTarget("example.org/docs/guia.pdf");
            Assert.StartsWith(PdfStudioService.VirtualOrigin + "viewer.html?file=", domainOnly);
            Assert.True(PdfStudioService.TryUnwrapInternalViewerUrl(domainOnly, out string? unwrapped));
            Assert.Equal("https://example.org/docs/guia.pdf", unwrapped);

            string searchQuery = OmniboxInputResolver.ResolveNavigationTarget(
                "resumen historia .pdf",
                q => $"https://search.example/q={Uri.EscapeDataString(q)}");
            Assert.StartsWith("https://search.example/q=", searchQuery);
        }
        finally
        {
            PdfStudioService.ClearAuthorizedFiles();
        }
    }

    [Fact]
    public async Task FetchRemotePdfAsync_ValidRemotePdf_ReturnsBytes()
    {
        byte[] samplePdf = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<<>>\nendobj\n%%EOF");
        using var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(samplePdf)
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf") }
            }
        });
        using var client = new HttpClient(handler);

        var res = await PdfStudioService.FetchRemotePdfAsync(
            new Uri("https://example.org/manual.pdf"),
            cookieHeaderProvider: null,
            userAgent: null,
            httpClientOverride: client);

        Assert.Equal(200, res.StatusCode);
        Assert.Equal("application/pdf", res.ContentType);
        Assert.Equal(samplePdf, res.Bytes);
    }

    [Fact]
    public async Task FetchRemotePdfAsync_FollowsPublicRedirectsAndScopesCookiesPerHop()
    {
        byte[] samplePdf = Encoding.ASCII.GetBytes("%PDF-1.4\n%test\n");
        var seenCookies = new List<(string Host, string? Cookie)>();

        using var handler = new StubHttpMessageHandler(req =>
        {
            req.Headers.TryGetValues("Cookie", out var cookieVals);
            string? cookie = cookieVals != null ? string.Join("; ", cookieVals) : null;
            seenCookies.Add((req.RequestUri!.Host, cookie));

            if (req.RequestUri.Host == "portal.example.org")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://cdn.example.net/files/doc.pdf");
                return redirect;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(samplePdf)
            };
        });
        using var client = new HttpClient(handler);

        Task<string?> CookieLookup(Uri uri)
        {
            if (uri.Host == "portal.example.org") return Task.FromResult<string?>("session=abc123");
            return Task.FromResult<string?>(null);
        }

        var res = await PdfStudioService.FetchRemotePdfAsync(
            new Uri("https://portal.example.org/descargar.pdf"),
            cookieHeaderProvider: CookieLookup,
            httpClientOverride: client);

        Assert.Equal(200, res.StatusCode);
        Assert.Equal(2, seenCookies.Count);
        Assert.Equal(("portal.example.org", "session=abc123"), seenCookies[0]);
        Assert.Equal(("cdn.example.net", (string?)null), seenCookies[1]);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/internal.pdf")]
    [InlineData("http://localhost/secret.pdf")]
    [InlineData("http://192.168.1.10/router.pdf")]
    public async Task FetchRemotePdfAsync_BlocksPublicRedirectToPrivateNetwork(string redirectTarget)
    {
        using var handler = new StubHttpMessageHandler(_ =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri(redirectTarget);
            return redirect;
        });
        using var client = new HttpClient(handler);

        var res = await PdfStudioService.FetchRemotePdfAsync(
            new Uri("https://public.example.org/tricky.pdf"),
            httpClientOverride: client);

        Assert.Equal(403, res.StatusCode);
    }

    [Fact]
    public async Task FetchRemotePdfAsync_RejectsHtmlLoginResponseAndInvalidBinary()
    {
        using var htmlHandler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<!DOCTYPE html><html><body>Login required</body></html>", Encoding.UTF8, "text/html")
        });
        using var htmlClient = new HttpClient(htmlHandler);

        var htmlRes = await PdfStudioService.FetchRemotePdfAsync(
            new Uri("https://example.org/protected.pdf"),
            httpClientOverride: htmlClient);

        Assert.Equal(415, htmlRes.StatusCode);
        Assert.Contains("página web", htmlRes.ErrorReason, StringComparison.OrdinalIgnoreCase);

        using var binHandler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.ASCII.GetBytes("NOT_A_PDF_FILE"))
        });
        using var binClient = new HttpClient(binHandler);

        var binRes = await PdfStudioService.FetchRemotePdfAsync(
            new Uri("https://example.org/corrupt.pdf"),
            httpClientOverride: binClient);

        Assert.Equal(415, binRes.StatusCode);
    }

    [Fact]
    public async Task FetchRemotePdfAsync_AllowsDirectLocalhostPdfFromMock()
    {
        byte[] samplePdf = Encoding.ASCII.GetBytes("%PDF-1.6\n%localdev\n");
        using var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(samplePdf)
        });
        using var client = new HttpClient(handler);

        var res = await PdfStudioService.FetchRemotePdfAsync(
            new Uri("http://localhost:5173/boleta.pdf"),
            httpClientOverride: client);

        Assert.Equal(200, res.StatusCode);
        Assert.Equal(samplePdf, res.Bytes);
    }

    [Theory]
    [InlineData("viewer.html")]
    [InlineData("pdf.min.js")]
    [InlineData("pdf.worker.min.js")]
    [InlineData("pdf-lib.min.js")]
    public void EmbeddedResources_ArePresentAndReadable(string filename)
    {
        string resName = $"LexBrowser.Assets.pdf.{filename}";
        using var stream = typeof(PdfStudioService).Assembly.GetManifestResourceStream(resName);
        Assert.NotNull(stream);
        Assert.True(stream.Length > 0);
    }
}
