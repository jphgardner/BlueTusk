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

    /// <summary>Install or migrate the schema. Run during deployment, not on application hot paths.</summary>
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
                archived_through bigint NOT NULL DEFAULT 0 CHECK (archived_through >= 0),
                retained_through bigint NOT NULL DEFAULT 0 CHECK (retained_through >= 0),
                local_retention_enabled boolean NOT NULL DEFAULT false,
                retention_operator text NULL CHECK (length(retention_operator) BETWEEN 1 AND 200),
                retention_change_ref text NULL CHECK (length(retention_change_ref) BETWEEN 1 AND 200),
                retention_enabled_at timestamptz NULL,
                CONSTRAINT streams_retention_bounds
                    CHECK (retained_through <= archived_through AND archived_through <= last_sequence),
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
            );
            CREATE TABLE IF NOT EXISTS {_schema}.event_identities (
                tenant_id text NOT NULL, event_id uuid NOT NULL, stream_id text NOT NULL,
                sequence bigint NOT NULL CHECK (sequence > 0), event_type text NOT NULL,
                version integer NOT NULL, occurred_at timestamptz NOT NULL,
                payload_length integer NOT NULL CHECK (payload_length > 0),
                payload_sha256 bytea NULL CHECK (payload_sha256 IS NULL OR octet_length(payload_sha256) = 32),
                PRIMARY KEY (tenant_id, event_id),
                UNIQUE (tenant_id, stream_id, sequence),
                FOREIGN KEY (tenant_id, stream_id) REFERENCES {_schema}.streams (tenant_id, stream_id)
            );
            CREATE TABLE IF NOT EXISTS {_schema}.archive_segments (
                tenant_id text NOT NULL, stream_id text NOT NULL,
                first_sequence bigint NOT NULL CHECK (first_sequence > 0),
                last_sequence bigint NOT NULL CHECK (last_sequence >= first_sequence),
                archive_id text NOT NULL CHECK (length(archive_id) BETWEEN 1 AND 2048),
                batch_sha256 bytea NOT NULL CHECK (octet_length(batch_sha256) = 32),
                event_count integer NOT NULL CHECK (event_count > 0),
                archived_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                PRIMARY KEY (tenant_id, stream_id, first_sequence),
                FOREIGN KEY (tenant_id, stream_id) REFERENCES {_schema}.streams (tenant_id, stream_id)
            )
            """, cancellationToken).ConfigureAwait(false);
        int version;
        await using (var command = Command(connection, transaction, $"SELECT version FROM {_schema}.schema_version WHERE singleton"))
        {
            version = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (version is not 1 and not 2 and not 3 and not 4)
            {
                throw new InvalidOperationException($"Unsupported BlueTusk.Events schema version {version}.");
            }
        }

        if (version == 1)
        {
            await ExecuteAsync(connection, transaction, $"""
                ALTER TABLE {_schema}.streams ADD COLUMN IF NOT EXISTS archived_through bigint NOT NULL DEFAULT 0;
                ALTER TABLE {_schema}.streams ADD COLUMN IF NOT EXISTS retained_through bigint NOT NULL DEFAULT 0;
                ALTER TABLE {_schema}.streams ADD COLUMN IF NOT EXISTS local_retention_enabled boolean NOT NULL DEFAULT false;
                ALTER TABLE {_schema}.streams ADD COLUMN IF NOT EXISTS retention_operator text NULL;
                ALTER TABLE {_schema}.streams ADD COLUMN IF NOT EXISTS retention_change_ref text NULL;
                ALTER TABLE {_schema}.streams ADD COLUMN IF NOT EXISTS retention_enabled_at timestamptz NULL;
                DO $migration$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint
                        WHERE conname='streams_retention_bounds' AND conrelid='{_schema}.streams'::regclass)
                    THEN
                        ALTER TABLE {_schema}.streams ADD CONSTRAINT streams_retention_bounds
                            CHECK (retained_through <= archived_through AND archived_through <= last_sequence);
                    END IF;
                END $migration$;
                UPDATE {_schema}.schema_version SET version = 2 WHERE singleton
                """, cancellationToken).ConfigureAwait(false);
        }

        // The control relation is deliberately separate from the outbox. Deployment must add it to
        // every outbox publication before the first intent is emitted; no published delete is enabled.
        await ExecuteAsync(connection, transaction, $"""
            CREATE TABLE IF NOT EXISTS {_schema}.published_retention_intents (
                retention_epoch uuid PRIMARY KEY,
                tenant_id text NOT NULL CHECK (length(tenant_id) BETWEEN 1 AND 200),
                stream_id text NOT NULL CHECK (length(stream_id) BETWEEN 1 AND 200),
                first_sequence bigint NOT NULL CHECK (first_sequence > 0),
                through_sequence bigint NOT NULL CHECK (through_sequence >= first_sequence),
                archive_manifest_sha256 bytea NOT NULL CHECK (octet_length(archive_manifest_sha256) = 32),
                source_system_identifier text NOT NULL,
                source_database text NOT NULL,
                source_database_oid oid NOT NULL,
                source_timeline bigint NOT NULL CHECK (source_timeline > 0),
                source_slot text NOT NULL,
                source_publication text NOT NULL,
                source_publication_oid oid NOT NULL,
                recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                UNIQUE (tenant_id, stream_id, through_sequence),
                FOREIGN KEY (tenant_id, stream_id) REFERENCES {_schema}.streams (tenant_id, stream_id)
            );
            CREATE OR REPLACE FUNCTION {_schema}.reject_retention_intent_mutation() RETURNS trigger LANGUAGE plpgsql AS $trigger$
            BEGIN
                RAISE EXCEPTION 'Published retention intents are append-only';
            END $trigger$;
            DO $migration$ BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_trigger
                    WHERE tgname='retention_intent_immutable' AND tgrelid='{_schema}.published_retention_intents'::regclass)
                THEN
                    CREATE TRIGGER retention_intent_immutable BEFORE UPDATE OR DELETE OR TRUNCATE
                        ON {_schema}.published_retention_intents FOR EACH STATEMENT
                        EXECUTE FUNCTION {_schema}.reject_retention_intent_mutation();
                END IF;
            END $migration$;
            UPDATE {_schema}.schema_version SET version = 3 WHERE singleton AND version = 2
            """, cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, transaction, $"""
            ALTER TABLE {_schema}.streams ADD COLUMN IF NOT EXISTS published_membership_revision
                bigint NOT NULL DEFAULT 0 CHECK (published_membership_revision >= 0);
            ALTER TABLE {_schema}.published_retention_intents ADD COLUMN IF NOT EXISTS membership_revision
                bigint NOT NULL DEFAULT 0 CHECK (membership_revision >= 0);
            CREATE TABLE IF NOT EXISTS {_schema}.published_retention_members (
                tenant_id text NOT NULL, stream_id text NOT NULL,
                consumer_group text NOT NULL CHECK (length(consumer_group) BETWEEN 1 AND 200),
                target_incarnation uuid NOT NULL CHECK (target_incarnation <> '00000000-0000-0000-0000-000000000000'::uuid),
                membership_revision bigint NOT NULL CHECK (membership_revision > 0),
                source_system_identifier text NOT NULL, source_database text NOT NULL,
                source_database_oid oid NOT NULL, source_timeline bigint NOT NULL,
                source_slot text NOT NULL, source_publication text NOT NULL, source_publication_oid oid NOT NULL,
                registered_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                PRIMARY KEY (tenant_id,stream_id,consumer_group,target_incarnation),
                UNIQUE (tenant_id,stream_id,membership_revision),
                FOREIGN KEY (tenant_id,stream_id) REFERENCES {_schema}.streams (tenant_id,stream_id)
            );
            CREATE TABLE IF NOT EXISTS {_schema}.published_retention_targets (
                tenant_id text NOT NULL, stream_id text NOT NULL, consumer_group text NOT NULL,
                target_incarnation uuid NOT NULL, membership_revision bigint NOT NULL CHECK (membership_revision > 0),
                source_system_identifier text NOT NULL, source_database text NOT NULL,
                source_database_oid oid NOT NULL, source_timeline bigint NOT NULL,
                source_slot text NOT NULL, source_publication text NOT NULL, source_publication_oid oid NOT NULL,
                target_system_identifier text NOT NULL, target_database text NOT NULL,
                target_database_oid oid NOT NULL, target_timeline bigint NOT NULL,
                registered_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                PRIMARY KEY (tenant_id,stream_id,consumer_group,target_incarnation)
            );
            CREATE TABLE IF NOT EXISTS {_schema}.published_retention_target_checkpoints (
                consumer_group text NOT NULL, target_incarnation uuid NOT NULL,
                source_system_identifier text NOT NULL, source_database text NOT NULL,
                source_slot text NOT NULL, source_publication text NOT NULL, source_timeline bigint NOT NULL,
                commit_end_position numeric(20,0) NOT NULL CHECK (commit_end_position >= 0),
                source_transaction_id bigint NOT NULL CHECK (source_transaction_id >= 0),
                updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                PRIMARY KEY (consumer_group,target_incarnation,source_system_identifier,source_database,
                    source_slot,source_publication,source_timeline)
            );
            CREATE TABLE IF NOT EXISTS {_schema}.published_retention_acknowledgements (
                retention_epoch uuid NOT NULL, tenant_id text NOT NULL, stream_id text NOT NULL,
                consumer_group text NOT NULL, target_incarnation uuid NOT NULL,
                first_sequence bigint NOT NULL, through_sequence bigint NOT NULL,
                archive_manifest_sha256 bytea NOT NULL CHECK (octet_length(archive_manifest_sha256)=32),
                membership_revision bigint NOT NULL,
                source_system_identifier text NOT NULL, source_database text NOT NULL,
                source_database_oid oid NOT NULL, source_timeline bigint NOT NULL,
                source_slot text NOT NULL, source_publication text NOT NULL, source_publication_oid oid NOT NULL,
                target_system_identifier text NOT NULL, target_database text NOT NULL,
                target_database_oid oid NOT NULL, target_timeline bigint NOT NULL,
                commit_end_position numeric(20,0) NOT NULL CHECK (commit_end_position > 0),
                acknowledged_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                PRIMARY KEY (retention_epoch,consumer_group,target_incarnation),
                FOREIGN KEY (tenant_id,stream_id,consumer_group,target_incarnation)
                    REFERENCES {_schema}.published_retention_targets (tenant_id,stream_id,consumer_group,target_incarnation)
            );
            DO $migration$ BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_trigger
                    WHERE tgname='retention_members_immutable' AND tgrelid='{_schema}.published_retention_members'::regclass)
                THEN
                    CREATE TRIGGER retention_members_immutable BEFORE UPDATE OR DELETE OR TRUNCATE
                        ON {_schema}.published_retention_members FOR EACH STATEMENT
                        EXECUTE FUNCTION {_schema}.reject_retention_intent_mutation();
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_trigger
                    WHERE tgname='retention_targets_immutable' AND tgrelid='{_schema}.published_retention_targets'::regclass)
                THEN
                    CREATE TRIGGER retention_targets_immutable BEFORE UPDATE OR DELETE OR TRUNCATE
                        ON {_schema}.published_retention_targets FOR EACH STATEMENT
                        EXECUTE FUNCTION {_schema}.reject_retention_intent_mutation();
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_trigger
                    WHERE tgname='retention_acks_immutable' AND tgrelid='{_schema}.published_retention_acknowledgements'::regclass)
                THEN
                    CREATE TRIGGER retention_acks_immutable BEFORE UPDATE OR DELETE OR TRUNCATE
                        ON {_schema}.published_retention_acknowledgements FOR EACH STATEMENT
                        EXECUTE FUNCTION {_schema}.reject_retention_intent_mutation();
                END IF;
            END $migration$;
            UPDATE {_schema}.schema_version SET version = 4 WHERE singleton AND version = 3
            """, cancellationToken).ConfigureAwait(false);

        // The database-owned insert fence also covers an older application binary that still writes
        // outbox rows during a rolling deployment. It cannot reuse an ID whose payload was pruned.
        await ExecuteAsync(connection, transaction, $"""
            CREATE OR REPLACE FUNCTION {_schema}.record_outbox_identity() RETURNS trigger LANGUAGE plpgsql AS $trigger$
            BEGIN
                INSERT INTO {_schema}.event_identities
                    (tenant_id,event_id,stream_id,sequence,event_type,version,occurred_at,payload_length,payload_sha256)
                VALUES(NEW.tenant_id,NEW.event_id,NEW.stream_id,NEW.sequence,NEW.event_type,NEW.version,
                    NEW.occurred_at,octet_length(NEW.payload),sha256(NEW.payload));
                RETURN NEW;
            END $trigger$;
            DO $migration$ BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_trigger
                    WHERE tgname='outbox_identity_insert' AND tgrelid='{_schema}.outbox'::regclass)
                THEN
                    CREATE TRIGGER outbox_identity_insert AFTER INSERT ON {_schema}.outbox
                        FOR EACH ROW EXECUTE FUNCTION {_schema}.record_outbox_identity();
                END IF;
            END $migration$
            """, cancellationToken).ConfigureAwait(false);

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
        var existing = new Dictionary<Guid, EventIdentity>();
        await using (var command = Command(connection, transaction, $"""
            WITH incoming AS MATERIALIZED (
                SELECT event_id FROM jsonb_to_recordset(CAST(@batch AS jsonb)) AS i(event_id uuid)
            )
            SELECT e.stream_id, e.sequence, e.event_id, e.event_type, e.version, e.occurred_at,
                e.payload_length, e.payload_sha256, o.payload
            FROM {_schema}.event_identities e
            JOIN incoming i ON i.event_id = e.event_id
            LEFT JOIN {_schema}.outbox o ON o.tenant_id=e.tenant_id AND o.stream_id=e.stream_id AND o.sequence=e.sequence
            WHERE e.tenant_id = @tenant
            UNION ALL
            SELECT o.stream_id,o.sequence,o.event_id,o.event_type,o.version,o.occurred_at,
                octet_length(o.payload),NULL::bytea,o.payload
            FROM {_schema}.outbox o JOIN incoming i ON i.event_id=o.event_id
            WHERE o.tenant_id=@tenant AND NOT EXISTS (
                SELECT 1 FROM {_schema}.event_identities e WHERE e.tenant_id=o.tenant_id AND e.event_id=o.event_id)
            """, ("batch", wireBatch), ("tenant", stream.TenantId)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var value = new EventIdentity(reader.GetString(0), reader.GetInt64(1), reader.GetGuid(2),
                    reader.GetString(3), reader.GetInt32(4),
                    new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc)),
                    reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetFieldValue<byte[]>(7),
                    reader.IsDBNull(8) ? null : reader.GetFieldValue<byte[]>(8));
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
                if (!string.Equals(prior.StreamId, stream.StreamId, StringComparison.Ordinal) ||
                    !string.Equals(prior.EventType, value.EventType, StringComparison.Ordinal) ||
                    prior.Version != value.Version || prior.OccurredAt != PostgreSqlTimestamp(value.OccurredAt) ||
                    prior.PayloadLength != value.Payload.Length ||
                    (prior.Payload is not null
                        ? !prior.Payload.AsSpan().SequenceEqual(value.Payload.Span)
                        : prior.PayloadSha256 is null || !prior.PayloadSha256.AsSpan().SequenceEqual(SHA256.HashData(value.Payload.Span))))
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
        await using (var admission = Command(connection, transaction, $"""
            SELECT retained_through FROM {_schema}.streams WHERE tenant_id=@tenant AND stream_id=@stream FOR UPDATE
            """, ("tenant", stream.TenantId), ("stream", stream.StreamId)))
        {
            var retainedThrough = Convert.ToInt64(await admission.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (retainedThrough > 0)
            {
                await using var registered = Command(connection, transaction, $"""
                    SELECT checkpoint FROM {_schema}.replay
                    WHERE consumer_id=@consumer AND tenant_id=@tenant AND stream_id=@stream
                    """, ("consumer", consumerId), ("tenant", stream.TenantId), ("stream", stream.StreamId));
                var checkpoint = await registered.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (checkpoint is null or DBNull || Convert.ToInt64(checkpoint, CultureInfo.InvariantCulture) < retainedThrough)
                {
                    throw new EventHistoryUnavailableException(stream, retainedThrough);
                }
            }
        }
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
            WITH floor AS MATERIALIZED (
                SELECT COALESCE((SELECT retained_through FROM {_schema}.streams
                    WHERE tenant_id=@tenant AND stream_id=@stream),0) AS retained_through
            ), candidates AS MATERIALIZED (
                SELECT stream_id, sequence, event_id, event_type, version, occurred_at, payload
                FROM {_schema}.outbox WHERE tenant_id = @tenant AND stream_id = @stream AND sequence > @after
                ORDER BY sequence LIMIT @count
            ), bounded AS (
                SELECT *, sum(octet_length(payload)) OVER (ORDER BY sequence) AS payload_bytes FROM candidates
            )
            SELECT floor.retained_through,b.stream_id,b.sequence,b.event_id,b.event_type,b.version,b.occurred_at,b.payload
            FROM floor LEFT JOIN bounded b ON b.payload_bytes <= @bytes ORDER BY b.sequence
            """, ("tenant", stream.TenantId), ("stream", stream.StreamId), ("after", afterSequence),
            ("count", maximumEvents), ("bytes", maximumPayloadBytes));
        var result = new List<StoredEvent>(Math.Min(maximumEvents, 256));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var retainedThrough = reader.GetInt64(0);
            if (afterSequence < retainedThrough)
            {
                throw new EventHistoryUnavailableException(stream, retainedThrough);
            }

            if (!reader.IsDBNull(1))
            {
                result.Add(ReadEvent(reader, stream.TenantId, 1));
            }
        }

        return result;
    }

    private static StoredEvent ReadEvent(DbDataReader reader, string tenantId, int offset) => new(
        new EventStreamKey(tenantId, reader.GetString(offset)), reader.GetInt64(offset + 1), reader.GetGuid(offset + 2),
        reader.GetString(offset + 3), reader.GetInt32(offset + 4),
        new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(offset + 5), DateTimeKind.Utc)),
        reader.GetFieldValue<byte[]>(offset + 6));

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

    private sealed record EventIdentity(string StreamId, long Sequence, Guid EventId, string EventType,
        int Version, DateTimeOffset OccurredAt, int PayloadLength, byte[]? PayloadSha256, byte[]? Payload);
}
