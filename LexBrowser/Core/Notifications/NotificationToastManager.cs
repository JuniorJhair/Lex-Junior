using System;
using System.Collections.Generic;
using System.Windows;
using LexBrowser.UI.Models;
using LexBrowser.UI.Views;
using Microsoft.Web.WebView2.Core;

namespace LexBrowser.Core.Notifications;

/// <summary>
/// Administrador de posición y ciclo de vida de los avisos flotantes (Toasts) en pantalla.
/// 0.0% CPU en reposo: solo activo mientras haya toasts visibles (máximo 3 simultáneos).
/// </summary>
public static class NotificationToastManager
{
    private const int MaxVisibleToasts = 3;
    private static readonly List<NotificationToastWindow> _activeToasts = new();
    private static readonly Queue<Action> _pendingQueue = new();

    public static void ShowToast(NotificationItem item, CoreWebView2Notification? nativeNotif, BrowserTab? tab)
    {
        if (_activeToasts.Count >= MaxVisibleToasts)
        {
            // Si la cola está llena, encolar hasta que se libere un espacio
            if (_pendingQueue.Count < 10)
            {
                _pendingQueue.Enqueue(() => ShowToast(item, nativeNotif, tab));
            }
            return;
        }

        var toast = new NotificationToastWindow(item, nativeNotif, tab);
        _activeToasts.Add(toast);
        RepositionToasts();
        toast.Show();
    }

    public static void OnToastClosed(NotificationToastWindow toast)
    {
        _activeToasts.Remove(toast);
        RepositionToasts();

        // Si hay notificaciones pendientes en la cola, mostrar la siguiente
        if (_pendingQueue.Count > 0 && _activeToasts.Count < MaxVisibleToasts)
        {
            var next = _pendingQueue.Dequeue();
            next.Invoke();
        }
    }

    private static void RepositionToasts()
    {
        try
        {
            var workArea = SystemParameters.WorkArea;
            const double margin = 16;
            const double spacing = 8;
            double toastHeight = 100;
            double toastWidth = 360;

            for (int i = 0; i < _activeToasts.Count; i++)
            {
                var t = _activeToasts[i];
                t.Left = workArea.Right - toastWidth - margin;
                t.Top = workArea.Bottom - (i + 1) * (toastHeight + spacing) - margin;
            }
        }
        catch { }
    }
}
