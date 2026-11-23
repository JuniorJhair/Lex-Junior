using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LexJuniorPlayer.Models;
using LexJuniorPlayer.Services;
using Microsoft.Win32;

namespace LexJuniorPlayer;

public sealed class AppCardViewModel
{
    public InstalledAppInfo Model { get; }
    public string PackageName => Model.PackageName;
    public string AppName => Model.AppName;
    public string VersionLabel => Model.VersionLabel;
    public string SupportedAbis => Model.SupportedAbis;
    public Visibility HasAbiLabel => string.IsNullOrWhiteSpace(Model.SupportedAbis) ? Visibility.Collapsed : Visibility.Visible;
    public string Monogram => string.IsNullOrWhiteSpace(Model.AppName)
        ? "A"
        : Model.AppName.Trim().Substring(0, 1).ToUpperInvariant();
    public ImageSource? IconBitmap { get; }

    public AppCardViewModel(InstalledAppInfo model)
    {
        Model = model;
        if (!string.IsNullOrWhiteSpace(model.IconPath) && File.Exists(model.IconPath))
        {
            IconBitmap = LoadUnlockedBitmap(model.IconPath);
        }
    }

    private static ImageSource? LoadUnlockedBitmap(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.DecodePixelWidth = 128;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}

public partial class MainWindow : Window
{
    private SdkPaths _sdk;
    private readonly AppLibraryStore _store;
    private readonly AndroidEngineService _engine;
    private readonly EmulatorWindowController _windowController = new();
    private EngineSettings _settings;
    private List<InstalledAppInfo> _apps = new();
    private bool _isBusy;
    private string? _runningSerial;
    private string? _volumeAppliedForSerial;
    private Action? _pendingModalAction;
    private Action? _pendingCancelAction;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _windowWatchTimer;
    private HwndSource? _hwndSource;
    private EmulatorOverlayWindow? _overlayWindow;
    private DateTime _lastFullscreenToggleUtc = DateTime.MinValue;
    private DateTime _fullscreenEnteredUtc = DateTime.MinValue;
    private DateTime _lastAdbVerifyUtc = DateTime.MinValue;
    private EmulatorWindowState _lastWindowState = new(false, false, false, false, false, false, 0, default, default, 96);
    private (bool Open, bool Fullscreen, bool Maximized, bool Busy, bool CanSendKeys, bool HideToolbar, bool AutoFullscreen)? _lastButtonUiState;
    private bool _pendingAutoFullscreenOnLaunch;
    private bool _isPasting;
    private bool _shutdownStarted;
    private bool _allowClose;
    private bool _hooksCleaned;
    private IntPtr _fgHook;
    private WinEventDelegate? _fgHookProc;
    private IntPtr _locHook;
    private WinEventDelegate? _locHookProc;
    private uint _locHookPid;
    private bool _locUpdateQueued;

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    public MainWindow()
    {
        InitializeComponent();
        _store = new AppLibraryStore();
        _settings = _store.LoadSettings();
        _sdk = SdkLocator.Discover(_settings.CustomSdkPath, _settings.SelectedSystemImageRelPath);

        var settingsMigrated = AppLibraryStore.ApplyOneTimeHostTuning(
            _settings,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            Environment.ProcessorCount);

        var (resW, resH, resDensity) = EmulatorWindowController.ResolveGuestResolution(_settings);
        var resolutionAdjusted = _settings.ResolutionWidth != resW ||
                                 _settings.ResolutionHeight != resH ||
                                 _settings.Density != resDensity;
        if (resolutionAdjusted)
        {
            _settings.ResolutionWidth = resW;
            _settings.ResolutionHeight = resH;
            _settings.Density = resDensity;
        }

        if (settingsMigrated || resolutionAdjusted)
        {
            _store.SaveSettings(_settings);
            AvdProvisioner.EnsureCleanAvd(_sdk, _settings, out _);
        }

        _engine = new AndroidEngineService(_sdk, _store);
        _engine.BackgroundStatusUpdated += msg =>
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (!_isBusy) FooterStatusText.Text = msg;
            });
        };

        var wasUncleanShutdown = false;
        if (File.Exists(_store.UncleanFlagPath))
        {
            wasUncleanShutdown = true;
            var avdDir = AvdProvisioner.GetAvdDirectory(_sdk, _settings.AvdName);
            AvdProvisioner.CleanupDeadAvdLocks(avdDir);
            AvdProvisioner.InvalidateQuickBootSnapshot(_sdk, _settings.AvdName);
            _engine.RequestColdBootOnNextStart();
            try { File.Delete(_store.UncleanFlagPath); } catch { }
        }

        UpdateSdkSummary();
        ReloadAppsList();
        UpdateEmulatorControlButtons();

        if (wasUncleanShutdown)
        {
            FooterStatusText.Text = "Cierre anterior interrumpido; el próximo inicio hará un arranque completo limpio.";
        }
        else if (settingsMigrated)
        {
            FooterStatusText.Text = $"Ajustes de rendimiento actualizados ({_settings.RamMb} MB RAM · {_settings.CpuCores} núcleos). El próximo inicio hará un arranque completo único.";
        }

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += async (_, _) => await RefreshEngineStatusAsync(forceAdbCheck: false);
        _statusTimer.Start();

        _windowWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _windowWatchTimer.Tick += (_, _) => EnforceEmulatorWindowControls();
        _windowWatchTimer.Start();

        PreviewKeyDown += MainWindow_PreviewKeyDown;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        _hwndSource = HwndSource.FromHwnd(helper.Handle);
        _hwndSource?.AddHook(WndProc);

        _fgHookProc = OnForegroundWindowChanged;
        _fgHook = SetWinEventHook(0x0003, 0x0003, IntPtr.Zero, _fgHookProc, 0, 0, 0x0000); // EVENT_SYSTEM_FOREGROUND | WINEVENT_OUTOFCONTEXT

        await RefreshEngineStatusAsync();
        EnforceEmulatorWindowControls();

        if (!string.IsNullOrWhiteSpace(App.StartupApkArg))
        {
            await HandleApkSelectionAsync(App.StartupApkArg);
            return;
        }

        if (!_sdk.HasSystemImage || string.IsNullOrEmpty(_sdk.EmulatorExe) || string.IsNullOrEmpty(_sdk.AdbExe))
        {
            FooterStatusText.Text = "Faltan componentes del Android SDK; abriendo verificador de requisitos...";
            OpenPrerequisitesWizard();
        }

        if (!string.IsNullOrWhiteSpace(_settings.AutoLaunchPackage) &&
            _sdk.HasSystemImage && !string.IsNullOrEmpty(_sdk.EmulatorExe) && !string.IsNullOrEmpty(_sdk.AdbExe))
        {
            var autoApp = _apps.FirstOrDefault(a =>
                string.Equals(a.PackageName, _settings.AutoLaunchPackage, StringComparison.OrdinalIgnoreCase));
            if (autoApp != null)
            {
                await ExecuteLaunchAppAsync(autoApp);
            }
        }
    }

    private void CleanupWindowHooksAndOverlay()
    {
        if (_hooksCleaned) return;
        _hooksCleaned = true;

        _engine.CancelAllBackgroundCompiles();
        _statusTimer.Stop();
        _windowWatchTimer.Stop();
        if (_fgHook != IntPtr.Zero)
        {
            UnhookWinEvent(_fgHook);
            _fgHook = IntPtr.Zero;
            _fgHookProc = null;
        }
        UnhookEmulatorLocationEvent();
        if (_overlayWindow != null)
        {
            try { _overlayWindow.Close(); } catch { }
            _overlayWindow = null;
        }
        if (_hwndSource != null)
        {
            _windowController.UnregisterHotkey(_hwndSource.Handle);
            _hwndSource.RemoveHook(WndProc);
        }
    }

    public void HandleSessionEndingFastShutdown()
    {
        var avdDir = AvdProvisioner.GetAvdDirectory(_sdk, _settings.AvdName);
        var hasLiveEngine = _runningSerial != null ||
                            _engine.CachedSerial != null ||
                            (!string.IsNullOrEmpty(avdDir) && AndroidEngineService.HasAnyLiveAvdProcess(avdDir));
        if (!hasLiveEngine)
            return;

        try
        {
            File.WriteAllText(_store.UncleanFlagPath, DateTime.UtcNow.ToString("O"));
        }
        catch { }

        if (string.IsNullOrEmpty(_sdk.AdbExe))
            return;

        var sw = Stopwatch.StartNew();
        try
        {
            var serial = _runningSerial ?? _engine.CachedSerial;
            var args = string.IsNullOrWhiteSpace(serial) ? "emu kill" : $"-s {serial} emu kill";
            var psi = new ProcessStartInfo(_sdk.AdbExe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var remaining = Math.Max(200, 4000 - (int)sw.ElapsedMilliseconds);
                if (!proc.WaitForExit(remaining))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                }
            }
        }
        catch { }

        while (sw.ElapsedMilliseconds < 4000 && AndroidEngineService.HasAnyLiveAvdProcess(avdDir))
        {
            Thread.Sleep(100);
        }
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;
        if (App.IsSessionEnding)
        {
            _allowClose = true;
            CleanupWindowHooksAndOverlay();
            return;
        }

        var avdDir = AvdProvisioner.GetAvdDirectory(_sdk, _settings.AvdName);
        var hasLiveEngine = _runningSerial != null ||
                            (!string.IsNullOrEmpty(avdDir) && AndroidEngineService.HasAnyLiveAvdProcess(avdDir));

        if (_settings.ShutdownEngineOnExit && hasLiveEngine)
        {
            e.Cancel = true;
            if (_shutdownStarted) return;
            _shutdownStarted = true;

            App.SignalShuttingDown();
            CleanupWindowHooksAndOverlay();
            Hide();

            try
            {
                await _engine.StopEngineAsync(_settings.AvdName);
            }
            catch { }

            _allowClose = true;
            Application.Current.Shutdown();
            return;
        }

        App.SignalShuttingDown();
        _allowClose = true;
        CleanupWindowHooksAndOverlay();
        Application.Current.Shutdown();
    }

    private void OnForegroundWindowChanged(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        Dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
        {
            var myHwnd = _hwndSource?.Handle ?? IntPtr.Zero;
            _windowController.UpdateScopedF11Hotkey(myHwnd, _overlayWindow?.NativeHandle ?? IntPtr.Zero);
            if (_lastWindowState.IsEmulatorWindowOpen)
            {
                UpdateOverlayWindow();
            }
            else
            {
                EnforceEmulatorWindowControls(forceScan: false);
            }
        });
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == EmulatorWindowController.WmHotkey && wParam.ToInt32() == EmulatorWindowController.HotkeyIdF11)
        {
            ToggleEmulatorFullscreen(fromHotkey: true);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleEmulatorFullscreen(fromHotkey: true);
            e.Handled = true;
        }
    }

    private void EnsureEmulatorLocationEventHook(uint emulatorPid)
    {
        if (emulatorPid == 0)
        {
            UnhookEmulatorLocationEvent();
            return;
        }

        if (_locHook != IntPtr.Zero && _locHookPid == emulatorPid)
            return;

        UnhookEmulatorLocationEvent();
        _locHookProc = OnEmulatorWindowLocationChanged;
        // EVENT_OBJECT_LOCATIONCHANGE = 0x800B (WINEVENT_OUTOFCONTEXT = 0x0000) scoped to emulator process
        _locHook = SetWinEventHook(0x800B, 0x800B, IntPtr.Zero, _locHookProc, emulatorPid, 0, 0x0000);
        _locHookPid = _locHook != IntPtr.Zero ? emulatorPid : 0;
    }

    private void UnhookEmulatorLocationEvent()
    {
        if (_locHook != IntPtr.Zero)
        {
            UnhookWinEvent(_locHook);
            _locHook = IntPtr.Zero;
        }
        _locHookProc = null;
        _locHookPid = 0;
    }

    private void OnEmulatorWindowLocationChanged(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished || idObject != 0 || idChild != 0)
            return;
        if (hwnd == IntPtr.Zero || hwnd != _windowController.MainEmulatorHwnd)
            return;
        if (_locUpdateQueued)
            return;

        _locUpdateQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _locUpdateQueued = false;
            if (_hooksCleaned) return;
            _lastWindowState = _windowController.RefreshAndEnforce(_settings.AvdName, _settings.HideEmulatorToolbar, forceScan: false);
            UpdateOverlayWindow();
        });
    }

    private void EnforceEmulatorWindowControls(bool forceScan = false)
    {
        _lastWindowState = _windowController.RefreshAndEnforce(_settings.AvdName, _settings.HideEmulatorToolbar, forceScan);
        EnsureEmulatorLocationEventHook(_lastWindowState.IsEmulatorWindowOpen ? _lastWindowState.EmulatorProcessId : 0);

        // High frequency (120 ms) is only needed while fullscreen for smooth top-edge cursor reveal;
        // windowed mode follows instant move/resize events via EVENT_OBJECT_LOCATIONCHANGE so 600 ms suffices.
        var targetWatchInterval = !_lastWindowState.IsEmulatorWindowOpen
            ? TimeSpan.FromMilliseconds(2500)
            : (_lastWindowState.IsFullscreen ? TimeSpan.FromMilliseconds(120) : TimeSpan.FromMilliseconds(600));
        if (_windowWatchTimer.Interval != targetWatchInterval)
        {
            _windowWatchTimer.Interval = targetWatchInterval;
        }

        var hwnd = _hwndSource?.Handle ?? IntPtr.Zero;
        _windowController.UpdateScopedF11Hotkey(hwnd, _overlayWindow?.NativeHandle ?? IntPtr.Zero);
        UpdateOverlayWindow();
        UpdateEmulatorControlButtons();
    }

    private void HideOverlayIfVisible()
    {
        if (_overlayWindow is { IsVisible: true })
        {
            _overlayWindow.Hide();
        }
    }

    private void UpdateOverlayWindow()
    {
        if (!_lastWindowState.IsEmulatorWindowOpen || _lastWindowState.IsMinimized ||
            (_pendingAutoFullscreenOnLaunch && !_lastWindowState.IsFullscreen))
        {
            HideOverlayIfVisible();
            return;
        }

        if (_overlayWindow == null)
        {
            _overlayWindow = new EmulatorOverlayWindow();
            _overlayWindow.FullscreenRequested += () => ToggleEmulatorFullscreen(fromHotkey: false);
            _overlayWindow.SidebarToggleRequested += ToggleSidebarCore;
            _overlayWindow.AndroidBackRequested += () => _ = SendAndroidKeyAsync(4);
            _overlayWindow.PasteRequested += () => _ = PasteClipboardToAndroidAsync();
        }

        var isOurFg = _windowController.IsOurSuiteForeground(_hwndSource?.Handle ?? IntPtr.Zero, _overlayWindow.NativeHandle);
        if (_lastWindowState.IsFullscreen)
        {
            var withinHintWindow = (DateTime.UtcNow - _fullscreenEnteredUtc).TotalSeconds < 2.0;
            var shouldShow = _windowController.ShouldShowFullscreenOverlay(
                _overlayWindow.IsVisible,
                isOurFg,
                withinHintWindow,
                _overlayWindow.IsMouseOver,
                _lastWindowState);
            if (!shouldShow)
            {
                HideOverlayIfVisible();
                return;
            }
        }
        else if (!isOurFg && !_overlayWindow.IsMouseOver &&
                 !_windowController.IsWindowedTitleBarVisibleOnScreen(_lastWindowState))
        {
            HideOverlayIfVisible();
            return;
        }

        _overlayWindow.UpdatePositionAndLabels(_lastWindowState, _settings.HideEmulatorToolbar);
        var becameVisible = false;
        if (!_overlayWindow.IsVisible)
        {
            _overlayWindow.Show();
            becameVisible = true;
        }
        EmulatorWindowController.EnsureOverlayAboveEmulator(_overlayWindow.NativeHandle, forcePlace: becameVisible);
    }

    private void UpdateEmulatorControlButtons()
    {
        var canSendKeys = !_isBusy && _runningSerial != null;
        var nextState = (
            _lastWindowState.IsEmulatorWindowOpen,
            _lastWindowState.IsFullscreen,
            _lastWindowState.IsMaximizedFit,
            _isBusy,
            canSendKeys,
            _settings.HideEmulatorToolbar,
            _settings.StartFullscreenOnLaunch);

        if (_lastButtonUiState == nextState)
            return;

        _lastButtonUiState = nextState;

        ToggleSidebarBtn.Content = _settings.HideEmulatorToolbar
            ? "◫ Barra derecha: Oculta"
            : "◫ Barra derecha: Visible";

        AutoFullscreenToggleBtn.Content = _settings.StartFullscreenOnLaunch
            ? "⛶ Auto pantalla completa: SÍ"
            : "⛶ Auto pantalla completa: NO";

        FullscreenBtn.IsEnabled = _lastWindowState.IsEmulatorWindowOpen;
        FullscreenBtn.Content = _lastWindowState.IsFullscreen
            ? "↙ Ventana normal (F11)"
            : "⛶ Pantalla completa (F11)";

        MaximizeEmuBtn.IsEnabled = _lastWindowState.IsEmulatorWindowOpen;
        MaximizeEmuBtn.Content = _lastWindowState.IsMaximizedFit
            ? "🗗 Restaurar ventana"
            : "🗖 Ventana grande";

        AndroidBackBtn.IsEnabled = canSendKeys;
        AndroidHomeBtn.IsEnabled = canSendKeys;
    }

    private void ToggleEmulatorFullscreen(bool fromHotkey = false)
    {
        var now = DateTime.UtcNow;
        if (fromHotkey && !EmulatorWindowController.ShouldAcceptHotkeyToggle(now, _lastFullscreenToggleUtc))
            return;

        _lastFullscreenToggleUtc = now;

        if (!_windowController.ToggleFullscreen(_settings.AvdName, _settings.HideEmulatorToolbar))
            return;

        if (_windowController.IsFullscreen)
            _fullscreenEnteredUtc = now;

        EnforceEmulatorWindowControls(forceScan: true);
        FooterStatusText.Text = _lastWindowState.IsFullscreen
            ? "Pantalla completa activada (pulsa F11 o mueve el mouse arriba para salir)."
            : "Modo ventana restaurado.";
    }

    private void UpdateSdkSummary()
    {
        if (!_sdk.HasSystemImage || string.IsNullOrEmpty(_sdk.EmulatorExe) || string.IsNullOrEmpty(_sdk.AdbExe))
        {
            SdkReadyText.Text = "⚠ Faltan requisitos del SDK — pulsa '🩺 Requisitos'";
            SdkReadyText.Foreground = Brushes.Orange;
        }
        else
        {
            var apiText = _sdk.ActiveSystemImage != null ? $"API {_sdk.ActiveSystemImage.ApiLevel}" : "Android";
            SdkReadyText.Text = $"{apiText} · {_settings.ResolutionWidth}×{_settings.ResolutionHeight} · {_settings.RamMb} MB · GPU:{_settings.GpuMode}";
            SdkReadyText.Foreground = (Brush)FindResource("TextSecondary");
        }
    }

    private void ReloadAppsList()
    {
        _apps = _store.LoadApps();
        AppsListControl.ItemsSource = _apps.Select(a => new AppCardViewModel(a)).ToList();
        AppCountText.Text = _apps.Count == 1 ? "1 app" : $"{_apps.Count} apps";
        EmptyStateCard.Visibility = _apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RefreshEngineStatusAsync(bool forceAdbCheck = true)
    {
        if (_isBusy) return;
        if (!forceAdbCheck && _runningSerial == null && !AnyEmulatorHostProcessRunning())
        {
            var idleInterval = TimeSpan.FromSeconds(15);
            if (_statusTimer.Interval != idleInterval) _statusTimer.Interval = idleInterval;
            return;
        }
        if (!forceAdbCheck &&
            _runningSerial != null &&
            _windowController.IsEmulatorProcessAlive() &&
            (DateTime.UtcNow - _lastAdbVerifyUtc).TotalSeconds < 20)
        {
            return;
        }

        try
        {
            _lastAdbVerifyUtc = DateTime.UtcNow;
            var serial = await _engine.FindRunningAvdSerialAsync(_settings.AvdName);
            _runningSerial = serial;
            if (serial != null)
            {
                StatusDot.Fill = (Brush)FindResource("AccentGreen");
                StatusPillText.Text = $"Motor activo ({serial})";
                PowerBtn.IsEnabled = true;

                if (!string.Equals(_volumeAppliedForSerial, serial, StringComparison.Ordinal))
                {
                    _volumeAppliedForSerial = serial;
                    _ = _engine.EnsureMaxMediaVolumeAsync(serial);
                }
            }
            else
            {
                _volumeAppliedForSerial = null;
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(100, 116, 139));
                StatusPillText.Text = "Motor detenido (0 MB RAM)";
                PowerBtn.IsEnabled = false;
            }
            UpdateEmulatorControlButtons();
            var targetInterval = _runningSerial != null ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(15);
            if (_statusTimer.Interval != targetInterval) _statusTimer.Interval = targetInterval;
        }
        catch { }
    }

    private static bool AnyEmulatorHostProcessRunning()
    {
        foreach (var name in new[] { "qemu-system-x86_64", "emulator" })
        {
            var procs = Process.GetProcessesByName(name);
            try { if (procs.Length > 0) return true; }
            finally { foreach (var p in procs) p.Dispose(); }
        }
        return false;
    }

    private void SetBusy(bool busy, string message)
    {
        _isBusy = busy;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        FooterStatusText.Text = message;
        BrowseApkBtn.IsEnabled = !busy;
        SyncBtn.IsEnabled = !busy;
        SettingsBtn.IsEnabled = !busy;
        if (busy)
        {
            StatusDot.Fill = (Brush)FindResource("AccentCyan");
            StatusPillText.Text = "Procesando...";
            PowerBtn.IsEnabled = false;
        }
        UpdateEmulatorControlButtons();
    }

    private void BrowseApkBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Seleccionar paquete Android (APK / XAPK / APKS / APKM)",
            Filter = "Paquetes Android (*.apk;*.xapk;*.apks;*.apkm)|*.apk;*.xapk;*.apks;*.apkm|Todos los archivos (*.*)|*.*",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            _ = HandleApkSelectionAsync(dialog.FileName);
        }
    }

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        if (GetFirstApkFromDrop(e) != null)
        {
            e.Effects = DragDropEffects.Copy;
            DropZoneBorder.BorderBrush = (Brush)FindResource("AccentCyan");
            DropZoneBorder.Background = new SolidColorBrush(Color.FromRgb(16, 30, 50));
            DropHintText.Text = "Suelta el archivo (.apk, .xapk, .apks o .apkm) aquí para instalarlo / actualizarlo.";
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        ResetDropZoneStyle();
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        ResetDropZoneStyle();
        var apk = GetFirstApkFromDrop(e);
        if (apk != null)
        {
            _ = HandleApkSelectionAsync(apk);
        }
    }

    private void ResetDropZoneStyle()
    {
        DropZoneBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(38, 52, 80));
        DropZoneBorder.Background = new SolidColorBrush(Color.FromRgb(16, 24, 39));
        DropHintText.Text = "Arrastra cualquier archivo .apk, .xapk, .apks o .apkm a esta ventana (solo optimizado para apps de streaming; si ya está instalada, actualiza conservando datos).";
    }

    private static string? GetFirstApkFromDrop(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        return files?.FirstOrDefault(f => ApkBundleExtractor.IsSupportedPackageFile(f) && File.Exists(f));
    }

    private async Task HandleApkSelectionAsync(string apkPath)
    {
        if (_isBusy) return;
        SetBusy(true, $"Analizando {Path.GetFileName(apkPath)}...");
        BundleInstallPlan? plan = null;
        try
        {
            var guestAbis = _sdk.ActiveSystemImage?.SupportedAbis;
            plan = await ApkBundleExtractor.PreparePackageAsync(apkPath, _sdk.Aapt2Exe, guestAbis);
        }
        finally
        {
            SetBusy(false, "Listo.");
            await RefreshEngineStatusAsync();
        }

        if (plan == null)
        {
            ShowAlertModal("Archivo inválido", "No se pudo leer el paquete seleccionado (.apk / .xapk / .apks / .apkm).");
            return;
        }

        var meta = plan.PrimaryMetadata;
        var existing = _apps.FirstOrDefault(a =>
            string.Equals(a.PackageName, meta.PackageName, StringComparison.OrdinalIgnoreCase));

        string splitTag = plan.IsSplitBundle
            ? $"\nPaquete dividido: {plan.ApkPathsToInstall.Count} módulos" + (plan.ObbSourceDir != null ? " + datos OBB" : "")
            : string.Empty;

        string title;
        string actionLabel;
        string detail;
        if (existing == null)
        {
            title = $"Instalar {meta.AppName}";
            actionLabel = "Instalar ahora";
            detail = $"Paquete: {meta.PackageName}\n" +
                     $"Versión: {(string.IsNullOrWhiteSpace(meta.VersionName) ? $"#{meta.VersionCode}" : $"v{meta.VersionName} (#{meta.VersionCode})")}\n" +
                     $"Arquitectura: {meta.SupportedAbis}{splitTag}\n\n" +
                     "Se instalará limpio dentro de Lex Junior Player.";
        }
        else if (meta.VersionCode > existing.VersionCode && existing.VersionCode > 0)
        {
            title = $"Actualizar {meta.AppName}";
            actionLabel = "Actualizar versión";
            detail = $"Versión actual: {existing.VersionLabel} (#{existing.VersionCode})\n" +
                     $"Nueva versión: v{meta.VersionName} (#{meta.VersionCode})\n" +
                     $"Paquete: {meta.PackageName}{splitTag}\n\n" +
                     "Se actualizará conservando tus cuentas y datos guardados.";
        }
        else
        {
            title = $"Reinstalar / Cambiar versión de {meta.AppName}";
            actionLabel = "Reinstalar";
            detail = $"Versión instalada: {existing.VersionLabel} (#{existing.VersionCode})\n" +
                     $"Archivo elegido: v{meta.VersionName} (#{meta.VersionCode})\n" +
                     $"Paquete: {meta.PackageName}{splitTag}";
        }

        ShowConfirmModal(
            title,
            detail,
            actionLabel,
            isDanger: false,
            onConfirm: () => _ = ExecuteInstallPlanAsync(plan),
            onCancel: () => ApkBundleExtractor.CleanupTempDir(plan.TempExtractDir));
    }

    private async Task ExecuteInstallPlanAsync(BundleInstallPlan plan)
    {
        if (_isBusy)
        {
            ApkBundleExtractor.CleanupTempDir(plan.TempExtractDir);
            return;
        }
        var meta = plan.PrimaryMetadata;
        SetBusy(true, $"Preparando instalación de {meta.AppName}...");
        try
        {
            var installed = await _engine.InstallPackageBundleAsync(
                plan,
                _settings,
                msg => Dispatcher.Invoke(() => FooterStatusText.Text = msg)
            );
            ReloadAppsList();
            SetBusy(false, $"✓ {installed.AppName} lista. Puedes pulsar '▶ Abrir' ahora (optimización en segundo plano).");
            await RefreshEngineStatusAsync();
        }
        catch (Exception ex)
        {
            SetBusy(false, "Error al instalar paquete.");
            await RefreshEngineStatusAsync();
            ShowAlertModal("No se pudo instalar el paquete", ex.Message);
        }
    }

    private void LaunchApp_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        if ((sender as FrameworkElement)?.Tag is AppCardViewModel vm)
        {
            _ = ExecuteLaunchAppAsync(vm.Model);
        }
    }

    private async Task ExecuteLaunchAppAsync(InstalledAppInfo app)
    {
        if (_isBusy) return;
        SetBusy(true, $"Iniciando {app.AppName}...");
        _pendingAutoFullscreenOnLaunch = _settings.StartFullscreenOnLaunch;
        try
        {
            await _engine.LaunchAppAsync(
                app,
                _settings,
                msg => Dispatcher.Invoke(() => FooterStatusText.Text = msg)
            );
            ReloadAppsList();
            SetBusy(false, $"▶ {app.AppName} abierta en Lex Junior Player (pulsa '⛶ Pantalla completa' arriba o F11).");
            await RefreshEngineStatusAsync();
            if (_settings.StartFullscreenOnLaunch)
            {
                _lastWindowState = _windowController.RefreshAndEnforce(_settings.AvdName, _settings.HideEmulatorToolbar, forceScan: true);
                if (_lastWindowState.IsEmulatorWindowOpen)
                {
                    await Task.Delay(250);
                }
                _fullscreenEnteredUtc = DateTime.UtcNow;
                _windowController.SetFullscreen(_settings.AvdName, true, _settings.HideEmulatorToolbar);
                _pendingAutoFullscreenOnLaunch = false;
                EnforceEmulatorWindowControls(forceScan: true);
            }
            else
            {
                _pendingAutoFullscreenOnLaunch = false;
                EnforceEmulatorWindowControls(forceScan: true);
                _windowController.FocusEmulatorWindow();
            }
        }
        catch (Exception ex)
        {
            SetBusy(false, "Error al abrir la aplicación.");
            await RefreshEngineStatusAsync();
            ShowAlertModal("No se pudo abrir la aplicación", ex.Message);
        }
        finally
        {
            _pendingAutoFullscreenOnLaunch = false;
        }
    }

    private void UpdateApp_Click(object sender, RoutedEventArgs e)
    {
        BrowseApkBtn_Click(sender, e);
    }

    private void UninstallApp_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        if ((sender as FrameworkElement)?.Tag is AppCardViewModel vm)
        {
            ShowConfirmModal(
                $"Desinstalar {vm.AppName}",
                $"Se eliminará {vm.AppName} ({vm.PackageName}) y sus datos del emulador limpio.",
                "Desinstalar",
                isDanger: true,
                () => _ = ExecuteUninstallAppAsync(vm.Model)
            );
        }
    }

    private async Task ExecuteUninstallAppAsync(InstalledAppInfo app)
    {
        if (_isBusy) return;
        SetBusy(true, $"Desinstalando {app.AppName}...");
        try
        {
            await _engine.UninstallAppAsync(
                app.PackageName,
                _settings,
                msg => Dispatcher.Invoke(() => FooterStatusText.Text = msg)
            );
            ReloadAppsList();
            SetBusy(false, $"✓ {app.AppName} desinstalada.");
            await RefreshEngineStatusAsync();
        }
        catch (Exception ex)
        {
            SetBusy(false, "Error al desinstalar.");
            await RefreshEngineStatusAsync();
            ShowAlertModal("No se pudo desinstalar", ex.Message);
        }
    }

    private async void SyncBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        SetBusy(true, "Sincronizando apps instaladas...");
        try
        {
            var serial = await _engine.EnsureEngineRunningAsync(
                _settings,
                msg => Dispatcher.Invoke(() => FooterStatusText.Text = msg)
            );
            await _engine.SyncInstalledAppsWithRunningEngineAsync(serial);
            ReloadAppsList();
            SetBusy(false, "✓ Lista sincronizada con el motor Android.");
            await RefreshEngineStatusAsync();
        }
        catch (Exception ex)
        {
            SetBusy(false, "No se pudo sincronizar.");
            await RefreshEngineStatusAsync();
            ShowAlertModal("Error de sincronización", ex.Message);
        }
    }

    private async void PowerBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        SetBusy(true, "Apagando motor Android y guardando estado...");
        try
        {
            await _engine.StopEngineAsync(_settings.AvdName);
            SetBusy(false, "Motor apagado (RAM liberada).");
            await RefreshEngineStatusAsync();
        }
        catch (Exception ex)
        {
            SetBusy(false, ex.Message);
            await RefreshEngineStatusAsync();
        }
    }

    private void PrereqsBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        OpenPrerequisitesWizard();
    }

    private void OpenPrerequisitesWizard()
    {
        var wizard = new SetupWizardWindow(isInstallerMode: false, isUninstallMode: false, shutdownOnFinish: false)
        {
            Owner = this
        };
        wizard.ShowDialog();
        _settings = _store.LoadSettings();
        _sdk = SdkLocator.Discover(_settings.CustomSdkPath, _settings.SelectedSystemImageRelPath);
        _engine.UpdateSdk(_sdk);
        AvdProvisioner.EnsureCleanAvd(_sdk, _settings, out _);
        UpdateSdkSummary();
        _ = RefreshEngineStatusAsync();
    }

    private void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        var prevSdk = _settings.CustomSdkPath;
        var prevImg = _settings.SelectedSystemImageRelPath;
        var win = new SettingsWindow(_settings, _apps) { Owner = this };
        if (win.ShowDialog() == true)
        {
            _settings = win.ResultSettings;
            _store.SaveSettings(_settings);
            _sdk = SdkLocator.Discover(_settings.CustomSdkPath, _settings.SelectedSystemImageRelPath);
            _engine.UpdateSdk(_sdk);
            if (!string.Equals(prevSdk, _settings.CustomSdkPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(prevImg, _settings.SelectedSystemImageRelPath, StringComparison.OrdinalIgnoreCase))
            {
                AvdProvisioner.InvalidateQuickBootSnapshot(_sdk, _settings.AvdName);
                _engine.RequestColdBootOnNextStart();
            }
            AvdProvisioner.EnsureCleanAvd(_sdk, _settings, out _);
            UpdateSdkSummary();
            EnforceEmulatorWindowControls();
            FooterStatusText.Text = "Ajustes guardados. La visibilidad de la barra derecha se aplicó al instante; cambios de imagen/hardware aplican al reiniciar el motor.";
        }
    }

    private void ToggleSidebarBtn_Click(object sender, RoutedEventArgs e) => ToggleSidebarCore();

    private void ToggleSidebarCore()
    {
        _settings.HideEmulatorToolbar = !_settings.HideEmulatorToolbar;
        _store.SaveSettings(_settings);
        EnforceEmulatorWindowControls(forceScan: true);
        FooterStatusText.Text = _settings.HideEmulatorToolbar
            ? "Barra lateral flotante ocultada automáticamente."
            : "Barra lateral flotante visible.";
    }

    private void AutoFullscreenToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        _settings.StartFullscreenOnLaunch = !_settings.StartFullscreenOnLaunch;
        _store.SaveSettings(_settings);
        UpdateEmulatorControlButtons();
        FooterStatusText.Text = _settings.StartFullscreenOnLaunch
            ? "Las aplicaciones ahora se abrirán directamente en Pantalla completa."
            : "Las aplicaciones se abrirán en modo ventana normal.";
    }

    private void FullscreenBtn_Click(object sender, RoutedEventArgs e) => ToggleEmulatorFullscreen(fromHotkey: false);

    private void MaximizeEmuBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_windowController.ToggleMaximizeWorkArea(_settings.AvdName, _settings.HideEmulatorToolbar))
        {
            EnforceEmulatorWindowControls();
        }
    }

    private async void AndroidBackBtn_Click(object sender, RoutedEventArgs e) => await SendAndroidKeyAsync(4);

    private async void AndroidHomeBtn_Click(object sender, RoutedEventArgs e) => await SendAndroidKeyAsync(3);

    private async Task SendAndroidKeyAsync(int keyCode)
    {
        if (_runningSerial == null || _isBusy) return;
        try
        {
            await _engine.SendKeyEventAsync(_runningSerial, keyCode);
            _windowController.FocusEmulatorWindow();
        }
        catch { }
    }

    private async Task PasteClipboardToAndroidAsync()
    {
        if (_runningSerial == null || _isBusy || _isPasting) return;
        string text;
        try
        {
            text = Clipboard.GetText();
        }
        catch
        {
            return;
        }

        if (string.IsNullOrEmpty(text))
        {
            FooterStatusText.Text = "El portapapeles está vacío.";
            return;
        }

        _isPasting = true;
        try
        {
            var allAscii = await _engine.PasteTextAsync(_runningSerial, text);
            FooterStatusText.Text = allAscii
                ? "Texto pegado en Android."
                : "Algunos caracteres no ASCII no se pudieron pegar";
            _windowController.FocusEmulatorWindow();
        }
        catch
        {
            FooterStatusText.Text = "No se pudo pegar texto en Android.";
        }
        finally
        {
            _isPasting = false;
        }
    }

    private void ShowConfirmModal(string title, string body, string confirmText, bool isDanger, Action onConfirm, Action? onCancel = null)
    {
        ModalTitleText.Text = title;
        ModalBodyText.Text = body;
        ModalConfirmBtn.Content = confirmText;
        ModalConfirmBtn.Style = (Style)FindResource(isDanger ? "DangerBtn" : "PrimaryBtn");
        ModalCancelBtn.Visibility = Visibility.Visible;
        _pendingModalAction = onConfirm;
        _pendingCancelAction = onCancel;
        ModalOverlay.Visibility = Visibility.Visible;
    }

    private void ShowAlertModal(string title, string body)
    {
        ModalTitleText.Text = title;
        ModalBodyText.Text = body;
        ModalConfirmBtn.Content = "Entendido";
        ModalConfirmBtn.Style = (Style)FindResource("PrimaryBtn");
        ModalCancelBtn.Visibility = Visibility.Collapsed;
        _pendingModalAction = null;
        _pendingCancelAction = null;
        ModalOverlay.Visibility = Visibility.Visible;
    }

    private void ModalConfirm_Click(object sender, RoutedEventArgs e)
    {
        var act = _pendingModalAction;
        _pendingModalAction = null;
        _pendingCancelAction = null;
        ModalOverlay.Visibility = Visibility.Collapsed;
        act?.Invoke();
    }

    private void ModalCancel_Click(object sender, RoutedEventArgs e)
    {
        var cancelAct = _pendingCancelAction;
        _pendingModalAction = null;
        _pendingCancelAction = null;
        ModalOverlay.Visibility = Visibility.Collapsed;
        cancelAct?.Invoke();
    }

    private void MinimizeBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeBtn_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}
