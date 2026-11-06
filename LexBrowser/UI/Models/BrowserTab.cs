using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LexBrowser.UI.Models;

public enum TabPinMode
{
    None,
    LogoOnly,
    LogoAndName
}

public class DetectedMediaItem
{
    public string Url { get; set; } = "";
    public string? AudioPairUrl { get; set; }
    public string? FallbackHlsUrl { get; set; }
    public string? CustomUserAgent { get; set; }
    public string? CustomCookieHeader { get; set; }
    public string? CustomOriginHeader { get; set; }
    public string? CustomAuthorizationHeader { get; set; }
    public bool IsHlsStream { get; set; } = false;
    public bool IsYouTubeStream { get; set; } = false;
    public int PriorityScore { get; set; } = 100;
    public string Referer { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Category { get; set; } = "Video"; // "Video", "Audio", "Imagen", "Documento"
    public string CategoryIcon { get; set; } = "🎬";
    public string FormatBadge { get; set; } = "MP4";
    public string SizeDisplay { get; set; } = "Directo";
    public long SizeBytes { get; set; } = 0;
    public long VideoSizeBytes { get; set; } = 0;
    public long AudioSizeBytes { get; set; } = 0;
}

public class BrowserTab : INotifyPropertyChanged
{
    private string _title = "Nueva pestaña";
    private string _url = "";
    private bool _isLoading = false;
    private bool _canGoBack = false;
    private bool _canGoForward = false;
    private int _blockedAdsCount = 0;
    private bool _hasActiveMedia = false;
    private string _mediaTitle = "";
    private double _mediaCurrentTime = 0;
    private double _mediaDuration = 0;
    private bool _mediaIsPlaying = false;
    private bool _isSelected = false;
    private TabPinMode _pinMode = TabPinMode.None;
    private bool _isMuted = false;
    private bool _isIncognito = false;
    private bool _isNewTab = true;
    private int _volumeBoostPercent = 100;
    private bool _isMediaSnifferActive = false;
    private bool _keepSnifferActiveOnNavigate = false;

    public Guid Id { get; } = Guid.NewGuid();
    public WebView2 WebView { get; }
    public MainWindow? OwnerWindow { get; set; }
    public bool IsDisposed { get; set; } = false;
    public bool IsLazyLoaded { get; set; } = false;

    /// <summary>Registro en memoria de subframes (iframes) activos para sincronización instantánea de volumen y multimedia.</summary>
    public ConcurrentDictionary<CoreWebView2Frame, byte> ActiveFrames { get; } = new();

    /// <summary>Nivel de amplificación de volumen de la pestaña (0% a 300%, por defecto 100%).</summary>
    public int VolumeBoostPercent
    {
        get => _volumeBoostPercent;
        set => SetField(ref _volumeBoostPercent, Math.Clamp(value, 0, 300));
    }

    /// <summary>Indica si el detector bajo demanda de archivos multimedia está activo en esta pestaña.</summary>
    public bool IsMediaSnifferActive
    {
        get => _isMediaSnifferActive;
        set => SetField(ref _isMediaSnifferActive, value);
    }

    /// <summary>Si es true, mantiene activo el detector multimedia al cambiar de página en esta pestaña.</summary>
    public bool KeepSnifferActiveOnNavigate
    {
        get => _keepSnifferActiveOnNavigate;
        set => SetField(ref _keepSnifferActiveOnNavigate, value);
    }

    /// <summary>Sesión CDP bajo demanda para captura profunda de streams e iframes de cualquier origen.</summary>
    public LexBrowser.Core.MediaSnifferSession? SnifferSession { get; set; }

    /// <summary>Desbloqueador dinámico CORS para amplificación de audio > 100% en orígenes cruzados.</summary>
    public LexBrowser.Core.CorsMediaUnlocker? CorsUnlocker { get; set; }

    /// <summary>URI actual de navegación de la pestaña, si está disponible.</summary>
    public Uri? CurrentUri
    {
        get
        {
            try
            {
                if (WebView?.Source != null) return WebView.Source;
                if (!string.IsNullOrEmpty(_url) && Uri.TryCreate(_url, UriKind.Absolute, out var u)) return u;
            }
            catch { }
            return null;
        }
    }

    /// <summary>Lista acotada de recursos multimedia/documentos detectados cuando el sniffer está activo.</summary>
    public ObservableCollection<DetectedMediaItem> DetectedMedia { get; } = new();

