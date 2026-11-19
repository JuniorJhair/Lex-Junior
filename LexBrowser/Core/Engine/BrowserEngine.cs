using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace LexBrowser.Core.Engine;

public static class BrowserEngine
{
    private static CoreWebView2Environment? _sharedEnvironment;

    // SemaphoreSlim permite await dentro de la sección crítica (lock no lo permite)
    private static readonly SemaphoreSlim _initLock = new(1, 1);

    private static bool _appliedSmartScreenAtCreation;
    private static bool _appliedClientSidePhishingAtCreation;

    public static bool IsEnvironmentCreated => Volatile.Read(ref _sharedEnvironment) != null;
    public static bool AppliedSmartScreenAtCreation => _appliedSmartScreenAtCreation;
    public static bool AppliedClientSidePhishingAtCreation => _appliedClientSidePhishingAtCreation;

    /// <summary>
    /// Indica si el entorno ya creado en este proceso usó flags de SmartScreen o detección de phishing
    /// distintos a los guardados actualmente en SettingsManager.Current.
    /// </summary>
    public static bool RequiresRestartForCurrentSettings()
    {
        if (!IsEnvironmentCreated) return false;
        var s = SettingsManager.Current;
        return _appliedSmartScreenAtCreation != s.EnableSmartScreen ||
               _appliedClientSidePhishingAtCreation != s.EnableClientSidePhishingDetection;
    }

    /// <summary>
    /// Construye dinámicamente los flags de Chromium según los ajustes del perfil.
    /// Conserva intactos los demás flags (incluidos HardwareMediaKeyHandling y --autoplay-policy).
    /// </summary>
    internal static string BuildEngineFlags(BrowserSettings? settings)
    {
        settings ??= new BrowserSettings();

        string disabledFeatures = settings.EnableSmartScreen
            ? "OptimizationHints,Translate,InterestCohort,MediaRouter,HardwareMediaKeyHandling"
            : "OptimizationHints,Translate,InterestCohort,msSmartScreenProtection,MediaRouter,HardwareMediaKeyHandling";

        string phishingFlag = settings.EnableClientSidePhishingDetection
            ? string.Empty
            : "--disable-client-side-phishing-detection ";

        return
            "--disable-background-networking " +
            "--disable-sync " +
            "--no-pings " +
            "--disable-breakpad " +
            "--disable-domain-reliability " +
            phishingFlag +
            "--disable-default-apps " +
            $"--disable-features={disabledFeatures} " +
            "--enable-features=OverlayScrollbar,WebRtcHideLocalIpsWithMdns " +
            "--autoplay-policy=no-user-gesture-required";
    }

    /// <summary>
    /// Flags de Chromium para compatibilidad con invocaciones diagnósticas: refleja los ajustes actuales.
    /// </summary>
    private static string BuildCurrentEngineFlags() => BuildEngineFlags(SettingsManager.Current);

    internal const int HResultInvalidState = unchecked((int)0x8007139F);
    private static readonly int[] DefaultRestartRetryDelaysMs = { 250, 500, 1000, 1500, 2000 };

    internal static async Task<T> CreateWithInvalidStateRetryAsync<T>(
        Func<CancellationToken, Task<T>> factory,
        int[]? retryDelaysMs = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        int[] delays = retryDelaysMs ?? DefaultRestartRetryDelaysMs;

        for (int attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await factory(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsInvalidStateError(ex) && attempt < delays.Length)
            {
                int delayMs = Math.Max(1, delays[attempt]);
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
        }
    }

    internal static bool IsInvalidStateError(Exception ex)
    {
        for (Exception? cur = ex; cur != null; cur = cur.InnerException)
        {
            if (cur.HResult == HResultInvalidState)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Devuelve el entorno WebView2 compartido, creándolo una sola vez de forma
    /// thread-safe mediante un SemaphoreSlim (compatible con async/await).
    /// Si otro proceso WebView2 anterior está cerrándose sobre UserData (0x8007139F), reintenta con espera creciente.
    /// </summary>
    public static async Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        // Fast path: ya inicializado
        if (_sharedEnvironment != null)
            return _sharedEnvironment;

        await _initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Double-check dentro del lock
            if (_sharedEnvironment != null)
                return _sharedEnvironment;

            var settingsSnapshot = SettingsManager.Current;
            bool smartScreen = settingsSnapshot.EnableSmartScreen;
            bool phishingDetection = settingsSnapshot.EnableClientSidePhishingDetection;
            string flags = BuildEngineFlags(settingsSnapshot);

            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LexBrowser",
                "UserData"
            );

            Directory.CreateDirectory(userDataFolder);

            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = flags,
                AllowSingleSignOnUsingOSPrimaryAccount = false
            };

            var created = await CreateWithInvalidStateRetryAsync(_ =>
                CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: userDataFolder,
                    options: options)).ConfigureAwait(false);

            _appliedSmartScreenAtCreation = smartScreen;
            _appliedClientSidePhishingAtCreation = phishingDetection;
            Volatile.Write(ref _sharedEnvironment, created);

            return created;
        }
        finally
        {
            _initLock.Release();
        }
    }
}
