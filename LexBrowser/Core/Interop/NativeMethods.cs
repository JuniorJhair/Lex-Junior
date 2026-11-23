using System;
using System.Runtime.InteropServices;
using System.Text;

namespace LexBrowser.Core.Interop;

internal static class NativeMethods
{
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const int WS_MAXIMIZEBOX = 0x00010000;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

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

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

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

