using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using LexBrowser.Core;
using LexBrowser.Core.AdBlock;
using LexBrowser.Core.Engine;
using LexBrowser.Core.Notifications;

namespace LexBrowser.UI.Views;

public partial class SettingsWindow : Window
{
    private static readonly SolidColorBrush BrushEmeraldFg = CreateFrozenBrush(0x34, 0xD3, 0x99);
    private static readonly SolidColorBrush BrushEmeraldBg = CreateFrozenBrush(0x06, 0x5F, 0x46);
    private static readonly SolidColorBrush BrushSecondaryFg = CreateFrozenBrush(0xA1, 0xA1, 0xAA);
    private static readonly SolidColorBrush BrushPrimaryIndigo = CreateFrozenBrush(0x4F, 0x46, 0xE5);
    private static readonly SolidColorBrush BrushSiteCardBg = CreateFrozenBrush(0x18, 0x18, 0x24);
    private static readonly SolidColorBrush BrushSiteCardBorder = CreateFrozenBrush(0x29, 0x29, 0x3C);
    private static readonly SolidColorBrush BrushSiteIconBg = CreateFrozenBrush(0x23, 0x23, 0x34);
    private static readonly SolidColorBrush BrushBadgeBlockBg = CreateFrozenBrush(0x3B, 0x16, 0x1B);
    private static readonly SolidColorBrush BrushBadgeBlockFg = CreateFrozenBrush(0xF8, 0x71, 0x71);
    private static readonly SolidColorBrush BrushBadgeAllowBg = CreateFrozenBrush(0x0E, 0x33, 0x21);
    private static readonly SolidColorBrush BrushBadgeSilentBg = CreateFrozenBrush(0x28, 0x20, 0x48);
    private static readonly SolidColorBrush BrushBadgeSilentFg = CreateFrozenBrush(0xA7, 0x8B, 0xFA);
    private static readonly SolidColorBrush BrushBadgeAskBg = CreateFrozenBrush(0x25, 0x25, 0x33);
    private static readonly SolidColorBrush BrushBadgeAskFg = CreateFrozenBrush(0xD4, 0xD4, 0xD8);
    private static readonly SolidColorBrush BrushDrawerBg = CreateFrozenBrush(0x14, 0x14, 0x1F);
    private static readonly SolidColorBrush BrushDrawerBorder = CreateFrozenBrush(0x31, 0x2E, 0x81);
    private static readonly SolidColorBrush BrushComboBg = CreateFrozenBrush(0x1E, 0x1E, 0x2A);
    private static readonly SolidColorBrush BrushComboBorder = CreateFrozenBrush(0x3D, 0x3D, 0x56);

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private readonly CoreWebView2? _core;
    private bool _isInitializing = true;

    public SettingsWindow(CoreWebView2? core = null)
    {
        _core = core;
        InitializeComponent();

        Loaded += (_, _) =>
        {
            if (CardBrowserImport != null && Owner is MainWindow mw && mw.IsPrivateImportRestricted)
            {
                CardBrowserImport.Visibility = Visibility.Collapsed;
            }
        };

        var ver = typeof(SettingsWindow).Assembly.GetName().Version;
        if (TxtSettingsVersionBadge != null && ver != null)
        {
            TxtSettingsVersionBadge.Text = $"⚡ Lex Junior v{ver.Major}.{ver.Minor}.{ver.Build}";
        }

        LoadCurrentSettings();
        _isInitializing = false;
        Activated += (s, e) => UpdateDefaultBrowserUI();
        Closed += (_, _) => SettingsManager.FlushDeferredSave();
    }

