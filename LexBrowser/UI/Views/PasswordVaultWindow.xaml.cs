using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LexBrowser.Core;

namespace LexBrowser.UI.Views;

public sealed class CredentialViewModel : INotifyPropertyChanged
{
    public SavedCredential Model { get; }
    private bool _isRevealed;
    private int _secondsRemaining;

    public event PropertyChangedEventHandler? PropertyChanged;

    public CredentialViewModel(SavedCredential model)
    {
        Model = model;
    }

    public string Id => Model.Id;
    public string Domain => Model.Domain;
    public string DomainInitial => Model.DomainInitial;
    public string UpdatedDisplay => Model.UpdatedDisplay;
    public string UsernameDisplay => string.IsNullOrWhiteSpace(Model.Username) ? "(Sin usuario especificado)" : Model.Username;

    public bool IsRevealed
    {
        get => _isRevealed;
        set
        {
            _isRevealed = value;
            NotifyAll();
        }
    }

    public int SecondsRemaining
    {
        get => _secondsRemaining;
        set
        {
            _secondsRemaining = value;
            NotifyAll();
        }
    }

    public string PasswordDisplay => _isRevealed ? Model.Password : "••••••••••••";
    public string PeekButtonText => _isRevealed ? "🙈 Ocultar" : "👁️ Ver (5s)";
    public string PeekStatusBadge => _isRevealed ? $"👁️ Visible por {_secondsRemaining}s (se ocultará sola)" : string.Empty;
    public Brush PasswordBrush => _isRevealed
        ? new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99))
        : new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xAA));

    private void NotifyAll()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PasswordDisplay)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PeekButtonText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PeekStatusBadge)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PasswordBrush)));
    }
}

public partial class PasswordVaultWindow : Window
{
    public event Action<string>? NavigateRequested;
    public event Action<string>? ClipboardShieldActivated;

    private readonly List<CredentialViewModel> _currentItems = new();
    private readonly DispatcherTimer _peekCountdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _bannerTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private bool _isInitializing = true;
    private string _lastGeneratedRecoveryCode = string.Empty;

    public PasswordVaultWindow(string initialTab = "Vault")
    {
        InitializeComponent();

        _peekCountdownTimer.Tick += PeekCountdownTimer_Tick;
        _bannerTimer.Tick += (_, _) =>
        {
            _bannerTimer.Stop();
            VaultStatusBanner.Visibility = Visibility.Collapsed;
        };

        LoadStateIntoUI();
        _isInitializing = false;

        if (string.Equals(initialTab, "Lock", StringComparison.OrdinalIgnoreCase))
        {
            TabBrowserLock.IsChecked = true;
        }
    }

    private void LoadStateIntoUI()
    {
        var s = SettingsManager.Current;
        ChkEnablePasswordManager.IsChecked = s.EnablePasswordManager;

        if (!string.IsNullOrWhiteSpace(s.SecurityQuestion))
        {
            TxtSecurityQuestion.Text = s.SecurityQuestion;
        }

        switch (s.BrowserLockMode)
        {
            case "SmartSession":
                RbLockSmart.IsChecked = true;
                break;
            case "AlwaysOnStartup":
                RbLockAlways.IsChecked = true;
                break;
            case "VaultOnly":
                RbLockVaultOnly.IsChecked = true;
                break;
            default:
                RbLockNone.IsChecked = true;
                break;
        }

        UpdatePinConfigPanelVisibility();
        RefreshCredentialsList();
    }

