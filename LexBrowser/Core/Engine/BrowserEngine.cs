using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace LexBrowser.Core.Engine;

public static class BrowserEngine
{
    private static CoreWebView2Environment? _sharedEnvironment;

    // SemaphoreSlim permite await dentro de la sección crítica (lock no lo permite)
    private static readonly SemaphoreSlim _initLock = new(1, 1);

    /// <summary>
    /// Flags de Chromium diseñados para eliminar telemetría, desactivar procesos
    /// en segundo plano inútiles, reducir el consumo de RAM y optimizar video por hardware.
    /// </summary>
    private const string OptimizedEngineFlags =
        "--disable-background-networking " +
        "--disable-sync " +
        "--disable-component-update " +
        "--no-pings " +
        "--disable-breakpad " +
        "--disable-domain-reliability " +
        "--disable-client-side-phishing-detection " +
        "--disable-speech-api " +
        "--disable-default-apps " +
        "--disable-features=OptimizationHints,Translate,InterestCohort," +
            "CalculateNativeWinOcclusion,MediaEngagementBypassAutoplayPolicies " +
        "--enable-features=OverlayScrollbar,AutoPictureInPictureVideoHeuristics " +
        "--autoplay-policy=no-user-gesture-required " +
        "--disable-renderer-backgrounding " +
        "--disable-backgrounding-occluded-windows";

    /// <summary>
    /// Devuelve el entorno WebView2 compartido, creándolo una sola vez de forma
    /// thread-safe mediante un SemaphoreSlim (compatible con async/await).
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

            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LexBrowser",
                "UserData"
            );

            Directory.CreateDirectory(userDataFolder);

            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = OptimizedEngineFlags,
                AllowSingleSignOnUsingOSPrimaryAccount = false
            };

            _sharedEnvironment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder,
                options: options
            ).ConfigureAwait(false);

            return _sharedEnvironment;
        }
        finally
        {
            _initLock.Release();
        }
    }
}
