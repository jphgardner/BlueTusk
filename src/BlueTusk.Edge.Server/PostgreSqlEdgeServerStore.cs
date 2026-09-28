using System.Data;
using System.Data.Common;
using System.Globalization;

namespace BlueTusk.Edge.Server;

/// <summary>Host-controlled materialized selective scopes. Each business record, change and stable mutation receipt commits together.</summary>
public sealed partial class PostgreSqlEdgeServerStore : IAsyncDisposable
{
    private readonly DbDataSource _source;
    private readonly EdgeServerDataSourceOwnership _ownership;
    private readonly string _schema;
    private int _disposed;

    public PostgreSqlEdgeServerStore(DbDataSource source, EdgeServerOptions? options = null, EdgeServerDataSourceOwnership ownership = EdgeServerDataSourceOwnership.Borrowed)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(ownership)) { throw new ArgumentOutOfRangeException(nameof(ownership)); }
        Options = options ?? new EdgeServerOptions();
        EdgeValidation.Key(Options.Schema, nameof(options), 63);
        _schema = '"' + Options.Schema.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
        if (Options.MaxScopes is < 1 or > 100_000 || Options.MaxRecordBytes is < 1 or > 64 * 1024 * 1024 ||
            Options.MaxRecordsPerScope is < 1 or > 1_000_000 || Options.MaxReceiptsPerScope is < 1 or > 1_000_000 || Options.MaxChangesPerScope is < 1 or > 1_000_000 ||
            Options.MaxSnapshotsPerScope is < 1 or > 64 || Options.MaxBatchRecords is < 1 or > 10_000 || Options.CommandTimeoutSeconds is < 1 or > 300 ||
            Options.MaxRecordBytesPerScope < Options.MaxRecordBytes || Options.MaxChangeBytesPerScope < Options.MaxRecordBytes || Options.MaxBatchBytes < Options.MaxRecordBytes ||
            Options.SnapshotLifetime <= TimeSpan.Zero || Options.SnapshotLifetime > TimeSpan.FromHours(1))
        { throw new ArgumentException("Server options exceed bounded record, feed, receipt, snapshot or request limits.", nameof(options)); }
        _source = source;
        _ownership = ownership;
    }
    public EdgeServerOptions Options { get; }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var guard = Command(connection, transaction, "SELECT pg_advisory_xact_lock(hashtextextended(@name,0))"))
        {
            Parameter(guard, "name", "BlueTusk.Edge:" + Options.Schema);
            _ = await guard.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var setup = Command(connection, transaction, $"""
            CREATE SCHEMA IF NOT EXISTS {_schema};
            CREATE TABLE IF NOT EXISTS {_schema}.metadata(singleton boolean PRIMARY KEY DEFAULT true CHECK(singleton), version integer NOT NULL, max_record_bytes integer NOT NULL);
            INSERT INTO {_schema}.metadata(version,max_record_bytes) VALUES(1,{Options.MaxRecordBytes.ToString(CultureInfo.InvariantCulture)}) ON CONFLICT DO NOTHING;
            CREATE SEQUENCE IF NOT EXISTS {_schema}.revisions AS bigint NO CYCLE;
            CREATE TABLE IF NOT EXISTS {_schema}.scopes(tenant text COLLATE "C" NOT NULL, scope text COLLATE "C" NOT NULL, epoch bigint NOT NULL CHECK(epoch>0), head bigint NOT NULL DEFAULT 0, floor bigint NOT NULL DEFAULT 0, record_count bigint NOT NULL DEFAULT 0,record_bytes bigint NOT NULL DEFAULT 0,receipt_count bigint NOT NULL DEFAULT 0,change_count bigint NOT NULL DEFAULT 0,change_bytes bigint NOT NULL DEFAULT 0,PRIMARY KEY(tenant,scope),CHECK(head>=floor AND floor>=0 AND record_count>=0 AND record_bytes>=0 AND receipt_count>=0 AND change_count>=0 AND change_bytes>=0));
            CREATE TABLE IF NOT EXISTS {_schema}.records(tenant text COLLATE "C" NOT NULL,scope text COLLATE "C" NOT NULL,epoch bigint NOT NULL,id text COLLATE "C" NOT NULL,revision bigint NOT NULL CHECK(revision>0),payload bytea NOT NULL CHECK(octet_length(payload)<={Options.MaxRecordBytes.ToString(CultureInfo.InvariantCulture)}),deleted boolean NOT NULL,PRIMARY KEY(tenant,scope,epoch,id),CHECK(NOT deleted OR octet_length(payload)=0));
            CREATE TABLE IF NOT EXISTS {_schema}.changes(tenant text COLLATE "C" NOT NULL,scope text COLLATE "C" NOT NULL,epoch bigint NOT NULL,position bigint NOT NULL,id text COLLATE "C" NOT NULL,revision bigint NOT NULL,payload bytea NOT NULL,deleted boolean NOT NULL,PRIMARY KEY(tenant,scope,epoch,position));
            CREATE TABLE IF NOT EXISTS {_schema}.receipts(tenant text COLLATE "C" NOT NULL,scope text COLLATE "C" NOT NULL,epoch bigint NOT NULL,mutation_id uuid NOT NULL,fingerprint text NOT NULL,outcome smallint NOT NULL CHECK(outcome IN(0,1)),id text COLLATE "C" NULL,revision bigint NULL,payload bytea NULL,deleted boolean NULL,PRIMARY KEY(tenant,scope,epoch,mutation_id));
            CREATE TABLE IF NOT EXISTS {_schema}.snapshots(tenant text COLLATE "C" NOT NULL,scope text COLLATE "C" NOT NULL,epoch bigint NOT NULL,snapshot_id uuid NOT NULL,position bigint NOT NULL,expires_at timestamptz NOT NULL,PRIMARY KEY(tenant,scope,epoch,snapshot_id));
            CREATE TABLE IF NOT EXISTS {_schema}.snapshot_records(tenant text COLLATE "C" NOT NULL,scope text COLLATE "C" NOT NULL,epoch bigint NOT NULL,snapshot_id uuid NOT NULL,id text COLLATE "C" NOT NULL,revision bigint NOT NULL,payload bytea NOT NULL,deleted boolean NOT NULL,PRIMARY KEY(tenant,scope,epoch,snapshot_id,id),FOREIGN KEY(tenant,scope,epoch,snapshot_id) REFERENCES {_schema}.snapshots ON DELETE CASCADE);
            """)) { _ = await setup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        await using (var validate = Command(connection, transaction, $"SELECT version,max_record_bytes FROM {_schema}.metadata WHERE singleton"))
        await using (var reader = await validate.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetInt32(0) != 1 || reader.GetInt32(1) != Options.MaxRecordBytes)
            { throw new InvalidOperationException("The Edge server storage version or installed record byte contract differs."); }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Administrative epoch activation. Newer epochs explicitly discard the prior scope's data and replay identities.</summary>
    public async ValueTask ActivateScopeAsync(EdgeScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var guard = Command(connection, transaction, "SELECT pg_advisory_xact_lock(hashtextextended(@schema,1))"))
        {
            Parameter(guard, "schema", Options.Schema);
            _ = await guard.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        long? epoch;
        await using (var read = Scoped(connection, transaction, $"SELECT epoch FROM {_schema}.scopes WHERE tenant=@tenant AND scope=@scope FOR UPDATE", scope))
        { epoch = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long installed ? installed : null; }
        if (epoch > scope.Epoch) { throw new EdgeScopeMismatchException(); }
        if (epoch == scope.Epoch) { await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); return; }
        if (epoch is null)
        {
            await using var budget = Command(connection, transaction, $"SELECT count(*) FROM {_schema}.scopes");
            if ((long)(await budget.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! >= Options.MaxScopes) { throw new EdgeCapacityException("Server scope cardinality is full."); }
        }
        foreach (var table in new[] { "records", "changes", "receipts", "snapshots" })
        {
            await using var clear = Scoped(connection, transaction, $"DELETE FROM {_schema}.{table} WHERE tenant=@tenant AND scope=@scope", scope);
            _ = await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var activate = Scoped(connection, transaction, $"INSERT INTO {_schema}.scopes(tenant,scope,epoch) VALUES(@tenant,@scope,@epoch) ON CONFLICT(tenant,scope) DO UPDATE SET epoch=EXCLUDED.epoch,head=0,floor=0,record_count=0,record_bytes=0,receipt_count=0,change_count=0,change_bytes=0", scope))
        { _ = await activate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<EdgeRecord?> GetAsync(EdgeScope scope, string id, CancellationToken cancellationToken = default)
    {
        EdgeValidation.Key(id, nameof(id), 512);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        _ = await LockScopeAsync(connection, transaction, scope, cancellationToken, write: false).ConfigureAwait(false);
        var result = await RecordAsync(connection, transaction, scope, id, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public ValueTask<EdgeMutationOutcome> ApplyMutationAsync(EdgeMutation mutation, CancellationToken cancellationToken = default) =>
        ApplyMutationCoreAsync(mutation, null, cancellationToken);

    /// <summary>Commits application database effects with the first applied record/feed/receipt. Replayed identities and conflicts never invoke the callback.</summary>
    public ValueTask<EdgeMutationOutcome> ApplyMutationWithBusinessAsync(EdgeMutation mutation,
        Func<DbConnection, DbTransaction, EdgeMutation, EdgeRecord, CancellationToken, ValueTask> writeBusiness,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writeBusiness);
        return ApplyMutationCoreAsync(mutation, writeBusiness, cancellationToken);
    }

    private async ValueTask<EdgeMutationOutcome> ApplyMutationCoreAsync(EdgeMutation mutation,
        Func<DbConnection, DbTransaction, EdgeMutation, EdgeRecord, CancellationToken, ValueTask>? writeBusiness, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (mutation.Payload.Length > Options.MaxRecordBytes) { throw new EdgeCapacityException("Server mutation exceeds the record byte budget."); }
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var state = await LockScopeAsync(connection, transaction, mutation.Scope, cancellationToken).ConfigureAwait(false);
        await using (var receipt = Scoped(connection, transaction, $"SELECT fingerprint,outcome,id,revision,payload,deleted FROM {_schema}.receipts WHERE tenant=@tenant AND scope=@scope AND epoch=@epoch AND mutation_id=@mutation", mutation.Scope))
        {
            Parameter(receipt, "mutation", mutation.Id);
            await using var reader = await receipt.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetString(0) != mutation.Fingerprint) { throw new EdgeMutationIdentityException(); }
                var replay = new EdgeMutationOutcome((EdgeMutationOutcomeKind)reader.GetInt16(1), reader.IsDBNull(2) ? null : ReadRecord(reader, 2));
                await reader.DisposeAsync().ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return replay;
            }
        }
        if (state.ReceiptCount >= Options.MaxReceiptsPerScope) { throw new EdgeCapacityException("Server receipt retention is full; rotate scope under an explicit replay policy."); }
        var current = await RecordAsync(connection, transaction, mutation.Scope, mutation.DocumentId, cancellationToken).ConfigureAwait(false);
        EdgeMutationOutcome outcome;
        if ((current?.Revision ?? 0) != mutation.ExpectedRevision)
        { outcome = new(EdgeMutationOutcomeKind.Conflict, current); }
        else
        {
            var count = state.RecordCount + (current is null ? 1 : 0);
            var bytes = state.RecordBytes - (current?.Payload.Length ?? 0) + mutation.Payload.Length;
            if (count > Options.MaxRecordsPerScope || bytes > Options.MaxRecordBytesPerScope || state.ChangeCount >= Options.MaxChangesPerScope || state.ChangeBytes + mutation.Payload.Length > Options.MaxChangeBytesPerScope)
            { throw new EdgeCapacityException("Server record/feed retention capacity is full."); }
            long revision;
            await using (var next = Command(connection, transaction, $"SELECT nextval('{_schema.Replace("'", "''", StringComparison.Ordinal)}.revisions'::regclass)"))
            { revision = (long)(await next.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!; }
            var record = new EdgeRecord(mutation.DocumentId, revision, mutation.Payload, mutation.Kind is EdgeMutationKind.Delete);
            var position = checked(state.Head + 1);
            await using (var write = Scoped(connection, transaction, $"INSERT INTO {_schema}.records(tenant,scope,epoch,id,revision,payload,deleted) VALUES(@tenant,@scope,@epoch,@id,@revision,@payload,@deleted) ON CONFLICT(tenant,scope,epoch,id) DO UPDATE SET revision=EXCLUDED.revision,payload=EXCLUDED.payload,deleted=EXCLUDED.deleted", mutation.Scope))
            {
                AddRecord(write, record);
                _ = await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using (var append = Scoped(connection, transaction, $"INSERT INTO {_schema}.changes VALUES(@tenant,@scope,@epoch,@position,@id,@revision,@payload,@deleted); UPDATE {_schema}.scopes SET head=@position,record_count=@count,record_bytes=@bytes,change_count=change_count+1,change_bytes=change_bytes+octet_length(@payload) WHERE tenant=@tenant AND scope=@scope AND epoch=@epoch", mutation.Scope))
            {
                Parameter(append, "position", position);
                Parameter(append, "count", count);
                Parameter(append, "bytes", bytes);
                AddRecord(append, record);
                _ = await append.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            outcome = new(EdgeMutationOutcomeKind.Applied, record);
        }
        await using (var insert = Scoped(connection, transaction, $"INSERT INTO {_schema}.receipts VALUES(@tenant,@scope,@epoch,@mutation,@fingerprint,@outcome,@id,@revision,@payload,@deleted); UPDATE {_schema}.scopes SET receipt_count=receipt_count+1 WHERE tenant=@tenant AND scope=@scope AND epoch=@epoch", mutation.Scope))
        {
            Parameter(insert, "mutation", mutation.Id);
            Parameter(insert, "fingerprint", mutation.Fingerprint);
            Parameter(insert, "outcome", (short)outcome.Kind);
            Parameter(insert, "id", outcome.ServerRecord?.Id);
            Parameter(insert, "revision", outcome.ServerRecord?.Revision, DbType.Int64);
            Parameter(insert, "payload", outcome.ServerRecord?.Payload.ToArray(), DbType.Binary);
            Parameter(insert, "deleted", outcome.ServerRecord?.Deleted, DbType.Boolean);
            _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        if (outcome.Kind is EdgeMutationOutcomeKind.Applied && writeBusiness is not null)
        {
            // The host must write through this exact connection/transaction and must not commit/dispose them or perform external I/O.
            await writeBusiness(connection, transaction, mutation, outcome.ServerRecord!, cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _ownership is EdgeServerDataSourceOwnership.Owned)
        { await _source.DisposeAsync().ConfigureAwait(false); }
    }

    private async ValueTask<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    }
    private async ValueTask<ScopeState> LockScopeAsync(DbConnection connection, DbTransaction transaction, EdgeScope scope, CancellationToken cancellationToken, bool write = true)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var command = Scoped(connection, transaction, $"SELECT epoch,head,floor,record_count,record_bytes,receipt_count,change_count,change_bytes FROM {_schema}.scopes WHERE tenant=@tenant AND scope=@scope " + (write ? "FOR UPDATE" : "FOR SHARE"), scope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetInt64(0) != scope.Epoch) { throw new EdgeScopeMismatchException(); }
        return new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7));
    }
    private async ValueTask<EdgeRecord?> RecordAsync(DbConnection connection, DbTransaction transaction, EdgeScope scope, string id, CancellationToken cancellationToken)
    {
        await using var command = Scoped(connection, transaction, $"SELECT id,revision,payload,deleted FROM {_schema}.records WHERE tenant=@tenant AND scope=@scope AND epoch=@epoch AND id=@id", scope);
        Parameter(command, "id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRecord(reader) : null;
    }
    private sealed record ScopeState(long Head, long Floor, long RecordCount, long RecordBytes, long ReceiptCount, long ChangeCount, long ChangeBytes);
    private DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        return command;
    }
    private DbCommand Scoped(DbConnection connection, DbTransaction? transaction, string sql, EdgeScope scope)
    {
        var command = Command(connection, transaction, sql);
        Parameter(command, "tenant", scope.Tenant);
        Parameter(command, "scope", scope.Id);
        Parameter(command, "epoch", scope.Epoch);
        return command;
    }
    private static void Parameter(DbCommand command, string name, object? value, DbType? type = null)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        if (type is not null) { parameter.DbType = type.Value; }
        command.Parameters.Add(parameter);
    }
    private static void AddRecord(DbCommand command, EdgeRecord record)
    {
        Parameter(command, "id", record.Id);
        Parameter(command, "revision", record.Revision);
        Parameter(command, "payload", record.Payload.ToArray(), DbType.Binary);
        Parameter(command, "deleted", record.Deleted);
    }
    private static EdgeRecord ReadRecord(DbDataReader reader, int offset = 0) =>
        new(reader.GetString(offset), reader.GetInt64(offset + 1), reader.GetFieldValue<byte[]>(offset + 2), reader.GetBoolean(offset + 3));
}
