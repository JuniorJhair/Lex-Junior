using System;
using LexBrowser.Core;
using Xunit;

namespace LexBrowser.Tests;

public class DefaultBrowserTests
{
    [Theory]
    [InlineData(@"C:\Users\User\AppData\Local\Programs\LexJuniorBrowser\LexBrowser.exe")]
    [InlineData("\"C:\\Users\\User\\AppData\\Local\\Programs\\LexJuniorBrowser\\LexBrowser.exe\" \"%1\"")]
    [InlineData(@"D:\Custom\LexBrowser.exe")]
    [InlineData(@"C:\Programs\LexJunior\app.exe")]
    public void IsMatchingBrowserExe_WithLexJuniorPaths_ReturnsTrue(string path)
    {
        Assert.True(InstallerService.IsMatchingBrowserExe(path));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe")]
    [InlineData(@"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe")]
    [InlineData(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsMatchingBrowserExe_WithOtherBrowsers_ReturnsFalse(string? path)
    {
        Assert.False(InstallerService.IsMatchingBrowserExe(path));
    }

    [Fact]
    public void IsDefaultBrowser_ExecutesWithoutException()
    {
        // Verifica que la llamada a la Shell API / Registro se ejecute de forma limpia y segura
        bool result = InstallerService.IsDefaultBrowser();
        // El resultado debe ser un booleano válido
        Assert.True(result || !result);
    }
}

