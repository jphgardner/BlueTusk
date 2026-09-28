using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using BlueTusk.Data;
using BlueTusk.Jobs;

namespace BlueTusk.Workflows;

public sealed partial class PostgreSqlWorkflowStore
{
    internal async ValueTask<WorkflowActivityContext?> PrepareAsync(JobLease lease, WorkflowDispatch dispatch, CancellationToken cancellationToken)
    {
        var key = new WorkflowKey(lease.Scope, dispatch.WorkflowId);
        var fenced = await Jobs.ExecuteFencedAsync<WorkflowActivityContext?>(lease, async (connection, transaction, token) =>
        {
            var snapshot = await SnapshotAsync(connection, transaction, key, locked: true, token).ConfigureAwait(false);
            if (snapshot is null || snapshot.Status is WorkflowStatus.Succeeded or WorkflowStatus.Failed or WorkflowStatus.Canceled ||
                !await ReserveHistoryAsync(connection, transaction, key, token).ConfigureAwait(false))
            {
                return null;
            }

            var definition = await DefinitionAsync(connection, transaction, key.Scope, snapshot.Definition, snapshot.Version, token).ConfigureAwait(false);
            var node = definition.Nodes.SingleOrDefault(candidate => candidate.Id == dispatch.NodeId);
            if (node is null)
            {
                return null;
            }

            var state = await NodeAsync(connection, transaction, key, node.Id, token).ConfigureAwait(false);
            Guid? expectedJob = dispatch.Compensation ? state?.CompensationJobId : state?.JobId;
            if (state is null || expectedJob != lease.JobId || state.FencingToken > lease.FencingToken ||
                (dispatch.Compensation ? state.Status != WorkflowNodeStatus.Compensating : state.Status is not (WorkflowNodeStatus.Scheduled or WorkflowNodeStatus.Running)))
            {
                return null;
            }

            if (!dispatch.Compensation && snapshot.Status == WorkflowStatus.Compensating)
            {
                await UpdateNodeStateAsync(connection, transaction, key, node.Id, WorkflowNodeStatus.Skipped, token).ConfigureAwait(false);
                _ = await AppendAsync(connection, transaction, key, "activity_stopped", node.Id, code: null, token).ConfigureAwait(false);
                await ScheduleCompensationAsync(connection, transaction, key, definition, token).ConfigureAwait(false);
                return null;
            }

            if (node.Kind == WorkflowNodeKind.Timer)
            {
                await CompleteNodeAsync(connection, transaction, key, node.Id, ReadOnlyMemory<byte>.Empty, "timer_fired", token).ConfigureAwait(false);
                await AdvanceAsync(connection, transaction, key, definition, token).ConfigureAwait(false);
                return null;
            }

            if (node.Kind != WorkflowNodeKind.Activity || (dispatch.Compensation && node.Compensation is null))
            {
                return null;
            }

            if (state.FencingToken < lease.FencingToken)
            {
                await using var command = Command(connection, transaction, $"""
                    UPDATE {_schema}.nodes SET status = @status, fencing_token = @fence
                    WHERE tenant = @tenant AND queue = @queue AND workflow_id = @id AND id = @node
                    """);
                Key(command, key);
                Add(command, "node", node.Id);
                Add(command, "status", dispatch.Compensation ? (short)WorkflowNodeStatus.Compensating : (short)WorkflowNodeStatus.Running);
                Add(command, "fence", lease.FencingToken);
                _ = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                _ = await AppendAsync(connection, transaction, key, dispatch.Compensation ? "compensation_started" : "activity_started", node.Id, code: null, token).ConfigureAwait(false);
            }

            byte[] input;
            await using (var command = Command(connection, transaction,
                $"SELECT input FROM {_schema}.instances WHERE tenant = @tenant AND queue = @queue AND id = @id"))
            {
                Key(command, key);
                input = (byte[])(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!;
            }

            var dependencies = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
            int totalBytes = input.Length + (dispatch.Compensation ? state.Result.Length : 0);
            if (totalBytes > _options.MaximumActivityInputBytes)
            {
                throw new JobHandlerException("activity_input_limit", retryable: false);
            }

            if (node.DependsOn.Count != 0)
            {
                string filter = string.Join(',', node.DependsOn.Select((_, index) => "@dependency" + index));
                await using var command = Command(connection, transaction, $"""
                    WITH bounded AS (
                        SELECT id, sum(octet_length(result)) OVER () AS total_bytes FROM {_schema}.nodes
                        WHERE tenant = @tenant AND queue = @queue AND workflow_id = @id AND id IN ({filter})
                    ) SELECT n.id, n.result FROM {_schema}.nodes n JOIN bounded b ON b.id = n.id
                    WHERE n.tenant = @tenant AND n.queue = @queue AND n.workflow_id = @id AND b.total_bytes <= @budget
                    """);
                Key(command, key);
                Add(command, "budget", _options.MaximumActivityInputBytes - totalBytes);
                for (int index = 0; index < node.DependsOn.Count; index++)
                {
                    Add(command, "dependency" + index, node.DependsOn[index]);
                }

                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    dependencies.Add(reader.GetString(0), reader.GetFieldValue<byte[]>(1));
                }

                if (dependencies.Count != node.DependsOn.Count)
                {
                    throw new JobHandlerException("activity_input_limit", retryable: false);
                }
            }

            return new WorkflowActivityContext(key, node.Id, dispatch.Compensation ? node.Compensation! : node.Activity!,
                dispatch.Compensation, lease.Attempt, lease.FencingToken,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0',
                    key.Scope.Tenant, key.Scope.Queue, key.Id.ToString("N"), node.Id, dispatch.Compensation ? "compensate" : "execute")))),
                input, dependencies.ToFrozenDictionary(StringComparer.Ordinal), dispatch.Compensation ? state.Result : ReadOnlyMemory<byte>.Empty);
        }, cancellationToken).ConfigureAwait(false);
        return fenced.Executed ? fenced.Value : null;
    }

    internal async ValueTask FinishAsync(JobLease lease, WorkflowDispatch dispatch, ReadOnlyMemory<byte> result, CancellationToken cancellationToken)
    {
        if (result.Length > _options.MaximumResultBytes)
        {
            throw new JobHandlerException("activity_result_limit", retryable: false);
        }

        byte[] ownedResult = result.ToArray();
        var key = new WorkflowKey(lease.Scope, dispatch.WorkflowId);
        _ = await Jobs.ExecuteFencedAsync(lease, async (connection, transaction, token) =>
            await FinishCoreAsync(connection, transaction, key, lease, dispatch, ownedResult, token).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask ExecuteTransactionalActivityAsync(JobLease lease, WorkflowDispatch dispatch, WorkflowActivityContext context,
        TransactionalWorkflowActivity action, CancellationToken cancellationToken)
    {
        var key = new WorkflowKey(lease.Scope, dispatch.WorkflowId);
        _ = await Jobs.ExecuteFencedAsync(lease, async (connection, transaction, token) =>
        {
            var snapshot = await SnapshotAsync(connection, transaction, key, locked: true, token).ConfigureAwait(false);
            if (snapshot is null || snapshot.Status is WorkflowStatus.Succeeded or WorkflowStatus.Failed or WorkflowStatus.Canceled ||
                !await ReserveHistoryAsync(connection, transaction, key, token).ConfigureAwait(false))
            {
                return false;
            }

            var state = await NodeAsync(connection, transaction, key, dispatch.NodeId, token).ConfigureAwait(false);
            if (!Matches(state, lease, dispatch.Compensation))
            {
                return false;
            }

            if (!dispatch.Compensation && snapshot.Status == WorkflowStatus.Compensating)
            {
                await UpdateNodeStateAsync(connection, transaction, key, dispatch.NodeId, WorkflowNodeStatus.Skipped, token).ConfigureAwait(false);
                _ = await AppendAsync(connection, transaction, key, "activity_stopped", dispatch.NodeId, code: null, token).ConfigureAwait(false);
                var stoppedDefinition = await DefinitionAsync(connection, transaction, key.Scope, snapshot.Definition, snapshot.Version, token).ConfigureAwait(false);
                await ScheduleCompensationAsync(connection, transaction, key, stoppedDefinition, token).ConfigureAwait(false);
                return false;
            }

            var result = await action(connection, transaction, context, token).ConfigureAwait(false);
            if (result.Length > _options.MaximumResultBytes)
            {
                throw new JobHandlerException("activity_result_limit", retryable: false);
            }

            return await FinishCoreAsync(connection, transaction, key, lease, dispatch, result.ToArray(), token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> FinishCoreAsync(BlueTuskConnection connection, BlueTuskTransaction transaction,
        WorkflowKey key, JobLease lease, WorkflowDispatch dispatch, byte[] ownedResult, CancellationToken token)
    {
        var snapshot = await SnapshotAsync(connection, transaction, key, locked: true, token).ConfigureAwait(false);
        if (snapshot is null || snapshot.Status is WorkflowStatus.Succeeded or WorkflowStatus.Failed or WorkflowStatus.Canceled ||
            !await ReserveHistoryAsync(connection, transaction, key, token).ConfigureAwait(false))
        {
            return false;
        }

        var state = await NodeAsync(connection, transaction, key, dispatch.NodeId, token).ConfigureAwait(false);
        if (!Matches(state, lease, dispatch.Compensation))
        {
            return false;
        }

        var definition = await DefinitionAsync(connection, transaction, key.Scope, snapshot.Definition, snapshot.Version, token).ConfigureAwait(false);
        if (dispatch.Compensation)
        {
            await UpdateNodeStateAsync(connection, transaction, key, dispatch.NodeId, WorkflowNodeStatus.Compensated, token).ConfigureAwait(false);
            _ = await AppendAsync(connection, transaction, key, "compensation_completed", dispatch.NodeId, code: null, token).ConfigureAwait(false);
            await ScheduleCompensationAsync(connection, transaction, key, definition, token).ConfigureAwait(false);
        }
        else
        {
            await CompleteNodeAsync(connection, transaction, key, dispatch.NodeId, ownedResult, "activity_completed", token).ConfigureAwait(false);
            if (snapshot.Status == WorkflowStatus.Compensating)
            {
                await ScheduleCompensationAsync(connection, transaction, key, definition, token).ConfigureAwait(false);
            }
            else
            {
                await AdvanceAsync(connection, transaction, key, definition, token).ConfigureAwait(false);
            }
        }

        return true;
    }

    internal async ValueTask RecordFailureAsync(JobLease lease, WorkflowDispatch dispatch, string failureCode, bool retryable, CancellationToken cancellationToken)
    {
        var key = new WorkflowKey(lease.Scope, dispatch.WorkflowId);
        _ = await Jobs.ExecuteFencedAsync(lease, async (connection, transaction, token) =>
        {
            var snapshot = await SnapshotAsync(connection, transaction, key, locked: true, token).ConfigureAwait(false);
            if (snapshot is null || snapshot.Status is WorkflowStatus.Succeeded or WorkflowStatus.Failed or WorkflowStatus.Canceled ||
                !await ReserveHistoryAsync(connection, transaction, key, token).ConfigureAwait(false))
            {
                return false;
            }

            var state = await NodeAsync(connection, transaction, key, dispatch.NodeId, token).ConfigureAwait(false);
            // Preparation itself can fail before recording the current token.
            if (state is null || (dispatch.Compensation ? state.CompensationJobId : state.JobId) != lease.JobId || state.FencingToken > lease.FencingToken)
            {
                return false;
            }

            bool terminal = !retryable || lease.Attempt >= lease.MaximumAttempts || (!dispatch.Compensation && snapshot.Status == WorkflowStatus.Compensating);
            string eventName = dispatch.Compensation ? (terminal ? "compensation_failed" : "compensation_retry") : (terminal ? "activity_failed" : "activity_retry");
            _ = await AppendAsync(connection, transaction, key, eventName, dispatch.NodeId, failureCode, token).ConfigureAwait(false);
            if (terminal)
            {
                await FailNodeAsync(connection, transaction, key, dispatch.NodeId, failureCode, token).ConfigureAwait(false);
                var definition = await DefinitionAsync(connection, transaction, key.Scope, snapshot.Definition, snapshot.Version, token).ConfigureAwait(false);
                if (dispatch.Compensation)
                {
                    await SetTerminalAsync(connection, transaction, key, WorkflowStatus.Failed, "compensation_failed", token).ConfigureAwait(false);
                    _ = await AppendAsync(connection, transaction, key, "failed", dispatch.NodeId, "compensation_failed", token).ConfigureAwait(false);
                }
                else
                {
                    await BeginCompensationAsync(connection, transaction, key, definition, WorkflowStatus.Failed, failureCode, token).ConfigureAwait(false);
                }
            }

            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static bool Matches(WorkflowNodeSnapshot? state, JobLease lease, bool compensation) =>
        state is not null && (compensation ? state.CompensationJobId : state.JobId) == lease.JobId &&
        state.FencingToken == lease.FencingToken &&
        (compensation ? state.Status == WorkflowNodeStatus.Compensating : state.Status == WorkflowNodeStatus.Running);

    private async ValueTask<WorkflowNodeSnapshot?> NodeAsync(BlueTuskConnection connection, BlueTuskTransaction? transaction,
        WorkflowKey key, string node, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"""
            SELECT status, fencing_token, job_id, compensation_job_id, result, failure_code FROM {_schema}.nodes
            WHERE tenant = @tenant AND queue = @queue AND workflow_id = @id AND id = @node
            """);
        Key(command, key);
        Add(command, "node", node);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(node, (WorkflowNodeStatus)reader.GetInt16(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.GetFieldValue<byte[]>(4), reader.IsDBNull(5) ? null : reader.GetString(5))
            : null;
    }

    private async ValueTask UpdateNodeStateAsync(BlueTuskConnection connection, BlueTuskTransaction transaction,
        WorkflowKey key, string node, WorkflowNodeStatus status, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"""
            UPDATE {_schema}.nodes SET status = @status WHERE tenant = @tenant AND queue = @queue AND workflow_id = @id AND id = @node
            """);
        Key(command, key);
        Add(command, "node", node);
        Add(command, "status", (short)status);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask FailNodeAsync(BlueTuskConnection connection, BlueTuskTransaction transaction,
        WorkflowKey key, string node, string failure, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"""
            UPDATE {_schema}.nodes SET status = 4, failure_code = @failure
            WHERE tenant = @tenant AND queue = @queue AND workflow_id = @id AND id = @node
            """);
        Key(command, key);
        Add(command, "node", node);
        Add(command, "failure", failure);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask BeginCompensationAsync(BlueTuskConnection connection, BlueTuskTransaction transaction,
        WorkflowKey key, WorkflowDefinition definition, WorkflowStatus terminal, string failure, CancellationToken cancellationToken)
    {
        await using (var command = Command(connection, transaction, $"""
            UPDATE {_schema}.instances SET status = 1, terminal_status = @terminal, failure_code = @failure
            WHERE tenant = @tenant AND queue = @queue AND id = @id AND status = 0
            """))
        {
            Key(command, key);
            Add(command, "terminal", (short)terminal);
            Add(command, "failure", failure);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        _ = await AppendAsync(connection, transaction, key, "compensation_requested", node: null, failure, cancellationToken).ConfigureAwait(false);
        await SkipNodesAsync(connection, transaction, key, includeRunning: false, cancellationToken).ConfigureAwait(false);
        await ScheduleCompensationAsync(connection, transaction, key, definition, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask SkipNodesAsync(BlueTuskConnection connection, BlueTuskTransaction transaction,
        WorkflowKey key, bool includeRunning, CancellationToken cancellationToken)
    {
        var skipped = new List<string>();
        await using (var command = Command(connection, transaction, $"""
            UPDATE {_schema}.nodes SET status = 5 WHERE tenant = @tenant AND queue = @queue AND workflow_id = @id
                AND status IN ({(includeRunning ? "0,1,2" : "0,1")}) RETURNING id
            """))
        {
            Key(command, key);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                skipped.Add(reader.GetString(0));
            }
        }

        foreach (string node in skipped)
        {
            _ = await AppendAsync(connection, transaction, key, "node_skipped", node, code: null, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask ScheduleCompensationAsync(BlueTuskConnection connection, BlueTuskTransaction transaction,
        WorkflowKey key, WorkflowDefinition definition, CancellationToken cancellationToken)
    {
        var states = await StatesAsync(connection, transaction, key, cancellationToken).ConfigureAwait(false);
        if (states.Values.Any(status => status is WorkflowNodeStatus.Running or WorkflowNodeStatus.Compensating))
        {
            return;
        }

        var candidates = definition.Nodes.Where(node => node.Compensation is not null && states[node.Id] == WorkflowNodeStatus.Completed)
            .Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        string? next = null;
        await using (var command = Command(connection, transaction, $"""
            SELECT id FROM {_schema}.nodes WHERE tenant = @tenant AND queue = @queue AND workflow_id = @id AND status = 3
            ORDER BY completion_sequence DESC LIMIT @count
            """))
        {
            Key(command, key);
            Add(command, "count", _options.MaximumNodes);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string id = reader.GetString(0);
                if (candidates.Contains(id))
                {
                    next = id;
                    break;
                }
            }
        }

        if (next is not null)
        {
            var node = definition.Nodes.Single(candidate => candidate.Id == next);
            Guid job = await Jobs.EnqueueAsync(DispatchRequest(key, node, compensation: true), transaction, cancellationToken).ConfigureAwait(false);
            await using var command = Command(connection, transaction, $"""
                UPDATE {_schema}.nodes SET status = 6, compensation_job_id = @job, fencing_token = 0
                WHERE tenant = @tenant AND queue = @queue AND workflow_id = @id AND id = @node
                """);
            Key(command, key);
            Add(command, "node", next);
            Add(command, "job", job);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _ = await AppendAsync(connection, transaction, key, "compensation_scheduled", next, code: null, cancellationToken).ConfigureAwait(false);
            return;
        }

        WorkflowStatus terminal;
        string? failure;
        await using (var command = Command(connection, transaction,
            $"SELECT terminal_status, failure_code FROM {_schema}.instances WHERE tenant = @tenant AND queue = @queue AND id = @id"))
        {
            Key(command, key);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            terminal = (WorkflowStatus)reader.GetInt16(0);
            failure = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        await SetTerminalAsync(connection, transaction, key, terminal, failure, cancellationToken).ConfigureAwait(false);
        _ = await AppendAsync(connection, transaction, key, terminal == WorkflowStatus.Canceled ? "canceled" : "failed", node: null, failure, cancellationToken).ConfigureAwait(false);
    }
}
