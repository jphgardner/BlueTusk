using System.Data.Common;
using BlueTusk.Streams;
using BlueTusk.TypeSystem;

namespace BlueTusk.Projections;

public sealed record ProjectionIdentity
{
    public ProjectionIdentity(string name, int version, string definitionFingerprint, ChangeSourceIdentity source)
    {
        ProjectionValidation.Key(name, nameof(name));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        ProjectionValidation.Key(definitionFingerprint, nameof(definitionFingerprint));
        ArgumentNullException.ThrowIfNull(source);
        Name = name;
        Version = version;
        DefinitionFingerprint = definitionFingerprint;
        Source = source;
    }

    public string Name { get; }
    public int Version { get; }
    public string DefinitionFingerprint { get; }
    public ChangeSourceIdentity Source { get; }
}

public enum ProjectionBuildPhase
{
    Empty,
    Snapshot,
    CatchingUp,
    Resetting,
}

public sealed record ProjectionState(ProjectionIdentity Identity, ProjectionBuildPhase Phase,
    BlueTuskLogSequenceNumber Checkpoint, Guid? SnapshotEpoch, int ExpectedTables, int CompletedTables,
    long SnapshotRows, long Generation);

public sealed record ProjectionLease(ProjectionIdentity Identity, string OwnerId, long FencingToken);

public sealed record ProjectionDependency
{
    public ProjectionDependency(string tableId, string keyId)
    {
        ProjectionValidation.Key(tableId, nameof(tableId));
        ProjectionValidation.Key(keyId, nameof(keyId));
        TableId = tableId;
        KeyId = keyId;
    }

    public string TableId { get; }
    public string KeyId { get; }
}

public sealed record ProjectionDocument(string TenantId, string Key, ReadOnlyMemory<byte> Payload);

public sealed record ProjectionDocumentWrite(string TenantId, string Key, ReadOnlyMemory<byte> Payload,
    IReadOnlyList<ProjectionDependency> Dependencies);

public sealed record ProjectionDependencyPage(IReadOnlyList<string> DocumentKeys, string? ContinueAfter);

public sealed record ProjectionPublication(int? Version, long Revision);

public sealed record ProjectionPageCursor(int Version, long Revision, string AfterKey);

public sealed record ProjectionDocumentPage(ProjectionPublication Publication,
    IReadOnlyList<ProjectionDocument> Documents, ProjectionPageCursor? ContinueAfter);

public sealed record ProjectionPublishedAggregate(ProjectionPublication Publication, decimal Value);

public sealed record ProjectionSourceWrite(string TenantId, ProjectionDependency SourceKey, ReadOnlyMemory<byte> Payload);

public sealed record ProjectionSourceDelete(string TenantId, ProjectionDependency SourceKey);

public sealed record ProjectionApplyResult(bool WasApplied, BlueTuskLogSequenceNumber Checkpoint, long Generation);

/// <summary>
/// Application-owned multi-table/join/aggregate semantics. Persist all source mirrors, read models and
/// dependencies through the context. Never query the current source database to reconstruct historical WAL.
/// </summary>
public interface IProjectionDefinition
{
    ProjectionIdentity Identity { get; }

    ValueTask ApplySnapshotAsync(ChangeSnapshotBatch batch, ProjectionWriteContext context,
        CancellationToken cancellationToken);

    ValueTask ApplyTransactionAsync(ChangeTransaction transaction, ProjectionWriteContext context,
        CancellationToken cancellationToken);
}

public sealed class ProjectionFencedException : InvalidOperationException
{
    public ProjectionFencedException() : base("The projection worker's lease is expired or replaced. Its work must be rolled back.") { ProjectionsDiagnostics.LeaseFenced(); }
}

public sealed class ProjectionBoundExceededException : InvalidOperationException
{
    public ProjectionBoundExceededException(string message) : base(message) { ProjectionsDiagnostics.BoundExceeded(); }
}

public sealed class ProjectionRetiredException : InvalidOperationException
{
    public ProjectionRetiredException() : base("This projection version is permanently retired. Its identity and fencing tombstones are retained; register a new version.") { }
}

public sealed record ProjectionRetentionResult(int DeletedRows, bool HasRemainingRows);

public sealed record ProjectionSnapshotResetProgress(Guid Epoch, int DeletedRows, bool IsComplete);

public sealed class PostgreSqlProjectionsOptions
{
    public string Schema { get; init; } = "bluetusk_projections";
    public int MaximumChangesPerTransaction { get; init; } = 100_000;
    public long MaximumTransactionBytes { get; init; } = 67_108_864;
    public int MaximumSnapshotBatchRows { get; init; } = 2048;
    public int MaximumDocumentBytes { get; init; } = 1_048_576;
    public int MaximumDependenciesPerDocument { get; init; } = 256;
    public int MaximumDependencyPageSize { get; init; } = 1024;
    public int MaximumInvalidationsPerTransaction { get; init; } = 100_000;
    public int MaximumWriteOperationsPerTransaction { get; init; } = 100_000;
    public int MaximumWriteBatchSize { get; init; } = 1024;
    public int MaximumWriteBatchBytes { get; init; } = 8_388_608;
    public int MaximumResetBatchRows { get; init; } = 1024;
    public int CommandTimeoutSeconds { get; init; } = 30;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumResetBatchRows);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumResetBatchRows, 65_536);
        ArgumentException.ThrowIfNullOrWhiteSpace(Schema);
        if (Schema.Length > 63 || Schema.Any(static character => !char.IsAsciiLetterOrDigit(character) && character != '_') ||
            !char.IsAsciiLetter(Schema[0]) && Schema[0] != '_')
        {
            throw new ArgumentException("Schema must be a 1 to 63 character ASCII PostgreSQL identifier.", nameof(Schema));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumChangesPerTransaction);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumTransactionBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumSnapshotBatchRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumDocumentBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumDocumentBytes, 16_777_216);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumDependenciesPerDocument);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumDependencyPageSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumDependencyPageSize, 65_536);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumInvalidationsPerTransaction);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumWriteOperationsPerTransaction);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumWriteBatchSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumWriteBatchSize, 65_536);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumWriteBatchBytes, MaximumDocumentBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumWriteBatchBytes, 67_108_864);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(CommandTimeoutSeconds);
    }
}

internal static class ProjectionValidation
{
    internal static void Key(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 200 || value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Projection keys must contain 1 to 200 characters and no NUL characters.", parameterName);
        }
    }
}

internal static class ProjectionSql
{
    internal static DbCommand Command(DbConnection connection, DbTransaction? transaction, int timeout,
        string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = timeout;
        command.CommandText = sql;
        foreach (var entry in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = entry.Name;
            parameter.Value = entry.Value;
            command.Parameters.Add(parameter);
        }

        return command;
    }
}
