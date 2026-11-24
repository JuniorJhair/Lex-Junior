using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using LexBrowser.Core;

namespace LexBrowser.UI.Views;

public enum ReleaseNoteBlockKind
{
    Heading,
    Bullet,
    Paragraph,
    Divider
}

public enum ReleaseNoteSpanKind
{
    Normal,
    Bold,
    Code
}

public sealed record ReleaseNoteSpan(ReleaseNoteSpanKind Kind, string Text);

public sealed record ReleaseNoteBlock(ReleaseNoteBlockKind Kind, IReadOnlyList<ReleaseNoteSpan> Spans);

public partial class UpdatePromptDialog : Window
{
    private readonly UpdateReleaseInfo _update;
    private readonly Action<int>? _externalProgressCallback;
    private bool _isDownloading;

    private static readonly Regex MarkdownLinkRegex = new(
        @"\[(?<label>[^\]]+)\]\((?<url>[^)]+)\)",
        RegexOptions.Compiled);

    private static readonly Regex InlineTokenRegex = new(
        @"(\*\*(?<bold>[^*]+)\*\*|__(?<ubold>[^_]+)__|`(?<code>[^`]+)`)",
        RegexOptions.Compiled);

    private static readonly Regex NumberedListPrefixRegex = new(
        @"^\d+[\.\)]\s+",
        RegexOptions.Compiled);

