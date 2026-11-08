using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using LexBrowser.Core;

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
            e.Handled = true; // Previene cierres inesperados por excepciones de UI
        };
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        try
        {
            // Registrar AppID de Windows para que el icono anclado en la barra de tareas sea siempre el oficial
            SetCurrentProcessExplicitAppUserModelID("LexJunior.Browser");
            InstallerService.RefreshWindowsIconCache();
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
                else if (!arg.StartsWith("-") && !arg.StartsWith("/"))
                {
                    initialUrl = arg;
                }
            }
        }

        // Si ya hay una ventana de Lex Junior abierta en el sistema, reenviarle la acción o URL por NamedPipe
        // y terminar este proceso en ~5ms (evita procesos duplicados y ahorra 100% de memoria RAM).
        if (!SingleInstanceIpc.TryAcquireSingleInstance())
        {
            string payload = isGuestLaunch ? "CMD:GUEST" :
                             isIncognitoLaunch ? "CMD:INCOGNITO" :
                             isNewTabLaunch ? "CMD:NEW_TAB" :
                             (initialUrl ?? "CMD:NEW_TAB");

            await SingleInstanceIpc.SendUrlToExistingInstanceAsync(payload);
            Shutdown();
            return;
        }

        // Auto-registro en segundo plano en Windows (StartMenuInternet), limpieza de instaladores residuales
        // y precarga asíncrona del historial y la bóveda para eliminar tirones al primer uso.
        _ = Task.Run(() =>
        {
            try { _ = HistoryManager.GetTotalCount(); } catch { }
            try { _ = PasswordManager.Store.GetTotalCount(); } catch { }
            try { InstallerService.RegisterAsDefaultBrowser(); } catch { }
            try { InstallerService.CleanOldTempInstallers(); } catch { }
        });

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
}
