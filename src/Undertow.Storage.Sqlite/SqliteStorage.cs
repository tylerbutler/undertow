using Microsoft.Data.Sqlite;
using Undertow.Abstractions;

namespace Undertow.Storage.Sqlite;

/// <summary>
/// SQLite-backed document + git-object storage. WAL mode, synchronous=NORMAL,
/// busy_timeout 5000. The composite primary keys are the access paths
/// (ops by topic+sn, refs by tenant+path), both WITHOUT ROWID.
/// </summary>
public sealed class SqliteStorage : IDocumentStore, IGitObjectStore, IDisposable
{
    private readonly string _connectionString;
    // In-memory databases live only while at least one connection is open.
    private readonly SqliteConnection? _keepAlive;

    private SqliteStorage(string connectionString, SqliteConnection? keepAlive)
    {
        _connectionString = connectionString;
        _keepAlive = keepAlive;
    }

    /// <summary>Open (creating if missing) a database file.</summary>
    public static SqliteStorage OpenFile(string path)
    {
        var storage = new SqliteStorage($"Data Source={path}", keepAlive: null);
        storage.Initialize();
        return storage;
    }

    /// <summary>Open a private shared-cache in-memory database.</summary>
    public static SqliteStorage OpenInMemory()
    {
        var name = $"undertow-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={name};Mode=Memory;Cache=Shared";
        var keepAlive = new SqliteConnection(connectionString);
        keepAlive.Open();
        var storage = new SqliteStorage(connectionString, keepAlive);
        storage.Initialize();
        return storage;
    }

