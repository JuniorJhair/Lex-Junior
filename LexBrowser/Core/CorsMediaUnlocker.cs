using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace LexBrowser.Core;

/// <summary>
/// Inyecta Access-Control-Allow-Origin en respuestas de MEDIA (solo cabeceras; el cuerpo nunca pasa por C#).
/// Solo existe mientras el usuario amplifica > 100% en una fuente cross-origin. Se destruye al navegar o cerrar la pestaña.
///
/// Por qué cubre "*" y no solo el origen del src:
///   archive.org/download/x.mp4  --302-->  ia800102.us.archive.org/.../x.mp4
/// En una carga CORS, CADA salto (la redirección y la respuesta final) necesita ACAO. El patrón por origen
/// solo cubría el primer salto, y el segundo host hacía fallar la carga ("No se pudo cargar esta fuente").
/// Se filtra únicamente por tipo de recurso Media, así que el resto del tráfico de la pestaña no se pausa.
/// </summary>
public sealed class CorsMediaUnlocker : IDisposable
{
    private const string FetchPatterns =
        "{\"patterns\":[{\"urlPattern\":\"*\",\"resourceType\":\"Media\",\"requestStage\":\"Response\"}]}";

    private readonly CoreWebView2 _wv;
    private readonly SemaphoreSlim _gate = new(1, 1);                       // evita tormentas de Fetch.enable concurrentes
    private readonly Dictionary<string, string> _sessionByTarget = new();   // targetId -> sessionId (iframes OOPIF)
    private CoreWebView2DevToolsProtocolEventReceiver? _rx;
    private bool _mainEnabled;
    private volatile bool _disposed;

    public CorsMediaUnlocker(CoreWebView2 wv) => _wv = wv;

