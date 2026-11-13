using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LexBrowser.Core.Tv;

public sealed class M3uParseResult
{
    public List<TvChannel> Channels { get; init; } = new();
    public int SkippedUnsupportedScheme { get; set; }
    public int SkippedPrivate { get; set; }
    public int SkippedInvalid { get; set; }
    public int SkippedDuplicate { get; set; }
    public bool IsStandaloneHlsManifest { get; set; }
}

/// <summary>Lector de listas M3U/M3U8 provistas por el usuario. No propaga cabeceras de terceros.</summary>
public static class M3uParser
{
    public const int MaxChannels = 50000;

    private static readonly Regex AttrRegex =
        new("([A-Za-z0-9_\\-]+)=\"([^\"]*)\"", RegexOptions.Compiled);

    public static List<TvChannel> Parse(string? text, string source = "user")
        => ParseDetailed(text, source, null).Channels;

    public static M3uParseResult ParseDetailed(string? text, string source = "user", string? sourceUrl = null)
    {
        var outcome = new M3uParseResult();
        if (string.IsNullOrWhiteSpace(text)) return outcome;

        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TvChannel? current = null;
        bool sawExtX = false;

        foreach (string raw in text.Split('\n'))
        {
            if (outcome.Channels.Count >= MaxChannels) break;
            string line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            {
                // Ignorar metadatos de cabecera
                continue;
            }

            if (line.StartsWith("#EXT-X-", StringComparison.OrdinalIgnoreCase))
            {
                sawExtX = true;
                continue;
            }

            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                current = ParseInfo(line, source);
                continue;
            }

            if (line.StartsWith("#EXTGRP:", StringComparison.OrdinalIgnoreCase))
            {
                if (current != null && current.Group.Length == 0)
                    current.Group = TvUrlGuard.SanitizeHeaderValue(line.Substring(8), 80);
                continue;
            }

            if (line[0] == '#') continue;
            if (current == null) continue;

            // Limpiar posibles sufijos pipe de Kodi (|Referer=...) sin usarlos
            string rawUrlPart = line;
            int pipe = rawUrlPart.IndexOf('|');
            if (pipe > 0)
            {
                rawUrlPart = rawUrlPart.Substring(0, pipe).Trim();
            }

            if (ClassifyUrl(rawUrlPart, out var safeUri, outcome))
            {
                if (!seenUrls.Add(safeUri!.AbsoluteUri))
                {
                    outcome.SkippedDuplicate++;
                }
                else
                {
                    current.Url = safeUri.AbsoluteUri;
                    if (current.Name.Length == 0) current.Name = safeUri.Host;
                    if (current.Id.Length == 0) current.Id = "u:" + current.Url;
                    outcome.Channels.Add(current);
                }
            }

            current = null;
        }

        if (outcome.Channels.Count == 0 && sawExtX &&
            TvUrlGuard.IsAllowedRemoteUrl(sourceUrl, out var standaloneUri) && standaloneUri != null)
        {
            outcome.IsStandaloneHlsManifest = true;
            outcome.Channels.Add(new TvChannel
            {
                Number = 1,
                Id = "hls:" + standaloneUri.AbsoluteUri,
                Name = string.IsNullOrWhiteSpace(source) ? standaloneUri.Host : TvUrlGuard.SanitizeHeaderValue(source, 80),
                Url = standaloneUri.AbsoluteUri,
                Group = "Stream HLS",
                Source = source
            });
        }

        return outcome;
    }

    private static bool ClassifyUrl(string rawUrl, out Uri? safeUri, M3uParseResult outcome)
    {
        safeUri = null;
        if (string.IsNullOrWhiteSpace(rawUrl) || rawUrl.Length > 2048 ||
            !Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var parsed))
        {
            outcome.SkippedInvalid++;
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            outcome.SkippedUnsupportedScheme++;
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            outcome.SkippedInvalid++;
            return false;
        }

        if (TvUrlGuard.IsPrivateOrLocalHost(parsed.Host))
        {
            outcome.SkippedPrivate++;
            return false;
        }

        if (!TvUrlGuard.IsAllowedRemoteUrl(rawUrl, out safeUri) || safeUri == null)
        {
            outcome.SkippedInvalid++;
            return false;
        }

        return true;
    }

    private static TvChannel ParseInfo(string line, string source)
    {
        var ch = new TvChannel { Source = source };
        int comma = FindNameComma(line);
        string head = comma >= 0 ? line.Substring(0, comma) : line;
        string name = comma >= 0 ? line.Substring(comma + 1) : "";

        foreach (Match m in AttrRegex.Matches(head))
        {
            string key = m.Groups[1].Value.ToLowerInvariant();
            string val = m.Groups[2].Value.Trim();
            switch (key)
            {
                case "tvg-id": ch.Id = TvUrlGuard.SanitizeHeaderValue(val, 120); break;
                case "tvg-name":
                    if (ch.Name.Length == 0) ch.Name = TvUrlGuard.SanitizeHeaderValue(val, 120);
                    break;
                case "tvg-chno":
                    if (int.TryParse(val, out int chNo) && chNo > 0 && chNo < 100000) ch.Number = chNo;
                    break;
                case "tvg-logo":
                    ch.Logo = TvUrlGuard.IsAllowedRemoteUrl(val, out var lu) && lu != null && lu.Scheme == Uri.UriSchemeHttps
                        ? lu.AbsoluteUri
                        : "";
                    break;
                case "tvg-language": ch.Language = TvUrlGuard.SanitizeHeaderValue(val, 40); break;
                case "tvg-country": ch.Country = TvUrlGuard.SanitizeHeaderValue(val, 40); break;
                case "group-title": ch.Group = TvUrlGuard.SanitizeHeaderValue(val, 80); break;
            }
        }

        string commaName = TvUrlGuard.SanitizeHeaderValue(name, 120);
        if (commaName.Length > 0) ch.Name = commaName;
        return ch;
    }

    /// <summary>Busca la coma que separa atributos y nombre, ignorando las que están entre comillas.</summary>
    private static int FindNameComma(string line)
    {
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"') inQuotes = !inQuotes;
            else if (c == ',' && !inQuotes) return i;
        }
        return -1;
    }
}
