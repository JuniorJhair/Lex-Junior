using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using LexBrowser.Core;
using LexBrowser.Core.AdBlock;
using LexBrowser.Core.Engine;
using LexBrowser.UI.Models;
using LexBrowser.UI.Views;

namespace LexBrowser;

public partial class MainWindow : Window
{
    public ObservableCollection<BrowserTab> Tabs { get; } = new();
    private BrowserTab? _activeTab;

    // Ventana flotante manual para "Página Flotante"
    private readonly FloatingPipWindow _pipWindow = new();

    // Historial para "Volver a abrir pestaña cerrada" (Ctrl+Shift+T)
    private readonly Stack<(string Url, string Title, bool IsIncognito)> _closedTabsHistory = new();

    // Temporizador para el banner de 'Deshacer' de marcadores (10 segundos)
    private readonly DispatcherTimer _undoBookmarkTimer = new();

    // Temporizador para la notificación Toast flotante
    private readonly DispatcherTimer _toastTimer = new();

    // Temporizador para el widget flotante de zoom (4 segundos)
    private readonly DispatcherTimer _zoomWidgetTimer = new();

    // Estado previo antes de entrar a pantalla completa real
    private WindowState _savedWindowState = WindowState.Normal;
    private ResizeMode _savedResizeMode = ResizeMode.CanResizeWithGrip;

    // Variables para arrastre visual suave de pestañas y desprendimiento a nueva ventana (Tab Tear-Off)
    private Point _tabDragStartPoint;
    private BrowserTab? _draggedTab;
    private Border? _draggedBorder;
    private bool _isDraggingTab = false;
    private BrowserTab? _initialAttachedTab;
    private string? _initialUrl;

    public MainWindow() : this((string?)null, null)
    {
    }

    public MainWindow(string? initialUrl) : this(initialUrl, null)
    {
    }

    public MainWindow(BrowserTab? initialTab) : this(null, initialTab)
    {
    }

    public MainWindow(string? initialUrl, BrowserTab? initialTab)
    {
        _initialUrl = NormalizeExternalUrl(initialUrl);
        _initialAttachedTab = initialTab;
        InitializeComponent();
        InitializeTranslateLanguageList();
        TabsItemsControl.ItemsSource = Tabs;
        BookmarksItemsControl.ItemsSource = BookmarkManager.Bookmarks;

        // Configurar temporizador de 10 segundos para Deshacer
        _undoBookmarkTimer.Interval = TimeSpan.FromSeconds(10);
        _undoBookmarkTimer.Tick += (s, e) =>
        {
            _undoBookmarkTimer.Stop();
            UndoBanner.Visibility = Visibility.Collapsed;
        };

        // Configurar temporizador para la notificación Toast (6 segundos)
        _toastTimer.Interval = TimeSpan.FromSeconds(6);
        _toastTimer.Tick += (s, e) =>
        {
            HideToast();
        };

        // Configurar temporizador para el widget flotante de zoom (4 segundos)
        _zoomWidgetTimer.Interval = TimeSpan.FromSeconds(4);
        _zoomWidgetTimer.Tick += (s, e) =>
        {
            _zoomWidgetTimer.Stop();
            FloatingZoomWidget.Visibility = Visibility.Collapsed;
        };

        // Escuchar cambios de configuración y descargas
        SettingsManager.SettingsChanged += ApplySettings;
        DownloadManager.DownloadsChanged += OnDownloadsChanged;
        ApplySettings();

        // Callback cuando el usuario pulsa "⇱ Volver" en la Página Flotante manual
        _pipWindow.OnReturnToTabRequested = (tab) =>
        {
            Dispatcher.Invoke(() =>
            {
                _pipWindow.HideAndDetach();
                ReinsertWebViewToContainer(tab);
                if (WindowState == WindowState.Minimized)
                {
                    WindowState = WindowState.Normal;
                }
                Topmost = true;
                Topmost = false;
                Activate();
                Focus();
                SelectTab(tab, force: true);
            });
        };

        _pipWindow.OnDetachWebView = (tab) =>
        {
            Dispatcher.Invoke(() =>
            {
                SetMediaProgressReporting(tab, false);
                ReinsertWebViewToContainer(tab);
            });
        };

        Loaded += MainWindow_Loaded;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        StateChanged += MainWindow_StateChanged;
        SizeChanged += MainWindow_SizeOrLocationChanged;
        LocationChanged += MainWindow_SizeOrLocationChanged;
        UpdateWindowMargin();
    }

