using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using LexBrowser.Core;

namespace LexBrowser.UI.Controls;

public partial class PrivacyOptionsPanelControl : UserControl
{
    private readonly string _radioGroupName = "AutoUpdateMode_" + Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, CheckBox> _toggleById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> _toggleStateLabelById = new(StringComparer.Ordinal);
    private readonly List<FrameworkElement> _restartBadges = new();

    private RadioButton? _rbAutoUpdateManual;
    private RadioButton? _rbAutoUpdateOnStartup;
    private Button? _btnCheckUpdatesNow;
    private TextBlock? _txtUpdateCheckStatus;
    private TextBlock? _txtSearchEngineTarget;

    private bool _isLoading;
    private bool _showEngineRestartTags;
    private bool _showManualUpdateCheckButton;
    private string _defaultSearchEngineName = "Google";
    private PrivacyProfileValues _currentValues = PrivacyProfiles.ProfileB;

    public event EventHandler<PrivacyProfileValues>? ValuesChanged;

    public PrivacyOptionsPanelControl()
    {
        InitializeComponent();
        BuildRows();
    }

    public PrivacyProfileValues CurrentValues => _currentValues;

    public bool ShowEngineRestartTags
    {
        get => _showEngineRestartTags;
        set
        {
            if (_showEngineRestartTags == value) return;
            _showEngineRestartTags = value;
            UpdateRestartBadgesVisibility();
        }
    }

    public bool ShowManualUpdateCheckButton
    {
        get => _showManualUpdateCheckButton;
        set
        {
            if (_showManualUpdateCheckButton == value) return;
            _showManualUpdateCheckButton = value;
            if (_btnCheckUpdatesNow != null)
            {
                _btnCheckUpdatesNow.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            }
            if (_txtUpdateCheckStatus != null && !value)
            {
                _txtUpdateCheckStatus.Visibility = Visibility.Collapsed;
            }
        }
    }

