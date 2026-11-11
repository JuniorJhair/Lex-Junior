using System;

namespace LexBrowser.Core;

/// <summary>
/// Instantánea inmutable del estado de una pestaña para decidir si puede suspenderse.
/// </summary>
public readonly record struct TabSuspensionCandidate(
    bool IsActive,
    bool IsPinned,
    bool IsNewTab,
    bool IsLazyLoaded,
    bool IsLoading,
    bool IsDisposed,
    bool IsPlayingAudio,
    bool HasActiveMedia,
    bool IsPipSource,
    bool IsFloatingTarget,
    bool IsSnifferActive,
    bool IsFullScreen,
    bool HasNotificationPermission,
    bool HasCaptureDevicePermission,
    bool AlreadySuspended,
    DateTime LastDeactivatedUtc,
    DateTime NextAttemptUtc);

/// <summary>
/// Política pura y determinista de suspensión de pestañas inactivas en segundo plano.
/// </summary>
public static class TabSuspensionPolicy
{
    public static bool CanSuspend(in TabSuspensionCandidate c, int idleMinutes, bool suspendPinned, DateTime nowUtc)
    {
        var eligibleUtc = NextEligibleUtc(in c, idleMinutes, suspendPinned, nowUtc);
        return eligibleUtc.HasValue && nowUtc >= eligibleUtc.Value;
    }

    public static DateTime? NextEligibleUtc(in TabSuspensionCandidate c, int idleMinutes, bool suspendPinned, DateTime nowUtc)
    {
        _ = nowUtc;
        if (idleMinutes <= 0) return null;
        if (c.IsActive) return null;
        if (c.AlreadySuspended) return null;
        if (c.IsNewTab) return null;
        if (c.IsLazyLoaded) return null;
        if (c.IsLoading) return null;
        if (c.IsDisposed) return null;
        if (c.IsPlayingAudio) return null;
        if (c.HasActiveMedia) return null;
        if (c.IsPipSource) return null;
        if (c.IsFloatingTarget) return null;
        if (c.IsSnifferActive) return null;
        if (c.IsFullScreen) return null;
        if (c.IsPinned && !suspendPinned) return null;
        if (c.HasNotificationPermission) return null;
        if (c.HasCaptureDevicePermission) return null;
        if (c.LastDeactivatedUtc == DateTime.MinValue) return null;

        DateTime idleEligible = (DateTime.MaxValue - c.LastDeactivatedUtc).TotalMinutes <= idleMinutes
            ? DateTime.MaxValue
            : c.LastDeactivatedUtc.AddMinutes(idleMinutes);

        return c.NextAttemptUtc > idleEligible ? c.NextAttemptUtc : idleEligible;
    }
}

