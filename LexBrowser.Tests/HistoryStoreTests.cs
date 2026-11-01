using System.Text.Json;
using LexBrowser.Core;
using Xunit;

namespace LexBrowser.Tests;

public sealed class HistoryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lex_hist_" + Guid.NewGuid().ToString("N"));
    private readonly string _file;
    private DateTime _now = new(2026, 11, 1, 12, 0, 0);
    private bool _enabled = true;

    public HistoryStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "history.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private HistoryStore NewStore(TimeSpan? delay = null, int maxEntries = HistoryStore.DefaultMaxEntries) =>
        new(_file, () => _enabled, () => _now, delay ?? TimeSpan.FromMinutes(10), maxEntries);

    // ── Altas ─────────────────────────────────────────────────────────────
    [Fact]
    public void AddEntry_registers_visit_with_domain()
    {
        using var s = NewStore();
        s.AddEntry("https://www.youtube.com/watch?v=1", "Video 1");
        var e = Assert.Single(s.GetEntries());
        Assert.Equal("Video 1", e.Title);
        Assert.Equal("youtube.com", e.Domain);
    }

    [Theory]
    [InlineData("about:blank")]
    [InlineData("data:text/html,hola")]
    [InlineData("file:///C:/NewTabPage.html")]
    [InlineData("")]
    [InlineData("   ")]
    public void AddEntry_ignores_internal_pages(string url)
    {
        using var s = NewStore();
        s.AddEntry(url, "x");
        Assert.Equal(0, s.GetTotalCount());
    }

    [Fact]
    public void AddEntry_does_nothing_when_history_is_disabled()
    {
        _enabled = false;
        using var s = NewStore();
        s.AddEntry("https://a.com/", "A");
        s.UpdateTitle("https://a.com/", "A2");
        Assert.Equal(0, s.GetTotalCount());
    }

    [Fact]
    public void Blank_title_falls_back_to_url()
    {
        using var s = NewStore();
        s.AddEntry("https://a.com/x", "  ");
        Assert.Equal("https://a.com/x", s.GetEntries()[0].Title);
    }

    [Fact]
    public void Same_url_within_5_seconds_is_merged()
    {
        using var s = NewStore();
        s.AddEntry("https://a.com/", "Primero");
        _now = _now.AddSeconds(3);
        s.AddEntry("https://a.com/", "Segundo");
        var e = Assert.Single(s.GetEntries());
        Assert.Equal("Segundo", e.Title);
    }

    [Fact]
    public void Same_url_after_5_seconds_is_a_new_visit()
    {
        using var s = NewStore();
        s.AddEntry("https://a.com/", "A");
        _now = _now.AddSeconds(6);
        s.AddEntry("https://a.com/", "A");
        Assert.Equal(2, s.GetTotalCount());
    }

    // ── UpdateTitle: evita el duplicado que había entre NavigationCompleted y DocumentTitleChanged ─
    [Fact]
    public void UpdateTitle_on_same_url_never_creates_a_duplicate_even_much_later()
    {
        using var s = NewStore();
        s.AddEntry("https://www.youtube.com/watch?v=1", "Video");
        _now = _now.AddMinutes(10); // YouTube cambia el título (notificaciones) mucho después
        s.UpdateTitle("https://www.youtube.com/watch?v=1", "(1) Video");
        var e = Assert.Single(s.GetEntries());
        Assert.Equal("(1) Video", e.Title);
    }

    [Fact]
    public void UpdateTitle_on_different_url_records_spa_navigation()
    {
        using var s = NewStore();
        s.AddEntry("https://www.youtube.com/watch?v=1", "Video 1");
        _now = _now.AddSeconds(30);
        s.UpdateTitle("https://www.youtube.com/watch?v=2", "Video 2");
        Assert.Equal(2, s.GetTotalCount());
    }

    [Fact]
    public void UpdateTitle_with_blank_title_keeps_existing_title()
    {
        using var s = NewStore();
        s.AddEntry("https://a.com/", "Real");
        s.UpdateTitle("https://a.com/", "");
        Assert.Equal("Real", s.GetEntries()[0].Title);
    }

    [Fact]
    public void Entries_are_capped_at_MaxEntries_keeping_the_newest()
    {
        const int cap = 20;
        using var s = NewStore(maxEntries: cap);
        for (int i = 0; i < cap + 10; i++)
        {
            _now = _now.AddSeconds(10);
            s.AddEntry($"https://a.com/{i}", "t" + i);
        }
        Assert.Equal(cap, s.GetTotalCount());
        Assert.DoesNotContain(s.GetEntries(), e => e.Url == "https://a.com/0");
        Assert.Contains(s.GetEntries(), e => e.Url == $"https://a.com/{cap + 9}");
    }

    // ── Persistencia ─────────────────────────────────────────────────────
    [Fact]
    public void AddEntry_does_not_touch_disk_until_flush_or_debounce()
    {
        using var s = NewStore(TimeSpan.FromMinutes(10));
        s.AddEntry("https://a.com/", "A");
        Assert.False(File.Exists(_file)); // nada de escritura síncrona en el hilo de UI
        s.Flush();
        Assert.True(File.Exists(_file));
    }

    [Fact]
    public async Task Debounce_writes_in_background_after_delay()
    {
        using var s = NewStore(TimeSpan.FromMilliseconds(150));
        for (int i = 0; i < 20; i++) s.AddEntry($"https://a.com/{i}", "t");
        for (int i = 0; i < 40 && !File.Exists(_file); i++) await Task.Delay(50);
        Assert.True(File.Exists(_file));
        using var reload = NewStore();
        Assert.Equal(20, reload.GetTotalCount());
    }

    [Fact]
    public void Flush_roundtrip_preserves_data_and_leaves_no_tmp_file()
    {
        using (var s = NewStore())
        {
            s.AddEntry("https://a.com/1", "Uno");
            _now = _now.AddMinutes(1);
            s.AddEntry("https://b.com/2", "Dos");
            s.Flush();
        }
        Assert.False(File.Exists(_file + ".tmp"));

        using var again = NewStore();
        var entries = again.GetEntries();
        Assert.Equal(2, entries.Count);
        Assert.Equal("Dos", entries[0].Title); // más reciente primero
    }

    [Fact]
    public void Saved_json_is_compact_and_has_no_ui_computed_properties()
    {
        using var s = NewStore();
        s.AddEntry("https://a.com/", "A");
        s.Flush();
        string json = File.ReadAllText(_file);
        Assert.DoesNotContain("DisplayTime", json);
        Assert.DoesNotContain("DayGroup", json);
        Assert.DoesNotContain("FaviconUrl", json);
        Assert.DoesNotContain("\n", json);
    }

    [Fact]
    public void Loads_legacy_indented_file_that_contains_computed_properties()
    {
        File.WriteAllText(_file, """
        [
          {
            "Id": "abc",
            "Url": "https://old.com/",
            "Title": "Viejo",
            "Domain": "old.com",
            "VisitedAt": "2026-10-01T10:00:00",
            "DisplayTime": "10:00:00",
            "DisplayDate": "01/10/2026",
            "DayGroup": "x",
            "FaviconUrl": "pack://application:,,,/Assets/logo.png"
          }
        ]
        """);
        using var s = NewStore();
        var e = Assert.Single(s.GetEntries());
        Assert.Equal("Viejo", e.Title);
    }

    [Fact]
    public void Corrupt_file_does_not_throw_and_starts_empty()
    {
        File.WriteAllText(_file, "{ esto no es json");
        using var s = NewStore();
        Assert.Equal(0, s.GetTotalCount());
        s.AddEntry("https://a.com/", "A");
        Assert.Equal(1, s.GetTotalCount());
    }

    [Fact]
    public void Dispose_flushes_pending_changes()
    {
        var s = NewStore();
        s.AddEntry("https://a.com/", "A");
        s.Dispose();
        Assert.True(File.Exists(_file));
    }

    // ── Borrados (inmediatos) ────────────────────────────────────────────
    [Fact]
    public void Deletes_persist_immediately()
    {
        using var s = NewStore();
        s.AddEntry("https://a.com/", "A");
        _now = _now.AddMinutes(1);
        s.AddEntry("https://b.com/", "B");
        s.Flush();

        s.DeleteByDomain("a.com");
        using var check = NewStore();
        Assert.Equal(new[] { "b.com" }, check.GetEntries().Select(e => e.Domain));

        s.ClearAll();
        Assert.Equal("[]", File.ReadAllText(_file));
    }

    [Fact]
    public void DeleteEntry_and_DeleteEntries_remove_by_id()
    {
        using var s = NewStore();
        for (int i = 0; i < 4; i++) { _now = _now.AddMinutes(1); s.AddEntry($"https://a.com/{i}", "t"); }
        var all = s.GetEntries();
        s.DeleteEntry(all[0].Id);
        s.DeleteEntries(new[] { all[1].Id, all[2].Id });
        Assert.Single(s.GetEntries());
    }

    [Fact]
    public void DeleteByDay_and_TimeRange_work()
    {
        using var s = NewStore();
        s.AddEntry("https://a.com/1", "d1");
        _now = _now.AddDays(1);
        s.AddEntry("https://b.com/2", "d2");
        s.DeleteByDay(new DateTime(2026, 11, 1));
        Assert.Equal("d2", Assert.Single(s.GetEntries()).Title);

        s.DeleteByTimeRange(_now.AddHours(-1), _now.AddHours(1), "otro.com");
        Assert.Equal(1, s.GetTotalCount());
        s.DeleteByTimeRange(_now.AddHours(-1), _now.AddHours(1), "Todos");
        Assert.Equal(0, s.GetTotalCount());
    }

    // ── Consultas ─────────────────────────────────────────────────────────
    [Fact]
    public void GetEntries_filters_by_text_domain_and_dates()
    {
        using var s = NewStore();
        s.AddEntry("https://youtube.com/a", "Gatitos");
        _now = _now.AddDays(1);
        s.AddEntry("https://github.com/b", "Repositorio");

        Assert.Single(s.GetEntries(filterText: "gatit"));
        Assert.Single(s.GetEntries(domain: "github.com"));
        Assert.Equal(2, s.GetEntries(domain: "Todos").Count);
        Assert.Single(s.GetEntries(fromDate: _now.AddHours(-1)));
        Assert.Single(s.GetEntries(toDate: _now.AddHours(-1)));
    }

    [Fact]
    public void GetDistinctDomains_counts_and_sorts()
    {
        using var s = NewStore();
        for (int i = 0; i < 3; i++) { _now = _now.AddMinutes(1); s.AddEntry($"https://a.com/{i}", "t"); }
        _now = _now.AddMinutes(1);
        s.AddEntry("https://b.com/", "t");
        var d = s.GetDistinctDomains();
        Assert.Equal("a.com", d[0].Domain);
        Assert.Equal(3, d[0].Count);
    }

    [Fact]
    public void FileSize_text_reflects_state()
    {
        using var s = NewStore();
        Assert.Equal("0 KB", s.GetFileSizeFormatted());
        s.AddEntry("https://a.com/", "A");
        s.Flush();
        Assert.EndsWith("B", s.GetFileSizeFormatted());
        _enabled = false;
        Assert.Contains("desactivado", s.GetFileSizeFormatted());
    }

    [Fact]
    public void HistoryChanged_event_fires_on_changes()
    {
        using var s = NewStore();
        int count = 0;
        s.HistoryChanged += () => count++;
        s.AddEntry("https://a.com/", "A");
        s.ClearAll();
        Assert.Equal(2, count);
    }

    [Fact]
    public void Concurrent_adds_do_not_corrupt_state()
    {
        using var s = NewStore(TimeSpan.FromMilliseconds(20));
        Parallel.For(0, 200, i => s.AddEntry($"https://site{i % 20}.com/{i}", "t"));
        s.Flush();
        using var again = NewStore();
        Assert.Equal(200, again.GetTotalCount());
        JsonDocument.Parse(File.ReadAllText(_file)); // JSON válido
    }

    // ── ExtractRootDomain ────────────────────────────────────────────────
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=1", "youtube.com")]
    [InlineData("https://m.youtube.com/", "youtube.com")]
    [InlineData("https://music.youtube.com/", "youtube.com")]
    [InlineData("https://www.google.com.pe/search", "google.com.pe")]
    [InlineData("https://foo.bar.com.pe/", "bar.com.pe")]
    [InlineData("https://www.bbc.co.uk/news", "bbc.co.uk")]
    [InlineData("https://example.co.uk/", "example.co.uk")]
    [InlineData("http://localhost:3000/", "localhost")]
    [InlineData("http://192.168.1.10:8080/x", "192.168.1.10")]
    [InlineData("http://[::1]:5000/", "[::1]")]
    [InlineData("no es una url", "")]
    [InlineData("", "")]
    public void ExtractRootDomain_cases(string url, string expected) =>
        Assert.Equal(expected, HistoryStore.ExtractRootDomain(url));
}