    private void LoadCurrentSettings()
    {
        var s = SettingsManager.Current;
        ChkFloatingPage.IsChecked = s.ShowFloatingPageButton;
        ChkAutoVideoPip.IsChecked = s.EnableAutoVideoPip;
        ChkBookmarksBar.IsChecked = s.ShowBookmarksBar;
        ChkAdBlocker.IsChecked = s.EnableAdBlocker;
        ChkVideoPlayerDefuser.IsChecked = s.EnableVideoPlayerDefuser;
        ChkImaSdkStub.IsChecked = s.EnableImaSdkStub;
        ChkCosmeticShield.IsChecked = s.EnableCosmeticShield;
        ChkSubframeWebShield.IsChecked = s.EnableWebShieldInSubframes;
        if (ChkDiagnosticMode != null) ChkDiagnosticMode.IsChecked = ShieldLogger.DiagnosticMode;
        UpdateAdBlockerSubTogglesEnabledState();
        ChkRestoreSession.IsChecked = s.RestorePreviousSession;
        ChkEnableHistory.IsChecked = s.EnableHistory;
        if (ChkEnableSearchSuggestions != null) ChkEnableSearchSuggestions.IsChecked = s.EnableSearchSuggestions;
        if (ChkEnableExternalFavicons != null) ChkEnableExternalFavicons.IsChecked = s.EnableExternalFaviconService;
        if (CmbTabSuspensionMinutes != null) SetComboByTag(CmbTabSuspensionMinutes, s.TabSuspensionMinutes.ToString());
        if (ChkSuspendPinnedTabs != null) ChkSuspendPinnedTabs.IsChecked = s.SuspendPinnedTabs;
        if (ChkCookieBannerHiding != null) ChkCookieBannerHiding.IsChecked = s.EnableCookieBannerHiding;
        if (ChkEnableReadAloud != null) ChkEnableReadAloud.IsChecked = s.EnableReadAloud;
        if (CmbReadAloudRate != null) SetComboByTag(CmbReadAloudRate, Math.Clamp(s.ReadAloudRate, -5, 5).ToString());
        if (CmbReadAloudVoice != null)
        {
            while (CmbReadAloudVoice.Items.Count > 1)
                CmbReadAloudVoice.Items.RemoveAt(CmbReadAloudVoice.Items.Count - 1);

            foreach (var (voiceName, displayLabel) in ReadAloudService.GetAvailableVoices())
            {
                CmbReadAloudVoice.Items.Add(new ComboBoxItem
                {
                    Content = displayLabel,
                    Tag = voiceName
                });
            }

            SetComboByTag(CmbReadAloudVoice, s.ReadAloudVoiceName ?? string.Empty);
            if (CmbReadAloudVoice.SelectedIndex < 0 && CmbReadAloudVoice.Items.Count > 0)
            {
                CmbReadAloudVoice.SelectedIndex = 0;
            }
        }

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

        switch (s.NotificationGlobalPolicy)
        {
            case "BlockAll": RbPolicyBlockAll.IsChecked = true; break;
            case "SilentAll": RbPolicySilentAll.IsChecked = true; break;
            default: RbPolicyAsk.IsChecked = true; break;
        }

        ChkDiscardOlder24h.IsChecked = s.NotificationDiscardOlderThan24Hours;
        ChkDndFullscreen.IsChecked = s.NotificationDndOnFullscreen;

        SetComboByTag(CmbGlobalDailyLimit, s.NotificationGlobalDailyLimit.ToString());
        SetComboByTag(CmbDefaultSiteLimit, s.NotificationDefaultSiteDailyLimit.ToString());

        UpdateDefaultBrowserUI();
        RefreshNotificationSitesList();
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
        else if (section.Equals("Notifications", StringComparison.OrdinalIgnoreCase))
        {
            TabNotifications.IsChecked = true;
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
        if (PanelGeneral == null || PanelSearch == null || PanelBookmarks == null || PanelTelemetry == null || PanelNotifications == null) return;

        PanelGeneral.Visibility = (TabGeneral.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
        PanelSearch.Visibility = (TabSearch.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
        PanelBookmarks.Visibility = (TabBookmarks.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
        PanelNotifications.Visibility = (TabNotifications.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
        PanelTelemetry.Visibility = (TabTelemetry.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;

        if (TabNotifications.IsChecked == true)
        {
            RefreshNotificationSitesList();
        }
    }

    private void UpdateAdBlockerSubTogglesEnabledState()
    {
        bool masterEnabled = ChkAdBlocker.IsChecked == true;
        if (PanelAdBlockSubModules != null)
        {
            PanelAdBlockSubModules.IsEnabled = masterEnabled;
            PanelAdBlockSubModules.Opacity = masterEnabled ? 1.0 : 0.45;
        }
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        var s = SettingsManager.Current;
        s.ShowFloatingPageButton = ChkFloatingPage.IsChecked == true;
        s.EnableAutoVideoPip = ChkAutoVideoPip.IsChecked == true;
        s.ShowBookmarksBar = ChkBookmarksBar.IsChecked == true;
        s.EnableAdBlocker = ChkAdBlocker.IsChecked == true;
        s.EnableVideoPlayerDefuser = ChkVideoPlayerDefuser.IsChecked == true;
        s.EnableImaSdkStub = ChkImaSdkStub.IsChecked == true;
        s.EnableCosmeticShield = ChkCosmeticShield.IsChecked == true;
        s.EnableWebShieldInSubframes = ChkSubframeWebShield.IsChecked == true;
        if (ChkDiagnosticMode != null)
        {
            bool diag = ChkDiagnosticMode.IsChecked == true;
            ShieldLogger.DiagnosticMode = diag;
            s.Shield.DiagnosticMode = diag;
        }
        UpdateAdBlockerSubTogglesEnabledState();

        // Sincronizar módulos globales de LexShield
        s.Shield.Modules.Cosmetic = s.EnableCosmeticShield;
        s.Shield.Modules.Stubs = s.EnableImaSdkStub;
        s.Shield.Modules.VideoDefuser = s.EnableVideoPlayerDefuser;
        s.Shield.Modules.Subframes = s.EnableWebShieldInSubframes;

        s.RestorePreviousSession = ChkRestoreSession.IsChecked == true;
        s.NotificationDiscardOlderThan24Hours = ChkDiscardOlder24h.IsChecked == true;
        s.NotificationDndOnFullscreen = ChkDndFullscreen.IsChecked == true;
        if (ChkEnableSearchSuggestions != null) s.EnableSearchSuggestions = ChkEnableSearchSuggestions.IsChecked == true;
        if (ChkEnableExternalFavicons != null) s.EnableExternalFaviconService = ChkEnableExternalFavicons.IsChecked == true;
        if (ChkSuspendPinnedTabs != null) s.SuspendPinnedTabs = ChkSuspendPinnedTabs.IsChecked == true;
        if (ChkCookieBannerHiding != null) s.EnableCookieBannerHiding = ChkCookieBannerHiding.IsChecked == true;
        if (ChkEnableReadAloud != null) s.EnableReadAloud = ChkEnableReadAloud.IsChecked == true;

        bool wantsHistory = ChkEnableHistory.IsChecked == true;
        if (!wantsHistory && s.EnableHistory)
        {
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

    private void TxtCustomSearchUrl_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;

        SettingsManager.Current.CustomSearchUrl = TxtCustomSearchUrl.Text.Trim();
        SettingsManager.SaveDeferred(600, notify: true);
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
                if (!SettingsManager.Current.WasPreviouslyDefaultBrowser || !SettingsManager.Current.UserDecidedDefaultBrowser)
                {
                    SettingsManager.Current.WasPreviouslyDefaultBrowser = true;
                    SettingsManager.Current.UserDecidedDefaultBrowser = true;
                    SettingsManager.Save();
                }

                TxtDefaultBrowserStatus.Text = "🎉 ¡Gracias por preferirnos como tu navegador predeterminado en Windows!";
                TxtDefaultBrowserStatus.Foreground = BrushEmeraldFg;
                BtnSetDefaultBrowser.Content = "✓ Ya es predeterminado";
                BtnSetDefaultBrowser.Background = BrushEmeraldBg;
                BtnSetDefaultBrowser.Foreground = BrushEmeraldFg;
                BtnSetDefaultBrowser.IsEnabled = false;
            }
            else
            {
                if (SettingsManager.Current.WasPreviouslyDefaultBrowser)
                {
                    SettingsManager.Current.WasPreviouslyDefaultBrowser = false;
                    SettingsManager.Save();
                }

                TxtDefaultBrowserStatus.Text = "Haz de Lex Junior tu navegador principal para abrir enlaces externos.";
                TxtDefaultBrowserStatus.Foreground = BrushSecondaryFg;
                BtnSetDefaultBrowser.Content = "🌐 Establecer como predeterminado";
                BtnSetDefaultBrowser.Background = BrushPrimaryIndigo;
                BtnSetDefaultBrowser.Foreground = Brushes.White;
                BtnSetDefaultBrowser.IsEnabled = true;
            }
        }
        catch { }
    }

    private async void BtnSetDefaultBrowser_Click(object sender, RoutedEventArgs e)
    {
        InstallerService.OpenWindowsDefaultAppsSettings();
        UpdateDefaultBrowserUI();

        for (int i = 0; i < 5; i++)
        {
            await Task.Delay(1000);
            if (!IsLoaded) break;
            if (InstallerService.IsDefaultBrowser())
            {
                UpdateDefaultBrowserUI();
                break;
            }
        }
    }

    #region Métodos de Notificaciones Web (Nivel Senior, sin solapamientos)

    private void SetComboByTag(ComboBox combo, string tagValue)
    {
        if (combo == null) return;
        foreach (ComboBoxItem item in combo.Items)
        {
            if (string.Equals(item.Tag?.ToString(), tagValue, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                break;
            }
        }
    }

    private void PolicyRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        var s = SettingsManager.Current;
        if (RbPolicyBlockAll.IsChecked == true) s.NotificationGlobalPolicy = "BlockAll";
        else if (RbPolicySilentAll.IsChecked == true) s.NotificationGlobalPolicy = "SilentAll";
        else s.NotificationGlobalPolicy = "Ask";

        SettingsManager.Save();
    }

    private void Limits_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        var s = SettingsManager.Current;

        if (CmbGlobalDailyLimit?.SelectedItem is ComboBoxItem itemGlobal && int.TryParse(itemGlobal.Tag?.ToString(), out int globalLimit))
        {
            s.NotificationGlobalDailyLimit = globalLimit;
        }

        if (CmbDefaultSiteLimit?.SelectedItem is ComboBoxItem itemSite && int.TryParse(itemSite.Tag?.ToString(), out int siteLimit))
        {
            s.NotificationDefaultSiteDailyLimit = siteLimit;
        }

        SettingsManager.Save();
    }

    private void GeneralCombos_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        var s = SettingsManager.Current;

        if (CmbTabSuspensionMinutes?.SelectedItem is ComboBoxItem suspItem &&
            int.TryParse(suspItem.Tag?.ToString(), out int suspMins))
        {
            s.TabSuspensionMinutes = Math.Max(0, suspMins);
        }

        if (CmbReadAloudRate?.SelectedItem is ComboBoxItem rateItem &&
            int.TryParse(rateItem.Tag?.ToString(), out int rate))
        {
            s.ReadAloudRate = Math.Clamp(rate, -5, 5);
        }

        if (CmbReadAloudVoice?.SelectedItem is ComboBoxItem voiceItem)
        {
            s.ReadAloudVoiceName = voiceItem.Tag?.ToString() ?? string.Empty;
        }

        SettingsManager.Save();
    }

    private void BtnOpenImportWizard_Click(object sender, RoutedEventArgs e)
    {
        if (Owner is MainWindow mw && mw.IsPrivateImportRestricted)
        {
            MessageBox.Show(
                "La importación de datos no está disponible en Modo Invitado ni en navegación Incógnito.",
                "Modo privado",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var wizard = new ImportWizardWindow
        {
            Owner = this
        };
        wizard.ShowDialog();
    }

    private void FilterSites_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isInitializing) RefreshNotificationSitesList();
    }

    private void TxtSearchSite_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isInitializing) RefreshNotificationSitesList();
    }

    private void BtnResetAllSites_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "¿Estás seguro de que deseas restablecer todos los permisos de notificaciones?\n\nTodos los sitios volverán a su estado neutral (Preguntar).",
            "Restablecer permisos de sitios",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            NotificationPermissionManager.ResetAllSites();
            RefreshNotificationSitesList();
        }
    }

    private void RefreshNotificationSitesList()
    {
        if (PanelSitesContainer == null) return;

        var allSites = NotificationPermissionManager.GetAllSites();
        int totalSites = allSites.Count;
        int blockedCount = allSites.Count(s => s.Mode == SiteNotificationMode.Block);
        int allowedCount = allSites.Count(s => s.Mode == SiteNotificationMode.Allow);
        int silentCount = allSites.Count(s => s.Mode == SiteNotificationMode.Silent);

        if (FilterAll != null) FilterAll.Content = $"Todos ({totalSites})";
        if (FilterBlocked != null) FilterBlocked.Content = $"Bloqueados ({blockedCount})";
        if (FilterAllowed != null) FilterAllowed.Content = $"Permitidos ({allowedCount})";
        if (FilterSilent != null) FilterSilent.Content = $"Silenciosos ({silentCount})";

        if (TxtNotificationSitesSummary != null)
        {
            TxtNotificationSitesSummary.Text = $"Sitios registrados: {totalSites} · Bloqueados: {blockedCount} · Permitidos: {allowedCount} · Silenciosos: {silentCount}";
        }

        IEnumerable<SiteNotificationEntry> filtered = allSites;
        if (FilterBlocked?.IsChecked == true)
        {
            filtered = filtered.Where(s => s.Mode == SiteNotificationMode.Block);
        }
        else if (FilterAllowed?.IsChecked == true)
        {
            filtered = filtered.Where(s => s.Mode == SiteNotificationMode.Allow);
        }
        else if (FilterSilent?.IsChecked == true)
        {
            filtered = filtered.Where(s => s.Mode == SiteNotificationMode.Silent);
        }

        string query = TxtSearchSite?.Text?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!string.IsNullOrEmpty(query))
        {
            filtered = filtered.Where(s => s.Origin.ToLowerInvariant().Contains(query));
        }

        var list = filtered.OrderByDescending(s => s.CreatedAt).ToList();
        PanelSitesContainer.Children.Clear();

        if (list.Count == 0)
        {
            TxtNoSitesMessage.Visibility = Visibility.Visible;
            return;
        }

        TxtNoSitesMessage.Visibility = Visibility.Collapsed;

        foreach (var entry in list)
        {
            var rowCard = CreateSiteRowCard(entry);
            PanelSitesContainer.Children.Add(rowCard);
        }
    }

    private FrameworkElement CreateSiteRowCard(SiteNotificationEntry entry)
    {
        var container = new Border
        {
            Background = BrushSiteCardBg,
            BorderBrush = BrushSiteCardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var rootStack = new StackPanel();

        var mainGrid = new Grid();
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        string host = "sitio";
        try { host = new Uri(entry.Origin).Host; } catch { host = entry.Origin; }

        var iconBorder = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(6),
            Background = BrushSiteIconBg,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        var iconImg = new Image
        {
            Width = 16,
            Height = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var iconTxt = new TextBlock
        {
            Text = "🔔",
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        try
        {
            var fav = FaviconService.GetCachedFavicon(host);
            if (fav != null)
            {
                iconImg.Source = fav;
                iconBorder.Child = iconImg;
            }
            else
            {
                iconBorder.Child = iconTxt;
            }
        }
        catch { iconBorder.Child = iconTxt; }
        Grid.SetColumn(iconBorder, 0);
        mainGrid.Children.Add(iconBorder);

        var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };

        var titleBlock = new TextBlock
        {
            Text = host,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12.5,
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        infoStack.Children.Add(titleBlock);

        var detailsStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };

        string badgeText;
        Brush badgeBg;
        Brush badgeFg;
        switch (entry.Mode)
        {
            case SiteNotificationMode.Block:
                badgeText = "🚫 Bloqueado";
                badgeBg = BrushBadgeBlockBg;
                badgeFg = BrushBadgeBlockFg;
                break;
            case SiteNotificationMode.Allow:
                badgeText = "✅ Permitido";
                badgeBg = BrushBadgeAllowBg;
                badgeFg = BrushEmeraldFg;
                break;
            case SiteNotificationMode.Silent:
                badgeText = "🔕 Silencioso";
                badgeBg = BrushBadgeSilentBg;
                badgeFg = BrushBadgeSilentFg;
                break;
            default:
                badgeText = "❓ Preguntar";
                badgeBg = BrushBadgeAskBg;
                badgeFg = BrushBadgeAskFg;
                break;
        }

        var badgeBorder = new Border
        {
            Background = badgeBg,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(0, 0, 8, 0),
            Child = new TextBlock
            {
                Text = badgeText,
                Foreground = badgeFg,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            }
        };
        detailsStack.Children.Add(badgeBorder);

        string quotaText = entry.DailyLimit > 0 ? $"Cuota: {entry.DailyLimit}/día" : "Cuota: Predeterminada";
        detailsStack.Children.Add(new TextBlock
        {
            Text = quotaText,
            Foreground = BrushSecondaryFg,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });

        detailsStack.Children.Add(new TextBlock
        {
            Text = $"Agregado: {entry.CreatedAt.ToLocalTime():dd/MM/yyyy}",
            Foreground = BrushSecondaryFg,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        });

        infoStack.Children.Add(detailsStack);
        Grid.SetColumn(infoStack, 1);
        mainGrid.Children.Add(infoStack);

        var actionButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var customDrawer = new Border
        {
            Background = BrushDrawerBg,
            BorderBrush = BrushDrawerBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed
        };

        if (entry.Mode == SiteNotificationMode.Block)
        {
            var btnUnblock = new Button
            {
                Content = "Quitar bloqueo",
                Style = (Style)FindResource("LexGhostButton"),
                FontSize = 11,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand,
                ToolTip = "Eliminar de la lista de bloqueo (volver a preguntar en próximas visitas)"
            };
            btnUnblock.Click += (s, e) =>
            {
                NotificationPermissionManager.RemoveSite(entry.Origin);
                RefreshNotificationSitesList();
            };
            actionButtons.Children.Add(btnUnblock);

            var btnUnblockAndAllow = new Button
            {
                Content = "Desbloquear y Permitir",
                Style = (Style)FindResource("LexPrimaryButton"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 11,
                Padding = new Thickness(12, 5, 12, 5),
                Cursor = Cursors.Hand,
                ToolTip = "Habilitar notificaciones para este sitio"
            };
            btnUnblockAndAllow.Click += (s, e) =>
            {
                customDrawer.Visibility = customDrawer.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            };
            actionButtons.Children.Add(btnUnblockAndAllow);
        }
        else
        {
            var btnCustomize = new Button
            {
                Content = "⚙️ Reglas",
                Style = (Style)FindResource("LexGhostButton"),
                FontSize = 11,
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 4, 0),
                Cursor = Cursors.Hand,
                ToolTip = "Ajustar cuota diaria y modo de este sitio"
            };
            btnCustomize.Click += (s, e) =>
            {
                customDrawer.Visibility = customDrawer.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            };
            actionButtons.Children.Add(btnCustomize);

            var btnBlock = new Button
            {
                Content = "🚫",
                Style = (Style)FindResource("LexGhostButton"),
                FontSize = 11,
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 4, 0),
                Cursor = Cursors.Hand,
                ToolTip = "Bloquear notificaciones de este sitio"
            };
            btnBlock.Click += (s, e) =>
            {
                NotificationPermissionManager.SetSiteMode(entry.Origin, SiteNotificationMode.Block, isIncognito: false);
                RefreshNotificationSitesList();
            };
            actionButtons.Children.Add(btnBlock);

            var btnRemove = new Button
            {
                Content = "✕",
                Style = (Style)FindResource("LexGhostButton"),
                FontSize = 11,
                Padding = new Thickness(6, 4, 6, 4),
                Cursor = Cursors.Hand,
                ToolTip = "Restablecer a neutro (Preguntar)"
            };
            btnRemove.Click += (s, e) =>
            {
                NotificationPermissionManager.RemoveSite(entry.Origin);
                RefreshNotificationSitesList();
            };
            actionButtons.Children.Add(btnRemove);
        }

        Grid.SetColumn(actionButtons, 2);
        mainGrid.Children.Add(actionButtons);
        rootStack.Children.Add(mainGrid);

        var drawerStack = new StackPanel();

        var promptGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        promptGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        promptGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var promptText = new TextBlock
        {
            Text = $"¿Cómo deseas configurar las notificaciones para {host}?",
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(promptText, 0);
        promptGrid.Children.Add(promptText);

        var btnCloseDrawer = new Button
        {
            Content = "✕",
            Style = (Style)FindResource("LexGhostButton"),
            FontSize = 11,
            Padding = new Thickness(4, 2, 4, 2),
            Cursor = Cursors.Hand
        };
        btnCloseDrawer.Click += (s, e) => customDrawer.Visibility = Visibility.Collapsed;
        Grid.SetColumn(btnCloseDrawer, 1);
        promptGrid.Children.Add(btnCloseDrawer);
        drawerStack.Children.Add(promptGrid);

        var optionsGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        optionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        optionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var col1Stack = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
        col1Stack.Children.Add(new TextBlock
        {
            Text = "Límite diario de este sitio:",
            FontSize = 11,
            Foreground = BrushSecondaryFg,
            Margin = new Thickness(0, 0, 0, 3)
        });

        var cmbSiteLimit = new ComboBox
        {
            Height = 28,
            FontSize = 11,
            Background = BrushComboBg,
            Foreground = Brushes.White,
            BorderBrush = BrushComboBorder
        };
        cmbSiteLimit.Items.Add(new ComboBoxItem { Content = "Predeterminado (Global)", Tag = "0" });
        cmbSiteLimit.Items.Add(new ComboBoxItem { Content = "Máx 1 al día", Tag = "1" });
        cmbSiteLimit.Items.Add(new ComboBoxItem { Content = "Máx 2 al día", Tag = "2" });
        cmbSiteLimit.Items.Add(new ComboBoxItem { Content = "Máx 3 al día", Tag = "3" });
        cmbSiteLimit.Items.Add(new ComboBoxItem { Content = "Máx 5 al día", Tag = "5" });
        cmbSiteLimit.Items.Add(new ComboBoxItem { Content = "Máx 10 al día", Tag = "10" });
        SetComboByTag(cmbSiteLimit, entry.DailyLimit.ToString());
        col1Stack.Children.Add(cmbSiteLimit);
        Grid.SetColumn(col1Stack, 0);
        optionsGrid.Children.Add(col1Stack);

        var col2Stack = new StackPanel { Margin = new Thickness(6, 0, 0, 0) };
        col2Stack.Children.Add(new TextBlock
        {
            Text = "Modo de entrega:",
            FontSize = 11,
            Foreground = BrushSecondaryFg,
            Margin = new Thickness(0, 0, 0, 3)
        });

        var cmbSiteMode = new ComboBox
        {
            Height = 28,
            FontSize = 11,
            Background = BrushComboBg,
            Foreground = Brushes.White,
            BorderBrush = BrushComboBorder
        };
        cmbSiteMode.Items.Add(new ComboBoxItem { Content = "Aviso emergente (Toast)", Tag = "Allow" });
        cmbSiteMode.Items.Add(new ComboBoxItem { Content = "Solo silencioso (Centro)", Tag = "Silent" });
        string currentModeTag = entry.Mode == SiteNotificationMode.Silent ? "Silent" : "Allow";
        SetComboByTag(cmbSiteMode, currentModeTag);
        col2Stack.Children.Add(cmbSiteMode);
        Grid.SetColumn(col2Stack, 1);
        optionsGrid.Children.Add(col2Stack);

        drawerStack.Children.Add(optionsGrid);

        var drawerButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

        var btnApplyDefault = new Button
        {
            Content = "Usar estándar",
            Style = (Style)FindResource("LexGhostButton"),
            FontSize = 11,
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = Cursors.Hand
        };
        btnApplyDefault.Click += (s, e) =>
        {
            NotificationPermissionManager.UpdateSiteCustomization(entry.Origin, 0, SiteNotificationMode.Allow, soundEnabled: true, isIncognito: false);
            RefreshNotificationSitesList();
        };
        drawerButtons.Children.Add(btnApplyDefault);

        var btnSaveCustom = new Button
        {
            Content = "Guardar reglas",
            Style = (Style)FindResource("LexPrimaryButton"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
            Padding = new Thickness(10, 4, 10, 4),
            Cursor = Cursors.Hand
        };
        btnSaveCustom.Click += (s, e) =>
        {
            int siteLimit = 0;
            if (cmbSiteLimit.SelectedItem is ComboBoxItem limitItem && int.TryParse(limitItem.Tag?.ToString(), out int parsedLimit))
            {
                siteLimit = parsedLimit;
            }

            SiteNotificationMode targetMode = SiteNotificationMode.Allow;
            if (cmbSiteMode.SelectedItem is ComboBoxItem modeItem && string.Equals(modeItem.Tag?.ToString(), "Silent", StringComparison.OrdinalIgnoreCase))
            {
                targetMode = SiteNotificationMode.Silent;
            }

            NotificationPermissionManager.UpdateSiteCustomization(entry.Origin, siteLimit, targetMode, soundEnabled: true, isIncognito: false);
            RefreshNotificationSitesList();
        };
        drawerButtons.Children.Add(btnSaveCustom);

        drawerStack.Children.Add(drawerButtons);
        customDrawer.Child = drawerStack;
        rootStack.Children.Add(customDrawer);

        container.Child = rootStack;
        return container;
    }

    #endregion

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        GlobalScrollBehavior.HandleSmoothScrollViewerMouseWheel(sender, e, 28.0);
    }
}
