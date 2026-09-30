using System.Text.Json.Serialization;
using BlueTusk.Jobs;

namespace BlueTusk.Workflows;

public enum WorkflowNodeKind { Activity, Timer, Signal, Join }
public enum WorkflowStatus { Running, Compensating, Succeeded, Failed, Canceled }
public enum WorkflowNodeStatus { Blocked, Scheduled, Running, Completed, Failed, Skipped, Compensating, Compensated }

public sealed record WorkflowNode
{
    public required string Id { get; init; }
    public required WorkflowNodeKind Kind { get; init; }
    public IReadOnlyList<string> DependsOn { get; init; } = Array.Empty<string>();
    public string? Activity { get; init; }
    public string? Compensation { get; init; }
    public string? Signal { get; init; }
    public TimeSpan Delay { get; init; }
    public int MaximumAttempts { get; init; } = 5;
}

public sealed record WorkflowDefinition
{
    public required string Name { get; init; }
    public required int Version { get; init; }
    public required IReadOnlyList<WorkflowNode> Nodes { get; init; }
}

public sealed record WorkflowKey(JobScope Scope, Guid Id);
public sealed record WorkflowStartRequest
{
    public required JobScope Scope { get; init; }
    public required string Definition { get; init; }
    public required int Version { get; init; }
    public required ReadOnlyMemory<byte> Input { get; init; }
    public string? DeduplicationKey { get; init; }
}

public sealed record WorkflowSnapshot(
    WorkflowKey Key, string Definition, int Version, WorkflowStatus Status,
    long Revision, DateTimeOffset? CompletedAt, string? FailureCode);

public sealed record WorkflowNodeSnapshot(
    string Id, WorkflowNodeStatus Status, long FencingToken,
    Guid? JobId, Guid? CompensationJobId, ReadOnlyMemory<byte> Result, string? FailureCode);

public sealed record WorkflowHistoryEntry(
    long Sequence, DateTimeOffset Timestamp, string Event, string? NodeId, string? Code);

public sealed record WorkflowActivityContext(
    WorkflowKey Workflow, string NodeId, string Activity, bool IsCompensation,
    int Attempt, long FencingToken, string IdempotencyKey,
    ReadOnlyMemory<byte> InitialInput, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> DependencyResults,
    ReadOnlyMemory<byte> ActivityResult);

internal sealed record WorkflowDispatch(Guid WorkflowId, string NodeId, bool Compensation);

[JsonSerializable(typeof(WorkflowDefinition))]
[JsonSerializable(typeof(WorkflowDispatch))]
internal sealed partial class WorkflowJsonContext : JsonSerializerContext;
