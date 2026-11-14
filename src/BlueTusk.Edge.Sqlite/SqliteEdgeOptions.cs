namespace BlueTusk.Edge.Sqlite;

public sealed record SqliteEdgeOptions
{
    public required string DatabasePath { get; init; }
    public int MaxRecordBytes { get; init; } = 512 * 1024;
    /// <summary>Maximum authoritative cached rows; snapshot staging has its own reserve.</summary>
    public int MaxCacheRecords { get; init; } = 100_000;
    public long MaxCacheBytes { get; init; } = 256L * 1024 * 1024;
    /// <summary>Transient snapshot reserve. A full refresh can require active plus staged storage on disk.</summary>
    public int MaxStagedRecords { get; init; } = 100_000;
    public long MaxStagedBytes { get; init; } = 256L * 1024 * 1024;
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
        BlueTuskGuard.NotNullOrWhiteSpace(DatabasePath);
        BlueTuskGuard.NotNull(TimeProvider);
        BlueTuskGuard.NotNegativeOrZero(MaxRecordBytes);
        BlueTuskGuard.NotNegativeOrZero(MaxCacheRecords);
        BlueTuskGuard.NotNegativeOrZero(MaxCacheBytes);
        BlueTuskGuard.NotNegativeOrZero(MaxStagedRecords);
        BlueTuskGuard.NotNegativeOrZero(MaxStagedBytes);
        BlueTuskGuard.NotNegativeOrZero(MaxPendingMutations);
        BlueTuskGuard.NotNegativeOrZero(MaxPendingBytes);
        BlueTuskGuard.NotNegativeOrZero(MaxReceiptRecords);
        BlueTuskGuard.NotNegativeOrZero(MaxScopes);
        BlueTuskGuard.NotNegativeOrZero(MaxBatchRecords);
        BlueTuskGuard.NotNegativeOrZero(MaxBatchBytes);
        BlueTuskGuard.NotNegativeOrZero(MaxPageSize);
        BlueTuskGuard.NotNegativeOrZero(MaxPageBytes);
        BlueTuskGuard.NotLessThan(BusyTimeoutSeconds, 1);
        BlueTuskGuard.NotGreaterThan(BusyTimeoutSeconds, 60);
        if (MaxRecordBytes > MaxCacheBytes || MaxRecordBytes > MaxStagedBytes || MaxRecordBytes > MaxPendingBytes || MaxRecordBytes > MaxBatchBytes ||
            MaxRecordBytes > MaxPageBytes || MaxBatchRecords > 10_000 || MaxPageSize > 10_000)
        {
            throw new ArgumentException("Edge record budgets must fit inside bounded cache, queue, batch and page budgets.");
        }
    }
}
