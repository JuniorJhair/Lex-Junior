using System;
using System.Collections.Generic;
using LexBrowser.Core;
using Xunit;

namespace LexBrowser.Tests;

public class ReadAloudServiceTests
{
    [Fact]
    public void NormalizeText_collapses_whitespace_removes_control_chars_and_limits_to_30000()
    {
        Assert.Equal(string.Empty, ReadAloudService.NormalizeText(null));
        Assert.Equal(string.Empty, ReadAloudService.NormalizeText("   \t\r\n  "));

        string dirty = "  Hola\u0007   mundo\r\n\tcon\u0000   espacios  ";
        Assert.Equal("Hola mundo con espacios", ReadAloudService.NormalizeText(dirty));

        string oversized = new string('a', 35000);
        string normalized = ReadAloudService.NormalizeText(oversized);
        Assert.Equal(ReadAloudService.MaxCharacters, normalized.Length);
    }

    [Fact]
    public void SelectVoiceName_prefers_explicit_voice_otherwise_matches_two_letter_culture_else_null()
    {
        var voices = new List<(string Name, string TwoLetterIsoLanguage)>
        {
            ("Microsoft David Desktop", "en"),
            ("Microsoft Sabina Desktop", "es"),
            ("Microsoft Hortense Desktop", "fr")
        };

        // Preferencia explícita por nombre
        Assert.Equal("Microsoft Hortense Desktop",
            ReadAloudService.SelectVoiceName(voices, "Microsoft Hortense Desktop", "es"));

        // Preferencia inexistente cae a selección por idioma
        Assert.Equal("Microsoft Sabina Desktop",
            ReadAloudService.SelectVoiceName(voices, "Voz Inexistente", "es-PE"));

        // Automática por idioma corto o con región
        Assert.Equal("Microsoft David Desktop",
            ReadAloudService.SelectVoiceName(voices, "", "en_US"));

        // Sin coincidencia devuelve null (voz predeterminada)
        Assert.Null(ReadAloudService.SelectVoiceName(voices, "", "ja"));
        Assert.Null(ReadAloudService.SelectVoiceName(Array.Empty<(string, string)>(), "Microsoft David Desktop", "en"));
    }
}

