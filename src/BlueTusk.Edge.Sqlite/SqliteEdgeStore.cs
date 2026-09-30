using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace BlueTusk.Edge.Sqlite;

/// <summary>File-backed SQLite cache and durable write queue. Every state transition uses one immediate transaction.</summary>
public sealed partial class SqliteEdgeStore : IEdgeChainedLocalStore
{
    public const int CurrentSchemaVersion = 3;
    private readonly string _connectionString;

    public SqliteEdgeStore(SqliteEdgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Options = options;
        var path = Path.GetFullPath(options.DatabasePath);
        if (options.DatabasePath is ":memory:" || options.DatabasePath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Edge requires an ordinary persistent database file path.", nameof(options));
        }

        DatabasePath = path;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = options.BusyTimeoutSeconds,
        }.ToString();
    }

    public SqliteEdgeOptions Options { get; }
    public string DatabasePath { get; }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var pragma = Command(connection, null, "PRAGMA journal_mode=WAL"))
        {
            _ = await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        using var transaction = connection.BeginTransaction(deferred: false);
        await using (var metadata = Command(connection, transaction, "CREATE TABLE IF NOT EXISTS schema_metadata(singleton INTEGER PRIMARY KEY CHECK(singleton=1),version INTEGER NOT NULL); INSERT OR IGNORE INTO schema_metadata VALUES(1,3)"))
        {
            _ = await metadata.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var metadata = Command(connection, transaction, "SELECT version FROM schema_metadata WHERE singleton=1"))
        {
            var version = Convert.ToInt32(await metadata.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (version is < 1 or > CurrentSchemaVersion)
            {
                throw new InvalidOperationException("The SQLite Edge durable schema version is unsupported.");
            }
        }

        await using (var command = Command(connection, transaction, """
            CREATE TABLE IF NOT EXISTS scopes (
                tenant TEXT NOT NULL, scope_id TEXT NOT NULL, epoch INTEGER NOT NULL CHECK(epoch>0),
                checkpoint INTEGER NOT NULL DEFAULT 0 CHECK(checkpoint>=0), ready INTEGER NOT NULL DEFAULT 0,
                snapshot_id TEXT, snapshot_position INTEGER, PRIMARY KEY(tenant,scope_id));
            CREATE TABLE IF NOT EXISTS records (
                tenant TEXT NOT NULL,scope_id TEXT NOT NULL,epoch INTEGER NOT NULL,document_id TEXT NOT NULL,
                revision INTEGER NOT NULL CHECK(revision>0),payload BLOB NOT NULL,deleted INTEGER NOT NULL,
                fingerprint TEXT NOT NULL,PRIMARY KEY(tenant,scope_id,epoch,document_id));
            CREATE TABLE IF NOT EXISTS snapshot_records (
                tenant TEXT NOT NULL,scope_id TEXT NOT NULL,epoch INTEGER NOT NULL,snapshot_id TEXT NOT NULL,
                document_id TEXT NOT NULL,revision INTEGER NOT NULL CHECK(revision>0),payload BLOB NOT NULL,
                deleted INTEGER NOT NULL,fingerprint TEXT NOT NULL,
                PRIMARY KEY(tenant,scope_id,epoch,snapshot_id,document_id));
            CREATE TABLE IF NOT EXISTS mutations (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                tenant TEXT NOT NULL,scope_id TEXT NOT NULL,epoch INTEGER NOT NULL,mutation_id TEXT NOT NULL,
                document_id TEXT NOT NULL,expected_revision INTEGER NOT NULL CHECK(expected_revision>=0),kind INTEGER NOT NULL,
                payload BLOB NOT NULL,fingerprint TEXT NOT NULL,status INTEGER NOT NULL DEFAULT 0,
                fence INTEGER NOT NULL DEFAULT 0,lease_until INTEGER,
                UNIQUE(tenant,scope_id,epoch,mutation_id),UNIQUE(tenant,scope_id,epoch,document_id));
            CREATE INDEX IF NOT EXISTS mutations_claim ON mutations(tenant,scope_id,epoch,status,sequence);
            CREATE TABLE IF NOT EXISTS receipts (
                tenant TEXT NOT NULL,scope_id TEXT NOT NULL,epoch INTEGER NOT NULL,mutation_id TEXT NOT NULL,
                fingerprint TEXT NOT NULL,outcome INTEGER NOT NULL,outcome_fingerprint TEXT NOT NULL,
                recorded_at INTEGER NOT NULL,resolved_by TEXT,PRIMARY KEY(tenant,scope_id,epoch,mutation_id));
            CREATE INDEX IF NOT EXISTS receipts_age ON receipts(recorded_at);
            CREATE TABLE IF NOT EXISTS ordered_streams (
                tenant TEXT NOT NULL,scope_id TEXT NOT NULL,epoch INTEGER NOT NULL,stream_id TEXT NOT NULL,
                next_sequence INTEGER NOT NULL CHECK(next_sequence>0),horizon INTEGER NOT NULL DEFAULT 0 CHECK(horizon>=0),
                PRIMARY KEY(tenant,scope_id,epoch));
            CREATE TABLE IF NOT EXISTS ordered_confirmations (
                tenant TEXT NOT NULL,scope_id TEXT NOT NULL,epoch INTEGER NOT NULL,stream_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,mutation_id TEXT NOT NULL,document_id TEXT NOT NULL,
                expected_revision INTEGER NOT NULL,kind INTEGER NOT NULL,payload BLOB NOT NULL,
                fingerprint TEXT NOT NULL,confirmed INTEGER NOT NULL DEFAULT 0 CHECK(confirmed IN(0,1)),
                PRIMARY KEY(tenant,scope_id,epoch,stream_id,sequence),UNIQUE(tenant,scope_id,epoch,mutation_id));
            CREATE INDEX IF NOT EXISTS ordered_confirmations_next ON ordered_confirmations(tenant,scope_id,epoch,stream_id,confirmed,sequence);
            UPDATE schema_metadata SET version=3 WHERE singleton=1;
            """))
        {
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ActivateScopeAsync(EdgeScope scope, EdgeEpochChangePolicy policy = EdgeEpochChangePolicy.RejectIfPending, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        long? oldEpoch = null;
        await using (var command = Command(connection, transaction, "SELECT epoch FROM scopes WHERE tenant=@tenant AND scope_id=@scope"))
        {
            ScopeParameters(command, scope, includeEpoch: false);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is not null)
            {
                oldEpoch = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }
        }

        if (oldEpoch > scope.Epoch)
        {
            throw new EdgeScopeMismatchException();
        }

        if (oldEpoch == scope.Epoch)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (oldEpoch is null)
        {
            await using var capacity = Command(connection, transaction, "SELECT count(*) FROM scopes");
            if (Convert.ToInt64(await capacity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) >= Options.MaxScopes)
            {
                throw new EdgeCapacityException("The local store has reached its scope budget.");
            }
        }

        if (oldEpoch is not null)
        {
            await using (var command = Command(connection, transaction, "SELECT count(*) FROM mutations WHERE tenant=@tenant AND scope_id=@scope"))
            {
                ScopeParameters(command, scope, includeEpoch: false);
                if (policy is EdgeEpochChangePolicy.RejectIfPending && Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0)
                {
                    throw new EdgeRevisionConflictException();
                }
            }

            foreach (var table in new[] { "records", "snapshot_records", "mutations", "receipts", "ordered_confirmations", "ordered_streams" })
            {
                await using var command = Command(connection, transaction, $"DELETE FROM {table} WHERE tenant=@tenant AND scope_id=@scope");
                ScopeParameters(command, scope, includeEpoch: false);
                _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await using (var command = Command(connection, transaction, "INSERT INTO scopes(tenant,scope_id,epoch) VALUES(@tenant,@scope,@epoch) ON CONFLICT(tenant,scope_id) DO UPDATE SET epoch=excluded.epoch,checkpoint=0,ready=0,snapshot_id=NULL,snapshot_position=NULL"))
        {
            ScopeParameters(command, scope);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<EdgeCheckpoint> GetCheckpointAsync(EdgeScope scope, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await CheckScopeAsync(connection, null, scope, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<EdgeCachedDocument?> GetAsync(EdgeScope scope, string id, CancellationToken cancellationToken = default)
    {
        EdgeValidation.Key(id, nameof(id), 512);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        _ = await CheckScopeAsync(connection, transaction, scope, cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, transaction, CacheSql("AND k.document_id=@id", page: false));
        ScopeParameters(command, scope);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadCached(reader) : null;
    }

    public async ValueTask<EdgeCachePage> ReadPageAsync(EdgeScope scope, int pageSize = 100, string? afterId = null, CancellationToken cancellationToken = default)
    {
        if (pageSize < 1 || pageSize > Options.MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        if (afterId is not null)
        {
            EdgeValidation.Key(afterId, nameof(afterId), 512);
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        _ = await CheckScopeAsync(connection, transaction, scope, cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, transaction, CacheSql("AND (@after IS NULL OR k.document_id>@after) AND COALESCE(CASE WHEN m.mutation_id IS NOT NULL THEN m.kind ELSE r.deleted END,0)=0", page: true));
        ScopeParameters(command, scope);
        command.Parameters.AddWithValue("after", (object?)afterId ?? DBNull.Value);
        command.Parameters.AddWithValue("count", pageSize + 1);
        command.Parameters.AddWithValue("bytes", Options.MaxPageBytes);
        var items = new List<EdgeCachedDocument>(pageSize);
        var more = false;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (items.Count == pageSize || reader.IsDBNull(2))
            {
                more = true;
                break;
            }

            items.Add(ReadCached(reader));
        }

        if (more && items.Count == 0)
        {
            throw new EdgeCapacityException("A cached document exceeds the page byte budget.");
        }

        return new EdgeCachePage(items.AsReadOnly(), more ? items[^1].Id : null);
    }

    private static string CacheSql(string predicate, bool page)
    {
        var sql = $"""
            WITH keys AS (
                SELECT document_id FROM records WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch
                UNION SELECT document_id FROM mutations WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch),
            selected AS (
                SELECT k.document_id,COALESCE(r.revision,0) AS revision,
                    CASE WHEN m.mutation_id IS NOT NULL THEN m.payload ELSE r.payload END AS payload,
                    CASE WHEN m.mutation_id IS NOT NULL THEN m.kind ELSE r.deleted END AS deleted,
                    m.mutation_id,m.status
                FROM keys k
                LEFT JOIN records r ON r.tenant=@tenant AND r.scope_id=@scope AND r.epoch=@epoch AND r.document_id=k.document_id
                LEFT JOIN mutations m ON m.tenant=@tenant AND m.scope_id=@scope AND m.epoch=@epoch AND m.document_id=k.document_id
                WHERE 1=1 {predicate} ORDER BY k.document_id
            """;
        return page
            ? sql + " LIMIT @count),bounded AS(SELECT *,sum(length(payload)) OVER(ORDER BY document_id) AS total_bytes FROM selected) SELECT document_id,revision,CASE WHEN total_bytes<=@bytes THEN payload ELSE NULL END,deleted,mutation_id,status FROM bounded ORDER BY document_id"
            : sql + ") SELECT document_id,revision,payload,deleted,mutation_id,status FROM selected";
    }

    private static EdgeCachedDocument ReadCached(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetInt64(1), (byte[])reader.GetValue(2), reader.GetInt32(3) != 0,
        reader.IsDBNull(4) ? null : Guid.ParseExact(reader.GetString(4), "N"),
        reader.IsDBNull(5) ? null : (EdgeMutationStatus)reader.GetInt32(5));

    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = Command(connection, null, "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL");
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void ScopeParameters(SqliteCommand command, EdgeScope scope, bool includeEpoch = true)
    {
        ArgumentNullException.ThrowIfNull(scope);
        command.Parameters.AddWithValue("tenant", scope.Tenant);
        command.Parameters.AddWithValue("scope", scope.Id);
        if (includeEpoch)
        {
            command.Parameters.AddWithValue("epoch", scope.Epoch);
        }
    }

    private static async ValueTask<EdgeCheckpoint> CheckScopeAsync(SqliteConnection connection, SqliteTransaction? transaction, EdgeScope scope, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT checkpoint,ready FROM scopes WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch");
        ScopeParameters(command, scope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new EdgeScopeMismatchException();
        }

        return new EdgeCheckpoint(reader.GetInt64(0), reader.GetInt32(1) != 0);
    }

    private void ValidateRecords(IReadOnlyList<EdgeRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count > Options.MaxBatchRecords)
        {
            throw new EdgeCapacityException("Edge batch exceeds the record count budget.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        long bytes = 0;
        foreach (var record in records)
        {
            ArgumentNullException.ThrowIfNull(record);
            if (record.Payload.Length > Options.MaxRecordBytes || !ids.Add(record.Id))
            {
                throw new EdgeCapacityException("Edge batch has an oversized payload or duplicate document key.");
            }

            bytes = checked(bytes + record.Payload.Length + Encoding.UTF8.GetByteCount(record.Id));
        }

        if (bytes > Options.MaxBatchBytes)
        {
            throw new EdgeCapacityException("Edge batch exceeds its byte budget.");
        }
    }

    private static string RecordFingerprint(EdgeRecord record) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(record.Deleted + ":" + Convert.ToBase64String(record.Payload.Span))));

    private async ValueTask CheckCacheBudgetAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT (SELECT count(*) FROM records),(SELECT COALESCE(sum(length(payload)),0) FROM records),(SELECT count(*) FROM snapshot_records),(SELECT COALESCE(sum(length(payload)),0) FROM snapshot_records)");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (reader.GetInt64(0) > Options.MaxCacheRecords || reader.GetInt64(1) > Options.MaxCacheBytes ||
            reader.GetInt64(2) > Options.MaxStagedRecords || reader.GetInt64(3) > Options.MaxStagedBytes)
        {
            throw new EdgeCapacityException("The bounded active cache or snapshot staging area is full; narrow the authorized scope.");
        }
    }
}
