using System.Buffers;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BlueTusk.Live;

namespace BlueTusk.Projections.Live;

public sealed class PostgreSqlProjectionLiveReplayOptions
{
    public string Schema { get; init; } = "bluetusk_projections";
    public int MaximumAppendEvents { get; init; } = 1024;
    public int MaximumEventBytes { get; init; } = 1_048_576;
    public int MaximumBatchBytes { get; init; } = 8_388_608;
    public int MaximumReadEvents { get; init; } = 4096;
    public int PruneBatchRows { get; init; } = 1024;
    public TimeSpan RetentionWindow { get; init; } = TimeSpan.FromHours(1);
    public int CommandTimeoutSeconds { get; init; } = 30;
}

public sealed record ProjectionLivePublisherLease(LiveSubscriptionIdentity Identity, string OwnerId, long FencingToken);

public sealed class ProjectionLivePublisherFencedException : InvalidOperationException
{
    public ProjectionLivePublisherFencedException() : base("The durable Live publisher lease expired or was replaced. Close local subscribers and resume on the current owner.") { ProjectionLiveDiagnostics.Fence(); }
}

/// <summary>One PostgreSQL boundary for publisher fencing, bounded replay append and retained resume history.</summary>
public sealed class PostgreSqlProjectionLiveReplayStore
{
    private readonly DbDataSource _dataSource;
    private readonly PostgreSqlProjectionLiveReplayOptions _options;
    private readonly string _schema;

