using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using BlueTusk.Live;
using BlueTusk.Streams;
using BlueTusk.TypeSystem;

namespace BlueTusk.ContinuousGraph;

public enum ContinuousGraphIncrementalDisposition
{
    Unrelated,
    Exact,
    RequiresRepair,
}

public enum ContinuousGraphEvaluationMode
{
    Initial,
    Unrelated,
    Incremental,
    AuthoritativeRepair,
    Duplicate,
}

/// <summary>Identifies the correctness tier used to maintain a graph result.</summary>
public enum ContinuousGraphMaintenanceTier
{
    None,
    TrustedCdcDelta,
    AuthoritativeDelta,
    AuthoritativeRepair,
}

/// <summary>Evidence required before direct CDC projection is enabled.</summary>
public sealed record ContinuousGraphCdcTrustContract(
    string SchemaFingerprint,
    bool HasCompleteOldAndNewValues,
    bool HasExactChangedColumns,
    bool HasSufficientReplicaIdentity,
    bool EnforcesSecurityScope)
{
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(SchemaFingerprint) &&
        HasCompleteOldAndNewValues &&
        HasExactChangedColumns &&
        HasSufficientReplicaIdentity &&
        EnforcesSecurityScope;
}

/// <summary>
/// Authoritative affected-key rows produced by a registered incremental evaluator.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1000:Do not declare static members on generic types",
    Justification = "The named constructors preserve valid disposition-specific invariants.")]
public sealed class ContinuousGraphIncrementalResult<TResult, TKey>
    where TResult : class
    where TKey : notnull
{
    private readonly ReadOnlyCollection<TKey> _affectedKeys;
    private readonly ReadOnlyCollection<TResult> _rows;

    private ContinuousGraphIncrementalResult(
        ContinuousGraphIncrementalDisposition disposition,
        IEnumerable<TKey> affectedKeys,
        IEnumerable<TResult> rows,
        string? detail,
        ContinuousGraphMaintenanceTier maintenanceTier =
            ContinuousGraphMaintenanceTier.AuthoritativeDelta)
    {
        Disposition = disposition;
        _affectedKeys = Array.AsReadOnly(affectedKeys.ToArray());
        _rows = Array.AsReadOnly(rows.ToArray());
        if (_affectedKeys.Any(static key => key is null))
        {
            throw new ArgumentException(
                "An affected graph key cannot be null.",
                nameof(affectedKeys));
        }

        if (_rows.Any(static row => row is null))
        {
            throw new ArgumentException(
                "An incremental graph row cannot be null.",
                nameof(rows));
        }

        Detail = detail;
        MaintenanceTier = maintenanceTier;
    }

    public ContinuousGraphIncrementalDisposition Disposition { get; }

    public IReadOnlyList<TKey> AffectedKeys => _affectedKeys;

    public IReadOnlyList<TResult> Rows => _rows;

    public string? Detail { get; }

    public ContinuousGraphMaintenanceTier MaintenanceTier { get; }

    public static ContinuousGraphIncrementalResult<TResult, TKey> Unrelated(
        string? detail = null) =>
        new(
            ContinuousGraphIncrementalDisposition.Unrelated,
            [],
            [],
            detail,
            ContinuousGraphMaintenanceTier.None);

    public static ContinuousGraphIncrementalResult<TResult, TKey> Exact(
        IEnumerable<TKey> affectedKeys,
        IEnumerable<TResult> rows,
        string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(affectedKeys);
        ArgumentNullException.ThrowIfNull(rows);
        return new(
            ContinuousGraphIncrementalDisposition.Exact,
            affectedKeys,
            rows,
            detail);
    }

    public static ContinuousGraphIncrementalResult<TResult, TKey> RequiresRepair(
        string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        return new(
            ContinuousGraphIncrementalDisposition.RequiresRepair,
            [],
            [],
            detail,
            ContinuousGraphMaintenanceTier.None);
    }

    internal ContinuousGraphIncrementalResult<TResult, TKey> WithMaintenanceTier(
        ContinuousGraphMaintenanceTier maintenanceTier) =>
        new(Disposition, _affectedKeys, _rows, Detail, maintenanceTier);
}

