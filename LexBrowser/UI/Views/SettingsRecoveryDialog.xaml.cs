using System;
using System.Windows;
using System.Windows.Input;
using LexBrowser.Core;

namespace LexBrowser.UI.Views;

public partial class SettingsRecoveryDialog : Window
{
    public bool ContinueWithRecovered { get; private set; }

    public SettingsRecoveryDialog(string? detailsMessage = null)
    {
        InitializeComponent();
        UpdateThemeStyles(SettingsManager.Current?.Theme);

        TxtRecoveryDetails.Text = string.IsNullOrWhiteSpace(detailsMessage)
            ? (SettingsManager.SettingsRecoveryWarningMessage ??
               "Se creó una copia preventiva del archivo dañado y se cargaron los valores válidos en memoria.")
            : detailsMessage;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                ChooseLeaveAsIs();
            }
        };
    }

    public void UpdateThemeStyles(string? theme = null)
    {
        string target = BrowserThemeManager.NormalizeTheme(theme ?? SettingsManager.Current?.Theme);
        BrowserThemeManager.ApplyThemePalette(Resources, target);
    }

    private void BtnContinueRecovered_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsManager.ResolveRecoveryAndAllowSaves())
        {
            SettingsManager.Save();
            ContinueWithRecovered = true;
        }
        else
        {
            ContinueWithRecovered = false;
        }

        DialogResult = ContinueWithRecovered;
        Close();
    }

    private void BtnLeaveAsIs_Click(object sender, RoutedEventArgs e)
    {
        ChooseLeaveAsIs();
    }

    private void ChooseLeaveAsIs()
    {
        ContinueWithRecovered = false;
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
