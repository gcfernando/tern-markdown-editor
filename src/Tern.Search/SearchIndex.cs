using System.Globalization;
using Microsoft.Data.Sqlite;
using Tern.Workspaces;

namespace Tern.Search;

/// <summary>Thrown when the bundled SQLite cannot do full-text search, so the app can show a clear message instead of failing on first use.</summary>
public sealed class FtsUnavailableException : Exception
{
    public FtsUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>What is remembered about an indexed file so that unchanged files are skipped on the next run.</summary>
public sealed record IndexStamp(long MtimeTicks, long Size, string Hash);

/// <summary>One row to write. <see cref="RelativePath"/> uses forward slashes and is relative to the workspace root.</summary>
public sealed record IndexedDocument(string RelativePath, ExtractedDocument Content, IndexStamp Stamp);

public sealed record CorpusStats(int DocumentCount, double AverageTokenCount);

/// <summary>
/// The on-disk SQLite FTS5 index. It is a disposable cache: a version or workspace mismatch, or a corrupt file, makes
/// <see cref="Open"/> delete it and start over (see <see cref="WasRecreated"/>). One writer connection guarded by a lock;
/// each read opens its own connection so queries never block on, or interleave with, a write transaction (WAL).
/// </summary>
public sealed class SearchIndex : IDisposable
{
    public const int SchemaVersion = 1;

    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    private readonly Lock _writeGate = new();
    private readonly Lock _readGate = new();
    private readonly string _connectionString;
    private SqliteConnection? _writer;
    private SqliteConnection? _reader;
    private bool _disposed;

    private SearchIndex(string databasePath, string workspaceRoot)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        Root = WorkspacePaths.Normalize(workspaceRoot);
        // Pooling would keep file handles alive after Dispose, which blocks deleting the cache on Windows.
        _connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
    }

    public string DatabasePath { get; }

    public string Root { get; }

    /// <summary>True when an existing cache was discarded and rebuilt during <see cref="Open"/> (or <see cref="Reset"/>).</summary>
    public bool WasRecreated { get; private set; }

    public string? RecreationReason { get; private set; }

    /// <summary>Verifies that the bundled SQLite has FTS5 and bm25. Throws <see cref="FtsUnavailableException"/> otherwise.</summary>
    public static void EnsureFtsAvailable()
    {
        try
        {
            using var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = "CREATE VIRTUAL TABLE probe USING fts5(a, tokenize='unicode61 remove_diacritics 2')";
            create.ExecuteNonQuery();
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO probe(a) VALUES ('probe')";
            insert.ExecuteNonQuery();
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT bm25(probe) FROM probe WHERE probe MATCH 'probe'";
            _ = query.ExecuteScalar();
        }
        catch (SqliteException ex)
        {
            throw new FtsUnavailableException("Full-text search is unavailable: the bundled SQLite library was built without FTS5 support (" + ex.Message + ").", ex);
        }
    }

    public static SearchIndex Open(string databasePath, string workspaceRoot)
    {
        EnsureFtsAvailable();
        var index = new SearchIndex(databasePath, workspaceRoot);
        index.OpenOrRecreate();
        return index;
    }

    public static bool IsCorruption(SqliteException ex) =>
        ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase;

    /// <summary>Deletes everything and starts with an empty index. Used when corruption is detected after opening.</summary>
    public void Reset(string reason)
    {
        lock (_writeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CloseReader();
            CloseWriter();
            DeleteFiles();
            CreateFresh();
            WasRecreated = true;
            RecreationReason = reason;
        }
    }

    public int DocumentCount => Read(connection => Convert.ToInt32(Scalar(connection, "SELECT COUNT(*) FROM docs"), CultureInfo.InvariantCulture));

    public CorpusStats GetCorpusStats() => Read(ReadCorpusStats);