    public void Dispose()
    {
        _keepAlive?.Dispose();
        SqliteConnection.ClearAllPools();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;

            CREATE TABLE IF NOT EXISTS documents  (topic TEXT PRIMARY KEY, created_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS ops        (topic TEXT NOT NULL, sequence_number INTEGER NOT NULL,
                                                   payload TEXT NOT NULL,
                                                   PRIMARY KEY (topic, sequence_number)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS summaries  (topic TEXT PRIMARY KEY, handle TEXT NOT NULL,
                                                   sequence_number INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS checkpoints(topic TEXT PRIMARY KEY, sequence_number INTEGER NOT NULL,
                                                   minimum_sequence_number INTEGER NOT NULL,
                                                   version INTEGER NOT NULL,
                                                   updated_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS objects    (tenant TEXT NOT NULL, sha TEXT NOT NULL, body TEXT NOT NULL,
                                                   PRIMARY KEY (tenant, sha)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS refs       (tenant TEXT NOT NULL, path TEXT NOT NULL, sha TEXT NOT NULL,
                                                   PRIMARY KEY (tenant, path)) WITHOUT ROWID;
            """;
        command.ExecuteNonQuery();
    }

    // ── IDocumentStore ──────────────────────────────────────────────────────

    public ValueTask<bool> HasDocumentAsync(string topic, CancellationToken ct = default)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM documents WHERE topic = @topic";
        command.Parameters.AddWithValue("@topic", topic);
        return ValueTask.FromResult(command.ExecuteScalar() is not null);
    }

    public ValueTask CreateDocumentAsync(string topic, long createdAt, CancellationToken ct = default)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO documents (topic, created_at) VALUES (@topic, @createdAt) ON CONFLICT DO NOTHING";
        command.Parameters.AddWithValue("@topic", topic);
        command.Parameters.AddWithValue("@createdAt", createdAt);
        command.ExecuteNonQuery();
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<OpRecord>> GetOpsAsync(
        string topic, long fromExclusive, long? toInclusive, CancellationToken ct = default)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT sequence_number, payload FROM ops
            WHERE topic = @topic AND sequence_number > @from
              AND (@to IS NULL OR sequence_number <= @to)
            ORDER BY sequence_number
            """;
        command.Parameters.AddWithValue("@topic", topic);
        command.Parameters.AddWithValue("@from", fromExclusive);
        command.Parameters.AddWithValue("@to", (object?)toInclusive ?? DBNull.Value);

        var ops = new List<OpRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            ops.Add(new OpRecord(reader.GetInt64(0), reader.GetString(1)));
        return ValueTask.FromResult<IReadOnlyList<OpRecord>>(ops);
    }

    public ValueTask<long?> GetMaxOpSequenceNumberAsync(string topic, CancellationToken ct = default)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(sequence_number) FROM ops WHERE topic = @topic";
        command.Parameters.AddWithValue("@topic", topic);
        var result = command.ExecuteScalar();
        return ValueTask.FromResult(result is long max ? max : (long?)null);
    }

    public ValueTask<SummaryRecord?> GetSummaryAsync(string topic, CancellationToken ct = default)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT handle, sequence_number FROM summaries WHERE topic = @topic";
        command.Parameters.AddWithValue("@topic", topic);
        using var reader = command.ExecuteReader();
        return ValueTask.FromResult(
            reader.Read() ? new SummaryRecord(reader.GetString(0), reader.GetInt64(1)) : null);
    }

    public ValueTask PutSummaryAsync(string topic, SummaryRecord summary, CancellationToken ct = default)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO summaries (topic, handle, sequence_number)
            VALUES (@topic, @handle, @sn)
            ON CONFLICT (topic) DO UPDATE SET handle = @handle, sequence_number = @sn
            """;
        command.Parameters.AddWithValue("@topic", topic);
        command.Parameters.AddWithValue("@handle", summary.Handle);
        command.Parameters.AddWithValue("@sn", summary.SequenceNumber);
        command.ExecuteNonQuery();
        return ValueTask.CompletedTask;
    }

    public ValueTask<CheckpointRecord?> LoadCheckpointAsync(string topic, CancellationToken ct = default)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT sequence_number, minimum_sequence_number, version, updated_at
            FROM checkpoints WHERE topic = @topic
            """;
        command.Parameters.AddWithValue("@topic", topic);
        using var reader = command.ExecuteReader();
        return ValueTask.FromResult(
            reader.Read()
                ? new CheckpointRecord(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3))
                : null);
    }

    public ValueTask PruneOpsBelowAsync(string topic, long belowExclusive, CancellationToken ct = default)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ops WHERE topic = @topic AND sequence_number < @below";
        command.Parameters.AddWithValue("@topic", topic);
        command.Parameters.AddWithValue("@below", belowExclusive);
        command.ExecuteNonQuery();
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> CommitSequencedAsync(
        string topic, OpRecord[] ops, CheckpointRecord next, long expectedVersion,
        CancellationToken ct = default)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        // Advance the checkpoint under optimistic concurrency. A fresh document
        // has no row; expectedVersion 0 means "no other writer has been here".
        using var advance = connection.CreateCommand();
        advance.Transaction = transaction;
        advance.CommandText =
            """
            INSERT INTO checkpoints (topic, sequence_number, minimum_sequence_number, version, updated_at)
            VALUES (@topic, @sn, @msn, @version, @updatedAt)
            ON CONFLICT (topic) DO UPDATE
              SET sequence_number = @sn, minimum_sequence_number = @msn,
                  version = @version, updated_at = @updatedAt
              WHERE checkpoints.version = @expectedVersion
            """;
        advance.Parameters.AddWithValue("@topic", topic);
        advance.Parameters.AddWithValue("@sn", next.SequenceNumber);
        advance.Parameters.AddWithValue("@msn", next.MinimumSequenceNumber);
        advance.Parameters.AddWithValue("@version", next.Version);
        advance.Parameters.AddWithValue("@updatedAt", next.UpdatedAt);
        advance.Parameters.AddWithValue("@expectedVersion", expectedVersion);

        if (advance.ExecuteNonQuery() != 1)
        {
            transaction.Rollback();
            return ValueTask.FromResult(false);
        }

        foreach (var op in ops)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO ops (topic, sequence_number, payload) VALUES (@topic, @sn, @payload)
                ON CONFLICT (topic, sequence_number) DO UPDATE SET payload = @payload
                """;
            insert.Parameters.AddWithValue("@topic", topic);
            insert.Parameters.AddWithValue("@sn", op.SequenceNumber);
            insert.Parameters.AddWithValue("@payload", op.Payload);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
        return ValueTask.FromResult(true);
    }

    // ── IGitObjectStore ─────────────────────────────────────────────────────

    public ValueTask<string?> GetObjectAsync(string tenant, string sha, CancellationToken ct = default)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT body FROM objects WHERE tenant = @tenant AND sha = @sha";
        command.Parameters.AddWithValue("@tenant", tenant);
        command.Parameters.AddWithValue("@sha", sha);
        return ValueTask.FromResult(command.ExecuteScalar() as string);
    }

    public ValueTask PutObjectAsync(string tenant, string sha, string body, CancellationToken ct = default)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO objects (tenant, sha, body) VALUES (@tenant, @sha, @body)
            ON CONFLICT (tenant, sha) DO UPDATE SET body = @body
            """;
        command.Parameters.AddWithValue("@tenant", tenant);
        command.Parameters.AddWithValue("@sha", sha);
        command.Parameters.AddWithValue("@body", body);
        command.ExecuteNonQuery();
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyDictionary<string, string>> GetObjectsAsync(
        string tenant, IReadOnlyCollection<string> shas, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>();
        if (shas.Count == 0)
            return ValueTask.FromResult<IReadOnlyDictionary<string, string>>(result);

        using var connection = Open();
        // Batched WHERE sha IN (...) so historian tree walks stay O(depth)
        // round-trips instead of N+1 queries.
        foreach (var chunk in shas.Distinct().Chunk(500))
        {
            using var command = connection.CreateCommand();
            var parameters = chunk.Select((sha, i) =>
            {
                command.Parameters.AddWithValue($"@sha{i}", sha);
                return $"@sha{i}";
            });
            command.CommandText =
                $"SELECT sha, body FROM objects WHERE tenant = @tenant AND sha IN ({string.Join(",", parameters)})";
            command.Parameters.AddWithValue("@tenant", tenant);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                result[reader.GetString(0)] = reader.GetString(1);
        }

        return ValueTask.FromResult<IReadOnlyDictionary<string, string>>(result);
    }

    public ValueTask<string?> GetRefAsync(string tenant, string path, CancellationToken ct = default)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sha FROM refs WHERE tenant = @tenant AND path = @path";
        command.Parameters.AddWithValue("@tenant", tenant);
        command.Parameters.AddWithValue("@path", path);
        return ValueTask.FromResult(command.ExecuteScalar() as string);
    }

