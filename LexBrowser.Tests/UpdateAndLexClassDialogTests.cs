using System.Linq;
using LexBrowser.Core;
using LexBrowser.UI.Views;
using Xunit;

namespace LexBrowser.Tests;

public class UpdateAndLexClassDialogTests
{
    [Fact]
    public void ParseReleaseNotesBlocks_ParsesHeadingsBulletsBoldAndCode_WithoutRawAsterisks()
    {
        const string markdown = """
            ## Novedades de v2.7.0
            - **Perfiles de Privacidad (A / B / C / Personalizado)**: Control total de conexiones de red en `settings.json`.
            - Soporte completo para **PDFs virtuales** (`http://` y `https://`) en [Lex PDF Studio](https://github.com/JuniorJhair/Lex-Junior).
            ---
            **Seguridad y Estabilidad**
            1. Verificación criptográfica **SHA-256** del instalador.
            """;

        var blocks = UpdatePromptDialog.ParseReleaseNotesBlocks(markdown);

        Assert.Equal(6, blocks.Count);
        Assert.Equal(ReleaseNoteBlockKind.Heading, blocks[0].Kind);
        Assert.Equal("Novedades de v2.7.0", string.Concat(blocks[0].Spans.Select(s => s.Text)));

        Assert.Equal(ReleaseNoteBlockKind.Bullet, blocks[1].Kind);
        Assert.Contains(blocks[1].Spans, s => s.Kind == ReleaseNoteSpanKind.Bold && s.Text == "Perfiles de Privacidad (A / B / C / Personalizado)");
        Assert.Contains(blocks[1].Spans, s => s.Kind == ReleaseNoteSpanKind.Code && s.Text == "settings.json");

        Assert.Equal(ReleaseNoteBlockKind.Bullet, blocks[2].Kind);
        Assert.Contains(blocks[2].Spans, s => s.Kind == ReleaseNoteSpanKind.Bold && s.Text == "PDFs virtuales");
        Assert.Contains(blocks[2].Spans, s => s.Text.Contains("Lex PDF Studio"));

        Assert.Equal(ReleaseNoteBlockKind.Divider, blocks[3].Kind);
        Assert.Equal(ReleaseNoteBlockKind.Heading, blocks[4].Kind);
        Assert.Equal(ReleaseNoteBlockKind.Bullet, blocks[5].Kind);

        // Ningún span debe contener sintaxis cruda "**" ni backticks "`"
        foreach (var block in blocks)
        {
            foreach (var span in block.Spans)
            {
                Assert.DoesNotContain("**", span.Text);
                Assert.DoesNotContain("`", span.Text);
            }
        }
    }

    [Theory]
    [InlineData("lex://class", true)]
    [InlineData("lex:class", true)]
    [InlineData("https://class.lex.local/index.html", true)]
    [InlineData("https://tv.lex.local/index.html", false)]
    [InlineData("https://example.com", false)]
    public void IsLexClassUrl_RecognizesLexClassShortcuts(string input, bool expected)
    {
        Assert.Equal(expected, LexClassComingSoonDialog.IsLexClassUrl(input));
    }

    [Fact]
    public void OmniboxInputResolver_ResolvesLexClassShortcut()
    {
        string target = OmniboxInputResolver.ResolveNavigationTarget("lex://class");
        Assert.Equal(LexClassComingSoonDialog.VirtualUrl, target);
    }
}
