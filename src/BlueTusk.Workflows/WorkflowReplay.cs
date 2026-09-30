using System.Collections.Frozen;
using System.Data;

namespace BlueTusk.Workflows;

public sealed record WorkflowReplayResult(
    WorkflowKey Key, string Definition, int Version, WorkflowStatus Status,
    IReadOnlyDictionary<string, WorkflowNodeStatus> NodeStates, bool MatchesPersistedState);

public sealed partial class PostgreSqlWorkflowStore
{
    /// <summary>Reconstructs execution states from durable history in a consistent snapshot, without executing external activities.</summary>
    public async ValueTask<WorkflowReplayResult?> ReplayAsync(WorkflowKey key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        var snapshot = await SnapshotAsync(connection, transaction, key, locked: false, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return null;
        }

        var definition = await DefinitionAsync(connection, transaction, key.Scope, snapshot.Definition, snapshot.Version, cancellationToken).ConfigureAwait(false);
        var replay = definition.Nodes.ToDictionary(node => node.Id, _ => WorkflowNodeStatus.Blocked, StringComparer.Ordinal);
        await using (var command = Command(connection, transaction, $"""
            SELECT DISTINCT ON (node_id) node_id, event FROM {_schema}.history
            WHERE tenant = @tenant AND queue = @queue AND workflow_id = @id AND node_id IS NOT NULL
                AND event IN ('activity_scheduled','timer_scheduled','activity_started','activity_completed','timer_fired','joined','signal_consumed',
                    'activity_failed','activity_stopped','node_skipped','compensation_scheduled','compensation_started','compensation_completed','compensation_failed')
            ORDER BY node_id, sequence DESC LIMIT @count
            """))
        {
            Key(command, key);
            Add(command, "count", _options.MaximumNodes);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string node = reader.GetString(0);
                if (replay.ContainsKey(node))
                {
                    replay[node] = reader.GetString(1) switch
                    {
                        "activity_scheduled" or "timer_scheduled" => WorkflowNodeStatus.Scheduled,
                        "activity_started" => WorkflowNodeStatus.Running,
                        "activity_completed" or "timer_fired" or "joined" or "signal_consumed" => WorkflowNodeStatus.Completed,
                        "activity_failed" or "compensation_failed" => WorkflowNodeStatus.Failed,
                        "activity_stopped" or "node_skipped" => WorkflowNodeStatus.Skipped,
                        "compensation_scheduled" or "compensation_started" => WorkflowNodeStatus.Compensating,
                        "compensation_completed" => WorkflowNodeStatus.Compensated,
                        _ => throw new InvalidOperationException("Workflow history has an unknown state transition."),
                    };
                }
            }
        }

        WorkflowStatus replayStatus;
        await using (var command = Command(connection, transaction, $"""
            SELECT event FROM {_schema}.history WHERE tenant = @tenant AND queue = @queue AND workflow_id = @id
                AND event IN ('started','compensation_requested','succeeded','failed','canceled') ORDER BY sequence DESC LIMIT 1
            """))
        {
            Key(command, key);
            replayStatus = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) switch
            {
                "started" => WorkflowStatus.Running,
                "compensation_requested" => WorkflowStatus.Compensating,
                "succeeded" => WorkflowStatus.Succeeded,
                "failed" => WorkflowStatus.Failed,
                "canceled" => WorkflowStatus.Canceled,
                _ => throw new InvalidOperationException("Workflow history is missing its execution identity."),
            };
        }

        var persisted = await StatesAsync(connection, transaction, key, cancellationToken).ConfigureAwait(false);
        bool matches = replayStatus == snapshot.Status && replay.Count == persisted.Count &&
            replay.All(pair => persisted.TryGetValue(pair.Key, out var status) && pair.Value == status);
        await using (var command = Command(connection, transaction, $"""
            SELECT count(*), coalesce(max(sequence), 0), coalesce(min(sequence), 0) FROM {_schema}.history
            WHERE tenant = @tenant AND queue = @queue AND workflow_id = @id
            """))
        {
            Key(command, key);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            matches &= reader.GetInt64(0) == snapshot.Revision && reader.GetInt64(1) == snapshot.Revision && reader.GetInt64(2) == 1;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(key, snapshot.Definition, snapshot.Version, replayStatus, replay.ToFrozenDictionary(StringComparer.Ordinal), matches);
    }
}
