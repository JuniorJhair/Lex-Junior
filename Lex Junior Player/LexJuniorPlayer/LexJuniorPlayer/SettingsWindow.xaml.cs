using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LexJuniorPlayer.Models;
using LexJuniorPlayer.Services;
using Microsoft.Win32;

namespace LexJuniorPlayer;

public partial class SettingsWindow : Window
{
    private readonly EngineSettings _settings;
    private readonly IReadOnlyList<InstalledAppInfo> _apps;
    private SdkPaths _discoveredSdk;

    public SettingsWindow(EngineSettings current, IReadOnlyList<InstalledAppInfo> apps)
    {
        InitializeComponent();
        _settings = new EngineSettings
        {
            AvdName = current.AvdName,
            CustomSdkPath = current.CustomSdkPath,
            SelectedSystemImageRelPath = current.SelectedSystemImageRelPath,
            GpuMode = string.IsNullOrWhiteSpace(current.GpuMode) ? "auto" : current.GpuMode,
            ResolutionWidth = current.ResolutionWidth,
            ResolutionHeight = current.ResolutionHeight,
            Density = current.Density,
            RamMb = current.RamMb,
            CpuCores = current.CpuCores,
            PreferTvActivity = current.PreferTvActivity,
            ShutdownEngineOnExit = current.ShutdownEngineOnExit,
            QuickBootEnabled = current.QuickBootEnabled,
            AutoLaunchPackage = current.AutoLaunchPackage,
            HideEmulatorToolbar = current.HideEmulatorToolbar,
            StartFullscreenOnLaunch = current.StartFullscreenOnLaunch,
            PerformanceTunedVersion = current.PerformanceTunedVersion,
            AspectPreset = current.AspectPreset
        };
        _apps = apps;
        _discoveredSdk = SdkLocator.Discover(_settings.CustomSdkPath, _settings.SelectedSystemImageRelPath);
        PopulateFields();
    }

    public EngineSettings ResultSettings => _settings;

    private void PopulateFields()
    {
        RefreshSdkAndSystemImages();

        SelectComboByTag(GpuModeCombo, string.IsNullOrWhiteSpace(_settings.GpuMode) ? "auto" : _settings.GpuMode);

        var pixels = _settings.ResolutionWidth * _settings.ResolutionHeight;
        var resTag = pixels switch
        {
            >= 1_750_000 => "1920x1080",
            >= 1_150_000 => "1600x900",
            _ => "1280x720"
        };
        SelectComboByTag(ResolutionCombo, resTag);
        SelectComboByTag(AspectCombo, string.IsNullOrWhiteSpace(_settings.AspectPreset) ? "auto" : _settings.AspectPreset);
        SelectComboByTag(RamCombo, _settings.RamMb.ToString());
        SelectComboByTag(CoresCombo, _settings.CpuCores.ToString());

        AutoLaunchCombo.Items.Clear();
        AutoLaunchCombo.Items.Add(new ComboBoxItem { Content = "Mostrar mi lista limpia de APKs (Recomendado)", Tag = "" });
        foreach (var app in _apps)
        {
            AutoLaunchCombo.Items.Add(new ComboBoxItem
            {
                Content = $"Abrir directo: {app.AppName} ({app.VersionLabel})",
                Tag = app.PackageName
            });
        }
        SelectComboByTag(AutoLaunchCombo, _settings.AutoLaunchPackage);

        PreferTvCheck.IsChecked = _settings.PreferTvActivity;
        ShutdownOnExitCheck.IsChecked = _settings.ShutdownEngineOnExit;
        QuickBootCheck.IsChecked = _settings.QuickBootEnabled;
        HideToolbarCheck.IsChecked = _settings.HideEmulatorToolbar;
        AutoFullscreenCheck.IsChecked = _settings.StartFullscreenOnLaunch;
    }

    private void RefreshSdkAndSystemImages()
    {
        _discoveredSdk = SdkLocator.Discover(_settings.CustomSdkPath, _settings.SelectedSystemImageRelPath);
        SdkPathBox.Text = string.IsNullOrWhiteSpace(_settings.CustomSdkPath)
            ? (string.IsNullOrWhiteSpace(_discoveredSdk.SdkRoot)
                ? $"Auto (no detectado · sugerido: {SdkLocator.GetRecommendedInstallSdkRoot(null)})"
                : $"Auto: {_discoveredSdk.SdkRoot}")
            : _settings.CustomSdkPath;

        SystemImageCombo.Items.Clear();
        var images = _discoveredSdk.AvailableSystemImages ?? Array.Empty<SystemImageInfo>();
        if (images.Count == 0)
        {
            SystemImageCombo.Items.Add(new ComboBoxItem
            {
                Content = "No hay imágenes instaladas — usa 'Verificar requisitos' arriba",
                Tag = ""
            });
            SystemImageCombo.SelectedIndex = 0;
            SystemImageNoteText.Text = "Aún no se detecta ninguna imagen Android en este SDK.";
            SystemImageNoteText.Foreground = System.Windows.Media.Brushes.Orange;
            return;
        }

        foreach (var img in images)
        {
            SystemImageCombo.Items.Add(new ComboBoxItem
            {
                Content = img.SelectorLabel,
                Tag = img.RelativePath,
                DataContext = img
            });
        }

        var targetTag = string.IsNullOrWhiteSpace(_settings.SelectedSystemImageRelPath)
            ? _discoveredSdk.ActiveSystemImage?.RelativePath ?? images[0].RelativePath
            : _settings.SelectedSystemImageRelPath.TrimEnd('\\', '/');
        SelectComboByTag(SystemImageCombo, targetTag);
        UpdateSelectedImageNote();
    }

