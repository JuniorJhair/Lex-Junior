using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shell;
using System.Windows.Threading;
using LexBrowser.Core;
using LexBrowser.Core.AdBlock;
using LexBrowser.Core.Engine;
using LexBrowser.Core.Interop;
using LexBrowser.Core.Notifications;
using LexBrowser.Core.Pdf;
using LexBrowser.Core.Tv;
using LexBrowser.UI.Models;
using LexBrowser.UI.Views;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LexBrowser;

public partial class MainWindow
{
    private static readonly byte[] EmptyResponseBytes = Array.Empty<byte>();
    private bool _fullScreenChromeActive = false;
    private readonly Dictionary<BrowserTab, double> _preFullScreenZoomByTab = new();
    private DispatcherTimer? _suspensionTimer;
    private bool _isSuspensionTickRunning;
    private bool _isWindowClosing;

    private TabSuspensionCandidate BuildSuspensionCandidate(BrowserTab tab)
    {
        var core = tab.WebView?.CoreWebView2;
        bool isPlayingAudio = false;
        bool isFullScreen = false;
        bool alreadySuspended = tab.IsSuspendedByLex;
        if (core != null && !tab.IsDisposed)
        {
            try { isPlayingAudio = core.IsDocumentPlayingAudio; } catch { }
            try { isFullScreen = core.ContainsFullScreenElement; } catch { }
            try { alreadySuspended = alreadySuspended || core.IsSuspended; } catch { }
        }

        string? origin = NotificationPermissionManager.NormalizeOrigin(tab.Url ?? tab.CurrentUri?.AbsoluteUri);
        bool hasNotificationPermission = false;
        if (!string.IsNullOrEmpty(origin))
        {
            var notifMode = NotificationPermissionManager.GetSiteMode(origin, tab.IsIncognito);
            hasNotificationPermission = notifMode is SiteNotificationMode.Allow
                or SiteNotificationMode.Silent
                or SiteNotificationMode.Digest;
        }

        bool isFloatingTarget = _pipWindow?.IsVisible == true && _pipWindow.TargetTab == tab;
        bool isPipSource = _nativePipSourceTab == tab;

        return new TabSuspensionCandidate(
            IsActive: _activeTab == tab || tab.IsSelected,
            IsPinned: tab.IsPinned,
            IsNewTab: tab.IsNewTab,
            IsLazyLoaded: tab.IsLazyLoaded,
            IsLoading: tab.IsLoading,
            IsDisposed: tab.IsDisposed,
            IsPlayingAudio: isPlayingAudio,
            HasActiveMedia: tab.HasActiveMedia || tab.MediaIsPlaying,
            IsPipSource: isPipSource,
            IsFloatingTarget: isFloatingTarget,
            IsSnifferActive: tab.IsMediaSnifferActive,
            IsFullScreen: isFullScreen,
            HasNotificationPermission: hasNotificationPermission,
            HasCaptureDevicePermission: tab.HasCaptureDevicePermission,
            AlreadySuspended: alreadySuspended,
            LastDeactivatedUtc: tab.LastDeactivatedUtc,
            NextAttemptUtc: tab.NextSuspendAttemptUtc);
    }

    private void RescheduleSuspensionTimer()
    {
        if (_isWindowClosing)
        {
            _suspensionTimer?.Stop();
            return;
        }

        int idleMinutes = SettingsManager.Current.TabSuspensionMinutes;
        if (idleMinutes <= 0 || Tabs.Count == 0)
        {
            _suspensionTimer?.Stop();
            return;
        }

        bool suspendPinned = SettingsManager.Current.SuspendPinnedTabs;
        DateTime nowUtc = DateTime.UtcNow;
        DateTime? minEligible = null;

        for (int i = 0; i < Tabs.Count; i++)
        {
            var candidate = BuildSuspensionCandidate(Tabs[i]);
            DateTime? eligible = TabSuspensionPolicy.NextEligibleUtc(in candidate, idleMinutes, suspendPinned, nowUtc);
            if (eligible.HasValue && (!minEligible.HasValue || eligible.Value < minEligible.Value))
            {
                minEligible = eligible.Value;
            }
        }

        if (!minEligible.HasValue)
        {
            _suspensionTimer?.Stop();
            return;
        }

        TimeSpan delay = minEligible.Value - nowUtc;
        if (delay < TimeSpan.FromSeconds(30))
        {
            delay = TimeSpan.FromSeconds(30);
        }

        if (_suspensionTimer == null)
        {
            _suspensionTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _suspensionTimer.Tick += SuspensionTimer_Tick;
        }
        else
        {
            _suspensionTimer.Stop();
        }

        _suspensionTimer.Interval = delay;
        _suspensionTimer.Start();
    }

    private async void SuspensionTimer_Tick(object? sender, EventArgs e)
    {
        _suspensionTimer?.Stop();
        if (_isWindowClosing || _isSuspensionTickRunning) return;
        _isSuspensionTickRunning = true;

        try
        {
            int idleMinutes = SettingsManager.Current.TabSuspensionMinutes;
            if (idleMinutes <= 0) return;

            bool suspendPinned = SettingsManager.Current.SuspendPinnedTabs;
            var snapshot = Tabs.ToList();

            foreach (var tab in snapshot)
            {
                if (_isWindowClosing) break;
                var candidate = BuildSuspensionCandidate(tab);
                if (TabSuspensionPolicy.CanSuspend(in candidate, idleMinutes, suspendPinned, DateTime.UtcNow))
                {
                    await TrySuspendTabAsync(tab);
                }
            }
        }
        finally
        {
            _isSuspensionTickRunning = false;
            if (!_isWindowClosing)
            {
                RescheduleSuspensionTimer();
            }
        }
    }

    private async Task TrySuspendTabAsync(BrowserTab tab)
    {
        var webView = tab.WebView;
        var core = webView?.CoreWebView2;
        if (core == null || tab.IsDisposed) return;

        CoreWebView2Controller? controller = null;
        bool forcedHidden = false;
        try
        {
            // WebView2 requiere IsVisible == false para suspender; aseguramos el controlador si sigue visible.
            controller = GetController(webView);
            if (controller != null && controller.IsVisible)
            {
                controller.IsVisible = false;
                forcedHidden = true;
            }

            bool ok = await core.TrySuspendAsync();
            if (!ok)
            {
                if (forcedHidden && controller != null && !tab.IsDisposed &&
                    (_activeTab == tab || webView!.Visibility == Visibility.Visible))
                {
                    controller.IsVisible = true;
                }
                tab.NextSuspendAttemptUtc = DateTime.UtcNow.AddMinutes(5);
                return;
            }

            tab.IsSuspendedByLex = true;
            // Carrera: el usuario pudo seleccionar la pestaña mientras se suspendía
            if (_activeTab == tab || tab.IsDisposed)
            {
                ResumeTab(tab);
            }
            else
            {
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
            }
        }
        catch (Exception ex)
        {
            if (forcedHidden && controller != null && !tab.IsDisposed &&
                (_activeTab == tab || webView!.Visibility == Visibility.Visible))
            {
                try { controller.IsVisible = true; } catch { }
            }
            tab.NextSuspendAttemptUtc = DateTime.UtcNow.AddMinutes(10);
            ShieldLogger.LogForTab(tab, "TAB_SUSPEND_ERROR", tab.CurrentUri?.Host ?? "unknown", "", "Suspend", ex.Message);
        }
    }

    private void ResumeTab(BrowserTab tab)
    {
        bool shouldResume = tab.IsSuspendedByLex;
        var core = tab.WebView?.CoreWebView2;
        if (!shouldResume && core != null && !tab.IsDisposed)
        {
            try { shouldResume = core.IsSuspended; } catch { }
        }
        if (!shouldResume) return;

        tab.IsSuspendedByLex = false;
        try
        {
            if (core != null && core.IsSuspended)
            {
                core.Resume();
            }

            if (tab.WebView != null && tab.WebView.Visibility == Visibility.Visible)
            {
                var controller = GetController(tab.WebView);
                if (controller != null && !controller.IsVisible)
                {
                    controller.IsVisible = true;
                }
            }
        }
        catch (Exception ex)
        {
            ShieldLogger.LogForTab(tab, "TAB_RESUME_ERROR", tab.CurrentUri?.Host ?? "unknown", "", "Resume", ex.Message);
        }
    }

