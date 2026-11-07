using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LexBrowser.Core;
using LexBrowser.Core.AdBlock;
using LexBrowser.Core.Engine;
using Xunit;

namespace LexBrowser.Tests;

[Collection("SettingsCollection")]
public class AuditRegressionTests
{
    private static byte[] CreateValidPngBytes()
    {
        var pixels = new byte[4 * 4 * 4];
        var bmp = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, pixels, 16);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task FaviconService_IncognitoMode_DoesNotCacheDomainInMemory()
    {
        FaviconService.ClearCache();
        string incognitoUrl = "https://private-incognito-test.example.org/secret";
        using var stream = new MemoryStream(CreateValidPngBytes());

        var icon = await FaviconService.ResolveFaviconAsync(
            incognitoUrl,
            webViewStream: stream,
            faviconUri: null,
            isIncognito: true);

        Assert.NotNull(icon);
        Assert.Null(FaviconService.GetCachedFavicon("private-incognito-test.example.org"));
    }

    [Fact]
    public async Task FaviconService_NormalMode_CachesDomainInMemory()
    {
        FaviconService.ClearCache();
        string normalUrl = "https://normal-session-test.example.org/page";
        using var stream = new MemoryStream(CreateValidPngBytes());

        var icon = await FaviconService.ResolveFaviconAsync(
            normalUrl,
            webViewStream: stream,
            faviconUri: null,
            isIncognito: false);

        Assert.NotNull(icon);
        Assert.NotNull(FaviconService.GetCachedFavicon("normal-session-test.example.org"));
        FaviconService.ClearCache();
    }

    [Fact]
    public void NewTabPage_RespectsSelectedDefaultSearchEngine()
    {
        string originalEngine = SettingsManager.Current.DefaultSearchEngine;
        try
        {
            SettingsManager.Current.DefaultSearchEngine = "DuckDuckGo";
            string htmlDdg = NewTabPage.GetHtml(incognito: false);
            Assert.Contains("Buscar en DuckDuckGo", htmlDdg);
            Assert.Contains("https://duckduckgo.com/?q=__LEX_QUERY__", htmlDdg);

            SettingsManager.Current.DefaultSearchEngine = "Brave";
            string htmlBrave = NewTabPage.GetHtml(incognito: true);
            Assert.Contains("Buscar en Brave Search", htmlBrave);
            Assert.Contains("https://search.brave.com/search?q=__LEX_QUERY__", htmlBrave);
        }
        finally
        {
            SettingsManager.Current.DefaultSearchEngine = originalEngine;
        }
    }

    [Theory]
    [InlineData("DuckDuckGo", "hola mundo", "https://duckduckgo.com/?q=hola%20mundo")]
    [InlineData("Brave", "c# wpf", "https://search.brave.com/search?q=c%23%20wpf")]
    [InlineData("Bing", "test", "https://www.bing.com/search?q=test")]
    [InlineData("Google", "lex", "https://www.google.com/search?q=lex")]
    public void SettingsManager_GetSearchUrl_FormatsCorrectly(string engine, string query, string expected)
    {
        string originalEngine = SettingsManager.Current.DefaultSearchEngine;
        try
        {
            SettingsManager.Current.DefaultSearchEngine = engine;
            string actual = SettingsManager.GetSearchUrl(query);
            Assert.Equal(expected, actual);
        }
        finally
        {
            SettingsManager.Current.DefaultSearchEngine = originalEngine;
        }
    }

    [Fact]
    public void YouTubeOptimizer_ContainsPolymerBackdropAndPipHandlers()
    {
        string script = YouTubeOptimizer.InjectedScript;
        Assert.Contains("closePolymerDialog", script);
        Assert.Contains("tp-yt-iron-overlay-backdrop", script);
        Assert.Contains("pointer-events: none !important", script);
        Assert.Contains("pip_leaving_now", script);
        Assert.Contains("pip_left", script);
    }

