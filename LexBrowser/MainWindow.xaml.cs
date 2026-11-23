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
    private bool _pendingBrowserFullScreenEnter = false;

    // Variables para arrastre visual suave de pestañas y desprendimiento a nueva ventana (Tab Tear-Off)
    private Point _tabDragStartPoint;
    private BrowserTab? _draggedTab;
    private Border? _draggedBorder;
    private bool _isDraggingTab = false;
    private BrowserTab? _initialAttachedTab;
    private string? _initialUrl;
    private readonly bool _startInIncognito = false;
    private readonly bool _startInGuest = false;
    private bool _hasInitializedSession = false;
    private DispatcherTimer? _sessionSaveTimer;

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
        InitializeOmniboxBehavior();
        TabsItemsControl.ItemsSource = Tabs;
        ItemsOmniboxSuggestions.ItemsSource = _omniboxSuggestions;
        Tabs.CollectionChanged += (s, e) =>
        {
            UpdateTabWidths();
            ScheduleSessionSave();
        };

        bool deferBookmarksInit =
            _isGuestSession ||
            !FaviconService.IsStartupNetworkGateOpen ||
            SettingsManager.DowngradeDetected ||
            ShouldShowPrivacyOnboardingWindow(SettingsManager.Current, _isGuestSession, _initialAttachedTab != null) ||
            PasswordManager.ShouldRequireUnlockOnStartup();
        BookmarksItemsControl.ItemsSource = deferBookmarksInit ? null : BookmarkManager.Bookmarks;

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

        // Escuchar cambios de configuración, descargas, actualizaciones y notificaciones
        SettingsManager.SettingsChanged += ApplySettings;
        UpdateManager.UpdateDiscovered += OnUpdateDiscovered;
        DownloadManager.Load();
        DownloadManager.DownloadsChanged += OnDownloadsChanged;
        NotificationManager.OnNotificationsChanged += UpdateNotificationBadge;
        ApplySettings();
        UpdateDownloadsToolbarIndicator();
        UpdateNotificationBadge();
        if (PopupFullScreenExit != null)
        {
            PopupFullScreenExit.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            {
                double x = Math.Max(0, (targetSize.Width - popupSize.Width) * 0.5);
                return new[] { new System.Windows.Controls.Primitives.CustomPopupPlacement(new Point(x, 0), System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal) };
            };
        }

        Deactivated += (_, _) =>
        {
            HideToast();
            HideFullScreenExitPopupImmediate();
            CloseSnapLayoutsPopupImmediate();
            if (PopupOmniboxSuggestions != null && PopupOmniboxSuggestions.IsOpen)
                PopupOmniboxSuggestions.IsOpen = false;
        };
        LocationChanged += (_, _) =>
        {
            CloseSnapLayoutsPopupImmediate();
            if (PopupOmniboxSuggestions != null && PopupOmniboxSuggestions.IsOpen)
                PopupOmniboxSuggestions.IsOpen = false;
        };
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
            {
                HideToast();
                HideFullScreenExitPopupImmediate();
            }
        };

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
        PreviewMouseDown += (_, _) =>
        {
            if (PopupSnapLayouts != null && PopupSnapLayouts.IsOpen &&
                !IsCursorOverSnapElement(BtnMaxRestore, expandTopPx: 8, expandBottomPx: 10, expandSidePx: 4) &&
                !IsCursorOverSnapElement(SnapLayoutsCard, expandTopPx: 10, expandBottomPx: 6, expandSidePx: 6))
            {
                CloseSnapLayoutsPopupImmediate();
            }
        };
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

    private static bool _hasShownSettingsRecoveryNotice = false;
    private bool _hasStartedPostOnboardingTasks = false;

    public enum StartupExecutionStage
    {
        GuestSession,
        FirstTimePinSetupOverlay,
        PinUnlockOverlay,
        AttachExistingTab,
        OnboardingAndTabs,
        DirectTabs
    }

    internal static bool ShouldShowPrivacyOnboardingWindow(
        BrowserSettings? settings,
        bool isGuestSession,
        bool hasInitialAttachedTab)
    {
        if (isGuestSession || hasInitialAttachedTab)
            return false;

        return (settings?.PrivacyProfileOnboardingVersion ?? 0) < PrivacyProfiles.CurrentOnboardingVersion;
    }

    internal static StartupExecutionStage DetermineStartupStage(
        bool startInGuest,
        bool hasInitialAttachedTab,
        bool hasCompletedSecuritySetup,
        bool hasMasterPasswordConfigured,
        bool lockSettingsPotentiallyLost,
        bool shouldRequireUnlockOnStartup,
        int tabsCount,
        int privacyOnboardingVersion)
    {
        if (startInGuest)
            return StartupExecutionStage.GuestSession;

        if (!hasInitialAttachedTab &&
            !hasCompletedSecuritySetup &&
            !hasMasterPasswordConfigured &&
            !lockSettingsPotentiallyLost)
        {
            return StartupExecutionStage.FirstTimePinSetupOverlay;
        }

        if (!hasInitialAttachedTab && shouldRequireUnlockOnStartup)
            return StartupExecutionStage.PinUnlockOverlay;

        if (hasInitialAttachedTab)
            return StartupExecutionStage.AttachExistingTab;

        if (tabsCount == 0 && privacyOnboardingVersion < PrivacyProfiles.CurrentOnboardingVersion)
            return StartupExecutionStage.OnboardingAndTabs;

        return StartupExecutionStage.DirectTabs;
    }

    private Task EnsurePrivacyOnboardingCompletedBeforeTabsAsync()
    {
        if (!ShouldShowPrivacyOnboardingWindow(SettingsManager.Current, _isGuestSession, _initialAttachedTab != null))
            return Task.CompletedTask;

        // Si el archivo original está exclusivamente bloqueado sin poder copiarse respaldo, no forzar escritura ni perfil B
        if (SettingsManager.HadUnreadableSettingsFile && string.IsNullOrEmpty(SettingsManager.CorruptSettingsBackupPath))
            return Task.CompletedTask;

        bool engineAlreadyCreated = BrowserEngine.IsEnvironmentCreated;
        bool isFreshInstall = SettingsManager.IsFreshInstall;
        bool closedWithoutChoice = false;
        bool settingsWereChanged = false;

        _isPrivacyOnboardingOpen = true;
        FaviconService.SetStartupNetworkGate(false);
        try
        {
            var setupWin = new PrivacyProfileSetupWindow(
                isFreshInstall: isFreshInstall,
                isEnvironmentAlreadyCreated: engineAlreadyCreated,
                isReopenedManually: false)
            {
                Owner = this
            };
            setupWin.ShowDialog();
            closedWithoutChoice = setupWin.ClosedWithoutChoice;
            settingsWereChanged = setupWin.SettingsWereChanged;
        }
        finally
        {
            _isPrivacyOnboardingOpen = false;
        }

        if (closedWithoutChoice)
        {
            ShowToast(
                "Puedes ajustar tu perfil cuando quieras en Configuración → Privacidad y seguridad.",
                "Abrir ajustes",
                () => OpenSettings("Privacy"),
                durationSeconds: 7);
        }

        if (engineAlreadyCreated && settingsWereChanged && BrowserEngine.RequiresRestartForCurrentSettings())
        {
            ShowToast(
                "Los cambios en SmartScreen o detección de phishing se aplican al reiniciar Lex Junior.",
                "Reiniciar ahora",
                () => App.RestartBrowserSafely(),
                durationSeconds: 8);
        }

        return Task.CompletedTask;
    }

    private void ReleaseFaviconGateAndRefreshBookmarks()
    {
        if (_isGuestSession)
        {
            if (BookmarksItemsControl.ItemsSource != null)
                BookmarksItemsControl.ItemsSource = null;
            return;
        }

        FaviconService.SetStartupNetworkGate(true);

        if (BookmarksItemsControl.ItemsSource == null)
        {
            BookmarksItemsControl.ItemsSource = BookmarkManager.Bookmarks;
        }

        foreach (var bk in BookmarkManager.Bookmarks)
        {
            bk.RefreshFaviconFromProfileState();
        }
    }

    private void StartPostOnboardingBackgroundTasks()
    {
        if (_isGuestSession || IsLocked || _hasStartedPostOnboardingTasks)
            return;

        _hasStartedPostOnboardingTasks = true;
        _ = CheckForBrowserUpdateInBackgroundAsync();
        _ = CheckDefaultBrowserStatusAsync();
    }

    internal void PersistOwnerSessionNowIfEligible()
    {
        try
        {
            if (!_isGuestSession && !IsLocked && SettingsManager.Current.RestorePreviousSession && Tabs.Count > 0)
            {
                SessionManager.SaveSession(Tabs, _activeTab);
            }
        }
        catch { }
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
            return;
        }

        if (SettingsManager.DowngradeDetected)
        {
            var downgradeDlg = new VersionDowngradeDialog(
                SettingsManager.CurrentAppVersion.ToString(3),
                SettingsManager.DetectedNewerProfileVersion ?? "superior")
            {
                Owner = this
            };
            downgradeDlg.ShowDialog();
            Application.Current?.Shutdown();
            return;
        }

        bool canShowOnboardingWindow =
            ShouldShowPrivacyOnboardingWindow(SettingsManager.Current, _isGuestSession, _initialAttachedTab != null) &&
            (!SettingsManager.HadUnreadableSettingsFile || !string.IsNullOrEmpty(SettingsManager.CorruptSettingsBackupPath));

        if (SettingsManager.AutoSavesSuspended && !canShowOnboardingWindow && !_hasShownSettingsRecoveryNotice)
        {
            _hasShownSettingsRecoveryNotice = true;
            if (!string.IsNullOrEmpty(SettingsManager.CorruptSettingsBackupPath) || !File.Exists(SettingsManager.SettingsPath))
            {
                var recoveryDialog = new SettingsRecoveryDialog(SettingsManager.SettingsRecoveryWarningMessage)
                {
                    Owner = this
                };
                recoveryDialog.ShowDialog();
            }
            else
            {
                MessageBox.Show(
                    SettingsManager.SettingsRecoveryWarningMessage ??
                    "No se pudo leer ni respaldar settings.json porque está bloqueado en disco. El guardado automático seguirá desactivado para no sobrescribirlo.",
                    "Recuperación de configuración — Lex Junior",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        else if (SettingsManager.HadUnreadableSettingsFile && !_hasShownSettingsRecoveryNotice)
        {
            _hasShownSettingsRecoveryNotice = true;
            MessageBox.Show(
                SettingsManager.SettingsRecoveryWarningMessage ?? "No se pudo leer settings.json y se creó un respaldo preventivo.",
                "Recuperación de configuración — Lex Junior",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        // Orden en propietario: (1) setup/desbloqueo PIN; (2) ventana inicial de perfiles; (3) pestañas + tareas de fondo
        bool hasCompletedSecuritySetup = SettingsManager.Current.HasCompletedInitialSecuritySetup ||
                                         File.Exists(SettingsManager.SecuritySetupMarkerPath);

        var stage = DetermineStartupStage(
            startInGuest: false,
            hasInitialAttachedTab: _initialAttachedTab != null,
            hasCompletedSecuritySetup: hasCompletedSecuritySetup,
            hasMasterPasswordConfigured: PasswordManager.HasMasterPasswordConfigured(),
            lockSettingsPotentiallyLost: SettingsManager.IsVaultRecoveryLockActive,
            shouldRequireUnlockOnStartup: PasswordManager.ShouldRequireUnlockOnStartup(),
            tabsCount: Tabs.Count,
            privacyOnboardingVersion: SettingsManager.Current.PrivacyProfileOnboardingVersion);

        switch (stage)
        {
            case StartupExecutionStage.FirstTimePinSetupOverlay:
                ShowFirstTimeSecuritySetupOverlay();
                return;

            case StartupExecutionStage.PinUnlockOverlay:
                ShowBrowserLockOverlay();
                return;

            case StartupExecutionStage.AttachExistingTab:
                var initial = _initialAttachedTab!;
                _initialAttachedTab = null;
                AttachExistingTab(initial);
                _hasInitializedSession = true;
                ReleaseFaviconGateAndRefreshBookmarks();
                ScheduleSessionSave();
                StartPostOnboardingBackgroundTasks();
                return;

            case StartupExecutionStage.OnboardingAndTabs:
            case StartupExecutionStage.DirectTabs:
            default:
                await EnsurePrivacyOnboardingCompletedBeforeTabsAsync();
                ReleaseFaviconGateAndRefreshBookmarks();
                ApplySettings();

                if (Tabs.Count == 0)
                {
                    if (_startInIncognito)
                    {
                        string? incUrl = _initialUrl;
                        _initialUrl = null;
                        await AddNewTabAsync(incUrl, isIncognito: true);
                    }
                    else
                    {
                        await RestoreOrInitializeSessionTabsAsync();
                    }
                }

                StartPostOnboardingBackgroundTasks();
                return;
        }
    }

    internal static bool ShouldPromptActiveDownloadsOnClose(bool isRestartingProcess, bool hasActiveDownloads, int remainingOtherMainWindows = 0)
        => !isRestartingProcess && hasActiveDownloads && remainingOtherMainWindows == 0;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            SyncAndReconcileAllActiveDownloadsOnUi();
            int remainingOtherWindows = Application.Current?.Windows
                .OfType<MainWindow>()
                .Count(w => w != this && !w._isWindowClosing && w.IsLoaded) ?? 0;

            bool hasActiveDownloads = DownloadManager.GetActiveSummary().HasActive;
            if (ShouldPromptActiveDownloadsOnClose(App.IsRestartingProcess, hasActiveDownloads, remainingOtherWindows))
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

                DownloadManager.CancelAllActive();
            }
            else if (hasActiveDownloads && remainingOtherWindows == 0)
            {
                DownloadManager.CancelAllActive();
            }
        }
        catch { }

        _sessionSaveTimer?.Stop();

        bool isLastOwnerWindow = false;
        try
        {
            isLastOwnerWindow = Application.Current == null ||
                                !Application.Current.Windows.OfType<MainWindow>().Any(w => w != this && !w._isGuestSession);
        }
        catch { }

        if (ShouldPersistSessionOnClose(
            _isGuestSession,
            isLastOwnerWindow,
            SettingsManager.Current.RestorePreviousSession,
            _hasInitializedSession,
            IsLocked))
        {
            SessionManager.SaveSession(Tabs, _activeTab);
        }
        else if (_isGuestSession && _stashedOwnerTabs.Count > 0 && SettingsManager.Current.RestorePreviousSession)
        {
            SessionManager.SaveSession(_stashedOwnerTabs, _stashedOwnerActiveTab);
        }

        base.OnClosing(e);
    }

    internal static bool ShouldPersistSessionOnClose(
        bool isGuestSession,
        bool isLastOwnerWindow,
        bool restorePreviousSession,
        bool hasInitializedSession,
        bool isLocked)
    {
        return !isGuestSession &&
               isLastOwnerWindow &&
               restorePreviousSession &&
               hasInitializedSession &&
               !isLocked;
    }

    private void ScheduleSessionSave()
    {
        if (_isWindowClosing || _isGuestSession || IsLocked || !_hasInitializedSession ||
            !SettingsManager.Current.RestorePreviousSession)
        {
            return;
        }

        if (_sessionSaveTimer == null)
        {
            _sessionSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _sessionSaveTimer.Tick += (_, _) =>
            {
                _sessionSaveTimer?.Stop();
                if (_isWindowClosing || _isGuestSession || IsLocked || !_hasInitializedSession ||
                    !SettingsManager.Current.RestorePreviousSession)
                {
                    return;
                }

                var validTabs = Tabs
                    .Where(t => !t.IsIncognito && !t.IsDisposed && !string.IsNullOrWhiteSpace(t.Url) &&
                                !t.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
                                t.Url != "about:blank" && !t.IsNewTab)
                    .ToList();

                int activeIdx = _activeTab != null ? validTabs.IndexOf(_activeTab) : 0;
                if (activeIdx < 0) activeIdx = 0;

                var items = validTabs.Select(t => new SessionTabItem
                {
                    Url = t.Url,
                    Title = t.Title,
                    IsPinned = t.IsPinned
                }).ToList();

                _ = Task.Run(() => SessionManager.SaveSessionSnapshot(items, activeIdx));
            };
        }

        _sessionSaveTimer.Stop();
        _sessionSaveTimer.Start();
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
            UpdateManager.UpdateDiscovered -= OnUpdateDiscovered;
            DownloadManager.DownloadsChanged -= OnDownloadsChanged;
            NotificationManager.OnNotificationsChanged -= UpdateNotificationBadge;
            DownloadManager.Flush();
            ReadAloudService.Stop();
            _isWindowClosing = true;
            HideFullScreenExitPopupImmediate();
            _sessionSaveTimer?.Stop();
            _suspensionTimer?.Stop();
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
                    try { tab.SnifferSession?.Dispose(); tab.SnifferSession = null; } catch { }
                    try { tab.CorsUnlocker?.Dispose(); tab.CorsUnlocker = null; } catch { }
                    try { tab.ClearDetectedMedia(); } catch { }
                    try { tab.ActiveFrames.Clear(); } catch { }
                    try { tab.RecentReloadTimesUtc.Clear(); } catch { }
                    try { tab.CompatibilityPromptShownDomains.Clear(); } catch { }
                    try { tab.FaviconImage = null; } catch { }
                    try { tab.OwnerWindow = null; } catch { }
                    if (tab.IsSuspendedByLex)
                    {
                        ResumeTab(tab);
                    }
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
                try { ReadAloudService.Dispose(); } catch { }
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

        _hasInitializedSession = true;
        if (!string.IsNullOrWhiteSpace(launchUrl) && !IsLocked && !_isGuestSession)
        {
            ScheduleSessionSave();
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

        if (string.Equals(commandOrUrl, "CMD:LEXCLASS", StringComparison.OrdinalIgnoreCase))
        {
            OpenOrFocusLexClassWindow();
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

    private void OnUpdateDiscovered(UpdateReleaseInfo update)
    {
        if (_isGuestSession || update == null) return;
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_isGuestSession) return;
                TxtUpdateAvailableLabel.Text = $"Actualizar ({update.VersionTag})";
                BtnUpdateAvailable.ToolTip = $"¡Nueva versión {update.VersionTag} disponible! Haz clic para instalarla automáticamente.";
                BtnUpdateAvailable.Visibility = Visibility.Visible;
            });
        }
        catch { }
    }

    private async Task CheckForBrowserUpdateInBackgroundAsync()
    {
        if (_isGuestSession || IsLocked) return;

        try
        {
            await Task.Delay(2500);
            if (_isGuestSession) return;
            var update = await UpdateManager.CheckForUpdatesAsync(isUserInitiated: false);
            if (update != null && !_isGuestSession)
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
        // Solo se verifica una vez tras arrancar la ventana, sin temporizadores recurrentes
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

        if (_isGuestSession || IsLocked || !FaviconService.IsStartupNetworkGateOpen)
        {
            if (BookmarksItemsControl.ItemsSource != null)
                BookmarksItemsControl.ItemsSource = null;
        }
        else if (BookmarksItemsControl.ItemsSource == null)
        {
            BookmarksItemsControl.ItemsSource = BookmarkManager.Bookmarks;
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
        RescheduleSuspensionTimer();
        UpdateThemeStyles(s.Theme);
    }

    public void UpdateThemeStyles(string? theme = null)
    {
        string target = BrowserThemeManager.NormalizeTheme(theme ?? SettingsManager.Current.Theme);
        BrowserThemeManager.ApplyThemePalette(Resources, target);

        if (Resources["BgDark"] is Brush bgBrush)
        {
            Background = bgBrush;
        }
        if (Resources["TextLight"] is Brush fgBrush)
        {
            Foreground = fgBrush;
        }

        // Refrescar cualquier pestaña que esté en Nueva Pestaña (lex://newtab)
        foreach (var tab in Tabs)
        {
            if (tab.IsNewTab && !tab.IsDisposed && tab.WebView?.CoreWebView2 != null)
            {
                try
                {
                    tab.WebView.NavigateToString(NewTabPage.GetHtml(tab.IsIncognito, isGuest: tab.IsGuest));
                }
                catch { }
            }
        }

        UpdateShieldBadgeText();
        UpdateVolumeBoosterUI();
        UpdateMediaSnifferUI();
        UpdateCurrentTabBookmarkUI();
        UpdateTabWidths();
    }

    private Action? _toastActionCallback;

    private void ShowToast(string message, string? actionLabel = null, Action? onAction = null, int durationSeconds = 6)
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
        _toastTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(durationSeconds, 3, 25));
        _toastTimer.Start();
    }

    private void ToastBorder_MouseEnter(object sender, MouseEventArgs e)
    {
        _toastTimer.Stop();
    }

    private void ToastBorder_MouseLeave(object sender, MouseEventArgs e)
    {
        if (PopupToast != null && PopupToast.IsOpen)
        {
            _toastTimer.Start();
        }
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
    private System.Collections.Generic.IEnumerable<System.Windows.Controls.Primitives.Popup?> EnumerateToolbarPopups()
    {
        yield return PopupShieldPanel;
        yield return PopupDownloads;
        yield return PopupSitePermissions;
        yield return PopupVolumeBooster;
        yield return PopupMediaSniffer;
        yield return PopupNotifications;
        yield return PopupTranslate;
        yield return PopupOmniboxSuggestions;
        yield return PopupZoomWidget;
        yield return PopupAutofillPicker;
        yield return PopupSnapLayouts;
    }

    internal bool HasOpenToolbarPopups()
    {
        foreach (var popup in EnumerateToolbarPopups())
        {
            if (popup != null && popup.IsOpen)
                return true;
        }
        return false;
    }

    internal bool CloseOpenToolbarPopups()
    {
        bool closedAny = false;
        foreach (var popup in EnumerateToolbarPopups())
        {
            if (popup != null && popup.IsOpen)
            {
                popup.IsOpen = false;
                closedAny = true;
            }
        }
        return closedAny;
    }

    internal static bool ShouldExitFullScreenOnEscape(bool fullScreenChromeActive, bool isTabInDomFullScreen)
        => fullScreenChromeActive && !isTabInDomFullScreen;

    private void RunShortcutAction(bool deferWindowMutations, Action action)
    {
        if (!deferWindowMutations)
        {
            action();
            return;
        }

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_isWindowClosing || IsLocked || IsBrowserLockOverlayActive())
                return;
            action();
        });
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsLocked)
        {
            return;
        }

        Key actualKey = e.Key == Key.System ? e.SystemKey : e.Key;
        if (HandleBrowserShortcutKey(actualKey, Keyboard.Modifiers, _activeTab?.WebView?.CoreWebView2, e.IsRepeat))
        {
            e.Handled = true;
        }
    }

    private Key _lastNonRepeatShortcutKey = Key.None;
    private ModifierKeys _lastNonRepeatShortcutModifiers = ModifierKeys.None;
    private long _lastNonRepeatShortcutTickMs = 0;
    private long _lastF11ToggleTickMs = 0;

    internal static bool ShouldAcceptShortcutInvocation(
        Key key,
        ModifierKeys modifiers,
        bool isRepeat,
        long nowTickMs,
        ref Key lastKey,
        ref ModifierKeys lastModifiers,
        ref long lastTickMs,
        ref long lastF11TickMs)
    {
        if (modifiers == ModifierKeys.None && key == Key.F11)
        {
            if (isRepeat) return false;
            if (lastF11TickMs != 0 && unchecked(nowTickMs - lastF11TickMs) < 250)
                return false;
            lastF11TickMs = nowTickMs;
            return true;
        }

        if (!isRepeat)
        {
            if (key == lastKey && modifiers == lastModifiers && lastTickMs != 0 && unchecked(nowTickMs - lastTickMs) < 40)
                return false;

            lastKey = key;
            lastModifiers = modifiers;
            lastTickMs = nowTickMs;
        }

        return true;
    }

    private bool AcceptShortcutOnce(Key key, ModifierKeys modifiers, bool isRepeat) =>
        ShouldAcceptShortcutInvocation(
            key,
            modifiers,
            isRepeat,
            Environment.TickCount64,
            ref _lastNonRepeatShortcutKey,
            ref _lastNonRepeatShortcutModifiers,
            ref _lastNonRepeatShortcutTickMs,
            ref _lastF11ToggleTickMs);

    internal bool HandleBrowserShortcutKey(
        Key key,
        ModifierKeys modifiers,
        Microsoft.Web.WebView2.Core.CoreWebView2? activeWebViewCore = null,
        bool isRepeat = false,
        bool deferWindowMutations = false)
    {
        if (IsLocked || IsBrowserLockOverlayActive())
        {
            return false;
        }

        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            bool hasPopups = HasOpenToolbarPopups();
            bool shouldExitFs = ShouldExitFullScreenOnEscape(
                _fullScreenChromeActive || _pendingBrowserFullScreenEnter,
                IsTabInDomFullScreen(_activeTab));

            if (hasPopups || shouldExitFs)
            {
                if (AcceptShortcutOnce(key, modifiers, isRepeat))
                {
                    RunShortcutAction(deferWindowMutations, () =>
                    {
                        if (CloseOpenToolbarPopups())
                            return;

                        if (ShouldExitFullScreenOnEscape(_fullScreenChromeActive, IsTabInDomFullScreen(_activeTab)))
                        {
                            ExitAllFullScreen();
                        }
                    });
                }
                return true;
            }
        }

        // 0.0 F11: Pantalla completa global del navegador (desduplicado entre AcceleratorKeyPressed y PreviewKeyDown)
        if (modifiers == ModifierKeys.None && key == Key.F11)
        {
            if (AcceptShortcutOnce(key, modifiers, isRepeat))
            {
                _pendingBrowserFullScreenEnter = !_isBrowserF11FullScreen && !IsTabInDomFullScreen(_activeTab);
                RunShortcutAction(deferWindowMutations, () =>
                {
                    _pendingBrowserFullScreenEnter = false;
                    ToggleBrowserFullScreen();
                });
            }
            return true;
        }

        // 0. F12 o Ctrl + Shift + I: Abrir Herramientas de Desarrollo (DevTools) en la pestaña activa
        if (key == Key.F12 || (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.I))
        {
            if (!AcceptShortcutOnce(key, modifiers, isRepeat)) return true;
            RunShortcutAction(deferWindowMutations, () =>
            {
                if (activeWebViewCore != null)
                {
                    try { activeWebViewCore.OpenDevToolsWindow(); } catch { }
                }
                else
                {
                    OpenDevToolsForActiveTab();
                }
            });
            return true;
        }

        // 0.05 Ctrl + Shift + L: Exportar Shield Logger a carpeta Logs\
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.L)
        {
            if (!AcceptShortcutOnce(key, modifiers, isRepeat)) return true;
            RunShortcutAction(deferWindowMutations, () => CopyOrExportShieldLog());
            return true;
        }

        // 0.08 Atajo configurable para No Molestar (por defecto Ctrl + Shift + D)
        if (IsNotificationDndShortcut(key, modifiers))
        {
            if (!AcceptShortcutOnce(key, modifiers, isRepeat)) return true;
            RunShortcutAction(deferWindowMutations, () =>
            {
                if (!_isGuestSession)
                {
                    ToggleNotificationDoNotDisturb();
                }
            });
            return true;
        }

        // 0.1 Ctrl + Shift + K: Abrir Bóveda de Contraseñas
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.K)
        {
            if (!AcceptShortcutOnce(key, modifiers, isRepeat)) return true;
            RunShortcutAction(deferWindowMutations, () => OpenPasswordVault());
            return true;
        }

        // 1. Ctrl + Tab: Siguiente pestaña (desduplicado contra eco doble WPF + WebView2)
        if (modifiers == ModifierKeys.Control && key == Key.Tab)
        {
            if (!AcceptShortcutOnce(key, modifiers, isRepeat)) return true;
            RunShortcutAction(deferWindowMutations, () =>
            {
                if (Tabs.Count > 1 && _activeTab != null)
                {
                    int currentIdx = Tabs.IndexOf(_activeTab);
                    int nextIdx = (currentIdx + 1) % Tabs.Count;
                    SelectTab(Tabs[nextIdx]);
                }
            });
            return true;
        }

        // 2. Ctrl + Shift + Tab: Pestaña anterior (desduplicado contra eco doble WPF + WebView2)
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.Tab)
        {
            if (!AcceptShortcutOnce(key, modifiers, isRepeat)) return true;
            RunShortcutAction(deferWindowMutations, () =>
            {
                if (Tabs.Count > 1 && _activeTab != null)
                {
                    int currentIdx = Tabs.IndexOf(_activeTab);
                    int prevIdx = (currentIdx - 1 + Tabs.Count) % Tabs.Count;
                    SelectTab(Tabs[prevIdx]);
                }
            });
            return true;
        }

        // 3. Ctrl + Shift + B: Barra de Accesos Directos
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.B)
        {
            if (!AcceptShortcutOnce(key, modifiers, isRepeat)) return true;
            RunShortcutAction(deferWindowMutations, () =>
            {
                if (_isGuestSession) return;
                SettingsManager.Current.ShowBookmarksBar = !SettingsManager.Current.ShowBookmarksBar;
                SettingsManager.Save();
                if (!SettingsManager.Current.ShowBookmarksBar)
                {
                    ShowToast("Barra de accesos directos oculta. Para volver a activarla ve a Configuración (o presiona Ctrl+Shift+B)");
                }
            });
            return true;
        }

        // 4. Ctrl + T o Ctrl + N: Abrir nueva pestaña
        if (modifiers == ModifierKeys.Control && (key == Key.T || key == Key.N))
        {
            if (!AcceptShortcutOnce(key, modifiers, isRepeat)) return true;
            RunShortcutAction(deferWindowMutations, () => _ = AddNewTabAsync(null, isIncognito: _isGuestSession));
            return true;
        }

        // 5. Ctrl + Shift + N: Modo Incógnito
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.N)
        {
            if (!AcceptShortcutOnce(key, modifiers, isRepeat)) return true;
            RunShortcutAction(deferWindowMutations, () =>
            {
                if (_isGuestSession)
                {
                    if (!TryUnlockFromGuestMode("Ingresa tu PIN de propietario para abrir una pestaña de incógnito"))
                        return;
                }
                _ = AddNewTabAsync(null, isIncognito: true);
            });
            return true;
        }

        // 6. Ctrl + Shift + T: Reabrir última pestaña cerrada
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.T)
        {
            if (!AcceptShortcutOnce(key, modifiers, isRepeat)) return true;
            RunShortcutAction(deferWindowMutations, () =>
            {
                if (!_isGuestSession)
                {
                    ReopenLastClosedTab();
                }
            });
            return true;
        }

        // 7. Ctrl + W: Cerrar pestaña actual
        if (modifiers == ModifierKeys.Control && key == Key.W)
        {
            if (!AcceptShortcutOnce(key, modifiers, isRepeat)) return true;
            RunShortcutAction(deferWindowMutations, () =>
            {
                if (_activeTab != null) CloseTab(_activeTab);
            });
            return true;
        }

        // 8. Ctrl + R o F5: Recargar
        if ((modifiers == ModifierKeys.Control && key == Key.R) || (modifiers == ModifierKeys.None && key == Key.F5))
        {
            if (!AcceptShortcutOnce(key, modifiers, isRepeat)) return true;
            RunShortcutAction(deferWindowMutations, () =>
            {
                if (_activeTab != null)
                {
                    RecordUserReloadAndCheckCompat(_activeTab);
                    _activeTab.WebView?.CoreWebView2?.Reload();
                }
            });
            return true;
        }

        // 9. Ctrl + L o Alt + D: Enfocar y seleccionar barra de URL
        if ((modifiers == ModifierKeys.Control && key == Key.L) ||
            (modifiers == ModifierKeys.Alt && key == Key.D))
        {
            RunShortcutAction(deferWindowMutations, () =>
            {
                TxtUrl.Focus();
                TxtUrl.SelectAll();
            });
            return true;
        }

        // 10. Ctrl + ,: Abrir Configuración
        if (modifiers == ModifierKeys.Control && key == Key.OemComma)
        {
            RunShortcutAction(deferWindowMutations, () => OpenSettings("General"));
            return true;
        }

        // 10.1 Ctrl + H: Abrir Historial de Navegación y Limpieza
        if (modifiers == ModifierKeys.Control && key == Key.H)
        {
            RunShortcutAction(deferWindowMutations, OpenHistory);
            return true;
        }

        // 10.1b Ctrl + J: Abrir Historial de Descargas
        if (modifiers == ModifierKeys.Control && key == Key.J)
        {
            RunShortcutAction(deferWindowMutations, () =>
            {
                if (_isGuestSession && !TryUnlockFromGuestMode("Desbloquea con tu PIN para ver el Historial de Descargas"))
                    return;
                RefreshDownloadsPopupUI();
                PopupDownloads.IsOpen = !PopupDownloads.IsOpen;
            });
            return true;
        }

        // 10.2 Ctrl + 0: Restablecer zoom a 100%
        if (modifiers == ModifierKeys.Control && (key == Key.D0 || key == Key.NumPad0))
        {
            RunShortcutAction(deferWindowMutations, ResetZoom);
            return true;
        }

        // 10.3 Ctrl + +: Aumentar zoom
        if (modifiers == ModifierKeys.Control && (key == Key.OemPlus || key == Key.Add || key == Key.Oem1 || key == Key.Oem6))
        {
            RunShortcutAction(deferWindowMutations, ZoomIn);
            return true;
        }

        // 10.4 Ctrl + -: Reducir zoom
        if (modifiers == ModifierKeys.Control && (key == Key.OemMinus || key == Key.Subtract || key == Key.OemQuestion || key == Key.OemPeriod))
        {
            RunShortcutAction(deferWindowMutations, ZoomOut);
            return true;
        }

        // 11. Ctrl + 1 a 8: Salto a pestaña por número
        if (modifiers == ModifierKeys.Control && key >= Key.D1 && key <= Key.D8)
        {
            int index = key - Key.D1;
            if (index < Tabs.Count)
            {
                RunShortcutAction(deferWindowMutations, () =>
                {
                    if (index < Tabs.Count) SelectTab(Tabs[index]);
                });
                return true;
            }
        }

        // 12. Ctrl + 9: Salto a la última pestaña
        if (modifiers == ModifierKeys.Control && key == Key.D9)
        {
            if (Tabs.Count > 0)
            {
                RunShortcutAction(deferWindowMutations, () =>
                {
                    if (Tabs.Count > 0) SelectTab(Tabs.Last());
                });
                return true;
            }
        }

        return false;
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