    /// <summary>Conjunto O(1) para evitar duplicados de URLs detectadas.</summary>
    public HashSet<string> DetectedMediaUrls { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void ClearDetectedMedia()
    {
        DetectedMedia.Clear();
        DetectedMediaUrls.Clear();
    }

    public string Title
    {
        get => _title;
        set
        {
            if (SetField(ref _title, value))
            {
                OnPropertyChanged(nameof(DisplayTitle));
            }
        }
    }

    public string Url
    {
        get => _url;
        set
        {
            if (SetField(ref _url, value))
            {
                OnPropertyChanged(nameof(FaviconIcon));
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetField(ref _isLoading, value);
    }

    public bool CanGoBack
    {
        get => _canGoBack;
        set => SetField(ref _canGoBack, value);
    }

    public bool CanGoForward
    {
        get => _canGoForward;
        set => SetField(ref _canGoForward, value);
    }

    public int BlockedAdsCount
    {
        get => _blockedAdsCount;
        set => SetField(ref _blockedAdsCount, value);
    }

    public bool HasActiveMedia
    {
        get => _hasActiveMedia;
        set
        {
            if (SetField(ref _hasActiveMedia, value))
            {
                OnPropertyChanged(nameof(AudioIndicatorText));
            }
        }
    }

    public string MediaTitle
    {
        get => _mediaTitle;
        set => SetField(ref _mediaTitle, value);
    }

    public double MediaCurrentTime
    {
        get => _mediaCurrentTime;
        set => SetField(ref _mediaCurrentTime, value);
    }

    public double MediaDuration
    {
        get => _mediaDuration;
        set => SetField(ref _mediaDuration, value);
    }

    public bool MediaIsPlaying
    {
        get => _mediaIsPlaying;
        set => SetField(ref _mediaIsPlaying, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    /// <summary>Modo de fijado de la pestaña.</summary>
    public TabPinMode PinMode
    {
        get => _pinMode;
        set
        {
            if (SetField(ref _pinMode, value))
            {
                OnPropertyChanged(nameof(IsPinned));
                OnPropertyChanged(nameof(ShowTitleInTab));
                OnPropertyChanged(nameof(ShowCloseButton));
                OnPropertyChanged(nameof(DisplayTitle));
            }
        }
    }

    public bool IsPinned => PinMode != TabPinMode.None;
    public bool ShowTitleInTab => PinMode != TabPinMode.LogoOnly;
    public bool ShowCloseButton => PinMode == TabPinMode.None;

    public string DisplayTitle
    {
        get
        {
            if (PinMode == TabPinMode.LogoOnly) return "";
            return Title;
        }
    }

    private ImageSource? _faviconImage;

    /// <summary>Imagen real del favicon oficial del sitio web.</summary>
    public ImageSource? FaviconImage
    {
        get => _faviconImage;
        set
        {
            if (SetField(ref _faviconImage, value))
            {
                OnPropertyChanged(nameof(HasFaviconImage));
                OnPropertyChanged(nameof(HasNoFaviconImage));
            }
        }
    }

    /// <summary>Indica si la pestaña tiene un favicon real cargado en mapa de bits.</summary>
    public bool HasFaviconImage => _faviconImage != null;

    /// <summary>Indica si no hay favicon real (usado para mostrar el icono emoji de respaldo).</summary>
    public bool HasNoFaviconImage => _faviconImage == null;

    /// <summary>Icono / Favicon representativo del sitio web.</summary>
    public string FaviconIcon
    {
        get
        {
            if (IsIncognito) return "🕵️";
            if (IsNewTab || string.IsNullOrWhiteSpace(Url)) return "⚡";

            string lower = Url.ToLowerInvariant();
            if (lower.Contains("youtube.com")) return "▶";
            if (lower.Contains("google.com")) return "🌐";
            if (lower.Contains("twitch.tv")) return "🎮";
            if (lower.Contains("twitter.com") || lower.Contains("x.com")) return "𝕏";
            if (lower.Contains("facebook.com")) return "👥";
            if (lower.Contains("github.com")) return "🐙";
            if (lower.Contains("netflix.com") || lower.Contains("disney") || lower.Contains("pelis")) return "🎬";
            if (lower.Contains("futbol") || lower.Contains("libre") || lower.Contains("deporte")) return "⚽";
            return "📄";
        }
    }

    /// <summary>Audio de la pestaña silenciado.</summary>
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (SetField(ref _isMuted, value))
            {
                OnPropertyChanged(nameof(AudioIndicatorText));
            }
        }
    }

    /// <summary>Icono dinámico para el indicador de audio.</summary>
    public string AudioIndicatorText => IsMuted ? "🔇" : (HasActiveMedia ? "🔊" : "");

    /// <summary>Pestaña creada en modo incógnito.</summary>
    public bool IsIncognito
    {
        get => _isIncognito;
        set
        {
            if (SetField(ref _isIncognito, value))
            {
                OnPropertyChanged(nameof(FaviconIcon));
            }
        }
    }

    /// <summary>True cuando muestra la página de nueva pestaña.</summary>
    public bool IsNewTab
    {
        get => _isNewTab;
        set
        {
            if (SetField(ref _isNewTab, value))
            {
                OnPropertyChanged(nameof(FaviconIcon));
            }
        }
    }

    public BrowserTab(WebView2 webView)
    {
        WebView = webView;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
