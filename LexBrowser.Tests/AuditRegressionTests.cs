using System;
using System.IO;
using LexBrowser.Core;
using LexBrowser.Core.AdBlock;
using LexBrowser.Core.Pdf;
using LexBrowser.UI.Models;
using Microsoft.Web.WebView2.Core;
using Xunit;

namespace LexBrowser.Tests;

[Collection("SettingsCollection")]
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
            Assert.True(normal.Subframes);

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
            Assert.False(compat.Subframes);
        }
        finally
        {
            TestSettingsIsolation.ResetToIsolatedDefault();
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
            TestSettingsIsolation.ResetToIsolatedDefault();
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

    [Fact]
    public void BrowserThemeManager_NormalizesAndAppliesThemes_Cleanly()
    {
        Assert.Equal(BrowserThemeManager.Classic, BrowserThemeManager.NormalizeTheme(null));
        Assert.Equal(BrowserThemeManager.Classic, BrowserThemeManager.NormalizeTheme(""));
        Assert.Equal(BrowserThemeManager.Classic, BrowserThemeManager.NormalizeTheme("InvalidThemeName"));
        Assert.Equal(BrowserThemeManager.Dark, BrowserThemeManager.NormalizeTheme("Dark"));
        Assert.Equal(BrowserThemeManager.Dark, BrowserThemeManager.NormalizeTheme("Oscuro"));
        Assert.Equal(BrowserThemeManager.Light, BrowserThemeManager.NormalizeTheme("Light"));
        Assert.Equal(BrowserThemeManager.Light, BrowserThemeManager.NormalizeTheme("Claro"));
        Assert.Equal(BrowserThemeManager.Classic, BrowserThemeManager.NormalizeTheme("Classic"));
        Assert.Equal(BrowserThemeManager.Classic, BrowserThemeManager.NormalizeTheme("Clásico"));
        Assert.Equal(BrowserThemeManager.Classic, BrowserThemeManager.NormalizeTheme("Original"));

        var dict = new System.Windows.ResourceDictionary();
        BrowserThemeManager.ApplyThemePalette(dict, BrowserThemeManager.Light);
        Assert.True(dict.Contains("BgDark"));
        Assert.True(dict.Contains("SurfaceDark"));
        Assert.True(dict.Contains("TextLight"));
        Assert.True(dict.Contains("AccentColor"));

        // New Tab Page refleja el tema activo en su HTML generado
        var s = SettingsManager.Current;
        string origTheme = s.Theme;
        try
        {
            s.Theme = "Classic";
            string classicHtml = NewTabPage.GetHtml();
            Assert.Contains("#000000", classicHtml);
            Assert.Contains("Menos anuncios", classicHtml);
            Assert.Contains("Sin telemetría propia", classicHtml);
            Assert.Contains("Privado", classicHtml);
            Assert.DoesNotContain("Ultra-ligero", classicHtml, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Navegación rápida", classicHtml, StringComparison.OrdinalIgnoreCase);

            s.Theme = "Light";
            string lightHtml = NewTabPage.GetHtml();
            Assert.Contains("#F8FAFC", lightHtml);

            s.Theme = "Dark";
            string darkHtml = NewTabPage.GetHtml();
            Assert.Contains("#0F0F12", darkHtml);
        }
        finally
        {
            s.Theme = origTheme;
        }
    }

    [Fact]
    public void DailymotionRules_NeverReplacesVideoManifestM3u8WithVmapStubEvenIfQueryContainsMonetization()
    {
        var manifestUri = new Uri("https://dmxleo.dailymotion.com/cdn/manifest/video/x8t73c2.m3u8?ps=539x303&monetization_mode=none");
        bool isStub = DailymotionRules.TryGetRedirectStub(
            manifestUri,
            out _,
            out _,
            "geo.dailymotion.com",
            CoreWebView2WebResourceContext.XmlHttpRequest);
        Assert.False(isStub);

        var vmapUri = new Uri("https://www.dailymotion.com/vast/vmap?video=x8t73c2&monetization=1");
        bool isVmap = DailymotionRules.TryGetRedirectStub(
            vmapUri,
            out byte[] vmapBytes,
            out _,
            "geo.dailymotion.com",
            CoreWebView2WebResourceContext.XmlHttpRequest);
        Assert.True(isVmap);
        Assert.NotEmpty(vmapBytes);
    }

    [Fact]
    public void ClassifyWebResourceRequest_AttributesNoReferrerCrossOriginSubframeXhrToSubframeHost()
    {
        var tab = new BrowserTab();
        tab.RecordActiveSubframeHost("cdn.dotanborito.net");

        var m3u8Uri = new Uri("https://cdn.dotanborito.net/m3u8/565787491bda871ffc1862a9034ee537");
        var mainUri = new Uri("https://hentaila.com/media/slug/6");

        var (mainTabHost, originHost, policyHost, frameDesc) = ShieldRouter.ClassifyWebResourceRequest(
            m3u8Uri,
            mainUri,
            mainUri.AbsoluteUri,
            referer: "",
            secFetchDest: "empty",
            CoreWebView2WebResourceContext.XmlHttpRequest,
            secFetchSite: "same-origin",
            tab.IsKnownSubframeHost);

        Assert.Equal("hentaila.com", mainTabHost);
        Assert.Equal("cdn.dotanborito.net", originHost);
        Assert.Equal("hentaila.com", policyHost);
        Assert.StartsWith("Subframe:cdn.dotanborito.net|", frameDesc, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserThemeManager_AppliesLightContrastPalettesAndNewTabXIcon()
    {
        var lightDict = new System.Windows.ResourceDictionary();
        BrowserThemeManager.ApplyThemePalette(lightDict, BrowserThemeManager.Light);

        Assert.Equal(System.Windows.Media.Color.FromRgb(0xF1, 0xF5, 0xF9),
            Assert.IsType<System.Windows.Media.SolidColorBrush>(lightDict["TelemetryBadgeBg"]).Color);
        Assert.Equal(System.Windows.Media.Color.FromRgb(0x0F, 0x17, 0x2A),
            Assert.IsType<System.Windows.Media.SolidColorBrush>(lightDict["TelemetryBadgeFg"]).Color);
        Assert.Equal(System.Windows.Media.Color.FromRgb(0xEF, 0xF6, 0xFF),
            Assert.IsType<System.Windows.Media.SolidColorBrush>(lightDict["ShieldBadgeBg"]).Color);
        Assert.Equal(System.Windows.Media.Color.FromRgb(0x03, 0x69, 0xA1),
            Assert.IsType<System.Windows.Media.SolidColorBrush>(lightDict["ShieldBadgeActiveFg"]).Color);
        Assert.Equal(System.Windows.Media.Color.FromRgb(0xF8, 0xFA, 0xFC),
            Assert.IsType<System.Windows.Media.SolidColorBrush>(lightDict["PanelWindowBg"]).Color);
        Assert.Equal(System.Windows.Media.Color.FromRgb(0x0F, 0x17, 0x2A),
            Assert.IsType<System.Windows.Media.SolidColorBrush>(lightDict["PanelTextStrong"]).Color);
        Assert.Equal(System.Windows.Media.Color.FromRgb(0x06, 0x5F, 0x46),
            Assert.IsType<System.Windows.Media.SolidColorBrush>(lightDict["DefaultBtnDoneFg"]).Color);
        Assert.IsType<System.Windows.Media.LinearGradientBrush>(lightDict["PanelTelemetryBg"]);

        var s = SettingsManager.Current;
        string origTheme = s.Theme;
        try
        {
            s.Theme = "Light";
            string lightHtml = NewTabPage.GetHtml();
            Assert.Contains(@"fill=""#0F172A""", lightHtml, StringComparison.Ordinal);

            s.Theme = "Dark";
            string darkHtml = NewTabPage.GetHtml();
            Assert.Contains(@"fill=""#F4F4F5""", darkHtml, StringComparison.Ordinal);
        }
        finally
        {
            s.Theme = origTheme;
        }
    }

    [Fact]
    public void SettingsSidebarCopyMatchesNewTabFooter_AndThemeBindingsExist()
    {
        string? repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string settingsXaml = File.ReadAllText(Path.Combine(repoRoot, "LexBrowser", "UI", "Views", "SettingsWindow.xaml"));
        string historyXaml = File.ReadAllText(Path.Combine(repoRoot, "LexBrowser", "UI", "Views", "HistoryWindow.xaml"));
        string newTabFooter = "Sin telemetría propia · Menos anuncios · Privado";

        Assert.Contains(newTabFooter, settingsXaml, StringComparison.Ordinal);
        Assert.Contains(newTabFooter, NewTabPage.GetHtml(), StringComparison.Ordinal);
        Assert.Contains("{DynamicResource PanelWindowBg}", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource PanelWindowBg}", historyXaml, StringComparison.Ordinal);
        Assert.Contains("DefaultBrowserBtnStyle", settingsXaml, StringComparison.Ordinal);

        // Todas las claves DynamicResource Panel* / Badge* usadas en vistas deben quedar pobladas en Light, Dark y Classic
        string mainXaml = File.ReadAllText(Path.Combine(repoRoot, "LexBrowser", "MainWindow.xaml"));
        var referencedKeys = System.Text.RegularExpressions.Regex
            .Matches(settingsXaml + "\n" + historyXaml + "\n" + mainXaml, @"\{DynamicResource\s+([A-Za-z0-9_]+)\}")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(referencedKeys);
        foreach (string theme in new[] { BrowserThemeManager.Light, BrowserThemeManager.Dark, BrowserThemeManager.Classic })
        {
            var dict = new System.Windows.ResourceDictionary();
            BrowserThemeManager.ApplyThemePalette(dict, theme);
            foreach (string key in referencedKeys)
            {
                Assert.True(dict.Contains(key), $"Falta clave DynamicResource '{key}' en tema '{theme}'.");
            }
        }
    }

    [Fact]
    public void Release26_NoticesEmbeddedAndLoaded_CreditingCreator()
    {
        string loaded = LexBrowser.UI.Views.SettingsWindow.LoadThirdPartyNoticesText();
        Assert.Contains("Junior Pacherrez", loaded, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Web.WebView2", loaded, StringComparison.Ordinal);
        Assert.Contains("Mozilla PDF.js", loaded, StringComparison.Ordinal);
        Assert.DoesNotContain("no encontrado", loaded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no disponible", loaded, StringComparison.OrdinalIgnoreCase);

        using Stream? embedded = typeof(LexBrowser.UI.Views.SettingsWindow).Assembly
            .GetManifestResourceStream("THIRD_PARTY_NOTICES.txt");
        Assert.NotNull(embedded);
        using var reader = new StreamReader(embedded!, System.Text.Encoding.UTF8);
        string embeddedText = reader.ReadToEnd();
        Assert.Contains("Junior Pacherrez", embeddedText, StringComparison.Ordinal);
    }

    [Fact]
    public void Release26_ShieldLogExportWritesToLogsSubdirectory()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"lex_shield_log_test_{Guid.NewGuid():N}");
        try
        {
            string? exported = LexBrowser.MainWindow.WriteShieldLogExport("evento-test-shield", tempRoot);
            string expectedPath = Path.Combine(tempRoot, "Logs", "LexJunior_ShieldLog.txt");

            Assert.Equal(expectedPath, exported);
            Assert.True(File.Exists(expectedPath));
            Assert.Equal("evento-test-shield", File.ReadAllText(expectedPath, System.Text.Encoding.UTF8));
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Release26_OfficialUiAndWebsiteCopy_DefaultsClassicAndNoPlaceholders()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string settingsXaml = File.ReadAllText(Path.Combine(repoRoot, "LexBrowser", "UI", "Views", "SettingsWindow.xaml"));
        string webHtml = File.ReadAllText(Path.Combine(repoRoot, "website", "index.html"));
        string webJs = File.ReadAllText(Path.Combine(repoRoot, "website", "script.js"));

        Assert.Contains("Adaptador de Google IMA SDK", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("Modo Diagnóstico Verboso de LexShield", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("(Temporal en esta sesión)", settingsXaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Simulador de Google IMA", settingsXaml, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("class=\"browser-window reveal theme-classic\" id=\"browser-mockup\"", webHtml, StringComparison.Ordinal);
        Assert.Contains("data-theme=\"classic\"", webHtml, StringComparison.Ordinal);
        Assert.Contains("data-theme=\"dark\"", webHtml, StringComparison.Ordinal);
        Assert.Contains("data-theme=\"light\"", webHtml, StringComparison.Ordinal);
        Assert.Contains("v2.6.0", webHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("neutralizado en 0.01s", webHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("streaming-hd.example", webHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("streaming-hd.example", webJs, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ChunkedReadStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _maxChunk;
        private int _pos;

        public ChunkedReadStream(byte[] data, int maxChunk)
        {
            _data = data;
            _maxChunk = maxChunk;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= _data.Length) return 0;
            int n = Math.Min(Math.Min(count, _maxChunk), _data.Length - _pos);
            Array.Copy(_data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override System.Threading.Tasks.ValueTask<int> ReadAsync(Memory<byte> buffer, System.Threading.CancellationToken cancellationToken = default)
        {
            if (_pos >= _data.Length) return new ValueTask<int>(0);
            int n = Math.Min(Math.Min(buffer.Length, _maxChunk), _data.Length - _pos);
            _data.AsSpan(_pos, n).CopyTo(buffer.Span);
            _pos += n;
            return new ValueTask<int>(n);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void CriticalFix2_WebMessageOriginChecksAndNativeShortcuts_AreEnforced()
    {
        Uri tabUri = new("https://app.example.com/login");
        Assert.True(LexBrowser.MainWindow.IsSameTopDocumentOrigin("https://app.example.com/oauth", tabUri));
        Assert.True(LexBrowser.MainWindow.IsSecureOriginForPasswordMessage("https://app.example.com/oauth", tabUri));
        Assert.True(LexBrowser.MainWindow.IsSecureOriginForPasswordMessage("http://localhost:3000/callback", new Uri("http://localhost:3000/login")));

        Assert.False(LexBrowser.MainWindow.IsSameTopDocumentOrigin("https://evil.com/frame", tabUri));
        Assert.False(LexBrowser.MainWindow.IsSameTopDocumentOrigin("https://app.example.com:8443/login", tabUri));
        Assert.False(LexBrowser.MainWindow.IsSecureOriginForPasswordMessage("http://bank.example.com/login", new Uri("http://bank.example.com/login")));

        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string tabsCs = File.ReadAllText(Path.Combine(repoRoot, "LexBrowser", "MainWindow.Tabs.cs"));
        string mediaJs = MediaRuntime.InjectedScript;
        string oauthCs = File.ReadAllText(Path.Combine(repoRoot, "LexBrowser", "UI", "Views", "OAuthPopupWindow.xaml.cs"));

        Assert.DoesNotContain("type: 'shortcut_new_tab'", mediaJs, StringComparison.Ordinal);
        Assert.DoesNotContain("type: 'shortcut_close_tab'", mediaJs, StringComparison.Ordinal);
        Assert.DoesNotContain("type: 'shortcut_reopen_tab'", mediaJs, StringComparison.Ordinal);
        Assert.DoesNotContain("type: 'open_history'", mediaJs, StringComparison.Ordinal);

        Assert.DoesNotContain("type == \"shortcut_new_tab\"", tabsCs, StringComparison.Ordinal);
        Assert.DoesNotContain("type == \"shortcut_close_tab\"", tabsCs, StringComparison.Ordinal);
        Assert.DoesNotContain("type == \"shortcut_reopen_tab\"", tabsCs, StringComparison.Ordinal);
        Assert.DoesNotContain("type == \"open_history\"", tabsCs, StringComparison.Ordinal);
        Assert.Contains("AcceleratorKeyPressed", tabsCs, StringComparison.Ordinal);

        Assert.DoesNotContain("autofill_credentials", oauthCs, StringComparison.Ordinal);
        Assert.Contains("IsSecureOriginForPasswordMessage", oauthCs, StringComparison.Ordinal);
    }

    [Fact]
    public void CriticalFix3_SessionNotClearedWhenClosedWhileLocked()
    {
        // Cerrar en pantalla de PIN / antes de restaurar sesión no debe borrar session_tabs.json
        Assert.False(LexBrowser.MainWindow.ShouldPersistSessionOnClose(
            isGuestSession: false,
            isLastOwnerWindow: true,
            restorePreviousSession: true,
            hasInitializedSession: false,
            isLocked: true));
        Assert.False(LexBrowser.MainWindow.ShouldPersistSessionOnClose(
            isGuestSession: false,
            isLastOwnerWindow: true,
            restorePreviousSession: true,
            hasInitializedSession: false,
            isLocked: false));
        Assert.False(LexBrowser.MainWindow.ShouldPersistSessionOnClose(
            isGuestSession: false,
            isLastOwnerWindow: true,
            restorePreviousSession: true,
            hasInitializedSession: true,
            isLocked: true));
        Assert.False(LexBrowser.MainWindow.ShouldPersistSessionOnClose(
            isGuestSession: true,
            isLastOwnerWindow: true,
            restorePreviousSession: true,
            hasInitializedSession: true,
            isLocked: false));
        Assert.True(LexBrowser.MainWindow.ShouldPersistSessionOnClose(
            isGuestSession: false,
            isLastOwnerWindow: true,
            restorePreviousSession: true,
            hasInitializedSession: true,
            isLocked: false));
    }

    [Fact]
    public async System.Threading.Tasks.Task CriticalFix4_UpdateManagerVerifiesSha256AndUsesLocalAppDataFolder()
    {
        string sampleHex = "160b4171a6e085d370a4d50cf48e1a224b8f0ab33b1af966e670e07e13df5797";
        Assert.Equal(sampleHex, UpdateManager.ParseAssetSha256Digest($"sha256:{sampleHex.ToUpperInvariant()}"));
        Assert.Null(UpdateManager.ParseAssetSha256Digest("sha256:short"));
        Assert.Null(UpdateManager.ParseAssetSha256Digest(null));

        Assert.True(UpdateManager.IsTrustedGitHubReleaseDownloadUrl("https://github.com/JuniorJhair/Lex-Junior/releases/download/v2.6.0/LexJuniorSetup.exe"));
        Assert.False(UpdateManager.IsTrustedGitHubReleaseDownloadUrl("http://github.com/JuniorJhair/Lex-Junior/releases/download/v2.6.0/LexJuniorSetup.exe"));
        Assert.False(UpdateManager.IsTrustedGitHubReleaseDownloadUrl("https://github.com/OtherUser/Lex-Junior/releases/download/v2.6.0/LexJuniorSetup.exe"));

        Assert.StartsWith(InstallerService.UserDataDir, UpdateManager.GetUpdatesDirectory(), StringComparison.OrdinalIgnoreCase);

        string tempFile = Path.Combine(Path.GetTempPath(), $"lex_upd_verify_{Guid.NewGuid():N}.bin");
        try
        {
            byte[] payload = System.Text.Encoding.UTF8.GetBytes("LexJuniorUpdaterPayload-v2.6.0");
            await File.WriteAllBytesAsync(tempFile, payload);
            string exactSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant();

            Assert.True(await UpdateManager.VerifyDownloadedInstallerAsync(tempFile, $"sha256:{exactSha}", payload.Length));
            Assert.False(await UpdateManager.VerifyDownloadedInstallerAsync(tempFile, null, payload.Length));
            Assert.False(await UpdateManager.VerifyDownloadedInstallerAsync(tempFile, $"sha256:{new string('0', 64)}", payload.Length));
            Assert.False(await UpdateManager.VerifyDownloadedInstallerAsync(tempFile, $"sha256:{exactSha}", payload.Length + 1));
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task CriticalFix5_SingleInstanceIpc_UsesCurrentUserOnlyAndReadsChunkedLongUrls()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string ipcCs = File.ReadAllText(Path.Combine(repoRoot, "LexBrowser", "Core", "SingleInstanceIpc.cs"));
        Assert.Contains("PipeOptions.CurrentUserOnly", ipcCs, StringComparison.Ordinal);

        string longUrl = "https://example.com/path?" + new string('a', 2400);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(longUrl);
        using var chunked = new ChunkedReadStream(bytes, maxChunk: 300);

        string decoded = await SingleInstanceIpc.ReadBoundedPayloadAsync(chunked, maxBytes: 16384);
        Assert.Equal(longUrl, decoded);

        using var cappedStream = new ChunkedReadStream(bytes, maxChunk: 200);
        string capped = await SingleInstanceIpc.ReadBoundedPayloadAsync(cappedStream, maxBytes: 512);
        Assert.Equal(512, capped.Length);
    }

    [Fact]
    public void CriticalFix6_StartupDoesNotCallRefreshWindowsIconCacheOnUiThread()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string appCs = File.ReadAllText(Path.Combine(repoRoot, "LexBrowser", "App.xaml.cs"));
        int onStartupIdx = appCs.IndexOf("OnStartup(", StringComparison.Ordinal);
        int bgTaskIdx = appCs.IndexOf("Task.Run(", onStartupIdx, StringComparison.Ordinal);
        string uiStartupSlice = appCs.Substring(onStartupIdx, bgTaskIdx - onStartupIdx);

        Assert.DoesNotContain("RefreshWindowsIconCache", uiStartupSlice, StringComparison.Ordinal);
        Assert.Contains("RefreshIconCacheOnceIfVersionChanged", appCs, StringComparison.Ordinal);
    }
}