    [Fact]
    public void YouTubeOptimizer_ForwardsTabShortcuts_AndRemovesVideoOnlyMode()
    {
        string script = YouTubeOptimizer.InjectedScript;
        Assert.Contains("shortcut_new_tab", script);
        Assert.Contains("shortcut_reopen_tab", script);
        Assert.Contains("shortcut_close_tab", script);
        Assert.DoesNotContain("set_video_only_mode", script);
        Assert.DoesNotContain("lex-video-only-style", script);
    }

    [Fact]
    public void YouTubeOptimizer_InjectedScript_HasValidJavaScriptSyntax()
    {
        string tempJs = Path.Combine(Path.GetTempPath(), $"lex_yt_syntax_{Guid.NewGuid():N}.js");
        try
        {
            File.WriteAllText(tempJs, YouTubeOptimizer.InjectedScript);
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "node",
                Arguments = $"--check \"{tempJs}\"",
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            Assert.NotNull(proc);
            string stderr = proc!.StandardError.ReadToEnd();
            proc.WaitForExit(5000);
            Assert.True(proc.ExitCode == 0, $"JavaScript SyntaxError in YouTubeOptimizer.InjectedScript:\n{stderr}");
        }
        finally
        {
            if (File.Exists(tempJs)) File.Delete(tempJs);
        }
    }

    [Fact]
    public void NewTabPage_RendersOfficialBrandSvgIcons_WithoutOldEmojis()
    {
        string html = NewTabPage.GetHtml(incognito: false);
        Assert.DoesNotContain(">▶️</div>", html);
        Assert.DoesNotContain(">🌐</div>", html);
        Assert.DoesNotContain(">📧</div>", html);
        Assert.DoesNotContain(">📅</div>", html);
        Assert.DoesNotContain(">💾</div>", html);
        Assert.DoesNotContain(">🎮</div>", html);
        Assert.Contains("#FF0000", html); // YouTube SVG
        Assert.Contains("#9146FF", html); // Twitch SVG
    }

    [Fact]
    public void YouTubeOptimizer_SupportsVolumeBoost300AndOnDemandMediaScan()
    {
        string script = YouTubeOptimizer.InjectedScript;
        Assert.Contains("set_volume_boost", script);
        Assert.Contains("createDynamicsCompressor", script);
        Assert.Contains("scan_page_media", script);
        Assert.Contains("media_detected_batch", script);
    }

    [Theory]
    [InlineData("v1.0.0", 1, 0, 0)]
    [InlineData("1.2.5", 1, 2, 5)]
    [InlineData("V2.1.0-beta", 2, 1, 0)]
    public void UpdateManager_ParseVersionTag_ParsesReleaseTagsCorrectly(string tag, int major, int minor, int build)
    {
        var parsed = UpdateManager.ParseVersionTag(tag);
        Assert.NotNull(parsed);
        Assert.Equal(new Version(major, minor, build), parsed);
    }

    [Fact]
    public void InstallerService_RecognizesCommandLineModes()
    {
        Assert.True(InstallerService.ShouldRunAsInstaller(new[] { "--install" }));
        Assert.True(InstallerService.ShouldRunAsUninstaller(new[] { "--uninstall" }));
        Assert.True(InstallerService.ShouldRunAsSilentUpdate(new[] { "--silent-update" }));
        Assert.False(InstallerService.ShouldRunAsUninstaller(Array.Empty<string>()));
    }

    [Fact]
    public void WebView2_GetController_ReflectsExpectedMembers()
    {
        var type = typeof(Microsoft.Web.WebView2.Wpf.WebView2);
        var baseField = type.GetField("m_webview2Base", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(baseField);
        var controllerProp = baseField.FieldType.GetProperty("CoreWebView2Controller", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(controllerProp);
        Assert.Equal(typeof(Microsoft.Web.WebView2.Core.CoreWebView2Controller), controllerProp.PropertyType);
    }
}

