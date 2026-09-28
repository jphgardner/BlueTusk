using System.Data.Common;
using System.Security.Claims;

namespace BlueTusk.Studio;

public sealed record StudioOptions
{
    public string Path { get; init; } = "/bluetusk/studio";
    public required string ReadPolicy { get; init; }
    public required string QueryPolicy { get; init; }
    public int MaximumConcurrentQueries { get; init; } = 8;
    public int MaximumRows { get; init; } = 500;
    public int MaximumRequestBytes { get; init; } = 256 * 1024;
    public int MaximumReplyBytes { get; init; } = 4 * 1024 * 1024;
    public int QueryTimeoutSeconds { get; init; } = 10;

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(ReadPolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(QueryPolicy);
        if (!Path.StartsWith('/') || Path.EndsWith('/') || Path.Any(character =>
            !char.IsAsciiLetterOrDigit(character) && character is not ('/' or '-' or '_')))
        {
            throw new ArgumentException("Studio requires a normalized absolute route prefix.", nameof(Path));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumConcurrentQueries, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumConcurrentQueries, 64);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumRows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumRows, 5000);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumRequestBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumRequestBytes, 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumReplyBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumReplyBytes, 16 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(QueryTimeoutSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(QueryTimeoutSeconds, 30);
    }
}

/// <summary>The resolver selects a principal's database role/data source. SQL authorization remains PostgreSQL-owned.</summary>
public sealed record StudioDatabaseScope(DbDataSource DataSource, IReadOnlyList<string> Schemas);

public interface IStudioScopeResolver
{
    ValueTask<StudioDatabaseScope> ResolveAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default);
}

public sealed record StudioAuditRecord(Guid OperationId, string ActorId, string QueryFingerprint, string Outcome, int ReturnedRows);

public interface IStudioAuditSink
{
    ValueTask RecordAsync(StudioAuditRecord record, CancellationToken cancellationToken = default);
}

public sealed record StudioQueryRequest(string Sql, bool Explain = false, int? MaximumRows = null);
public sealed record StudioQueryResult(ReadOnlyMemory<byte> Json, int Rows);

public sealed class StudioCapacityException : InvalidOperationException
{
    public StudioCapacityException() : base("The Studio query capacity is exhausted.") { }
}

public sealed class StudioReplyLimitException : InvalidOperationException
{
    public StudioReplyLimitException() : base("The Studio query exceeds the configured reply byte bound.") { }
}
