using System.Collections.Frozen;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using BlueTusk.Data;
using BlueTusk.Jobs;

namespace BlueTusk.Workflows;

public delegate ValueTask<ReadOnlyMemory<byte>> TransactionalWorkflowActivity(
    BlueTuskConnection connection, BlueTuskTransaction transaction,
    WorkflowActivityContext context, CancellationToken cancellationToken);

internal sealed record WorkflowActivityRegistration(
    Func<WorkflowActivityContext, CancellationToken, ValueTask<ReadOnlyMemory<byte>>>? Handler,
    TransactionalWorkflowActivity? TransactionalHandler);

public sealed class WorkflowActivityRegistry
{
    private readonly Dictionary<string, WorkflowActivityRegistration> _activities = new(StringComparer.Ordinal);

    public WorkflowActivityRegistry Register(string activity,
        Func<WorkflowActivityContext, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> handler)
    {
        WorkflowOptions.Name(activity, nameof(activity));
        ArgumentNullException.ThrowIfNull(handler);
        WorkflowOptions.Range(_activities.Count + 1, 1, 127, nameof(activity));
        _activities.Add(activity, new(handler, null));
        return this;
    }

    /// <summary>Runs database effects, durable activity result and downstream dispatch in one fenced transaction.</summary>
    public WorkflowActivityRegistry RegisterTransactional(string activity, TransactionalWorkflowActivity handler)
    {
        WorkflowOptions.Name(activity, nameof(activity));
        ArgumentNullException.ThrowIfNull(handler);
        WorkflowOptions.Range(_activities.Count + 1, 1, 127, nameof(activity));
        _activities.Add(activity, new(null, handler));
        return this;
    }

    public WorkflowActivityRegistry Register<TInput, TResult>(string activity,
        JsonTypeInfo<TInput> inputType, JsonTypeInfo<TResult> resultType,
        Func<TInput, WorkflowActivityContext, CancellationToken, ValueTask<TResult>> handler)
    {
        ArgumentNullException.ThrowIfNull(inputType);
        ArgumentNullException.ThrowIfNull(resultType);
        ArgumentNullException.ThrowIfNull(handler);
        return Register(activity, async (context, cancellationToken) =>
        {
            TInput? input;
            try
            {
                input = JsonSerializer.Deserialize(context.InitialInput.Span, inputType);
            }
            catch (JsonException)
            {
                throw new JobHandlerException("workflow_invalid_input", retryable: false);
            }

            if (input is null)
            {
                throw new JobHandlerException("workflow_invalid_input", retryable: false);
            }

            TResult result = await handler(input, context, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.SerializeToUtf8Bytes(result, resultType);
        });
    }

    internal FrozenDictionary<string, WorkflowActivityRegistration> Snapshot() =>
        _activities.ToFrozenDictionary(StringComparer.Ordinal);
}

public sealed record WorkflowWorkerOptions
{
    public JobWorkerOptions Jobs { get; init; } = new() { DispatchRecurringSchedules = false };
    public TimeSpan RecoveryInterval { get; init; } = TimeSpan.FromSeconds(1);
    public int RecoveryBatchSize { get; init; } = 128;
}

public static class WorkflowTelemetry
{
    public const string InstrumentationName = "BlueTusk.Workflows";
    public static ActivitySource ActivitySource { get; } = new(InstrumentationName);
    public static Meter Meter { get; } = new(InstrumentationName);
    internal static Counter<long> RecoveryFailures { get; } = Meter.CreateCounter<long>("bluetusk.workflows.recovery_failures");
    internal static Counter<long> Recovered { get; } = Meter.CreateCounter<long>("bluetusk.workflows.recovered_dispatches");
    internal static Histogram<double> ActivityDuration { get; } = Meter.CreateHistogram<double>("bluetusk.workflows.activity.duration", "s");
}

/// <summary>Runs a bounded Jobs worker plus a cursor-based durable recovery sweep.</summary>
public sealed class WorkflowWorker
{
    private readonly PostgreSqlWorkflowStore _store;
    private readonly JobScope _scope;
    private readonly WorkflowWorkerOptions _options;
    private readonly FrozenDictionary<string, WorkflowActivityRegistration> _activities;
    private readonly JobWorker _worker;

    public WorkflowWorker(PostgreSqlWorkflowStore store, JobScope scope, string owner,
        WorkflowActivityRegistry activities, WorkflowWorkerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(activities);
        _options = options ?? new WorkflowWorkerOptions();
        ArgumentNullException.ThrowIfNull(_options.Jobs);
        WorkflowOptions.Range(_options.RecoveryBatchSize, 1, store.MaximumBatchSize, nameof(options));
        if (_options.RecoveryInterval < TimeSpan.FromMilliseconds(1) || _options.RecoveryInterval > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        _store = store;
        _scope = scope;
        _activities = activities.Snapshot();
        var handlers = new JobHandlerRegistry().Register(PostgreSqlWorkflowStore.TimerJobType,
            WorkflowJsonContext.Default.WorkflowDispatch, ExecuteAsync);
        foreach (string activityName in _activities.Keys)
        {
            handlers.Register(PostgreSqlWorkflowStore.ActivityJobType(activityName), WorkflowJsonContext.Default.WorkflowDispatch, ExecuteAsync);
        }
        _worker = new JobWorker(store.Jobs, scope, owner, handlers, _options.Jobs);
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task jobs = _worker.RunAsync(lifetime.Token);
        Task recovery = RecoverAsync(lifetime.Token);
        try
        {
            _ = await Task.WhenAny(jobs, recovery).ConfigureAwait(false);
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(jobs, recovery).ConfigureAwait(false);
        }
    }

    private async ValueTask ExecuteAsync(WorkflowDispatch dispatch, JobExecutionContext job, CancellationToken cancellationToken)
    {
        var lease = job.Lease ?? throw new JobHandlerException("lease_required", retryable: false);
        using var activity = WorkflowTelemetry.ActivitySource.StartActivity(dispatch.Compensation ? "workflow.compensate" : "workflow.activity");
        long started = Stopwatch.GetTimestamp();
        try
        {
            var context = await _store.PrepareAsync(lease, dispatch, cancellationToken).ConfigureAwait(false);
            if (context is null)
            {
                return;
            }

            if (!_activities.TryGetValue(context.Activity, out var handler))
            {
                throw new JobHandlerException("workflow_handler_missing", retryable: false);
            }

            if (handler.TransactionalHandler is { } transactional)
            {
                await _store.ExecuteTransactionalActivityAsync(lease, dispatch, context, transactional, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var result = await handler.Handler!(context, cancellationToken).ConfigureAwait(false);
                await _store.FinishAsync(lease, dispatch, result, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JobHandlerException exception)
        {
            await _store.RecordFailureAsync(lease, dispatch, exception.FailureCode, exception.Retryable, cancellationToken).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            await _store.RecordFailureAsync(lease, dispatch, "workflow_activity_failed", retryable: true, cancellationToken).ConfigureAwait(false);
            throw new JobHandlerException("workflow_activity_failed");
        }
        finally
        {
            WorkflowTelemetry.ActivityDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        WorkflowRecoveryCursor? cursor = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var result = await _store.ReconcileAsync(_scope, _options.RecoveryBatchSize, cursor, cancellationToken).ConfigureAwait(false);
                    cursor = result.NextCursor;
                    WorkflowTelemetry.Recovered.Add(result.Recovered);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    WorkflowTelemetry.RecoveryFailures.Add(1);
                }

                await Task.Delay(_options.RecoveryInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Structured cooperative shutdown.
        }
    }
}
