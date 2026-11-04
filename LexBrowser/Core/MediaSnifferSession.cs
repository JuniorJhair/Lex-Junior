using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LexBrowser.UI.Models;
using Microsoft.Web.WebView2.Core;

namespace LexBrowser.Core;

/// <summary>
/// Sesión de detección multimedia BAJO DEMANDA para UNA pestaña.
/// Capas: CDP Network (con auto-attach a iframes OOPIF) + escaneo retroactivo por frame +
/// resolución HTTP de embeds (Streamwish, Vidhide, Mp4upload, etc.) + una recarga única con captura activa si está vacío.
/// Garantiza 0% CPU y 0 MB RAM en reposo: al llamar a Dispose() apaga CDP y limpia colecciones.
/// </summary>
public sealed class MediaSnifferSession : IDisposable
{
    private const string AutoAttachOn = "{\"autoAttach\":true,\"waitForDebuggerOnStart\":false,\"flatten\":true}";
    private const string AutoAttachOff = "{\"autoAttach\":false,\"waitForDebuggerOnStart\":false,\"flatten\":true}";
    private const string NetEnable = "{\"maxTotalBufferSize\":1000000,\"maxResourceBufferSize\":100000}";

    private readonly CoreWebView2 _wv;
    private readonly Action _onChanged;
    private readonly Dictionary<string, string> _sessionUrls = new();
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _hlsChildren = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _embedsDone = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(CoreWebView2DevToolsProtocolEventReceiver Rx, EventHandler<CoreWebView2DevToolsProtocolEventReceivedEventArgs> H)> _subs = new();
    private CancellationTokenSource _cts = new();
    private bool _reloadedOnce;
    private volatile bool _expectReload;
    private int _embedBudget = 8;

    public List<DetectedMediaItem> Items { get; } = new();
    public bool IsDisposed { get; private set; }
    public string PageTitle { get; set; } = "";

    public MediaSnifferSession(CoreWebView2 wv, Action onChanged)
    {
        _wv = wv;
        _onChanged = onChanged;
    }

    // ───────────── Ciclo de vida ─────────────

    public async Task StartAsync()
    {
        Subscribe("Target.attachedToTarget", OnAttached);
        Subscribe("Network.responseReceived", OnResponse);

        await Cdp("Network.enable", NetEnable);
        await Cdp("Target.setAutoAttach", AutoAttachOn);

        string url = _wv.Source ?? "";
        if (MediaExtractorService.TryExtractYouTubeVideoId(url) != null)
        {
            _ = ExtractYouTubeAsync(url);
            return;
        }

        _ = ScanTopAsync();
        _ = AutoReloadIfEmptyAsync();
    }

    /// <summary>Se llama desde NavigationStarting: true si la navegación la causó la propia sesión (recarga única).</summary>
    public bool ConsumeExpectedReload()
    {
        if (!_expectReload) return false;
        _expectReload = false;
        return true;
    }

    /// <summary>Modo mantener activo al cambiar de página: limpia datos de la página anterior.</summary>
    public void ResetForNewPage()
    {
        Items.Clear();
        _seen.Clear();
        _hlsChildren.Clear();
        _embedsDone.Clear();
        _sessionUrls.Clear();
        _embedBudget = 8;
        _reloadedOnce = false;
        _onChanged();
    }

    public async Task RescanAsync()
    {
        if (IsDisposed) return;
        _embedsDone.Clear();
        _embedBudget = 8;
        try { await Cdp("Target.setAutoAttach", AutoAttachOn); } catch { }
        await ScanTopAsync();
    }

    /// <summary>Recarga una vez con CDP encendido para capturar los pedidos iniciales del player.</summary>
    public void ReloadAndCapture()
    {
        if (IsDisposed) return;
        _reloadedOnce = true;
        _expectReload = true;
        _wv.Reload();
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        try { _cts.Cancel(); _cts.Dispose(); } catch { }
        foreach (var (rx, h) in _subs)
        {
            try { rx.DevToolsProtocolEventReceived -= h; } catch { }
        }
        _subs.Clear();
        try
        {
            _ = _wv.CallDevToolsProtocolMethodAsync("Target.setAutoAttach", AutoAttachOff);
            _ = _wv.CallDevToolsProtocolMethodAsync("Network.disable", "{}");
        }
        catch { }
        Items.Clear();
        _seen.Clear();
        _hlsChildren.Clear();
        _embedsDone.Clear();
        _sessionUrls.Clear();
    }

    // ───────────── CDP ─────────────

