using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using LexBrowser.Core;
using LexBrowser.Core.AdBlock;
using LexBrowser.UI.Models;

namespace LexBrowser;

public partial class MainWindow
{
    private bool _isUpdatingVolumeSlider = false;
    private long _lastVolumeBoostInjectTick = 0;
    private bool _volumeBoostPendingFlush = false;

    private string _currentMediaFilter = "Todos";
    private bool _isUpdatingSnifferCheck = false;
    private bool _isExtractingYouTube = false;

    private static readonly SolidColorBrush BrushVolBoosted = CreateFrozenBrush(0x31, 0x2E, 0x81);
    private static readonly SolidColorBrush BrushVolNormal = CreateFrozenBrush(0x25, 0x25, 0x36);
    private static readonly SolidColorBrush BrushSnifferDotOn = CreateFrozenBrush(0x10, 0xB9, 0x81);
    private static readonly SolidColorBrush BrushSnifferDotOff = CreateFrozenBrush(0xA1, 0xA1, 0xAA);
    private static readonly SolidColorBrush BrushSnifferBgOn = CreateFrozenBrush(0x1E, 0x3A, 0x3A);
    private static readonly SolidColorBrush BrushSnifferBtnStop = CreateFrozenBrush(0xDC, 0x26, 0x26);
    private static readonly SolidColorBrush BrushSnifferBtnStart = CreateFrozenBrush(0x4F, 0x46, 0xE5);
    private static readonly SolidColorBrush BrushSnifferSubActive = CreateFrozenBrush(0x34, 0xD3, 0x99);
    private static readonly SolidColorBrush BrushSnifferSubIdle = CreateFrozenBrush(0xA1, 0xA1, 0xAA);
    private static readonly SolidColorBrush BrushMediaFilterActive = CreateFrozenBrush(0x4F, 0x46, 0xE5);
    private static readonly SolidColorBrush BrushMediaFilterInactive = CreateFrozenBrush(0x25, 0x25, 0x34);

    // ─────────────────────────────────────────────────────────────────────
    // AMPLIFICADOR DE VOLUMEN HASTA 300% (VOLUME MASTER INTEGRADO)
    // ─────────────────────────────────────────────────────────────────────
    private void BtnVolumeBooster_Click(object sender, RoutedEventArgs e)
    {
        if (IsLocked) return;
        UpdateVolumeBoosterUI();
        PopupVolumeBooster.IsOpen = !PopupVolumeBooster.IsOpen;
    }