    public void LoadValues(PrivacyProfileValues values, string? defaultSearchEngine = null)
    {
        _isLoading = true;
        try
        {
            _currentValues = values with
            {
                AutoUpdateCheckMode = PrivacyProfiles.NormalizeAutoUpdateCheckMode(values.AutoUpdateCheckMode)
            };

            if (!string.IsNullOrWhiteSpace(defaultSearchEngine))
            {
                _defaultSearchEngineName = defaultSearchEngine;
            }

            foreach (var desc in PrivacyProfiles.OptionDescriptors)
            {
                if (desc.Kind == PrivacyOptionKind.Toggle &&
                    _toggleById.TryGetValue(desc.Id, out var chk))
                {
                    bool enabled = PrivacyProfiles.IsOptionEnabledInValue(desc.Id, _currentValues);
                    chk.IsChecked = enabled;
                    UpdateToggleStateLabel(desc.Id, enabled);
                }
            }

            bool isManual = string.Equals(
                PrivacyProfiles.NormalizeAutoUpdateCheckMode(_currentValues.AutoUpdateCheckMode),
                PrivacyProfiles.AutoUpdateManual,
                StringComparison.Ordinal);

            if (_rbAutoUpdateManual != null) _rbAutoUpdateManual.IsChecked = isManual;
            if (_rbAutoUpdateOnStartup != null) _rbAutoUpdateOnStartup.IsChecked = !isManual;

            if (_txtSearchEngineTarget != null)
            {
                _txtSearchEngineTarget.Text = $"Se enviaría a: {FormatSearchEngineDisplayName(_defaultSearchEngineName)} (cambia tu buscador en la pestaña Buscador).";
            }

            UpdateCoherenceNoticesUI();
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void BuildRows()
    {
        RowsHost.Children.Clear();
        _toggleById.Clear();
        _toggleStateLabelById.Clear();
        _restartBadges.Clear();

        Style? toggleStyle = TryFindResource("LexThemedToggle") as Style;
        Style? radioStyle = TryFindResource("LexThemedRadioButton") as Style;
        Style? secondaryBtnStyle = TryFindResource("LexThemedSecondaryButton") as Style;

        foreach (var desc in PrivacyProfiles.OptionDescriptors)
        {
            var card = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 13, 16, 13),
                Margin = new Thickness(0, 0, 0, 10)
            };
            card.SetResourceReference(Border.BackgroundProperty, "PanelCardBg");
            card.SetResourceReference(Border.BorderBrushProperty, "PanelBorder");

            var root = new StackPanel();

            // Cabecera de fila: Título + etiqueta opcional "Requiere reiniciar" + control derecha
            var topGrid = new Grid();
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };

            var titleRow = new WrapPanel { Orientation = Orientation.Horizontal };
            var txtTitle = new TextBlock
            {
                Text = desc.Title,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            txtTitle.SetResourceReference(TextBlock.ForegroundProperty, "PanelTextPrimary");
            titleRow.Children.Add(txtTitle);

            if (desc.RequiresEngineRestart)
            {
                var restartPill = new Border
                {
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(7, 2, 7, 2),
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Visibility = _showEngineRestartTags ? Visibility.Visible : Visibility.Collapsed
                };
                restartPill.SetResourceReference(Border.BackgroundProperty, "PanelWarningBadgeBg");

                var restartLabel = new TextBlock
                {
                    Text = "Requiere reiniciar",
                    FontSize = 10.5,
                    FontWeight = FontWeights.Bold
                };
                restartLabel.SetResourceReference(TextBlock.ForegroundProperty, "PanelWarningBadgeFg");
                restartPill.Child = restartLabel;

                _restartBadges.Add(restartPill);
                titleRow.Children.Add(restartPill);
            }

            titleStack.Children.Add(titleRow);

            var txtDesc = new TextBlock
            {
                Text = desc.ShortSummary,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            };
            txtDesc.SetResourceReference(TextBlock.ForegroundProperty, "PanelTextMuted");
            titleStack.Children.Add(txtDesc);

            Grid.SetColumn(titleStack, 0);
            topGrid.Children.Add(titleStack);

            // Control derecho según tipo de fila
            if (desc.Kind == PrivacyOptionKind.InformationalAlwaysActive)
            {
                var pill = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 4, 10, 4),
                    VerticalAlignment = VerticalAlignment.Center
                };
                pill.SetResourceReference(Border.BackgroundProperty, "PanelBadgeAllowBg");

                var pillText = new TextBlock
                {
                    Text = "Activo (siempre)",
                    FontSize = 11,
                    FontWeight = FontWeights.Bold
                };
                pillText.SetResourceReference(TextBlock.ForegroundProperty, "PanelBadgeAllowFg");
                pill.Child = pillText;

                Grid.SetColumn(pill, 1);
                topGrid.Children.Add(pill);
            }
            else if (desc.Kind == PrivacyOptionKind.Toggle)
            {
                var ctrlPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var stateLabel = new TextBlock
                {
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0),
                    MinWidth = 76,
                    TextAlignment = TextAlignment.Right
                };
                _toggleStateLabelById[desc.Id] = stateLabel;
                ctrlPanel.Children.Add(stateLabel);

                var chk = new CheckBox
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                if (toggleStyle != null) chk.Style = toggleStyle;
                System.Windows.Automation.AutomationProperties.SetName(chk, desc.Title);

                string capturedId = desc.Id;
                chk.Click += (_, _) => OnToggleClicked(capturedId, chk.IsChecked == true);

                _toggleById[desc.Id] = chk;
                ctrlPanel.Children.Add(chk);

