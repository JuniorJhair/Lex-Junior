using System;
using System.Runtime.InteropServices;
using System.Text;

namespace LexBrowser.Core.Interop;

internal static class NativeMethods
{
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const int WS_CAPTION = 0x00C00000;
    public const int WS_THICKFRAME = 0x00040000;
    public const int WS_MINIMIZEBOX = 0x00020000;
    public const int WS_MAXIMIZEBOX = 0x00010000;
    public const int WS_MAXIMIZE = 0x01000000;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_HIDEWINDOW = 0x0080;
    public const uint SWP_NOOWNERZORDER = 0x0200;
    public const int SW_SHOWNORMAL = 1;
    public const int SW_SHOWMAXIMIZED = 3;
    public const int WM_GETMINMAXINFO = 0x0024;
    public const int WM_WINDOWPOSCHANGING = 0x0046;
    public const int WM_WINDOWPOSCHANGED = 0x0047;
    public const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWCP_DEFAULT = 0;
    public const int DWMWCP_DONOTROUND = 1;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int left, top, right, bottom;
        public int Width => right - left;
        public int Height => bottom - top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    private class CTaskbarList { }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("602D4995-B13A-429b-A66E-1935E44F4317")]
    private interface ITaskbarList2
    {
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(IntPtr hwnd);
        [PreserveSig] int DeleteTab(IntPtr hwnd);
        [PreserveSig] int ActivateTab(IntPtr hwnd);
        [PreserveSig] int SetActiveAlt(IntPtr hwnd);
        [PreserveSig] int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);
    }

    public static void SetTaskbarFullscreenState(IntPtr hwnd, bool isFullscreen)
    {
        if (hwnd == IntPtr.Zero) return;
        ITaskbarList2? taskbar = null;
        try
        {
            taskbar = (ITaskbarList2)new CTaskbarList();
            if (taskbar.HrInit() == 0)
            {
                _ = taskbar.MarkFullscreenWindow(hwnd, isFullscreen);
            }
        }
        catch { }
        finally
        {
            if (taskbar != null)
            {
                try { Marshal.ReleaseComObject(taskbar); } catch { }
            }
        }
    }

    public static void SetWindowCornerPreference(IntPtr hwnd, bool round)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int pref = round ? DWMWCP_DEFAULT : DWMWCP_DONOTROUND;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch { }
    }

    public static void SetWindowTransitionsDisabled(IntPtr hwnd, bool disabled)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int val = disabled ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref val, sizeof(int));
        }
        catch { }
    }

    public static bool ClearWindowRegionIfSet(IntPtr hwnd, bool redraw = true)
    {
        if (hwnd == IntPtr.Zero) return false;
        IntPtr probeRgn = IntPtr.Zero;
        try
        {
            probeRgn = CreateRectRgn(0, 0, 0, 0);
            int rgnType = probeRgn != IntPtr.Zero ? GetWindowRgn(hwnd, probeRgn) : 0;
            if (rgnType != 0)
            {
                _ = SetWindowRgn(hwnd, IntPtr.Zero, redraw);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (probeRgn != IntPtr.Zero)
            {
                try { DeleteObject(probeRgn); } catch { }
            }
        }
    }

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPlacement(IntPtr hWnd, [In] ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    public static extern int GetWindowRgn(IntPtr hWnd, IntPtr hRgn);

    [DllImport("user32.dll")]
    public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SHGetPropertyStoreForWindow(
        IntPtr hwnd,
        ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IWindowPropertyStore propertyStore);

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IWindowPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out WIN_PROPERTYKEY pkey);
        void GetValue(ref WIN_PROPERTYKEY key, out WIN_PROPVARIANT pv);
        void SetValue(ref WIN_PROPERTYKEY key, ref WIN_PROPVARIANT pv);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct WIN_PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
        public WIN_PROPERTYKEY(Guid guid, uint id) { fmtid = guid; pid = id; }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct WIN_PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pwszVal;
    }

    /// <summary>
    /// Asigna <c>System.AppUserModel.ID</c> (y opcionalmente icono/nombre de relanzamiento)
    /// directamente al HWND para que Windows agrupe e identifique esta ventana con su propio icono en la barra de tareas.
    /// </summary>
    public static void SetWindowAppUserModelId(
        IntPtr hwnd,
        string appUserModelId,
        string? relaunchIconResource = null,
        string? relaunchDisplayName = null,
        string? relaunchCommand = null)
    {
        if (hwnd == IntPtr.Zero || string.IsNullOrWhiteSpace(appUserModelId)) return;
        try
        {
            var iid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store == null)
                return;

            var appUserModelGuid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
            SetStringProp(store, new WIN_PROPERTYKEY(appUserModelGuid, 5), appUserModelId);
            if (!string.IsNullOrWhiteSpace(relaunchCommand))
                SetStringProp(store, new WIN_PROPERTYKEY(appUserModelGuid, 2), relaunchCommand);
            if (!string.IsNullOrWhiteSpace(relaunchIconResource))
                SetStringProp(store, new WIN_PROPERTYKEY(appUserModelGuid, 3), relaunchIconResource);
            if (!string.IsNullOrWhiteSpace(relaunchDisplayName))
                SetStringProp(store, new WIN_PROPERTYKEY(appUserModelGuid, 4), relaunchDisplayName);
            store.Commit();
        }
        catch { }

        static void SetStringProp(IWindowPropertyStore store, WIN_PROPERTYKEY key, string value)
        {
            var pv = new WIN_PROPVARIANT
            {
                vt = 31, // VT_LPWSTR
                pwszVal = Marshal.StringToCoTaskMemUni(value)
            };
            try
            {
                store.SetValue(ref key, ref pv);
            }
            finally
            {
                Marshal.FreeCoTaskMem(pv.pwszVal);
            }
        }
    }
}