    private void SystemImageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectedImageNote();
    }

    private void UpdateSelectedImageNote()
    {
        if (SystemImageCombo.SelectedItem is ComboBoxItem item && item.DataContext is SystemImageInfo img)
        {
            if (img.SupportsArm32 && img.SupportsArm64)
            {
                SystemImageNoteText.Text = $"Ruta: {img.RelativePath} · Compatible con APKs ARM (32/64-bit) y x86_64.";
                SystemImageNoteText.Foreground = System.Windows.Media.Brushes.LightGreen;
            }
            else if (img.SupportsArm64)
            {
                SystemImageNoteText.Text = $"⚠ {img.RelativePath}: solo 64-bit (ARM64 + x86_64). APKs antiguas de 32-bit no abrirán aquí.";
                SystemImageNoteText.Foreground = System.Windows.Media.Brushes.Orange;
            }
            else
            {
                SystemImageNoteText.Text = $"⚠ {img.RelativePath}: solo x86_64 nativo (sin traductor ARM).";
                SystemImageNoteText.Foreground = System.Windows.Media.Brushes.Orange;
            }
        }
    }

    private void BrowseSdk_Click(object sender, RoutedEventArgs e)
    {
        var initial = !string.IsNullOrWhiteSpace(_settings.CustomSdkPath) && Directory.Exists(_settings.CustomSdkPath)
            ? _settings.CustomSdkPath
            : (_discoveredSdk.SdkRoot ?? SdkLocator.GetRecommendedInstallSdkRoot(null));

        var dlg = new OpenFolderDialog
        {
            Title = "Seleccionar carpeta del Android SDK",
            InitialDirectory = Directory.Exists(initial) ? initial : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        if (dlg.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            _settings.CustomSdkPath = dlg.FolderName;
            RefreshSdkAndSystemImages();
        }
    }

    private void ResetSdk_Click(object sender, RoutedEventArgs e)
    {
        _settings.CustomSdkPath = string.Empty;
        RefreshSdkAndSystemImages();
    }

    private void CheckPrereqsBtn_Click(object sender, RoutedEventArgs e)
    {
        var wizard = new SetupWizardWindow(isInstallerMode: false, isUninstallMode: false, shutdownOnFinish: false)
        {
            Owner = this
        };
        wizard.ShowDialog();
        var latestStore = new AppLibraryStore().LoadSettings();
        if (!string.IsNullOrWhiteSpace(latestStore.CustomSdkPath))
        {
            _settings.CustomSdkPath = latestStore.CustomSdkPath;
        }
        RefreshSdkAndSystemImages();
    }

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private void CreateShortcut_Click(object sender, RoutedEventArgs e)
    {
        var icoCandidate = Path.Combine(AppContext.BaseDirectory, "Assets", "lex_junior_player.ico");
        if (!File.Exists(icoCandidate))
        {
            icoCandidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Assets", "lex_junior_player.ico"));
        }

        if (ShortcutHelper.TryCreateDesktopShortcut(icoCandidate, out var path))
        {
            ShortcutStatusText.Text = $"Acceso directo creado: {path}";
            ShortcutStatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
            ShortcutStatusText.Visibility = Visibility.Visible;
        }
        else
        {
            ShortcutStatusText.Text = "No se pudo crear el acceso directo automáticamente.";
            ShortcutStatusText.Foreground = System.Windows.Media.Brushes.Salmon;
            ShortcutStatusText.Visibility = Visibility.Visible;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (SystemImageCombo.SelectedItem is ComboBoxItem imgItem && imgItem.Tag is string imgRel)
        {
            _settings.SelectedSystemImageRelPath = imgRel;
        }

        if (GpuModeCombo.SelectedItem is ComboBoxItem gpuItem && gpuItem.Tag is string gpuMode)
        {
            _settings.GpuMode = gpuMode;
        }

        if (ResolutionCombo.SelectedItem is ComboBoxItem resItem && resItem.Tag is string resStr)
        {
            var parts = resStr.Split('x');
            if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
            {
                _settings.ResolutionWidth = w;
                _settings.ResolutionHeight = h;
                _settings.Density = w >= 1920 ? 320 : 213;
            }
        }

        if (AspectCombo.SelectedItem is ComboBoxItem aspectItem && aspectItem.Tag is string aspectStr)
            _settings.AspectPreset = aspectStr;

        var (resolvedW, resolvedH, resolvedDensity) = EmulatorWindowController.ResolveGuestResolution(_settings);
        _settings.ResolutionWidth = resolvedW;
        _settings.ResolutionHeight = resolvedH;
        _settings.Density = resolvedDensity;

        if (RamCombo.SelectedItem is ComboBoxItem ramItem && int.TryParse(ramItem.Tag?.ToString(), out var ram))
            _settings.RamMb = ram;

        if (CoresCombo.SelectedItem is ComboBoxItem coreItem && int.TryParse(coreItem.Tag?.ToString(), out var cores))
            _settings.CpuCores = cores;

        if (AutoLaunchCombo.SelectedItem is ComboBoxItem autoItem)
            _settings.AutoLaunchPackage = autoItem.Tag?.ToString() ?? string.Empty;

        _settings.PreferTvActivity = PreferTvCheck.IsChecked == true;
        _settings.ShutdownEngineOnExit = ShutdownOnExitCheck.IsChecked == true;
        _settings.QuickBootEnabled = QuickBootCheck.IsChecked == true;
        _settings.HideEmulatorToolbar = HideToolbarCheck.IsChecked == true;
        _settings.StartFullscreenOnLaunch = AutoFullscreenCheck.IsChecked == true;

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
