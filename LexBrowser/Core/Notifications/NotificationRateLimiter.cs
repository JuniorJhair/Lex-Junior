using System;
using System.Collections.Concurrent;

namespace LexBrowser.Core.Notifications;

/// <summary>
/// Controlador de cuotas, límites diarios y prevención de saturación de notificaciones.
/// 0.0% CPU en reposo: evaluación pasiva en el microsegundo de llegada del evento, sin temporizadores.
/// </summary>
public static class NotificationRateLimiter
{
    private static readonly ConcurrentDictionary<string, int> _dailyCountsByOrigin = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, int> _dailyGlobalCounts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DateTime> _lastSentByOrigin = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Evalúa si una notificación entrante está autorizada a mostrarse según límites diarios y temporales.
    /// Si es aprobada, registra el conteo automáticamente.
    /// </summary>
    public static bool EvaluateNotification(
        string origin,
        DateTime? notificationTimestamp,
        int siteCustomDailyLimit,
        int defaultSiteDailyLimit,
        int globalDailyLimit,
        bool discardOlderThan24Hours,
        out string reason)
    {
        string normOrigin = NotificationPermissionManager.NormalizeOrigin(origin);
        DateTime now = DateTime.UtcNow;

        // 1. Antigüedad (> 24 horas o día previo)
        if (discardOlderThan24Hours && notificationTimestamp.HasValue)
        {
            if (now - notificationTimestamp.Value > TimeSpan.FromHours(24) ||
                notificationTimestamp.Value.Date < now.Date)
            {
                reason = "Notificación descartada por tener más de 24 horas o corresponder a un día anterior.";
                return false;
            }
        }

        // 2. Control de ráfaga (Flood protection): máx 1 cada 2 segundos por el mismo origen
        if (_lastSentByOrigin.TryGetValue(normOrigin, out var lastSent))
        {
            if ((now - lastSent).TotalSeconds < 2.0)
            {
                reason = "Notificación bloqueada temporalmente por protección contra saturación (ráfaga).";
                return false;
            }
        }

        string dayKey = now.ToString("yyyyMMdd");

        // 3. Límite diario por sitio (personalizado o predeterminado)
        int effectiveSiteLimit = siteCustomDailyLimit > 0 ? siteCustomDailyLimit : defaultSiteDailyLimit;
        if (effectiveSiteLimit > 0)
        {
            string siteKey = $"{dayKey}:{normOrigin}";
            int currentSiteCount = _dailyCountsByOrigin.GetOrAdd(siteKey, 0);
            if (currentSiteCount >= effectiveSiteLimit)
            {
                reason = $"Límite diario alcanzado para este sitio ({currentSiteCount}/{effectiveSiteLimit}).";
                return false;
            }
        }

        // 4. Límite diario global acumulado entre todos los sitios
        if (globalDailyLimit > 0)
        {
            int currentGlobalCount = _dailyGlobalCounts.GetOrAdd(dayKey, 0);
            if (currentGlobalCount >= globalDailyLimit)
            {
                reason = $"Cuota diaria global de notificaciones alcanzada ({currentGlobalCount}/{globalDailyLimit}).";
                return false;
            }
        }

        // Permitida: actualizar contadores de hoy
        string registeredSiteKey = $"{dayKey}:{normOrigin}";
        _dailyCountsByOrigin.AddOrUpdate(registeredSiteKey, 1, (_, count) => count + 1);
        _dailyGlobalCounts.AddOrUpdate(dayKey, 1, (_, count) => count + 1);
        _lastSentByOrigin[normOrigin] = now;

        reason = "Aprobada";
        return true;
    }

    /// <summary>
    /// Devuelve cuántas notificaciones ha enviado un origen el día de hoy.
    /// </summary>
    public static int GetTodayCountForSite(string origin)
    {
        string normOrigin = NotificationPermissionManager.NormalizeOrigin(origin);
        string siteKey = $"{DateTime.UtcNow:yyyyMMdd}:{normOrigin}";
        return _dailyCountsByOrigin.TryGetValue(siteKey, out int count) ? count : 0;
    }

    /// <summary>
    /// Devuelve cuántas notificaciones se han recibido en total hoy entre todos los sitios.
    /// </summary>
    public static int GetTodayGlobalCount()
    {
        string dayKey = DateTime.UtcNow.ToString("yyyyMMdd");
        return _dailyGlobalCounts.TryGetValue(dayKey, out int count) ? count : 0;
    }

    /// <summary>
    /// Reinicia los contadores para pruebas unitarias.
    /// </summary>
    public static void ResetForTesting()
    {
        _dailyCountsByOrigin.Clear();
        _dailyGlobalCounts.Clear();
        _lastSentByOrigin.Clear();
    }
}
