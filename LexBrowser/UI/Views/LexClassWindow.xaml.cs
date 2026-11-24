using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using LexBrowser.Core;
using LexBrowser.Core.Engine;
using LexBrowser.Core.Interop;
using Microsoft.Web.WebView2.Core;

namespace LexBrowser.UI.Views;

/// <summary>
/// Ventana limpia e independiente para Lex Junior Class:
/// sin barra de URL, sin marcadores, sin descargas y sin registro en el historial ni sesiones del navegador.
/// </summary>
public partial class LexClassWindow : Window
{
    private const string FullscreenBridgeScript = """
        (function () {
          if (window.__lexClassNativeBridgeAttached) return;
          window.__lexClassNativeBridgeAttached = true;
          window.addEventListener('keydown', function (ev) {
            if (!ev || ev.repeat) return;
            if (ev.key === 'F11') {
              ev.preventDefault();
              window.chrome && window.chrome.webview && window.chrome.webview.postMessage({ type: 'lexclass_toggle_fullscreen' });
            } else if (ev.key === 'Escape') {
              window.chrome && window.chrome.webview && window.chrome.webview.postMessage({ type: 'lexclass_escape_key' });
            }
          }, true);
        })();
        """;

    private CoreWebView2? _core;
    private bool _isFullScreen = false;
    private readonly FullScreenWindowCoordinator _fullScreenCoordinator = new();
    private FullScreenExitHoverController? _fullScreenExitHoverController;
    private bool _isClosing = false;
    private long _lastF11ToggleTickMs = 0;

    private FullScreenExitHoverController GetFullScreenExitHoverController()
        => _fullScreenExitHoverController ??= new FullScreenExitHoverController(
            this,
            () => RootLayoutGrid,
            () => PopupFullScreenExit,
            () => FullScreenExitCard,
            () => _isFullScreen,
            () => _isClosing);

    private void ToggleFullScreenFromShortcut()
    {
        long now = Environment.TickCount64;
        if (_lastF11ToggleTickMs > 0 && unchecked(now - _lastF11ToggleTickMs) < 250)
            return;
        _lastF11ToggleTickMs = now;
        SetFullScreen(!_isFullScreen);
    }

    internal readonly record struct SafeWindowBounds(double Left, double Top, double Width, double Height);

    internal static SafeWindowBounds ComputeSafeCenteredBounds(
        double workLeft,
        double workTop,
        double workWidth,
        double workHeight,
        double preferredMaxWidth = 1200.0,
        double preferredMaxHeight = 760.0,
        double minWidth = 760.0,
        double minHeight = 480.0,
        double ratio = 0.86)
    {
        if (workWidth <= 0 || workHeight <= 0)
            return new SafeWindowBounds(workLeft, workTop, Math.Max(1.0, preferredMaxWidth), Math.Max(1.0, preferredMaxHeight));

        double safeRatio = Math.Clamp(ratio, 0.4, 1.0);
        double effMinW = Math.Min(Math.Max(1.0, minWidth), workWidth);
        double effMinH = Math.Min(Math.Max(1.0, minHeight), workHeight);

        double targetW = Math.Clamp(Math.Min(preferredMaxWidth, Math.Round(workWidth * safeRatio)), effMinW, workWidth);
        double targetH = Math.Clamp(Math.Min(preferredMaxHeight, Math.Round(workHeight * safeRatio)), effMinH, workHeight);

        double left = Math.Max(workLeft, workLeft + Math.Floor((workWidth - targetW) * 0.5));
        double top = Math.Max(workTop, workTop + Math.Floor((workHeight - targetH) * 0.5));

        if (left + targetW > workLeft + workWidth)
            left = Math.Max(workLeft, workLeft + workWidth - targetW);
        if (top + targetH > workTop + workHeight)
            top = Math.Max(workTop, workTop + workHeight - targetH);

        return new SafeWindowBounds(left, top, targetW, targetH);
    }

