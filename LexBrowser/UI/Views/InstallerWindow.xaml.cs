using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LexBrowser.Core;

namespace LexBrowser.UI.Views;

public partial class InstallerWindow : Window
{
    private readonly bool _isUninstallMode;
    private readonly bool _isLexClassMode;
    private readonly bool _shutdownOnComplete;
    private bool _isWorking = false;

    public InstallerWindow(bool isUninstallMode = false, bool isLexClassMode = false, bool shutdownOnComplete = true)
    {
        InitializeComponent();
        _isUninstallMode = isUninstallMode;
        _isLexClassMode = isLexClassMode;
        _shutdownOnComplete = shutdownOnComplete;

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
        else if (_isLexClassMode)
        {
            ApplyLexClassInstallerTheme();
        }
        else if (InstallerService.IsAlreadyInstalled())
        {
            BtnPrimaryAction.Content = "🔄 Actualizar / Reinstalar Lex Junior";
        }
    }

    private void ApplyLexClassInstallerTheme()
    {
        Title = "Instalador de Lex Junior Class";
        try
        {
            Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/lexclass.ico"));
            ImgHeaderLogo.Source = new BitmapImage(new Uri("pack://application:,,,/Assets/lexclass.png"));
        }
        catch { }

        OuterFrame.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x00));
        LogoBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFB, 0x92, 0x3C));
        VersionBadgeBorder.Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x16, 0x0A));
        VersionBadgeBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x00));
        TxtVersionBadge.Foreground = new SolidColorBrush(Color.FromRgb(0xFD, 0xBA, 0x74));
        TxtVersionBadge.Text = "PROTOTIPO";

        TxtHeaderTitle.Text = "LEX JUNIOR CLASS";
        TxtSubtitle.Text = "Academia local interactiva · Ventana limpia sin URL · Funciona sin internet";

        ChkDesktopShortcut.Content = "Agregar acceso directo de Lex Junior Class en el Escritorio (icono propio)";
        ChkDesktopShortcut.IsChecked = true;
        ChkStartMenuShortcut.Content = "Agregar Lex Junior Class al Menú Inicio de Windows";
        ChkStartMenuShortcut.IsChecked = true;
        ChkLaunchAfterInstall.Content = "Abrir Lex Junior Class al terminar";
        ChkLaunchAfterInstall.IsChecked = false;
        CardLexClassShortcut.Visibility = Visibility.Collapsed;
        Height = 405;

        ProgressInstall.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x00));
        BtnPrimaryAction.Content = "🎓 Instalar acceso de Lex Junior Class";
        BtnPrimaryAction.Background = new SolidColorBrush(Color.FromRgb(0xEA, 0x58, 0x0C));
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
                if (_shutdownOnComplete) Application.Current.Shutdown();
                else Close();
            }
            else if (_isLexClassMode)
            {
                bool desktop = ChkDesktopShortcut.IsChecked == true;
                bool startMenu = ChkStartMenuShortcut.IsChecked == true;
                bool launch = ChkLaunchAfterInstall.IsChecked == true;

                await InstallerService.InstallLexClassShortcutsAsync(desktop, startMenu, progress);
                await Task.Delay(250);

                if (launch)
                {
                    if (_shutdownOnComplete)
                    {
                        InstallerService.LaunchInstalledLexClass();
                    }
                    else
                    {
                        var win = new LexClassWindow();
                        win.Show();
                    }
                }

                if (_shutdownOnComplete)
                {
                    Application.Current.Shutdown();
                }
                else
                {
                    DialogResult = true;
                    Close();
                }
            }
            else
            {
                bool desktop = ChkDesktopShortcut.IsChecked == true;
                bool startMenu = ChkStartMenuShortcut.IsChecked == true;
                bool launch = ChkLaunchAfterInstall.IsChecked == true;
                bool lexClassDesktop = ChkLexClassDesktopShortcut.IsChecked == true;

                await InstallerService.InstallAsync(desktop, startMenu, progress, lexClassDesktop);
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
