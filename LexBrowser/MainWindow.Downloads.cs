using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LexBrowser.Core;

namespace LexBrowser;

public partial class MainWindow
{
    private bool _isDownloadingBrowserUpdate = false;
    private long _lastDownloadsUiRefreshTick = 0;
    private bool _downloadsUiRefreshPending = false;

    // ─────────────────────────────────────────────────────────────────────
    // HISTORIAL DE DESCARGAS LIGERO (0% CPU EN REPOSO + THROTTLE UI 200ms)
    // ─────────────────────────────────────────────────────────────────────
    private void OnDownloadsChanged()
    {
        long now = Environment.TickCount64;
        if (now - _lastDownloadsUiRefreshTick >= 200)
        {
            _lastDownloadsUiRefreshTick = now;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (PopupDownloads != null && PopupDownloads.IsOpen)
                {
                    RefreshDownloadsPopupUI();
                }
            });
            return;
        }

        if (_downloadsUiRefreshPending) return;
        _downloadsUiRefreshPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, async () =>
        {
            await Task.Delay(200);
            _downloadsUiRefreshPending = false;
            _lastDownloadsUiRefreshTick = Environment.TickCount64;
            if (PopupDownloads != null && PopupDownloads.IsOpen)
            {
                RefreshDownloadsPopupUI();
            }
        });
    }

    private void BtnDownloads_Click(object sender, RoutedEventArgs e)
    {
        if (IsLocked) return;
        if (_isGuestSession && !TryUnlockFromGuestMode("Desbloquea con tu PIN para ver el Historial de Descargas"))
        {
            return;
        }

        RefreshDownloadsPopupUI();
        PopupDownloads.IsOpen = !PopupDownloads.IsOpen;
    }

    private void RefreshDownloadsPopupUI()
    {
        if (ItemsDownloadsHistory == null || TxtDownloadsEmptyState == null || ScrollDownloadsHistory == null)
            return;

        var entries = DownloadManager.GetEntries();
        if (ItemsDownloadsHistory.ItemsSource != entries)
        {
            ItemsDownloadsHistory.ItemsSource = entries;
        }

        if (TxtDownloadsCountSubtitle != null)
        {
            TxtDownloadsCountSubtitle.Text = entries.Count == 0
                ? "Ligero · 0% consumo en segundo plano"
                : $"{entries.Count} {(entries.Count == 1 ? "archivo registrado" : "archivos registrados")} · 0% CPU en reposo";
        }

        if (entries.Count == 0)
        {
            ScrollDownloadsHistory.Visibility = Visibility.Collapsed;
            TxtDownloadsEmptyState.Visibility = Visibility.Visible;
        }
        else
        {
            TxtDownloadsEmptyState.Visibility = Visibility.Collapsed;
            ScrollDownloadsHistory.Visibility = Visibility.Visible;
        }
    }

    private void BtnOpenDownloadsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Directory.Exists(folder))
            {
                folder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void BtnClearDownloadsHistory_Click(object sender, RoutedEventArgs e)
    {
        DownloadManager.ClearAll();
        RefreshDownloadsPopupUI();
    }

    private void BtnOpenDownloadedFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is DownloadEntry entry && !string.IsNullOrWhiteSpace(entry.FilePath))
        {
            if (!File.Exists(entry.FilePath))
            {
                ShowToast("⚠️ El archivo ya no se encuentra en esa ubicación.");
                return;
            }
            try
            {
                PopupDownloads.IsOpen = false;
                Process.Start(new ProcessStartInfo
                {
                    FileName = entry.FilePath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ShowToast($"⚠️ No se pudo abrir el archivo: {ex.Message}");
            }
        }
    }

    private void BtnShowDownloadedFileInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is DownloadEntry entry && !string.IsNullOrWhiteSpace(entry.FilePath))
        {
            try
            {
                PopupDownloads.IsOpen = false;
                if (File.Exists(entry.FilePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{entry.FilePath}\"",
                        UseShellExecute = true
                    });
                }
                else
                {
                    string? dir = Path.GetDirectoryName(entry.FilePath);
                    if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = dir,
                            UseShellExecute = true
                        });
                    }
                    else
                    {
                        ShowToast("⚠️ El archivo o la carpeta ya no existen.");
                    }
                }
            }
            catch { }
        }
    }

    private void BtnRemoveDownloadEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is DownloadEntry entry)
        {
            DownloadManager.DeleteEntry(entry.Id);
            RefreshDownloadsPopupUI();
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // AUTO-ACTUALIZADOR CONECTADO A GITHUB RELEASES (JuniorJhair/Lex-Junior)
    // ─────────────────────────────────────────────────────────────────────
    private async void BtnUpdateAvailable_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloadingBrowserUpdate || IsLocked) return;
        var update = UpdateManager.AvailableUpdate;
        if (update == null) return;

        var confirm = MessageBox.Show(
            $"Está disponible la nueva versión {update.VersionTag} de Lex Junior Browser.\n\n" +
            $"{(string.IsNullOrWhiteSpace(update.ReleaseNotes) ? "Incluye mejoras de rendimiento, seguridad y estabilidad." : update.ReleaseNotes)}\n\n" +
            "¿Deseas descargar e instalar la actualización ahora mismo? (Tus marcadores, historial y configuración se conservarán intactos).",
            "Actualización disponible — Lex Junior",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (confirm != MessageBoxResult.Yes) return;

        _isDownloadingBrowserUpdate = true;
        BtnUpdateAvailable.IsEnabled = false;

        var progress = new Progress<int>(pct =>
        {
            TxtUpdateAvailableLabel.Text = $"Descargando {pct}%";
        });

        bool ok = await UpdateManager.DownloadAndApplyUpdateAsync(update, progress);
        if (!ok)
        {
            _isDownloadingBrowserUpdate = false;
            BtnUpdateAvailable.IsEnabled = true;
            TxtUpdateAvailableLabel.Text = $"Actualizar ({update.VersionTag})";
            ShowToast("⚠️ No se pudo descargar la actualización. Verifica tu conexión a internet.");
        }
    }
}

