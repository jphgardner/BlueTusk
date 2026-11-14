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
        BlueTuskGuard.NotNullOrWhiteSpace(Path);
        BlueTuskGuard.NotNullOrWhiteSpace(ReadPolicy);
        BlueTuskGuard.NotNullOrWhiteSpace(QueryPolicy);
        if (!Path.StartsWith('/') || Path.EndsWith('/') || Path.Any(character =>
            !BlueTuskRuntime.IsAsciiLetterOrDigit(character) && character is not ('/' or '-' or '_')))
        {
            throw new ArgumentException("Studio requires a normalized absolute route prefix.", nameof(Path));
        }
        BlueTuskGuard.NotLessThan(MaximumConcurrentQueries, 1);
        BlueTuskGuard.NotGreaterThan(MaximumConcurrentQueries, 64);
        BlueTuskGuard.NotLessThan(MaximumRows, 1);
        BlueTuskGuard.NotGreaterThan(MaximumRows, 5000);
        BlueTuskGuard.NotLessThan(MaximumRequestBytes, 1024);
        BlueTuskGuard.NotGreaterThan(MaximumRequestBytes, 1024 * 1024);
        BlueTuskGuard.NotLessThan(MaximumReplyBytes, 1024);
        BlueTuskGuard.NotGreaterThan(MaximumReplyBytes, 16 * 1024 * 1024);
        BlueTuskGuard.NotLessThan(QueryTimeoutSeconds, 1);
        BlueTuskGuard.NotGreaterThan(QueryTimeoutSeconds, 30);
    }
}

/// <summary>The resolver selects a principal's database role/data source. SQL authorization remains PostgreSQL-owned.</summary>
public sealed record StudioDatabaseScope(DbDataSource DataSource, IReadOnlyList<string> Schemas)
{
    /// <summary>Host-issued stable, non-sensitive identity for the selected database/tenant audit scope.</summary>
    public string AuditScopeId { get; init; } = string.Empty;
}

public interface IStudioScopeResolver
{
    ValueTask<StudioDatabaseScope> ResolveAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default);
}

public sealed record StudioAuditRecord(Guid OperationId, string ActorId, string QueryFingerprint, string Outcome, int ReturnedRows)
{
    public string ScopeId { get; init; } = string.Empty;
}

public interface IStudioAuditSink
{
    ValueTask RecordAsync(StudioAuditRecord record, CancellationToken cancellationToken = default);
}

/// <summary>An operation identity falls at or before the sealed audit retention horizon.</summary>
public sealed class StudioAuditHorizonException : InvalidOperationException
{
    public StudioAuditHorizonException() : base("The Studio audit operation ID is sealed by the retention horizon.") { }
}

public sealed record StudioQueryRequest(string Sql, bool Explain = false, int? MaximumRows = null)
{
    /// <summary>Client-issued identity retained for audit reconciliation after an uncertain response.</summary>
    public Guid OperationId { get; init; }
}
public sealed record StudioQueryResult(ReadOnlyMemory<byte> Json, int Rows);

public sealed class StudioCapacityException : InvalidOperationException
{
    public StudioCapacityException() : base("The Studio query capacity is exhausted.") { }
}

public sealed class StudioReplyLimitException : InvalidOperationException
{
    public StudioReplyLimitException() : base("The Studio query exceeds the configured reply byte bound.") { }
}
