using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace LexBrowser.Core.Import;

/// <summary>
/// Importa contraseñas desde 'Login Data' de Chromium (mejor esfuerzo v10/v11; reporta v20 App-Bound).
/// </summary>
public static class ChromiumPasswordImporter
{
    public static bool TryDecryptMasterKeyFromLocalStateJson(string localStateJson, out byte[] masterKey, out string? error)
    {
        masterKey = Array.Empty<byte>();
        error = null;

        if (string.IsNullOrWhiteSpace(localStateJson))
        {
            error = "El archivo Local State está vacío.";
            return false;
        }

        byte[]? decodedKey = null;
        try
        {
            using var doc = JsonDocument.Parse(localStateJson);
            if (!doc.RootElement.TryGetProperty("os_crypt", out var osCrypt) ||
                !osCrypt.TryGetProperty("encrypted_key", out var encProp) ||
                encProp.ValueKind != JsonValueKind.String)
            {
                error = "No se encontró 'os_crypt.encrypted_key' en Local State.";
                return false;
            }

            string? b64 = encProp.GetString();
            if (string.IsNullOrWhiteSpace(b64))
            {
                error = "La clave cifrada de Local State está vacía.";
                return false;
            }

            decodedKey = Convert.FromBase64String(b64);
            if (decodedKey.Length <= 5 ||
                decodedKey[0] != (byte)'D' ||
                decodedKey[1] != (byte)'P' ||
                decodedKey[2] != (byte)'A' ||
                decodedKey[3] != (byte)'P' ||
                decodedKey[4] != (byte)'I')
            {
                error = "Formato de clave DPAPI no reconocido en Local State.";
                return false;
            }

            byte[] dpapiPayload = new byte[decodedKey.Length - 5];
            try
            {
                Buffer.BlockCopy(decodedKey, 5, dpapiPayload, 0, dpapiPayload.Length);
                masterKey = ProtectedData.Unprotect(dpapiPayload, null, DataProtectionScope.CurrentUser);
                return masterKey.Length > 0;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dpapiPayload);
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException)
        {
            error = $"No se pudo descifrar la clave maestra de Chromium: {ex.GetType().Name}.";
            return false;
        }
        finally
        {
            if (decodedKey != null)
            {
                CryptographicOperations.ZeroMemory(decodedKey);
            }
        }
    }