    private void ApplySafeInitialWorkAreaBounds()
    {
        try
        {
            var wa = SystemParameters.WorkArea;
            if (wa.Width <= 0 || wa.Height <= 0) return;

            var b = ComputeSafeCenteredBounds(
                wa.Left,
                wa.Top,
                wa.Width,
                wa.Height,
                preferredMaxWidth: 1200.0,
                preferredMaxHeight: 760.0,
                minWidth: MinWidth,
                minHeight: MinHeight);

            Width = b.Width;
            Height = b.Height;
            Left = b.Left;
            Top = b.Top;
        }
        catch { }
    }

    private void ApplySafeNativeMonitorBounds(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || WindowState != WindowState.Normal) return;
        try
        {
            IntPtr monitor = IntPtr.Zero;
            if (NativeMethods.GetCursorPos(out var cursorPt))
            {
                monitor = NativeMethods.MonitorFromPoint(cursorPt, 2 /* MONITOR_DEFAULTTONEAREST */);
            }
            if (monitor == IntPtr.Zero)
            {
                monitor = NativeMethods.MonitorFromWindow(hwnd, 2);
            }
            if (monitor == IntPtr.Zero) return;

            var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return;

            uint dpi = NativeMethods.GetDpiForWindow(hwnd);
            double scale = dpi > 0 ? dpi / 96.0 : 1.0;

            var b = ComputeSafeCenteredBounds(
                info.rcWork.left,
                info.rcWork.top,
                info.rcWork.Width,
                info.rcWork.Height,
                preferredMaxWidth: 1200.0 * scale,
                preferredMaxHeight: 760.0 * scale,
                minWidth: MinWidth * scale,
                minHeight: MinHeight * scale);

            NativeMethods.SetWindowPos(
                hwnd,
                IntPtr.Zero,
                (int)Math.Round(b.Left),
                (int)Math.Round(b.Top),
                (int)Math.Round(b.Width),
                (int)Math.Round(b.Height),
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }
        catch { }
    }

