using System;
using System.Windows;
using System.Windows.Input;
using LexBrowser.Core;

namespace LexBrowser.UI.Views;

public partial class PinPromptDialog : Window
{
    // Último PIN que ya fue rechazado (por el auto-desbloqueo o por Enter).
    // Evita que el mismo intento fallido cuente dos veces (auto-verificación + pulsar Enter).
    private string? _lastRejectedCandidate;

    public PinPromptDialog(string? actionDescription = null)
    {
        InitializeComponent();

        // Límite responsivo: máximo 85% del área de trabajo de la pantalla actual
        MaxHeight = SystemParameters.WorkArea.Height * 0.85;
        MinHeight = 360;

        if (!string.IsNullOrWhiteSpace(actionDescription))
        {
            TxtPromptSubtitle.Text = actionDescription;
        }

        Loaded += (_, _) => PwdPinInput.Focus();
    }

    private static string FormatWait(TimeSpan wait)
    {
        if (wait.TotalSeconds >= 60)
            return $"{(int)Math.Ceiling(wait.TotalMinutes)} min";
        return $"{(int)Math.Ceiling(wait.TotalSeconds)} s";
    }

    /// <summary>Muestra "incorrecto" o, si ya hay bloqueo por intentos, cuánto falta para reintentar.</summary>
    private void ShowPinFailure()
    {
        TimeSpan wait = PasswordManager.GetPinLockoutRemaining();
        TxtPinError.Text = wait > TimeSpan.Zero
            ? $"⏳ Demasiados intentos. Espera {FormatWait(wait)} para volver a intentar."
            : "❌ Contraseña o PIN incorrecto. Inténtalo de nuevo.";
        TxtPinError.Visibility = Visibility.Visible;
        PwdPinInput.SelectAll();
        PwdPinInput.Focus();
    }

    private void PwdPinInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        TxtPinError.Visibility = Visibility.Collapsed;
        string current = PwdPinInput.Password;
        int targetLen = SettingsManager.Current.MasterPasswordLength;

        // Auto-desbloqueo instantáneo al llegar al número de caracteres configurados (ej. 6 dígitos)
        if (targetLen > 0 && current.Length == targetLen)
        {
            // Durante un bloqueo no se verifica nada (ni se suma ningún intento)
            if (PasswordManager.IsPinLockedOut)
            {
                ShowPinFailure();
                return;
            }

            if (current == _lastRejectedCandidate)
                return;

            if (PasswordManager.VerifyMasterPassword(current))
            {
                PasswordManager.MarkSessionUnlocked();
                DialogResult = true;
                Close();
            }
            else
            {
                _lastRejectedCandidate = current;
                ShowPinFailure();
            }
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

    private void BtnConfirmPin_Click(object sender, RoutedEventArgs e)
    {
        string candidate = PwdPinInput.Password;

        if (PasswordManager.IsPinLockedOut)
        {
            ShowPinFailure();
            return;
        }

        // Ese mismo PIN ya se probó y falló (auto-desbloqueo): no volver a contarlo como otro intento.
        if (candidate == _lastRejectedCandidate)
        {
            ShowPinFailure();
            return;
        }

        if (PasswordManager.VerifyMasterPassword(candidate))
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

    private void BtnApplyRecovery_Click(object sender, RoutedEventArgs e)
    {
        string answer = TxtRecoveryAnswer.Text;
        string newPin = PwdNewPinAfterRecovery.Password;

        // La recuperación comparte el límite de intentos con el PIN
        TimeSpan wait = PasswordManager.GetPinLockoutRemaining();
        if (wait > TimeSpan.Zero)
        {
            TxtRecoveryError.Text = $"⏳ Demasiados intentos. Espera {FormatWait(wait)} para volver a intentar.";
            TxtRecoveryError.Visibility = Visibility.Visible;
            return;
        }

        if (!PasswordManager.VerifyRecoveryAnswerOrCode(answer))
        {
            wait = PasswordManager.GetPinLockoutRemaining();
            TxtRecoveryError.Text = wait > TimeSpan.Zero
                ? $"⏳ Demasiados intentos. Espera {FormatWait(wait)} para volver a intentar."
                : "❌ La respuesta secreta o el código de recuperación no coinciden.";
            TxtRecoveryError.Visibility = Visibility.Visible;
            return;
        }

        if (string.IsNullOrWhiteSpace(newPin))
        {
            TxtRecoveryError.Text = "⚠️ Escribe un nuevo PIN o contraseña para continuar.";
            TxtRecoveryError.Visibility = Visibility.Visible;
            return;
        }

        var s = SettingsManager.Current;
        PasswordManager.ConfigureMasterPassword(newPin, s.BrowserLockMode, s.SecurityQuestion, answer);
        MessageBox.Show(
            "¡Tu PIN/contraseña fue actualizado con éxito!\nTodas tus contraseñas guardadas se conservaron intactas.",
            "Acceso Recuperado — Lex Junior",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        DialogResult = true;
        Close();
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
