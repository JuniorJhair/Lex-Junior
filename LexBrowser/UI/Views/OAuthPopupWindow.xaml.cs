using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using LexBrowser.Core;
using LexBrowser.Core.AdBlock;
using LexBrowser.Core.Engine;

namespace LexBrowser.UI.Views;

/// <summary>
/// Ventana emergente real conectada a <c>e.NewWindow</c> de WebView2.
/// Preserva <c>window.opener</c> y <c>postMessage</c> para que inicios de sesión OAuth
/// ("Continuar con Google", Apple, Microsoft, Discord, X/Twitter, etc.) funcionen al 100% y cierren solos.
/// </summary>
public partial class OAuthPopupWindow : Window
{
    private readonly bool _isIncognito;
    public event Action<string, string, string>? PasswordSubmittedInPopup;

    public OAuthPopupWindow(bool isIncognito, string? initialUrl = null, double? requestedWidth = null, double? requestedHeight = null)
    {
        _isIncognito = isIncognito;
        InitializeComponent();

        if (!string.IsNullOrWhiteSpace(initialUrl))
        {
            TxtPopupUrl.Text = initialUrl;
        }

        if (requestedWidth.HasValue && requestedWidth.Value >= 320 && requestedWidth.Value <= 1400)
        {
            Width = requestedWidth.Value;
        }
        if (requestedHeight.HasValue && requestedHeight.Value >= 360 && requestedHeight.Value <= 1000)
        {
            Height = requestedHeight.Value;
        }
    }

    public async Task<CoreWebView2> InitializePopupCoreAsync()
    {
        var env = await BrowserEngine.GetEnvironmentAsync();
        if (_isIncognito)
        {
            var opts = env.CreateCoreWebView2ControllerOptions();
            opts.IsInPrivateModeEnabled = true;
            await PopupWebView.EnsureCoreWebView2Async(env, opts);
        }
        else
        {
            await PopupWebView.EnsureCoreWebView2Async(env);
        }

        var core = PopupWebView.CoreWebView2;

        // Inyectar captura pasiva de contraseñas por si el usuario escribe su cuenta en la ventana emergente
        try
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(PasswordManager.InjectedPasswordScript);
        }
        catch { }

        core.DocumentTitleChanged += (_, _) =>
        {
            Dispatcher.Invoke(() =>
            {
                if (!string.IsNullOrWhiteSpace(core.DocumentTitle))
                {
                    TxtPopupTitle.Text = $"{core.DocumentTitle} — Lex Junior";
                    Title = TxtPopupTitle.Text;
                }
            });
        };

        core.SourceChanged += (_, _) =>
        {
            Dispatcher.Invoke(() =>
            {
                string src = core.Source ?? string.Empty;
                TxtPopupUrl.Text = src;
                TxtPopupLockIcon.Text = src.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "🔒" : "ℹ️";
            });
        };

        core.NavigationCompleted += (_, e) =>
        {
            Dispatcher.Invoke(() =>
            {
                if (e.IsSuccess && !_isIncognito && SettingsManager.Current.EnablePasswordManager && !string.IsNullOrWhiteSpace(core.Source))
                {
                    if (PasswordVaultStore.IsDomainMatch(core.Source, core.Source, requireHttpsForAutofill: true))
                    {
                        var matches = PasswordManager.Store.GetCredentialsForUrl(core.Source);
                        if (matches.Count > 0)
                        {
                            string payload = System.Text.Json.JsonSerializer.Serialize(new
                            {
                                action = "autofill_credentials",
                                username = matches[0].Username,
                                password = matches[0].Password
                            });
                            try { core.PostWebMessageAsJson(payload); } catch { }
                        }
                    }
                }
            });
        };

        core.WebMessageReceived += (_, e) =>
        {
            try
            {
                if (_isIncognito || !SettingsManager.Current.EnablePasswordManager) return;
                string raw = e.WebMessageAsJson;
                if (string.IsNullOrEmpty(raw) || raw.Length > 4096) return;

                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (root.TryGetProperty("type", out var tProp) && tProp.GetString() == "password_submitted")
                {
                    string u = root.TryGetProperty("url", out var uProp) ? (uProp.GetString() ?? "") : "";
                    string usr = root.TryGetProperty("username", out var usrProp) ? (usrProp.GetString() ?? "") : "";
                    string pwd = root.TryGetProperty("password", out var pwdProp) ? (pwdProp.GetString() ?? "") : "";
                    if (!string.IsNullOrWhiteSpace(u) && !string.IsNullOrEmpty(pwd))
                    {
                        Dispatcher.BeginInvoke(() => PasswordSubmittedInPopup?.Invoke(u, usr, pwd));
                    }
                }
            }
            catch { }
        };

        // CRÍTICO: Cuando Google/Apple termina de autenticar y ejecuta window.close(), cerrar automáticamente la ventana emergente
        core.WindowCloseRequested += (_, _) =>
        {
            Dispatcher.Invoke(() =>
            {
                try { Close(); } catch { }
            });
        };

        return core;
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            PopupWebView?.Dispose();
        }
        catch { }
        base.OnClosed(e);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            BtnMaximize_Click(sender, e);
        }
        else if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}

