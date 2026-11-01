using System;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using LexBrowser.UI.Models;
using Microsoft.Web.WebView2.Wpf;

namespace LexBrowser.UI.Views;

public partial class FloatingPipWindow : Window
{
    // ── Pestaña actualmente "viviendo" en el PiP ──────────────────────────
    public BrowserTab? TargetTab { get; private set; }

    // Callback: el usuario pidió volver a la pestaña
    public Action<BrowserTab>? OnReturnToTabRequested { get; set; }

    // Callback: devuelve el WebView2 al BrowserContainer principal
    public Action<BrowserTab>? OnDetachWebView { get; set; }

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
    public void AttachTab(BrowserTab tab, bool? videoOnlyMode = null)
    {
        // Si ya hay otra pestaña, primero devolvemos su WebView
        if (TargetTab != null && TargetTab != tab)
        {
            DetachCurrentWebView();
        }

        TargetTab = tab;

        // Auto-detectar modo Solo Video: si es YouTube o tiene multimedia activa, activar Solo Video por defecto para estiramiento libre
        bool isMediaTab = tab.HasActiveMedia || (!string.IsNullOrWhiteSpace(tab.Url) && tab.Url.Contains("youtube.com"));
        IsVideoOnlyMode = videoOnlyMode ?? isMediaTab;

        // Quitar el WebView2 de su padre actual (BrowserContainer)
        var webView = tab.WebView;
        if (webView.Parent is Panel oldParent)
        {
            oldParent.Children.Remove(webView);
        }

        // Insertar el WebView2 real dentro del VideoContainer del PiP
        VideoContainer.Children.Clear();
        VideoPlaceholder.Visibility = Visibility.Collapsed;
        VideoContainer.Children.Add(webView);

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
        SendJsCommand("set_video_only_mode", IsVideoOnlyMode);

        // Sincronizar estado inicial de controles
        UpdateMediaControls(tab.MediaTitle, tab.MediaCurrentTime, tab.MediaDuration, tab.MediaIsPlaying);
    }

    // ─────────────────────────────────────────────────────────────────────
    // DETACH: Devuelve el WebView2 al llamador (MainWindow lo reinserta)
    // ─────────────────────────────────────────────────────────────────────
    private void DetachCurrentWebView()
    {
        if (TargetTab == null) return;

        // Desactivar modo solo video antes de devolverlo
        try
        {
            if (TargetTab.WebView?.CoreWebView2 != null)
            {
                string json = JsonSerializer.Serialize(new { action = "set_video_only_mode", enabled = false });
                TargetTab.WebView.CoreWebView2.PostWebMessageAsJson(json);
            }
        }
        catch { }

        // Quitar el WebView del VideoContainer
        VideoContainer.Children.Clear();
        VideoPlaceholder.Visibility = Visibility.Visible;

        // Notificar a MainWindow para que lo reinserte
        OnDetachWebView?.Invoke(TargetTab);
        TargetTab = null;
    }

    public void HideAndDetach()
    {
        DetachCurrentWebView();
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
            OnReturnToTabRequested?.Invoke(TargetTab);
            // HideAndDetach se llama desde MainWindow después de reinsertar
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
            if (TargetTab?.WebView?.CoreWebView2 == null) return;

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

            TargetTab.WebView.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch
        {
            // Error silencioso si el WebView fue desechado
        }
    }
}