    private void SliderVolumeBoost_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingVolumeSlider || _activeTab == null) return;
        int pct = (int)Math.Round(e.NewValue);
        _activeTab.VolumeBoostPercent = pct;
        UpdateVolumeBoosterUI();
        SendVolumeBoostToTabThrottled(_activeTab);
    }

    private void SendVolumeBoostToTabThrottled(BrowserTab tab)
    {
        long now = Environment.TickCount64;
        if (now - _lastVolumeBoostInjectTick >= 60)
        {
            _lastVolumeBoostInjectTick = now;
            SendVolumeBoostToTab(tab);
            return;
        }

        if (_volumeBoostPendingFlush) return;
        _volumeBoostPendingFlush = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, async () =>
        {
            await Task.Delay(60);
            _volumeBoostPendingFlush = false;
            _lastVolumeBoostInjectTick = Environment.TickCount64;
            if (!tab.IsDisposed)
            {
                SendVolumeBoostToTab(tab);
            }
        });
    }

    private void BtnVolumeReset_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab == null) return;
        _activeTab.VolumeBoostPercent = 100;
        UpdateVolumeBoosterUI();
        SendVolumeBoostToTab(_activeTab);
    }

    private void BtnVolumePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab == null) return;
        if (sender is Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out int preset))
        {
            _activeTab.VolumeBoostPercent = Math.Clamp(preset, 0, 300);
            UpdateVolumeBoosterUI();
            SendVolumeBoostToTab(_activeTab);
        }
    }

    public void BroadcastToFrames(BrowserTab tab, string script)
    {
        try { _ = tab.WebView.CoreWebView2.ExecuteScriptAsync(script); } catch { }
        foreach (var f in tab.ActiveFrames.Keys.ToArray())
        {
            try { _ = f.ExecuteScriptAsync(script); } catch { }
        }
    }

    public void SetMediaProgressReporting(BrowserTab tab, bool enabled)
    {
        try
        {
            if (tab?.WebView?.CoreWebView2 == null || tab.IsDisposed) return;
            BroadcastToFrames(tab, $"window.__lexSetMediaProgress && window.__lexSetMediaProgress({(enabled ? "true" : "false")});");
        }
        catch { }
    }

    private void SendVolumeBoostToTab(BrowserTab tab)
    {
        try
        {
            if (tab?.WebView?.CoreWebView2 == null || tab.IsDisposed) return;
            int pct = tab.VolumeBoostPercent;
            string script = $"if (window.__lexSetVolumeBoostGlobal) window.__lexSetVolumeBoostGlobal({pct});";
            BroadcastToFrames(tab, script);
        }
        catch { }
    }

    private void UpdateVolumeBoosterUI()
    {
        if (TxtVolumeBoosterLabel == null) return;
        int pct = _activeTab?.VolumeBoostPercent ?? 100;

        _isUpdatingVolumeSlider = true;
        if (SliderVolumeBoost != null)
        {
            SliderVolumeBoost.Value = pct;
        }
        _isUpdatingVolumeSlider = false;

        TxtVolumeBoosterLabel.Text = $"{pct}%";
        if (TxtVolumePopupValue != null)
        {
            TxtVolumePopupValue.Text = $"{pct}%";
        }

        if (TxtVolumeBoosterIcon != null)
        {
            TxtVolumeBoosterIcon.Text = pct == 0 ? "🔇" : (pct > 100 ? "🚀" : "🔊");
        }

        if (BorderVolumeBooster != null)
        {
            BorderVolumeBooster.Background = pct != 100 ? BrushVolBoosted : BrushVolNormal;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // DETECTOR Y DESCARGADOR MULTIMEDIA BAJO DEMANDA (0 MB RAM EN REPOSO)
    // ─────────────────────────────────────────────────────────────────────
    private void BtnMediaSniffer_Click(object sender, RoutedEventArgs e)
    {
        if (IsLocked) return;
        UpdateMediaSnifferUI();
        PopupMediaSniffer.IsOpen = !PopupMediaSniffer.IsOpen;
    }

    private void BtnToggleSnifferActive_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab == null) return;
        _activeTab.IsMediaSnifferActive = !_activeTab.IsMediaSnifferActive;

        if (_activeTab.IsMediaSnifferActive)
        {
            StartSnifferSessionForTab(_activeTab);
            RequestDomMediaScan(_activeTab);
        }
        else
        {
            StopSnifferSessionForTab(_activeTab);
            _activeTab.ClearDetectedMedia();
        }

        UpdateMediaSnifferUI();
    }

    private void StartSnifferSessionForTab(BrowserTab tab)
    {
        try
        {
            StopSnifferSessionForTab(tab);
            if (tab.WebView?.CoreWebView2 == null) return;

            var session = new MediaSnifferSession(tab.WebView.CoreWebView2, () =>
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    if (tab.IsDisposed || !tab.IsMediaSnifferActive || tab.SnifferSession == null) return;
                    foreach (var item in tab.SnifferSession.Items)
                    {
                        string uniqueKey = $"{item.FormatBadge}|{item.Url}";
                        if (tab.DetectedMediaUrls.Add(uniqueKey))
                        {
                            InsertMediaItemSorted(tab, item);
                        }
                    }
                    if (_activeTab == tab)
                    {
                        UpdateMediaSnifferUI();
                    }
                });
            })
            {
                PageTitle = tab.Title
            };

            tab.SnifferSession = session;
            _ = session.StartAsync();
        }
        catch (Exception ex)
        {
            MediaExtractorService.LogMediaDiagnostic($"[Sniffer] Error al iniciar sesión CDP: {ex.Message}");
        }
    }

    private void StopSnifferSessionForTab(BrowserTab tab)
    {
        try
        {
            tab.SnifferSession?.Dispose();
            tab.SnifferSession = null;
        }
        catch { }
    }

    private void ChkKeepSnifferOnNavigate_Changed(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingSnifferCheck || _activeTab == null) return;
        _activeTab.KeepSnifferActiveOnNavigate = ChkKeepSnifferOnNavigate.IsChecked == true;
    }

    private void BtnRescanMedia_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab == null) return;
        _activeTab.IsMediaSnifferActive = true;
        _activeTab.ClearDetectedMedia();
        if (_activeTab.SnifferSession != null)
        {
            _ = _activeTab.SnifferSession.RescanAsync();
        }
        else
        {
            StartSnifferSessionForTab(_activeTab);
        }
        RequestDomMediaScan(_activeTab);
        UpdateMediaSnifferUI();
    }

    private void BtnClearMedia_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab == null) return;
        _activeTab.ClearDetectedMedia();
        if (_activeTab.SnifferSession != null)
        {
            _activeTab.SnifferSession.Items.Clear();
        }
        UpdateMediaSnifferUI();
    }

    private void BtnMediaFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string filter)
        {
            _currentMediaFilter = filter;
            if (BtnFilterAll != null) BtnFilterAll.Background = filter == "Todos" ? BrushMediaFilterActive : BrushMediaFilterInactive;
            if (BtnFilterVideo != null) BtnFilterVideo.Background = filter == "Video" ? BrushMediaFilterActive : BrushMediaFilterInactive;
            if (BtnFilterAudio != null) BtnFilterAudio.Background = filter == "Audio" ? BrushMediaFilterActive : BrushMediaFilterInactive;
            if (BtnFilterImage != null) BtnFilterImage.Background = filter == "Imagen" ? BrushMediaFilterActive : BrushMediaFilterInactive;
            if (BtnFilterDoc != null) BtnFilterDoc.Background = filter == "Documento" ? BrushMediaFilterActive : BrushMediaFilterInactive;

            RefreshFilteredMediaList();
        }
    }

    private void RequestDomMediaScan(BrowserTab tab)
    {
        try
        {
            if (tab == null || tab.IsDisposed) return;

            string? ytVideoId = MediaExtractorService.TryExtractYouTubeVideoId(tab.Url);
            if (!string.IsNullOrWhiteSpace(ytVideoId))
            {
                _ = ExtractAndRegisterYouTubeStreamsAsync(tab, tab.Url, tab.Title);
                return;
            }

            if (tab.WebView?.CoreWebView2 == null) return;
            string json = JsonSerializer.Serialize(new { action = "scan_page_media" });
            tab.WebView.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch { }
    }

    private async Task ExtractAndRegisterYouTubeStreamsAsync(BrowserTab tab, string pageUrl, string fallbackTitle)
    {
        try
        {
            _isExtractingYouTube = true;
            if (_activeTab == tab) RefreshFilteredMediaList();

            var items = await MediaExtractorService.ExtractYouTubeStreamsAsync(pageUrl, fallbackTitle);

            await Dispatcher.InvokeAsync(() =>
            {
                _isExtractingYouTube = false;
                if (tab.IsDisposed || !tab.IsMediaSnifferActive) return;

                foreach (var item in items)
                {
                    string uniqueKey = $"{item.FormatBadge}|{item.Url}";
                    if (tab.DetectedMediaUrls.Add(uniqueKey))
                    {
                        InsertMediaItemSorted(tab, item);
                    }
                }

                if (_activeTab == tab)
                {
                    UpdateMediaSnifferUI();
                }
            });
        }
        catch
        {
            _isExtractingYouTube = false;
            if (_activeTab == tab) Dispatcher.Invoke(UpdateMediaSnifferUI);
        }
    }

    private static void InsertMediaItemSorted(BrowserTab tab, DetectedMediaItem item)
    {
        int idx = 0;
        while (idx < tab.DetectedMedia.Count && tab.DetectedMedia[idx].PriorityScore >= item.PriorityScore)
        {
            idx++;
        }
        tab.DetectedMedia.Insert(idx, item);
    }

    private void InspectNetworkMediaResponse(BrowserTab tab, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(tab.Url) &&
                (tab.Url.Contains("youtube.com/", StringComparison.OrdinalIgnoreCase) ||
                 tab.Url.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            string url = e.Request.Uri;
            if (string.IsNullOrWhiteSpace(url)) return;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
            if (AdBlockFilter.ShouldBlock(uri)) return;

            string contentType = "";
            long contentLength = 0;
            string? reqReferer = null;
            string? reqOrigin = null;
            string? reqCookie = null;
            string? reqAuth = null;
            string? reqUserAgent = null;

            try
            {
                var headers = e.Response.Headers;
                if (headers.Contains("Content-Type"))
                    contentType = headers.GetHeader("Content-Type") ?? "";

                if (headers.Contains("Content-Range"))
                {
                    string cr = headers.GetHeader("Content-Range") ?? "";
                    int slashIdx = cr.LastIndexOf('/');
                    if (slashIdx >= 0 && slashIdx + 1 < cr.Length &&
                        long.TryParse(cr.AsSpan(slashIdx + 1).Trim(), out long totalFromRange) &&
                        totalFromRange > 0)
                    {
                        contentLength = totalFromRange;
                    }
                }

                if (contentLength <= 0 && headers.Contains("Content-Length") && long.TryParse(headers.GetHeader("Content-Length"), out long len))
                    contentLength = len;

                var reqHeaders = e.Request.Headers;
                if (reqHeaders.Contains("Referer")) reqReferer = reqHeaders.GetHeader("Referer");
                if (reqHeaders.Contains("Origin")) reqOrigin = reqHeaders.GetHeader("Origin");
                if (reqHeaders.Contains("Cookie")) reqCookie = reqHeaders.GetHeader("Cookie");
                if (reqHeaders.Contains("Authorization")) reqAuth = reqHeaders.GetHeader("Authorization");
                if (reqHeaders.Contains("User-Agent")) reqUserAgent = reqHeaders.GetHeader("User-Agent");
            }
            catch { }

            if (MediaExtractorService.IsGarbageMediaUrl(url, contentType, contentLength, categoryHint: null))
                return;

            RegisterDetectedMediaUrl(tab, url, categoryHint: null, contentType, contentLength, reqReferer, reqOrigin, reqCookie, reqAuth, reqUserAgent);
        }
        catch { }
    }

    private void RegisterDetectedMediaUrl(
        BrowserTab tab,
        string url,
        string? categoryHint,
        string? contentType,
        long contentLength,
        string? reqReferer = null,
        string? reqOrigin = null,
        string? reqCookie = null,
        string? reqAuth = null,
        string? reqUserAgent = null)
    {
        if (tab == null || tab.IsDisposed || !tab.IsMediaSnifferActive) return;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (AdBlockFilter.ShouldBlock(uri)) return;
        if (MediaExtractorService.IsGarbageMediaUrl(url, contentType, contentLength, categoryHint)) return;

        string pathLower = uri.AbsolutePath.ToLowerInvariant();
        string ctLower = (contentType ?? "").ToLowerInvariant();
        string effectiveReferer = !string.IsNullOrWhiteSpace(reqReferer) ? reqReferer : tab.Url;

        if (pathLower.EndsWith(".m3u8") || ctLower.Contains("mpegurl"))
        {
            if (tab.DetectedMediaUrls.Add(url))
            {
                _ = ExpandAndRegisterHlsAsync(tab, url, effectiveReferer, reqOrigin, reqCookie, reqUserAgent);
            }
            return;
        }

        string category = "";
        string formatBadge = "";
        int priority = 500;

        if (pathLower.EndsWith(".mp4") || ctLower.Contains("video/mp4")) { category = "Video"; formatBadge = "MP4"; priority = 700; }
        else if (pathLower.EndsWith(".webm") || ctLower.Contains("video/webm")) { category = "Video"; formatBadge = "WEBM"; priority = 690; }
        else if (pathLower.EndsWith(".mkv") || ctLower.Contains("video/x-matroska")) { category = "Video"; formatBadge = "MKV"; priority = 690; }
        else if (ctLower.StartsWith("video/") && contentLength >= 350_000) { category = "Video"; formatBadge = "VIDEO"; priority = 650; }
        else if (pathLower.EndsWith(".mp3") || ctLower.Contains("audio/mpeg")) { category = "Audio"; formatBadge = "MP3"; priority = 600; }
        else if (pathLower.EndsWith(".m4a") || pathLower.EndsWith(".aac") || ctLower.Contains("audio/mp4")) { category = "Audio"; formatBadge = "M4A"; priority = 600; }
        else if (pathLower.EndsWith(".wav") || ctLower.Contains("audio/wav")) { category = "Audio"; formatBadge = "WAV"; priority = 590; }
        else if (pathLower.EndsWith(".flac") || ctLower.Contains("audio/flac")) { category = "Audio"; formatBadge = "FLAC"; priority = 590; }
        else if (pathLower.EndsWith(".ogg") || ctLower.Contains("audio/ogg")) { category = "Audio"; formatBadge = "OGG"; priority = 580; }
        else if (pathLower.EndsWith(".pdf") || ctLower.Contains("application/pdf")) { category = "Documento"; formatBadge = "PDF"; priority = 550; }
        else if (pathLower.EndsWith(".zip") || ctLower.Contains("application/zip")) { category = "Documento"; formatBadge = "ZIP"; priority = 540; }
        else if (pathLower.EndsWith(".rar") || ctLower.Contains("rar")) { category = "Documento"; formatBadge = "RAR"; priority = 540; }
        else if (pathLower.EndsWith(".7z") || ctLower.Contains("7z")) { category = "Documento"; formatBadge = "7Z"; priority = 540; }
        else if (pathLower.EndsWith(".docx") || pathLower.EndsWith(".doc") || ctLower.Contains("word")) { category = "Documento"; formatBadge = "DOCX"; priority = 530; }
        else if (pathLower.EndsWith(".xlsx") || pathLower.EndsWith(".xls") || ctLower.Contains("spreadsheet") || ctLower.Contains("excel")) { category = "Documento"; formatBadge = "XLSX"; priority = 530; }
        else if (pathLower.EndsWith(".pptx") || pathLower.EndsWith(".ppt") || ctLower.Contains("presentation") || ctLower.Contains("powerpoint")) { category = "Documento"; formatBadge = "PPTX"; priority = 530; }
        else if (ctLower.StartsWith("image/") && contentLength >= 250_000)
        {
            category = "Imagen";
            priority = 300;
            if (pathLower.EndsWith(".png") || ctLower.Contains("png")) formatBadge = "PNG";
            else if (pathLower.EndsWith(".webp") || ctLower.Contains("webp")) formatBadge = "WEBP";
            else formatBadge = "JPG";
        }
        else if (categoryHint == "Video" && (pathLower.Contains(".mp4") || pathLower.Contains("video")))
        {
            category = "Video";
            formatBadge = "MP4";
            priority = 680;
        }

        if (string.IsNullOrEmpty(category)) return;
        if (!tab.DetectedMediaUrls.Add(url)) return;

        if (tab.DetectedMedia.Count >= 50)
        {
            var last = tab.DetectedMedia[^1];
            tab.DetectedMediaUrls.Remove(last.Url);
            tab.DetectedMedia.RemoveAt(tab.DetectedMedia.Count - 1);
        }

        string baseTitle = MediaExtractorService.SanitizeTitle(!string.IsNullOrWhiteSpace(tab.Title) ? tab.Title : uri.Host);
        if (string.IsNullOrWhiteSpace(baseTitle)) baseTitle = "Archivo";

        string rawFileName = Path.GetFileName(uri.LocalPath);
        string fileName;
        if (!string.IsNullOrWhiteSpace(rawFileName) && rawFileName.Length >= 4 && rawFileName.Length <= 64 &&
            !Regex.IsMatch(rawFileName, @"^[0-9a-fA-F_-]{16,}(\.[a-z0-9]+)?$"))
        {
            try { rawFileName = Uri.UnescapeDataString(rawFileName); } catch { }
            fileName = MediaExtractorService.SanitizeTitle(rawFileName);
            if (!Path.HasExtension(fileName))
            {
                fileName += $".{formatBadge.ToLowerInvariant()}";
            }
        }
        else
        {
            fileName = $"{baseTitle}.{formatBadge.ToLowerInvariant()}";
        }

        string icon = category switch
        {
            "Video" => "🎬",
            "Audio" => "🎵",
            "Imagen" => "🖼️",
            _ => "📄"
        };

        string sizeDisplay = contentLength > 0
            ? $"{FormatBytes(contentLength)} • Descarga directa"
            : "Descarga directa";

        var item = new DetectedMediaItem
        {
            Url = url,
            Referer = effectiveReferer,
            CustomOriginHeader = reqOrigin,
            CustomCookieHeader = reqCookie,
            CustomAuthorizationHeader = reqAuth,
            CustomUserAgent = reqUserAgent,
            FileName = fileName,
            Category = category,
            CategoryIcon = icon,
            FormatBadge = formatBadge,
            SizeBytes = contentLength,
            VideoSizeBytes = contentLength,
            SizeDisplay = sizeDisplay,
            PriorityScore = priority + (int)Math.Min(99, contentLength / (1024 * 1024))
        };

        InsertMediaItemSorted(tab, item);

        if (_activeTab == tab)
        {
            UpdateMediaSnifferUI();
        }
    }

    private async Task ExpandAndRegisterHlsAsync(
        BrowserTab tab,
        string m3u8Url,
        string? customReferer = null,
        string? customOrigin = null,
        string? customCookie = null,
        string? customUserAgent = null)
    {
        try
        {
            string pageUrl = !string.IsNullOrWhiteSpace(customReferer) ? customReferer : tab.Url;
            string pageTitle = tab.Title;
            var hlsItems = await MediaExtractorService.ExpandHlsPlaylistAsync(m3u8Url, pageUrl, pageTitle);
            if (hlsItems.Count == 0) return;

            await Dispatcher.InvokeAsync(() =>
            {
                if (tab.IsDisposed || !tab.IsMediaSnifferActive) return;
                foreach (var item in hlsItems)
                {
                    item.CustomOriginHeader = customOrigin;
                    item.CustomCookieHeader = customCookie;
                    item.CustomUserAgent = customUserAgent;
                    if (tab.DetectedMediaUrls.Add(item.Url))
                    {
                        InsertMediaItemSorted(tab, item);
                    }
                }
                if (_activeTab == tab)
                {
                    UpdateMediaSnifferUI();
                }
            });
        }
        catch { }
    }

    private static string FormatBytes(long bytes) => MediaExtractorService.FormatBytes(bytes);

    private void UpdateMediaSnifferUI()
    {
        if (TxtMediaSnifferLabel == null) return;

        bool active = _activeTab?.IsMediaSnifferActive ?? false;
        int count = _activeTab?.DetectedMedia.Count ?? 0;

        _isUpdatingSnifferCheck = true;
        if (ChkKeepSnifferOnNavigate != null)
        {
            ChkKeepSnifferOnNavigate.IsChecked = _activeTab?.KeepSnifferActiveOnNavigate ?? false;
        }
        _isUpdatingSnifferCheck = false;

        TxtMediaSnifferLabel.Text = active && count > 0 ? $"Multimedia ({count})" : "Multimedia";
        DotMediaSnifferStatus.Fill = active ? BrushSnifferDotOn : BrushSnifferDotOff;
        BorderMediaSniffer.Background = active ? BrushSnifferBgOn : BrushVolNormal;

        if (TxtToggleSnifferBtn != null && BtnToggleSnifferActive != null && TxtSnifferStatusSubtitle != null)
        {
            if (active)
            {
                TxtToggleSnifferBtn.Text = "⏹ Desactivar";
                BtnToggleSnifferActive.Background = BrushSnifferBtnStop; // #DC2626 (Contraste WCAG AA 4.8:1)
                TxtSnifferStatusSubtitle.Text = _isExtractingYouTube
                    ? "⏳ Analizando calidades HD del video..."
                    : $"🟢 Activo en esta pestaña ({count} listos para descargar)";
                TxtSnifferStatusSubtitle.Foreground = BrushSnifferSubActive;
            }
            else
            {
                TxtToggleSnifferBtn.Text = "⚡ Activar aquí";
                BtnToggleSnifferActive.Background = BrushSnifferBtnStart;
                TxtSnifferStatusSubtitle.Text = "Apagado (0 MB RAM en reposo)";
                TxtSnifferStatusSubtitle.Foreground = BrushSnifferSubIdle;
            }
        }

        RefreshFilteredMediaList();
    }

    private void RefreshFilteredMediaList()
    {
        if (ItemsDetectedMedia == null || TxtMediaEmptyState == null || ScrollDetectedMedia == null) return;

        if (_activeTab == null || !_activeTab.IsMediaSnifferActive)
        {
            ItemsDetectedMedia.ItemsSource = null;
            ScrollDetectedMedia.Visibility = Visibility.Collapsed;
            TxtMediaEmptyState.Text = "Presiona '⚡ Activar aquí' para extraer videos (YouTube 1080p/720p, Shorts, redes sociales, streams HLS), audios y documentos sin archivos basura.";
            TxtMediaEmptyState.Visibility = Visibility.Visible;
            return;
        }

        IEnumerable<DetectedMediaItem> query = _activeTab.DetectedMedia;
        if (_currentMediaFilter == "Video")
            query = query.Where(x => x.Category == "Video");
        else if (_currentMediaFilter == "Audio")
            query = query.Where(x => x.Category == "Audio");
        else if (_currentMediaFilter == "Imagen")
            query = query.Where(x => x.Category == "Imagen");
        else if (_currentMediaFilter == "Documento")
            query = query.Where(x => x.Category == "Documento");

        var list = query.ToList();
        ItemsDetectedMedia.ItemsSource = list;

        if (list.Count == 0)
        {
            ScrollDetectedMedia.Visibility = Visibility.Collapsed;
            TxtMediaEmptyState.Text = _isExtractingYouTube
                ? "⏳ Extrayendo calidades 1080p HD, 720p HD y Audio del video de YouTube..."
                : "Escuchando videos y audios reales en esta página... Dale Play al video para capturar el flujo limpio.";
            TxtMediaEmptyState.Visibility = Visibility.Visible;
        }
        else
        {
            TxtMediaEmptyState.Visibility = Visibility.Collapsed;
            ScrollDetectedMedia.Visibility = Visibility.Visible;
        }
    }

    private void BtnCopyMediaUrl_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is DetectedMediaItem item && !string.IsNullOrWhiteSpace(item.Url))
        {
            try
            {
                Clipboard.SetText(item.Url);
                PopupMediaSniffer.IsOpen = false;
                ShowToast($"📋 Enlace copiado: {item.FileName}");
            }
            catch { }
        }
    }

    private async void BtnDownloadDetectedMedia_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not DetectedMediaItem item || string.IsNullOrWhiteSpace(item.Url))
            return;

        PopupMediaSniffer.IsOpen = false;

        string safeName = item.FileName;
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            safeName = safeName.Replace(c, '_');
        }

        string downloadsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var sfd = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Guardar archivo detectado — Lex Junior",
            FileName = safeName,
            InitialDirectory = Directory.Exists(downloadsFolder) ? downloadsFolder : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Filter = "Todos los archivos (*.*)|*.*"
        };

        if (sfd.ShowDialog(this) != true) return;

        string targetPath = sfd.FileName;
        string displayName = Path.GetFileName(targetPath);
        bool isIncog = _activeTab?.IsIncognito == true;
        var dlEntry = DownloadManager.RegisterDownload(displayName, targetPath, item.Url, item.SizeBytes, "En progreso", isIncog);
        RefreshDownloadsPopupUI();
        PopupDownloads.IsOpen = true;
        ShowToast($"⬇️ Descargando: {displayName}", "Detalles ⬇", () =>
        {
            RefreshDownloadsPopupUI();
            PopupDownloads.IsOpen = true;
        });

        try
        {
            if (!item.IsYouTubeStream &&
                string.IsNullOrWhiteSpace(item.CustomCookieHeader) &&
                _activeTab?.WebView?.CoreWebView2?.CookieManager != null)
            {
                try
                {
                    var cookies = await _activeTab.WebView.CoreWebView2.CookieManager.GetCookiesAsync(item.Url);
                    if (cookies != null && cookies.Count > 0)
                    {
                        item.CustomCookieHeader = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
                    }
                }
                catch { }
            }

            long lastReportTick = 0;
            long finalSize = await MediaExtractorService.DownloadMediaItemAsync(
                item,
                targetPath,
                onProgress: (received, total) =>
                {
                    long now = Environment.TickCount64;
                    if (now - lastReportTick >= 200 || (total > 0 && received >= total))
                    {
                        lastReportTick = now;
                        DownloadManager.UpdateDownload(dlEntry.Id, received, total > 0 ? total : item.SizeBytes, "En progreso");
                    }
                });

            DownloadManager.UpdateDownload(dlEntry.Id, finalSize, finalSize, "Completado");
            ShowToast($"✅ Descarga completada: {displayName}", "Ver archivo 📂", () =>
            {
                RefreshDownloadsPopupUI();
                PopupDownloads.IsOpen = true;
            });
        }
        catch (Exception ex)
        {
            DownloadManager.UpdateDownload(dlEntry.Id, 0, item.SizeBytes, "Error");
            ShowToast($"⚠️ Error al descargar: {ex.Message}", "Detalles ⬇", () =>
            {
                RefreshDownloadsPopupUI();
                PopupDownloads.IsOpen = true;
            });
        }
    }
}