    private void ApplyFullScreenChrome(bool enter)
    {
        if (enter == _fullScreenChromeActive) return;
        _fullScreenChromeActive = enter;

        var targetTab = _activeTab;

        if (enter)
        {
            if (targetTab?.WebView != null && !_preFullScreenZoomByTab.ContainsKey(targetTab))
            {
                try { _preFullScreenZoomByTab[targetTab] = targetTab.WebView.ZoomFactor; }
                catch { _preFullScreenZoomByTab[targetTab] = 1.0; }
            }

            _savedWindowState = WindowState;
            _savedResizeMode = ResizeMode;

            // Colapsar barras superiores completamente
            RowHeader.Height = new GridLength(0);
            RowNavBar.Height = new GridLength(0);
            RowBookmarksBar.Height = new GridLength(0);
            RowInfoBar.Height = new GridLength(0);
            RowProgressBar.Height = new GridLength(0);

            // Eliminar WindowChrome durante fullscreen para evitar bordes negros o franjas de taskbar
            WindowChrome.SetWindowChrome(this, null);

            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
            UpdateWindowMargin();
        }
        else
        {
            // Restaurar barras del navegador (Auditoría E7: mantener barra en modo invitado)
            RowHeader.Height = new GridLength(42);
            RowNavBar.Height = new GridLength(50);
            bool showBar = SettingsManager.Current.ShowBookmarksBar || _isGuestSession;
            RowBookmarksBar.Height = showBar ? new GridLength(34) : new GridLength(0);
            RowBookmarksGrid.Visibility = showBar ? Visibility.Visible : Visibility.Collapsed;
            RowInfoBar.Height = GridLength.Auto;
            RowProgressBar.Height = new GridLength(2);

            // Restaurar WindowChrome normal
            WindowChrome.SetWindowChrome(this, AppWindowChrome);

            ResizeMode = _savedResizeMode;

            if (_savedWindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
                WindowState = WindowState.Maximized;
            }
            else
            {
                WindowState = _savedWindowState;
            }

            UpdateWindowMargin();

            if (targetTab != null && !targetTab.IsDisposed && targetTab.WebView != null)
            {
                bool hasPreZoom = false;
                double preFullScreenZoom = 1.0;
                bool isStillFs = targetTab.WebView.CoreWebView2?.ContainsFullScreenElement == true;
                if (!isStillFs && _preFullScreenZoomByTab.Remove(targetTab, out preFullScreenZoom))
                {
                    hasPreZoom = true;
                }

                try
                {
                    if (hasPreZoom)
                    {
                        targetTab.WebView.ZoomFactor = preFullScreenZoom;
                    }
                    GetController(targetTab.WebView)?.NotifyParentWindowPositionChanged();
                    UpdateZoomIndicatorUI(showFloating: false);
                }
                catch { }

                _ = Dispatcher.InvokeAsync(async () =>
                {
                    await Task.Delay(150);
                    var core = targetTab.WebView?.CoreWebView2;
                    if (!targetTab.IsDisposed && targetTab.WebView != null && core != null && !core.ContainsFullScreenElement)
                    {
                        try
                        {
                            if (hasPreZoom && Math.Abs(targetTab.WebView.ZoomFactor - preFullScreenZoom) > 0.01)
                            {
                                targetTab.WebView.ZoomFactor = preFullScreenZoom;
                            }
                            GetController(targetTab.WebView)?.NotifyParentWindowPositionChanged();
                            UpdateZoomIndicatorUI(showFloating: false);
                        }
                        catch { }
                    }
                });
            }
        }
    }

    private static bool IsSafeHttpUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    // Pinceles estáticos congelados para evitar asignaciones repetidas (Auditoría L3)
    private static readonly SolidColorBrush s_bookmarkDefaultBorderBrush = CreateFrozenBrush(0x2E, 0x2E, 0x3A);
    private static readonly SolidColorBrush s_bookmarkDefaultBgBrush = CreateFrozenBrush(0x18, 0x18, 0x22);
    private static readonly SolidColorBrush s_bookmarkActiveBgBrush = CreateFrozenBrush(0x22, 0x22, 0x38);
    private static readonly SolidColorBrush s_bookmarkMutedIconBrush = CreateFrozenBrush(0x8B, 0x8B, 0x96);
    private static readonly SolidColorBrush s_bookmarkNormalIconBrush = CreateFrozenBrush(0xA1, 0xA1, 0xAA);
    private static readonly SolidColorBrush s_translateActiveBgBrush = CreateFrozenBrush(0x1E, 0x3A, 0x8A);
    private static readonly SolidColorBrush s_translateDefaultBgBrush = CreateFrozenBrush(0x1E, 0x29, 0x3B);
    private static readonly SolidColorBrush s_dragTabBorderBrush = CreateFrozenBrush(0x81, 0x8C, 0xF8);

    public record TranslateLanguageItem(string Code, string Name);

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