    public PostgreSqlProjectionLiveReplayStore(DbDataSource dataSource, PostgreSqlProjectionLiveReplayOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _options = options ?? new();
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.Schema);
        if (_options.Schema.Length > 63 || _options.Schema.Any(static c => !char.IsAsciiLetterOrDigit(c) && c != '_') || (!char.IsAsciiLetter(_options.Schema[0]) && _options.Schema[0] != '_'))
        { throw new ArgumentException("Schema must be a 1..63 ASCII PostgreSQL identifier.", nameof(options)); }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaximumAppendEvents);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.MaximumAppendEvents, 65536);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaximumEventBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.MaximumEventBytes, 16_777_216);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaximumBatchBytes, _options.MaximumEventBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.MaximumBatchBytes, 67_108_864);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaximumReadEvents);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.MaximumReadEvents, 65536);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.PruneBatchRows);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.PruneBatchRows, 65536);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.CommandTimeoutSeconds);
        if (_options.RetentionWindow < TimeSpan.FromMilliseconds(1) || _options.RetentionWindow > TimeSpan.FromDays(365)) { throw new ArgumentOutOfRangeException(nameof(options)); }
        _dataSource = dataSource; _schema = '"' + _options.Schema + '"';
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "SELECT pg_advisory_xact_lock(hashtextextended(@schema,0))", cancellationToken, ("schema", _options.Schema + ":projections-live")).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"""
            CREATE SCHEMA IF NOT EXISTS {_schema};
            CREATE TABLE IF NOT EXISTS {_schema}.projection_live_metadata(singleton boolean PRIMARY KEY CHECK(singleton),version integer NOT NULL);
            INSERT INTO {_schema}.projection_live_metadata VALUES(true,1) ON CONFLICT DO NOTHING;
            CREATE TABLE IF NOT EXISTS {_schema}.projection_live_replay(
                identity_fingerprint char(64) PRIMARY KEY,owner_id text NULL,fencing_token bigint NOT NULL DEFAULT 0 CHECK(fencing_token>=0),expires_at timestamptz NULL,
                first_available_sequence bigint NOT NULL DEFAULT 1 CHECK(first_available_sequence>0),last_sequence bigint NOT NULL DEFAULT 0 CHECK(last_sequence>=0),
                CHECK(first_available_sequence<=last_sequence+1));
            CREATE TABLE IF NOT EXISTS {_schema}.projection_live_events(
                identity_fingerprint char(64) NOT NULL REFERENCES {_schema}.projection_live_replay(identity_fingerprint),sequence bigint NOT NULL CHECK(sequence>0),
                kind integer NOT NULL,content_type text NOT NULL,payload bytea NOT NULL,integrity bytea NOT NULL CHECK(octet_length(integrity)=32),
                recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),PRIMARY KEY(identity_fingerprint,sequence));
            CREATE INDEX IF NOT EXISTS projection_live_retention ON {_schema}.projection_live_events(recorded_at,identity_fingerprint,sequence)
            """, cancellationToken).ConfigureAwait(false);
        await using var version = Command(connection, transaction, $"SELECT version FROM {_schema}.projection_live_metadata WHERE singleton");
        if (Convert.ToInt32(await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 1)
        { throw new InvalidOperationException("Unsupported durable projection Live schema version."); }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ProjectionLivePublisher?> AcquireAsync(LiveSubscriptionIdentity identity, string ownerId, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity); ValidateOwner(ownerId); ValidateDuration(duration);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"INSERT INTO {_schema}.projection_live_replay(identity_fingerprint) VALUES(@identity) ON CONFLICT DO NOTHING", cancellationToken, ("identity", identity.Fingerprint)).ConfigureAwait(false);
        await using var command = Command(connection, transaction, $"""
            UPDATE {_schema}.projection_live_replay SET owner_id=@owner,fencing_token=fencing_token+1,expires_at=clock_timestamp()+(@duration*interval '1 millisecond')
            WHERE identity_fingerprint=@identity AND (owner_id IS NULL OR expires_at<=clock_timestamp()) RETURNING fencing_token
            """, ("identity", identity.Fingerprint), ("owner", ownerId), ("duration", duration.TotalMilliseconds));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        ProjectionLiveDiagnostics.Acquisition(value is not null and not DBNull);
        return value is null or DBNull ? null : new ProjectionLivePublisher(this, new(identity, ownerId, Convert.ToInt64(value, CultureInfo.InvariantCulture)), duration);
    }

    internal async ValueTask EnsureActiveAsync(ProjectionLivePublisherLease lease, TimeSpan? renew, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, renew is null ? $"""
            SELECT fencing_token FROM {_schema}.projection_live_replay WHERE identity_fingerprint=@identity AND owner_id=@owner AND fencing_token=@token AND expires_at>clock_timestamp()
            """ : $"""
            UPDATE {_schema}.projection_live_replay SET expires_at=clock_timestamp()+(@duration*interval '1 millisecond')
            WHERE identity_fingerprint=@identity AND owner_id=@owner AND fencing_token=@token AND expires_at>clock_timestamp() RETURNING fencing_token
            """, ("identity", lease.Identity.Fingerprint), ("owner", lease.OwnerId), ("token", lease.FencingToken), ("duration", renew?.TotalMilliseconds ?? 0));
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null or DBNull) { throw new ProjectionLivePublisherFencedException(); }
    }

    internal async ValueTask<bool> ReleaseAsync(ProjectionLivePublisherLease lease, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ExecuteAsync(connection, null, $"UPDATE {_schema}.projection_live_replay SET owner_id=NULL,expires_at=NULL WHERE identity_fingerprint=@identity AND owner_id=@owner AND fencing_token=@token", cancellationToken,
            ("identity", lease.Identity.Fingerprint), ("owner", lease.OwnerId), ("token", lease.FencingToken)).ConfigureAwait(false) == 1;
    }

    internal async ValueTask<LiveReplayAppendResult> AppendAsync(ProjectionLivePublisherLease lease, LiveReplayAppendRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Identity != lease.Identity) { throw new ArgumentException("A publisher cannot append another subscription identity.", nameof(request)); }
        if (request.Events.Count > _options.MaximumAppendEvents) { throw new ProjectionBoundExceededException("Live replay append event count exceeded."); }
        long bytes = 0;
        foreach (var item in request.Events)
        {
            bytes += item.Payload.Length;
            if (item.Payload.Length > _options.MaximumEventBytes || bytes > _options.MaximumBatchBytes || item.ContentType.Length > 200 || item.ContentType.Contains('\0') || !LiveReplayJsonSerializer.VerifyIntegrity(item))
            { throw new ProjectionBoundExceededException("Live replay payload/content/integrity admission failed."); }
        }
        var json = Serialize(request.Events);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        long head;
        await using (var command = Command(connection, transaction, $"""
            SELECT last_sequence,owner_id,fencing_token,expires_at>clock_timestamp() FROM {_schema}.projection_live_replay WHERE identity_fingerprint=@identity FOR UPDATE
            """, ("identity", lease.Identity.Fingerprint)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(1) || reader.GetString(1) != lease.OwnerId || reader.GetInt64(2) != lease.FencingToken || reader.IsDBNull(3) || !reader.GetBoolean(3))
            { throw new ProjectionLivePublisherFencedException(); }
            head = reader.GetInt64(0);
        }
        if (head != request.ExpectedLastSequence)
        {
            await using var match = Command(connection, transaction, $"""
                SELECT count(*) FROM {_schema}.projection_live_events e JOIN jsonb_to_recordset(CAST(@events AS jsonb)) i(sequence bigint,kind integer,content text,payload text,integrity text)
                ON e.identity_fingerprint=@identity AND e.sequence=i.sequence AND e.kind=i.kind AND e.content_type=i.content
                    AND e.payload=decode(i.payload,'base64') AND e.integrity=decode(i.integrity,'base64')
                """, ("identity", lease.Identity.Fingerprint), ("events", json));
            var matches = Convert.ToInt32(await match.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == request.Events.Count;
            await CheckFenceAsync(connection, transaction, lease, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (matches && head >= request.Events[^1].Sequence) { ProjectionLiveDiagnostics.Duplicate(request.Events.Count); }
            return new(matches && head >= request.Events[^1].Sequence ? LiveReplayAppendStatus.AlreadyStored : LiveReplayAppendStatus.SequenceConflict, head);
        }
        await ExecuteAsync(connection, transaction, $"""
            INSERT INTO {_schema}.projection_live_events(identity_fingerprint,sequence,kind,content_type,payload,integrity)
            SELECT @identity,i.sequence,i.kind,i.content,decode(i.payload,'base64'),decode(i.integrity,'base64')
            FROM jsonb_to_recordset(CAST(@events AS jsonb)) i(sequence bigint,kind integer,content text,payload text,integrity text)
            """, cancellationToken, ("identity", lease.Identity.Fingerprint), ("events", json)).ConfigureAwait(false);
        await using var update = Command(connection, transaction, $"""
            UPDATE {_schema}.projection_live_replay SET last_sequence=@head WHERE identity_fingerprint=@identity AND owner_id=@owner AND fencing_token=@token AND expires_at>clock_timestamp()
            """, ("head", request.Events[^1].Sequence), ("identity", lease.Identity.Fingerprint), ("owner", lease.OwnerId), ("token", lease.FencingToken));
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) { throw new ProjectionLivePublisherFencedException(); }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        ProjectionLiveDiagnostics.Append(request.Events.Count);
        return new(LiveReplayAppendStatus.Stored, request.Events[^1].Sequence);
    }

    public async ValueTask<LiveReplayReadResult> ReadAsync(LiveSubscriptionIdentity identity, long afterSequence, int maximumEvents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity); ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEvents); ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumEvents, _options.MaximumReadEvents);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, $"""
            WITH h AS(SELECT first_available_sequence,last_sequence FROM {_schema}.projection_live_replay WHERE identity_fingerprint=@identity),
            p AS(SELECT *,sum(octet_length(payload)::bigint) OVER(ORDER BY sequence) AS bytes FROM
                (SELECT sequence,kind,content_type,payload,integrity FROM {_schema}.projection_live_events WHERE identity_fingerprint=@identity AND sequence>@after ORDER BY sequence LIMIT @count) e)
            SELECT h.first_available_sequence,h.last_sequence,p.sequence,p.kind,p.content_type,
                CASE WHEN p.bytes<=@bytes THEN p.payload ELSE NULL END,p.integrity FROM h LEFT JOIN p ON true ORDER BY p.sequence
            """, ("identity", identity.Fingerprint), ("after", afterSequence), ("count", maximumEvents), ("bytes", _options.MaximumBatchBytes));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        long first = 0, head = 0; var exists = false; var events = new List<LiveReplayEvent>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            exists = true; first = reader.GetInt64(0); head = reader.GetInt64(1);
            if (!reader.IsDBNull(2) && !reader.IsDBNull(5)) { events.Add(LiveReplayEvent.Restore(reader.GetInt64(2), (LiveEventKind)reader.GetInt32(3), reader.GetString(4), reader.GetFieldValue<byte[]>(5), reader.GetFieldValue<byte[]>(6))); }
        }
        var status = !exists || afterSequence > head ? LiveReplayReadStatus.NotFound : afterSequence < first - 1 ? LiveReplayReadStatus.Expired : afterSequence == head ? LiveReplayReadStatus.Current : LiveReplayReadStatus.Available;
        if (status == LiveReplayReadStatus.Available)
        {
            if (events.Count == 0) { throw new ProjectionBoundExceededException("The next retained Live event is missing or exceeds this store's byte contract. Replay cannot skip it."); }
            for (var index = 0; index < events.Count; index++)
            { if (events[index].Sequence != checked(afterSequence + index + 1)) { throw new InvalidOperationException("Retained Live replay contains a sequence gap."); } }
        }
        return new(status, first, head, status == LiveReplayReadStatus.Available ? events : []);
    }

    public async ValueTask<int> PruneAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var identities = new List<string>();
        await using (var select = Command(connection, transaction, $"""
            SELECT r.identity_fingerprint FROM {_schema}.projection_live_replay r
            WHERE EXISTS(SELECT 1 FROM {_schema}.projection_live_events e WHERE e.identity_fingerprint=r.identity_fingerprint AND e.sequence=r.first_available_sequence AND e.recorded_at<transaction_timestamp()-(@retention*interval '1 millisecond'))
            ORDER BY r.identity_fingerprint LIMIT @limit FOR UPDATE SKIP LOCKED
            """, ("retention", _options.RetentionWindow.TotalMilliseconds), ("limit", _options.PruneBatchRows)))
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        { while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { identities.Add(reader.GetString(0)); } }
        if (identities.Count == 0) { await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); return 0; }
        var selected = SerializeIdentities(identities);
        var deleted = await ExecuteAsync(connection, transaction, $"""
            WITH d AS(SELECT e.ctid FROM {_schema}.projection_live_events e WHERE e.identity_fingerprint IN(SELECT jsonb_array_elements_text(CAST(@identities AS jsonb)))
                AND e.recorded_at<transaction_timestamp()-(@retention*interval '1 millisecond')
                AND NOT EXISTS(SELECT 1 FROM {_schema}.projection_live_events n WHERE n.identity_fingerprint=e.identity_fingerprint AND n.sequence<e.sequence AND n.recorded_at>=transaction_timestamp()-(@retention*interval '1 millisecond'))
                ORDER BY e.identity_fingerprint,e.sequence LIMIT @limit FOR UPDATE)
            DELETE FROM {_schema}.projection_live_events e USING d WHERE e.ctid=d.ctid
            """, cancellationToken, ("identities", selected), ("retention", _options.RetentionWindow.TotalMilliseconds), ("limit", _options.PruneBatchRows)).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"""
            UPDATE {_schema}.projection_live_replay r SET first_available_sequence=COALESCE((SELECT min(e.sequence) FROM {_schema}.projection_live_events e WHERE e.identity_fingerprint=r.identity_fingerprint),r.last_sequence+1)
            WHERE r.identity_fingerprint IN(SELECT jsonb_array_elements_text(CAST(@identities AS jsonb)))
            """, cancellationToken, ("identities", selected)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        ProjectionLiveDiagnostics.Prune(deleted);
        return deleted;
    }

    private async ValueTask CheckFenceAsync(DbConnection connection, DbTransaction transaction, ProjectionLivePublisherLease lease, CancellationToken token)
    {
        await using var command = Command(connection, transaction, $"SELECT fencing_token FROM {_schema}.projection_live_replay WHERE identity_fingerprint=@identity AND owner_id=@owner AND fencing_token=@token AND expires_at>clock_timestamp()",
            ("identity", lease.Identity.Fingerprint), ("owner", lease.OwnerId), ("token", lease.FencingToken));
        if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is null or DBNull) { throw new ProjectionLivePublisherFencedException(); }
    }
    private DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; command.CommandTimeout = _options.CommandTimeoutSeconds;
        foreach (var (name, value) in parameters) { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter); }
        return command;
    }
    private async ValueTask<int> ExecuteAsync(DbConnection connection, DbTransaction? transaction, string sql, CancellationToken token, params (string Name, object Value)[] parameters)
    { await using var command = Command(connection, transaction, sql, parameters); return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false); }
    private static void ValidateOwner(string owner)
    { ArgumentException.ThrowIfNullOrWhiteSpace(owner); if (owner.Length > 200 || owner.Contains('\0')) { throw new ArgumentException("Invalid Live owner identity.", nameof(owner)); } }
    private static void ValidateDuration(TimeSpan duration)
    { if (duration < TimeSpan.FromMilliseconds(1) || duration > TimeSpan.FromHours(24)) { throw new ArgumentOutOfRangeException(nameof(duration)); } }
    private static string SerializeIdentities(List<string> identities)
    { var buffer = new ArrayBufferWriter<byte>(); using var writer = new Utf8JsonWriter(buffer); writer.WriteStartArray(); foreach (var identity in identities) { writer.WriteStringValue(identity); } writer.WriteEndArray(); writer.Flush(); return Encoding.UTF8.GetString(buffer.WrittenSpan); }
    private static string Serialize(IReadOnlyList<LiveReplayEvent> events)
    {
        var buffer = new ArrayBufferWriter<byte>(); using var writer = new Utf8JsonWriter(buffer); writer.WriteStartArray();
        foreach (var item in events)
        {
            writer.WriteStartObject(); writer.WriteNumber("sequence", item.Sequence); writer.WriteNumber("kind", (int)item.Kind); writer.WriteString("content", item.ContentType);
            writer.WriteBase64String("payload", item.Payload.Span); writer.WriteBase64String("integrity", item.IntegrityHash.Span); writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.Flush(); return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

/// <summary>A lease-bound replay writer. Ownership and replay append share the store's database transaction.</summary>
public sealed class ProjectionLivePublisher : ILiveReplayStore, IAsyncDisposable
{
    private readonly PostgreSqlProjectionLiveReplayStore _store;
    private readonly TimeSpan _duration;
    private long _renewed = Stopwatch.GetTimestamp();
    private int _disposed;
    internal ProjectionLivePublisher(PostgreSqlProjectionLiveReplayStore store, ProjectionLivePublisherLease lease, TimeSpan duration) { _store = store; Lease = lease; _duration = duration; }
    public ProjectionLivePublisherLease Lease { get; }
    public async ValueTask EnsureActiveAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var renew = Stopwatch.GetElapsedTime(Interlocked.Read(ref _renewed)) >= _duration / 3;
        await _store.EnsureActiveAsync(Lease, renew ? _duration : null, cancellationToken).ConfigureAwait(false);
        if (renew) { Interlocked.Exchange(ref _renewed, Stopwatch.GetTimestamp()); }
    }
    public ValueTask<LiveReplayAppendResult> AppendAsync(LiveReplayAppendRequest request, CancellationToken cancellationToken = default)
    { ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this); return _store.AppendAsync(Lease, request, cancellationToken); }
    public ValueTask<LiveReplayReadResult> ReadAsync(LiveSubscriptionIdentity identity, long afterSequence, int maximumEvents, CancellationToken cancellationToken = default)
    { if (identity != Lease.Identity) { throw new ArgumentException("The publisher is bound to another authorized subscription.", nameof(identity)); } return _store.ReadAsync(identity, afterSequence, maximumEvents, cancellationToken); }
    public ValueTask<int> PruneAsync(CancellationToken cancellationToken = default) => _store.PruneAsync(cancellationToken);
    public async ValueTask DisposeAsync()
    { if (Interlocked.Exchange(ref _disposed, 1) == 0) { _ = await _store.ReleaseAsync(Lease, CancellationToken.None).ConfigureAwait(false); } }
}
