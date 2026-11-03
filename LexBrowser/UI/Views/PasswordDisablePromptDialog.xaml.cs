using System.Windows;
using LexBrowser.Core;

namespace LexBrowser.UI.Views;

public enum PasswordDisableChoice
{
    KeepAndDisable,
    DeleteAndDisable,
    Cancel
}

public partial class PasswordDisablePromptDialog : Window
{
    public PasswordDisableChoice Result { get; private set; } = PasswordDisableChoice.Cancel;

    public PasswordDisablePromptDialog()
    {
        InitializeComponent();
        int count = PasswordManager.Store.GetTotalCount();
        TxtSavedCountInfo.Text = count == 1
            ? "Tienes 1 contraseña guardada en tu bóveda cifrada."
            : $"Tienes {count} contraseñas guardadas en tu bóveda cifrada.";
    }

    private void BtnKeepAndDisable_Click(object sender, RoutedEventArgs e)
    {
        Result = PasswordDisableChoice.KeepAndDisable;
        DialogResult = true;
        Close();
    }

    private void BtnDeleteAndDisable_Click(object sender, RoutedEventArgs e)
    {
        Result = PasswordDisableChoice.DeleteAndDisable;
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        Result = PasswordDisableChoice.Cancel;
        DialogResult = false;
        Close();
    }
}