    private void RefreshCredentialsList()
    {
        if (ItemsVaultCredentials == null) return;

        string filter = TxtSearchCredentials?.Text ?? string.Empty;
        if (TxtSearchPlaceholder != null)
        {
            TxtSearchPlaceholder.Visibility = string.IsNullOrEmpty(filter) ? Visibility.Visible : Visibility.Collapsed;
        }

        _currentItems.Clear();
        foreach (var cred in PasswordManager.Store.GetAllCredentials(filter))
        {
            _currentItems.Add(new CredentialViewModel(cred));
        }

        ItemsVaultCredentials.ItemsSource = null;
        ItemsVaultCredentials.ItemsSource = _currentItems;

        if (TxtEmptyVaultState != null)
        {
            TxtEmptyVaultState.Visibility = _currentItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        var excluded = PasswordManager.Store.GetExcludedDomains();
        if (BorderExcludedDomainsSection != null && ItemsExcludedDomains != null)
        {
            ItemsExcludedDomains.ItemsSource = excluded;
            BorderExcludedDomainsSection.Visibility = excluded.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ShowInternalBanner(string message)
    {
        TxtVaultStatusBanner.Text = message;
        VaultStatusBanner.Visibility = Visibility.Visible;
        _bannerTimer.Stop();
        _bannerTimer.Start();
    }

    private bool EnsureAuthorizedForSensitiveAction(string reason)
    {
        if (!PasswordManager.ShouldRequireUnlockForVaultAction())
            return true;

        var prompt = new PinPromptDialog(reason) { Owner = this };
        return prompt.ShowDialog() == true;
    }

    private void ChkEnablePasswordManager_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        bool wantsEnabled = ChkEnablePasswordManager.IsChecked == true;
        var s = SettingsManager.Current;

        // Si tiene PIN/contraseña configurado, pedir confirmación al activar o desactivar
        if (PasswordManager.HasMasterPasswordConfigured())
        {
            string actionDesc = wantsEnabled
                ? "Confirma tu PIN para activar el Guardador de Contraseñas"
                : "Confirma tu PIN para desactivar el Guardador de Contraseñas";

            var pinDialog = new PinPromptDialog(actionDesc) { Owner = this };
            if (pinDialog.ShowDialog() != true)
            {
                ChkEnablePasswordManager.IsChecked = s.EnablePasswordManager;
                return;
            }
        }

        if (!wantsEnabled && s.EnablePasswordManager)
        {
            int count = PasswordManager.Store.GetTotalCount();
            if (count > 0)
            {
                var disableDialog = new PasswordDisablePromptDialog { Owner = this };
                if (disableDialog.ShowDialog() == true)
                {
                    if (disableDialog.Result == PasswordDisableChoice.DeleteAndDisable)
                    {
                        PasswordManager.Store.ClearAllCredentials();
                        s.EnablePasswordManager = false;
                        ShowInternalBanner("🗑️ Guardador desactivado y todas las contraseñas guardadas fueron eliminadas.");
                    }
                    else if (disableDialog.Result == PasswordDisableChoice.KeepAndDisable)
                    {
                        s.EnablePasswordManager = false;
                        ShowInternalBanner("📦 Guardador en pausa: tus contraseñas siguen guardadas bajo llave por si vuelves a activarlo.");
                    }
                }
                else
                {
                    ChkEnablePasswordManager.IsChecked = true;
                    return;
                }
            }
            else
            {
                s.EnablePasswordManager = false;
                ShowInternalBanner("⏸️ Guardador de contraseñas desactivado.");
            }
        }
        else
        {
            s.EnablePasswordManager = true;
            ShowInternalBanner("✅ Guardador y autocompletado de contraseñas activado.");
        }

        SettingsManager.Save();
        RefreshCredentialsList();
    }

    private void TxtSearchCredentials_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;
        RefreshCredentialsList();
    }

    private void BtnToggleAddManual_Click(object sender, RoutedEventArgs e)
    {
        if (BorderAddManualForm.Visibility == Visibility.Visible)
        {
            BorderAddManualForm.Visibility = Visibility.Collapsed;
        }
        else
        {
            BorderAddManualForm.Visibility = Visibility.Visible;
            TxtManualDomain.Focus();
        }
    }

    private void BtnSaveManualCredential_Click(object sender, RoutedEventArgs e)
    {
        string domain = TxtManualDomain.Text.Trim();
        string user = TxtManualUsername.Text.Trim();
        string pwd = PwdManualPassword.Password;

        string normalizedDomain = PasswordVaultStore.ExtractBaseDomain(domain);
        if (string.IsNullOrEmpty(normalizedDomain))
        {
            ShowInternalBanner("⚠️ Escribe un dominio válido (ejemplo: google.com, netflix.com o viabcp.com.pe).");
            return;
        }

        if (string.IsNullOrEmpty(pwd))
        {
            ShowInternalBanner("⚠️ Escribe la contraseña que deseas guardar.");
            return;
        }

        PasswordManager.Store.SaveOrUpdateCredential(normalizedDomain, user, pwd);
        TxtManualDomain.Text = string.Empty;
        TxtManualUsername.Text = string.Empty;
        PwdManualPassword.Password = string.Empty;
        BorderAddManualForm.Visibility = Visibility.Collapsed;

        RefreshCredentialsList();
        ShowInternalBanner($"✅ Contraseña guardada para {normalizedDomain} (detectará cualquier ruta dentro de {normalizedDomain}).");
    }

    private void BtnOpenCredentialSite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is CredentialViewModel vm)
        {
            NavigateRequested?.Invoke($"https://{vm.Domain}");
            Close();
        }
    }

    private void BtnCopyUsername_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is CredentialViewModel vm)
        {
            if (string.IsNullOrWhiteSpace(vm.Model.Username))
            {
                ShowInternalBanner("ℹ️ Esta cuenta no tiene un nombre de usuario registrado.");
                return;
            }
            try
            {
                Clipboard.SetText(vm.Model.Username);
                ShowInternalBanner($"📋 Usuario '{vm.Model.Username}' copiado al portapapeles.");
            }
            catch { }
        }
    }

    private void BtnPeekPassword_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not CredentialViewModel vm) return;

        if (vm.IsRevealed)
        {
            vm.IsRevealed = false;
            return;
        }

        if (!EnsureAuthorizedForSensitiveAction($"Confirma tu PIN para ver la contraseña de {vm.Domain}"))
            return;

        foreach (var item in _currentItems)
        {
            item.IsRevealed = false;
        }

        vm.SecondsRemaining = 5;
        vm.IsRevealed = true;
        _peekCountdownTimer.Start();
        ShowInternalBanner($"👁️ Vistazo seguro activo para {vm.Domain}: la contraseña se ocultará automáticamente en 5 segundos.");
    }

    private void PeekCountdownTimer_Tick(object? sender, EventArgs e)
    {
        bool anyActive = false;
        foreach (var item in _currentItems)
        {
            if (item.IsRevealed)
            {
                item.SecondsRemaining--;
                if (item.SecondsRemaining <= 0)
                {
                    item.IsRevealed = false;
                }
                else
                {
                    anyActive = true;
                }
            }
        }

        if (!anyActive)
        {
            _peekCountdownTimer.Stop();
        }
    }

    private void BtnCopyPassword_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not CredentialViewModel vm) return;

        if (!EnsureAuthorizedForSensitiveAction($"Confirma tu PIN para copiar la contraseña de {vm.Domain}"))
            return;

        ClipboardShieldActivated?.Invoke(vm.Model.Password);
        ShowInternalBanner($"📋 Contraseña de {vm.Domain} copiada — 🛡️ Escudo de Portapapeles activo: se borrará sola del portapapeles en 20 segundos.");
    }

    private void BtnDeleteCredential_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not CredentialViewModel vm) return;

        if (!EnsureAuthorizedForSensitiveAction($"Confirma tu PIN para eliminar la contraseña de {vm.Domain}"))
            return;

        PasswordManager.Store.DeleteCredential(vm.Id);
        RefreshCredentialsList();
        ShowInternalBanner($"🗑️ Contraseña de {vm.Domain} eliminada.");
    }

    private void BtnRemoveExcludedDomain_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string domain)
        {
            PasswordManager.Store.RemoveExcludedDomain(domain);
            RefreshCredentialsList();
            ShowInternalBanner($"✅ {domain} podrá volver a guardar contraseñas cuando inicies sesión.");
        }
    }

    private void NavTab_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelVaultList == null || PanelBrowserLock == null) return;
        PanelVaultList.Visibility = TabVaultList.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelBrowserLock.Visibility = TabBrowserLock.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LockModeRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        // Determinar qué opción fue seleccionada
        string newMode = "SmartSession";
        if (RbLockAlways.IsChecked == true) newMode = "AlwaysOnStartup";
        else if (RbLockVaultOnly.IsChecked == true) newMode = "VaultOnly";
        else if (RbLockNone.IsChecked == true) newMode = "None";

        string currentMode = SettingsManager.Current.BrowserLockMode;

        // Si ya está en ese modo, solo actualizar la visibilidad y salir
        if (newMode == currentMode)
        {
            UpdatePinConfigPanelVisibility();
            return;
        }

        bool hasPin = PasswordManager.HasMasterPasswordConfigured();

        // CASO 1: El usuario ya tiene una contraseña configurada
        if (hasPin)
        {
            if (newMode == "None")
            {
                // Quiere desactivar la contraseña
                var pinDialog = new PinPromptDialog("Confirma tu PIN actual para desactivar el uso de contraseña en el navegador")
                {
                    Owner = this
                };
                if (pinDialog.ShowDialog() == true)
                {
                    PasswordManager.DisableMasterPassword();
                    BorderRecoveryCodeDisplay.Visibility = Visibility.Collapsed;
                    UpdatePinConfigPanelVisibility();
                    MessageBox.Show(
                        "Has elegido 'No me gustaría usar contraseña'.\n\n" +
                        "El navegador y la bóveda abrirán directamente sin pedir PIN.",
                        "Sin Contraseña Activo — Lex Junior",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    // Canceló o falló: restaurar selección previa
                    _isInitializing = true;
                    LoadStateIntoUI();
                    _isInitializing = false;
                }
            }
            else
            {
                // Cambiar entre opciones con PIN (1: Modo Inteligente, 2: Siempre, 3: Solo Bóveda)
                string modeTitle = newMode switch
                {
                    "SmartSession" => "Modo Inteligente",
                    "AlwaysOnStartup" => "Siempre que se abra el navegador",
                    "VaultOnly" => "Solo para ver contraseñas guardadas",
                    _ => "nuevo modo"
                };

                var pinDialog = new PinPromptDialog($"Confirma tu PIN actual para cambiar al modo: {modeTitle}")
                {
                    Owner = this
                };

                if (pinDialog.ShowDialog() == true)
                {
                    SettingsManager.Current.BrowserLockMode = newMode;
                    if (newMode == "SmartSession")
                    {
                        SettingsManager.Current.LastUnlockedBootId = PasswordManager.GetCurrentWindowsBootId();
                    }
                    SettingsManager.Save();
                    UpdatePinConfigPanelVisibility();
                    ShowInternalBanner($"✅ Modo de protección actualizado a: {modeTitle}.");
                }
                else
                {
                    // Canceló o falló: restaurar selección previa
                    _isInitializing = true;
                    LoadStateIntoUI();
                    _isInitializing = false;
                }
            }
        }
        else
        {
            // CASO 2: No tiene contraseña configurada todavía (estaba en None)
            // Al hacer clic en 1, 2 o 3, se muestra el formulario para configurar su nuevo PIN
            UpdatePinConfigPanelVisibility();
            if (newMode != "None" && BorderPinForm != null)
            {
                PwdMasterPin.Focus();
            }
        }
    }

    private void UpdatePinConfigPanelVisibility()
    {
        if (BorderPinConfiguration == null) return;
        bool isNone = RbLockNone.IsChecked == true;
        BorderPinConfiguration.Visibility = isNone ? Visibility.Collapsed : Visibility.Visible;

        bool hasPin = PasswordManager.HasMasterPasswordConfigured();
        if (BorderPinActiveStatus != null)
        {
            BorderPinActiveStatus.Visibility = (hasPin && !isNone) ? Visibility.Visible : Visibility.Collapsed;
        }
        if (TxtPinLengthBadge != null && hasPin)
        {
            int len = SettingsManager.Current.MasterPasswordLength;
            TxtPinLengthBadge.Text = len > 0 ? $"{len} dígitos" : "Activo";
        }
        if (BorderPinForm != null)
        {
            BorderPinForm.Visibility = (hasPin && !isNone) ? Visibility.Collapsed : Visibility.Visible;
            if (TxtPinFormTitle != null)
            {
                TxtPinFormTitle.Text = hasPin
                    ? "2. Modificar PIN o Contraseña y Preguntas"
                    : "2. Configura tu PIN o Contraseña y tu Método de Recuperación";
            }
        }
    }

    private void BtnChangePin_Click(object sender, RoutedEventArgs e)
    {
        var pinDialog = new PinPromptDialog("Ingresa tu PIN actual para modificar tu contraseña o preguntas") { Owner = this };
        if (pinDialog.ShowDialog() == true)
        {
            if (BorderPinForm != null)
            {
                BorderPinForm.Visibility = Visibility.Visible;
                PwdMasterPin.Focus();
            }
        }
    }

    private void BtnDisablePinDirect_Click(object sender, RoutedEventArgs e)
    {
        var pinDialog = new PinPromptDialog("Ingresa tu PIN actual para desactivar la protección por contraseña") { Owner = this };
        if (pinDialog.ShowDialog() == true)
        {
            PasswordManager.DisableMasterPassword();
            RbLockNone.IsChecked = true;
            UpdatePinConfigPanelVisibility();
            ShowInternalBanner("🔓 Contraseña del navegador desactivada.");
        }
    }

    private void BtnSaveLockSettings_Click(object sender, RoutedEventArgs e)
    {
        string selectedMode = "SmartSession";
        if (RbLockAlways.IsChecked == true) selectedMode = "AlwaysOnStartup";
        else if (RbLockVaultOnly.IsChecked == true) selectedMode = "VaultOnly";
        else if (RbLockNone.IsChecked == true) selectedMode = "None";

        if (selectedMode == "None")
        {
            if (PasswordManager.HasMasterPasswordConfigured())
            {
                if (!EnsureAuthorizedForSensitiveAction("Confirma tu PIN actual para desactivar la protección"))
                {
                    return;
                }
            }
            PasswordManager.DisableMasterPassword();
            UpdatePinConfigPanelVisibility();
            ShowInternalBanner("🔓 Configuración guardada sin contraseña.");
            return;
        }

        // Si ya tenía una contraseña configurada, verificarla antes de cambiarla
        if (PasswordManager.HasMasterPasswordConfigured() && !EnsureAuthorizedForSensitiveAction("Confirma tu PIN actual para modificar la configuración de bloqueo"))
        {
            return;
        }

        string pin1 = PwdMasterPin.Password;
        string pin2 = PwdMasterPinConfirm.Password;

        // Si dejó los campos en blanco pero ya tenía contraseña configurada, solo actualizar el modo de bloqueo
        if (string.IsNullOrEmpty(pin1) && string.IsNullOrEmpty(pin2) && PasswordManager.HasMasterPasswordConfigured())
        {
            SettingsManager.Current.BrowserLockMode = selectedMode;
            if (selectedMode == "SmartSession")
            {
                SettingsManager.Current.LastUnlockedBootId = PasswordManager.GetCurrentWindowsBootId();
            }
            SettingsManager.Save();
            MessageBox.Show(
                "El modo de bloqueo del navegador se actualizó correctamente conservando tu PIN actual.",
                "Modo Actualizado — Lex Junior",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrEmpty(pin1))
        {
            MessageBox.Show(
                "Escribe un PIN (por ejemplo de 6 dígitos) o una contraseña de letras/números.",
                "PIN Requerido — Lex Junior",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            PwdMasterPin.Focus();
            return;
        }

        if (pin1 != pin2)
        {
            MessageBox.Show(
                "El PIN y su confirmación no coinciden. Verifica que ambos sean iguales.",
                "Verificación — Lex Junior",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            PwdMasterPinConfirm.Focus();
            return;
        }

        string recoveryCode = PasswordManager.ConfigureMasterPassword(
            pin1,
            selectedMode,
            TxtSecurityQuestion.Text,
            TxtSecurityAnswer.Text);

        _lastGeneratedRecoveryCode = recoveryCode;
        TxtGeneratedRecoveryCode.Text = recoveryCode;
        BorderRecoveryCodeDisplay.Visibility = Visibility.Visible;

        PwdMasterPin.Password = string.Empty;
        PwdMasterPinConfirm.Password = string.Empty;
    }

    private void BtnCopyRecoveryCode_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastGeneratedRecoveryCode))
        {
            try
            {
                Clipboard.SetText(_lastGeneratedRecoveryCode);
                MessageBox.Show(
                    $"Código de recuperación {_lastGeneratedRecoveryCode} copiado al portapapeles.\nGuárdalo en un lugar seguro.",
                    "Código Copiado — Lex Junior",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch { }
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        _peekCountdownTimer.Stop();
        _bannerTimer.Stop();
        Close();
    }

    private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 1. Si el evento proviene de un ComboBox abierto con scroll interno, desplazar el combo suavemente a 28 px
        if (GlobalScrollBehavior.TryScrollOpenComboBox(e.OriginalSource as DependencyObject, e.Delta))
        {
            e.Handled = true;
            return;
        }

        // 2. Si el puntero está fuera, o el combo no tiene scroll interno: desplazar la ventana principal suavemente a 28 px
        e.Handled = true;
        if (sender is ScrollViewer scrollViewer)
        {
            double deltaUnits = e.Delta / 120.0;
            double scrollPixels = deltaUnits * 28.0;
            double newOffset = Math.Clamp(scrollViewer.VerticalOffset - scrollPixels, 0, scrollViewer.ScrollableHeight);
            scrollViewer.ScrollToVerticalOffset(newOffset);
        }
    }
}

