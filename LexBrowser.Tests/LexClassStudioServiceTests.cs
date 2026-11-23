using System;
using System.IO;
using System.IO.Compression;
using LexBrowser.Core;
using Xunit;

namespace LexBrowser.Tests;

[Collection("SettingsCollection")]
public class LexClassStudioServiceTests
{
    private static string FindRepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "LexBrowser")) &&
                Directory.Exists(Path.Combine(dir, "LexBrowser.Tests")))
            {
                return dir;
            }
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }
        return AppContext.BaseDirectory;
    }

    [Theory]
    [InlineData("lex://class", true)]
    [InlineData("lex:class", true)]
    [InlineData("https://class.lex.local/index.html", true)]
    [InlineData("https://class.lex.local/assets/app.js", true)]
    [InlineData("https://tv.lex.local/index.html", false)]
    [InlineData("https://example.com", false)]
    [InlineData("", false)]
    public void IsClassUrl_ValidatesExpectedSchemesAndHosts(string rawUrl, bool expected)
    {
        Assert.Equal(expected, LexClassStudioService.IsClassUrl(rawUrl));
    }

    [Theory]
    [InlineData("", true, "index.html")]
    [InlineData("/", true, "index.html")]
    [InlineData("/assets/index.js?v=1", true, "assets/index.js")]
    [InlineData("/lex-universo/manifest.json#hash", true, "lex-universo/manifest.json")]
    [InlineData("../../Windows/System32/cmd.exe", false, "")]
    [InlineData("/assets/../secret.txt", false, "")]
    [InlineData("C:\\secret.txt", false, "")]
    public void TryResolveSafeRelativeAssetPath_BlocksTraversalAndNormalizesValidPaths(string rawPath, bool expectedOk, string expectedRel)
    {
        bool ok = LexClassStudioService.TryResolveSafeRelativeAssetPath(rawPath, out string resolved);
        Assert.Equal(expectedOk, ok);
        if (expectedOk)
        {
            Assert.Equal(expectedRel, resolved);
        }
    }

    [Fact]
    public void LoadStaticAsset_LoadsBuiltIndexHtmlAndAssets()
    {
        byte[]? htmlBytes = LexClassStudioService.LoadStaticAsset("index.html", out string resolved);
        Assert.NotNull(htmlBytes);
        Assert.Equal("index.html", resolved);
        string html = System.Text.Encoding.UTF8.GetString(htmlBytes!);
        Assert.Contains("Universo de Mundos", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HomePortalView", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Volver al portal", html, StringComparison.OrdinalIgnoreCase);

        byte[]? cssBytes = LexClassStudioService.LoadStaticAsset("assets/styles.css", out string cssResolved);
        Assert.NotNull(cssBytes);
        Assert.Equal("assets/styles.css", cssResolved);

        byte[]? appBytes = LexClassStudioService.LoadStaticAsset("assets/app.js", out string appResolved);
        Assert.NotNull(appBytes);
        Assert.Equal("assets/app.js", appResolved);

        // Garantizar que los recursos puros de Universo están incrustados dentro del binario para PublishSingleFile=true
        Assert.True(LexClassStudioService.HasEmbeddedAsset("index.html"));
        Assert.True(LexClassStudioService.HasEmbeddedAsset("assets/styles.css"));
        Assert.True(LexClassStudioService.HasEmbeddedAsset("assets/app.js"));
        Assert.False(LexClassStudioService.HasEmbeddedAsset("lex-universo/styles.css"));
    }

    [Theory]
    [InlineData("www.youtube.com", true)]
    [InlineData("youtube.com", true)]
    [InlineData("m.youtube.com", true)]
    [InlineData("music.youtube.com", true)]
    [InlineData("youtu.be", true)]
    [InlineData("youtube-nocookie.com", true)]
    [InlineData("evil-youtube.com", false)]
    [InlineData("google.com", false)]
    [InlineData(null, false)]
    public void YouTubeIsolation_RecognizesOfficialYouTubeDomainsOnly(string? host, bool expected)
    {
        Assert.Equal(expected, MainWindow.IsYouTubeHost(host));
    }

    [Fact]
    public void ParseManifestJson_AcceptsValidWorldsAndSkipsUnsafeIds()
    {
        const string json = """
            {
              "worlds": [
                {
                  "id": "matematicas",
                  "version": "2.8.0",
                  "status": "ready",
                  "downloadUrl": "https://github.com/JuniorJhair/Lex-Junior/releases/download/v2.8.0/matematicas.zip",
                  "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "sizeBytes": 12345,
                  "sizeLabel": "12 KB",
                  "note": "Nuevos ejercicios"
                },
                {
                  "id": "../evil",
                  "version": "9.9.9",
                  "status": "ready"
                }
              ]
            }
            """;

        var entries = LexClassStudioService.ParseManifestJson(json);
        Assert.Single(entries);
        Assert.Equal("matematicas", entries[0].Id);
        Assert.Equal("2.8.0", entries[0].Version);

        string injected = LexClassStudioService.BuildInjectStateScriptJson();
        Assert.Contains("\"worlds\"", injected, StringComparison.Ordinal);
        Assert.Contains("\"matematicas\"", injected, StringComparison.Ordinal);
        Assert.Contains("\"idiomas\"", injected, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractVerifiedZipSafely_ExtractsSafeZipAndRejectsZipSlip()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"lexclass_zip_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            string safeZip = Path.Combine(tempRoot, "safe.zip");
            using (var z = ZipFile.Open(safeZip, ZipArchiveMode.Create))
            {
                var entry = z.CreateEntry("assets/hello.txt");
                using var w = new StreamWriter(entry.Open());
                w.Write("hola-lex-class");
            }

            string outDir = Path.Combine(tempRoot, "out_safe");
            LexClassStudioService.ExtractVerifiedZipSafely(safeZip, outDir);
            Assert.True(File.Exists(Path.Combine(outDir, "assets", "hello.txt")));
            Assert.Equal("hola-lex-class", File.ReadAllText(Path.Combine(outDir, "assets", "hello.txt")));

            string evilZip = Path.Combine(tempRoot, "evil.zip");
            using (var z = ZipFile.Open(evilZip, ZipArchiveMode.Create))
            {
                var entry = z.CreateEntry("../../escape.txt");
                using var w = new StreamWriter(entry.Open());
                w.Write("owned");
            }

            string evilOutDir = Path.Combine(tempRoot, "out_evil");
            Assert.Throws<InvalidDataException>(() => LexClassStudioService.ExtractVerifiedZipSafely(evilZip, evilOutDir));
            Assert.False(File.Exists(Path.Combine(tempRoot, "escape.txt")));
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    [Theory]
    [InlineData(new[] { "--lexclass" }, true, false)]
    [InlineData(new[] { "/lexclass" }, true, false)]
    [InlineData(new[] { "--install-lexclass" }, false, true)]
    [InlineData(new[] { "/install-lexclass" }, false, true)]
    [InlineData(new[] { "--incognito" }, false, false)]
    public void InstallerService_ParsesLexClassCliArguments(string[] args, bool expectLauncher, bool expectInstaller)
    {
        Assert.Equal(expectLauncher, InstallerService.ShouldRunAsLexClassLauncher(args));
        Assert.Equal(expectInstaller, InstallerService.ShouldRunAsLexClassInstaller(args));
    }

    [Fact]
    public void UiAndPrivacyInvariants_MatchApprovedToolbarLabelsAndHistoryExclusion()
    {
        string root = FindRepoRoot();
        string mainXaml = File.ReadAllText(Path.Combine(root, "LexBrowser", "MainWindow.xaml"));
        string classWinCs = File.ReadAllText(Path.Combine(root, "LexBrowser", "UI", "Views", "LexClassWindow.xaml.cs"));
        string classWinXaml = File.ReadAllText(Path.Combine(root, "LexBrowser", "UI", "Views", "LexClassWindow.xaml"));
        string sessionCs = File.ReadAllText(Path.Combine(root, "LexBrowser", "Core", "SessionManager.cs"));
        string tabsCs = File.ReadAllText(Path.Combine(root, "LexBrowser", "MainWindow.Tabs.cs"));

        // Toolbar depurada y honesta (+Lex agrupa Lex Class, Lex Media y Lex Player APK)
        Assert.Contains("Text=\"Flotar web\"", mainXaml);
        Assert.Contains("Text=\"Media\"", mainXaml);
        Assert.Contains("Text=\"Telemetría?\"", mainXaml);
        Assert.Contains("x:Name=\"TxtLexHubBtnLabel\" Text=\"+Lex\"", mainXaml);
        Assert.Contains("Text=\"Lex Class\"", mainXaml);
        Assert.Contains("Text=\"Lex Media\"", mainXaml);
        Assert.Contains("Text=\"Lex Player APK\"", mainXaml);
        Assert.DoesNotContain("Text=\"PRONTO\"", mainXaml);

        // Ventana limpia de Lex Class: sin omnibox ni barra de direcciones, con pantalla completa y sin registro en historial/sesión
        string classWinNoComments = System.Text.RegularExpressions.Regex.Replace(
            classWinXaml, "<!--.*?-->", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.DoesNotContain("TxtUrl", classWinNoComments);
        Assert.DoesNotContain("Omnibox", classWinNoComments);
        Assert.DoesNotContain("<TextBox", classWinNoComments);
        Assert.Contains("BtnFullScreen", classWinNoComments);
        Assert.Contains("Key.F11", classWinCs);
        Assert.DoesNotContain("HistoryManager", classWinCs);
        Assert.DoesNotContain("SessionManager", classWinCs);

        Assert.Contains("LexClassStudioService.IsClassUrl", sessionCs);
        Assert.Contains("LexClassStudioService.IsClassUrl", tabsCs);
        Assert.EndsWith("#universo", LexClassStudioService.HomeUrl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VerticalOffset=\"4\" AllowsTransparency=\"True\" StaysOpen=\"False\"", mainXaml);
        Assert.Contains("HorizontalOffset=\"-96\" VerticalOffset=\"0\" AllowsTransparency=\"True\" StaysOpen=\"True\"", mainXaml);
        Assert.DoesNotContain("ToolTip=\"Maximizar / Diseños de ventana\"", mainXaml);

        // El diálogo de actualización de Lex Class usa modal tematizado propio con progreso y botón directo (sin MessageBox blanco)
        string updDialogXaml = File.ReadAllText(Path.Combine(root, "LexBrowser", "UI", "Views", "LexClassUpdateDialog.xaml"));
        string updDialogCs = File.ReadAllText(Path.Combine(root, "LexBrowser", "UI", "Views", "LexClassUpdateDialog.xaml.cs"));
        Assert.Contains("BtnPrimaryAction", updDialogXaml);
        Assert.Contains("BarProgress", updDialogXaml);
        Assert.Contains("LexClassUpdateDialog", classWinCs);
        Assert.DoesNotContain("MessageBox.Show", classWinCs);
        Assert.Contains("InstallOrUpdateWorldAsync", updDialogCs);
    }
}
