using System;
using System.Collections.Generic;
using LexBrowser.Core;
using Xunit;

namespace LexBrowser.Tests;

public sealed class TabSuspensionPolicyTests
{
    private static readonly DateTime BaseDeactivatedUtc = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PastIdleNowUtc = BaseDeactivatedUtc.AddMinutes(20);

    private static TabSuspensionCandidate CreateEligibleBase() => new(
        IsActive: false,
        IsPinned: false,
        IsNewTab: false,
        IsLazyLoaded: false,
        IsLoading: false,
        IsDisposed: false,
        IsPlayingAudio: false,
        HasActiveMedia: false,
        IsPipSource: false,
        IsFloatingTarget: false,
        IsSnifferActive: false,
        IsFullScreen: false,
        HasNotificationPermission: false,
        HasCaptureDevicePermission: false,
        AlreadySuspended: false,
        LastDeactivatedUtc: BaseDeactivatedUtc,
        NextAttemptUtc: DateTime.MinValue);

    public static IEnumerable<object[]> ExclusionCases()
    {
        yield return new object[] { "IdleMinutesZero", CreateEligibleBase(), 0, false, PastIdleNowUtc };
        yield return new object[] { "IdleMinutesNegative", CreateEligibleBase(), -5, false, PastIdleNowUtc };
        yield return new object[] { "IsActive", CreateEligibleBase() with { IsActive = true }, 15, false, PastIdleNowUtc };
        yield return new object[] { "AlreadySuspended", CreateEligibleBase() with { AlreadySuspended = true }, 15, false, PastIdleNowUtc };
        yield return new object[] { "IsNewTab", CreateEligibleBase() with { IsNewTab = true }, 15, false, PastIdleNowUtc };
        yield return new object[] { "IsLazyLoaded", CreateEligibleBase() with { IsLazyLoaded = true }, 15, false, PastIdleNowUtc };
        yield return new object[] { "IsLoading", CreateEligibleBase() with { IsLoading = true }, 15, false, PastIdleNowUtc };
        yield return new object[] { "IsDisposed", CreateEligibleBase() with { IsDisposed = true }, 15, false, PastIdleNowUtc };
        yield return new object[] { "IsPlayingAudio", CreateEligibleBase() with { IsPlayingAudio = true }, 15, false, PastIdleNowUtc };
        yield return new object[] { "HasActiveMedia", CreateEligibleBase() with { HasActiveMedia = true }, 15, false, PastIdleNowUtc };
        yield return new object[] { "IsPipSource", CreateEligibleBase() with { IsPipSource = true }, 15, false, PastIdleNowUtc };
        yield return new object[] { "IsFloatingTarget", CreateEligibleBase() with { IsFloatingTarget = true }, 15, false, PastIdleNowUtc };
        yield return new object[] { "IsSnifferActive", CreateEligibleBase() with { IsSnifferActive = true }, 15, false, PastIdleNowUtc };
        yield return new object[] { "IsFullScreen", CreateEligibleBase() with { IsFullScreen = true }, 15, false, PastIdleNowUtc };
        yield return new object[] { "PinnedWithoutAllow", CreateEligibleBase() with { IsPinned = true }, 15, false, PastIdleNowUtc };
        yield return new object[] { "HasNotificationPermission", CreateEligibleBase() with { HasNotificationPermission = true }, 15, false, PastIdleNowUtc };
        yield return new object[] { "HasCaptureDevicePermission", CreateEligibleBase() with { HasCaptureDevicePermission = true }, 15, false, PastIdleNowUtc };
        yield return new object[] { "LastDeactivatedMinValue", CreateEligibleBase() with { LastDeactivatedUtc = DateTime.MinValue }, 15, false, PastIdleNowUtc };
    }

    [Theory]
    [MemberData(nameof(ExclusionCases))]
    public void CanSuspend_And_NextEligibleUtc_ExcludeEachProtectedRule(
        string reason,
        TabSuspensionCandidate candidate,
        int idleMinutes,
        bool suspendPinned,
        DateTime nowUtc)
    {
        Assert.False(TabSuspensionPolicy.CanSuspend(in candidate, idleMinutes, suspendPinned, nowUtc), reason);
        Assert.Null(TabSuspensionPolicy.NextEligibleUtc(in candidate, idleMinutes, suspendPinned, nowUtc));
    }

    [Fact]
    public void CanSuspend_ReturnsFalse_BeforeIdleOrBackoff_AndReturnsExactNextEligibleUtc()
    {
        var candidate = CreateEligibleBase();
        var beforeIdle = BaseDeactivatedUtc.AddMinutes(10);

        Assert.False(TabSuspensionPolicy.CanSuspend(in candidate, 15, suspendPinned: false, beforeIdle));
        Assert.Equal(BaseDeactivatedUtc.AddMinutes(15),
            TabSuspensionPolicy.NextEligibleUtc(in candidate, 15, suspendPinned: false, beforeIdle));

        var backoff = candidate with { NextAttemptUtc = BaseDeactivatedUtc.AddMinutes(22) };
        Assert.False(TabSuspensionPolicy.CanSuspend(in backoff, 15, suspendPinned: false, PastIdleNowUtc));
        Assert.Equal(BaseDeactivatedUtc.AddMinutes(22),
            TabSuspensionPolicy.NextEligibleUtc(in backoff, 15, suspendPinned: false, PastIdleNowUtc));
    }

    [Fact]
    public void CanSuspend_ReturnsTrue_AtExactDeadline_AndWhenPinnedAllowed()
    {
        var candidate = CreateEligibleBase();
        var exact = BaseDeactivatedUtc.AddMinutes(15);

        Assert.True(TabSuspensionPolicy.CanSuspend(in candidate, 15, suspendPinned: false, exact));
        Assert.Equal(exact, TabSuspensionPolicy.NextEligibleUtc(in candidate, 15, suspendPinned: false, exact));

        var pinned = candidate with { IsPinned = true };
        Assert.True(TabSuspensionPolicy.CanSuspend(in pinned, 15, suspendPinned: true, exact));
        Assert.Equal(exact, TabSuspensionPolicy.NextEligibleUtc(in pinned, 15, suspendPinned: true, exact));
    }

    [Fact]
    public void NextEligibleUtc_ClampsNearDateTimeMaxValueWithoutOverflow()
    {
        var candidate = CreateEligibleBase() with { LastDeactivatedUtc = DateTime.MaxValue.AddMinutes(-2) };
        var result = TabSuspensionPolicy.NextEligibleUtc(in candidate, 15, suspendPinned: false, DateTime.UtcNow);
        Assert.Equal(DateTime.MaxValue, result);
    }
}

