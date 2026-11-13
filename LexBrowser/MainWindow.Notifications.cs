using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using LexBrowser.Core;
using LexBrowser.Core.AdBlock;
using LexBrowser.Core.Engine;
using LexBrowser.Core.Notifications;
using LexBrowser.UI.Models;

namespace LexBrowser;

public partial class MainWindow
{
    private static readonly SolidColorBrush BrushNotifCardBg = CreateFrozenBrush(0x1E, 0x1E, 0x2C);
    private static readonly SolidColorBrush BrushNotifCardBorder = CreateFrozenBrush(0x2D, 0x2D, 0x40);
    private static readonly SolidColorBrush BrushNotifIconBg = CreateFrozenBrush(0x28, 0x28, 0x38);
    private static readonly SolidColorBrush BrushNotifHostFg = CreateFrozenBrush(0x81, 0x8C, 0xF8);
    private static readonly SolidColorBrush BrushNotifTimeFg = CreateFrozenBrush(0xA1, 0xA1, 0xAA); // Contraste >= 6.8:1
    private static readonly SolidColorBrush BrushNotifBodyFg = CreateFrozenBrush(0xD4, 0xD4, 0xD8);

    public static bool IsDoNotDisturbActive => SettingsManager.Current?.NotificationDndActive ?? false;

    private void ToggleNotificationDoNotDisturb()
    {
        bool active = NotificationManager.ToggleDnd();
        string status = active ? "activado (alertas silenciadas al Centro)" : "desactivado (alertas normales)";
        ShowToast($"🔕 Modo No Molestar {status}");
        UpdateNotificationBadge();
    }

