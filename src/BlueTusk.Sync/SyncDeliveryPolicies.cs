using System.Text;

namespace BlueTusk.Sync;

/// <summary>Identifies destination operations eligible for an explicit retry policy.</summary>
public enum SyncPipelineOperation
{
    Provision,
    ResetSnapshot,
    StartSnapshot,
    ApplySnapshotBatch,
    CompleteSnapshot,
    ApplyTransaction,
    StoreQuarantine,
}

/// <summary>Describes one failed destination attempt before a retry decision.</summary>
public sealed record SyncRetryContext(
    string PipelineId,
    string Destination,
    SyncPipelineOperation Operation,
    int Attempt,
    Exception Exception);

/// <summary>Classifies destination failures; permanent failures are never retried implicitly.</summary>
public interface ISyncRetryClassifier
{
    bool IsTransient(SyncRetryContext context);
}

/// <summary>Controls bounded exponential retry for explicitly transient destination failures.</summary>
public sealed record SyncRetryOptions
{
    public int MaximumAttempts { get; init; } = 5;

    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    public TimeSpan MaximumDelay { get; init; } = TimeSpan.FromSeconds(10);

    public double BackoffFactor { get; init; } = 2;

    public double JitterRatio { get; init; } = 0.2;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumAttempts, 100);
        ArgumentOutOfRangeException.ThrowIfLessThan(InitialDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumDelay, InitialDelay);
        if (!double.IsFinite(BackoffFactor) || BackoffFactor < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(BackoffFactor));
        }

        if (!double.IsFinite(JitterRatio) || JitterRatio is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(JitterRatio));
        }
    }

    internal TimeSpan DelayBeforeAttempt(int nextAttempt)
    {
        var exponent = Math.Max(0, nextAttempt - 2);
        var milliseconds = Math.Min(
            MaximumDelay.TotalMilliseconds,
            InitialDelay.TotalMilliseconds * Math.Pow(BackoffFactor, exponent));
        if (JitterRatio > 0 && milliseconds > 0)
        {
            var jitter = 1 + ((Random.Shared.NextDouble() * 2 - 1) * JitterRatio);
            milliseconds = Math.Clamp(
                milliseconds * jitter,
                0,
                MaximumDelay.TotalMilliseconds);
        }

        return TimeSpan.FromMilliseconds(milliseconds);
    }
}

/// <summary>Applies sequential transaction and transformed-byte pacing.</summary>
public sealed record SyncRateLimitOptions
{
    public double? MaximumTransactionsPerSecond { get; init; }

    public long? MaximumTransformedBytesPerSecond { get; init; }

    internal void Validate()
    {
        if (MaximumTransactionsPerSecond is { } transactions &&
            (!double.IsFinite(transactions) || transactions <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumTransactionsPerSecond));
        }

        if (MaximumTransformedBytesPerSecond is { } bytes && bytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumTransformedBytesPerSecond));
        }
    }
}

internal sealed class SyncDeliveryRateLimiter
{
    private readonly SyncRateLimitOptions _options;
    private readonly TimeProvider _timeProvider;
    private double _nextTransactionTimestamp;
    private double _nextByteTimestamp;
    private bool _initialized;

    public SyncDeliveryRateLimiter(
        SyncRateLimitOptions options,
        TimeProvider timeProvider)
    {
        _options = options;
        _timeProvider = timeProvider;
    }

    public async ValueTask<TimeSpan> WaitAsync(
        long transformedBytes,
        bool countTransaction,
        CancellationToken cancellationToken)
    {
        if (_options.MaximumTransactionsPerSecond is null &&
            _options.MaximumTransformedBytesPerSecond is null)
        {
            return TimeSpan.Zero;
        }

        var now = (double)_timeProvider.GetTimestamp();
        if (!_initialized)
        {
            _nextTransactionTimestamp = now;
            _nextByteTimestamp = now;
            _initialized = true;
        }

        var scheduled = Math.Max(now, Math.Max(_nextTransactionTimestamp, _nextByteTimestamp));
        var frequency = _timeProvider.TimestampFrequency;
        _nextTransactionTimestamp = countTransaction &&
                                    _options.MaximumTransactionsPerSecond is { } transactions
            ? scheduled + (frequency / transactions)
            : scheduled;
        _nextByteTimestamp = transformedBytes > 0 &&
                             _options.MaximumTransformedBytesPerSecond is { } bytes
            ? scheduled + ((frequency * transformedBytes) / bytes)
            : scheduled;
        var delayTicks = scheduled - now;
        if (delayTicks <= 0)
        {
            return TimeSpan.Zero;
        }

        var delay = TimeSpan.FromSeconds(delayTicks / frequency);
        await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
        return delay;
    }

    public static long EstimateBytes(IEnumerable<SyncMutation> mutations)
    {
        long total = 0;
        foreach (var mutation in mutations)
        {
            total = checked(total + mutation.Content.Length);
            total = checked(total + Encoding.UTF8.GetByteCount(mutation.Collection));
            total = checked(total + Encoding.UTF8.GetByteCount(mutation.Key ?? string.Empty));
            total = checked(total + Encoding.UTF8.GetByteCount(mutation.PartitionKey ?? string.Empty));
            total = checked(total + Encoding.UTF8.GetByteCount(mutation.ContentType ?? string.Empty));
        }

        return total;
    }

    public static long EstimateBytes(IEnumerable<SyncSnapshotMutation> mutations)
    {
        long total = 0;
        foreach (var mutation in mutations)
        {
            total = checked(total + mutation.Content.Length);
            total = checked(total + Encoding.UTF8.GetByteCount(mutation.Collection));
            total = checked(total + Encoding.UTF8.GetByteCount(mutation.Key));
            total = checked(total + Encoding.UTF8.GetByteCount(mutation.PartitionKey ?? string.Empty));
            total = checked(total + Encoding.UTF8.GetByteCount(mutation.ContentType));
        }

        return total;
    }
}
