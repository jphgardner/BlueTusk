namespace BlueTusk.Edge.Server;

public enum EdgeServerDataSourceOwnership { Borrowed, Owned }
public sealed record EdgeServerOptions
{
    public string Schema { get; init; } = "bluetusk_edge";
    public int MaxScopes { get; init; } = 256;
    public int MaxRecordBytes { get; init; } = 512 * 1024;
    public int MaxRecordsPerScope { get; init; } = 100_000;
    public long MaxRecordBytesPerScope { get; init; } = 256L * 1024 * 1024;
    public int MaxReceiptsPerScope { get; init; } = 100_000;
    /// <summary>Bounds durable ordered-client stream fences in each scope epoch.</summary>
    public int MaxOrderedStreamsPerScope { get; init; } = 1024;
    /// <summary>Caps retained authoritative outcome payloads; a new identity is refused rather than evicting its retry fence.</summary>
    public long MaxReceiptBytesPerScope { get; init; } = 256L * 1024 * 1024;
    public int MaxChangesPerScope { get; init; } = 100_000;
    public long MaxChangeBytesPerScope { get; init; } = 256L * 1024 * 1024;
    public int MaxSnapshotsPerScope { get; init; } = 4;
    public int MaxBatchRecords { get; init; } = 512;
    public long MaxBatchBytes { get; init; } = 8L * 1024 * 1024;
    public TimeSpan SnapshotLifetime { get; init; } = TimeSpan.FromMinutes(2);
    public int CommandTimeoutSeconds { get; init; } = 30;
}
public sealed record EdgeServerSnapshotPage(IReadOnlyList<EdgeRecord> Records, string? NextAfterId);
public sealed class EdgeServerSnapshotExpiredException() : Exception("The Edge snapshot is expired, missing or belongs to another scope.");
public sealed class EdgeServerReplayExpiredException() : Exception("The requested Edge checkpoint precedes the retained replay floor; obtain a fresh consistent snapshot.");
public sealed class EdgeServerReceiptFinalizedException() : Exception("The mutation receipt was confirmed and its outcome was released; the mutation identity cannot be applied again.");
public sealed class EdgeServerReceiptMissingException() : Exception("The mutation receipt does not exist in the active scope.");
