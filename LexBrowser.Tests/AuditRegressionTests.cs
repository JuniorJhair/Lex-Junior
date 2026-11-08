using System;
using System.IO;
using LexBrowser.Core;
using LexBrowser.Core.Pdf;
using Xunit;

namespace LexBrowser.Tests;

public class AuditRegressionTests
{
    [Fact]
    public void PdfStudioService_BlocksUnauthorizedLocalFiles_AndAllowsExplicitlyAuthorizedPdf()
    {
        PdfStudioService.ClearAuthorizedFiles();
        string tempPdf = Path.Combine(Path.GetTempPath(), $"lex_audit_test_{Guid.NewGuid():N}.pdf");
        string tempTxt = Path.Combine(Path.GetTempPath(), $"lex_audit_test_{Guid.NewGuid():N}.txt");

        try
        {
            File.WriteAllBytes(tempPdf, new byte[] { 0x25, 0x50, 0x44, 0x46 });
            File.WriteAllText(tempTxt, "secret");

            // Archivo local no autorizado previamente debe ser rechazado (Auditoría E6)
            Assert.False(PdfStudioService.IsLocalFileAuthorized(tempPdf));

            // Archivos que no son .pdf jamás deben autorizarse
            PdfStudioService.AuthorizeLocalFile(tempTxt);
            Assert.False(PdfStudioService.IsLocalFileAuthorized(tempTxt));

            // Al generar la URL del visor (o autorizar explícitamente), el PDF queda en la lista blanca
            string viewerUrl = PdfStudioService.GetViewerUrl(tempPdf);
            Assert.True(PdfStudioService.IsLocalFileAuthorized(tempPdf));
            Assert.StartsWith("https://pdf.lex.local/viewer.html?file=", viewerUrl, StringComparison.OrdinalIgnoreCase);

            // Desempaquetar URL interna del visor devuelve la ruta original (Auditoría F5)
            Assert.True(PdfStudioService.TryUnwrapInternalViewerUrl(viewerUrl, out string? unwrapped));
            Assert.NotNull(unwrapped);
            Assert.Equal(Path.GetFullPath(tempPdf), Path.GetFullPath(unwrapped!), ignoreCase: true);
        }
        finally
        {
            PdfStudioService.ClearAuthorizedFiles();
            try { if (File.Exists(tempPdf)) File.Delete(tempPdf); } catch { }
            try { if (File.Exists(tempTxt)) File.Delete(tempTxt); } catch { }
        }
    }

    [Fact]
    public void PdfStudioService_ValidatesOriginAndRefererStrictly()
    {
        Assert.False(PdfStudioService.IsValidPdfOriginOrReferer(null, null));
        Assert.False(PdfStudioService.IsValidPdfOriginOrReferer("https://attacker.example.com", "https://attacker.example.com/page"));
        Assert.False(PdfStudioService.IsValidPdfOriginOrReferer("null", ""));
        Assert.True(PdfStudioService.IsValidPdfOriginOrReferer("https://pdf.lex.local", null));
        Assert.True(PdfStudioService.IsValidPdfOriginOrReferer(null, "https://pdf.lex.local/viewer.html?file=test.pdf"));
    }

    [Theory]
    [InlineData("localhost:3000", "http://localhost:3000")]
    [InlineData("localhost:5173/app", "http://localhost:5173/app")]
    [InlineData("127.0.0.1:8080/api", "http://127.0.0.1:8080/api")]
    [InlineData("192.168.1.1:8080/admin", "http://192.168.1.1:8080/admin")]
    [InlineData("[::1]:8080/test", "http://[::1]:8080/test")]
    [InlineData("intranet:9000/status", "http://intranet:9000/status")]
    [InlineData("github.com/dotnet/runtime", "https://github.com/dotnet/runtime")]
    [InlineData("https://example.com/docs", "https://example.com/docs")]
    public void OmniboxInputResolver_ResolvesUrlsAndHostPortsCorrectly(string rawInput, string expectedUrl)
    {
        string actual = OmniboxInputResolver.ResolveNavigationTarget(
            rawInput,
            q => $"https://www.google.com/search?q={Uri.EscapeDataString(q)}");

        Assert.Equal(expectedUrl, actual);
    }

    [Theory]
    [InlineData("como programar en c#")]
    [InlineData("noticias")]
    [InlineData("what is a port:number")]
    public void OmniboxInputResolver_RoutesSearchQueriesToSearchEngine(string query)
    {
        string actual = OmniboxInputResolver.ResolveNavigationTarget(
            query,
            q => $"https://search.example/q={Uri.EscapeDataString(q)}");

        Assert.StartsWith("https://search.example/q=", actual, StringComparison.Ordinal);
    }

    [Fact]
    public void OmniboxInputResolver_DetectsLocalAndPrivateHostsForFaviconPrivacy()
    {
        Assert.True(OmniboxInputResolver.IsLocalOrPrivateHost("localhost"));
        Assert.True(OmniboxInputResolver.IsLocalOrPrivateHost("127.0.0.1"));
        Assert.True(OmniboxInputResolver.IsLocalOrPrivateHost("10.0.0.5"));
        Assert.True(OmniboxInputResolver.IsLocalOrPrivateHost("192.168.1.1"));
        Assert.True(OmniboxInputResolver.IsLocalOrPrivateHost("172.20.0.1"));
        Assert.True(OmniboxInputResolver.IsLocalOrPrivateHost("printer.local"));
        Assert.True(OmniboxInputResolver.IsLocalOrPrivateHost("intranet-server"));

        Assert.False(OmniboxInputResolver.IsLocalOrPrivateHost("wikipedia.org"));
        Assert.False(OmniboxInputResolver.IsLocalOrPrivateHost("github.com"));
    }

    [Fact]
    public void HistoryStore_UnwrapsPdfViewerUrls_AndPrecalculatesSearchFields()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"lex_hist_audit_{Guid.NewGuid():N}.json");
        try
        {
            var store = new HistoryStore(tempFile);
            string wrappedPdf = PdfStudioService.GetViewerUrl("https://example.com/manual.pdf");

            store.AddEntry(wrappedPdf, "Manual Técnico PDF");

            var snapshot = store.GetRecentSnapshot(100);
            Assert.Single(snapshot);
            Assert.Equal("https://example.com/manual.pdf", snapshot[0].Url);
            Assert.Equal("example.com", snapshot[0].Domain);
            Assert.Equal("example.com/manual.pdf", snapshot[0].CleanDisplayUrl);
            Assert.Contains("manual técnico pdf", snapshot[0].TitleLower);
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public void PasswordManager_BuildSafeAutofillScript_DoesNotUsePostMessage()
    {
        string script = PasswordManager.BuildSafeAutofillScript("user@example.com", "S3cr3t\"Pass");
        Assert.DoesNotContain("postMessage", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("user@example.com", script, StringComparison.Ordinal);
    }
}
