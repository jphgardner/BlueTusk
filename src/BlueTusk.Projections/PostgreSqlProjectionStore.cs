using System.Data;
using System.Data.Common;
using System.Globalization;
using BlueTusk.Streams;
using BlueTusk.TypeSystem;

namespace BlueTusk.Projections;

/// <summary>Versioned destination state and read models. Application effects and CDC checkpoint are colocated.</summary>
public sealed partial class PostgreSqlProjectionStore
{
    private readonly DbDataSource _dataSource;
    private readonly PostgreSqlProjectionsOptions _options;
    private readonly string _schema;

    public PostgreSqlProjectionStore(DbDataSource dataSource, PostgreSqlProjectionsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _options = options ?? new PostgreSqlProjectionsOptions();
        _options.Validate();
        _dataSource = dataSource;
        _schema = '"' + _options.Schema + '"';
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "SELECT pg_advisory_xact_lock(hashtextextended(@schema, 0))",
            cancellationToken, ("schema", _options.Schema)).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"CREATE SCHEMA IF NOT EXISTS {_schema}", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"""
            CREATE TABLE IF NOT EXISTS {_schema}.schema_version(singleton boolean PRIMARY KEY CHECK(singleton), version integer NOT NULL);
            INSERT INTO {_schema}.schema_version VALUES(true, 1) ON CONFLICT DO NOTHING;
            CREATE TABLE IF NOT EXISTS {_schema}.state (
                projection text NOT NULL, version integer NOT NULL CHECK(version > 0),
                definition_fingerprint text NOT NULL, source_fingerprint text NOT NULL,
                source_lineage text NULL,
                source_snapshot_contract jsonb NULL,
                phase integer NOT NULL DEFAULT 0 CHECK(phase BETWEEN 0 AND 3),
                checkpoint numeric(20,0) NOT NULL DEFAULT 0 CHECK(checkpoint BETWEEN 0 AND 18446744073709551615),
                generation bigint NOT NULL DEFAULT 0 CHECK(generation >= 0),
                snapshot_epoch uuid NULL, snapshot_position numeric(20,0) NULL,snapshot_started_at timestamptz NULL,
                expected_tables integer NOT NULL DEFAULT 0 CHECK(expected_tables >= 0),
                completed_tables integer NOT NULL DEFAULT 0 CHECK(completed_tables >= 0 AND completed_tables <= expected_tables),
                snapshot_rows bigint NOT NULL DEFAULT 0 CHECK(snapshot_rows >= 0),
                owner_id text NULL, expires_at timestamptz NULL, fencing_token bigint NOT NULL DEFAULT 0 CHECK(fencing_token >= 0),
                PRIMARY KEY(projection, version)
            );
            CREATE TABLE IF NOT EXISTS {_schema}.heads (
                projection text PRIMARY KEY, active_version integer NULL,
                publication_revision bigint NOT NULL DEFAULT 0 CHECK(publication_revision >= 0),
                FOREIGN KEY(projection, active_version) REFERENCES {_schema}.state(projection, version)
            );
            CREATE TABLE IF NOT EXISTS {_schema}.documents (
                projection text NOT NULL, version integer NOT NULL, tenant_id text NOT NULL,
                document_key text COLLATE "C" NOT NULL, payload bytea NOT NULL CHECK(octet_length(payload) > 0),
                PRIMARY KEY(projection, version, tenant_id, document_key),
                FOREIGN KEY(projection, version) REFERENCES {_schema}.state(projection, version)
            );
            CREATE TABLE IF NOT EXISTS {_schema}.dependencies (
                projection text NOT NULL, version integer NOT NULL, tenant_id text NOT NULL,
                document_key text COLLATE "C" NOT NULL, table_id text NOT NULL, key_id text NOT NULL,
                PRIMARY KEY(projection, version, tenant_id, document_key, table_id, key_id),
                FOREIGN KEY(projection, version, tenant_id, document_key)
                    REFERENCES {_schema}.documents(projection, version, tenant_id, document_key) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS dependencies_lookup ON {_schema}.dependencies(projection, version, tenant_id, table_id, key_id, document_key);
            CREATE TABLE IF NOT EXISTS {_schema}.source_rows (
                projection text NOT NULL, version integer NOT NULL, tenant_id text NOT NULL, table_id text NOT NULL,
                key_id text NOT NULL, payload bytea NOT NULL CHECK(octet_length(payload) > 0),
                PRIMARY KEY(projection, version, tenant_id, table_id, key_id),
                FOREIGN KEY(projection, version) REFERENCES {_schema}.state(projection, version)
            );
            CREATE TABLE IF NOT EXISTS {_schema}.aggregates (
                projection text NOT NULL, version integer NOT NULL, tenant_id text NOT NULL,
                group_key text NOT NULL, metric text NOT NULL, value numeric NOT NULL
                    CHECK(abs(value) <= 79228162514264337593543950335),
                PRIMARY KEY(projection, version, tenant_id, group_key, metric),
                FOREIGN KEY(projection, version) REFERENCES {_schema}.state(projection, version)
            );
            CREATE TABLE IF NOT EXISTS {_schema}.snapshot_tables (
                projection text NOT NULL, version integer NOT NULL, table_id text NOT NULL,
                next_sequence bigint NOT NULL DEFAULT 0, completed boolean NOT NULL DEFAULT false,
                PRIMARY KEY(projection, version, table_id),
                FOREIGN KEY(projection, version) REFERENCES {_schema}.state(projection, version)
            );
            CREATE TABLE IF NOT EXISTS {_schema}.snapshot_batches (
                projection text NOT NULL, version integer NOT NULL, table_id text NOT NULL,
                sequence bigint NOT NULL CHECK(sequence >= 0), fingerprint bytea NOT NULL CHECK(octet_length(fingerprint) = 32),
                PRIMARY KEY(projection, version, table_id, sequence),
                FOREIGN KEY(projection, version) REFERENCES {_schema}.state(projection, version)
            );
            CREATE TABLE IF NOT EXISTS {_schema}.snapshot_row_keys (
                projection text NOT NULL, version integer NOT NULL, table_id text NOT NULL, key_id text NOT NULL,
                PRIMARY KEY(projection, version, table_id, key_id),
                FOREIGN KEY(projection, version) REFERENCES {_schema}.state(projection, version)
            );
            CREATE TABLE IF NOT EXISTS {_schema}.retired_versions (
                projection text NOT NULL, version integer NOT NULL,
                retired_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                PRIMARY KEY(projection, version),
                FOREIGN KEY(projection, version) REFERENCES {_schema}.state(projection, version)
            );
            CREATE TABLE IF NOT EXISTS {_schema}.recovery_tickets (
                projection text NOT NULL, recovery_id uuid NOT NULL,
                active_version integer NOT NULL, candidate_version integer NOT NULL,
                target_lineage text NOT NULL, target_source text NOT NULL,
                minimum_snapshot_position numeric(20,0) NOT NULL,
                reason text NOT NULL, started_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                completed_at timestamptz NULL,
                PRIMARY KEY(projection,recovery_id),
                CHECK(active_version <> candidate_version),
                FOREIGN KEY(projection,active_version) REFERENCES {_schema}.state(projection,version),
                FOREIGN KEY(projection,candidate_version) REFERENCES {_schema}.state(projection,version)
            );
            CREATE TABLE IF NOT EXISTS {_schema}.maintenance_fences (
                projection text NOT NULL, version integer NOT NULL, maintenance_id uuid NOT NULL, reason text NOT NULL,
                started_at timestamptz NOT NULL DEFAULT clock_timestamp(), PRIMARY KEY(projection,version),
                FOREIGN KEY(projection,version) REFERENCES {_schema}.state(projection,version)
            )
            """, cancellationToken).ConfigureAwait(false);
        await using (var command = Command(connection, transaction, $"SELECT version FROM {_schema}.schema_version WHERE singleton"))
        {
            var schemaVersion = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (schemaVersion is not 1 and not 2 and not 3 and not 4 and not 5 and not 6)
            {
                throw new InvalidOperationException("The projection schema version is not supported.");
            }
        }

        await ExecuteAsync(connection, transaction, $"ALTER TABLE {_schema}.heads ADD COLUMN IF NOT EXISTS publication_revision bigint NOT NULL DEFAULT 0 CHECK(publication_revision >= 0); ALTER TABLE {_schema}.state ADD COLUMN IF NOT EXISTS source_lineage text NULL; ALTER TABLE {_schema}.state ADD COLUMN IF NOT EXISTS source_snapshot_contract jsonb NULL; ALTER TABLE {_schema}.state ADD COLUMN IF NOT EXISTS snapshot_started_at timestamptz NULL; ALTER TABLE {_schema}.state DROP CONSTRAINT IF EXISTS state_phase_check; ALTER TABLE {_schema}.state ADD CONSTRAINT state_phase_check CHECK(phase BETWEEN 0 AND 3); CREATE UNIQUE INDEX IF NOT EXISTS recovery_pending ON {_schema}.recovery_tickets(projection) WHERE completed_at IS NULL; UPDATE {_schema}.schema_version SET version = 6 WHERE singleton", cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RegisterAsync(ProjectionIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"""
            INSERT INTO {_schema}.state(projection, version, definition_fingerprint, source_fingerprint)
            VALUES(@projection, @version, @definition, @source) ON CONFLICT DO NOTHING
            """, cancellationToken, ("projection", identity.Name), ("version", identity.Version),
            ("definition", identity.DefinitionFingerprint), ("source", identity.Source.Fingerprint)).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"INSERT INTO {_schema}.heads(projection) VALUES(@projection) ON CONFLICT DO NOTHING",
            cancellationToken, ("projection", identity.Name)).ConfigureAwait(false);
        _ = await ReadStateAsync(connection, transaction, identity, forUpdate: true, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ProjectionState> ReadStateAsync(ProjectionIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return (await ReadStateAsync(connection, null, identity, false, cancellationToken).ConfigureAwait(false)).State;
    }

    /// <summary>Binds actual catalogue/timeline lineage before any snapshot or CDC work. Rebinding an existing history fails closed.</summary>
    public async ValueTask RegisterWithLineageAsync(ProjectionIdentity identity, ProjectionSourceLineage lineage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(lineage);
        if (identity.Source != lineage.Source) { throw new ArgumentException("Lineage evidence belongs to a different Streams source.", nameof(lineage)); }
        await RegisterAsync(identity, cancellationToken).ConfigureAwait(false);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var state = await ReadStateAsync(connection, transaction, identity, true, cancellationToken).ConfigureAwait(false);
        await using var read = Command(connection, transaction, $"SELECT source_lineage FROM {_schema}.state WHERE projection=@projection AND version=@version", ("projection", identity.Name), ("version", identity.Version));
        var prior = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (prior is string fingerprint)
        {
            if (!string.Equals(fingerprint, lineage.Fingerprint, StringComparison.Ordinal)) { throw new InvalidOperationException("The registered projection's source catalogue/timeline lineage changed. Rebuild under a new version and explicit recovery policy."); }
        }
        else
        {
            if (state.State.Phase != ProjectionBuildPhase.Empty || state.State.Generation != 0 || state.State.Checkpoint != BlueTuskLogSequenceNumber.Zero)
            {
                throw new InvalidOperationException("Lineage must be bound before snapshot/CDC coverage starts; an existing unverified history cannot be retroactively certified.");
            }
            await ExecuteAsync(connection, transaction, $"UPDATE {_schema}.state SET source_lineage=@lineage, source_snapshot_contract=CAST(@contract AS jsonb) WHERE projection=@projection AND version=@version",
                cancellationToken, ("projection", identity.Name), ("version", identity.Version), ("lineage", lineage.Fingerprint), ("contract", lineage.SnapshotContract)).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ProjectionLease?> AcquireAsync(ProjectionIdentity identity, string ownerId, TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ProjectionValidation.Key(ownerId, nameof(ownerId));
        ValidateDuration(duration);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = IdentityCommand(connection, null, identity, $"""
            UPDATE {_schema}.state SET owner_id = @owner, fencing_token = fencing_token + 1,
                expires_at = clock_timestamp() + @milliseconds * interval '1 millisecond'
            WHERE projection = @projection AND version = @version AND definition_fingerprint = @definition AND source_fingerprint = @source
                AND (expires_at IS NULL OR expires_at <= clock_timestamp())
                AND NOT EXISTS(SELECT 1 FROM {_schema}.retired_versions r WHERE r.projection = @projection AND r.version = @version)
                AND NOT EXISTS(SELECT 1 FROM {_schema}.recovery_tickets r WHERE r.projection = @projection AND r.active_version = @version)
                AND NOT EXISTS(SELECT 1 FROM {_schema}.maintenance_fences r WHERE r.projection = @projection AND r.version = @version)
            RETURNING fencing_token
            """, ("owner", ownerId), ("milliseconds", duration.TotalMilliseconds));
        var token = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return token is null || token is DBNull ? null : new ProjectionLease(identity, ownerId, Convert.ToInt64(token, CultureInfo.InvariantCulture));
    }

    public async ValueTask<bool> RenewAsync(ProjectionLease lease, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        ValidateLease(lease);
        ValidateDuration(duration);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = LeaseCommand(connection, null, lease, $"""
            UPDATE {_schema}.state SET expires_at = clock_timestamp() + @milliseconds * interval '1 millisecond'
            WHERE projection = @projection AND version = @version AND owner_id = @owner AND fencing_token = @token AND expires_at > clock_timestamp()
            """, ("milliseconds", duration.TotalMilliseconds));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async ValueTask<bool> ReleaseAsync(ProjectionLease lease, CancellationToken cancellationToken = default)
    {
        ValidateLease(lease);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = LeaseCommand(connection, null, lease, $"""
            UPDATE {_schema}.state SET owner_id = NULL, expires_at = NULL
            WHERE projection = @projection AND version = @version AND owner_id = @owner AND fencing_token = @token
            """);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <summary>Apply only an ordered committed Streams transaction. Two-phase and synthetic deliveries fail closed.</summary>
    public async ValueTask<ProjectionApplyResult> ApplyAsync(ProjectionLease lease, IProjectionDefinition definition,
        ChangeTransaction transaction, CancellationToken cancellationToken = default)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var outcome = "failed";
        try
        {
            var result = await ApplyCoreAsync(lease, definition, transaction, cancellationToken).ConfigureAwait(false);
            outcome = result.WasApplied ? "committed" : "duplicate";
            return result;
        }
        catch (OperationCanceledException) { outcome = "canceled"; throw; }
        finally { ProjectionsDiagnostics.Apply(started, outcome); }
    }

    private async ValueTask<ProjectionApplyResult> ApplyCoreAsync(ProjectionLease lease, IProjectionDefinition definition,
        ChangeTransaction transaction, CancellationToken cancellationToken)
    {
        ValidateDefinition(lease, definition);
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction.Source != lease.Identity.Source || transaction.Outcome != ChangeTransactionOutcome.Committed ||
            transaction.IsTwoPhase || transaction.IsSynthetic || transaction.CommitEndPosition == BlueTuskLogSequenceNumber.Zero)
        {
            throw new ArgumentException("Projection CDC requires an ordered non-synthetic committed transaction from the registered Streams source.", nameof(transaction));
        }

        if (transaction.Changes.Count > _options.MaximumChangesPerTransaction || transaction.Changes.EstimatedBytes > _options.MaximumTransactionBytes)
        {
            throw new ProjectionBoundExceededException("The source CDC transaction exceeds the configured change or byte bound.");
        }

        await ValidateChangeBytesAsync(transaction, cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var targetTransaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        // Promotion and application use the same heads-before-state lock order. The revision is
        // a commit-ordered materialized invalidation marker, never an independent CDC source.
        await LockHeadAsync(connection, targetTransaction, lease.Identity.Name, cancellationToken).ConfigureAwait(false);
        var locked = await ReadStateAsync(connection, targetTransaction, lease.Identity, true, cancellationToken).ConfigureAwait(false);
        EnsureLease(lease, locked);
        if (locked.State.Phase != ProjectionBuildPhase.CatchingUp)
        {
            throw new InvalidOperationException("Complete a consistent snapshot before applying live CDC transactions.");
        }

        if (transaction.CommitEndPosition <= locked.State.Checkpoint)
        {
            await targetTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ProjectionApplyResult(false, locked.State.Checkpoint, locked.State.Generation);
        }

        var context = new ProjectionWriteContext(connection, targetTransaction, lease.Identity, _options);
        try
        {
            await definition.ApplyTransactionAsync(transaction, context, cancellationToken).ConfigureAwait(false);
            context.EnsureComplete();
        }
        finally
        {
            context.Close();
        }

        await CheckpointAsync(connection, targetTransaction, lease, transaction.CommitEndPosition, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, targetTransaction, $"UPDATE {_schema}.heads SET publication_revision = publication_revision + 1 WHERE projection = @projection AND active_version = @version",
            cancellationToken, ("projection", lease.Identity.Name), ("version", lease.Identity.Version)).ConfigureAwait(false);
        await targetTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        ProjectionsDiagnostics.Commit("cdc", context);
        return new ProjectionApplyResult(true, transaction.CommitEndPosition, checked(locked.State.Generation + 1));
    }

    /// <summary>
    /// Atomically switch the public read pointer after snapshot completion and catch-up to the required
    /// barrier and current active version. Expected active version protects against concurrent operators.
    /// </summary>
    public ValueTask PromoteAsync(ProjectionLease lease, BlueTuskLogSequenceNumber requiredPosition,
        int? expectedActiveVersion, CancellationToken cancellationToken = default) =>
        PromoteCoreAsync(lease, requiredPosition, expectedActiveVersion, null, cancellationToken);

    /// <summary>Explicitly permits a different slot only with matching pre-snapshot persisted lineage and fresh actual candidate evidence.</summary>
    public ValueTask PromoteWithLineageAsync(ProjectionLease lease, BlueTuskLogSequenceNumber requiredPosition,
        int expectedActiveVersion, ProjectionCutoverEvidence freshCandidateEvidence, CancellationToken cancellationToken = default)
    {
        ValidateLease(lease);
        ArgumentNullException.ThrowIfNull(freshCandidateEvidence);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedActiveVersion);
        if (lease.Identity.Source != freshCandidateEvidence.Lineage.Source) { throw new ArgumentException("Fresh lineage belongs to a different candidate source.", nameof(freshCandidateEvidence)); }
        if (System.Diagnostics.Stopwatch.GetElapsedTime(freshCandidateEvidence.Lineage.CaptureTimestamp) > TimeSpan.FromMinutes(1))
        {
            throw new InvalidOperationException("Different-slot promotion requires source lineage evidence captured within the last minute. Recapture the actual source catalogue/timeline.");
        }
        var barrier = requiredPosition >= freshCandidateEvidence.BarrierPosition ? requiredPosition : freshCandidateEvidence.BarrierPosition;
        return PromoteCoreAsync(lease, barrier, expectedActiveVersion, freshCandidateEvidence.Lineage, cancellationToken);
    }

    private async ValueTask PromoteCoreAsync(ProjectionLease lease, BlueTuskLogSequenceNumber requiredPosition,
        int? expectedActiveVersion, ProjectionSourceLineage? evidence, CancellationToken cancellationToken)
    {
        ValidateLease(lease);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        int? currentVersion;
        await using (var command = Command(connection, transaction, $"SELECT active_version FROM {_schema}.heads WHERE projection = @projection FOR UPDATE",
                         ("projection", lease.Identity.Name)))
        {
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            currentVersion = value is null || value is DBNull ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        if (currentVersion != expectedActiveVersion)
        {
            throw new InvalidOperationException("The active projection version changed; reread it before attempting cutover.");
        }
        await using (var recovery = Command(connection, transaction,
                         $"SELECT EXISTS(SELECT 1 FROM {_schema}.recovery_tickets WHERE projection=@projection AND completed_at IS NULL) OR EXISTS(SELECT 1 FROM {_schema}.maintenance_fences WHERE projection=@projection AND version=@active)", ("projection", lease.Identity.Name), ("active", currentVersion ?? 0)))
        {
            if (Convert.ToBoolean(await recovery.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture))
            {
                throw new InvalidOperationException("A fenced operator recovery is pending; cutover must complete that durable recovery ticket.");
            }
        }

        var activePosition = BlueTuskLogSequenceNumber.Zero;
        string? activeLineage = null;
        string? candidateLineage = null;
        // Lock versions in a consistent numeric order. CDC workers only lock one version.
        await using (var command = Command(connection, transaction, $"""
            SELECT version, checkpoint, source_fingerprint, source_lineage FROM {_schema}.state
            WHERE projection = @projection AND (version = @candidate OR version = @active) ORDER BY version FOR UPDATE
            """, ("projection", lease.Identity.Name), ("candidate", lease.Identity.Version), ("active", currentVersion ?? lease.Identity.Version)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetInt32(0) == currentVersion)
                {
                    activeLineage = reader.IsDBNull(3) ? null : reader.GetString(3);
                    if (evidence is null && !string.Equals(reader.GetString(2), lease.Identity.Source.Fingerprint, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("A cutover cannot change the source identity.");
                    }

                    activePosition = new BlueTuskLogSequenceNumber((ulong)reader.GetDecimal(1));
                }
                if (reader.GetInt32(0) == lease.Identity.Version) { candidateLineage = reader.IsDBNull(3) ? null : reader.GetString(3); }
            }
        }

        if (evidence is not null && (activeLineage is null || candidateLineage is null ||
            !string.Equals(activeLineage, candidateLineage, StringComparison.Ordinal) || !string.Equals(candidateLineage, evidence.Fingerprint, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Different-slot cutover requires matching actual source/database/timeline/publication lineage bound before each version's snapshot, plus matching fresh evidence.");
        }
        if (currentVersion is not null && evidence is null && (activeLineage is not null || candidateLineage is not null) &&
            !string.Equals(activeLineage, candidateLineage, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Strict cutover cannot change bound catalogue/timeline lineage, including under an identical Streams source identity. Use an explicit fresh-snapshot operator recovery.");
        }

        var locked = await ReadStateAsync(connection, transaction, lease.Identity, true, cancellationToken).ConfigureAwait(false);
        EnsureLease(lease, locked);
        if (locked.State.Phase != ProjectionBuildPhase.CatchingUp || locked.State.Checkpoint < requiredPosition ||
            locked.State.Checkpoint < activePosition)
        {
            throw new InvalidOperationException("The candidate has not completed its consistent snapshot and caught up to the cutover barrier/current active checkpoint.");
        }

        await using (var command = LeaseCommand(connection, transaction, lease, $"""
            UPDATE {_schema}.state SET generation = generation + 1
            WHERE projection = @projection AND version = @version AND owner_id = @owner AND fencing_token = @token AND expires_at > clock_timestamp()
            """))
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new ProjectionFencedException();
            }
        }

        await ExecuteAsync(connection, transaction, $"UPDATE {_schema}.heads SET active_version = @version, publication_revision = publication_revision + 1 WHERE projection = @projection",
            cancellationToken, ("projection", lease.Identity.Name), ("version", lease.Identity.Version)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        ProjectionsDiagnostics.Commit("promotion");
    }

    public async ValueTask<int?> ReadActiveVersionAsync(string projectionName, CancellationToken cancellationToken = default)
    {
        ProjectionValidation.Key(projectionName, nameof(projectionName));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, $"SELECT active_version FROM {_schema}.heads WHERE projection = @projection", ("projection", projectionName));
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null || result is DBNull ? null : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    public async ValueTask<ProjectionDocument?> ReadActiveDocumentAsync(string projectionName, string tenantId, string key,
        CancellationToken cancellationToken = default)
    {
        ProjectionValidation.Key(projectionName, nameof(projectionName));
        ProjectionValidation.Key(tenantId, nameof(tenantId));
        ProjectionValidation.Key(key, nameof(key));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, $"""
            SELECT d.payload FROM {_schema}.documents d JOIN {_schema}.heads h ON h.projection = d.projection AND h.active_version = d.version
            WHERE d.projection = @projection AND d.tenant_id = @tenant AND d.document_key = @key
            """, ("projection", projectionName), ("tenant", tenantId), ("key", key));
        var payload = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return payload is byte[] bytes ? new ProjectionDocument(tenantId, key, bytes) : null;
    }

    private async ValueTask<LockedState> ReadStateAsync(DbConnection connection, DbTransaction? transaction,
        ProjectionIdentity identity, bool forUpdate, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"""
            SELECT definition_fingerprint, source_fingerprint, phase, checkpoint, snapshot_epoch,
                expected_tables, completed_tables, snapshot_rows, generation, owner_id, fencing_token,
                expires_at > clock_timestamp(), snapshot_position,
                EXISTS(SELECT 1 FROM {_schema}.retired_versions r WHERE r.projection = @projection AND r.version = @version),snapshot_started_at,
                EXISTS(SELECT 1 FROM {_schema}.recovery_tickets r WHERE r.projection = @projection AND r.active_version = @version)
                    OR EXISTS(SELECT 1 FROM {_schema}.maintenance_fences r WHERE r.projection = @projection AND r.version = @version)
            FROM {_schema}.state WHERE projection = @projection AND version = @version
            """ + (forUpdate ? " FOR UPDATE" : string.Empty), ("projection", identity.Name), ("version", identity.Version));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The projection version is not registered.");
        }

        if (!string.Equals(reader.GetString(0), identity.DefinitionFingerprint, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(1), identity.Source.Fingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The projection version is bound to a different definition or Streams source. Register a new version.");
        }

        if (reader.GetBoolean(13)) { throw new ProjectionRetiredException(); }

        var state = new ProjectionState(identity, (ProjectionBuildPhase)reader.GetInt32(2),
            new BlueTuskLogSequenceNumber((ulong)reader.GetDecimal(3)), reader.IsDBNull(4) ? null : reader.GetGuid(4),
            reader.GetInt32(5), reader.GetInt32(6), reader.GetInt64(7), reader.GetInt64(8));
        return new LockedState(state, reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetInt64(10),
            !reader.IsDBNull(11) && reader.GetBoolean(11) && !reader.GetBoolean(15), reader.IsDBNull(12) ? BlueTuskLogSequenceNumber.Zero : new BlueTuskLogSequenceNumber((ulong)reader.GetDecimal(12)),
            reader.IsDBNull(14) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(14), DateTimeKind.Utc)));
    }

    private async ValueTask CheckpointAsync(DbConnection connection, DbTransaction transaction, ProjectionLease lease,
        BlueTuskLogSequenceNumber checkpoint, CancellationToken cancellationToken)
    {
        await using var command = LeaseCommand(connection, transaction, lease, $"""
            UPDATE {_schema}.state SET checkpoint = @checkpoint, generation = generation + 1
            WHERE projection = @projection AND version = @version AND owner_id = @owner AND fencing_token = @token AND expires_at > clock_timestamp()
            """, ("checkpoint", (decimal)checkpoint.Value));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new ProjectionFencedException();
        }
    }

    private static void ValidateDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.FromMilliseconds(1) || duration > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Lease duration must be between 1 ms and 24 hours.");
        }
    }

    private async ValueTask ValidateChangeBytesAsync(ChangeTransaction transaction, CancellationToken cancellationToken)
    {
        long bytes = 0;
        var count = 0;
        await foreach (var change in transaction.Changes.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (change.Id.Source != transaction.Source || change.Id.CommitEndPosition != transaction.CommitEndPosition ||
                change.Id.TransactionId != transaction.TransactionId || change.Id.Ordinal != count++)
            {
                throw new ArgumentException("Every CDC change must retain its ordered transaction identity.", nameof(transaction));
            }

            bytes = checked(bytes + (change switch
            {
                InsertChange insert => RowBytes(insert.NewRow),
                UpdateChange update => checked(RowBytes(update.OldRow) + RowBytes(update.NewRow)),
                DeleteChange delete => RowBytes(delete.OldRow),
                LogicalMessageChange logical => logical.Content.Length,
                TruncateChange => 0,
                _ => throw new ArgumentException("The projection store requires raw Streams CDC row contracts.", nameof(transaction))
            }));
            if (count > _options.MaximumChangesPerTransaction || bytes > _options.MaximumTransactionBytes)
            {
                throw new ProjectionBoundExceededException("The actual CDC transaction exceeds the configured change or byte bound.");
            }
        }

        if (count != transaction.Changes.Count)
        {
            throw new ArgumentException("CDC change enumeration did not match the declared transaction change count.", nameof(transaction));
        }
    }

    private static long RowBytes(ChangeRow row)
    {
        long bytes = 0;
        foreach (var value in row.Values)
        {
            bytes = checked(bytes + value.Data.Length);
        }

        return bytes;
    }

    private static void ValidateLease(ProjectionLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(lease.Identity);
        ProjectionValidation.Key(lease.OwnerId, nameof(lease));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lease.FencingToken);
    }

    private static void ValidateDefinition(ProjectionLease lease, IProjectionDefinition definition)
    {
        ValidateLease(lease);
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Identity != lease.Identity)
        {
            throw new ArgumentException("The handler's definition does not match the leased projection version.", nameof(definition));
        }
    }

    private static void EnsureLease(ProjectionLease lease, LockedState state)
    {
        if (!state.IsLeaseActive || state.FencingToken != lease.FencingToken || !string.Equals(state.OwnerId, lease.OwnerId, StringComparison.Ordinal))
        {
            throw new ProjectionFencedException();
        }
    }

    private DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object Value)[] parameters) =>
        ProjectionSql.Command(connection, transaction, _options.CommandTimeoutSeconds, sql, parameters);

    private DbCommand IdentityCommand(DbConnection connection, DbTransaction? transaction, ProjectionIdentity identity,
        string sql, params (string Name, object Value)[] additional)
    {
        var parameters = new (string Name, object Value)[additional.Length + 4];
        parameters[0] = ("projection", identity.Name);
        parameters[1] = ("version", identity.Version);
        parameters[2] = ("definition", identity.DefinitionFingerprint);
        parameters[3] = ("source", identity.Source.Fingerprint);
        additional.CopyTo(parameters, 4);
        return Command(connection, transaction, sql, parameters);
    }

    private DbCommand LeaseCommand(DbConnection connection, DbTransaction? transaction, ProjectionLease lease,
        string sql, params (string Name, object Value)[] additional)
    {
        var parameters = new (string Name, object Value)[additional.Length + 4];
        parameters[0] = ("projection", lease.Identity.Name);
        parameters[1] = ("version", lease.Identity.Version);
        parameters[2] = ("owner", lease.OwnerId);
        parameters[3] = ("token", lease.FencingToken);
        additional.CopyTo(parameters, 4);
        return Command(connection, transaction, sql, parameters);
    }

    private async ValueTask ExecuteAsync(DbConnection connection, DbTransaction? transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record LockedState(ProjectionState State, string? OwnerId, long FencingToken,
        bool IsLeaseActive, BlueTuskLogSequenceNumber SnapshotPosition, DateTimeOffset? SnapshotStartedAt);

    private async ValueTask LockHeadAsync(DbConnection connection, DbTransaction transaction, string name, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"SELECT projection FROM {_schema}.heads WHERE projection = @projection FOR UPDATE", ("projection", name));
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException("The projection name is not registered.");
        }
    }
}
