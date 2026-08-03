using System.Data.Common;
using System.Text;
using BlueTusk.Streams;

namespace BlueTusk.Sync.PostgreSql;

public sealed record PostgreSqlSyncOptions
{
    public required DbDataSource DestinationDataSource { get; init; }

    public string ControlSchema { get; init; } = "bluetusk_sync";

    public int MaxDocumentBytes { get; init; } = 16 * 1024 * 1024;

    public long MaxTransactionBytes { get; init; } = 256L * 1024 * 1024;

    public IPostgreSqlSyncMutationWriter? MutationWriter { get; init; }

    internal string QuotedControlSchema => QuoteIdentifier(ControlSchema);

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(DestinationDataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(ControlSchema);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxDocumentBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxTransactionBytes);
        if (MaxDocumentBytes > MaxTransactionBytes)
        {
            throw new ArgumentException(
                "The document limit cannot exceed the transaction limit.",
                nameof(MaxDocumentBytes));
        }

        if (ControlSchema.Contains('\0') || Encoding.UTF8.GetByteCount(ControlSchema) > 63)
        {
            throw new ArgumentException(
                "The control schema must be a valid PostgreSQL identifier of at most 63 UTF-8 bytes.",
                nameof(ControlSchema));
        }
    }

    private static string QuoteIdentifier(string value) =>
        '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
}

public interface IPostgreSqlSyncMutationWriter
{
    ValueTask ResetSnapshotAsync(
        DbConnection connection,
        DbTransaction transaction,
        string pipelineId,
        SnapshotReset reset,
        CancellationToken cancellationToken = default);

    ValueTask ApplySnapshotBatchAsync(
        DbConnection connection,
        DbTransaction transaction,
        SyncSnapshotBatch batch,
        CancellationToken cancellationToken = default);

    ValueTask ApplyTransactionAsync(
        DbConnection connection,
        DbTransaction transaction,
        SyncTransactionBatch batch,
        CancellationToken cancellationToken = default);
}

public class PostgreSqlSyncException : Exception
{
    public PostgreSqlSyncException(string message)
        : base(message)
    {
    }

    public PostgreSqlSyncException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class PostgreSqlSyncSourceMismatchException : PostgreSqlSyncException
{
    public PostgreSqlSyncSourceMismatchException(string message)
        : base(message)
    {
    }
}

public sealed class PostgreSqlSyncSnapshotException : PostgreSqlSyncException
{
    public PostgreSqlSyncSnapshotException(string message)
        : base(message)
    {
    }
}