    /// <summary>Idempotente. Devuelve true solo cuando la intercepción ya está realmente activa. (mediaOrigin se conserva por compatibilidad.)</summary>
    public async Task<bool> AllowAsync(string mediaOrigin)
    {
        if (_disposed) return false;
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return false;

            if (_rx == null)
            {
                _rx = _wv.GetDevToolsProtocolEventReceiver("Fetch.requestPaused");
                _rx.DevToolsProtocolEventReceived += OnPaused;
            }

            if (!_mainEnabled)
            {
                await _wv.CallDevToolsProtocolMethodAsync("Fetch.enable", FetchPatterns);
                _mainEnabled = true;
            }

            await AttachIframesAsync();   // se repite en cada llamada: cubre iframes creados después
            return !_disposed;
        }
        catch (Exception ex)
        {
            MediaExtractorService.LogMediaDiagnostic($"[CorsUnlock] {ex.Message}");
            return false;
        }
        finally { _gate.Release(); }
    }

    private async Task AttachIframesAsync()
    {
        string targetsJson;
        try { targetsJson = await _wv.CallDevToolsProtocolMethodAsync("Target.getTargets", "{}"); }
        catch (Exception ex) { MediaExtractorService.LogMediaDiagnostic($"[CorsUnlock] getTargets: {ex.Message}"); return; }

        using var doc = JsonDocument.Parse(targetsJson);
        foreach (var t in doc.RootElement.GetProperty("targetInfos").EnumerateArray())
        {
            if (_disposed) return;
            if (t.GetProperty("type").GetString() != "iframe") continue;
            string tid = t.GetProperty("targetId").GetString() ?? "";
            if (tid.Length == 0 || _sessionByTarget.ContainsKey(tid)) continue;

            try
            {
                using var att = JsonDocument.Parse(await _wv.CallDevToolsProtocolMethodAsync(
                    "Target.attachToTarget", JsonSerializer.Serialize(new { targetId = tid, flatten = true })));
                string sid = att.RootElement.GetProperty("sessionId").GetString() ?? "";
                if (sid.Length == 0) continue;

                _sessionByTarget[tid] = sid;
                await _wv.CallDevToolsProtocolMethodForSessionAsync(sid, "Fetch.enable", FetchPatterns);
            }
            catch (Exception ex)
            {
                MediaExtractorService.LogMediaDiagnostic($"[CorsUnlock] iframe {tid}: {ex.Message}");
            }
        }
    }

    private Task<string> Cdp(string? sid, string method, object p)
        => string.IsNullOrEmpty(sid)
            ? _wv.CallDevToolsProtocolMethodAsync(method, JsonSerializer.Serialize(p))
            : _wv.CallDevToolsProtocolMethodForSessionAsync(sid, method, JsonSerializer.Serialize(p));

    private void OnPaused(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        if (_disposed) return;
        string? sid = string.IsNullOrEmpty(e.SessionId) ? null : e.SessionId;
        _ = HandlePausedAsync(sid, e.ParameterObjectAsJson);
    }

    private async Task HandlePausedAsync(string? sid, string json)
    {
        string requestId = "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            requestId = root.GetProperty("requestId").GetString() ?? "";
            if (requestId.Length == 0) return;

            // Solo tocamos peticiones en modo CORS (las que llevan Origin). El resto continúa intacto.
            string? origin = null;
            if (root.TryGetProperty("request", out var req) && req.TryGetProperty("headers", out var reqHeaders))
            {
                foreach (var h in reqHeaders.EnumerateObject())
                {
                    if (h.Name.Equals("Origin", StringComparison.OrdinalIgnoreCase)) { origin = h.Value.GetString(); break; }
                }
            }

            if (string.IsNullOrEmpty(origin) ||
                !root.TryGetProperty("responseStatusCode", out var statusEl) ||
                !root.TryGetProperty("responseHeaders", out var respHeaders))
            {
                await Cdp(sid, "Fetch.continueResponse", new { requestId });
                return;
            }

            int status = statusEl.GetInt32();
            string phrase = root.TryGetProperty("responseStatusText", out var stEl) ? (stEl.GetString() ?? "") : "";
            if (phrase.Length == 0) phrase = DefaultPhrase(status);   // HTTP/2 no trae texto de estado

            var headers = new List<object>();
            foreach (var h in respHeaders.EnumerateArray())
            {
                string n = h.GetProperty("name").GetString() ?? "";
                string v = h.GetProperty("value").GetString() ?? "";
                // Quitamos ACAO/ACAC/etc. existentes: si el servidor manda otro origen, también fallaría.
                if (n.StartsWith("access-control-allow-", StringComparison.OrdinalIgnoreCase) ||
                    n.Equals("access-control-expose-headers", StringComparison.OrdinalIgnoreCase)) continue;
                headers.Add(new { name = n, value = v });
            }
            headers.Add(new { name = "Access-Control-Allow-Origin", value = origin });
            headers.Add(new { name = "Vary", value = "Origin" });
            headers.Add(new { name = "Access-Control-Expose-Headers", value = "Content-Length, Content-Range, Accept-Ranges" });

            // Al modificar cabeceras se envía también código y frase: así no depende de cómo Chromium trate los campos ausentes.
            await Cdp(sid, "Fetch.continueResponse", new
            {
                requestId,
                responseCode = status,
                responsePhrase = phrase,
                responseHeaders = headers
            });
        }
        catch
        {
            // Jamás dejar una petición pausada: el video se congelaría.
            if (requestId.Length > 0)
            {
                try { await Cdp(sid, "Fetch.continueResponse", new { requestId }); } catch { }
            }
        }
    }

    private static string DefaultPhrase(int code) => code switch
    {
        200 => "OK",
        206 => "Partial Content",
        301 => "Moved Permanently",
        302 => "Found",
        303 => "See Other",
        307 => "Temporary Redirect",
        308 => "Permanent Redirect",
        _ => "OK"
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { if (_rx != null) _rx.DevToolsProtocolEventReceived -= OnPaused; } catch { }
        try { _ = _wv.CallDevToolsProtocolMethodAsync("Fetch.disable", "{}"); } catch { }

        foreach (var sid in _sessionByTarget.Values.Where(s => s.Length > 0))
        {
            try { _ = _wv.CallDevToolsProtocolMethodForSessionAsync(sid, "Fetch.disable", "{}"); } catch { }
            try { _ = _wv.CallDevToolsProtocolMethodAsync("Target.detachFromTarget", JsonSerializer.Serialize(new { sessionId = sid })); } catch { }
        }
        _sessionByTarget.Clear();
        _rx = null;
    }
}
