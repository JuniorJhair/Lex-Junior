using System;
using System.IO;
using LexBrowser.Core.AdBlock;
using LexBrowser.Core.AdBlock.ShieldCore;
using Xunit;

namespace LexBrowser.Tests;

public class WebShieldCosmeticSelectorTests
{
    [Theory]
    [InlineData("download-box")]
    [InlineData("upload-zone")]
    [InlineData("read-more")]
    [InlineData("thread-list")]
    public void LexShieldCosmetic_DoesNotOverMatchBenignClassNames(string benignClass)
    {
        var data = LexShieldData.TryGetDefault();
        Assert.NotNull(data);
        string css = data!.GetCosmeticEngine().QueryGenericCss(
            "example.org",
            new[] { benignClass },
            Array.Empty<string>(),
            genericHide: false,
            SitePolicy.Resolve("example.org"));

        Assert.DoesNotContain(benignClass, css, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WebShield_EliminatesDuplicateGenericCssAndBlanketShims()
    {
        string script = WebShield.GetVideoDefuserScript(enableVideoDefuser: true);
        Assert.DoesNotContain("__lex_cosmetic_shield", script);
        Assert.DoesNotContain("popMagic", script);
        Assert.DoesNotContain("ZorDon", script);
        Assert.DoesNotContain("vastUrl", script);
    }

    [Theory]
    [InlineData("bootstrap")]
    [InlineData("defuser")]
    public void ShieldScripts_HaveValidJavaScriptSyntax(string target)
    {
        string js = target == "bootstrap"
            ? LexShieldScriptletEngine.BuildBootstrapScript("syntax-check")
            : WebShield.GetVideoDefuserScript(enableVideoDefuser: true);

        string tempJs = Path.Combine(Path.GetTempPath(), $"lex_{target}_syntax_{Guid.NewGuid():N}.js");
        try
        {
            File.WriteAllText(tempJs, js);
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
            Assert.True(proc.ExitCode == 0, $"JavaScript SyntaxError en {target}:\n{stderr}");
        }
        finally
        {
            if (File.Exists(tempJs)) File.Delete(tempJs);
        }
    }
}
