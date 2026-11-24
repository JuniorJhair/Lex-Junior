using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LexBrowser.Core;

namespace LexBrowser.UI.Views;

public partial class PrivacyProfileSetupWindow : Window
{
    private readonly bool _isFreshInstall;
    private readonly bool _isEnvironmentAlreadyCreated;
    private readonly bool _isReopenedManually;
    private readonly PrivacyProfileValues _initialExistingValues;

    private bool _isInitializing = true;
    private string _selectedCardKey = PrivacyProfiles.ProfileKeyB; // "A" | "B" | "C" | "Personalizado"
    private PrivacyProfileValues _draftCustomValues;
    private bool _hasEditedDraftInCustom;
    private bool _confirmedByUser;

    public bool ClosedWithoutChoice { get; private set; }
    public bool SettingsWereChanged { get; private set; }
    public string ActiveResultProfileKey { get; private set; } = PrivacyProfiles.ProfileKeyB;

    public PrivacyProfileSetupWindow(
        bool isFreshInstall,
        bool isEnvironmentAlreadyCreated,
        bool isReopenedManually = false)
    {
        _isFreshInstall = isFreshInstall;
        _isEnvironmentAlreadyCreated = isEnvironmentAlreadyCreated;
        _isReopenedManually = isReopenedManually;

        _initialExistingValues = PrivacyProfiles.FromSettings(SettingsManager.Current);

        InitializeComponent();
        UpdateThemeStyles(SettingsManager.Current?.Theme);

        CustomOptionsPanel.ShowEngineRestartTags = _isEnvironmentAlreadyCreated;
        CustomOptionsPanel.ShowManualUpdateCheckButton = false;
        CustomOptionsPanel.ValuesChanged += CustomOptionsPanel_ValuesChanged;

        if (_isEnvironmentAlreadyCreated)
        {
            TxtRestartHintOnExistingEngine.Visibility = Visibility.Visible;
        }

        ConfigureInitialState();
        BuildComparisonTable();

        _isInitializing = false;
        RefreshSelectedCardUI();

        Loaded += Window_Loaded;
        PreviewKeyDown += Window_PreviewKeyDown;
        Closing += Window_Closing;
    }

