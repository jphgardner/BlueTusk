namespace BlueTusk.ControlPlane;

/// <summary>
/// Reconciles versioned desired state through one fenced provider mutation at a time.
/// </summary>
public sealed class ManagedDeploymentController
{
    private readonly IManagedDeploymentStore _store;
    private readonly IManagedDeploymentLeaseStore _leases;
    private readonly IManagedTenantQuotaSource _quotas;
    private readonly IManagedInfrastructureProviderResolver _providers;
    private readonly string _owner;
    private readonly TimeSpan _leaseDuration;
    private readonly TimeProvider _timeProvider;

    public ManagedDeploymentController(
        IManagedDeploymentStore store,
        IManagedDeploymentLeaseStore leases,
        IManagedTenantQuotaSource quotas,
        IManagedInfrastructureProviderResolver providers,
        string owner,
        TimeSpan? leaseDuration = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(leases);
        ArgumentNullException.ThrowIfNull(quotas);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (owner.Length > 512)
        {
            throw new ArgumentException("Lease owner cannot exceed 512 characters.", nameof(owner));
        }

        var effectiveDuration = leaseDuration ?? TimeSpan.FromMinutes(2);
        if (effectiveDuration < TimeSpan.FromSeconds(15) ||
            effectiveDuration > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseDuration),
                "Lease duration must be between 15 seconds and one hour.");
        }

        _store = store;
        _leases = leases;
        _quotas = quotas;
        _providers = providers;
        _owner = owner;
        _leaseDuration = effectiveDuration;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<ManagedReconcileResult> ReconcileAsync(
        string deploymentId,
        CancellationToken cancellationToken = default)
    {
        ValidateDeploymentId(deploymentId);
        var lease = await _leases.TryAcquireAsync(
            deploymentId,
            _owner,
            _leaseDuration,
            cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            throw new ManagedDeploymentLeaseException(
                $"Deployment '{deploymentId}' is being reconciled by another owner.");
        }

        await using var guard = new LeaseGuard(
            _leases,
            lease,
            _leaseDuration,
            _timeProvider,
            cancellationToken);
        try
        {
            var deployment = await _store.GetAsync(deploymentId, guard.Token)
                .ConfigureAwait(false);
            if (deployment is null)
            {
                throw new KeyNotFoundException($"Deployment '{deploymentId}' does not exist.");
            }

            ManagedDeploymentValidation.Validate(deployment.Spec);
            if (deployment.Spec.Paused)
            {
                var paused = await SaveStatusAsync(
                    deployment,
                    ManagedDeploymentState.Paused,
                    deployment.Status.DesiredFingerprint,
                    deployment.Status.AppliedPlanFingerprint,
                    deployment.Status.ProviderResourceId,
                    null,
                    lease.FencingToken,
                    guard).ConfigureAwait(false);
                return Result(paused, changed: paused.Status.State != deployment.Status.State);
            }

            var quota = await _quotas.GetQuotaAsync(deployment.Spec.TenantId, guard.Token)
                .ConfigureAwait(false);
            var usage = await _quotas.GetUsageAsync(
                deployment.Spec.TenantId,
                deployment.Spec.DeploymentId,
                deployment.Spec,
                guard.Token).ConfigureAwait(false);
            ManagedDeploymentValidation.EnforceQuota(deployment.Spec, quota, usage);

            var desiredFingerprint = ManagedDeploymentValidation.GetFingerprint(deployment.Spec);
            var planning = await SaveStatusAsync(
                deployment,
                ManagedDeploymentState.Planning,
                desiredFingerprint,
                deployment.Status.AppliedPlanFingerprint,
                deployment.Status.ProviderResourceId,
                null,
                lease.FencingToken,
                guard).ConfigureAwait(false);

            var provider = _providers.Resolve(deployment.Spec.Provider);
            if (!string.Equals(provider.Name, deployment.Spec.Provider, StringComparison.Ordinal))
            {
                throw new ManagedDeploymentValidationException(
                    "provider-name-mismatch",
                    "The resolved provider name does not match desired state.");
            }

            var plan = await provider.PlanAsync(
                deployment.Spec,
                planning.Status,
                guard.Token).ConfigureAwait(false);
            ValidatePlan(deployment.Spec, desiredFingerprint, plan);
            guard.ThrowIfLost();

            if (!plan.RequiresChange)
            {
                var ready = await SaveStatusAsync(
                    planning,
                    ManagedDeploymentState.Ready,
                    desiredFingerprint,
                    plan.PlanFingerprint,
                    deployment.Status.ProviderResourceId,
                    null,
                    lease.FencingToken,
                    guard).ConfigureAwait(false);
                return Result(ready, changed: false);
            }

            var applying = await SaveStatusAsync(
                planning,
                ManagedDeploymentState.Applying,
                desiredFingerprint,
                planning.Status.AppliedPlanFingerprint,
                planning.Status.ProviderResourceId,
                null,
                lease.FencingToken,
                guard).ConfigureAwait(false);
            var applied = await provider.ApplyAsync(
                deployment.Spec,
                plan,
                lease.FencingToken,
                guard.Token).ConfigureAwait(false);
            ValidateProviderResult(plan, applied);
            guard.ThrowIfLost();

            var completed = await SaveStatusAsync(
                applying,
                ManagedDeploymentState.Ready,
                desiredFingerprint,
                applied.AppliedPlanFingerprint,
                applied.ProviderResourceId,
                null,
                lease.FencingToken,
                guard).ConfigureAwait(false);
            return Result(completed, changed: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (guard.IsLost)
        {
            throw new ManagedDeploymentLeaseException(
                "The managed deployment reconciliation lease was lost.");
        }
        catch (Exception exception)
        {
            await TryRecordFailureAsync(
                deploymentId,
                lease.FencingToken,
                GetDiagnosticCode(exception),
                guard).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<ManagedReconcileResult> DeleteAsync(
        string deploymentId,
        long expectedGeneration,
        bool overrideProtection,
        CancellationToken cancellationToken = default)
    {
        ValidateDeploymentId(deploymentId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedGeneration);

        var lease = await _leases.TryAcquireAsync(
            deploymentId,
            _owner,
            _leaseDuration,
            cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            throw new ManagedDeploymentLeaseException(
                $"Deployment '{deploymentId}' is being reconciled by another owner.");
        }

        await using var guard = new LeaseGuard(
            _leases,
            lease,
            _leaseDuration,
            _timeProvider,
            cancellationToken);
        try
        {
            var deployment = await _store.GetAsync(deploymentId, guard.Token)
                .ConfigureAwait(false);
            if (deployment is null)
            {
                throw new KeyNotFoundException($"Deployment '{deploymentId}' does not exist.");
            }

            if (deployment.Spec.Generation != expectedGeneration)
            {
                throw new ManagedDeploymentConcurrencyException(
                    "Desired state changed before deletion could begin.");
            }

            if (deployment.Spec.DeleteProtection && !overrideProtection)
            {
                throw new ManagedDeploymentValidationException(
                    "delete-protection-enabled",
                    "Delete protection must be explicitly overridden.");
            }

            if (deployment.Status.State == ManagedDeploymentState.Deleted)
            {
                return Result(deployment, changed: false);
            }

            var deleting = await SaveStatusAsync(
                deployment,
                ManagedDeploymentState.Deleting,
                deployment.Status.DesiredFingerprint,
                deployment.Status.AppliedPlanFingerprint,
                deployment.Status.ProviderResourceId,
                null,
                lease.FencingToken,
                guard).ConfigureAwait(false);
            var provider = _providers.Resolve(deployment.Spec.Provider);
            await provider.DeleteAsync(
                deployment.Spec,
                lease.FencingToken,
                guard.Token).ConfigureAwait(false);
            guard.ThrowIfLost();

            var deleted = await SaveStatusAsync(
                deleting,
                ManagedDeploymentState.Deleted,
                deployment.Status.DesiredFingerprint,
                deployment.Status.AppliedPlanFingerprint,
                null,
                null,
                lease.FencingToken,
                guard).ConfigureAwait(false);
            return Result(deleted, changed: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (guard.IsLost)
        {
            throw new ManagedDeploymentLeaseException(
                "The managed deployment reconciliation lease was lost.");
        }
        catch (Exception exception)
        {
            await TryRecordFailureAsync(
                deploymentId,
                lease.FencingToken,
                GetDiagnosticCode(exception),
                guard).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<ManagedDeployment> SaveStatusAsync(
        ManagedDeployment deployment,
        ManagedDeploymentState state,
        string? desiredFingerprint,
        string? appliedPlanFingerprint,
        string? providerResourceId,
        string? diagnosticCode,
        long fencingToken,
        LeaseGuard guard)
    {
        guard.ThrowIfLost();
        var currentSpec = await _store.GetAsync(deployment.Spec.DeploymentId, guard.Token)
            .ConfigureAwait(false);
        if (currentSpec is null ||
            currentSpec.Spec.Generation != deployment.Spec.Generation)
        {
            throw new ManagedDeploymentConcurrencyException(
                "Desired state changed while reconciliation was in progress.");
        }

        if (currentSpec.Status.Revision != deployment.Status.Revision)
        {
            throw new ManagedDeploymentConcurrencyException(
                "Observed state changed while reconciliation was in progress.");
        }

        var status = new ManagedDeploymentStatus(
            state,
            deployment.Spec.Generation,
            checked(deployment.Status.Revision + 1),
            fencingToken,
            desiredFingerprint,
            appliedPlanFingerprint,
            providerResourceId,
            diagnosticCode,
            _timeProvider.GetUtcNow());
        return await _store.UpdateStatusAsync(
            deployment.Spec.DeploymentId,
            status,
            deployment.Status.Revision,
            guard.Token).ConfigureAwait(false);
    }

    private async ValueTask TryRecordFailureAsync(
        string deploymentId,
        long fencingToken,
        string diagnosticCode,
        LeaseGuard guard)
    {
        try
        {
            guard.ThrowIfLost();
            var deployment = await _store.GetAsync(deploymentId, guard.Token)
                .ConfigureAwait(false);
            if (deployment is null ||
                deployment.Status.State is ManagedDeploymentState.Deleted or
                    ManagedDeploymentState.Paused)
            {
                return;
            }

            _ = await SaveStatusAsync(
                deployment,
                ManagedDeploymentState.Failed,
                deployment.Status.DesiredFingerprint,
                deployment.Status.AppliedPlanFingerprint,
                deployment.Status.ProviderResourceId,
                diagnosticCode,
                fencingToken,
                guard).ConfigureAwait(false);
        }
        catch
        {
            // The original failure remains authoritative. A later reconciliation observes
            // incomplete state through generation/revision and provider idempotency.
        }
    }

    private static void ValidatePlan(
        ManagedDeploymentSpec spec,
        string desiredFingerprint,
        ManagedDeploymentPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!string.Equals(plan.DeploymentId, spec.DeploymentId, StringComparison.Ordinal) ||
            plan.Generation != spec.Generation ||
            !string.Equals(plan.DesiredFingerprint, desiredFingerprint, StringComparison.Ordinal))
        {
            throw new ManagedDeploymentValidationException(
                "provider-plan-mismatch",
                "Provider plan identity does not match desired state.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(plan.PlanFingerprint);
        ArgumentNullException.ThrowIfNull(plan.Actions);
        if (plan.PlanFingerprint.Length > 256 || plan.Actions.Count > 1024)
        {
            throw new ManagedDeploymentValidationException(
                "provider-plan-unbounded",
                "Provider plan exceeds control-plane limits.");
        }

        foreach (var action in plan.Actions)
        {
            ArgumentNullException.ThrowIfNull(action);
            if (string.IsNullOrWhiteSpace(action.Kind) ||
                string.IsNullOrWhiteSpace(action.Resource) ||
                string.IsNullOrWhiteSpace(action.Summary) ||
                action.Kind.Length > 128 ||
                action.Resource.Length > 1024 ||
                action.Summary.Length > 2048)
            {
                throw new ManagedDeploymentValidationException(
                    "provider-plan-action-invalid",
                    "Provider plan contains an invalid action.");
            }
        }
    }

    private static void ValidateProviderResult(
        ManagedDeploymentPlan plan,
        ManagedProviderResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(result.ProviderResourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(result.AppliedPlanFingerprint);
        if (result.ProviderResourceId.Length > 2048 ||
            !string.Equals(
                result.AppliedPlanFingerprint,
                plan.PlanFingerprint,
                StringComparison.Ordinal))
        {
            throw new ManagedDeploymentValidationException(
                "provider-result-invalid",
                "Provider result does not match the applied plan.");
        }
    }

    private static string GetDiagnosticCode(Exception exception) =>
        exception switch
        {
            ManagedDeploymentValidationException validation => validation.Code,
            ManagedDeploymentConcurrencyException => "concurrent-update",
            ManagedDeploymentLeaseException => "lease-lost",
            OperationCanceledException => "cancelled",
            _ => "provider-failure",
        };

    private static ManagedReconcileResult Result(ManagedDeployment deployment, bool changed) =>
        new(
            deployment.Spec.DeploymentId,
            deployment.Spec.Generation,
            deployment.Status.State,
            changed,
            deployment.Status.DiagnosticCode);

    private static void ValidateDeploymentId(string deploymentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId);
        if (deploymentId.Length > 128)
        {
            throw new ArgumentException(
                "Deployment ID cannot exceed 128 characters.",
                nameof(deploymentId));
        }
    }

    private sealed class LeaseGuard : IAsyncDisposable
    {
        private readonly IManagedDeploymentLeaseStore _store;
        private readonly TimeSpan _duration;
        private readonly CancellationTokenSource _lifetime;
        private readonly CancellationTokenSource _linked;
        private readonly Task _renewal;
        private ManagedDeploymentLease _lease;
        private Exception? _failure;

        internal LeaseGuard(
            IManagedDeploymentLeaseStore store,
            ManagedDeploymentLease lease,
            TimeSpan duration,
            TimeProvider timeProvider,
            CancellationToken cancellationToken)
        {
            _store = store;
            _lease = lease;
            _duration = duration;
            _lifetime = new CancellationTokenSource();
            _linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token);
            Token = _linked.Token;
            _renewal = RenewAsync(timeProvider);
        }

        internal CancellationToken Token { get; }

        internal bool IsLost => _failure is not null;

        internal void ThrowIfLost()
        {
            if (_failure is not null)
            {
                throw new ManagedDeploymentLeaseException(
                    "The managed deployment reconciliation lease was lost.");
            }

            Token.ThrowIfCancellationRequested();
        }

        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            try
            {
                await _renewal.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            try
            {
                await _store.ReleaseAsync(_lease, CancellationToken.None).ConfigureAwait(false);
            }
            catch (ManagedDeploymentLeaseException)
            {
            }

            _linked.Dispose();
            _lifetime.Dispose();
        }

        private async Task RenewAsync(TimeProvider timeProvider)
        {
            var interval = TimeSpan.FromTicks(_duration.Ticks / 3);
            try
            {
                while (true)
                {
                    await Task.Delay(interval, timeProvider, _linked.Token).ConfigureAwait(false);
                    _lease = await _store.RenewAsync(
                        _lease,
                        _duration,
                        _linked.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_linked.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                _failure = exception;
                await _linked.CancelAsync().ConfigureAwait(false);
            }
        }
    }
}
