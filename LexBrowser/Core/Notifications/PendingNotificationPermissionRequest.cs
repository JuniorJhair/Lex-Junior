using System;
using Microsoft.Web.WebView2.Core;

namespace LexBrowser.Core.Notifications;

/// <summary>
/// Encapsula una solicitud pendiente de permiso de notificaciones para una pestaña,
/// manteniendo el deferral de WebView2 de forma segura y garantizando su resolución.
/// </summary>
public sealed class PendingNotificationPermissionRequest
{
    private readonly object _lock = new();

    public CoreWebView2PermissionRequestedEventArgs EventArgs { get; }
    public CoreWebView2Deferral Deferral { get; }
    public string Origin { get; }
    public string DisplayHost { get; }
    public bool IsResolved { get; private set; }

    public PendingNotificationPermissionRequest(
        CoreWebView2PermissionRequestedEventArgs eventArgs,
        CoreWebView2Deferral deferral,
        string origin,
        string displayHost)
    {
        EventArgs = eventArgs ?? throw new ArgumentNullException(nameof(eventArgs));
        Deferral = deferral ?? throw new ArgumentNullException(nameof(deferral));
        Origin = origin ?? string.Empty;
        DisplayHost = displayHost ?? string.Empty;
    }

    /// <summary>
    /// Resuelve la solicitud con el estado especificado y completa el deferral.
    /// Es idempótentemente seguro: llamadas posteriores son no-op.
    /// </summary>
    public void Resolve(CoreWebView2PermissionState state, bool savesInProfile)
    {
        lock (_lock)
        {
            if (IsResolved) return;
            IsResolved = true;

            try
            {
                EventArgs.State = state;
                EventArgs.SavesInProfile = savesInProfile;
                EventArgs.Handled = true;
            }
            catch { }

            try
            {
                Deferral.Complete();
            }
            catch { }
        }
    }
}

