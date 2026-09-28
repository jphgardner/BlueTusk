using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlueTusk.Search.OpenSearch;

public sealed record OpenSearchCursorStoreOptions
{
    public string Schema { get; init; } = "bluetusk_opensearch_cursors";
    public int MaxCandidates { get; init; } = 1000;
    public int MaxSnapshotBytes { get; init; } = 8 * 1024 * 1024;
    public int MaxSnapshotsPerTenantIndex { get; init; } = 128;
    public int MaxSnapshots { get; init; } = 4096;
    public long MaxRetainedBytes { get; init; } = 64L * 1024 * 1024;
    public int MaxConcurrentOperations { get; init; } = 8;
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(2);
    public int CommandTimeoutSeconds { get; init; } = 30;
}

/// <summary>Durable bounded rank snapshots, bound to physical target generation and authenticated permission scope.</summary>
public sealed class PostgreSqlOpenSearchCursorStore : IAsyncDisposable
{
    private readonly DbDataSource _source;
    private readonly SearchDataSourceOwnership _ownership;
    private readonly string _schema;
    private readonly SemaphoreSlim _operations;
    private int _disposed;

    public PostgreSqlOpenSearchCursorStore(DbDataSource source, OpenSearchCursorStoreOptions? options = null,
        SearchDataSourceOwnership ownership = SearchDataSourceOwnership.Borrowed)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(ownership)) { throw new ArgumentOutOfRangeException(nameof(ownership)); }
        Options = options ?? new OpenSearchCursorStoreOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(Options.Schema);
        if (Options.Schema.Contains('\0', StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(Options.Schema) > 63 ||
            Options.MaxCandidates is < 1 or > 10_000 || Options.MaxSnapshotBytes is < 1 or > 256 * 1024 * 1024 ||
            Options.MaxSnapshotsPerTenantIndex is < 1 or > 10_000 || Options.MaxSnapshots is < 1 or > 100_000 ||
            Options.MaxRetainedBytes < Options.MaxSnapshotBytes || Options.MaxConcurrentOperations is < 1 or > 256 ||
            Options.Lifetime <= TimeSpan.Zero || Options.Lifetime > TimeSpan.FromHours(1) || Options.CommandTimeoutSeconds is < 1 or > 300)
        { throw new ArgumentException("Cursor options exceed supported retention, byte or concurrency bounds.", nameof(options)); }
        _source = source; _ownership = ownership; _schema = '"' + Options.Schema.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
        _operations = new(Options.MaxConcurrentOperations, Options.MaxConcurrentOperations);
    }
    public OpenSearchCursorStoreOptions Options { get; }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (var guard = Command(connection, transaction, "SELECT pg_advisory_xact_lock(hashtextextended(@schema,0))"))
            { Parameter(guard, "schema", "BlueTusk.Search.OpenSearch.Cursors:" + Options.Schema); _ = await guard.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
            await using (var create = Command(connection, transaction, $"""
                CREATE SCHEMA IF NOT EXISTS {_schema};
                CREATE TABLE IF NOT EXISTS {_schema}.metadata(singleton boolean PRIMARY KEY DEFAULT true CHECK(singleton),version integer NOT NULL,max_snapshot_bytes integer NOT NULL,max_candidates integer NOT NULL,retained_count bigint NOT NULL DEFAULT 0,retained_bytes bigint NOT NULL DEFAULT 0,CHECK(retained_count>=0 AND retained_bytes>=0));
                INSERT INTO {_schema}.metadata(version,max_snapshot_bytes,max_candidates) VALUES(1,{Options.MaxSnapshotBytes.ToString(CultureInfo.InvariantCulture)},{Options.MaxCandidates.ToString(CultureInfo.InvariantCulture)}) ON CONFLICT DO NOTHING;
                CREATE TABLE IF NOT EXISTS {_schema}.queries(query_id uuid PRIMARY KEY,target text NOT NULL,tenant text NOT NULL,index_name text NOT NULL,scope_fingerprint text NOT NULL,expires_at timestamptz NOT NULL,payload bytea NOT NULL CHECK(octet_length(payload)<={Options.MaxSnapshotBytes.ToString(CultureInfo.InvariantCulture)}));
                CREATE INDEX IF NOT EXISTS query_scope ON {_schema}.queries(target,tenant,index_name);
                CREATE INDEX IF NOT EXISTS query_expiration ON {_schema}.queries(expires_at,query_id);
                """)) { _ = await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
            await using (var validate = Command(connection, transaction, $"SELECT version,max_snapshot_bytes,max_candidates FROM {_schema}.metadata WHERE singleton"))
            await using (var reader = await validate.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetInt32(0) != 1 || reader.GetInt32(1) != Options.MaxSnapshotBytes || reader.GetInt32(2) != Options.MaxCandidates)
                { throw new InvalidOperationException("The installed OpenSearch cursor format or serialization bounds differ."); }
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _operations.Release(); }
    }

    internal async ValueTask<SearchCursor> SaveAsync(string target, SearchScope scope, string fingerprint, IReadOnlyList<SearchHit> hits, CancellationToken cancellationToken)
    {
        if (hits.Count > Options.MaxCandidates) { throw new SearchBackpressureException(); }
        using var encoded = new BoundedPayload(Options.MaxSnapshotBytes);
        JsonSerializer.Serialize(encoded, hits.ToArray(), CursorJsonContext.Default.SearchHitArray);
        var payload = encoded.ToArray();
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await LockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            _ = await PruneAsync(connection, transaction, 128, cancellationToken).ConfigureAwait(false);
            await using (var budget = Command(connection, transaction, $"SELECT retained_count,retained_bytes,(SELECT count(*) FROM {_schema}.queries WHERE target=@target AND tenant=@tenant AND index_name=@index) FROM {_schema}.metadata WHERE singleton"))
            {
                ScopeParameters(budget, target, scope, fingerprint);
                await using var reader = await budget.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (reader.GetInt64(0) >= Options.MaxSnapshots || reader.GetInt64(1) + payload.Length > Options.MaxRetainedBytes || reader.GetInt64(2) >= Options.MaxSnapshotsPerTenantIndex)
                { throw new SearchBackpressureException(); }
            }
            var cursor = new SearchCursor(Guid.NewGuid(), 0);
            await using (var insert = Command(connection, transaction, $"INSERT INTO {_schema}.queries VALUES(@id,@target,@tenant,@index,@scope,clock_timestamp()+(@lifetime * interval '1 second'),@payload); UPDATE {_schema}.metadata SET retained_count=retained_count+1,retained_bytes=retained_bytes+octet_length(@payload) WHERE singleton"))
            {
                ScopeParameters(insert, target, scope, fingerprint); Parameter(insert, "id", cursor.QueryId); Parameter(insert, "lifetime", Options.Lifetime.TotalSeconds); Parameter(insert, "payload", payload);
                _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); return cursor;
        }
        finally { _operations.Release(); }
    }

    internal async ValueTask<(IReadOnlyList<SearchHit> Hits, DateTimeOffset Expires)> LoadAsync(string target, SearchScope scope, string fingerprint, SearchCursor cursor, CancellationToken cancellationToken)
    {
        if (cursor.QueryId == Guid.Empty || cursor.AfterRank < 0) { throw new SearchCursorExpiredException(); }
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var load = Command(connection, null, $"SELECT payload,expires_at FROM {_schema}.queries WHERE query_id=@id AND target=@target AND tenant=@tenant AND index_name=@index AND scope_fingerprint=@scope AND expires_at>clock_timestamp()");
            ScopeParameters(load, target, scope, fingerprint); Parameter(load, "id", cursor.QueryId);
            await using var reader = await load.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { throw new SearchCursorExpiredException(); }
            var payload = reader.GetFieldValue<byte[]>(0);
            if (payload.Length > Options.MaxSnapshotBytes) { throw new SearchCursorExpiredException(); }
            var hits = JsonSerializer.Deserialize(payload, CursorJsonContext.Default.SearchHitArray) ?? throw new SearchCursorExpiredException();
            if (hits.Length > Options.MaxCandidates || cursor.AfterRank > hits.Length || hits.Any(static hit => hit is null || !double.IsFinite(hit.Score) || hit.SourceVersion <= 0))
            { throw new SearchCursorExpiredException(); }
            return (Array.AsReadOnly(hits), new DateTimeOffset(reader.GetDateTime(1)));
        }
        finally { _operations.Release(); }
    }

    public async ValueTask<int> PruneExpiredAsync(int maximumCount = 128, CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 10_000) { throw new ArgumentOutOfRangeException(nameof(maximumCount)); }
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await LockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var count = await PruneAsync(connection, transaction, maximumCount, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); return count;
        }
        finally { _operations.Release(); }
    }
    private async ValueTask LockAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"SELECT singleton FROM {_schema}.metadata WHERE singleton FOR UPDATE");
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true) { throw new InvalidOperationException("Initialize cursor storage before use."); }
    }
    private async ValueTask<int> PruneAsync(DbConnection connection, DbTransaction transaction, int maximumCount, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"""
            WITH victims AS(SELECT query_id FROM {_schema}.queries WHERE expires_at<=clock_timestamp() ORDER BY expires_at,query_id LIMIT @limit),
            removed AS(DELETE FROM {_schema}.queries WHERE query_id IN(SELECT query_id FROM victims) RETURNING octet_length(payload) AS bytes),
            totals AS(SELECT count(*) AS count,coalesce(sum(bytes),0) AS bytes FROM removed)
            UPDATE {_schema}.metadata SET retained_count=retained_count-totals.count,retained_bytes=retained_bytes-totals.bytes FROM totals WHERE singleton RETURNING totals.count::integer
            """);
        Parameter(command, "limit", maximumCount); return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }
    private async ValueTask EnterAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!await _operations.WaitAsync(0, cancellationToken).ConfigureAwait(false)) { throw new SearchBackpressureException(); }
        if (Volatile.Read(ref _disposed) != 0) { _operations.Release(); throw new ObjectDisposedException(nameof(PostgreSqlOpenSearchCursorStore)); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        for (var i = 0; i < Options.MaxConcurrentOperations; i++) { await _operations.WaitAsync().ConfigureAwait(false); }
        if (_ownership is SearchDataSourceOwnership.Owned) { await _source.DisposeAsync().ConfigureAwait(false); }
    }
    private DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql)
    { var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; command.CommandTimeout = Options.CommandTimeoutSeconds; return command; }
    private static void ScopeParameters(DbCommand command, string target, SearchScope scope, string fingerprint)
    { Parameter(command, "target", target); Parameter(command, "tenant", scope.Tenant); Parameter(command, "index", scope.Index); Parameter(command, "scope", fingerprint); }
    private static void Parameter(DbCommand command, string name, object value)
    { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter); }
    private sealed class BoundedPayload(int maximum) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        private void Check(int count) { if (Position + count > maximum) { throw new SearchBackpressureException(); } }
    }
}

[JsonSerializable(typeof(SearchHit[]))]
internal sealed partial class CursorJsonContext : JsonSerializerContext;
