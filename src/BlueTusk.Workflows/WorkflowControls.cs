using BlueTusk.Data;
using BlueTusk.Jobs;

namespace BlueTusk.Workflows;

public sealed record WorkflowRecoveryCursor(Guid WorkflowId, string NodeId);
public sealed record WorkflowRecoveryResult(int Inspected, int Recovered, WorkflowRecoveryCursor? NextCursor);

public sealed partial class PostgreSqlWorkflowStore
{
    /// <summary>Buffers an idempotent signal even before its wait node is ready.</summary>
    public async ValueTask<bool> SignalAsync(WorkflowKey key, string name, string identity, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        WorkflowOptions.Name(name, nameof(name), 100);
        WorkflowOptions.Name(identity, nameof(identity));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, _options.MaximumSignalBytes);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await SnapshotAsync(connection, transaction, key, locked: true, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return false;
        }

        await using (var command = Command(connection, transaction, $"""
            SELECT payload FROM {_schema}.signals WHERE tenant = @tenant AND queue = @queue AND workflow_id = @id AND name = @name AND identity = @identity
            """))
        {
            Key(command, key);
            Add(command, "name", name);
            Add(command, "identity", identity);
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is byte[] previous)
            {
                if (!previous.AsSpan().SequenceEqual(payload.Span))
                {
                    throw new InvalidOperationException("Signal identity conflicts with a different payload.");
                }

                return false;
            }
        }

        if (snapshot.Status != WorkflowStatus.Running)
        {
            return false;
        }

        if (!await ReserveHistoryAsync(connection, transaction, key, cancellationToken).ConfigureAwait(false))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var definition = await DefinitionAsync(connection, transaction, key.Scope, snapshot.Definition, snapshot.Version, cancellationToken).ConfigureAwait(false);
        if (!definition.Nodes.Any(node => node.Kind == WorkflowNodeKind.Signal && node.Signal == name))
        {
            throw new ArgumentException("The signal is not declared by this workflow definition.", nameof(name));
        }

        await using (var command = Command(connection, transaction, $"""
            UPDATE {_schema}.instances SET signal_count = signal_count + 1
            WHERE tenant = @tenant AND queue = @queue AND id = @id AND signal_count < @limit RETURNING signal_count
            """))
        {
            Key(command, key);
            Add(command, "limit", _options.MaximumSignalsPerWorkflow);
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not int)
            {
                throw new InvalidOperationException("The workflow signal admission limit is exhausted.");
            }
        }

        await using (var command = Command(connection, transaction, $"""
            INSERT INTO {_schema}.signals (tenant, queue, workflow_id, name, identity, payload) VALUES (@tenant, @queue, @id, @name, @identity, @payload)
            """))
        {
            Key(command, key);
            Add(command, "name", name);
            Add(command, "identity", identity);
            Add(command, "payload", payload.ToArray());
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        _ = await AppendAsync(connection, transaction, key, "signal_received", node: null, code: null, cancellationToken).ConfigureAwait(false);
        await AdvanceAsync(connection, transaction, key, definition, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<bool> CancelAsync(WorkflowKey key, bool compensate = true, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        var jobs = new List<Guid>();
        await using (var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            var snapshot = await SnapshotAsync(connection, transaction, key, locked: true, cancellationToken).ConfigureAwait(false);
            if (snapshot is null || snapshot.Status != WorkflowStatus.Running)
            {
                return false;
            }

            if (!await ReserveHistoryAsync(connection, transaction, key, cancellationToken).ConfigureAwait(false))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            var nodes = await NodesAsync(connection, transaction, key, cancellationToken).ConfigureAwait(false);
            jobs.AddRange(nodes.Where(node => node.Status is WorkflowNodeStatus.Scheduled or WorkflowNodeStatus.Running)
                .Select(node => node.JobId).OfType<Guid>());
            _ = await AppendAsync(connection, transaction, key, "cancellation_requested", node: null, code: null, cancellationToken).ConfigureAwait(false);
            if (compensate)
            {
                var definition = await DefinitionAsync(connection, transaction, key.Scope, snapshot.Definition, snapshot.Version, cancellationToken).ConfigureAwait(false);
                await BeginCompensationAsync(connection, transaction, key, definition, WorkflowStatus.Canceled, "canceled", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await SkipNodesAsync(connection, transaction, key, includeRunning: true, cancellationToken).ConfigureAwait(false);
                await SetTerminalAsync(connection, transaction, key, WorkflowStatus.Canceled, "canceled", cancellationToken).ConfigureAwait(false);
                _ = await AppendAsync(connection, transaction, key, "canceled", node: null, code: null, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        // Do not lock Jobs rows while holding an instance lock: dispatch takes these locks in the opposite order.
        foreach (Guid job in jobs)
        {
            _ = await Jobs.CancelAsync(key.Scope, job, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>Migrates a quiescent running instance. Completed contracts must remain identical; revision guards operator races.</summary>
    public async ValueTask<bool> MigrateAsync(WorkflowKey key, int targetVersion, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        WorkflowOptions.Range(targetVersion, 1, int.MaxValue, nameof(targetVersion));
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await SnapshotAsync(connection, transaction, key, locked: true, cancellationToken).ConfigureAwait(false);
        if (snapshot is null || snapshot.Status != WorkflowStatus.Running || snapshot.Revision != expectedRevision || snapshot.Version == targetVersion)
        {
            return false;
        }

        var states = await StatesAsync(connection, transaction, key, cancellationToken).ConfigureAwait(false);
        if (states.Values.Any(status => status is not (WorkflowNodeStatus.Blocked or WorkflowNodeStatus.Completed)))
        {
            return false;
        }

        if (!await ReserveHistoryAsync(connection, transaction, key, cancellationToken).ConfigureAwait(false))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var original = await DefinitionAsync(connection, transaction, key.Scope, snapshot.Definition, snapshot.Version, cancellationToken).ConfigureAwait(false);
        var target = await DefinitionAsync(connection, transaction, key.Scope, snapshot.Definition, targetVersion, cancellationToken).ConfigureAwait(false);
        foreach (string completed in states.Where(pair => pair.Value == WorkflowNodeStatus.Completed).Select(pair => pair.Key))
        {
            var oldNode = original.Nodes.Single(node => node.Id == completed);
            var newNode = target.Nodes.SingleOrDefault(node => node.Id == completed);
            if (newNode is null || !SameContract(oldNode, newNode))
            {
                throw new InvalidOperationException("Migration cannot remove or reinterpret a completed node contract.");
            }
        }

        await using (var command = Command(connection, transaction,
            $"DELETE FROM {_schema}.nodes WHERE tenant = @tenant AND queue = @queue AND workflow_id = @id AND status = 0"))
        {
            Key(command, key);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var node in target.Nodes.Where(node => !states.TryGetValue(node.Id, out var status) || status != WorkflowNodeStatus.Completed))
        {
            await InsertNodeAsync(connection, transaction, key, node.Id, cancellationToken).ConfigureAwait(false);
        }

        await using (var command = Command(connection, transaction,
            $"UPDATE {_schema}.instances SET version = @version WHERE tenant = @tenant AND queue = @queue AND id = @id"))
        {
            Key(command, key);
            Add(command, "version", targetVersion);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        _ = await AppendAsync(connection, transaction, key, "migrated", node: null, "v" + targetVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        await AdvanceAsync(connection, transaction, key, target, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<WorkflowRecoveryResult> ReconcileAsync(JobScope scope, int maximumCount,
        WorkflowRecoveryCursor? after = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Batch(maximumCount);
        if (after is not null)
        {
            WorkflowOptions.Name(after.NodeId, nameof(after), 100);
        }

        var candidates = new List<(Guid Workflow, string Node, Guid Job, bool Compensation)>();
        await using (var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = Command(connection, transaction: null, $"""
            SELECT n.workflow_id, n.id, CASE WHEN n.status = 6 THEN n.compensation_job_id ELSE n.job_id END, n.status = 6
            FROM {_schema}.nodes n JOIN {_schema}.instances w ON w.tenant = n.tenant AND w.queue = n.queue AND w.id = n.workflow_id
            WHERE n.tenant = @tenant AND n.queue = @queue AND n.status IN (1,2,6) AND w.status IN (0,1)
                AND (n.workflow_id, n.id) > (@after, @node)
            ORDER BY n.workflow_id, n.id LIMIT @count
            """))
        {
            Scope(command, scope);
            Add(command, "after", after?.WorkflowId ?? Guid.Empty);
            Add(command, "node", after?.NodeId ?? string.Empty);
            Add(command, "count", maximumCount);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                candidates.Add((reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetBoolean(3)));
            }
        }

        int recovered = 0;
        foreach (var candidate in candidates)
        {
            var job = await Jobs.ReadAsync(scope, candidate.Job, cancellationToken).ConfigureAwait(false);
            if (job is not null && job.Status is not (JobStatus.Failed or JobStatus.Canceled))
            {
                continue;
            }

            var key = new WorkflowKey(scope, candidate.Workflow);
            await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = await SnapshotAsync(connection, transaction, key, locked: true, cancellationToken).ConfigureAwait(false);
            if (snapshot is null || snapshot.Status is not (WorkflowStatus.Running or WorkflowStatus.Compensating))
            {
                continue;
            }

            var state = await NodeAsync(connection, transaction, key, candidate.Node, cancellationToken).ConfigureAwait(false);
            if (state is null || (candidate.Compensation ? state.CompensationJobId : state.JobId) != candidate.Job ||
                state.Status is not (WorkflowNodeStatus.Scheduled or WorkflowNodeStatus.Running or WorkflowNodeStatus.Compensating))
            {
                continue;
            }

            if (!await ReserveHistoryAsync(connection, transaction, key, cancellationToken).ConfigureAwait(false))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                recovered++;
                continue;
            }

            string failure = job is null ? "dispatch_missing" : job.Status == JobStatus.Canceled ? "dispatch_canceled" : job.LastFailureCode ?? "dispatch_failed";
            var definition = await DefinitionAsync(connection, transaction, key.Scope, snapshot.Definition, snapshot.Version, cancellationToken).ConfigureAwait(false);
            _ = await AppendAsync(connection, transaction, key, "dispatch_recovered", candidate.Node, failure, cancellationToken).ConfigureAwait(false);
            if (candidate.Compensation)
            {
                await FailNodeAsync(connection, transaction, key, candidate.Node, failure, cancellationToken).ConfigureAwait(false);
                _ = await AppendAsync(connection, transaction, key, "compensation_failed", candidate.Node, failure, cancellationToken).ConfigureAwait(false);
                await SetTerminalAsync(connection, transaction, key, WorkflowStatus.Failed, "compensation_failed", cancellationToken).ConfigureAwait(false);
                _ = await AppendAsync(connection, transaction, key, "failed", candidate.Node, "compensation_failed", cancellationToken).ConfigureAwait(false);
            }
            else if (snapshot.Status == WorkflowStatus.Compensating)
            {
                await UpdateNodeStateAsync(connection, transaction, key, candidate.Node, WorkflowNodeStatus.Skipped, cancellationToken).ConfigureAwait(false);
                _ = await AppendAsync(connection, transaction, key, "activity_stopped", candidate.Node, failure, cancellationToken).ConfigureAwait(false);
                await ScheduleCompensationAsync(connection, transaction, key, definition, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await FailNodeAsync(connection, transaction, key, candidate.Node, failure, cancellationToken).ConfigureAwait(false);
                _ = await AppendAsync(connection, transaction, key, "activity_failed", candidate.Node, failure, cancellationToken).ConfigureAwait(false);
                await BeginCompensationAsync(connection, transaction, key, definition, WorkflowStatus.Failed, failure, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            recovered++;
        }

        var cursor = candidates.Count == maximumCount ? new WorkflowRecoveryCursor(candidates[^1].Workflow, candidates[^1].Node) : null;
        return new(candidates.Count, recovered, cursor);
    }

    private static bool SameContract(WorkflowNode left, WorkflowNode right) =>
        left.Kind == right.Kind && left.Activity == right.Activity && left.Compensation == right.Compensation &&
        left.Signal == right.Signal && left.Delay == right.Delay && left.MaximumAttempts == right.MaximumAttempts &&
        left.DependsOn.SequenceEqual(right.DependsOn, StringComparer.Ordinal);
}
