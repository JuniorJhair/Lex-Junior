using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LexBrowser.Core;

namespace LexBrowser.UI.Views;

public partial class InstallerWindow : Window
{
    private readonly bool _isUninstallMode;
    private bool _isWorking = false;

    public InstallerWindow(bool isUninstallMode = false)
    {
        InitializeComponent();
        _isUninstallMode = isUninstallMode;
        TxtVersionBadge.Text = $"v{UpdateManager.CurrentVersionString}";
        TxtInstallLocation.Text = $"Ruta de instalación: {InstallerService.InstallDir}";

        if (_isUninstallMode)
        {
            Title = "Desinstalar Lex Junior Browser";
            TxtHeaderTitle.Text = "DESINSTALAR LEX JUNIOR";
            TxtSubtitle.Text = "Eliminación limpia de archivos, accesos directos y registros";
            PanelInstallOptions.Visibility = Visibility.Collapsed;
            PanelUninstallOptions.Visibility = Visibility.Visible;
            BtnPrimaryAction.Content = "🗑️ Desinstalar ahora";
            BtnPrimaryAction.Background = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        }
        else if (InstallerService.IsAlreadyInstalled())
        {
            BtnPrimaryAction.Content = "🔄 Actualizar / Reinstalar Lex Junior";
        }
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
        if (_isWorking) return;
        Close();
    }

    private async void BtnPrimaryAction_Click(object sender, RoutedEventArgs e)
    {
        if (_isWorking) return;
        _isWorking = true;
        BtnPrimaryAction.IsEnabled = false;
        BtnCancel.IsEnabled = false;

        var progress = new Progress<(int Percent, string Status)>(p =>
        {
            ProgressInstall.Value = p.Percent;
            TxtProgressStatus.Text = p.Status;
        });

        try
        {
            if (_isUninstallMode)
            {
                bool deleteData = ChkDeleteUserData.IsChecked == true;
                await InstallerService.UninstallAsync(deleteData, progress);
                MessageBox.Show(
                    "Lex Junior Browser se ha desinstalado correctamente y tu equipo ha quedado limpio.",
                    "Desinstalación completada",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Application.Current.Shutdown();
            }
            else
            {
                bool desktop = ChkDesktopShortcut.IsChecked == true;
                bool startMenu = ChkStartMenuShortcut.IsChecked == true;
                bool launch = ChkLaunchAfterInstall.IsChecked == true;

                await InstallerService.InstallAsync(desktop, startMenu, progress);
                await Task.Delay(350);

                if (launch)
                {
                    InstallerService.LaunchInstalledBrowser();
                }

                Application.Current.Shutdown();
            }
        }
        catch (Exception ex)
        {
            _isWorking = false;
            BtnPrimaryAction.IsEnabled = true;
            BtnCancel.IsEnabled = true;
            TxtProgressStatus.Text = "Error durante el proceso.";
            MessageBox.Show(
                $"Ocurrió un error:\n{ex.Message}",
                "Instalador de Lex Junior",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
