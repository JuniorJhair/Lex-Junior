using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using LexJuniorPlayer.Models;

namespace LexJuniorPlayer.Services;

public sealed record EmulatorWindowState(
    bool IsEmulatorWindowOpen,
    bool HasSideToolbar,
    bool IsSideToolbarHidden,
    bool IsFullscreen,
    bool IsMaximizedFit,
    bool IsMinimized,
    uint EmulatorProcessId,
    EmulatorWindowController.Rect MainRectPhysical,
    EmulatorWindowController.Rect MonitorRectPhysical,
    uint Dpi);

/// <summary>
/// Manages the native Qt display and floating toolbar HWNDs of the clean Android emulator:
/// hides the right-side button strip, enables instant non-bouncing borderless fullscreen,
/// scopes F11 (with MOD_NOREPEAT), and exposes coordinates for the floating control pill.
/// </summary>
public sealed class EmulatorWindowController
{
    public const int HotkeyIdF11 = 0x4C4A; // "LJ"
    public const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkF11 = 0x7A;

    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const uint GwOwner = 4;
    private const uint GaRoot = 2;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const int SwRestore = 9;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoCopyBits = 0x0100;
    private const uint MonitorDefaultToPrimary = 1;
    private const uint MonitorDefaultToNearest = 2;

    private const int DwmwaTransitionsForceDisabled = 3;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwcpRound = 2;

    private const long WsVisible = 0x1000_0000L;
    private const long WsCaption = 0x00C0_0000L;
    private const long WsThickFrame = 0x0004_0000L;
    private const long WsMaximizeBox = 0x0001_0000L;
    private const long WsExTopMost = 0x0000_0008L;
    private const long WsExToolWindow = 0x0000_0080L;
    private const long WsExNoActivate = 0x0800_0000L;

    private IntPtr _mainHwnd;
    private IntPtr _sideHwnd;
    private uint _emulatorPid;
    private uint _boostedPid;
    private bool _isFullscreen;
    private bool _isMaximizedFit;
    private long? _savedNormalStyle;
    private Rect? _savedNormalRect;
    private bool _f11Registered;
    private DateTime _lastProcessScanUtc = DateTime.MinValue;
    private int _sideScanAttempts;
    private bool _sideScanGaveUp;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PointPx
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int CbSize;
        public Rect RcMonitor;
        public Rect RcWork;
        public uint DwFlags;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointPx lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(PointPx point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref uint pvAttribute, int cbAttribute);

    public IntPtr MainEmulatorHwnd => _mainHwnd;
    public bool IsFullscreen => _isFullscreen;