    public LexClassWindow()
    {
        InitializeComponent();
        ApplySafeInitialWorkAreaBounds();
        Loaded += LexClassWindow_Loaded;
        if (PopupFullScreenExit != null)
        {
            PopupFullScreenExit.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            {
                double x = Math.Max(0, (targetSize.Width - popupSize.Width) * 0.5);
                return new[] { new CustomPopupPlacement(new Point(x, 0), PopupPrimaryAxis.Horizontal) };
            };
        }
        Activated += (_, _) =>
        {
            if (_isFullScreen && !_isClosing)
            {
                _fullScreenCoordinator.Reassert(this);
            }
        };
        Deactivated += (_, _) => HideFullScreenExitPopupImmediate();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_STYLE);
        if ((style & NativeMethods.WS_MAXIMIZEBOX) == 0)
        {
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_STYLE, style | NativeMethods.WS_MAXIMIZEBOX);
        }

        string iconPath = InstallerService.EnsureLexClassIconOnDisk();
        string exePath = Environment.ProcessPath ?? InstallerService.InstalledExePath;
        NativeMethods.SetWindowAppUserModelId(
            hwnd,
            InstallerService.LexClassAppUserModelId,
            File.Exists(iconPath) ? $"{iconPath},0" : null,
            "Lex Junior Class",
            !string.IsNullOrWhiteSpace(exePath) ? $"\"{exePath}\" --lexclass" : null);

        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WorkAreaHook);
        ApplySafeNativeMonitorBounds(hwnd);
    }

    private IntPtr WorkAreaHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        return _fullScreenCoordinator.HandleWindowHook(
            this,
            hwnd,
            msg,
            wParam,
            lParam,
            _isFullScreen,
            MinWidth,
            MinHeight,
            clampMinTrackToWorkArea: true,
            ref handled);
    }

    private async void LexClassWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await InitializeWebViewAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var env = await BrowserEngine.GetEnvironmentAsync();
            await ClassWebView.EnsureCoreWebView2Async(env);
            if (_isClosing) return;

            var core = ClassWebView.CoreWebView2;
            _core = core;

            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = true;

            try { await core.AddScriptToExecuteOnDocumentCreatedAsync(FullscreenBridgeScript); } catch { }

            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (s, args) =>
            {
                if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri))
                    return;

                if (uri.Host.Equals(LexClassStudioService.Host, StringComparison.OrdinalIgnoreCase))
                {
                    byte[]? bytes = LexClassStudioService.LoadStaticAsset(uri.AbsolutePath, out string resolvedRel);
                    if (bytes == null)
                    {
                        byte[] notFound = System.Text.Encoding.UTF8.GetBytes("Not found");
                        args.Response = env.CreateWebResourceResponse(
                            new MemoryStream(notFound, writable: false),
                            404,
                            "Not Found",
                            LexClassStudioService.BuildResponseHeaders("text/plain; charset=utf-8"));
                        return;
                    }

                    string headers = LexClassStudioService.BuildResponseHeaders(LexClassStudioService.GetMimeType(resolvedRel));
                    args.Response = env.CreateWebResourceResponse(
                        new MemoryStream(bytes, writable: false),
                        200,
                        "OK",
                        headers);
                    return;
                }

                if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                    uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] blocked = System.Text.Encoding.UTF8.GetBytes("External network disabled in Lex Junior Class window");
                    args.Response = env.CreateWebResourceResponse(
                        new MemoryStream(blocked, writable: false),
                        403,
                        "Forbidden",
                        "Content-Type: text/plain; charset=utf-8");
                }
            };

            core.NavigationStarting += (s, args) =>
            {
                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri))
                {
                    if (uri.Host.Equals(LexClassStudioService.Host, StringComparison.OrdinalIgnoreCase) ||
                        uri.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase) ||
                        uri.Scheme.Equals("blob", StringComparison.OrdinalIgnoreCase) ||
                        uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
                args.Cancel = true;
            };

            core.NewWindowRequested += (s, args) =>
            {
                args.Handled = true;
            };

            core.ContainsFullScreenElementChanged += (s, _) =>
            {
                Dispatcher.BeginInvoke(() => SetFullScreen(core.ContainsFullScreenElement));
            };

            core.WebMessageReceived += (s, args) =>
            {
                _ = HandleWebMessageAsync(args.WebMessageAsJson);
            };

            core.NavigationCompleted += (s, args) =>
            {
                Dispatcher.BeginInvoke(async () =>
                {
                    if (LoadingOverlay != null) LoadingOverlay.Visibility = Visibility.Collapsed;
                    await PushWorldsStateToWebViewAsync();
                    _ = RefreshWorldsInBackgroundAsync(forceOnline: false);
                });
            };

            core.Navigate(LexClassStudioService.HomeUrl);
        }
        catch (Exception ex)
        {
            if (TxtLoadingSubtitle != null)
            {
                TxtLoadingSubtitle.Text = $"No se pudo iniciar Lex Junior Class: {ex.Message}";
            }
        }
    }

    private async Task HandleWebMessageAsync(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson) || _isClosing) return;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            if (!doc.RootElement.TryGetProperty("type", out var typeProp)) return;

            string msgType = typeProp.GetString() ?? "";
            switch (msgType)
            {
                case "lexclass_toggle_fullscreen":
                    _ = Dispatcher.BeginInvoke(ToggleFullScreenFromShortcut);
                    break;

                case "lexclass_escape_key":
                    if (_isFullScreen)
                        _ = Dispatcher.BeginInvoke(() => SetFullScreen(false));
                    break;

                case "lexclass_world_action":
                    {
                        string worldId = doc.RootElement.TryGetProperty("worldId", out var wProp) ? (wProp.GetString() ?? "") : "";
                        await HandleWorldActionAsync(worldId);
                        break;
                    }
            }
        }
        catch { }
    }

    private async Task HandleWorldActionAsync(string worldId)
    {
        if (!LexClassStudioService.IsValidWorldId(worldId)) return;

        string displayName = LexClassUpdateDialog.FormatWorldDisplayName(worldId);
        int lastUiPct = -1;
        long lastUiTick = 0;

        await Dispatcher.InvokeAsync(() =>
        {
            TxtPackSyncStatus.Text = $"Descargando {displayName}...";
        });
        await PushWorldsStateToWebViewAsync();

        var progress = new Progress<(int Percent, string Stage)>(report =>
        {
            int pct = Math.Clamp(report.Percent, 0, 100);
            long now = Environment.TickCount64;
            if (pct != lastUiPct && (pct >= 90 || pct == 0 || unchecked(now - lastUiTick) >= 250 || Math.Abs(pct - lastUiPct) >= 10))
            {
                lastUiPct = pct;
                lastUiTick = now;
                _ = Dispatcher.BeginInvoke(async () =>
                {
                    if (_isClosing) return;
                    TxtPackSyncStatus.Text = $"Actualizando {displayName} · {pct}%";
                    await PushWorldsStateToWebViewAsync();
                });
            }
        });

        var (ok, message) = await LexClassStudioService.InstallOrUpdateWorldAsync(worldId, progress);

        await Dispatcher.InvokeAsync(async () =>
        {
            if (_isClosing) return;
            TxtPackSyncStatus.Text = ok
                ? $"Al día · v{LexClassStudioService.GetEffectiveDisplayVersion()}"
                : "Modo offline local";
            await PushWorldsStateToWebViewAsync();
            if (ok && _core != null)
            {
                _core.Reload();
            }
            else if (!ok)
            {
                var dlg = new LexClassUpdateDialog(
                    LexClassUpdateDialogMode.OfflineOrError,
                    customMessage: message)
                {
                    Owner = this
                };
                dlg.ShowDialog();
            }
        });
    }

    private async Task PushWorldsStateToWebViewAsync()
    {
        try
        {
            if (_core == null || _isClosing) return;
            string json = LexClassStudioService.BuildInjectStateScriptJson();
            await _core.ExecuteScriptAsync($"window.__lexSetWorldsState && window.__lexSetWorldsState({json});");
        }
        catch { }
    }

    private async Task RefreshWorldsInBackgroundAsync(bool forceOnline)
    {
        try
        {
            bool updated = await LexClassStudioService.RefreshRemoteCatalogAsync(forceOnline);
            if (_isClosing) return;

            var pending = LexClassStudioService.GetPendingWorldActions();
            int totalPending = pending.Count;
            string displayVer = LexClassStudioService.GetEffectiveDisplayVersion();

            await Dispatcher.InvokeAsync(async () =>
            {
                if (_isClosing) return;

                if (totalPending > 0)
                {
                    TxtPackSyncStatus.Text = totalPending == 1
                        ? "1 actualización lista"
                        : $"{totalPending} actualizaciones listas";
                }
                else
                {
                    TxtPackSyncStatus.Text = updated
                        ? $"Al día · v{displayVer}"
                        : "100% Local";
                }

                if (forceOnline)
                {
                    TxtCheckWorldsLabel.Text = "🔄 Buscar actualizaciones";
                    BtnCheckWorlds.IsEnabled = true;
                }
                await PushWorldsStateToWebViewAsync();

                if (forceOnline)
                {
                    var mode = totalPending > 0
                        ? LexClassUpdateDialogMode.UpdatesAvailable
                        : (updated ? LexClassUpdateDialogMode.UpToDate : LexClassUpdateDialogMode.OfflineOrError);

                    var dlg = new LexClassUpdateDialog(mode, pending)
                    {
                        Owner = this
                    };
                    bool? res = dlg.ShowDialog();

                    if (dlg.WorldsUpdated || res == true)
                    {
                        var afterPending = LexClassStudioService.GetPendingWorldActions();
                        string updatedVer = LexClassStudioService.GetEffectiveDisplayVersion();
                        TxtPackSyncStatus.Text = afterPending.Count > 0
                            ? (afterPending.Count == 1 ? "1 actualización lista" : $"{afterPending.Count} actualizaciones listas")
                            : $"Al día · v{updatedVer}";
                        await PushWorldsStateToWebViewAsync();
                        _core?.Reload();
                    }
                }
            });
        }
        catch
        {
            if (forceOnline)
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    TxtPackSyncStatus.Text = "100% Local";
                    TxtCheckWorldsLabel.Text = "🔄 Buscar actualizaciones";
                    BtnCheckWorlds.IsEnabled = true;
                });
            }
        }
    }

    private void HideFullScreenExitPopupImmediate()
        => _fullScreenExitHoverController?.HideImmediate();

    private void SyncFullScreenExitHoverMonitor()
        => GetFullScreenExitHoverController().Sync();

    public void SetFullScreen(bool enter)
    {
        if (enter == _isFullScreen)
        {
            SyncFullScreenExitHoverMonitor();
            return;
        }
        _isFullScreen = enter;

        if (enter)
        {
            _fullScreenCoordinator.EnterFullScreen(this, () =>
            {
                RowHeader.Height = new GridLength(0);
                RowAccentLine.Height = new GridLength(0);
                ApplyMaximizedMargin();
            });
            TxtFullScreenBtnLabel.Text = "✕ Salir";
            SyncFullScreenExitHoverMonitor();

            _ = Dispatcher.InvokeAsync(() =>
            {
                if (!_isClosing && _isFullScreen)
                {
                    _fullScreenCoordinator.Reassert(this);
                }
            }, DispatcherPriority.Loaded);
        }
        else
        {
            SyncFullScreenExitHoverMonitor();
            _fullScreenCoordinator.ExitFullScreen(
                this,
                restoreChromeAndRows: () =>
                {
                    RowHeader.Height = new GridLength(42);
                    RowAccentLine.Height = new GridLength(1.5);
                },
                applyFinalMargins: ApplyMaximizedMargin);
            TxtFullScreenBtnLabel.Text = "⛶ Pantalla completa";
        }

        ApplyMaximizedMargin();
    }

    private void ApplyMaximizedMargin()
    {
        if (RootLayoutGrid == null) return;
        RootLayoutGrid.Margin = new Thickness(0);

        if (ClassWindowChrome != null)
        {
            bool isFs = _isFullScreen || _fullScreenCoordinator.IsEnteringFullScreen;
            ClassWindowChrome.CaptionHeight = isFs ? 0 : 42;
            ClassWindowChrome.ResizeBorderThickness = (isFs || WindowState == WindowState.Maximized)
                ? new Thickness(0)
                : new Thickness(6);
        }
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (BtnMaxRestore != null)
        {
            BtnMaxRestore.Content = WindowState == WindowState.Maximized ? "❐" : "□";
        }
        ApplyMaximizedMargin();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.F11 && !e.IsRepeat)
        {
            e.Handled = true;
            ToggleFullScreenFromShortcut();
        }
        else if (key == Key.Escape && _isFullScreen)
        {
            e.Handled = true;
            SetFullScreen(false);
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            BtnMaximize_Click(sender, e);
        }
        else if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void BtnCheckWorlds_Click(object sender, RoutedEventArgs e)
    {
        BtnCheckWorlds.IsEnabled = false;
        TxtCheckWorldsLabel.Text = "⏳ Buscando...";
        TxtPackSyncStatus.Text = "Consultando GitHub...";
        _ = RefreshWorldsInBackgroundAsync(forceOnline: true);
    }

    private void BtnFullScreen_Click(object sender, RoutedEventArgs e) => SetFullScreen(!_isFullScreen);
    private void BtnExitFullScreen_Click(object sender, RoutedEventArgs e) => SetFullScreen(false);
    private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void BtnMaximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _isClosing = true;
        HideFullScreenExitPopupImmediate();
        try { ClassWebView?.Dispose(); } catch { }
        base.OnClosed(e);
    }
}
