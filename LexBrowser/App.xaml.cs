using System;
using System.Runtime.InteropServices;
using System.Windows;

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

        if (LexBrowser.Core.InstallerService.ShouldRunAsSilentUpdate(e.Args))
        {
            try
            {
                await LexBrowser.Core.InstallerService.PerformSilentUpdateAndRestartAsync();
            }
            catch { }
            Shutdown();
            return;
        }

        if (LexBrowser.Core.InstallerService.ShouldRunAsUninstaller(e.Args))
        {
            var uninstaller = new LexBrowser.UI.Views.InstallerWindow(isUninstallMode: true);
            MainWindow = uninstaller;
            uninstaller.Show();
            return;
        }

        if (LexBrowser.Core.InstallerService.ShouldRunAsInstaller(e.Args))
        {
            var installer = new LexBrowser.UI.Views.InstallerWindow(isUninstallMode: false);
            MainWindow = installer;
            installer.Show();
            return;
        }

        var browserWindow = new MainWindow();
        MainWindow = browserWindow;
        browserWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Garantiza que el historial pendiente (guardado diferido) llegue a disco al cerrar
        try { LexBrowser.Core.HistoryManager.Flush(); } catch { }
        base.OnExit(e);
    }
}
