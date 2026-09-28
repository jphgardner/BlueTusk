namespace BlueTusk.Edge.Sqlite;

public sealed record SqliteEdgeOptions
{
    public required string DatabasePath { get; init; }
    public int MaxRecordBytes { get; init; } = 512 * 1024;
    public int MaxCacheRecords { get; init; } = 100_000;
    public long MaxCacheBytes { get; init; } = 256L * 1024 * 1024;
    public int MaxPendingMutations { get; init; } = 10_000;
    public long MaxPendingBytes { get; init; } = 32L * 1024 * 1024;
    public int MaxReceiptRecords { get; init; } = 100_000;
    public int MaxScopes { get; init; } = 256;
    public int MaxBatchRecords { get; init; } = 512;
    public long MaxBatchBytes { get; init; } = 8L * 1024 * 1024;
    public int MaxPageSize { get; init; } = 1000;
    public long MaxPageBytes { get; init; } = 8L * 1024 * 1024;
    public int BusyTimeoutSeconds { get; init; } = 5;
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DatabasePath);
        ArgumentNullException.ThrowIfNull(TimeProvider);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxRecordBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxCacheRecords);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxCacheBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPendingMutations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPendingBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxReceiptRecords);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxScopes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxBatchRecords);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxBatchBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPageBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(BusyTimeoutSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(BusyTimeoutSeconds, 60);
        if (MaxRecordBytes > MaxCacheBytes || MaxRecordBytes > MaxPendingBytes || MaxRecordBytes > MaxBatchBytes ||
            MaxRecordBytes > MaxPageBytes || MaxBatchRecords > 10_000 || MaxPageSize > 10_000)
        {
            throw new ArgumentException("Edge record budgets must fit inside bounded cache, queue, batch and page budgets.");
        }
    }
}
