using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using LexJuniorPlayer.Models;
using LexJuniorPlayer.Services;
using Microsoft.Win32;

namespace LexJuniorPlayer;

public sealed class PrereqRowViewModel
{
    public PrerequisiteCheckItem Model { get; }
    public string Name => Model.Name;
    public string Summary => Model.Summary;
    public string SizeHint => Model.SizeHint;
    public string VersionLine => $"Actual: {Model.InstalledVersion} · Mínimo: {Model.RequiredVersion}";

    public string BadgeText { get; }
    public Brush BadgeBg { get; }
    public Brush BadgeBorder { get; }
    public Brush BadgeFg { get; }

    public PrereqRowViewModel(PrerequisiteCheckItem item)
    {
        Model = item;
        (BadgeText, BadgeBg, BadgeBorder, BadgeFg) = item.Status switch
        {
            PrereqStatus.Ready => (
                "✓ AL DÍA",
                CreateBrush(16, 42, 32),
                CreateBrush(16, 185, 129),
                CreateBrush(110, 231, 183)),
            PrereqStatus.Outdated => (
                "↻ ACTUALIZAR",
                CreateBrush(48, 28, 12),
                CreateBrush(245, 158, 11),
                CreateBrush(253, 211, 77)),
            PrereqStatus.Missing => (
                "⬇ FALTA",
                CreateBrush(15, 32, 56),
                CreateBrush(56, 189, 248),
                CreateBrush(125, 211, 252)),
            _ => (
                "⚠ REVISAR",
                CreateBrush(45, 30, 12),
                CreateBrush(245, 158, 11),
                CreateBrush(252, 211, 77))
        };
    }

    private static SolidColorBrush CreateBrush(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }
}

public partial class SetupWizardWindow : Window
{
    private readonly bool _isInstallerMode;
    private readonly bool _isUninstallMode;
    private readonly bool _shutdownOnFinish;
    private readonly AppLibraryStore _store = new();
    private EngineSettings _settings;
    private PrerequisiteReport? _report;
    private CancellationTokenSource? _cts;
    private bool _isWorking;

    public bool ChangesApplied { get; private set; }

    public SetupWizardWindow(
        bool isInstallerMode = true,
        bool isUninstallMode = false,
        bool shutdownOnFinish = true)
    {
        InitializeComponent();
        _isInstallerMode = isInstallerMode;
        _isUninstallMode = isUninstallMode;
        _shutdownOnFinish = shutdownOnFinish;
        _settings = _store.LoadSettings();

        if (_isUninstallMode)
        {
            Title = "Desinstalar Lex Junior Player";
            HeaderTitleText.Text = "Desinstalar Lex Junior Player";
            InstallOptionsPanel.Visibility = Visibility.Collapsed;
            UninstallOptionsPanel.Visibility = Visibility.Visible;
            BrowseSdkBtn.IsEnabled = false;
            RescanBtn.IsEnabled = false;
            PrimaryActionBtn.Content = "Desinstalar ahora";
            PrimaryActionBtn.Style = (Style)FindResource("DangerBtn");
            StatusText.Text = "Confirma si deseas desinstalar el lanzador.";
            PrereqItemsControl.Visibility = Visibility.Collapsed;
        }
        else if (!_isInstallerMode)
        {
            Title = "Verificador de Requisitos · Lex Junior Player";
            HeaderTitleText.Text = "Requisitos del Motor Android Limpio";
            LaunchAfterCheck.Visibility = Visibility.Collapsed;
            CloseBtn.Content = "Cerrar";
        }

        Loaded += async (_, _) =>
        {
            if (!_isUninstallMode)
            {
                await RefreshReportAsync();
            }
        };
        Closing += (_, _) => _cts?.Cancel();
    }

