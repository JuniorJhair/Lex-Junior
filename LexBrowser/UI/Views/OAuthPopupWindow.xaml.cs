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
    private CoreWebView2? _popupCore;
    private string? _oauthAutofillSourceUrl;
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
        _popupCore = core;

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
                if (!e.IsSuccess || _isIncognito) return;
                TxtPopupUrl.Text = core.Source ?? string.Empty;
            });
        };

        core.WebMessageReceived += (_, e) =>
        {
            try
            {
                if (_isIncognito || !SettingsManager.Current.EnablePasswordManager) return;
                string actualSource = e.Source ?? string.Empty;
                Uri? currentUri = Uri.TryCreate(core.Source, UriKind.Absolute, out var cu) ? cu : null;
                if (!MainWindow.IsSecureOriginForPasswordMessage(actualSource, currentUri)) return;

                string raw = e.WebMessageAsJson;
                if (string.IsNullOrEmpty(raw) || raw.Length > 4096) return;

                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var tProp)) return;
                string msgType = tProp.GetString() ?? "";

                if (msgType == "autofill_requested")
                {
                    var matches = PasswordManager.Store.GetAutofillCredentialsForUrl(actualSource);
                    if (matches.Count == 1)
                    {
                        var primary = matches[0];
                        string script = PasswordManager.BuildSafeAutofillScript(primary.Username, primary.Password);
                        Dispatcher.BeginInvoke(async () =>
                        {
                            try { await core.ExecuteScriptAsync(script); } catch { }
                        });
                    }
                    else if (matches.Count > 1)
                    {
                        Rect? inputRect = null;
                        if (root.TryGetProperty("rect", out var rectProp) && rectProp.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            double l = rectProp.TryGetProperty("left", out var lp) && lp.TryGetDouble(out var lv) ? lv : 0;
                            double t = rectProp.TryGetProperty("top", out var tp) && tp.TryGetDouble(out var tv) ? tv : 0;
                            double r = rectProp.TryGetProperty("right", out var rp) && rp.TryGetDouble(out var rv) ? rv : l + 220;
                            double b = rectProp.TryGetProperty("bottom", out var bp) && bp.TryGetDouble(out var bv) ? bv : t + 36;
                            if (r > l && b > t) inputRect = new Rect(l, t, r - l, b - t);
                        }
                        Dispatcher.BeginInvoke(() => ShowOAuthAutofillPicker(actualSource, matches, inputRect));
                    }
                }
                else if (msgType == "password_submitted")
                {
                    string usr = root.TryGetProperty("username", out var usrProp) ? (usrProp.GetString() ?? "") : "";
                    string pwd = root.TryGetProperty("password", out var pwdProp) ? (pwdProp.GetString() ?? "") : "";
                    if (!string.IsNullOrEmpty(pwd))
                    {
                        Dispatcher.BeginInvoke(() => PasswordSubmittedInPopup?.Invoke(actualSource, usr, pwd));
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

    private void ShowOAuthAutofillPicker(
        string sourceUrl,
        System.Collections.Generic.IReadOnlyList<SavedCredential> matches,
        Rect? inputRect)
    {
        if (PopupOAuthAutofillPicker == null || ItemsOAuthAutofillAccounts == null || PopupWebViewHost == null) return;
        _oauthAutofillSourceUrl = sourceUrl;
        ItemsOAuthAutofillAccounts.ItemsSource = matches;

        double hostW = PopupWebViewHost.ActualWidth > 0 ? PopupWebViewHost.ActualWidth : 480;
        double hostH = PopupWebViewHost.ActualHeight > 0 ? PopupWebViewHost.ActualHeight : 520;
        double x = inputRect.HasValue ? Math.Clamp(inputRect.Value.Left, 8, Math.Max(8, hostW - 292)) : Math.Max(10, hostW - 292);
        double y = inputRect.HasValue ? Math.Clamp(inputRect.Value.Bottom + 4, 8, Math.Max(8, hostH - 200)) : 12;

        PopupOAuthAutofillPicker.HorizontalOffset = x;
        PopupOAuthAutofillPicker.VerticalOffset = y;
        PopupOAuthAutofillPicker.IsOpen = true;
    }

    private async void BtnPickOAuthAutofillAccount_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (PopupOAuthAutofillPicker != null) PopupOAuthAutofillPicker.IsOpen = false;
            if (sender is not System.Windows.Controls.Button btn || btn.Tag is not SavedCredential picked) return;
            var core = _popupCore;
            if (core == null) return;

            Uri? currentUri = Uri.TryCreate(core.Source, UriKind.Absolute, out var cu) ? cu : null;
            string sourceUrl = !string.IsNullOrWhiteSpace(_oauthAutofillSourceUrl) ? _oauthAutofillSourceUrl : (core.Source ?? "");
            if (!MainWindow.IsSecureOriginForPasswordMessage(sourceUrl, currentUri)) return;

            string pwd = PasswordManager.Store.GetPasswordById(picked.Id) ?? picked.Password;
            if (string.IsNullOrEmpty(pwd)) return;

            try { PopupWebView.Focus(); } catch { }
            string script = PasswordManager.BuildSafeAutofillScript(picked.Username, pwd, forceReplace: true);
            await core.ExecuteScriptAsync(script);
        }
        catch { }
    }
}

