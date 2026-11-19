using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using LexBrowser.Core;

namespace LexBrowser.UI.Views;

public partial class VersionDowngradeDialog : Window
{
    private readonly string _newerProfileVersion;
    private bool _isUpdating;

    public VersionDowngradeDialog(string runningVersion, string newerProfileVersion)
    {
        _newerProfileVersion = newerProfileVersion;
        InitializeComponent();
        UpdateThemeStyles(SettingsManager.Current?.Theme);

        TxtDowngradeSummary.Text = SettingsManager.DowngradeWarningMessage ??
            $"Este perfil fue usado por última vez en v{newerProfileVersion} y esta instalación es v{runningVersion}. Debes actualizar antes de continuar.";
        TxtVersionsComparison.Text = $"Versión actual ejecutada: v{runningVersion}  ·  Versión requerida por el perfil: v{newerProfileVersion} o superior";

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !_isUpdating)
            {
                e.Handled = true;
                DialogResult = false;
                Close();
            }
        };
    }

    public void UpdateThemeStyles(string? theme = null)
    {
        string target = BrowserThemeManager.NormalizeTheme(theme ?? SettingsManager.Current?.Theme);
        BrowserThemeManager.ApplyThemePalette(Resources, target);
    }

    private async void BtnUpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdating) return;
        _isUpdating = true;
        BtnUpdateNow.IsEnabled = false;
        BtnExitApp.IsEnabled = false;
        TxtUpdateStatus.Visibility = Visibility.Visible;
        TxtUpdateStatus.Text = "Buscando la última versión publicada en GitHub Releases...";

        try
        {
            var info = await UpdateManager.CheckForUpdatesAsync(isUserInitiated: true);
            if (info == null)
            {
                TxtUpdateStatus.Text =
                    $"No se encontró en línea una versión más nueva que v{SettingsManager.CurrentAppVersion.ToString(3)} (o no hay conexión a internet). " +
                    $"Abre la página de descargas o ejecuta el instalador de v{_newerProfileVersion} o superior.";
                return;
            }

            TxtUpdateStatus.Text = $"Descargando {info.VersionTag}... 0%";
            var progress = new Progress<int>(pct =>
            {
                TxtUpdateStatus.Text = $"Descargando {info.VersionTag}... {pct}%";
            });

            bool ok = await UpdateManager.DownloadAndApplyUpdateAsync(info, progress);
            if (!ok)
            {
                TxtUpdateStatus.Text = "No se pudo descargar o verificar el instalador. Usa «Abrir descargas» o verifica tu conexión.";
            }
        }
        catch (Exception ex)
        {
            TxtUpdateStatus.Text = $"Error al comprobar actualización: {ex.Message}";
        }
        finally
        {
            _isUpdating = false;
            BtnUpdateNow.IsEnabled = true;
            BtnExitApp.IsEnabled = true;
        }
    }

    private void BtnOpenReleases_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(UpdateManager.LatestReleaseWebUrl) { UseShellExecute = true });
        }
        catch { }
    }

    private void BtnExitApp_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdating) return;
        DialogResult = false;
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }
}
