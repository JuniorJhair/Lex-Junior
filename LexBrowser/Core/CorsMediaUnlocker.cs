using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace LexBrowser.Core;

/// <summary>
/// Inyecta Access-Control-Allow-Origin en respuestas de MEDIA de orígenes concretos (solo mientras el usuario
/// amplifica > 100%), sin tocar el cuerpo. Se destruye al navegar o cerrar la pestaña.
/// </summary>
public sealed class CorsMediaUnlocker : IDisposable
{
    private readonly CoreWebView2 _wv;
    private readonly HashSet<string> _origins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _sessionByTarget = new();
    private CoreWebView2DevToolsProtocolEventReceiver? _rx;
    private bool _disposed;

    public CorsMediaUnlocker(CoreWebView2 wv) => _wv = wv;

    public async Task<bool> AllowAsync(string mediaOrigin)
    {
        if (_disposed) return false;
        if (!Uri.TryCreate(mediaOrigin, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https")) return false;
        _origins.Add(u.GetLeftPart(UriPartial.Authority));

        if (_rx == null)
        {
            _rx = _wv.GetDevToolsProtocolEventReceiver("Fetch.requestPaused");
            _rx.DevToolsProtocolEventReceived += OnPaused;
        }

        try { await EnableAsync(); return true; }
        catch (Exception ex)
        {
            MediaExtractorService.LogMediaDiagnostic($"[CorsUnlock] {ex.Message}");
            return false;
        }
    }

    private async Task EnableAsync()
    {
        string json = JsonSerializer.Serialize(new
        {
            patterns = _origins.Select(o => new { urlPattern = o + "/*", resourceType = "Media", requestStage = "Response" }).ToArray()
        });
        await _wv.CallDevToolsProtocolMethodAsync("Fetch.enable", json);

        // iframes cross-site (OOPIF): sesión propia por target.
        try
        {
            using var doc = JsonDocument.Parse(await _wv.CallDevToolsProtocolMethodAsync("Target.getTargets", "{}"));
            foreach (var t in doc.RootElement.GetProperty("targetInfos").EnumerateArray())
            {
                if (t.GetProperty("type").GetString() != "iframe") continue;
                string tid = t.GetProperty("targetId").GetString() ?? "";
                if (tid.Length == 0 || _sessionByTarget.ContainsKey(tid)) continue;

                using var att = JsonDocument.Parse(await _wv.CallDevToolsProtocolMethodAsync(
                    "Target.attachToTarget", JsonSerializer.Serialize(new { targetId = tid, flatten = true })));
                _sessionByTarget[tid] = att.RootElement.GetProperty("sessionId").GetString() ?? "";
            }
        }
        catch (Exception ex) { MediaExtractorService.LogMediaDiagnostic($"[CorsUnlock] OOPIF: {ex.Message}"); }

        foreach (var sid in _sessionByTarget.Values.Where(s => s.Length > 0))
            await _wv.CallDevToolsProtocolMethodForSessionAsync(sid, "Fetch.enable", json);
    }

    private Task<string> Cdp(string? sid, string method, object p)
        => string.IsNullOrEmpty(sid)
            ? _wv.CallDevToolsProtocolMethodAsync(method, JsonSerializer.Serialize(p))
            : _wv.CallDevToolsProtocolMethodForSessionAsync(sid, method, JsonSerializer.Serialize(p));

    private async void OnPaused(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        string? sid = string.IsNullOrEmpty(e.SessionId) ? null : e.SessionId;
        string requestId = "";
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var root = doc.RootElement;
            requestId = root.GetProperty("requestId").GetString() ?? "";

            var headers = new List<object>();
            bool hasAcao = false;
            if (root.TryGetProperty("responseHeaders", out var rh))
            {
                foreach (var h in rh.EnumerateArray())
                {
                    string n = h.GetProperty("name").GetString() ?? "";
                    string v = h.GetProperty("value").GetString() ?? "";
                    if (n.Equals("access-control-allow-origin", StringComparison.OrdinalIgnoreCase)) hasAcao = true;
                    headers.Add(new { name = n, value = v });
                }
            }
            if (!hasAcao)
            {
                headers.Add(new { name = "Access-Control-Allow-Origin", value = "*" });   // la carga usa credentials omit
                headers.Add(new { name = "Access-Control-Expose-Headers", value = "Content-Length, Content-Range, Accept-Ranges" });
            }
            await Cdp(sid, "Fetch.continueResponse", new { requestId, responseHeaders = headers });
        }
        catch
        {
            // Jamás dejar una petición pausada: el video se congelaría.
            try { await Cdp(sid, "Fetch.continueResponse", new { requestId }); } catch { }
        }
    }

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
        _origins.Clear();
    }
}

