using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using LexBrowser.Core;
using LexBrowser.Core.Interop;
using LexBrowser.UI.Models;

namespace LexBrowser;

public partial class MainWindow
{
    private IntPtr _nativePipHwnd = IntPtr.Zero;
    private NativeMethods.RECT _lastNativePipRect;
    private NativeMethods.RECT _lastNativePipVisibleRect;
    private uint _lastNativePipDpi = 120;
    private NativeMethods.POINT _pipExitCursorPos;
    private bool _nativePipIsVertical = false;
    private bool _canRecordUserPipResize = false;
    private int _pipSessionId = 0;
    private DispatcherTimer? _nativePipTimer;
    private int _nativePipLostTicks = 0;
    private BrowserTab? _nativePipSourceTab;

    private IntPtr FindNativePipWindow()
    {
        IntPtr found = IntPtr.Zero;
        IntPtr fallbackFound = IntPtr.Zero;
        IntPtr mainHwnd = new WindowInteropHelper(this).Handle;
        uint expectedBrowserPid = _activeTab?.WebView?.CoreWebView2?.BrowserProcessId
            ?? Tabs.FirstOrDefault(t => !t.IsDisposed && t.WebView?.CoreWebView2 != null)?.WebView?.CoreWebView2?.BrowserProcessId
            ?? 0;

        NativeMethods.EnumWindows((hWnd, lParam) =>
        {
            if (hWnd == mainHwnd) return true;
            if (!NativeMethods.IsWindowVisible(hWnd) || NativeMethods.IsIconic(hWnd)) return true;

            int exStyle = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);
            if ((exStyle & NativeMethods.WS_EX_TOPMOST) == 0) return true;

            var sbClass = new StringBuilder(64);
            NativeMethods.GetClassName(hWnd, sbClass, sbClass.Capacity);
            if (sbClass.ToString() != "Chrome_WidgetWin_1") return true;

            if (NativeMethods.GetWindowTextLength(hWnd) <= 0) return true;

            if (!NativeMethods.GetWindowRect(hWnd, out var rc)) return true;
            if (rc.Width < 120 || rc.Height < 80 || rc.left < -10000) return true;

            if (expectedBrowserPid > 0)
            {
                NativeMethods.GetWindowThreadProcessId(hWnd, out uint winPid);
                if (winPid == expectedBrowserPid)
                {
                    found = hWnd;
                    return false;
                }
                if (fallbackFound == IntPtr.Zero) fallbackFound = hWnd;
                return true;
            }

            found = hWnd;
            return false;
        }, IntPtr.Zero);