    private async Task RefreshReportAsync()
    {
        if (_isWorking) return;
        PrimaryActionBtn.IsEnabled = false;
        StatusText.Text = "Detectando componentes ya instalados en tu equipo...";
        InstallProgress.IsIndeterminate = true;

        try
        {
            _report = await PrerequisiteService.CheckAllAsync(
                _settings.CustomSdkPath,
                _settings.SelectedSystemImageRelPath,
                includeAppBinaryCheck: _isInstallerMode);

            SdkPathText.Text = _report.DetectedSdkRoot;
            PrereqItemsControl.ItemsSource = _report.Items.Select(i => new PrereqRowViewModel(i)).ToList();

            int missingCount = _report.Items.Count(i => i.CanAutoInstall && i.Status == PrereqStatus.Missing);
            int outdatedCount = _report.Items.Count(i => i.CanAutoInstall && i.Status == PrereqStatus.Outdated);
            int readyCount = _report.Items.Count(i => i.Status == PrereqStatus.Ready);

            if (missingCount > 0 && outdatedCount > 0)
            {
                StatusText.Text = $"Faltan {missingCount} componente(s) y {outdatedCount} está(n) desactualizado(s). Los {readyCount} al día NO se reinstalarán.";
                PrimaryActionBtn.Content = "⬇ Instalar faltantes y actualizar";
            }
            else if (missingCount > 0)
            {
                StatusText.Text = $"Faltan {missingCount} componente(s). Todo lo que ya tienes al día ({readyCount}) se conserva sin reinstalar.";
                PrimaryActionBtn.Content = "⬇ Descargar e instalar faltantes";
            }
            else if (outdatedCount > 0)
            {
                StatusText.Text = $"Se detectaron {outdatedCount} componente(s) desactualizado(s) necesarios para un buen funcionamiento.";
                PrimaryActionBtn.Content = "↻ Actualizar desactualizados";
            }
            else
            {
                StatusText.Text = _report.HasAccelerationWarning
                    ? "SDK listo. Revisa el aviso de aceleración por hardware (WHPX/AEHD)."
                    : "✅ Todos los requisitos están instalados y actualizados. No hace falta reinstalar nada.";
                PrimaryActionBtn.Content = _isInstallerMode ? "✓ Aplicar accesos y continuar" : "✓ Todo listo";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"No se pudieron verificar todos los requisitos: {ex.Message}";
        }
        finally
        {
            InstallProgress.IsIndeterminate = false;
            InstallProgress.Value = 0;
            PrimaryActionBtn.IsEnabled = true;
        }
    }

    private async void BrowseSdkBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isWorking) return;
        var dlg = new OpenFolderDialog
        {
            Title = "Seleccionar carpeta de Android SDK",
            InitialDirectory = Directory.Exists(_settings.CustomSdkPath)
                ? _settings.CustomSdkPath
                : SdkLocator.GetRecommendedInstallSdkRoot(_settings.CustomSdkPath)
        };
        if (dlg.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            _settings.CustomSdkPath = dlg.FolderName;
            _store.SaveSettings(_settings);
            await RefreshReportAsync();
        }
    }

    private async void RescanBtn_Click(object sender, RoutedEventArgs e)
    {
        await RefreshReportAsync();
    }

    private async void PrimaryActionBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isWorking) return;

        if (_isUninstallMode)
        {
            _isWorking = true;
            PrimaryActionBtn.IsEnabled = false;
            CloseBtn.IsEnabled = false;
            StatusText.Text = "Desinstalando Lex Junior Player...";
            InstallProgress.IsIndeterminate = true;
            try
            {
                await PlayerInstallerService.UninstallAsync(DeleteDataCheck.IsChecked == true);
                MessageBox.Show(
                    "Lex Junior Player fue desinstalado correctamente. Tu Android SDK compartido no fue borrado.",
                    "Desinstalación completada",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                if (_shutdownOnFinish) Application.Current.Shutdown();
                else Close();
            }
            catch (Exception ex)
            {
                _isWorking = false;
                PrimaryActionBtn.IsEnabled = true;
                CloseBtn.IsEnabled = true;
                InstallProgress.IsIndeterminate = false;
                MessageBox.Show(ex.Message, "Error al desinstalar", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return;
        }

        if (_report == null)
        {
            await RefreshReportAsync();
            if (_report == null) return;
        }

        // If user is only running the prereq checker (not installer) and nothing is missing/outdated, just finish cleanly
        if (!_isInstallerMode && !_report.HasMissingOrOutdatedAutoInstallable)
        {
            if (DesktopShortcutCheck.IsChecked == true)
            {
                ShortcutHelper.TryCreateDesktopShortcut(PlayerInstallerService.EnsureIconOnDisk(), out _);
            }
            DialogResult = true;
            Close();
            return;
        }

        _isWorking = true;
        _cts = new CancellationTokenSource();
        PrimaryActionBtn.IsEnabled = false;
        BrowseSdkBtn.IsEnabled = false;
        RescanBtn.IsEnabled = false;
        CloseBtn.Content = "Cancelar";
        InstallProgress.IsIndeterminate = false;
        InstallProgress.Value = 2;

        try
        {
            await PlayerInstallerService.InstallMissingOrOutdatedAsync(
                _report,
                createDesktopShortcut: DesktopShortcutCheck.IsChecked == true,
                createStartMenuShortcut: StartMenuShortcutCheck.IsChecked == true,
                includeAppBinary: _isInstallerMode,
                forceReinstallBinary: false,
                reportProgress: (pct, msg) => Dispatcher.Invoke(() =>
                {
                    InstallProgress.Value = pct;
                    StatusText.Text = msg;
                }),
                ct: _cts.Token);

            ChangesApplied = true;
            _isWorking = false;
            CloseBtn.Content = "Cerrar";
            BrowseSdkBtn.IsEnabled = true;
            RescanBtn.IsEnabled = true;

            await RefreshReportAsync();

            if (_isInstallerMode && LaunchAfterCheck.IsChecked == true &&
                File.Exists(PrerequisiteService.DefaultInstalledExePath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = PrerequisiteService.DefaultInstalledExePath,
                        WorkingDirectory = PrerequisiteService.DefaultAppInstallDir,
                        UseShellExecute = true
                    });
                }
                catch { }
            }

            if (_shutdownOnFinish)
            {
                Application.Current.Shutdown();
            }
            else if (!_report.HasMissingOrOutdatedAutoInstallable)
            {
                DialogResult = true;
                Close();
            }
        }
        catch (OperationCanceledException)
        {
            _isWorking = false;
            CloseBtn.Content = "Cerrar";
            BrowseSdkBtn.IsEnabled = true;
            RescanBtn.IsEnabled = true;
            PrimaryActionBtn.IsEnabled = true;
            StatusText.Text = "Operación cancelada por el usuario.";
            InstallProgress.Value = 0;
        }
        catch (Exception ex)
        {
            _isWorking = false;
            CloseBtn.Content = "Cerrar";
            BrowseSdkBtn.IsEnabled = true;
            RescanBtn.IsEnabled = true;
            PrimaryActionBtn.IsEnabled = true;
            StatusText.Text = $"Error: {ex.Message}";
            InstallProgress.Value = 0;
            MessageBox.Show(
                $"No se pudieron instalar todos los requisitos:\n{ex.Message}",
                "Error en requisitos",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isWorking)
        {
            _cts?.Cancel();
            return;
        }
        Close();
    }
}
