using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using LexBrowser.Core;
using LexBrowser.Core.Import;
using Microsoft.Win32;

namespace LexBrowser.UI.Views;

public partial class ImportWizardWindow : Window
{
    private CancellationTokenSource? _discoveryCts;
    private CancellationTokenSource? _importCts;
    private bool _isImporting;
    private bool _isClosing;

    public ImportWizardWindow()
    {
        InitializeComponent();
        Loaded += ImportWizardWindow_Loaded;
        Closing += ImportWizardWindow_Closing;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private bool IsRestrictedSession()
    {
        if (Owner is MainWindow mw) return mw.IsPrivateImportRestricted;
        if (Owner is Window w && w.Owner is MainWindow parentMw) return parentMw.IsPrivateImportRestricted;
        return false;
    }

    private async void ImportWizardWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (IsRestrictedSession())
        {
            TxtProfileStatus.Text = "La importación de datos no está disponible en Modo Invitado ni en navegación Incógnito.";
            BtnImportProfile.IsEnabled = false;
            BtnImportCsv.IsEnabled = false;
            ChkImportBookmarks.IsEnabled = false;
            ChkImportHistory.IsEnabled = false;
            ChkImportPasswords.IsEnabled = false;
            return;
        }

        if (!SettingsManager.Current.EnableHistory)
        {
            ChkImportHistory.IsChecked = false;
            TxtHistoryHint.Text = "El historial está desactivado en Configuración. Actívalo primero si deseas importarlo.";
        }

        _discoveryCts?.Cancel();
        _discoveryCts?.Dispose();
        _discoveryCts = new CancellationTokenSource();
        var token = _discoveryCts.Token;

        try
        {
            var warnings = new List<string>();
            var profiles = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                return BrowserProfileLocator.DiscoverProfiles(warnings: warnings);
            }, token);

            if (_isClosing || token.IsCancellationRequested) return;

            CmbProfiles.ItemsSource = profiles;
            if (profiles.Count > 0)
            {
                CmbProfiles.IsEnabled = true;
                CmbProfiles.SelectedIndex = 0;
                TxtProfileStatus.Text = $"Se detectaron {profiles.Count} perfiles compatibles.";
            }
            else
            {
                CmbProfiles.IsEnabled = false;
                BtnImportProfile.IsEnabled = false;
                TxtProfileStatus.Text = "No se detectaron perfiles locales de Chrome, Edge, Brave, Vivaldi u Opera. Puedes usar «Importar desde archivo CSV…».";
            }

