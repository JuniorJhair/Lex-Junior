using System;
using Xunit;
using LexBrowser.Core;

namespace LexBrowser.Tests;

public class WindowsPrivacyTests
{
    [Fact]
    public void IsCapabilityBlocked_NullOrEmpty_ReturnsFalse()
    {
        Assert.False(WindowsPrivacy.IsCapabilityBlocked(null!));
        Assert.False(WindowsPrivacy.IsCapabilityBlocked(""));
        Assert.False(WindowsPrivacy.IsCapabilityBlocked("   "));
    }

    [Fact]
    public void IsMicrophoneBlocked_DoesNotThrow()
    {
        var ex = Record.Exception(() => WindowsPrivacy.IsMicrophoneBlocked());
        Assert.Null(ex);
    }

    [Fact]
    public void IsCameraBlocked_DoesNotThrow()
    {
        var ex = Record.Exception(() => WindowsPrivacy.IsCameraBlocked());
        Assert.Null(ex);
    }
}
