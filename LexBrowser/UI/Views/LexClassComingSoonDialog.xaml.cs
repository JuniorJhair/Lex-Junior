using System;
using System.Windows;
using System.Windows.Input;
using LexBrowser.Core;

namespace LexBrowser.UI.Views;

public partial class LexClassComingSoonDialog : Window
{
    public const string VirtualUrl = "https://class.lex.local/index.html";

    public LexClassComingSoonDialog()
    {
        InitializeComponent();
        UpdateThemeStyles(SettingsManager.Current?.Theme);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                DialogResult = true;
                Close();
            }
        };
    }

    public static bool IsLexClassUrl(string? rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
            return false;

        string trimmed = rawInput.Trim();
        if (trimmed.Equals("lex://class", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("lex://class/", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("lex:class", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme.Equals("lex", StringComparison.OrdinalIgnoreCase) &&
                (uri.Host.Equals("class", StringComparison.OrdinalIgnoreCase) ||
                 uri.AbsolutePath.Trim('/').Equals("class", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if ((uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
                 uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)) &&
                uri.Host.Equals("class.lex.local", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public void UpdateThemeStyles(string? theme = null)
    {
        string target = BrowserThemeManager.NormalizeTheme(theme ?? SettingsManager.Current?.Theme);
        BrowserThemeManager.ApplyThemePalette(Resources, target);
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
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
