using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using LexBrowser.Core;

namespace LexBrowser.UI.Views;

public partial class SettingsWindow : Window
{
    private readonly CoreWebView2? _core;
    private bool _isInitializing = true;

    public SettingsWindow(CoreWebView2? core = null)
    {
        _core = core;
        InitializeComponent();

        var ver = typeof(SettingsWindow).Assembly.GetName().Version;
        if (TxtSettingsVersionBadge != null && ver != null)
        {
            TxtSettingsVersionBadge.Text = $"⚡ Lex Junior v{ver.Major}.{ver.Minor}.{ver.Build}";
        }

        LoadCurrentSettings();
        _isInitializing = false;
        Activated += (s, e) => UpdateDefaultBrowserUI();
    }

    private void LoadCurrentSettings()
    {
        var s = SettingsManager.Current;
        ChkFloatingPage.IsChecked = s.ShowFloatingPageButton;
        ChkAutoVideoPip.IsChecked = s.EnableAutoVideoPip;
        ChkBookmarksBar.IsChecked = s.ShowBookmarksBar;
        ChkAdBlocker.IsChecked = s.EnableAdBlocker;
        ChkRestoreSession.IsChecked = s.RestorePreviousSession;
        ChkEnableHistory.IsChecked = s.EnableHistory;
        TxtSettingsHistoryStorage.Text = $"Espacio ocupado: {HistoryManager.GetHistoryFileSizeFormatted()}";
        if (TxtSettingsVaultSummary != null)
        {
            int pwdCount = PasswordManager.Store.GetTotalCount();
            string lockLabel = PasswordManager.HasMasterPasswordConfigured() ? "Con PIN activo" : "Sin PIN";
            TxtSettingsVaultSummary.Text = $"Contraseñas guardadas: {pwdCount} · Estado: {lockLabel}";
        }

        TxtCustomSearchUrl.Text = s.CustomSearchUrl;

        switch (s.DefaultSearchEngine)
        {
            case "DuckDuckGo": RbDuckDuckGo.IsChecked = true; break;
            case "Bing": RbBing.IsChecked = true; break;
            case "Brave": RbBrave.IsChecked = true; break;
            case "Custom": RbCustom.IsChecked = true; break;
            default: RbGoogle.IsChecked = true; break;
        }

        UpdateDefaultBrowserUI();
    }

    public void OpenToSection(string section)
    {
        if (section.Equals("Telemetry", StringComparison.OrdinalIgnoreCase))
        {
            TabTelemetry.IsChecked = true;
        }
        else if (section.Equals("Bookmarks", StringComparison.OrdinalIgnoreCase))
        {
            TabBookmarks.IsChecked = true;
        }
        else if (section.Equals("Search", StringComparison.OrdinalIgnoreCase))
        {
            TabSearch.IsChecked = true;
        }
        else
        {
            TabGeneral.IsChecked = true;
        }
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelGeneral == null || PanelSearch == null || PanelBookmarks == null || PanelTelemetry == null) return;

