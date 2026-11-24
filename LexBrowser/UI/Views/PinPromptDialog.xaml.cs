using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using LexBrowser.Core;

namespace LexBrowser.UI.Views;

public partial class PinPromptDialog : Window
{
    private string? _lastRejectedCandidate;
    private bool _isVerifying;

    public PinPromptDialog(string? actionDescription = null)
    {
        InitializeComponent();

        MaxHeight = SystemParameters.WorkArea.Height * 0.85;
        MinHeight = 360;

        if (!string.IsNullOrWhiteSpace(actionDescription))
        {
            TxtPromptSubtitle.Text = actionDescription;
        }

        Loaded += (_, _) => PwdPinInput.Focus();
    }

    private void ShowPinFailure()
    {
        TimeSpan wait = PasswordManager.GetPinLockoutRemaining();
        TxtPinError.Text = wait > TimeSpan.Zero
            ? $"⏳ Demasiados intentos. Espera {PasswordManager.FormatLockoutWait(wait)} para volver a intentar."
            : "❌ Contraseña o PIN incorrecto. Inténtalo de nuevo.";
        TxtPinError.Visibility = Visibility.Visible;
        PwdPinInput.SelectAll();
        PwdPinInput.Focus();
    }

    private async void PwdPinInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        TxtPinError.Visibility = Visibility.Collapsed;
        string current = PwdPinInput.Password;
        int targetLen = SettingsManager.Current.MasterPasswordLength;

        if (targetLen > 0 && current.Length == targetLen)
        {
            await VerifyPinCandidateAsync(current, isAutoTrigger: true);
        }
    }

    private void PwdPinInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            BtnConfirmPin_Click(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            BtnCancel_Click(sender, e);
        }
    }

    private async void BtnConfirmPin_Click(object sender, RoutedEventArgs e)
    {
        await VerifyPinCandidateAsync(PwdPinInput.Password, isAutoTrigger: false);
    }

    private async Task VerifyPinCandidateAsync(string candidate, bool isAutoTrigger)
    {
        if (_isVerifying) return;

        if (PasswordManager.IsPinLockedOut)
        {
            ShowPinFailure();
            return;
        }

        if (candidate == _lastRejectedCandidate)
        {
            if (!isAutoTrigger) ShowPinFailure();
            return;
        }

        _isVerifying = true;
        try
        {
            bool ok = await PasswordManager.VerifyMasterPasswordAsync(candidate);
            if (!IsLoaded) return;

            if (ok)
            {
                PasswordManager.MarkSessionUnlocked();
                DialogResult = true;
                Close();
            }
            else
            {
                _lastRejectedCandidate = candidate;
                ShowPinFailure();
            }
        }
        finally
        {
            _isVerifying = false;
        }
    }

    private void BtnShowRecovery_Click(object sender, RoutedEventArgs e)
    {
        var s = SettingsManager.Current;
        string q = string.IsNullOrWhiteSpace(s.SecurityQuestion)
            ? "Escribe tu Código de Recuperación (ej. LEX-XXXX-XXXX):"
            : $"Pregunta secreta: \"{s.SecurityQuestion}\" (o escribe tu Código de Recuperación {s.RecoveryCodeHint}):";

        TxtRecoveryQuestionLabel.Text = q;
        TxtRecoveryError.Visibility = Visibility.Collapsed;
        PanelPinEntry.Visibility = Visibility.Collapsed;
        PanelRecovery.Visibility = Visibility.Visible;
        TxtRecoveryAnswer.Focus();
    }

    private void BtnBackToPin_Click(object sender, RoutedEventArgs e)
    {
        PanelRecovery.Visibility = Visibility.Collapsed;
        PanelPinEntry.Visibility = Visibility.Visible;
        PwdPinInput.Focus();
    }

    private async void BtnApplyRecovery_Click(object sender, RoutedEventArgs e)
    {
        if (_isVerifying) return;

        string answer = TxtRecoveryAnswer.Text;
        string newPin = PwdNewPinAfterRecovery.Password;

        _isVerifying = true;
        try
        {
            var result = await PasswordManager.TryRecoverAndResetPinAsync(answer, newPin);
            if (!IsLoaded) return;

            if (!result.Success || string.IsNullOrEmpty(result.NewRecoveryCode))
            {
                TxtRecoveryError.Text = result.ErrorMessage;
                TxtRecoveryError.Visibility = Visibility.Visible;
                return;
            }

            MessageBox.Show(
                this,
                PasswordManager.BuildPinRecoveredMessage(result.NewRecoveryCode),
                "Acceso Recuperado — Lex Junior",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            CrashLogger.Log("PinPromptDialog.BtnApplyRecovery_Click", ex);
        }
        finally
        {
            _isVerifying = false;
        }
    }

    private void BtnEmergencyReset_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "⚠️ CONFIRMACIÓN DE RESTABLECIMIENTO DE EMERGENCIA\n\n" +
            "¿Qué ocurrirá ahora mismo?\n" +
            "• SE ELIMINARÁN: Únicamente las contraseñas guardadas en la bóveda (por seguridad para que nadie ajeno acceda a tus cuentas).\n" +
            "• SE CONSERVARÁN INTACTOS: Tus marcadores, páginas ancladas, historial, descargas y configuración.\n" +
            "• El bloqueo por contraseña quedará desactivado.\n\n" +
            "¿Deseas continuar?",
            "Restablecer acceso — Lex Junior",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm == MessageBoxResult.Yes)
        {
            PasswordManager.EmergencyResetLockAndClearVault();
            DialogResult = true;
            Close();
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
