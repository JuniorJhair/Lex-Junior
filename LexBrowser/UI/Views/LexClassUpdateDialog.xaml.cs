using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LexBrowser.Core;

namespace LexBrowser.UI.Views;

public enum LexClassUpdateDialogMode
{
    UpdatesAvailable,
    UpToDate,
    OfflineOrError
}

public partial class LexClassUpdateDialog : Window
{
    private readonly LexClassUpdateDialogMode _mode;
    private readonly List<LexClassPendingWorldInfo> _pendingWorlds;
    private readonly List<(LexClassPendingWorldInfo Info, CheckBox Box)> _worldChecks = new();
    private readonly Func<string, IProgress<(int Percent, string Stage)>?, Task<(bool Ok, string Message)>> _installWorldAsync;
    private bool _isBusy;
    private bool _completedUpdate;

    public bool WorldsUpdated { get; private set; }

    public LexClassUpdateDialog(
        LexClassUpdateDialogMode mode,
        IReadOnlyList<LexClassPendingWorldInfo>? pendingWorlds = null,
        string? customMessage = null,
        Func<string, IProgress<(int Percent, string Stage)>?, Task<(bool Ok, string Message)>>? installOverride = null)
    {
        _mode = mode;
        _pendingWorlds = pendingWorlds?.ToList() ?? new List<LexClassPendingWorldInfo>();
        _installWorldAsync = installOverride ?? ((id, prog) => LexClassStudioService.InstallOrUpdateWorldAsync(id, prog));

        InitializeComponent();
        ConfigureView(customMessage);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !_isBusy)
            {
                e.Handled = true;
                DialogResult = WorldsUpdated;
                Close();
            }
        };
    }

    private void ConfigureView(string? customMessage)
    {
        string displayVer = LexClassStudioService.GetEffectiveDisplayVersion();

        if (_mode == LexClassUpdateDialogMode.UpdatesAvailable && _pendingWorlds.Count > 0)
        {
            int updates = _pendingWorlds.Count(w => w.IsUpdate);
            int downloads = _pendingWorlds.Count - updates;
            string highestVer = _pendingWorlds
                .Select(w => w.RemoteVersion)
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? displayVer;

            TxtHeadline.Text = _pendingWorlds.Count == 1
                ? $"¡Actualización lista para {FormatWorldDisplayName(_pendingWorlds[0].Id)}!"
                : $"¡{_pendingWorlds.Count} paquetes listos para Lex Class!";
            TxtSubtitle.Text = "Se descargará únicamente el contenido de estudio (sin reinstalar el navegador).";
            TxtStatusBadge.Text = $"v{displayVer} ➔ v{highestVer}";
            TxtIntegrityFoot.Text = "🔒 Paquete verificado con SHA-256 · Se guarda offline";

            WorldsScroll.Visibility = Visibility.Visible;
            PopulatePendingCards();

            BtnLater.Visibility = Visibility.Visible;
            BtnLater.Content = "Más tarde";
            BtnPrimaryAction.Content = updates > 0 && downloads == 0
                ? "🔄 Actualizar ahora"
                : (updates == 0 && downloads > 0 ? "⬇ Descargar e instalar" : "🔄 Instalar actualizaciones");
            BtnPrimaryAction.IsEnabled = true;
        }
        else if (_mode == LexClassUpdateDialogMode.UpToDate)
        {
            TxtHeadline.Text = "✅ Tu academia ya está al día";
            TxtSubtitle.Text = string.IsNullOrWhiteSpace(customMessage)
                ? $"Todos tus mundos de Lex Junior Class están sincronizados en su versión más reciente (v{displayVer})."
                : customMessage;
            TxtStatusBadge.Text = $"Al día · v{displayVer}";
            TxtStatusBadge.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6EE7B7"));
            BadgeStatusPill.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#064E3B"));
            BadgeStatusPill.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));

            WorldsScroll.Visibility = Visibility.Collapsed;
            BtnLater.Visibility = Visibility.Collapsed;
            BtnPrimaryAction.Content = "✓ Entendido";
        }
        else
        {
            TxtHeadline.Text = "📡 Modo local 100 % offline";
            TxtSubtitle.Text = string.IsNullOrWhiteSpace(customMessage)
                ? "No se encontró una versión más nueva publicada o estás sin conexión a internet. Todo tu contenido instalado sigue funcionando sin conexión."
                : customMessage;
            TxtStatusBadge.Text = "Offline local";
            TxtStatusBadge.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FDE68A"));
            BadgeStatusPill.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3B2506"));
            BadgeStatusPill.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"));

            WorldsScroll.Visibility = Visibility.Collapsed;
            BtnLater.Visibility = Visibility.Collapsed;
            BtnPrimaryAction.Content = "✓ Entendido";
        }
    }

    private void PopulatePendingCards()
    {
        WorldsContainer.Children.Clear();
        _worldChecks.Clear();

        foreach (var item in _pendingWorlds)
        {
            var card = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#141A31")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D3558")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(13, 10, 13, 10),
                Margin = new Thickness(0, 0, 0, 8)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var cb = new CheckBox
            {
                IsChecked = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 11, 0),
                Cursor = Cursors.Hand
            };
            cb.Checked += (_, _) => UpdateInstallButtonEnabled();
            cb.Unchecked += (_, _) => UpdateInstallButtonEnabled();
            Grid.SetColumn(cb, 0);
            grid.Children.Add(cb);
            _worldChecks.Add((item, cb));

            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(new TextBlock
            {
                Text = $"{FormatWorldEmoji(item.Id)} {FormatWorldDisplayName(item.Id)}",
                FontSize = 13.5,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F8FAFC")),
                VerticalAlignment = VerticalAlignment.Center
            });

            string verText = !string.IsNullOrWhiteSpace(item.LocalVersion)
                ? $"v{item.LocalVersion} ➔ v{item.RemoteVersion}"
                : $"v{item.RemoteVersion}";
            var verBadge = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(item.IsUpdate ? "#2A1B0E" : "#0B2938")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(item.IsUpdate ? "#FB923C" : "#38BDF8")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(9, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = verText,
                    FontSize = 10.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(item.IsUpdate ? "#FDBA74" : "#7DD3FC"))
                }
            };
            titleRow.Children.Add(verBadge);
            textStack.Children.Add(titleRow);

            if (!string.IsNullOrWhiteSpace(item.Note))
            {
                textStack.Children.Add(new TextBlock
                {
                    Text = item.Note,
                    FontSize = 11.5,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CBD5E1")),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }

            Grid.SetColumn(textStack, 1);
            grid.Children.Add(textStack);

            if (!string.IsNullOrWhiteSpace(item.SizeLabel))
            {
                var sizeTxt = new TextBlock
                {
                    Text = item.SizeLabel,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#94A3B8")),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 0, 0)
                };
                Grid.SetColumn(sizeTxt, 2);
                grid.Children.Add(sizeTxt);
            }

            card.Child = grid;
            WorldsContainer.Children.Add(card);
        }
    }

    private void UpdateInstallButtonEnabled()
    {
        if (_isBusy || _completedUpdate || _mode != LexClassUpdateDialogMode.UpdatesAvailable)
            return;
        BtnPrimaryAction.IsEnabled = _worldChecks.Any(x => x.Box.IsChecked == true);
    }

    private async void BtnPrimaryAction_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        if (_mode != LexClassUpdateDialogMode.UpdatesAvailable || _completedUpdate)
        {
            DialogResult = WorldsUpdated;
            Close();
            return;
        }

        var selected = _worldChecks.Where(x => x.Box.IsChecked == true).Select(x => x.Info).ToList();
        if (selected.Count == 0) return;

        _isBusy = true;
        BtnPrimaryAction.IsEnabled = false;
        BtnLater.IsEnabled = false;
        BtnClose.IsEnabled = false;
        foreach (var (_, box) in _worldChecks) box.IsEnabled = false;

        ProgressPanel.Visibility = Visibility.Visible;
        BarProgress.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6366F1"));
        BarProgress.Value = 0;
        TxtProgressPercent.Text = "0%";
        TxtProgressStage.Text = "Conectando con GitHub Releases...";

        int total = selected.Count;
        string? lastError = null;
        int succeeded = 0;

        for (int i = 0; i < total; i++)
        {
            var world = selected[i];
            string displayName = FormatWorldDisplayName(world.Id);
            var progress = new Progress<(int Percent, string Stage)>(report =>
            {
                int overall = Math.Clamp((int)Math.Round(((i * 100.0) + report.Percent) / total), 0, 100);
                BarProgress.Value = overall;
                TxtProgressPercent.Text = $"{overall}%";
                TxtProgressStage.Text = string.IsNullOrWhiteSpace(report.Stage)
                    ? $"Actualizando {displayName}..."
                    : $"{displayName}: {report.Stage}";
            });

            var (ok, message) = await _installWorldAsync(world.Id, progress);
            if (ok)
            {
                succeeded++;
                WorldsUpdated = true;
            }
            else
            {
                lastError = message;
                break;
            }
        }

        _isBusy = false;
        BtnClose.IsEnabled = true;

        if (lastError == null && succeeded == total)
        {
            _completedUpdate = true;
            BarProgress.Value = 100;
            BarProgress.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
            TxtProgressPercent.Text = "100%";
            TxtProgressPercent.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6EE7B7"));
            TxtProgressStage.Text = "✅ Paquete verificado con SHA-256 e instalado offline.";

            string newVer = LexClassStudioService.GetEffectiveDisplayVersion();
            TxtHeadline.Text = "🎉 ¡Lex Junior Class actualizado!";
            TxtSubtitle.Text = $"Tu academia ya tiene lo último en modo 100 % local (v{newVer}). Pulsa Continuar para aplicar los cambios.";
            TxtStatusBadge.Text = $"Listo · v{newVer}";
            TxtStatusBadge.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6EE7B7"));
            BadgeStatusPill.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#064E3B"));
            BadgeStatusPill.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));

            BtnLater.Visibility = Visibility.Collapsed;
            BtnPrimaryAction.Content = "✨ Continuar";
            BtnPrimaryAction.IsEnabled = true;
        }
        else
        {
            BarProgress.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F43F5E"));
            TxtProgressPercent.Text = "Error";
            TxtProgressPercent.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FDA4AF"));
            TxtProgressStage.Text = $"⚠️ {lastError ?? "No se pudo completar la actualización."}";

            foreach (var (_, box) in _worldChecks) box.IsEnabled = true;
            BtnLater.IsEnabled = true;
            BtnPrimaryAction.Content = "↻ Reintentar";
            BtnPrimaryAction.IsEnabled = true;
        }
    }

    internal static string FormatWorldDisplayName(string worldId) => worldId?.Trim().ToLowerInvariant() switch
    {
        "matematicas" => "Matemáticas",
        "idiomas" => "Idiomas (Inglés + LexBook)",
        "chino" => "Chino Mandarín",
        "quechua" => "Quechua",
        "ciencias_naturales" or "ciencias-naturales" => "Ciencias Naturales",
        "ciencias_sociales" or "ciencias-sociales" => "Ciencias Sociales",
        "comunicacion" => "Comunicación",
        "programacion" => "Programación",
        _ => string.IsNullOrWhiteSpace(worldId) ? "Mundo" : char.ToUpperInvariant(worldId[0]) + worldId[1..]
    };

    private static string FormatWorldEmoji(string worldId) => worldId?.Trim().ToLowerInvariant() switch
    {
        "matematicas" => "🪐",
        "idiomas" => "🌍",
        "comunicacion" => "📝",
        "programacion" => "💻",
        _ => "✨"
    };

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        DialogResult = WorldsUpdated;
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