    public UpdatePromptDialog(UpdateReleaseInfo update, Action<int>? externalProgressCallback = null)
    {
        _update = update ?? throw new ArgumentNullException(nameof(update));
        _externalProgressCallback = externalProgressCallback;

        InitializeComponent();
        UpdateThemeStyles(SettingsManager.Current?.Theme);

        string currentVer = UpdateManager.CurrentVersionString;
        string targetTag = _update.VersionTag.StartsWith("v", StringComparison.OrdinalIgnoreCase)
            ? _update.VersionTag
            : $"v{_update.VersionTag}";

        TxtHeadline.Text = $"¡Lex Junior {targetTag} está disponible!";
        TxtVersionBadge.Text = $"v{currentVer}  ➔  {targetTag}";

        if (_update.ExpectedSizeBytes > 0)
        {
            double mb = _update.ExpectedSizeBytes / (1024.0 * 1024.0);
            TxtIntegrityBadge.Text = $"🔒 Verificado SHA-256 · {mb:0.0} MB";
        }

        RenderReleaseNotes(_update.ReleaseNotes);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !_isDownloading)
            {
                e.Handled = true;
                DialogResult = false;
                Close();
            }
        };
    }

    public void UpdateThemeStyles(string? theme = null)
        => BrowserThemeManager.ApplyThemeToWindow(this, theme);

    public static IReadOnlyList<ReleaseNoteBlock> ParseReleaseNotesBlocks(string? rawMarkdown)
    {
        string input = string.IsNullOrWhiteSpace(rawMarkdown)
            ? "### Mejoras principales\n- Optimización general de **rendimiento**, **privacidad** y **estabilidad**.\n- Correcciones de seguridad y mejoras visuales en la interfaz."
            : rawMarkdown;

        var blocks = new List<ReleaseNoteBlock>();
        string[] lines = input.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            // Ignorar bloques de código triple backtick
            if (line.StartsWith("```", StringComparison.Ordinal))
                continue;

            // Separadores horizontales
            if (line == "---" || line == "***" || line == "___")
            {
                blocks.Add(new ReleaseNoteBlock(ReleaseNoteBlockKind.Divider, Array.Empty<ReleaseNoteSpan>()));
                continue;
            }

            // Encabezados (#, ##, ###, ####)
            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                string headingText = line.TrimStart('#').Trim();
                if (!string.IsNullOrWhiteSpace(headingText))
                {
                    blocks.Add(new ReleaseNoteBlock(ReleaseNoteBlockKind.Heading, ParseInlineSpans(headingText)));
                }
                continue;
            }

            // Viñetas (-, *, +, •) o listas numeradas (1. )
            if (line.StartsWith("- ", StringComparison.Ordinal) ||
                line.StartsWith("* ", StringComparison.Ordinal) ||
                line.StartsWith("+ ", StringComparison.Ordinal) ||
                line.StartsWith("• ", StringComparison.Ordinal))
            {
                string itemText = line.Substring(2).Trim();
                if (!string.IsNullOrWhiteSpace(itemText))
                {
                    blocks.Add(new ReleaseNoteBlock(ReleaseNoteBlockKind.Bullet, ParseInlineSpans(itemText)));
                }
                continue;
            }

            var numMatch = NumberedListPrefixRegex.Match(line);
            if (numMatch.Success)
            {
                string itemText = line.Substring(numMatch.Length).Trim();
                if (!string.IsNullOrWhiteSpace(itemText))
                {
                    blocks.Add(new ReleaseNoteBlock(ReleaseNoteBlockKind.Bullet, ParseInlineSpans(itemText)));
                }
                continue;
            }

            // Si una línea entera viene envuelta en **Título** como subtítulo
            if (line.StartsWith("**", StringComparison.Ordinal) &&
                line.EndsWith("**", StringComparison.Ordinal) &&
                line.Length > 4 &&
                line.IndexOf("**", 2, StringComparison.Ordinal) == line.Length - 2)
            {
                string headingText = line.Substring(2, line.Length - 4).Trim();
                blocks.Add(new ReleaseNoteBlock(ReleaseNoteBlockKind.Heading, ParseInlineSpans(headingText)));
                continue;
            }

            blocks.Add(new ReleaseNoteBlock(ReleaseNoteBlockKind.Paragraph, ParseInlineSpans(line)));
        }

        return blocks;
    }

    public static IReadOnlyList<ReleaseNoteSpan> ParseInlineSpans(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return Array.Empty<ReleaseNoteSpan>();

        // 1. Convertir enlaces [texto](url) -> texto
        string cleaned = MarkdownLinkRegex.Replace(rawText, "${label}");

        var spans = new List<ReleaseNoteSpan>();
        int lastIndex = 0;

        foreach (Match match in InlineTokenRegex.Matches(cleaned))
        {
            if (match.Index > lastIndex)
            {
                string before = SanitizeResidualMarkdownMarkers(cleaned.Substring(lastIndex, match.Index - lastIndex));
                if (!string.IsNullOrEmpty(before))
                    spans.Add(new ReleaseNoteSpan(ReleaseNoteSpanKind.Normal, before));
            }

            if (match.Groups["bold"].Success)
            {
                string boldText = SanitizeResidualMarkdownMarkers(match.Groups["bold"].Value);
                if (!string.IsNullOrEmpty(boldText))
                    spans.Add(new ReleaseNoteSpan(ReleaseNoteSpanKind.Bold, boldText));
            }
            else if (match.Groups["ubold"].Success)
            {
                string boldText = SanitizeResidualMarkdownMarkers(match.Groups["ubold"].Value);
                if (!string.IsNullOrEmpty(boldText))
                    spans.Add(new ReleaseNoteSpan(ReleaseNoteSpanKind.Bold, boldText));
            }
            else if (match.Groups["code"].Success)
            {
                string codeText = match.Groups["code"].Value.Trim();
                if (!string.IsNullOrEmpty(codeText))
                    spans.Add(new ReleaseNoteSpan(ReleaseNoteSpanKind.Code, codeText));
            }

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < cleaned.Length)
        {
            string tail = SanitizeResidualMarkdownMarkers(cleaned.Substring(lastIndex));
            if (!string.IsNullOrEmpty(tail))
                spans.Add(new ReleaseNoteSpan(ReleaseNoteSpanKind.Normal, tail));
        }

        return spans;
    }

    private static string SanitizeResidualMarkdownMarkers(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        // Eliminar cualquier asterisco doble o backtick suelto para que nunca se vea "**" en pantalla
        return text.Replace("**", "").Replace("__", "").Replace("`", "");
    }

    private void RenderReleaseNotes(string? rawMarkdown)
    {
        NotesContainer.Children.Clear();
        var blocks = ParseReleaseNotesBlocks(rawMarkdown);

        var brushStrong = TryFindResource("PanelTextStrong") as Brush ?? Brushes.White;
        var brushBody = TryFindResource("PanelTextBody") as Brush ?? Brushes.LightGray;
        var brushAccent = TryFindResource("PanelAccentFg") as Brush ?? Brushes.CornflowerBlue;
        var brushBorder = TryFindResource("PanelBorderSubtle") as Brush ?? Brushes.DimGray;
        var brushCodeBg = TryFindResource("PanelCardBg") as Brush ?? Brushes.Transparent;

        bool isFirstHeading = true;

        foreach (var block in blocks)
        {
            switch (block.Kind)
            {
                case ReleaseNoteBlockKind.Divider:
                {
                    var divider = new Border
                    {
                        Height = 1,
                        Background = brushBorder,
                        Margin = new Thickness(0, 8, 0, 8),
                        Opacity = 0.65
                    };
                    NotesContainer.Children.Add(divider);
                    break;
                }

                case ReleaseNoteBlockKind.Heading:
                {
                    var headerPanel = new Border
                    {
                        Background = brushCodeBg,
                        BorderBrush = brushBorder,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(7),
                        Padding = new Thickness(10, 6, 10, 6),
                        Margin = new Thickness(0, isFirstHeading ? 2 : 12, 0, 6)
                    };
                    isFirstHeading = false;

                    var tb = new TextBlock
                    {
                        FontSize = 12.8,
                        FontWeight = FontWeights.Bold,
                        Foreground = brushStrong,
                        TextWrapping = TextWrapping.Wrap
                    };
                    tb.Inlines.Add(new Run("✦ ") { Foreground = brushAccent, FontWeight = FontWeights.Bold });
                    AppendInlines(tb, block.Spans, brushStrong, brushStrong, brushAccent, brushCodeBg, forceBold: true);

                    headerPanel.Child = tb;
                    NotesContainer.Children.Add(headerPanel);
                    break;
                }

                case ReleaseNoteBlockKind.Bullet:
                {
                    var row = new Grid
                    {
                        Margin = new Thickness(4, 3, 2, 5)
                    };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    var bulletDot = new TextBlock
                    {
                        Text = "•",
                        FontSize = 14,
                        FontWeight = FontWeights.Bold,
                        Foreground = brushAccent,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(2, -1, 0, 0)
                    };
                    Grid.SetColumn(bulletDot, 0);

                    var tb = new TextBlock
                    {
                        FontSize = 12.2,
                        Foreground = brushBody,
                        TextWrapping = TextWrapping.Wrap,
                        LineHeight = 18.5
                    };
                    AppendInlines(tb, block.Spans, brushStrong, brushBody, brushAccent, brushCodeBg, forceBold: false);
                    Grid.SetColumn(tb, 1);

                    row.Children.Add(bulletDot);
                    row.Children.Add(tb);
                    NotesContainer.Children.Add(row);
                    break;
                }

                case ReleaseNoteBlockKind.Paragraph:
                {
                    var tb = new TextBlock
                    {
                        FontSize = 12.2,
                        Foreground = brushBody,
                        TextWrapping = TextWrapping.Wrap,
                        LineHeight = 18.5,
                        Margin = new Thickness(2, 3, 2, 6)
                    };
                    AppendInlines(tb, block.Spans, brushStrong, brushBody, brushAccent, brushCodeBg, forceBold: false);
                    NotesContainer.Children.Add(tb);
                    break;
                }
            }
        }
    }

    private static void AppendInlines(
        TextBlock target,
        IReadOnlyList<ReleaseNoteSpan> spans,
        Brush strongBrush,
        Brush bodyBrush,
        Brush accentBrush,
        Brush codeBgBrush,
        bool forceBold)
    {
        foreach (var span in spans)
        {
            if (span.Kind == ReleaseNoteSpanKind.Bold || forceBold)
            {
                target.Inlines.Add(new Run(span.Text)
                {
                    FontWeight = FontWeights.Bold,
                    Foreground = strongBrush
                });
            }
            else if (span.Kind == ReleaseNoteSpanKind.Code)
            {
                target.Inlines.Add(new Run($" {span.Text} ")
                {
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = accentBrush,
                    Background = codeBgBrush
                });
            }
            else
            {
                target.Inlines.Add(new Run(span.Text)
                {
                    Foreground = bodyBrush
                });
            }
        }
    }

    private async void BtnInstallNow_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloading) return;
        _isDownloading = true;

        BtnInstallNow.IsEnabled = false;
        BtnLater.IsEnabled = false;
        BtnClose.IsEnabled = false;
        DownloadProgressPanel.Visibility = Visibility.Visible;
        TxtDownloadStatus.Text = $"Descargando Lex Junior {_update.VersionTag}...";
        TxtDownloadPercent.Text = "0%";
        BarDownloadProgress.Value = 0;

        var progress = new Progress<int>(pct =>
        {
            int clamped = Math.Clamp(pct, 0, 100);
            BarDownloadProgress.Value = clamped;
            TxtDownloadPercent.Text = $"{clamped}%";
            TxtDownloadStatus.Text = clamped >= 100
                ? "Verificando integridad SHA-256 e iniciando instalador..."
                : $"Descargando instalador oficial ({_update.VersionTag})...";
            _externalProgressCallback?.Invoke(clamped);
        });

        bool ok = await UpdateManager.DownloadAndApplyUpdateAsync(_update, progress);
        if (!ok)
        {
            _isDownloading = false;
            BtnInstallNow.IsEnabled = true;
            BtnLater.IsEnabled = true;
            BtnClose.IsEnabled = true;
            TxtDownloadStatus.Text = "⚠️ No se pudo completar o verificar la descarga. Revisa tu conexión o inténtalo de nuevo.";
            TxtDownloadPercent.Text = "Error";
        }
    }

    private void BtnViewOnGitHub_Click(object sender, RoutedEventArgs e)
    {
        string targetUrl = !string.IsNullOrWhiteSpace(_update.HtmlUrl)
            ? _update.HtmlUrl
            : UpdateManager.LatestReleaseWebUrl;
        try
        {
            Process.Start(new ProcessStartInfo(targetUrl) { UseShellExecute = true });
        }
        catch { }
    }

    private void BtnLater_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloading) return;
        DialogResult = false;
        Close();
    }

    private void NotesScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        UI.GlobalScrollBehavior.HandleSmoothScrollViewerMouseWheel(sender, e, 32.0);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }
}
