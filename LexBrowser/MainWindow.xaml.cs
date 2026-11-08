using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LexBrowser.Core;
using LexBrowser.Core.Engine;
using LexBrowser.Core.Interop;
using LexBrowser.Core.Notifications;
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
    private readonly bool _startInIncognito = false;
    private readonly bool _startInGuest = false;

    private static readonly SolidColorBrush BrushBrandGuest = CreateFrozenBrush(0x38, 0xBD, 0xF8);
    private static readonly SolidColorBrush BrushBrandOwner = CreateFrozenBrush(0x81, 0x8C, 0xF8);

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public MainWindow() : this((string?)null, null)
    {
    }

    public MainWindow(string? initialUrl) : this(initialUrl, null)
    {
    }

    public MainWindow(BrowserTab? initialTab) : this(null, initialTab)
    {
    }

    public MainWindow(string? initialUrl, BrowserTab? initialTab) : this(initialUrl, initialTab, false, false)
    {
    }

    public MainWindow(string? initialUrl, BrowserTab? initialTab = null, bool startInIncognito = false, bool startInGuest = false)
    {
        _initialUrl = NormalizeExternalUrl(initialUrl);
        _initialAttachedTab = initialTab;
        _startInIncognito = startInIncognito;
        _startInGuest = startInGuest;
        if (startInGuest)
        {
            _isGuestSession = true;
        }

        InitializeComponent();
        InitializeTranslateLanguageList();
        TabsItemsControl.ItemsSource = Tabs;
        ItemsOmniboxSuggestions.ItemsSource = _omniboxSuggestions;
        Tabs.CollectionChanged += (s, e) => UpdateTabWidths();
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
            if (PopupZoomWidget != null) PopupZoomWidget.IsOpen = false;
        };

        // Escuchar cambios de configuración, descargas y notificaciones
        SettingsManager.SettingsChanged += ApplySettings;
        DownloadManager.DownloadsChanged += OnDownloadsChanged;
        NotificationManager.OnNotificationsChanged += UpdateNotificationBadge;
        ApplySettings();
        UpdateNotificationBadge();

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
        BrowserContainer.SizeChanged += (s, e) =>
        {
            if (_activeTab?.WebView != null && !_activeTab.IsDisposed)
            {
                try { GetController(_activeTab.WebView)?.NotifyParentWindowPositionChanged(); } catch { }
            }
        };
        UpdateWindowMargin();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        CleanLegacyDiagnosticLogs();

        // Si fue abierto explícitamente en Modo Invitado desde la barra de tareas
        if (_startInGuest)
        {
            _isGuestSession = true;
            ApplySettings();
            await AddNewTabAsync(_initialUrl, isIncognito: true);
            ShowToast("👤 Modo Invitado activo: navegas en una sesión aislada sin acceso a las contraseñas ni marcadores del propietario.");
        }
        else
        {
            // Verificar si es la primera vez que abre el navegador (para ofrecer configurar PIN o elegir "No me gustaría usar contraseña")
            bool hasCompletedSecuritySetup = SettingsManager.Current.HasCompletedInitialSecuritySetup ||
                                             File.Exists(SettingsManager.SecuritySetupMarkerPath);

            if (_initialAttachedTab == null &&
                !hasCompletedSecuritySetup &&
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
                if (_startInIncognito)
                {
                    await AddNewTabAsync(_initialUrl, isIncognito: true);
                }
                else
                {
                    await RestoreOrInitializeSessionTabsAsync();
                }
            }
        }

        // Comprobar silenciosamente si existe una nueva versión publicada en GitHub Releases
        _ = CheckForBrowserUpdateInBackgroundAsync();

        // Comprobar una sola vez y sin consumo en segundo plano si es el navegador predeterminado
        _ = CheckDefaultBrowserStatusAsync();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            bool hasActiveDownloads = DownloadManager.GetEntries().Any(d => d.IsInProgress);
            if (hasActiveDownloads)
            {
                var confirm = MessageBox.Show(
                    "Hay descargas activas en progreso.\n\n¿Deseas cerrar el navegador de todos modos y cancelar las descargas en curso?",
                    "Descargas en curso — Lex Junior",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (confirm != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }
        }
        catch { }

        if (!_isGuestSession && SettingsManager.Current.RestorePreviousSession)
        {
            SessionManager.SaveSession(Tabs, _activeTab);
        }
        else if (_isGuestSession && _stashedOwnerTabs.Count > 0 && SettingsManager.Current.RestorePreviousSession)
        {
            SessionManager.SaveSession(_stashedOwnerTabs, _stashedOwnerActiveTab);
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            if (WindowState == WindowState.Normal)
            {
                RecordCurrentNormalBounds(saveToDisk: false);
            }
            SettingsManager.FlushDeferredSave();
            SettingsManager.Save(notify: false);

            SettingsManager.SettingsChanged -= ApplySettings;
            DownloadManager.DownloadsChanged -= OnDownloadsChanged;
            NotificationManager.OnNotificationsChanged -= UpdateNotificationBadge;
            DownloadManager.Flush();
            _nativePipTimer?.Stop();
            _undoBookmarkTimer.Stop();
            _toastTimer.Stop();
            _zoomWidgetTimer.Stop();
            _promptPeekTimer?.Stop();
            _clipboardShieldTimer?.Stop();
            _omniboxLiveSuggestCts?.Cancel();

            if (_pipWindow.IsVisible)
                _pipWindow.HideAndDetach();

            _pipWindow.OnReturnToTabRequested = null;
            _pipWindow.OnDetachWebView = null;
            try { _pipWindow.Close(); } catch { }

            foreach (var tab in Tabs.Concat(_stashedOwnerTabs).ToList())
            {
                if (tab.OwnerWindow == this && !tab.IsDisposed)
                {
                    tab.IsDisposed = true;
                    try { tab.WebView?.CoreWebView2?.Stop(); } catch { }
                    try { tab.WebView?.Dispose(); } catch { }
                }
            }
            Tabs.Clear();
            _stashedOwnerTabs.Clear();

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

                    var tab = await AddNewTabAsync(item.Url, isIncognito: false, skipInitialNavigate: !isActive, bypassLockCheck: true);
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
            var extTab = await AddNewTabAsync(launchUrl, isIncognito: false, bypassLockCheck: true);
            SelectTab(extTab, force: true);
        }
        else if (!sessionRestored && Tabs.Count == 0)
        {
            await AddNewTabAsync(null, isIncognito: false, bypassLockCheck: true);
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

    public async Task HandleExternalIpcCommandAsync(string commandOrUrl)
    {
        if (string.IsNullOrWhiteSpace(commandOrUrl)) return;

        if (string.Equals(commandOrUrl, "CMD:GUEST", StringComparison.OrdinalIgnoreCase))
        {
            if (!_isGuestSession)
            {
                await EnterGuestModeAsync();
            }
            return;
        }

        if (IsLocked)
        {
            if (!commandOrUrl.StartsWith("CMD:", StringComparison.OrdinalIgnoreCase))
            {
                _initialUrl = NormalizeExternalUrl(commandOrUrl);
            }
            return;
        }

        if (string.Equals(commandOrUrl, "CMD:INCOGNITO", StringComparison.OrdinalIgnoreCase))
        {
            if (_isGuestSession)
            {
                if (!TryUnlockFromGuestMode("Ingresa tu PIN de propietario para abrir una pestaña de incógnito"))
                    return;
            }
            await AddNewTabAsync(null, isIncognito: true);
            return;
        }

        if (string.Equals(commandOrUrl, "CMD:NEW_TAB", StringComparison.OrdinalIgnoreCase))
        {
            await AddNewTabAsync(null, isIncognito: _isGuestSession);
            return;
        }

        await AddNewTabFromExternalAsync(commandOrUrl);
    }

    public async Task AddNewTabFromExternalAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        string? normalized = NormalizeExternalUrl(url);
        if (string.IsNullOrWhiteSpace(normalized)) return;

        if (IsLocked)
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
            else
            {
                if (SettingsManager.Current.WasPreviouslyDefaultBrowser)
                {
                    SettingsManager.Current.WasPreviouslyDefaultBrowser = false;
                    SettingsManager.Save();
                }
            }

            // 2. Si el usuario ya tomó una decisión explícita permanente ("Sí" o "No volver a mostrar"):
            if (SettingsManager.Current.UserDecidedDefaultBrowser)
            {
                DefaultBrowserPromptBanner.Visibility = Visibility.Collapsed;
                return;
            }

            // 3. Si aún no ha decidido (o eligió "Ahora no" en sesiones previas), mostrarlo en la infobar superior
            DefaultBrowserPromptBanner.Visibility = Visibility.Visible;

            // 4. Permanecer visible por al menos 3 minutos. Si el usuario lo ignora por más de 3 minutos, desaparece esta sesión
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
        // "Ahora no" oculta el banner solo durante la sesión actual sin marcar decisión permanente
        DefaultBrowserPromptBanner.Visibility = Visibility.Collapsed;
    }

    private void BtnNeverAskDefaultBrowser_Click(object sender, RoutedEventArgs e)
    {
        DefaultBrowserPromptBanner.Visibility = Visibility.Collapsed;
        SettingsManager.Current.UserDecidedDefaultBrowser = true;
        SettingsManager.Save();

        ShowToast(
            "No volveremos a mostrar este aviso. Puedes cambiar tu navegador predeterminado cuando quieras en Configuración.",
            "Ir a Ajustes",
            () => OpenSettings("General"));
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

        if (TxtBrandTitle != null)
        {
            if (_isGuestSession)
            {
                TxtBrandTitle.Text = "LEX JUNIOR · INVITADO";
                TxtBrandTitle.Foreground = BrushBrandGuest;
            }
            else
            {
                TxtBrandTitle.Text = "LEX JUNIOR";
                TxtBrandTitle.Foreground = BrushBrandOwner;
            }
        }

        UpdateNotificationBadge();
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
    // GESTOR DE ATAJOS DE TECLADO GLOBALES (UNIVERSALES)
    // ─────────────────────────────────────────────────────────────────────
    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsLocked)
        {
            return;
        }

        // 0. F12 o Ctrl + Shift + I: Abrir Herramientas de Desarrollo (DevTools) en la pestaña activa
        if (e.Key == Key.F12 || (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.I))
        {
            e.Handled = true;
            OpenDevToolsForActiveTab();
            return;
        }

        // 0.05 Ctrl + Shift + L: Volcar y copiar Shield Logger (registro de seguridad y bloqueos)
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.L)
        {
            e.Handled = true;
            CopyOrExportShieldLog();
            return;
        }

        // 0.08 Atajo configurable para No Molestar (por defecto Ctrl + Shift + D)
        if (IsNotificationDndShortcut(e))
        {
            e.Handled = true;
            if (!_isGuestSession)
            {
                ToggleNotificationDoNotDisturb();
            }
            return;
        }

        // 0.1 Ctrl + Shift + K: Abrir Bóveda de Contraseñas
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
            if (_isGuestSession) return;
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
            await AddNewTabAsync(null, isIncognito: _isGuestSession);
            return;
        }

        // 5. Ctrl + Shift + N: Modo Incógnito
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.N)
        {
            e.Handled = true;
            if (_isGuestSession)
            {
                if (!TryUnlockFromGuestMode("Ingresa tu PIN de propietario para abrir una pestaña de incógnito"))
                    return;
            }
            await AddNewTabAsync(null, isIncognito: true);
            return;
        }

        // 6. Ctrl + Shift + T: Reabrir última pestaña cerrada
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.T)
        {
            e.Handled = true;
            if (!_isGuestSession)
            {
                ReopenLastClosedTab();
            }
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
            if (_isGuestSession && !TryUnlockFromGuestMode("Desbloquea con tu PIN para ver el Historial de Descargas"))
                return;
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

        // 10.3 Ctrl + +: Aumentar zoom (solo teclas estándar de zoom para no interferir con atajos de código/IDE web)
        if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.OemPlus || e.Key == Key.Add))
        {
            e.Handled = true;
            ZoomIn();
            return;
        }

        // 10.4 Ctrl + -: Reducir zoom
        if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.OemMinus || e.Key == Key.Subtract))
        {
            e.Handled = true;
            ZoomOut();
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

    private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        UI.GlobalScrollBehavior.HandleSmoothScrollViewerMouseWheel(sender, e, 28.0);
    }

    private void TabsScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer sv && sv.ScrollableWidth > 0)
        {
            e.Handled = true;
            double delta = (e.Delta / 120.0) * 64.0;
            double target = Math.Clamp(sv.HorizontalOffset - delta, 0, sv.ScrollableWidth);
            sv.ScrollToHorizontalOffset(target);
        }
    }
}
