using System.Collections.Frozen;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace BlueTusk.Jobs;

public sealed record JobExecutionContext(Guid JobId, JobScope Scope, int Attempt, long FencingToken)
{
    public JobLease? Lease { get; init; }
}

public interface IJobHandler<in T>
{
    ValueTask ExecuteAsync(T payload, JobExecutionContext context, CancellationToken cancellationToken);
}

/// <summary>A stable classification without exception text. Retry policy lives in the worker.</summary>
public sealed class JobHandlerException : Exception
{
    public JobHandlerException(string failureCode, bool retryable = true)
        : base("A job handler reported a classified failure.")
    {
        JobValidation.FailureCode(failureCode);
        FailureCode = failureCode;
        Retryable = retryable;
    }

    public string FailureCode { get; }
    public bool Retryable { get; }
}

/// <summary>Register before starting a worker. The worker takes an immutable registry snapshot.</summary>
public sealed class JobHandlerRegistry
{
    private readonly Dictionary<string, Func<JobLease, CancellationToken, ValueTask>> _handlers = new(StringComparer.Ordinal);

    public JobHandlerRegistry Register<T>(string jobType, JsonTypeInfo<T> typeInfo, IJobHandler<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Register(jobType, typeInfo, handler.ExecuteAsync);
    }

    public JobHandlerRegistry Register<T>(
        string jobType, JsonTypeInfo<T> typeInfo, Func<T, JobExecutionContext, CancellationToken, ValueTask> handler)
    {
        JobValidation.Name(jobType, nameof(jobType));
        ArgumentNullException.ThrowIfNull(typeInfo);
        ArgumentNullException.ThrowIfNull(handler);
        if (_handlers.Count >= 128)
        {
            throw new InvalidOperationException("A worker registry supports at most 128 job types.");
        }

        _handlers.Add(jobType, (lease, cancellationToken) =>
        {
            T? payload;
            try
            {
                payload = JsonSerializer.Deserialize(lease.Payload.Span, typeInfo);
            }
            catch (JsonException)
            {
                throw new JobHandlerException("invalid_payload", retryable: false);
            }

            if (payload is null)
            {
                throw new JobHandlerException("invalid_payload", retryable: false);
            }

            return handler(payload, new JobExecutionContext(lease.JobId, lease.Scope, lease.Attempt, lease.FencingToken) { Lease = lease }, cancellationToken);
        });
        return this;
    }

    internal FrozenDictionary<string, Func<JobLease, CancellationToken, ValueTask>> Snapshot() => _handlers.ToFrozenDictionary(StringComparer.Ordinal);
}

public sealed record JobRetryPolicy
{
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaximumDelay { get; init; } = TimeSpan.FromHours(1);
    public double JitterFraction { get; init; } = 0.2;

    public TimeSpan GetDelay(int attempt)
    {
        Validate();
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        double ticks = Math.Min(MaximumDelay.Ticks, InitialDelay.Ticks * Math.Pow(2, Math.Min(attempt - 1, 62)));
        // One-sided jitter avoids exceeding the configured ceiling.
        ticks *= 1 - Random.Shared.NextDouble() * JitterFraction;
        return TimeSpan.FromTicks((long)ticks);
    }

    internal void Validate()
    {
        JobValidation.Duration(InitialDelay, nameof(InitialDelay), TimeSpan.FromDays(365));
        JobValidation.Duration(MaximumDelay, nameof(MaximumDelay), TimeSpan.FromDays(365));
        if (MaximumDelay < InitialDelay || !double.IsFinite(JitterFraction) || JitterFraction < 0 || JitterFraction > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(JitterFraction));
        }
    }
}

public sealed record JobWorkerOptions
{
    public int Concurrency { get; init; } = 8;
    public int ClaimBatchSize { get; init; } = 32;
    public int MaximumInFlightPayloadBytes { get; init; } = 16_777_216;
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan StoreFailureBackoff { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan SchedulePollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public bool DispatchRecurringSchedules { get; init; } = true;
    public JobRetryPolicy RetryPolicy { get; init; } = new();

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(Concurrency, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Concurrency, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(ClaimBatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ClaimBatchSize, 256);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumInFlightPayloadBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumInFlightPayloadBytes, 1_073_741_824);
        JobValidation.Duration(LeaseDuration, nameof(LeaseDuration), TimeSpan.FromDays(1));
        JobValidation.Duration(HeartbeatInterval, nameof(HeartbeatInterval), TimeSpan.FromHours(8));
        JobValidation.Duration(PollInterval, nameof(PollInterval), TimeSpan.FromMinutes(1));
        JobValidation.Duration(StoreFailureBackoff, nameof(StoreFailureBackoff), TimeSpan.FromMinutes(5));
        JobValidation.Duration(SchedulePollInterval, nameof(SchedulePollInterval), TimeSpan.FromMinutes(5));
        if (HeartbeatInterval > LeaseDuration / 3)
        {
            throw new ArgumentException("Heartbeat interval must be at most one third of the lease duration.");
        }

        ArgumentNullException.ThrowIfNull(RetryPolicy);
        RetryPolicy.Validate();
    }
}

/// <summary>Bounded, structured worker lifetime. RunAsync owns and observes every handler and heartbeat task.</summary>
public sealed class JobWorker
{
    private readonly PostgreSqlJobStore _store;
    private readonly JobScope _scope;
    private readonly string _owner;
    private readonly JobWorkerOptions _options;
    private readonly FrozenDictionary<string, Func<JobLease, CancellationToken, ValueTask>> _handlers;
    private readonly string[] _jobTypes;
    private int _running;