    public static bool IsMainEmulatorWindowTitle(string title, string avdName)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(avdName))
            return false;

        return title.Contains("Android Emulator", StringComparison.OrdinalIgnoreCase) &&
               title.Contains(avdName, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSideToolbarCandidate(string className, int width, int height)
    {
        if (string.IsNullOrWhiteSpace(className))
            return false;

        return className.StartsWith("Qt", StringComparison.OrdinalIgnoreCase) &&
               className.Contains("Tool", StringComparison.OrdinalIgnoreCase) &&
               width >= 30 && width <= 150 &&
               height >= 250;
    }

    public static long ComputeBorderlessFullscreenStyle(long currentStyle) =>
        (currentStyle & ~(WsCaption | WsThickFrame)) | WsVisible;

    public static long ComputeRestoredWindowStyle(long savedStyle) =>
        savedStyle | WsVisible | WsCaption | WsThickFrame | WsMaximizeBox;

    public static bool ShouldAcceptHotkeyToggle(DateTime nowUtc, DateTime lastToggleUtc, int cooldownMs = 250) =>
        (nowUtc - lastToggleUtc).TotalMilliseconds >= cooldownMs;

    /// <summary>
    /// Computes fullscreen pill visibility with vertical enter/exit hysteresis so hovering
    /// across the floating pill does not cause rapid Show/Hide thrashing over OpenGL.
    /// </summary>
    public static bool ComputeFullscreenOverlayTargetVisibility(
        bool isCurrentlyVisible,
        bool isOurSuiteForeground,
        bool withinHintWindow,
        bool isPointerOverOverlay,
        PointPx cursor,
        Rect monitorRect,
        int enterBandPhysicalPx = 14,
        int exitBandPhysicalPx = 76)
    {
        if (!isOurSuiteForeground)
            return false;

        if (withinHintWindow || isPointerOverOverlay)
            return true;

        if (cursor.X < monitorRect.Left || cursor.X > monitorRect.Right)
            return false;

        var bandPx = isCurrentlyVisible ? exitBandPhysicalPx : enterBandPhysicalPx;
        return cursor.Y >= monitorRect.Top && cursor.Y <= monitorRect.Top + bandPx;
    }

    public static bool RectEquals(Rect a, Rect b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    public static bool ShouldScanWindowHandles(bool forceScan, bool hasMain, bool hasSide, bool sideGaveUp, DateTime nowUtc, DateTime lastScanUtc)
    {
        if (hasMain && (hasSide || sideGaveUp)) return false;
        if (forceScan) return true;
        var minMs = hasMain ? 5000 : 2000;
        return (nowUtc - lastScanUtc).TotalMilliseconds >= minMs;
    }

    public static (int W, int H) ComputeGuestResolution(int monitorW, int monitorH, int targetPixels = 1280 * 720)
    {
        if (monitorW <= 0 || monitorH <= 0) return (1280, 720);
        double ar = (double)monitorW / monitorH;
        int h = (int)Math.Round(Math.Sqrt(targetPixels / ar));
        int w = (int)Math.Round(h * ar);
        w = (w + 4) / 8 * 8;
        h = (h + 4) / 8 * 8;
        return (Math.Max(w, 960), Math.Max(h, 540));
    }

    public static (int Width, int Height, int Density) ResolveGuestResolution(EngineSettings settings)
    {
        int currentPixels = settings.ResolutionWidth * settings.ResolutionHeight;
        int targetPixels = currentPixels switch
        {
            >= 1_750_000 => 1920 * 1080,
            >= 1_150_000 => 1600 * 900,
            _ => 1280 * 720
        };

        var (refW, refH) = settings.AspectPreset switch
        {
            "16:9" => (16, 9),
            "16:10" => (16, 10),
            "21:9" => (21, 9),
            "4:3" => (4, 3),
            _ => GetPrimaryMonitorPhysicalSize()
        };

        var (w, h) = ComputeGuestResolution(refW, refH, targetPixels);
        int density = targetPixels >= 1920 * 1080 ? 320 : 213;
        return (w, h, density);
    }

    private static (int W, int H) GetPrimaryMonitorPhysicalSize()
    {
        var mon = GetMonitorForWindow(IntPtr.Zero, MonitorDefaultToPrimary);
        return mon.RcMonitor.Width > 0 && mon.RcMonitor.Height > 0
            ? (mon.RcMonitor.Width, mon.RcMonitor.Height)
            : (16, 9);
    }

    public bool IsEmulatorProcessAlive() =>
        _mainHwnd != IntPtr.Zero && IsWindow(_mainHwnd);

    public EmulatorWindowState RefreshAndEnforce(string avdName, bool hideToolbar, bool forceScan = false)
    {
        EnsureTrackedHandles(avdName, forceScan);

        if (_mainHwnd == IntPtr.Zero)
        {
            return new EmulatorWindowState(false, false, false, false, false, false, 0, default, default, 96);
        }

        if (_emulatorPid != 0 && _boostedPid != _emulatorPid)
        {
            TryBoostEmulatorProcessPriority(_emulatorPid);
            _boostedPid = _emulatorPid;
        }

        if (_sideHwnd != IntPtr.Zero)
        {
            var sideVisible = IsWindowVisible(_sideHwnd);
            if (hideToolbar && sideVisible)
            {
                ShowWindow(_sideHwnd, SwHide);
            }
            else if (!hideToolbar && !sideVisible)
            {
                ShowWindow(_sideHwnd, SwShowNoActivate);
            }
        }

        GetWindowRect(_mainHwnd, out var mainRect);
        var mon = GetMonitorForWindow(_mainHwnd);
        var dpi = GetDpiForWindow(_mainHwnd);
        if (dpi == 0) dpi = 96;
        var isMinimized = IsIconic(_mainHwnd);
        var isSideHidden = _sideHwnd != IntPtr.Zero && !IsWindowVisible(_sideHwnd);

        return new EmulatorWindowState(
            IsEmulatorWindowOpen: true,
            HasSideToolbar: _sideHwnd != IntPtr.Zero,
            IsSideToolbarHidden: isSideHidden,
            IsFullscreen: _isFullscreen,
            IsMaximizedFit: _isMaximizedFit,
            IsMinimized: isMinimized,
            EmulatorProcessId: _emulatorPid,
            MainRectPhysical: mainRect,
            MonitorRectPhysical: mon.RcMonitor,
            Dpi: dpi);
    }

    public bool IsOurSuiteForeground(IntPtr launcherHwnd, IntPtr overlayHwnd)
    {
        if (_mainHwnd == IntPtr.Zero || !IsWindow(_mainHwnd))
            return false;

        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero)
            return false;

        if (fg == _mainHwnd || fg == launcherHwnd || (overlayHwnd != IntPtr.Zero && fg == overlayHwnd))
            return true;

        GetWindowThreadProcessId(fg, out var fgPid);
        if (fgPid == (uint)Environment.ProcessId)
            return true;

        return _emulatorPid != 0 && fgPid == _emulatorPid;
    }

    public bool ShouldShowFullscreenOverlay(
        bool isCurrentlyVisible,
        bool isOurSuiteForeground,
        bool withinHintWindow,
        bool isPointerOverOverlay,
        EmulatorWindowState state)
    {
        if (!isOurSuiteForeground)
            return false;

        if (withinHintWindow || isPointerOverOverlay)
            return true;

        return GetCursorPos(out var pt) && ComputeFullscreenOverlayTargetVisibility(
            isCurrentlyVisible,
            isOurSuiteForeground,
            withinHintWindow,
            isPointerOverOverlay,
            pt,
            state.MonitorRectPhysical);
    }

    /// <summary>
    /// Returns true if key sample spots on the windowed Qt emulator title bar (outside the overlay pill)
    /// are directly visible on screen without being covered by an unrelated window.
    /// </summary>
    public bool IsWindowedTitleBarVisibleOnScreen(EmulatorWindowState state)
    {
        if (!state.IsEmulatorWindowOpen || state.IsMinimized || state.IsFullscreen || _mainHwnd == IntPtr.Zero)
            return false;

        var r = state.MainRectPhysical;
        if (r.Width < 360 || r.Height < 200)
            return false;

        int y = r.Top + Math.Max(10, (int)Math.Round((state.Dpi > 0 ? state.Dpi / 96.0 : 1.0) * 14));
        return IsPointOnEmulatorWindow(new PointPx { X = r.Left + 85, Y = y }) ||
               IsPointOnEmulatorWindow(new PointPx { X = r.Right - 70, Y = y });
    }

    private bool IsPointOnEmulatorWindow(PointPx pt)
    {
        var hit = WindowFromPoint(pt);
        if (hit == IntPtr.Zero)
            return false;

        var root = GetAncestor(hit, GaRoot);
        if (root == IntPtr.Zero) root = hit;
        if (root == _mainHwnd)
            return true;

        GetWindowThreadProcessId(root, out var pid);
        return _emulatorPid != 0 && pid == _emulatorPid;
    }

    public void UpdateScopedF11Hotkey(IntPtr launcherHwnd, IntPtr overlayHwnd)
    {
        if (launcherHwnd == IntPtr.Zero)
            return;

        var shouldBeActive = IsOurSuiteForeground(launcherHwnd, overlayHwnd);

        if (shouldBeActive && !_f11Registered)
        {
            _f11Registered = RegisterHotKey(launcherHwnd, HotkeyIdF11, ModNoRepeat, VkF11);
        }
        else if (!shouldBeActive && _f11Registered)
        {
            UnregisterHotKey(launcherHwnd, HotkeyIdF11);
            _f11Registered = false;
        }
    }

    public void UnregisterHotkey(IntPtr launcherHwnd)
    {
        if (_f11Registered && launcherHwnd != IntPtr.Zero)
        {
            UnregisterHotKey(launcherHwnd, HotkeyIdF11);
            _f11Registered = false;
        }
    }

    public bool ToggleFullscreen(string avdName, bool hideToolbar) =>
        SetFullscreen(avdName, !_isFullscreen, hideToolbar);

    public bool SetFullscreen(string avdName, bool fullscreen, bool hideToolbar)
    {
        EnsureTrackedHandles(avdName, forceScan: true);
        if (_mainHwnd == IntPtr.Zero)
            return false;

        if (IsIconic(_mainHwnd))
        {
            ShowWindow(_mainHwnd, SwRestore);
        }

        if (fullscreen)
        {
            CaptureNormalBoundsIfNeeded();
            var mon = GetMonitorForWindow(_mainHwnd);
            var fsStyle = ComputeBorderlessFullscreenStyle(_savedNormalStyle ?? GetWindowStyle(_mainHwnd));
            ApplyWindowStyleAndBounds(_mainHwnd, fsStyle, mon.RcMonitor, SwpFrameChanged | SwpShowWindow | SwpNoCopyBits);

            _isFullscreen = true;
            _isMaximizedFit = false;
        }
        else
        {
            RestoreNormalBounds();
            _isFullscreen = false;
            _isMaximizedFit = false;
        }

        if (hideToolbar && _sideHwnd != IntPtr.Zero && IsWindowVisible(_sideHwnd))
        {
            ShowWindow(_sideHwnd, SwHide);
        }

        ForceForegroundWindow(_mainHwnd);
        return true;
    }

    public bool ToggleMaximizeWorkArea(string avdName, bool hideToolbar)
    {
        EnsureTrackedHandles(avdName, forceScan: true);
        if (_mainHwnd == IntPtr.Zero)
            return false;

        if (IsIconic(_mainHwnd))
        {
            ShowWindow(_mainHwnd, SwRestore);
        }

        if (_isMaximizedFit && !_isFullscreen)
        {
            RestoreNormalBounds();
            _isMaximizedFit = false;
        }
        else
        {
            CaptureNormalBoundsIfNeeded();
            var mon = GetMonitorForWindow(_mainHwnd);
            var normalStyle = ComputeRestoredWindowStyle(_savedNormalStyle ?? GetWindowStyle(_mainHwnd));
            ApplyWindowStyleAndBounds(_mainHwnd, normalStyle, mon.RcWork, SwpFrameChanged | SwpNoZOrder | SwpNoCopyBits);

            _isFullscreen = false;
            _isMaximizedFit = true;
        }

        if (hideToolbar && _sideHwnd != IntPtr.Zero && IsWindowVisible(_sideHwnd))
        {
            ShowWindow(_sideHwnd, SwHide);
        }

        ForceForegroundWindow(_mainHwnd);
        return true;
    }

    private static void ForceForegroundWindow(IntPtr targetHwnd)
    {
        if (targetHwnd == IntPtr.Zero)
            return;

        if (SetForegroundWindow(targetHwnd))
            return;

        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero)
            return;

        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint curThread = GetCurrentThreadId();
        bool attached = false;
        try
        {
            if (fgThread != 0 && fgThread != curThread)
                attached = AttachThreadInput(curThread, fgThread, true);

            SetForegroundWindow(targetHwnd);
        }
        finally
        {
            if (attached)
                AttachThreadInput(curThread, fgThread, false);
        }
    }

    public void FocusEmulatorWindow()
    {
        if (_mainHwnd != IntPtr.Zero && IsWindow(_mainHwnd))
        {
            if (IsIconic(_mainHwnd))
                ShowWindow(_mainHwnd, SwRestore);
            SetForegroundWindow(_mainHwnd);
        }
    }

    public static void DisableWindowTransitions(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;

        try
        {
            int disable = 1;
            _ = DwmSetWindowAttribute(hwnd, DwmwaTransitionsForceDisabled, ref disable, sizeof(int));
        }
        catch { }
    }

    /// <summary>
    /// Configures the overlay pill HWND without WS_EX_LAYERED, applying Win11 DWM rounded
    /// corners and disabling DWM transitions so showing/hiding over OpenGL never flickers.
    /// Returns true if OS-level DWM rounded corners were applied (Win11+).
    /// </summary>
    public static bool ConfigureNonLayeredRoundedOverlay(IntPtr overlayHwnd, uint borderColorBgr = 0x00F8BD38)
    {
        if (overlayHwnd == IntPtr.Zero)
            return false;

        var currentEx = GetWindowExStyle(overlayHwnd);
        SetWindowExStyle(overlayHwnd, currentEx | WsExToolWindow | WsExNoActivate);
        DisableWindowTransitions(overlayHwnd);

        try
        {
            int corner = DwmwcpRound;
            var hrCorner = DwmSetWindowAttribute(overlayHwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));
            _ = DwmSetWindowAttribute(overlayHwnd, DwmwaBorderColor, ref borderColorBgr, sizeof(uint));
            return hrCorner == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Ensures the overlay stays topmost above the emulator without firing SetWindowPos
    /// repeatedly on every timer tick when WS_EX_TOPMOST is already active.
    /// </summary>
    public static void EnsureOverlayAboveEmulator(IntPtr overlayHwnd, bool forcePlace = false)
    {
        if (overlayHwnd == IntPtr.Zero)
            return;

        if (!forcePlace && (GetWindowExStyle(overlayHwnd) & WsExTopMost) != 0)
            return;

        SetWindowPos(overlayHwnd, new IntPtr(-1), 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private static void ApplyWindowStyleAndBounds(IntPtr hwnd, long targetStyle, Rect targetRect, uint swpFlags)
    {
        DisableWindowTransitions(hwnd);
        var currentStyle = GetWindowStyle(hwnd);
        var sameRect = GetWindowRect(hwnd, out var curRect) && RectEquals(curRect, targetRect);
        if (currentStyle == targetStyle && sameRect)
            return;

        if (currentStyle != targetStyle)
        {
            SetWindowStyle(hwnd, targetStyle);
        }

        SetWindowPos(
            hwnd,
            IntPtr.Zero,
            targetRect.Left,
            targetRect.Top,
            targetRect.Width,
            targetRect.Height,
            swpFlags);
    }

    private void EnsureTrackedHandles(string avdName, bool forceScan)
    {
        if (_mainHwnd != IntPtr.Zero && !IsWindow(_mainHwnd))
        {
            ResetState();
        }

        if (_sideHwnd != IntPtr.Zero && !IsWindow(_sideHwnd))
        {
            _sideHwnd = IntPtr.Zero;
        }

        var nowUtc = DateTime.UtcNow;
        if (!ShouldScanWindowHandles(
                forceScan,
                hasMain: _mainHwnd != IntPtr.Zero,
                hasSide: _sideHwnd != IntPtr.Zero,
                sideGaveUp: _sideScanGaveUp,
                nowUtc: nowUtc,
                lastScanUtc: _lastProcessScanUtc))
        {
            return;
        }

        _lastProcessScanUtc = nowUtc;

        if (_mainHwnd == IntPtr.Zero)
        {
            var qemuPids = new HashSet<uint>();
            if (_emulatorPid != 0)
            {
                qemuPids.Add(_emulatorPid);
            }
            else
            {
                foreach (var p in Process.GetProcessesByName("qemu-system-x86_64"))
                {
                    try { qemuPids.Add((uint)p.Id); }
                    catch { }
                    finally { p.Dispose(); }
                }
            }

            if (qemuPids.Count == 0)
            {
                ResetState();
                return;
            }

            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out var pid);
                if (!qemuPids.Contains(pid)) return true;
                if (!IsWindowVisible(h)) return true;
                if (GetWindow(h, GwOwner) != IntPtr.Zero) return true;

                var title = ReadWindowText(h);
                if (IsMainEmulatorWindowTitle(title, avdName))
                {
                    if (_mainHwnd != h)
                    {
                        _mainHwnd = h;
                        _emulatorPid = pid;
                        _sideScanAttempts = 0;
                        _sideScanGaveUp = false;
                    }
                    return false;
                }
                return true;
            }, IntPtr.Zero);
        }

        if (_mainHwnd != IntPtr.Zero && _sideHwnd == IntPtr.Zero && !_sideScanGaveUp)
        {
            _sideScanAttempts++;
            bool allowFallback = _sideScanAttempts >= 2;
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out var pid);
                if (pid != _emulatorPid) return true;
                if (GetWindow(h, GwOwner) != _mainHwnd) return true;

                var cls = ReadClassName(h);
                if (!GetWindowRect(h, out var rect)) return true;
                if (IsSideToolbarCandidate(cls, rect.Width, rect.Height) ||
                    (allowFallback && IsWindowVisible(h) && rect.Width >= 30 && rect.Width <= 150 && rect.Height >= 250))
                {
                    _sideHwnd = h;
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            if (_sideHwnd == IntPtr.Zero && _sideScanAttempts >= 4)
            {
                _sideScanGaveUp = true;
            }
        }
    }

    private static void TryBoostEmulatorProcessPriority(uint pid)
    {
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            if (proc.PriorityClass == ProcessPriorityClass.Normal || proc.PriorityClass == ProcessPriorityClass.BelowNormal)
            {
                proc.PriorityClass = ProcessPriorityClass.AboveNormal;
            }
        }
        catch { }
    }

    private void CaptureNormalBoundsIfNeeded()
    {
        if (_isFullscreen || _isMaximizedFit || _mainHwnd == IntPtr.Zero)
            return;

        _savedNormalStyle ??= GetWindowStyle(_mainHwnd);
        if (GetWindowRect(_mainHwnd, out var rect) && rect.Width > 200 && rect.Height > 150)
        {
            _savedNormalRect = rect;
        }
    }

    private void RestoreNormalBounds()
    {
        if (_mainHwnd == IntPtr.Zero) return;

        DisableWindowTransitions(_mainHwnd);
        var targetStyle = _savedNormalStyle.HasValue
            ? _savedNormalStyle.Value | WsVisible
            : ComputeRestoredWindowStyle(GetWindowStyle(_mainHwnd));
        var targetRect = _savedNormalRect ?? new Rect { Left = 80, Top = 80, Right = 1360, Bottom = 838 };
        ApplyWindowStyleAndBounds(_mainHwnd, targetStyle, targetRect, SwpFrameChanged | SwpNoZOrder | SwpNoCopyBits);
    }

    private void ResetState()
    {
        _mainHwnd = IntPtr.Zero;
        _sideHwnd = IntPtr.Zero;
        _emulatorPid = 0;
        _boostedPid = 0;
        _isFullscreen = false;
        _isMaximizedFit = false;
        _savedNormalStyle = null;
        _savedNormalRect = null;
        _sideScanAttempts = 0;
        _sideScanGaveUp = false;
    }

    private static MonitorInfo GetMonitorForWindow(IntPtr hwnd, uint flags = MonitorDefaultToNearest)
    {
        var mi = new MonitorInfo { CbSize = Marshal.SizeOf<MonitorInfo>() };
        var monitor = MonitorFromWindow(hwnd, flags);
        GetMonitorInfo(monitor, ref mi);
        return mi;
    }

    private static long GetWindowStyle(IntPtr hwnd) =>
        GetWindowLongCompat(hwnd, GwlStyle);

    private static void SetWindowStyle(IntPtr hwnd, long style) =>
        SetWindowLongCompat(hwnd, GwlStyle, style);

    private static long GetWindowExStyle(IntPtr hwnd) =>
        GetWindowLongCompat(hwnd, GwlExStyle);

    private static void SetWindowExStyle(IntPtr hwnd, long exStyle) =>
        SetWindowLongCompat(hwnd, GwlExStyle, exStyle);

    private static long GetWindowLongCompat(IntPtr hwnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index).ToInt64() : GetWindowLong32(hwnd, index);

    private static void SetWindowLongCompat(IntPtr hwnd, int index, long value)
    {
        if (IntPtr.Size == 8)
            SetWindowLongPtr64(hwnd, index, new IntPtr(value));
        else
            SetWindowLong32(hwnd, index, unchecked((int)value));
    }

    private static string ReadWindowText(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        _ = GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string ReadClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(128);
        _ = GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
