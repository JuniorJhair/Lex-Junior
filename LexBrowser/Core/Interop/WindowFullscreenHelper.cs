using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LexBrowser.Core.Interop;

/// <summary>
/// Helpers y coordinador compartido para pantalla completa real sin barra de tareas en Windows 10/11:
/// mantiene WindowChrome adjunto (seguro en F11/Esc rápidos), limpia WS_MAXIMIZE y HRGN de recorte,
/// posiciona la ventana sobre rcMonitor con HWND_TOP y notifica a Explorer vía ITaskbarList2.
/// </summary>
internal static class WindowFullscreenHelper
{
    public const int FullScreenStripStyleMask =
        NativeMethods.WS_MAXIMIZE |
        NativeMethods.WS_CAPTION |
        NativeMethods.WS_THICKFRAME |
        NativeMethods.WS_MINIMIZEBOX |
        NativeMethods.WS_MAXIMIZEBOX;

    public const int RestorableFrameStyleMask =
        NativeMethods.WS_CAPTION |
        NativeMethods.WS_THICKFRAME |
        NativeMethods.WS_MINIMIZEBOX |
        NativeMethods.WS_MAXIMIZEBOX;

    public const int DefaultBorderlessChromeStyleFallback =
        NativeMethods.WS_THICKFRAME |
        NativeMethods.WS_MINIMIZEBOX |
        NativeMethods.WS_MAXIMIZEBOX;

    public static int ComputeBorderlessFullScreenStyle(int currentStyle)
        => currentStyle & ~FullScreenStripStyleMask;

    public static int CaptureRestorableFrameStyleMask(int currentStyle)
        => currentStyle & RestorableFrameStyleMask;

    public static int RestoreFrameStyle(int currentStyle, int? savedMask, int fallbackMask = DefaultBorderlessChromeStyleFallback)
    {
        int mask = savedMask.HasValue && savedMask.Value != 0 ? savedMask.Value : fallbackMask;
        return currentStyle | mask;
    }

    public static bool RectEquals(in NativeMethods.RECT a, in NativeMethods.RECT b)
        => a.left == b.left && a.top == b.top && a.right == b.right && a.bottom == b.bottom;

    public static bool ShouldSnapWindowPosToFullScreen(
        int x,
        int y,
        int cx,
        int cy,
        uint flags,
        in NativeMethods.RECT targetMonitorRect,
        bool isTransitioning,
        bool isMinimizedOrIconic)
    {
        if (isMinimizedOrIconic || (flags & NativeMethods.SWP_HIDEWINDOW) != 0)
            return false;

        if (x <= -10000 || y <= -10000)
            return false;

        if (targetMonitorRect.Width <= 0 || targetMonitorRect.Height <= 0)
            return false;

        bool hasNoMove = (flags & NativeMethods.SWP_NOMOVE) != 0;
        bool hasNoSize = (flags & NativeMethods.SWP_NOSIZE) != 0;
        if (hasNoMove && hasNoSize)
            return false;

        if (isTransitioning)
            return true;

        bool sizeMismatch = !hasNoSize && (cx < targetMonitorRect.Width || cy < targetMonitorRect.Height);
        bool posMismatch = !hasNoMove && (x != targetMonitorRect.left || y != targetMonitorRect.top);
        return sizeMismatch || posMismatch;
    }

    public static NativeMethods.MINMAXINFO BuildMinMaxInfo(
        NativeMethods.MINMAXINFO mmi,
        in NativeMethods.MONITORINFO info,
        bool useFullMonitorBounds,
        double minWidthDip,
        double minHeightDip,
        uint dpi,
        bool clampMinTrackToWorkArea)
    {
        var targetRect = useFullMonitorBounds ? info.rcMonitor : info.rcWork;
        mmi.ptMaxPosition.x = targetRect.left - info.rcMonitor.left;
        mmi.ptMaxPosition.y = targetRect.top - info.rcMonitor.top;
        mmi.ptMaxSize.x = targetRect.right - targetRect.left;
        mmi.ptMaxSize.y = targetRect.bottom - targetRect.top;

        mmi.ptMaxTrackSize.x = Math.Max(mmi.ptMaxTrackSize.x, info.rcMonitor.Width);
        mmi.ptMaxTrackSize.y = Math.Max(mmi.ptMaxTrackSize.y, info.rcMonitor.Height);

        double scale = dpi > 0 ? dpi / 96.0 : 1.0;
        int minW = (int)Math.Ceiling(minWidthDip * scale);
        int minH = (int)Math.Ceiling(minHeightDip * scale);
        if (clampMinTrackToWorkArea)
        {
            minW = Math.Min(minW, Math.Max(1, info.rcWork.Width));
            minH = Math.Min(minH, Math.Max(1, info.rcWork.Height));
        }

        mmi.ptMinTrackSize.x = minW;
        mmi.ptMinTrackSize.y = minH;
        return mmi;
    }
}

