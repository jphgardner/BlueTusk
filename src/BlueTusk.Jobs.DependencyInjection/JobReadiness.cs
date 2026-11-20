using System.Text.Json.Serialization;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BlueTusk.Jobs.DependencyInjection;

public sealed record JobReadiness(HealthStatus Status, string Code, JobQueueHealth? Queue);

[JsonSerializable(typeof(JobReadiness))]
internal sealed partial class JobReadinessJsonContext : JsonSerializerContext;

public sealed record JobHealthCheckOptions
{
    public int MaximumObserved { get; init; } = 1000;
    public int MaximumPending { get; init; } = 1000;
    public int MaximumExpiredLeases { get; init; }
    public TimeSpan MaximumReadyAge { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    internal void Validate()
    {
        BlueTuskGuard.NotLessThan(MaximumObserved, 1);
        BlueTuskGuard.NotGreaterThan(MaximumObserved, 100000);
        BlueTuskGuard.NotNegative(MaximumPending);
        BlueTuskGuard.NotGreaterThan(MaximumPending, MaximumObserved);
        BlueTuskGuard.NotNegative(MaximumExpiredLeases);
        BlueTuskGuard.NotGreaterThan(MaximumExpiredLeases, MaximumObserved);
        if (MaximumReadyAge < TimeSpan.FromMilliseconds(1) || MaximumReadyAge > TimeSpan.FromDays(365))
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumReadyAge));
        }
        if (Timeout < TimeSpan.FromMilliseconds(10) || Timeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(Timeout));
        }
    }
}
