using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using LexBrowser.Core;
using Xunit;

namespace LexBrowser.Tests;

public class MediaExtractorTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/abc123XYZ_-", "abc123XYZ_-")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=12345", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ&list=RD", "dQw4w9WgXcQ")]
    public void TryExtractYouTubeVideoId_ExtractsValidId(string url, string expectedId)
    {
        string? actual = MediaExtractorService.TryExtractYouTubeVideoId(url);
        Assert.Equal(expectedId, actual);
    }

    [Theory]
    [InlineData("https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg", "image/jpeg", 500_000)]
    [InlineData("https://yt3.ggpht.com/ytc/avatar123.jpg", "image/jpeg", 400_000)]
    [InlineData("https://rr1---sn-abc.googlevideo.com/videoplayback?expire=1&range=0-12345", "video/mp4", 12345)]
    [InlineData("https://cdn.site.com/hls/segment-001.ts", "video/mp2t", 1_200_000)]
    [InlineData("https://cdn.site.com/dash/chunk-stream0-00001.m4s", "video/iso.segment", 900_000)]
    [InlineData("https://site.com/assets/ui-click.mp3", "audio/mpeg", 15_000)]
    [InlineData("https://site.com/assets/tiny-loop.mp4", "video/mp4", 120_000)]
    public void IsGarbageMediaUrl_BlocksGarbageAndFragments(string url, string contentType, long length)
    {
        Assert.True(MediaExtractorService.IsGarbageMediaUrl(url, contentType, length));
    }

    [Theory]
    [InlineData("https://cdn.example.com/movies/pelicula-completa.mp4", "video/mp4", 45_000_000)]
    [InlineData("https://streaming.example.com/live/master.m3u8", "application/vnd.apple.mpegurl", 1200)]
    [InlineData("https://cdn.example.com/music/cancion.mp3", "audio/mpeg", 4_500_000)]
    [InlineData("https://cdn.example.com/docs/manual-oficial.pdf", "application/pdf", 1_500_000)]
    public void IsGarbageMediaUrl_AllowsLegitimateMedia(string url, string contentType, long length)
    {
        Assert.False(MediaExtractorService.IsGarbageMediaUrl(url, contentType, length));
    }

    [Fact]
    public void ParseYouTubePlayerResponseJson_ExtractsHdVideoAndAudioCleanly()
    {
        string sampleJson = """
        {
          "playabilityStatus": { "status": "OK" },
          "videoDetails": {
            "videoId": "dQw4w9WgXcQ",
            "title": "Video Oficial de Prueba - YouTube",
            "lengthSeconds": "212"
          },
          "streamingData": {
            "formats": [
              {
                "itag": 18,
                "url": "https://rr1.googlevideo.com/videoplayback?itag=18&clen=12582912",
                "mimeType": "video/mp4; codecs=\"avc1.42001E, mp4a.40.2\"",
                "width": 640,
                "height": 360,
                "contentLength": "12582912"
              }
            ],
            "adaptiveFormats": [
              {
                "itag": 137,
                "url": "https://rr1.googlevideo.com/videoplayback?itag=137&clen=52428800",
                "mimeType": "video/mp4; codecs=\"avc1.640028\"",
                "bitrate": 4200000,
                "width": 1920,
                "height": 1080,
                "qualityLabel": "1080p",
                "contentLength": "52428800"
              },
              {
                "itag": 136,
                "url": "https://rr1.googlevideo.com/videoplayback?itag=136&clen=26214400",
                "mimeType": "video/mp4; codecs=\"avc1.4d401f\"",
                "bitrate": 2100000,
                "width": 1280,
                "height": 720,
                "qualityLabel": "720p",
                "contentLength": "26214400"
              },
              {
                "itag": 140,
                "url": "https://rr1.googlevideo.com/videoplayback?itag=140&clen=3407872",
                "mimeType": "audio/mp4; codecs=\"mp4a.40.2\"",
                "bitrate": 128000,
                "contentLength": "3407872"
              }
            ]
          }
        }
        """;

        var items = MediaExtractorService.ParseYouTubePlayerResponseJson(
            sampleJson,
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            "Fallback",
            "TestUA");

        Assert.Equal(5, items.Count);
        Assert.Equal("MP4 1080p HD", items[0].FormatBadge);
        Assert.Equal("Video", items[0].Category);
        Assert.NotNull(items[0].AudioPairUrl);
        Assert.Equal(52428800, items[0].VideoSizeBytes);
        Assert.Equal(3407872, items[0].AudioSizeBytes);

        Assert.Equal("MP4 720p HD", items[1].FormatBadge);
        Assert.Equal("Video", items[1].Category);
        Assert.NotNull(items[1].AudioPairUrl);

        Assert.Equal("MP4 360p", items[2].FormatBadge);
        Assert.Equal("Video", items[2].Category);
        Assert.Null(items[2].AudioPairUrl);

        Assert.Equal("AUDIO MP3", items[3].FormatBadge);
        Assert.Equal("Audio", items[3].Category);
        Assert.Equal(3407872, items[3].VideoSizeBytes);

        Assert.Equal("AUDIO M4A", items[4].FormatBadge);
        Assert.Equal("Audio", items[4].Category);
        Assert.Equal(3407872, items[4].VideoSizeBytes);
    }

    [Fact]
    public void ParseHlsManifestContent_ExtractsResolutionVariantsSorted()
    {
        string masterM3u8 = """
        #EXTM3U
        #EXT-X-VERSION:3
        #EXT-X-STREAM-INF:BANDWIDTH=1500000,RESOLUTION=854x480
        480p/index.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=4500000,RESOLUTION=1920x1080
        1080p/index.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=2800000,RESOLUTION=1280x720
        https://cdn.example.com/hls/720p/index.m3u8
        """;

        var baseUri = new Uri("https://cdn.example.com/hls/master.m3u8");
        var variants = MediaExtractorService.ParseHlsManifestContent(masterM3u8, baseUri, "https://example.com/watch", "Episodio 1");

        Assert.Equal(3, variants.Count);
        Assert.Equal("HLS 1080p", variants[0].FormatBadge);
        Assert.Equal("https://cdn.example.com/hls/1080p/index.m3u8", variants[0].Url);
        Assert.True(variants[0].IsHlsStream);

        Assert.Equal("HLS 720p", variants[1].FormatBadge);
        Assert.Equal("HLS 480p", variants[2].FormatBadge);
    }

    [Fact]
    public void MuxFragmentedMp4_CombinesVideoAndAudioTracksProperly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "LexMuxTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        string vPath = Path.Combine(tempDir, "video.mp4");
        string aPath = Path.Combine(tempDir, "audio.m4a");
        string outPath = Path.Combine(tempDir, "merged.mp4");

        try
        {
            File.WriteAllBytes(vPath, BuildSyntheticFragmentedMp4(trackId: 1, samplePayload: new byte[] { 0x11, 0x22, 0x33, 0x44 }));
            File.WriteAllBytes(aPath, BuildSyntheticFragmentedMp4(trackId: 1, samplePayload: new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }));

            MediaExtractorService.MuxFragmentedMp4(vPath, aPath, outPath);

            Assert.True(File.Exists(outPath));
            byte[] merged = File.ReadAllBytes(outPath);
            Assert.True(merged.Length > 80);

            string ascii = Encoding.ASCII.GetString(merged);
            Assert.Contains("ftyp", ascii);
            Assert.Contains("moov", ascii);
            Assert.Contains("mvex", ascii);
            Assert.Contains("moof", ascii);
            Assert.Contains("mdat", ascii);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static byte[] BuildSyntheticFragmentedMp4(uint trackId, byte[] samplePayload)
    {
        byte[] ftyp = MakeBox("ftyp", Encoding.ASCII.GetBytes("isom\0\0\x02\0isomiso2dash"));

        byte[] mvhdPayload = new byte[100];
        BinaryPrimitives.WriteUInt32BigEndian(mvhdPayload.AsSpan(96, 4), 2u);
        byte[] mvhd = MakeBox("mvhd", mvhdPayload);

        byte[] tkhdPayload = new byte[84];
        BinaryPrimitives.WriteUInt32BigEndian(tkhdPayload.AsSpan(12, 4), trackId);
        byte[] trak = MakeBox("trak", MakeBox("tkhd", tkhdPayload));

        byte[] trexPayload = new byte[24];
        BinaryPrimitives.WriteUInt32BigEndian(trexPayload.AsSpan(4, 4), trackId);
        byte[] mvex = MakeBox("mvex", MakeBox("trex", trexPayload));

        byte[] moov = MakeBox("moov", Combine(mvhd, trak, mvex));

        byte[] mfhdPayload = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(mfhdPayload.AsSpan(4, 4), 1u);

        byte[] tfhdPayload = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(tfhdPayload.AsSpan(4, 4), trackId);

        byte[] moof = MakeBox("moof", Combine(MakeBox("mfhd", mfhdPayload), MakeBox("traf", MakeBox("tfhd", tfhdPayload))));
        byte[] mdat = MakeBox("mdat", samplePayload);

        return Combine(ftyp, moov, moof, mdat);
    }

    private static byte[] MakeBox(string type, byte[] payload)
    {
        byte[] box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(0, 4), (uint)box.Length);
        Encoding.ASCII.GetBytes(type, 0, 4, box, 4);
        Buffer.BlockCopy(payload, 0, box, 8, payload.Length);
        return box;
    }

    private static byte[] Combine(params byte[][] arrays)
    {
        using var ms = new MemoryStream();
        foreach (var arr in arrays) ms.Write(arr, 0, arr.Length);
        return ms.ToArray();
    }

    [Fact]
    public async Task ExtractYouTubeStreamsAsync_RealWorldVideo_ExtractsVideoAndAudioWithVisitorData()
    {
        string targetUrl = "https://www.youtube.com/watch?v=pCPYb7KPoUc";
        var items = await MediaExtractorService.ExtractYouTubeStreamsAsync(targetUrl, "Jósean Log - Doma");

        Assert.NotEmpty(items);
        Assert.Contains(items, x => x.Category == "Video" && x.FormatBadge.Contains("MP4"));
        Assert.Contains(items, x => x.Category == "Audio" && x.FormatBadge.Contains("AUDIO"));

        var videoItem = items.First(x => x.Category == "Video");
        Assert.False(string.IsNullOrWhiteSpace(videoItem.Url));
        Assert.Contains("googlevideo.com", videoItem.Url);
    }

    [Fact]
    public async Task DownloadMediaItemAsync_RealWorldYouTube_DownloadsAudioSuccessfully()
    {
        string targetUrl = "https://www.youtube.com/watch?v=pCPYb7KPoUc";
        var items = await MediaExtractorService.ExtractYouTubeStreamsAsync(targetUrl, "Jósean Log - Doma");
        var audioItem = items.FirstOrDefault(x => x.Category == "Audio" && x.FormatBadge == "AUDIO M4A")
                     ?? items.FirstOrDefault(x => x.Category == "Audio");

        Assert.NotNull(audioItem);

        string tempFile = Path.Combine(Path.GetTempPath(), $"LexTest_Doma_{Guid.NewGuid():N}.m4a");
        try
        {
            long bytesDownloaded = await MediaExtractorService.DownloadMediaItemAsync(audioItem, tempFile);

            Assert.True(File.Exists(tempFile), "El archivo de audio descargado debe existir en disco.");
            long diskSize = new FileInfo(tempFile).Length;
            Assert.True(diskSize > 1_000_000, $"El archivo debe tener al menos 1 MB. Tamaño real: {diskSize} bytes.");
            Assert.Equal(diskSize, bytesDownloaded);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }
}

