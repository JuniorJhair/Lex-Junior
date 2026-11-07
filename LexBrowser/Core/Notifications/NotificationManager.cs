using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using LexBrowser.Core.AdBlock;
using LexBrowser.UI.Models;
using Microsoft.Web.WebView2.Core;

namespace LexBrowser.Core.Notifications;

/// <summary>
/// Despachador central y gestor de notificaciones W3C recibidas vía WebView2.
/// 0.0% CPU en reposo: totalmente reactivo a eventos. Sin sondeos ni bucles.
/// Maneja sanitizado, cuotas, modo No Molestar pasivo, historial e invocación de toasts WPF.
/// </summary>
public static class NotificationManager
{
    private static readonly object _historyLock = new();
    private const int MaxHistoryItems = 200;
    private static readonly List<NotificationItem> _history = new();
    private static int _unreadCount = 0;

    public static event Action? OnNotificationsChanged;

    #region P/Invoke DND Pasivo (SHQueryUserNotificationState)

    private enum USER_NOTIFICATION_STATE
    {
        QUNS_NOT_PRESENT = 1,
        QUNS_BUSY = 2,
        QUNS_RUNNING_D3D_FULL_SCREEN = 3,
        QUNS_PRESENTATION_MODE = 4,
        QUNS_ACCEPTS_NOTIFICATIONS = 5,
        QUNS_QUIET_TIME = 6,
        QUNS_APP = 7
    }

    [DllImport("shell32.dll", SetLastError = false)]
    private static extern int SHQueryUserNotificationState(out USER_NOTIFICATION_STATE pquns);

    /// <summary>
    /// Consulta el estado del sistema en Windows para saber si el usuario está en pantalla completa,
    /// jugando o en una presentación (llamado únicamente al recibir un evento, 0% CPU en reposo).
    /// </summary>
    public static bool IsSystemInQuietMode()
    {
        try
        {
            if (SHQueryUserNotificationState(out var state) == 0)
            {
                return state == USER_NOTIFICATION_STATE.QUNS_BUSY ||
                       state == USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN ||
                       state == USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE ||
                       state == USER_NOTIFICATION_STATE.QUNS_QUIET_TIME;
            }
        }
        catch { }
        return false;
    }

    #endregion

