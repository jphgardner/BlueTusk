namespace BlueTusk.ControlPlane.Tests;

public sealed class ManagedHostingTests
{
    private static readonly ManagedTenantQuota GenerousQuota =
        new(10, 100, 1_000_000, 1L << 50, 1L << 50);

    [Fact]
    public async Task Reconciliation_emits_bounded_operation_metrics()
    {
        var measurements =
            new System.Collections.Concurrent.ConcurrentQueue<(string Name, long Value, string? Outcome)>();
        using var listener = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "BlueTusk.ControlPlane")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            string? outcome = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "bluetusk.control_plane.outcome")
                {
                    outcome = tag.Value?.ToString();
                }
            }

            measurements.Enqueue((instrument.Name, value, outcome));
        });
        listener.Start();

        var store = new InMemoryManagedDeploymentStore();
        await store.PutAsync(Spec(), expectedGeneration: 0);
        var controller = Controller(store, new RecordingProvider());

        _ = await controller.ReconcileAsync(
            "orders",
            TestContext.Current.CancellationToken);

        Assert.Contains(
            measurements,
            item => item.Name == "bluetusk.control_plane.operations.active" &&
                item.Value == 1);
        Assert.Contains(
            measurements,
            item => item.Name == "bluetusk.control_plane.operations.active" &&
                item.Value == -1);
        Assert.Contains(
            measurements,
            item => item.Name == "bluetusk.control_plane.operations" &&
                item.Value == 1 &&
                item.Outcome == "changed");
    }

    [Fact]
    public void Fingerprint_is_canonical_and_excludes_generation()
    {
        var first = Spec() with
        {
            Labels = new Dictionary<string, string>
            {
                ["team"] = "data",
                ["environment"] = "production",
            },
        };
        var second = first with
        {
            Generation = 99,
            Labels = new Dictionary<string, string>
            {
                ["environment"] = "production",
                ["team"] = "data",
            },
        };

        Assert.Equal(
            ManagedDeploymentValidation.GetFingerprint(first),
            ManagedDeploymentValidation.GetFingerprint(second));
    }

    [Fact]
    public void Validation_rejects_duplicate_workloads_and_raw_control_characters()
    {
        var duplicate = Spec() with
        {
            Workloads =
            [
                Workload(ManagedWorkloadKind.Streams),
                Workload(ManagedWorkloadKind.Streams),
            ],
        };
        var exception = Assert.Throws<ManagedDeploymentValidationException>(
            () => ManagedDeploymentValidation.Validate(duplicate));
        Assert.Equal("workload-kind-duplicate", exception.Code);

        var invalid = Spec() with { DeploymentId = "unsafe\nidentifier" };
        exception = Assert.Throws<ManagedDeploymentValidationException>(
            () => ManagedDeploymentValidation.Validate(invalid));
        Assert.Equal("identifier-invalid", exception.Code);
    }

    [Fact]
    public void Quotas_are_checked_with_overflows_and_stable_diagnostic_codes()
    {
        var usage = ManagedDeploymentValidation.GetRequestedUsage(Spec());
        var exception = Assert.Throws<ManagedDeploymentValidationException>(
            () => ManagedDeploymentValidation.EnforceQuota(
                Spec(),
                GenerousQuota with { MaximumReplicas = usage.Replicas - 1 },
                usage));

        Assert.Equal("quota-replicas-exceeded", exception.Code);
    }

    [Fact]
    public async Task Store_enforces_generation_CAS_and_copies_mutable_inputs()
    {
        var store = new InMemoryManagedDeploymentStore();
        var settings = new Dictionary<string, string> { ["mode"] = "safe" };
        var spec = Spec() with
        {
            Workloads =
            [
                Workload(ManagedWorkloadKind.Streams) with { Settings = settings },
            ],
        };

        var created = await store.PutAsync(spec, expectedGeneration: 0);
        settings["mode"] = "unsafe";

        Assert.Equal(
            "safe",
            Assert.Single(created.Spec.Workloads).Settings["mode"]);
        Assert.Equal(
            "safe",
            Assert.Single((await store.GetAsync(spec.DeploymentId))!.Spec.Workloads)
                .Settings["mode"]);

        await Assert.ThrowsAsync<ManagedDeploymentConcurrencyException>(
            () => store.PutAsync(
                spec with { Generation = 2 },
                expectedGeneration: 9).AsTask());
        await Assert.ThrowsAsync<ManagedDeploymentConcurrencyException>(
            () => store.PutAsync(
                spec with { Generation = 3 },
                expectedGeneration: 1).AsTask());
    }

    [Fact]
    public async Task Controller_applies_once_then_converges_without_provider_mutation()
    {
        var store = new InMemoryManagedDeploymentStore();
        var provider = new RecordingProvider();
        await store.PutAsync(Spec(), expectedGeneration: 0);
        var controller = Controller(store, provider);

        var first = await controller.ReconcileAsync("orders");
        var second = await controller.ReconcileAsync("orders");

        Assert.Equal(ManagedDeploymentState.Ready, first.State);
        Assert.True(first.Changed);
        Assert.False(second.Changed);
        Assert.Equal(1, provider.ApplyCount);
        Assert.Equal(2, provider.PlanCount);
        Assert.All(provider.FencingTokens, token => Assert.True(token > 0));
        var stored = Assert.IsType<ManagedDeployment>(await store.GetAsync("orders"));
        Assert.Equal(stored.Status.DesiredFingerprint, provider.LastDesiredFingerprint);
        Assert.Equal("resource/orders", stored.Status.ProviderResourceId);
        Assert.Null(stored.Status.DiagnosticCode);
    }

    [Fact]
    public async Task Quota_failure_never_reaches_the_provider_and_records_a_safe_code()
    {
        var store = new InMemoryManagedDeploymentStore();
        var provider = new RecordingProvider();
        await store.PutAsync(Spec(), expectedGeneration: 0);
        var quota = new ManagedDeploymentQuotaSource(
            store,
            new Dictionary<string, ManagedTenantQuota>
            {
                ["tenant-a"] = GenerousQuota with { MaximumDeployments = 0 },
            });
        var controller = new ManagedDeploymentController(
            store,
            store,
            quota,
            new ManagedInfrastructureProviderResolver([provider]),
            "worker-a");

        var exception = await Assert.ThrowsAsync<ManagedDeploymentValidationException>(
            () => controller.ReconcileAsync("orders").AsTask());

        Assert.Equal("quota-deployments-exceeded", exception.Code);
        Assert.Equal(0, provider.PlanCount);
        var stored = Assert.IsType<ManagedDeployment>(await store.GetAsync("orders"));
        Assert.Equal(ManagedDeploymentState.Failed, stored.Status.State);
        Assert.Equal("quota-deployments-exceeded", stored.Status.DiagnosticCode);
    }

    [Fact]
    public async Task Provider_failure_records_only_a_stable_diagnostic()
    {
        var store = new InMemoryManagedDeploymentStore();
        var provider = new RecordingProvider
        {
            ApplyException = new InvalidOperationException("do-not-store-this-message"),
        };
        await store.PutAsync(Spec(), expectedGeneration: 0);
        var controller = Controller(store, provider);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.ReconcileAsync("orders").AsTask());

        Assert.Equal("do-not-store-this-message", exception.Message);
        var stored = Assert.IsType<ManagedDeployment>(await store.GetAsync("orders"));
        Assert.Equal(ManagedDeploymentState.Failed, stored.Status.State);
        Assert.Equal("provider-failure", stored.Status.DiagnosticCode);
        Assert.DoesNotContain(
            "do-not-store-this-message",
            stored.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Active_lease_fences_a_second_controller()
    {
        var store = new InMemoryManagedDeploymentStore();
        var provider = new RecordingProvider { BlockPlanning = true };
        await store.PutAsync(Spec(), expectedGeneration: 0);
        var first = Controller(store, provider, "worker-a");
        var second = Controller(store, provider, "worker-b");
        var inFlight = first.ReconcileAsync("orders").AsTask();
        await provider.PlanStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<ManagedDeploymentLeaseException>(
            () => second.ReconcileAsync("orders").AsTask());

        provider.AllowPlan.TrySetResult();
        Assert.Equal(ManagedDeploymentState.Ready, (await inFlight).State);
    }

    [Fact]
    public async Task Delete_requires_generation_match_and_explicit_protection_override()
    {
        var store = new InMemoryManagedDeploymentStore();
        var provider = new RecordingProvider();
        var spec = Spec() with { DeleteProtection = true };
        await store.PutAsync(spec, expectedGeneration: 0);
        var controller = Controller(store, provider);
        await controller.ReconcileAsync("orders");

        var exception = await Assert.ThrowsAsync<ManagedDeploymentValidationException>(
            () => controller.DeleteAsync(
                "orders",
                expectedGeneration: 1,
                overrideProtection: false).AsTask());
        Assert.Equal("delete-protection-enabled", exception.Code);
        Assert.Equal(0, provider.DeleteCount);

        var deleted = await controller.DeleteAsync(
            "orders",
            expectedGeneration: 1,
            overrideProtection: true);
        Assert.Equal(ManagedDeploymentState.Deleted, deleted.State);
        Assert.Equal(1, provider.DeleteCount);
        Assert.Null((await store.GetAsync("orders"))!.Status.ProviderResourceId);
    }

    [Fact]
    public async Task Lease_tokens_increase_after_release_and_stale_release_is_rejected()
    {
        var store = new InMemoryManagedDeploymentStore();
        var first = Assert.IsType<ManagedDeploymentLease>(
            await store.TryAcquireAsync("orders", "worker-a", TimeSpan.FromMinutes(1)));
        await store.ReleaseAsync(first);
        var second = Assert.IsType<ManagedDeploymentLease>(
            await store.TryAcquireAsync("orders", "worker-b", TimeSpan.FromMinutes(1)));

        Assert.True(second.FencingToken > first.FencingToken);
        await Assert.ThrowsAsync<ManagedDeploymentLeaseException>(
            () => store.ReleaseAsync(first).AsTask());
    }

    private static ManagedDeploymentController Controller(
        InMemoryManagedDeploymentStore store,
        RecordingProvider provider,
        string owner = "worker-a") =>
        new(
            store,
            store,
            new ManagedDeploymentQuotaSource(
                store,
                new Dictionary<string, ManagedTenantQuota>
                {
                    ["tenant-a"] = GenerousQuota,
                }),
            new ManagedInfrastructureProviderResolver([provider]),
            owner);

    private static ManagedDeploymentSpec Spec() =>
        new(
            "orders",
            "tenant-a",
            "test",
            "eu-west",
            1,
            Paused: false,
            DeleteProtection: false,
            [Workload(ManagedWorkloadKind.Streams)],
            new Dictionary<string, string>());

    private static ManagedWorkloadSpec Workload(ManagedWorkloadKind kind) =>
        new(
            kind,
            "1.0.0",
            new ManagedResourceRequest(
                Replicas: 2,
                CpuMillicoresPerReplica: 500,
                MemoryBytesPerReplica: 256 * 1024 * 1024,
                StorageBytes: 1024 * 1024 * 1024),
            [new ManagedSecretReference("vault", "postgres/orders", "7")],
            new Dictionary<string, string>());

    private sealed class RecordingProvider : IManagedInfrastructureProvider
    {
        public string Name => "test";

        public int PlanCount { get; private set; }

        public int ApplyCount { get; private set; }

        public int DeleteCount { get; private set; }

        public string? LastDesiredFingerprint { get; private set; }

        public Exception? ApplyException { get; init; }

        public bool BlockPlanning { get; init; }

        public TaskCompletionSource PlanStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowPlan { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<long> FencingTokens { get; } = [];

        public async ValueTask<ManagedDeploymentPlan> PlanAsync(
            ManagedDeploymentSpec desired,
            ManagedDeploymentStatus current,
            CancellationToken cancellationToken = default)
        {
            PlanCount++;
            var desiredFingerprint = ManagedDeploymentValidation.GetFingerprint(desired);
            LastDesiredFingerprint = desiredFingerprint;
            var planFingerprint = "plan-" + desiredFingerprint;
            PlanStarted.TrySetResult();
            if (BlockPlanning)
            {
                await AllowPlan.Task.WaitAsync(cancellationToken);
            }

            return new ManagedDeploymentPlan(
                desired.DeploymentId,
                desired.Generation,
                desiredFingerprint,
                planFingerprint,
                !string.Equals(
                    current.AppliedPlanFingerprint,
                    planFingerprint,
                    StringComparison.Ordinal),
                [new ManagedDeploymentAction("upsert", "worker", "Apply desired worker state.")]);
        }

        public ValueTask<ManagedProviderResult> ApplyAsync(
            ManagedDeploymentSpec desired,
            ManagedDeploymentPlan plan,
            long fencingToken,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyCount++;
            FencingTokens.Add(fencingToken);
            if (ApplyException is not null)
            {
                throw ApplyException;
            }

            return ValueTask.FromResult(
                new ManagedProviderResult(
                    "resource/" + desired.DeploymentId,
                    plan.PlanFingerprint));
        }

        public ValueTask DeleteAsync(
            ManagedDeploymentSpec desired,
            long fencingToken,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteCount++;
            FencingTokens.Add(fencingToken);
            return ValueTask.CompletedTask;
        }
    }
}
