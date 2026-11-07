using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using LexBrowser.Core;
using LexBrowser.Core.Engine;
using LexBrowser.Core.Notifications;
using LexBrowser.UI.Models;
using Microsoft.Web.WebView2.Core;

namespace LexBrowser.UI.Views;

public partial class NotificationToastWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private readonly NotificationItem _item;
    private readonly CoreWebView2Notification? _nativeNotif;
    private readonly BrowserTab? _tab;
    private DispatcherTimer? _autoCloseTimer;
    private bool _isClosing = false;

    public NotificationToastWindow(NotificationItem item, CoreWebView2Notification? nativeNotif, BrowserTab? tab)
    {
        InitializeComponent();
        _item = item;
        _nativeNotif = nativeNotif;
        _tab = tab;

        TxtHost.Text = item.DisplayHost;
        TxtTitle.Text = item.Title;
        TxtBody.Text = item.Body;

        // Cargar favicon en caché de forma asíncrona y segura
        try
        {
            var fav = FaviconService.GetCachedFavicon(item.DisplayHost);
            if (fav != null)
            {
                ImgFavicon.Source = fav;
                ImgFavicon.Visibility = Visibility.Visible;
                TxtFallbackIcon.Visibility = Visibility.Collapsed;
            }
        }
        catch { }

        // Si la página solicita cerrar la notificación vía JS (notification.close())
        if (_nativeNotif != null)
        {
            try
            {
                _nativeNotif.CloseRequested += (_, _) =>
                {
                    Dispatcher.BeginInvoke(Dismiss);
                };
            }
            catch { }
        }

        // Auto-cierre a los 6 segundos (a menos que RequiresInteraction sea true)
        if (!item.RequiresInteraction)
        {
            _autoCloseTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(6)
            };
            _autoCloseTimer.Tick += (_, _) =>
            {
                _autoCloseTimer.Stop();
                Dismiss();
            };
            _autoCloseTimer.Start();
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Aplicar estilos Win32 para no robar foco ni aparecer en Alt+Tab
        var helper = new WindowInteropHelper(this);
        IntPtr exStyle = GetWindowLongPtr(helper.Handle, GWL_EXSTYLE);
        long newExStyle = exStyle.ToInt64() | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        SetWindowLongPtr(helper.Handle, GWL_EXSTYLE, new IntPtr(newExStyle));
    }

    private void ToastCard_MouseEnter(object sender, MouseEventArgs e)
    {
        _autoCloseTimer?.Stop();
    }

    private void ToastCard_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_autoCloseTimer != null && !_isClosing)
        {
            _autoCloseTimer.Start();
        }
    }

    private void ToastCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isClosing) return;

        try
        {
            _nativeNotif?.ReportClicked();
        }
        catch { }

        _item.IsRead = true;

        // Enfocar la pestaña correspondiente o navegar al origen
        if (_tab != null && !_tab.IsDisposed)
        {
            if (Application.Current.MainWindow is MainWindow mainWin)
            {
                mainWin.FocusAndSelectTab(_tab);
            }
        }

        Dismiss();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Dismiss();
    }

    public void Dismiss()
    {
        if (_isClosing) return;
        _isClosing = true;

        _autoCloseTimer?.Stop();
        _autoCloseTimer = null;

        try
        {
            _nativeNotif?.ReportClosed();
        }
        catch { }

        NotificationToastManager.OnToastClosed(this);
        Close();
    }
}
