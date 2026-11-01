using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace LexBrowser.Core.Engine;

/// <summary>
/// Controlador Win32 de alto rendimiento para el mini-reproductor nativo de Chromium.
/// Intercepta el arrastre de bordes antes del bucle modal de Edge para eliminar el
/// tope rígido de 800px y permitir estiramiento 100% fluido a 60+ FPS, doble clic para
/// maximizar (estilo Brave) y Ctrl + Rueda del ratón.
/// </summary>
public static class NativePipResizer
{
    private static IntPtr _mouseHook = IntPtr.Zero;
    private static LowLevelMouseProc? _mouseProc;

    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int VK_CONTROL = 0x11;

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [Flags]
    private enum ResizeEdge
    {
        None = 0,
        Left = 1,
        Right = 2,
        Top = 4,
        Bottom = 8,
        TopLeft = Top | Left,
        TopRight = Top | Right,
        BottomLeft = Bottom | Left,
        BottomRight = Bottom | Right
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT Point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    private const uint GA_ROOT = 2;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    private const int IDC_SIZEALL = 32646;
    private const int IDC_SIZENWSE = 32642;
    private const int IDC_SIZENESW = 32643;
    private const int IDC_SIZEWE = 32644;
    private const int IDC_SIZENS = 32645;

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    // Estado del arrastre activo fluido
    private static bool _isCustomResizing = false;
    private static IntPtr _activePipHwnd = IntPtr.Zero;
    private static ResizeEdge _activeEdge = ResizeEdge.None;
    private static RECT _startRect;
    private static POINT _startPoint;
    private static double _startAspectRatio = 16.0 / 9.0;

    // Estado de doble clic
    private static uint _lastClickTime = 0;
    private static POINT _lastClickPoint;
    private static RECT _savedNormalRect;
    private static bool _isMaximizedMode = false;

    public static void Initialize()
    {
        if (_mouseHook != IntPtr.Zero) return;

        try
        {
            _mouseProc = MouseHookCallback;
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;
            var modHandle = GetModuleHandle(curModule?.ModuleName);
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, modHandle, 0);
        }
        catch { }
    }