    // ─────────────────────────────────────────────────────────────────────
    // MAXIMIZADO LIMITADO AL ÁREA DE TRABAJO (la barra de tareas no tapa el contenido)
    // Con WindowStyle=None, Windows maximiza sobre el monitor completo y la barra de tareas
    // tapa la parte inferior de la página (línea de tiempo de los Shorts). Se corrige limitando
    // el tamaño maximizado al área de trabajo. En pantalla completa real no se aplica.
    // ─────────────────────────────────────────────────────────────────────
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        source?.AddHook(WorkAreaHook);
    }

    private IntPtr WorkAreaHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;
        if (WindowChrome.GetWindowChrome(this) == null) return IntPtr.Zero; // pantalla completa real

        var monitor = NativeMethods.MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;

        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

        var mmi = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);
        mmi.ptMaxPosition.x = info.rcWork.left - info.rcMonitor.left;
        mmi.ptMaxPosition.y = info.rcWork.top - info.rcMonitor.top;
        mmi.ptMaxSize.x = info.rcWork.right - info.rcWork.left;
        mmi.ptMaxSize.y = info.rcWork.bottom - info.rcWork.top;

        uint dpi = NativeMethods.GetDpiForWindow(hwnd);
        double scale = dpi > 0 ? dpi / 96.0 : 1.0;
        mmi.ptMinTrackSize.x = (int)Math.Ceiling(MinWidth * scale);
        mmi.ptMinTrackSize.y = (int)Math.Ceiling(MinHeight * scale);

        System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    private static class NativeMethods
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOPMOST = 0x00000008;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct POINT { public int x; public int y; }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct RECT
        {
            public int left, top, right, bottom;
            public int Width => right - left;
            public int Height => bottom - top;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    }

    private static readonly byte[] EmptyResponseBytes = Array.Empty<byte>();

    // ─────────────────────────────────────────────────────────────────────
    // CONTROLADOR DE TAMAÑO Y BOTONES DEL MINI-REPRODUCTOR NATIVO (WebView2)
    // ─────────────────────────────────────────────────────────────────────
    private IntPtr _nativePipHwnd = IntPtr.Zero;
    private NativeMethods.RECT _lastNativePipRect;
    private NativeMethods.RECT _lastNativePipVisibleRect;
    private uint _lastNativePipDpi = 120;
    private NativeMethods.POINT _pipExitCursorPos;
    private bool _nativePipIsVertical = false;
    private bool _canRecordUserPipResize = false;
    private int _pipSessionId = 0;
    private DispatcherTimer? _nativePipTimer;

    private IntPtr FindNativePipWindow()
    {
        IntPtr found = IntPtr.Zero;
        IntPtr fallbackFound = IntPtr.Zero;
        IntPtr mainHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        uint expectedBrowserPid = _activeTab?.WebView?.CoreWebView2?.BrowserProcessId
            ?? Tabs.FirstOrDefault(t => !t.IsDisposed && t.WebView?.CoreWebView2 != null)?.WebView?.CoreWebView2?.BrowserProcessId
            ?? 0;

        NativeMethods.EnumWindows((hWnd, lParam) =>
        {
            if (hWnd == mainHwnd) return true;
            if (!NativeMethods.IsWindowVisible(hWnd) || NativeMethods.IsIconic(hWnd)) return true;

            int exStyle = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);
            if ((exStyle & NativeMethods.WS_EX_TOPMOST) == 0) return true;

            var sbClass = new System.Text.StringBuilder(64);
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
        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out var visRc, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.RECT>()) == 0 && visRc.Width > 0)
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
            var mi = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
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

    private void StartNativePipMonitoring(bool isVertical)
    {
        int session = ++_pipSessionId;
        _nativePipIsVertical = isVertical;
        _canRecordUserPipResize = false;
        _nativePipHwnd = IntPtr.Zero;

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
        if (_nativePipHwnd == IntPtr.Zero || !NativeMethods.IsWindow(_nativePipHwnd) || !NativeMethods.IsWindowVisible(_nativePipHwnd))
        {
            _nativePipHwnd = FindNativePipWindow();
            if (_nativePipHwnd == IntPtr.Zero) return;
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
                        SettingsManager.Save(notify: false);
                        LogPipEvent($"{{\"type\":\"pip_size_saved\",\"w\":{w},\"h\":{h},\"vertical\":true}}");
                    }
                }
                else
                {
                    if (Math.Abs(w - SettingsManager.Current.NativePipWidth) > 2 || Math.Abs(h - SettingsManager.Current.NativePipHeight) > 2)
                    {
                        SettingsManager.Current.NativePipWidth = w;
                        SettingsManager.Current.NativePipHeight = h;
                        SettingsManager.Save(notify: false);
                        LogPipEvent($"{{\"type\":\"pip_size_saved\",\"w\":{w},\"h\":{h},\"vertical\":false}}");
                    }
                }
            }
        }
    }

    // Registro de diagnóstico desactivado en producción para garantizar 0 rastros en disco (Incógnito y Privacidad)
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

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        CleanLegacyDiagnosticLogs();

        // Verificar si es la primera vez que abre el navegador (para ofrecer configurar PIN o elegir "No me gustaría usar contraseña")
        if (_initialAttachedTab == null &&
            !SettingsManager.Current.HasCompletedInitialSecuritySetup &&
            !PasswordManager.HasMasterPasswordConfigured())
        {
            ShowFirstTimeSecuritySetupOverlay();
        }
        else if (_initialAttachedTab == null && PasswordManager.ShouldRequireUnlockOnStartup())
        {
            ShowBrowserLockOverlay();
        }
        else if (_initialAttachedTab != null)
        {
            var initial = _initialAttachedTab;
            _initialAttachedTab = null;
            AttachExistingTab(initial);
        }
        else if (Tabs.Count == 0)
        {
            await RestoreOrInitializeSessionTabsAsync();
        }

        // Comprobar silenciosamente si existe una nueva versión publicada en GitHub Releases
        _ = CheckForBrowserUpdateInBackgroundAsync();

        // Comprobar una sola vez y sin consumo en segundo plano si es el navegador predeterminado
        _ = CheckDefaultBrowserStatusAsync();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_isGuestSession && SettingsManager.Current.RestorePreviousSession)
        {
            SessionManager.SaveSession(Tabs, _activeTab);
        }
        base.OnClosing(e);
    }

    private async Task RestoreOrInitializeSessionTabsAsync()
    {
        string? launchUrl = !string.IsNullOrWhiteSpace(_initialUrl) ? _initialUrl : null;
        _initialUrl = null;

        if (_isGuestSession)
        {
            await AddNewTabAsync(launchUrl, isIncognito: true);
            return;
        }

        bool sessionRestored = false;
        if (SettingsManager.Current.RestorePreviousSession)
        {
            var savedSession = SessionManager.LoadSession();
            if (savedSession != null && savedSession.Tabs.Count > 0)
            {
                int activeIndex = Math.Clamp(savedSession.ActiveIndex, 0, savedSession.Tabs.Count - 1);
                BrowserTab? targetActiveTab = null;

                for (int i = 0; i < savedSession.Tabs.Count; i++)
                {
                    var item = savedSession.Tabs[i];
                    bool isActive = (i == activeIndex && string.IsNullOrWhiteSpace(launchUrl));

                    var tab = await AddNewTabAsync(item.Url, isIncognito: false, skipInitialNavigate: !isActive);
                    if (!string.IsNullOrWhiteSpace(item.Title))
                    {
                        tab.Title = item.Title;
                    }
                    if (item.IsPinned)
                    {
                        tab.PinMode = TabPinMode.LogoAndName;
                    }

                    if (!isActive)
                    {
                        tab.IsLazyLoaded = true;
                    }
                    else
                    {
                        targetActiveTab = tab;
                    }
                }

                if (targetActiveTab != null)
                {
                    SelectTab(targetActiveTab, force: true);
                }
                sessionRestored = true;
            }
        }

        // Si se abrió con un archivo o enlace externo (ej. PDF), abrirlo en una pestaña activa
        if (!string.IsNullOrWhiteSpace(launchUrl))
        {
            var extTab = await AddNewTabAsync(launchUrl, isIncognito: false);
            SelectTab(extTab, force: true);
        }
        else if (!sessionRestored && Tabs.Count == 0)
        {
            await AddNewTabAsync(null, isIncognito: false);
        }
    }

    private static string? NormalizeExternalUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim('"', ' ', '\'');
        if (raw.StartsWith("-") || raw.StartsWith("/")) return null;

        if (File.Exists(raw))
        {
            try { return new Uri(raw).AbsoluteUri; } catch { }
        }
        return raw;
    }

    public async Task AddNewTabFromExternalAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        string? normalized = NormalizeExternalUrl(url);
        if (string.IsNullOrWhiteSpace(normalized)) return;

        if (BrowserLockOverlay.Visibility == Visibility.Visible)
        {
            _initialUrl = normalized;
            return;
        }

        await AddNewTabAsync(normalized, isIncognito: _isGuestSession);
    }

    private async Task CheckForBrowserUpdateInBackgroundAsync()
    {
        try
        {
            await Task.Delay(2500);
            var update = await UpdateManager.CheckForUpdatesAsync();
            if (update != null)
            {
                Dispatcher.Invoke(() =>
                {
                    TxtUpdateAvailableLabel.Text = $"Actualizar ({update.VersionTag})";
                    BtnUpdateAvailable.ToolTip = $"¡Nueva versión {update.VersionTag} disponible! Haz clic para instalarla automáticamente.";
                    BtnUpdateAvailable.Visibility = Visibility.Visible;
                });
            }
        }
        catch { }
    }

    private async Task CheckDefaultBrowserStatusAsync()
    {
        // 0.0% CPU: solo se verifica una vez tras arrancar la ventana, sin hilos ni procesos en segundo plano
        if (_isGuestSession) return;

        // Esperar 1.5s para no competir con el arranque inicial de pestañas
        await Task.Delay(1500);

        try
        {
            bool isDefault = await Task.Run(() => InstallerService.IsDefaultBrowser());

            // 1. Si ya es predeterminado en Windows: marcar y NUNCA mostrar el aviso
            if (isDefault)
            {
                SettingsManager.Current.WasPreviouslyDefaultBrowser = true;
                SettingsManager.Current.UserDecidedDefaultBrowser = true;
                SettingsManager.Save();
                DefaultBrowserPromptBanner.Visibility = Visibility.Collapsed;
                return;
            }

            // 2. Si el usuario ya tomó una decisión explícita previa (hizo clic en "Sí" o en "Ahora no"):
            // respetamos absolutamente su decisión y no volvemos a interrumpirlo
            if (SettingsManager.Current.UserDecidedDefaultBrowser)
            {
                DefaultBrowserPromptBanner.Visibility = Visibility.Collapsed;
                return;
            }

            // 3. Si aún no ha decidido (o lo ignoró en aperturas previas), mostrarlo en la infobar superior
            DefaultBrowserPromptBanner.Visibility = Visibility.Visible;

            // 4. Permanecer visible por al menos 3 minutos. Si el usuario lo ignora por más de 3 minutos, desaparece esta sesión
            // Task.Delay usa 0.0% CPU y no deja procesos en segundo plano
            await Task.Delay(TimeSpan.FromMinutes(3));
            if (!SettingsManager.Current.UserDecidedDefaultBrowser && DefaultBrowserPromptBanner.Visibility == Visibility.Visible)
            {
                DefaultBrowserPromptBanner.Visibility = Visibility.Collapsed;
            }
        }
        catch { }
    }

    private void BtnSetAsDefault_Click(object sender, RoutedEventArgs e)
    {
        DefaultBrowserPromptBanner.Visibility = Visibility.Collapsed;
        SettingsManager.Current.UserDecidedDefaultBrowser = true;
        SettingsManager.Save();

        try
        {
            InstallerService.OpenWindowsDefaultAppsSettings();
            ShowToast("✨ Abriendo configuración de Windows para elegir a Lex Junior como predeterminado.");
        }
        catch (Exception ex)
        {
            ShowToast($"Error al abrir configuración: {ex.Message}");
        }
    }

    private void BtnDismissDefaultBrowser_Click(object sender, RoutedEventArgs e)
    {
        DefaultBrowserPromptBanner.Visibility = Visibility.Collapsed;
        SettingsManager.Current.UserDecidedDefaultBrowser = true;
        SettingsManager.Save();

        ShowToast(
            "Si deseas cambiar de opinión, en Configuración dejamos un botón para elegir tu navegador predeterminado cuando lo desees.",
            "Ir a Ajustes",
            () => OpenSettings("General"));
    }

    public void AttachExistingTab(BrowserTab tab, int? insertIndex = null)
    {
        if (tab == null || tab.IsDisposed) return;

        tab.OwnerWindow = this;

        if (!Tabs.Contains(tab))
        {
            if (insertIndex.HasValue && insertIndex.Value >= 0 && insertIndex.Value <= Tabs.Count)
                Tabs.Insert(insertIndex.Value, tab);
            else
                Tabs.Add(tab);
        }

        ReinsertWebViewToContainer(tab);
        SelectTab(tab, force: true);
    }

    private void ApplySettings()
    {
        var s = SettingsManager.Current;

        // Mostrar / Ocultar botón de Página Flotante en Navbar
        BtnTogglePip.Visibility = s.ShowFloatingPageButton ? Visibility.Visible : Visibility.Collapsed;

        // Mostrar / Ocultar Barra de Accesos Directos (en Modo Invitado siempre mostramos el banner de invitado)
        bool showBar = s.ShowBookmarksBar || _isGuestSession;
        RowBookmarksBar.Height = showBar ? new GridLength(34) : new GridLength(0);
        RowBookmarksGrid.Visibility = showBar ? Visibility.Visible : Visibility.Collapsed;
        if (BorderGuestSessionBanner != null)
        {
            BorderGuestSessionBanner.Visibility = _isGuestSession ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // REINSERTAR EL WEBVIEW AL CONTENEDOR PRINCIPAL
    // ─────────────────────────────────────────────────────────────────────
    private void ReinsertWebViewToContainer(BrowserTab tab)
    {
        if (tab?.WebView == null || tab.IsDisposed) return;
        var webView = tab.WebView;

        if (webView.Parent is Panel oldParent && oldParent != BrowserContainer)
        {
            oldParent.Children.Remove(webView);
        }

        if (!BrowserContainer.Children.Contains(webView))
        {
            BrowserContainer.Children.Add(webView);
        }

        webView.Visibility = (_activeTab == tab) ? Visibility.Visible : Visibility.Collapsed;
    }

    // ─────────────────────────────────────────────────────────────────────
    // CREAR NUEVA PESTAÑA (Al costado de la activa o en índice indicado)
    // ─────────────────────────────────────────────────────────────────────
    private async Task<BrowserTab> AddNewTabAsync(string? initialUrl = null, bool isIncognito = false, int? insertIndex = null, bool skipInitialNavigate = false)
    {
        if (_isGuestSession)
        {
            isIncognito = true;
        }

        var webView = new WebView2
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        bool isNewTabDefault = string.IsNullOrWhiteSpace(initialUrl) && !skipInitialNavigate;

        var tab = new BrowserTab(webView)
        {
            Url = isNewTabDefault ? "" : (initialUrl ?? ""),
            Title = isNewTabDefault ? (isIncognito ? "Incógnito" : "Nueva pestaña") : "Cargando...",
            IsIncognito = isIncognito,
            IsNewTab = isNewTabDefault,
            OwnerWindow = this
        };

        int resolvedIndex = insertIndex ?? (_activeTab != null ? Tabs.IndexOf(_activeTab) + 1 : Tabs.Count);
        if (resolvedIndex >= 0 && resolvedIndex <= Tabs.Count)
        {
            Tabs.Insert(resolvedIndex, tab);
        }
        else
        {
            Tabs.Add(tab);
        }

        if (!BrowserContainer.Children.Contains(webView))
        {
            webView.Visibility = Visibility.Collapsed;
            BrowserContainer.Children.Add(webView);
        }

        SelectTab(tab);

        try
        {
            var env = await BrowserEngine.GetEnvironmentAsync();
            if (isIncognito)
            {
                var controllerOptions = env.CreateCoreWebView2ControllerOptions();
                controllerOptions.IsInPrivateModeEnabled = true;
                await webView.EnsureCoreWebView2Async(env, controllerOptions);
            }
            else
            {
                await webView.EnsureCoreWebView2Async(env);
            }

            await ConfigureTabWebViewAsync(tab);

            if (!skipInitialNavigate)
            {
                if (isNewTabDefault)
                {
                    webView.NavigateToString(NewTabPage.GetHtml(isIncognito));
                }
                else if (!string.IsNullOrWhiteSpace(initialUrl))
                {
                    webView.CoreWebView2.Navigate(initialUrl!);
                }
            }
        }
        catch (Exception ex)
        {
            tab.Title = "Error";
            MessageBox.Show(
                $"Error al inicializar el motor de navegación:\n{ex.Message}",
                "Lex Junior", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        return tab;
    }

    // ─────────────────────────────────────────────────────────────────────
    // CONFIGURACIÓN DE EVENTOS DEL WEBVIEW (Enrutados a tab.OwnerWindow)
    // ─────────────────────────────────────────────────────────────────────
    private async Task ConfigureTabWebViewAsync(BrowserTab tab)
    {
        var core = tab.WebView.CoreWebView2;
        if (core == null) return;

        // Inyectar script universal de optimización, anti-anuncios, iframes, PiP, traducción limpia y captura/autocompletado pasivo de contraseñas
        try
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(YouTubeOptimizer.InjectedScript);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(PasswordManager.InjectedPasswordScript);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(TranslationService.InjectedShieldScript);
        }
        catch { }

        // 1. MANEJO DE PANTALLA COMPLETA REAL DE LAPTOP (CERO FRANJAS VACÍAS)
        core.ContainsFullScreenElementChanged += (s, e) =>
        {
            var win = tab.OwnerWindow ?? this;
            win.Dispatcher.Invoke(() =>
            {
                if (core.ContainsFullScreenElement)
                {
                    win._savedWindowState = win.WindowState;
                    win._savedResizeMode = win.ResizeMode;

                    // Colapsar barras superiores completamente
                    win.RowHeader.Height = new GridLength(0);
                    win.RowNavBar.Height = new GridLength(0);
                    win.RowBookmarksBar.Height = new GridLength(0);
                    win.RowInfoBar.Height = new GridLength(0);
                    win.RowProgressBar.Height = new GridLength(0);

                    // Eliminar WindowChrome durante fullscreen para evitar bordes negros o franjas de taskbar
                    WindowChrome.SetWindowChrome(win, null);

                    win.ResizeMode = ResizeMode.NoResize;
                    win.WindowState = WindowState.Normal;
                    win.WindowState = WindowState.Maximized;
                    win.UpdateWindowMargin();
                }
                else
                {
                    // Restaurar barras del navegador
                    win.RowHeader.Height = new GridLength(42);
                    win.RowNavBar.Height = new GridLength(50);
                    win.RowBookmarksBar.Height = SettingsManager.Current.ShowBookmarksBar ? new GridLength(34) : new GridLength(0);
                    win.RowInfoBar.Height = GridLength.Auto;
                    win.RowProgressBar.Height = new GridLength(2);

                    // Restaurar WindowChrome normal
                    WindowChrome.SetWindowChrome(win, win.AppWindowChrome);

                    win.ResizeMode = win._savedResizeMode;
                    win.WindowState = win._savedWindowState;
                    win.UpdateWindowMargin();
                }
            });
        };

        // 2. ESCUDO ANTI-POPUNDERS + CONEXIÓN NATIVA DE VENTANAS EMERGENTES OAUTH (Google, Apple, X/Twitter, Microsoft)
        core.WindowCloseRequested += (s, e) =>
        {
            var win = tab.OwnerWindow ?? this;
            win.Dispatcher.Invoke(() =>
            {
                if (!tab.IsDisposed && win.Tabs.Count > 1)
                {
                    win.CloseTab(tab);
                }
            });
        };

        core.NewWindowRequested += (s, e) =>
        {
            var win = tab.OwnerWindow ?? this;
            string url = e.Uri ?? string.Empty;

            Uri? currentUri = null;
            try
            {
                if (tab.WebView?.Source != null)
                {
                    currentUri = tab.WebView.Source;
                }
            }
            catch { }

            // 2.1 Si la URL es vacía o about:blank sin interacción del usuario en un sitio no confiable, bloquear popunder
            if (string.IsNullOrWhiteSpace(url) || url == "about:blank")
            {
                if (!e.IsUserInitiated)
                {
                    e.Handled = true;
                    return;
                }
            }
            else if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                if (SettingsManager.Current.EnableAdBlocker && (AdBlockFilter.ShouldBlock(uri) || AdBlockFilter.IsSuspiciousPopup(uri, currentUri)))
                {
                    e.Handled = true;
                    win.Dispatcher.Invoke(() =>
                    {
                        tab.BlockedAdsCount++;
                        if (win._activeTab == tab)
                            win.UpdateShieldBadgeText();
                    });
                    return;
                }
            }

            // 2.2 Determinar si la página pidió una ventana emergente tipo OAuth / Login ("Continuar con Google / Apple")
            bool hasPopupSize = false;
            double? reqW = null;
            double? reqH = null;
            try
            {
                if (e.WindowFeatures != null && e.WindowFeatures.HasSize)
                {
                    hasPopupSize = true;
                    reqW = e.WindowFeatures.Width;
                    reqH = e.WindowFeatures.Height;
                }
            }
            catch { }

            bool isOAuthUrl = url.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase) ||
                              url.Contains("appleid.apple.com", StringComparison.OrdinalIgnoreCase) ||
                              url.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) ||
                              url.Contains("login.live.com", StringComparison.OrdinalIgnoreCase) ||
                              url.Contains("/oauth", StringComparison.OrdinalIgnoreCase) ||
                              url.Contains("gsi/select", StringComparison.OrdinalIgnoreCase);

            var deferral = e.GetDeferral();
            win.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    if (hasPopupSize || isOAuthUrl)
                    {
                        // Abrir ventana emergente conectada (OAuthPopupWindow) como en Chrome/Brave
                        // preservando window.opener y postMessage para que el login se complete y cierre solo.
                        var popupWin = new OAuthPopupWindow(tab.IsIncognito, url, reqW, reqH)
                        {
                            Owner = win
                        };
                        popupWin.PasswordSubmittedInPopup += (subUrl, subUser, subPwd) =>
                        {
                            win.PromptSaveOrUpdatePassword(subUrl, subUser, subPwd);
                        };
                        popupWin.Show();

                        var popupCore = await popupWin.InitializePopupCoreAsync();
                        e.NewWindow = popupCore;
                        e.Handled = true;
                    }
                    else
                    {
                        // Enlace normal en nueva pestaña: conectar e.NewWindow sin doble navegación (abre solo 1 pestaña)
                        var newTab = await win.AddNewTabAsync(url, isIncognito: tab.IsIncognito, skipInitialNavigate: true);
                        if (newTab?.WebView?.CoreWebView2 != null)
                        {
                            e.NewWindow = newTab.WebView.CoreWebView2;
                        }
                        e.Handled = true;
                    }
                }
                catch
                {
                    e.Handled = true;
                }
                finally
                {
                    try { deferral.Complete(); } catch { }
                }
            });
        };

        // 3. Filtro de red (bloqueo de recursos publicitarios y rastreadores)
        // Optimización de rendimiento COM / RAM: Acotar a contextos que realmente transportan scripts,
        // telemetría y banners (Script, XHR, Fetch, Image, Ping, Other).
        // Se excluyen deliberadamente 'Media' (streaming de video/audio de cientos de MBs),
        // 'Stylesheet' (CSS) y 'Font' para no saturar el bus COM ni retener buffers en memoria.
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Script);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.XmlHttpRequest);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Fetch);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Image);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Ping);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Other);
        core.WebResourceRequested += (s, e) =>
        {
            if (SettingsManager.Current.EnableAdBlocker && Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) && AdBlockFilter.ShouldBlock(uri))
            {
                // En YouTube, responder con 204 No Content en endpoints de anuncios para que los scripts
                // anti-bloqueador de YouTube no fuercen la pausa artificial al primer segundo de video
                int statusCode = 403;
                string statusReason = "Blocked by LexShield";
                if (uri.Host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                    uri.Host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase))
                {
                    statusCode = 204;
                    statusReason = "No Content";
                }

                e.Response = core.Environment.CreateWebResourceResponse(
                    new MemoryStream(EmptyResponseBytes, writable: false), statusCode, statusReason, "Content-Type: text/plain");

                var win = tab.OwnerWindow ?? this;
                win.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    if (tab.IsDisposed) return;
                    tab.BlockedAdsCount++;
                    if (win._activeTab == tab)
                        win.UpdateShieldBadgeText();
                });
            }
        };

        // 3.1 Escudo contra secuestro de notificaciones y permisos invasivos
        core.PermissionRequested += (s, e) =>
        {
            if (SettingsManager.Current.EnableAdBlocker && e.PermissionKind == CoreWebView2PermissionKind.Notifications)
            {
                e.State = CoreWebView2PermissionState.Deny;
                e.Handled = true;
            }
        };

        // 3.2 Recuperación automática si el proceso de renderizado de la pestaña falla inesperadamente
        core.ProcessFailed += (s, e) =>
        {
            if (tab.IsDisposed) return;
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited)
            {
                var win = tab.OwnerWindow ?? this;
                win.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    if (!tab.IsDisposed)
                    {
                        try { core.Reload(); } catch { }
                    }
                });
            }
        };

        // 3.3 Sincronización instantánea de iframes (reproductores externos, anime, streaming)
        core.FrameCreated += (s, e) =>
        {
            if (tab.IsDisposed) return;
            var frame = e.Frame;
            tab.ActiveFrames.TryAdd(frame, 0);
            frame.Destroyed += (fs, fe) => tab.ActiveFrames.TryRemove(frame, out _);

            int currentBoost = tab.VolumeBoostPercent;
            if (currentBoost != 100)
            {
                try
                {
                    _ = frame.ExecuteScriptAsync($"if (window.__lexSetVolumeBoostGlobal) window.__lexSetVolumeBoostGlobal({currentBoost});");
                }
                catch { }
            }
        };

        // Al iniciar una navegación en el frame principal, descartar subframes obsoletos y desbloqueadores de la página previa
        core.NavigationStarting += (s, e) =>
        {
            tab.ActiveFrames.Clear();
            tab.CorsUnlocker?.Dispose();
            tab.CorsUnlocker = null;
        };

        // Detección nativa de reproducción de audio para suspender o priorizar memoria
        core.IsDocumentPlayingAudioChanged += (s, e) =>
        {
            var win = tab.OwnerWindow ?? this;
            win.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (tab.IsDisposed) return;
                bool isPlayingAudio = core.IsDocumentPlayingAudio;
                tab.HasActiveMedia = isPlayingAudio;
                if (!tab.IsSelected && (_pipWindow?.TargetTab != tab))
                {
                    try
                    {
                        core.MemoryUsageTargetLevel = isPlayingAudio
                            ? CoreWebView2MemoryUsageTargetLevel.Normal
                            : CoreWebView2MemoryUsageTargetLevel.Low;
                    }
                    catch { }
                }
            });
        };

        // 4. Escuchar mensajes del script inyectado (AdBlock, retorno PiP, atajos de teclado y estado multimedia)
        core.WebMessageReceived += (s, e) =>
        {
            if (tab.IsDisposed) return;
            var win = tab.OwnerWindow ?? this;
            try
            {
                string rawJson = e.WebMessageAsJson;
                if (string.IsNullOrEmpty(rawJson) || rawJson.Length > 4096) return;

                using var doc = JsonDocument.Parse(rawJson);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)) return;
                string type = typeProp.GetString() ?? "";

                if (type == "ad_blocked")
                {
                    win.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                    {
                        if (tab.IsDisposed) return;
                        tab.BlockedAdsCount++;
                        if (win._activeTab == tab)
                            win.UpdateShieldBadgeText();
                    });
                }
                else if (type == "shortcut_new_tab")
                {
                    bool incog = root.TryGetProperty("incognito", out var incProp) && incProp.GetBoolean();
                    win.Dispatcher.BeginInvoke(async () =>
                    {
                        int nextIdx = win._activeTab != null ? win.Tabs.IndexOf(win._activeTab) + 1 : win.Tabs.Count;
                        await win.AddNewTabAsync(null, isIncognito: incog, insertIndex: nextIdx);
                    });
                }
                else if (type == "shortcut_reopen_tab")
                {
                    win.Dispatcher.BeginInvoke(() => win.ReopenLastClosedTab());
                }
                else if (type == "shortcut_close_tab")
                {
                    win.Dispatcher.BeginInvoke(() =>
                    {
                        if (win._activeTab != null) win.CloseTab(win._activeTab);
                    });
                }
                else if (type == "pip_entered")
                {
                    int vw = root.TryGetProperty("videoWidth", out var vwProp) ? vwProp.GetInt32() : 16;
                    int vh = root.TryGetProperty("videoHeight", out var vhProp) ? vhProp.GetInt32() : 9;
                    string pipUrl = root.TryGetProperty("url", out var uProp) ? (uProp.GetString() ?? "") : "";
                    bool isVert = (vh > vw) || pipUrl.Contains("/shorts/", StringComparison.OrdinalIgnoreCase);

                    win.Dispatcher.BeginInvoke(() => win.StartNativePipMonitoring(isVert));
                }
                else if (type == "pip_leaving_now")
                {
                    // Capturar en 0ms la posición exacta del cursor sobre los botones del PiP antes de que el usuario mueva el mouse
                    NativeMethods.GetCursorPos(out win._pipExitCursorPos);
                    win._pipSessionId++;
                    win._canRecordUserPipResize = false;

                    if (win._lastNativePipVisibleRect.Width == 0)
                    {
                        IntPtr h = win._nativePipHwnd != IntPtr.Zero ? win._nativePipHwnd : win.FindNativePipWindow();
                        if (h != IntPtr.Zero) win.UpdateCachedNativePipBounds(h);
                    }

                    win.Dispatcher.BeginInvoke(() => win._nativePipTimer?.Stop());
                }
                else if (type == "pip_left")
                {
                    bool isPaused = root.TryGetProperty("isPaused", out var pProp) && pProp.GetBoolean();
                    double scale = win._lastNativePipDpi > 0 ? win._lastNativePipDpi / 96.0 : 1.25;
                    if (scale <= 0) scale = 1.25;

                    double rightDip = (win._lastNativePipVisibleRect.right - win._pipExitCursorPos.x) / scale;
                    double topDip = (win._pipExitCursorPos.y - win._lastNativePipVisibleRect.top) / scale;

                    // Los 3 botones superiores derechos en el PiP nativo de Chromium (VideoOverlayWindowViews):
                    //  - Cerrar (✕):    0 .. 36 DIP desde el borde derecho (además pausa el video: isPaused == true)
                    //  - Volver ([↗]): 36 .. 62.5 DIP desde el borde derecho (isPaused == false)
                    //  - Minimizar (─): >= 62.5 DIP desde el borde derecho (isPaused == false)
                    bool clickedMinimize = !isPaused && win._lastNativePipVisibleRect.Width > 0 && rightDip >= 62.5;
                    string actionTaken = isPaused ? "close_paused" : (clickedMinimize ? "minimize_keep_playing" : "return_to_tab");

                    LogPipEvent($"{{\"type\":\"pip_left\",\"action\":\"{actionTaken}\",\"isPaused\":{isPaused.ToString().ToLowerInvariant()},\"rightDip\":{rightDip:F1},\"topDip\":{topDip:F1},\"rectW\":{win._lastNativePipVisibleRect.Width},\"rectH\":{win._lastNativePipVisibleRect.Height}}}");

                    if (!isPaused && !clickedMinimize)
                    {
                        win.Dispatcher.Invoke(() =>
                        {
                            if (tab.IsDisposed) return;
                            if (win.WindowState == WindowState.Minimized)
                            {
                                win.WindowState = WindowState.Normal;
                            }
                            win.Topmost = true;
                            win.Topmost = false;
                            win.Activate();
                            win.Focus();
                            win.SelectTab(tab, force: true);

                            if (root.TryGetProperty("isFullScreen", out var fsProp) && fsProp.GetBoolean())
                            {
                                string restoreJson = JsonSerializer.Serialize(new { action = "restore_fullscreen" });
                                core.PostWebMessageAsJson(restoreJson);
                            }
                        });
                    }
                }
                else if (type == "pip_closed" || type == "pip_dismissed")
                {
                    LogPipEvent(e.WebMessageAsJson);
                }
                else if (type == "pip_return_requested")
                {
                    LogPipEvent(e.WebMessageAsJson);
                    win.Dispatcher.Invoke(() =>
                    {
                        if (tab.IsDisposed) return;
                        if (win.WindowState == WindowState.Minimized)
                        {
                            win.WindowState = WindowState.Normal;
                        }
                        win.Topmost = true;
                        win.Topmost = false;
                        win.Activate();
                        win.Focus();
                        win.SelectTab(tab, force: true);

                        if (root.TryGetProperty("isFullScreen", out var fsProp) && fsProp.GetBoolean())
                        {
                            string restoreJson = JsonSerializer.Serialize(new { action = "restore_fullscreen" });
                            core.PostWebMessageAsJson(restoreJson);
                        }
                    });
                }
                else if (type == "media_state")
                {
                    var data = root.GetProperty("data");
                    bool isPlaying = data.GetProperty("isPlaying").GetBoolean();
                    string title = data.GetProperty("title").GetString() ?? "";
                    double curTime = data.GetProperty("currentTime").GetDouble();
                    double duration = data.GetProperty("duration").GetDouble();

                    win.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                    {
                        if (tab.IsDisposed) return;
                        tab.HasActiveMedia = isPlaying || curTime > 0;
                        tab.MediaIsPlaying = isPlaying;
                        tab.MediaTitle = title;
                        tab.MediaCurrentTime = curTime;
                        tab.MediaDuration = duration;

                        if (win._pipWindow.IsVisible && win._pipWindow.TargetTab == tab)
                        {
                            win._pipWindow.UpdateMediaControls(title, curTime, duration, isPlaying);
                        }
                    });
                }
                else if (type == "cors_unlock_req")
                {
                    if (tab.VolumeBoostPercent <= 100) return;   // solo mientras el usuario amplifica
                    string origin = root.TryGetProperty("origin", out var oP) ? (oP.GetString() ?? "") : "";
                    win.Dispatcher.BeginInvoke(async () =>
                    {
                        if (tab.IsDisposed) return;
                        tab.CorsUnlocker ??= new CorsMediaUnlocker(core);
                        if (await tab.CorsUnlocker.AllowAsync(origin))
                            win.BroadcastToFrames(tab, $"window.__lexCorsUnlockAck && window.__lexCorsUnlockAck({JsonSerializer.Serialize(origin)});");
                    });
                }
                else if (type == "audio_status")
                {
                    string status = root.TryGetProperty("status", out var sP) ? (sP.GetString() ?? "") : "";
                    string? msg = status switch
                    {
                        "drm" => "🔒 Contenido con protección DRM: no se puede amplificar por encima de 100%.",
                        "cors_blocked" or "route_failed" => "🔇 Este reproductor no permite amplificar sobre 100%. Volumen devuelto a 100%.",
                        _ => null
                    };
                    if (msg != null && tab.VolumeBoostPercent > 100)
                    {
                        win.Dispatcher.BeginInvoke(() =>
                        {
                            if (tab.IsDisposed) return;
                            tab.VolumeBoostPercent = 100;
                            if (win._activeTab == tab) win.UpdateVolumeBoosterUI();
                            win.SendVolumeBoostToTab(tab);
                            win.ShowToast(msg);
                        });
                    }
                }
                else if (type == "open_history")
                {
                    win.Dispatcher.BeginInvoke(() => win.OpenHistory());
                }
                else if (type == "password_submitted")
                {
                    if (tab.IsIncognito || win._isGuestSession || !SettingsManager.Current.EnablePasswordManager)
                        return;

                    string submittedUrl = root.TryGetProperty("url", out var uProp) ? (uProp.GetString() ?? "") : "";
                    string submittedUser = root.TryGetProperty("username", out var usrProp) ? (usrProp.GetString() ?? "") : "";
                    string submittedPwd = root.TryGetProperty("password", out var pwdProp) ? (pwdProp.GetString() ?? "") : "";

                    if (!string.IsNullOrWhiteSpace(submittedUrl) && !string.IsNullOrEmpty(submittedPwd))
                    {
                        win.Dispatcher.BeginInvoke(() => win.PromptSaveOrUpdatePassword(submittedUrl, submittedUser, submittedPwd));
                    }
                }
                else if (type == "media_detected_batch")
                {
                    if (!tab.IsMediaSnifferActive) return;
                    if (root.TryGetProperty("items", out var itemsProp) && itemsProp.ValueKind == JsonValueKind.Array)
                    {
                        var pairs = new List<(string Url, string Category)>();
                        foreach (var el in itemsProp.EnumerateArray())
                        {
                            string u = el.TryGetProperty("url", out var uProp) ? (uProp.GetString() ?? "") : "";
                            string c = el.TryGetProperty("category", out var cProp) ? (cProp.GetString() ?? "") : "";
                            if (!string.IsNullOrWhiteSpace(u))
                            {
                                pairs.Add((u, c));
                            }
                        }
                        if (pairs.Count > 0)
                        {
                            win.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                            {
                                foreach (var (u, c) in pairs)
                                {
                                    win.RegisterDetectedMediaUrl(tab, u, c, contentType: null, contentLength: 0);
                                }
                            });
                        }
                    }
                }
            }
            catch { }
        };

        // 5. Detector Multimedia de Red Bajo Demanda (0 consumo cuando tab.IsMediaSnifferActive == false)
        core.WebResourceResponseReceived += (s, e) =>
        {
            if (!tab.IsMediaSnifferActive || tab.IsDisposed) return;
            var win = tab.OwnerWindow ?? this;
            win.InspectNetworkMediaResponse(tab, e);
        };

        // 6. Historial de Descargas Ligero (0% CPU en reposo; se dispara solo al iniciar/avanzar una descarga)
        core.DownloadStarting += (s, e) =>
        {
            try
            {
                var op = e.DownloadOperation;
                string resultPath = e.ResultFilePath ?? string.Empty;
                string fileName = Path.GetFileName(resultPath);
                long total = (long)(op.TotalBytesToReceive ?? 0UL);
                var entry = DownloadManager.RegisterDownload(fileName, resultPath, op.Uri, total, "En progreso", tab.IsIncognito);

                var win = tab.OwnerWindow ?? this;
                win.Dispatcher.Invoke(() =>
                {
                    win.RefreshDownloadsPopupUI();
                    win.PopupDownloads.IsOpen = true;
                    win.ShowToast($"⬇️ Descargando: {fileName}", "Detalles ⬇", () =>
                    {
                        win.RefreshDownloadsPopupUI();
                        win.PopupDownloads.IsOpen = true;
                    });
                });

                op.BytesReceivedChanged += (senderOp, args) =>
                {
                    long received = op.BytesReceived;
                    long tot = (long)(op.TotalBytesToReceive ?? 0UL);
                    DownloadManager.UpdateDownload(entry.Id, received, tot, "En progreso");
                };

                op.StateChanged += (senderOp, args) =>
                {
                    long received = op.BytesReceived;
                    long tot = (long)(op.TotalBytesToReceive ?? 0UL);
                    string stateStr = op.State switch
                    {
                        CoreWebView2DownloadState.Completed => "Completado",
                        CoreWebView2DownloadState.Interrupted => "Cancelado",
                        _ => "En progreso"
                    };
                    DownloadManager.UpdateDownload(entry.Id, received, tot, stateStr);

                    if (op.State == CoreWebView2DownloadState.Completed)
                    {
                        win.Dispatcher.Invoke(() =>
                        {
                            win.ShowToast($"✅ Descarga completada: {fileName}", "Ver archivo 📂", () =>
                            {
                                win.RefreshDownloadsPopupUI();
                                win.PopupDownloads.IsOpen = true;
                            });
                        });
                    }
                };
            }
            catch { }
        };

        // Escuchar cambios de zoom en el WebView para actualizar el indicador y widget flotante
        tab.WebView.ZoomFactorChanged += (s, e) =>
        {
            var win = tab.OwnerWindow ?? this;
            win.Dispatcher.Invoke(() =>
            {
                if (win._activeTab == tab)
                {
                    win.UpdateZoomIndicatorUI(showFloating: true);
                }
            });
        };

        core.NavigationStarting += (s, e) =>
        {
            if (tab.IsDisposed) return;
            var win = tab.OwnerWindow ?? this;

            win.Dispatcher.Invoke(() =>
            {
                if (tab.IsDisposed) return;
                tab.IsLoading = true;
                if (win._activeTab == tab) win.LoadingProgressBar.Visibility = Visibility.Visible;

                // Auto-apagado y liberación de RAM del detector multimedia al cambiar/recargar página (salvo que el usuario marque mantener activo)
                if (tab.SnifferSession != null && tab.SnifferSession.ConsumeExpectedReload())
                {
                    // Recarga única iniciada por el sniffer para capturar el player desde el inicio
                }
                else if (tab.IsMediaSnifferActive && !tab.KeepSnifferActiveOnNavigate)
                {
                    win.StopSnifferSessionForTab(tab);
                    tab.IsMediaSnifferActive = false;
                    tab.ClearDetectedMedia();
                    if (win._activeTab == tab) win.UpdateMediaSnifferUI();
                }
                else if (tab.IsMediaSnifferActive && tab.KeepSnifferActiveOnNavigate)
                {
                    tab.SnifferSession?.ResetForNewPage();
                    tab.ClearDetectedMedia();
                    if (win._activeTab == tab) win.UpdateMediaSnifferUI();
                }
            });
        };

        core.FaviconChanged += (s, e) =>
        {
            if (tab.IsDisposed) return;
            var win = tab.OwnerWindow ?? this;
            _ = win.UpdateTabFaviconAsync(tab);
        };

        core.NavigationCompleted += (s, e) =>
        {
            var win = tab.OwnerWindow ?? this;
            win.Dispatcher.Invoke(() =>
            {
                if (tab.IsDisposed) return;
                tab.IsLoading = false;
                tab.CanGoBack = core.CanGoBack;
                tab.CanGoForward = core.CanGoForward;
                if (win._activeTab == tab)
                {
                    win.LoadingProgressBar.Visibility = Visibility.Collapsed;
                    win.UpdateNavigationButtons();
                    win.UpdateCurrentTabBookmarkUI();
                    if (tab.IsNewTab) win.FocusOmniboxIfNewTab(tab);
                }

                // Re-aplicar amplificación de volumen si la pestaña tenía un nivel distinto de 100%
                if (tab.VolumeBoostPercent != 100)
                {
                    win.SendVolumeBoostToTab(tab);
                }

                // Si el usuario activó mantener el detector multimedia entre páginas, escanear la nueva página
                if (tab.IsMediaSnifferActive)
                {
                    if (tab.SnifferSession != null)
                    {
                        _ = tab.SnifferSession.RescanAsync();
                    }
                    win.RequestDomMediaScan(tab);
                }

                if (e.IsSuccess && !tab.IsIncognito && !tab.IsNewTab && !string.IsNullOrWhiteSpace(core.Source))
                {
                    HistoryManager.AddEntry(core.Source, core.DocumentTitle);
                    if (!win._isGuestSession && SettingsManager.Current.EnablePasswordManager)
                    {
                        win.TryAutofillSavedCredentials(tab, core.Source);
                    }
                }

                if (_pipWindow?.IsVisible == true && _pipWindow.TargetTab == tab)
                    win.SetMediaProgressReporting(tab, true);

                _ = win.UpdateTabFaviconAsync(tab);
                _ = win.UpdateTranslateIndicatorUIAsync(tab);
            });
        };

        core.SourceChanged += (s, e) =>
        {
            var win = tab.OwnerWindow ?? this;
            win.Dispatcher.Invoke(() =>
            {
                if (tab.IsDisposed) return;
                string prevUrl = tab.Url;
                if (core.Source.StartsWith("data:text/html") || core.Source == "about:blank")
                {
                    tab.Url = "";
                    tab.IsNewTab = true;
                }
                else
                {
                    tab.Url = core.Source;
                    tab.IsNewTab = false;
                }

                if (tab.IsMediaSnifferActive && !string.Equals(prevUrl, tab.Url, StringComparison.OrdinalIgnoreCase))
                {
                    string? prevYtId = MediaExtractorService.TryExtractYouTubeVideoId(prevUrl);
                    string? newYtId = MediaExtractorService.TryExtractYouTubeVideoId(tab.Url);

                    if (tab.KeepSnifferActiveOnNavigate || (prevYtId == null && newYtId != null))
                    {
                        tab.ClearDetectedMedia();
                        win.RequestDomMediaScan(tab);
                    }
                    else if (prevYtId != newYtId)
                    {
                        tab.IsMediaSnifferActive = false;
                        tab.ClearDetectedMedia();
                    }
                }

                if (win._activeTab == tab)
                {
                    win._isUpdatingOmniboxTextProgrammatically = true;
                    win.TxtUrl.Text = tab.Url;
                    win._isUpdatingOmniboxTextProgrammatically = false;
                    win.PopupOmniboxSuggestions.IsOpen = false;
                    win.UpdateSecurityIndicatorUI();
                    win.UpdateCurrentTabBookmarkUI();
                    win.UpdateMediaSnifferUI();
                }
            });
        };

        core.DocumentTitleChanged += (s, e) =>
        {
            var win = tab.OwnerWindow ?? this;
            win.Dispatcher.Invoke(() =>
            {
                if (tab.IsDisposed) return;
                if (tab.IsNewTab)
                {
                    tab.Title = tab.IsIncognito ? "Incógnito" : "Nueva pestaña";
                }
                else
                {
                    tab.Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "Nueva pestaña" : core.DocumentTitle;
                    if (!tab.IsIncognito && !string.IsNullOrWhiteSpace(core.Source))
                    {
                        HistoryManager.UpdateTitle(core.Source, tab.Title);
                    }
                }

                if (win._activeTab == tab)
                {
                    win.UpdateCurrentTabBookmarkUI();
                }
            });
        };
    }

    private async Task UpdateTabFaviconAsync(BrowserTab tab)
    {
        if (tab?.WebView?.CoreWebView2 == null || tab.IsDisposed) return;
        try
        {
            if (tab.IsNewTab || string.IsNullOrWhiteSpace(tab.Url) || tab.Url == "about:blank")
            {
                Dispatcher.Invoke(() => tab.FaviconImage = null);
                return;
            }

            Stream? stream = null;
            try
            {
                stream = await tab.WebView.CoreWebView2.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            }
            catch { }

            string faviconUri = "";
            try
            {
                faviconUri = tab.WebView.CoreWebView2.FaviconUri ?? "";
            }
            catch { }

            var icon = await FaviconService.ResolveFaviconAsync(tab.Url, stream, faviconUri, tab.IsIncognito);
            if (stream != null)
            {
                try { stream.Dispose(); } catch { }
            }

            if (icon != null && !tab.IsDisposed)
            {
                Dispatcher.Invoke(() =>
                {
                    if (!tab.IsDisposed)
                    {
                        tab.FaviconImage = icon;
                    }
                });
            }
        }
        catch
        {
            // Fallback silencioso sin romper la navegación
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // SELECCIONAR PESTAÑA Y MINI-REPRODUCTOR LIBRE AUTOMÁTICO
    // ─────────────────────────────────────────────────────────────────────
    private void SelectTab(BrowserTab? tab, bool force = false)
    {
        if (tab == null || tab.IsDisposed) return;
        if (!force && _activeTab == tab) return;

        try
        {
            var previousTab = _activeTab;

            // Mini-reproductor automático universal (si está activado en Configuración):
            if (SettingsManager.Current.EnableAutoVideoPip && previousTab != null && previousTab != tab && !previousTab.IsDisposed)
            {
                try
                {
                    if (previousTab.WebView?.CoreWebView2 != null)
                    {
                        _ = TriggerAutoPiPAsync(previousTab);
                    }
                }
                catch { }
            }

            // Al volver a la pestaña, salir de PiP y restaurar estado original (ej. pantalla completa)
            try
            {
                if (tab.WebView?.CoreWebView2 != null)
                {
                    _ = TriggerExitPiPAsync(tab);
                }
            }
            catch { }

            // Si la pestaña seleccionada estaba en la ventana de Página Flotante manual, cerrarla y reinsertarla
            if (_pipWindow?.IsVisible == true && _pipWindow.TargetTab == tab)
            {
                _pipWindow.HideAndDetach();
                ReinsertWebViewToContainer(tab);
            }

            foreach (var t in Tabs)
            {
                bool isCurrent = (t == tab);
                t.IsSelected = isCurrent;
                if (t.WebView != null && !t.IsDisposed)
                {
                    // Si esta pestaña está actualmente flotando en _pipWindow, debe seguir Visible para no quedar negra
                    bool isFloating = _pipWindow?.IsVisible == true && _pipWindow.TargetTab == t;
                    t.WebView.Visibility = (isCurrent || isFloating) ? Visibility.Visible : Visibility.Collapsed;

                    // Optimización activa de memoria RAM: podar working set en pestañas ocultas
                    // CONDICIÓN CRÍTICA: Nunca degradar memoria si la pestaña está reproduciendo audio o está en ventana flotante
                    try
                    {
                        if (t.WebView.CoreWebView2 != null)
                        {
                            if (!isCurrent && !isFloating)
                            {
                                bool isPlayingAudio = t.WebView.CoreWebView2.IsDocumentPlayingAudio;
                                t.WebView.CoreWebView2.MemoryUsageTargetLevel = isPlayingAudio
                                    ? CoreWebView2MemoryUsageTargetLevel.Normal
                                    : CoreWebView2MemoryUsageTargetLevel.Low;
                            }
                            else if (isCurrent)
                            {
                                t.WebView.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
                            }
                        }
                    }
                    catch { }
                }
            }

            _activeTab = tab;

            if (tab?.WebView != null)
            {
                // Asegurar que el WebView esté montado en BrowserContainer sin remover las demás
                if (!BrowserContainer.Children.Contains(tab.WebView))
                {
                    if (tab.WebView.Parent is Panel existingParent)
                    {
                        existingParent.Children.Remove(tab.WebView);
                    }
                    BrowserContainer.Children.Add(tab.WebView);
                }

                tab.WebView.Visibility = Visibility.Visible;
            }

            if (tab != null && tab.IsLazyLoaded && !string.IsNullOrWhiteSpace(tab.Url))
            {
                tab.IsLazyLoaded = false;
                if (tab.WebView?.CoreWebView2 != null)
                {
                    tab.WebView.CoreWebView2.Navigate(tab.Url);
                }
            }

            if (tab != null)
            {
                _isUpdatingOmniboxTextProgrammatically = true;
                TxtUrl.Text = tab.IsNewTab ? "" : tab.Url;
                _isUpdatingOmniboxTextProgrammatically = false;
                PopupOmniboxSuggestions.IsOpen = false;
                UpdateShieldBadgeText();
                LoadingProgressBar.Visibility = tab.IsLoading ? Visibility.Visible : Visibility.Collapsed;
            }
            UpdateNavigationButtons();
            UpdateSecurityIndicatorUI();
            UpdateCurrentTabBookmarkUI();
            UpdateZoomIndicatorUI(showFloating: false);
            UpdateVolumeBoosterUI();
            UpdateMediaSnifferUI();
            _ = UpdateTranslateIndicatorUIAsync(tab);
            FocusOmniboxIfNewTab(tab);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SelectTab] Error: {ex.Message}");
        }
    }

    private void FocusOmniboxIfNewTab(BrowserTab? tab)
    {
        if (tab == null || tab.IsDisposed || !tab.IsNewTab || _activeTab != tab) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
        {
            if (tab.IsDisposed || _activeTab != tab || !tab.IsNewTab) return;
            TxtUrl.Focus();
            Keyboard.Focus(TxtUrl);
            TxtUrl.CaretIndex = TxtUrl.Text.Length;
            TxtUrl.SelectAll();
        });
    }

    private void InitializeTranslateLanguageList()
    {
        var languages = new List<TranslateLanguageItem>
        {
            // Idiomas principales de acceso rápido
            new("es", "⭐ Español"),
            new("en", "⭐ Inglés (English)"),
            new("fr", "⭐ Francés (Français)"),
            new("pt", "⭐ Portugués (Português)"),
            new("de", "⭐ Alemán (Deutsch)"),
            new("it", "⭐ Italiano"),
            new("ja", "⭐ Japonés (日本語)"),
            new("zh-CN", "⭐ Chino Simplificado (简体中文)"),
            new("zh-TW", "⭐ Chino Tradicional (繁體中文)"),
            new("ru", "⭐ Ruso (Русский)"),
            new("ko", "⭐ Coreano (한국어)"),
            new("ar", "⭐ Árabe (العربية)"),
            new("hi", "⭐ Hindi (हिन्दी)"),
            new("nl", "⭐ Holandés (Nederlands)"),

            // Lista completa de idiomas mundiales (orden alfabético en español)
            new("af", "Afrikáans"),
            new("sq", "Albanés"),
            new("am", "Amárico"),
            new("hy", "Armenio"),
            new("as", "Asamés"),
            new("ay", "Aymara"),
            new("az", "Azerbaiyano"),
            new("bm", "Bambara"),
            new("bn", "Bengalí"),
            new("bho", "Bhojpuri"),
            new("be", "Bielorruso"),
            new("my", "Birmano"),
            new("bs", "Bosnio"),
            new("bg", "Búlgaro"),
            new("ca", "Catalán"),
            new("ceb", "Cebuano"),
            new("cs", "Checo"),
            new("ny", "Chichewa"),
            new("si", "Cingalés"),
            new("co", "Corso"),
            new("hr", "Croata"),
            new("da", "Danés"),
            new("dv", "Divehi"),
            new("doi", "Dogri"),
            new("sk", "Eslovaco"),
            new("sl", "Esloveno"),
            new("eo", "Esperanto"),
            new("et", "Estonio"),
            new("eu", "Euskera (Vasco)"),
            new("ee", "Ewe"),
            new("tl", "Filipino (Tagalo)"),
            new("fi", "Finés"),
            new("fy", "Frisón"),
            new("gd", "Gaélico escocés"),
            new("cy", "Galés"),
            new("gl", "Gallego"),
            new("ka", "Georgiano"),
            new("el", "Griego"),
            new("gn", "Guaraní"),
            new("gu", "Guyaratí"),
            new("ha", "Hausa"),
            new("haw", "Hawaiano"),
            new("he", "Hebreo"),
            new("hmn", "Hmong"),
            new("hu", "Húngaro"),
            new("ig", "Igbo"),
            new("ilo", "Ilocano"),
            new("id", "Indonesio"),
            new("ga", "Irlandés"),
            new("is", "Islandés"),
            new("jv", "Javanés"),
            new("km", "Jemer (Camboyano)"),
            new("kn", "Kannada"),
            new("kk", "Kazajo"),
            new("rw", "Kinyarwanda"),
            new("ky", "Kirguís"),
            new("gom", "Konkani"),
            new("kri", "Krio"),
            new("ku", "Kurdo (Kurmanji)"),
            new("ckb", "Kurdo (Sorani)"),
            new("lo", "Lao"),
            new("la", "Latín"),
            new("lv", "Letón"),
            new("ln", "Lingala"),
            new("lt", "Lituano"),
            new("lg", "Luganda"),
            new("lb", "Luxemburgués"),
            new("mk", "Macedonio"),
            new("mai", "Maithili"),
            new("mg", "Malgache"),
            new("ms", "Malayo"),
            new("ml", "Malayalam"),
            new("mt", "Maltés"),
            new("mi", "Maorí"),
            new("mr", "Maratí"),
            new("mni-Mtei", "Meiteilon (Manipuri)"),
            new("mzo", "Mizo"),
            new("mn", "Mongol"),
            new("ne", "Nepalí"),
            new("no", "Noruego"),
            new("om", "Oromo"),
            new("pa", "Panyabí"),
            new("ps", "Pashto"),
            new("fa", "Persa"),
            new("pl", "Polaco"),
            new("qu", "Quechua"),
            new("ro", "Rumano"),
            new("sm", "Samoano"),
            new("sa", "Sánscrito"),
            new("nso", "Sepedi"),
            new("sr", "Serbio"),
            new("st", "Sesotho"),
            new("sn", "Shona"),
            new("sd", "Sindhi"),
            new("so", "Somalí"),
            new("sw", "Suajili"),
            new("sv", "Sueco"),
            new("su", "Sundanés"),
            new("tg", "Tayiko"),
            new("ta", "Tamil"),
            new("tt", "Tártaro"),
            new("te", "Telugu"),
            new("th", "Tailandés"),
            new("ti", "Tigriña"),
            new("ts", "Tsonga"),
            new("tr", "Turco"),
            new("tk", "Turcomano"),
            new("ak", "Twi"),
            new("uk", "Ucraniano"),
            new("ug", "Uigur"),
            new("ur", "Urdu"),
            new("uz", "Uzbeko"),
            new("vi", "Vietnamita"),
            new("xh", "Xhosa"),
            new("yi", "Yidis"),
            new("yo", "Yoruba"),
            new("zu", "Zulú")
        };
        CmbTargetLanguage.ItemsSource = languages;
        CmbTargetLanguage.SelectedValue = "es";
    }

    private async Task UpdateTranslateIndicatorUIAsync(BrowserTab? tab)
    {
        if (tab == null || tab.IsDisposed || tab.IsNewTab || tab != _activeTab || tab.WebView?.CoreWebView2 == null)
        {
            PillTranslateIndicator.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            bool isTranslated = await TranslationService.IsPageTranslatedAsync(tab.WebView.CoreWebView2);
            if (isTranslated)
            {
                PillTranslateIndicator.Visibility = Visibility.Visible;
                TxtTranslateAction.Text = "Original";
                PillTranslateIndicator.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E3A8A"));
                PillTranslateIndicator.ToolTip = "Página traducida (clic para cambiar idioma o restaurar)";
                return;
            }

            string lang = await TranslationService.DetectPageLanguageAsync(tab.WebView.CoreWebView2);
            bool isForeign = !string.IsNullOrEmpty(lang) && !lang.StartsWith("es", StringComparison.OrdinalIgnoreCase);
            PillTranslateIndicator.Visibility = isForeign ? Visibility.Visible : Visibility.Collapsed;
            TxtTranslateAction.Text = "Traducir";
            PillTranslateIndicator.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E293B"));
            PillTranslateIndicator.ToolTip = string.IsNullOrEmpty(lang) ? "Traducir esta página" : $"Traducir página ({lang.ToUpperInvariant()})";
        }
        catch
        {
            PillTranslateIndicator.Visibility = Visibility.Collapsed;
        }
    }

    private void PillTranslate_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab?.WebView?.CoreWebView2 == null || _activeTab.IsDisposed) return;
        PopupTranslate.IsOpen = !PopupTranslate.IsOpen;
    }

    private void BtnCloseTranslatePopup_Click(object sender, RoutedEventArgs e)
    {
        PopupTranslate.IsOpen = false;
    }

    private async void BtnDoTranslate_Click(object sender, RoutedEventArgs e)
    {
        PopupTranslate.IsOpen = false;
        if (_activeTab?.WebView?.CoreWebView2 == null || _activeTab.IsDisposed) return;

        string targetLang = CmbTargetLanguage.SelectedValue as string ?? "es";
        bool translated = await TranslationService.TranslatePageAsync(_activeTab.WebView.CoreWebView2, targetLang);
        if (translated)
        {
            TxtTranslateAction.Text = "Original";
            PillTranslateIndicator.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E3A8A"));
            ShowToast($"🌐 Traduciendo página a {targetLang.ToUpperInvariant()}... Clic para opciones.");
        }
    }

    private async void BtnRestoreTranslate_Click(object sender, RoutedEventArgs e)
    {
        PopupTranslate.IsOpen = false;
        if (_activeTab?.WebView?.CoreWebView2 == null || _activeTab.IsDisposed) return;

        await TranslationService.RestoreOriginalAsync(_activeTab.WebView.CoreWebView2);
        TxtTranslateAction.Text = "Traducir";
        PillTranslateIndicator.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E293B"));
        ShowToast("Página restaurada al idioma original.");
    }

    private void UpdateNavigationButtons()
    {
        if (_activeTab?.WebView?.CoreWebView2 != null)
        {
            BtnBack.IsEnabled = _activeTab.WebView.CoreWebView2.CanGoBack;
            BtnForward.IsEnabled = _activeTab.WebView.CoreWebView2.CanGoForward;
        }
    }

    private async Task TriggerAutoPiPAsync(BrowserTab tab)
    {
        if (tab?.WebView?.CoreWebView2 == null) return;
        try
        {
            // Ejecutar con CDP Runtime.evaluate y userGesture: true
            // Esto otorga 'User Activation' a nivel de Chromium,
            // garantizando que video.requestPictureInPicture() NUNCA falle por falta de interacción.
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
    // ARRASTRAR Y DESPLAZAR PESTAÑAS VISUALMENTE (LIVE SMOOTH DRAG & REORDER)
    // ─────────────────────────────────────────────────────────────────────
    private void Tab_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (e.OriginalSource is DependencyObject dep && FindVisualParent<Button>(dep) != null)
            {
                return;
            }

            if (sender is FrameworkElement el && el.DataContext is BrowserTab tab)
            {
                _draggedTab = tab;
                _draggedBorder = el as Border;
                _tabDragStartPoint = e.GetPosition(this);
                _isDraggingTab = false;
                SelectTab(tab);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TabDrag] Error in LeftButtonDown: {ex.Message}");
        }
    }

    private void Tab_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        try
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                if (_isDraggingTab) EndTabDrag();
                return;
            }

            if (_draggedTab == null || _draggedBorder == null) return;

            Point currentPos = e.GetPosition(this);
            double deltaX = currentPos.X - _tabDragStartPoint.X;
            double deltaY = currentPos.Y - _tabDragStartPoint.Y;

            if (!_isDraggingTab)
            {
                if (Math.Abs(deltaX) > 6 || Math.Abs(deltaY) > 8)
                {
                    _isDraggingTab = true;
                    _draggedBorder.CaptureMouse();

                    Panel.SetZIndex(_draggedBorder, 999);
                    _draggedBorder.Opacity = 0.9;
                    _draggedBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x81, 0x8C, 0xF8));
                    _draggedBorder.BorderThickness = new Thickness(1.5);
                    _draggedBorder.Effect = new DropShadowEffect
                    {
                        BlurRadius = 14,
                        ShadowDepth = 4,
                        Direction = 270,
                        Opacity = 0.6,
                        Color = Colors.Black
                    };
                }
                else
                {
                    return;
                }
            }

            if (_isDraggingTab)
            {
                bool isOutsideTabBar = Math.Abs(deltaY) > 42 || currentPos.X < -30 || currentPos.X > ActualWidth + 30;
                if (isOutsideTabBar)
                {
                    // Indicador visual de desprendimiento (Tab Tear-Off hacia nueva ventana u otra ventana)
                    _draggedBorder.Opacity = 0.65;
                    _draggedBorder.RenderTransform = new TranslateTransform(deltaX, deltaY);
                    return;
                }

                _draggedBorder.Opacity = 0.9;

                // 1. Desplazar visualmente la pestaña arrastrada siguiendo el ratón píxel a píxel
                _draggedBorder.RenderTransform = new TranslateTransform(deltaX, 0);

                int currentIndex = Tabs.IndexOf(_draggedTab);
                if (currentIndex < 0) return;

                double currentWidth = _draggedBorder.ActualWidth > 0 ? _draggedBorder.ActualWidth : 120;

                // 2. Comprobar si cruza hacia la derecha (permite reordenar pestañas ancladas y normales libremente)
                if (deltaX > 15)
                {
                    int nextIndex = currentIndex + 1;
                    if (nextIndex < Tabs.Count)
                    {
                        double neighborWidth = currentWidth;
                        var container = TabsItemsControl.ItemContainerGenerator.ContainerFromIndex(nextIndex) as FrameworkElement;
                        if (container != null && container.ActualWidth > 0)
                        {
                            neighborWidth = container.ActualWidth;
                        }

                        double swapThreshold = (currentWidth + neighborWidth) * 0.40;
                        if (deltaX > swapThreshold)
                        {
                            Tabs.Move(currentIndex, nextIndex);
                            _tabDragStartPoint = new Point(_tabDragStartPoint.X + neighborWidth, _tabDragStartPoint.Y);
                            deltaX = currentPos.X - _tabDragStartPoint.X;
                            _draggedBorder.RenderTransform = new TranslateTransform(deltaX, 0);
                        }
                    }
                }
                // 3. Comprobar si cruza hacia la izquierda
                else if (deltaX < -15)
                {
                    int prevIndex = currentIndex - 1;
                    if (prevIndex >= 0)
                    {
                        double neighborWidth = currentWidth;
                        var container = TabsItemsControl.ItemContainerGenerator.ContainerFromIndex(prevIndex) as FrameworkElement;
                        if (container != null && container.ActualWidth > 0)
                        {
                            neighborWidth = container.ActualWidth;
                        }

                        double swapThreshold = -(currentWidth + neighborWidth) * 0.40;
                        if (deltaX < swapThreshold)
                        {
                            Tabs.Move(currentIndex, prevIndex);
                            _tabDragStartPoint = new Point(_tabDragStartPoint.X - neighborWidth, _tabDragStartPoint.Y);
                            deltaX = currentPos.X - _tabDragStartPoint.X;
                            _draggedBorder.RenderTransform = new TranslateTransform(deltaX, 0);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TabDrag] Error in MouseMove: {ex.Message}");
            EndTabDrag();
        }
    }

    private void Tab_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndTabDrag();
    }

    private void Window_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndTabDrag();
    }

    private void EndTabDrag()
    {
        BrowserTab? tabToTearOff = null;
        MainWindow? targetDockWindow = null;

        try
        {
            if (_isDraggingTab && _draggedTab != null)
            {
                Point releasePos = Mouse.GetPosition(this);
                double deltaY = releasePos.Y - _tabDragStartPoint.Y;
                bool isOutsideTabBar = Math.Abs(deltaY) > 42 || releasePos.X < -30 || releasePos.X > ActualWidth + 30;

                if (isOutsideTabBar)
                {
                    targetDockWindow = TryFindTargetWindowAtCursor();
                    if (targetDockWindow != null || Tabs.Count > 1)
                    {
                        tabToTearOff = _draggedTab;
                    }
                }
            }

            if (_draggedBorder != null)
            {
                if (_draggedBorder.IsMouseCaptured)
                {
                    _draggedBorder.ReleaseMouseCapture();
                }
                _draggedBorder.RenderTransform = null;
                _draggedBorder.Opacity = 1.0;
                _draggedBorder.ClearValue(Border.BorderBrushProperty);
                _draggedBorder.ClearValue(Border.BorderThicknessProperty);
                _draggedBorder.Effect = null;
                Panel.SetZIndex(_draggedBorder, 0);
            }

            // Limpieza preventiva en todos los contenedores de pestañas
            for (int i = 0; i < Tabs.Count; i++)
            {
                var cp = TabsItemsControl.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
                if (cp != null)
                {
                    cp.RenderTransform = null;
                    Panel.SetZIndex(cp, 0);
                }
            }

            if (tabToTearOff == null && _draggedTab != null)
            {
                SelectTab(_draggedTab);
            }
        }
        catch { }
        finally
        {
            _isDraggingTab = false;
            _draggedBorder = null;
            _draggedTab = null;
        }

        if (tabToTearOff != null)
        {
            if (targetDockWindow != null)
            {
                MoveTabToWindow(tabToTearOff, targetDockWindow);
            }
            else if (Tabs.Count > 1)
            {
                DetachTabToNewWindow(tabToTearOff, useCursorPosition: true);
            }
        }
    }

    private MainWindow? TryFindTargetWindowAtCursor()
    {
        if (Application.Current == null) return null;
        if (!NativeMethods.GetCursorPos(out var screenPt)) return null;

        foreach (Window w in Application.Current.Windows)
        {
            if (w is MainWindow other && other != this && other.IsVisible && other.WindowState != WindowState.Minimized)
            {
                try
                {
                    Point relPt = other.PointFromScreen(new Point(screenPt.x, screenPt.y));
                    if (relPt.X >= 0 && relPt.X <= other.ActualWidth && relPt.Y >= -12 && relPt.Y <= 86)
                    {
                        return other;
                    }
                }
                catch { }
            }
        }
        return null;
    }

    private void MoveTabToWindow(BrowserTab tab, MainWindow targetWindow)
    {
        if (tab == null || tab.IsDisposed || targetWindow == null || targetWindow == this) return;

        int index = Tabs.IndexOf(tab);
        bool wasActive = (_activeTab == tab);

        if (_pipWindow.TargetTab == tab)
        {
            _pipWindow.HideAndDetach();
        }

        Tabs.Remove(tab);
        if (BrowserContainer.Children.Contains(tab.WebView))
        {
            BrowserContainer.Children.Remove(tab.WebView);
        }

        if (Tabs.Count == 0)
        {
            _activeTab = null;
            targetWindow.AttachExistingTab(tab);
            targetWindow.Activate();
            targetWindow.Focus();
            Close();
            return;
        }
        else if (wasActive)
        {
            _activeTab = null;
            int nextIndex = Math.Clamp(index, 0, Tabs.Count - 1);
            SelectTab(Tabs[nextIndex], force: true);
        }

        targetWindow.AttachExistingTab(tab);
        targetWindow.Activate();
        targetWindow.Focus();
    }

    private void DetachTabToNewWindow(BrowserTab tab, bool useCursorPosition = false)
    {
        if (tab == null || tab.IsDisposed || Tabs.Count <= 1) return;

        int index = Tabs.IndexOf(tab);
        bool wasActive = (_activeTab == tab);

        if (_pipWindow.TargetTab == tab)
        {
            _pipWindow.HideAndDetach();
        }

        Tabs.Remove(tab);
        if (BrowserContainer.Children.Contains(tab.WebView))
        {
            BrowserContainer.Children.Remove(tab.WebView);
        }

        if (wasActive && Tabs.Count > 0)
        {
            _activeTab = null;
            int nextIndex = Math.Clamp(index, 0, Tabs.Count - 1);
            SelectTab(Tabs[nextIndex], force: true);
        }

        var newWin = new MainWindow(tab)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Width = ActualWidth > 400 ? ActualWidth : 1280,
            Height = ActualHeight > 300 ? ActualHeight : 800
        };

        if (useCursorPosition && NativeMethods.GetCursorPos(out var pt))
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
            double scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
            newWin.Left = (pt.x / scaleX) - 140;
            newWin.Top = (pt.y / scaleY) - 18;
        }
        else
        {
            newWin.Left = (WindowState == WindowState.Maximized ? 80 : Left + 36);
            newWin.Top = (WindowState == WindowState.Maximized ? 60 : Top + 36);
        }

        newWin.Show();
        newWin.Activate();
        newWin.Focus();
    }

    // ─────────────────────────────────────────────────────────────────────
    // FIJADO DE PESTAÑAS (SOLO LOGO, LOGO Y NOMBRE, DESFIJAR)
    // ─────────────────────────────────────────────────────────────────────
    private void MenuPinLogoOnly_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetTabFromMenuItem(sender);
        if (tab == null) return;

        tab.PinMode = TabPinMode.LogoOnly;
        MoveToPinnedSection(tab);
    }

    private void MenuPinLogoAndName_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetTabFromMenuItem(sender);
        if (tab == null) return;

        tab.PinMode = TabPinMode.LogoAndName;
        MoveToPinnedSection(tab);
    }

    private void MenuUnpin_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetTabFromMenuItem(sender);
        if (tab == null) return;

        tab.PinMode = TabPinMode.None;

        // Mover inmediatamente después de todas las fijadas
        int oldIndex = Tabs.IndexOf(tab);
        int targetIndex = Tabs.Count(t => t.IsPinned);
        if (oldIndex != targetIndex && oldIndex >= 0 && targetIndex < Tabs.Count)
        {
            Tabs.Move(oldIndex, targetIndex);
        }
    }

    private void MoveToPinnedSection(BrowserTab tab)
    {
        int oldIndex = Tabs.IndexOf(tab);
        // Colocar automáticamente al inicio (o al final de las fijadas)
        int targetIndex = Tabs.Count(t => t.IsPinned && t != tab);
        if (oldIndex != targetIndex && oldIndex >= 0)
        {
            Tabs.Move(oldIndex, targetIndex);
        }
        SelectTab(tab);
    }

    // ─────────────────────────────────────────────────────────────────────
    // BARRA DE ACCESOS DIRECTOS PRO (BOOKMARKS BAR)
    // ─────────────────────────────────────────────────────────────────────
    private void UpdateCurrentTabBookmarkUI()
    {
        if (_activeTab == null || string.IsNullOrWhiteSpace(_activeTab.Url) || _activeTab.IsNewTab)
        {
            BorderCurrentTabBookmark.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x3A));
            BorderCurrentTabBookmark.Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x22));
            TxtCurrentTabBookmarkIcon.Text = "＋";
            TxtCurrentTabBookmarkIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x71, 0x71, 0x7A));
            TxtCurrentTabBookmarkTitle.Text = "Anclar página";
            return;
        }

        bool isBookmarked = BookmarkManager.IsBookmarked(_activeTab.Url);

        if (isBookmarked)
        {
            BorderCurrentTabBookmark.BorderBrush = (SolidColorBrush)FindResource("AccentColor");
            BorderCurrentTabBookmark.Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x38));
            TxtCurrentTabBookmarkIcon.Text = "★";
            TxtCurrentTabBookmarkIcon.Foreground = (SolidColorBrush)FindResource("AccentHover");
            TxtCurrentTabBookmarkTitle.Text = string.IsNullOrWhiteSpace(_activeTab.Title) ? "Anclado" : _activeTab.Title;
        }
        else
        {
            BorderCurrentTabBookmark.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x3A));
            BorderCurrentTabBookmark.Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x22));
            TxtCurrentTabBookmarkIcon.Text = "＋";
            TxtCurrentTabBookmarkIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xAA));
            TxtCurrentTabBookmarkTitle.Text = string.IsNullOrWhiteSpace(_activeTab.Title) ? "Anclar página" : $"Anclar {_activeTab.Title}";
        }
    }

    private void CurrentTabBookmark_Click(object sender, MouseButtonEventArgs e)
    {
        if (_activeTab == null || string.IsNullOrWhiteSpace(_activeTab.Url) || _activeTab.IsNewTab)
            return;

        bool nowBookmarked = BookmarkManager.ToggleBookmark(_activeTab.Title, _activeTab.Url);
        UpdateCurrentTabBookmarkUI();

        if (!nowBookmarked)
        {
            ShowUndoBanner();
        }
    }

    private static bool IsSafeHttpUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private void UpdateSecurityIndicatorUI()
    {
        if (TxtSecurityIcon == null || BadgeGoogleOmnibox == null) return;
        string url = _activeTab?.Url ?? "";
        bool isTypingOrNewTab = _activeTab == null || _activeTab.IsNewTab || string.IsNullOrWhiteSpace(url) || (TxtUrl != null && TxtUrl.IsKeyboardFocusWithin);

        if (isTypingOrNewTab)
        {
            BadgeGoogleOmnibox.Visibility = Visibility.Visible;
            TxtSecurityIcon.Visibility = Visibility.Collapsed;
        }
        else if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            BadgeGoogleOmnibox.Visibility = Visibility.Collapsed;
            TxtSecurityIcon.Visibility = Visibility.Visible;
            TxtSecurityIcon.Text = "🔒";
            TxtSecurityIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
            TxtSecurityIcon.ToolTip = "Conexión cifrada segura (HTTPS)";
        }
        else
        {
            BadgeGoogleOmnibox.Visibility = Visibility.Collapsed;
            TxtSecurityIcon.Visibility = Visibility.Visible;
            TxtSecurityIcon.Text = "⚠️";
            TxtSecurityIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
            TxtSecurityIcon.ToolTip = "Conexión no cifrada (HTTP)";
        }
    }

    private void BookmarkItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is BookmarkItem item)
        {
            if (!IsSafeHttpUrl(item.Url)) return;
            if (_activeTab?.WebView != null)
            {
                if (_activeTab.WebView.CoreWebView2 != null)
                {
                    _activeTab.WebView.CoreWebView2.Navigate(item.Url);
                }
                else if (Uri.TryCreate(item.Url, UriKind.Absolute, out var navUri))
                {
                    _activeTab.WebView.Source = navUri;
                }
            }
        }
    }

    private void BtnRemoveBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is BookmarkItem item)
        {
            BookmarkManager.Remove(item);
            UpdateCurrentTabBookmarkUI();
            ShowUndoBanner();
        }
    }

    private void ShowUndoBanner()
    {
        UndoBanner.Visibility = Visibility.Visible;
        _undoBookmarkTimer.Stop();
        _undoBookmarkTimer.Start();
    }

    private void BtnUndoBookmark_Click(object sender, RoutedEventArgs e)
    {
        BookmarkManager.UndoRemove();
        _undoBookmarkTimer.Stop();
        UndoBanner.Visibility = Visibility.Collapsed;
        UpdateCurrentTabBookmarkUI();
    }

    private void BtnCloseUndoBanner_Click(object sender, RoutedEventArgs e)
    {
        _undoBookmarkTimer.Stop();
        UndoBanner.Visibility = Visibility.Collapsed;
    }

    private void BtnHideBookmarksBar_Click(object sender, RoutedEventArgs e)
    {
        SettingsManager.Current.ShowBookmarksBar = false;
        SettingsManager.Save();
        ShowToast("Barra de accesos directos oculta. Para volver a activarla ve a Configuración (o presiona Ctrl+Shift+B)");
    }

    private Action? _toastActionCallback;

    private void ShowToast(string message, string? actionLabel = null, Action? onAction = null)
    {
        if (TxtToastMessage == null || PopupToast == null || ToastNotification == null) return;

        TxtToastMessage.Text = message;
        _toastActionCallback = onAction;

        if (!string.IsNullOrWhiteSpace(actionLabel) && BtnToastAction != null)
        {
            BtnToastAction.Content = actionLabel;
            BtnToastAction.Visibility = Visibility.Visible;
        }
        else if (BtnToastAction != null)
        {
            BtnToastAction.Visibility = Visibility.Collapsed;
        }

        ToastNotification.Visibility = Visibility.Visible;
        PopupToast.IsOpen = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void ToastAction_Click(object sender, RoutedEventArgs e)
    {
        var cb = _toastActionCallback;
        HideToast();
        cb?.Invoke();
    }

    private void ToastGoSettings_Click(object sender, RoutedEventArgs e)
    {
        HideToast();
        OpenSettings("Bookmarks");
    }

    private void ToastClose_Click(object sender, RoutedEventArgs e)
    {
        HideToast();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        if (PopupToast != null) PopupToast.IsOpen = false;
        if (ToastNotification != null) ToastNotification.Visibility = Visibility.Collapsed;
        _toastActionCallback = null;
    }

    // ─────────────────────────────────────────────────────────────────────
    // BARRA DE URL / OMNIBOX INTELIGENTE (SUGERENCIAS EN VIVO + HISTORIAL)
    // ─────────────────────────────────────────────────────────────────────
    private bool _isUpdatingOmniboxTextProgrammatically = false;
    private readonly List<OmniboxSuggestionItem> _omniboxSuggestions = new();
    private int _selectedOmniboxIndex = 0;
    private System.Threading.CancellationTokenSource? _omniboxLiveSuggestCts;

    private void TxtUrl_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!TxtUrl.IsKeyboardFocusWithin)
        {
            TxtUrl.Focus();
            TxtUrl.SelectAll();
            e.Handled = true;
        }
    }

    private void TxtUrl_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        UpdateSecurityIndicatorUI();
        string current = TxtUrl.Text.Trim();
        if (!string.IsNullOrEmpty(current) && !current.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !current.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            _ = RefreshOmniboxSuggestionsAsync(current);
        }
    }

    private void TxtUrl_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _omniboxLiveSuggestCts?.Cancel();
        PopupOmniboxSuggestions.IsOpen = false;
        UpdateSecurityIndicatorUI();
    }

    private void TxtUrl_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingOmniboxTextProgrammatically || !TxtUrl.IsKeyboardFocusWithin)
            return;

        string query = TxtUrl.Text.Trim();
        if (string.IsNullOrEmpty(query))
        {
            _omniboxLiveSuggestCts?.Cancel();
            PopupOmniboxSuggestions.IsOpen = false;
            return;
        }

        _ = RefreshOmniboxSuggestionsAsync(query);
    }

    private async Task RefreshOmniboxSuggestionsAsync(string query)
    {
        _omniboxLiveSuggestCts?.Cancel();
        var cts = new System.Threading.CancellationTokenSource();
        _omniboxLiveSuggestCts = cts;
        var token = cts.Token;

        bool isPrivate = _isGuestSession || (_activeTab?.IsIncognito == true);
        var localHistory = isPrivate
            ? (IReadOnlyList<HistoryEntry>)Array.Empty<HistoryEntry>()
            : HistoryManager.GetEntries();
        var localBookmarks = isPrivate
            ? (IReadOnlyList<BookmarkItem>)Array.Empty<BookmarkItem>()
            : BookmarkManager.Bookmarks;

        string engineName = SettingsManager.Current.DefaultSearchEngine;

        // 1. Mostrar instantáneamente (< 0.5 ms) las búsquedas recientes y páginas/videos ya navegados del historial
        var initialList = OmniboxSuggestionService.BuildSuggestions(
            query,
            localHistory,
            localBookmarks,
            liveGoogleSuggestions: null,
            searchEngineName: engineName);

        ApplyOmniboxSuggestionsToPopup(initialList);

        // 2. Debounce de 120 ms y consulta ligera a las sugerencias predictivas de Google
        try
        {
            await Task.Delay(120, token);
            if (token.IsCancellationRequested || !TxtUrl.IsKeyboardFocusWithin) return;

            var liveSuggestions = await OmniboxSuggestionService.FetchGoogleAutocompleteAsync(query, token);
            if (token.IsCancellationRequested || !TxtUrl.IsKeyboardFocusWithin || liveSuggestions.Count == 0)
                return;

            var combinedList = OmniboxSuggestionService.BuildSuggestions(
                query,
                localHistory,
                localBookmarks,
                liveGoogleSuggestions: liveSuggestions,
                searchEngineName: engineName);

            ApplyOmniboxSuggestionsToPopup(combinedList);
        }
        catch { }
    }

    private void ApplyOmniboxSuggestionsToPopup(List<OmniboxSuggestionItem> items)
    {
        _omniboxSuggestions.Clear();
        _omniboxSuggestions.AddRange(items);
        _selectedOmniboxIndex = 0;

        for (int i = 0; i < _omniboxSuggestions.Count; i++)
        {
            _omniboxSuggestions[i].IsSelected = (i == 0);
        }

        ItemsOmniboxSuggestions.ItemsSource = null;
        ItemsOmniboxSuggestions.ItemsSource = _omniboxSuggestions;
        PopupOmniboxSuggestions.IsOpen = _omniboxSuggestions.Count > 0 && TxtUrl.IsKeyboardFocusWithin;
    }

    private void TxtUrl_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!PopupOmniboxSuggestions.IsOpen || _omniboxSuggestions.Count == 0)
        {
            if (e.Key == Key.Escape)
            {
                PopupOmniboxSuggestions.IsOpen = false;
            }
            return;
        }

        if (e.Key == Key.Down)
        {
            e.Handled = true;
            MoveOmniboxSelection(+1);
        }
        else if (e.Key == Key.Up)
        {
            e.Handled = true;
            MoveOmniboxSelection(-1);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            PopupOmniboxSuggestions.IsOpen = false;
        }
    }

    private void MoveOmniboxSelection(int delta)
    {
        if (_omniboxSuggestions.Count == 0) return;

        _selectedOmniboxIndex = (_selectedOmniboxIndex + delta + _omniboxSuggestions.Count) % _omniboxSuggestions.Count;
        for (int i = 0; i < _omniboxSuggestions.Count; i++)
        {
            _omniboxSuggestions[i].IsSelected = (i == _selectedOmniboxIndex);
        }

        ItemsOmniboxSuggestions.ItemsSource = null;
        ItemsOmniboxSuggestions.ItemsSource = _omniboxSuggestions;

        var selected = _omniboxSuggestions[_selectedOmniboxIndex];
        _isUpdatingOmniboxTextProgrammatically = true;
        TxtUrl.Text = selected.TargetInputOrUrl;
        TxtUrl.CaretIndex = TxtUrl.Text.Length;
        _isUpdatingOmniboxTextProgrammatically = false;
    }

    private void OmniboxSuggestionRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is OmniboxSuggestionItem item)
        {
            e.Handled = true;
            PopupOmniboxSuggestions.IsOpen = false;
            NavigateOmniboxInput(item.TargetInputOrUrl);
        }
    }

    private void BtnDeleteOmniboxSuggestion_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button btn && btn.Tag is OmniboxSuggestionItem item && !string.IsNullOrEmpty(item.HistoryEntryId))
        {
            e.Handled = true;
            HistoryManager.DeleteEntry(item.HistoryEntryId);
            _omniboxSuggestions.Remove(item);
            ItemsOmniboxSuggestions.ItemsSource = null;
            ItemsOmniboxSuggestions.ItemsSource = _omniboxSuggestions;
            if (_omniboxSuggestions.Count == 0)
            {
                PopupOmniboxSuggestions.IsOpen = false;
            }
        }
    }

    private void TxtUrl_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _activeTab?.WebView == null) return;

        e.Handled = true;
        _omniboxLiveSuggestCts?.Cancel();

        string input = TxtUrl.Text.Trim();
        if (PopupOmniboxSuggestions.IsOpen &&
            _selectedOmniboxIndex >= 0 &&
            _selectedOmniboxIndex < _omniboxSuggestions.Count)
        {
            input = _omniboxSuggestions[_selectedOmniboxIndex].TargetInputOrUrl.Trim();
        }

        PopupOmniboxSuggestions.IsOpen = false;
        NavigateOmniboxInput(input);
    }

    private void NavigateOmniboxInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || _activeTab?.WebView == null) return;

        string targetUrl;
        if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            targetUrl = input;
        }
        else if (input.Contains('.') && !input.Contains(' '))
        {
            targetUrl = "https://" + input;
        }
        else
        {
            targetUrl = SettingsManager.GetSearchUrl(input);
        }

        _isUpdatingOmniboxTextProgrammatically = true;
        TxtUrl.Text = targetUrl;
        _isUpdatingOmniboxTextProgrammatically = false;

        if (_activeTab.WebView.CoreWebView2 != null)
        {
            _activeTab.WebView.CoreWebView2.Navigate(targetUrl);
            _activeTab.WebView.Focus();
        }
        else if (Uri.TryCreate(targetUrl, UriKind.Absolute, out var navUri))
        {
            _activeTab.WebView.Source = navUri;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // DOBLE CLIC EN HEADER VACÍO PARA MAXIMIZAR / RESTAURAR
    // ─────────────────────────────────────────────────────────────────────
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source)
        {
            if (FindVisualParent<Button>(source) != null || FindVisualParent<MenuItem>(source) != null)
                return;
        }

        if (e.ClickCount == 2)
        {
            BtnMaximize_Click(sender, e);
        }
        else if (e.ClickCount == 1 && e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private static T? FindVisualParent<T>(DependencyObject element) where T : DependencyObject
    {
        var parent = VisualTreeHelper.GetParent(element);
        while (parent != null)
        {
            if (parent is T typed) return typed;
            parent = VisualTreeHelper.GetParent(parent);
        }
        return null;
    }

    // ─────────────────────────────────────────────────────────────────────
    // BOTÓN PÁGINA FLOTANTE (MANUAL)
    // ─────────────────────────────────────────────────────────────────────
    private void BtnTogglePip_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab == null) return;

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

    // ─────────────────────────────────────────────────────────────────────
    // CONFIGURACIÓN Y PILL 0 TELEMETRÍA
    // ─────────────────────────────────────────────────────────────────────
    private void BadgeTelemetry_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenSettings("Telemetry");
    }

    private void BtnHistory_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        OpenHistory();
    }

    private CoreWebView2? GetPersistentProfileCoreWebView2()
    {
        if (_activeTab != null && !_activeTab.IsIncognito && _activeTab.WebView?.CoreWebView2 != null)
        {
            return _activeTab.WebView.CoreWebView2;
        }

        var normalTab = Tabs.FirstOrDefault(t => !t.IsIncognito && !t.IsDisposed && t.WebView?.CoreWebView2 != null);
        if (normalTab?.WebView?.CoreWebView2 != null)
        {
            return normalTab.WebView.CoreWebView2;
        }

        return _activeTab?.WebView?.CoreWebView2;
    }

    /// <summary>
    /// Reintegra la pestaña flotante a su contenedor original antes de abrir ventanas modales (Configuración, Historial),
    /// evitando bloqueos de interfaz y aplicando la visibilidad correcta (Collapsed si no es la activa).
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

    public void OpenHistory()
    {
        if (_isGuestSession && !TryUnlockFromGuestMode("Desbloquea con tu PIN para ver el Historial personal"))
        {
            return;
        }

        EnsureFloatingTabReintegrated();

        try
        {
            var historyWin = new HistoryWindow(GetPersistentProfileCoreWebView2())
            {
                Owner = this
            };
            historyWin.UrlSelected += async (url) =>
            {
                if (IsSafeHttpUrl(url))
                {
                    bool isCurrentTabEmpty = _activeTab != null &&
                        (_activeTab.IsNewTab ||
                         string.IsNullOrWhiteSpace(_activeTab.Url) ||
                         _activeTab.Url.Equals("about:blank", StringComparison.OrdinalIgnoreCase) ||
                         _activeTab.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase));

                    if (isCurrentTabEmpty && _activeTab?.WebView?.CoreWebView2 != null)
                    {
                        _activeTab.IsNewTab = false;
                        _activeTab.Url = url;
                        _activeTab.WebView.CoreWebView2.Navigate(url);
                    }
                    else
                    {
                        var newTab = await AddNewTabAsync(url);
                        SelectTab(newTab, force: true);
                    }
                }
            };
            historyWin.ShowDialog();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[History] Error al abrir historial: {ex}");
        }
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        OpenSettings("General");
    }

    private void OpenSettings(string defaultSection = "General")
    {
        if (_isGuestSession && !TryUnlockFromGuestMode("Desbloquea con tu PIN para abrir la Configuración personal"))
        {
            return;
        }

        EnsureFloatingTabReintegrated();

        try
        {
            string prevEngine = SettingsManager.Current.DefaultSearchEngine;
            string prevCustom = SettingsManager.Current.CustomSearchUrl;

            var settingsWin = new SettingsWindow(GetPersistentProfileCoreWebView2())
            {
                Owner = this
            };
            settingsWin.OpenToSection(defaultSection);
            settingsWin.ShowDialog();
            ApplySettings();

            if (prevEngine != SettingsManager.Current.DefaultSearchEngine ||
                prevCustom != SettingsManager.Current.CustomSearchUrl)
            {
                foreach (var t in Tabs.Where(t => t.IsNewTab && !t.IsDisposed && t.WebView?.CoreWebView2 != null))
                {
                    try { t.WebView.NavigateToString(NewTabPage.GetHtml(t.IsIncognito)); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] Error al abrir configuración: {ex}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // GESTIÓN DE ZOOM INTELIGENTE Y RESTABLECIMIENTO (NIVEL SENIOR)
    // ─────────────────────────────────────────────────────────────────────
    private void UpdateZoomIndicatorUI(bool showFloating = false)
    {
        if (_activeTab?.WebView == null)
        {
            PillZoomIndicator.Visibility = Visibility.Collapsed;
            FloatingZoomWidget.Visibility = Visibility.Collapsed;
            return;
        }

        double factor = _activeTab.WebView.ZoomFactor;
        int percentage = (int)Math.Round(factor * 100);

        if (Math.Abs(factor - 1.0) < 0.02)
        {
            // Zoom al 100%: mantener interfaz limpia
            PillZoomIndicator.Visibility = Visibility.Collapsed;
            FloatingZoomWidget.Visibility = Visibility.Collapsed;
            _zoomWidgetTimer.Stop();
        }
        else
        {
            // Zoom modificado
            TxtZoomValue.Text = $"{percentage}%";
            PillZoomIndicator.Visibility = Visibility.Visible;

            if (showFloating)
            {
                TxtFloatingZoomValue.Text = $"{percentage}%";
                FloatingZoomWidget.Visibility = Visibility.Visible;
                _zoomWidgetTimer.Stop();
                _zoomWidgetTimer.Start();
            }
        }
    }

    private void ResetZoom()
    {
        if (_activeTab?.WebView != null)
        {
            _activeTab.WebView.ZoomFactor = 1.0;
            UpdateZoomIndicatorUI(showFloating: false);
        }
    }

    private void PillZoom_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ResetZoom();
    }

    private void BtnZoomReset_Click(object sender, RoutedEventArgs e)
    {
        ResetZoom();
    }

    private void BtnZoomIn_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab?.WebView != null)
        {
            _activeTab.WebView.ZoomFactor = Math.Min(5.0, _activeTab.WebView.ZoomFactor + 0.1);
            UpdateZoomIndicatorUI(showFloating: true);
        }
    }

    private void BtnZoomOut_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab?.WebView != null)
        {
            _activeTab.WebView.ZoomFactor = Math.Max(0.25, _activeTab.WebView.ZoomFactor - 0.1);
            UpdateZoomIndicatorUI(showFloating: true);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // GESTOR DE ATAJOS DE TECLADO GLOBALES (UNIVERSALES)
    // ─────────────────────────────────────────────────────────────────────
    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (BrowserLockOverlay != null && BrowserLockOverlay.Visibility == Visibility.Visible)
        {
            return;
        }

        // 0. Ctrl + Shift + K: Abrir Bóveda de Contraseñas
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.K)
        {
            e.Handled = true;
            OpenPasswordVault();
            return;
        }

        // 1. Ctrl + Tab: Cambiar a la siguiente pestaña cíclicamente
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Tab)
        {
            e.Handled = true;
            if (Tabs.Count > 1 && _activeTab != null)
            {
                int currentIdx = Tabs.IndexOf(_activeTab);
                int nextIdx = (currentIdx + 1) % Tabs.Count;
                SelectTab(Tabs[nextIdx]);
            }
            return;
        }

        // 2. Ctrl + Shift + Tab: Cambiar a la pestaña anterior
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Tab)
        {
            e.Handled = true;
            if (Tabs.Count > 1 && _activeTab != null)
            {
                int currentIdx = Tabs.IndexOf(_activeTab);
                int prevIdx = (currentIdx - 1 + Tabs.Count) % Tabs.Count;
                SelectTab(Tabs[prevIdx]);
            }
            return;
        }

        // 3. Ctrl + Shift + B: Mostrar / Ocultar Barra de Accesos Directos
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.B)
        {
            e.Handled = true;
            SettingsManager.Current.ShowBookmarksBar = !SettingsManager.Current.ShowBookmarksBar;
            SettingsManager.Save();
            if (!SettingsManager.Current.ShowBookmarksBar)
            {
                ShowToast("Barra de accesos directos oculta. Para volver a activarla ve a Configuración (o presiona Ctrl+Shift+B)");
            }
            return;
        }

        // 4. Ctrl + T o Ctrl + N: Abrir nueva pestaña
        if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.T || e.Key == Key.N))
        {
            e.Handled = true;
            await AddNewTabAsync(null, isIncognito: false);
            return;
        }

        // 5. Ctrl + Shift + N: Modo Incógnito
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.N)
        {
            e.Handled = true;
            await AddNewTabAsync(null, isIncognito: true);
            return;
        }

        // 6. Ctrl + Shift + T: Reabrir última pestaña cerrada
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.T)
        {
            e.Handled = true;
            ReopenLastClosedTab();
            return;
        }

        // 7. Ctrl + W: Cerrar pestaña actual
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.W)
        {
            e.Handled = true;
            if (_activeTab != null) CloseTab(_activeTab);
            return;
        }

        // 8. Ctrl + R o F5: Recargar
        if ((Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.R) || e.Key == Key.F5)
        {
            e.Handled = true;
            _activeTab?.WebView?.CoreWebView2?.Reload();
            return;
        }

        // 9. Ctrl + L o Alt + D: Enfocar y seleccionar barra de URL
        if ((Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.L) ||
            (Keyboard.Modifiers == ModifierKeys.Alt && e.Key == Key.D))
        {
            e.Handled = true;
            TxtUrl.Focus();
            TxtUrl.SelectAll();
            return;
        }

        // 10. Ctrl + ,: Abrir Configuración
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.OemComma)
        {
            e.Handled = true;
            OpenSettings("General");
            return;
        }

        // 10.1 Ctrl + H: Abrir Historial de Navegación y Limpieza
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.H)
        {
            e.Handled = true;
            OpenHistory();
            return;
        }

        // 10.1b Ctrl + J: Abrir Historial de Descargas
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.J)
        {
            e.Handled = true;
            RefreshDownloadsPopupUI();
            PopupDownloads.IsOpen = !PopupDownloads.IsOpen;
            return;
        }

        // 10.2 Ctrl + 0: Restablecer zoom a 100%
        if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.D0 || e.Key == Key.NumPad0))
        {
            e.Handled = true;
            ResetZoom();
            return;
        }

        // 10.3 Ctrl + +: Aumentar zoom
        if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.OemPlus || e.Key == Key.Add))
        {
            e.Handled = true;
            BtnZoomIn_Click(sender, e);
            return;
        }

        // 10.4 Ctrl + -: Reducir zoom
        if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.OemMinus || e.Key == Key.Subtract))
        {
            e.Handled = true;
            BtnZoomOut_Click(sender, e);
            return;
        }

        // 11. Ctrl + 1 a 8: Salto a pestaña por número
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key >= Key.D1 && e.Key <= Key.D8)
        {
            int index = e.Key - Key.D1;
            if (index < Tabs.Count)
            {
                e.Handled = true;
                SelectTab(Tabs[index]);
                return;
            }
        }

        // 12. Ctrl + 9: Salto a la última pestaña
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.D9)
        {
            if (Tabs.Count > 0)
            {
                e.Handled = true;
                SelectTab(Tabs.Last());
                return;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // BOTONES Y PESTAÑAS
    // ─────────────────────────────────────────────────────────────────────
    private async void BtnNewTab_Click(object sender, RoutedEventArgs e)
    {
        await AddNewTabAsync(null, isIncognito: false);
    }

    private async void BtnNewIncognitoTab_Click(object sender, RoutedEventArgs e)
    {
        await AddNewTabAsync(null, isIncognito: true);
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is BrowserTab tabToClose)
        {
            CloseTab(tabToClose);
        }
    }

    private void CloseTab(BrowserTab tabToClose)
    {
        if (tabToClose.IsDisposed) return;
        tabToClose.IsDisposed = true;

        StopSnifferSessionForTab(tabToClose);
        tabToClose.CorsUnlocker?.Dispose();
        tabToClose.CorsUnlocker = null;
        tabToClose.ClearDetectedMedia();

        // Privacidad de Incógnito: jamás guardar pestañas de incógnito en el historial de Ctrl+Shift+T
        if (!tabToClose.IsIncognito && !string.IsNullOrWhiteSpace(tabToClose.Url) && !tabToClose.IsNewTab)
        {
            _closedTabsHistory.Push((tabToClose.Url, tabToClose.Title, false));
        }

        int index = Tabs.IndexOf(tabToClose);
        bool wasActive = (_activeTab == tabToClose);

        Tabs.Remove(tabToClose);

        if (_pipWindow.TargetTab == tabToClose)
        {
            _pipWindow.HideAndDetach();
        }

        // Si cerramos la pestaña activa, cambiar a otra pestaña ANTES de disponer nada
        if (Tabs.Count == 0)
        {
            _activeTab = null;
            Close();
            return;
        }
        else if (wasActive)
        {
            _activeTab = null; // Limpiar para que previousTab sea null en SelectTab
            int nextIndex = Math.Clamp(index, 0, Tabs.Count - 1);
            SelectTab(Tabs[nextIndex]);
        }

        // Detener navegación activa para evitar peticiones pendientes y scripts concurrentes
        try { tabToClose.WebView?.CoreWebView2?.Stop(); } catch { }

        // Retirar del contenedor visual
        if (BrowserContainer.Children.Contains(tabToClose.WebView))
        {
            BrowserContainer.Children.Remove(tabToClose.WebView);
        }

        // Disponer en segundo plano seguro fuera del flujo crítico de eventos
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            try { tabToClose.WebView?.Dispose(); } catch { }
        });
    }

    private async void ReopenLastClosedTab()
    {
        if (_closedTabsHistory.Count > 0)
        {
            var (url, title, isIncognito) = _closedTabsHistory.Pop();
            await AddNewTabAsync(url, isIncognito);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // MENÚ CONTEXTUAL DE PESTAÑAS (CLIC DERECHO)
    // ─────────────────────────────────────────────────────────────────────
    private BrowserTab? GetTabFromMenuItem(object sender)
    {
        if (sender is MenuItem mi)
        {
            if (mi.DataContext is BrowserTab tab) return tab;
            if (mi.Parent is ContextMenu cm && cm.PlacementTarget is FrameworkElement fe && fe.DataContext is BrowserTab targetTab)
                return targetTab;
        }
        return _activeTab;
    }

    private async void MenuNewTabRight_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetTabFromMenuItem(sender);
        int targetIndex = tab != null ? Tabs.IndexOf(tab) + 1 : Tabs.Count;
        await AddNewTabAsync(null, isIncognito: tab?.IsIncognito ?? false, insertIndex: targetIndex);
    }

    private async void MenuDuplicateTab_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetTabFromMenuItem(sender);
        if (tab == null) return;
        int targetIndex = Tabs.IndexOf(tab) + 1;
        var duplicated = await AddNewTabAsync(tab.Url, isIncognito: tab.IsIncognito, insertIndex: targetIndex);

        // Si la pestaña original estaba fijada, conservar su modo fijado
        if (tab.IsPinned)
        {
            duplicated.PinMode = tab.PinMode;
        }
    }

    private void MenuToggleMute_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetTabFromMenuItem(sender);
        if (tab?.WebView?.CoreWebView2 != null)
        {
            tab.IsMuted = !tab.IsMuted;
            try
            {
                tab.WebView.CoreWebView2.IsMuted = tab.IsMuted;
            }
            catch { }

            string json = JsonSerializer.Serialize(new { action = "set_mute", muted = tab.IsMuted });
            tab.WebView.CoreWebView2.PostWebMessageAsJson(json);
        }
    }

    private async void MenuMoveToNewWindow_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetTabFromMenuItem(sender);
        if (tab != null)
        {
            if (Tabs.Count <= 1)
            {
                await AddNewTabAsync(null, isIncognito: false);
            }
            DetachTabToNewWindow(tab, useCursorPosition: false);
        }
    }

    private void MenuReload_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetTabFromMenuItem(sender);
        tab?.WebView?.CoreWebView2?.Reload();
    }

    private void MenuCloseTab_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetTabFromMenuItem(sender);
        if (tab != null) CloseTab(tab);
    }

    private void MenuCloseOtherTabs_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetTabFromMenuItem(sender);
        if (tab == null) return;

        var otherTabs = Tabs.Where(t => t != tab && !t.IsPinned).ToList();
        foreach (var t in otherTabs)
        {
            CloseTab(t);
        }
    }

    private void MenuCloseTabsToRight_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetTabFromMenuItem(sender);
        if (tab == null) return;

        int index = Tabs.IndexOf(tab);
        var tabsToRight = Tabs.Skip(index + 1).Where(t => !t.IsPinned).ToList();
        foreach (var t in tabsToRight)
        {
            CloseTab(t);
        }
    }

    private void MenuReopenClosedTab_Click(object sender, RoutedEventArgs e)
    {
        ReopenLastClosedTab();
    }

    // ─────────────────────────────────────────────────────────────────────
    // NAVEGACIÓN Y VENTANA
    // ─────────────────────────────────────────────────────────────────────
    private void BtnBack_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab?.WebView?.CoreWebView2?.CanGoBack == true)
            _activeTab.WebView.CoreWebView2.GoBack();
    }

    private void BtnForward_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab?.WebView?.CoreWebView2?.CanGoForward == true)
            _activeTab.WebView.CoreWebView2.GoForward();
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        _activeTab?.WebView?.CoreWebView2?.Reload();
    }

    private void BtnHome_Click(object sender, RoutedEventArgs e)
    {
        _activeTab?.WebView?.CoreWebView2?.Navigate("https://www.youtube.com");
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private bool _isApplyingNormalBounds = false;

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            BtnMaxRestore.Content = "□";
        }
        else
        {
            RecordCurrentNormalBounds(saveToDisk: true);
            WindowState = WindowState.Maximized;
            BtnMaxRestore.Content = "❐";
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            if (WindowState == WindowState.Normal)
            {
                RecordCurrentNormalBounds(saveToDisk: false);
            }
            SettingsManager.Save(notify: false);

            SettingsManager.SettingsChanged -= ApplySettings;
            DownloadManager.DownloadsChanged -= OnDownloadsChanged;
            DownloadManager.Flush();
            _nativePipTimer?.Stop();
            _undoBookmarkTimer.Stop();
            _toastTimer.Stop();
            _zoomWidgetTimer.Stop();

            if (_pipWindow.IsVisible)
                _pipWindow.HideAndDetach();

            _pipWindow.OnReturnToTabRequested = null;
            _pipWindow.OnDetachWebView = null;
            try { _pipWindow.Close(); } catch { }

            foreach (var tab in Tabs.ToList())
            {
                if (tab.OwnerWindow == this && !tab.IsDisposed)
                {
                    tab.IsDisposed = true;
                    try { tab.WebView?.CoreWebView2?.Stop(); } catch { }
                    try { tab.WebView?.Dispose(); } catch { }
                }
            }
            Tabs.Clear();

            int remainingWindows = 0;
            try
            {
                remainingWindows = Application.Current.Windows.OfType<MainWindow>().Count(w => w != this && w.IsLoaded);
            }
            catch { }

            if (remainingWindows == 0)
            {
                try { SingleInstanceIpc.Release(); } catch { }
                try { Application.Current.Shutdown(); } catch { }
            }
        }
        catch { }

        base.OnClosed(e);
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        UpdateWindowMargin();
        if (WindowState == WindowState.Maximized)
        {
            BtnMaxRestore.Content = "❐";
        }
        else if (WindowState == WindowState.Normal)
        {
            BtnMaxRestore.Content = "□";
            ApplyRestoredWindowBounds();
        }
        UpdateResponsiveToolbarLayout();
    }

    private void MainWindow_SizeOrLocationChanged(object? sender, EventArgs e)
    {
        UpdateResponsiveToolbarLayout();
        if (!IsLoaded || _isApplyingNormalBounds || WindowState != WindowState.Normal)
            return;

        RecordCurrentNormalBounds(saveToDisk: false);
    }

    private void UpdateResponsiveToolbarLayout()
    {
        double w = ActualWidth > 0 ? ActualWidth : Width;
        if (double.IsNaN(w) || w <= 0) return;

        bool isCompact = w < 1020;
        bool isUltraCompact = w < 700;

        if (TxtPipBtnLabel != null)
            TxtPipBtnLabel.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;

        if (TxtMediaSnifferLabel != null)
            TxtMediaSnifferLabel.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;

        if (TxtTelemetryLabel != null)
            TxtTelemetryLabel.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;

        if (TxtBrandTitle != null)
            TxtBrandTitle.Visibility = isUltraCompact ? Visibility.Collapsed : Visibility.Visible;

        if (BtnHome != null)
            BtnHome.Visibility = isUltraCompact ? Visibility.Collapsed : Visibility.Visible;

        UpdateShieldBadgeText();
    }

    private void UpdateShieldBadgeText()
    {
        if (TxtShieldBadge == null) return;
        int count = _activeTab?.BlockedAdsCount ?? 0;
        double w = ActualWidth > 0 ? ActualWidth : Width;
        bool isCompact = !double.IsNaN(w) && w > 0 && w < 1020;
        TxtShieldBadge.Text = isCompact ? $"{count}" : $"{count} bloqueados";
    }

    private void RecordCurrentNormalBounds(bool saveToDisk)
    {
        if (_isApplyingNormalBounds || WindowState != WindowState.Normal)
            return;

        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : Height;
        if (w < MinWidth || h < MinHeight || double.IsNaN(Left) || double.IsNaN(Top))
            return;

        var workArea = SystemParameters.WorkArea;
        if (w >= workArea.Width - 4 && h >= workArea.Height - 4)
            return;

        var s = SettingsManager.Current;
        s.MainWindowWidth = w;
        s.MainWindowHeight = h;
        s.MainWindowLeft = Left;
        s.MainWindowTop = Top;
        s.HasCustomMainWindowBounds = true;

        if (saveToDisk)
        {
            SettingsManager.Save(notify: false);
        }
    }

    private void ApplyRestoredWindowBounds()
    {
        _isApplyingNormalBounds = true;
        try
        {
            var workArea = SystemParameters.WorkArea;
            var s = SettingsManager.Current;

            if (!s.HasCustomMainWindowBounds ||
                double.IsNaN(s.MainWindowLeft) ||
                double.IsNaN(s.MainWindowTop) ||
                s.MainWindowWidth < MinWidth ||
                s.MainWindowHeight < MinHeight)
            {
                // Primera vez que el usuario restaura la ventana tras instalar: centrar en pantalla
                double defaultW = Math.Min(1280, workArea.Width * 0.85);
                double defaultH = Math.Min(800, workArea.Height * 0.85);
                double centeredLeft = workArea.Left + (workArea.Width - defaultW) / 2.0;
                double centeredTop = workArea.Top + (workArea.Height - defaultH) / 2.0;

                Width = defaultW;
                Height = defaultH;
                Left = centeredLeft;
                Top = centeredTop;

                s.MainWindowWidth = defaultW;
                s.MainWindowHeight = defaultH;
                s.MainWindowLeft = centeredLeft;
                s.MainWindowTop = centeredTop;
                s.HasCustomMainWindowBounds = true;
                SettingsManager.Save(notify: false);
            }
            else
            {
                // Restaurar exactamente el tamaño y posición donde el usuario la dejó
                double savedW = Math.Clamp(s.MainWindowWidth, MinWidth, workArea.Width);
                double savedH = Math.Clamp(s.MainWindowHeight, MinHeight, workArea.Height);
                double savedLeft = Math.Clamp(s.MainWindowLeft, workArea.Left - savedW + 120, workArea.Right - 120);
                double savedTop = Math.Clamp(s.MainWindowTop, workArea.Top, workArea.Bottom - 80);

                Width = savedW;
                Height = savedH;
                Left = savedLeft;
                Top = savedTop;
            }
        }
        finally
        {
            _isApplyingNormalBounds = false;
        }
    }

    private void UpdateWindowMargin()
    {
        RootWindowGrid.Margin = new Thickness(0);
        if (BrowserContainer != null)
        {
            // En ventana restaurada (Normal), dejamos los 6px del borde de agarre (ResizeBorderThickness="6")
            // libres del HWND hijo de WebView2 para poder estirar desde cualquier costado, borde inferior o esquina.
            BrowserContainer.Margin = WindowState == WindowState.Normal
                ? new Thickness(6, 0, 6, 6)
                : new Thickness(0);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // AMPLIFICADOR DE VOLUMEN HASTA 300% (VOLUME MASTER INTEGRADO)
    // ─────────────────────────────────────────────────────────────────────
    private bool _isUpdatingVolumeSlider = false;

    private void BtnVolumeBooster_Click(object sender, RoutedEventArgs e)
    {
        UpdateVolumeBoosterUI();
        PopupVolumeBooster.IsOpen = !PopupVolumeBooster.IsOpen;
    }

    private void SliderVolumeBoost_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingVolumeSlider || _activeTab == null) return;
        int pct = (int)Math.Round(e.NewValue);
        _activeTab.VolumeBoostPercent = pct;
        UpdateVolumeBoosterUI();
        SendVolumeBoostToTab(_activeTab);
    }

    private void BtnVolumeReset_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab == null) return;
        _activeTab.VolumeBoostPercent = 100;
        UpdateVolumeBoosterUI();
        SendVolumeBoostToTab(_activeTab);
    }

    private void BtnVolumePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab == null) return;
        if (sender is Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out int preset))
        {
            _activeTab.VolumeBoostPercent = Math.Clamp(preset, 0, 300);
            UpdateVolumeBoosterUI();
            SendVolumeBoostToTab(_activeTab);
        }
    }

    public void BroadcastToFrames(BrowserTab tab, string script)
    {
        try { _ = tab.WebView.CoreWebView2.ExecuteScriptAsync(script); } catch { }
        foreach (var f in tab.ActiveFrames.Keys.ToArray())
        {
            try { _ = f.ExecuteScriptAsync(script); } catch { }
        }
    }

    public void SetMediaProgressReporting(BrowserTab tab, bool enabled)
    {
        try
        {
            if (tab?.WebView?.CoreWebView2 == null || tab.IsDisposed) return;
            BroadcastToFrames(tab, $"window.__lexSetMediaProgress && window.__lexSetMediaProgress({(enabled ? "true" : "false")});");
        }
        catch { }
    }

    private void SendVolumeBoostToTab(BrowserTab tab)
    {
        try
        {
            if (tab?.WebView?.CoreWebView2 == null || tab.IsDisposed) return;
            int pct = tab.VolumeBoostPercent;
            string script = $"if (window.__lexSetVolumeBoostGlobal) window.__lexSetVolumeBoostGlobal({pct});";
            BroadcastToFrames(tab, script);
        }
        catch { }
    }

    private void UpdateVolumeBoosterUI()
    {
        if (TxtVolumeBoosterLabel == null) return;
        int pct = _activeTab?.VolumeBoostPercent ?? 100;

        _isUpdatingVolumeSlider = true;
        if (SliderVolumeBoost != null)
        {
            SliderVolumeBoost.Value = pct;
        }
        _isUpdatingVolumeSlider = false;

        TxtVolumeBoosterLabel.Text = $"{pct}%";
        if (TxtVolumePopupValue != null)
        {
            TxtVolumePopupValue.Text = $"{pct}%";
        }

        if (TxtVolumeBoosterIcon != null)
        {
            TxtVolumeBoosterIcon.Text = pct == 0 ? "🔇" : (pct > 100 ? "🚀" : "🔊");
        }

        if (BorderVolumeBooster != null)
        {
            BorderVolumeBooster.Background = pct != 100
                ? new SolidColorBrush(Color.FromRgb(0x31, 0x2E, 0x81))
                : new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x36));
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // DETECTOR Y DESCARGADOR MULTIMEDIA BAJO DEMANDA (0 MB RAM EN REPOSO)
    // ─────────────────────────────────────────────────────────────────────
    private string _currentMediaFilter = "Todos";
    private bool _isUpdatingSnifferCheck = false;
    private bool _isExtractingYouTube = false;

    private void BtnMediaSniffer_Click(object sender, RoutedEventArgs e)
    {
        UpdateMediaSnifferUI();
        PopupMediaSniffer.IsOpen = !PopupMediaSniffer.IsOpen;
    }

    private void BtnToggleSnifferActive_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab == null) return;
        _activeTab.IsMediaSnifferActive = !_activeTab.IsMediaSnifferActive;

        if (_activeTab.IsMediaSnifferActive)
        {
            StartSnifferSessionForTab(_activeTab);
            RequestDomMediaScan(_activeTab);
        }
        else
        {
            StopSnifferSessionForTab(_activeTab);
            _activeTab.ClearDetectedMedia();
        }

        UpdateMediaSnifferUI();
    }

    private void StartSnifferSessionForTab(BrowserTab tab)
    {
        try
        {
            StopSnifferSessionForTab(tab);
            if (tab.WebView?.CoreWebView2 == null) return;

            var session = new MediaSnifferSession(tab.WebView.CoreWebView2, () =>
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    if (tab.IsDisposed || !tab.IsMediaSnifferActive || tab.SnifferSession == null) return;
                    foreach (var item in tab.SnifferSession.Items)
                    {
                        string uniqueKey = $"{item.FormatBadge}|{item.Url}";
                        if (tab.DetectedMediaUrls.Add(uniqueKey))
                        {
                            InsertMediaItemSorted(tab, item);
                        }
                    }
                    if (_activeTab == tab)
                    {
                        UpdateMediaSnifferUI();
                    }
                });
            })
            {
                PageTitle = tab.Title
            };

            tab.SnifferSession = session;
            _ = session.StartAsync();
        }
        catch (Exception ex)
        {
            MediaExtractorService.LogMediaDiagnostic($"[Sniffer] Error al iniciar sesión CDP: {ex.Message}");
        }
    }

    private void StopSnifferSessionForTab(BrowserTab tab)
    {
        try
        {
            tab.SnifferSession?.Dispose();
            tab.SnifferSession = null;
        }
        catch { }
    }

    private void ChkKeepSnifferOnNavigate_Changed(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingSnifferCheck || _activeTab == null) return;
        _activeTab.KeepSnifferActiveOnNavigate = ChkKeepSnifferOnNavigate.IsChecked == true;
    }

    private void BtnRescanMedia_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab == null) return;
        _activeTab.IsMediaSnifferActive = true;
        _activeTab.ClearDetectedMedia();
        if (_activeTab.SnifferSession != null)
        {
            _ = _activeTab.SnifferSession.RescanAsync();
        }
        else
        {
            StartSnifferSessionForTab(_activeTab);
        }
        RequestDomMediaScan(_activeTab);
        UpdateMediaSnifferUI();
    }

    private void BtnClearMedia_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab == null) return;
        _activeTab.ClearDetectedMedia();
        if (_activeTab.SnifferSession != null)
        {
            _activeTab.SnifferSession.Items.Clear();
        }
        UpdateMediaSnifferUI();
    }

    private void BtnMediaFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string filter)
        {
            _currentMediaFilter = filter;
            var activeBg = new SolidColorBrush(Color.FromRgb(0x63, 0x66, 0xF1));
            var inactiveBg = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x34));

            if (BtnFilterAll != null) BtnFilterAll.Background = filter == "Todos" ? activeBg : inactiveBg;
            if (BtnFilterVideo != null) BtnFilterVideo.Background = filter == "Video" ? activeBg : inactiveBg;
            if (BtnFilterAudio != null) BtnFilterAudio.Background = filter == "Audio" ? activeBg : inactiveBg;
            if (BtnFilterImage != null) BtnFilterImage.Background = filter == "Imagen" ? activeBg : inactiveBg;
            if (BtnFilterDoc != null) BtnFilterDoc.Background = filter == "Documento" ? activeBg : inactiveBg;

            RefreshFilteredMediaList();
        }
    }

    private void RequestDomMediaScan(BrowserTab tab)
    {
        try
        {
            if (tab == null || tab.IsDisposed) return;

            // 1. Si estamos en un video o Short de YouTube, extraer todas las calidades limpias (1080p HD, 720p HD, 360p, Audio HQ)
            string? ytVideoId = MediaExtractorService.TryExtractYouTubeVideoId(tab.Url);
            if (!string.IsNullOrWhiteSpace(ytVideoId))
            {
                _ = ExtractAndRegisterYouTubeStreamsAsync(tab, tab.Url, tab.Title);
                return;
            }

            // 2. En otros sitios web, solicitar escaneo bajo demanda al DOM e iframes
            if (tab.WebView?.CoreWebView2 == null) return;
            string json = JsonSerializer.Serialize(new { action = "scan_page_media" });
            tab.WebView.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch { }
    }

    private async Task ExtractAndRegisterYouTubeStreamsAsync(BrowserTab tab, string pageUrl, string fallbackTitle)
    {
        try
        {
            _isExtractingYouTube = true;
            if (_activeTab == tab) RefreshFilteredMediaList();

            var items = await MediaExtractorService.ExtractYouTubeStreamsAsync(pageUrl, fallbackTitle);

            await Dispatcher.InvokeAsync(() =>
            {
                _isExtractingYouTube = false;
                if (tab.IsDisposed || !tab.IsMediaSnifferActive) return;

                foreach (var item in items)
                {
                    string uniqueKey = $"{item.FormatBadge}|{item.Url}";
                    if (tab.DetectedMediaUrls.Add(uniqueKey))
                    {
                        InsertMediaItemSorted(tab, item);
                    }
                }

                if (_activeTab == tab)
                {
                    UpdateMediaSnifferUI();
                }
            });
        }
        catch
        {
            _isExtractingYouTube = false;
            if (_activeTab == tab) Dispatcher.Invoke(UpdateMediaSnifferUI);
        }
    }

    private static void InsertMediaItemSorted(BrowserTab tab, DetectedMediaItem item)
    {
        int idx = 0;
        while (idx < tab.DetectedMedia.Count && tab.DetectedMedia[idx].PriorityScore >= item.PriorityScore)
        {
            idx++;
        }
        tab.DetectedMedia.Insert(idx, item);
    }

    private void InspectNetworkMediaResponse(BrowserTab tab, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        try
        {
            // En YouTube usamos exclusivamente el extractor limpio InnerTube para evitar capturar segmentos/basura
            if (!string.IsNullOrWhiteSpace(tab.Url) &&
                (tab.Url.Contains("youtube.com/", StringComparison.OrdinalIgnoreCase) ||
                 tab.Url.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            string url = e.Request.Uri;
            if (string.IsNullOrWhiteSpace(url)) return;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
            if (AdBlockFilter.ShouldBlock(uri)) return;

            string contentType = "";
            long contentLength = 0;
            string? reqReferer = null;
            string? reqOrigin = null;
            string? reqCookie = null;
            string? reqAuth = null;
            string? reqUserAgent = null;

            try
            {
                var headers = e.Response.Headers;
                if (headers.Contains("Content-Type"))
                    contentType = headers.GetHeader("Content-Type") ?? "";

                // Si es respuesta parcial 206 (ej. Content-Range: bytes 0-65535/15482910), tomar el tamaño total real tras la barra '/'
                if (headers.Contains("Content-Range"))
                {
                    string cr = headers.GetHeader("Content-Range") ?? "";
                    int slashIdx = cr.LastIndexOf('/');
                    if (slashIdx >= 0 && slashIdx + 1 < cr.Length &&
                        long.TryParse(cr.AsSpan(slashIdx + 1).Trim(), out long totalFromRange) &&
                        totalFromRange > 0)
                    {
                        contentLength = totalFromRange;
                    }
                }

                if (contentLength <= 0 && headers.Contains("Content-Length") && long.TryParse(headers.GetHeader("Content-Length"), out long len))
                    contentLength = len;

                var reqHeaders = e.Request.Headers;
                if (reqHeaders.Contains("Referer")) reqReferer = reqHeaders.GetHeader("Referer");
                if (reqHeaders.Contains("Origin")) reqOrigin = reqHeaders.GetHeader("Origin");
                if (reqHeaders.Contains("Cookie")) reqCookie = reqHeaders.GetHeader("Cookie");
                if (reqHeaders.Contains("Authorization")) reqAuth = reqHeaders.GetHeader("Authorization");
                if (reqHeaders.Contains("User-Agent")) reqUserAgent = reqHeaders.GetHeader("User-Agent");
            }
            catch { }

            if (MediaExtractorService.IsGarbageMediaUrl(url, contentType, contentLength, categoryHint: null))
                return;

            RegisterDetectedMediaUrl(tab, url, categoryHint: null, contentType, contentLength, reqReferer, reqOrigin, reqCookie, reqAuth, reqUserAgent);
        }
        catch { }
    }

    private void RegisterDetectedMediaUrl(
        BrowserTab tab,
        string url,
        string? categoryHint,
        string? contentType,
        long contentLength,
        string? reqReferer = null,
        string? reqOrigin = null,
        string? reqCookie = null,
        string? reqAuth = null,
        string? reqUserAgent = null)
    {
        if (tab == null || tab.IsDisposed || !tab.IsMediaSnifferActive) return;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (AdBlockFilter.ShouldBlock(uri)) return;
        if (MediaExtractorService.IsGarbageMediaUrl(url, contentType, contentLength, categoryHint)) return;

        string pathLower = uri.AbsolutePath.ToLowerInvariant();
        string ctLower = (contentType ?? "").ToLowerInvariant();
        string effectiveReferer = !string.IsNullOrWhiteSpace(reqReferer) ? reqReferer : tab.Url;

        // Si es un manifiesto HLS (.m3u8), expandirlo asíncronamente en sus calidades reales (1080p, 720p, etc.)
        if (pathLower.EndsWith(".m3u8") || ctLower.Contains("mpegurl"))
        {
            if (tab.DetectedMediaUrls.Add(url))
            {
                _ = ExpandAndRegisterHlsAsync(tab, url, effectiveReferer, reqOrigin, reqCookie, reqUserAgent);
            }
            return;
        }

        string category = "";
        string formatBadge = "";
        int priority = 500;

        // 1. Clasificación por extensión o Content-Type (estricta para evitar archivos basura)
        if (pathLower.EndsWith(".mp4") || ctLower.Contains("video/mp4")) { category = "Video"; formatBadge = "MP4"; priority = 700; }
        else if (pathLower.EndsWith(".webm") || ctLower.Contains("video/webm")) { category = "Video"; formatBadge = "WEBM"; priority = 690; }
        else if (pathLower.EndsWith(".mkv") || ctLower.Contains("video/x-matroska")) { category = "Video"; formatBadge = "MKV"; priority = 690; }
        else if (ctLower.StartsWith("video/") && contentLength >= 350_000) { category = "Video"; formatBadge = "VIDEO"; priority = 650; }
        else if (pathLower.EndsWith(".mp3") || ctLower.Contains("audio/mpeg")) { category = "Audio"; formatBadge = "MP3"; priority = 600; }
        else if (pathLower.EndsWith(".m4a") || pathLower.EndsWith(".aac") || ctLower.Contains("audio/mp4")) { category = "Audio"; formatBadge = "M4A"; priority = 600; }
        else if (pathLower.EndsWith(".wav") || ctLower.Contains("audio/wav")) { category = "Audio"; formatBadge = "WAV"; priority = 590; }
        else if (pathLower.EndsWith(".flac") || ctLower.Contains("audio/flac")) { category = "Audio"; formatBadge = "FLAC"; priority = 590; }
        else if (pathLower.EndsWith(".ogg") || ctLower.Contains("audio/ogg")) { category = "Audio"; formatBadge = "OGG"; priority = 580; }
        else if (pathLower.EndsWith(".pdf") || ctLower.Contains("application/pdf")) { category = "Documento"; formatBadge = "PDF"; priority = 550; }
        else if (pathLower.EndsWith(".zip") || ctLower.Contains("application/zip")) { category = "Documento"; formatBadge = "ZIP"; priority = 540; }
        else if (pathLower.EndsWith(".rar") || ctLower.Contains("rar")) { category = "Documento"; formatBadge = "RAR"; priority = 540; }
        else if (pathLower.EndsWith(".7z") || ctLower.Contains("7z")) { category = "Documento"; formatBadge = "7Z"; priority = 540; }
        else if (pathLower.EndsWith(".docx") || pathLower.EndsWith(".doc") || ctLower.Contains("word")) { category = "Documento"; formatBadge = "DOCX"; priority = 530; }
        else if (pathLower.EndsWith(".xlsx") || pathLower.EndsWith(".xls") || ctLower.Contains("spreadsheet") || ctLower.Contains("excel")) { category = "Documento"; formatBadge = "XLSX"; priority = 530; }
        else if (pathLower.EndsWith(".pptx") || pathLower.EndsWith(".ppt") || ctLower.Contains("presentation") || ctLower.Contains("powerpoint")) { category = "Documento"; formatBadge = "PPTX"; priority = 530; }
        else if (ctLower.StartsWith("image/") && contentLength >= 250_000)
        {
            category = "Imagen";
            priority = 300;
            if (pathLower.EndsWith(".png") || ctLower.Contains("png")) formatBadge = "PNG";
            else if (pathLower.EndsWith(".webp") || ctLower.Contains("webp")) formatBadge = "WEBP";
            else formatBadge = "JPG";
        }
        else if (categoryHint == "Video" && (pathLower.Contains(".mp4") || pathLower.Contains("video")))
        {
            category = "Video";
            formatBadge = "MP4";
            priority = 680;
        }

        if (string.IsNullOrEmpty(category)) return;
        if (!tab.DetectedMediaUrls.Add(url)) return;

        // Límite estricto de 50 elementos por pestaña para garantizar consumo despreciable de RAM (< 35 KB)
        if (tab.DetectedMedia.Count >= 50)
        {
            var last = tab.DetectedMedia[^1];
            tab.DetectedMediaUrls.Remove(last.Url);
            tab.DetectedMedia.RemoveAt(tab.DetectedMedia.Count - 1);
        }

        string baseTitle = MediaExtractorService.SanitizeTitle(!string.IsNullOrWhiteSpace(tab.Title) ? tab.Title : uri.Host);
        if (string.IsNullOrWhiteSpace(baseTitle)) baseTitle = "Archivo";

        string rawFileName = Path.GetFileName(uri.LocalPath);
        string fileName;
        if (!string.IsNullOrWhiteSpace(rawFileName) && rawFileName.Length >= 4 && rawFileName.Length <= 64 &&
            !System.Text.RegularExpressions.Regex.IsMatch(rawFileName, @"^[0-9a-fA-F_-]{16,}(\.[a-z0-9]+)?$"))
        {
            try { rawFileName = Uri.UnescapeDataString(rawFileName); } catch { }
            fileName = MediaExtractorService.SanitizeTitle(rawFileName);
            if (!Path.HasExtension(fileName))
            {
                fileName += $".{formatBadge.ToLowerInvariant()}";
            }
        }
        else
        {
            fileName = $"{baseTitle}.{formatBadge.ToLowerInvariant()}";
        }

        string icon = category switch
        {
            "Video" => "🎬",
            "Audio" => "🎵",
            "Imagen" => "🖼️",
            _ => "📄"
        };

        string sizeDisplay = contentLength > 0
            ? $"{FormatBytes(contentLength)} • Descarga directa"
            : "Descarga directa";

        var item = new DetectedMediaItem
        {
            Url = url,
            Referer = effectiveReferer,
            CustomOriginHeader = reqOrigin,
            CustomCookieHeader = reqCookie,
            CustomAuthorizationHeader = reqAuth,
            CustomUserAgent = reqUserAgent,
            FileName = fileName,
            Category = category,
            CategoryIcon = icon,
            FormatBadge = formatBadge,
            SizeBytes = contentLength,
            VideoSizeBytes = contentLength,
            SizeDisplay = sizeDisplay,
            PriorityScore = priority + (int)Math.Min(99, contentLength / (1024 * 1024))
        };

        InsertMediaItemSorted(tab, item);

        if (_activeTab == tab)
        {
            UpdateMediaSnifferUI();
        }
    }

    private async Task ExpandAndRegisterHlsAsync(
        BrowserTab tab,
        string m3u8Url,
        string? customReferer = null,
        string? customOrigin = null,
        string? customCookie = null,
        string? customUserAgent = null)
    {
        try
        {
            string pageUrl = !string.IsNullOrWhiteSpace(customReferer) ? customReferer : tab.Url;
            string pageTitle = tab.Title;
            var hlsItems = await MediaExtractorService.ExpandHlsPlaylistAsync(m3u8Url, pageUrl, pageTitle);
            if (hlsItems.Count == 0) return;

            await Dispatcher.InvokeAsync(() =>
            {
                if (tab.IsDisposed || !tab.IsMediaSnifferActive) return;
                foreach (var item in hlsItems)
                {
                    item.CustomOriginHeader = customOrigin;
                    item.CustomCookieHeader = customCookie;
                    item.CustomUserAgent = customUserAgent;
                    if (tab.DetectedMediaUrls.Add(item.Url))
                    {
                        InsertMediaItemSorted(tab, item);
                    }
                }
                if (_activeTab == tab)
                {
                    UpdateMediaSnifferUI();
                }
            });
        }
        catch { }
    }

    private static string FormatBytes(long bytes) => MediaExtractorService.FormatBytes(bytes);

    private void UpdateMediaSnifferUI()
    {
        if (TxtMediaSnifferLabel == null) return;

        bool active = _activeTab?.IsMediaSnifferActive ?? false;
        int count = _activeTab?.DetectedMedia.Count ?? 0;

        _isUpdatingSnifferCheck = true;
        if (ChkKeepSnifferOnNavigate != null)
        {
            ChkKeepSnifferOnNavigate.IsChecked = _activeTab?.KeepSnifferActiveOnNavigate ?? false;
        }
        _isUpdatingSnifferCheck = false;

        TxtMediaSnifferLabel.Text = active && count > 0 ? $"Multimedia ({count})" : "Multimedia";
        DotMediaSnifferStatus.Fill = active
            ? new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81))
            : new SolidColorBrush(Color.FromRgb(0x71, 0x71, 0x7A));

        BorderMediaSniffer.Background = active
            ? new SolidColorBrush(Color.FromRgb(0x1E, 0x3A, 0x3A))
            : new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x36));

        if (TxtToggleSnifferBtn != null && BtnToggleSnifferActive != null && TxtSnifferStatusSubtitle != null)
        {
            if (active)
            {
                TxtToggleSnifferBtn.Text = "⏹ Desactivar";
                BtnToggleSnifferActive.Background = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                TxtSnifferStatusSubtitle.Text = _isExtractingYouTube
                    ? "⏳ Analizando calidades HD del video..."
                    : $"🟢 Activo en esta pestaña ({count} listos para descargar)";
                TxtSnifferStatusSubtitle.Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
            }
            else
            {
                TxtToggleSnifferBtn.Text = "⚡ Activar aquí";
                BtnToggleSnifferActive.Background = new SolidColorBrush(Color.FromRgb(0x63, 0x66, 0xF1));
                TxtSnifferStatusSubtitle.Text = "Apagado (0 MB RAM en reposo)";
                TxtSnifferStatusSubtitle.Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
            }
        }

        RefreshFilteredMediaList();
    }

    private void RefreshFilteredMediaList()
    {
        if (ItemsDetectedMedia == null || TxtMediaEmptyState == null || ScrollDetectedMedia == null) return;

        if (_activeTab == null || !_activeTab.IsMediaSnifferActive)
        {
            ItemsDetectedMedia.ItemsSource = null;
            ScrollDetectedMedia.Visibility = Visibility.Collapsed;
            TxtMediaEmptyState.Text = "Presiona '⚡ Activar aquí' para extraer videos (YouTube 1080p/720p, Shorts, redes sociales, streams HLS), audios y documentos sin archivos basura.";
            TxtMediaEmptyState.Visibility = Visibility.Visible;
            return;
        }

        IEnumerable<DetectedMediaItem> query = _activeTab.DetectedMedia;
        if (_currentMediaFilter == "Video")
            query = query.Where(x => x.Category == "Video");
        else if (_currentMediaFilter == "Audio")
            query = query.Where(x => x.Category == "Audio");
        else if (_currentMediaFilter == "Imagen")
            query = query.Where(x => x.Category == "Imagen");
        else if (_currentMediaFilter == "Documento")
            query = query.Where(x => x.Category == "Documento");

        var list = query.ToList();
        ItemsDetectedMedia.ItemsSource = list;

        if (list.Count == 0)
        {
            ScrollDetectedMedia.Visibility = Visibility.Collapsed;
            TxtMediaEmptyState.Text = _isExtractingYouTube
                ? "⏳ Extrayendo calidades 1080p HD, 720p HD y Audio del video de YouTube..."
                : "Escuchando videos y audios reales en esta página... Dale Play al video para capturar el flujo limpio.";
            TxtMediaEmptyState.Visibility = Visibility.Visible;
        }
        else
        {
            TxtMediaEmptyState.Visibility = Visibility.Collapsed;
            ScrollDetectedMedia.Visibility = Visibility.Visible;
        }
    }

    private void BtnCopyMediaUrl_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is DetectedMediaItem item && !string.IsNullOrWhiteSpace(item.Url))
        {
            try
            {
                Clipboard.SetText(item.Url);
                PopupMediaSniffer.IsOpen = false;
                ShowToast($"📋 Enlace copiado: {item.FileName}");
            }
            catch { }
        }
    }

    private async void BtnDownloadDetectedMedia_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not DetectedMediaItem item || string.IsNullOrWhiteSpace(item.Url))
            return;

        PopupMediaSniffer.IsOpen = false;

        string safeName = item.FileName;
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            safeName = safeName.Replace(c, '_');
        }

        string downloadsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var sfd = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Guardar archivo detectado — Lex Junior",
            FileName = safeName,
            InitialDirectory = Directory.Exists(downloadsFolder) ? downloadsFolder : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Filter = "Todos los archivos (*.*)|*.*"
        };

        if (sfd.ShowDialog(this) != true) return;

        string targetPath = sfd.FileName;
        string displayName = Path.GetFileName(targetPath);
        bool isIncog = _activeTab?.IsIncognito == true;
        var dlEntry = DownloadManager.RegisterDownload(displayName, targetPath, item.Url, item.SizeBytes, "En progreso", isIncog);
        RefreshDownloadsPopupUI();
        PopupDownloads.IsOpen = true;
        ShowToast($"⬇️ Descargando: {displayName}", "Detalles ⬇", () =>
        {
            RefreshDownloadsPopupUI();
            PopupDownloads.IsOpen = true;
        });

        try
        {
            if (!item.IsYouTubeStream &&
                string.IsNullOrWhiteSpace(item.CustomCookieHeader) &&
                _activeTab?.WebView?.CoreWebView2?.CookieManager != null)
            {
                try
                {
                    var cookies = await _activeTab.WebView.CoreWebView2.CookieManager.GetCookiesAsync(item.Url);
                    if (cookies != null && cookies.Count > 0)
                    {
                        item.CustomCookieHeader = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
                    }
                }
                catch { }
            }

            long lastReportTick = 0;
            long finalSize = await MediaExtractorService.DownloadMediaItemAsync(
                item,
                targetPath,
                onProgress: (received, total) =>
                {
                    long now = Environment.TickCount64;
                    if (now - lastReportTick >= 180 || (total > 0 && received >= total))
                    {
                        lastReportTick = now;
                        DownloadManager.UpdateDownload(dlEntry.Id, received, total > 0 ? total : item.SizeBytes, "En progreso");
                    }
                });

            DownloadManager.UpdateDownload(dlEntry.Id, finalSize, finalSize, "Completado");
            ShowToast($"✅ Descarga completada: {displayName}", "Ver archivo 📂", () =>
            {
                RefreshDownloadsPopupUI();
                PopupDownloads.IsOpen = true;
            });
        }
        catch (Exception ex)
        {
            DownloadManager.UpdateDownload(dlEntry.Id, 0, item.SizeBytes, "Error");
            ShowToast($"⚠️ Error al descargar: {ex.Message}", "Detalles ⬇", () =>
            {
                RefreshDownloadsPopupUI();
                PopupDownloads.IsOpen = true;
            });
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // HISTORIAL DE DESCARGAS LIGERO (0% CPU EN REPOSO)
    // ─────────────────────────────────────────────────────────────────────
    private void OnDownloadsChanged()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (PopupDownloads != null && PopupDownloads.IsOpen)
            {
                RefreshDownloadsPopupUI();
            }
        });
    }

    private void BtnDownloads_Click(object sender, RoutedEventArgs e)
    {
        RefreshDownloadsPopupUI();
        PopupDownloads.IsOpen = !PopupDownloads.IsOpen;
    }

    private void RefreshDownloadsPopupUI()
    {
        if (ItemsDownloadsHistory == null || TxtDownloadsEmptyState == null || ScrollDownloadsHistory == null)
            return;

        var entries = DownloadManager.GetEntries();
        if (ItemsDownloadsHistory.ItemsSource != entries)
        {
            ItemsDownloadsHistory.ItemsSource = entries;
        }

        if (TxtDownloadsCountSubtitle != null)
        {
            TxtDownloadsCountSubtitle.Text = entries.Count == 0
                ? "Ligero · 0% consumo en segundo plano"
                : $"{entries.Count} {(entries.Count == 1 ? "archivo registrado" : "archivos registrados")} · 0% CPU en reposo";
        }

        if (entries.Count == 0)
        {
            ScrollDownloadsHistory.Visibility = Visibility.Collapsed;
            TxtDownloadsEmptyState.Visibility = Visibility.Visible;
        }
        else
        {
            TxtDownloadsEmptyState.Visibility = Visibility.Collapsed;
            ScrollDownloadsHistory.Visibility = Visibility.Visible;
        }
    }

    private void BtnOpenDownloadsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Directory.Exists(folder))
            {
                folder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void BtnClearDownloadsHistory_Click(object sender, RoutedEventArgs e)
    {
        DownloadManager.ClearAll();
        RefreshDownloadsPopupUI();
    }

    private void BtnOpenDownloadedFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is DownloadEntry entry && !string.IsNullOrWhiteSpace(entry.FilePath))
        {
            if (!File.Exists(entry.FilePath))
            {
                ShowToast("⚠️ El archivo ya no se encuentra en esa ubicación.");
                return;
            }
            try
            {
                PopupDownloads.IsOpen = false;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = entry.FilePath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ShowToast($"⚠️ No se pudo abrir el archivo: {ex.Message}");
            }
        }
    }

    private void BtnShowDownloadedFileInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is DownloadEntry entry && !string.IsNullOrWhiteSpace(entry.FilePath))
        {
            try
            {
                PopupDownloads.IsOpen = false;
                if (File.Exists(entry.FilePath))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{entry.FilePath}\"",
                        UseShellExecute = true
                    });
                }
                else
                {
                    string? dir = Path.GetDirectoryName(entry.FilePath);
                    if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = dir,
                            UseShellExecute = true
                        });
                    }
                    else
                    {
                        ShowToast("⚠️ El archivo o la carpeta ya no existen.");
                    }
                }
            }
            catch { }
        }
    }

    private void BtnRemoveDownloadEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is DownloadEntry entry)
        {
            DownloadManager.DeleteEntry(entry.Id);
            RefreshDownloadsPopupUI();
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // AUTO-ACTUALIZADOR CONECTADO A GITHUB RELEASES (JuniorJhair/Lex-Junior)
    // ─────────────────────────────────────────────────────────────────────
    private bool _isDownloadingBrowserUpdate = false;

    private async void BtnUpdateAvailable_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloadingBrowserUpdate) return;
        var update = UpdateManager.AvailableUpdate;
        if (update == null) return;

        var confirm = MessageBox.Show(
            $"Está disponible la nueva versión {update.VersionTag} de Lex Junior Browser.\n\n" +
            $"{(string.IsNullOrWhiteSpace(update.ReleaseNotes) ? "Incluye mejoras de rendimiento, seguridad y estabilidad." : update.ReleaseNotes)}\n\n" +
            "¿Deseas descargar e instalar la actualización ahora mismo? (Tus marcadores, historial y configuración se conservarán intactos).",
            "Actualización disponible — Lex Junior",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (confirm != MessageBoxResult.Yes) return;

        _isDownloadingBrowserUpdate = true;
        BtnUpdateAvailable.IsEnabled = false;

        var progress = new Progress<int>(pct =>
        {
            TxtUpdateAvailableLabel.Text = $"Descargando {pct}%";
        });

        bool ok = await UpdateManager.DownloadAndApplyUpdateAsync(update, progress);
        if (!ok)
        {
            _isDownloadingBrowserUpdate = false;
            BtnUpdateAvailable.IsEnabled = true;
            TxtUpdateAvailableLabel.Text = $"Actualizar ({update.VersionTag})";
            ShowToast("⚠️ No se pudo descargar la actualización. Verifica tu conexión a internet.");
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // BÓVEDA DE CONTRASEÑAS, BLOQUEO INTELIGENTE Y MODO INVITADO (0% CPU)
    // ─────────────────────────────────────────────────────────────────────
    private bool _isGuestSession = false;
    private string _pendingSaveOriginUrl = string.Empty;
    private DispatcherTimer? _promptPeekTimer;
    private DispatcherTimer? _clipboardShieldTimer;
    private string _lastCopiedSecretPassword = string.Empty;

    private void BtnPasswordVault_Click(object sender, RoutedEventArgs e)
    {
        OpenPasswordVault();
    }

    public void OpenPasswordVault(string initialSection = "Vault")
    {
        if (_isGuestSession)
        {
            if (!TryUnlockFromGuestMode("Desbloquea con tu PIN de propietario para acceder a la Bóveda de Contraseñas"))
                return;
        }
        else if (PasswordManager.ShouldRequireUnlockForVaultAction())
        {
            var pinDialog = new PinPromptDialog("Confirma tu PIN para abrir la Bóveda de Contraseñas") { Owner = this };
            if (pinDialog.ShowDialog() != true)
                return;
        }

        var vaultWin = new PasswordVaultWindow(initialSection)
        {
            Owner = this
        };
        vaultWin.NavigateRequested += async (targetUrl) =>
        {
            if (IsSafeHttpUrl(targetUrl))
            {
                if (_activeTab?.WebView?.CoreWebView2 != null && _activeTab.IsNewTab)
                {
                    _activeTab.WebView.CoreWebView2.Navigate(targetUrl);
                }
                else
                {
                    await AddNewTabAsync(targetUrl, isIncognito: _isGuestSession);
                }
            }
        };
        vaultWin.ClipboardShieldActivated += ActivateClipboardShield;
        vaultWin.ShowDialog();
        ApplySettings();
    }

    public void ActivateClipboardShield(string secretPassword)
    {
        if (string.IsNullOrEmpty(secretPassword)) return;
        try
        {
            Clipboard.SetText(secretPassword);
            _lastCopiedSecretPassword = secretPassword;

            if (_clipboardShieldTimer == null)
            {
                _clipboardShieldTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
                _clipboardShieldTimer.Tick += (_, _) =>
                {
                    _clipboardShieldTimer.Stop();
                    try
                    {
                        if (!string.IsNullOrEmpty(_lastCopiedSecretPassword) &&
                            Clipboard.ContainsText() &&
                            Clipboard.GetText() == _lastCopiedSecretPassword)
                        {
                            Clipboard.Clear();
                            ShowToast("🛡️ Escudo de Portapapeles: la contraseña copiada se limpió automáticamente (20s).");
                        }
                    }
                    catch { }
                    _lastCopiedSecretPassword = string.Empty;
                };
            }

            _clipboardShieldTimer.Stop();
            _clipboardShieldTimer.Start();
            ShowToast("📋 Contraseña copiada — 🛡️ Escudo activo: se borrará sola del portapapeles en 20s.");
        }
        catch { }
    }

    private void PromptSaveOrUpdatePassword(string url, string username, string password)
    {
        if (_isGuestSession || !SettingsManager.Current.EnablePasswordManager)
            return;

        string baseDomain = PasswordVaultStore.ExtractBaseDomain(url);
        if (string.IsNullOrEmpty(baseDomain) || PasswordManager.Store.IsDomainExcluded(baseDomain))
            return;

        var existing = PasswordManager.Store.FindExistingCredential(baseDomain, username);
        if (existing != null && existing.Password == password)
        {
            // Ya está guardada exactamente con la misma contraseña: no molestar al usuario
            return;
        }

        _pendingSaveOriginUrl = url;
        TxtSavePwdPromptTitle.Text = existing != null
            ? $"¿Actualizar contraseña para {baseDomain}?"
            : $"¿Guardar contraseña para {baseDomain}?";

        TxtSavePwdDomainSubtitle.Text = $"Se asociará al dominio '{baseDomain}' y funcionará en todas sus rutas.";
        TxtPromptSaveUsername.Text = username;
        PwdPromptSavePassword.Password = password;
        TxtPromptSavePasswordPlain.Text = password;
        TxtPromptSavePasswordPlain.Visibility = Visibility.Collapsed;
        PwdPromptSavePassword.Visibility = Visibility.Visible;
        BtnPromptPeekPassword.Content = "👁️";

        SavePasswordBanner.Visibility = Visibility.Visible;
    }

    private void BtnPromptPeekPassword_Click(object sender, RoutedEventArgs e)
    {
        if (TxtPromptSavePasswordPlain.Visibility == Visibility.Visible)
        {
            _promptPeekTimer?.Stop();
            PwdPromptSavePassword.Password = TxtPromptSavePasswordPlain.Text;
            TxtPromptSavePasswordPlain.Visibility = Visibility.Collapsed;
            PwdPromptSavePassword.Visibility = Visibility.Visible;
            BtnPromptPeekPassword.Content = "👁️";
            return;
        }

        TxtPromptSavePasswordPlain.Text = PwdPromptSavePassword.Password;
        PwdPromptSavePassword.Visibility = Visibility.Collapsed;
        TxtPromptSavePasswordPlain.Visibility = Visibility.Visible;
        BtnPromptPeekPassword.Content = "🙈 5s";

        if (_promptPeekTimer == null)
        {
            _promptPeekTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _promptPeekTimer.Tick += (_, _) =>
            {
                _promptPeekTimer.Stop();
                if (TxtPromptSavePasswordPlain.Visibility == Visibility.Visible)
                {
                    PwdPromptSavePassword.Password = TxtPromptSavePasswordPlain.Text;
                    TxtPromptSavePasswordPlain.Visibility = Visibility.Collapsed;
                    PwdPromptSavePassword.Visibility = Visibility.Visible;
                    BtnPromptPeekPassword.Content = "👁️";
                }
            };
        }

        _promptPeekTimer.Stop();
        _promptPeekTimer.Start();
    }

    private void BtnConfirmSavePassword_Click(object sender, RoutedEventArgs e)
    {
        string pwd = TxtPromptSavePasswordPlain.Visibility == Visibility.Visible
            ? TxtPromptSavePasswordPlain.Text
            : PwdPromptSavePassword.Password;

        string user = TxtPromptSaveUsername.Text.Trim();
        string domain = PasswordVaultStore.ExtractBaseDomain(_pendingSaveOriginUrl);

        if (!string.IsNullOrEmpty(domain) && !string.IsNullOrEmpty(pwd))
        {
            PasswordManager.Store.SaveOrUpdateCredential(_pendingSaveOriginUrl, user, pwd);
            ShowToast($"🔑 Contraseña guardada para {domain} (disponible en tu Bóveda 🔑).");
        }

        _promptPeekTimer?.Stop();
        SavePasswordBanner.Visibility = Visibility.Collapsed;
    }

    private void BtnDismissSavePassword_Click(object sender, RoutedEventArgs e)
    {
        _promptPeekTimer?.Stop();
        SavePasswordBanner.Visibility = Visibility.Collapsed;
    }

    private void BtnNeverSaveForDomain_Click(object sender, RoutedEventArgs e)
    {
        string domain = PasswordVaultStore.ExtractBaseDomain(_pendingSaveOriginUrl);
        if (!string.IsNullOrEmpty(domain))
        {
            PasswordManager.Store.AddExcludedDomain(domain);
            ShowToast($"🚫 No se volverá a preguntar en {domain} (puedes revertirlo en la Bóveda 🔑).");
        }

        _promptPeekTimer?.Stop();
        SavePasswordBanner.Visibility = Visibility.Collapsed;
    }

    private void TryAutofillSavedCredentials(BrowserTab tab, string pageUrl)
    {
        try
        {
            if (tab?.WebView?.CoreWebView2 == null || tab.IsDisposed || tab.IsIncognito || _isGuestSession)
                return;

            // Solo autocompletar en sitios HTTPS o localhost (protección anti-phishing)
            if (!PasswordVaultStore.IsDomainMatch(pageUrl, pageUrl, requireHttpsForAutofill: true))
                return;

            var matches = PasswordManager.Store.GetCredentialsForUrl(pageUrl);
            if (matches.Count == 0) return;

            var primary = matches[0];
            string payload = JsonSerializer.Serialize(new
            {
                action = "autofill_credentials",
                username = primary.Username,
                password = primary.Password
            });

            tab.WebView.CoreWebView2.PostWebMessageAsJson(payload);
        }
        catch { }
    }

    // ─────────────────────────────────────────────────────────────────────
    // PANTALLA DE BLOQUEO AL INICIAR, CONFIGURACIÓN INICIAL Y MODO INVITADO
    // ─────────────────────────────────────────────────────────────────────
    private void ShowFirstTimeSecuritySetupOverlay()
    {
        LockOverlayFirstTimeSetupPanel.Visibility = Visibility.Visible;
        LockOverlayPinPanel.Visibility = Visibility.Collapsed;
        LockOverlayRecoveryPanel.Visibility = Visibility.Collapsed;
        TxtFirstSetupError.Visibility = Visibility.Collapsed;
        PwdFirstSetupPin.Password = string.Empty;
        BrowserLockOverlay.Visibility = Visibility.Visible;

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => PwdFirstSetupPin.Focus());
    }

    private void BtnFirstSetupSavePin_Click(object sender, RoutedEventArgs e)
    {
        string pin = PwdFirstSetupPin.Password;
        if (string.IsNullOrWhiteSpace(pin))
        {
            TxtFirstSetupError.Text = "⚠️ Escribe un PIN o contraseña (ej. 6 dígitos o letras) o pulsa abajo 'No me gustaría usar contraseña'.";
            TxtFirstSetupError.Visibility = Visibility.Visible;
            PwdFirstSetupPin.Focus();
            return;
        }

        string mode = "SmartSession";
        if (CmbFirstSetupLockMode.SelectedItem is ComboBoxItem item && item.Tag is string tag && !string.IsNullOrWhiteSpace(tag))
        {
            mode = tag;
        }

        string question = TxtFirstSetupQuestion.Text.Trim();
        string answer = TxtFirstSetupAnswer.Text.Trim();

        string recoveryCode = PasswordManager.ConfigureMasterPassword(pin, mode, question, answer);

        MessageBox.Show(
            $"¡Tu PIN/contraseña ha quedado activado con éxito!\n\n" +
            $"• Código de Recuperación de Emergencia: {recoveryCode}\n" +
            $"(Guárdalo o recuérdalo junto con tu respuesta secreta por si algún día olvidas tu PIN; así podrás recuperar el acceso sin perder tus contraseñas guardadas).",
            "Protección Activada — Lex Junior",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        CompleteOverlayUnlock();
        ShowToast("🔒 Protección con PIN activada. Puedes cambiar tus opciones cuando quieras en el botón 🔑.");
    }

    private void BtnFirstSetupSkipNoPassword_Click(object sender, RoutedEventArgs e)
    {
        SettingsManager.Current.BrowserLockMode = "None";
        SettingsManager.Current.HasCompletedInitialSecuritySetup = true;
        SettingsManager.Save();

        CompleteOverlayUnlock();
        ShowToast("🔓 Configurado sin contraseña al abrir. Puedes activarla cuando quieras desde el botón 🔑.");
    }

    private void ShowBrowserLockOverlay()
    {
        var s = SettingsManager.Current;
        TxtLockOverlayModeSubtitle.Text = s.BrowserLockMode == "SmartSession"
            ? "Modo Inteligente activo (Sesión de Windows nueva o reanudada)"
            : "Protección siempre activa al abrir el navegador";

        LockOverlayFirstTimeSetupPanel.Visibility = Visibility.Collapsed;
        LockOverlayRecoveryPanel.Visibility = Visibility.Collapsed;
        LockOverlayPinPanel.Visibility = Visibility.Visible;
        TxtOverlayPinError.Visibility = Visibility.Collapsed;
        PwdOverlayPinInput.Password = string.Empty;
        BrowserLockOverlay.Visibility = Visibility.Visible;

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => PwdOverlayPinInput.Focus());
    }

    private async void CompleteOverlayUnlock()
    {
        PasswordManager.MarkSessionUnlocked();
        _isGuestSession = false;
        BrowserLockOverlay.Visibility = Visibility.Collapsed;
        ApplySettings();

        if (Tabs.Count == 0)
        {
            await RestoreOrInitializeSessionTabsAsync();
        }
    }

    private void PwdOverlayPinInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        TxtOverlayPinError.Visibility = Visibility.Collapsed;
        string current = PwdOverlayPinInput.Password;
        int expectedLen = SettingsManager.Current.MasterPasswordLength;

        if (expectedLen > 0 && current.Length == expectedLen && PasswordManager.VerifyMasterPassword(current))
        {
            CompleteOverlayUnlock();
        }
    }

    private void PwdOverlayPinInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            BtnOverlayUnlock_Click(sender, e);
        }
    }

    private void BtnOverlayUnlock_Click(object sender, RoutedEventArgs e)
    {
        if (PasswordManager.VerifyMasterPassword(PwdOverlayPinInput.Password))
        {
            CompleteOverlayUnlock();
        }
        else
        {
            TxtOverlayPinError.Text = "❌ PIN o contraseña incorrecta. Inténtalo de nuevo o entra como Invitado.";
            TxtOverlayPinError.Visibility = Visibility.Visible;
            PwdOverlayPinInput.SelectAll();
            PwdOverlayPinInput.Focus();
        }
    }

    private async void BtnEnterGuestMode_Click(object sender, RoutedEventArgs e)
    {
        _isGuestSession = true;
        BrowserLockOverlay.Visibility = Visibility.Collapsed;
        ApplySettings();

        if (Tabs.Count == 0)
        {
            string? launchUrl = !string.IsNullOrWhiteSpace(_initialUrl) ? _initialUrl : null;
            _initialUrl = null;
            await AddNewTabAsync(launchUrl, isIncognito: true);
        }

        ShowToast("👤 Modo Invitado activo: navegas en una sesión aislada sin acceso a las contraseñas ni marcadores del propietario.");
    }

    private async void BtnUnlockFromGuest_Click(object sender, RoutedEventArgs e)
    {
        await UnlockFromGuestToOwnerAsync();
    }

    private async Task<bool> UnlockFromGuestToOwnerAsync()
    {
        var pinDialog = new PinPromptDialog("Ingresa tu PIN de propietario para salir del Modo Invitado y restaurar tus páginas y contraseñas") { Owner = this };
        if (pinDialog.ShowDialog() == true)
        {
            _isGuestSession = false;
            PasswordManager.MarkSessionUnlocked();
            ApplySettings();

            // Limpiar pestañas temporales del invitado sin invocar Close() en la ventana
            foreach (var t in Tabs.ToList())
            {
                t.IsDisposed = true;
                try { t.WebView?.CoreWebView2?.Stop(); } catch { }
                try { t.WebView?.Dispose(); } catch { }
            }
            Tabs.Clear();
            BrowserContainer.Children.Clear();
            _activeTab = null;

            await RestoreOrInitializeSessionTabsAsync();
            ShowToast("🔓 Sesión de propietario desbloqueada: tus pestañas, marcadores y contraseñas han sido restaurados.");
            return true;
        }
        return false;
    }

    private bool TryUnlockFromGuestMode(string reason)
    {
        var pinDialog = new PinPromptDialog(reason) { Owner = this };
        if (pinDialog.ShowDialog() == true)
        {
            _isGuestSession = false;
            ApplySettings();
            ShowToast("🔓 Sesión de propietario desbloqueada: tus marcadores, historial y bóveda están disponibles.");
            return true;
        }
        return false;
    }

    private void BtnOverlayShowRecovery_Click(object sender, RoutedEventArgs e)
    {
        var s = SettingsManager.Current;
        TxtOverlayRecoveryQuestion.Text = string.IsNullOrWhiteSpace(s.SecurityQuestion)
            ? "Ingresa tu Código de Recuperación de Emergencia (ej. LEX-XXXX-XXXX) para conservar tus contraseñas:"
            : $"Pregunta secreta: \"{s.SecurityQuestion}\" (o ingresa tu Código de Recuperación {s.RecoveryCodeHint}):";

        TxtOverlayRecoveryError.Visibility = Visibility.Collapsed;
        TxtOverlayRecoveryAnswer.Text = string.Empty;
        PwdOverlayNewPin.Password = string.Empty;
        LockOverlayPinPanel.Visibility = Visibility.Collapsed;
        LockOverlayRecoveryPanel.Visibility = Visibility.Visible;
        TxtOverlayRecoveryAnswer.Focus();
    }

    private void BtnOverlayBackToPin_Click(object sender, RoutedEventArgs e)
    {
        LockOverlayRecoveryPanel.Visibility = Visibility.Collapsed;
        LockOverlayPinPanel.Visibility = Visibility.Visible;
        PwdOverlayPinInput.Focus();
    }

    private void BtnOverlayApplyRecovery_Click(object sender, RoutedEventArgs e)
    {
        string answer = TxtOverlayRecoveryAnswer.Text;
        string newPin = PwdOverlayNewPin.Password;

        if (!PasswordManager.VerifyRecoveryAnswerOrCode(answer))
        {
            TxtOverlayRecoveryError.Text = "❌ La respuesta secreta o el código de recuperación no coinciden.";
            TxtOverlayRecoveryError.Visibility = Visibility.Visible;
            return;
        }

        if (string.IsNullOrWhiteSpace(newPin))
        {
            TxtOverlayRecoveryError.Text = "⚠️ Escribe tu nuevo PIN o contraseña para continuar.";
            TxtOverlayRecoveryError.Visibility = Visibility.Visible;
            return;
        }

        var s = SettingsManager.Current;
        PasswordManager.ConfigureMasterPassword(newPin, s.BrowserLockMode, s.SecurityQuestion, answer);
        CompleteOverlayUnlock();
        ShowToast("✅ ¡PIN actualizado con éxito! Todas tus contraseñas guardadas siguen intactas.");
    }

    private void BtnOverlayEmergencyReset_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "⚠️ CONFIRMACIÓN DE RESTABLECIMIENTO DE EMERGENCIA\n\n" +
            "¿Qué ocurrirá ahora mismo?\n" +
            "• SE ELIMINARÁN: Únicamente las contraseñas guardadas en la bóveda (por seguridad para que nadie ajeno vea tus cuentas).\n" +
            "• SE CONSERVARÁN INTACTOS: Tus marcadores, páginas ancladas, historial, descargas y configuración del navegador.\n" +
            "• El bloqueo por PIN se desactivará para que puedas usar tu navegador.\n\n" +
            "¿Deseas continuar?",
            "Restablecer acceso — Lex Junior",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm == MessageBoxResult.Yes)
        {
            PasswordManager.EmergencyResetLockAndClearVault();
            CompleteOverlayUnlock();
            ShowToast("🔓 Acceso restablecido (se vació solo la bóveda de contraseñas; tus marcadores y ajustes siguen intactos).");
        }
    }

    private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (sender is ScrollViewer scrollViewer)
        {
            double deltaUnits = e.Delta / 120.0;
            double scrollPixels = deltaUnits * 28.0;
            double newOffset = Math.Clamp(scrollViewer.VerticalOffset - scrollPixels, 0, scrollViewer.ScrollableHeight);
            scrollViewer.ScrollToVerticalOffset(newOffset);
        }
    }
}
