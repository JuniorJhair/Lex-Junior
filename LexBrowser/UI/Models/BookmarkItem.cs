using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LexBrowser.Core.Engine;

namespace LexBrowser.UI.Models;

public class BookmarkItem : INotifyPropertyChanged
{
    private string _title = "";
    private string _url = "";
    private string _faviconUrl = "";
    private ImageSource? _faviconImage;
    private bool _isLoadingFavicon = false;

    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }

    public string Url
    {
        get => _url;
        set
        {
            if (SetField(ref _url, value))
            {
                _faviconImage = null;
                _isLoadingFavicon = false;
                OnPropertyChanged(nameof(FaviconImage));
                OnPropertyChanged(nameof(HasFaviconImage));
                OnPropertyChanged(nameof(HasNoFaviconImage));
            }
        }
    }

    public string FaviconUrl
    {
        get => _faviconUrl;
        set => SetField(ref _faviconUrl, value);
    }

    [JsonIgnore]
    public ImageSource? FaviconImage
    {
        get
        {
            if (_faviconImage == null && !string.IsNullOrWhiteSpace(Url))
            {
                string domain = FaviconService.ExtractDomain(Url);
                var cached = FaviconService.GetCachedFavicon(domain);
                if (cached != null)
                {
                    _faviconImage = cached;
                    return _faviconImage;
                }

                if (!_isLoadingFavicon)
                {
                    _isLoadingFavicon = true;
                    _ = LoadFaviconAsync(Url);
                }
            }
            return _faviconImage;
        }
        set
        {
            if (SetField(ref _faviconImage, value))
            {
                OnPropertyChanged(nameof(HasFaviconImage));
                OnPropertyChanged(nameof(HasNoFaviconImage));
            }
        }
    }

    private async Task LoadFaviconAsync(string url)
    {
        try
        {
            var icon = await FaviconService.ResolveFaviconAsync(url);
            if (icon != null)
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    FaviconImage = icon;
                });
            }
        }
        catch { }
        finally
        {
            _isLoadingFavicon = false;
        }
    }

    [JsonIgnore]
    public bool HasFaviconImage => FaviconImage != null;
    [JsonIgnore]
    public bool HasNoFaviconImage => FaviconImage == null;

    [JsonIgnore]
    public string DisplayIcon
    {
        get
        {
            if (Url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)) return "▶";
            if (Url.Contains("google.com", StringComparison.OrdinalIgnoreCase)) return "🌐";
            if (Url.Contains("twitch.tv", StringComparison.OrdinalIgnoreCase)) return "🎮";
            if (Url.Contains("twitter.com", StringComparison.OrdinalIgnoreCase) || Url.Contains("x.com", StringComparison.OrdinalIgnoreCase)) return "𝕏";
            if (Url.Contains("disney", StringComparison.OrdinalIgnoreCase)) return "✨";
            if (Url.Contains("netflix", StringComparison.OrdinalIgnoreCase)) return "🎬";
            if (Url.Contains("futbol", StringComparison.OrdinalIgnoreCase) || Url.Contains("libre", StringComparison.OrdinalIgnoreCase)) return "⚽";
            return "🔖";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(DisplayIcon));
        return true;
    }
}

