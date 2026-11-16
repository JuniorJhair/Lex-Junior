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
    private readonly System.Collections.ObjectModel.ObservableCollection<DownloadEntry> _downloadsObservable = new();

    // ─────────────────────────────────────────────────────────────────────
    // HISTORIAL DE DESCARGAS EN SESIÓN (BAJO DEMANDA + THROTTLE UI ~4/s)
    // ─────────────────────────────────────────────────────────────────────
    private void OnDownloadsChanged()
    {
        long now = Environment.TickCount64;
        if (now - _lastDownloadsUiRefreshTick >= 220)
        {
            _lastDownloadsUiRefreshTick = now;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                UpdateDownloadsToolbarIndicator();
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
            await Task.Delay(220);
            _downloadsUiRefreshPending = false;
            _lastDownloadsUiRefreshTick = Environment.TickCount64;
            UpdateDownloadsToolbarIndicator();
            if (PopupDownloads != null && PopupDownloads.IsOpen)
            {
                RefreshDownloadsPopupUI();
            }
        });
    }

    internal void UpdateDownloadsToolbarIndicator()
    {
        if (BtnDownloads == null) return;

        var summary = DownloadManager.GetActiveSummary();
        if (!summary.HasActive)
        {
            if (BadgeDownloadsCount != null) BadgeDownloadsCount.Visibility = Visibility.Collapsed;
            if (TxtDownloadsBtnStatus != null) TxtDownloadsBtnStatus.Visibility = Visibility.Collapsed;
            if (PbDownloadsToolbar != null)
            {
                PbDownloadsToolbar.Visibility = Visibility.Collapsed;
                PbDownloadsToolbar.IsIndeterminate = false;
                PbDownloadsToolbar.Value = 0;
            }
            if (TxtDownloadsBtnIcon != null)
                TxtDownloadsBtnIcon.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA1, 0xA1, 0xAA));
            BtnDownloads.ToolTip = "Historial de Descargas (Ctrl+J)";
            return;
        }

        if (TxtDownloadsBtnIcon != null)
            TxtDownloadsBtnIcon.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x38, 0xBD, 0xF8));

        if (BadgeDownloadsCount != null && TxtDownloadsBadgeCount != null)
        {
            TxtDownloadsBadgeCount.Text = summary.ActiveCount.ToString();
            BadgeDownloadsCount.Visibility = Visibility.Visible;
        }

        if (TxtDownloadsBtnStatus != null)
        {
            TxtDownloadsBtnStatus.Text = summary.HasIndeterminate
                ? (summary.PausedCount == summary.ActiveCount ? "⏸" : "⋯")
                : $"{summary.TotalProgressPercent}%";
            TxtDownloadsBtnStatus.Visibility = Visibility.Visible;
        }

        if (PbDownloadsToolbar != null)
        {
            PbDownloadsToolbar.Visibility = Visibility.Visible;
            PbDownloadsToolbar.IsIndeterminate = summary.HasIndeterminate && summary.InProgressCount > 0;
            if (!PbDownloadsToolbar.IsIndeterminate)
                PbDownloadsToolbar.Value = summary.TotalProgressPercent;
        }

        BtnDownloads.ToolTip = summary.HasIndeterminate
            ? $"{summary.ActiveCount} {(summary.ActiveCount == 1 ? "descarga activa" : "descargas activas")} (Ctrl+J)"
            : $"{summary.ActiveCount} {(summary.ActiveCount == 1 ? "descarga activa" : "descargas activas")} · {summary.TotalProgressPercent}% (Ctrl+J)";
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

        UpdateDownloadsToolbarIndicator();

        var entries = DownloadManager.GetEntries();
        if (ItemsDownloadsHistory.ItemsSource != _downloadsObservable)
        {
            ItemsDownloadsHistory.ItemsSource = _downloadsObservable;
        }

        // Actualizar colección observable sin reemplazar innecesariamente cuando el orden es idéntico (evita parpadeos de botones)
        bool sameIds = _downloadsObservable.Count == entries.Count;
        if (sameIds)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                if (!ReferenceEquals(_downloadsObservable[i], entries[i]))
                {
                    sameIds = false;
                    break;
                }
            }
        }

        if (!sameIds)
        {
            _downloadsObservable.Clear();
            for (int i = 0; i < entries.Count; i++)
                _downloadsObservable.Add(entries[i]);
        }

        if (TxtDownloadsCountSubtitle != null)
        {
            var summary = DownloadManager.GetActiveSummary();
            if (summary.HasActive)
            {
                TxtDownloadsCountSubtitle.Text = $"{summary.ActiveCount} {(summary.ActiveCount == 1 ? "activa" : "activas")} · {entries.Count} total";
            }
            else
            {
                TxtDownloadsCountSubtitle.Text = entries.Count == 0
                    ? "Solo en esta sesión · Inactivo"
                    : $"{entries.Count} {(entries.Count == 1 ? "archivo registrado" : "archivos registrados")} · Solo en esta sesión";
            }
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

    internal void RevealFileInExplorer(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        try
        {
            if (File.Exists(filePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{filePath}\"",
                    UseShellExecute = true
                });
            }
            else
            {
                string? dir = Path.GetDirectoryName(filePath);
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
            if (PopupDownloads != null) PopupDownloads.IsOpen = false;
            RevealFileInExplorer(entry.FilePath);
        }
    }

    private void BtnPauseDownload_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is DownloadEntry entry)
            entry.PauseAction?.Invoke();
    }

    private void BtnResumeDownload_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is DownloadEntry entry)
            entry.ResumeAction?.Invoke();
    }

    private void BtnCancelDownload_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is DownloadEntry entry)
            entry.CancelAction?.Invoke();
    }

    private void BtnRetryDownload_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is DownloadEntry entry)
        {
            if (PopupDownloads != null) PopupDownloads.IsOpen = false;
            entry.RetryAction?.Invoke();
        }
    }

    private void BtnRemoveDownloadEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is DownloadEntry entry)
        {
            if (entry.IsActive)
                entry.CancelAction?.Invoke();
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