    internal static CorpusStats ReadCorpusStats(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), COALESCE(AVG(token_count), 0) FROM docs";
        using var reader = command.ExecuteReader();
        reader.Read();
        return new CorpusStats(reader.GetInt32(0), reader.GetDouble(1));
    }

    public IReadOnlyDictionary<string, IndexStamp> GetAllStamps() => Read(connection =>
    {
        var result = new Dictionary<string, IndexStamp>(WorkspacePaths.Comparer);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path, mtime_ticks, size, hash FROM docs";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = new IndexStamp(reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3));
        }

        return result;
    });

    public IndexStamp? GetStamp(string relativePath) => Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT mtime_ticks, size, hash FROM docs WHERE path = @path";
        command.Parameters.AddWithValue("@path", relativePath);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new IndexStamp(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2)) : null;
    });

    /// <summary>Applies upserts, removals of single files and removals of whole folders (prefix with trailing slash) in one transaction.</summary>
    public void Apply(IEnumerable<IndexedDocument> upserts, IEnumerable<string> removals, IEnumerable<string>? removedFolders = null)
    {
        lock (_writeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var connection = Writer();
            using var transaction = connection.BeginTransaction();
            foreach (var path in removals)
            {
                RemoveOne(connection, transaction, path);
            }

            foreach (var folder in removedFolders ?? [])
            {
                RemoveFolder(connection, transaction, folder.EndsWith('/') ? folder : folder + "/");
            }

            foreach (var document in upserts)
            {
                Upsert(connection, transaction, document);
            }

            transaction.Commit();
        }
    }

    /// <summary>Records a new mtime/size for a file whose content hash did not change, so it is not re-hashed next time.</summary>
    public void Touch(string relativePath, IndexStamp stamp)
    {
        lock (_writeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var command = Writer().CreateCommand();
            command.CommandText = "UPDATE docs SET mtime_ticks = @m, size = @s, hash = @h WHERE path = @p";
            command.Parameters.AddWithValue("@m", stamp.MtimeTicks);
            command.Parameters.AddWithValue("@s", stamp.Size);
            command.Parameters.AddWithValue("@h", stamp.Hash);
            command.Parameters.AddWithValue("@p", relativePath);
            command.ExecuteNonQuery();
        }
    }

    public void Clear()
    {
        lock (_writeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var connection = Writer();
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction, "DELETE FROM fts");
            Execute(connection, transaction, "DELETE FROM docs");
            transaction.Commit();
        }
    }

    // One long-lived reader: opening a WAL database costs tens of milliseconds, far more than a typical query.
    internal T Read<T>(Func<SqliteConnection, T> action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_readGate)
        {
            if (_reader is null)
            {
                var opened = new SqliteConnection(_connectionString);
                try
                {
                    opened.Open();
                }
                catch
                {
                    opened.Dispose();
                    throw;
                }

                _reader = opened;
            }

            try
            {
                return action(_reader);
            }
            catch (SqliteException)
            {
                CloseReader();
                throw;
            }
        }
    }

    private void CloseReader()
    {
        lock (_readGate)
        {
            _reader?.Dispose();
            _reader = null;
        }
    }

    public void Dispose()
    {
        lock (_writeGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CloseReader();
            CloseWriter();
        }
    }

    private SqliteConnection Writer()
    {
        if (_writer is not null)
        {
            return _writer;
        }

        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        _writer = connection;
        return connection;
    }

    private void CloseWriter()
    {
        _writer?.Dispose();
        _writer = null;
    }

    private void OpenOrRecreate()
    {
        lock (_writeGate)
        {
            if (!File.Exists(DatabasePath))
            {
                CreateFresh();
                return;
            }

            string? problem = null;
            try
            {
                problem = ValidateExisting();
            }
            catch (SqliteException ex) when (IsCorruption(ex) || ex.SqliteErrorCode == 1)
            {
                // Error 1 (generic) covers damaged headers that parse but whose schema pages are unreadable.
                problem = "The index file was corrupt: " + ex.Message;
            }

            if (problem is null)
            {
                return;
            }

            CloseReader();
            CloseWriter();
            DeleteFiles();
            CreateFresh();
            WasRecreated = true;
            RecreationReason = problem;
        }
    }

    private string? ValidateExisting()
    {
        var connection = Writer();
        Execute(connection, null, "PRAGMA journal_mode = WAL");
        var version = MetaValue(connection, "schema_version");
        if (version != SchemaVersion.ToString(CultureInfo.InvariantCulture))
        {
            return $"Index schema version {version ?? "(none)"} does not match {SchemaVersion}.";
        }

        var root = MetaValue(connection, "root");
        if (root is null || !WorkspacePaths.PathsEqual(root, Root))
        {
            return "The index belongs to a different workspace folder.";
        }

        // Touch both tables so damaged pages surface now rather than during the first search.
        _ = Scalar(connection, "SELECT COUNT(*) FROM docs");
        _ = Scalar(connection, "SELECT COUNT(*) FROM fts");
        return null;
    }

    private void CreateFresh()
    {
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = Writer();
        Execute(connection, null, "PRAGMA journal_mode = WAL");
        Execute(connection, null, "PRAGMA synchronous = NORMAL");
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)");
        Execute(connection, transaction, """
            CREATE TABLE docs (
                id INTEGER PRIMARY KEY,
                path TEXT NOT NULL UNIQUE,
                mtime_ticks INTEGER NOT NULL,
                size INTEGER NOT NULL,
                hash TEXT NOT NULL,
                title TEXT NOT NULL,
                tags TEXT NOT NULL,
                token_count INTEGER NOT NULL)
            """);
        Execute(connection, transaction, "CREATE VIRTUAL TABLE fts USING fts5(title, headings, tags, path, body, tokenize = 'unicode61 remove_diacritics 2', prefix = '2 3')");
        SetMeta(connection, transaction, "schema_version", SchemaVersion.ToString(CultureInfo.InvariantCulture));
        SetMeta(connection, transaction, "root", Root);
        transaction.Commit();
    }

    private void DeleteFiles()
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            var path = DatabasePath + suffix;
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Overwrite instead: an index we cannot delete must still not be trusted.
                if (suffix.Length == 0)
                {
                    File.WriteAllBytes(path, []);
                }
            }
        }
    }

    private static void Upsert(SqliteConnection connection, SqliteTransaction transaction, IndexedDocument document)
    {
        long id;
        using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = "SELECT id FROM docs WHERE path = @p";
            find.Parameters.AddWithValue("@p", document.RelativePath);
            id = find.ExecuteScalar() is long existing ? existing : -1;
        }

        var content = document.Content;
        var tags = string.Join(' ', content.Tags);
        if (id >= 0)
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM fts WHERE rowid = @id";
            delete.Parameters.AddWithValue("@id", id);
            delete.ExecuteNonQuery();

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE docs SET mtime_ticks = @m, size = @s, hash = @h, title = @t, tags = @g, token_count = @c WHERE id = @id";
            BindDoc(update, document, tags);
            update.Parameters.AddWithValue("@id", id);
            update.ExecuteNonQuery();
        }
        else
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO docs (path, mtime_ticks, size, hash, title, tags, token_count) VALUES (@p, @m, @s, @h, @t, @g, @c); SELECT last_insert_rowid()";
            insert.Parameters.AddWithValue("@p", document.RelativePath);
            BindDoc(insert, document, tags);
            id = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        using var fts = connection.CreateCommand();
        fts.Transaction = transaction;
        fts.CommandText = "INSERT INTO fts (rowid, title, headings, tags, path, body) VALUES (@id, @t, @hd, @g, @p, @b)";
        fts.Parameters.AddWithValue("@id", id);
        fts.Parameters.AddWithValue("@t", content.Title);
        fts.Parameters.AddWithValue("@hd", string.Join('\n', content.Headings));
        fts.Parameters.AddWithValue("@g", tags);
        fts.Parameters.AddWithValue("@p", document.RelativePath);
        fts.Parameters.AddWithValue("@b", content.Body);
        fts.ExecuteNonQuery();
    }

    private static void BindDoc(SqliteCommand command, IndexedDocument document, string tags)
    {
        command.Parameters.AddWithValue("@m", document.Stamp.MtimeTicks);
        command.Parameters.AddWithValue("@s", document.Stamp.Size);
        command.Parameters.AddWithValue("@h", document.Stamp.Hash);
        command.Parameters.AddWithValue("@t", document.Content.Title);
        command.Parameters.AddWithValue("@g", tags);
        command.Parameters.AddWithValue("@c", document.Content.TokenCount);
    }

    private static void RemoveOne(SqliteConnection connection, SqliteTransaction transaction, string path)
    {
        using var ids = connection.CreateCommand();
        ids.Transaction = transaction;
        ids.CommandText = "DELETE FROM fts WHERE rowid IN (SELECT id FROM docs WHERE path = @p)";
        ids.Parameters.AddWithValue("@p", path);
        ids.ExecuteNonQuery();

        using var docs = connection.CreateCommand();
        docs.Transaction = transaction;
        docs.CommandText = "DELETE FROM docs WHERE path = @p";
        docs.Parameters.AddWithValue("@p", path);
        docs.ExecuteNonQuery();
    }

    private static void RemoveFolder(SqliteConnection connection, SqliteTransaction transaction, string prefix)
    {
        using var ids = connection.CreateCommand();
        ids.Transaction = transaction;
        ids.CommandText = "DELETE FROM fts WHERE rowid IN (SELECT id FROM docs WHERE substr(path, 1, length(@p)) = @p)";
        ids.Parameters.AddWithValue("@p", prefix);
        ids.ExecuteNonQuery();

        using var docs = connection.CreateCommand();
        docs.Transaction = transaction;
        docs.CommandText = "DELETE FROM docs WHERE substr(path, 1, length(@p)) = @p";
        docs.Parameters.AddWithValue("@p", prefix);
        docs.ExecuteNonQuery();
    }

    private static string? MetaValue(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = @k";
        command.Parameters.AddWithValue("@k", key);
        return command.ExecuteScalar() as string;
    }

    private static void SetMeta(SqliteConnection connection, SqliteTransaction transaction, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES (@k, @v)";
        command.Parameters.AddWithValue("@k", key);
        command.Parameters.AddWithValue("@v", value);
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
