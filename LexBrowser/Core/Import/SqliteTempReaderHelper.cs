using System;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace LexBrowser.Core.Import;

/// <summary>
/// Copia bases de datos SQLite posiblemente bloqueadas por otro navegador a un temporal efímero de solo lectura.
/// </summary>
internal static class SqliteTempReaderHelper
{
    private static int _initialized;

    public static void EnsureSqliteInitialized()
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) == 0)
        {
            SQLitePCL.Batteries_V2.Init();
        }
    }

    public static TempSqliteDatabase OpenReadOnlyCopy(string sourceDbPath, CancellationToken cancellationToken = default)
    {
        EnsureSqliteInitialized();
        cancellationToken.ThrowIfCancellationRequested();

        string tempPath = Path.Combine(Path.GetTempPath(), $"lex_imp_{Guid.NewGuid():N}.db");
        try
        {
            using (var src = new FileStream(sourceDbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var dst = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                src.CopyTo(dst);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = tempPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            conn.Open();

            return new TempSqliteDatabase(conn, tempPath);
        }
        catch
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
    }

    internal static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch
        {
            // Limpieza no crítica en temp
        }
    }

    internal sealed class TempSqliteDatabase : IDisposable
    {
        public SqliteConnection Connection { get; }
        private readonly string _tempPath;
        private bool _disposed;

        public TempSqliteDatabase(SqliteConnection connection, string tempPath)
        {
            Connection = connection;
            _tempPath = tempPath;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                Connection.Close();
                SqliteConnection.ClearPool(Connection);
                Connection.Dispose();
            }
            catch
            {
                // Limpieza no crítica
            }
            finally
            {
                TryDeleteTempFile(_tempPath);
            }
        }
    }
}