        PanelGeneral.Visibility = (TabGeneral.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
        PanelSearch.Visibility = (TabSearch.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
        PanelBookmarks.Visibility = (TabBookmarks.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
        PanelTelemetry.Visibility = (TabTelemetry.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        var s = SettingsManager.Current;
        s.ShowFloatingPageButton = ChkFloatingPage.IsChecked == true;
        s.EnableAutoVideoPip = ChkAutoVideoPip.IsChecked == true;
        s.ShowBookmarksBar = ChkBookmarksBar.IsChecked == true;
        s.EnableAdBlocker = ChkAdBlocker.IsChecked == true;
        s.RestorePreviousSession = ChkRestoreSession.IsChecked == true;

        bool wantsHistory = ChkEnableHistory.IsChecked == true;
        if (!wantsHistory && s.EnableHistory)
        {
            // Estaba activado y el usuario lo acaba de desactivar
            int total = HistoryManager.GetTotalCount();
            if (total > 0)
            {
                var dialog = new HistoryDisablePromptDialog { Owner = this };
                bool? dialogResult = dialog.ShowDialog();

                if (dialogResult == true)
                {
                    if (dialog.Result == HistoryDisableChoice.DeleteAndDisable)
                    {
                        HistoryManager.ClearAll();
                        s.EnableHistory = false;
                    }
                    else if (dialog.Result == HistoryDisableChoice.KeepAndDisable)
                    {
                        s.EnableHistory = false;
                    }
                }
                else
                {
                    // Canceló: revertir el switch a activado
                    ChkEnableHistory.IsChecked = true;
                    return;
                }
            }
            else
            {
                s.EnableHistory = false;
            }
        }
        else
        {
            s.EnableHistory = wantsHistory;
        }

        SettingsManager.Save();
        TxtSettingsHistoryStorage.Text = $"Espacio ocupado: {HistoryManager.GetHistoryFileSizeFormatted()}";
    }

    private void BtnOpenHistoryFromSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var historyWin = new HistoryWindow(_core)
            {
                Owner = this
            };
            historyWin.ShowDialog();
            TxtSettingsHistoryStorage.Text = $"Espacio ocupado: {HistoryManager.GetHistoryFileSizeFormatted()}";
        }
        catch { }
    }

    private void BtnOpenVaultFromSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (PasswordManager.ShouldRequireUnlockForVaultAction())
            {
                var pinPrompt = new PinPromptDialog("Confirma tu PIN para abrir la Bóveda de Contraseñas") { Owner = this };
                if (pinPrompt.ShowDialog() != true) return;
            }

            var vaultWin = new PasswordVaultWindow { Owner = this };
            vaultWin.ShowDialog();

            int pwdCount = PasswordManager.Store.GetTotalCount();
            string lockLabel = PasswordManager.HasMasterPasswordConfigured() ? "Con PIN activo" : "Sin PIN";
            TxtSettingsVaultSummary.Text = $"Contraseñas guardadas: {pwdCount} · Estado: {lockLabel}";
        }
        catch { }
    }

    private void SaveSearch_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        var s = SettingsManager.Current;
        if (RbDuckDuckGo.IsChecked == true) s.DefaultSearchEngine = "DuckDuckGo";
        else if (RbBing.IsChecked == true) s.DefaultSearchEngine = "Bing";
        else if (RbBrave.IsChecked == true) s.DefaultSearchEngine = "Brave";
        else if (RbCustom.IsChecked == true) s.DefaultSearchEngine = "Custom";
        else s.DefaultSearchEngine = "Google";

        SettingsManager.Save();
    }

    private void TxtCustomSearchUrl_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_isInitializing) return;

        SettingsManager.Current.CustomSearchUrl = TxtCustomSearchUrl.Text.Trim();
        SettingsManager.Save();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void UpdateDefaultBrowserUI()
    {
        try
        {
            bool isDefault = InstallerService.IsDefaultBrowser();
            if (isDefault)
            {
                TxtDefaultBrowserStatus.Text = "🎉 ¡Gracias por preferirnos como tu navegador predeterminado en Windows!";
                TxtDefaultBrowserStatus.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#34D399"));
                BtnSetDefaultBrowser.Content = "✓ Ya es predeterminado";
                BtnSetDefaultBrowser.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#065F46"));
                BtnSetDefaultBrowser.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#34D399"));
                BtnSetDefaultBrowser.IsEnabled = false;
            }
            else
            {
                TxtDefaultBrowserStatus.Text = "Haz de Lex Junior tu navegador principal para abrir enlaces externos.";
                TxtDefaultBrowserStatus.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#71717A"));
                BtnSetDefaultBrowser.Content = "🌐 Establecer como predeterminado";
                BtnSetDefaultBrowser.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#6366F1"));
                BtnSetDefaultBrowser.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFFFFF"));
                BtnSetDefaultBrowser.IsEnabled = true;
            }
        }
        catch { }
    }

    private void BtnSetDefaultBrowser_Click(object sender, RoutedEventArgs e)
    {
        InstallerService.OpenWindowsDefaultAppsSettings();
        UpdateDefaultBrowserUI();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (GlobalScrollBehavior.HasAnyOpenComboBox())
        {
            e.Handled = true;
            GlobalScrollBehavior.ScrollActiveComboBox(e.Delta);
            return;
        }

        e.Handled = true;
        if (sender is ScrollViewer scrollViewer)
        {
            double deltaUnits = e.Delta / 120.0;
            double scrollPixels = deltaUnits * 28.0;
            double newOffset = Math.Clamp(scrollViewer.VerticalOffset - scrollPixels, 0, scrollViewer.ScrollableHeight);
            scrollViewer.ScrollToVerticalOffset(newOffset);
        }
    }
}

