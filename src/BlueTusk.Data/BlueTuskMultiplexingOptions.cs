namespace BlueTusk.Data;

/// <summary>Controls bounded statement multiplexing for data-source-owned commands.</summary>
public sealed class BlueTuskMultiplexingOptions
{
    /// <summary>
    /// Gets or sets the number of concurrent multiplexing workers. Zero selects a value from the
    /// configured physical pool size.
    /// </summary>
    public int WorkerCount { get; set; }

    /// <summary>Gets or sets the maximum number of commands waiting for a multiplexing worker.</summary>
    public int QueueCapacity { get; set; } = 1_024;

    /// <summary>
    /// Gets or sets the maximum number of commands executed on one physical lease before the lease
    /// is returned to the pool.
    /// </summary>
    public int MaxCommandsPerLease { get; set; } = 65_536;

    /// <summary>
    /// Gets or sets the maximum number of independently synchronized commands written in one
    /// PostgreSQL pipeline flush.
    /// </summary>
    public int MaxPipelineCommands { get; set; } = 64;

    /// <summary>Gets or sets the maximum graceful-drain time when the data source is disposed.</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

    internal ResolvedMultiplexingOptions Resolve(int maximumPoolSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPoolSize);

        if (WorkerCount < 0)
        {
            throw new InvalidOperationException("WorkerCount cannot be negative.");
        }

        if (QueueCapacity <= 0)
        {
            throw new InvalidOperationException("QueueCapacity must be positive.");
        }

        if (MaxCommandsPerLease <= 0)
        {
            throw new InvalidOperationException("MaxCommandsPerLease must be positive.");
        }

        if (MaxPipelineCommands <= 0 || MaxPipelineCommands > MaxCommandsPerLease)
        {
            throw new InvalidOperationException(
                "MaxPipelineCommands must be positive and cannot exceed MaxCommandsPerLease.");
        }

        if (ShutdownTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("ShutdownTimeout must be positive.");
        }

        var workerCount = WorkerCount == 0
            ? Math.Max(1, Math.Min(4, maximumPoolSize / 2))
            : WorkerCount;
        if (workerCount > maximumPoolSize)
        {
            throw new InvalidOperationException(
                "Multiplexing workers cannot exceed Maximum Pool Size.");
        }

        return new ResolvedMultiplexingOptions(
            workerCount,
            QueueCapacity,
            MaxCommandsPerLease,
            MaxPipelineCommands,
            ShutdownTimeout);
    }

    internal BlueTuskMultiplexingOptions Clone() => new()
    {
        WorkerCount = WorkerCount,
        QueueCapacity = QueueCapacity,
        MaxCommandsPerLease = MaxCommandsPerLease,
        MaxPipelineCommands = MaxPipelineCommands,
        ShutdownTimeout = ShutdownTimeout,
    };
}

/// <summary>Controls whether an individual command may use a multiplexed statement lane.</summary>
public enum BlueTuskMultiplexingMode
{
    /// <summary>Use multiplexing only when the provider can classify the command as session-neutral.</summary>
    Auto,

    /// <summary>Require multiplexing and fail before execution when the command requires session affinity.</summary>
    Require,

    /// <summary>Always execute on a dedicated physical lease.</summary>
    Disable,
}

/// <summary>Describes a point-in-time snapshot of one data source's multiplexing scheduler.</summary>
public readonly record struct BlueTuskMultiplexingStatistics(
    bool Enabled,
    int Workers,
    int Queued,
    int Executing,
    long Accepted,
    long Completed,
    long Canceled,
    long Faulted,
    long PipelineFlushes,
    long PipelinedCommands);

internal readonly record struct ResolvedMultiplexingOptions(
    int WorkerCount,
    int QueueCapacity,
    int MaxCommandsPerLease,
    int MaxPipelineCommands,
    TimeSpan ShutdownTimeout);
