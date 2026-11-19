using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LexBrowser.Core;
using Microsoft.Web.WebView2.Core;

namespace LexBrowser;

public partial class MainWindow
{
    private static readonly SolidColorBrush BrushDownloadIdle = CreateFrozenBrush(0xA1, 0xA1, 0xAA);
    private static readonly SolidColorBrush BrushDownloadActive = CreateFrozenBrush(0x38, 0xBD, 0xF8);

    internal sealed class TrackedWebViewDownload
    {
        public required string EntryId { get; init; }
        public required CoreWebView2DownloadOperation Operation { get; init; }
        public required string FileName { get; init; }
        public required string ResultPath { get; init; }
        public long LastReportedTick;
        public long LastReportedBytes = -1;
        public long LastReportedTotal = -1;
        public CoreWebView2DownloadState? LastReportedState;
        public bool IsTerminal;
        public Action? DetachHandlersAction;
    }

    private static readonly object _activeOpsLock = new();
    private static readonly Dictionary<string, TrackedWebViewDownload> _activeWebViewDownloads = new(StringComparer.Ordinal);
    private static DispatcherTimer? _activeDownloadSyncTimer;

    private bool _isDownloadingBrowserUpdate = false;
    private readonly object _downloadsUiSync = new();
    private long _lastDownloadsUiRefreshTick = 0;
    private bool _downloadsUiRefreshPending = false;
    private readonly System.Collections.ObjectModel.ObservableCollection<DownloadEntry> _downloadsObservable = new();

    internal static void RegisterTrackedDownloadOperation(TrackedWebViewDownload tracked)
    {
        lock (_activeOpsLock)
        {
            _activeWebViewDownloads[tracked.EntryId] = tracked;
        }
        EnsureActiveDownloadSyncTimer();
    }

    internal static void NotifyTrackedDownloadResumed()
    {
        EnsureActiveDownloadSyncTimer();
    }

    private static void EnsureActiveDownloadSyncTimer()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;

        void EnsureOnUi()
        {
            bool hasInProgress;
            lock (_activeOpsLock)
            {
                hasInProgress = _activeWebViewDownloads.Values.Any(t =>
                {
                    if (t.IsTerminal) return false;
                    try { return t.Operation.State == CoreWebView2DownloadState.InProgress; }
                    catch { return false; }
                });
            }

            if (hasInProgress)
            {
                if (_activeDownloadSyncTimer == null)
                {
                    _activeDownloadSyncTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
                    {
                        Interval = TimeSpan.FromMilliseconds(350)
                    };
                    _activeDownloadSyncTimer.Tick += (_, _) => SyncAllTrackedDownloadsOnUi();
                }
                if (!_activeDownloadSyncTimer.IsEnabled)
                    _activeDownloadSyncTimer.Start();
            }
            else if (_activeDownloadSyncTimer != null && _activeDownloadSyncTimer.IsEnabled)
            {
                _activeDownloadSyncTimer.Stop();
            }
        }