    public void UpdateThemeStyles(string? theme = null)
        => BrowserThemeManager.ApplyThemeToWindow(this, theme);

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var wa = SystemParameters.WorkArea;
            if (wa.Width > 0 && wa.Height > 0)
            {
                Width = Math.Max(MinWidth, Math.Min(980, wa.Width * 0.94));
                Height = Math.Max(MinHeight, Math.Min(700, wa.Height * 0.92));
                Left = wa.Left + Math.Max(0, (wa.Width - Width) / 2);
                Top = wa.Top + Math.Max(0, (wa.Height - Height) / 2);
            }
        }
        catch { }

        var initialCard = _selectedCardKey switch
        {
            PrivacyProfiles.ProfileKeyA => CardA,
            PrivacyProfiles.ProfileKeyB => CardB,
            PrivacyProfiles.ProfileKeyC => CardC,
            _ => CardD
        };
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => initialCard.Focus());
    }

    private void ConfigureInitialState()
    {
        if (_isFreshInstall && !_isReopenedManually)
        {
            _selectedCardKey = PrivacyProfiles.ProfileKeyB;
            _draftCustomValues = PrivacyProfiles.ProfileB;
            CardB.IsChecked = true;

            BtnKeepExisting.Visibility = Visibility.Collapsed;
            TxtUpgradeDataNotice.Visibility = Visibility.Collapsed;
            BadgeUpgradeCurrentProfile.Visibility = Visibility.Collapsed;
        }
        else
        {
            string currentResolved = PrivacyProfiles.ResolveActiveProfile(_initialExistingValues);
            _draftCustomValues = _initialExistingValues;

            BadgeUpgradeCurrentProfile.Visibility = Visibility.Visible;
            TxtUpgradeCurrentProfileBadge.Text = currentResolved == PrivacyProfiles.ProfileKeyCustom
                ? "Tu configuración actual: Personalizado"
                : $"Tu configuración actual: {PrivacyProfiles.GetProfileDisplayName(currentResolved)}";

            if (!_isReopenedManually)
            {
                TxtUpgradeDataNotice.Visibility = Visibility.Visible;
                BtnKeepExisting.Visibility = Visibility.Visible;
                BtnKeepExisting.IsDefault = true;
                // Usar estilo secundario para el segundo botón cuando Conservar es primario por defecto
                if (TryFindResource("LexThemedSecondaryButton") is Style secStyle)
                {
                    BtnConfirmSelection.Style = secStyle;
                }
            }
            else
            {
                BtnKeepExisting.Visibility = Visibility.Visible;
                BtnKeepExisting.Content = "Cerrar sin cambios";
                if (TryFindResource("LexThemedSecondaryButton") is Style secStyle)
                {
                    BtnKeepExisting.Style = secStyle;
                }
            }

            _selectedCardKey = currentResolved;
            switch (currentResolved)
            {
                case PrivacyProfiles.ProfileKeyA:
                    CardA.IsChecked = true;
                    break;
                case PrivacyProfiles.ProfileKeyB:
                    CardB.IsChecked = true;
                    break;
                case PrivacyProfiles.ProfileKeyC:
                    CardC.IsChecked = true;
                    break;
                default:
                    CardD.IsChecked = true;
                    break;
            }
        }

        CustomOptionsPanel.LoadValues(_draftCustomValues, SettingsManager.Current.DefaultSearchEngine);
    }

    private void BuildComparisonTable()
    {
        ComparisonTableGrid.Children.Clear();
        ComparisonTableGrid.ColumnDefinitions.Clear();
        ComparisonTableGrid.RowDefinitions.Clear();

        bool showCurrentColumn = !_isFreshInstall || _isReopenedManually;

        ComparisonTableGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
        ComparisonTableGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ComparisonTableGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ComparisonTableGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (showCurrentColumn)
        {
            ComparisonTableGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        int rowIdx = 0;
        ComparisonTableGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddHeaderCell(rowIdx, 0, "Función");
        AddHeaderCell(rowIdx, 1, "Perfil A (Estricta)");
        AddHeaderCell(rowIdx, 2, "Perfil B (Equilibrado)");
        AddHeaderCell(rowIdx, 3, "Perfil C (Completo)");
        if (showCurrentColumn)
        {
            AddHeaderCell(rowIdx, 4, "Tu configuración actual");
        }
        rowIdx++;

        // Fila superior exigida en 6.3d
        ComparisonTableGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddTableLabelCell(rowIdx, 0, "Telemetría propia de Lex Junior", subtleRow: true);
        AddTableBadgeCell(rowIdx, 1, "No", isPositive: true, isNegative: true, subtleRow: true, neutralZero: true);
        AddTableBadgeCell(rowIdx, 2, "No", isPositive: true, isNegative: true, subtleRow: true, neutralZero: true);
        AddTableBadgeCell(rowIdx, 3, "No", isPositive: true, isNegative: true, subtleRow: true, neutralZero: true);
        if (showCurrentColumn)
        {
            AddTableBadgeCell(rowIdx, 4, "No", isPositive: true, isNegative: true, subtleRow: true, neutralZero: true);
        }
        rowIdx++;

        foreach (var desc in PrivacyProfiles.OptionDescriptors)
        {
            bool subtle = (rowIdx % 2 == 1);
            ComparisonTableGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddTableLabelCell(rowIdx, 0, desc.TableLabel, subtleRow: subtle);
            AddTableBadgeCellForText(rowIdx, 1, desc.ValueA, subtle);
            AddTableBadgeCellForText(rowIdx, 2, desc.ValueB, subtle);
            AddTableBadgeCellForText(rowIdx, 3, desc.ValueC, subtle);
            if (showCurrentColumn)
            {
                string currentLabel = PrivacyProfiles.GetOptionValueLabel(desc.Id, _initialExistingValues);
                AddTableBadgeCellForText(rowIdx, 4, currentLabel, subtle);
            }

            rowIdx++;
        }
    }

    private void AddHeaderCell(int row, int col, string text)
    {
        var border = new Border
        {
            Padding = new Thickness(8, 7, 8, 7),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
        border.SetResourceReference(Border.BorderBrushProperty, "PanelBorder");

        var tb = new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = col == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Center,
            TextAlignment = col == 0 ? TextAlignment.Left : TextAlignment.Center
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "PanelTextStrong");

        border.Child = tb;
        Grid.SetRow(border, row);
        Grid.SetColumn(border, col);
        ComparisonTableGrid.Children.Add(border);
    }

    private void AddTableLabelCell(int row, int col, string text, bool subtleRow)
    {
        var border = new Border
        {
            Padding = new Thickness(8, 7, 8, 7),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
        border.SetResourceReference(Border.BorderBrushProperty, "PanelBorderSubtle");
        if (subtleRow)
            border.SetResourceReference(Border.BackgroundProperty, "PanelCardInsetBg");

        var tb = new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "PanelTextPrimary");

        border.Child = tb;
        Grid.SetRow(border, row);
        Grid.SetColumn(border, col);
        ComparisonTableGrid.Children.Add(border);
    }

    private void AddTableBadgeCellForText(int row, int col, string text, bool subtleRow)
    {
        bool isOn = text is "Activado" or "Activo" or "Al iniciar";
        bool isManual = text == "Manual";
        AddTableBadgeCell(row, col, text, isPositive: isOn, isNegative: !isOn && !isManual, subtleRow: subtleRow, neutralZero: isManual);
    }

    private void AddTableBadgeCell(int row, int col, string text, bool isPositive, bool isNegative, bool subtleRow, bool neutralZero = false)
    {
        var cell = new Border
        {
            Padding = new Thickness(6, 6, 6, 6),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
        cell.SetResourceReference(Border.BorderBrushProperty, "PanelBorderSubtle");
        if (subtleRow)
            cell.SetResourceReference(Border.BackgroundProperty, "PanelCardInsetBg");

        string bgKey = neutralZero ? "PanelBadgeAskBg" : (isPositive ? "PanelBadgeAllowBg" : "PanelBadgeBlockBg");
        string fgKey = neutralZero ? "PanelBadgeAskFg" : (isPositive ? "PanelBadgeAllowFg" : "PanelBadgeBlockFg");

        var pill = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 2, 8, 2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        pill.SetResourceReference(Border.BackgroundProperty, bgKey);

        var tb = new TextBlock
        {
            Text = text,
            FontSize = 10.5,
            FontWeight = FontWeights.Bold
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, fgKey);
        pill.Child = tb;

        cell.Child = pill;
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, col);
        ComparisonTableGrid.Children.Add(cell);
    }

    private void ProfileCard_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        if (sender == CardA) _selectedCardKey = PrivacyProfiles.ProfileKeyA;
        else if (sender == CardB) _selectedCardKey = PrivacyProfiles.ProfileKeyB;
        else if (sender == CardC) _selectedCardKey = PrivacyProfiles.ProfileKeyC;
        else if (sender == CardD)
        {
            _selectedCardKey = PrivacyProfiles.ProfileKeyCustom;
            if (!_hasEditedDraftInCustom)
            {
                _draftCustomValues = _isFreshInstall && !_isReopenedManually
                    ? PrivacyProfiles.ProfileB
                    : _initialExistingValues;
                CustomOptionsPanel.LoadValues(_draftCustomValues, SettingsManager.Current.DefaultSearchEngine);
            }
        }

        RefreshSelectedCardUI();
    }

    private void BtnCustomizeFromProfile_Click(object sender, RoutedEventArgs e)
    {
        // Partir exactamente del perfil que tenía seleccionado el usuario al abrir "Personalizar estas opciones" (6.6)
        _draftCustomValues = PrivacyProfiles.GetValuesForProfileKey(_selectedCardKey);
        _hasEditedDraftInCustom = true;
        CustomOptionsPanel.LoadValues(_draftCustomValues, SettingsManager.Current.DefaultSearchEngine);
        _selectedCardKey = PrivacyProfiles.ProfileKeyCustom;
        CardD.IsChecked = true;
        RefreshSelectedCardUI();
    }

    private void CustomOptionsPanel_ValuesChanged(object? sender, PrivacyProfileValues updated)
    {
        if (_isInitializing) return;
        _draftCustomValues = updated;
        _hasEditedDraftInCustom = true;
        RefreshSelectedCardUI();
    }

    private void RefreshSelectedCardUI()
    {
        if (_selectedCardKey == PrivacyProfiles.ProfileKeyCustom)
        {
            PanelSelectedProsCons.Visibility = Visibility.Collapsed;
            PanelCustomOptionsContainer.Visibility = Visibility.Visible;

            string deduced = PrivacyProfiles.ResolveActiveProfile(_draftCustomValues);
            TxtDraftResolvedProfileBadge.Text = deduced == PrivacyProfiles.ProfileKeyCustom
                ? "Estado calculado: Personalizado"
                : $"Estado calculado: {PrivacyProfiles.GetProfileDisplayName(deduced)}";

            UpdateConfirmButtonLabels(deduced);
            return;
        }

        PanelSelectedProsCons.Visibility = Visibility.Visible;
        PanelCustomOptionsContainer.Visibility = Visibility.Collapsed;

        switch (_selectedCardKey)
        {
            case PrivacyProfiles.ProfileKeyA:
                TxtSelectedProfileTitle.Text = "Perfil A — Privacidad estricta";
                TxtProfileAdvantages.Text =
                    "Lex Junior no hace consultas opcionales a servicios de seguridad, iconos ni sugerencias; tus dominios no se envían a Google para obtener iconos; lo que escribes en la barra no se envía al buscador mientras escribes; menos conexiones iniciadas por Lex Junior.";
                TxtProfileDisadvantages.Text =
                    "Sin protección SmartScreen ni detección de phishing del navegador, por lo que tienes menos protección ante páginas y descargas maliciosas; menos iconos disponibles (verás la letra inicial); sin sugerencias en vivo; las actualizaciones se buscan solo cuando tú lo pidas.";
                TxtProfileSpecificNote.Text =
                    "Nota: Esto no significa cero conexiones: las páginas que visites y el motor WebView2 siguen funcionando con normalidad.";
                TxtProfileSpecificNote.Visibility = Visibility.Visible;
                break;

            case PrivacyProfiles.ProfileKeyB:
                TxtSelectedProfileTitle.Text = "Perfil B — Equilibrado (Recomendado)";
                TxtProfileAdvantages.Text =
                    "Solicita al motor WebView2 habilitar SmartScreen (Microsoft) para páginas y descargas (efecto efectivo en red sujeto al runtime WebView2 instalado); iconos propios de cada sitio sin pasar por Google; te avisa cuando hay una versión nueva (solo se instala si tú confirmas).";
                TxtProfileDisadvantages.Text =
                    "Si el runtime aplica SmartScreen, consulta a Microsoft; el icono /favicon.ico se pide directamente al sitio; se consulta GitHub al abrir para avisar de versiones; sin sugerencias en vivo por defecto.";
                TxtProfileSpecificNote.Text = _isEnvironmentAlreadyCreated
                    ? "Aviso de motor: SmartScreen y detección de phishing en el navegador requieren reiniciar Lex Junior porque el motor WebView2 ya fue iniciado."
                    : "Nota técnica: Las opciones de SmartScreen y detección de phishing controlan los flags de inicio de WebView2; su alcance efectivo en red depende del runtime WebView2.";
                TxtProfileSpecificNote.Visibility = Visibility.Visible;
                break;

            case PrivacyProfiles.ProfileKeyC:
                TxtSelectedProfileTitle.Text = "Perfil C — Experiencia completa";
                TxtProfileAdvantages.Text =
                    "Más iconos disponibles (incluye Google Favicon API); sugerencias de búsqueda mientras escribes; solicita habilitar detección adicional de phishing en el motor junto con SmartScreen (sujeto al runtime WebView2); avisos de actualización.";
                TxtProfileDisadvantages.Text =
                    "Lo que escribes se envía al buscador elegido para mostrar sugerencias; dominios sin icono se consultan a Google; puede haber consultas de seguridad a Microsoft y Google según lo que aplique WebView2.";
                TxtProfileSpecificNote.Text = _isEnvironmentAlreadyCreated
                    ? "Aviso de motor: SmartScreen y detección de phishing en el navegador requieren reiniciar Lex Junior porque el motor WebView2 ya fue iniciado."
                    : "Nota técnica: Las opciones de SmartScreen y detección de phishing controlan los flags de inicio de WebView2; su alcance efectivo en red depende del runtime WebView2.";
                TxtProfileSpecificNote.Visibility = Visibility.Visible;
                break;
        }

        UpdateConfirmButtonLabels(_selectedCardKey);
    }

    private void UpdateConfirmButtonLabels(string targetProfileKey)
    {
        string displayName = PrivacyProfiles.GetProfileDisplayName(targetProfileKey);
        if (_isFreshInstall && !_isReopenedManually)
        {
            BtnConfirmSelection.Content = $"Continuar con {displayName}";
            TxtFooterSummary.Text = $"Selección lista para aplicar al iniciar: {displayName}";
        }
        else
        {
            BtnConfirmSelection.Content = $"Aplicar {displayName}";
            TxtFooterSummary.Text = _isReopenedManually
                ? $"Selección actual en comparativa: {displayName}"
                : $"Puedes conservar tu configuración tal como está o aplicar: {displayName}";
        }
    }

    private PrivacyProfileValues GetSelectedTargetValues()
    {
        return _selectedCardKey == PrivacyProfiles.ProfileKeyCustom
            ? _draftCustomValues
            : PrivacyProfiles.GetValuesForProfileKey(_selectedCardKey);
    }

    private void BtnKeepExisting_Click(object sender, RoutedEventArgs e)
    {
        _confirmedByUser = true;
        ClosedWithoutChoice = false;
        SettingsWereChanged = false;

        if (!_isReopenedManually)
        {
            PrivacyProfiles.ConfirmOnboardingSelection(SettingsManager.Current, selectedValuesToApply: null);
            SaveSettingsRespectingRecovery();
        }

        ActiveResultProfileKey = PrivacyProfiles.ResolveActiveProfile(SettingsManager.Current);
        DialogResult = true;
        Close();
    }

    private void BtnConfirmSelection_Click(object sender, RoutedEventArgs e)
    {
        var chosenValues = GetSelectedTargetValues();
        var prevValues = PrivacyProfiles.FromSettings(SettingsManager.Current);

        _confirmedByUser = true;
        ClosedWithoutChoice = false;
        SettingsWereChanged = !prevValues.EqualsProfile(chosenValues);

        PrivacyProfiles.ConfirmOnboardingSelection(SettingsManager.Current, chosenValues);
        SaveSettingsRespectingRecovery();

        if (SettingsWereChanged)
        {
            LexBrowser.Core.Engine.FaviconService.ClearCache();
        }

        ActiveResultProfileKey = PrivacyProfiles.ResolveActiveProfile(SettingsManager.Current);
        DialogResult = true;
        Close();
    }

    private static void SaveSettingsRespectingRecovery()
    {
        if (SettingsManager.HadUnreadableSettingsFile && SettingsManager.AutoSavesSuspended)
        {
            if (SettingsManager.ResolveRecoveryAndAllowSaves())
            {
                SettingsManager.Save();
            }
            return;
        }

        SettingsManager.Save();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_confirmedByUser) return;

        if (_isReopenedManually)
        {
            ClosedWithoutChoice = false;
            SettingsWereChanged = false;
            ActiveResultProfileKey = PrivacyProfiles.ResolveActiveProfile(SettingsManager.Current);
            return;
        }

        ClosedWithoutChoice = true;
        bool changed = PrivacyProfiles.ResolveClosedWithoutChoice(SettingsManager.Current, _isFreshInstall);
        SettingsWereChanged = changed;
        SaveSettingsRespectingRecovery();
        ActiveResultProfileKey = PrivacyProfiles.ResolveActiveProfile(SettingsManager.Current);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }

        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            var focused = Keyboard.FocusedElement as DependencyObject;
            var cards = new[] { CardA, CardB, CardC, CardD };
            int idx = -1;
            for (int i = 0; i < cards.Length; i++)
            {
                if (ReferenceEquals(focused, cards[i]) || (focused != null && cards[i].IsAncestorOf(focused)))
                {
                    idx = i;
                    break;
                }
            }

            if (idx >= 0)
            {
                int nextIdx = (e.Key is Key.Right or Key.Down)
                    ? (idx + 1) % cards.Length
                    : (idx - 1 + cards.Length) % cards.Length;

                cards[nextIdx].IsChecked = true;
                cards[nextIdx].Focus();
                e.Handled = true;
            }
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        LexBrowser.UI.GlobalScrollBehavior.HandleSmoothScrollViewerMouseWheel(sender, e, 28.0);
    }
}
