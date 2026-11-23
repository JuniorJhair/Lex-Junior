using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using LexBrowser.Core;

namespace LexBrowser.UI.Views;

public partial class LexPlayerSetupDialog : Window
{
    private CancellationTokenSource? _cts;
    private bool _isWorking;

    public LexPlayerSetupDialog()
    {
        InitializeComponent();
        if (LexPlayerLauncher.FindLocalPlayerSetupExe() != null)
        {
            BtnInstall.Content = "🚀 Sí, instalar Lex Player ahora";
            TxtStatus.Text = "Instalador ya disponible localmente. ¿Deseas abrir el asistente de instalación?";
        }
        Closing += (_, _) => _cts?.Cancel();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        if (_isWorking)
        {
            _cts?.Cancel();
            return;
        }
        Close();
    }

    private async void BtnInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_isWorking) return;

        if (LexPlayerLauncher.TryLaunchLocalSetup(out _))
        {
            Close();
            return;
        }

        _isWorking = true;
        _cts = new CancellationTokenSource();
        BtnInstall.IsEnabled = false;
        BtnCancel.Content = "Cancelar";

        var progress = new Progress<(int Percent, string Status)>(p =>
        {
            BarProgress.Value = p.Percent;
            TxtStatus.Text = p.Status;
        });

        try
        {
            var setupExe = await LexPlayerLauncher.DownloadSetupInstallerAsync(progress, _cts.Token);
            Process.Start(new ProcessStartInfo
            {
                FileName = setupExe,
                Arguments = "--install",
                WorkingDirectory = LexPlayerLauncher.InstalledPlayerDir,
                UseShellExecute = true
            });
            Close();
        }
        catch (OperationCanceledException)
        {
            _isWorking = false;
            BtnInstall.IsEnabled = true;
            BtnCancel.Content = "Ahora no";
            BarProgress.Value = 0;
            TxtStatus.Text = "Descarga cancelada.";
        }
        catch (Exception ex)
        {
            _isWorking = false;
            BtnInstall.IsEnabled = true;
            BtnCancel.Content = "Cerrar";
            BarProgress.Value = 0;
            TxtStatus.Text = $"No se pudo descargar automáticamente: {ex.Message}";
        }
    }
}
