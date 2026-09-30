using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace BlueTusk.Edge.Sqlite;

public sealed partial class SqliteEdgeStore
{
    public async ValueTask EnqueueAsync(EdgeMutation mutation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (mutation.Payload.Length > Options.MaxRecordBytes)
        {
            throw new EdgeCapacityException("Queued payload exceeds the record budget.");
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var checkpoint = await CheckScopeAsync(connection, transaction, mutation.Scope, cancellationToken).ConfigureAwait(false);
        if (!checkpoint.SnapshotReady)
        {
            throw new EdgeScopeMismatchException();
        }

        await EnqueueInTransactionAsync(connection, transaction, mutation, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<EdgeMutationLease?> ClaimAsync(EdgeScope scope, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        ValidateLeaseDuration(leaseDuration);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var lease = await ClaimInTransactionAsync(connection, transaction, scope, leaseDuration, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return lease;
    }

    private async ValueTask<EdgeMutationLease?> ClaimInTransactionAsync(SqliteConnection connection, SqliteTransaction transaction,
        EdgeScope scope, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var now = Options.TimeProvider.GetUtcNow();
        var expiry = now + leaseDuration;
        _ = await CheckScopeAsync(connection, transaction, scope, cancellationToken).ConfigureAwait(false);
        await CheckNoSnapshotAsync(connection, transaction, scope, cancellationToken).ConfigureAwait(false);
        EdgeMutation? mutation = null;
        long fence = 0;
        await using (var command = Command(connection, transaction, """
            SELECT mutation_id,document_id,expected_revision,kind,payload,fence
            FROM mutations m WHERE m.tenant=@tenant AND m.scope_id=@scope AND m.epoch=@epoch
                AND (m.status=0 OR (m.status=1 AND m.lease_until<=@now))
                AND (substr(m.mutation_id,13,1)!='8' OR substr(m.mutation_id,17,1)!='a' OR NOT EXISTS (
                    SELECT 1 FROM mutations prior WHERE prior.tenant=m.tenant AND prior.scope_id=m.scope_id AND prior.epoch=m.epoch
                        AND substr(prior.mutation_id,1,17)=substr(m.mutation_id,1,17)
                        AND prior.mutation_id<m.mutation_id AND prior.status IN(0,1)))
            ORDER BY m.sequence LIMIT 1
            """))
        {
            ScopeParameters(command, scope);
            command.Parameters.AddWithValue("now", now.ToUnixTimeMilliseconds());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                mutation = new EdgeMutation(scope, Guid.ParseExact(reader.GetString(0), "N"), reader.GetString(1), reader.GetInt64(2), (EdgeMutationKind)reader.GetInt32(3), (byte[])reader.GetValue(4));
                fence = checked(reader.GetInt64(5) + 1);
            }
        }

        if (mutation is null)
        {
            return null;
        }

        await using (var command = Command(connection, transaction, "UPDATE mutations SET status=1,fence=@fence,lease_until=@expiry WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch AND mutation_id=@mutation"))
        {
            MutationParameters(command, mutation);
            command.Parameters.AddWithValue("fence", fence);
            command.Parameters.AddWithValue("expiry", expiry.ToUnixTimeMilliseconds());
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return new EdgeMutationLease(mutation, fence, expiry);
    }

    private static void ValidateLeaseDuration(TimeSpan leaseDuration)
    {
        if (leaseDuration < TimeSpan.FromSeconds(1) || leaseDuration > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }
    }

    /// <summary>Atomically commits cache, queue and identity receipt. A stale owner cannot acknowledge a replacement lease.</summary>
    public async ValueTask AcknowledgeAsync(EdgeMutationLease lease, EdgeMutationOutcome outcome, CancellationToken cancellationToken = default)
    {
        var outcomeFingerprint = ValidateAcknowledgement(lease, outcome);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await AcknowledgeInTransactionAsync(connection, transaction, lease, outcome, outcomeFingerprint, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Commits the current outcome and leases the next mutation in one durable local transition.</summary>
    public async ValueTask<EdgeMutationLease?> AcknowledgeAndClaimNextAsync(EdgeMutationLease lease,
        EdgeMutationOutcome outcome, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        var outcomeFingerprint = ValidateAcknowledgement(lease, outcome);
        ValidateLeaseDuration(leaseDuration);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await AcknowledgeInTransactionAsync(connection, transaction, lease, outcome, outcomeFingerprint, cancellationToken).ConfigureAwait(false);
        var next = await ClaimInTransactionAsync(connection, transaction, lease.Mutation.Scope, leaseDuration, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return next;
    }

    private string ValidateAcknowledgement(EdgeMutationLease lease, EdgeMutationOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(outcome);
        if (!Enum.IsDefined(outcome.Kind) || outcome.Kind is EdgeMutationOutcomeKind.Applied && outcome.ServerRecord is null ||
            outcome.ServerRecord is not null && !string.Equals(outcome.ServerRecord.Id, lease.Mutation.DocumentId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Mutation outcomes must identify the mutated document and successful writes must carry an authoritative record.", nameof(outcome));
        }

        if (outcome.Kind is EdgeMutationOutcomeKind.Applied &&
            (outcome.ServerRecord!.Revision <= lease.Mutation.ExpectedRevision ||
             outcome.ServerRecord.Deleted != (lease.Mutation.Kind is EdgeMutationKind.Delete)))
        {
            throw new EdgeRevisionConflictException();
        }

        if (outcome.ServerRecord is not null)
        {
            ValidateRecords([outcome.ServerRecord]);
        }

        return OutcomeFingerprint(outcome);
    }

    private async ValueTask AcknowledgeInTransactionAsync(SqliteConnection connection, SqliteTransaction transaction,
        EdgeMutationLease lease, EdgeMutationOutcome outcome, string outcomeFingerprint, CancellationToken cancellationToken)
    {
        _ = await CheckScopeAsync(connection, transaction, lease.Mutation.Scope, cancellationToken).ConfigureAwait(false);
        await CheckNoSnapshotAsync(connection, transaction, lease.Mutation.Scope, cancellationToken).ConfigureAwait(false);
        await using (var receipt = Command(connection, transaction, "SELECT fingerprint,outcome_fingerprint FROM receipts WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch AND mutation_id=@mutation"))
        {
            MutationParameters(receipt, lease.Mutation);
            await using var reader = await receipt.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!string.Equals(reader.GetString(0), lease.Mutation.Fingerprint, StringComparison.Ordinal) || !string.Equals(reader.GetString(1), outcomeFingerprint, StringComparison.Ordinal))
                {
                    throw new EdgeMutationIdentityException();
                }

                return;
            }
        }

        await using (var pending = Command(connection, transaction, "SELECT fingerprint,status,fence,lease_until FROM mutations WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch AND mutation_id=@mutation"))
        {
            MutationParameters(pending, lease.Mutation);
            await using var reader = await pending.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                !string.Equals(reader.GetString(0), lease.Mutation.Fingerprint, StringComparison.Ordinal) ||
                reader.GetInt32(1) != (int)EdgeMutationStatus.Leased || reader.GetInt64(2) != lease.Fence ||
                reader.GetInt64(3) <= Options.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds())
            {
                throw new EdgeLeaseLostException();
            }
        }

        if (outcome.ServerRecord is not null)
        {
            await StoreRecordAsync(connection, transaction, lease.Mutation.Scope, outcome.ServerRecord, null, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await using var missing = Command(connection, transaction, "DELETE FROM records WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch AND document_id=@id AND revision<=@expected");
            MutationParameters(missing, lease.Mutation);
            missing.Parameters.AddWithValue("id", lease.Mutation.DocumentId);
            missing.Parameters.AddWithValue("expected", lease.Mutation.ExpectedRevision);
            _ = await missing.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var command = Command(connection, transaction, outcome.Kind is EdgeMutationOutcomeKind.Applied
            ? "DELETE FROM mutations WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch AND mutation_id=@mutation"
            : "UPDATE mutations SET status=2,lease_until=NULL WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch AND mutation_id=@mutation"))
        {
            MutationParameters(command, lease.Mutation);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var count = Command(connection, transaction, "SELECT count(*) FROM receipts"))
        {
            if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) >= Options.MaxReceiptRecords)
            {
                throw new EdgeCapacityException("Mutation receipt capacity is full; prune identities only under the application's remote replay contract.");
            }
        }

        await using (var receipt = Command(connection, transaction, "INSERT INTO receipts(tenant,scope_id,epoch,mutation_id,fingerprint,outcome,outcome_fingerprint,recorded_at) VALUES(@tenant,@scope,@epoch,@mutation,@fingerprint,@outcome,@outcome_fingerprint,@time)"))
        {
            MutationParameters(receipt, lease.Mutation);
            receipt.Parameters.AddWithValue("fingerprint", lease.Mutation.Fingerprint);
            receipt.Parameters.AddWithValue("outcome", (int)outcome.Kind);
            receipt.Parameters.AddWithValue("outcome_fingerprint", outcomeFingerprint);
            receipt.Parameters.AddWithValue("time", Options.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            _ = await receipt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await StageOrderedConfirmationAsync(connection, transaction, lease.Mutation, cancellationToken).ConfigureAwait(false);

        await CheckCacheBudgetAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Retires an explicit conflict and stages a new identity atomically. The caller chooses merge content and current expected revision.</summary>
    public async ValueTask ResolveConflictAsync(Guid conflictedMutationId, EdgeMutation replacement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (replacement.Id == conflictedMutationId || replacement.Payload.Length > Options.MaxRecordBytes)
        {
            throw new EdgeMutationIdentityException();
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        _ = await CheckScopeAsync(connection, transaction, replacement.Scope, cancellationToken).ConfigureAwait(false);
        await using (var resolved = Command(connection, transaction, "SELECT resolved_by FROM receipts WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch AND mutation_id=@old"))
        {
            ScopeParameters(resolved, replacement.Scope);
            resolved.Parameters.AddWithValue("old", conflictedMutationId.ToString("N"));
            var value = await resolved.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is string id)
            {
                if (!string.Equals(id, replacement.Id.ToString("N"), StringComparison.Ordinal))
                {
                    throw new EdgeMutationIdentityException();
                }

                await EnqueueInTransactionAsync(connection, transaction, replacement, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        await using (var pending = Command(connection, transaction, "DELETE FROM mutations WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch AND mutation_id=@old AND status=2 AND document_id=@id"))
        {
            ScopeParameters(pending, replacement.Scope);
            pending.Parameters.AddWithValue("old", conflictedMutationId.ToString("N"));
            pending.Parameters.AddWithValue("id", replacement.DocumentId);
            if (await pending.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new EdgeRevisionConflictException();
            }
        }

        await EnqueueInTransactionAsync(connection, transaction, replacement, cancellationToken).ConfigureAwait(false);
        await using (var receipt = Command(connection, transaction, "UPDATE receipts SET resolved_by=@replacement WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch AND mutation_id=@old AND outcome=1"))
        {
            ScopeParameters(receipt, replacement.Scope);
            receipt.Parameters.AddWithValue("old", conflictedMutationId.ToString("N"));
            receipt.Parameters.AddWithValue("replacement", replacement.Id.ToString("N"));
            if (await receipt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new EdgeRevisionConflictException();
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<int> PruneReceiptsAsync(DateTimeOffset recordedBefore, int maxRecords = 100, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRecords, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxRecords, 10_000);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, "DELETE FROM receipts WHERE rowid IN (SELECT r.rowid FROM receipts r WHERE r.recorded_at<@before AND NOT EXISTS(SELECT 1 FROM mutations m WHERE m.tenant=r.tenant AND m.scope_id=r.scope_id AND m.epoch=r.epoch AND m.mutation_id=r.mutation_id) ORDER BY r.recorded_at LIMIT @count)");
        command.Parameters.AddWithValue("before", recordedBefore.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("count", maxRecords);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask EnqueueInTransactionAsync(SqliteConnection connection, SqliteTransaction transaction, EdgeMutation mutation, CancellationToken cancellationToken, bool orderedAllocation = false)
    {
        await using (var existing = Command(connection, transaction, "SELECT fingerprint FROM mutations WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch AND mutation_id=@mutation UNION ALL SELECT fingerprint FROM receipts WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch AND mutation_id=@mutation LIMIT 1"))
        {
            MutationParameters(existing, mutation);
            var fingerprint = await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (fingerprint is string value)
            {
                if (!string.Equals(value, mutation.Fingerprint, StringComparison.Ordinal))
                {
                    throw new EdgeMutationIdentityException();
                }

                return;
            }
        }

        if (!orderedAllocation && EdgeOrderedMutationId.TryParse(mutation.Id, out var stream, out _))
        {
            await using var owner = Command(connection, transaction, "SELECT 1 FROM ordered_streams WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch AND stream_id=@stream");
            ScopeParameters(owner, mutation.Scope); owner.Parameters.AddWithValue("stream", stream);
            if (await owner.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null) { throw new EdgeMutationIdentityException(); }
        }

        await using (var revision = Command(connection, transaction, "SELECT revision FROM records WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch AND document_id=@id"))
        {
            ScopeParameters(revision, mutation.Scope);
            revision.Parameters.AddWithValue("id", mutation.DocumentId);
            var current = await revision.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (Convert.ToInt64(current ?? 0L, CultureInfo.InvariantCulture) != mutation.ExpectedRevision)
            {
                throw new EdgeRevisionConflictException();
            }
        }

        await using (var existing = Command(connection, transaction, "SELECT count(*) FROM mutations WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch AND document_id=@id"))
        {
            ScopeParameters(existing, mutation.Scope);
            existing.Parameters.AddWithValue("id", mutation.DocumentId);
            if (Convert.ToInt64(await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0)
            {
                throw new EdgeRevisionConflictException();
            }
        }

        await using (var capacity = Command(connection, transaction, "SELECT count(*),COALESCE(sum(length(payload)),0) FROM mutations"))
        await using (var reader = await capacity.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (reader.GetInt64(0) >= Options.MaxPendingMutations || reader.GetInt64(1) + mutation.Payload.Length > Options.MaxPendingBytes)
            {
                throw new EdgeCapacityException("The durable local mutation queue is full.");
            }
        }

        await using var insert = Command(connection, transaction, "INSERT INTO mutations(tenant,scope_id,epoch,mutation_id,document_id,expected_revision,kind,payload,fingerprint) VALUES(@tenant,@scope,@epoch,@mutation,@id,@expected,@kind,@payload,@fingerprint)");
        MutationParameters(insert, mutation);
        insert.Parameters.AddWithValue("id", mutation.DocumentId);
        insert.Parameters.AddWithValue("expected", mutation.ExpectedRevision);
        insert.Parameters.AddWithValue("kind", (int)mutation.Kind);
        insert.Parameters.AddWithValue("payload", mutation.Payload.ToArray());
        insert.Parameters.AddWithValue("fingerprint", mutation.Fingerprint);
        _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void MutationParameters(SqliteCommand command, EdgeMutation mutation)
    {
        ScopeParameters(command, mutation.Scope);
        command.Parameters.AddWithValue("mutation", mutation.Id.ToString("N"));
    }

    private static string OutcomeFingerprint(EdgeMutationOutcome outcome) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{outcome.Kind}:{outcome.ServerRecord?.Revision}:{(outcome.ServerRecord is null ? "missing" : RecordFingerprint(outcome.ServerRecord))}"))));
}
