using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LexBrowser.UI.Models;
using Xunit;

namespace LexBrowser.Tests;

public class BookmarkItemSerializationTests
{
    private static BitmapSource CreateIcon()
    {
        var pixels = new byte[4 * 4 * 4];
        var bmp = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, pixels, 16);
        bmp.Freeze();
        return bmp;
    }

    [Fact]
    public void Serialize_WithLoadedFavicon_DoesNotThrowAndOmitsImage()
    {
        var item = new BookmarkItem { Title = "Sitio", Url = "https://example.com" };
        item.FaviconImage = CreateIcon();

        string json = JsonSerializer.Serialize(new[] { item });

        Assert.Contains("https://example.com", json);
        Assert.DoesNotContain("FaviconImage", json);
        Assert.DoesNotContain("HasFaviconImage", json);
    }

    [Fact]
    public void RoundTrip_PreservesTitleAndUrl()
    {
        var item = new BookmarkItem { Title = "Sitio", Url = "https://example.com" };
        item.FaviconImage = CreateIcon();

        string json = JsonSerializer.Serialize(new[] { item });
        var loaded = JsonSerializer.Deserialize<BookmarkItem[]>(json);

        Assert.NotNull(loaded);
        Assert.Single(loaded!);
        Assert.Equal("Sitio", loaded![0].Title);
        Assert.Equal("https://example.com", loaded[0].Url);
    }
}