        if (dispatcher.CheckAccess())
            EnsureOnUi();
        else
            _ = dispatcher.BeginInvoke(DispatcherPriority.Normal, EnsureOnUi);
    }

    private static void SyncAllTrackedDownloadsOnUi()
    {
        List<TrackedWebViewDownload> snapshot;
        lock (_activeOpsLock)
        {
            snapshot = _activeWebViewDownloads.Values.ToList();
        }

        foreach (var tracked in snapshot)
        {
            SyncTrackedDownloadOperation(tracked, forceProgressUpdate: true);
        }

        EnsureActiveDownloadSyncTimer();
    }

    private static MainWindow? FindActiveHostWindowForDownloadToast()
    {
        if (Application.Current == null) return null;
        var windows = Application.Current.Windows.OfType<MainWindow>().Where(w => !w._isWindowClosing).ToList();
        return windows.FirstOrDefault(w => w.IsActive && !w._isGuestSession)
               ?? windows.FirstOrDefault(w => !w._isGuestSession)
               ?? windows.FirstOrDefault();
    }

    internal static void SyncTrackedDownloadOperation(TrackedWebViewDownload tracked, bool forceProgressUpdate)
    {
        if (tracked.IsTerminal) return;

        CoreWebView2DownloadOperation op = tracked.Operation;
        CoreWebView2DownloadState state;
        long received;
        long tot;
        DateTime estEnd;
        try
        {
            state = op.State;
            received = op.BytesReceived;
            tot = DownloadManager.SafeBytesFromUlong(op.TotalBytesToReceive);
            estEnd = DateTime.MinValue;
            try { estEnd = op.EstimatedEndTime; } catch { }
        }
        catch
        {
            return;
        }

        if (state == CoreWebView2DownloadState.InProgress)
        {
            long now = Environment.TickCount64;
            bool changed =
                tracked.LastReportedState != state ||
                received != tracked.LastReportedBytes ||
                tot != tracked.LastReportedTotal;

            if (!changed)
                return;

            bool stateTransition = tracked.LastReportedState != state;
            if (!forceProgressUpdate && !stateTransition && now - tracked.LastReportedTick < 220 && (tot <= 0 || received < tot))
                return;

            tracked.LastReportedTick = now;
            tracked.LastReportedState = state;
            tracked.LastReportedBytes = received;
            tracked.LastReportedTotal = tot;

            double? etaSec = estEnd > DateTime.Now ? (estEnd - DateTime.Now).TotalSeconds : null;
            DownloadManager.UpdateDownloadProgress(tracked.EntryId, received, tot, "En progreso", estimatedSecondsRemaining: etaSec);
            return;
        }

        if (state == CoreWebView2DownloadState.Completed)
        {
            tracked.IsTerminal = true;
            tracked.LastReportedState = state;
            lock (_activeOpsLock)
            {
                _activeWebViewDownloads.Remove(tracked.EntryId);
            }
            tracked.DetachHandlersAction?.Invoke();

            long finalTotal = tot > 0 ? tot : received;
            DownloadManager.UpdateDownloadProgress(tracked.EntryId, received > 0 ? received : finalTotal, finalTotal, "Completado");
            EnsureActiveDownloadSyncTimer();

            var host = FindActiveHostWindowForDownloadToast();
            host?.Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                host.RefreshDownloadsUiOnDispatcher();
                host.ShowToast($"✅ Descarga completada: {tracked.FileName}", "Abrir carpeta 📂", () =>
                {
                    host.RevealFileInExplorer(tracked.ResultPath);
                }, durationSeconds: 6);
            });
            return;
        }

        if (state == CoreWebView2DownloadState.Interrupted)
        {
            CoreWebView2DownloadInterruptReason reason;
            bool canResume;
            try
            {
                reason = op.InterruptReason;
                canResume = op.CanResume;
            }
            catch
            {
                return;
            }

            if (reason == CoreWebView2DownloadInterruptReason.UserPaused && canResume)
            {
                if (tracked.LastReportedState == state && tracked.LastReportedBytes == received && tracked.LastReportedTotal == tot)
                    return;

                tracked.LastReportedState = state;
                tracked.LastReportedBytes = received;
                tracked.LastReportedTotal = tot;
                DownloadManager.UpdateDownloadProgress(tracked.EntryId, received, tot, "Pausado", canResume: true);
                EnsureActiveDownloadSyncTimer();
                return;
            }

            if (reason == CoreWebView2DownloadInterruptReason.UserCanceled)
            {
                tracked.IsTerminal = true;
                tracked.LastReportedState = state;
                lock (_activeOpsLock)
                {
                    _activeWebViewDownloads.Remove(tracked.EntryId);
                }
                tracked.DetachHandlersAction?.Invoke();
                DownloadManager.UpdateDownloadProgress(tracked.EntryId, received, tot, "Cancelado", "Cancelada por el usuario");
                EnsureActiveDownloadSyncTimer();
                FindActiveHostWindowForDownloadToast()?.Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
                    FindActiveHostWindowForDownloadToast()?.RefreshDownloadsUiOnDispatcher());
                return;
            }

            tracked.LastReportedState = state;

            if (!canResume)
            {
                tracked.IsTerminal = true;
                lock (_activeOpsLock)
                {
                    _activeWebViewDownloads.Remove(tracked.EntryId);
                }
                tracked.DetachHandlersAction?.Invoke();
            }

            string reasonLabel = FormatDownloadInterruptReason(reason);
            DownloadManager.UpdateDownloadProgress(tracked.EntryId, received, tot, "Interrumpido", reasonLabel, canResume: canResume);
            EnsureActiveDownloadSyncTimer();

            var host = FindActiveHostWindowForDownloadToast();
            host?.Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                host.RefreshDownloadsUiOnDispatcher();
                host.ShowToast($"⚠️ Descarga interrumpida: {tracked.FileName} ({reasonLabel})", "Ver descargas", () =>
                {
                    host.RefreshDownloadsPopupUI();
                    if (host.PopupDownloads != null) host.PopupDownloads.IsOpen = true;
                });
            });
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // HISTORIAL DE DESCARGAS EN SESIÓN (BAJO DEMANDA + THROTTLE UI ~5/s CON TRAILING GARANTIZADO)
    // ─────────────────────────────────────────────────────────────────────
    private void OnDownloadsChanged()
    {
        bool runImmediately = false;
        bool scheduleTrailing = false;

        lock (_downloadsUiSync)
        {
            long now = Environment.TickCount64;
            if (!_downloadsUiRefreshPending && now - _lastDownloadsUiRefreshTick >= 180)
            {
                _lastDownloadsUiRefreshTick = now;
                runImmediately = true;
            }
            else if (!_downloadsUiRefreshPending)
            {
                _downloadsUiRefreshPending = true;
                scheduleTrailing = true;
            }
        }

        if (runImmediately)
        {
            try { Dispatcher.BeginInvoke(DispatcherPriority.Normal, RefreshDownloadsUiOnDispatcher); } catch { }
        }
        else if (scheduleTrailing)
        {
            _ = Task.Delay(180).ContinueWith(_ =>
            {
                lock (_downloadsUiSync)
                {
                    _downloadsUiRefreshPending = false;
                    _lastDownloadsUiRefreshTick = Environment.TickCount64;
                }
                try { Dispatcher.BeginInvoke(DispatcherPriority.Normal, RefreshDownloadsUiOnDispatcher); } catch { }
            }, TaskScheduler.Default);
        }
    }

    private void RefreshDownloadsUiOnDispatcher()
    {
        if (_isWindowClosing) return;
        UpdateDownloadsToolbarIndicator();
        if (PopupDownloads != null && PopupDownloads.IsOpen)
        {
            RefreshDownloadsPopupUI();
        }
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
                TxtDownloadsBtnIcon.Foreground = BrushDownloadIdle;
            BtnDownloads.ToolTip = "Historial de Descargas (Ctrl+J)";
            return;
        }

        if (TxtDownloadsBtnIcon != null)
            TxtDownloadsBtnIcon.Foreground = BrushDownloadActive;

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
    private void BtnUpdateAvailable_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloadingBrowserUpdate || IsLocked) return;
        var update = UpdateManager.AvailableUpdate;
        if (update == null) return;

        try
        {
            var dlg = new UI.Views.UpdatePromptDialog(
                update,
                pct =>
                {
                    _isDownloadingBrowserUpdate = pct > 0 && pct < 100;
                    TxtUpdateAvailableLabel.Text = $"Descargando {pct}%";
                })
            {
                Owner = this
            };

            dlg.ShowDialog();
        }
        finally
        {
            _isDownloadingBrowserUpdate = false;
            BtnUpdateAvailable.IsEnabled = true;
            TxtUpdateAvailableLabel.Text = $"Actualizar ({update.VersionTag})";
        }
    }
}

