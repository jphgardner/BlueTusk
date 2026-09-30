using System.Text.Json;
using BlueTusk.Data;
using BlueTusk.Jobs;

namespace BlueTusk.Workflows;

public sealed partial class PostgreSqlWorkflowStore
{
    private async ValueTask<WorkflowDefinition> DefinitionAsync(BlueTuskConnection connection, BlueTuskTransaction? transaction,
        JobScope scope, string name, int version, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"""
            SELECT definition, fingerprint FROM {_schema}.definitions
            WHERE tenant = @tenant AND queue = @queue AND name = @name AND version = @version
            """);
        Scope(command, scope);
        Add(command, "name", name);
        Add(command, "version", version);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The requested workflow definition version is not registered in this scope.");
        }

        byte[] bytes = reader.GetFieldValue<byte[]>(0);
        byte[] fingerprint = reader.GetFieldValue<byte[]>(1);
        var definition = JsonSerializer.Deserialize(bytes, WorkflowJsonContext.Default.WorkflowDefinition)
            ?? throw new InvalidOperationException("Persisted workflow definition is invalid.");
        var canonical = WorkflowDefinitionValidation.Canonicalize(definition, _options);
        if (definition.Name != name || definition.Version != version || !canonical.Bytes.AsSpan().SequenceEqual(bytes) ||
            !canonical.Fingerprint.AsSpan().SequenceEqual(fingerprint))
        {
            throw new InvalidOperationException("Persisted workflow definition does not match its durable fingerprint.");
        }

        return canonical.Definition;
    }

    private async ValueTask<WorkflowSnapshot?> SnapshotAsync(BlueTuskConnection connection, BlueTuskTransaction? transaction,
        WorkflowKey key, bool locked, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"""
            SELECT definition, version, status, revision, completed_at, failure_code FROM {_schema}.instances
            WHERE tenant = @tenant AND queue = @queue AND id = @id {(locked ? "FOR UPDATE" : string.Empty)}
            """);
        Key(command, key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(key, reader.GetString(0), reader.GetInt32(1), (WorkflowStatus)reader.GetInt16(2), reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4), reader.IsDBNull(5) ? null : reader.GetString(5))
            : null;
    }

    private async ValueTask<IReadOnlyList<WorkflowNodeSnapshot>> NodesAsync(BlueTuskConnection connection, BlueTuskTransaction? transaction,
        WorkflowKey key, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"""
            SELECT id, status, fencing_token, job_id, compensation_job_id, ''::bytea, failure_code FROM {_schema}.nodes
            WHERE tenant = @tenant AND queue = @queue AND workflow_id = @id ORDER BY id LIMIT @count
            """);
        Key(command, key);
        Add(command, "count", _options.MaximumNodes);
        var nodes = new List<WorkflowNodeSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            nodes.Add(new(reader.GetString(0), (WorkflowNodeStatus)reader.GetInt16(1), reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.IsDBNull(4) ? null : reader.GetGuid(4),
                reader.GetFieldValue<byte[]>(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return nodes;
    }

    private async ValueTask<Dictionary<string, WorkflowNodeStatus>> StatesAsync(BlueTuskConnection connection, BlueTuskTransaction transaction,
        WorkflowKey key, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"""
            SELECT id, status FROM {_schema}.nodes WHERE tenant = @tenant AND queue = @queue AND workflow_id = @id LIMIT @count
            """);
        Key(command, key);
        Add(command, "count", _options.MaximumNodes);
        var states = new Dictionary<string, WorkflowNodeStatus>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            states.Add(reader.GetString(0), (WorkflowNodeStatus)reader.GetInt16(1));
        }

        return states;
    }

    private async ValueTask InsertNodeAsync(BlueTuskConnection connection, BlueTuskTransaction transaction,
        WorkflowKey key, string node, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction,
            $"INSERT INTO {_schema}.nodes (tenant, queue, workflow_id, id) VALUES (@tenant, @queue, @id, @node)");
        Key(command, key);
        Add(command, "node", node);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<long> AppendAsync(BlueTuskConnection connection, BlueTuskTransaction transaction,
        WorkflowKey key, string @event, string? node, string? code, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"""
            WITH advanced AS (
                UPDATE {_schema}.instances SET revision = revision + 1, history_sequence = history_sequence + 1
                WHERE tenant = @tenant AND queue = @queue AND id = @id RETURNING history_sequence
            ) INSERT INTO {_schema}.history (tenant, queue, workflow_id, sequence, event, node_id, code)
                SELECT @tenant, @queue, @id, history_sequence, @event, @node, @code FROM advanced RETURNING sequence
            """);
        Key(command, key);
        Add(command, "event", @event);
        Add(command, "node", node);
        Add(command, "code", code);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private async ValueTask<bool> ReserveHistoryAsync(BlueTuskConnection connection, BlueTuskTransaction transaction,
        WorkflowKey key, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"""
            SELECT history_sequence FROM {_schema}.instances WHERE tenant = @tenant AND queue = @queue AND id = @id
            """);
        Key(command, key);
        long sequence = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        if (sequence > _options.MaximumHistoryEntries - (_options.MaximumNodes * 2 + 4))
        {
            await SetTerminalAsync(connection, transaction, key, WorkflowStatus.Failed, "history_limit", cancellationToken).ConfigureAwait(false);
            _ = await AppendAsync(connection, transaction, key, "failed", node: null, "history_limit", cancellationToken).ConfigureAwait(false);
            return false;
        }

        return true;
    }

    private async ValueTask SetTerminalAsync(BlueTuskConnection connection, BlueTuskTransaction transaction,
        WorkflowKey key, WorkflowStatus status, string? failure, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"""
            UPDATE {_schema}.instances SET status = @status, completed_at = clock_timestamp(), failure_code = @failure
            WHERE tenant = @tenant AND queue = @queue AND id = @id
            """);
        Key(command, key);
        Add(command, "status", (short)status);
        Add(command, "failure", failure);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AdvanceAsync(BlueTuskConnection connection, BlueTuskTransaction transaction,
        WorkflowKey key, WorkflowDefinition definition, CancellationToken cancellationToken)
    {
        var states = await StatesAsync(connection, transaction, key, cancellationToken).ConfigureAwait(false);
        bool changed;
        do
        {
            changed = false;
            foreach (var node in definition.Nodes)
            {
                if (states[node.Id] != WorkflowNodeStatus.Blocked || node.DependsOn.Any(dependency => states[dependency] != WorkflowNodeStatus.Completed))
                {
                    continue;
                }

                if (node.Kind == WorkflowNodeKind.Join)
                {
                    await CompleteNodeAsync(connection, transaction, key, node.Id, ReadOnlyMemory<byte>.Empty, "joined", cancellationToken).ConfigureAwait(false);
                    states[node.Id] = WorkflowNodeStatus.Completed;
                    changed = true;
                }
                else if (node.Kind == WorkflowNodeKind.Signal)
                {
                    var signal = await TakeSignalAsync(connection, transaction, key, node, cancellationToken).ConfigureAwait(false);
                    if (signal is not null)
                    {
                        await CompleteNodeAsync(connection, transaction, key, node.Id, signal, "signal_consumed", cancellationToken).ConfigureAwait(false);
                        states[node.Id] = WorkflowNodeStatus.Completed;
                        changed = true;
                    }
                }
                else
                {
                    var request = DispatchRequest(key, node, compensation: false) with { Delay = node.Delay };
                    Guid jobId = await Jobs.EnqueueAsync(request, transaction, cancellationToken).ConfigureAwait(false);
                    await using var command = Command(connection, transaction, $"""
                        UPDATE {_schema}.nodes SET status = 1, job_id = @job
                        WHERE tenant = @tenant AND queue = @queue AND workflow_id = @id AND id = @node AND status = 0
                        """);
                    Key(command, key);
                    Add(command, "node", node.Id);
                    Add(command, "job", jobId);
                    _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    _ = await AppendAsync(connection, transaction, key, node.Kind == WorkflowNodeKind.Timer ? "timer_scheduled" : "activity_scheduled", node.Id, code: null, cancellationToken).ConfigureAwait(false);
                    states[node.Id] = WorkflowNodeStatus.Scheduled;
                }
            }
        }
        while (changed);

        if (states.Values.All(status => status == WorkflowNodeStatus.Completed))
        {
            await SetTerminalAsync(connection, transaction, key, WorkflowStatus.Succeeded, failure: null, cancellationToken).ConfigureAwait(false);
            _ = await AppendAsync(connection, transaction, key, "succeeded", node: null, code: null, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<byte[]?> TakeSignalAsync(BlueTuskConnection connection, BlueTuskTransaction transaction,
        WorkflowKey key, WorkflowNode node, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"""
            WITH selected AS (
                SELECT identity FROM {_schema}.signals WHERE tenant = @tenant AND queue = @queue AND workflow_id = @id
                    AND name = @name AND consumed_by IS NULL ORDER BY received_at, identity FOR UPDATE LIMIT 1
            ) UPDATE {_schema}.signals s SET consumed_by = @node FROM selected x
            WHERE s.tenant = @tenant AND s.queue = @queue AND s.workflow_id = @id AND s.name = @name AND s.identity = x.identity
            RETURNING s.payload
            """);
        Key(command, key);
        Add(command, "node", node.Id);
        Add(command, "name", node.Signal);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as byte[];
    }

    private async ValueTask CompleteNodeAsync(BlueTuskConnection connection, BlueTuskTransaction transaction,
        WorkflowKey key, string node, ReadOnlyMemory<byte> result, string @event, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(result.Length, _options.MaximumResultBytes);
        long sequence = await AppendAsync(connection, transaction, key, @event, node, code: null, cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, transaction, $"""
            UPDATE {_schema}.nodes SET status = 3, result = @result, completion_sequence = @sequence
            WHERE tenant = @tenant AND queue = @queue AND workflow_id = @id AND id = @node
            """);
        Key(command, key);
        Add(command, "node", node);
        Add(command, "result", result.ToArray());
        Add(command, "sequence", sequence);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static JobRequest DispatchRequest(WorkflowKey key, WorkflowNode node, bool compensation) =>
        JobRequest.FromJson(key.Scope, node.Kind == WorkflowNodeKind.Timer ? TimerJobType : ActivityJobType(compensation ? node.Compensation! : node.Activity!),
            new WorkflowDispatch(key.Id, node.Id, compensation), WorkflowJsonContext.Default.WorkflowDispatch) with
        {
            DeduplicationKey = "wf:" + key.Id.ToString("N") + ":" + node.Id + (compensation ? ":comp" : ":run"),
            MaximumAttempts = node.MaximumAttempts,
        };
}
