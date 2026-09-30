using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using BlueTusk.Data;
using BlueTusk.Jobs;
using BlueTusk.Jobs.DependencyInjection;
using BlueTusk.Workflows.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BlueTusk.Workflows.DependencyInjection.Tests;

public sealed class HostingReadinessTests
{
    [Fact]
    public async Task ScopedSaturationAndGeneratedJsonAreRedactedAndMetricsHaveFixedCardinality()
    {
        await using var database = await WorkflowDatabase.CreateAsync();
        var measurements = new ConcurrentBag<(string Name, string[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name is JobHealthTelemetry.InstrumentationName or WorkflowHealthTelemetry.InstrumentationName)
            {
                current.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => measurements.Add((instrument.Name, tags.ToArray().Select(tag => tag.Key + "=" + tag.Value).ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => measurements.Add((instrument.Name, tags.ToArray().Select(tag => tag.Key + "=" + tag.Value).ToArray())));
        listener.Start();
        await database.Store.RegisterDefinitionAsync(database.Scope, new WorkflowDefinition
        {
            Name = "test",
            Version = 1,
            Nodes = [new() { Id = "wait", Kind = WorkflowNodeKind.Signal, Signal = "never" }],
        });
        for (int index = 0; index < 4; index++)
        {
            _ = await database.Store.StartAsync(database.Request());
            _ = await database.Store.Jobs.EnqueueAsync(new JobRequest { Scope = database.Scope, JobType = "ops", Payload = new byte[] { 42 } });
        }
        var jobs = new JobQueueHealthCheck(database.Store.Jobs, database.Scope,
            new JobHealthCheckOptions { MaximumObserved = 2, MaximumPending = 2 });
        var workflows = new WorkflowScopeHealthCheck(database.Store, database.Scope,
            new WorkflowHealthCheckOptions { MaximumObserved = 2, MaximumRunning = 2, MaximumCompensating = 2, MaximumPendingJobs = 2 });
        var job = await jobs.ReadAsync();
        var workflow = await workflows.ReadAsync();
        Assert.Equal(HealthStatus.Degraded, job.Status);
        Assert.Equal(2, job.Queue!.PendingObserved);
        Assert.True(job.Queue.PendingCountCapped);
        Assert.Equal(HealthStatus.Degraded, workflow.Status);
        Assert.Equal(2, workflow.Scope!.RunningObserved);
        Assert.True(workflow.Scope.RunningCountCapped);
        Assert.Equal(2, workflow.Dispatch!.PendingObserved);
        string jobJson = JsonSerializer.Serialize(job, JobQueueHealthCheck.JsonTypeInfo);
        string workflowJson = JsonSerializer.Serialize(workflow, WorkflowScopeHealthCheck.JsonTypeInfo);
        Assert.DoesNotContain(database.Scope.Tenant, jobJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"" + database.Scope.Queue + "\"", workflowJson, StringComparison.Ordinal);
        Assert.Equal(job, JsonSerializer.Deserialize(jobJson, JobQueueHealthCheck.JsonTypeInfo));
        Assert.Equal(workflow, JsonSerializer.Deserialize(workflowJson, WorkflowScopeHealthCheck.JsonTypeInfo));
        var other = new WorkflowScopeHealthCheck(database.Store, new JobScope("other", database.Scope.Queue), new WorkflowHealthCheckOptions());
        Assert.Equal(HealthStatus.Healthy, (await other.ReadAsync()).Status);
        Assert.NotEmpty(measurements);
        Assert.All(measurements.SelectMany(row => row.Tags), tag => Assert.Contains(tag, AllowedMetricTags));
        Assert.Equal(6, measurements.Select(row => row.Name).Distinct().Count());
        Assert.Contains(measurements, row => row.Name == "bluetusk.jobs.health.probes" && row.Tags.Contains("status=degraded", StringComparer.Ordinal));
        Assert.Contains(measurements, row => row.Name == "bluetusk.workflows.health.probes" && row.Tags.Contains("status=healthy", StringComparer.Ordinal));
    }

    private static readonly string[] AllowedMetricTags = ["status=healthy", "status=degraded", "status=unhealthy"];

    [Fact]
    public async Task ReadOnlyHealthRoleWithTenantPolicyCannotSeeOtherTenantOrWriteAndDeniedProbeHasNoException()
    {
        await using var database = await WorkflowDatabase.CreateAsync();
        var otherScope = new JobScope("tenant-b", database.Scope.Queue);
        var definition = new WorkflowDefinition { Name = "test", Version = 1, Nodes = [new() { Id = "wait", Kind = WorkflowNodeKind.Signal, Signal = "never" }] };
        await database.Store.RegisterDefinitionAsync(database.Scope, definition);
        await database.Store.RegisterDefinitionAsync(otherScope, definition);
        _ = await database.Store.StartAsync(database.Request());
        _ = await database.Store.StartAsync(database.Request() with { Scope = otherScope });
        _ = await database.Store.Jobs.EnqueueAsync(new JobRequest { Scope = database.Scope, JobType = "ops", Payload = new byte[] { 1 } });
        _ = await database.Store.Jobs.EnqueueAsync(new JobRequest { Scope = otherScope, JobType = "ops", Payload = new byte[] { 2 } });
        await using var role = await OwnedOperationalRole.CreateAsync(database);
        await role.GrantHealthAsync();
        await database.ExecuteAsync($$"""
            ALTER TABLE {jobs}.jobs ENABLE ROW LEVEL SECURITY;
            CREATE POLICY health_owned_tenant ON {jobs}.jobs FOR SELECT TO "{{role.Name}}" USING (tenant = 'tenant-a');
            ALTER TABLE {schema}.instances ENABLE ROW LEVEL SECURITY;
            CREATE POLICY health_owned_tenant ON {schema}.instances FOR SELECT TO "{{role.Name}}" USING (tenant = 'tenant-a');
            """);
        var store = new PostgreSqlWorkflowStore(role.Source, database.Options, database.JobOptions);
        var allowed = new JobQueueHealthCheck(store.Jobs, database.Scope, new JobHealthCheckOptions());
        Assert.Equal(1, (await allowed.ReadAsync()).Queue!.PendingObserved);
        var foreign = new JobQueueHealthCheck(store.Jobs, otherScope, new JobHealthCheckOptions());
        Assert.Equal(0, (await foreign.ReadAsync()).Queue!.PendingObserved);
        await Assert.ThrowsAsync<BlueTuskException>(() => store.Jobs.EnqueueAsync(new JobRequest
        {
            Scope = database.Scope,
            JobType = "denied",
            Payload = new byte[] { 3 },
        }).AsTask());
        var workflowCheck = new WorkflowScopeHealthCheck(store, database.Scope, new WorkflowHealthCheckOptions());
        var permitted = await workflowCheck.ReadAsync();
        Assert.Equal(HealthStatus.Healthy, permitted.Status);
        Assert.Equal(1, permitted.Scope!.RunningObserved);
        var foreignWorkflow = new WorkflowScopeHealthCheck(store, otherScope, new WorkflowHealthCheckOptions());
        Assert.Equal(0, (await foreignWorkflow.ReadAsync()).Scope!.RunningObserved);
        await database.ExecuteAsync($"REVOKE SELECT ON {{schema}}.instances FROM \"{role.Name}\"");
        var denied = await workflowCheck.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, denied.Status);
        Assert.Equal("workflows.health.access_denied", denied.Description);
        Assert.Null(denied.Exception);
        Assert.Empty(denied.Data);
        await database.ExecuteAsync($"REVOKE SELECT ON {{jobs}}.jobs FROM \"{role.Name}\"");
        var deniedJobs = await allowed.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal("jobs.health.access_denied", deniedJobs.Description);
        Assert.Null(deniedJobs.Exception);
    }

    [Fact]
    public async Task LockedDatabaseProbeHasFiniteDeadlineRejectsOverlappingWorkAndRecoversAfterRollback()
    {
        await using var database = await WorkflowDatabase.CreateAsync();
        var check = new JobQueueHealthCheck(database.Store.Jobs, database.Scope,
            new JobHealthCheckOptions { Timeout = TimeSpan.FromMilliseconds(400) });
        await using var connection = await database.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new BlueTuskCommand($"LOCK TABLE \"{database.JobOptions.Schema}\".jobs IN ACCESS EXCLUSIVE MODE", connection) { Transaction = transaction };
        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
        var elapsed = Stopwatch.StartNew();
        Task<JobReadiness> blocked = check.ReadAsync().AsTask();
        await Task.Delay(50);
        var overlapping = await check.ReadAsync();
        Assert.Equal("jobs.health.probe_busy", overlapping.Code);
        var timedOut = await blocked.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("jobs.health.deadline", timedOut.Code);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5));
        await transaction.RollbackAsync();
        Assert.Equal(HealthStatus.Healthy, (await check.ReadAsync()).Status);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check.ReadAsync(canceled.Token).AsTask());
    }

    [Fact]
    public async Task TypedDiFactoriesAreBoundedReusableAndDoNotInitializeDatabaseOrOwnItsSource()
    {
        await using var database = await WorkflowDatabase.CreateAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        int factories = 0;
        services.AddJobsReadiness("jobs", database.Scope, _ => { Interlocked.Increment(ref factories); return database.Store.Jobs; }, new JobHealthCheckOptions());
        services.AddWorkflowsReadiness("workflows", database.Scope, _ => database.Store, new WorkflowHealthCheckOptions());
        Assert.Throws<InvalidOperationException>(() => services.AddJobsReadiness("jobs", database.Scope, _ => database.Store.Jobs, new JobHealthCheckOptions()));
        await using (var provider = services.BuildServiceProvider())
        {
            var health = provider.GetRequiredService<HealthCheckService>();
            Assert.Equal(HealthStatus.Healthy, (await health.CheckHealthAsync()).Status);
            Assert.Equal(HealthStatus.Healthy, (await health.CheckHealthAsync()).Status);
            Assert.Equal(1, factories);
        }
        await using (var secondProvider = services.BuildServiceProvider())
        {
            Assert.Equal(HealthStatus.Healthy, (await secondProvider.GetRequiredService<HealthCheckService>().CheckHealthAsync()).Status);
            Assert.Equal(2, factories);
        }
        Assert.Equal(0, (await database.Store.Jobs.InspectAsync(database.Scope)).PendingObserved);
        var bounded = new ServiceCollection();
        for (int index = 0; index < 128; index++)
        {
            bounded.AddJobsReadiness("check-" + index, database.Scope, _ => database.Store.Jobs, new JobHealthCheckOptions());
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => bounded.AddJobsReadiness("overflow", database.Scope, _ => database.Store.Jobs, new JobHealthCheckOptions()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JobQueueHealthCheck(database.Store.Jobs, database.Scope, new JobHealthCheckOptions { Timeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkflowScopeHealthCheck(database.Store, database.Scope, new WorkflowHealthCheckOptions { MaximumObserved = 100001 }));
    }

    [Fact]
    public async Task SignalWaitAgeIsExplicitPolicyAndWorkflowProbeDeadlineAndCallerCancellationReleaseGate()
    {
        await using var database = await WorkflowDatabase.CreateAsync();
        _ = await database.StartAsync([new() { Id = "wait", Kind = WorkflowNodeKind.Signal, Signal = "never" }]);
        await database.ExecuteAsync("UPDATE {schema}.instances SET created_at = clock_timestamp() - interval '1 day'");
        var normal = new WorkflowScopeHealthCheck(database.Store, database.Scope, new WorkflowHealthCheckOptions());
        Assert.Equal(HealthStatus.Healthy, (await normal.ReadAsync()).Status);
        var aged = new WorkflowScopeHealthCheck(database.Store, database.Scope,
            new WorkflowHealthCheckOptions { MaximumActiveAge = TimeSpan.FromHours(1) });
        Assert.Equal("workflows.health.pressure", (await aged.ReadAsync()).Code);
        await using var connection = await database.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new BlueTuskCommand($"LOCK TABLE \"{database.Options.Schema}\".instances IN ACCESS EXCLUSIVE MODE", connection) { Transaction = transaction };
        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
        var bounded = new WorkflowScopeHealthCheck(database.Store, database.Scope, new WorkflowHealthCheckOptions { Timeout = TimeSpan.FromMilliseconds(400) });
        Task<WorkflowReadiness> first = bounded.ReadAsync().AsTask();
        await Task.Delay(50);
        Assert.Equal("workflows.health.probe_busy", (await bounded.ReadAsync()).Code);
        Assert.Equal("workflows.health.deadline", (await first.WaitAsync(TimeSpan.FromSeconds(5))).Code);
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => normal.ReadAsync(caller.Token).AsTask());
        await transaction.RollbackAsync();
        Assert.Equal(HealthStatus.Healthy, (await normal.ReadAsync()).Status);
        Assert.Equal(HealthStatus.Healthy, (await bounded.ReadAsync()).Status);
    }
}
