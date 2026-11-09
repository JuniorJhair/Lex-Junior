using System;
using System.IO;
using LexBrowser.Core;
using LexBrowser.Core.AdBlock;
using LexBrowser.Core.Pdf;
using LexBrowser.UI.Models;
using Microsoft.Web.WebView2.Core;
using Xunit;

namespace LexBrowser.Tests;

[Collection("ShieldLoggerCollection")]
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

    [Theory]
    [InlineData("www.dailymotion.com", "dailymotion.com", true)]
    [InlineData("geo.dailymotion.com", "dailymotion.com", true)]
    [InlineData("dailymotion.com", "dailymotion.com", true)]
    [InlineData("notdailymotion.com", "dailymotion.com", false)]
    [InlineData("attacker-dailymotion.com", "dailymotion.com", false)]
    [InlineData("google.com.pe", "google.com.pe", true)]
    [InlineData("mail.google.com.pe", "google.com.pe", true)]
    [InlineData("google.com.pe.attacker.com", "google.com.pe", false)]
    public void PublicSuffix_HostMatchesDomain_ValidatesBoundaries(string host, string domain, bool expected)
    {
        Assert.Equal(expected, PublicSuffix.HostMatchesDomain(host, domain));
    }

    [Theory]
    [InlineData("www.sunat.gob.pe", "sunat.gob.pe")]
    [InlineData("portal.pucp.edu.pe", "pucp.edu.pe")]
    [InlineData("noticias.com.ar", "noticias.com.ar")]
    [InlineData("bbc.co.uk", "bbc.co.uk")]
    [InlineData("sub.example.com", "example.com")]
    [InlineData("example.org", "example.org")]
    public void PublicSuffix_GetRegistrableDomain_ExtractsCompoundDomains(string host, string expected)
    {
        Assert.Equal(expected, PublicSuffix.GetRegistrableDomain(host));
    }

    [Fact]
    public void PublicSuffix_IsSameRegistrableDomain_WorksCorrectly()
    {
        Assert.True(PublicSuffix.IsSameRegistrableDomain("cdn.dailymotion.com", "geo.dailymotion.com"));
        Assert.True(PublicSuffix.IsSameRegistrableDomain("api.banco.com.pe", "banco.com.pe"));
        Assert.False(PublicSuffix.IsSameRegistrableDomain("banco.com.pe", "malicious-banco.com.pe"));
        Assert.False(PublicSuffix.IsSameRegistrableDomain("google.com", "google.attacker.com"));
    }

    [Fact]
    public void SitePolicy_Resolve_DefaultAndCompatModes()
    {
        string tempSettingsPath = Path.Combine(Path.GetTempPath(), $"lex_test_settings_{Guid.NewGuid():N}.json");
        try
        {
            SettingsManager.CustomSettingsPath = tempSettingsPath;
            SettingsManager.Load();

            var normal = SitePolicy.Resolve("example.com", temporaryCompatMode: false);
            Assert.Equal("Normal", normal.Mode);
            Assert.True(normal.ShieldEnabled);
            Assert.False(normal.IsCompatMode);
            Assert.True(normal.Network);
            Assert.True(normal.Stubs);
            Assert.True(normal.Popups);
            Assert.True(normal.Overlays);
            Assert.True(normal.Cosmetic);
            Assert.False(normal.VideoDefuser);
            Assert.False(normal.Subframes);

            var compat = SitePolicy.Resolve("example.com", temporaryCompatMode: true);
            Assert.True(compat.ShieldEnabled);
            Assert.True(compat.IsCompatMode);
            Assert.True(compat.Network);
            Assert.False(compat.Stubs);
            Assert.False(compat.ImaStub);
            Assert.True(compat.Popups);
            Assert.False(compat.Overlays);
            Assert.False(compat.Cosmetic);
            Assert.False(compat.VideoDefuser);
        }
        finally
        {
            SettingsManager.CustomSettingsPath = null;
            SettingsManager.Load();
            try { if (File.Exists(tempSettingsPath)) File.Delete(tempSettingsPath); } catch { }
        }
    }

    [Fact]
    public void SitePolicy_TryStripTrackingParameters_StripsOnlyTrackingParameters()
    {
        string tempSettingsPath = Path.Combine(Path.GetTempPath(), $"lex_test_settings_{Guid.NewGuid():N}.json");
        try
        {
            SettingsManager.CustomSettingsPath = tempSettingsPath;
            SettingsManager.Load();

            var uriWithTracking = new Uri("https://example.com/article?utm_source=newsletter&utm_medium=email&fbclid=12345&id=42");
            Assert.True(SitePolicy.TryStripTrackingParameters(uriWithTracking, out string cleaned));
            Assert.Equal("https://example.com/article?id=42", cleaned);

            // Flujos OAuth y URLs con tokens de autenticación o rutas /callback NUNCA deben alterarse
            var uriWithOAuth = new Uri("https://auth.example.com/callback?code=abc123xyz&state=state456&utm_campaign=promo");
            Assert.False(SitePolicy.TryStripTrackingParameters(uriWithOAuth, out _));

            // URL without tracking parameters returns false
            var cleanUri = new Uri("https://example.com/search?q=csharp&page=1");
            Assert.False(SitePolicy.TryStripTrackingParameters(cleanUri, out _));
        }
        finally
        {
            SettingsManager.CustomSettingsPath = null;
            SettingsManager.Load();
            try { if (File.Exists(tempSettingsPath)) File.Delete(tempSettingsPath); } catch { }
        }
    }

    [Fact]
    public void AdBlockResources_TryGetRedirectStub_ExcludesDocumentNavigations()
    {
        var vastUri = new Uri("https://adserver.example.com/vast/video.xml");
        // For Document navigation context, stub must NOT be returned (prevent XML being rendered as main page)
        Assert.False(AdBlockResources.TryGetRedirectStub(vastUri, out _, out _, "news.com", CoreWebView2WebResourceContext.Document));

        // For XHR / XML / Fetch / Other context, stub is returned
        Assert.True(AdBlockResources.TryGetRedirectStub(vastUri, out var bytes, out var contentType, "news.com", CoreWebView2WebResourceContext.XmlHttpRequest));
        Assert.Contains("xml", contentType);
        Assert.NotEmpty(bytes);
    }

    [Fact]
    public void AdBlockResources_TryGetRedirectStub_BoundaryCheckOnVast()
    {
        // Word containing 'vast' like 'vastu-shastra' must NOT be recognized as vast XML
        var benignUri = new Uri("https://example.com/articles/vastu-shastra-guide.html");
        Assert.False(AdBlockResources.TryGetRedirectStub(benignUri, out _, out _, "example.com", CoreWebView2WebResourceContext.XmlHttpRequest));
    }

    [Fact]
    public void AdBlockFilter_GoogleDomainBoundaryAndFacebookSdk()
    {
        Assert.True(AdBlockFilter.IsGoogleDomain("google.com"));
        Assert.True(AdBlockFilter.IsGoogleDomain("www.google.com.pe"));
        Assert.True(AdBlockFilter.IsGoogleDomain("mail.google.com"));
        Assert.False(AdBlockFilter.IsGoogleDomain("google.example.top"));
        Assert.False(AdBlockFilter.IsGoogleDomain("google.attacker.com"));

        // Facebook login SDK is allowed for login buttons
        var fbSdkUri = new Uri("https://connect.facebook.net/es_LA/sdk.js");
        Assert.False(AdBlockFilter.ShouldBlock(fbSdkUri, "store.com"));

        // Facebook tracking pixels/telemetry are blocked
        var fbPixelUri = new Uri("https://connect.facebook.net/signals/config/99999");
        Assert.True(AdBlockFilter.ShouldBlock(fbPixelUri, "store.com"));
        var fbTrUri = new Uri("https://www.facebook.com/tr?id=123&ev=PageView");
        Assert.True(AdBlockFilter.ShouldBlock(fbTrUri, "store.com"));
    }

    [Fact]
    public void ShieldLogger_GuestAndIncognitoPrivacy()
    {
        ShieldLogger.Clear();
        int initialCount = ShieldLogger.Count;

        var guestTab = new BrowserTab { IsGuest = true };
        ShieldLogger.LogForTab(guestTab, "REQUEST_BLOCKED_403", "attacker.com", "https://attacker.com/ad.js", "Script", "Blocked");
        Assert.Equal(initialCount, ShieldLogger.Count);

        var incognitoTab = new BrowserTab { IsIncognito = true };
        ShieldLogger.LogForTab(incognitoTab, "REQUEST_BLOCKED_403", "attacker.com", "https://attacker.com/ad.js", "Script", "Blocked");
        Assert.Equal(initialCount, ShieldLogger.Count);

        var normalTab = new BrowserTab { IsGuest = false, IsIncognito = false };
        ShieldLogger.LogForTab(normalTab, "REQUEST_BLOCKED_403", "adserver.com", "https://adserver.com/banner.js", "Script", "Blocked");
        Assert.Equal(initialCount + 1, ShieldLogger.Count);
    }
}