    private Task<string> Cdp(string method, string json = "{}", string? sessionId = null)
        => string.IsNullOrEmpty(sessionId)
            ? _wv.CallDevToolsProtocolMethodAsync(method, json)
            : _wv.CallDevToolsProtocolMethodForSessionAsync(sessionId, method, json);

    private void Subscribe(string eventName, EventHandler<CoreWebView2DevToolsProtocolEventReceivedEventArgs> handler)
    {
        var rx = _wv.GetDevToolsProtocolEventReceiver(eventName);
        rx.DevToolsProtocolEventReceived += handler;
        _subs.Add((rx, handler));
    }

    private void OnAttached(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        if (IsDisposed) return;
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var root = doc.RootElement;
            string sid = root.GetProperty("sessionId").GetString() ?? "";
            var ti = root.GetProperty("targetInfo");
            string type = ti.GetProperty("type").GetString() ?? "";
            string url = ti.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(sid) || (type != "iframe" && type != "page")) return;

            _sessionUrls[sid] = url;
            _ = InitChildSessionAsync(sid, url);
        }
        catch { }
    }

    private async Task InitChildSessionAsync(string sid, string url)
    {
        try
        {
            await Cdp("Network.enable", NetEnable, sid);
            await Cdp("Target.setAutoAttach", AutoAttachOn, sid);

            await Task.Delay(800, _cts.Token);
            if (IsDisposed) return;

            string res = await Cdp("Runtime.evaluate",
                JsonSerializer.Serialize(new { expression = MediaExtractorService.ScanJs, returnByValue = true }), sid);
            using var doc = JsonDocument.Parse(res);
            string? val = doc.RootElement.GetProperty("result").TryGetProperty("value", out var v) ? v.GetString() : null;
            if (val != null) await ProcessScanAsync(val, url);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MediaExtractorService.LogMediaDiagnostic($"[Sniffer] Sesión iframe {url}: {ex.Message}");
        }
    }

    // ───────────── Captura de red ─────────────

    private void OnResponse(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        if (IsDisposed) return;
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var root = doc.RootElement;
            var resp = root.GetProperty("response");

            string url = resp.GetProperty("url").GetString() ?? "";
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;

            string type = root.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";
            if (type is "Script" or "Stylesheet" or "Font" or "Ping" or "Manifest" or "CSPViolationReport") return;

            int status = resp.TryGetProperty("status", out var st) ? st.GetInt32() : 200;
            if (status >= 400) return;

            string? mime = resp.TryGetProperty("mimeType", out var mm) ? mm.GetString() : null;
            if (MediaExtractorService.CategorizeResource(url, mime) == null) return;

            long len = 0;
            if (resp.TryGetProperty("headers", out var hs))
            {
                foreach (var h in hs.EnumerateObject())
                {
                    string v = h.Value.ToString();
                    if (h.Name.Equals("content-length", StringComparison.OrdinalIgnoreCase)) long.TryParse(v, out len);
                    else if (h.Name.Equals("content-range", StringComparison.OrdinalIgnoreCase))
                    {
                        int slash = v.LastIndexOf('/');
                        if (slash >= 0 && long.TryParse(v[(slash + 1)..], out long total)) len = total;
                    }
                }
            }

            string? referer = null, ua = null, auth = null;
            if (resp.TryGetProperty("requestHeaders", out var rh))
            {
                foreach (var h in rh.EnumerateObject())
                {
                    if (h.Name.Equals("referer", StringComparison.OrdinalIgnoreCase)) referer = h.Value.GetString();
                    else if (h.Name.Equals("user-agent", StringComparison.OrdinalIgnoreCase)) ua = h.Value.GetString();
                    else if (h.Name.Equals("authorization", StringComparison.OrdinalIgnoreCase)) auth = h.Value.GetString();
                }
            }

            string frameHint = !string.IsNullOrEmpty(e.SessionId) && _sessionUrls.TryGetValue(e.SessionId, out var fu) && !string.IsNullOrEmpty(fu)
                ? fu : (_wv.Source ?? "");
            _ = HandleCapturedAsync(url, mime, len, referer ?? frameHint, ua, auth);
        }
        catch { }
    }

    private async Task HandleCapturedAsync(string url, string? mime, long len, string? referer, string? ua, string? auth)
    {
        if (IsDisposed) return;
        try
        {
            string? cat = MediaExtractorService.CategorizeResource(url, mime);
            if (cat == null) return;
            if (MediaExtractorService.IsGarbageMediaUrl(url, mime, len, cat)) return;
            if (_hlsChildren.Contains(url)) return;
            if (!_seen.Add(MediaExtractorService.DedupeKey(url))) return;

            string origin = MediaExtractorService.OriginOf(!string.IsNullOrEmpty(referer) ? referer! : url);
            string effUa = !string.IsNullOrWhiteSpace(ua) ? ua! : _wv.Settings.UserAgent;
            string? cookies = await CookieHeaderAsync(url);

            List<DetectedMediaItem> items;
            if (MediaExtractorService.IsHlsUrl(url, mime))
            {
                items = await MediaExtractorService.ExpandHlsPlaylistWithHeadersAsync(
                    url, referer ?? "", PageTitle, origin, effUa, cookies, _cts.Token);
                foreach (var it in items) _hlsChildren.Add(it.Url);
            }
            else
            {
                items = new List<DetectedMediaItem> { MediaExtractorService.BuildDirectItem(url, mime, len, cat, referer, PageTitle) };
            }

            if (IsDisposed || items.Count == 0) return;
            foreach (var it in items)
            {
                it.Referer = referer ?? it.Referer;
                it.CustomUserAgent = effUa;
                it.CustomOriginHeader = origin;
                it.CustomCookieHeader = cookies;
                it.CustomAuthorizationHeader = auth;
                Items.Add(it);
            }
            _onChanged();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MediaExtractorService.LogMediaDiagnostic($"[Sniffer] HandleCaptured {url}: {ex.Message}");
        }
    }

    // ───────────── Escaneo retroactivo + embeds ─────────────

    private async Task ScanTopAsync()
    {
        try
        {
            string raw = await _wv.ExecuteScriptAsync(MediaExtractorService.ScanJs);
            string? json = JsonSerializer.Deserialize<string>(raw);
            if (!string.IsNullOrEmpty(json)) await ProcessScanAsync(json, _wv.Source ?? "");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MediaExtractorService.LogMediaDiagnostic($"[Sniffer] ScanTop: {ex.Message}");
        }
    }

    private async Task ProcessScanAsync(string json, string frameUrlHint)
    {
        if (IsDisposed) return;
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string page = r.TryGetProperty("page", out var p) ? p.GetString() ?? frameUrlHint : frameUrlHint;
        string? ua = r.TryGetProperty("ua", out var u) ? u.GetString() : null;
        if (string.IsNullOrWhiteSpace(PageTitle) && r.TryGetProperty("title", out var t)) PageTitle = t.GetString() ?? "";

        if (r.TryGetProperty("urls", out var urls))
        {
            foreach (var el in urls.EnumerateArray())
            {
                if (el.GetString() is { } mediaUrl)
                {
                    await HandleCapturedAsync(mediaUrl, null, 0, page, ua, null);
                }
            }
        }

        if (r.TryGetProperty("frames", out var frames))
        {
            foreach (var el in frames.EnumerateArray())
            {
                if (el.GetString() is { } frameUrl)
                {
                    _ = ResolveEmbedAsync(frameUrl, page);
                }
            }
        }
    }

    private async Task ResolveEmbedAsync(string embedUrl, string pageUrl)
    {
        if (IsDisposed || _embedBudget <= 0) return;
        if (!MediaExtractorService.LooksLikePlayerEmbed(embedUrl) || !_embedsDone.Add(embedUrl)) return;
        _embedBudget--;

        var found = await MediaExtractorService.ResolveEmbedPageAsync(embedUrl, pageUrl, _cts.Token);
        foreach (var mediaUrl in found)
        {
            await HandleCapturedAsync(mediaUrl, null, 0, embedUrl, null, null);
        }
    }

    private async Task AutoReloadIfEmptyAsync()
    {
        try { await Task.Delay(3500, _cts.Token); } catch { return; }
        if (IsDisposed || Items.Count > 0 || _reloadedOnce) return;
        ReloadAndCapture();
    }

    // ───────────── YouTube y cookies ─────────────

    private async Task ExtractYouTubeAsync(string pageUrl)
    {
        try
        {
            var items = await MediaExtractorService.ExtractYouTubeStreamsAsync(pageUrl, PageTitle, ct: _cts.Token);
            if (IsDisposed) return;
            Items.AddRange(items);
            _onChanged();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MediaExtractorService.LogMediaDiagnostic($"[Sniffer] YouTube: {ex.Message}");
        }
    }

    private async Task<string?> CookieHeaderAsync(string url)
    {
        try
        {
            var cookies = await _wv.CookieManager.GetCookiesAsync(url);
            return cookies == null || cookies.Count == 0 ? null : string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
        }
        catch { return null; }
    }
}
