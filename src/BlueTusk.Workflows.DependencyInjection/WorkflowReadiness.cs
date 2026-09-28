using System.Text.Json.Serialization;
using BlueTusk.Jobs;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BlueTusk.Workflows.DependencyInjection;

public sealed record WorkflowReadiness(HealthStatus Status, string Code, WorkflowScopeHealth? Scope, JobQueueHealth? Dispatch);

[JsonSerializable(typeof(WorkflowReadiness))]
internal sealed partial class WorkflowReadinessJsonContext : JsonSerializerContext;

public sealed record WorkflowHealthCheckOptions
{
    public int MaximumObserved { get; init; } = 1000;
    public int MaximumRunning { get; init; } = 1000;
    public int MaximumCompensating { get; init; } = 1000;
    public int MaximumPendingJobs { get; init; } = 1000;
    public int MaximumExpiredLeases { get; init; }
    public TimeSpan? MaximumActiveAge { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumObserved, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumObserved, 100000);
        foreach (int value in new[] { MaximumRunning, MaximumCompensating, MaximumPendingJobs, MaximumExpiredLeases })
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaximumObserved);
        }
        if (MaximumActiveAge is { } age && (age < TimeSpan.FromMilliseconds(1) || age > TimeSpan.FromDays(365)))
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumActiveAge));
        }
        if (Timeout < TimeSpan.FromMilliseconds(10) || Timeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(Timeout));
        }
    }
}
