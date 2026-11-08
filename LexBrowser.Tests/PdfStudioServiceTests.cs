using System;
using LexBrowser.Core.Pdf;
using Xunit;

namespace LexBrowser.Tests;

public class PdfStudioServiceTests
{
    [Theory]
    [InlineData("file:///C:/Users/user/Downloads/Macmillan-Readers-1-Man-in-Iron-Mask.pdf")]
    [InlineData("C:\\Users\\user\\Documents\\contract.pdf")]
    [InlineData("https://example.com/books/sample.pdf")]
    [InlineData("https://docs.local/report.pdf?version=2")]
    [InlineData("http://archive.org/manual.pdf#page=5")]
    public void IsPdfTarget_ValidPdfUrls_ReturnsTrue(string url)
    {
        Assert.True(PdfStudioService.IsPdfTarget(url));
    }

    [Theory]
    [InlineData("https://pdf.lex.local/viewer.html?file=abc")]
    [InlineData("https://pdf.lex.local/viewer.html")]
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
        string localFile = @"C:\Users\test\sample.pdf";
        string viewerUrl = PdfStudioService.GetViewerUrl(localFile);

        Assert.StartsWith(PdfStudioService.VirtualOrigin + "viewer.html?file=", viewerUrl);
        string unescaped = Uri.UnescapeDataString(Uri.UnescapeDataString(viewerUrl));
        Assert.Contains("api/load-local-pdf", unescaped);
        Assert.Contains(@"C:\Users\test\sample.pdf", unescaped);
    }

    [Fact]
    public void GetViewerUrl_WithFileUri_FormatsLocalApiUrl()
    {
        string fileUri = "file:///C:/Users/test/sample.pdf";
        string viewerUrl = PdfStudioService.GetViewerUrl(fileUri);

        Assert.StartsWith(PdfStudioService.VirtualOrigin + "viewer.html?file=", viewerUrl);
        string unescaped = Uri.UnescapeDataString(Uri.UnescapeDataString(viewerUrl));
        Assert.Contains("api/load-local-pdf", unescaped);
        Assert.Contains(@"C:\Users\test\sample.pdf", unescaped);
    }

    [Fact]
    public void GetViewerUrl_WithRemoteHttpsUrl_EncodesParamProperly()
    {
        string remoteUrl = "https://example.org/documents/manual.pdf";
        string viewerUrl = PdfStudioService.GetViewerUrl(remoteUrl);

        Assert.StartsWith(PdfStudioService.VirtualOrigin + "viewer.html?file=", viewerUrl);
        Assert.Contains(Uri.EscapeDataString(remoteUrl), viewerUrl);
    }
}