/// <summary>
/// Resolves an affected-key set and executes the authorised key-scoped graph query.
/// </summary>
public interface IContinuousGraphIncrementalEvaluator<TResult, TKey>
    where TResult : class
    where TKey : notnull
{
    ValueTask<ContinuousGraphIncrementalResult<TResult, TKey>> EvaluateAsync(
        ChangeTransaction transaction,
        LiveQueryExecutionContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Explicit opt-in projector for mathematically exact in-memory CDC maintenance.
/// Implementations must request repair whenever they cannot prove their result.
/// </summary>
public interface IContinuousGraphCdcProjector<TResult, TKey>
    where TResult : class
    where TKey : notnull
{
    ContinuousGraphCdcTrustContract TrustContract { get; }

    ValueTask<ContinuousGraphIncrementalResult<TResult, TKey>> ProjectAsync(
        ChangeTransaction transaction,
        LiveQueryExecutionContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Resource, ordering, and repair policy for one incremental graph session.</summary>
public sealed class ContinuousGraphIncrementalOptions<TResult, TKey>
    where TResult : class
    where TKey : notnull
{
    public required IComparer<TResult> ResultOrdering { get; init; }

    public required IComparer<TKey> KeyOrdering { get; init; }

    public int MaximumAffectedKeys { get; init; } = 1_024;

    public int RepairAfterTransactions { get; init; } = 1_000;

    public TimeSpan MaximumRepairInterval { get; init; } = TimeSpan.FromMinutes(5);

    public LiveDiffOptions DiffOptions { get; init; } = new();

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(ResultOrdering);
        ArgumentNullException.ThrowIfNull(KeyOrdering);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumAffectedKeys);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(RepairAfterTransactions);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            MaximumRepairInterval,
            TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(DiffOptions);
        ArgumentNullException.ThrowIfNull(TimeProvider);
    }
}

public sealed class ContinuousGraphEvaluation<TResult, TKey>
    where TResult : class
    where TKey : notnull
{
    internal ContinuousGraphEvaluation(
        ContinuousGraphEvaluationMode mode,
        ContinuousGraphMaintenanceTier maintenanceTier,
        LiveDiffBatch<TResult, TKey>? batch,
        BlueTuskLogSequenceNumber? sourcePosition,
        string? detail,
        int affectedKeyCount,
        int queryCount)
    {
        Mode = mode;
        MaintenanceTier = maintenanceTier;
        Batch = batch;
        SourcePosition = sourcePosition;
        Detail = detail;
        AffectedKeyCount = affectedKeyCount;
        QueryCount = queryCount;
    }

    public ContinuousGraphEvaluationMode Mode { get; }

    public ContinuousGraphMaintenanceTier MaintenanceTier { get; }

    public LiveDiffBatch<TResult, TKey>? Batch { get; }

    public BlueTuskLogSequenceNumber? SourcePosition { get; }

    public string? Detail { get; }

    public int AffectedKeyCount { get; }

    public int QueryCount { get; }
}

/// <summary>
/// A prepared graph state transition that must be committed only after its events are durable.
/// </summary>
public sealed class ContinuousGraphEvaluationDelivery<TResult, TKey> : IAsyncDisposable
    where TResult : class
    where TKey : notnull
{
    private readonly Func<bool, ValueTask> _settle;
    private readonly Activity? _telemetryActivity;
    private readonly long _telemetryStarted;
    private int _settled;

    internal ContinuousGraphEvaluationDelivery(
        ContinuousGraphEvaluation<TResult, TKey> evaluation,
        Func<bool, ValueTask> settle)
    {
        Evaluation = evaluation;
        _settle = settle;
        (_telemetryStarted, _telemetryActivity) =
            ContinuousGraphDiagnostics.StartEvaluation(
                evaluation.Mode,
                evaluation.MaintenanceTier);
    }

    public ContinuousGraphEvaluation<TResult, TKey> Evaluation { get; }

    public async ValueTask CommitAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _settled, 1) != 0)
        {
            throw new InvalidOperationException(
                "A continuous graph evaluation can be settled only once.");
        }

        try
        {
            await _settle(true).ConfigureAwait(false);
            RecordTelemetry("committed");
        }
        catch
        {
            RecordTelemetry("commit_failed");
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _settled, 1) == 0)
        {
            try
            {
                await _settle(false).ConfigureAwait(false);
                RecordTelemetry("abandoned");
            }
            catch
            {
                RecordTelemetry("abandon_failed");
                throw;
            }
        }
    }

    private void RecordTelemetry(string outcome) =>
        ContinuousGraphDiagnostics.RecordEvaluation(
            Evaluation.Mode,
            Evaluation.MaintenanceTier,
            outcome,
            Evaluation.Batch?.Events.Count ?? 0,
            Evaluation.AffectedKeyCount,
            Evaluation.QueryCount,
            Evaluation.Detail,
            _telemetryStarted,
            _telemetryActivity);
}