        return found != IntPtr.Zero ? found : fallbackFound;
    }

    private bool UpdateCachedNativePipBounds(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd) || NativeMethods.IsIconic(hwnd)) return false;
        if (!NativeMethods.GetWindowRect(hwnd, out var rc)) return false;
        if (rc.Width < 120 || rc.Height < 80 || rc.left < -10000) return false;

        _lastNativePipRect = rc;
        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out var visRc, Marshal.SizeOf<NativeMethods.RECT>()) == 0 && visRc.Width > 0)
        {
            _lastNativePipVisibleRect = visRc;
        }
        else
        {
            _lastNativePipVisibleRect = rc;
        }

        uint dpi = NativeMethods.GetDpiForWindow(hwnd);
        if (dpi > 0) _lastNativePipDpi = dpi;
        return true;
    }

    private void ResizeNativePipWindow(IntPtr hwnd, int targetW, int targetH)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rc)) return;
        if (Math.Abs(rc.Width - targetW) <= 2 && Math.Abs(rc.Height - targetH) <= 2) return;

        var monitor = NativeMethods.MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        int minLeft = 10, minTop = 10, maxRight = 1910, maxBottom = 1040;
        if (monitor != IntPtr.Zero)
        {
            var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (NativeMethods.GetMonitorInfo(monitor, ref mi))
            {
                minLeft = mi.rcWork.left + 8;
                minTop = mi.rcWork.top + 8;
                maxRight = mi.rcWork.right - 8;
                maxBottom = mi.rcWork.bottom - 8;
            }
        }

        int maxW = maxRight - minLeft;
        int maxH = maxBottom - minTop;
        if (targetW > maxW) targetW = maxW;
        if (targetH > maxH) targetH = maxH;

        int newLeft = rc.right - targetW;
        int newTop = rc.bottom - targetH;
        if (newLeft + targetW > maxRight) newLeft = maxRight - targetW;
        if (newTop + targetH > maxBottom) newTop = maxBottom - targetH;
        if (newLeft < minLeft) newLeft = minLeft;
        if (newTop < minTop) newTop = minTop;

        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, newLeft, newTop, targetW, targetH, NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    private void StartNativePipMonitoring(bool isVertical, BrowserTab? sourceTab = null)
    {
        int session = ++_pipSessionId;
        _nativePipIsVertical = isVertical;
        _nativePipSourceTab = sourceTab ?? _activeTab;
        _canRecordUserPipResize = false;
        _nativePipHwnd = IntPtr.Zero;
        _nativePipLostTicks = 0;

        if (_nativePipTimer == null)
        {
            _nativePipTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _nativePipTimer.Tick += NativePipTimer_Tick;
        }
        _nativePipTimer.Stop();

        _ = ApplySavedNativePipSizeAsync(session, isVertical);
    }

    private async Task ApplySavedNativePipSizeAsync(int session, bool isVertical)
    {
        for (int i = 0; i < 24; i++)
        {
            if (session != _pipSessionId) return;
            IntPtr hwnd = FindNativePipWindow();
            if (hwnd != IntPtr.Zero)
            {
                _nativePipHwnd = hwnd;
                UpdateCachedNativePipBounds(hwnd);
                break;
            }
            await Task.Delay(25);
        }

        if (session != _pipSessionId) return;

        if (_nativePipHwnd != IntPtr.Zero)
        {
            int savedW = isVertical ? SettingsManager.Current.NativePipShortsWidth : SettingsManager.Current.NativePipWidth;
            int savedH = isVertical ? SettingsManager.Current.NativePipShortsHeight : SettingsManager.Current.NativePipHeight;

            if (savedW >= 160 && savedH >= 100)
            {
                ResizeNativePipWindow(_nativePipHwnd, savedW, savedH);
                await Task.Delay(70);
                if (session == _pipSessionId && NativeMethods.IsWindowVisible(_nativePipHwnd))
                {
                    ResizeNativePipWindow(_nativePipHwnd, savedW, savedH);
                }
                await Task.Delay(120);
                if (session == _pipSessionId && NativeMethods.IsWindowVisible(_nativePipHwnd))
                {
                    ResizeNativePipWindow(_nativePipHwnd, savedW, savedH);
                    UpdateCachedNativePipBounds(_nativePipHwnd);
                }
            }
            else
            {
                await Task.Delay(150);
                if (session == _pipSessionId && NativeMethods.IsWindowVisible(_nativePipHwnd))
                {
                    UpdateCachedNativePipBounds(_nativePipHwnd);
                }
            }
        }

        if (session == _pipSessionId)
        {
            _canRecordUserPipResize = true;
            _nativePipTimer?.Start();
        }
    }

    private void NativePipTimer_Tick(object? sender, EventArgs e)
    {
        if (_nativePipHwnd != IntPtr.Zero && NativeMethods.IsWindow(_nativePipHwnd))
        {
            _nativePipLostTicks = 0;
            if (!NativeMethods.IsWindowVisible(_nativePipHwnd))
            {
                return;
            }
        }
        else
        {
            _nativePipHwnd = FindNativePipWindow();
            if (_nativePipHwnd == IntPtr.Zero || !NativeMethods.IsWindow(_nativePipHwnd))
            {
                _nativePipLostTicks++;
                if (_nativePipLostTicks >= 15)
                {
                    _nativePipTimer?.Stop();
                    _nativePipLostTicks = 0;
                    _nativePipSourceTab = null;
                }
                return;
            }
            _nativePipLostTicks = 0;
        }

        if (!UpdateCachedNativePipBounds(_nativePipHwnd)) return;

        if (_canRecordUserPipResize)
        {
            int w = _lastNativePipRect.Width;
            int h = _lastNativePipRect.Height;
            if (w >= 160 && h >= 100)
            {
                if (_nativePipIsVertical)
                {
                    if (Math.Abs(w - SettingsManager.Current.NativePipShortsWidth) > 2 || Math.Abs(h - SettingsManager.Current.NativePipShortsHeight) > 2)
                    {
                        SettingsManager.Current.NativePipShortsWidth = w;
                        SettingsManager.Current.NativePipShortsHeight = h;
                        SettingsManager.SaveDeferred(600, notify: false);
                        LogPipEvent($"{{\"type\":\"pip_size_saved\",\"w\":{w},\"h\":{h},\"vertical\":true}}");
                    }
                }
                else
                {
                    if (Math.Abs(w - SettingsManager.Current.NativePipWidth) > 2 || Math.Abs(h - SettingsManager.Current.NativePipHeight) > 2)
                    {
                        SettingsManager.Current.NativePipWidth = w;
                        SettingsManager.Current.NativePipHeight = h;
                        SettingsManager.SaveDeferred(600, notify: false);
                        LogPipEvent($"{{\"type\":\"pip_size_saved\",\"w\":{w},\"h\":{h},\"vertical\":false}}");
                    }
                }
            }
        }
    }

    [Conditional("DEBUG")]
    private static void LogPipEvent(string json)
    {
    }

    private static void CleanLegacyDiagnosticLogs()
    {
        try
        {
            string legacyLog = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LexBrowser",
                "pip.log");
            if (File.Exists(legacyLog))
            {
                File.Delete(legacyLog);
            }
        }
        catch { }
    }

    private async Task TriggerAutoPiPAsync(BrowserTab tab)
    {
        if (tab?.WebView?.CoreWebView2 == null) return;
        try
        {
            string json = JsonSerializer.Serialize(new
            {
                expression = "(async () => { if (window.__lexEnterPiP) return await window.__lexEnterPiP(); return false; })()",
                userGesture = true,
                awaitPromise = true
            });

            await tab.WebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", json);
        }
        catch
        {
            try
            {
                string fallback = JsonSerializer.Serialize(new { action = "enter_pip" });
                tab.WebView.CoreWebView2.PostWebMessageAsJson(fallback);
            }
            catch { }
        }
    }

    private async Task TriggerExitPiPAsync(BrowserTab tab)
    {
        if (tab?.WebView?.CoreWebView2 == null) return;
        try
        {
            string json = JsonSerializer.Serialize(new
            {
                expression = "(async () => { if (window.__lexExitPiP) return await window.__lexExitPiP(); return false; })()",
                userGesture = true,
                awaitPromise = true
            });

            await tab.WebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", json);
        }
        catch
        {
            try
            {
                string fallback = JsonSerializer.Serialize(new { action = "exit_pip" });
                tab.WebView.CoreWebView2.PostWebMessageAsJson(fallback);
            }
            catch { }
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // BOTÓN PÁGINA FLOTANTE (MANUAL)
    // ─────────────────────────────────────────────────────────────────────
    private void BtnTogglePip_Click(object sender, RoutedEventArgs e)
    {
        if (IsLocked || _activeTab == null) return;

        if (_pipWindow.IsVisible && _pipWindow.TargetTab == _activeTab)
        {
            SetMediaProgressReporting(_activeTab, false);
            _pipWindow.HideAndDetach();
            ReinsertWebViewToContainer(_activeTab);
        }
        else
        {
            _pipWindow.AttachTab(_activeTab);
            if (!_pipWindow.IsVisible)
                _pipWindow.Show();
            _pipWindow.Activate();
            SetMediaProgressReporting(_activeTab, true);
        }
    }

    /// <summary>
    /// Reintegra la pestaña flotante a su contenedor original antes de abrir ventanas modales (Configuración, Historial).
    /// </summary>
    private void EnsureFloatingTabReintegrated()
    {
        if (_pipWindow?.IsVisible == true && _pipWindow.TargetTab != null)
        {
            var floatingTab = _pipWindow.TargetTab;
            _pipWindow.HideAndDetach();
            ReinsertWebViewToContainer(floatingTab);
            if (floatingTab != _activeTab && floatingTab.WebView != null)
            {
                floatingTab.WebView.Visibility = Visibility.Collapsed;
            }
        }
    }
}

