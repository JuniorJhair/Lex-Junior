using System;
using Microsoft.Win32;

namespace LexBrowser.Core;

/// <summary>
/// Provee comprobaciones seguras sobre si la configuración de privacidad de Windows
/// bloquea el acceso al micrófono o a la cámara para aplicaciones de escritorio clásicas (Win32/NonPackaged).
/// </summary>
public static class WindowsPrivacy
{
    private const string BaseConsentKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    /// <summary>
    /// Comprueba si el micrófono está bloqueado a nivel de sistema o para aplicaciones de escritorio.
    /// </summary>
    public static bool IsMicrophoneBlocked() => IsCapabilityBlocked("microphone");

    /// <summary>
    /// Comprueba si la cámara/webcam está bloqueada a nivel de sistema o para aplicaciones de escritorio.
    /// </summary>
    public static bool IsCameraBlocked() => IsCapabilityBlocked("webcam");

    /// <summary>
    /// Lee HKCU y HKLM bajo ConsentStore\{capability} y ConsentStore\{capability}\NonPackaged.
    /// Si el valor "Value" es "Deny" en cualquiera de las claves, retorna true (bloqueado).
    /// Si está ausente o no es "Deny", no asume bloqueo y retorna false.
    /// </summary>
    public static bool IsCapabilityBlocked(string capability)
    {
        if (string.IsNullOrWhiteSpace(capability)) return false;

        try
        {
            // 1. Clave de usuario principal (consentimiento global del usuario)
            using (var userCapKey = Registry.CurrentUser.OpenSubKey($@"{BaseConsentKey}\{capability}"))
            {
                if (userCapKey != null)
                {
                    var val = userCapKey.GetValue("Value") as string;
                    if (string.Equals(val, "Deny", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            // 2. Subclave de usuario NonPackaged (consentimiento específico para apps de escritorio)
            using (var userNonPackagedKey = Registry.CurrentUser.OpenSubKey($@"{BaseConsentKey}\{capability}\NonPackaged"))
            {
                if (userNonPackagedKey != null)
                {
                    var val = userNonPackagedKey.GetValue("Value") as string;
                    if (string.Equals(val, "Deny", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            // 3. Clave de máquina (política de directiva de grupo / GPO local)
            using (var machineCapKey = Registry.LocalMachine.OpenSubKey($@"{BaseConsentKey}\{capability}"))
            {
                if (machineCapKey != null)
                {
                    var val = machineCapKey.GetValue("Value") as string;
                    if (string.Equals(val, "Deny", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            // 4. Subclave de máquina NonPackaged
            using (var machineNonPackagedKey = Registry.LocalMachine.OpenSubKey($@"{BaseConsentKey}\{capability}\NonPackaged"))
            {
                if (machineNonPackagedKey != null)
                {
                    var val = machineNonPackagedKey.GetValue("Value") as string;
                    if (string.Equals(val, "Deny", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }
        catch
        {
            // Ante cualquier error de permisos de lectura en el registro, no bloquear
            return false;
        }
    }
}