public sealed class ContinuousGraphIncrementalStatus
{
    internal ContinuousGraphIncrementalStatus(
        bool isInitialized,
        BlueTuskLogSequenceNumber? sourcePosition,
        long nextSequence,
        long incrementalTransactions,
        long trustedCdcTransactions,
        long authoritativeDeltaTransactions,
        long authoritativeRepairs,
        long unrelatedTransactions,
        long duplicateTransactions,
        long fallbackRepairs,
        long affectedKeys,
        long queryCount,
        int transactionsSinceRepair,
        DateTimeOffset? lastRepairAt,
        string? lastFallbackReason)
    {
        IsInitialized = isInitialized;
        SourcePosition = sourcePosition;
        NextSequence = nextSequence;
        IncrementalTransactions = incrementalTransactions;
        TrustedCdcTransactions = trustedCdcTransactions;
        AuthoritativeDeltaTransactions = authoritativeDeltaTransactions;
        AuthoritativeRepairs = authoritativeRepairs;
        UnrelatedTransactions = unrelatedTransactions;
        DuplicateTransactions = duplicateTransactions;
        FallbackRepairs = fallbackRepairs;
        AffectedKeys = affectedKeys;
        QueryCount = queryCount;
        TransactionsSinceRepair = transactionsSinceRepair;
        LastRepairAt = lastRepairAt;
        LastFallbackReason = lastFallbackReason;
    }

    public bool IsInitialized { get; }

    public BlueTuskLogSequenceNumber? SourcePosition { get; }

    public long NextSequence { get; }

    public long IncrementalTransactions { get; }

    public long TrustedCdcTransactions { get; }

    public long AuthoritativeDeltaTransactions { get; }

    public long AuthoritativeRepairs { get; }

    public long UnrelatedTransactions { get; }

    public long DuplicateTransactions { get; }

    public long FallbackRepairs { get; }

    public long AffectedKeys { get; }

    public long QueryCount { get; }

    public int TransactionsSinceRepair { get; }

    public DateTimeOffset? LastRepairAt { get; }

    public string? LastFallbackReason { get; }
}

