using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace BlueTusk.Jobs;

/// <summary>An explicit tenant and queue boundary. Every store operation is scoped.</summary>
public sealed record JobScope
{
    public JobScope(string tenant, string queue)
    {
        JobValidation.Name(tenant, nameof(tenant));
        JobValidation.Name(queue, nameof(queue));
        Tenant = tenant;
        Queue = queue;
    }

    public string Tenant { get; }
    public string Queue { get; }
}

public enum JobStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Canceled,
}

/// <summary>Opaque bytes are copied on admission. Use generated JSON metadata for typed jobs.</summary>
public sealed record JobRequest
{
    public required JobScope Scope { get; init; }
    public required string JobType { get; init; }
    public required ReadOnlyMemory<byte> Payload { get; init; }
    public string? DeduplicationKey { get; init; }
    public TimeSpan Delay { get; init; }
    public int MaximumAttempts { get; init; } = 5;

    public static JobRequest FromJson<T>(
        JobScope scope, string jobType, T payload, JsonTypeInfo<T> typeInfo) => new()
        {
            Scope = scope,
            JobType = jobType,
            Payload = JsonSerializer.SerializeToUtf8Bytes(payload, typeInfo),
        };
}

/// <summary>A capability valid only until the database-clock lease deadline.</summary>
public sealed record JobLease(
    Guid JobId,
    JobScope Scope,
    string JobType,
    ReadOnlyMemory<byte> Payload,
    int Attempt,
    int MaximumAttempts,
    string Owner,
    long FencingToken,
    DateTimeOffset ExpiresAt);

public sealed record JobSnapshot(
    Guid JobId,
    JobScope Scope,
    string JobType,
    JobStatus Status,
    int Attempts,
    int MaximumAttempts,
    long FencingToken,
    DateTimeOffset AvailableAt,
    DateTimeOffset? CompletedAt,
    string? LastFailureCode);

public sealed record JobAttempt(
    int Attempt,
    long FencingToken,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string Outcome,
    string? FailureCode);

/// <summary>Recurring schedules enqueue at most one occurrence in each dispatch.</summary>
public enum JobMisfirePolicy
{
    /// <summary>Enqueue one missed occurrence and advance to the first future boundary.</summary>
    Coalesce,
    /// <summary>Skip when one or more complete intervals were missed.</summary>
    Skip,
}

public sealed record RecurringJobSchedule
{
    public required string Name { get; init; }
    public required JobRequest Job { get; init; }
    public required TimeSpan Interval { get; init; }
    public required DateTimeOffset FirstOccurrence { get; init; }
    public JobMisfirePolicy MisfirePolicy { get; init; } = JobMisfirePolicy.Coalesce;
}

internal static class JobValidation
{
    internal static void Name(string value, string parameter, int maximum = 200)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        if (value.Length > maximum || value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Job identity exceeds the allowed length or contains a null character.", parameter);
        }
    }

    internal static void Duration(TimeSpan value, string parameter, TimeSpan maximum, bool allowZero = false)
    {
        if (value < (allowZero ? TimeSpan.Zero : TimeSpan.FromMilliseconds(1)) || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameter);
        }
    }

    internal static void FailureCode(string value)
    {
        Name(value, nameof(value), 64);
        foreach (char character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-' and not '.')
            {
                throw new ArgumentException("Failure codes must contain only ASCII letters, digits, underscore, dash, or period.", nameof(value));
            }
        }
    }
}