            if (warnings.Count > 0)
            {
                TxtProfileStatus.Text += " (" + string.Join(" ", warnings) + ")";
            }
        }
        catch (OperationCanceledException)
        {
            // Ventana cerrada durante descubrimiento
        }
        catch (Exception ex)
        {
            if (!_isClosing)
            {
                TxtProfileStatus.Text = $"Error al buscar perfiles: {ex.Message}";
            }
        }
    }

    private void ImportWizardWindow_Closing(object? sender, CancelEventArgs e)
    {
        _isClosing = true;
        try { _discoveryCts?.Cancel(); } catch { }
        try { _importCts?.Cancel(); } catch { }
        _discoveryCts?.Dispose();
        _discoveryCts = null;
        _importCts?.Dispose();
        _importCts = null;
    }

    private void CmbProfiles_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateCategoryAvailability();
    }

    private void UpdateCategoryAvailability()
    {
        if (CmbProfiles.SelectedItem is not BrowserProfileInfo profile || _isImporting)
        {
            BtnImportProfile.IsEnabled = false;
            return;
        }

        ChkImportBookmarks.IsEnabled = profile.HasBookmarks;
        if (!profile.HasBookmarks) ChkImportBookmarks.IsChecked = false;
        TxtBookmarksHint.Text = profile.HasBookmarks
            ? "Conserva los existentes sin duplicar URLs."
            : "No se encontró el archivo Bookmarks en este perfil.";

        ChkImportHistory.IsEnabled = profile.HasHistory;
        if (!profile.HasHistory) ChkImportHistory.IsChecked = false;
        if (!SettingsManager.Current.EnableHistory)
        {
            TxtHistoryHint.Text = "El historial está desactivado en Configuración de Lex Junior (no se importará).";
        }
        else
        {
            TxtHistoryHint.Text = profile.HasHistory
                ? "Copia segura de solo lectura aunque el navegador siga abierto."
                : "No se encontró base SQLite de historial en este perfil.";
        }

        ChkImportPasswords.IsEnabled = profile.HasPasswords;
        if (!profile.HasPasswords) ChkImportPasswords.IsChecked = false;
        TxtPasswordsHint.Text = profile.HasPasswords
            ? "No sobrescribe contraseñas existentes. Las contraseñas v20 requieren CSV."
            : "No se encontró el archivo Login Data en este perfil.";

        BtnImportProfile.IsEnabled = profile.HasBookmarks || profile.HasHistory || profile.HasPasswords;
    }

    private bool VerifyPinForPasswordImport()
    {
        if (!PasswordManager.ShouldRequireUnlockForVaultAction())
        {
            return true;
        }

        var pinDialog = new PinPromptDialog("Confirma tu PIN para importar contraseñas a tu bóveda personal")
        {
            Owner = this
        };
        return pinDialog.ShowDialog() == true && !PasswordManager.ShouldRequireUnlockForVaultAction();
    }

    private void SetRunningUi(bool running)
    {
        _isImporting = running;
        CmbProfiles.IsEnabled = !running && CmbProfiles.Items.Count > 0;
        BtnImportCsv.IsEnabled = !running;
        BtnImportProfile.IsEnabled = !running && CmbProfiles.SelectedItem != null;
        BtnCancelImport.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        PanelProgress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

        if (!running)
        {
            UpdateCategoryAvailability();
        }
    }

    private async void BtnImportProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_isImporting || IsRestrictedSession()) return;
        if (CmbProfiles.SelectedItem is not BrowserProfileInfo profile) return;

        bool wantBookmarks = ChkImportBookmarks.IsChecked == true && profile.HasBookmarks;
        bool wantHistory = ChkImportHistory.IsChecked == true && profile.HasHistory;
        bool wantPasswords = ChkImportPasswords.IsChecked == true && profile.HasPasswords;

        if (!wantBookmarks && !wantHistory && !wantPasswords)
        {
            MessageBox.Show("Selecciona al menos una categoría disponible para importar.", "Importar datos",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (wantPasswords && !VerifyPinForPasswordImport())
        {
            return;
        }

        _importCts?.Dispose();
        _importCts = new CancellationTokenSource();
        var token = _importCts.Token;

        SetRunningUi(true);
        PanelSummary.Visibility = Visibility.Collapsed;
        ProgressImport.Value = 5;
        TxtProgressStatus.Text = $"Importando desde {profile.DisplayLabel}...";

        var progress = new Progress<(int Pct, string Msg)>(update =>
        {
            if (_isClosing) return;
            ProgressImport.Value = update.Pct;
            TxtProgressStatus.Text = update.Msg;
        });

        try
        {
            var summary = new ImportSummary { SourceDescription = profile.DisplayLabel };

            await Task.Run(() =>
            {
                int totalSteps = (wantBookmarks ? 1 : 0) + (wantHistory ? 1 : 0) + (wantPasswords ? 1 : 0);
                int doneSteps = 0;

                if (wantBookmarks)
                {
                    token.ThrowIfCancellationRequested();
                    ((IProgress<(int, string)>)progress).Report((Math.Max(10, doneSteps * 100 / totalSteps), "Importando marcadores..."));
                    summary.Bookmarks = ChromiumBookmarkImporter.ImportFromProfile(profile, token);
                    doneSteps++;
                }

                if (wantHistory)
                {
                    token.ThrowIfCancellationRequested();
                    ((IProgress<(int, string)>)progress).Report((Math.Max(15, doneSteps * 100 / totalSteps), "Importando historial..."));
                    summary.History = ChromiumHistoryImporter.ImportFromProfile(profile, token);
                    doneSteps++;
                }

                if (wantPasswords)
                {
                    token.ThrowIfCancellationRequested();
                    ((IProgress<(int, string)>)progress).Report((Math.Max(20, doneSteps * 100 / totalSteps), "Importando contraseñas..."));
                    summary.Passwords = ChromiumPasswordImporter.ImportFromProfile(profile, token);
                    doneSteps++;
                }

                ((IProgress<(int, string)>)progress).Report((100, "Importación finalizada."));
            }, token);

            if (!_isClosing)
            {
                RenderSummary(summary);
            }
        }
        catch (OperationCanceledException)
        {
            if (!_isClosing)
            {
                RenderMessageSummary("Importación cancelada por el usuario.");
            }
        }
        catch (Exception ex)
        {
            if (!_isClosing)
            {
                RenderMessageSummary($"Error inesperado durante la importación: {ex.Message}");
            }
        }
        finally
        {
            if (!_isClosing)
            {
                SetRunningUi(false);
            }
        }
    }

    private async void BtnImportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_isImporting || IsRestrictedSession()) return;

        if (!VerifyPinForPasswordImport())
        {
            return;
        }

        var dlg = new OpenFileDialog
        {
            Title = "Seleccionar archivo CSV de contraseñas",
            Filter = "Archivos CSV (*.csv)|*.csv|Todos los archivos (*.*)|*.*",
            CheckFileExists = true
        };

        if (dlg.ShowDialog(this) != true)
        {
            return;
        }

        string filePath = dlg.FileName;
        _importCts?.Dispose();
        _importCts = new CancellationTokenSource();
        var token = _importCts.Token;

        SetRunningUi(true);
        PanelSummary.Visibility = Visibility.Collapsed;
        ProgressImport.Value = 30;
        TxtProgressStatus.Text = "Analizando archivo CSV e importando contraseñas...";

        try
        {
            var res = await Task.Run(() => CsvPasswordImporter.ImportFromFile(filePath, token), token);
            if (_isClosing) return;

            ProgressImport.Value = 100;
            var summary = new ImportSummary
            {
                SourceDescription = System.IO.Path.GetFileName(filePath),
                Passwords = res,
                CsvRecommendation = res.Error == null ? CsvPasswordImporter.DeleteCsvAdvice : null
            };
            RenderSummary(summary);
        }
        catch (OperationCanceledException)
        {
            if (!_isClosing)
            {
                RenderMessageSummary("Importación desde CSV cancelada.");
            }
        }
        catch (Exception ex)
        {
            if (!_isClosing)
            {
                RenderMessageSummary($"Error al importar el CSV: {ex.GetType().Name}.");
            }
        }
        finally
        {
            if (!_isClosing)
            {
                SetRunningUi(false);
            }
        }
    }

    private void BtnCancelImport_Click(object sender, RoutedEventArgs e)
    {
        try { _importCts?.Cancel(); } catch { }
        BtnCancelImport.IsEnabled = false;
        TxtProgressStatus.Text = "Cancelando...";
    }

    private void RenderSummary(ImportSummary summary)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(summary.SourceDescription))
        {
            sb.AppendLine($"Origen: {summary.SourceDescription}");
        }

        AppendCategoryLine(sb, summary.Bookmarks);
        AppendCategoryLine(sb, summary.History);
        AppendCategoryLine(sb, summary.Passwords);

        if (!string.IsNullOrWhiteSpace(summary.CsvRecommendation))
        {
            sb.AppendLine();
            sb.AppendLine($"🔒 {summary.CsvRecommendation}");
        }

        TxtSummaryBody.Text = sb.ToString().TrimEnd();
        PanelSummary.Visibility = Visibility.Visible;
    }

    private void RenderMessageSummary(string msg)
    {
        TxtSummaryBody.Text = msg;
        PanelSummary.Visibility = Visibility.Visible;
    }

    private static void AppendCategoryLine(StringBuilder sb, ImportCategoryResult? result)
    {
        if (result == null) return;

        sb.AppendLine();
        if (result.HasError)
        {
            sb.AppendLine($"• {result.Category}: Error — {result.Error}");
            return;
        }

        sb.Append($"• {result.Category}: {result.Imported} importados, {result.Duplicates} duplicados, {result.Skipped} omitidos");
        if (result.UnsupportedAppBoundCount > 0)
        {
            sb.Append($", {result.UnsupportedAppBoundCount} protegidos con cifrado v20");
        }
        sb.AppendLine(".");

        if (result.HasWarning)
        {
            sb.AppendLine($"  ℹ {result.Warning}");
        }
    }
}

