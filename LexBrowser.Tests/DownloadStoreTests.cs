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

    [Fact]
    public void SanitizeAndMakeUniquePath_RejectsTraversalReservedAndDeduplicatesCollisions()
    {
        string targetDir = Path.Combine(_tempDir, "dl");
        Directory.CreateDirectory(targetDir);

        string pTraversal = DownloadStore.SanitizeAndMakeUniquePath(
            targetDir,
            @"..\..\evil<name>:CON?.zip",
            "https://example.com/fallback.zip",
            fileExists: _ => false);

        Assert.Equal(targetDir, Path.GetDirectoryName(pTraversal));
        Assert.DoesNotContain("..", Path.GetFileName(pTraversal));
        Assert.EndsWith(".zip", pTraversal, StringComparison.OrdinalIgnoreCase);

        string pReserved = DownloadStore.SanitizeAndMakeUniquePath(
            targetDir,
            "CON.txt",
            null,
            fileExists: _ => false);
        Assert.StartsWith("_", Path.GetFileName(pReserved));
        Assert.Equal(targetDir, Path.GetDirectoryName(pReserved));

        string existing = Path.Combine(targetDir, "video.mp4");
        string active = Path.Combine(targetDir, "video (1).mp4");
        string pUnique = DownloadStore.SanitizeAndMakeUniquePath(
            targetDir,
            "video.mp4",
            null,
            activePaths: new[] { active },
            fileExists: p => string.Equals(p, existing, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Path.Combine(targetDir, "video (2).mp4"), pUnique);
    }

    [Fact]
    public void ProgressEtaSpeedPauseResumeCancelRetryAndStartupRecovery_WorkWithoutWebView()
    {
        string storePath = Path.Combine(_tempDir, "downloads.json");
        var store = new DownloadStore(storePath);
        store.Load();

        string filePath = Path.Combine(_tempDir, "archive.bin");
        var entry = store.RegisterDownload("archive.bin", filePath, "https://cdn.example.com/archive.bin", totalBytes: -1);
        Assert.True(entry.IsIndeterminateProgress);
        Assert.Contains("Descargando", entry.StatusDisplay, StringComparison.OrdinalIgnoreCase);

        DateTime t0 = DateTime.UtcNow.AddSeconds(1);
        store.UpdateDownloadProgress(entry.Id, 0, 1000, "En progreso", nowUtc: t0);
        store.UpdateDownloadProgress(entry.Id, 250, 1000, "En progreso", nowUtc: t0.AddSeconds(1));

        Assert.Equal(25, entry.ProgressPercent);
        Assert.True(entry.SpeedBytesPerSec > 0);
        Assert.NotNull(entry.EstimatedSecondsRemaining);
        Assert.True(entry.EstimatedSecondsRemaining > 0);

        bool pausedCalled = false;
        bool resumedCalled = false;
        bool canceledCalled = false;
        bool retriedCalled = false;
        entry.PauseAction = () => pausedCalled = true;
        entry.ResumeAction = () => resumedCalled = true;
        entry.CancelAction = () => canceledCalled = true;
        entry.RetryAction = () => retriedCalled = true;

        Assert.True(entry.CanPause);
        entry.PauseAction();
        store.UpdateDownloadProgress(entry.Id, 250, 1000, "Pausado", canResume: true, nowUtc: t0.AddSeconds(2));
        Assert.True(pausedCalled);
        Assert.True(entry.CanResumeAction);

        entry.ResumeAction();
        Assert.True(resumedCalled);

        store.UpdateDownloadProgress(entry.Id, 250, 1000, "Interrumpido", interruptReason: "Conexión perdida", canResume: true, nowUtc: t0.AddSeconds(3));
        Assert.True(entry.IsInterrupted);
        Assert.True(entry.CanResumeAction);
        Assert.True(entry.CanRetry);
        Assert.Contains("Conexión perdida", entry.StatusDisplay);

        var summaryDuringIdle = store.GetActiveSummary();
        Assert.False(summaryDuringIdle.HasActive);

        // Re-activate and verify toolbar summary + CancelAllActive
        store.UpdateDownloadProgress(entry.Id, 500, 1000, "En progreso", nowUtc: t0.AddSeconds(4));
        var activeSummary = store.GetActiveSummary();
        Assert.Equal(1, activeSummary.ActiveCount);
        Assert.Equal(50, activeSummary.TotalProgressPercent);

        store.CancelAllActive();
        Assert.True(canceledCalled);
        Assert.Equal("Cancelado", entry.State);

        entry.RetryAction();
        Assert.True(retriedCalled);

        // Simulated crash recovery: persisted "En progreso" in JSON must load as "Interrumpido"
        File.WriteAllText(storePath,
            "[{\"Id\":\"zombie1\",\"FileName\":\"unfinished.iso\",\"FilePath\":\"C:\\\\tmp\\\\unfinished.iso\",\"Url\":\"https://x/unfinished.iso\",\"TotalBytes\":2048,\"BytesReceived\":512,\"State\":\"En progreso\",\"StartedAt\":\"2026-11-15T12:00:00Z\"}]");
        var recoveredStore = new DownloadStore(storePath);
        recoveredStore.Load();
        var zombie = Assert.Single(recoveredStore.GetEntries());
        Assert.Equal("Interrumpido", zombie.State);
        Assert.Contains("cerrada", zombie.InterruptReason ?? "", StringComparison.OrdinalIgnoreCase);
    }
}

