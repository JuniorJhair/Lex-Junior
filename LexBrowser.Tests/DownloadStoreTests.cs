using System;
using System.IO;
using LexBrowser.Core;
using Xunit;

namespace LexBrowser.Tests;

public class DownloadStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _downloadsFilePath;

    public DownloadStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LexBrowserTests_Downloads_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _downloadsFilePath = Path.Combine(_tempDir, "downloads.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    [Fact]
    public void RegisterDownload_NormalTab_PersistsWhenCompleted()
    {
        var store = new DownloadStore(_downloadsFilePath);
        var entry = store.RegisterDownload("video.mp4", @"C:\Downloads\video.mp4", "https://example.com/video.mp4", 10485760, "En progreso", isIncognito: false);

        Assert.Equal("🎬", entry.CategoryIcon);
        Assert.True(entry.IsInProgress);

        store.UpdateDownload(entry.Id, 10485760, 10485760, "Completado");

        var reloaded = new DownloadStore(_downloadsFilePath);
        var list = reloaded.GetEntries();
        Assert.Single(list);
        Assert.Equal("video.mp4", list[0].FileName);
        Assert.True(list[0].IsCompleted);
        Assert.Equal(100, list[0].ProgressPercent);
    }

    [Fact]
    public void RegisterDownload_IncognitoTab_NeverPersistedToDisk()
    {
        var store = new DownloadStore(_downloadsFilePath);
        var incog = store.RegisterDownload("secret.pdf", @"C:\Downloads\secret.pdf", "https://private.org/secret.pdf", 2048, "Completado", isIncognito: true);
        store.Flush();

        // En memoria mientras la sesión está viva sí aparece
        Assert.Single(store.GetEntries());

        // Pero al recargar desde disco (downloads.json) hay 0 rastros de la descarga privada
        var reloaded = new DownloadStore(_downloadsFilePath);
        Assert.Empty(reloaded.GetEntries());
    }

    [Fact]
    public void DeleteAndClearAll_RemovesEntriesCleanly()
    {
        var store = new DownloadStore(_downloadsFilePath);
        var e1 = store.RegisterDownload("a.zip", @"C:\Downloads\a.zip", "https://example.com/a.zip", 1024, "Completado");
        var e2 = store.RegisterDownload("b.mp3", @"C:\Downloads\b.mp3", "https://example.com/b.mp3", 2048, "Completado");

        Assert.Equal(2, store.GetEntries().Count);

        store.DeleteEntry(e1.Id);
        Assert.Single(store.GetEntries());
        Assert.Equal(e2.Id, store.GetEntries()[0].Id);

        store.ClearAll();
        Assert.Empty(store.GetEntries());
    }
}

