using System;
using System.IO;
using LexBrowser.Core.Notifications;
using Xunit;

namespace LexBrowser.Tests;

public class NotificationPermissionManagerTests
{
    [Fact]
    public void NormalizeOrigin_StandardHttps_ExtractsSchemeAndHostOnly()
    {
        string raw = "https://WWW.Example.COM/video/12345?ref=lex#heading";
        string normalized = NotificationPermissionManager.NormalizeOrigin(raw);
        Assert.Equal("https://www.example.com", normalized);
    }

    [Fact]
    public void NormalizeOrigin_CustomPort_PreservesPort()
    {
        string raw = "http://localhost:8080/api/notifications";
        string normalized = NotificationPermissionManager.NormalizeOrigin(raw);
        Assert.Equal("http://localhost:8080", normalized);
    }

    [Fact]
    public void NormalizeOrigin_DefaultHttpsPort443_OmitPort()
    {
        string raw = "https://example.org:443/home";
        string normalized = NotificationPermissionManager.NormalizeOrigin(raw);
        Assert.Equal("https://example.org", normalized);
    }

    [Fact]
    public void NormalMode_SetAndGetSiteMode_WorksCorrectly()
    {
        string origin = "https://test-normal-" + Guid.NewGuid().ToString("N") + ".org";
        try
        {
            Assert.Equal(SiteNotificationMode.Ask, NotificationPermissionManager.GetSiteMode(origin, isIncognito: false));

            NotificationPermissionManager.SetSiteMode(origin, SiteNotificationMode.Allow, isIncognito: false);
            Assert.Equal(SiteNotificationMode.Allow, NotificationPermissionManager.GetSiteMode(origin, isIncognito: false));

            NotificationPermissionManager.SetSiteMode(origin, SiteNotificationMode.Block, isIncognito: false);
            Assert.Equal(SiteNotificationMode.Block, NotificationPermissionManager.GetSiteMode(origin, isIncognito: false));
        }
        finally
        {
            NotificationPermissionManager.RemoveSite(origin);
        }
    }

    [Fact]
    public void IncognitoMode_IsIsolatedFromNormalSession()
    {
        string origin = "https://test-incognito-" + Guid.NewGuid().ToString("N") + ".org";
        try
        {
            NotificationPermissionManager.SetSiteMode(origin, SiteNotificationMode.Allow, isIncognito: true);

            // In incognito, it returns Allow
            Assert.Equal(SiteNotificationMode.Allow, NotificationPermissionManager.GetSiteMode(origin, isIncognito: true));

            // In normal session, it does NOT exist and returns Ask
            Assert.Equal(SiteNotificationMode.Ask, NotificationPermissionManager.GetSiteMode(origin, isIncognito: false));

            // Clearing incognito cache resets it
            NotificationPermissionManager.ClearIncognitoCache();
            Assert.Equal(SiteNotificationMode.Ask, NotificationPermissionManager.GetSiteMode(origin, isIncognito: true));
        }
        finally
        {
            NotificationPermissionManager.RemoveSite(origin);
            NotificationPermissionManager.ClearIncognitoCache();
        }
    }

    [Fact]
    public void RemoveSite_ResetsToAsk()
    {
        string origin = "https://test-remove-" + Guid.NewGuid().ToString("N") + ".org";
        NotificationPermissionManager.SetSiteMode(origin, SiteNotificationMode.Silent, isIncognito: false);
        Assert.Equal(SiteNotificationMode.Silent, NotificationPermissionManager.GetSiteMode(origin, isIncognito: false));

        bool removed = NotificationPermissionManager.RemoveSite(origin);
        Assert.True(removed);
        Assert.Equal(SiteNotificationMode.Ask, NotificationPermissionManager.GetSiteMode(origin, isIncognito: false));
    }
}