                Grid.SetColumn(ctrlPanel, 1);
                topGrid.Children.Add(ctrlPanel);
            }

            root.Children.Add(topGrid);

            // Detalle extra para Sugerencias de búsqueda: buscador de solo lectura
            if (desc.Id == "SearchSuggestions")
            {
                var searchInfo = new TextBlock
                {
                    Text = $"Se enviaría a: {FormatSearchEngineDisplayName(_defaultSearchEngineName)} (cambia tu buscador en la pestaña Buscador).",
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0)
                };
                searchInfo.SetResourceReference(TextBlock.ForegroundProperty, "PanelAccentFg");
                _txtSearchEngineTarget = searchInfo;
                root.Children.Add(searchInfo);
            }

            // Bloque especial para Búsqueda de actualizaciones (2 radios + botón manual en Configuración)
            if (desc.Kind == PrivacyOptionKind.AutoUpdateRadio)
            {
                var radioPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 2) };

                var rbManual = new RadioButton
                {
                    GroupName = _radioGroupName,
                    Content = "Solo cuando yo lo pida (manual)",
                    FontSize = 12,
                    Margin = new Thickness(0, 2, 0, 5)
                };
                if (radioStyle != null) rbManual.Style = radioStyle;
                rbManual.Checked += (_, _) => OnAutoUpdateRadioChanged(PrivacyProfiles.AutoUpdateManual);

                var rbStartup = new RadioButton
                {
                    GroupName = _radioGroupName,
                    Content = "Al abrir Lex Junior (solo avisa; instalar siempre requiere tu confirmación)",
                    FontSize = 12,
                    Margin = new Thickness(0, 2, 0, 4)
                };
                if (radioStyle != null) rbStartup.Style = radioStyle;
                rbStartup.Checked += (_, _) => OnAutoUpdateRadioChanged(PrivacyProfiles.AutoUpdateOnStartup);

                _rbAutoUpdateManual = rbManual;
                _rbAutoUpdateOnStartup = rbStartup;

                radioPanel.Children.Add(rbManual);
                radioPanel.Children.Add(rbStartup);

                var actionRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 6, 0, 0)
                };

                var btnCheckNow = new Button
                {
                    Content = "🔄 Buscar actualizaciones ahora",
                    Padding = new Thickness(12, 6, 12, 6),
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Visibility = _showManualUpdateCheckButton ? Visibility.Visible : Visibility.Collapsed
                };
                if (secondaryBtnStyle != null) btnCheckNow.Style = secondaryBtnStyle;
                btnCheckNow.Click += BtnCheckUpdatesNow_Click;

                var txtStatus = new TextBlock
                {
                    FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(12, 0, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                    Visibility = Visibility.Collapsed
                };
                txtStatus.SetResourceReference(TextBlock.ForegroundProperty, "PanelTextMuted");

                _btnCheckUpdatesNow = btnCheckNow;
                _txtUpdateCheckStatus = txtStatus;

                actionRow.Children.Add(btnCheckNow);
                actionRow.Children.Add(txtStatus);
                radioPanel.Children.Add(actionRow);

                root.Children.Add(radioPanel);
            }

            // Ventaja y Desventaja propias de la fila
            var prosConsBox = new Border
            {
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 8, 0, 0)
            };
            prosConsBox.SetResourceReference(Border.BackgroundProperty, "PanelCardInsetBg");

            var prosConsStack = new StackPanel();

            var txtAdv = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 3) };
            var advBold = new System.Windows.Documents.Run("Ventaja: ") { FontWeight = FontWeights.Bold };
            advBold.SetResourceReference(System.Windows.Documents.Run.ForegroundProperty, "PanelSuccessFg");
            var advBody = new System.Windows.Documents.Run(desc.Advantage);
            advBody.SetResourceReference(System.Windows.Documents.Run.ForegroundProperty, "PanelTextBody");
            txtAdv.Inlines.Add(advBold);
            txtAdv.Inlines.Add(advBody);

            var txtDis = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap };
            var disBold = new System.Windows.Documents.Run("Desventaja: ") { FontWeight = FontWeights.Bold };
            disBold.SetResourceReference(System.Windows.Documents.Run.ForegroundProperty, "PanelWarningFg");
            var disBody = new System.Windows.Documents.Run(desc.Disadvantage);
            disBody.SetResourceReference(System.Windows.Documents.Run.ForegroundProperty, "PanelTextBody");
            txtDis.Inlines.Add(disBold);
            txtDis.Inlines.Add(disBody);

            prosConsStack.Children.Add(txtAdv);
            prosConsStack.Children.Add(txtDis);
            prosConsBox.Child = prosConsStack;
            root.Children.Add(prosConsBox);

            card.Child = root;
            RowsHost.Children.Add(card);
        }
    }

    private void OnToggleClicked(string optionId, bool isChecked)
    {
        if (_isLoading) return;
        _currentValues = PrivacyProfiles.WithOptionUpdated(_currentValues, optionId, isChecked);
        UpdateToggleStateLabel(optionId, isChecked);
        UpdateCoherenceNoticesUI();
        ValuesChanged?.Invoke(this, _currentValues);
    }

    private void OnAutoUpdateRadioChanged(string mode)
    {
        if (_isLoading) return;
        _currentValues = PrivacyProfiles.WithOptionUpdated(_currentValues, "AutoUpdates", enabled: mode == PrivacyProfiles.AutoUpdateOnStartup, autoUpdateMode: mode);
        UpdateCoherenceNoticesUI();
        ValuesChanged?.Invoke(this, _currentValues);
    }

    private void UpdateToggleStateLabel(string optionId, bool enabled)
    {
        if (!_toggleStateLabelById.TryGetValue(optionId, out var label))
            return;

        label.Text = enabled ? "Activado" : "Desactivado";
        label.SetResourceReference(TextBlock.ForegroundProperty, enabled ? "PanelSuccessFg" : "PanelTextMuted");
    }

    private void UpdateRestartBadgesVisibility()
    {
        Visibility vis = _showEngineRestartTags ? Visibility.Visible : Visibility.Collapsed;
        foreach (var badge in _restartBadges)
        {
            badge.Visibility = vis;
        }
    }

    private void UpdateCoherenceNoticesUI()
    {
        NoticesHost.Children.Clear();
        var notices = PrivacyProfiles.GetCoherenceNotices(_currentValues);
        if (notices.Count == 0)
        {
            BorderCoherenceNotices.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (string msg in notices)
        {
            var tb = new TextBlock
            {
                Text = "• " + msg,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2)
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "PanelTextBody");
            NoticesHost.Children.Add(tb);
        }

        BorderCoherenceNotices.Visibility = Visibility.Visible;
    }

    private async void BtnCheckUpdatesNow_Click(object sender, RoutedEventArgs e)
    {
        if (_btnCheckUpdatesNow == null || _txtUpdateCheckStatus == null) return;

        _btnCheckUpdatesNow.IsEnabled = false;
        _btnCheckUpdatesNow.Content = "⏳ Comprobando...";
        _txtUpdateCheckStatus.Text = "Consultando GitHub Releases...";
        _txtUpdateCheckStatus.SetResourceReference(TextBlock.ForegroundProperty, "PanelTextMuted");
        _txtUpdateCheckStatus.Visibility = Visibility.Visible;

        try
        {
            var update = await UpdateManager.CheckForUpdatesAsync(isUserInitiated: true);
            if (update != null)
            {
                _txtUpdateCheckStatus.Text = $"Hay una versión nueva disponible: {update.VersionTag} (puedes instalarla desde el botón Actualizar de la barra superior).";
                _txtUpdateCheckStatus.SetResourceReference(TextBlock.ForegroundProperty, "PanelAccentFg");
            }
            else
            {
                _txtUpdateCheckStatus.Text = $"Estás al día (v{UpdateManager.CurrentVersionString}).";
                _txtUpdateCheckStatus.SetResourceReference(TextBlock.ForegroundProperty, "PanelSuccessFg");
            }
        }
        catch
        {
            _txtUpdateCheckStatus.Text = "No se pudo consultar si hay actualizaciones. Verifica tu conexión a internet.";
            _txtUpdateCheckStatus.SetResourceReference(TextBlock.ForegroundProperty, "PanelWarningFg");
        }
        finally
        {
            _btnCheckUpdatesNow.Content = "🔄 Buscar actualizaciones ahora";
            _btnCheckUpdatesNow.IsEnabled = true;
        }
    }

    private static string FormatSearchEngineDisplayName(string? raw) => raw switch
    {
        "DuckDuckGo" => "DuckDuckGo",
        "Bing" => "Microsoft Bing",
        "Brave" => "Brave Search",
        "Custom" => "Buscador personalizado",
        _ => "Google"
    };
}
