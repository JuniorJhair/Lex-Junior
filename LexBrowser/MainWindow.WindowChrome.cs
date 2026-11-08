using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using LexBrowser.Core;
using LexBrowser.Core.AdBlock;
using LexBrowser.Core.Interop;
using LexBrowser.UI.Models;
using LexBrowser.UI.Views;

namespace LexBrowser;

public partial class MainWindow
{
    private bool _isApplyingNormalBounds = false;
    private bool _shieldBadgeUpdatePending = false;

    private static readonly FieldInfo? WebView2BaseField =
        typeof(WebView2).GetField("m_webview2Base", BindingFlags.Instance | BindingFlags.NonPublic);
    private static PropertyInfo? _cachedControllerProperty;

    // ─────────────────────────────────────────────────────────────────────
    // MAXIMIZADO LIMITADO AL ÁREA DE TRABAJO (la barra de tareas no tapa el contenido)
    // ─────────────────────────────────────────────────────────────────────
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source?.AddHook(WorkAreaHook);
    }

    private IntPtr WorkAreaHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;
        if (WindowChrome.GetWindowChrome(this) == null) return IntPtr.Zero; // pantalla completa real

        var monitor = NativeMethods.MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;

        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);
        mmi.ptMaxPosition.x = info.rcWork.left - info.rcMonitor.left;
        mmi.ptMaxPosition.y = info.rcWork.top - info.rcMonitor.top;
        mmi.ptMaxSize.x = info.rcWork.right - info.rcWork.left;
        mmi.ptMaxSize.y = info.rcWork.bottom - info.rcWork.top;

        uint dpi = NativeMethods.GetDpiForWindow(hwnd);
        double scale = dpi > 0 ? dpi / 96.0 : 1.0;
        mmi.ptMinTrackSize.x = (int)Math.Ceiling(MinWidth * scale);
        mmi.ptMinTrackSize.y = (int)Math.Ceiling(MinHeight * scale);

        Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    // ─────────────────────────────────────────────────────────────────────
    // DOBLE CLIC EN HEADER VACÍO PARA MAXIMIZAR / RESTAURAR (Protege clics en pestañas)
    // ─────────────────────────────────────────────────────────────────────
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source)
        {
            if (FindVisualParent<Button>(source) != null ||
                FindVisualParent<MenuItem>(source) != null ||
                IsInsideTabElement(source))
            {
                return;
            }
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

    private static bool IsInsideTabElement(DependencyObject element)
    {
        var current = element;
        while (current != null)
        {
            if (current is FrameworkElement fe && (fe.Name == "TabBorder" || fe.DataContext is BrowserTab))
                return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
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

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

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

        foreach (var t in Tabs)
        {
            if (t.WebView != null && !t.IsDisposed)
            {
                try
                {
                    GetController(t.WebView)?.NotifyParentWindowPositionChanged();
                }
                catch { }
            }
        }
    }

    private void MainWindow_SizeOrLocationChanged(object? sender, EventArgs e)
    {
        UpdateResponsiveToolbarLayout();
        if (_activeTab?.WebView != null && !_activeTab.IsDisposed)
        {
            try
            {
                GetController(_activeTab.WebView)?.NotifyParentWindowPositionChanged();
            }
            catch { }
        }

        if (!IsLoaded || _isApplyingNormalBounds || WindowState != WindowState.Normal)
            return;

        RecordCurrentNormalBounds(saveToDisk: false);
    }

    private void TabStripContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTabWidths();
    }

    public void UpdateTabWidths()
    {
        if (Tabs == null || Tabs.Count == 0) return;

        double availableWidth = TabStripContainer?.ActualWidth ?? 0;
        if (availableWidth <= 0)
        {
            availableWidth = Math.Max(300, ActualWidth - 280);
        }

        double actionButtonsWidth = 76.0;
        double usableWidth = Math.Max(100, availableWidth - actionButtonsWidth);

        double pinnedTotalWidth = 0;
        int unpinnedCount = 0;

        foreach (var t in Tabs)
        {
            if (t.IsPinned)
            {
                double pinW = t.PinMode == TabPinMode.LogoOnly ? 40.0 : 110.0;
                t.TabWidth = pinW;
                pinnedTotalWidth += (pinW + 4.0);
            }
            else
            {
                unpinnedCount++;
            }
        }

        if (unpinnedCount == 0) return;

        double remainingWidthForUnpinned = Math.Max(0, usableWidth - pinnedTotalWidth);
        double perTabAvailable = (remainingWidthForUnpinned / unpinnedCount) - 4.0;
        double targetWidth = Math.Clamp(Math.Floor(perTabAvailable), 46.0, 215.0);

        foreach (var t in Tabs)
        {
            if (!t.IsPinned)
            {
                t.TabWidth = targetWidth;
            }
        }
    }

    private void UpdateResponsiveToolbarLayout()
    {
        double w = ActualWidth > 0 ? ActualWidth : Width;
        if (double.IsNaN(w) || w <= 0) return;

        // En pantallas de < 1280px (ej. proyectores 1024px o ventana dividida), colapsar etiquetas de botones laterales
        // para garantizar que el Omnibox disponga siempre de amplio espacio (> 380px).
        bool isCompact = w < 1280;
        bool isUltraCompact = w < 760;

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

    private void ScheduleShieldBadgeUpdate()
    {
        if (_shieldBadgeUpdatePending) return;
        _shieldBadgeUpdatePending = true;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, async () =>
        {
            await System.Threading.Tasks.Task.Delay(250);
            _shieldBadgeUpdatePending = false;
            UpdateShieldBadgeText();
        });
    }

    private static readonly SolidColorBrush BrushShieldActiveText = CreateFrozenBrush(0x38, 0xBD, 0xF8);
    private static readonly SolidColorBrush BrushShieldCompatText = CreateFrozenBrush(0xF5, 0x9E, 0x0B);
    private static readonly SolidColorBrush BrushShieldOffText = CreateFrozenBrush(0x9C, 0xA3, 0xAF);
    private static readonly SolidColorBrush BrushShieldGreen = CreateFrozenBrush(0x10, 0xB9, 0x81);
    private bool _isUpdatingShieldPopupUI = false;

    private void UpdateShieldBadgeText()
    {
        if (TxtShieldBadge == null) return;
        int count = _activeTab?.BlockedAdsCount ?? 0;
        double w = ActualWidth > 0 ? ActualWidth : Width;
        bool isCompact = !double.IsNaN(w) && w > 0 && w < 1150;

        string? host = _activeTab?.CurrentUri?.Host;
        var policy = SitePolicy.Resolve(host, _activeTab?.TemporaryCompatMode ?? false);

        if (!policy.ShieldEnabled)
        {
            TxtShieldBadge.Text = isCompact ? "Off" : "Pausado";
            TxtShieldBadge.Foreground = BrushShieldOffText;
        }
        else if (policy.IsCompatMode)
        {
            TxtShieldBadge.Text = isCompact ? $"{count} · ⚡" : $"{count} · Compatible";
            TxtShieldBadge.Foreground = BrushShieldCompatText;
        }
        else
        {
            TxtShieldBadge.Text = isCompact ? $"{count}" : $"{count} bloqueados";
            TxtShieldBadge.Foreground = BrushShieldActiveText;
        }

        if (PopupShieldPanel != null && PopupShieldPanel.IsOpen)
        {
            UpdateShieldPopupCounters();
        }
    }

    private string? GetActiveTabDomainForShield()
    {
        if (_activeTab == null || _activeTab.IsNewTab) return null;
        if (_activeTab.CurrentUri != null && (_activeTab.CurrentUri.Scheme == "http" || _activeTab.CurrentUri.Scheme == "https"))
        {
            return PublicSuffix.GetRegistrableDomain(_activeTab.CurrentUri.Host);
        }
        if (!string.IsNullOrWhiteSpace(_activeTab.Url) && Uri.TryCreate(_activeTab.Url, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https"))
        {
            return PublicSuffix.GetRegistrableDomain(u.Host);
        }
        return null;
    }

    private void BorderShieldBadge_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (PopupShieldPanel == null) return;

        if (PopupShieldPanel.IsOpen)
        {
            PopupShieldPanel.IsOpen = false;
            return;
        }

        UpdateShieldPopupUI();
        PopupShieldPanel.IsOpen = true;
    }

    private void BtnCloseShieldPopup_Click(object sender, RoutedEventArgs e)
    {
        if (PopupShieldPanel != null)
            PopupShieldPanel.IsOpen = false;
    }

    private void UpdateShieldPopupCounters()
    {
        if (_activeTab == null) return;
        if (TxtShieldCountNetwork != null) TxtShieldCountNetwork.Text = _activeTab.BlockedNetworkCount.ToString();
        if (TxtShieldCountStubs != null) TxtShieldCountStubs.Text = _activeTab.BlockedStubsCount.ToString();
        if (TxtShieldCountPopups != null) TxtShieldCountPopups.Text = _activeTab.BlockedPopupsCount.ToString();
        if (TxtShieldCountOverlays != null) TxtShieldCountOverlays.Text = (_activeTab.BlockedOverlaysCount + _activeTab.BlockedCosmeticCount + _activeTab.BlockedAdapterCount).ToString();
        if (TxtShieldCountTelemetry != null) TxtShieldCountTelemetry.Text = _activeTab.BlockedTelemetry204Count.ToString();
    }

    private void UpdateShieldPopupUI()
    {
        if (PopupShieldPanel == null) return;
        _isUpdatingShieldPopupUI = true;
        try
        {
            UpdateShieldPopupCounters();

            string? domain = GetActiveTabDomainForShield();
            bool hasSite = !string.IsNullOrEmpty(domain);
            TxtShieldPopupDomain.Text = hasSite ? domain! : "Nueva pestaña";

            var policy = SitePolicy.Resolve(domain, _activeTab?.TemporaryCompatMode ?? false);

            ChkShieldSiteEnabled.IsEnabled = hasSite && SettingsManager.Current.EnableAdBlocker;
            ChkShieldSiteEnabled.IsChecked = policy.ShieldEnabled;
            PanelShieldSiteModules.IsEnabled = hasSite && policy.ShieldEnabled;
            BtnShieldCompatMode.IsEnabled = hasSite && SettingsManager.Current.EnableAdBlocker;

            if (!SettingsManager.Current.EnableAdBlocker)
            {
                TxtShieldSiteModeStatus.Text = "Escudo global desactivado en Ajustes";
                TxtShieldSiteModeStatus.Foreground = BrushShieldOffText;
            }
            else if (!hasSite)
            {
                TxtShieldSiteModeStatus.Text = "Abre un sitio web para configurar su escudo";
                TxtShieldSiteModeStatus.Foreground = BrushShieldOffText;
            }
            else if (!policy.ShieldEnabled)
            {
                TxtShieldSiteModeStatus.Text = "Protección pausada en este sitio";
                TxtShieldSiteModeStatus.Foreground = BrushShieldOffText;
            }
            else if (policy.IsCompatMode)
            {
                TxtShieldSiteModeStatus.Text = "⚡ Modo compatible activo en este sitio";
                TxtShieldSiteModeStatus.Foreground = BrushShieldCompatText;
            }
            else
            {
                TxtShieldSiteModeStatus.Text = "Protección estándar activa en este sitio";
                TxtShieldSiteModeStatus.Foreground = BrushShieldGreen;
            }

            ChkSiteModNetwork.IsChecked = policy.Network;
            ChkSiteModStubs.IsChecked = policy.Stubs;
            ChkSiteModPopups.IsChecked = policy.Popups;
            ChkSiteModOverlays.IsChecked = policy.Overlays;
            ChkSiteModCosmetic.IsChecked = policy.Cosmetic;
            ChkSiteModVideoDefuser.IsChecked = policy.VideoDefuser;
            ChkSiteModAdapter.IsChecked = policy.Adapter;
        }
        finally
        {
            _isUpdatingShieldPopupUI = false;
        }
    }

    private async void ChkShieldSiteEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingShieldPopupUI) return;
        string? domain = GetActiveTabDomainForShield();
        if (string.IsNullOrEmpty(domain)) return;

        bool enabled = ChkShieldSiteEnabled.IsChecked == true;
        if (_activeTab != null) _activeTab.TemporaryCompatMode = false;

        var profile = SitePolicy.GetOrCreateSiteProfile(domain);
        profile.Mode = enabled ? SitePolicy.ModeNormal : SitePolicy.ModeOff;
        SettingsManager.Save();

        await RefreshAllTabsShieldScriptsAsync();
        UpdateShieldPopupUI();
        UpdateShieldBadgeText();
    }

    private async void ChkShieldSiteModule_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingShieldPopupUI) return;
        string? domain = GetActiveTabDomainForShield();
        if (string.IsNullOrEmpty(domain)) return;

        var profile = SitePolicy.GetOrCreateSiteProfile(domain);
        profile.Network = ChkSiteModNetwork.IsChecked == true;
        profile.Stubs = ChkSiteModStubs.IsChecked == true;
        profile.Popups = ChkSiteModPopups.IsChecked == true;
        profile.Overlays = ChkSiteModOverlays.IsChecked == true;
        profile.Cosmetic = ChkSiteModCosmetic.IsChecked == true;
        profile.VideoDefuser = ChkSiteModVideoDefuser.IsChecked == true;
        profile.Adapter = ChkSiteModAdapter.IsChecked == true;
        SettingsManager.Save();

        await RefreshAllTabsShieldScriptsAsync();
        UpdateShieldPopupUI();
        UpdateShieldBadgeText();
    }

    private async void BtnShieldCompatMode_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab == null) return;
        await ActivateCompatModeForTabAsync(_activeTab, persistForSite: true);
        UpdateShieldPopupUI();
        if (PopupShieldPanel != null) PopupShieldPanel.IsOpen = false;
    }

    private async void BtnShieldReloadNow_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab == null) return;
        if (PopupShieldPanel != null) PopupShieldPanel.IsOpen = false;
        await RefreshTabShieldScriptsAsync(_activeTab);
        _activeTab.WebView?.CoreWebView2?.Reload();
    }

    private void BtnShieldCopyTabDiag_Click(object sender, RoutedEventArgs e)
    {
        if (PopupShieldPanel != null) PopupShieldPanel.IsOpen = false;
        CopyOrExportShieldLog(onlyActiveTab: true);
    }

    public void RecordUserReloadAndCheckCompat(BrowserTab tab)
    {
        if (tab == null || tab.IsDisposed || !SettingsManager.Current.EnableAdBlocker) return;
        string? host = tab.CurrentUri?.Host;
        if (string.IsNullOrEmpty(host)) return;

        string regDomain = PublicSuffix.GetRegistrableDomain(host);
        if (string.IsNullOrEmpty(regDomain)) return;

        var policy = SitePolicy.Resolve(regDomain, tab.TemporaryCompatMode);
        if (!policy.ShieldEnabled || policy.IsCompatMode) return;

        var now = DateTime.UtcNow;
        if (!string.Equals(tab.LastReloadHost, regDomain, StringComparison.OrdinalIgnoreCase))
        {
            tab.LastReloadHost = regDomain;
            tab.RecentReloadTimesUtc.Clear();
        }

        tab.RecentReloadTimesUtc.RemoveAll(t => (now - t).TotalSeconds > 20);
        tab.RecentReloadTimesUtc.Add(now);

        if (tab.RecentReloadTimesUtc.Count >= 2 && !tab.CompatibilityPromptShownDomains.Contains(regDomain))
        {
            tab.CompatibilityPromptShownDomains.Add(regDomain);
            OfferCompatibilityModeToast(tab, $"¿Problemas al cargar {regDomain}?");
        }
    }

    public void OfferCompatibilityModeToast(BrowserTab tab, string reasonPrefix)
    {
        if (tab == null || tab.IsDisposed || !SettingsManager.Current.EnableAdBlocker) return;
        string? host = tab.CurrentUri?.Host;
        string regDomain = PublicSuffix.GetRegistrableDomain(host);
        if (string.IsNullOrEmpty(regDomain)) regDomain = "este sitio";

        var policy = SitePolicy.Resolve(regDomain, tab.TemporaryCompatMode);
        if (!policy.ShieldEnabled || policy.IsCompatMode) return;

        ShowToast(
            $"🛡️ {reasonPrefix} Puedes probar el Modo Compatible de LexShield en {regDomain}.",
            "⚡ Probar modo compatible",
            () => _ = ActivateCompatModeForTabAsync(tab, persistForSite: true),
            durationSeconds: 10);
    }

    public async Task ActivateCompatModeForTabAsync(BrowserTab tab, bool persistForSite = true)
    {
        if (tab == null || tab.IsDisposed) return;
        tab.TemporaryCompatMode = true;

        string? host = tab.CurrentUri?.Host;
        string regDomain = PublicSuffix.GetRegistrableDomain(host);
        if (persistForSite && !string.IsNullOrEmpty(regDomain))
        {
            SitePolicy.SetSiteMode(regDomain, SitePolicy.ModeCompat);
            SettingsManager.Save();
        }

        await RefreshAllTabsShieldScriptsAsync();
        UpdateShieldBadgeText();
        tab.WebView?.CoreWebView2?.Reload();
        ShowToast($"⚡ Modo compatible activado en {(string.IsNullOrEmpty(regDomain) ? "esta pestaña" : regDomain)}.");
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
        bool isFullscreen = _activeTab?.WebView?.CoreWebView2?.ContainsFullScreenElement ?? false;
        if (WindowState == WindowState.Maximized && !isFullscreen && WindowChrome.GetWindowChrome(this) != null)
        {
            RootWindowGrid.Margin = new Thickness(0);
            if (AppWindowChrome != null)
            {
                AppWindowChrome.ResizeBorderThickness = new Thickness(0);
            }
        }
        else
        {
            RootWindowGrid.Margin = new Thickness(0);
            if (AppWindowChrome != null)
            {
                AppWindowChrome.ResizeBorderThickness = new Thickness(6);
            }
        }

        if (BrowserContainer != null)
        {
            BrowserContainer.Margin = WindowState == WindowState.Normal
                ? new Thickness(6, 0, 6, 6)
                : new Thickness(0);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // GESTIÓN DE ZOOM INTELIGENTE Y RESTABLECIMIENTO (Airspace-Safe Popup)
    // ─────────────────────────────────────────────────────────────────────
    private void UpdateZoomIndicatorUI(bool showFloating = false)
    {
        if (_activeTab?.WebView == null)
        {
            PillZoomIndicator.Visibility = Visibility.Collapsed;
            FloatingZoomWidget.Visibility = Visibility.Collapsed;
            if (PopupZoomWidget != null) PopupZoomWidget.IsOpen = false;
            return;
        }

        double factor = _activeTab.WebView.ZoomFactor;
        int percentage = (int)Math.Round(factor * 100);

        if (Math.Abs(factor - 1.0) < 0.02)
        {
            PillZoomIndicator.Visibility = Visibility.Collapsed;
            FloatingZoomWidget.Visibility = Visibility.Collapsed;
            if (PopupZoomWidget != null) PopupZoomWidget.IsOpen = false;
            _zoomWidgetTimer.Stop();
        }
        else
        {
            TxtZoomValue.Text = $"{percentage}%";
            PillZoomIndicator.Visibility = Visibility.Visible;

            if (showFloating)
            {
                TxtFloatingZoomValue.Text = $"{percentage}%";
                FloatingZoomWidget.Visibility = Visibility.Visible;
                if (PopupZoomWidget != null) PopupZoomWidget.IsOpen = true;
                _zoomWidgetTimer.Stop();
                _zoomWidgetTimer.Start();
            }
        }
    }

    private static readonly double[] ZoomSteps =
    {
        0.25, 0.33, 0.50, 0.67, 0.75, 0.80, 0.90, 1.00, 1.10, 1.25, 1.50, 1.75, 2.00, 2.50, 3.00, 4.00, 5.00
    };

    public void ZoomIn()
    {
        if (_activeTab?.WebView == null) return;
        double current = _activeTab.WebView.ZoomFactor;
        double next = ZoomSteps.FirstOrDefault(s => s > current + 0.015);
        if (next == 0) next = ZoomSteps[^1];
        _activeTab.WebView.ZoomFactor = next;
        UpdateZoomIndicatorUI(showFloating: true);
    }

    public void ZoomOut()
    {
        if (_activeTab?.WebView == null) return;
        double current = _activeTab.WebView.ZoomFactor;
        double prev = ZoomSteps.LastOrDefault(s => s < current - 0.015);
        if (prev == 0) prev = ZoomSteps[0];
        _activeTab.WebView.ZoomFactor = prev;
        UpdateZoomIndicatorUI(showFloating: true);
    }

    public void ResetZoom()
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
        ZoomIn();
    }

    private void BtnZoomOut_Click(object sender, RoutedEventArgs e)
    {
        ZoomOut();
    }

    /// <summary>
    /// Acceso seguro con descriptores de reflexión en caché estática al CoreWebView2Controller subyacente.
    /// </summary>
    public static CoreWebView2Controller? GetController(WebView2? webView)
    {
        if (webView == null) return null;
        try
        {
            var baseObj = WebView2BaseField?.GetValue(webView);
            if (baseObj == null) return null;
            _cachedControllerProperty ??= baseObj.GetType().GetProperty("CoreWebView2Controller", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return _cachedControllerProperty?.GetValue(baseObj) as CoreWebView2Controller;
        }
        catch
        {
            return null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // CONFIGURACIÓN, HISTORIAL Y PILL SIN TELEMETRÍA
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

    public void OpenHistory()
    {
        if (IsLocked) return;
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
        if (IsLocked) return;
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
            _ = RefreshAllTabsShieldScriptsAsync();
            UpdateShieldBadgeText();

            if (prevEngine != SettingsManager.Current.DefaultSearchEngine ||
                prevCustom != SettingsManager.Current.CustomSearchUrl)
            {
                foreach (var t in Tabs.Where(t => t.IsNewTab && !t.IsDisposed && t.WebView?.CoreWebView2 != null))
                {
                    try { t.WebView.NavigateToString(NewTabPage.GetHtml(t.IsIncognito, isGuest: t.IsGuest)); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] Error al abrir configuración: {ex}");
        }
    }
}

