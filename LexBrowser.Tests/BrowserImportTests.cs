using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LexBrowser.Core;
using LexBrowser.Core.Import;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LexBrowser.Tests;

public sealed class BrowserImportTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "lex_imp_test_" + Guid.NewGuid().ToString("N"));

    public BrowserImportTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── 1. Conversión de tiempo Chromium ────────────────────────────────────

    [Theory]
    [InlineData(0L)]
    [InlineData(-100L)]
    [InlineData(long.MaxValue)]
    public void ChromiumTimestamp_InvalidOrOutOfRange_ReturnsFalse(long rawUs)
    {
        bool ok = ChromiumHistoryImporter.TryConvertChromiumTimestamp(rawUs, out _);
        Assert.False(ok);
    }

    [Fact]
    public void ChromiumTimestamp_ValidAndMaxBoundary_ConvertsToExactUtc()
    {
        var expected = new DateTime(2026, 11, 10, 18, 30, 0, DateTimeKind.Utc);
        long chromiumUs = expected.ToFileTimeUtc() / 10L;

        Assert.True(ChromiumHistoryImporter.TryConvertChromiumTimestamp(chromiumUs, out var actual));
        Assert.Equal(expected, actual);
        Assert.Equal(DateTimeKind.Utc, actual.Kind);

        long maxValidUs = DateTime.MaxValue.ToFileTimeUtc() / 10L;
        Assert.True(ChromiumHistoryImporter.TryConvertChromiumTimestamp(maxValidUs, out _));
        Assert.False(ChromiumHistoryImporter.TryConvertChromiumTimestamp(maxValidUs + 1, out _));
    }

    // ── 2. Parser JSON de marcadores Chromium (carpetas anidadas) ──────────

    [Fact]
    public void ParseBookmarksJson_FlattensNestedFolders_AndFiltersOnlyHttpHttps()
    {
        const string json = """
        {
          "roots": {
            "bookmark_bar": {
              "type": "folder",
              "name": "Barra",
              "children": [
                { "type": "url", "name": "Google", "url": "https://www.google.com/" },
                {
                  "type": "folder",
                  "name": "Trabajo",
                  "children": [
                    { "type": "url", "name": "Docs", "url": "http://docs.internal.local/guide" },
                    { "type": "url", "name": "Settings", "url": "chrome://settings" }
                  ]
                }
              ]
            },
            "other": {
              "type": "folder",
              "name": "Otros",
              "children": [
                { "type": "url", "name": "JS", "url": "javascript:alert(1)" },
                { "type": "url", "name": "GitHub", "url": "https://github.com/trending" }
              ]
            },
            "synced": {
              "type": "folder",
              "name": "Móvil",
              "children": [
                { "type": "url", "name": "Wiki", "url": "https://es.wikipedia.org/" }
              ]
            }
          }
        }
        """;

        var (items, skipped, error) = ChromiumBookmarkImporter.ParseBookmarksJson(json);

        Assert.Null(error);
        Assert.Equal(2, skipped);
        Assert.Equal(4, items.Count);
        Assert.Contains(items, b => b.Title == "Google" && b.Url == "https://www.google.com/");
        Assert.Contains(items, b => b.Title == "Docs" && b.Url == "http://docs.internal.local/guide");
        Assert.Contains(items, b => b.Title == "GitHub" && b.Url == "https://github.com/trending");
        Assert.Contains(items, b => b.Title == "Wiki" && b.Url == "https://es.wikipedia.org/");
    }

    // ── 3. Parser CSV RFC 4180 ──────────────────────────────────────────────

    [Fact]
    public void CsvPasswordImporter_ParsesQuotesCommasEscapedQuotesAndNewlines()
    {
        string csv = "\uFEFFNAME,URL,Username,PASSWORD\r\n" +
                     "\"Banco, Principal\",https://banco.example.com,\"jhair@example.com\",\"pa\"\"ss,123\"\r\n" +
                     "\"Nota\nMultilinea\",https://nota.example.org,admin,\"line1\nline2\"\r\n" +
                     "Incompleto,https://sinpass.example.com,user,\r\n";

        var (entries, skipped, error) = CsvPasswordImporter.ParseCsv(csv);

        Assert.Null(error);
        Assert.Equal(1, skipped);
        Assert.Equal(2, entries.Count);

        Assert.Equal("https://banco.example.com", entries[0].Url);
        Assert.Equal("jhair@example.com", entries[0].Username);
        Assert.Equal("pa\"ss,123", entries[0].Password);

        Assert.Equal("https://nota.example.org", entries[1].Url);
        Assert.Equal("admin", entries[1].Username);
        Assert.Equal("line1\nline2", entries[1].Password);
    }

    // ── 4. Cifrado Chromium AES-GCM (v10/v11) y reporte de v20 ──────────────

    [Theory]
    [InlineData("v10")]
    [InlineData("v11")]
    public void TryDecryptPasswordBlob_RoundTripAesGcm_WorksForV10AndV11(string prefix)
    {
        byte[] key = new byte[32];
        byte[] nonce = new byte[12];
        for (int i = 0; i < key.Length; i++) key[i] = (byte)(i + 1);
        for (int i = 0; i < nonce.Length; i++) nonce[i] = (byte)(100 + i);

        const string expected = "ClaveSegura#2026!";
        byte[] plain = Encoding.UTF8.GetBytes(expected);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[16];

        using (var aes = new AesGcm(key, 16))
        {
            aes.Encrypt(nonce, plain, cipher, tag);
        }

        byte[] blob = new byte[3 + nonce.Length + cipher.Length + tag.Length];
        Encoding.ASCII.GetBytes(prefix).CopyTo(blob, 0);
        nonce.CopyTo(blob, 3);
        cipher.CopyTo(blob, 15);
        tag.CopyTo(blob, 15 + cipher.Length);

        bool ok = ChromiumPasswordImporter.TryDecryptPasswordBlob(blob, key, out string decrypted, out bool isV20);

        Assert.True(ok);
        Assert.False(isV20);
        Assert.Equal(expected, decrypted);
    }

    [Fact]
    public void TryDecryptPasswordBlob_CountsV20AsUnsupportedAppBoundWithoutThrowing()
    {
        byte[] key = new byte[32];
        byte[] v20Blob = Encoding.ASCII.GetBytes("v20" + new string('X', 40));

        bool ok = ChromiumPasswordImporter.TryDecryptPasswordBlob(v20Blob, key, out string plaintext, out bool isV20);

        Assert.False(ok);
        Assert.True(isV20);
        Assert.Equal(string.Empty, plaintext);
    }

    // ── 5. SQLite winsqlite3 + HistoryStore.ImportEntries ───────────────────

    [Fact]
    public void ChromiumHistoryImporter_ReadsLockedSqliteCopy_AndHistoryStoreDeduplicatesAndRespectsSetting()
    {
        SQLitePCL.Batteries_V2.Init();
        string dbPath = Path.Combine(_tempDir, "History");

        var t1 = new DateTime(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 11, 5, 10, 5, 0, DateTimeKind.Utc);
        var t3 = new DateTime(2026, 11, 5, 10, 10, 0, DateTimeKind.Utc);

        using (var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE urls (id INTEGER PRIMARY KEY, url TEXT, title TEXT, last_visit_time INTEGER);" +
                              "INSERT INTO urls (url, title, last_visit_time) VALUES " +
                              "($u1, 'Sitio A', $t1), ($u1b, 'Sitio A Dupe', $t1ms), ($u2, 'Sitio B', $t2), ($u3, 'Sitio C', $t3), ('chrome://flags', 'Flags', $t3);";
            cmd.Parameters.AddWithValue("$u1", "https://uno.example.com/");
            cmd.Parameters.AddWithValue("$u1b", "https://uno.example.com/");
            cmd.Parameters.AddWithValue("$u2", "https://dos.example.com/");
            cmd.Parameters.AddWithValue("$u3", "https://tres.example.com/");
            cmd.Parameters.AddWithValue("$t1", t1.ToFileTimeUtc() / 10L);
            cmd.Parameters.AddWithValue("$t1ms", t1.AddMilliseconds(400).ToFileTimeUtc() / 10L);
            cmd.Parameters.AddWithValue("$t2", t2.ToFileTimeUtc() / 10L);
            cmd.Parameters.AddWithValue("$t3", t3.ToFileTimeUtc() / 10L);
            cmd.ExecuteNonQuery();
            SqliteConnection.ClearPool(conn);
        }

        // Simular otro navegador con el archivo abierto en lectura/escritura compartida
        List<(string Url, string Title, DateTime VisitedAt)> rows;
        int readSkipped;
        string? readErr;
        using (var lockHolder = new FileStream(dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            (rows, readSkipped, readErr) = ChromiumHistoryImporter.ReadFromDatabase(dbPath);
        }

        Assert.Null(readErr);
        Assert.Equal(1, readSkipped); // chrome://flags
        Assert.Equal(4, rows.Count);

        bool enabled = false;
        var now = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        string histJson = Path.Combine(_tempDir, "history.json");

        using (var disabledStore = new HistoryStore(histJson, () => enabled, () => now, TimeSpan.FromMinutes(10), maxEntries: 2))
        {
            var disabledRes = disabledStore.ImportEntries(rows);
            Assert.Equal(0, disabledRes.Imported);
            Assert.False(string.IsNullOrWhiteSpace(disabledRes.Warning));
            Assert.Equal(0, disabledStore.GetTotalCount());
        }

        enabled = true;
        using var store = new HistoryStore(histJson, () => enabled, () => now, TimeSpan.FromMinutes(10), maxEntries: 2);
        var res = store.ImportEntries(rows);

        Assert.Null(res.Warning);
        Assert.Equal(3, res.Imported); // 4 filas - 1 duplicada en mismo segundo
        Assert.Equal(1, res.Duplicates);
        Assert.Equal(2, store.GetTotalCount()); // respetó maxEntries=2 conservando los más recientes
        var kept = store.GetEntries();
        Assert.Equal("https://tres.example.com/", kept[0].Url);
        Assert.Equal("https://dos.example.com/", kept[1].Url);
    }

    // ── 6. PasswordVaultStore.ImportCredentials sin sobrescribir ni alterar exclusiones ─

    [Fact]
    public void PasswordVaultStore_ImportCredentials_DoesNotOverwriteDuplicates_NorMutateExcludedDomains()
    {
        string vaultPath = Path.Combine(_tempDir, "vault.dat");
        var store = new PasswordVaultStore(vaultPath);
        store.SaveOrUpdateCredential("https://github.com/login", "jhair", "ExistingPass!");
        store.AddExcludedDomain("https://banco-excluido.example.com");

        int vaultChangedCount = 0;
        store.VaultChanged += () => vaultChangedCount++;

        var res = store.ImportCredentials(new[]
        {
            ("https://login.github.com/", "JHAIR", "NewPassShouldNotOverwrite"),
            ("https://example.org/signin", "user1", "PassOne"),
            ("https://sub.example.org/auth", "user2", "PassTwo"),
            ("https://sub.example.org/auth", "user2", "PassTwoDup"),
            ("https://empty.example.org", "user3", "")
        });

        Assert.Null(res.Error);
        Assert.Equal(2, res.Imported);
        Assert.Equal(2, res.Duplicates);
        Assert.Equal(1, res.Skipped);
        Assert.Equal(1, vaultChangedCount); // Solo un VaultChanged para el lote completo

        var existing = store.FindExistingCredential("https://github.com", "jhair");
        Assert.NotNull(existing);
        Assert.Equal("ExistingPass!", existing!.Password);
        Assert.True(store.IsDomainExcluded("https://banco-excluido.example.com"));
        Assert.False(store.IsDomainExcluded("https://example.org"));
        Assert.Single(store.GetExcludedDomains());
    }

    // ── 7. BrowserProfileLocator (Chromium + Opera root profile) ───────────

    [Fact]
    public void BrowserProfileLocator_DiscoversMultiProfileAndOperaRoot()
    {
        string chromeUserData = Path.Combine(_tempDir, "ChromeUserData");
        string chromeDefault = Path.Combine(chromeUserData, "Default");
        string chromeP1 = Path.Combine(chromeUserData, "Profile 1");
        string operaStable = Path.Combine(_tempDir, "OperaStable");
        Directory.CreateDirectory(chromeDefault);
        Directory.CreateDirectory(chromeP1);
        Directory.CreateDirectory(operaStable);

        File.WriteAllText(Path.Combine(chromeDefault, "Bookmarks"), "{}");
        File.WriteAllText(Path.Combine(chromeP1, "History"), "");
        File.WriteAllText(Path.Combine(operaStable, "Login Data"), "");
        File.WriteAllText(Path.Combine(chromeUserData, "Local State"), """
        {
          "profile": {
            "info_cache": {
              "Default": { "name": "Personal" },
              "Profile 1": { "name": "Trabajo" }
            }
          }
        }
        """);

        var profiles = BrowserProfileLocator.DiscoverProfiles(new[]
        {
            new BrowserSpec("Google Chrome", chromeUserData),
            new BrowserSpec("Opera", operaStable, IsRootSingleProfile: true),
            new BrowserSpec("Missing", Path.Combine(_tempDir, "DoesNotExist"))
        });

        Assert.Equal(3, profiles.Count);
        Assert.Contains(profiles, p => p.BrowserName == "Google Chrome" && p.DisplayName == "Personal" && p.HasBookmarks);
        Assert.Contains(profiles, p => p.BrowserName == "Google Chrome" && p.DisplayName == "Trabajo" && p.HasHistory);
        Assert.Contains(profiles, p => p.BrowserName == "Opera" && p.HasPasswords && p.ProfileDirectory == operaStable);
    }
}
