namespace BlueTusk.Data;

/// <summary>Describes a point-in-time snapshot of one data source's connection pool.</summary>
public readonly record struct BlueTuskPoolStatistics(
    bool PoolingEnabled,
    int MinimumSize,
    int MaximumSize,
    int Total,
    int Idle,
    int Busy,
    int Waiting,
    long Opened,
    long Reused,
    long Discarded);
