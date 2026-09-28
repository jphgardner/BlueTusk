using System.Globalization;
using BlueTusk.TypeSystem;

namespace BlueTusk.Projections;

/// <summary>Permanent operator intent. Starting recovery freezes the former writer; it cannot be undone by lease reacquisition.</summary>
public sealed class ProjectionRecoveryTicket
{
    internal ProjectionRecoveryTicket(string projection, Guid recoveryId, int previousVersion, int candidateVersion,
        string targetLineage, BlueTuskLogSequenceNumber minimumSnapshotPosition, string reason, bool isComplete = false)
    {
        ProjectionName = projection; RecoveryId = recoveryId; PreviousVersion = previousVersion;
        CandidateVersion = candidateVersion; TargetLineageFingerprint = targetLineage;
        MinimumSnapshotPosition = minimumSnapshotPosition; Reason = reason;
        IsComplete = isComplete;
    }
    public string ProjectionName { get; }
    public Guid RecoveryId { get; }
    public int PreviousVersion { get; }
    public int CandidateVersion { get; }
    public string TargetLineageFingerprint { get; }
    public BlueTuskLogSequenceNumber MinimumSnapshotPosition { get; }
    public string Reason { get; }
    public bool IsComplete { get; }
}

public sealed partial class PostgreSqlProjectionStore
{
    /// <summary>
    /// Durably fences the published worker before controlled source DDL. Reads continue from its frozen model.
    /// This is irreversible: resume using a new lineage-bound version, fresh exported snapshot and operator recovery ticket.
    /// Source DDL must use a controlled deployment role; this method cannot detect uncoordinated change-and-revert history.
    /// </summary>
    public async ValueTask FenceForMaintenanceAsync(string projectionName, int expectedActiveVersion, Guid maintenanceId,
        string reason, CancellationToken cancellationToken = default)
    {
        ProjectionValidation.Key(projectionName, nameof(projectionName));
        ProjectionValidation.Key(reason, nameof(reason));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedActiveVersion);
        if (maintenanceId == Guid.Empty) { throw new ArgumentException("A stable nonempty maintenance identifier is required.", nameof(maintenanceId)); }
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockHeadAsync(connection, transaction, projectionName, cancellationToken).ConfigureAwait(false);
        await using (var head = Command(connection, transaction, $"SELECT active_version FROM {_schema}.heads WHERE projection=@projection", ("projection", projectionName)))
        {
            if (Convert.ToInt32(await head.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != expectedActiveVersion)
            { throw new InvalidOperationException("The expected published version changed before maintenance fencing."); }
        }
        var existed = false;
        await using (var command = Command(connection, transaction, $"SELECT maintenance_id,reason FROM {_schema}.maintenance_fences WHERE projection=@projection AND version=@version", ("projection", projectionName), ("version", expectedActiveVersion)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                existed = true;
                if (reader.GetGuid(0) != maintenanceId || reader.GetString(1) != reason) { throw new InvalidOperationException("This version already has a different immutable maintenance fence."); }
            }
        }
        if (!existed)
        {
            await ExecuteAsync(connection, transaction, $"""
                INSERT INTO {_schema}.maintenance_fences(projection,version,maintenance_id,reason) VALUES(@projection,@version,@maintenance,@reason);
                UPDATE {_schema}.state SET owner_id=NULL,expires_at=NULL,fencing_token=fencing_token+1,generation=generation+1 WHERE projection=@projection AND version=@version
                """, cancellationToken, ("projection", projectionName), ("version", expectedActiveVersion), ("maintenance", maintenanceId), ("reason", reason)).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        ProjectionsDiagnostics.Commit("maintenance_fenced");
    }

    /// <summary>
    /// Authorizes an explicit rebuild from the operator's authoritative source after failover or controlled DDL.
    /// Fences the published writer and records a permanent reason. It does not attest preservation of old acknowledged source writes.
    /// Candidate must be lineage-bound and empty; its fresh exported snapshot must start after the supplied verified source barrier.
    /// </summary>
    public async ValueTask<ProjectionRecoveryTicket> BeginRecoveryAsync(ProjectionIdentity candidate, int expectedActiveVersion,
        ProjectionCutoverEvidence freshCandidateEvidence, Guid recoveryId, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ValidateRecoveryEvidence(candidate, freshCandidateEvidence);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedActiveVersion);
        if (expectedActiveVersion == candidate.Version || recoveryId == Guid.Empty) { throw new ArgumentException("Recovery needs a new candidate version and a stable nonempty recovery identifier."); }
        ProjectionValidation.Key(reason, nameof(reason));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockHeadAsync(connection, transaction, candidate.Name, cancellationToken).ConfigureAwait(false);
        await using (var head = Command(connection, transaction, $"SELECT active_version FROM {_schema}.heads WHERE projection=@projection", ("projection", candidate.Name)))
        {
            if (Convert.ToInt32(await head.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != expectedActiveVersion)
            { throw new InvalidOperationException("The expected published version changed before operator recovery."); }
        }
        var locked = await ReadStateAsync(connection, transaction, candidate, true, cancellationToken).ConfigureAwait(false);
        var previous = await ReadRecoveryAsync(connection, transaction, candidate.Name, recoveryId, cancellationToken).ConfigureAwait(false);
        if (previous is not null)
        {
            if (previous.PreviousVersion != expectedActiveVersion || previous.CandidateVersion != candidate.Version || previous.TargetLineageFingerprint != freshCandidateEvidence.Lineage.Fingerprint || previous.Reason != reason)
            { throw new InvalidOperationException("The recovery identifier is already bound to different immutable operator intent."); }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return previous;
        }
        if (locked.State.Phase != ProjectionBuildPhase.Empty || locked.State.Generation != 0 || locked.State.SnapshotEpoch is not null)
        { throw new InvalidOperationException("Operator recovery requires a candidate whose fresh snapshot has not started."); }
        await using (var lineage = Command(connection, transaction, $"SELECT source_lineage FROM {_schema}.state WHERE projection=@projection AND version=@version", ("projection", candidate.Name), ("version", candidate.Version)))
        {
            if (await lineage.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string value || value != freshCandidateEvidence.Lineage.Fingerprint)
            { throw new InvalidOperationException("Candidate must bind the actual target source lineage before operator recovery."); }
        }
        await ExecuteAsync(connection, transaction, $"""
            INSERT INTO {_schema}.recovery_tickets(projection,recovery_id,active_version,candidate_version,target_lineage,target_source,minimum_snapshot_position,reason)
            VALUES(@projection,@recovery,@active,@candidate,@lineage,@source,@position,@reason);
            UPDATE {_schema}.state SET owner_id=NULL,expires_at=NULL,fencing_token=fencing_token+1,generation=generation+1
            WHERE projection=@projection AND version=@active
            """, cancellationToken, ("projection", candidate.Name), ("recovery", recoveryId), ("active", expectedActiveVersion), ("candidate", candidate.Version),
            ("lineage", freshCandidateEvidence.Lineage.Fingerprint), ("source", candidate.Source.Fingerprint), ("position", (decimal)freshCandidateEvidence.BarrierPosition.Value), ("reason", reason)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        ProjectionsDiagnostics.Commit("recovery_fenced");
        return new(candidate.Name, recoveryId, expectedActiveVersion, candidate.Version, freshCandidateEvidence.Lineage.Fingerprint, freshCandidateEvidence.BarrierPosition, reason);
    }

    /// <summary>Reopens durable recovery intent after operator/process loss. Published data remains readable while its old worker is fenced.</summary>
    public async ValueTask<ProjectionRecoveryTicket?> ReadRecoveryAsync(string projectionName, Guid recoveryId, CancellationToken cancellationToken = default)
    {
        ProjectionValidation.Key(projectionName, nameof(projectionName));
        if (recoveryId == Guid.Empty) { throw new ArgumentException("A stable recovery identifier is required.", nameof(recoveryId)); }
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadRecoveryAsync(connection, null, projectionName, recoveryId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes only this ticket's completed fresh snapshot plus retained WAL through fresh verified evidence.
    /// Old and new source LSNs are not compared across timeline/catalogue histories. The former version is permanently retired.
    /// </summary>
    public async ValueTask CompleteRecoveryAsync(ProjectionLease candidateLease, Guid recoveryId,
        ProjectionCutoverEvidence freshCandidateEvidence, CancellationToken cancellationToken = default)
    {
        ValidateLease(candidateLease);
        ValidateRecoveryEvidence(candidateLease.Identity, freshCandidateEvidence);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockHeadAsync(connection, transaction, candidateLease.Identity.Name, cancellationToken).ConfigureAwait(false);
        var ticket = await ReadRecoveryAsync(connection, transaction, candidateLease.Identity.Name, recoveryId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The durable operator recovery ticket does not exist.");
        if (ticket.CandidateVersion != candidateLease.Identity.Version || ticket.TargetLineageFingerprint != freshCandidateEvidence.Lineage.Fingerprint)
        { throw new InvalidOperationException("Recovery candidate/evidence differs from the durable operator intent."); }
        await using (var command = Command(connection, transaction, $"""
            SELECT h.active_version,r.completed_at IS NOT NULL FROM {_schema}.heads h JOIN {_schema}.recovery_tickets r ON r.projection=h.projection
            WHERE h.projection=@projection AND r.recovery_id=@recovery
            """, ("projection", ticket.ProjectionName), ("recovery", recoveryId)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { throw new InvalidOperationException("Recovery publication metadata is absent."); }
            if (reader.GetBoolean(1) && reader.GetInt32(0) == ticket.CandidateVersion)
            {
                // An exact retry after an ambiguous target commit may confirm the durable cutover.
                await reader.DisposeAsync().ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            if (reader.GetBoolean(1) || reader.GetInt32(0) != ticket.PreviousVersion)
            { throw new InvalidOperationException("The published version no longer matches this recovery's frozen predecessor or completed candidate."); }
        }
        var state = await ReadStateAsync(connection, transaction, candidateLease.Identity, true, cancellationToken).ConfigureAwait(false);
        EnsureLease(candidateLease, state);
        if (state.State.Phase != ProjectionBuildPhase.CatchingUp || state.State.SnapshotEpoch is null || state.SnapshotPosition < ticket.MinimumSnapshotPosition || state.State.Checkpoint < freshCandidateEvidence.BarrierPosition)
        { throw new InvalidOperationException("Recovery requires a fresh exported snapshot after its starting barrier and complete WAL coverage through its final verified barrier."); }
        await using (var command = Command(connection, transaction, $"SELECT source_lineage,source_fingerprint FROM {_schema}.state WHERE projection=@projection AND version=@version", ("projection", ticket.ProjectionName), ("version", ticket.CandidateVersion)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0) || reader.GetString(0) != ticket.TargetLineageFingerprint || reader.GetString(1) != freshCandidateEvidence.Lineage.Source.Fingerprint)
            { throw new InvalidOperationException("The candidate's bound source contract differs from the recovery ticket."); }
        }
        await CheckpointAsync(connection, transaction, candidateLease, state.State.Checkpoint, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"""
            UPDATE {_schema}.heads SET active_version=@candidate,publication_revision=publication_revision+1 WHERE projection=@projection;
            UPDATE {_schema}.recovery_tickets SET completed_at=clock_timestamp() WHERE projection=@projection AND recovery_id=@recovery;
            INSERT INTO {_schema}.retired_versions(projection,version) VALUES(@projection,@active) ON CONFLICT DO NOTHING
            """, cancellationToken, ("projection", ticket.ProjectionName), ("recovery", recoveryId), ("candidate", ticket.CandidateVersion), ("active", ticket.PreviousVersion)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        ProjectionsDiagnostics.Commit("recovery_cutover");
    }

    private async ValueTask<ProjectionRecoveryTicket?> ReadRecoveryAsync(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction? transaction,
        string name, Guid recoveryId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"SELECT active_version,candidate_version,target_lineage,minimum_snapshot_position,reason,completed_at IS NOT NULL FROM {_schema}.recovery_tickets WHERE projection=@projection AND recovery_id=@recovery",
            ("projection", name), ("recovery", recoveryId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? new(name, recoveryId, reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), new((ulong)reader.GetDecimal(3)), reader.GetString(4), reader.GetBoolean(5)) : null;
    }

    private static void ValidateRecoveryEvidence(ProjectionIdentity candidate, ProjectionCutoverEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (candidate.Source != evidence.Lineage.Source || System.Diagnostics.Stopwatch.GetElapsedTime(evidence.Lineage.CaptureTimestamp) > TimeSpan.FromMinutes(1))
        { throw new InvalidOperationException("Recovery requires fresh actual source evidence for the exact candidate Streams identity."); }
    }
}
