using System;
using System.Windows;
using System.Windows.Input;
using LexBrowser.Core;

namespace LexBrowser.UI.Views;

public partial class LexClassComingSoonDialog : Window
{
    public const string VirtualUrl = LexClassStudioService.HomeUrl;

    public bool LaunchLexClassRequested { get; private set; }

    public LexClassComingSoonDialog()
    {
        InitializeComponent();
        UpdateThemeStyles(SettingsManager.Current?.Theme);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                DialogResult = false;
                Close();
            }
        };
    }

    public static bool IsLexClassUrl(string? rawInput) =>
        LexClassStudioService.IsClassUrl(rawInput);

    public void UpdateThemeStyles(string? theme = null)
    {
        string target = BrowserThemeManager.NormalizeTheme(theme ?? SettingsManager.Current?.Theme);
        BrowserThemeManager.ApplyThemePalette(Resources, target);
    }

    private void BtnOpenLexClass_Click(object sender, RoutedEventArgs e)
    {
        LaunchLexClassRequested = true;
        DialogResult = true;
        Close();
    }

    private void BtnInstallDesktopShortcut_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var installer = new InstallerWindow(isUninstallMode: false, isLexClassMode: true, shutdownOnComplete: false)
            {
                Owner = this
            };
            installer.ShowDialog();
        }
        catch { }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        LaunchLexClassRequested = false;
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