    private bool IsNotificationDndShortcut(KeyEventArgs e)
    {
        string raw = SettingsManager.Current.NotificationDndShortcut ?? "Ctrl+Shift+D";
        if (string.IsNullOrWhiteSpace(raw) || raw.Equals("Ninguno", StringComparison.OrdinalIgnoreCase))
            return false;

        ModifierKeys expectedMods = ModifierKeys.None;
        Key expectedKey = Key.None;

        var parts = raw.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase))
                expectedMods |= ModifierKeys.Control;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                expectedMods |= ModifierKeys.Shift;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                expectedMods |= ModifierKeys.Alt;
            else if (Enum.TryParse<Key>(part, ignoreCase: true, out var parsedKey))
                expectedKey = parsedKey;
        }

        if (expectedKey == Key.None) return false;
        Key actualKey = e.Key == Key.System ? e.SystemKey : e.Key;
        return Keyboard.Modifiers == expectedMods && actualKey == expectedKey;
    }

    private void UpdateNotificationBadge()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateNotificationBadge);
            return;
        }

        try
        {
            // En Modo Invitado no mostrar el contador de notificaciones personales del propietario
            if (_isGuestSession)
            {
                BadgeNotifications.Visibility = Visibility.Collapsed;
                TxtNotificationsBtnIcon.Text = "🔕";
                BtnNotifications.ToolTip = "Centro de Notificaciones (bloqueado en Modo Invitado)";
                return;
            }

            int unread = NotificationManager.GetUnreadCount();
            bool isDnd = SettingsManager.Current?.NotificationDndActive ?? false;

            if (unread > 0)
            {
                BadgeNotifications.Visibility = Visibility.Visible;
                TxtBadgeNotificationsCount.Text = unread > 99 ? "99+" : unread.ToString();
            }
            else
            {
                BadgeNotifications.Visibility = Visibility.Collapsed;
            }

            TxtNotificationsBtnIcon.Text = isDnd ? "🔕" : "🔔";
            BtnNotifications.ToolTip = isDnd
                ? $"Notificaciones silenciadas (No Molestar activo · {unread} sin leer)"
                : $"Centro de Notificaciones ({unread} sin leer · {SettingsManager.Current?.NotificationDndShortcut ?? "Ctrl+Shift+D"} para No Molestar)";

            if (BtnToggleDnd != null)
            {
                BtnToggleDnd.Content = isDnd ? "🔔 Activar avisos" : "🔕 Silencio";
            }
        }
        catch { }
    }

    private void BtnNotifications_Click(object sender, RoutedEventArgs e)
    {
        if (IsLocked) return;
        if (_isGuestSession && !TryUnlockFromGuestMode("Desbloquea con tu PIN para ver el Centro de Notificaciones"))
        {
            return;
        }

        PopupNotifications.IsOpen = !PopupNotifications.IsOpen;
        if (PopupNotifications.IsOpen)
        {
            NotificationManager.MarkAllAsRead();
            UpdateNotificationBadge();
            RenderNotificationCenterItems();
        }
    }

    private void BtnToggleDnd_Click(object sender, RoutedEventArgs e)
    {
        ToggleNotificationDoNotDisturb();
    }

    private void BtnClearAllNotifications_Click(object sender, RoutedEventArgs e)
    {
        NotificationManager.ClearHistory();
        RenderNotificationCenterItems();
        UpdateNotificationBadge();
    }

    private void BtnOpenNotificationSettings_Click(object sender, RoutedEventArgs e)
    {
        PopupNotifications.IsOpen = false;
        OpenSettings("Notifications");
    }

    private void RenderNotificationCenterItems()
    {
        var items = NotificationManager.GetHistory();
        PanelNotificationsList.Children.Clear();

        if (items.Count == 0)
        {
            PanelNotificationsEmpty.Visibility = Visibility.Visible;
            PanelNotificationsList.Children.Add(PanelNotificationsEmpty);
            return;
        }

        PanelNotificationsEmpty.Visibility = Visibility.Collapsed;

        foreach (var item in items)
        {
            var card = new Border
            {
                Background = BrushNotifCardBg,
                BorderBrush = BrushNotifCardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 6),
                Cursor = Cursors.Hand
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var iconBorder = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(6),
                Background = BrushNotifIconBg,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 8, 0)
            };
            var iconImg = new Image
            {
                Width = 18,
                Height = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var iconTxt = new TextBlock
            {
                Text = "🔔",
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            try
            {
                var fav = FaviconService.GetCachedFavicon(item.DisplayHost);
                if (fav != null)
                {
                    iconImg.Source = fav;
                    iconBorder.Child = iconImg;
                }
                else
                {
                    iconBorder.Child = iconTxt;
                }
            }
            catch
            {
                iconBorder.Child = iconTxt;
            }
            Grid.SetColumn(iconBorder, 0);
            grid.Children.Add(iconBorder);

            var contentStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var hostBlock = new TextBlock
            {
                Text = item.DisplayHost,
                Foreground = BrushNotifHostFg,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(hostBlock, 0);
            headerGrid.Children.Add(hostBlock);

            var timeBlock = new TextBlock
            {
                Text = item.TimeFormatted,
                Foreground = BrushNotifTimeFg, // #A1A1AA (Contraste >= 6.8:1)
                FontSize = 11,
                Margin = new Thickness(6, 0, 0, 0)
            };
            Grid.SetColumn(timeBlock, 1);
            headerGrid.Children.Add(timeBlock);

            contentStack.Children.Add(headerGrid);

            var titleBlock = new TextBlock
            {
                Text = item.Title,
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 1)
            };
            contentStack.Children.Add(titleBlock);

            if (!string.IsNullOrWhiteSpace(item.Body))
            {
                var bodyBlock = new TextBlock
                {
                    Text = item.Body,
                    Foreground = BrushNotifBodyFg,
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxHeight = 32,
                    TextWrapping = TextWrapping.Wrap
                };
                contentStack.Children.Add(bodyBlock);
            }

            Grid.SetColumn(contentStack, 1);
            grid.Children.Add(contentStack);

            var removeBtn = new Button
            {
                Content = "✕",
                FontSize = 11,
                Foreground = BrushNotifTimeFg,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                Padding = new Thickness(4, 2, 4, 2),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(6, 0, 0, 0)
            };
            string itemId = item.Id;
            removeBtn.Click += (s, e) =>
            {
                e.Handled = true;
                NotificationManager.RemoveHistoryItem(itemId);
                RenderNotificationCenterItems();
                UpdateNotificationBadge();
            };
            Grid.SetColumn(removeBtn, 2);
            grid.Children.Add(removeBtn);

            card.Child = grid;

            card.MouseLeftButtonUp += (s, e) =>
            {
                item.IsRead = true;
                PopupNotifications.IsOpen = false;
                UpdateNotificationBadge();

                var existingTab = Tabs.FirstOrDefault(t => !t.IsDisposed && t.CurrentUri != null &&
                    t.CurrentUri.GetLeftPart(UriPartial.Authority).Equals(item.Origin, StringComparison.OrdinalIgnoreCase));

                if (existingTab != null)
                {
                    FocusAndSelectTab(existingTab);
                }
                else if (!string.IsNullOrEmpty(item.Origin))
                {
                    _ = AddNewTabAsync(item.Origin, isIncognito: false);
                }
            };

            PanelNotificationsList.Children.Add(card);
        }
    }

    // ── Notificaciones Web: Interfaz y Permisos Granulares (Fase 1) ──

    private void UpdateNotificationPermissionUI()
    {
        if (_activeTab?.PendingNotificationRequest != null && !_activeTab.PendingNotificationRequest.IsResolved)
        {
            string host = _activeTab.PendingNotificationRequest.DisplayHost;
            TxtNotificationPromptTitle.Text = string.IsNullOrEmpty(host) ? "Permiso de notificaciones" : $"{host} solicita permiso";
            TxtNotificationPromptMessage.Text = string.IsNullOrEmpty(host)
                ? "Este sitio solicita permiso para enviarte alertas y novedades en el escritorio."
                : $"El sitio {host} desea enviarte avisos y novedades en el escritorio.";
            PillNotificationPermission.Visibility = Visibility.Visible;
        }
        else
        {
            PillNotificationPermission.Visibility = Visibility.Collapsed;
            PopupNotificationPermission.IsOpen = false;
        }
    }

    private void PillNotificationPermission_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab?.PendingNotificationRequest != null && !_activeTab.PendingNotificationRequest.IsResolved)
        {
            PopupNotificationPermission.IsOpen = !PopupNotificationPermission.IsOpen;
        }
    }

    private void BtnNotificationAllow_Click(object sender, RoutedEventArgs e)
    {
        ResolveActiveNotificationPermission(SiteNotificationMode.Allow, CoreWebView2PermissionState.Allow, savesInProfile: !_activeTab?.IsIncognito ?? true);
    }

    private void BtnNotificationSilent_Click(object sender, RoutedEventArgs e)
    {
        ResolveActiveNotificationPermission(SiteNotificationMode.Silent, CoreWebView2PermissionState.Allow, savesInProfile: !_activeTab?.IsIncognito ?? true);
    }

    private void BtnNotificationDismiss_Click(object sender, RoutedEventArgs e)
    {
        ResolveActiveNotificationPermission(SiteNotificationMode.Ask, CoreWebView2PermissionState.Default, savesInProfile: false);
    }

    private void BtnNotificationBlock_Click(object sender, RoutedEventArgs e)
    {
        ResolveActiveNotificationPermission(SiteNotificationMode.Block, CoreWebView2PermissionState.Deny, savesInProfile: !_activeTab?.IsIncognito ?? true);
    }

    private void ResolveActiveNotificationPermission(SiteNotificationMode mode, CoreWebView2PermissionState state, bool savesInProfile)
    {
        PopupNotificationPermission.IsOpen = false;
        PillNotificationPermission.Visibility = Visibility.Collapsed;

        var tab = _activeTab;
        if (tab?.PendingNotificationRequest == null || tab.PendingNotificationRequest.IsResolved) return;

        var req = tab.PendingNotificationRequest;
        tab.PendingNotificationRequest = null;

        if (mode != SiteNotificationMode.Ask)
        {
            NotificationPermissionManager.SetSiteMode(req.Origin, mode, tab.IsIncognito);
        }

        req.Resolve(state, savesInProfile);

        string eventType = state switch
        {
            CoreWebView2PermissionState.Allow => "NOTIFICATION_PERMISSION_ALLOWED",
            CoreWebView2PermissionState.Deny => "NOTIFICATION_PERMISSION_BLOCKED",
            _ => "NOTIFICATION_PERMISSION_DISMISSED"
        };

        string tabDesc = $"Tab:{tab.TabId} ({req.DisplayHost})";
        ShieldLogger.Log(eventType, req.DisplayHost, req.Origin, "MainFrame", $"User selected {mode} (State={state})", tabDesc);

        if (state == CoreWebView2PermissionState.Deny)
        {
            ShowToast(
                $"🚫 Notificaciones bloqueadas para {req.DisplayHost}.",
                "Ajustes ⚙️",
                () => OpenSettings("Notifications")
            );
        }
        else if (state == CoreWebView2PermissionState.Allow)
        {
            ShowToast(
                $"🔔 Notificaciones permitidas para {req.DisplayHost}.",
                "Personalizar ⚙️",
                () => OpenSettings("Notifications")
            );
        }
    }

    private static bool _capturePermMigrationRunning;

    private async Task ResetStaleCapturePermissionsOnceAsync(CoreWebView2 core)
    {
        if (SettingsManager.Current.CapturePermissionsMigrationV1Done || _capturePermMigrationRunning) return;
        _capturePermMigrationRunning = true;
        try
        {
            var list = await core.Profile.GetNonDefaultPermissionSettingsAsync();
            int reset = 0;
            foreach (var s in list)
            {
                if (s.PermissionState != CoreWebView2PermissionState.Deny) continue;
                if (s.PermissionKind != CoreWebView2PermissionKind.Microphone &&
                    s.PermissionKind != CoreWebView2PermissionKind.Camera) continue;
                try
                {
                    await core.Profile.SetPermissionStateAsync(s.PermissionKind, s.PermissionOrigin, CoreWebView2PermissionState.Default);
                    reset++;
                }
                catch { }
            }
            SettingsManager.Current.CapturePermissionsMigrationV1Done = true;
            SettingsManager.Save(notify: false);
            ShieldLogger.Log("CAPTURE_PERMISSION_MIGRATION", "local", "", "Profile", $"Reset {reset} stale Deny entries (mic/camera)");
        }
        catch (Exception ex)
        {
            ShieldLogger.Log("CAPTURE_PERMISSION_MIGRATION_ERROR", "local", "", "Profile", ex.Message);
        }
        finally { _capturePermMigrationRunning = false; }
    }

    private void HandleCorePermissionRequested(BrowserTab tab, CoreWebView2 core, CoreWebView2PermissionRequestedEventArgs e)
    {
        if (tab.IsDisposed) return;

        string uriStr = e.Uri ?? string.Empty;
        string host = "unknown";
        try
        {
            if (!string.IsNullOrWhiteSpace(uriStr) && Uri.TryCreate(uriStr, UriKind.Absolute, out var parsedUri))
                host = parsedUri.Host;
        }
        catch { }

        string tabDesc = $"Tab:{tab.TabId} ({(tab.CurrentUri != null ? tab.CurrentUri.Host : "blank")})";

        if (e.PermissionKind == CoreWebView2PermissionKind.Camera ||
            e.PermissionKind == CoreWebView2PermissionKind.Microphone)
        {
            ShieldLogger.Log("CAPTURE_PERMISSION_REQUESTED", host, uriStr, "MainFrame", $"{e.PermissionKind} state={e.State} userInitiated={e.IsUserInitiated}", tabDesc);

            bool isSafeOrigin = false;

            try
            {
                if (Uri.TryCreate(uriStr, UriKind.Absolute, out var reqUri))
                {
                    if (reqUri.Scheme == Uri.UriSchemeHttps ||
                        string.Equals(reqUri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(reqUri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(reqUri.Host, "::1", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(reqUri.Host, "tv.lex.local", StringComparison.OrdinalIgnoreCase))
                    {
                        isSafeOrigin = true;
                    }
                }
            }
            catch { }

            // (a) Origen no seguro (solo https, localhost o tv.lex.local) -> Deny, Handled=true y registro en ShieldLogger
            if (!isSafeOrigin)
            {
                e.State = CoreWebView2PermissionState.Deny;
                e.SavesInProfile = false;
                e.Handled = true;
                ShieldLogger.Log("CAPTURE_PERMISSION_BLOCKED", host, uriStr, "InsecureOrigin",
                    $"{e.PermissionKind} permission denied because origin is not secure (requires https, localhost, or tv.lex.local)", tabDesc);
                return;
            }

            // (c) Incógnito e invitado: no guardar en perfil permanente
            if (tab.IsIncognito || tab.IsGuest)
            {
                e.SavesInProfile = false;
            }

            // (b) Si Windows lo bloquea para aplicaciones de escritorio -> ShowToast informativo y continuar flujo normal
            bool isBlockedByWindows = e.PermissionKind == CoreWebView2PermissionKind.Microphone
                ? WindowsPrivacy.IsMicrophoneBlocked()
                : WindowsPrivacy.IsCameraBlocked();

            if (isBlockedByWindows)
            {
                string deviceName = e.PermissionKind == CoreWebView2PermissionKind.Microphone ? "el micrófono" : "la cámara";
                string settingsUri = e.PermissionKind == CoreWebView2PermissionKind.Microphone ? "ms-settings:privacy-microphone" : "ms-settings:privacy-webcam";

                ShowToast(
                    $"Windows está bloqueando {deviceName} para aplicaciones de escritorio. Actívalo en Privacidad y seguridad.",
                    "Abrir ajustes",
                    () =>
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = settingsUri,
                                UseShellExecute = true
                            });
                        }
                        catch { }
                    }
                );
            }

            // (d) En el resto no pongas Handled. Mantén HasCaptureDevicePermission.
            if (e.State != CoreWebView2PermissionState.Deny)
            {
                tab.HasCaptureDevicePermission = true;
            }
            return;
        }

        if (e.PermissionKind != CoreWebView2PermissionKind.Notifications)
        {
            return;
        }

        bool isCrossOriginOrSubframe = false;
        try
        {
            if (tab.CurrentUri != null && !string.IsNullOrWhiteSpace(e.Uri))
            {
                var reqHost = new Uri(e.Uri).Host;
                var mainHost = tab.CurrentUri.Host;
                if (!string.Equals(reqHost, mainHost, StringComparison.OrdinalIgnoreCase) &&
                    !reqHost.EndsWith("." + mainHost, StringComparison.OrdinalIgnoreCase))
                {
                    isCrossOriginOrSubframe = true;
                }
            }
        }
        catch { }

        if (isCrossOriginOrSubframe)
        {
            e.State = CoreWebView2PermissionState.Deny;
            e.SavesInProfile = false;
            e.Handled = true;
            ShieldLogger.Log("NOTIFICATION_PERMISSION_BLOCKED", host, uriStr, "Subframe", "Cross-origin subframe blocked by security policy", tabDesc);
            return;
        }

        if (SettingsManager.Current.NotificationGlobalPolicy == "BlockAll")
        {
            e.State = CoreWebView2PermissionState.Deny;
            e.SavesInProfile = false;
            e.Handled = true;
            ShieldLogger.Log("NOTIFICATION_PERMISSION_BLOCKED", host, uriStr, "MainFrame", "Blocked by global policy (BlockAll)", tabDesc);
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(uriStr) && AdBlockFilter.ShouldBlock(new Uri(uriStr)))
            {
                e.State = CoreWebView2PermissionState.Deny;
                e.SavesInProfile = false;
                e.Handled = true;
                ShieldLogger.Log("NOTIFICATION_PERMISSION_BLOCKED", host, uriStr, "MainFrame", "Origin in ad/tracking filter list", tabDesc);
                return;
            }
        }
        catch { }

        string origin = NotificationPermissionManager.NormalizeOrigin(uriStr);
        var existingMode = NotificationPermissionManager.GetSiteMode(origin, tab.IsIncognito);

        if (existingMode == SiteNotificationMode.Block)
        {
            e.State = CoreWebView2PermissionState.Deny;
            e.SavesInProfile = !tab.IsIncognito;
            e.Handled = true;
            ShieldLogger.Log("NOTIFICATION_PERMISSION_BLOCKED", host, uriStr, "MainFrame", "Previously blocked by user preference", tabDesc);
            return;
        }

        if (existingMode == SiteNotificationMode.Allow ||
            existingMode == SiteNotificationMode.Silent ||
            existingMode == SiteNotificationMode.Digest)
        {
            e.State = CoreWebView2PermissionState.Allow;
            e.SavesInProfile = !tab.IsIncognito;
            e.Handled = true;
            ShieldLogger.Log("NOTIFICATION_PERMISSION_ALLOWED", host, uriStr, "MainFrame", $"Previously allowed (Mode={existingMode})", tabDesc);
            return;
        }

        if (SettingsManager.Current.NotificationGlobalPolicy == "SilentAll")
        {
            NotificationPermissionManager.SetSiteMode(origin, SiteNotificationMode.Silent, tab.IsIncognito);
            e.State = CoreWebView2PermissionState.Allow;
            e.SavesInProfile = !tab.IsIncognito;
            e.Handled = true;
            ShieldLogger.Log("NOTIFICATION_PERMISSION_ALLOWED", host, uriStr, "MainFrame", "Auto-allowed as Silent by global policy", tabDesc);
            return;
        }

        var deferral = e.GetDeferral();
        e.Handled = true;

        var pendingReq = new PendingNotificationPermissionRequest(e, deferral, origin, host);
        tab.PendingNotificationRequest = pendingReq;

        ShieldLogger.Log("NOTIFICATION_PERMISSION_REQUESTED", host, uriStr, "MainFrame", "User prompted via omnibox chip", tabDesc);

        var win = tab.OwnerWindow ?? this;
        win.Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            if (win._activeTab == tab)
            {
                win.UpdateNotificationPermissionUI();
                win.PopupNotificationPermission.IsOpen = true;
            }
        });
    }
}

