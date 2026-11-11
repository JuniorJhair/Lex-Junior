using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using LexBrowser.Core.AdBlock;
using Xunit;

namespace LexBrowser.Tests;

public class CookieBannerShieldTests
{
    [Fact]
    public void GetInjectedScript_substitutes_placeholders_and_site_policies()
    {
        var policies = new Dictionary<string, SiteShieldProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["example-off.com"] = new SiteShieldProfile { Mode = SitePolicy.ModeOff },
            ["example-compat.org"] = new SiteShieldProfile { Mode = SitePolicy.ModeCompat },
            ["example-mod.net"] = new SiteShieldProfile { CookieBanners = false }
        };

        string script = CookieBannerShield.GetInjectedScript(policies);

        Assert.DoesNotContain("const __LEX_SITE_POLICIES = {};", script);
        Assert.DoesNotContain("const __LEX_PAYMENT_AUTH_HOSTS = [];", script);

        Assert.Contains("example-off.com", script);
        Assert.Contains("example-compat.org", script);
        Assert.Contains("example-mod.net", script);
        Assert.Contains(SitePolicy.ModCookieBanners, script);
        Assert.Contains("paypal.com", script);
    }

    [Fact]
    public void Script_never_auto_clicks_or_writes_consent_cookies_and_has_required_exclusions()
    {
        string baseScript = CookieBannerShield.BaseScript;
        string injected = CookieBannerShield.GetInjectedScript(null);

        Assert.DoesNotContain(".click(", baseScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".click(", injected, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("document.cookie", baseScript, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("accounts.google.", injected);
        Assert.Contains("consent.google.", injected);
        Assert.Contains("consent.youtube.", injected);
        Assert.Contains("youtube.com", injected);
        Assert.Contains("#onetrust-consent-sdk", injected);
        Assert.Contains("#CybotCookiebotDialog", injected);
        Assert.Contains("#didomi-host", injected);
        Assert.Contains("cookie_banner_hidden", injected);
        Assert.Contains("modeLower === 'off' || modeLower === 'compat'", injected);
        Assert.Contains("siteDisabled.indexOf('cookiebanners') !== -1", injected);
    }

    [Fact]
    public void BaseScript_and_GetInjectedScript_have_valid_javascript_syntax()
    {
        var policies = new Dictionary<string, SiteShieldProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["sitetest.com"] = new SiteShieldProfile { Mode = SitePolicy.ModeOff, CookieBanners = false }
        };

        ValidateJsSyntax(CookieBannerShield.BaseScript, "base");
        ValidateJsSyntax(CookieBannerShield.GetInjectedScript(policies), "injected");
    }

    private static void ValidateJsSyntax(string script, string tag)
    {
        string tempJs = Path.Combine(Path.GetTempPath(), $"lex_cookie_shield_{tag}_{Guid.NewGuid():N}.js");
        try
        {
            File.WriteAllText(tempJs, script);
            var psi = new ProcessStartInfo
            {
                FileName = "node",
                Arguments = $"--check \"{tempJs}\"",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            Assert.NotNull(proc);
            string stderr = proc!.StandardError.ReadToEnd();
            proc.WaitForExit(5000);
            Assert.True(proc.ExitCode == 0, $"node --check failed for CookieBannerShield ({tag}): {stderr}");
        }
        finally
        {
            try { if (File.Exists(tempJs)) File.Delete(tempJs); } catch { }
        }
    }

    [Fact]
    public void SiteShieldProfile_CookieBanners_property_toggles_cleanly()
    {
        var profile = new SiteShieldProfile();
        Assert.True(profile.CookieBanners);

        profile.CookieBanners = false;
        Assert.False(profile.CookieBanners);
        Assert.Contains(SitePolicy.ModCookieBanners, profile.Disabled);

        profile.CookieBanners = true;
        Assert.True(profile.CookieBanners);
        Assert.DoesNotContain(SitePolicy.ModCookieBanners, profile.Disabled);
    }
}

