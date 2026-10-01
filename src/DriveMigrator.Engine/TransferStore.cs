using System.Globalization;
using System.Text.Json;
using DriveMigrator.Core;
using DriveMigrator.Core.Transfers;
using Microsoft.Data.Sqlite;

namespace DriveMigrator.Engine;

/// <summary>
/// SQLite persistence for transfer jobs and their items, so jobs survive pauses, crashes and restarts.
/// A single connection is shared and serialized; the engine's writes are small and frequent.
/// </summary>
public sealed class TransferStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();
    private SqliteTransaction? _transaction;

    private TransferStore(SqliteConnection connection) => _connection = connection;

    public static TransferStore Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        var store = new TransferStore(connection);
        store.Execute("""
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;
            CREATE TABLE IF NOT EXISTS jobs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                created_at TEXT NOT NULL,
                title TEXT NOT NULL,
                source_provider TEXT NOT NULL,
                source_account TEXT NOT NULL,
                dest_provider TEXT NOT NULL,
                dest_account TEXT NOT NULL,
                options TEXT NOT NULL,
                targets TEXT NOT NULL,
                status INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS items (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                job_id INTEGER NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
                parent_id INTEGER,
                kind INTEGER NOT NULL,
                name TEXT NOT NULL,
                depth INTEGER NOT NULL,
                source TEXT,
                target_parent TEXT,
                target TEXT,
                status INTEGER NOT NULL,
                error TEXT,
                bytes INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS items_job_status ON items(job_id, status);
            """);
        store.AddColumnIfMissing("items", "details", "TEXT");
        store.AddColumnIfMissing("items", "failure", "INTEGER NOT NULL DEFAULT 0");
        store.AddColumnIfMissing("items", "resolution", "INTEGER");
        store.AddColumnIfMissing("items", "replace", "TEXT");
        return store;
    }

    /// <summary>Creates a job and its top-level items (one per selected item).</summary>
    public JobRecord CreateJob(string title, AccountRef source, AccountRef destination, TransferOptions options, IReadOnlyList<TransferTarget> targets, IEnumerable<NewItem> items)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        lock (_gate)
        {
            using var transaction = _transaction = _connection.BeginTransaction();
            try
            {
                var createdAt = DateTimeOffset.UtcNow;
                var jobId = ExecuteScalar<long>(
                    """
                    INSERT INTO jobs (created_at, title, source_provider, source_account, dest_provider, dest_account, options, targets, status)
                    VALUES ($created, $title, $sp, $sa, $dp, $da, $options, $targets, $status) RETURNING id;
                    """,
                    ("$created", createdAt.ToString("O", CultureInfo.InvariantCulture)),
                    ("$title", title),
                    ("$sp", source.ProviderId),
                    ("$sa", source.AccountId),
                    ("$dp", destination.ProviderId),
                    ("$da", destination.AccountId),
                    ("$options", JsonSerializer.Serialize(options, Json)),
                    ("$targets", JsonSerializer.Serialize(targets, Json)),
                    ("$status", (int)JobStatus.Running));
                InsertItems(jobId, parentId: null, depth: 0, items);
                transaction.Commit();
                return new JobRecord(jobId, createdAt, title, source, destination, options, targets, JobStatus.Running);
            }
            finally
            {
                _transaction = null;
            }
        }
    }

    public IReadOnlyList<JobRecord> LoadJobs()
    {
        lock (_gate)
        {
            using var command = Command("SELECT id, created_at, title, source_provider, source_account, dest_provider, dest_account, options, targets, status FROM jobs ORDER BY id");
            using var reader = command.ExecuteReader();
            var jobs = new List<JobRecord>();
            while (reader.Read())
            {
                jobs.Add(new JobRecord(
                    reader.GetInt64(0),
                    DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                    reader.GetString(2),
                    new AccountRef(reader.GetString(3), reader.GetString(4)),
                    new AccountRef(reader.GetString(5), reader.GetString(6)),
                    JsonSerializer.Deserialize<TransferOptions>(reader.GetString(7), Json)!,
                    JsonSerializer.Deserialize<List<TransferTarget>>(reader.GetString(8), Json)!,
                    (JobStatus)reader.GetInt32(9)));
            }

            return jobs;
        }
    }

    public void SetJobStatus(long jobId, JobStatus status)
        => Execute("UPDATE jobs SET status = $status WHERE id = $id", ("$status", (int)status), ("$id", jobId));

    public void DeleteJob(long jobId) => Execute("DELETE FROM jobs WHERE id = $id", ("$id", jobId));

    /// <summary>Items still to do. Items left in progress by an interrupted run are treated as pending.</summary>
    public IReadOnlyList<ItemRecord> LoadPendingItems(long jobId)
    {
        Execute("UPDATE items SET status = $pending WHERE job_id = $job AND status = $inProgress",
            ("$pending", (int)ItemStatus.Pending), ("$job", jobId), ("$inProgress", (int)ItemStatus.InProgress));
        return QueryItems("job_id = $job AND status = $pending ORDER BY depth, id", ("$job", jobId), ("$pending", (int)ItemStatus.Pending));
    }

    public IReadOnlyList<ItemRecord> LoadItems(long jobId, ItemStatus status)
        => QueryItems("job_id = $job AND status = $status ORDER BY id", ("$job", jobId), ("$status", (int)status));

    public void MarkInProgress(long itemId) => SetStatus(itemId, ItemStatus.InProgress, target: null, error: null, bytes: 0, details: null, FailureKind.Error);

    public void MarkFinished(
        long itemId,
        ItemStatus status,
        MigrationNode? target = null,
        string? error = null,
        long bytes = 0,
        string? details = null,
        FailureKind failure = FailureKind.Error)
        => SetStatus(itemId, status, target, error, bytes, details, failure);

    /// <summary>
    /// Records that a container was created (or found) at <paramref name="target"/> and adds its children, atomically,
    /// so an interruption can never lose the children of a finished container.
    /// </summary>
    public IReadOnlyList<ItemRecord> CompleteContainer(ItemRecord container, MigrationNode? target, IEnumerable<NewItem> children)
    {
        ArgumentNullException.ThrowIfNull(container);
        lock (_gate)
        {
            long? firstId;
            using (var transaction = _transaction = _connection.BeginTransaction())
            {
                try
                {
                    firstId = InsertItems(container.JobId, container.Id, container.Depth + 1, children);
                    SetStatusCore(container.Id, ItemStatus.Done, target, error: null, bytes: 0, details: null, FailureKind.Error);
                    transaction.Commit();
                }
                finally
                {
                    _transaction = null;
                }
            }

            return firstId is null
                ? []
                : QueryItemsCore("parent_id = $parent AND id >= $first ORDER BY id", ("$parent", container.Id), ("$first", firstId.Value));
        }
    }

    /// <summary>
    /// Makes items that failed with an ordinary error pending again so the next run retries them. Checksum
    /// mismatches are left alone: copying them again overwrites files, so they have their own actions
    /// (<see cref="ResetMismatched"/>, <see cref="SkipMismatched"/>).
    /// </summary>
    public int ResetFailed(long jobId)
        => Execute(
            "UPDATE items SET status = $pending, error = NULL, details = NULL WHERE job_id = $job AND status = $failed AND failure = $error",
            ("$pending", (int)ItemStatus.Pending), ("$job", jobId), ("$failed", (int)ItemStatus.Failed), ("$error", (int)FailureKind.Error));

    /// <summary>
    /// Makes items that failed with <paramref name="kind"/> pending again, to be copied with <paramref name="resolution"/>
    /// instead of the job's conflict policy. With <see cref="ConflictPolicy.Overwrite"/> the destination file they
    /// were compared with is replaced.
    /// </summary>
    public int ResetMismatched(long jobId, FailureKind kind, ConflictPolicy resolution)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(kind, FailureKind.Error);
        return Execute(
            """
            UPDATE items SET status = $pending, error = NULL, details = NULL, failure = $error, resolution = $resolution,
                replace = CASE WHEN $resolution = $overwrite THEN target ELSE NULL END
            WHERE job_id = $job AND status = $failed AND failure = $kind
            """,
            ("$pending", (int)ItemStatus.Pending), ("$error", (int)FailureKind.Error), ("$resolution", (int)resolution),
            ("$overwrite", (int)ConflictPolicy.Overwrite), ("$job", jobId), ("$failed", (int)ItemStatus.Failed), ("$kind", (int)kind));
    }

    /// <summary>Settles items that failed with <paramref name="kind"/> as skipped, e.g. when the user keeps the destination's files.</summary>
    public int SkipMismatched(long jobId, FailureKind kind, string reason)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(kind, FailureKind.Error);
        return Execute(
            "UPDATE items SET status = $skipped, error = $reason, details = NULL, failure = $error WHERE job_id = $job AND status = $failed AND failure = $kind",
            ("$skipped", (int)ItemStatus.Skipped), ("$reason", reason), ("$error", (int)FailureKind.Error),
            ("$job", jobId), ("$failed", (int)ItemStatus.Failed), ("$kind", (int)kind));
    }

    public JobCounts GetCounts(long jobId)
    {
        lock (_gate)
        {
            using var command = Command("SELECT status, COUNT(*), SUM(bytes) FROM items WHERE job_id = $job GROUP BY status", ("$job", jobId));
            using var reader = command.ExecuteReader();
            int pending = 0, done = 0, skipped = 0, failed = 0;
            long bytes = 0;
            while (reader.Read())
            {
                var count = reader.GetInt32(1);
                bytes += reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                switch ((ItemStatus)reader.GetInt32(0))
                {
                    case ItemStatus.Pending or ItemStatus.InProgress: pending += count; break;
                    case ItemStatus.Done: done += count; break;
                    case ItemStatus.Skipped: skipped += count; break;
                    case ItemStatus.Failed: failed += count; break;
                }
            }

            return new JobCounts(pending, done, skipped, failed, bytes);
        }
    }

    public void Dispose() => _connection.Dispose();

    private long? InsertItems(long jobId, long? parentId, int depth, IEnumerable<NewItem> items)
    {
        long? firstId = null;
        using var command = Command("""
            INSERT INTO items (job_id, parent_id, kind, name, depth, source, target_parent, status)
            VALUES ($job, $parent, $kind, $name, $depth, $source, $targetParent, $status) RETURNING id;
            """);
        var job = command.Parameters.AddWithValue("$job", jobId);
        var parent = command.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
        var kind = command.Parameters.Add("$kind", SqliteType.Integer);
        var name = command.Parameters.Add("$name", SqliteType.Text);
        command.Parameters.AddWithValue("$depth", depth);
        var source = command.Parameters.Add("$source", SqliteType.Text);
        var targetParent = command.Parameters.Add("$targetParent", SqliteType.Text);
        command.Parameters.AddWithValue("$status", (int)ItemStatus.Pending);

        foreach (var item in items)
        {
            kind.Value = (int)item.Kind;
            name.Value = item.Name;
            source.Value = ToJson(item.Source);
            targetParent.Value = ToJson(item.TargetParent);
            var id = (long)command.ExecuteScalar()!;
            firstId ??= id;
        }

        return firstId;
    }

    private void SetStatus(long itemId, ItemStatus status, MigrationNode? target, string? error, long bytes, string? details, FailureKind failure)
    {
        lock (_gate)
        {
            SetStatusCore(itemId, status, target, error, bytes, details, failure);
        }
    }

    private void SetStatusCore(long itemId, ItemStatus status, MigrationNode? target, string? error, long bytes, string? details, FailureKind failure)
    {
        using var command = Command(
            "UPDATE items SET status = $status, target = $target, error = $error, bytes = $bytes, details = $details, failure = $failure WHERE id = $id",
            ("$status", (int)status), ("$target", ToJson(target)), ("$error", (object?)error ?? DBNull.Value), ("$bytes", bytes),
            ("$details", (object?)details ?? DBNull.Value), ("$failure", (int)failure), ("$id", itemId));
        command.ExecuteNonQuery();
    }

    /// <summary>Schema upgrade for databases created by earlier versions.</summary>
    private void AddColumnIfMissing(string table, string column, string type)
    {
        lock (_gate)
        {
            using var info = Command($"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column", ("$column", column));
            if (Convert.ToInt32(info.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
            {
                using var alter = Command($"ALTER TABLE {table} ADD COLUMN {column} {type}");
                alter.ExecuteNonQuery();
            }
        }
    }

    private List<ItemRecord> QueryItems(string where, params (string Name, object Value)[] parameters)
    {
        lock (_gate)
        {
            return QueryItemsCore(where, parameters);
        }
    }

    private List<ItemRecord> QueryItemsCore(string where, params (string Name, object Value)[] parameters)
    {
        using var command = Command(
            $"SELECT id, job_id, parent_id, kind, name, depth, source, target_parent, status, target, error, bytes, details, failure, resolution, replace FROM items WHERE {where}",
            parameters);
        using var reader = command.ExecuteReader();
        var items = new List<ItemRecord>();
        while (reader.Read())
        {
            items.Add(new ItemRecord(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                (CapabilityKind)reader.GetInt32(3),
                reader.GetString(4),
                reader.GetInt32(5),
                FromJson(reader, 6),
                FromJson(reader, 7),
                (ItemStatus)reader.GetInt32(8),
                FromJson(reader, 9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.GetInt64(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                (FailureKind)reader.GetInt32(13),
                reader.IsDBNull(14) ? null : (ConflictPolicy)reader.GetInt32(14),
                FromJson(reader, 15)));
        }

        return items;
    }

    private static object ToJson(MigrationNode? node) => node is null ? DBNull.Value : JsonSerializer.Serialize(node, Json);

    private static MigrationNode? FromJson(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : JsonSerializer.Deserialize<MigrationNode>(reader.GetString(ordinal), Json);

    private int Execute(string sql, params (string Name, object Value)[] parameters)
    {
        lock (_gate)
        {
            using var command = Command(sql, parameters);
            return command.ExecuteNonQuery();
        }
    }

    private T ExecuteScalar<T>(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }

#pragma warning disable CA2100 // SQL text is built from constants in this class; values are always parameters.
    private SqliteCommand Command(string sql, params (string Name, object Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = _transaction;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }
#pragma warning restore CA2100
}
