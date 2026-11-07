using LexBrowser.Core.Notifications;
using Xunit;

namespace LexBrowser.Tests;

public class NotificationSanitizerTests
{
    [Fact]
    public void Sanitize_StripsHtmlTags()
    {
        string raw = "<b>Importante:</b> Tu suscripción <script>alert(1)</script>está lista!";
        string cleaned = NotificationSanitizer.Sanitize(raw, 100);

        Assert.Equal("Importante: Tu suscripción está lista!", cleaned);
        Assert.DoesNotContain("<", cleaned);
        Assert.DoesNotContain(">", cleaned);
    }

    [Fact]
    public void Sanitize_StripsBidiOverrideCharacters()
    {
        // Caracteres de inversión de texto: \u202E (RLO) y \u200E (LRM)
        string spoofed = "Cuenta protegida \u202Edrowssap\u200E actualizada";
        string cleaned = NotificationSanitizer.Sanitize(spoofed, 100);

        Assert.False(cleaned.Contains('\u202E'));
        Assert.False(cleaned.Contains('\u200E'));
    }

    [Fact]
    public void Sanitize_StripsAsciiControlCharacters()
    {
        string raw = "Mensaje\u0000 con\u0007 caracteres\u001F de control";
        string cleaned = NotificationSanitizer.Sanitize(raw, 100);

        Assert.Equal("Mensaje con caracteres de control", cleaned);
    }

    [Fact]
    public void Sanitize_TruncatesToMaxLengthWithEllipsis()
    {
        string longText = new string('A', 150);
        string cleaned = NotificationSanitizer.Sanitize(longText, 50);

        Assert.True(cleaned.Length <= 51);
        Assert.EndsWith("…", cleaned);
    }
}