        ResumeTab(tab);
        ReinsertWebViewToContainer(tab);
        SelectTab(tab, force: true);
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
        if (_activeTab == tab || tab.IsSelected)
        {
            ResumeTab(tab);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // CREAR NUEVA PESTAÑA (Al costado de la activa o en índice indicado)
    // ─────────────────────────────────────────────────────────────────────
    private async Task<BrowserTab> AddNewTabAsync(string? initialUrl = null, bool isIncognito = false, int? insertIndex = null, bool skipInitialNavigate = false, bool bypassLockCheck = false)
    {
        if (!bypassLockCheck && IsBrowserLockOverlayActive())
        {
            if (_activeTab != null) return _activeTab;
        }
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
        string tabDefaultTitle = _isGuestSession ? "Modo Invitado" : (isIncognito ? "Incógnito" : "Nueva pestaña");

        var tab = new BrowserTab(webView)
        {
            Url = isNewTabDefault ? "" : (initialUrl ?? ""),
            Title = isNewTabDefault ? tabDefaultTitle : "Cargando...",
            IsIncognito = isIncognito,
            IsGuest = _isGuestSession,
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

        if (isNewTabDefault)
        {
            _isUpdatingOmniboxTextProgrammatically = true;
            TxtUrl.Text = "";
            _isUpdatingOmniboxTextProgrammatically = false;
            TxtUrl.Focus();
            Keyboard.Focus(TxtUrl);
            TxtUrl.CaretIndex = 0;
        }

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
                    try
                    {
                        webView.NavigateToString(NewTabPage.GetHtml(isIncognito, isGuest: _isGuestSession));
                    }
                    catch (Exception navEx)
                    {
                        ShieldLogger.Log("NAVIGATE_TO_STRING_ERROR", "local", "newtab", "MainFrame", navEx.Message, tab.TabId);
                        webView.NavigateToString("<!DOCTYPE html><html><body style='background:#0F0F12;color:#fff;font-family:sans-serif;padding:40px;text-align:center;'><h2>Nueva pestaña</h2></body></html>");
                    }
                }
                else if (!string.IsNullOrWhiteSpace(initialUrl))
                {
                    string navTarget = PdfStudioService.IsPdfTarget(initialUrl)
                        ? PdfStudioService.GetViewerUrl(initialUrl)
                        : initialUrl;
                    webView.CoreWebView2.Navigate(navTarget);
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

        try
        {
            tab.WebView.ZoomFactor = 1.0;
            var controller = GetController(tab.WebView);
            if (controller != null)
            {
                controller.NotifyParentWindowPositionChanged();
            }
        }
        catch { }

        try
        {
            core.Settings.AreDevToolsEnabled = true;
        }
        catch { }

        // Atajos F12, Ctrl+Shift+I (DevTools), Ctrl+Shift+L (Shield Logger) y control de Zoom cuando el foco está sobre el WebView
        tab.WebView.PreviewKeyDown += (s, e) =>
        {
            var win = tab.OwnerWindow ?? this;
            if (win.IsBrowserLockOverlayActive())
            {
                e.Handled = true;
                return;
            }

            if (e.Key == Key.F12 || (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.I))
            {
                e.Handled = true;
                try { core.OpenDevToolsWindow(); } catch { }
            }
            else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.L)
            {
                e.Handled = true;
                win.CopyOrExportShieldLog();
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.D0 || e.Key == Key.NumPad0))
            {
                e.Handled = true;
                win.ResetZoom();
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.OemPlus || e.Key == Key.Add || e.Key == Key.Oem1 || e.Key == Key.Oem6))
            {
                e.Handled = true;
                win.ZoomIn();
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.OemMinus || e.Key == Key.Subtract || e.Key == Key.OemQuestion || e.Key == Key.OemPeriod))
            {
                e.Handled = true;
                win.ZoomOut();
            }
        };

        // Zoom suave con Ctrl + Rueda del ratón cuando el cursor está sobre el WebView
        tab.WebView.PreviewMouseWheel += (s, e) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                if (e.Delta > 0)
                {
                    (tab.OwnerWindow ?? this).ZoomIn();
                }
                else if (e.Delta < 0)
                {
                    (tab.OwnerWindow ?? this).ZoomOut();
                }
            }
        };

        // Inyectar scripts de optimización, anti-anuncios, iframes, PiP y captura/autocompletado pasivo de contraseñas
        // cada uno en su propio bloque try/catch aislado para que un fallo en uno no cancele los demás.
        await RefreshTabShieldScriptsAsync(tab);

        try
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(PasswordManager.InjectedPasswordScript);
        }
        catch (Exception ex)
        {
            ShieldLogger.LogForTab(tab, "SCRIPT_INJECT_ERROR", "PasswordManager", "", "Init", ex.Message);
        }

        // 1. MANEJO DE PANTALLA COMPLETA REAL DE LAPTOP (CERO FRANJAS VACÍAS Y RESTABLECIMIENTO DE ZOOM)
        core.ContainsFullScreenElementChanged += (s, e) =>
        {
            var win = tab.OwnerWindow ?? this;
            win.Dispatcher.Invoke(() =>
            {
                bool enterFs = core.ContainsFullScreenElement;
                if (win._activeTab == tab)
                {
                    win.ApplyFullScreenChrome(enterFs);
                }
                else if (!enterFs && win._preFullScreenZoomByTab.Remove(tab, out double bgZoom))
                {
                    try { tab.WebView.ZoomFactor = bgZoom; } catch { }
                }
            });
        };

        // 2. ESCUDO ANTI-POPUNDERS + CONEXIÓN NATIVA DE VENTANAS EMERGENTES OAUTH
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
            tab.LastNewWindowRequestedTime = DateTime.UtcNow;

            Uri? currentUri = null;
            try
            {
                if (tab.WebView?.Source != null)
                {
                    currentUri = tab.WebView.Source;
                }
            }
            catch { }

            bool isFromSubframe = false;
            Uri? frameSourceUri = null;
            try
            {
                if (e.OriginalSourceFrameInfo != null)
                {
                    isFromSubframe = e.OriginalSourceFrameInfo.FrameKind != CoreWebView2FrameKind.MainFrame;
                    if (!string.IsNullOrEmpty(e.OriginalSourceFrameInfo.Source) &&
                        Uri.TryCreate(e.OriginalSourceFrameInfo.Source, UriKind.Absolute, out var fUri))
                    {
                        frameSourceUri = fUri;
                    }
                }
            }
            catch { }

            bool isOAuthUrl = url.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase) ||
                              url.Contains("appleid.apple.com", StringComparison.OrdinalIgnoreCase) ||
                              url.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) ||
                              url.Contains("login.live.com", StringComparison.OrdinalIgnoreCase) ||
                              url.Contains("/oauth", StringComparison.OrdinalIgnoreCase) ||
                              url.Contains("gsi/select", StringComparison.OrdinalIgnoreCase);

            string tabDesc = $"Tab:{tab.TabId} ({(currentUri != null ? currentUri.Host : "blank")})";
            var originPolicy = SitePolicy.Resolve(currentUri, tab.TemporaryCompatMode);

            if (string.IsNullOrWhiteSpace(url) || url == "about:blank")
            {
                if (originPolicy.ShieldEnabled && originPolicy.Popups && (isFromSubframe || !e.IsUserInitiated))
                {
                    ShieldLogger.LogForTab(tab, "NEW_WINDOW_CANCELLED", currentUri?.Host ?? "unknown", url, isFromSubframe ? "Subframe" : "MainFrame", "Empty/about:blank popunder", tabDesc);
                    e.Handled = true;
                    return;
                }
            }
            else if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                string destHost = uri.Host.ToLowerInvariant();
                bool isSensitiveAuthOrPay = isOAuthUrl ||
                    ShieldExemptions.IsPaymentOrAuthHost(destHost) ||
                    ShieldExemptions.IsPaymentOrAuthHost(frameSourceUri?.Host);

                if (originPolicy.ShieldEnabled && originPolicy.Popups && !isSensitiveAuthOrPay)
                {
                    bool shouldBlock = (originPolicy.Network && AdBlockFilter.ShouldBlock(uri, currentUri?.Host, CoreWebView2WebResourceContext.Document)) ||
                                       AdBlockFilter.IsSuspiciousPopup(uri, currentUri);
                    string blockReason = "AdBlockFilter domain or suspicious pattern";

                    if (!shouldBlock && isFromSubframe)
                    {
                        string targetHost = destHost;
                        bool matchesMainSite = currentUri != null && PublicSuffix.IsSameRegistrableDomain(targetHost, currentUri.Host);
                        bool matchesFrameSite = frameSourceUri != null && PublicSuffix.IsSameRegistrableDomain(targetHost, frameSourceUri.Host);
                        bool fromVideoEmbed = ShieldExemptions.IsVideoEmbedUrl(frameSourceUri);

                        if (!matchesMainSite && !matchesFrameSite && !AdBlockFilter.IsTrustedDomain(targetHost) &&
                            (!e.IsUserInitiated || fromVideoEmbed))
                        {
                            shouldBlock = true;
                            blockReason = fromVideoEmbed ? "Cross-origin video embed popup hijack" : "Cross-origin subframe unauthorized popunder";
                        }
                    }
                    else if (!shouldBlock && !isFromSubframe &&
                             ShieldExemptions.IsVideoEmbedUrl(currentUri) &&
                             currentUri != null &&
                             !PublicSuffix.IsSameRegistrableDomain(destHost, currentUri.Host) &&
                             !AdBlockFilter.IsTrustedDomain(destHost))
                    {
                        shouldBlock = true;
                        blockReason = "Video embed top-frame cross-origin popup hijack";
                    }

                    if (shouldBlock)
                    {
                        ShieldLogger.LogForTab(tab, "NEW_WINDOW_CANCELLED", currentUri?.Host ?? "unknown", uri.AbsoluteUri, isFromSubframe ? "Subframe" : "MainFrame", blockReason, tabDesc);
                        e.Handled = true;
                        win.Dispatcher.Invoke(() =>
                        {
                            tab.RecordShieldBlock(ShieldBlockCategory.Popup);
                            if (win._activeTab == tab)
                                win.UpdateShieldBadgeText();
                        });
                        return;
                    }
                }
            }

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

            ShieldLogger.LogForTab(tab, "NEW_WINDOW_ALLOWED", currentUri?.Host ?? "unknown", url, isFromSubframe ? "Subframe" : "MainFrame", isOAuthUrl ? "OAuth login window" : (hasPopupSize ? "Popup window with dimensions" : "User initiated new window"), tabDesc);

            var deferral = e.GetDeferral();
            _ = win.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    if (hasPopupSize || isOAuthUrl)
                    {
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
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Document);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Script);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.XmlHttpRequest);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Fetch);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Image);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Ping);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Other);
        core.AddWebResourceRequestedFilter("https://pdf.lex.local/*", CoreWebView2WebResourceContext.All);
        core.AddWebResourceRequestedFilter("https://tv.lex.local/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (s, e) =>
        {
            if (PdfStudioService.TryHandleWebResourceRequest(core.Environment, e))
                return;
            if (TvStudioService.TryHandleWebResourceRequest(core.Environment, e))
                return;

            if (SettingsManager.Current.EnableAdBlocker && Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri))
            {
                string mainTabHost = tab.CurrentUri?.Host ?? "unknown";
                if (mainTabHost == "unknown" && !string.IsNullOrEmpty(tab.Url) && Uri.TryCreate(tab.Url, UriKind.Absolute, out var tabUrlUri))
                {
                    mainTabHost = tabUrlUri.Host;
                }

                string originHost = mainTabHost;
                string frameDesc = e.ResourceContext.ToString();

                string referer = string.Empty;
                try
                {
                    var reqHeaders = e.Request.Headers;
                    if (reqHeaders.Contains("Referer"))
                        referer = reqHeaders.GetHeader("Referer");
                    else if (reqHeaders.Contains("Origin"))
                        referer = reqHeaders.GetHeader("Origin");
                }
                catch { }

                if (!string.IsNullOrEmpty(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var refUri))
                {
                    if (originHost == "unknown" || !refUri.Host.Equals(originHost, StringComparison.OrdinalIgnoreCase))
                    {
                        originHost = refUri.Host;
                        frameDesc = $"Subframe:{refUri.Host}|{e.ResourceContext}";
                    }
                }
                else if (e.ResourceContext == CoreWebView2WebResourceContext.Document)
                {
                    if (originHost == "unknown" || !uri.Host.Equals(originHost, StringComparison.OrdinalIgnoreCase))
                    {
                        originHost = uri.Host;
                        frameDesc = $"SubframeDoc:{uri.Host}";
                    }
                }

                // Evaluar política del sitio principal de la pestaña (o del originHost si la pestaña aún no tiene host) con caché por revisión
                string policyHost = (mainTabHost != "unknown" && !string.IsNullOrEmpty(mainTabHost)) ? mainTabHost : originHost;
                var policy = tab.ResolveCachedPolicy(policyHost);
                if (!policy.ShieldEnabled)
                    return;

                bool isDailymotionContext = PublicSuffix.HostMatchesDomain(mainTabHost, "dailymotion.com") ||
                                            (Uri.TryCreate(tab.Url, UriKind.Absolute, out var dmTabUri) && PublicSuffix.HostMatchesDomain(dmTabUri.Host, "dailymotion.com")) ||
                                            (Uri.TryCreate(referer, UriKind.Absolute, out var dmRefUri) && PublicSuffix.HostMatchesDomain(dmRefUri.Host, "dailymotion.com")) ||
                                            PublicSuffix.HostMatchesDomain(uri.Host, "dailymotion.com") ||
                                            PublicSuffix.HostMatchesDomain(originHost, "dailymotion.com");

                if (isDailymotionContext && (originHost == "unknown" || !PublicSuffix.HostMatchesDomain(originHost, "dailymotion.com")))
                {
                    originHost = "geo.dailymotion.com";
                }

                byte[] stubBytes = Array.Empty<byte>();
                string stubContentType = string.Empty;
                bool isRedirectStub = false;
                if (policy.Stubs)
                {
                    bool allowThisStub = policy.ImaStub || !uri.AbsolutePath.EndsWith("ima3.js", StringComparison.OrdinalIgnoreCase);
                    if (allowThisStub)
                    {
                        isRedirectStub = AdBlockResources.TryGetRedirectStub(uri, out stubBytes, out stubContentType, originHost, e.ResourceContext);
                    }
                }

                bool shouldBlockNetwork = !isRedirectStub && policy.Network && AdBlockFilter.ShouldBlock(uri, originHost, e.ResourceContext);

                if (isRedirectStub || shouldBlockNetwork)
                {
                    string tabDesc = ShieldLogger.DiagnosticMode
                        ? $"Tab:{tab.TabId} ({(tab.CurrentUri != null ? tab.CurrentUri.Host : "blank")})"
                        : $"Tab:{tab.TabId}";

                    int statusCode = 403;
                    string statusReason = "Blocked by LexShield";
                    string contentType = "Content-Type: text/plain";
                    byte[] responseBytes = EmptyResponseBytes;

                    bool isYouTubeApiJson = (PublicSuffix.HostMatchesDomain(uri.Host, "youtube.com") ||
                                             PublicSuffix.HostMatchesDomain(uri.Host, "youtu.be")) &&
                                            (uri.AbsolutePath.Contains("/youtubei/", StringComparison.OrdinalIgnoreCase) ||
                                             uri.AbsolutePath.Contains("/get_midroll_info", StringComparison.OrdinalIgnoreCase));

                    if (isRedirectStub)
                    {
                        statusCode = 200;
                        statusReason = "OK";
                        contentType = stubContentType;
                        responseBytes = stubBytes;

                        ShieldLogger.LogForTab(tab, "REQUEST_STUB_200", originHost, uri.AbsoluteUri, frameDesc, "AdBlockResources redirect stub (200 OK)", tabDesc);
                    }
                    else if (isYouTubeApiJson)
                    {
                        statusCode = 200;
                        statusReason = "OK";
                        contentType = "Content-Type: application/json; charset=utf-8";
                        responseBytes = Encoding.UTF8.GetBytes("{}");
                        ShieldLogger.LogForTab(tab, "REQUEST_STUB_200", originHost, uri.AbsoluteUri, frameDesc, "YouTube ad JSON stub (200 OK)", tabDesc);
                    }
                    else if (PublicSuffix.HostMatchesDomain(uri.Host, "youtube.com") ||
                             PublicSuffix.HostMatchesDomain(uri.Host, "youtu.be") ||
                             AdBlockFilter.IsGoogleDomain(originHost))
                    {
                        statusCode = 204;
                        statusReason = "No Content";
                        ShieldLogger.LogForTab(tab, "REQUEST_BLOCKED_204", originHost, uri.AbsoluteUri, frameDesc, "Google/YouTube ad or telemetry endpoint", tabDesc);
                    }
                    else
                    {
                        ShieldLogger.LogForTab(tab, "REQUEST_BLOCKED_403", originHost, uri.AbsoluteUri, frameDesc, "Blocked ad domain or pattern", tabDesc);
                    }

                    string allowOrigin = "*";
                    string allowHeaders = "*";
                    bool withCredentials = false;
                    try
                    {
                        var corsHeaders = e.Request.Headers;
                        if (corsHeaders.Contains("Origin"))
                        {
                            string reqOrigin = corsHeaders.GetHeader("Origin");
                            if (Uri.TryCreate(reqOrigin, UriKind.Absolute, out var originUri) &&
                                (originUri.Scheme == Uri.UriSchemeHttp || originUri.Scheme == Uri.UriSchemeHttps))
                            {
                                allowOrigin = originUri.GetLeftPart(UriPartial.Authority);
                                withCredentials = true;
                                if (corsHeaders.Contains("Access-Control-Request-Headers"))
                                {
                                    string reqHdrs = corsHeaders.GetHeader("Access-Control-Request-Headers");
                                    if (!string.IsNullOrWhiteSpace(reqHdrs) && reqHdrs.IndexOfAny(new[] { '\r', '\n' }) < 0)
                                        allowHeaders = reqHdrs;
                                }
                            }
                        }
                    }
                    catch { }

                    string responseHeaders = $"{contentType}\r\nAccess-Control-Allow-Origin: {allowOrigin}\r\n" +
                        (withCredentials ? "Access-Control-Allow-Credentials: true\r\nVary: Origin\r\n" : "") +
                        $"Access-Control-Allow-Methods: GET, POST, OPTIONS, HEAD\r\nAccess-Control-Allow-Headers: {allowHeaders}";

                    e.Response = core.Environment.CreateWebResourceResponse(
                        new MemoryStream(responseBytes, 0, responseBytes.Length, writable: false, publiclyVisible: true), statusCode, statusReason, responseHeaders);

                    var category = isRedirectStub
                        ? ShieldBlockCategory.Stub
                        : (statusCode == 204 ? ShieldBlockCategory.Telemetry204 : ShieldBlockCategory.Network);

                    var win = tab.OwnerWindow ?? this;
                    _ = win.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                    {
                        if (tab.IsDisposed) return;
                        tab.RecordShieldBlock(category);
                        if (win._activeTab == tab)
                            win.UpdateShieldBadgeText();
                    });
                }
                else
                {
                    // Señal C# -> JS para Dailymotion: al detectar manifiesto de video real, apagar el defuser inmediatamente
                    if (isDailymotionContext && !tab.DailymotionRealVideoSignaled &&
                        (uri.AbsoluteUri.Contains("cdndirector", StringComparison.OrdinalIgnoreCase) ||
                         uri.AbsoluteUri.Contains("manifest/video", StringComparison.OrdinalIgnoreCase) ||
                         (PublicSuffix.HostMatchesDomain(uri.Host, "dmcdn.net") && uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))))
                    {
                        tab.DailymotionRealVideoSignaled = true;
                        var win = tab.OwnerWindow ?? this;
                        _ = win.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                        {
                            if (!tab.IsDisposed && tab.WebView?.CoreWebView2 != null)
                            {
                                try { tab.WebView.CoreWebView2.PostWebMessageAsJson("{\"action\":\"dm_real_video_detected\"}"); } catch { }
                            }
                        });
                    }

                    if (ShieldLogger.DiagnosticMode)
                    {
                        string tabDesc = $"Tab:{tab.TabId} ({(tab.CurrentUri != null ? tab.CurrentUri.Host : "blank")})";
                        string reqHost = uri.Host;
                        string? currentHost = tab.CurrentUri?.Host;
                        bool isSubframeReq = frameDesc.StartsWith("Subframe", StringComparison.OrdinalIgnoreCase);

                        if (isSubframeReq && (e.ResourceContext == CoreWebView2WebResourceContext.XmlHttpRequest ||
                                              e.ResourceContext == CoreWebView2WebResourceContext.Fetch ||
                                              e.ResourceContext == CoreWebView2WebResourceContext.Document))
                        {
                            ShieldLogger.LogForTab(tab, "REQUEST_ALLOWED", originHost, uri.AbsoluteUri, frameDesc, "Subframe media/network request", tabDesc);
                        }
                        else if (!string.IsNullOrEmpty(currentHost) &&
                                 !PublicSuffix.HostMatchesDomain(reqHost, currentHost) &&
                                 !AdBlockFilter.IsTrustedDomain(reqHost))
                        {
                            ShieldLogger.LogForTab(tab, "REQUEST_ALLOWED", originHost, uri.AbsoluteUri, frameDesc, "External third-party resource", tabDesc);
                        }
                    }
                }
            }
        };

        // 3.1 Manejo granular de permisos (Notificaciones web nativas con control por sitio)
        core.PermissionRequested += (s, e) =>
        {
            HandleCorePermissionRequested(tab, core, e);
        };

        // 3.1b Despachador central de notificaciones W3C (Notification API)
        core.NotificationReceived += (s, e) =>
        {
            NotificationManager.HandleNotificationReceived(tab, e);
        };

        // 3.1c Menú contextual bajo demanda para "Leer texto seleccionado" (Windows SAPI local)
        core.ContextMenuRequested += (s, e) =>
        {
            if (tab.IsDisposed || !SettingsManager.Current.EnableReadAloud) return;

            bool hasSelection = e.ContextMenuTarget != null &&
                                e.ContextMenuTarget.HasSelection &&
                                !string.IsNullOrWhiteSpace(e.ContextMenuTarget.SelectionText);
            bool isSpeaking = ReadAloudService.IsSpeaking;
            if (!hasSelection && !isSpeaking) return;

            try
            {
                int insertIndex = e.MenuItems.Count;
                for (int i = 0; i < e.MenuItems.Count; i++)
                {
                    if (string.Equals(e.MenuItems[i].Name, "copy", StringComparison.OrdinalIgnoreCase))
                    {
                        insertIndex = i + 1;
                        break;
                    }
                }

                if (hasSelection)
                {
                    string selectedText = e.ContextMenuTarget!.SelectionText ?? string.Empty;
                    string ownerId = tab.TabId.ToString();
                    var readItem = core.Environment.CreateContextMenuItem(
                        "Leer texto seleccionado",
                        null,
                        CoreWebView2ContextMenuItemKind.Command);
                    readItem.CustomItemSelected += (_, _) =>
                    {
                        var win = tab.OwnerWindow ?? this;
                        _ = win.Dispatcher.InvokeAsync(async () =>
                        {
                            try
                            {
                                string lang = await TranslationService.DetectPageLanguageAsync(core);
                                ReadAloudService.Speak(selectedText, lang, ownerId);
                            }
                            catch (Exception ex)
                            {
                                ShieldLogger.Log("READ_ALOUD_TRIGGER_ERROR", "local", "", "ContextMenu", ex.Message);
                            }
                        });
                    };
                    e.MenuItems.Insert(insertIndex++, readItem);
                }

                if (isSpeaking)
                {
                    var stopItem = core.Environment.CreateContextMenuItem(
                        "Detener lectura",
                        null,
                        CoreWebView2ContextMenuItemKind.Command);
                    stopItem.CustomItemSelected += (_, _) => ReadAloudService.Stop();
                    e.MenuItems.Insert(insertIndex, stopItem);
                }
            }
            catch (Exception ex)
            {
                ShieldLogger.Log("READ_ALOUD_MENU_ERROR", "local", "", "ContextMenu", ex.Message);
            }
        };

        // 3.2 Recuperación automática acotada si el proceso de renderizado de la pestaña falla inesperadamente
        core.ProcessFailed += (s, e) =>
        {
            if (tab.IsDisposed) return;
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited)
            {
                var win = tab.OwnerWindow ?? this;
                _ = win.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    if (tab.IsDisposed) return;

                    var now = DateTime.UtcNow;
                    win.ResumeTab(tab);
                    tab.NextSuspendAttemptUtc = default;

                    if ((now - tab.LastCrashRecoveryUtc).TotalSeconds > 30)
                    {
                        tab.CrashRecoveryAttempts = 0;
                    }

                    if (tab.CrashRecoveryAttempts < 2)
                    {
                        tab.CrashRecoveryAttempts++;
                        tab.LastCrashRecoveryUtc = now;
                        try { core.Reload(); } catch { }
                        win.OfferCompatibilityModeToast(tab, "El proceso de la pestaña se reinició inesperadamente.");
                    }
                    else
                    {
                        win.OfferCompatibilityModeToast(tab, "La pestaña falló varias veces seguidas. Se detuvo la recarga automática.");
                    }
                    win.RescheduleSuspensionTimer();
                });
            }
        };

        // 3.3 Sincronización instantánea de iframes
        core.FrameCreated += (s, e) =>
        {
            if (tab.IsDisposed) return;
            var frame = e.Frame;
            tab.ActiveFrames.TryAdd(frame, 0);
            frame.Destroyed += (fs, fe) => tab.ActiveFrames.TryRemove(frame, out _);

            try
            {
                frame.NavigationStarting += (fs, fe) =>
                {
                    if (!ShieldLogger.DiagnosticMode) return;
                    string fTabDesc = $"Tab:{tab.TabId} ({(tab.CurrentUri != null ? tab.CurrentUri.Host : "blank")})";
                    ShieldLogger.LogForTab(tab, "SUBFRAME_NAVIGATION_STARTING", tab.CurrentUri?.Host ?? "unknown", fe.Uri, "Subframe", $"IsUserInitiated={fe.IsUserInitiated}, IsRedirected={fe.IsRedirected}", fTabDesc);
                };
            }
            catch { }

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

        // Al iniciar una navegación en el frame principal, descartar subframes obsoletos y solicitudes pendientes
        core.NavigationStarting += (s, e) =>
        {
            tab.ActiveFrames.Clear();
            tab.DailymotionRealVideoSignaled = false;
            tab.CorsUnlocker?.Dispose();
            tab.CorsUnlocker = null;

            if (!string.IsNullOrEmpty(e.Uri) &&
                Uri.TryCreate(e.Uri, UriKind.Absolute, out var nextNavUri) &&
                !string.Equals(nextNavUri.Host, tab.CurrentUri?.Host, StringComparison.OrdinalIgnoreCase))
            {
                tab.HasCaptureDevicePermission = false;
            }

            if (tab.PendingNotificationRequest != null)
            {
                tab.PendingNotificationRequest.Resolve(CoreWebView2PermissionState.Default, savesInProfile: false);
                tab.PendingNotificationRequest = null;
                var win = tab.OwnerWindow ?? this;
                _ = win.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    if (win._activeTab == tab)
                    {
                        win.PillNotificationPermission.Visibility = Visibility.Collapsed;
                        win.PopupNotificationPermission.IsOpen = false;
                    }
                });
            }
        };

        // Detección nativa de reproducción de audio para suspender o priorizar memoria
        core.IsDocumentPlayingAudioChanged += (s, e) =>
        {
            var win = tab.OwnerWindow ?? this;
            _ = win.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
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

                if (!isPlayingAudio)
                {
                    if (!tab.IsSelected && tab.LastDeactivatedUtc != default)
                    {
                        tab.LastDeactivatedUtc = DateTime.UtcNow;
                    }
                    win.RescheduleSuspensionTimer();
                }
            });
        };

        // 4. Escuchar mensajes del script inyectado
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
                    string reason = root.TryGetProperty("reason", out var rProp) ? (rProp.GetString() ?? "") : "ad_script";
                    string adUrl = root.TryGetProperty("url", out var uProp) ? (uProp.GetString() ?? "") : "";
                    string catStr = root.TryGetProperty("category", out var cProp) ? (cProp.GetString() ?? "") : "";
                    string eventType = reason.Contains("overlay") || reason.Contains("sibling") || reason.Contains("anchor")
                        ? "OVERLAY_REMOVED"
                        : (reason.Contains("window_open") ? "POPUP_DECOY_INJECTED" : "SCRIPT_DEFUSED");

                    ShieldBlockCategory category = catStr switch
                    {
                        "popup" => ShieldBlockCategory.Popup,
                        "overlay" => ShieldBlockCategory.Overlay,
                        "cosmetic" => ShieldBlockCategory.Cosmetic,
                        "adapter" => ShieldBlockCategory.Adapter,
                        _ => eventType == "OVERLAY_REMOVED"
                            ? ShieldBlockCategory.Overlay
                            : (eventType == "POPUP_DECOY_INJECTED" ? ShieldBlockCategory.Popup : ShieldBlockCategory.Adapter)
                    };

                    string tabDesc = $"Tab:{tab.TabId} ({(tab.CurrentUri != null ? tab.CurrentUri.Host : "blank")})";
                    ShieldLogger.LogForTab(tab, eventType, tab.CurrentUri?.Host ?? "unknown", string.IsNullOrEmpty(adUrl) ? (tab.CurrentUri?.AbsoluteUri ?? "") : adUrl, "DOM", reason, tabDesc);

                    _ = win.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                    {
                        if (tab.IsDisposed) return;
                        tab.RecordShieldBlock(category);
                        if (win._activeTab == tab)
                            win.UpdateShieldBadgeText();
                    });
                }
                else if (type == "shortcut_new_tab")
                {
                    bool incog = root.TryGetProperty("incognito", out var incProp) && incProp.GetBoolean();
                    _ = win.Dispatcher.BeginInvoke(async () =>
                    {
                        if (win.IsBrowserLockOverlayActive()) return;
                        int nextIdx = win._activeTab != null ? win.Tabs.IndexOf(win._activeTab) + 1 : win.Tabs.Count;
                        await win.AddNewTabAsync(null, isIncognito: incog, insertIndex: nextIdx);
                    });
                }
                else if (type == "shortcut_reopen_tab")
                {
                    _ = win.Dispatcher.BeginInvoke(() =>
                    {
                        if (!win.IsBrowserLockOverlayActive()) win.ReopenLastClosedTab();
                    });
                }
                else if (type == "shortcut_close_tab")
                {
                    _ = win.Dispatcher.BeginInvoke(() =>
                    {
                        var active = win._activeTab;
                        if (!win.IsBrowserLockOverlayActive() && active != null) win.CloseTab(active);
                    });
                }
                else if (type == "pip_entered")
                {
                    int vw = root.TryGetProperty("videoWidth", out var vwProp) ? vwProp.GetInt32() : 16;
                    int vh = root.TryGetProperty("videoHeight", out var vhProp) ? vhProp.GetInt32() : 9;
                    string pipUrl = root.TryGetProperty("url", out var uProp) ? (uProp.GetString() ?? "") : "";
                    bool isVert = (vh > vw) || pipUrl.Contains("/shorts/", StringComparison.OrdinalIgnoreCase);

                    _ = win.Dispatcher.BeginInvoke(() => win.StartNativePipMonitoring(isVert, tab));
                }
                else if (type == "pip_leaving_now")
                {
                    NativeMethods.GetCursorPos(out win._pipExitCursorPos);
                    win._pipSessionId++;
                    win._canRecordUserPipResize = false;

                    if (win._lastNativePipVisibleRect.Width == 0)
                    {
                        IntPtr h = win._nativePipHwnd != IntPtr.Zero ? win._nativePipHwnd : win.FindNativePipWindow();
                        if (h != IntPtr.Zero) win.UpdateCachedNativePipBounds(h);
                    }

                    _ = win.Dispatcher.BeginInvoke(() => { win._nativePipTimer?.Stop(); win._nativePipSourceTab = null; });
                }
                else if (type == "pip_left")
                {
                    bool isPaused = root.TryGetProperty("isPaused", out var pProp) && pProp.GetBoolean();
                    double scale = win._lastNativePipDpi > 0 ? win._lastNativePipDpi / 96.0 : 1.25;
                    if (scale <= 0) scale = 1.25;

                    double rightDip = (win._lastNativePipVisibleRect.right - win._pipExitCursorPos.x) / scale;
                    double topDip = (win._pipExitCursorPos.y - win._lastNativePipVisibleRect.top) / scale;

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

                    _ = win.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
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
                    if (tab.VolumeBoostPercent <= 100) return;
                    string origin = root.TryGetProperty("origin", out var oP) ? (oP.GetString() ?? "") : "";
                    _ = win.Dispatcher.BeginInvoke(async () =>
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
                        _ = win.Dispatcher.BeginInvoke(() =>
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
                    _ = win.Dispatcher.BeginInvoke(() => win.OpenHistory());
                }
                else if (type == "autofill_requested")
                {
                    // Auditoría E5: Autocompletado bajo interacción (foco en formulario) usando ExecuteScriptAsync aislado
                    if (tab.IsIncognito || win._isGuestSession || !SettingsManager.Current.EnablePasswordManager)
                        return;

                    string reqUrl = root.TryGetProperty("url", out var uProp) ? (uProp.GetString() ?? "") : "";
                    _ = win.Dispatcher.BeginInvoke(async () =>
                    {
                        string targetUrl = !string.IsNullOrWhiteSpace(reqUrl) ? reqUrl : tab.Url;
                        await win.HandleAutofillRequestAsync(tab, targetUrl);
                    });
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
                        _ = win.Dispatcher.BeginInvoke(() => win.PromptSaveOrUpdatePassword(submittedUrl, submittedUser, submittedPwd));
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
                            _ = win.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
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

        // 6. Historial de Descargas Ligero (0% CPU en reposo)
        core.DownloadStarting += (s, e) =>
        {
            try
            {
                var op = e.DownloadOperation;
                string resultPath = e.ResultFilePath ?? string.Empty;
                string fileName = Path.GetFileName(resultPath);
                long total = (long)(op.TotalBytesToReceive ?? 0UL);
                var entry = DownloadManager.RegisterDownload(fileName, resultPath, op.Uri, total, "En progreso", tab.IsIncognito || tab.IsGuest);

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

        // Escuchar cambios de zoom en el WebView para actualizar el indicador en Omnibox
        tab.WebView.ZoomFactorChanged += (s, e) =>
        {
            var win = tab.OwnerWindow ?? this;
            win.Dispatcher.Invoke(() =>
            {
                if (win._activeTab == tab)
                {
                    win.UpdateZoomIndicatorUI(showFloating: false);
                }
            });
        };

        core.NavigationStarting += (s, e) =>
        {
            if (tab.IsDisposed) return;
            ReadAloudService.StopIfOwner(tab.TabId.ToString());
            string FormatTabDesc() => $"Tab:{tab.TabId} ({(tab.CurrentUri != null ? tab.CurrentUri.Host : "blank")})";

            if (ShieldLogger.DiagnosticMode)
            {
                ShieldLogger.LogForTab(tab, "NAVIGATION_STARTING", tab.CurrentUri?.Host ?? "initial", e.Uri, "MainFrame", $"IsUserInitiated={e.IsUserInitiated}, IsRedirected={e.IsRedirected}, NavigationKind={e.NavigationKind}", FormatTabDesc());
            }

            // Redirección hacia Lex PDF Studio si el destino es un documento PDF
            if (PdfStudioService.IsPdfTarget(e.Uri))
            {
                e.Cancel = true;
                string viewerUrl = PdfStudioService.GetViewerUrl(e.Uri);
                var winRef = tab.OwnerWindow ?? this;
                _ = winRef.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!tab.IsDisposed)
                    {
                        tab.WebView?.CoreWebView2?.Navigate(viewerUrl);
                    }
                }));
                return;
            }

            if (TvStudioService.IsTvUrl(e.Uri))
            {
                return;
            }

            if (TvStudioService.IsCineShortcut(e.Uri))
            {
                e.Cancel = true;
                var winRef = tab.OwnerWindow ?? this;
                _ = winRef.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!tab.IsDisposed)
                    {
                        tab.WebView?.CoreWebView2?.Navigate(TvStudioService.CineUrl);
                    }
                }));
                return;
            }

            if (TvStudioService.IsTvShortcut(e.Uri))
            {
                e.Cancel = true;
                var winRef = tab.OwnerWindow ?? this;
                _ = winRef.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!tab.IsDisposed)
                    {
                        tab.WebView?.CoreWebView2?.Navigate(TvStudioService.HomeUrl);
                    }
                }));
                return;
            }

            // Filtro de seguridad y anti-secuestro de pestaña (Tab-under / Reverse Popunder)
            if (SettingsManager.Current.EnableAdBlocker && Uri.TryCreate(e.Uri, UriKind.Absolute, out var targetUri))
            {
                if (targetUri.Scheme == "http" || targetUri.Scheme == "https")
                {
                    var currentUri = tab.CurrentUri;
                    var originPolicy = SitePolicy.Resolve(currentUri ?? targetUri, tab.TemporaryCompatMode);
                    var targetPolicy = SitePolicy.Resolve(targetUri, tab.TemporaryCompatMode);

                    if (originPolicy.ShieldEnabled && originPolicy.Network &&
                        AdBlockFilter.ShouldBlock(targetUri, currentUri?.Host, CoreWebView2WebResourceContext.Document))
                    {
                        e.Cancel = true;
                        ShieldLogger.LogForTab(tab, "NAVIGATION_CANCELLED", currentUri?.Host ?? "unknown", targetUri.AbsoluteUri, "MainFrame", "Blocked ad/malware domain on navigation", FormatTabDesc());
                        return;
                    }

                    bool isCrossDomain = currentUri != null && !AdBlockFilter.IsSameSiteOrDomain(targetUri, currentUri);
                    bool isTrusted = AdBlockFilter.IsTrustedDomain(targetUri.Host);

                    if (originPolicy.ShieldEnabled && originPolicy.Popups && !e.IsUserInitiated && isCrossDomain && !isTrusted)
                    {
                        if (tab.LastNewWindowRequestedTime.HasValue &&
                            (DateTime.UtcNow - tab.LastNewWindowRequestedTime.Value).TotalMilliseconds < 3500)
                        {
                            e.Cancel = true;
                            ShieldLogger.LogForTab(tab, "TAB_UNDER_PREVENTED", currentUri?.Host ?? "unknown", targetUri.AbsoluteUri, "MainFrame", "Script navigation hijacked original tab after new window open", FormatTabDesc());
                            return;
                        }

                        if (AdBlockFilter.IsSuspiciousPopup(targetUri, currentUri))
                        {
                            e.Cancel = true;
                            ShieldLogger.LogForTab(tab, "NAVIGATION_CANCELLED", currentUri?.Host ?? "unknown", targetUri.AbsoluteUri, "MainFrame", "Suspicious script-initiated cross-origin redirect", FormatTabDesc());
                            return;
                        }
                    }

                    // Limpieza de parámetros de rastreo en URLs (utm_*, fbclid, gclid, etc.) en navegaciones directas
                    if (targetPolicy.ShieldEnabled && targetPolicy.Tracking && e.IsUserInitiated && !e.IsRedirected &&
                        SitePolicy.TryStripTrackingParameters(targetUri, out string cleanedUrl))
                    {
                        e.Cancel = true;
                        ShieldLogger.LogForTab(tab, "TRACKING_PARAMS_STRIPPED", targetUri.Host, cleanedUrl, "MainFrame", "Removed tracking query parameters", FormatTabDesc());
                        var winClean = tab.OwnerWindow ?? this;
                        _ = winClean.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (!tab.IsDisposed)
                            {
                                tab.WebView?.CoreWebView2?.Navigate(cleanedUrl);
                            }
                        }));
                        return;
                    }
                }
            }

            var win = tab.OwnerWindow ?? this;

            win.Dispatcher.Invoke(() =>
            {
                if (tab.IsDisposed) return;
                tab.IsLoading = true;
                if (win._activeTab == tab)
                {
                    win.LoadingProgressBar.Visibility = Visibility.Visible;
                }
                if (win._nativePipSourceTab == tab)
                {
                    win._nativePipTimer?.Stop();
                    win._nativePipLostTicks = 0;
                    win._nativePipSourceTab = null;
                }

                if (tab.SnifferSession != null && tab.SnifferSession.ConsumeExpectedReload())
                {
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
                if (win._activeTab != tab)
                {
                    if (tab.LastDeactivatedUtc == default)
                    {
                        tab.LastDeactivatedUtc = DateTime.UtcNow;
                    }
                    win.RescheduleSuspensionTimer();
                }
                tab.CanGoBack = core.CanGoBack;
                tab.CanGoForward = core.CanGoForward;
                if (win._activeTab == tab)
                {
                    win.LoadingProgressBar.Visibility = Visibility.Collapsed;
                    win.UpdateNavigationButtons();
                    win.UpdateCurrentTabBookmarkUI();
                }

                if (tab.VolumeBoostPercent != 100)
                {
                    win.SendVolumeBoostToTab(tab);
                }

                if (tab.IsMediaSnifferActive)
                {
                    if (tab.SnifferSession != null)
                    {
                        _ = tab.SnifferSession.RescanAsync();
                    }
                    win.RequestDomMediaScan(tab);
                }

                if (e.IsSuccess && !tab.IsIncognito && !tab.IsGuest && !tab.IsNewTab &&
                    !string.IsNullOrWhiteSpace(core.Source) && !TvStudioService.IsTvUrl(core.Source))
                {
                    HistoryManager.AddEntry(core.Source, core.DocumentTitle);
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
                    if (PdfStudioService.TryUnwrapInternalViewerUrl(core.Source, out string? realPdfUrl) && !string.IsNullOrWhiteSpace(realPdfUrl))
                    {
                        tab.Url = realPdfUrl;
                    }
                    else
                    {
                        tab.Url = core.Source;
                    }
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
                    bool isUserTypingInNewTab = tab.IsNewTab && win.TxtUrl.IsKeyboardFocusWithin && !string.IsNullOrEmpty(win.TxtUrl.Text);
                    if (!isUserTypingInNewTab)
                    {
                        win._isUpdatingOmniboxTextProgrammatically = true;
                        win.TxtUrl.Text = tab.IsNewTab ? "" : tab.Url;
                        win._isUpdatingOmniboxTextProgrammatically = false;
                    }
                    win.PopupOmniboxSuggestions.IsOpen = false;
                    win.UpdateSecurityIndicatorUI();
                    win.UpdateCurrentTabBookmarkUI();
                    win.UpdateMediaSnifferUI();
                    win.UpdateNavigationButtons();
                }
            });
        };

        core.HistoryChanged += (s, e) =>
        {
            var win = tab.OwnerWindow ?? this;
            _ = win.Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                if (tab.IsDisposed) return;
                tab.CanGoBack = core.CanGoBack;
                tab.CanGoForward = core.CanGoForward;
                if (win._activeTab == tab)
                {
                    win.UpdateNavigationButtons();
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
                    tab.Title = tab.IsGuest ? "Modo Invitado" : (tab.IsIncognito ? "Incógnito" : "Nueva pestaña");
                }
                else
                {
                    tab.Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "Nueva pestaña" : core.DocumentTitle;
                    if (!tab.IsIncognito && !tab.IsGuest &&
                        !string.IsNullOrWhiteSpace(core.Source) && !TvStudioService.IsTvUrl(core.Source))
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

            var icon = await FaviconService.ResolveFaviconAsync(tab.Url, stream, faviconUri, tab.IsIncognito || tab.IsGuest);
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
        catch { }
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
            if (previousTab != null && previousTab != tab && !previousTab.IsDisposed)
            {
                previousTab.LastDeactivatedUtc = DateTime.UtcNow;
            }

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

            try
            {
                if (tab.WebView?.CoreWebView2 != null)
                {
                    _ = TriggerExitPiPAsync(tab);
                }
            }
            catch { }

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
                    bool isFloating = _pipWindow?.IsVisible == true && _pipWindow.TargetTab == t;
                    t.WebView.Visibility = (isCurrent || isFloating) ? Visibility.Visible : Visibility.Collapsed;

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
            ApplyFullScreenChrome(tab.WebView?.CoreWebView2?.ContainsFullScreenElement == true);

            if (tab?.WebView != null)
            {
                if (!BrowserContainer.Children.Contains(tab.WebView))
                {
                    if (tab.WebView.Parent is Panel existingParent)
                    {
                        existingParent.Children.Remove(tab.WebView);
                    }
                    BrowserContainer.Children.Add(tab.WebView);
                }

                tab.WebView.Visibility = Visibility.Visible;
                ResumeTab(tab);
                try
                {
                    GetController(tab.WebView)?.NotifyParentWindowPositionChanged();
                }
                catch { }
            }

            if (tab != null && tab.IsLazyLoaded && !string.IsNullOrWhiteSpace(tab.Url))
            {
                tab.IsLazyLoaded = false;
                if (tab.WebView?.CoreWebView2 != null)
                {
                    // Auditoría L7: Redirigir PDFs restaurados perezosamente por PdfStudioService
                    string navTarget = PdfStudioService.IsPdfTarget(tab.Url)
                        ? PdfStudioService.GetViewerUrl(tab.Url)
                        : tab.Url;
                    tab.WebView.CoreWebView2.Navigate(navTarget);
                }
            }

            if (tab != null)
            {
                bool isUserTypingInCurrentNewTab = tab.IsNewTab && _activeTab == tab && TxtUrl.IsKeyboardFocusWithin && !string.IsNullOrEmpty(TxtUrl.Text);
                if (!isUserTypingInCurrentNewTab)
                {
                    _isUpdatingOmniboxTextProgrammatically = true;
                    TxtUrl.Text = tab.IsNewTab ? "" : tab.Url;
                    _isUpdatingOmniboxTextProgrammatically = false;
                }
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
            UpdateNotificationPermissionUI();
            _ = UpdateTranslateIndicatorUIAsync(tab);
            FocusOmniboxIfNewTab(tab);
            RescheduleSuspensionTimer();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SelectTab] Error: {ex.Message}");
        }
    }

    private void FocusOmniboxIfNewTab(BrowserTab? tab)
    {
        if (tab == null || tab.IsDisposed || !tab.IsNewTab || _activeTab != tab) return;
        if (TxtUrl.IsKeyboardFocusWithin && !string.IsNullOrEmpty(TxtUrl.Text)) return;

        TxtUrl.Focus();
        Keyboard.Focus(TxtUrl);
        TxtUrl.CaretIndex = TxtUrl.Text.Length;
    }

    private static string GetDefaultTranslationTargetLanguage()
    {
        string iso = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return string.IsNullOrWhiteSpace(iso) ? "es" : iso.ToLowerInvariant();
    }

    private void InitializeTranslateLanguageList()
    {
        var languages = new List<TranslateLanguageItem>
        {
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
        string defaultLang = GetDefaultTranslationTargetLanguage();
        CmbTargetLanguage.SelectedValue = languages.Any(l => l.Code.Equals(defaultLang, StringComparison.OrdinalIgnoreCase))
            ? defaultLang
            : "es";
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
                PillTranslateIndicator.Background = s_translateActiveBgBrush;
                PillTranslateIndicator.ToolTip = "Página traducida (clic para cambiar idioma o restaurar)";
                return;
            }

            string lang = await TranslationService.DetectPageLanguageAsync(tab.WebView.CoreWebView2);
            string preferredLang = GetDefaultTranslationTargetLanguage();
            bool isForeign = !string.IsNullOrEmpty(lang) && !lang.StartsWith(preferredLang, StringComparison.OrdinalIgnoreCase);
            PillTranslateIndicator.Visibility = isForeign ? Visibility.Visible : Visibility.Collapsed;
            TxtTranslateAction.Text = "Traducir";
            PillTranslateIndicator.Background = s_translateDefaultBgBrush;
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

        string targetLang = CmbTargetLanguage.SelectedValue as string ?? GetDefaultTranslationTargetLanguage();
        bool translated = await TranslationService.TranslatePageAsync(_activeTab.WebView.CoreWebView2, targetLang);
        if (translated)
        {
            TxtTranslateAction.Text = "Original";
            PillTranslateIndicator.Background = s_translateActiveBgBrush;
            ShowToast($"🌐 Traduciendo página a {targetLang.ToUpperInvariant()}... Clic para opciones.");
        }
    }

    private async void BtnRestoreTranslate_Click(object sender, RoutedEventArgs e)
    {
        PopupTranslate.IsOpen = false;
        if (_activeTab?.WebView?.CoreWebView2 == null || _activeTab.IsDisposed) return;

        await TranslationService.RestoreOriginalAsync(_activeTab.WebView.CoreWebView2);
        TxtTranslateAction.Text = "Traducir";
        PillTranslateIndicator.Background = s_translateDefaultBgBrush;
        ShowToast("Página restaurada al idioma original.");
    }

    // ─────────────────────────────────────────────────────────────────────
    // ARRASTRAR Y DESPLAZAR PESTAÑAS VISUALMENTE (LIVE SMOOTH DRAG & REORDER)
    // ─────────────────────────────────────────────────────────────────────
    private void Tab_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsBrowserLockOverlayActive()) return;
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
                    _draggedBorder.BorderBrush = s_dragTabBorderBrush;
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
                    _draggedBorder.Opacity = 0.65;
                    _draggedBorder.RenderTransform = new TranslateTransform(deltaX, deltaY);
                    return;
                }

                _draggedBorder.Opacity = 0.9;
                _draggedBorder.RenderTransform = new TranslateTransform(deltaX, 0);

                int currentIndex = Tabs.IndexOf(_draggedTab);
                if (currentIndex < 0) return;

                double currentWidth = _draggedBorder.ActualWidth > 0 ? _draggedBorder.ActualWidth : 120;

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
            if (w is MainWindow other && other != this &&
                other._isGuestSession == this._isGuestSession &&
                !other.IsBrowserLockOverlayActive() &&
                other.IsVisible && other.WindowState != WindowState.Minimized)
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
        if (targetWindow._isGuestSession != this._isGuestSession) return;

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

        ResumeTab(tab);
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
        RescheduleSuspensionTimer();

        var newWin = new MainWindow(null, tab, tab.IsIncognito, _isGuestSession)
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
        UpdateTabWidths();
    }

    private void MenuPinLogoAndName_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetTabFromMenuItem(sender);
        if (tab == null) return;

        tab.PinMode = TabPinMode.LogoAndName;
        MoveToPinnedSection(tab);
        UpdateTabWidths();
    }

    private void MenuUnpin_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetTabFromMenuItem(sender);
        if (tab == null) return;

        tab.PinMode = TabPinMode.None;

        int oldIndex = Tabs.IndexOf(tab);
        int targetIndex = Tabs.Count(t => t.IsPinned);
        if (oldIndex != targetIndex && oldIndex >= 0 && targetIndex < Tabs.Count)
        {
            Tabs.Move(oldIndex, targetIndex);
        }
        UpdateTabWidths();
        RescheduleSuspensionTimer();
    }

    private void MoveToPinnedSection(BrowserTab tab)
    {
        int oldIndex = Tabs.IndexOf(tab);
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
            BorderCurrentTabBookmark.BorderBrush = s_bookmarkDefaultBorderBrush;
            BorderCurrentTabBookmark.Background = s_bookmarkDefaultBgBrush;
            TxtCurrentTabBookmarkIcon.Text = "＋";
            TxtCurrentTabBookmarkIcon.Foreground = s_bookmarkMutedIconBrush;
            TxtCurrentTabBookmarkTitle.Text = "Anclar página";
            return;
        }

        bool isBookmarked = BookmarkManager.IsBookmarked(_activeTab.Url);

        if (isBookmarked)
        {
            BorderCurrentTabBookmark.BorderBrush = (SolidColorBrush)FindResource("AccentColor");
            BorderCurrentTabBookmark.Background = s_bookmarkActiveBgBrush;
            TxtCurrentTabBookmarkIcon.Text = "★";
            TxtCurrentTabBookmarkIcon.Foreground = (SolidColorBrush)FindResource("AccentHover");
            TxtCurrentTabBookmarkTitle.Text = string.IsNullOrWhiteSpace(_activeTab.Title) ? "Anclado" : _activeTab.Title;
        }
        else
        {
            BorderCurrentTabBookmark.BorderBrush = s_bookmarkDefaultBorderBrush;
            BorderCurrentTabBookmark.Background = s_bookmarkDefaultBgBrush;
            TxtCurrentTabBookmarkIcon.Text = "＋";
            TxtCurrentTabBookmarkIcon.Foreground = s_bookmarkNormalIconBrush;
            TxtCurrentTabBookmarkTitle.Text = string.IsNullOrWhiteSpace(_activeTab.Title) ? "Anclar página" : $"Anclar {_activeTab.Title}";
        }
    }

    private void CurrentTabBookmark_Click(object sender, MouseButtonEventArgs e)
    {
        if (_isGuestSession || _activeTab == null || string.IsNullOrWhiteSpace(_activeTab.Url) || _activeTab.IsNewTab)
            return;

        bool nowBookmarked = BookmarkManager.ToggleBookmark(_activeTab.Title, _activeTab.Url);
        UpdateCurrentTabBookmarkUI();

        if (!nowBookmarked)
        {
            ShowUndoBanner();
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
        if (_isGuestSession) return;
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

    public void FocusAndSelectTab(BrowserTab tab)
    {
        if (tab == null || tab.IsDisposed) return;
        SelectTab(tab, force: true);
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
        Focus();
    }

    // ─────────────────────────────────────────────────────────────────────
    // BOTONES Y PESTAÑAS (Auditoría E2: bloquear si BrowserLockOverlay está activo)
    // ─────────────────────────────────────────────────────────────────────
    private async void BtnNewTab_Click(object sender, RoutedEventArgs e)
    {
        if (IsBrowserLockOverlayActive()) return;
        await AddNewTabAsync(null, isIncognito: false);
    }

    private async void BtnNewIncognitoTab_Click(object sender, RoutedEventArgs e)
    {
        if (IsBrowserLockOverlayActive()) return;
        if (_isGuestSession)
        {
            if (!TryUnlockFromGuestMode("Ingresa tu PIN de propietario para abrir una pestaña de incógnito"))
                return;
        }
        await AddNewTabAsync(null, isIncognito: true);
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (IsBrowserLockOverlayActive()) return;
        if (sender is Button btn && btn.Tag is BrowserTab tabToClose)
        {
            CloseTab(tabToClose);
        }
    }

    private void CloseTab(BrowserTab tabToClose)
    {
        if (tabToClose.IsDisposed) return;
        tabToClose.IsDisposed = true;
        ReadAloudService.StopIfOwner(tabToClose.TabId.ToString());

        if (_nativePipSourceTab == tabToClose)
        {
            _nativePipTimer?.Stop();
            _nativePipLostTicks = 0;
            _nativePipSourceTab = null;
        }

        StopSnifferSessionForTab(tabToClose);
        _preFullScreenZoomByTab.Remove(tabToClose);
        tabToClose.CorsUnlocker?.Dispose();
        tabToClose.CorsUnlocker = null;
        tabToClose.ClearDetectedMedia();
        tabToClose.ActiveFrames.Clear();
        tabToClose.RecentReloadTimesUtc.Clear();
        tabToClose.CompatibilityPromptShownDomains.Clear();
        tabToClose.FaviconImage = null;
        tabToClose.OwnerWindow = null;

        if (tabToClose.PendingNotificationRequest != null)
        {
            tabToClose.PendingNotificationRequest.Resolve(CoreWebView2PermissionState.Default, savesInProfile: false);
            tabToClose.PendingNotificationRequest = null;
        }

        // Privacidad de Incógnito e Invitado: jamás guardar en el historial de Ctrl+Shift+T, y limitar a 25 (Auditoría F7)
        if (!tabToClose.IsIncognito && !tabToClose.IsGuest && !string.IsNullOrWhiteSpace(tabToClose.Url) && !tabToClose.IsNewTab)
        {
            if (_closedTabsHistory.Count >= 25)
            {
                var items = _closedTabsHistory.Take(24).Reverse().ToArray();
                _closedTabsHistory.Clear();
                foreach (var it in items)
                {
                    _closedTabsHistory.Push(it);
                }
            }
            _closedTabsHistory.Push((tabToClose.Url, tabToClose.Title, false));
        }

        int index = Tabs.IndexOf(tabToClose);
        bool wasActive = (_activeTab == tabToClose);

        Tabs.Remove(tabToClose);
        RescheduleSuspensionTimer();

        if (TvStudioService.IsTvUrl(tabToClose.Url))
        {
            bool anyTvOpen = Application.Current?.Windows
                .OfType<MainWindow>()
                .Any(w => w.Tabs.Any(t => !t.IsDisposed && TvStudioService.IsTvUrl(t.Url))) == true;
            if (!anyTvOpen)
                TvStudioService.ReleaseAll();
        }

        if (_pipWindow.TargetTab == tabToClose)
        {
            _pipWindow.HideAndDetach();
        }

        if (Tabs.Count == 0)
        {
            _activeTab = null;
            Close();
            return;
        }
        else if (wasActive)
        {
            _activeTab = null;
            int nextIndex = Math.Clamp(index, 0, Tabs.Count - 1);
            SelectTab(Tabs[nextIndex]);
        }

        if (tabToClose.IsSuspendedByLex) { ResumeTab(tabToClose); }
        try { tabToClose.WebView?.CoreWebView2?.Stop(); } catch { }

        if (BrowserContainer.Children.Contains(tabToClose.WebView))
        {
            BrowserContainer.Children.Remove(tabToClose.WebView);
        }

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            try { tabToClose.WebView?.Dispose(); } catch { }
        });
    }

    private async void ReopenLastClosedTab()
    {
        if (IsBrowserLockOverlayActive() || _isGuestSession) return;
        if (_closedTabsHistory.Count > 0)
        {
            var (url, _, isIncognito) = _closedTabsHistory.Pop();
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
        if (tab != null)
        {
            RecordUserReloadAndCheckCompat(tab);
            tab.WebView?.CoreWebView2?.Reload();
        }
    }

    public async Task RefreshTabShieldScriptsAsync(BrowserTab tab)
    {
        if (tab == null || tab.IsDisposed || tab.WebView?.CoreWebView2 == null)
            return;

        var core = tab.WebView.CoreWebView2;

        if (string.IsNullOrEmpty(tab.MediaRuntimeScriptId))
        {
            try
            {
                tab.MediaRuntimeScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(MediaRuntime.InjectedScript);
            }
            catch (Exception ex)
            {
                ShieldLogger.LogForTab(tab, "SCRIPT_INJECT_ERROR", "MediaRuntime", "", "Refresh", ex.Message);
            }
        }

        if (!string.IsNullOrEmpty(tab.YouTubeScriptId))
        {
            try { core.RemoveScriptToExecuteOnDocumentCreated(tab.YouTubeScriptId); } catch { }
            tab.YouTubeScriptId = null;
        }
        if (!string.IsNullOrEmpty(tab.DailymotionScriptId))
        {
            try { core.RemoveScriptToExecuteOnDocumentCreated(tab.DailymotionScriptId); } catch { }
            tab.DailymotionScriptId = null;
        }
        if (!string.IsNullOrEmpty(tab.WebShieldScriptId))
        {
            try { core.RemoveScriptToExecuteOnDocumentCreated(tab.WebShieldScriptId); } catch { }
            tab.WebShieldScriptId = null;
        }
        if (!string.IsNullOrEmpty(tab.CookieBannerScriptId))
        {
            try { core.RemoveScriptToExecuteOnDocumentCreated(tab.CookieBannerScriptId); } catch { }
            tab.CookieBannerScriptId = null;
        }

        var s = SettingsManager.Current;
        if (s.EnableAdBlocker && s.EnableYouTubeAdBlock && (s.Shield?.Adapters?.YouTube ?? true))
        {
            try
            {
                tab.YouTubeScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(YouTubeOptimizer.InjectedScript);
            }
            catch (Exception ex)
            {
                ShieldLogger.LogForTab(tab, "SCRIPT_INJECT_ERROR", "YouTubeOptimizer", "", "Refresh", ex.Message);
            }
        }
        if (s.EnableAdBlocker && s.EnableDailymotionAdBlock && (s.Shield?.Adapters?.Dailymotion ?? true))
        {
            try
            {
                tab.DailymotionScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(DailymotionOptimizer.InjectedScript);
            }
            catch (Exception ex)
            {
                ShieldLogger.LogForTab(tab, "SCRIPT_INJECT_ERROR", "DailymotionOptimizer", "", "Refresh", ex.Message);
            }
        }

        if (s.EnableAdBlocker)
        {
            try
            {
                tab.WebShieldScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(WebShield.InjectedScript);
            }
            catch (Exception ex)
            {
                ShieldLogger.LogForTab(tab, "SCRIPT_INJECT_ERROR", "WebShield", "", "Refresh", ex.Message);
            }
        }

        if (s.EnableCookieBannerHiding)
        {
            try
            {
                tab.CookieBannerScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(CookieBannerShield.InjectedScript);
            }
            catch (Exception ex)
            {
                ShieldLogger.LogForTab(tab, "SCRIPT_INJECT_ERROR", "CookieBannerShield", "", "Refresh", ex.Message);
            }
        }
    }

    public async Task RefreshAllTabsShieldScriptsAsync()
    {
        foreach (var tab in Tabs.ToList())
        {
            if (!tab.IsDisposed && tab.WebView?.CoreWebView2 != null)
            {
                await RefreshTabShieldScriptsAsync(tab);
            }
        }
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
}
