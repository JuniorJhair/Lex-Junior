using System;
using System.IO;
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

        // Extraer URL o ruta de archivo si el navegador fue ejecutado con argumentos externos
        string? initialUrl = null;
        if (e.Args != null && e.Args.Length > 0)
        {
            foreach (var arg in e.Args)
            {
                if (string.IsNullOrWhiteSpace(arg) || arg.StartsWith("-") || arg.StartsWith("/")) continue;
                initialUrl = arg;
                break;
            }
        }

        // Si ya hay una ventana de Lex Junior abierta en el sistema, reenviarle la URL por NamedPipe
        // y terminar este proceso en ~5ms (evita procesos duplicados y ahorra 100% de memoria RAM).
        if (!SingleInstanceIpc.TryAcquireSingleInstance())
        {
            await SingleInstanceIpc.SendUrlToExistingInstanceAsync(initialUrl);
            Shutdown();
            return;
        }

        // Auto-registro en segundo plano en Windows (StartMenuInternet) para que siempre
        // figure de inmediato en la lista oficial de "Navegadores web" de Windows.
        _ = Task.Run(() =>
        {
            try { InstallerService.RegisterAsDefaultBrowser(); } catch { }
        });

        var browserWindow = new MainWindow(initialUrl);
        MainWindow = browserWindow;

        // Iniciar el servidor de escucha IPC para enlaces externos entrantes mientras el navegador esté abierto
        SingleInstanceIpc.StartIpcServer(url =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (MainWindow is MainWindow win)
                {
                    if (win.WindowState == WindowState.Minimized)
                        win.WindowState = WindowState.Normal;
                    win.Activate();
                    win.Topmost = true;
                    win.Topmost = false;
                    win.Focus();

                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        _ = win.AddNewTabFromExternalAsync(url);
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

        // Garantiza que el historial pendiente (guardado diferido) llegue a disco al cerrar
        try { HistoryManager.Flush(); } catch { }
        base.OnExit(e);
    }
}