    public JobWorker(PostgreSqlJobStore store, JobScope scope, string owner, JobHandlerRegistry handlers, JobWorkerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(handlers);
        JobValidation.Name(owner, nameof(owner));
        _options = options ?? new JobWorkerOptions();
        _options.Validate();
        if (_options.ClaimBatchSize > store.MaximumClaimBatch || _options.MaximumInFlightPayloadBytes < store.MaximumPayloadBytes)
        {
            throw new ArgumentException("Worker batch or memory limit is incompatible with the store admission limits.", nameof(options));
        }

        _handlers = handlers.Snapshot();
        if (_handlers.Count == 0)
        {
            throw new ArgumentException("A worker requires at least one registered handler.", nameof(handlers));
        }

        _jobTypes = _handlers.Keys.ToArray();
        _store = store;
        _scope = scope;
        _owner = owner;
    }

    /// <summary>Cancellation stops claiming, cancels cooperative handlers, and awaits their exit. Uncompleted leases recover by expiry.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            throw new InvalidOperationException("This worker is already running.");
        }

        var active = new List<(Task Task, int Bytes)>(_options.Concurrency);
        int activeBytes = 0;
        long lastSchedulePoll = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                for (int index = active.Count - 1; index >= 0; index--)
                {
                    if (active[index].Task.IsCompleted)
                    {
                        await active[index].Task.ConfigureAwait(false);
                        activeBytes -= active[index].Bytes;
                        active.RemoveAt(index);
                    }
                }

                int memorySlots = (_options.MaximumInFlightPayloadBytes - activeBytes) / _store.MaximumPayloadBytes;
                if (active.Count >= _options.Concurrency || memorySlots == 0)
                {
                    await Task.WhenAny(active.Select(item => item.Task)).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    if (_options.DispatchRecurringSchedules &&
                        (lastSchedulePoll == 0 || Stopwatch.GetElapsedTime(lastSchedulePoll) >= _options.SchedulePollInterval))
                    {
                        _ = await _store.DispatchSchedulesAsync(_scope, _options.ClaimBatchSize, cancellationToken).ConfigureAwait(false);
                        lastSchedulePoll = Stopwatch.GetTimestamp();
                    }

                    int count = Math.Min(Math.Min(_options.ClaimBatchSize, _options.Concurrency - active.Count), memorySlots);
                    var leases = await _store.ClaimAsync(_scope, _owner, count, _options.LeaseDuration, _jobTypes, cancellationToken).ConfigureAwait(false);
                    foreach (var lease in leases)
                    {
                        // A synchronous part of a handler cannot delay starting other claimed leases.
                        active.Add((Task.Run(() => ExecuteAsync(lease, cancellationToken), CancellationToken.None), lease.Payload.Length));
                        activeBytes += lease.Payload.Length;
                    }

                    if (leases.Count == 0)
                    {
                        await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    JobTelemetry.StoreFailures.Add(1);
                    await Task.Delay(_options.StoreFailureBackoff, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cooperative stop; jobs retain their leases for bounded crash recovery.
        }
        finally
        {
            try
            {
                await Task.WhenAll(active.Select(item => item.Task)).ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _running, 0);
            }
        }
    }

    private async Task ExecuteAsync(JobLease lease, CancellationToken workerCancellation)
    {
        using var activity = JobTelemetry.ActivitySource.StartActivity("jobs.handle");
        long started = Stopwatch.GetTimestamp();
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(workerCancellation);
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(workerCancellation);
        Task heartbeat = HeartbeatAsync(lease, execution, heartbeatStop.Token);
        bool succeeded = false;
        bool retryable = true;
        string? failureCode = null;
        try
        {
            await _handlers[lease.JobType](lease, execution.Token).ConfigureAwait(false);
            succeeded = true;
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested)
        {
            // Lease revocation or shutdown leaves ownership of recovery with the database.
        }
        catch (JobHandlerException exception)
        {
            failureCode = exception.FailureCode;
            retryable = exception.Retryable;
        }
        catch (Exception)
        {
            failureCode = "handler_failed";
        }
        finally
        {
            await heartbeatStop.CancelAsync().ConfigureAwait(false);
            await heartbeat.ConfigureAwait(false);
            JobTelemetry.HandlerDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
        }

        if (execution.IsCancellationRequested)
        {
            return;
        }

        try
        {
            if (succeeded)
            {
                _ = await _store.CompleteAsync(lease, workerCancellation).ConfigureAwait(false);
            }
            else if (failureCode is not null)
            {
                _ = await _store.FailAsync(lease, failureCode, _options.RetryPolicy.GetDelay(lease.Attempt), retryable, workerCancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (workerCancellation.IsCancellationRequested)
        {
            // Shutdown may race the acknowledgement; recover after lease expiry.
        }
        catch (Exception)
        {
            JobTelemetry.StoreFailures.Add(1);
        }
    }

    private async Task HeartbeatAsync(JobLease lease, CancellationTokenSource execution, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await _store.HeartbeatAsync(lease, _options.LeaseDuration, cancellationToken).ConfigureAwait(false))
                {
                    await execution.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The handler finished or its worker stopped.
        }
        catch (Exception)
        {
            JobTelemetry.StoreFailures.Add(1);
            await execution.CancelAsync().ConfigureAwait(false);
        }
    }
}
