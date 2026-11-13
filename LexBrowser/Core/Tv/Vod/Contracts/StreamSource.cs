using System;
using System.Collections.Generic;

namespace LexBrowser.Core.Tv.Vod.Contracts;

public sealed class SubtitleTrack
{
    public string Language { get; set; } = "es";
    public string Label { get; set; } = "Español";
    public string Url { get; set; } = "";
    public string Format { get; set; } = "vtt"; // "vtt" | "srt"
}

/// <summary>
/// Fuente de reproducción VOD para el reproductor HTML5/HLS.js/Shaka Player.
/// </summary>
public sealed class StreamSource
{
    public string ProviderId { get; set; } = "";
    public string Title { get; set; } = ""; // e.g. "1080p - Archivo Oficial", "HLS Multi-Bitrate"
    public string Url { get; set; } = "";
    public string Quality { get; set; } = "1080p"; // "4K" | "1080p" | "720p" | "480p" | "auto"
    public string Format { get; set; } = "hls"; // "hls" | "mp4" | "webm" | "external"
    public List<SubtitleTrack> Subtitles { get; set; } = new();
    public Dictionary<string, string> Headers { get; set; } = new();
    public bool RequiresProxy { get; set; }
}

