using System;
using System.Collections.Generic;
using System.Linq;
using LexBrowser.Core.Tv;
using Xunit;

namespace LexBrowser.Tests;

public class TvMultiSourceTests
{
    [Fact]
    public void ParseFreeTvM3u_DetectsIndicatorsAndCleansName()
    {
        string m3uContent = @"#EXTM3U
#EXTINF:-1 tvg-id=""latina.pe"" tvg-logo=""https://example.com/logo1.png"" group-title=""General"",Latina TV Ⓖ [Geo]
https://example.com/stream1.m3u8
#EXTINF:-1 tvg-id=""panamericana.pe"" group-title=""General"",Panamericana TV Ⓢ
https://example.com/stream2.m3u8
#EXTINF:-1 tvg-id=""atv.pe"" group-title=""Noticias"",ATV Noticias Ⓨ
https://www.youtube.com/watch?v=dQw4w9WgXcQ
";

        var channels = TvMultiSourceService.ParseM3uToChannels(m3uContent, provenance: "Curado (Free-TV)", tier: 2, defaultGroup: "General");

        Assert.Equal(3, channels.Count);

        // Canal 1: Geo-bloqueado
        var ch1 = channels[0];
        Assert.Equal("Latina TV [Geo]", ch1.Name);
        Assert.True(ch1.IsGeoblocked);
        Assert.False(ch1.IsSd);
        Assert.False(ch1.IsYouTube);
        Assert.Equal(2, ch1.Tier);
        Assert.Equal("Curado (Free-TV)", ch1.Provenance);

        // Canal 2: SD
        var ch2 = channels[1];
        Assert.Equal("Panamericana TV", ch2.Name);
        Assert.False(ch2.IsGeoblocked);
        Assert.True(ch2.IsSd);
        Assert.False(ch2.IsYouTube);

        // Canal 3: YouTube
        var ch3 = channels[2];
        Assert.Equal("ATV Noticias", ch3.Name);
        Assert.True(ch3.IsYouTube);
        Assert.Equal("external", ch3.Kind);
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", ch3.ExternalUrl);
    }

    [Fact]
    public void MergeChannels_PrefersHigherTierAndAttachesAlternates()
    {
        // Tier 1: Oficial
        var t1 = new List<TvChannel>
        {
            new TvChannel
            {
                Id = "nasa.tv",
                Name = "NASA TV",
                Url = "https://official.nasa.gov/live.m3u8",
                Tier = 1,
                Provenance = "Oficial verificado"
            }
        };

        // Tier 2: Free-TV
        var t2 = new List<TvChannel>
        {
            new TvChannel
            {
                Id = "nasa.tv",
                Name = "NASA TV HD",
                Url = "https://freetv.cdn/nasa.m3u8",
                Tier = 2,
                Provenance = "Curado (Free-TV)"
            },
            new TvChannel
            {
                Id = "bloomberg.us",
                Name = "Bloomberg TV Ⓖ",
                Url = "https://freetv.cdn/bloomberg.m3u8",
                Tier = 2,
                IsGeoblocked = true
            }
        };

        // Tier 3: iptv-org
        var t3 = new List<TvChannel>
        {
            new TvChannel
            {
                Id = "nasa.tv",
                Name = "NASA TV",
                Url = "https://community.cdn/nasa.m3u8",
                Tier = 3,
                Provenance = "Comunitario (iptv-org)"
            },
            new TvChannel
            {
                Id = "france24.fr",
                Name = "France 24",
                Url = "https://france24.cdn/live.m3u8",
                Tier = 3,
                Provenance = "Comunitario (iptv-org)"
            }
        };

        var merged = TvMultiSourceService.MergeChannels(t1, t2, t3);

        Assert.Equal(3, merged.Count);

        // NASA TV debe provenir de Tier 1 y tener como alternativos los de Tier 2 y 3
        var nasa = merged.First(c => c.Id == "nasa.tv");
        Assert.Equal(1, nasa.Tier);
        Assert.Equal("https://official.nasa.gov/live.m3u8", nasa.Url);
        Assert.NotNull(nasa.Alternates);
        Assert.Equal(2, nasa.Alternates.Count);
        Assert.Contains(nasa.Alternates, a => a.Url == "https://freetv.cdn/nasa.m3u8");
        Assert.Contains(nasa.Alternates, a => a.Url == "https://community.cdn/nasa.m3u8");

        // Bloomberg proviene de Tier 2
        var bloomberg = merged.First(c => c.Id == "bloomberg.us");
        Assert.Equal(2, bloomberg.Tier);
        Assert.True(bloomberg.IsGeoblocked);

        // France 24 proviene de Tier 3
        var france = merged.First(c => c.Id == "france24.fr");
        Assert.Equal(3, france.Tier);
    }

    [Fact]
    public void MergeChannels_DeduplicatesByNormalizedUrlEvenIfIdsDiffer()
    {
        var t2 = new List<TvChannel>
        {
            new TvChannel
            {
                Id = "custom_id_1",
                Name = "DW Español",
                Url = "https://dw.com/live/es/playlist.m3u8?token=123",
                Tier = 2
            }
        };

        var t3 = new List<TvChannel>
        {
            new TvChannel
            {
                Id = "dw_espanol.de",
                Name = "Deutsche Welle Español",
                Url = "https://dw.com/live/es/playlist.m3u8", // Misma URL normalizada
                Tier = 3
            }
        };

        var merged = TvMultiSourceService.MergeChannels(Array.Empty<TvChannel>(), t2, t3);

        Assert.Single(merged);
        Assert.Equal(2, merged[0].Tier);
    }

    [Fact]
    public void ReleaseMemoryCache_ZeroesMultiSourceCache()
    {
        TvMultiSourceService.Shared.ReleaseMemoryCache();
        Assert.False(TvMultiSourceService.Shared.HasMemoryLoaded);
    }
}
