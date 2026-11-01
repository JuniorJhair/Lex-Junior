using System.Windows;
using LexBrowser.Core;

namespace LexBrowser.UI.Views;

public enum HistoryDisableChoice
{
    DeleteAndDisable,
    KeepAndDisable,
    Cancel
}

public partial class HistoryDisablePromptDialog : Window
{
    public HistoryDisableChoice Result { get; private set; } = HistoryDisableChoice.Cancel;

    public HistoryDisablePromptDialog()
    {
        InitializeComponent();
        TxtPagesCount.Text = $"{HistoryManager.GetTotalCount()} visitas";
        TxtStorageSize.Text = HistoryManager.GetHistoryFileSizeFormatted();
    }

    private void BtnDeleteAndDisable_Click(object sender, RoutedEventArgs e)
    {
        Result = HistoryDisableChoice.DeleteAndDisable;
        DialogResult = true;
        Close();
    }

    private void BtnKeepAndDisable_Click(object sender, RoutedEventArgs e)
    {
        Result = HistoryDisableChoice.KeepAndDisable;
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        Result = HistoryDisableChoice.Cancel;
        DialogResult = false;
        Close();
    }
}

