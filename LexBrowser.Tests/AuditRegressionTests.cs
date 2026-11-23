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

    [Fact]
    public void SnapLayouts_ComputeSnapZonePixels_CalculatesExactHalvesThirdsAndQuadrants()
    {
        // Monitor Full HD con barra de tareas abajo (rcWork = 0, 0, 1920, 1032)
        var leftHalf = LexBrowser.MainWindow.ComputeSnapZonePixels(0, 0, 1920, 1032, 0.0, 0.0, 0.5, 1.0, 640, 480);
        Assert.Equal((0, 0, 960, 1032), leftHalf);

        var rightTopQuarter = LexBrowser.MainWindow.ComputeSnapZonePixels(0, 0, 1920, 1032, 0.5, 0.0, 0.5, 0.5, 640, 480);
        Assert.Equal((960, 0, 960, 516), rightTopQuarter);

        var twoThirdsLeft = LexBrowser.MainWindow.ComputeSnapZonePixels(0, 0, 1920, 1032, 0.0, 0.0, 0.6667, 1.0, 640, 480);
        Assert.Equal(0, twoThirdsLeft.X);
        Assert.Equal(1280, twoThirdsLeft.Width);
        Assert.Equal(1032, twoThirdsLeft.Height);

        // Respeta MinWidth/MinHeight en monitores pequeños sin salirse del área de trabajo
        var clampedRight = LexBrowser.MainWindow.ComputeSnapZonePixels(100, 50, 800, 600, 0.6667, 0.0, 0.3333, 1.0, 500, 400);
        Assert.Equal(500, clampedRight.Width);
        Assert.True(clampedRight.X + clampedRight.Width <= 900);
    }

    [Fact]
    public void NewTabPage_IncludesMultiLinePasteNormalizationAndLiveSuggestions()
    {
        string html = NewTabPage.GetHtml(incognito: false, isGuest: false);
        Assert.Contains("normalizeBarText", html, StringComparison.Ordinal);
        Assert.Contains("addEventListener('paste'", html, StringComparison.Ordinal);
        Assert.Contains("newtab_suggest_req", html, StringComparison.Ordinal);
        Assert.Contains("__lexSetNewTabSuggestions", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserFixes_SourceInvariantsPresent()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string tabsCs = File.ReadAllText(Path.Combine(repoRoot, "LexBrowser", "MainWindow.Tabs.cs"));
        string xamlCs = File.ReadAllText(Path.Combine(repoRoot, "LexBrowser", "MainWindow.xaml.cs"));
        string securityCs = File.ReadAllText(Path.Combine(repoRoot, "LexBrowser", "MainWindow.Security.cs"));
        string downloadsXaml = File.ReadAllText(Path.Combine(repoRoot, "LexBrowser", "MainWindow.xaml"));
        string chromeCs = File.ReadAllText(Path.Combine(repoRoot, "LexBrowser", "MainWindow.WindowChrome.cs"));
        string fullscreenHelperCs = File.ReadAllText(Path.Combine(repoRoot, "LexBrowser", "Core", "Interop", "WindowFullscreenHelper.cs"));

        // F11 global fullscreen sin romper YouTube HTML5 fullscreen + botón flotante X arriba + Ctrl+Tab sin blanco ni choque PiP
        Assert.Contains("Key.F11", xamlCs, StringComparison.Ordinal);
        Assert.Contains("_isBrowserF11FullScreen", tabsCs, StringComparison.Ordinal);
        Assert.Contains("ShouldFullScreenChromeBeActive", tabsCs, StringComparison.Ordinal);
        Assert.Contains("RequestExitTabDomFullScreen", tabsCs, StringComparison.Ordinal);
        Assert.Contains("PopupFullScreenExit", downloadsXaml, StringComparison.Ordinal);
        Assert.Contains("BtnExitFullScreen_Click", downloadsXaml, StringComparison.Ordinal);
        Assert.Contains("_fullScreenCoordinator", tabsCs, StringComparison.Ordinal);
        Assert.Contains("SWP_FRAMECHANGED", fullscreenHelperCs, StringComparison.Ordinal);

        // Guardar como con Deferral + PopupDownloads con botón de descarga directa y sin marquee bloqueante
        Assert.Contains("e.GetDeferral()", tabsCs, StringComparison.Ordinal);
        Assert.Contains("SaveFileDialog", tabsCs, StringComparison.Ordinal);
        Assert.Contains("BtnToggleDirectDownload", downloadsXaml, StringComparison.Ordinal);

        // Selector multi-cuenta sin revelar todas por postMessage
        Assert.Contains("PopupAutofillPicker", securityCs, StringComparison.Ordinal);
        Assert.Contains("matches.Count == 1", securityCs, StringComparison.Ordinal);

        // Snap Layouts preserva bounds al hacer snap (solo restaura al desmaximizar manualmente)
        Assert.Contains("_pendingManualRestoreBounds", chromeCs, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortcutDeduplication_BlocksSameMillisecondDoubleF11AndCtrlTabEchoes()
    {
        var lastKey = System.Windows.Input.Key.None;
        var lastMods = System.Windows.Input.ModifierKeys.None;
        long lastTick = 0;
        long lastF11Tick = 0;

        // F11 físico: primer evento entra, eco inmediato (0-180ms) de otro pipeline se bloquea, nueva pulsación >=250ms entra
        Assert.True(MainWindow.ShouldAcceptShortcutInvocation(
            System.Windows.Input.Key.F11, System.Windows.Input.ModifierKeys.None, false,
            1000, ref lastKey, ref lastMods, ref lastTick, ref lastF11Tick));
        Assert.False(MainWindow.ShouldAcceptShortcutInvocation(
            System.Windows.Input.Key.F11, System.Windows.Input.ModifierKeys.None, false,
            1001, ref lastKey, ref lastMods, ref lastTick, ref lastF11Tick));
        Assert.False(MainWindow.ShouldAcceptShortcutInvocation(
            System.Windows.Input.Key.F11, System.Windows.Input.ModifierKeys.None, false,
            1240, ref lastKey, ref lastMods, ref lastTick, ref lastF11Tick));
        Assert.True(MainWindow.ShouldAcceptShortcutInvocation(
            System.Windows.Input.Key.F11, System.Windows.Input.ModifierKeys.None, false,
            1260, ref lastKey, ref lastMods, ref lastTick, ref lastF11Tick));

        // Ctrl+Tab: eco simultáneo en <40ms se descarta; nueva pulsación real (>=50ms) avanza pestaña
        Assert.True(MainWindow.ShouldAcceptShortcutInvocation(
            System.Windows.Input.Key.Tab, System.Windows.Input.ModifierKeys.Control, false,
            2000, ref lastKey, ref lastMods, ref lastTick, ref lastF11Tick));
        Assert.False(MainWindow.ShouldAcceptShortcutInvocation(
            System.Windows.Input.Key.Tab, System.Windows.Input.ModifierKeys.Control, false,
            2002, ref lastKey, ref lastMods, ref lastTick, ref lastF11Tick));
        Assert.True(MainWindow.ShouldAcceptShortcutInvocation(
            System.Windows.Input.Key.Tab, System.Windows.Input.ModifierKeys.Control, false,
            2060, ref lastKey, ref lastMods, ref lastTick, ref lastF11Tick));
    }

    [Theory]
    [InlineData(0, 0, 1536, 816)]  // 1080p @ 125% (barra abajo)
    [InlineData(0, 0, 1366, 720)]  // Laptop HD 1366x768 @ 100%
    [InlineData(0, 0, 1280, 672)]  // Laptop 1080p @ 150%
    [InlineData(0, 0, 1093, 574)]  // Laptop HD 1366x768 @ 125%
    [InlineData(0, 0, 853, 448)]   // Laptop 720p @ 150% (muy chica)
    [InlineData(1920, 0, 2560, 1392)] // Segundo monitor grande a la derecha
    [InlineData(64, 0, 1302, 768)] // Barra de tareas movida a la izquierda
    public void LexClassWindow_ComputeSafeCenteredBounds_NeverOverflowsWorkAreaOrHidesTopControls(
        double workLeft, double workTop, double workWidth, double workHeight)
    {
        var b = LexBrowser.UI.Views.LexClassWindow.ComputeSafeCenteredBounds(
            workLeft, workTop, workWidth, workHeight);

        Assert.True(b.Width > 0 && b.Width <= workWidth, $"Width {b.Width} fuera de WorkArea {workWidth}");
        Assert.True(b.Height > 0 && b.Height <= workHeight, $"Height {b.Height} fuera de WorkArea {workHeight}");
        Assert.True(b.Left >= workLeft, $"Left {b.Left} < workLeft {workLeft}");
        Assert.True(b.Top >= workTop, $"Top {b.Top} < workTop {workTop} (ocultaría ─ □ ✕)");
        Assert.True(b.Left + b.Width <= workLeft + workWidth + 0.01);
        Assert.True(b.Top + b.Height <= workTop + workHeight + 0.01);

        // Además debe estar centrado dentro del área útil cuando hay margen
        double expectedCenterLeft = workLeft + Math.Floor((workWidth - b.Width) * 0.5);
        double expectedCenterTop = workTop + Math.Floor((workHeight - b.Height) * 0.5);
        Assert.Equal(expectedCenterLeft, b.Left);
        Assert.Equal(expectedCenterTop, b.Top);
    }

    [Fact]
    public void LexClassWindow_UsesManualWorkAreaBoundsAndReputationSync()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string classXaml = File.ReadAllText(Path.Combine(repoRoot, "LexBrowser", "UI", "Views", "LexClassWindow.xaml"));
        string classCs = File.ReadAllText(Path.Combine(repoRoot, "LexBrowser", "UI", "Views", "LexClassWindow.xaml.cs"));
        string tabsCs = File.ReadAllText(Path.Combine(repoRoot, "LexBrowser", "MainWindow.Tabs.cs"));

        Assert.Contains("WindowStartupLocation=\"Manual\"", classXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Height=\"820\"", classXaml, StringComparison.Ordinal);
        Assert.Contains("ComputeSafeCenteredBounds", classCs, StringComparison.Ordinal);
        Assert.Contains("IsReputationCheckingRequired", tabsCs, StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentPipAndNewWindow_Stability_DisablesExperimentalDocPipAndGuardsCollisions()
    {
        string flagsSafe = LexBrowser.Core.Engine.BrowserEngine.BuildEngineFlags(new BrowserSettings { EnableSmartScreen = true });
        string flagsPrivacy = LexBrowser.Core.Engine.BrowserEngine.BuildEngineFlags(new BrowserSettings { EnableSmartScreen = false });
        Assert.Contains("DocumentPictureInPictureAPI", flagsSafe, StringComparison.Ordinal);
        Assert.Contains("DocumentPictureInPictureAPI", flagsPrivacy, StringComparison.Ordinal);
        Assert.Contains("HardwareMediaKeyHandling", flagsPrivacy, StringComparison.Ordinal);

        Assert.Contains("window.documentPictureInPicture && window.documentPictureInPicture.window", MediaRuntime.InjectedScript, StringComparison.Ordinal);

        Assert.True(LexBrowser.MainWindow.ShouldOpenNewWindowAsPopup(hasPopupSize: true, isOAuthUrl: false, url: "https://example.com", hasChromelessFeatures: false));
        Assert.True(LexBrowser.MainWindow.ShouldOpenNewWindowAsPopup(hasPopupSize: false, isOAuthUrl: true, url: "https://accounts.google.com", hasChromelessFeatures: false));
        Assert.True(LexBrowser.MainWindow.ShouldOpenNewWindowAsPopup(hasPopupSize: false, isOAuthUrl: false, url: "about:blank", hasChromelessFeatures: true));
        Assert.True(LexBrowser.MainWindow.ShouldOpenNewWindowAsPopup(hasPopupSize: false, isOAuthUrl: false, url: "", hasChromelessFeatures: true));
        Assert.False(LexBrowser.MainWindow.ShouldOpenNewWindowAsPopup(hasPopupSize: false, isOAuthUrl: false, url: "about:blank", hasChromelessFeatures: false));
        Assert.False(LexBrowser.MainWindow.ShouldOpenNewWindowAsPopup(hasPopupSize: false, isOAuthUrl: false, url: "https://www.tiktok.com/@user/video/1", hasChromelessFeatures: false));

        var tiktokTab = new LexBrowser.UI.Models.BrowserTab { Url = "https://www.tiktok.com", IsNewTab = false };
        var blankTransientTab = new LexBrowser.UI.Models.BrowserTab { Url = "about:blank", IsNewTab = false };
        var normalNewTab = new LexBrowser.UI.Models.BrowserTab { Url = "", IsNewTab = true };
        var normalPageTab = new LexBrowser.UI.Models.BrowserTab { Url = "https://example.com", IsNewTab = false };

        Assert.False(LexBrowser.MainWindow.ShouldTriggerAutoPiPOnTabSwitch(true, tiktokTab, blankTransientTab));
        Assert.False(LexBrowser.MainWindow.ShouldTriggerAutoPiPOnTabSwitch(false, tiktokTab, normalPageTab));
        Assert.False(LexBrowser.MainWindow.ShouldTriggerAutoPiPOnTabSwitch(true, tiktokTab, tiktokTab));
        Assert.True(LexBrowser.MainWindow.ShouldTriggerAutoPiPOnTabSwitch(true, tiktokTab, normalNewTab));
        Assert.True(LexBrowser.MainWindow.ShouldTriggerAutoPiPOnTabSwitch(true, tiktokTab, normalPageTab));
    }

    [Fact]
    public void FullScreen_F11AndEscape_KeepWindowChromeAttachedAndDeferComMutations()
    {
        Assert.True(LexBrowser.MainWindow.ShouldExitFullScreenOnEscape(fullScreenChromeActive: true, isTabInDomFullScreen: false));
        Assert.False(LexBrowser.MainWindow.ShouldExitFullScreenOnEscape(fullScreenChromeActive: true, isTabInDomFullScreen: true));
        Assert.False(LexBrowser.MainWindow.ShouldExitFullScreenOnEscape(fullScreenChromeActive: false, isTabInDomFullScreen: false));

        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string tabsCs = File.ReadAllText(Path.Combine(repoRoot, "LexBrowser", "MainWindow.Tabs.cs"));
        string xamlCs = File.ReadAllText(Path.Combine(repoRoot, "LexBrowser", "MainWindow.xaml.cs"));
        string chromeCs = File.ReadAllText(Path.Combine(repoRoot, "LexBrowser", "MainWindow.WindowChrome.cs"));
        string appCs = File.ReadAllText(Path.Combine(repoRoot, "LexBrowser", "App.xaml.cs"));
        string engineCs = File.ReadAllText(Path.Combine(repoRoot, "LexBrowser", "Core", "Engine", "BrowserEngine.cs"));

        Assert.DoesNotContain("WindowChrome.SetWindowChrome(this, null)", tabsCs, StringComparison.Ordinal);
        Assert.DoesNotContain("ResizeMode = ResizeMode.NoResize", tabsCs, StringComparison.Ordinal);
        Assert.Contains("deferWindowMutations: true", tabsCs, StringComparison.Ordinal);
        Assert.Contains("ShouldExitFullScreenOnEscape", xamlCs, StringComparison.Ordinal);
        Assert.Contains("AppWindowChrome.CaptionHeight = isFullscreen ? 0 : 42", chromeCs, StringComparison.Ordinal);
        Assert.Contains("AppDomain.CurrentDomain.UnhandledException", appCs, StringComparison.Ordinal);
        Assert.Contains("TaskScheduler.UnobservedTaskException", appCs, StringComparison.Ordinal);
        Assert.Contains("BrowserProcessExited", engineCs, StringComparison.Ordinal);
        Assert.Contains("BrowserProcessExited", tabsCs, StringComparison.Ordinal);
    }

    [Fact]
    public void FullScreen_TaskbarLayering_UsesITaskbarList2AndClearsNativeFrameWithoutDetachingChrome()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string nativeCs = File.ReadAllText(Path.Combine(repoRoot, "LexBrowser", "Core", "Interop", "NativeMethods.cs"));
        string helperCs = File.ReadAllText(Path.Combine(repoRoot, "LexBrowser", "Core", "Interop", "WindowFullscreenHelper.cs"));
        string tabsCs = File.ReadAllText(Path.Combine(repoRoot, "LexBrowser", "MainWindow.Tabs.cs"));
        string chromeCs = File.ReadAllText(Path.Combine(repoRoot, "LexBrowser", "MainWindow.WindowChrome.cs"));
        string classCs = File.ReadAllText(Path.Combine(repoRoot, "LexBrowser", "UI", "Views", "LexClassWindow.xaml.cs"));

        Assert.Contains("ITaskbarList2", nativeCs, StringComparison.Ordinal);
        Assert.Contains("MarkFullscreenWindow", nativeCs, StringComparison.Ordinal);
        Assert.Contains("SetTaskbarFullscreenState", nativeCs, StringComparison.Ordinal);
        Assert.Contains("ClearWindowRegionIfSet", nativeCs, StringComparison.Ordinal);
        Assert.Contains("SetTaskbarFullscreenState(hwnd, true)", helperCs, StringComparison.Ordinal);
        Assert.Contains("SetTaskbarFullscreenState(hwnd, false)", helperCs, StringComparison.Ordinal);
        Assert.Contains("ClearWindowRegionIfSet(hwnd", helperCs, StringComparison.Ordinal);
        Assert.Contains("WM_WINDOWPOSCHANGING", helperCs, StringComparison.Ordinal);
        Assert.Contains("WM_WINDOWPOSCHANGED", helperCs, StringComparison.Ordinal);
        Assert.Contains("_fullScreenCoordinator", tabsCs, StringComparison.Ordinal);
        Assert.Contains("ReassertFullScreenTaskbarLayering", tabsCs, StringComparison.Ordinal);
        Assert.Contains("ReassertFullScreenTaskbarLayering", chromeCs, StringComparison.Ordinal);
        Assert.Contains("_fullScreenCoordinator", classCs, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowChrome.SetWindowChrome(this, null)", classCs, StringComparison.Ordinal);

        // Unit tests para máscaras Win32 y guarda anti-parpadeo de pantalla completa
        int maximizedStyle = 0x16CF0000; // WS_VISIBLE|WS_CLIPSIBLINGS|WS_CLIPCHILDREN|WS_MAXIMIZE|WS_CAPTION|WS_THICKFRAME|WS_MIN/MAXBOX
        int captured = LexBrowser.Core.Interop.WindowFullscreenHelper.CaptureRestorableFrameStyleMask(maximizedStyle);
        int fsStyle = LexBrowser.Core.Interop.WindowFullscreenHelper.ComputeBorderlessFullScreenStyle(maximizedStyle);
        Assert.Equal(0, fsStyle & LexBrowser.Core.Interop.WindowFullscreenHelper.FullScreenStripStyleMask);
        int restoredStyle = LexBrowser.Core.Interop.WindowFullscreenHelper.RestoreFrameStyle(fsStyle, captured);
        Assert.Equal(captured, restoredStyle & captured);
        Assert.Equal(0, restoredStyle & LexBrowser.Core.Interop.NativeMethods.WS_MAXIMIZE);

        var monitorRect = new LexBrowser.Core.Interop.NativeMethods.RECT { left = 0, top = 0, right = 1920, bottom = 1080 };
        Assert.True(LexBrowser.Core.Interop.WindowFullscreenHelper.ShouldSnapWindowPosToFullScreen(
            100, 100, 1280, 800, 0, in monitorRect, isTransitioning: true, isMinimizedOrIconic: false));
        Assert.True(LexBrowser.Core.Interop.WindowFullscreenHelper.ShouldSnapWindowPosToFullScreen(
            0, 0, 1920, 1020, 0, in monitorRect, isTransitioning: false, isMinimizedOrIconic: false));
        Assert.False(LexBrowser.Core.Interop.WindowFullscreenHelper.ShouldSnapWindowPosToFullScreen(
            -32000, -32000, 160, 28, 0, in monitorRect, isTransitioning: false, isMinimizedOrIconic: true));
        Assert.False(LexBrowser.Core.Interop.WindowFullscreenHelper.ShouldSnapWindowPosToFullScreen(
            0, 0, 1920, 1080, 0, in monitorRect, isTransitioning: false, isMinimizedOrIconic: false));
    }

    [Fact]
    public void LexHub_PopupCards_DoNotInheritFixedHeightNavBtnStyle()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string mainXaml = File.ReadAllText(Path.Combine(repoRoot, "LexBrowser", "MainWindow.xaml"));

        Assert.Contains("x:Key=\"LexHubCardBtn\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BtnLexMenuClass\" Style=\"{StaticResource LexHubCardBtn}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BtnLexMenuMedia\" Style=\"{StaticResource LexHubCardBtn}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BtnLexMenuPlayer\" Style=\"{StaticResource LexHubCardBtn}\"", mainXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"BtnLexMenuClass\" Style=\"{StaticResource NavBtn}\"", mainXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"BtnLexMenuMedia\" Style=\"{StaticResource NavBtn}\"", mainXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"BtnLexMenuPlayer\" Style=\"{StaticResource NavBtn}\"", mainXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void LexHub_LexMediaAndPrereqInstaller_AreIntegratedCleanly()
    {
        Assert.Equal("Lex Media", LexBrowser.Core.Tv.LexTvLegalInfo.ApplicationName);
        Assert.True(LexBrowser.Core.Tv.TvStudioService.IsTvShortcut("lex://media"));
        Assert.True(LexBrowser.Core.Tv.TvStudioService.IsTvShortcut("lex://tv"));
        Assert.Equal(LexBrowser.Core.Tv.TvStudioService.HomeUrl, LexBrowser.Core.OmniboxInputResolver.ResolveNavigationTarget("lex://media"));

        var prereq = LexBrowser.Core.InstallerService.InspectBrowserPrerequisites();
        Assert.NotNull(prereq.SummaryLine);
        Assert.Contains("WebView2", prereq.SummaryLine, StringComparison.Ordinal);

        Assert.Contains("LexJuniorPlayerSetup.exe", LexBrowser.Core.LexPlayerLauncher.PlayerSetupDownloadUrl, StringComparison.Ordinal);

        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string mainXaml = File.ReadAllText(Path.Combine(repoRoot, "LexBrowser", "MainWindow.xaml"));
        string mainXamlCs = File.ReadAllText(Path.Combine(repoRoot, "LexBrowser", "MainWindow.xaml.cs"));
        string instCs = File.ReadAllText(Path.Combine(repoRoot, "LexBrowser", "Core", "InstallerService.cs"));

        Assert.Contains("PopupLexHub", mainXaml, StringComparison.Ordinal);
        Assert.Contains("yield return PopupLexHub;", mainXamlCs, StringComparison.Ordinal);
        Assert.Contains("EnsureWebView2RuntimeAsync", instCs, StringComparison.Ordinal);
        Assert.Contains("allowSkipSameBinary", instCs, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateManager_SelectsBrowserSetupAssetOverAlphabeticallyEarlierPlayerSetup()
    {
        const string githubAssetsJson = """
        [
          {
            "name": "LexJuniorPlayerSetup.exe",
            "browser_download_url": "https://github.com/JuniorJhair/Lex-Junior/releases/download/v2.8.2/LexJuniorPlayerSetup.exe",
            "digest": "sha256:dfe76b1b08babfcf1b17b37c5d6e6ffe7b4d4e361d4eeac309ef921f716ac5cd",
            "size": 72382409
          },
          {
            "name": "LexJuniorSetup.exe",
            "browser_download_url": "https://github.com/JuniorJhair/Lex-Junior/releases/download/v2.8.3/LexJuniorSetup.exe",
            "digest": "sha256:dfc10b737d3a8f6ddca34c2752bee979412a71b066e7b82202407aec63b64870",
            "size": 82034011
          }
        ]
        """;

        using var doc = System.Text.Json.JsonDocument.Parse(githubAssetsJson);
        var selected = UpdateManager.SelectBrowserInstallerAsset(doc.RootElement);
        Assert.True(selected.HasValue);
        Assert.EndsWith("/LexJuniorSetup.exe", selected.Value.DownloadUrl, StringComparison.Ordinal);
        Assert.Equal("dfc10b737d3a8f6ddca34c2752bee979412a71b066e7b82202407aec63b64870", selected.Value.Sha256Hex);
        Assert.Equal(82034011, selected.Value.SizeBytes);

        Assert.False(UpdateManager.IsBrowserInstallerAssetName("LexJuniorPlayerSetup.exe"));
        Assert.True(UpdateManager.IsBrowserInstallerAssetName("LexJuniorSetup.exe"));
    }
}
