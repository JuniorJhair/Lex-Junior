using System;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Threading.Tasks;
using LexBrowser.Core;
using LexBrowser.Core.AdBlock;
using LexBrowser.Core.Engine;
using LexBrowser.UI.Models;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LexBrowser.UI.Views;

public partial class FloatingPipWindow : Window
{
    // ── Pestaña actualmente "viviendo" en el PiP ──────────────────────────
    public BrowserTab? TargetTab { get; private set; }

    // Callback: el usuario pidió volver a la pestaña
    public Action<BrowserTab>? OnReturnToTabRequested { get; set; }

    // Callback: el mini-reproductor se liberó; la pestaña origen ya trae MediaCurrentTime / MediaIsPlaying
    // para que MainWindow retome la reproducción donde quedó.
    public Action<BrowserTab>? OnDetachWebView { get; set; }

    private WebView2? _view;
    private bool _viewIncognito;
    private double _lastTime;
    private bool _lastPlaying;

    private bool _isUpdatingSlider = false;
    private bool _isMuted = false;

    public FloatingPipWindow()
    {
        InitializeComponent();
        Loaded += FloatingPipWindow_Loaded;
        Closing += FloatingPipWindow_Closing;
    }

    private void FloatingPipWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Posicionar en esquina inferior derecha
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 24;
        Top = workArea.Bottom - Height - 24;
    }

    private void FloatingPipWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Nunca cerrar la ventana; solo ocultarla para reusar
        e.Cancel = true;
        HideAndDetach();
    }

    public bool IsVideoOnlyMode { get; private set; } = false;

    // ─────────────────────────────────────────────────────────────────────
    // ATTACH: Mueve el WebView2 de la pestaña al área de video del PiP
    // ─────────────────────────────────────────────────────────────────────
    public async Task AttachTabAsync(BrowserTab tab, bool? videoOnlyMode = null)
    {
        // Si ya hay otra pestaña en el mini-reproductor, la liberamos primero
        if (TargetTab != null && TargetTab != tab)
        {
            DetachCurrentWebView(resumeSource: false);
        }

        TargetTab = tab;
        _lastTime = tab.MediaCurrentTime;
        _lastPlaying = tab.MediaIsPlaying;

        try
        {
            await EnsureViewAsync(tab.IsIncognito);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MiniPlayer] No se pudo crear el WebView2: {ex.Message}");
            TargetTab = null;
            return;
        }

        // Auto-detectar modo Solo Video: si es YouTube o tiene multimedia activa, activar Solo Video por defecto para estiramiento libre
        bool isMediaTab = tab.HasActiveMedia || (!string.IsNullOrWhiteSpace(tab.Url) && tab.Url.Contains("youtube.com"));
        IsVideoOnlyMode = videoOnlyMode ?? isMediaTab;

        // El WebView2 propio vive siempre dentro del VideoContainer (se crea en EnsureViewAsync):
        // nunca se quita del árbol visual para no destruir su superficie.
        VideoPlaceholder.Visibility = Visibility.Collapsed;

        // Ajustar dimensiones iniciales según sea video vertical (Shorts) o estándar (16:9)
        var workArea = SystemParameters.WorkArea;
        bool isVertical = !string.IsNullOrEmpty(tab.Url) && tab.Url.Contains("/shorts/");
        if (isVertical)
        {
            double w = 420;
            double h = Math.Min(740, workArea.Height - 40);
            Width = w;
            Height = h;
            Left = workArea.Right - w - 24;
            Top = workArea.Bottom - h - 24;
        }
        else
        {
            double w = 640;
            double h = 390;
            Width = w;
            Height = h;
            Left = workArea.Right - w - 24;
            Top = workArea.Bottom - h - 24;
        }

        // Configurar badge y modo
        TxtPipBadge.Text = IsVideoOnlyMode ? "▶ Mini-Reproductor" : "🗖 Página Flotante";
        TxtToggleVideoOnly.Text = IsVideoOnlyMode ? "🗖 Web Completa" : "🎬 Solo Video";

        // Sincronizar estado inicial de controles
        UpdateMediaControls(tab.MediaTitle, tab.MediaCurrentTime, tab.MediaDuration, tab.MediaIsPlaying);

        // La pestaña origen se pausa (el audio pasa al mini-reproductor) y este retoma desde el mismo segundo.
        PostToTab(tab, new { action = "pause" });
        string target = MiniPlayerUrl.Build(tab.Url, tab.MediaCurrentTime);
        _view!.CoreWebView2.Navigate(target);
    }

    // ─────────────────────────────────────────────────────────────────────
    // WebView2 propio del mini-reproductor (misma sesión/cookies/adblock que las pestañas)
    // ─────────────────────────────────────────────────────────────────────
    private async Task EnsureViewAsync(bool incognito)
    {
        if (_view != null && _viewIncognito != incognito)
        {
            VideoContainer.Children.Remove(_view);
            _view.Dispose();
            _view = null;
        }
        if (_view != null) return;

        var view = new WebView2
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        VideoContainer.Children.Add(view);

        var env = await BrowserEngine.GetEnvironmentAsync();
        if (incognito)
        {
            var options = env.CreateCoreWebView2ControllerOptions();
            options.IsInPrivateModeEnabled = true;
            await view.EnsureCoreWebView2Async(env, options);
        }
        else
        {
            await view.EnsureCoreWebView2Async(env);
        }

        var core = view.CoreWebView2;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(YouTubeOptimizer.InjectedScript);

        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (s, e) =>
        {
            if (SettingsManager.Current.EnableAdBlocker &&
                Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) &&
                AdBlockFilter.ShouldBlock(uri))
            {
                e.Response = core.Environment.CreateWebResourceResponse(
                    new System.IO.MemoryStream(), 403, "Blocked by LexShield", "Content-Type: text/plain");
            }
        };

        // Los popups dentro del mini-reproductor nunca abren ventanas
        core.NewWindowRequested += (s, e) => e.Handled = true;
        core.PermissionRequested += (s, e) =>
        {
            if (e.PermissionKind == CoreWebView2PermissionKind.Notifications)
            {
                e.State = CoreWebView2PermissionState.Deny;
                e.Handled = true;
            }
        };
        core.NavigationCompleted += (s, e) =>
        {
            if (IsVideoOnlyMode) SendJsCommand("set_video_only_mode", true);
        };
        core.WebMessageReceived += OnViewMessage;

        _view = view;
        _viewIncognito = incognito;
    }

    private void OnViewMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "media_state") return;

            var data = root.GetProperty("data");
            bool isPlaying = data.GetProperty("isPlaying").GetBoolean();
            string title = data.GetProperty("title").GetString() ?? "";
            double time = data.GetProperty("currentTime").GetDouble();
            double duration = data.GetProperty("duration").GetDouble();

            _lastTime = time;
            _lastPlaying = isPlaying;
            if (TargetTab != null) TargetTab.MediaCurrentTime = time;
            UpdateMediaControls(title, time, duration, isPlaying);
        }
        catch { }
    }

    private static void PostToTab(BrowserTab tab, object message)
    {
        try
        {
            tab.WebView?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message));
        }
        catch { }
    }

    // ─────────────────────────────────────────────────────────────────────
    // DETACH: libera el mini-reproductor y devuelve el control a la pestaña origen
    // ─────────────────────────────────────────────────────────────────────
    private void DetachCurrentWebView(bool resumeSource)
    {
        var tab = TargetTab;
        if (tab == null) return;
        TargetTab = null;

        // Detener el video del mini-reproductor (corta el audio) sin destruir el control
        try { _view?.CoreWebView2?.Navigate("about:blank"); } catch { }
        VideoPlaceholder.Visibility = Visibility.Visible;

        // La pestaña origen retoma en el segundo donde quedó el mini-reproductor
        tab.MediaCurrentTime = _lastTime;
        tab.MediaIsPlaying = resumeSource && _lastPlaying;
        OnDetachWebView?.Invoke(tab);
    }

    public void HideAndDetach(bool resumeSource = false)
    {
        DetachCurrentWebView(resumeSource);
        Hide();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Actualizar los controles de la barra inferior
    // ─────────────────────────────────────────────────────────────────────
    public void UpdateMediaControls(string title, double currentTime, double duration, bool isPlaying)
    {
        Dispatcher.Invoke(() =>
        {
            if (!string.IsNullOrWhiteSpace(title))
            {
                TxtHeaderTitle.Text = title;
                TxtMediaTitle.Text = title;
            }

            TxtPlayPause.Text = isPlaying ? "⏸" : "▶";
            BtnPlayPause.Background = isPlaying
                ? System.Windows.Media.Brushes.Transparent
                : new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#6366F1"));

            TxtCurrentTime.Text = FormatTime(currentTime);
            TxtDuration.Text = FormatTime(duration);

            if (duration > 0 && !SeekSlider.IsMouseCaptureWithin)
            {
                _isUpdatingSlider = true;
                SeekSlider.Maximum = duration;
                SeekSlider.Value = currentTime;
                _isUpdatingSlider = false;
            }
        });
    }

    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) return "0:00";
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.Hours > 0 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }

    // ─────────────────────────────────────────────────────────────────────
    // EVENTOS DE UI
    // ─────────────────────────────────────────────────────────────────────

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Solo mover si el clic está en la barra de título u área no interactiva y el botón sigue presionado
        if (e.Source is not Button && e.Source is not Slider && e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void BtnToggleVideoOnly_Click(object sender, RoutedEventArgs e)
    {
        IsVideoOnlyMode = !IsVideoOnlyMode;
        TxtPipBadge.Text = IsVideoOnlyMode ? "▶ Mini-Reproductor" : "🗖 Página Flotante";
        TxtToggleVideoOnly.Text = IsVideoOnlyMode ? "🗖 Web Completa" : "🎬 Solo Video";
        SendJsCommand("set_video_only_mode", IsVideoOnlyMode);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            BtnMaximize_Click(sender, e);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;

    private void StartWindowResize(int direction)
    {
        try
        {
            ReleaseCapture();
            var helper = new WindowInteropHelper(this);
            SendMessage(helper.Handle, WM_NCLBUTTONDOWN, (IntPtr)direction, IntPtr.Zero);
        }
        catch { }
    }

    private void ResizeTop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) StartWindowResize(HTTOP);
    }

    private void ResizeBottom_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) StartWindowResize(HTBOTTOM);
    }

    private void ResizeLeft_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) StartWindowResize(HTLEFT);
    }

    private void ResizeRight_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) StartWindowResize(HTRIGHT);
    }

    private void ResizeTopLeft_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) StartWindowResize(HTTOPLEFT);
    }

    private void ResizeTopRight_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) StartWindowResize(HTTOPRIGHT);
    }

    private void ResizeBottomLeft_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) StartWindowResize(HTBOTTOMLEFT);
    }

    private void ResizeBottomRight_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) StartWindowResize(HTBOTTOMRIGHT);
    }

    private void ResizeCorner_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) StartWindowResize(HTBOTTOMRIGHT);
    }

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            TxtMaximize.Text = "🗖";
        }
        else
        {
            WindowState = WindowState.Maximized;
            TxtMaximize.Text = "🗗";
        }
    }

    private void BtnSizePresets_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.IsOpen = true;
        }
    }

    private void PresetSize_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string tag)
        {
            if (tag == "fullscreen")
            {
                WindowState = WindowState.Maximized;
                TxtMaximize.Text = "🗗";
                return;
            }

            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
                TxtMaximize.Text = "🗖";
            }

            var parts = tag.Split('x');
            if (parts.Length == 2 && double.TryParse(parts[0], out double w) && double.TryParse(parts[1], out double h))
            {
                Width = w;
                Height = h;

                // Mantener dentro del área visible de la pantalla
                var workArea = SystemParameters.WorkArea;
                if (Left + Width > workArea.Right) Left = Math.Max(workArea.Left, workArea.Right - Width - 10);
                if (Top + Height > workArea.Bottom) Top = Math.Max(workArea.Top, workArea.Bottom - Height - 10);
            }
        }
    }

    private void BtnReturnToTab_Click(object sender, RoutedEventArgs e)
    {
        if (TargetTab != null)
        {
            // Primero se libera (guarda el segundo actual); MainWindow selecciona la pestaña y reanuda.
            var tab = TargetTab;
            OnReturnToTabRequested?.Invoke(tab);
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        HideAndDetach();
    }

    private void BtnPlayPause_Click(object sender, RoutedEventArgs e)
    {
        SendJsCommand("toggle_play");
    }

    private void BtnRewind_Click(object sender, RoutedEventArgs e)
    {
        if (TargetTab != null)
        {
            double newTime = Math.Max(0, TargetTab.MediaCurrentTime - 10);
            SendJsCommand("seek", newTime);
        }
    }

    private void BtnForward_Click(object sender, RoutedEventArgs e)
    {
        if (TargetTab != null)
        {
            double newTime = Math.Min(TargetTab.MediaDuration, TargetTab.MediaCurrentTime + 10);
            SendJsCommand("seek", newTime);
        }
    }

    private void BtnMute_Click(object sender, RoutedEventArgs e)
    {
        _isMuted = !_isMuted;
        TxtMute.Text = _isMuted ? "🔇" : "🔊";
        SendJsCommand("toggle_mute");
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingSlider) return;
        if (SeekSlider.IsMouseCaptureWithin)
        {
            SendJsCommand("seek", SeekSlider.Value);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Enviar comandos JS al WebView2 de la pestaña
    // ─────────────────────────────────────────────────────────────────────
    private void SendJsCommand(string action, object? extra = null)
    {
        try
        {
            if (_view?.CoreWebView2 == null) return;

            string json;
            if (action == "seek" && extra is double seekTime)
            {
                json = JsonSerializer.Serialize(new { action, time = seekTime });
            }
            else if (action == "set_video_only_mode" && extra is bool enabled)
            {
                json = JsonSerializer.Serialize(new { action, enabled });
            }
            else
            {
                json = JsonSerializer.Serialize(new { action });
            }

            _view.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch
        {
            // Error silencioso si el WebView fue desechado
        }
    }
}
