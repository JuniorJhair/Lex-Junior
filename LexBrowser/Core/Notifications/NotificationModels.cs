using System;
using System.Text.Json.Serialization;

namespace LexBrowser.Core.Notifications;

/// <summary>
/// Representa un elemento de notificación procesado y listo para ser mostrado
/// en la interfaz o guardado en el centro de notificaciones.
/// </summary>
public sealed class NotificationItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Origin { get; set; } = string.Empty;
    public string DisplayHost { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? IconUrl { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public bool IsRead { get; set; } = false;
    public bool RequiresInteraction { get; set; } = false;

    [JsonIgnore]
    public Action? OnClick { get; set; }

    [JsonIgnore]
    public Action? OnClose { get; set; }

    [JsonIgnore]
    public string TimeFormatted
    {
        get
        {
            var span = DateTime.UtcNow - ReceivedAt;
            if (span.TotalSeconds < 60) return "Hace un momento";
            if (span.TotalMinutes < 60) return $"Hace {(int)span.TotalMinutes}m";
            if (span.TotalHours < 24) return $"Hace {(int)span.TotalHours}h";
            return ReceivedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
        }
    }
}
