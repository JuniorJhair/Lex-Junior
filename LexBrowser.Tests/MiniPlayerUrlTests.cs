using LexBrowser.Core;
using Xunit;

namespace LexBrowser.Tests;

public class MiniPlayerUrlTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc", true)]
    [InlineData("https://m.youtube.com/watch?v=abc", true)]
    [InlineData("https://www.youtube.com/shorts/xyz", true)]
    [InlineData("https://www.youtube.com/live/xyz", true)]
    [InlineData("https://youtu.be/abc", true)]
    [InlineData("https://youtu.be/", false)]
    [InlineData("https://www.youtube.com/", false)]
    [InlineData("https://www.youtube.com/feed/subscriptions", false)]
    [InlineData("https://music.youtube.com/watch?v=abc", false)]
    [InlineData("https://notyoutube.com/watch?v=abc", false)]
    [InlineData("https://evil.com/?u=youtube.com/watch", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsYouTubeVideo_cases(string? url, bool expected) =>
        Assert.Equal(expected, MiniPlayerUrl.IsYouTubeVideo(url));

    [Fact]
    public void Build_adds_t_param_on_watch()
    {
        string r = MiniPlayerUrl.Build("https://www.youtube.com/watch?v=abc", 32.9);
        Assert.Contains("v=abc", r);
        Assert.Contains("t=32s", r);
    }

    [Fact]
    public void Build_replaces_existing_t_and_start()
    {
        string r = MiniPlayerUrl.Build("https://www.youtube.com/watch?v=abc&t=5s&start=9&list=PL1", 100);
        Assert.Contains("t=100s", r);
        Assert.DoesNotContain("t=5s", r);
        Assert.DoesNotContain("start=9", r);
        Assert.Contains("list=PL1", r);
    }

    [Fact]
    public void Build_keeps_shorts_untouched() =>
        Assert.Equal("https://www.youtube.com/shorts/xyz", MiniPlayerUrl.Build("https://www.youtube.com/shorts/xyz", 20));

    [Theory]
    [InlineData(0)]
    [InlineData(0.5)]
    [InlineData(-3)]
    [InlineData(double.NaN)]
    public void Build_ignores_start_of_video(double seconds) =>
        Assert.Equal("https://www.youtube.com/watch?v=abc", MiniPlayerUrl.Build("https://www.youtube.com/watch?v=abc", seconds));

    [Fact]
    public void Build_does_not_touch_non_youtube() =>
        Assert.Equal("https://example.com/video?x=1", MiniPlayerUrl.Build("https://example.com/video?x=1", 50));

    [Fact]
    public void Build_returns_input_when_not_a_url() =>
        Assert.Equal("no url", MiniPlayerUrl.Build("no url", 50));

    [Fact]
    public void IsShort_detects_shorts_only()
    {
        Assert.True(MiniPlayerUrl.IsShort("https://www.youtube.com/shorts/xyz"));
        Assert.False(MiniPlayerUrl.IsShort("https://www.youtube.com/watch?v=abc"));
    }
}