    public ValueTask<IReadOnlyList<KeyValuePair<string, string>>> ListRefsAsync(
        string tenant, CancellationToken ct = default)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path, sha FROM refs WHERE tenant = @tenant ORDER BY path";
        command.Parameters.AddWithValue("@tenant", tenant);
        var refs = new List<KeyValuePair<string, string>>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            refs.Add(new KeyValuePair<string, string>(reader.GetString(0), reader.GetString(1)));
        return ValueTask.FromResult<IReadOnlyList<KeyValuePair<string, string>>>(refs);
    }

    public ValueTask PutRefAsync(string tenant, string path, string sha, CancellationToken ct = default)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO refs (tenant, path, sha) VALUES (@tenant, @path, @sha)
            ON CONFLICT (tenant, path) DO UPDATE SET sha = @sha
            """;
        command.Parameters.AddWithValue("@tenant", tenant);
        command.Parameters.AddWithValue("@path", path);
        command.Parameters.AddWithValue("@sha", sha);
        command.ExecuteNonQuery();
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> TryCreateRefAsync(string tenant, string path, string sha, CancellationToken ct = default)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO refs (tenant, path, sha) VALUES (@tenant, @path, @sha) ON CONFLICT DO NOTHING";
        command.Parameters.AddWithValue("@tenant", tenant);
        command.Parameters.AddWithValue("@path", path);
        command.Parameters.AddWithValue("@sha", sha);
        return ValueTask.FromResult(command.ExecuteNonQuery() == 1);
    }
}