    /// <summary>
    /// Descifra un blob de password_value Chromium (`v10`/`v11` AES-GCM). Marca `v20` como no descifrable externamente.
    /// </summary>
    public static bool TryDecryptPasswordBlob(
        ReadOnlySpan<byte> encryptedBlob,
        ReadOnlySpan<byte> masterKey,
        out string plaintext,
        out bool isAppBoundV20)
    {
        plaintext = string.Empty;
        isAppBoundV20 = false;

        if (encryptedBlob.IsEmpty) return false;

        if (encryptedBlob.Length >= 3 &&
            encryptedBlob[0] == (byte)'v' &&
            encryptedBlob[1] == (byte)'2' &&
            encryptedBlob[2] == (byte)'0')
        {
            isAppBoundV20 = true;
            return false;
        }

        bool isAesGcmVersion = encryptedBlob.Length >= 3 &&
            encryptedBlob[0] == (byte)'v' &&
            encryptedBlob[1] == (byte)'1' &&
            (encryptedBlob[2] == (byte)'0' || encryptedBlob[2] == (byte)'1');

        if (isAesGcmVersion)
        {
            // Formato Chromium: 3 bytes ('v10'/'v11') + nonce 12 bytes + ciphertext + tag 16 bytes
            const int headerLen = 3 + 12 + 16;
            if (encryptedBlob.Length < headerLen || masterKey.Length == 0)
            {
                return false;
            }

            ReadOnlySpan<byte> nonce = encryptedBlob.Slice(3, 12);
            int cipherLen = encryptedBlob.Length - headerLen;
            ReadOnlySpan<byte> ciphertext = encryptedBlob.Slice(15, cipherLen);
            ReadOnlySpan<byte> tag = encryptedBlob.Slice(15 + cipherLen, 16);

            byte[] plainBytes = new byte[cipherLen];
            try
            {
                using var aes = new AesGcm(masterKey, 16);
                aes.Decrypt(nonce, ciphertext, tag, plainBytes);
                plaintext = Encoding.UTF8.GetString(plainBytes);
                return !string.IsNullOrEmpty(plaintext);
            }
            catch (CryptographicException)
            {
                return false;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }

        // Compatibilidad DPAPI heredado sin prefijo v10/v11/v20
        try
        {
            byte[] cipherCopy = encryptedBlob.ToArray();
            byte[] plainBytes = ProtectedData.Unprotect(cipherCopy, null, DataProtectionScope.CurrentUser);
            try
            {
                plaintext = Encoding.UTF8.GetString(plainBytes);
                return !string.IsNullOrEmpty(plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public static ImportCategoryResult ImportFromProfile(
        BrowserProfileInfo profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        string loginDbPath = Path.Combine(profile.ProfileDirectory, "Login Data");
        string localStatePath = Path.Combine(profile.UserDataDirectory, "Local State");

        if (!File.Exists(loginDbPath))
        {
            return new ImportCategoryResult("Contraseñas", 0, 0, 0, Error: "No se encontró el archivo 'Login Data' en el perfil seleccionado.");
        }

        byte[] masterKey = Array.Empty<byte>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(localStatePath))
            {
                using var lsStream = new FileStream(localStatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var lsReader = new StreamReader(lsStream);
                string localStateJson = lsReader.ReadToEnd();
                if (!TryDecryptMasterKeyFromLocalStateJson(localStateJson, out masterKey, out string? keyError) &&
                    !string.IsNullOrEmpty(keyError))
                {
                    masterKey = Array.Empty<byte>();
                }
            }

            var credentials = new List<(string Url, string Username, string Password)>();
            int skipped = 0;
            int unsupportedV20 = 0;

            using (var db = SqliteTempReaderHelper.OpenReadOnlyCopy(loginDbPath, cancellationToken))
            using (var cmd = db.Connection.CreateCommand())
            {
                cmd.CommandText = "SELECT origin_url, username_value, password_value FROM logins";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string url = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    string username = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    if (reader.IsDBNull(2) || string.IsNullOrWhiteSpace(url))
                    {
                        skipped++;
                        continue;
                    }

                    long blobLen = reader.GetBytes(2, 0, null, 0, 0);
                    if (blobLen <= 0 || blobLen > 1024 * 64)
                    {
                        skipped++;
                        continue;
                    }

                    byte[] blob = new byte[blobLen];
                    reader.GetBytes(2, 0, blob, 0, (int)blobLen);

                    if (TryDecryptPasswordBlob(blob, masterKey, out string password, out bool isV20))
                    {
                        credentials.Add((url.Trim(), username, password));
                    }
                    else if (isV20)
                    {
                        unsupportedV20++;
                    }
                    else
                    {
                        skipped++;
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            int imported = 0, duplicates = 0, storeSkipped = 0;
            string? saveError = null;
            if (credentials.Count > 0)
            {
                (imported, duplicates, storeSkipped, saveError) = PasswordManager.ImportCredentials(credentials);
            }

            string? warning = unsupportedV20 > 0
                ? $"{unsupportedV20} contraseñas usan cifrado v20 ligado a la aplicación y no pueden descifrarse externamente. Expórtalas a CSV desde el otro navegador e usa «Importar desde archivo CSV…»."
                : null;

            return new ImportCategoryResult(
                Category: "Contraseñas",
                Imported: imported,
                Duplicates: duplicates,
                Skipped: skipped + storeSkipped,
                UnsupportedAppBoundCount: unsupportedV20,
                Error: saveError,
                Warning: warning);
        }
        catch (OperationCanceledException)
        {
            return new ImportCategoryResult("Contraseñas", 0, 0, 0, Warning: "Importación de contraseñas cancelada.");
        }
        catch (Exception ex)
        {
            return new ImportCategoryResult("Contraseñas", 0, 0, 0, Error: $"Error al leer Login Data: {ex.GetType().Name}.");
        }
        finally
        {
            if (masterKey.Length > 0)
            {
                CryptographicOperations.ZeroMemory(masterKey);
            }
        }
    }
}

