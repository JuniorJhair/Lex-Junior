using System;
using System.IO;
using LexBrowser.Core.AdBlock;
using Xunit;

namespace LexBrowser.Tests;

public class WebShieldCosmeticSelectorTests
{
    // Selectores antiguos por subcadena: ocultaban "download-box", "upload-zone", "read-more", "thread-list", etc.
    [Theory]
    [InlineData("div[class*='ad-']")]
    [InlineData("div[id*='ad-']")]
    [InlineData("div[class*='ads-']")]
    [InlineData("div[id*='ads-']")]
    [InlineData("div[class*='adv-']")]
    [InlineData("div[id*='adv-']")]
    [InlineData("div[class*='ad-box']")]
    [InlineData("div[id*='ad-box']")]
    [InlineData("div[class*='ad_box']")]
    [InlineData("div[id*='ad_box']")]
    [InlineData("div[class*='ad-zone']")]
    [InlineData("div[id*='ad-zone']")]
    [InlineData("div[class*='ad-slot']")]
    [InlineData("div[id*='ad-slot']")]
    [InlineData("div[class*='ad-block']")]
    [InlineData("div[id*='ad-block']")]
    [InlineData("div[class*='ads-holder']")]
    [InlineData("div[class*='ad-placement']")]
    public void BaseScript_NoLongerUsesBroadSubstringAdSelectors(string oldSelector)
    {
        Assert.DoesNotContain(oldSelector, WebShield.BaseScript);
    }

    [Fact]
    public void BaseScript_BuildsWordBoundaryAdSelectors()
    {
        string script = WebShield.BaseScript;
        Assert.Contains("const adWord", script);
        Assert.Contains("generatedAdSelectors", script);
        // Separadores permitidos antes del token: inicio, espacio, guion y guion bajo
        Assert.Contains("[' ', '-', '_']", script);
    }

    [Fact]
    public void BaseScript_HasValidJavaScriptSyntax()
    {
        string tempJs = Path.Combine(Path.GetTempPath(), $"lex_webshield_syntax_{Guid.NewGuid():N}.js");
        try
        {
            File.WriteAllText(tempJs, WebShield.BaseScript);
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
            Assert.True(proc.ExitCode == 0, $"JavaScript SyntaxError en WebShield.BaseScript:\n{stderr}");
        }
        finally
        {
            if (File.Exists(tempJs)) File.Delete(tempJs);
        }
    }
}