    /// <summary>
    /// Procesa una notificación web recibida desde CoreWebView2.NotificationReceived.
    /// </summary>
    public static void HandleNotificationReceived(BrowserTab tab, CoreWebView2NotificationReceivedEventArgs e)
    {
        if (tab == null || tab.IsDisposed) return;

        // 1. Marcar como Handled para evitar que Windows genere toasts nativos duplicados
        e.Handled = true;

        var notif = e.Notification;
        if (notif == null) return;

        string rawOrigin = tab.CurrentUri != null ? tab.CurrentUri.GetLeftPart(UriPartial.Authority) : string.Empty;
        string origin = NotificationPermissionManager.NormalizeOrigin(rawOrigin);
        string displayHost = tab.CurrentUri?.Host ?? "Sitio web";

        var settings = SettingsManager.Current ?? new BrowserSettings();

        // 2. Comprobar política global
        if (settings.NotificationGlobalPolicy.Equals("BlockAll", StringComparison.OrdinalIgnoreCase))
        {
            ShieldLogger.Log("NOTIFICATION_DISCARDED", displayHost, origin, "MainFrame", "Discarded by GlobalPolicy=BlockAll");
            return;
        }

        // 3. Comprobar permiso del origen
        var mode = NotificationPermissionManager.GetSiteMode(origin, tab.IsIncognito);
        if (mode == SiteNotificationMode.Block || mode == SiteNotificationMode.Ask)
        {
            ShieldLogger.Log("NOTIFICATION_DISCARDED", displayHost, origin, "MainFrame", $"Discarded by SiteMode={mode}");
            return;
        }

        // Si la política global es SilentAll, forzar modo silencioso
        if (settings.NotificationGlobalPolicy.Equals("SilentAll", StringComparison.OrdinalIgnoreCase))
        {
            mode = SiteNotificationMode.Silent;
        }

        // 4. Evaluar cuotas y límites diarios
        var siteEntry = NotificationPermissionManager.GetSiteEntry(origin, tab.IsIncognito);
        int customLimit = siteEntry?.DailyLimit ?? 0;
        int defaultLimit = settings.NotificationDefaultSiteDailyLimit;
        int globalLimit = settings.NotificationGlobalDailyLimit;
        bool discardOlder = settings.NotificationDiscardOlderThan24Hours;

        DateTime? notifTimestamp = null;
        try
        {
            if (notif.Timestamp != default)
            {
                notifTimestamp = notif.Timestamp.ToUniversalTime();
            }
        }
        catch { }

        if (!NotificationRateLimiter.EvaluateNotification(origin, notifTimestamp, customLimit, defaultLimit, globalLimit, discardOlder, out string limitReason))
        {
            ShieldLogger.Log("NOTIFICATION_RATE_LIMITED", displayHost, origin, "MainFrame", limitReason);
            return;
        }

        // 5. Sanitizar título y cuerpo
        string cleanTitle = NotificationSanitizer.Sanitize(notif.Title, 100);
        if (string.IsNullOrWhiteSpace(cleanTitle)) cleanTitle = displayHost;

        string cleanBody = NotificationSanitizer.Sanitize(notif.Body, 300);

        // 6. Comprobar modo No Molestar (DND)
        bool isDnd = settings.NotificationDndActive;
        if (!isDnd && settings.NotificationDndOnFullscreen && IsSystemInQuietMode())
        {
            isDnd = true;
        }

        // 7. Crear el elemento de notificación
        var item = new NotificationItem
        {
            Origin = origin,
            DisplayHost = displayHost,
            Title = cleanTitle,
            Body = cleanBody,
            IconUrl = notif.IconUri,
            RequiresInteraction = notif.RequiresInteraction,
            ReceivedAt = DateTime.UtcNow,
            IsRead = false
        };

        // Guardar en el historial en memoria
        lock (_historyLock)
        {
            _history.Insert(0, item);
            if (_history.Count > MaxHistoryItems)
            {
                _history.RemoveAt(_history.Count - 1);
            }
            _unreadCount++;
        }

        NotificationPermissionManager.RecordNotificationSent(origin, tab.IsIncognito);
        ShieldLogger.Log("NOTIFICATION_DELIVERED", displayHost, origin, "MainFrame", $"Delivered (Mode={mode}, DND={isDnd})");

        // Disparar evento para actualizar el badge en UI
        Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            OnNotificationsChanged?.Invoke();
        });

        // 8. Notificar a WebView2 que fue mostrada
        try { notif.ReportShown(); } catch { }

        // Si es Silenciosa, Resumen o DND activo, no desplegamos el toast flotante en pantalla
        if (mode == SiteNotificationMode.Silent || mode == SiteNotificationMode.Digest || isDnd)
        {
            return;
        }

        // 9. Mostrar Toast nativo flotante en pantalla
        Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            try
            {
                NotificationToastManager.ShowToast(item, notif, tab);
            }
            catch (Exception ex)
            {
                ShieldLogger.Log("NOTIFICATION_TOAST_ERROR", displayHost, origin, "MainFrame", ex.Message);
            }
        });
    }

    #region Gestión de Historial y Lectura

    public static IReadOnlyList<NotificationItem> GetHistory()
    {
        lock (_historyLock)
        {
            return _history.ToList();
        }
    }

    public static int GetUnreadCount()
    {
        lock (_historyLock)
        {
            return _unreadCount;
        }
    }

    public static void MarkAllAsRead()
    {
        lock (_historyLock)
        {
            foreach (var item in _history)
            {
                item.IsRead = true;
            }
            _unreadCount = 0;
        }
        OnNotificationsChanged?.Invoke();
    }

    public static void ClearHistory()
    {
        lock (_historyLock)
        {
            _history.Clear();
            _unreadCount = 0;
        }
        OnNotificationsChanged?.Invoke();
    }

    public static void RemoveHistoryItem(string id)
    {
        lock (_historyLock)
        {
            var item = _history.FirstOrDefault(x => x.Id == id);
            if (item != null)
            {
                if (!item.IsRead && _unreadCount > 0) _unreadCount--;
                _history.Remove(item);
            }
        }
        OnNotificationsChanged?.Invoke();
    }

    public static bool ToggleDnd()
    {
        var s = SettingsManager.Current;
        if (s == null) return false;
        s.NotificationDndActive = !s.NotificationDndActive;
        SettingsManager.Save();
        OnNotificationsChanged?.Invoke();
        return s.NotificationDndActive;
    }

    #endregion
}
