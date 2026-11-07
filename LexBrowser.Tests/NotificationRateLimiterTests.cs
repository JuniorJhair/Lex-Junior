using System;
using LexBrowser.Core.Notifications;
using Xunit;

namespace LexBrowser.Tests;

public class NotificationRateLimiterTests
{
    public NotificationRateLimiterTests()
    {
        NotificationRateLimiter.ResetForTesting();
    }

    [Fact]
    public void EvaluateNotification_EnforcesSiteDailyLimit()
    {
        string origin = "https://news.example.com";
        int siteCustomLimit = 2;

        bool first = NotificationRateLimiter.EvaluateNotification(
            origin, DateTime.UtcNow, siteCustomLimit, defaultSiteDailyLimit: 0, globalDailyLimit: 0,
            discardOlderThan24Hours: true, out string r1);

        Assert.True(first);
        Assert.Equal("Aprobada", r1);

        // Simular paso de 3 segundos para no activar flood protection
        System.Threading.Thread.Sleep(2100);

        bool second = NotificationRateLimiter.EvaluateNotification(
            origin, DateTime.UtcNow, siteCustomLimit, defaultSiteDailyLimit: 0, globalDailyLimit: 0,
            discardOlderThan24Hours: true, out string r2);

        Assert.True(second);
        Assert.Equal("Aprobada", r2);

        System.Threading.Thread.Sleep(2100);

        bool third = NotificationRateLimiter.EvaluateNotification(
            origin, DateTime.UtcNow, siteCustomLimit, defaultSiteDailyLimit: 0, globalDailyLimit: 0,
            discardOlderThan24Hours: true, out string r3);

        Assert.False(third);
        Assert.Contains("Límite diario alcanzado", r3);
    }

    [Fact]
    public void EvaluateNotification_DiscardsNotificationsOlderThan24Hours()
    {
        string origin = "https://alerts.example.org";
        DateTime oldTimestamp = DateTime.UtcNow.AddHours(-26);

        bool allowed = NotificationRateLimiter.EvaluateNotification(
            origin, oldTimestamp, siteCustomDailyLimit: 5, defaultSiteDailyLimit: 0, globalDailyLimit: 0,
            discardOlderThan24Hours: true, out string reason);

        Assert.False(allowed);
        Assert.Contains("más de 24 horas", reason);
    }

    [Fact]
    public void EvaluateNotification_PreventsFloodSpamWithin2Seconds()
    {
        string origin = "https://chat.example.net";

        bool first = NotificationRateLimiter.EvaluateNotification(
            origin, DateTime.UtcNow, siteCustomDailyLimit: 10, defaultSiteDailyLimit: 0, globalDailyLimit: 0,
            discardOlderThan24Hours: true, out string r1);

        Assert.True(first);

        // Inmediatamente después (< 2s)
        bool spam = NotificationRateLimiter.EvaluateNotification(
            origin, DateTime.UtcNow, siteCustomDailyLimit: 10, defaultSiteDailyLimit: 0, globalDailyLimit: 0,
            discardOlderThan24Hours: true, out string r2);

        Assert.False(spam);
        Assert.Contains("saturación", r2);
    }

    [Fact]
    public void EvaluateNotification_EnforcesGlobalDailyQuotaAcrossMultipleSites()
    {
        int globalLimit = 2;

        bool s1 = NotificationRateLimiter.EvaluateNotification(
            "https://site-a.com", DateTime.UtcNow, siteCustomDailyLimit: 0, defaultSiteDailyLimit: 0,
            globalDailyLimit: globalLimit, discardOlderThan24Hours: true, out string r1);

        Assert.True(s1);

        System.Threading.Thread.Sleep(2100);

        bool s2 = NotificationRateLimiter.EvaluateNotification(
            "https://site-b.com", DateTime.UtcNow, siteCustomDailyLimit: 0, defaultSiteDailyLimit: 0,
            globalDailyLimit: globalLimit, discardOlderThan24Hours: true, out string r2);

        Assert.True(s2);

        System.Threading.Thread.Sleep(2100);

        bool s3 = NotificationRateLimiter.EvaluateNotification(
            "https://site-c.com", DateTime.UtcNow, siteCustomDailyLimit: 0, defaultSiteDailyLimit: 0,
            globalDailyLimit: globalLimit, discardOlderThan24Hours: true, out string r3);

        Assert.False(s3);
        Assert.Contains("Cuota diaria global", r3);
    }
}
