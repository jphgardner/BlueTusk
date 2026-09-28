using BlueTusk.Jobs;

namespace BlueTusk.Workflows;

public sealed record WorkflowScopeHealth(
    int RunningObserved, bool RunningCountCapped,
    int CompensatingObserved, bool CompensatingCountCapped,
    TimeSpan? OldestActiveAge);

public sealed partial class PostgreSqlWorkflowStore
{
    /// <summary>Reads bounded active-instance counts and database-clock age without enumerating terminal history.</summary>
    public async ValueTask<WorkflowScopeHealth> InspectAsync(JobScope scope, int maximumObserved = 10000, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        WorkflowOptions.Range(maximumObserved, 1, 100000, nameof(maximumObserved));
        await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, transaction: null, $"""
            SELECT
                (SELECT count(*)::integer FROM (SELECT 1 FROM {_schema}.instances WHERE tenant = @tenant AND queue = @queue AND status = 0 LIMIT @limit) r),
                (SELECT count(*)::integer FROM (SELECT 1 FROM {_schema}.instances WHERE tenant = @tenant AND queue = @queue AND status = 1 LIMIT @limit) c),
                (SELECT least(greatest(extract(epoch FROM (clock_timestamp() - created_at)), 0), 3153600000)::double precision
                    FROM {_schema}.instances WHERE tenant = @tenant AND queue = @queue AND status IN (0,1)
                    ORDER BY created_at, id LIMIT 1)
            """);
        Scope(command, scope);
        Add(command, "limit", maximumObserved + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        int running = reader.GetInt32(0);
        int compensating = reader.GetInt32(1);
        return new(Math.Min(running, maximumObserved), running > maximumObserved,
            Math.Min(compensating, maximumObserved), compensating > maximumObserved,
            reader.IsDBNull(2) ? null : TimeSpan.FromSeconds(reader.GetDouble(2)));
    }
}
