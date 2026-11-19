using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LexBrowser.Core;
using LexBrowser.Core.AdBlock;
using LexBrowser.Core.Engine;

namespace LexBrowser;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    [DllImport("shell32.dll", SetLastError = true)]
    private static extern void SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string AppID);

    public App()
    {
        // Registrar comportamiento global de ComboBox y ScrollViewer (aislamiento de rueda y auto-cierre sin timers)
        LexBrowser.UI.GlobalScrollBehavior.Initialize();

        DispatcherUnhandledException += (s, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[CRASH PREVENTED] Unhandled exception: {e.Exception}");
            if (ShieldLogger.DiagnosticMode)
            {
                ShieldLogger.Log("UI_DISPATCHER_ERROR", "app", "", "Dispatcher", $"{e.Exception.GetType().Name}: {e.Exception.Message}");
            }
            e.Handled = true; // Previene cierres inesperados por excepciones de UI
        };
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        try
        {
            // Registrar AppID de Windows para que el icono anclado en la barra de tareas sea siempre el oficial
            SetCurrentProcessExplicitAppUserModelID("LexJunior.Browser");
        }
        catch { }

        base.OnStartup(e);

        if (InstallerService.ShouldRunAsSilentUpdate(e.Args))
        {
            try
            {
                await InstallerService.PerformSilentUpdateAndRestartAsync();
            }
            catch { }
            Shutdown();
            return;
        }

        if (InstallerService.ShouldRunAsUninstaller(e.Args))
        {
            var uninstaller = new LexBrowser.UI.Views.InstallerWindow(isUninstallMode: true);
            MainWindow = uninstaller;
            uninstaller.Show();
            return;
        }

        if (InstallerService.ShouldRunAsInstaller(e.Args))
        {
            var installer = new LexBrowser.UI.Views.InstallerWindow(isUninstallMode: false);
            MainWindow = installer;
            installer.Show();
            return;
        }

        // Inicializar JumpList nativo en la barra de tareas de Windows (Tareas: Nueva pestaña, Incógnito, Invitado)
        JumpListManager.Initialize();

        int? waitParentPid = ParseWaitPidArgument(e.Args);
        if (waitParentPid.HasValue)
        {
            await WaitForParentProcessExitAsync(waitParentPid.Value, timeoutMs: 10_000);
        }

        // Extraer comandos o URL si el navegador fue ejecutado con argumentos externos
        string? initialUrl = null;
        bool isIncognitoLaunch = false;
        bool isGuestLaunch = false;
        bool isNewTabLaunch = false;

        if (e.Args != null && e.Args.Length > 0)
        {
            foreach (var arg in e.Args)
            {
                if (string.Equals(arg, "--incognito", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "-incognito", StringComparison.OrdinalIgnoreCase))
                {
                    isIncognitoLaunch = true;
                }
                else if (string.Equals(arg, "--guest", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(arg, "-guest", StringComparison.OrdinalIgnoreCase))
                {
                    isGuestLaunch = true;
                }
                else if (string.Equals(arg, "--new-tab", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(arg, "-new-tab", StringComparison.OrdinalIgnoreCase))
                {
                    isNewTabLaunch = true;
                }
                else if (arg.StartsWith("--wait-pid=", StringComparison.OrdinalIgnoreCase) ||
                         arg.StartsWith("/wait-pid=", StringComparison.OrdinalIgnoreCase))
                {
                    // Ya procesado antes de comprobar instancia única y crear WebView2
                }
                else if (!arg.StartsWith("-") && !arg.StartsWith("/"))
                {
                    initialUrl = arg;
                }
            }
        }

        // Si ya hay una ventana de Lex Junior abierta en el sistema, reenviarle la acción o URL por NamedPipe
        // y terminar esta segunda instancia para evitar procesos o ventanas duplicadas.
        if (!SingleInstanceIpc.TryAcquireSingleInstance())
        {
            string payload = isGuestLaunch ? "CMD:GUEST" :
                             isIncognitoLaunch ? "CMD:INCOGNITO" :
                             isNewTabLaunch ? "CMD:NEW_TAB" :
                             (initialUrl ?? "CMD:NEW_TAB");

            bool sent = await SingleInstanceIpc.SendUrlToExistingInstanceAsync(payload);
            if (!sent)
            {
                await Task.Delay(250);
                sent = await SingleInstanceIpc.SendUrlToExistingInstanceAsync(payload, timeoutMs: 2000);
            }

            if (sent || !SingleInstanceIpc.TryAcquireSingleInstance())
            {
                Shutdown();
                return;
            }
        }

        // Auto-registro en segundo plano en Windows (StartMenuInternet), limpieza de instaladores residuales
        // y precarga asíncrona del historial y la bóveda para eliminar tirones al primer uso.
        _ = Task.Run(() =>
        {
            try { LexBrowser.Core.AdBlock.ShieldRouter.PreloadIfConfigured(); } catch { }
            try { _ = HistoryManager.GetTotalCount(); } catch { }
            try { _ = PasswordManager.Store.GetTotalCount(); } catch { }
            try { InstallerService.RegisterAsDefaultBrowser(); } catch { }
            try { InstallerService.RefreshIconCacheOnceIfVersionChanged(UpdateManager.CurrentVersionString); } catch { }
            try { InstallerService.CleanOldTempInstallers(); } catch { }
        });

        BrowserThemeManager.ApplyTheme(SettingsManager.Current.Theme);

        // Mantener cerrada la compuerta de red de favicons antes de instanciar MainWindow
        // si el arranque requiere bloqueo PIN o elegir perfil inicial (evita fugas antes de elegir Perfil A).
        bool needsLockOrSetupGate = !isGuestLaunch &&
            (SettingsManager.DowngradeDetected ||
             PasswordManager.ShouldRequireUnlockOnStartup() ||
             (!SettingsManager.Current.HasCompletedInitialSecuritySetup &&
              !File.Exists(SettingsManager.SecuritySetupMarkerPath) &&
              !PasswordManager.HasMasterPasswordConfigured()) ||
             LexBrowser.MainWindow.ShouldShowPrivacyOnboardingWindow(SettingsManager.Current, isGuestSession: false, hasInitialAttachedTab: false));

        if (isGuestLaunch || needsLockOrSetupGate)
        {
            FaviconService.SetStartupNetworkGate(false);
        }

        var browserWindow = new MainWindow(
            initialUrl: initialUrl,
            startInIncognito: isIncognitoLaunch,
            startInGuest: isGuestLaunch);
        MainWindow = browserWindow;

        // Avisar si la bóveda no pudo leerse o cifrarse (nunca se degrada a texto plano)
        try
        {
            var vault = PasswordManager.Store;
            vault.VaultError += msg => Dispatcher.BeginInvoke(() =>
                MessageBox.Show(msg, "Bóveda de contraseñas — Lex Junior", MessageBoxButton.OK, MessageBoxImage.Warning));

            if (!string.IsNullOrEmpty(vault.LastError))
            {
                string startupError = vault.LastError!;
                _ = Dispatcher.BeginInvoke(() =>
                    MessageBox.Show(startupError, "Bóveda de contraseñas — Lex Junior", MessageBoxButton.OK, MessageBoxImage.Warning));
            }
        }
        catch { }

        // Iniciar el servidor de escucha IPC para enlaces externos entrantes mientras el navegador esté abierto
        SingleInstanceIpc.StartIpcServer(payload =>
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                bool isGuestCmd = string.Equals(payload, "CMD:GUEST", StringComparison.OrdinalIgnoreCase);
                var openWindows = Windows.OfType<MainWindow>().ToList();

                MainWindow? win = null;
                if (isGuestCmd)
                {
                    win = openWindows.FirstOrDefault(w => w.IsGuestSessionWindow) ?? openWindows.FirstOrDefault();
                }
                else
                {
                    win = openWindows.FirstOrDefault(w => !w.IsGuestSessionWindow && w.IsActive) ??
                          openWindows.FirstOrDefault(w => !w.IsGuestSessionWindow) ??
                          openWindows.FirstOrDefault();
                }

                if (win != null)
                {
                    if (win.WindowState == WindowState.Minimized)
                        win.WindowState = WindowState.Normal;
                    win.Activate();
                    win.Topmost = true;
                    win.Topmost = false;
                    win.Focus();

                    if (!string.IsNullOrWhiteSpace(payload))
                    {
                        _ = win.HandleExternalIpcCommandAsync(payload);
                    }
                }
            });
        });

        browserWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Liberar mutex y servidor IPC
        try { SingleInstanceIpc.Release(); } catch { }

        // Garantiza que la configuración diferida y el historial pendiente lleguen a disco al cerrar
        try { SettingsManager.FlushDeferredSave(); } catch { }
        try { HistoryManager.Flush(); } catch { }
        base.OnExit(e);
    }

    internal static bool IsRestartingProcess { get; private set; }

    internal static int? ParseWaitPidArgument(string[]? args)
    {
        if (args == null || args.Length == 0) return null;

        foreach (string arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg)) continue;
            if (arg.StartsWith("--wait-pid=", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith("/wait-pid=", StringComparison.OrdinalIgnoreCase))
            {
                int eq = arg.IndexOf('=');
                if (eq > 0 && int.TryParse(arg[(eq + 1)..], out int pid) && pid > 0 && pid != Environment.ProcessId)
                {
                    return pid;
                }
            }
        }

        return null;
    }

    internal static async Task WaitForParentProcessExitAsync(int parentPid, int timeoutMs = 10_000, CancellationToken ct = default)
    {
        if (parentPid <= 0 || parentPid == Environment.ProcessId)
            return;

        try
        {
            using var parentProcess = Process.GetProcessById(parentPid);
            if (parentProcess.HasExited)
                return;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(Math.Max(100, timeoutMs));
            await parentProcess.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch { }
    }

    /// <summary>
    /// Reinicia de forma segura el proceso actual de Lex Junior conservando la sesión abierta
    /// y respetando el PIN al volver a arrancar.
    /// </summary>
    public static void RestartBrowserSafely()
    {
        try
        {
            if (DownloadManager.GetActiveSummary().HasActive)
            {
                var confirm = MessageBox.Show(
                    "Hay descargas activas en progreso.\n\nSi reinicias ahora se cancelarán las descargas en curso.\n\n¿Deseas reiniciar Lex Junior de todos modos?",
                    "Descargas en curso — Lex Junior",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (confirm != MessageBoxResult.Yes)
                    return;

                DownloadManager.CancelAllActive();
            }
        }
        catch { }

        IsRestartingProcess = true;

        try
        {
            foreach (Window win in Current.Windows)
            {
                if (win is MainWindow mw)
                {
                    mw.PersistOwnerSessionNowIfEligible();
                }
            }
        }
        catch { }

        try { SettingsManager.FlushDeferredSave(); } catch { }
        try { HistoryManager.Flush(); } catch { }
        try { SingleInstanceIpc.Release(); } catch { }

        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"--wait-pid={Environment.ProcessId}",
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory,
                    UseShellExecute = true
                });
            }
        }
        catch { }

        try { Current?.Shutdown(); } catch { }
    }
}