/// <summary>
/// Maintains a bounded graph result using authorised affected-key queries and fail-closed repair.
/// </summary>
public sealed class ContinuousGraphIncrementalSession<TResult, TKey> :
    IAsyncDisposable
    where TResult : class
    where TKey : notnull
{
    private readonly ContinuousGraphQueryPlan<TResult, TKey> _plan;
    private readonly IContinuousGraphIncrementalEvaluator<TResult, TKey> _evaluator;
    private readonly ContinuousGraphIncrementalOptions<TResult, TKey> _options;
    private readonly LiveQueryExecutionContext _execution;
    private readonly int _resultLimit;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _statusGate = new();
    private LiveResultSnapshot<TResult, TKey>? _snapshot;
    private BlueTuskLogSequenceNumber? _lastPosition;
    private uint? _lastTransactionId;
    private long _nextSequence = 1;
    private long _incrementalTransactions;
    private long _trustedCdcTransactions;
    private long _authoritativeDeltaTransactions;
    private long _authoritativeRepairs;
    private long _unrelatedTransactions;
    private long _duplicateTransactions;
    private long _fallbackRepairs;
    private long _affectedKeys;
    private long _queryCount;
    private int _transactionsSinceRepair;
    private DateTimeOffset? _lastRepairAt;
    private string? _lastFallbackReason;
    private int _disposed;

    internal ContinuousGraphIncrementalSession(
        ContinuousGraphQueryPlan<TResult, TKey> plan,
        LiveQueryArguments arguments,
        LiveSecurityScope securityScope,
        IContinuousGraphIncrementalEvaluator<TResult, TKey> evaluator,
        ContinuousGraphIncrementalOptions<TResult, TKey> options,
        int? resultLimit)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(securityScope);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _resultLimit = resultLimit ?? plan.LivePlan.MaximumResultCount;
        if (_resultLimit <= 0 ||
            _resultLimit > plan.LivePlan.MaximumResultCount)
        {
            throw new ArgumentOutOfRangeException(nameof(resultLimit));
        }

        _plan = plan;
        _evaluator = evaluator;
        _options = options;
        _execution = new LiveQueryExecutionContext(arguments, securityScope);
        Identity = LiveSubscriptionIdentity.Create(
            plan.LivePlan,
            arguments,
            securityScope,
            _resultLimit);
    }

    public LiveSubscriptionIdentity Identity { get; }

    public ContinuousGraphIncrementalStatus Status
    {
        get
        {
            lock (_statusGate)
            {
                return new ContinuousGraphIncrementalStatus(
                    _snapshot is not null,
                    _lastPosition,
                    _nextSequence,
                    _incrementalTransactions,
                    _trustedCdcTransactions,
                    _authoritativeDeltaTransactions,
                    _authoritativeRepairs,
                    _unrelatedTransactions,
                    _duplicateTransactions,
                    _fallbackRepairs,
                    _affectedKeys,
                    _queryCount,
                    _transactionsSinceRepair,
                    _lastRepairAt,
                    _lastFallbackReason);
            }
        }
    }

    public async ValueTask<ContinuousGraphEvaluationDelivery<TResult, TKey>>
        PrepareInitialAsync(
            long nextSequence = 1,
            CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nextSequence);
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_snapshot is not null)
            {
                throw new InvalidOperationException(
                    "A continuous graph incremental session is already initialized.");
            }

            var rows = await ExecuteAuthoritativeAsync(cancellationToken)
                .ConfigureAwait(false);
            var batch = LiveResultDiffer.Initial(
                rows,
                _plan.LivePlan.KeySelector,
                _plan.LivePlan.KeyComparer,
                nextSequence);
            var proposal = new Proposal(
                batch.Snapshot,
                null,
                null,
                checked(batch.LastSequence + 1),
                ContinuousGraphEvaluationMode.Initial,
                ContinuousGraphMaintenanceTier.AuthoritativeRepair,
                batch,
                "authoritative-initial",
                IsRepair: true,
                IsFallback: false,
                AffectedKeyCount: 0,
                QueryCount: 1);
            return CreateDelivery(proposal);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    public async ValueTask<ContinuousGraphEvaluationDelivery<TResult, TKey>>
        PrepareTransactionAsync(
            ChangeTransaction transaction,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            EnsureInitialized();
            if (_lastPosition is { } lastPosition)
            {
                if (transaction.CommitEndPosition == lastPosition &&
                    transaction.TransactionId == _lastTransactionId)
                {
                    return CreateDelivery(new Proposal(
                        _snapshot!,
                        _lastPosition,
                        _lastTransactionId,
                        _nextSequence,
                        ContinuousGraphEvaluationMode.Duplicate,
                        ContinuousGraphMaintenanceTier.None,
                        null,
                        "at-least-once-redelivery",
                        IsRepair: false,
                        IsFallback: false,
                        AffectedKeyCount: 0,
                        QueryCount: 0));
                }

                if (transaction.CommitEndPosition <= lastPosition)
                {
                    throw new ContinuousGraphIncrementalException(
                        $"Source transaction {transaction.TransactionId} at {transaction.CommitEndPosition} does not advance session position {lastPosition}.");
                }
            }

            if (RequiresScheduledRepair())
            {
                return await PrepareRepairCoreAsync(
                    transaction.CommitEndPosition,
                    transaction.TransactionId,
                    "scheduled-authoritative-repair",
                    isFallback: false,
                    cancellationToken).ConfigureAwait(false);
            }

            if (transaction.Outcome is ChangeTransactionOutcome.RolledBack or
                ChangeTransactionOutcome.Prepared)
            {
                return CreateDelivery(new Proposal(
                    _snapshot!,
                    transaction.CommitEndPosition,
                    transaction.TransactionId,
                    _nextSequence,
                    ContinuousGraphEvaluationMode.Unrelated,
                    ContinuousGraphMaintenanceTier.None,
                    null,
                    $"two-phase-{transaction.Outcome.ToString().ToLowerInvariant()}",
                    IsRepair: false,
                    IsFallback: false,
                    AffectedKeyCount: 0,
                    QueryCount: 0));
            }

            if (transaction.IsTwoPhase)
            {
                return await PrepareRepairCoreAsync(
                    transaction.CommitEndPosition,
                    transaction.TransactionId,
                    "two-phase-commit-repair",
                    isFallback: true,
                    cancellationToken).ConfigureAwait(false);
            }

            var result = await _evaluator.EvaluateAsync(
                transaction,
                _execution,
                cancellationToken).ConfigureAwait(false) ??
                throw new ContinuousGraphIncrementalException(
                    "The incremental evaluator returned null.");
            return result.Disposition switch
            {
                ContinuousGraphIncrementalDisposition.Unrelated =>
                    CreateDelivery(new Proposal(
                        _snapshot!,
                        transaction.CommitEndPosition,
                        transaction.TransactionId,
                        _nextSequence,
                        ContinuousGraphEvaluationMode.Unrelated,
                        ContinuousGraphMaintenanceTier.None,
                        null,
                        result.Detail,
                        IsRepair: false,
                        IsFallback: false,
                        AffectedKeyCount: 0,
                        QueryCount: 0)),
                ContinuousGraphIncrementalDisposition.RequiresRepair =>
                    await PrepareRepairCoreAsync(
                        transaction.CommitEndPosition,
                        transaction.TransactionId,
                        result.Detail ?? "evaluator-requested-repair",
                        isFallback: true,
                        cancellationToken).ConfigureAwait(false),
                ContinuousGraphIncrementalDisposition.Exact =>
                    await PrepareExactCoreAsync(
                        transaction,
                        result,
                        cancellationToken).ConfigureAwait(false),
                _ => throw new ContinuousGraphIncrementalException(
                    $"Unknown incremental disposition '{result.Disposition}'."),
            };
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    public async ValueTask<ContinuousGraphEvaluationDelivery<TResult, TKey>>
        PrepareRepairAsync(
            string detail,
            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            EnsureInitialized();
            return await PrepareRepairCoreAsync(
                _lastPosition,
                _lastTransactionId,
                detail,
                isFallback: false,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        _gate.Release();
        _gate.Dispose();
    }

    private async ValueTask<ContinuousGraphEvaluationDelivery<TResult, TKey>>
        PrepareExactCoreAsync(
            ChangeTransaction transaction,
            ContinuousGraphIncrementalResult<TResult, TKey> result,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var affected = result.AffectedKeys
            .Distinct(_plan.LivePlan.KeyComparer)
            .ToArray();
        if (affected.Length == 0)
        {
            throw new ContinuousGraphIncrementalException(
                "An exact incremental result must contain at least one affected key.");
        }

        if (affected.Length > _options.MaximumAffectedKeys)
        {
            return await PrepareRepairCoreAsync(
                transaction.CommitEndPosition,
                transaction.TransactionId,
                $"affected-key-limit:{affected.Length}",
                isFallback: true,
                cancellationToken).ConfigureAwait(false);
        }

        var affectedSet = affected.ToHashSet(_plan.LivePlan.KeyComparer);
        var visibleAffected = new Dictionary<TKey, TResult>(
            Math.Min(affected.Length, _snapshot!.Rows.Count),
            _plan.LivePlan.KeyComparer);
        for (var index = 0; index < _snapshot.Rows.Count; index++)
        {
            var key = _snapshot.Keys[index];
            if (affectedSet.Contains(key))
            {
                visibleAffected.Add(key, _snapshot.Rows[index]);
            }
        }

        var authoritative = new Dictionary<TKey, TResult>(
            _plan.LivePlan.KeyComparer);
        foreach (var row in result.Rows)
        {
            ArgumentNullException.ThrowIfNull(row);
            var key = _plan.LivePlan.KeySelector(row);
            if (!affectedSet.Contains(key))
            {
                throw new ContinuousGraphIncrementalException(
                    $"The incremental evaluator returned row key '{key}' outside its affected-key set.");
            }

            if (!authoritative.TryAdd(key, row))
            {
                throw new ContinuousGraphIncrementalException(
                    $"The incremental evaluator returned duplicate row key '{key}'.");
            }
        }

        foreach (var key in affected)
        {
            var wasVisible = visibleAffected.TryGetValue(key, out var previous);
            var isVisible = authoritative.TryGetValue(key, out var current);
            if (wasVisible && !isVisible)
            {
                return await PrepareRepairCoreAsync(
                    transaction.CommitEndPosition,
                    transaction.TransactionId,
                    "visible-row-removed-or-left-predicate",
                    isFallback: true,
                    cancellationToken).ConfigureAwait(false);
            }

            if (wasVisible &&
                isVisible &&
                _options.ResultOrdering.Compare(current!, previous!) > 0)
            {
                return await PrepareRepairCoreAsync(
                    transaction.CommitEndPosition,
                    transaction.TransactionId,
                    "visible-row-rank-worsened",
                    isFallback: true,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        var comparer = new ResultComparer(
            _plan.LivePlan.KeySelector,
            _options.ResultOrdering,
            _options.KeyOrdering);
        var candidates = authoritative.Values.ToArray();
        Array.Sort(candidates, comparer);
        var unaffected = new List<TResult>(_snapshot.Rows.Count);
        for (var index = 0; index < _snapshot.Rows.Count; index++)
        {
            if (!affectedSet.Contains(_snapshot.Keys[index]))
            {
                unaffected.Add(_snapshot.Rows[index]);
            }
        }

        var ordered = new TResult[Math.Min(
            _resultLimit,
            unaffected.Count + candidates.Length)];
        for (int target = 0, left = 0, right = 0;
             target < ordered.Length;
             target++)
        {
            if (right >= candidates.Length ||
                (left < unaffected.Count && comparer.Compare(
                    unaffected[left],
                    candidates[right]) <= 0))
            {
                ordered[target] = unaffected[left++];
            }
            else
            {
                ordered[target] = candidates[right++];
            }
        }
        var membershipAndOrderUnchanged = ordered.Length == _snapshot.Rows.Count;
        if (membershipAndOrderUnchanged)
        {
            for (var index = 0; index < ordered.Length; index++)
            {
                if (!_plan.LivePlan.KeyComparer.Equals(
                    _snapshot.Keys[index],
                    _plan.LivePlan.KeySelector(ordered[index])))
                {
                    membershipAndOrderUnchanged = false;
                    break;
                }
            }
        }

        LiveDiffBatch<TResult, TKey> batch;
        if (membershipAndOrderUnchanged)
        {
            var affectedRows = ordered
                .Where(row => affectedSet.Contains(_plan.LivePlan.KeySelector(row)))
                .ToArray();
            batch = LiveResultDiffer.DiffAffected(
                _snapshot,
                affectedRows,
                _plan.LivePlan.KeySelector,
                _plan.LivePlan.RowComparer,
                _options.DiffOptions,
                _nextSequence);
        }
        else
        {
            batch = LiveResultDiffer.Diff(
                _snapshot,
                ordered,
                _plan.LivePlan.KeySelector,
                _plan.LivePlan.RowComparer,
                _plan.LivePlan.KeyComparer,
                _options.DiffOptions,
                _nextSequence);
        }
        var nextSequence = batch.Events.Count == 0
            ? _nextSequence
            : checked(batch.LastSequence + 1);
        return CreateDelivery(new Proposal(
            batch.Snapshot,
            transaction.CommitEndPosition,
            transaction.TransactionId,
            nextSequence,
            ContinuousGraphEvaluationMode.Incremental,
            result.MaintenanceTier,
            batch.Events.Count == 0 ? null : batch,
            result.Detail,
            IsRepair: false,
            IsFallback: false,
            AffectedKeyCount: affected.Length,
            QueryCount: result.MaintenanceTier is
                ContinuousGraphMaintenanceTier.AuthoritativeDelta ? 1 : 0));
    }

    private async ValueTask<ContinuousGraphEvaluationDelivery<TResult, TKey>>
        PrepareRepairCoreAsync(
            BlueTuskLogSequenceNumber? sourcePosition,
            uint? transactionId,
            string detail,
            bool isFallback,
            CancellationToken cancellationToken)
    {
        var rows = await ExecuteAuthoritativeAsync(cancellationToken)
            .ConfigureAwait(false);
        var batch = LiveResultDiffer.Diff(
            _snapshot!,
            rows,
            _plan.LivePlan.KeySelector,
            _plan.LivePlan.RowComparer,
            _plan.LivePlan.KeyComparer,
            _options.DiffOptions,
            _nextSequence);
        var nextSequence = batch.Events.Count == 0
            ? _nextSequence
            : checked(batch.LastSequence + 1);
        return CreateDelivery(new Proposal(
            batch.Snapshot,
            sourcePosition,
            transactionId,
            nextSequence,
            ContinuousGraphEvaluationMode.AuthoritativeRepair,
            ContinuousGraphMaintenanceTier.AuthoritativeRepair,
            batch.Events.Count == 0 ? null : batch,
            detail,
            IsRepair: true,
            isFallback,
            AffectedKeyCount: 0,
            QueryCount: 1));
    }

    private async ValueTask<IReadOnlyList<TResult>> ExecuteAuthoritativeAsync(
        CancellationToken cancellationToken)
    {
        var rows = await _plan.LivePlan.ExecuteAsync(
            _execution,
            cancellationToken).ConfigureAwait(false) ??
            throw new ContinuousGraphIncrementalException(
                "The authoritative graph query returned null.");
        if (rows.Count > _resultLimit)
        {
            throw new ContinuousGraphIncrementalException(
                $"The authoritative graph query returned {rows.Count} rows, exceeding its session limit {_resultLimit}.");
        }

        return rows;
    }

    private ContinuousGraphEvaluationDelivery<TResult, TKey> CreateDelivery(
        Proposal proposal)
    {
        var evaluation = new ContinuousGraphEvaluation<TResult, TKey>(
            proposal.Mode,
            proposal.MaintenanceTier,
            proposal.Batch,
            proposal.SourcePosition,
            proposal.Detail,
            proposal.AffectedKeyCount,
            proposal.QueryCount);
        return new ContinuousGraphEvaluationDelivery<TResult, TKey>(
            evaluation,
            commit =>
            {
                if (commit)
                {
                    lock (_statusGate)
                    {
                        _snapshot = proposal.Snapshot;
                        _lastPosition = proposal.SourcePosition;
                        _lastTransactionId = proposal.TransactionId;
                        _nextSequence = proposal.NextSequence;
                        if (proposal.Mode is
                            ContinuousGraphEvaluationMode.Incremental)
                        {
                            _incrementalTransactions++;
                            if (proposal.MaintenanceTier is
                                ContinuousGraphMaintenanceTier.TrustedCdcDelta)
                            {
                                _trustedCdcTransactions++;
                            }
                            else if (proposal.MaintenanceTier is
                                ContinuousGraphMaintenanceTier.AuthoritativeDelta)
                            {
                                _authoritativeDeltaTransactions++;
                            }

                            _affectedKeys += proposal.AffectedKeyCount;
                            _queryCount += proposal.QueryCount;
                            _transactionsSinceRepair++;
                        }
                        else if (proposal.Mode is
                            ContinuousGraphEvaluationMode.Unrelated)
                        {
                            _unrelatedTransactions++;
                            _transactionsSinceRepair++;
                        }
                        else if (proposal.Mode is
                            ContinuousGraphEvaluationMode.Duplicate)
                        {
                            _duplicateTransactions++;
                        }

                        if (proposal.IsRepair)
                        {
                            _queryCount += proposal.QueryCount;
                            if (proposal.Mode is
                                ContinuousGraphEvaluationMode.AuthoritativeRepair)
                            {
                                _authoritativeRepairs++;
                                if (proposal.IsFallback)
                                {
                                    _fallbackRepairs++;
                                    _lastFallbackReason = proposal.Detail;
                                }
                            }

                            _transactionsSinceRepair = 0;
                            _lastRepairAt = _options.TimeProvider.GetUtcNow();
                        }
                    }
                }

                _gate.Release();
                return ValueTask.CompletedTask;
            });
    }

    private bool RequiresScheduledRepair()
    {
        if (_transactionsSinceRepair >= _options.RepairAfterTransactions)
        {
            return true;
        }

        return _lastRepairAt is { } repairedAt &&
            _options.TimeProvider.GetUtcNow() - repairedAt >=
            _options.MaximumRepairInterval;
    }

    private void EnsureInitialized()
    {
        if (_snapshot is null)
        {
            throw new InvalidOperationException(
                "The continuous graph incremental session is not initialized.");
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

    private sealed record Proposal(
        LiveResultSnapshot<TResult, TKey> Snapshot,
        BlueTuskLogSequenceNumber? SourcePosition,
        uint? TransactionId,
        long NextSequence,
        ContinuousGraphEvaluationMode Mode,
        ContinuousGraphMaintenanceTier MaintenanceTier,
        LiveDiffBatch<TResult, TKey>? Batch,
        string? Detail,
        bool IsRepair,
        bool IsFallback,
        int AffectedKeyCount,
        int QueryCount);

    private sealed class ResultComparer(
        Func<TResult, TKey> keySelector,
        IComparer<TResult> resultOrdering,
        IComparer<TKey> keyOrdering) : IComparer<TResult>
    {
        public int Compare(TResult? x, TResult? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var result = resultOrdering.Compare(x, y);
            return result != 0
                ? result
                : keyOrdering.Compare(keySelector(x), keySelector(y));
        }
    }
}

/// <summary>
/// Persists graph events before acknowledging each Streams transaction delivery.
/// </summary>
public sealed class ContinuousGraphIncrementalConsumer<TResult, TKey> :
    IChangeStreamConsumer,
    IAsyncDisposable
    where TResult : class
    where TKey : notnull
{
    private readonly ContinuousGraphIncrementalSession<TResult, TKey> _session;
    private readonly ILiveReplayStore _replayStore;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private int _initialized;
    private int _snapshotActive;
    private int _disposed;

    public ContinuousGraphIncrementalConsumer(
        ContinuousGraphIncrementalSession<TResult, TKey> session,
        ILiveReplayStore replayStore)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(replayStore);
        _session = session;
        _replayStore = replayStore;
    }

    public LiveSubscriptionIdentity Identity => _session.Identity;

    public async ValueTask InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _initialized) != 0)
        {
            return;
        }

        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (Volatile.Read(ref _initialized) != 0)
            {
                return;
            }

            var replay = await _replayStore.ReadAsync(
                Identity,
                0,
                1,
                cancellationToken).ConfigureAwait(false);
            var nextSequence = checked(replay.LastSequence + 1);
            await using var evaluation =
                await _session.PrepareInitialAsync(
                    nextSequence,
                    cancellationToken).ConfigureAwait(false);
            await PersistAsync(
                evaluation.Evaluation.Batch,
                cancellationToken).ConfigureAwait(false);
            await evaluation.CommitAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _initialized, 1);
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public ValueTask ResetSnapshotAsync(
        SnapshotReset reset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reset);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        Volatile.Write(ref _snapshotActive, 1);
        return ValueTask.CompletedTask;
    }

    public ValueTask StartSnapshotAsync(
        SnapshotStart start,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        Volatile.Write(ref _snapshotActive, 1);
        return ValueTask.CompletedTask;
    }

    public ValueTask ConsumeSnapshotBatchAsync(
        ChangeSnapshotBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _snapshotActive) == 0)
        {
            throw new ContinuousGraphIncrementalException(
                "A graph snapshot batch arrived outside a snapshot lifecycle.");
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask CompleteSnapshotAsync(
        SnapshotComplete complete,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(complete);
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _snapshotActive, 0) == 0)
        {
            throw new ContinuousGraphIncrementalException(
                "A graph snapshot completed without an active snapshot lifecycle.");
        }

        if (Volatile.Read(ref _initialized) == 0)
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var repair = await _session.PrepareRepairAsync(
            "snapshot-complete-repair",
            cancellationToken).ConfigureAwait(false);
        await PersistAsync(
            repair.Evaluation.Batch,
            cancellationToken).ConfigureAwait(false);
        await repair.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ConsumeTransactionAsync(
        ChangeTransactionDelivery delivery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        try
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            await using var evaluation =
                await _session.PrepareTransactionAsync(
                    delivery.Transaction,
                    cancellationToken).ConfigureAwait(false);
            await PersistAsync(
                evaluation.Evaluation.Batch,
                cancellationToken).ConfigureAwait(false);
            await evaluation.CommitAsync(cancellationToken).ConfigureAwait(false);
            await delivery.AcknowledgeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (delivery.State is ChangeDeliveryState.Active)
            {
                try
                {
                    await delivery.NackAsync(exception, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception nackException)
                {
                    throw new AggregateException(exception, nackException);
                }
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _initializeGate.WaitAsync(CancellationToken.None)
            .ConfigureAwait(false);
        _initializeGate.Release();
        _initializeGate.Dispose();
    }

    private async ValueTask PersistAsync(
        LiveDiffBatch<TResult, TKey>? batch,
        CancellationToken cancellationToken)
    {
        if (batch is null || batch.Events.Count == 0)
        {
            return;
        }

        var events = batch.Events
            .Select(static graphEvent =>
                LiveReplayJsonSerializer.Serialize(graphEvent))
            .ToArray();
        var expected = checked(events[0].Sequence - 1);
        var result = await _replayStore.AppendAsync(
            new LiveReplayAppendRequest(Identity, expected, events),
            cancellationToken).ConfigureAwait(false);
        if (result.Status is LiveReplayAppendStatus.SequenceConflict ||
            result.CurrentLastSequence != events[^1].Sequence)
        {
            throw new ContinuousGraphIncrementalException(
                $"Graph replay append ended at {result.CurrentLastSequence}, expected {events[^1].Sequence}.");
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
}

public class ContinuousGraphIncrementalException : Exception
{
    public ContinuousGraphIncrementalException(string message)
        : base(message)
    {
    }

    public ContinuousGraphIncrementalException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }
}
