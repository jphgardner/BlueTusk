using System.Buffers;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BlueTusk.Events;

/// <summary>
/// PostgreSQL transactional event outbox/inbox and per-stream replay. The supplied data source is borrowed.
/// Append and inbox methods never commit, roll back, or dispose caller-owned transactions.
/// </summary>
public sealed partial class PostgreSqlEventStore
{
    private readonly DbDataSource _dataSource;
    private readonly PostgreSqlEventsOptions _options;
    private readonly string _schema;

    public PostgreSqlEventStore(DbDataSource dataSource, PostgreSqlEventsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _options = options ?? new PostgreSqlEventsOptions();
        _options.Validate();
        _dataSource = dataSource;
        _schema = '"' + _options.Schema + '"';
    }

    /// <summary>Install schema version 1. Run during deployment, not on application hot paths.</summary>
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended(@schema, 0))", cancellationToken,
            ("schema", _options.Schema)).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"CREATE SCHEMA IF NOT EXISTS {_schema}", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"""
            CREATE TABLE IF NOT EXISTS {_schema}.schema_version (
                singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
                version integer NOT NULL CHECK (version > 0)
            );
            INSERT INTO {_schema}.schema_version(singleton, version) VALUES (true, 1) ON CONFLICT DO NOTHING;
            CREATE TABLE IF NOT EXISTS {_schema}.streams (
                tenant_id text NOT NULL CHECK (length(tenant_id) BETWEEN 1 AND 200),
                stream_id text NOT NULL CHECK (length(stream_id) BETWEEN 1 AND 200),
                last_sequence bigint NOT NULL DEFAULT 0 CHECK (last_sequence >= 0),
                PRIMARY KEY (tenant_id, stream_id)
            );
            CREATE TABLE IF NOT EXISTS {_schema}.outbox (
                tenant_id text NOT NULL,
                stream_id text NOT NULL,
                sequence bigint NOT NULL CHECK (sequence > 0),
                event_id uuid NOT NULL CHECK (event_id <> '00000000-0000-0000-0000-000000000000'::uuid),
                event_type text NOT NULL CHECK (length(event_type) BETWEEN 1 AND 200),
                version integer NOT NULL CHECK (version > 0),
                occurred_at timestamptz NOT NULL,
                payload bytea NOT NULL CHECK (octet_length(payload) > 0),
                recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                PRIMARY KEY (tenant_id, stream_id, sequence),
                UNIQUE (tenant_id, event_id),
                FOREIGN KEY (tenant_id, stream_id) REFERENCES {_schema}.streams (tenant_id, stream_id)
            );
            CREATE TABLE IF NOT EXISTS {_schema}.inbox (
                consumer_id text NOT NULL CHECK (length(consumer_id) BETWEEN 1 AND 200),
                tenant_id text NOT NULL,
                event_id uuid NOT NULL,
                stream_id text NOT NULL,
                sequence bigint NOT NULL CHECK (sequence > 0),
                event_type text NOT NULL,
                version integer NOT NULL CHECK (version > 0),
                occurred_at timestamptz NOT NULL,
                payload_hash bytea NOT NULL CHECK (octet_length(payload_hash) = 32),
                processed_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                PRIMARY KEY (consumer_id, tenant_id, event_id)
            );
            CREATE TABLE IF NOT EXISTS {_schema}.replay (
                consumer_id text NOT NULL CHECK (length(consumer_id) BETWEEN 1 AND 200),
                tenant_id text NOT NULL,
                stream_id text NOT NULL,
                checkpoint bigint NOT NULL DEFAULT 0 CHECK (checkpoint >= 0),
                fencing_token bigint NOT NULL DEFAULT 0 CHECK (fencing_token >= 0),
                owner_id text NULL,
                expires_at timestamptz NULL,
                PRIMARY KEY (consumer_id, tenant_id, stream_id),
                FOREIGN KEY (tenant_id, stream_id) REFERENCES {_schema}.streams (tenant_id, stream_id)
            )
            """, cancellationToken).ConfigureAwait(false);
        await using (var command = Command(connection, transaction, $"SELECT version FROM {_schema}.schema_version WHERE singleton"))
        {
            var version = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (version != 1)
            {
                throw new InvalidOperationException($"Unsupported BlueTusk.Events schema version {version}.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Append a bounded batch to one tenant stream, using the application's transaction. Stream locking
    /// guarantees gap-free commit ordering within that stream; independent streams can append concurrently.
    /// Duplicate identities must have identical content and timestamps. Cross-stream identities conflict.
    /// </summary>
    public async ValueTask<IReadOnlyList<EventAppendReceipt>> AppendAsync(DbConnection connection,
        DbTransaction transaction, EventStreamKey stream, IReadOnlyList<EventWrite> events,
        CancellationToken cancellationToken = default)
    {
        EventValidation.Transaction(connection, transaction);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentOutOfRangeException.ThrowIfZero(events.Count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(events.Count, _options.MaximumAppendEvents);
        long byteCount = 0;
        var identities = new HashSet<Guid>();
        foreach (var value in events)
        {
            ArgumentNullException.ThrowIfNull(value);
            byteCount += value.Payload.Length;
            if (value.Payload.Length > _options.MaximumEventBytes || byteCount > _options.MaximumAppendBytes)
            {
                throw new ArgumentException("The append exceeds the configured event or batch payload byte bound.", nameof(events));
            }

            if (!identities.Add(value.EventId))
            {
                throw new ArgumentException("A batch cannot contain duplicate event identities.", nameof(events));
            }
        }

        long lastSequence;
        // Upsert obtains a row lock held until the application transaction ends. A sequence/identity
        // column alone is not sufficient: later transactions could commit ahead of earlier offsets.
        await using (var command = Command(connection, transaction, $"""
            INSERT INTO {_schema}.streams (tenant_id, stream_id) VALUES (@tenant, @stream)
            ON CONFLICT (tenant_id, stream_id) DO UPDATE SET last_sequence = {_schema}.streams.last_sequence
            RETURNING last_sequence
            """, ("tenant", stream.TenantId), ("stream", stream.StreamId)))
        {
            lastSequence = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        var wireBatch = SerializeBatch(events);
        var existing = new Dictionary<Guid, StoredEvent>();
        await using (var command = Command(connection, transaction, $"""
            SELECT o.stream_id, o.sequence, o.event_id, o.event_type, o.version, o.occurred_at, o.payload
            FROM {_schema}.outbox o
            JOIN jsonb_to_recordset(CAST(@batch AS jsonb)) AS i(event_id uuid) ON i.event_id = o.event_id
            WHERE o.tenant_id = @tenant
            """, ("batch", wireBatch), ("tenant", stream.TenantId)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var value = ReadEvent(reader, stream.TenantId);
                existing.Add(value.EventId, value);
            }
        }

        var receipts = new EventAppendReceipt[events.Count];
        var appended = 0;
        for (var i = 0; i < events.Count; i++)
        {
            var value = events[i];
            if (existing.TryGetValue(value.EventId, out var prior))
            {
                if (prior.Stream != stream || !string.Equals(prior.EventType, value.EventType, StringComparison.Ordinal) ||
                    prior.Version != value.Version || prior.OccurredAt != PostgreSqlTimestamp(value.OccurredAt) ||
                    !prior.Payload.Span.SequenceEqual(value.Payload.Span))
                {
                    throw new EventIdentityConflictException(value.EventId);
                }

                receipts[i] = new EventAppendReceipt(value.EventId, prior.Sequence, true);
            }
            else
            {
                receipts[i] = new EventAppendReceipt(value.EventId, checked(lastSequence + ++appended), false);
            }
        }

        if (appended > 0)
        {
            var freshBatch = existing.Count == 0 ? wireBatch : SerializeBatch(events.Where(value => !existing.ContainsKey(value.EventId)).ToArray());
            await ExecuteAsync(connection, transaction, $"""
                WITH incoming AS (
                    SELECT i.* FROM jsonb_to_recordset(CAST(@batch AS jsonb)) AS i(
                        ordinal integer, event_id uuid, event_type text, version integer, occurred_at timestamptz, payload text)
                ), fresh AS (
                    SELECT i.*, row_number() OVER (ORDER BY ordinal) AS sequence_offset
                    FROM incoming i
                )
                INSERT INTO {_schema}.outbox (tenant_id, stream_id, sequence, event_id, event_type, version, occurred_at, payload)
                SELECT @tenant, @stream, @base + sequence_offset, event_id, event_type, version, occurred_at, decode(payload, 'base64')
                FROM fresh ORDER BY ordinal
                """, cancellationToken, ("batch", freshBatch), ("tenant", stream.TenantId), ("stream", stream.StreamId),
                ("base", lastSequence)).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, $"""
                UPDATE {_schema}.streams SET last_sequence = @last WHERE tenant_id = @tenant AND stream_id = @stream
                """, cancellationToken, ("last", checked(lastSequence + appended)),
                ("tenant", stream.TenantId), ("stream", stream.StreamId)).ConfigureAwait(false);
        }

        EventsDiagnostics.Prepared(appended, receipts.Length - appended);
        return receipts;
    }

    /// <summary>Read immutable events after an exclusive sequence. All reads require an explicit tenant and stream.</summary>
    public async ValueTask<IReadOnlyList<StoredEvent>> ReadAsync(EventStreamKey stream, long afterSequence = 0,
        int maximumEvents = 256, int maximumPayloadBytes = 8_388_608, CancellationToken cancellationToken = default)
    {
        ValidateRead(stream, afterSequence, maximumEvents, maximumPayloadBytes);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadAsync(connection, null, stream, afterSequence, maximumEvents, maximumPayloadBytes, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Run a handler once per consumer/tenant/event in the caller's transaction. Duplicate delivery waits
    /// for any concurrent delivery to commit or roll back. On handler failure the caller must roll back.
    /// This guarantee covers database effects in this transaction, not external network side effects.
    /// </summary>
    public async ValueTask<bool> ProcessInboxAsync(DbConnection connection, DbTransaction transaction,
        string consumerId, StoredEvent value, EventTransactionHandler handler, CancellationToken cancellationToken = default)
    {
        EventValidation.Transaction(connection, transaction);
        EventValidation.Key(consumerId, nameof(consumerId));
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(value.Stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value.Sequence);
        EventValidation.Key(value.EventType, nameof(value));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value.Version);
        if (value.EventId == Guid.Empty || value.Payload.IsEmpty || value.Payload.Length > _options.MaximumEventBytes)
        {
            throw new ArgumentException("Inbox events require a stable nonempty identity and a payload within the configured event bound.", nameof(value));
        }

        var payloadHash = SHA256.HashData(value.Payload.Span);
        await using var command = Command(connection, transaction, $"""
            INSERT INTO {_schema}.inbox (consumer_id, tenant_id, event_id, stream_id, sequence, event_type, version, occurred_at, payload_hash)
            VALUES (@consumer, @tenant, @event, @stream, @sequence, @type, @version, @occurred, @hash)
            ON CONFLICT (consumer_id, tenant_id, event_id) DO NOTHING RETURNING sequence
            """, ("consumer", consumerId), ("tenant", value.Stream.TenantId), ("event", value.EventId),
            ("stream", value.Stream.StreamId), ("sequence", value.Sequence), ("type", value.EventType),
            ("version", value.Version), ("occurred", PostgreSqlTimestamp(value.OccurredAt).UtcDateTime), ("hash", payloadHash));
        var inserted = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (inserted is null || inserted is DBNull)
        {
            await using var duplicate = Command(connection, transaction, $"""
                SELECT stream_id, sequence, event_type, version, occurred_at, payload_hash
                FROM {_schema}.inbox WHERE consumer_id = @consumer AND tenant_id = @tenant AND event_id = @event
                """, ("consumer", consumerId), ("tenant", value.Stream.TenantId), ("event", value.EventId));
            await using var reader = await duplicate.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                !string.Equals(reader.GetString(0), value.Stream.StreamId, StringComparison.Ordinal) || reader.GetInt64(1) != value.Sequence ||
                !string.Equals(reader.GetString(2), value.EventType, StringComparison.Ordinal) || reader.GetInt32(3) != value.Version ||
                reader.GetDateTime(4).Ticks != PostgreSqlTimestamp(value.OccurredAt).UtcDateTime.Ticks ||
                !reader.GetFieldValue<byte[]>(5).AsSpan().SequenceEqual(payloadHash))
            {
                throw new EventIdentityConflictException(value.EventId);
            }

            EventsDiagnostics.InboxAttempt("duplicate");
            return false;
        }

        try { await handler(value, connection, transaction, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { EventsDiagnostics.InboxAttempt("canceled"); throw; }
        catch { EventsDiagnostics.InboxAttempt("failed"); throw; }
        EventsDiagnostics.InboxAttempt("prepared");
        return true;
    }

    /// <summary>Acquire a replay lease. A successful acquisition always advances the fencing token.</summary>
    public async ValueTask<EventReplayLease?> AcquireReplayAsync(string consumerId, EventStreamKey stream,
        string ownerId, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        EventValidation.Key(consumerId, nameof(consumerId));
        ArgumentNullException.ThrowIfNull(stream);
        EventValidation.Key(ownerId, nameof(ownerId));
        ValidateDuration(duration);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"""
            INSERT INTO {_schema}.streams (tenant_id, stream_id) VALUES (@tenant, @stream) ON CONFLICT DO NOTHING
            """, cancellationToken, ("tenant", stream.TenantId), ("stream", stream.StreamId)).ConfigureAwait(false);
        long? token;
        await using (var command = Command(connection, transaction, $"""
            INSERT INTO {_schema}.replay (consumer_id, tenant_id, stream_id, owner_id, fencing_token, expires_at)
            VALUES (@consumer, @tenant, @stream, @owner, 1, clock_timestamp() + @milliseconds * interval '1 millisecond')
            ON CONFLICT (consumer_id, tenant_id, stream_id) DO UPDATE
            SET owner_id = @owner, fencing_token = {_schema}.replay.fencing_token + 1,
                expires_at = clock_timestamp() + @milliseconds * interval '1 millisecond'
            WHERE {_schema}.replay.expires_at IS NULL OR {_schema}.replay.expires_at <= clock_timestamp()
            RETURNING fencing_token
            """, ("consumer", consumerId), ("tenant", stream.TenantId), ("stream", stream.StreamId),
            ("owner", ownerId), ("milliseconds", duration.TotalMilliseconds)))
        {
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            token = result is null || result is DBNull ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return token is { } fencingToken ? new EventReplayLease(consumerId, stream, ownerId, fencingToken) : null;
    }

    public async ValueTask<bool> RenewReplayAsync(EventReplayLease lease, TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ValidateLease(lease);
        ValidateDuration(duration);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = LeaseCommand(connection, null, lease, $"""
            UPDATE {_schema}.replay SET expires_at = clock_timestamp() + @milliseconds * interval '1 millisecond'
            WHERE consumer_id = @consumer AND tenant_id = @tenant AND stream_id = @stream
                AND owner_id = @owner AND fencing_token = @token AND expires_at > clock_timestamp()
            """, ("milliseconds", duration.TotalMilliseconds));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async ValueTask<bool> ReleaseReplayAsync(EventReplayLease lease, CancellationToken cancellationToken = default)
    {
        ValidateLease(lease);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = LeaseCommand(connection, null, lease, $"""
            UPDATE {_schema}.replay SET owner_id = NULL, expires_at = NULL
            WHERE consumer_id = @consumer AND tenant_id = @tenant AND stream_id = @stream
                AND owner_id = @owner AND fencing_token = @token
            """);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <summary>
    /// Replay one bounded batch; inbox, handler database effects, and checkpoint commit atomically.
    /// Replay row locking serializes batches and acquisitions. Expired workers cannot commit a checkpoint.
    /// Use a lease duration longer than a batch's upper-bound handling time and renew between batches.
    /// </summary>
    public async ValueTask<EventReplayResult> ReplayAsync(EventReplayLease lease, EventTransactionHandler handler,
        int maximumEvents = 256, int maximumPayloadBytes = 8_388_608, CancellationToken cancellationToken = default)
    {
        ValidateLease(lease);
        ArgumentNullException.ThrowIfNull(handler);
        ValidateRead(lease.Stream, 0, maximumEvents, maximumPayloadBytes);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);
        long checkpoint;
        await using (var command = LeaseCommand(connection, transaction, lease, $"""
            SELECT checkpoint FROM {_schema}.replay
            WHERE consumer_id = @consumer AND tenant_id = @tenant AND stream_id = @stream
                AND owner_id = @owner AND fencing_token = @token AND expires_at > clock_timestamp()
            FOR UPDATE
            """))
        {
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is null || result is DBNull)
            {
                throw new EventReplayFencedException();
            }

            checkpoint = Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }

        var events = await ReadAsync(connection, transaction, lease.Stream, checkpoint, maximumEvents,
            maximumPayloadBytes, cancellationToken).ConfigureAwait(false);
        var handled = 0;
        foreach (var value in events)
        {
            if (value.Sequence != checked(checkpoint + 1))
            {
                throw new InvalidOperationException("The outbox has a sequence gap; replay cannot silently skip missing events.");
            }

            if (await ProcessInboxAsync(connection, transaction, lease.ConsumerId, value, handler, cancellationToken).ConfigureAwait(false))
            {
                handled++;
            }

            checkpoint = value.Sequence;
        }

        await using (var command = LeaseCommand(connection, transaction, lease, $"""
            UPDATE {_schema}.replay SET checkpoint = @checkpoint
            WHERE consumer_id = @consumer AND tenant_id = @tenant AND stream_id = @stream
                AND owner_id = @owner AND fencing_token = @token AND expires_at > clock_timestamp()
            """, ("checkpoint", checkpoint)))
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new EventReplayFencedException();
            }
        }

        long head;
        await using (var command = Command(connection, transaction, $"""
            SELECT last_sequence FROM {_schema}.streams WHERE tenant_id = @tenant AND stream_id = @stream
            """, ("tenant", lease.Stream.TenantId), ("stream", lease.Stream.StreamId)))
        {
            head = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        EventsDiagnostics.ReplayCommitted(handled, Math.Max(0, head - checkpoint));
        return new EventReplayResult(handled, checkpoint, checkpoint >= head);
    }

    public async ValueTask<long> ReadCheckpointAsync(string consumerId, EventStreamKey stream,
        CancellationToken cancellationToken = default)
    {
        EventValidation.Key(consumerId, nameof(consumerId));
        ArgumentNullException.ThrowIfNull(stream);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, $"""
            SELECT checkpoint FROM {_schema}.replay WHERE consumer_id = @consumer AND tenant_id = @tenant AND stream_id = @stream
            """, ("consumer", consumerId), ("tenant", stream.TenantId), ("stream", stream.StreamId));
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null || result is DBNull ? 0 : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private async ValueTask<IReadOnlyList<StoredEvent>> ReadAsync(DbConnection connection, DbTransaction? transaction,
        EventStreamKey stream, long afterSequence, int maximumEvents, int maximumPayloadBytes, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"""
            WITH candidates AS MATERIALIZED (
                SELECT stream_id, sequence, event_id, event_type, version, occurred_at, payload
                FROM {_schema}.outbox WHERE tenant_id = @tenant AND stream_id = @stream AND sequence > @after
                ORDER BY sequence LIMIT @count
            ), bounded AS (
                SELECT *, sum(octet_length(payload)) OVER (ORDER BY sequence) AS payload_bytes FROM candidates
            )
            SELECT stream_id, sequence, event_id, event_type, version, occurred_at, payload
            FROM bounded WHERE payload_bytes <= @bytes ORDER BY sequence
            """, ("tenant", stream.TenantId), ("stream", stream.StreamId), ("after", afterSequence),
            ("count", maximumEvents), ("bytes", maximumPayloadBytes));
        var result = new List<StoredEvent>(Math.Min(maximumEvents, 256));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadEvent(reader, stream.TenantId));
        }

        return result;
    }

    private static StoredEvent ReadEvent(DbDataReader reader, string tenantId) => new(
        new EventStreamKey(tenantId, reader.GetString(0)), reader.GetInt64(1), reader.GetGuid(2), reader.GetString(3),
        reader.GetInt32(4), new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc)),
        reader.GetFieldValue<byte[]>(6));

    private void ValidateRead(EventStreamKey stream, long afterSequence, int maximumEvents, int maximumPayloadBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEvents);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumEvents, _options.MaximumAppendEvents);
        // A byte budget smaller than an allowed individual event can permanently stall the cursor.
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPayloadBytes, _options.MaximumEventBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumPayloadBytes, _options.MaximumAppendBytes);
    }

    private static void ValidateLease(EventReplayLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        EventValidation.Key(lease.ConsumerId, nameof(lease));
        EventValidation.Key(lease.OwnerId, nameof(lease));
        ArgumentNullException.ThrowIfNull(lease.Stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lease.FencingToken);
    }

    private static void ValidateDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.FromMilliseconds(1) || duration > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Replay leases must be between 1 ms and 24 hours.");
        }
    }

    private DbCommand LeaseCommand(DbConnection connection, DbTransaction? transaction, EventReplayLease lease,
        string sql, params (string Name, object Value)[] additional)
    {
        var command = Command(connection, transaction, sql, ("consumer", lease.ConsumerId), ("tenant", lease.Stream.TenantId),
            ("stream", lease.Stream.StreamId), ("owner", lease.OwnerId), ("token", lease.FencingToken));
        foreach (var parameter in additional)
        {
            AddParameter(command, parameter.Name, parameter.Value);
        }

        return command;
    }

    private DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = _options.CommandTimeoutSeconds;
        foreach (var parameter in parameters)
        {
            AddParameter(command, parameter.Name, parameter.Value);
        }

        return command;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private async ValueTask ExecuteAsync(DbConnection connection, DbTransaction? transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DateTimeOffset PostgreSqlTimestamp(DateTimeOffset value) =>
        new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);

    private static string SerializeBatch(IReadOnlyList<EventWrite> events)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            for (var i = 0; i < events.Count; i++)
            {
                var value = events[i];
                writer.WriteStartObject();
                writer.WriteNumber("ordinal", i);
                writer.WriteString("event_id", value.EventId);
                writer.WriteString("event_type", value.EventType);
                writer.WriteNumber("version", value.Version);
                writer.WriteString("occurred_at", PostgreSqlTimestamp(value.OccurredAt));
                writer.WriteBase64String("payload", value.Payload.Span);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
