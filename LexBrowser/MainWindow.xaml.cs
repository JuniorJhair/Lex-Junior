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

    // Variables para arrastre visual suave de pestañas
    private Point _tabDragStartPoint;
    private BrowserTab? _draggedTab;
    private Border? _draggedBorder;
    private bool _isDraggingTab = false;

    public MainWindow()
    {
        InitializeComponent();
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
            _toastTimer.Stop();
            ToastNotification.Visibility = Visibility.Collapsed;
        };

        // Configurar temporizador para el widget flotante de zoom (4 segundos)
        _zoomWidgetTimer.Interval = TimeSpan.FromSeconds(4);
        _zoomWidgetTimer.Tick += (s, e) =>
        {
            _zoomWidgetTimer.Stop();
            FloatingZoomWidget.Visibility = Visibility.Collapsed;
        };

        // Escuchar cambios de configuración
        SettingsManager.SettingsChanged += ApplySettings;
        ApplySettings();

        // Callback cuando el usuario pulsa "⇱ Volver" en la Página Flotante manual
        _pipWindow.OnReturnToTabRequested = (tab) =>
        {
            Dispatcher.Invoke(() =>
            {
                ReinsertWebViewToContainer(tab);
                if (WindowState == WindowState.Minimized)
                {
                    WindowState = WindowState.Normal;
                }
                Activate();
                SelectTab(tab);
            });
        };

        _pipWindow.OnDetachWebView = (tab) =>
        {
            Dispatcher.Invoke(() => ReinsertWebViewToContainer(tab));
        };

        Loaded += MainWindow_Loaded;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await AddNewTabAsync(null, isIncognito: false);
    }

    private void ApplySettings()
    {
        var s = SettingsManager.Current;

        // Mostrar / Ocultar botón de Página Flotante en Navbar
        BtnTogglePip.Visibility = s.ShowFloatingPageButton ? Visibility.Visible : Visibility.Collapsed;

        // Mostrar / Ocultar Barra de Accesos Directos
        RowBookmarksBar.Height = s.ShowBookmarksBar ? new GridLength(34) : new GridLength(0);
        RowBookmarksGrid.Visibility = s.ShowBookmarksBar ? Visibility.Visible : Visibility.Collapsed;
    }

    // ─────────────────────────────────────────────────────────────────────
    // REINSERTAR EL WEBVIEW AL CONTENEDOR PRINCIPAL
    // ─────────────────────────────────────────────────────────────────────
    private void ReinsertWebViewToContainer(BrowserTab tab)
    {
        var webView = tab.WebView;
        if (webView.Parent is Panel oldParent)
        {
            oldParent.Children.Remove(webView);
        }

        if (_activeTab == tab)
        {
            BrowserContainer.Children.Clear();
            BrowserContainer.Children.Add(webView);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // CREAR NUEVA PESTAÑA
    // ─────────────────────────────────────────────────────────────────────
    private async Task<BrowserTab> AddNewTabAsync(string? initialUrl = null, bool isIncognito = false, int? insertIndex = null)
    {
        var webView = new WebView2
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        bool isNewTabDefault = string.IsNullOrWhiteSpace(initialUrl);

        var tab = new BrowserTab(webView)
        {
            Url = isNewTabDefault ? "" : initialUrl!,
            Title = isNewTabDefault ? (isIncognito ? "Incógnito" : "Nueva pestaña") : "Cargando...",
            IsIncognito = isIncognito,
            IsNewTab = isNewTabDefault
        };

        if (insertIndex.HasValue && insertIndex.Value >= 0 && insertIndex.Value <= Tabs.Count)
        {
            Tabs.Insert(insertIndex.Value, tab);
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

            if (isNewTabDefault)
            {
                webView.NavigateToString(NewTabPage.GetHtml(isIncognito));
            }
            else
            {
                webView.CoreWebView2.Navigate(initialUrl!);
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
    // CONFIGURACIÓN DE EVENTOS DEL WEBVIEW
    // ─────────────────────────────────────────────────────────────────────
    private async Task ConfigureTabWebViewAsync(BrowserTab tab)
    {
        var core = tab.WebView.CoreWebView2;
        if (core == null) return;

        // Inyectar script universal de optimización, anti-anuncios, iframes y PiP
        try
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(YouTubeOptimizer.InjectedScript);
        }
        catch { }

        // 1. MANEJO DE PANTALLA COMPLETA REAL DE LAPTOP (CERO FRANJAS VACÍAS)
        core.ContainsFullScreenElementChanged += (s, e) =>
        {
            Dispatcher.Invoke(() =>
            {
                if (core.ContainsFullScreenElement)
                {
                    _savedWindowState = WindowState;
                    _savedResizeMode = ResizeMode;

                    // Colapsar barras superiores completamente
                    RowHeader.Height = new GridLength(0);
                    RowNavBar.Height = new GridLength(0);
                    RowBookmarksBar.Height = new GridLength(0);
                    RowProgressBar.Height = new GridLength(0);

                    // Eliminar WindowChrome durante fullscreen para evitar bordes negros o franjas de taskbar
                    WindowChrome.SetWindowChrome(this, null);

                    ResizeMode = ResizeMode.NoResize;
                    WindowState = WindowState.Normal;
                    WindowState = WindowState.Maximized;
                }
                else
                {
                    // Restaurar barras del navegador
                    RowHeader.Height = new GridLength(42);
                    RowNavBar.Height = new GridLength(50);
                    RowBookmarksBar.Height = SettingsManager.Current.ShowBookmarksBar ? new GridLength(34) : new GridLength(0);
                    RowProgressBar.Height = new GridLength(2);

                    // Restaurar WindowChrome normal
                    WindowChrome.SetWindowChrome(this, AppWindowChrome);

                    ResizeMode = _savedResizeMode;
                    WindowState = _savedWindowState;
                }
            });
        };

        // 2. ESCUDO ANTI-POPUNDERS Y CLICK-JACKING
        core.NewWindowRequested += (s, e) =>
        {
            e.Handled = true;

            string url = e.Uri;
            if (string.IsNullOrWhiteSpace(url) || url == "about:blank") return;

            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                Uri? currentUri = null;
                try
                {
                    if (tab.WebView != null && tab.WebView.Source != null)
                    {
                        currentUri = tab.WebView.Source;
                    }
                }
                catch { }

                if (SettingsManager.Current.EnableAdBlocker && (AdBlockFilter.ShouldBlock(uri) || AdBlockFilter.IsSuspiciousPopup(uri, currentUri)))
                {
                    Dispatcher.Invoke(() =>
                    {
                        tab.BlockedAdsCount++;
                        if (_activeTab == tab)
                            TxtShieldBadge.Text = $"{tab.BlockedAdsCount} bloqueados";
                    });
                    return;
                }
            }

            Dispatcher.Invoke(async () =>
            {
                await AddNewTabAsync(url, isIncognito: tab.IsIncognito);
            });
        };

        // 3. Filtro de red (bloqueo de recursos publicitarios)
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (s, e) =>
        {
            if (SettingsManager.Current.EnableAdBlocker && Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) && AdBlockFilter.ShouldBlock(uri))
            {
                e.Response = core.Environment.CreateWebResourceResponse(
                    new MemoryStream(), 403, "Blocked by LexShield", "Content-Type: text/plain");

                Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    tab.BlockedAdsCount++;
                    if (_activeTab == tab)
                        TxtShieldBadge.Text = $"{tab.BlockedAdsCount} bloqueados";
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

        // 4. Escuchar mensajes del script inyectado (AdBlock, retorno PiP y estado multimedia)
        core.WebMessageReceived += (s, e) =>
        {
            if (tab.IsDisposed) return;
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)) return;
                string type = typeProp.GetString() ?? "";

                if (type == "ad_blocked")
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                    {
                        if (tab.IsDisposed) return;
                        tab.BlockedAdsCount++;
                        if (_activeTab == tab)
                            TxtShieldBadge.Text = $"{tab.BlockedAdsCount} bloqueados";
                    });
                }
                else if (type == "pip_return_requested")
                {
                    // RETORNO DE PIP NATIVO: El usuario pulsó 'Volver a la pestaña' en el mini-reproductor
                    Dispatcher.Invoke(() =>
                    {
                        if (tab.IsDisposed) return;
                        if (WindowState == WindowState.Minimized)
                        {
                            WindowState = WindowState.Normal;
                        }
                        Activate();
                        Focus();
                        SelectTab(tab);

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

                    Dispatcher.Invoke(() =>
                    {
                        if (tab.IsDisposed) return;
                        tab.HasActiveMedia = isPlaying || curTime > 0;
                        tab.MediaIsPlaying = isPlaying;
                        tab.MediaTitle = title;
                        tab.MediaCurrentTime = curTime;
                        tab.MediaDuration = duration;

                        if (_pipWindow.IsVisible && _pipWindow.TargetTab == tab)
                        {
                            _pipWindow.UpdateMediaControls(title, curTime, duration, isPlaying);
                        }
                    });
                }
                else if (type == "open_history")
                {
                    Dispatcher.Invoke(() => OpenHistory());
                }
            }
            catch { }
        };

        // Escuchar cambios de zoom en el WebView para actualizar el indicador y widget flotante
        tab.WebView.ZoomFactorChanged += (s, e) => Dispatcher.Invoke(() =>
        {
            if (_activeTab == tab)
            {
                UpdateZoomIndicatorUI(showFloating: true);
            }
        });

        // 5. Interceptar atajos de teclado globales (Ctrl + H, Ctrl + 0) en el control WebView2
        tab.WebView.KeyDown += (s, e) =>
        {
            if (e.Key == Key.H && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                OpenHistory();
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.D0 || e.Key == Key.NumPad0))
            {
                e.Handled = true;
                ResetZoom();
            }
        };

        core.NavigationStarting += (s, e) =>
        {
            if (tab.IsDisposed) return;

            Dispatcher.Invoke(() =>
            {
                if (tab.IsDisposed) return;
                tab.IsLoading = true;
                if (_activeTab == tab) LoadingProgressBar.Visibility = Visibility.Visible;
            });
        };

        core.FaviconChanged += (s, e) =>
        {
            if (tab.IsDisposed) return;
            _ = UpdateTabFaviconAsync(tab);
        };

        core.NavigationCompleted += (s, e) => Dispatcher.Invoke(() =>
        {
            if (tab.IsDisposed) return;
            tab.IsLoading = false;
            tab.CanGoBack = core.CanGoBack;
            tab.CanGoForward = core.CanGoForward;
            if (_activeTab == tab)
            {
                LoadingProgressBar.Visibility = Visibility.Collapsed;
                UpdateNavigationButtons();
                UpdateCurrentTabBookmarkUI();
            }

            if (e.IsSuccess && !tab.IsIncognito && !tab.IsNewTab && !string.IsNullOrWhiteSpace(core.Source))
            {
                HistoryManager.AddEntry(core.Source, core.DocumentTitle);
            }

            _ = UpdateTabFaviconAsync(tab);
        });

        core.SourceChanged += (s, e) => Dispatcher.Invoke(() =>
        {
            if (tab.IsDisposed) return;
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

            if (_activeTab == tab)
            {
                TxtUrl.Text = tab.Url;
                UpdateCurrentTabBookmarkUI();
            }
        });

        core.DocumentTitleChanged += (s, e) => Dispatcher.Invoke(() =>
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
                    HistoryManager.AddEntry(core.Source, tab.Title);
                }
            }

            if (_activeTab == tab)
            {
                UpdateCurrentTabBookmarkUI();
            }
        });
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

            var icon = await FaviconService.ResolveFaviconAsync(tab.Url, stream, faviconUri);
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
    private void SelectTab(BrowserTab? tab)
    {
        if (tab == null || _activeTab == tab || tab.IsDisposed) return;

        try
        {
            var previousTab = _activeTab;

            // Mini-reproductor automático universal (si está activado en Configuración):
            if (SettingsManager.Current.EnableAutoVideoPip && previousTab != null && !previousTab.IsDisposed)
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

            // Si la pestaña estaba en la ventana de Página Flotante manual, cerrarla
            if (_pipWindow.IsVisible && _pipWindow.TargetTab == tab)
            {
                _pipWindow.HideAndDetach();
            }

            foreach (var t in Tabs)
            {
                bool isCurrent = (t == tab);
                t.IsSelected = isCurrent;
                if (t.WebView != null && !t.IsDisposed)
                {
                    t.WebView.Visibility = isCurrent ? Visibility.Visible : Visibility.Collapsed;
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

            if (tab != null)
            {
                TxtUrl.Text = tab.IsNewTab ? "" : tab.Url;
                TxtShieldBadge.Text = $"{tab.BlockedAdsCount} bloqueados";
                LoadingProgressBar.Visibility = tab.IsLoading ? Visibility.Visible : Visibility.Collapsed;
            }
            UpdateNavigationButtons();
            UpdateCurrentTabBookmarkUI();
            UpdateZoomIndicatorUI(showFloating: false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SelectTab] Error: {ex.Message}");
        }
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

            if (!_isDraggingTab)
            {
                if (Math.Abs(deltaX) > 6)
                {
                    _isDraggingTab = true;

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
        try
        {
            if (_draggedBorder != null)
            {
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

            if (_draggedTab != null)
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

    private void BookmarkItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is BookmarkItem item)
        {
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

    private void ShowToast(string message)
    {
        TxtToastMessage.Text = message;
        ToastNotification.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void ToastGoSettings_Click(object sender, RoutedEventArgs e)
    {
        ToastNotification.Visibility = Visibility.Collapsed;
        OpenSettings("Bookmarks");
    }

    private void ToastClose_Click(object sender, RoutedEventArgs e)
    {
        _toastTimer.Stop();
        ToastNotification.Visibility = Visibility.Collapsed;
    }

    // ─────────────────────────────────────────────────────────────────────
    // BARRA DE URL / OMNIBOX (COMPORTAMIENTO EXACTO CHROME Y BRAVE)
    // ─────────────────────────────────────────────────────────────────────
    private void TxtUrl_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Si la barra de URL no tenía el foco, este es el 1er clic:
        // Enfoca, selecciona todo el texto de la URL y previene que el clic desmarque la selección.
        if (!TxtUrl.IsKeyboardFocusWithin)
        {
            TxtUrl.Focus();
            TxtUrl.SelectAll();
            e.Handled = true; // Impide que WPF desubraye y ubique el cursor inmediatamente
        }
        // Si ya tenía el foco, este es el 2do clic o doble clic:
        // Se deja pasar normalmente para posicionar el palito parpadeante donde se hizo clic.
    }

    private void TxtUrl_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _activeTab?.WebView == null) return;

        string input = TxtUrl.Text.Trim();
        if (string.IsNullOrEmpty(input)) return;

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

        if (_activeTab.WebView.CoreWebView2 != null)
        {
            _activeTab.WebView.CoreWebView2.Navigate(targetUrl);
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
            _pipWindow.HideAndDetach();
            ReinsertWebViewToContainer(_activeTab);
        }
        else
        {
            _pipWindow.AttachTab(_activeTab);
            if (!_pipWindow.IsVisible)
                _pipWindow.Show();
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

    public void OpenHistory()
    {
        try
        {
            var historyWin = new HistoryWindow(_activeTab?.WebView?.CoreWebView2)
            {
                Owner = this
            };
            historyWin.UrlSelected += async (url) =>
            {
                if (!string.IsNullOrWhiteSpace(url))
                {
                    if (_activeTab?.WebView?.CoreWebView2 != null)
                        _activeTab.WebView.CoreWebView2.Navigate(url);
                    else
                        await AddNewTabAsync(url);
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
        try
        {
            var settingsWin = new SettingsWindow
            {
                Owner = this
            };
            settingsWin.OpenToSection(defaultSection);
            settingsWin.ShowDialog();
            ApplySettings();
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

        if (!string.IsNullOrWhiteSpace(tabToClose.Url) && !tabToClose.IsNewTab)
        {
            _closedTabsHistory.Push((tabToClose.Url, tabToClose.Title, tabToClose.IsIncognito));
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

    private void MenuMoveToNewWindow_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetTabFromMenuItem(sender);
        if (tab != null)
        {
            _pipWindow.AttachTab(tab);
            if (!_pipWindow.IsVisible)
                _pipWindow.Show();
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

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            BtnMaxRestore.Content = "□";
        }
        else
        {
            WindowState = WindowState.Maximized;
            BtnMaxRestore.Content = "❐";
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        if (_pipWindow.IsVisible)
            _pipWindow.HideAndDetach();

        _pipWindow.OnReturnToTabRequested = null;
        _pipWindow.OnDetachWebView = null;
        _pipWindow.Close();
        Close();
    }
}
