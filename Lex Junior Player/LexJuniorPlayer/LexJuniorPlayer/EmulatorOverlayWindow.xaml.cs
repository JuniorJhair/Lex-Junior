using System;
using System.Windows;
using System.Windows.Interop;
using LexJuniorPlayer.Services;

namespace LexJuniorPlayer;

public partial class EmulatorOverlayWindow : Window
{
    public event Action? FullscreenRequested;
    public event Action? SidebarToggleRequested;
    public event Action? AndroidBackRequested;
    public event Action? PasteRequested;

    public IntPtr NativeHandle { get; private set; }

    public EmulatorOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeHandle = hwnd;
        if (hwnd == IntPtr.Zero) return;

        if (!EmulatorWindowController.ConfigureNonLayeredRoundedOverlay(hwnd))
        {
            PillBorder.BorderThickness = new Thickness(1);
        }
    }

    public void UpdatePositionAndLabels(EmulatorWindowState state, bool sidebarHidden)
    {
        double scale = state.Dpi > 0 ? state.Dpi / 96.0 : 1.0;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
        {
            scale = source.CompositionTarget.TransformToDevice.M11;
            if (scale <= 0) scale = state.Dpi > 0 ? state.Dpi / 96.0 : 1.0;
        }

        var fsText = state.IsFullscreen ? "↙ Salir (F11)" : "⛶ Pantalla completa";
        var sbText = sidebarHidden ? "◫ Mostrar barra" : "◫ Ocultar barra";

        var layoutChanged = false;
        if (!Equals(OverlayFullscreenBtn.Content, fsText))
        {
            OverlayFullscreenBtn.Content = fsText;
            layoutChanged = true;
        }

        if (!Equals(OverlaySidebarBtn.Content, sbText))
        {
            OverlaySidebarBtn.Content = sbText;
            layoutChanged = true;
        }

        if (layoutChanged || ActualWidth <= 10)
        {
            Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }

        double pillW = ActualWidth > 20 ? ActualWidth : (DesiredSize.Width > 20 ? DesiredSize.Width : 300);
        double targetLeft;
        double targetTop;

        if (state.IsFullscreen)
        {
            double monLeftDip = state.MonitorRectPhysical.Left / scale;
            double monWidthDip = state.MonitorRectPhysical.Width / scale;
            double monTopDip = state.MonitorRectPhysical.Top / scale;
            targetLeft = monLeftDip + Math.Max(0, (monWidthDip - pillW) / 2.0);
            targetTop = monTopDip + 6;
        }
        else
        {
            double emuLeftDip = state.MainRectPhysical.Left / scale;
            double emuRightDip = state.MainRectPhysical.Right / scale;
            double emuTopDip = state.MainRectPhysical.Top / scale;

            // Anchor inside empty right side of the Qt titlebar right before Minimize/Maximize/Close
            double preferredLeft = emuRightDip - pillW - 144;
            targetLeft = Math.Max(emuLeftDip + 275, preferredLeft);
            targetTop = emuTopDip + 3;
        }

        if (double.IsNaN(Left) || Math.Abs(Left - targetLeft) >= 0.5)
        {
            Left = targetLeft;
        }

        if (double.IsNaN(Top) || Math.Abs(Top - targetTop) >= 0.5)
        {
            Top = targetTop;
        }
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => FullscreenRequested?.Invoke();
    private void Sidebar_Click(object sender, RoutedEventArgs e) => SidebarToggleRequested?.Invoke();
    private void Back_Click(object sender, RoutedEventArgs e) => AndroidBackRequested?.Invoke();
    private void Paste_Click(object sender, RoutedEventArgs e) => PasteRequested?.Invoke();
}