internal sealed class FullScreenWindowCoordinator
{
    public bool IsTransitioning { get; private set; }
    public WindowState SavedWindowState { get; private set; } = WindowState.Normal;
    public bool HasSavedPlacement { get; private set; }
    private NativeMethods.WINDOWPLACEMENT _savedPlacement;
    private int? _savedStyleMask;
    private NativeMethods.RECT _targetMonitorRect;
    private bool _hasTargetMonitorRect;

    public void CapturePreFullScreenStateIfNeeded(Window window)
    {
        if (HasSavedPlacement) return;

        SavedWindowState = window.WindowState == WindowState.Minimized ? WindowState.Normal : window.WindowState;
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
        {
            var wp = new NativeMethods.WINDOWPLACEMENT { length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>() };
            if (NativeMethods.GetWindowPlacement(hwnd, ref wp))
            {
                _savedPlacement = wp;
                HasSavedPlacement = true;
            }
        }
    }

    public void EnterFullScreen(Window window, Action collapseChromeAndMargins)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        IsTransitioning = true;
        try
        {
            CapturePreFullScreenStateIfNeeded(window);

            if (hwnd != IntPtr.Zero)
            {
                var monitor = NativeMethods.MonitorFromWindow(hwnd, 2);
                var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
                if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
                {
                    _targetMonitorRect = info.rcMonitor;
                    _hasTargetMonitorRect = true;
                }

                NativeMethods.SetWindowTransitionsDisabled(hwnd, true);
            }

            collapseChromeAndMargins();

            if (window.WindowState != WindowState.Normal)
            {
                window.WindowState = WindowState.Normal;
            }

            ApplyFullScreenNativeState(window, hwnd, forceFrameChanged: true);
        }
        finally
        {
            IsTransitioning = false;
            if (hwnd != IntPtr.Zero)
            {
                NativeMethods.SetWindowTransitionsDisabled(hwnd, false);
            }
        }
    }

    public void Reassert(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        IsTransitioning = true;
        try
        {
            ApplyFullScreenNativeState(window, hwnd, forceFrameChanged: false);
        }
        finally
        {
            IsTransitioning = false;
        }
    }

    private void ApplyFullScreenNativeState(Window window, IntPtr hwnd, bool forceFrameChanged)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            var monitor = NativeMethods.MonitorFromWindow(hwnd, 2);
            var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                _targetMonitorRect = info.rcMonitor;
                _hasTargetMonitorRect = true;
            }

            if (window.WindowState != WindowState.Normal && !NativeMethods.IsIconic(hwnd))
            {
                window.WindowState = WindowState.Normal;
            }

            int curStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_STYLE);
            int restorableBits = WindowFullscreenHelper.CaptureRestorableFrameStyleMask(curStyle);
            if (restorableBits != 0)
            {
                _savedStyleMask = (_savedStyleMask ?? 0) | restorableBits;
            }

            int targetStyle = WindowFullscreenHelper.ComputeBorderlessFullScreenStyle(curStyle);
            bool styleChanged = curStyle != targetStyle;
            if (styleChanged)
            {
                NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_STYLE, targetStyle);
            }

            NativeMethods.SetWindowCornerPreference(hwnd, round: false);

            if (_hasTargetMonitorRect)
            {
                bool boundsChanged = !NativeMethods.GetWindowRect(hwnd, out var currentRect) ||
                                     !WindowFullscreenHelper.RectEquals(in currentRect, in _targetMonitorRect);

                uint swpFlags = NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOOWNERZORDER;
                if (forceFrameChanged || styleChanged)
                    swpFlags |= NativeMethods.SWP_FRAMECHANGED;
                if (!window.IsActive)
                    swpFlags |= NativeMethods.SWP_NOACTIVATE;

                if (forceFrameChanged || styleChanged || boundsChanged)
                {
                    NativeMethods.SetWindowPos(
                        hwnd,
                        IntPtr.Zero,
                        _targetMonitorRect.left,
                        _targetMonitorRect.top,
                        _targetMonitorRect.Width,
                        _targetMonitorRect.Height,
                        swpFlags);
                }
            }

            NativeMethods.ClearWindowRegionIfSet(hwnd, redraw: true);
            NativeMethods.SetTaskbarFullscreenState(hwnd, true);
        }
        catch { }
    }

    public void ExitFullScreen(Window window, Action restoreChromeAndRows, Action applyFinalMargins)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        IsTransitioning = true;
        try
        {
            if (hwnd != IntPtr.Zero)
            {
                NativeMethods.SetWindowTransitionsDisabled(hwnd, true);
                NativeMethods.SetTaskbarFullscreenState(hwnd, false);
                NativeMethods.SetWindowCornerPreference(hwnd, round: true);

                int curStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_STYLE);
                int restoredStyle = WindowFullscreenHelper.RestoreFrameStyle(curStyle, _savedStyleMask);
                if (restoredStyle != curStyle)
                {
                    NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_STYLE, restoredStyle);
                }
                _savedStyleMask = null;
            }

            restoreChromeAndRows();

            if (SavedWindowState == WindowState.Maximized)
            {
                if (hwnd != IntPtr.Zero && HasSavedPlacement)
                {
                    var placementToApply = _savedPlacement;
                    placementToApply.showCmd = NativeMethods.SW_SHOWMAXIMIZED;
                    NativeMethods.SetWindowPlacement(hwnd, ref placementToApply);
                }

                if (window.WindowState != WindowState.Maximized)
                {
                    window.WindowState = WindowState.Maximized;
                }

                if (hwnd != IntPtr.Zero)
                {
                    if (HasSavedPlacement)
                    {
                        var currentWp = new NativeMethods.WINDOWPLACEMENT { length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>() };
                        if (NativeMethods.GetWindowPlacement(hwnd, ref currentWp))
                        {
                            currentWp.rcNormalPosition = _savedPlacement.rcNormalPosition;
                            NativeMethods.SetWindowPlacement(hwnd, ref currentWp);
                        }
                    }

                    var monitor = NativeMethods.MonitorFromWindow(hwnd, 2);
                    var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
                    if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
                    {
                        NativeMethods.SetWindowPos(
                            hwnd,
                            IntPtr.Zero,
                            info.rcWork.left,
                            info.rcWork.top,
                            info.rcWork.Width,
                            info.rcWork.Height,
                            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
                    }
                }
            }
            else
            {
                if (window.WindowState != WindowState.Normal)
                {
                    window.WindowState = WindowState.Normal;
                }

                if (hwnd != IntPtr.Zero)
                {
                    if (HasSavedPlacement)
                    {
                        var placementToApply = _savedPlacement;
                        placementToApply.showCmd = NativeMethods.SW_SHOWNORMAL;
                        NativeMethods.SetWindowPlacement(hwnd, ref placementToApply);
                    }

                    var r = _savedPlacement.rcNormalPosition;
                    if (HasSavedPlacement && r.Width > 0 && r.Height > 0)
                    {
                        NativeMethods.SetWindowPos(
                            hwnd,
                            IntPtr.Zero,
                            r.left,
                            r.top,
                            r.Width,
                            r.Height,
                            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
                    }
                }
            }

            applyFinalMargins();
            HasSavedPlacement = false;
            _hasTargetMonitorRect = false;
        }
        finally
        {
            IsTransitioning = false;
            if (hwnd != IntPtr.Zero)
            {
                NativeMethods.SetWindowTransitionsDisabled(hwnd, false);
            }
        }
    }

    public IntPtr HandleWindowHook(
        Window window,
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        bool isFullScreenActive,
        double minWidthDip,
        double minHeightDip,
        bool clampMinTrackToWorkArea,
        ref bool handled)
    {
        if (msg == NativeMethods.WM_GETMINMAXINFO)
        {
            var monitor = NativeMethods.MonitorFromWindow(hwnd, 2);
            if (monitor == IntPtr.Zero) return IntPtr.Zero;

            var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

            bool useFullMonitorBounds = isFullScreenActive || IsTransitioning ||
                                        System.Windows.Shell.WindowChrome.GetWindowChrome(window) == null;

            var mmi = Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);
            uint dpi = NativeMethods.GetDpiForWindow(hwnd);
            mmi = WindowFullscreenHelper.BuildMinMaxInfo(
                mmi,
                in info,
                useFullMonitorBounds,
                minWidthDip,
                minHeightDip,
                dpi,
                clampMinTrackToWorkArea);
            Marshal.StructureToPtr(mmi, lParam, true);
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == NativeMethods.WM_WINDOWPOSCHANGING && (isFullScreenActive || IsTransitioning))
        {
            bool isMinimizedOrIconic = window.WindowState == WindowState.Minimized || NativeMethods.IsIconic(hwnd);
            var wp = Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(lParam);
            var targetRect = ResolveTargetMonitorRect(hwnd, wp);
            if (WindowFullscreenHelper.ShouldSnapWindowPosToFullScreen(
                    wp.x, wp.y, wp.cx, wp.cy, wp.flags, in targetRect, IsTransitioning, isMinimizedOrIconic))
            {
                wp.x = targetRect.left;
                wp.y = targetRect.top;
                wp.cx = targetRect.Width;
                wp.cy = targetRect.Height;
                wp.flags &= ~(NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE);
                Marshal.StructureToPtr(wp, lParam, false);
            }
            return IntPtr.Zero;
        }

        if (msg == NativeMethods.WM_WINDOWPOSCHANGED && (isFullScreenActive || IsTransitioning))
        {
            if (window.WindowState != WindowState.Minimized && !NativeMethods.IsIconic(hwnd))
            {
                NativeMethods.ClearWindowRegionIfSet(hwnd, redraw: false);
            }
        }

        return IntPtr.Zero;
    }

    private NativeMethods.RECT ResolveTargetMonitorRect(IntPtr hwnd, in NativeMethods.WINDOWPOS wp)
    {
        if (IsTransitioning && _hasTargetMonitorRect)
            return _targetMonitorRect;

        var monitor = NativeMethods.MonitorFromWindow(hwnd, 2);
        if (monitor != IntPtr.Zero)
        {
            var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                _targetMonitorRect = info.rcMonitor;
                _hasTargetMonitorRect = true;
                return info.rcMonitor;
            }
        }

        return _targetMonitorRect;
    }
}