    public static void Shutdown()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }

    private static bool IsNativePipWindow(IntPtr hwnd, out IntPtr rootHwnd)
    {
        rootHwnd = GetAncestor(hwnd, GA_ROOT);
        if (rootHwnd == IntPtr.Zero) rootHwnd = hwnd;

        var sbClass = new StringBuilder(256);
        GetClassName(rootHwnd, sbClass, 256);
        string className = sbClass.ToString();

        if (className != "Chrome_WidgetWin_1") return false;

        var sbTitle = new StringBuilder(512);
        GetWindowText(rootHwnd, sbTitle, 512);
        string title = sbTitle.ToString().ToLowerInvariant();

        return title.Contains("youtube.com") || title.Contains("video") || title.Length < 35;
    }

    private static ResizeEdge GetResizeEdge(POINT pt, RECT rc)
    {
        const int margin = 10;
        if (pt.X < rc.Left - margin || pt.X > rc.Right + margin ||
            pt.Y < rc.Top - margin || pt.Y > rc.Bottom + margin)
        {
            return ResizeEdge.None;
        }

        ResizeEdge edge = ResizeEdge.None;
        if (pt.X <= rc.Left + margin) edge |= ResizeEdge.Left;
        else if (pt.X >= rc.Right - margin) edge |= ResizeEdge.Right;

        if (pt.Y <= rc.Top + margin) edge |= ResizeEdge.Top;
        else if (pt.Y >= rc.Bottom - margin) edge |= ResizeEdge.Bottom;

        return edge;
    }

    private static void UpdateCursorForEdge(ResizeEdge edge)
    {
        int cursorId = 0;
        if (edge == ResizeEdge.TopLeft || edge == ResizeEdge.BottomRight) cursorId = IDC_SIZENWSE;
        else if (edge == ResizeEdge.TopRight || edge == ResizeEdge.BottomLeft) cursorId = IDC_SIZENESW;
        else if ((edge & (ResizeEdge.Left | ResizeEdge.Right)) != 0) cursorId = IDC_SIZEWE;
        else if ((edge & (ResizeEdge.Top | ResizeEdge.Bottom)) != 0) cursorId = IDC_SIZENS;

        if (cursorId != 0)
        {
            SetCursor(LoadCursor(IntPtr.Zero, cursorId));
        }
    }

    private static IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

            // 1. CONTROL CON RUEDA DEL RATÓN (Ctrl + Rueda sobre el reproductor)
            if (msg == WM_MOUSEWHEEL)
            {
                bool isCtrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                if (isCtrl)
                {
                    IntPtr target = WindowFromPoint(hookStruct.pt);
                    if (IsNativePipWindow(target, out IntPtr rootHwnd))
                    {
                        short delta = (short)((hookStruct.mouseData >> 16) & 0xFFFF);
                        ResizePipWindow(rootHwnd, delta > 0 ? 1.12 : 0.88);
                        return (IntPtr)1;
                    }
                }
            }

            // 2. DETECCIÓN DE CLIC EN BORDES Y DOBLE CLIC EN BARRA SUPERIOR
            else if (msg == WM_LBUTTONDOWN)
            {
                IntPtr target = WindowFromPoint(hookStruct.pt);
                if (IsNativePipWindow(target, out IntPtr rootHwnd))
                {
                    GetWindowRect(rootHwnd, out RECT rc);
                    var edge = GetResizeEdge(hookStruct.pt, rc);

                    // A) Si el usuario hace clic en el borde exterior:
                    // CONSUMIMOS el clic para que Edge NO active su bucle modal rígido de 800px
                    if (edge != ResizeEdge.None)
                    {
                        _isCustomResizing = true;
                        _activePipHwnd = rootHwnd;
                        _activeEdge = edge;
                        _startRect = rc;
                        _startPoint = hookStruct.pt;
                        _startAspectRatio = (double)rc.Width / Math.Max(1, rc.Height);

                        UpdateCursorForEdge(edge);
                        return (IntPtr)1; // Consumido por Lex Junior
                    }

                    // B) Si el usuario hace doble clic en la barra de título (primeros 34px)
                    if (hookStruct.pt.Y <= rc.Top + 34 && hookStruct.pt.X < rc.Right - 90)
                    {
                        uint now = hookStruct.time;
                        if (now - _lastClickTime <= GetDoubleClickTime() &&
                            Math.Abs(hookStruct.pt.X - _lastClickPoint.X) < 8 &&
                            Math.Abs(hookStruct.pt.Y - _lastClickPoint.Y) < 8)
                        {
                            ToggleMaximizePip(rootHwnd, rc);
                            _lastClickTime = 0;
                            return (IntPtr)1; // Consumido
                        }

                        _lastClickTime = now;
                        _lastClickPoint = hookStruct.pt;
                    }
                }
            }

            // 3. ARRASTRE FLUIDO EN TIEMPO REAL A 60+ FPS
            else if (msg == WM_MOUSEMOVE)
            {
                if (_isCustomResizing && _activePipHwnd != IntPtr.Zero)
                {
                    PerformFluidResize(hookStruct.pt);
                    UpdateCursorForEdge(_activeEdge);
                    return (IntPtr)1; // Consumido
                }
                else
                {
                    // Dar feedback de cursor visual cuando el puntero pase por los bordes
                    IntPtr target = WindowFromPoint(hookStruct.pt);
                    if (IsNativePipWindow(target, out IntPtr rootHwnd))
                    {
                        GetWindowRect(rootHwnd, out RECT rc);
                        var edge = GetResizeEdge(hookStruct.pt, rc);
                        if (edge != ResizeEdge.None)
                        {
                            UpdateCursorForEdge(edge);
                        }
                    }
                }
            }

            // 4. FINALIZAR ARRASTRE
            else if (msg == WM_LBUTTONUP)
            {
                if (_isCustomResizing)
                {
                    _isCustomResizing = false;
                    _activePipHwnd = IntPtr.Zero;
                    _activeEdge = ResizeEdge.None;
                    return (IntPtr)1; // Consumido
                }
            }
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private static void PerformFluidResize(POINT curPos)
    {
        int dx = curPos.X - _startPoint.X;
        int dy = curPos.Y - _startPoint.Y;

        int newLeft = _startRect.Left;
        int newTop = _startRect.Top;
        int newRight = _startRect.Right;
        int newBottom = _startRect.Bottom;

        if ((_activeEdge & ResizeEdge.Left) != 0) newLeft = Math.Min(_startRect.Right - 160, _startRect.Left + dx);
        if ((_activeEdge & ResizeEdge.Right) != 0) newRight = Math.Max(_startRect.Left + 160, _startRect.Right + dx);
        if ((_activeEdge & ResizeEdge.Top) != 0) newTop = Math.Min(_startRect.Bottom - 120, _startRect.Top + dy);
        if ((_activeEdge & ResizeEdge.Bottom) != 0) newBottom = Math.Max(_startRect.Top + 120, _startRect.Bottom + dy);

        int newW = newRight - newLeft;
        int newH = newBottom - newTop;

        var screen = SystemParameters.WorkArea;
        newW = Math.Clamp(newW, 160, (int)screen.Width);
        newH = Math.Clamp(newH, 120, (int)screen.Height);

        // Si se arrastra desde la izquierda, anclar posición derecha
        if ((_activeEdge & ResizeEdge.Left) != 0) newLeft = _startRect.Right - newW;
        if ((_activeEdge & ResizeEdge.Top) != 0) newTop = _startRect.Bottom - newH;

        if (newLeft < (int)screen.Left) newLeft = (int)screen.Left;
        if (newTop < (int)screen.Top) newTop = (int)screen.Top;

        SetWindowPos(_activePipHwnd, IntPtr.Zero, newLeft, newTop, newW, newH, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private static void ToggleMaximizePip(IntPtr hwnd, RECT currentRc)
    {
        var screen = SystemParameters.WorkArea;
        double aspect = (double)currentRc.Width / Math.Max(1, currentRc.Height);

        if (!_isMaximizedMode)
        {
            _savedNormalRect = currentRc;
            _isMaximizedMode = true;

            int targetW, targetH, targetX, targetY;

            // Video Vertical (Shorts 9:16): Estirar desde arriba hasta la barra de tareas (como Brave Imagen 4)
            if (aspect < 1.0)
            {
                targetH = (int)screen.Height - 16;
                targetW = (int)(targetH * (9.0 / 16.0));
                targetX = (int)screen.Right - targetW - 20;
                targetY = (int)screen.Top + 8;
            }
            // Video Horizontal (16:9): Estirar a lo ancho de la laptop (como Brave Imagen 3)
            else
            {
                targetW = (int)(screen.Width * 0.90);
                targetH = (int)(targetW * (9.0 / 16.0));
                if (targetH > screen.Height - 20)
                {
                    targetH = (int)screen.Height - 20;
                    targetW = (int)(targetH * (16.0 / 9.0));
                }
                targetX = (int)(screen.Left + (screen.Width - targetW) / 2);
                targetY = (int)(screen.Top + (screen.Height - targetH) / 2);
            }

            SetWindowPos(hwnd, IntPtr.Zero, targetX, targetY, targetW, targetH, SWP_NOZORDER | SWP_NOACTIVATE);
        }
        else
        {
            _isMaximizedMode = false;
            int w = _savedNormalRect.Width > 200 ? _savedNormalRect.Width : 640;
            int h = _savedNormalRect.Height > 150 ? _savedNormalRect.Height : 360;
            int x = (int)screen.Right - w - 24;
            int y = (int)screen.Bottom - h - 24;

            SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
        }
    }

    private static void ResizePipWindow(IntPtr hwnd, double factor)
    {
        if (!GetWindowRect(hwnd, out RECT rc)) return;

        int newW = (int)Math.Round(rc.Width * factor);
        int newH = (int)Math.Round(rc.Height * factor);

        var screen = SystemParameters.WorkArea;
        newW = Math.Clamp(newW, 160, (int)screen.Width);
        newH = Math.Clamp(newH, 120, (int)screen.Height);

        int newX = rc.Right - newW;
        int newY = rc.Bottom - newH;

        if (newX < (int)screen.Left) newX = (int)screen.Left;
        if (newY < (int)screen.Top) newY = (int)screen.Top;

        SetWindowPos(hwnd, IntPtr.Zero, newX, newY, newW, newH, SWP_NOZORDER | SWP_NOACTIVATE);
    }
}
