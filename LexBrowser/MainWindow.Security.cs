using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LexBrowser.Core;
using LexBrowser.Core.AdBlock;
using LexBrowser.UI.Models;
using LexBrowser.UI.Views;

namespace LexBrowser;

public partial class MainWindow
{
    private bool _isGuestSession = false;
    private readonly List<BrowserTab> _stashedOwnerTabs = new();
    private BrowserTab? _stashedOwnerActiveTab;

    public bool IsGuestSessionWindow => _isGuestSession;
    internal bool IsPrivateImportRestricted => _isGuestSession || (_activeTab?.IsIncognito == true);

    private string _pendingSaveOriginUrl = string.Empty;
    private DispatcherTimer? _promptPeekTimer;
    private DispatcherTimer? _clipboardShieldTimer;
    private string _lastCopiedSecretPassword = string.Empty;
    private string? _lastRejectedOverlayCandidate;
    private bool _isVerifyingOverlayPin = false;

    /// <summary>Indica si la pantalla de bloqueo por PIN está activa.</summary>
    public bool IsLocked => BrowserLockOverlay != null && BrowserLockOverlay.Visibility == Visibility.Visible;

    public bool IsBrowserLockOverlayActive() => IsLocked;

    private void SetLockOverlayVisible(bool visible)
    {
        if (BrowserLockOverlay != null)
        {
            BrowserLockOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
        if (TabStripContainer != null)
        {
            TabStripContainer.IsHitTestVisible = !visible;
        }
    }

    public void OpenDevToolsForActiveTab()
    {
        if (IsLocked) return;
        try
        {
            var core = _activeTab?.WebView?.CoreWebView2;
            if (core != null)
            {
                core.Settings.AreDevToolsEnabled = true;
                core.OpenDevToolsWindow();
            }
        }
        catch { }
    }

    public void CopyOrExportShieldLog(bool onlyActiveTab = false)
    {
        if (IsLocked) return;
        if (_isGuestSession && !TryUnlockFromGuestMode("Desbloquea con tu PIN para exportar el registro de seguridad"))
        {
            return;
        }

        try
        {
            string? tabFilter = (onlyActiveTab && _activeTab != null) ? $"Tab:{_activeTab.TabId} " : null;
            string dump = ShieldLogger.DumpLog(tabFilter);
            int count = ShieldLogger.Count;

            try
            {
                Clipboard.SetDataObject(dump, true);
            }
            catch { }

            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string logPath = Path.Combine(desktopPath, "LexJunior_ShieldLog.txt");
            try
            {
                File.WriteAllText(logPath, dump, System.Text.Encoding.UTF8);
            }
            catch { }

            string scopeLabel = onlyActiveTab ? "de esta pestaña" : $"({count} eventos)";
            ShowToast($"🛡️ Shield Log {scopeLabel} copiado", "Abrir Log 📄", () =>
            {
                try
                {
                    if (File.Exists(logPath))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(logPath) { UseShellExecute = true });
                    }
                }
                catch { }
            });
        }
        catch (Exception ex)
        {
            ShowToast($"Error al exportar Shield Log: {ex.Message}");
        }
    }

    private void BtnPasswordVault_Click(object sender, RoutedEventArgs e)
    {
        OpenPasswordVault();
    }

    public void OpenPasswordVault(string initialSection = "Vault")
    {
        if (IsLocked) return;
        if (_isGuestSession)
        {
            if (!TryUnlockFromGuestMode("Desbloquea con tu PIN de propietario para acceder a la Bóveda de Contraseñas"))
                return;
        }
        else if (PasswordManager.ShouldRequireUnlockForVaultAction())
        {
            var pinDialog = new PinPromptDialog("Confirma tu PIN para abrir la Bóveda de Contraseñas") { Owner = this };
            if (pinDialog.ShowDialog() != true)
                return;
        }

        var vaultWin = new PasswordVaultWindow(initialSection)
        {
            Owner = this
        };
        vaultWin.NavigateRequested += async (targetUrl) =>
        {
            if (IsSafeHttpUrl(targetUrl))
            {
                if (_activeTab?.WebView?.CoreWebView2 != null && _activeTab.IsNewTab)
                {
                    _activeTab.WebView.CoreWebView2.Navigate(targetUrl);
                }
                else
                {
                    await AddNewTabAsync(targetUrl, isIncognito: _isGuestSession);
                }
            }
        };
        vaultWin.ClipboardShieldActivated += ActivateClipboardShield;
        vaultWin.ShowDialog();
        ApplySettings();
    }

    public void ActivateClipboardShield(string secretPassword)
    {
        if (string.IsNullOrEmpty(secretPassword)) return;
        try
        {
            var dataObject = new DataObject();
            dataObject.SetText(secretPassword);
            // Excluir del historial del portapapeles de Windows (Win+V) y de sincronización en la nube
            dataObject.SetData("ExcludeClipboardContentFromMonitorProcessing", true);
            dataObject.SetData("CanIncludeInClipboardHistory", 0);
            dataObject.SetData("CanUploadToCloudClipboard", 0);
            Clipboard.SetDataObject(dataObject, false);

            _lastCopiedSecretPassword = secretPassword;

            if (_clipboardShieldTimer == null)
            {
                _clipboardShieldTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
                _clipboardShieldTimer.Tick += (_, _) =>
                {
                    _clipboardShieldTimer.Stop();
                    try
                    {
                        if (!string.IsNullOrEmpty(_lastCopiedSecretPassword) &&
                            Clipboard.ContainsText() &&
                            Clipboard.GetText() == _lastCopiedSecretPassword)
                        {
                            Clipboard.Clear();
                            ShowToast("🛡️ Escudo de Portapapeles: la contraseña copiada se limpió automáticamente (20s).");
                        }
                    }
                    catch { }
                    _lastCopiedSecretPassword = string.Empty;
                };
            }

            _clipboardShieldTimer.Stop();
            _clipboardShieldTimer.Start();
            ShowToast("📋 Contraseña copiada — 🛡️ Escudo activo: se borrará sola del portapapeles en 20s.");
        }
        catch { }
    }

    private void PromptSaveOrUpdatePassword(string url, string username, string password)
    {
        if (_isGuestSession || IsLocked || !SettingsManager.Current.EnablePasswordManager)
            return;

        string baseDomain = PasswordVaultStore.ExtractBaseDomain(url);
        if (string.IsNullOrEmpty(baseDomain) || PasswordManager.Store.IsDomainExcluded(baseDomain))
            return;

        var existing = PasswordManager.Store.FindExistingCredential(baseDomain, username);
        if (existing != null && existing.Password == password)
        {
            return;
        }

        _pendingSaveOriginUrl = url;
        TxtSavePwdPromptTitle.Text = existing != null
            ? $"¿Actualizar contraseña para {baseDomain}?"
            : $"¿Guardar contraseña para {baseDomain}?";

        TxtSavePwdDomainSubtitle.Text = $"Se asociará al dominio '{baseDomain}' y funcionará en todas sus rutas.";
        TxtPromptSaveUsername.Text = username;
        PwdPromptSavePassword.Password = password;
        TxtPromptSavePasswordPlain.Text = string.Empty;
        TxtPromptSavePasswordPlain.Visibility = Visibility.Collapsed;
        PwdPromptSavePassword.Visibility = Visibility.Visible;
        BtnPromptPeekPassword.Content = "👁️";

        SavePasswordBanner.Visibility = Visibility.Visible;
        if (PopupSavePassword != null) PopupSavePassword.IsOpen = true;
    }

    private void HideSavePasswordBanner()
    {
        _promptPeekTimer?.Stop();
        SavePasswordBanner.Visibility = Visibility.Collapsed;
        if (PopupSavePassword != null) PopupSavePassword.IsOpen = false;
        PwdPromptSavePassword.Password = string.Empty;
        TxtPromptSavePasswordPlain.Text = string.Empty;
        _pendingSaveOriginUrl = string.Empty;
    }

    private void BtnPromptPeekPassword_Click(object sender, RoutedEventArgs e)
    {
        if (TxtPromptSavePasswordPlain.Visibility == Visibility.Visible)
        {
            _promptPeekTimer?.Stop();
            PwdPromptSavePassword.Password = TxtPromptSavePasswordPlain.Text;
            TxtPromptSavePasswordPlain.Text = string.Empty;
            TxtPromptSavePasswordPlain.Visibility = Visibility.Collapsed;
            PwdPromptSavePassword.Visibility = Visibility.Visible;
            BtnPromptPeekPassword.Content = "👁️";
            return;
        }

        TxtPromptSavePasswordPlain.Text = PwdPromptSavePassword.Password;
        PwdPromptSavePassword.Visibility = Visibility.Collapsed;
        TxtPromptSavePasswordPlain.Visibility = Visibility.Visible;
        BtnPromptPeekPassword.Content = "🙈 5s";

        if (_promptPeekTimer == null)
        {
            _promptPeekTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _promptPeekTimer.Tick += (_, _) =>
            {
                _promptPeekTimer.Stop();
                if (TxtPromptSavePasswordPlain.Visibility == Visibility.Visible)
                {
                    PwdPromptSavePassword.Password = TxtPromptSavePasswordPlain.Text;
                    TxtPromptSavePasswordPlain.Text = string.Empty;
                    TxtPromptSavePasswordPlain.Visibility = Visibility.Collapsed;
                    PwdPromptSavePassword.Visibility = Visibility.Visible;
                    BtnPromptPeekPassword.Content = "👁️";
                }
            };
        }

        _promptPeekTimer.Stop();
        _promptPeekTimer.Start();
    }

    private void BtnConfirmSavePassword_Click(object sender, RoutedEventArgs e)
    {
        string pwd = TxtPromptSavePasswordPlain.Visibility == Visibility.Visible
            ? TxtPromptSavePasswordPlain.Text
            : PwdPromptSavePassword.Password;

        string user = TxtPromptSaveUsername.Text.Trim();
        string domain = PasswordVaultStore.ExtractBaseDomain(_pendingSaveOriginUrl);

        if (!string.IsNullOrEmpty(domain) && !string.IsNullOrEmpty(pwd))
        {
            PasswordManager.Store.SaveOrUpdateCredential(_pendingSaveOriginUrl, user, pwd);
            ShowToast($"🔑 Contraseña guardada para {domain} (disponible en tu Bóveda 🔑).");
        }

        HideSavePasswordBanner();
    }

    private void BtnDismissSavePassword_Click(object sender, RoutedEventArgs e)
    {
        HideSavePasswordBanner();
    }

    private void BtnNeverSaveForDomain_Click(object sender, RoutedEventArgs e)
    {
        string domain = PasswordVaultStore.ExtractBaseDomain(_pendingSaveOriginUrl);
        if (!string.IsNullOrEmpty(domain))
        {
            PasswordManager.Store.AddExcludedDomain(domain);
            ShowToast($"🚫 No se volverá a preguntar en {domain} (puedes revertirlo en la Bóveda 🔑).");
        }

        HideSavePasswordBanner();
    }

    /// <summary>
    /// Autocompletado seguro bajo demanda: solo se ejecuta cuando el usuario enfoca un campo de login en una página HTTPS/localhost
    /// verificada contra el origen real de la pestaña, inyectando los valores mediante ExecuteScriptAsync (sin PostWebMessageAsJson).
    /// </summary>
    private async Task HandleAutofillRequestAsync(BrowserTab tab, string messageSourceUrl)
    {
        try
        {
            if (tab?.WebView?.CoreWebView2 == null || tab.IsDisposed || tab.IsIncognito || _isGuestSession || IsLocked)
                return;

            if (!SettingsManager.Current.EnablePasswordManager)
                return;

            string mainUrl = tab.CurrentUri?.AbsoluteUri ?? tab.Url;
            if (string.IsNullOrWhiteSpace(mainUrl))
                return;

            // Verificar que el mensaje provenga del mismo dominio que la pestaña principal y sea HTTPS (o localhost)
            string effectiveUrl = !string.IsNullOrWhiteSpace(messageSourceUrl) ? messageSourceUrl : mainUrl;
            if (!PasswordVaultStore.IsDomainMatch(mainUrl, effectiveUrl, requireHttpsForAutofill: true))
                return;

            var matches = PasswordManager.Store.GetCredentialsForUrl(effectiveUrl);
            if (matches.Count == 0) return;

            var primary = matches[0];
            string script = PasswordManager.BuildSafeAutofillScript(primary.Username, primary.Password);
            await tab.WebView.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch { }
    }

    // ─────────────────────────────────────────────────────────────────────
    // PANTALLA DE BLOQUEO AL INICIAR, CONFIGURACIÓN INICIAL Y MODO INVITADO
    // ─────────────────────────────────────────────────────────────────────
    private void ShowFirstTimeSecuritySetupOverlay()
    {
        LockOverlayFirstTimeSetupPanel.Visibility = Visibility.Visible;
        LockOverlayPinPanel.Visibility = Visibility.Collapsed;
        LockOverlayRecoveryPanel.Visibility = Visibility.Collapsed;
        TxtFirstSetupError.Visibility = Visibility.Collapsed;
        PwdFirstSetupPin.Password = string.Empty;
        SetLockOverlayVisible(true);

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => PwdFirstSetupPin.Focus());
    }

    private async void BtnFirstSetupSavePin_Click(object sender, RoutedEventArgs e)
    {
        string pin = PwdFirstSetupPin.Password;
        if (string.IsNullOrWhiteSpace(pin))
        {
            TxtFirstSetupError.Text = "⚠️ Escribe un PIN o contraseña (ej. 6 dígitos o letras) o pulsa abajo 'No me gustaría usar contraseña'.";
            TxtFirstSetupError.Visibility = Visibility.Visible;
            PwdFirstSetupPin.Focus();
            return;
        }

        string mode = "SmartSession";
        if (CmbFirstSetupLockMode.SelectedItem is ComboBoxItem item && item.Tag is string tag && !string.IsNullOrWhiteSpace(tag))
        {
            mode = tag;
        }

        string question = TxtFirstSetupQuestion.Text.Trim();
        string answer = TxtFirstSetupAnswer.Text.Trim();

        string recoveryCode = PasswordManager.ConfigureMasterPassword(pin, mode, question, answer);

        MessageBox.Show(
            $"¡Tu PIN/contraseña ha quedado activado con éxito!\n\n" +
            $"• Código de Recuperación de Emergencia: {recoveryCode}\n" +
            $"(Guárdalo o recuérdalo junto con tu respuesta secreta por si algún día olvidas tu PIN; así podrás recuperar el acceso sin perder tus contraseñas guardadas).",
            "Protección Activada — Lex Junior",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        await CompleteOverlayUnlockAsync();
        ShowToast("🔒 Protección con PIN activada. Puedes cambiar tus opciones cuando quieras en el botón 🔑.");
    }

    private async void BtnFirstSetupSkipNoPassword_Click(object sender, RoutedEventArgs e)
    {
        SettingsManager.Current.BrowserLockMode = "None";
        SettingsManager.Current.HasCompletedInitialSecuritySetup = true;
        try { File.WriteAllText(SettingsManager.SecuritySetupMarkerPath, "1"); } catch { }
        SettingsManager.Save();

        await CompleteOverlayUnlockAsync();
        ShowToast("🔓 Configurado sin contraseña al abrir. Puedes activarla cuando quieras desde el botón 🔑.");
    }

    private static string FormatLockoutWait(TimeSpan wait)
    {
        if (wait.TotalSeconds >= 60)
            return $"{(int)Math.Ceiling(wait.TotalMinutes)} min";
        return $"{(int)Math.Ceiling(wait.TotalSeconds)} s";
    }

    private void ShowOverlayPinFailure()
    {
        TimeSpan wait = PasswordManager.GetPinLockoutRemaining();
        TxtOverlayPinError.Text = wait > TimeSpan.Zero
            ? $"⏳ Demasiados intentos. Espera {FormatLockoutWait(wait)} para volver a intentar."
            : "❌ PIN o contraseña incorrecta. Inténtalo de nuevo o entra como Invitado.";
        TxtOverlayPinError.Visibility = Visibility.Visible;
        PwdOverlayPinInput.SelectAll();
        PwdOverlayPinInput.Focus();
    }

    private void ShowBrowserLockOverlay()
    {
        var s = SettingsManager.Current;
        TxtLockOverlayModeSubtitle.Text = s.BrowserLockMode == "SmartSession"
            ? "Modo Inteligente activo (Sesión de Windows nueva o reanudada)"
            : "Protección siempre activa al abrir el navegador";

        LockOverlayFirstTimeSetupPanel.Visibility = Visibility.Collapsed;
        LockOverlayRecoveryPanel.Visibility = Visibility.Collapsed;
        LockOverlayPinPanel.Visibility = Visibility.Visible;
        TxtOverlayPinError.Visibility = Visibility.Collapsed;
        PwdOverlayPinInput.Password = string.Empty;
        _lastRejectedOverlayCandidate = null;
        SetLockOverlayVisible(true);

        if (PasswordManager.IsPinLockedOut)
        {
            ShowOverlayPinFailure();
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => PwdOverlayPinInput.Focus());
    }

    private async Task CompleteOverlayUnlockAsync()
    {
        try
        {
            _lastRejectedOverlayCandidate = null;
            PasswordManager.MarkSessionUnlocked();
            _isGuestSession = false;
            SetLockOverlayVisible(false);
            ApplySettings();

            if (Tabs.Count == 0)
            {
                if (_startInIncognito)
                {
                    await AddNewTabAsync(_initialUrl, isIncognito: true, bypassLockCheck: true);
                }
                else
                {
                    await RestoreOrInitializeSessionTabsAsync();
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Unlock] Error en CompleteOverlayUnlockAsync: {ex.Message}");
        }
    }

    private async void PwdOverlayPinInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_isVerifyingOverlayPin) return;
        TxtOverlayPinError.Visibility = Visibility.Collapsed;
        string current = PwdOverlayPinInput.Password;
        int expectedLen = SettingsManager.Current.MasterPasswordLength;

        if (expectedLen > 0 && current.Length == expectedLen)
        {
            if (PasswordManager.IsPinLockedOut)
            {
                ShowOverlayPinFailure();
                return;
            }

            if (current == _lastRejectedOverlayCandidate)
                return;

            _isVerifyingOverlayPin = true;
            try
            {
                if (await PasswordManager.VerifyMasterPasswordAsync(current))
                {
                    await CompleteOverlayUnlockAsync();
                }
                else
                {
                    _lastRejectedOverlayCandidate = current;
                    ShowOverlayPinFailure();
                }
            }
            finally
            {
                _isVerifyingOverlayPin = false;
            }
        }
    }

    private void PwdOverlayPinInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            BtnOverlayUnlock_Click(sender, e);
        }
    }

    private async void BtnOverlayUnlock_Click(object sender, RoutedEventArgs e)
    {
        if (_isVerifyingOverlayPin) return;
        string candidate = PwdOverlayPinInput.Password;

        if (PasswordManager.IsPinLockedOut)
        {
            ShowOverlayPinFailure();
            return;
        }

        if (candidate == _lastRejectedOverlayCandidate)
        {
            ShowOverlayPinFailure();
            return;
        }

        _isVerifyingOverlayPin = true;
        try
        {
            if (await PasswordManager.VerifyMasterPasswordAsync(candidate))
            {
                await CompleteOverlayUnlockAsync();
            }
            else
            {
                _lastRejectedOverlayCandidate = candidate;
                ShowOverlayPinFailure();
            }
        }
        finally
        {
            _isVerifyingOverlayPin = false;
        }
    }

    private async void BtnEnterGuestMode_Click(object sender, RoutedEventArgs e)
    {
        await EnterGuestModeAsync();
    }

    public async Task EnterGuestModeAsync()
    {
        string? launchUrl = !string.IsNullOrWhiteSpace(_initialUrl) ? _initialUrl : null;
        _initialUrl = null;

        // Si esta ventana ya es la ventana dedicada de Invitado, simplemente asegurar que tenga una pestaña activa
        if (_isGuestSession)
        {
            if (Tabs.Count == 0 || !string.IsNullOrWhiteSpace(launchUrl))
            {
                await AddNewTabAsync(launchUrl, isIncognito: true, bypassLockCheck: true);
            }
            return;
        }

        // Auditoría LexShield Parte 2 (Sección 9): Abrir el Modo Invitado en una ventana MainWindow dedicada
        // sin mutar ni vaciar la ventana del propietario.
        var existingGuestWin = Application.Current?.Windows.OfType<MainWindow>()
            .FirstOrDefault(w => w != this && w._isGuestSession && w.IsLoaded);

        if (existingGuestWin != null)
        {
            if (existingGuestWin.WindowState == WindowState.Minimized)
                existingGuestWin.WindowState = WindowState.Normal;
            existingGuestWin.Activate();
            existingGuestWin.Focus();
            if (!string.IsNullOrWhiteSpace(launchUrl))
            {
                await existingGuestWin.AddNewTabAsync(launchUrl, isIncognito: true, bypassLockCheck: true);
            }
            return;
        }

        var guestWin = new MainWindow(initialUrl: launchUrl, initialTab: null, startInIncognito: true, startInGuest: true);
        guestWin.Show();
        guestWin.Activate();
        guestWin.Focus();
    }

    private async void BtnUnlockFromGuest_Click(object sender, RoutedEventArgs e)
    {
        await UnlockFromGuestToOwnerAsync();
    }

    private void DisposeGuestTabsAndRestoreStashedOwnerTabs()
    {
        foreach (var t in Tabs.ToList())
        {
            t.IsDisposed = true;
            try { t.WebView?.CoreWebView2?.Stop(); } catch { }
            if (t.WebView != null && BrowserContainer.Children.Contains(t.WebView))
            {
                BrowserContainer.Children.Remove(t.WebView);
            }
            try { t.WebView?.Dispose(); } catch { }
        }
        Tabs.Clear();
        _activeTab = null;

        if (_stashedOwnerTabs.Count > 0)
        {
            foreach (var ownerTab in _stashedOwnerTabs)
            {
                if (!ownerTab.IsDisposed)
                {
                    Tabs.Add(ownerTab);
                    if (ownerTab.WebView != null && !BrowserContainer.Children.Contains(ownerTab.WebView))
                    {
                        ownerTab.WebView.Visibility = Visibility.Collapsed;
                        BrowserContainer.Children.Add(ownerTab.WebView);
                    }
                }
            }
            var target = (_stashedOwnerActiveTab != null && Tabs.Contains(_stashedOwnerActiveTab))
                ? _stashedOwnerActiveTab
                : Tabs.FirstOrDefault();
            _stashedOwnerTabs.Clear();
            _stashedOwnerActiveTab = null;

            if (target != null)
            {
                SelectTab(target, force: true);
            }
        }
    }

    private async Task<bool> UnlockFromGuestToOwnerAsync()
    {
        var pinDialog = new PinPromptDialog("Ingresa tu PIN de propietario para salir del Modo Invitado y volver a tu cuenta personal") { Owner = this };
        if (pinDialog.ShowDialog() == true)
        {
            PasswordManager.MarkSessionUnlocked();

            var ownerWin = Application.Current?.Windows.OfType<MainWindow>()
                .FirstOrDefault(w => w != this && !w._isGuestSession);

            if (ownerWin != null)
            {
                if (ownerWin.IsLocked)
                {
                    await ownerWin.CompleteOverlayUnlockAsync();
                }
                if (ownerWin.WindowState == WindowState.Minimized)
                {
                    ownerWin.WindowState = WindowState.Normal;
                }
                ownerWin.Activate();
                ownerWin.Focus();
                ownerWin.ShowToast("🔓 Sesión de propietario desbloqueada.");
                Close();
                return true;
            }

            if (_stashedOwnerTabs.Count > 0)
            {
                _isGuestSession = false;
                ApplySettings();
                DisposeGuestTabsAndRestoreStashedOwnerTabs();
                if (Tabs.Count == 0)
                {
                    await RestoreOrInitializeSessionTabsAsync();
                }
                ShowToast("🔓 Sesión de propietario desbloqueada: tus pestañas, marcadores y contraseñas han sido restaurados.");
                return true;
            }

            var newOwnerWin = new MainWindow();
            if (Application.Current != null)
            {
                Application.Current.MainWindow = newOwnerWin;
            }
            newOwnerWin.Show();
            newOwnerWin.Activate();
            newOwnerWin.Focus();
            Close();
            return true;
        }
        return false;
    }

    private bool TryUnlockFromGuestMode(string reason)
    {
        _ = UnlockFromGuestToOwnerAsync();
        return false;
    }

    private void BtnOverlayShowRecovery_Click(object sender, RoutedEventArgs e)
    {
        var s = SettingsManager.Current;
        TxtOverlayRecoveryQuestion.Text = string.IsNullOrWhiteSpace(s.SecurityQuestion)
            ? "Ingresa tu Código de Recuperación de Emergencia (ej. LEX-XXXX-XXXX) para conservar tus contraseñas:"
            : $"Pregunta secreta: \"{s.SecurityQuestion}\" (o ingresa tu Código de Recuperación {s.RecoveryCodeHint}):";

        TxtOverlayRecoveryError.Visibility = Visibility.Collapsed;
        TxtOverlayRecoveryAnswer.Text = string.Empty;
        PwdOverlayNewPin.Password = string.Empty;
        LockOverlayPinPanel.Visibility = Visibility.Collapsed;
        LockOverlayRecoveryPanel.Visibility = Visibility.Visible;
        TxtOverlayRecoveryAnswer.Focus();
    }

    private void BtnOverlayBackToPin_Click(object sender, RoutedEventArgs e)
    {
        LockOverlayRecoveryPanel.Visibility = Visibility.Collapsed;
        LockOverlayPinPanel.Visibility = Visibility.Visible;
        PwdOverlayPinInput.Focus();
    }

    private async void BtnOverlayApplyRecovery_Click(object sender, RoutedEventArgs e)
    {
        if (_isVerifyingOverlayPin) return;
        string answer = TxtOverlayRecoveryAnswer.Text;
        string newPin = PwdOverlayNewPin.Password;

        TimeSpan wait = PasswordManager.GetPinLockoutRemaining();
        if (wait > TimeSpan.Zero)
        {
            TxtOverlayRecoveryError.Text = $"⏳ Demasiados intentos. Espera {FormatLockoutWait(wait)} para volver a intentar.";
            TxtOverlayRecoveryError.Visibility = Visibility.Visible;
            return;
        }

        _isVerifyingOverlayPin = true;
        try
        {
            if (!await PasswordManager.VerifyRecoveryAnswerOrCodeAsync(answer))
            {
                wait = PasswordManager.GetPinLockoutRemaining();
                TxtOverlayRecoveryError.Text = wait > TimeSpan.Zero
                    ? $"⏳ Demasiados intentos. Espera {FormatLockoutWait(wait)} para volver a intentar."
                    : "❌ La respuesta secreta o el código de recuperación no coinciden.";
                TxtOverlayRecoveryError.Visibility = Visibility.Visible;
                return;
            }

            if (string.IsNullOrWhiteSpace(newPin))
            {
                TxtOverlayRecoveryError.Text = "⚠️ Escribe tu nuevo PIN o contraseña para continuar.";
                TxtOverlayRecoveryError.Visibility = Visibility.Visible;
                return;
            }

            var s = SettingsManager.Current;
            PasswordManager.ConfigureMasterPassword(newPin, s.BrowserLockMode, s.SecurityQuestion, answer);
            _lastRejectedOverlayCandidate = null;
            await CompleteOverlayUnlockAsync();
            ShowToast("✅ ¡PIN actualizado con éxito! Todas tus contraseñas guardadas siguen intactas.");
        }
        finally
        {
            _isVerifyingOverlayPin = false;
        }
    }

    private async void BtnOverlayEmergencyReset_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "⚠️ CONFIRMACIÓN DE RESTABLECIMIENTO DE EMERGENCIA\n\n" +
            "¿Qué ocurrirá ahora mismo?\n" +
            "• SE ELIMINÁN: Únicamente las contraseñas guardadas en la bóveda (por seguridad para que nadie ajeno vea tus cuentas).\n" +
            "• SE CONSERVARÁN INTACTOS: Tus marcadores, páginas ancladas, historial, descargas y configuración del navegador.\n" +
            "• El bloqueo por PIN se desactivará para que puedas usar tu navegador.\n\n" +
            "¿Deseas continuar?",
            "Restablecer acceso — Lex Junior",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm == MessageBoxResult.Yes)
        {
            PasswordManager.EmergencyResetLockAndClearVault();
            await CompleteOverlayUnlockAsync();
            ShowToast("🔓 Acceso restablecido (se vació solo la bóveda de contraseñas; tus marcadores y ajustes siguen intactos).");
        }
    }
}
