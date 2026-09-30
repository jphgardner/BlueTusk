using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlueTusk.ControlPlane;
using BlueTusk.Live;
using Xunit.Sdk;

namespace BlueTusk.StressTests;

public sealed class LiveControlPlaneEnduranceTests
{
    private const int LiveRowCount = 10_000;
    private const int DeploymentCount = 256;

    [Fact]
    public async Task Live_and_control_plane_remain_bounded_through_churn_and_drift_checks()
    {
        var settings = ReadSettings();
        var cancellationToken = TestContext.Current.CancellationToken;
        var rows = Enumerable.Range(1, LiveRowCount)
            .Select(static id => new LiveRow(id, 0))
            .ToArray();
        var initial = LiveResultDiffer.Initial<LiveRow, int>(
            rows,
            static row => row.Id);
        var snapshot = initial.Snapshot;
        var nextSequence = initial.LastSequence + 1;

        var store = new InMemoryManagedDeploymentStore();
        for (var index = 0; index < DeploymentCount; index++)
        {
            await store.PutAsync(
                CreateDeployment(index, generation: 1, revision: 0),
                expectedGeneration: 0,
                cancellationToken);
        }
        var fleet = new ManagedDeploymentFleetQueryService(store);
        var audit = new CountingAuditStore();
        var operations = new CountingOperationHandler();
        var executor = new ControlPlaneOperationExecutor(
            new RoleControlPlaneAuthorizer(),
            audit,
            operations);
        var actor = new ControlPlaneActor(
            "endurance-operator",
            new HashSet<ControlPlaneRole> { ControlPlaneRole.Operator });

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var process = Process.GetCurrentProcess();
        var cycles = 0L;
        var liveUpdates = 0L;
        var authoritativeChecks = 0L;
        var inventoryReads = 0L;
        var operationExecutions = 0L;
        var maximumCycleMilliseconds = 0L;
        var maximumWorkingSetBytes = process.WorkingSet64;
        var startAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
        var startGen0 = GC.CollectionCount(0);
        var startGen1 = GC.CollectionCount(1);
        var startGen2 = GC.CollectionCount(2);

        while (stopwatch.Elapsed < settings.Duration || cycles == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cycleWatch = Stopwatch.StartNew();
            var cycle = checked(cycles + 1);

            var rowIndex = (int)(cycles % rows.Length);
            var changed = new LiveRow(rowIndex + 1, cycle);
            rows[rowIndex] = changed;
            var diff = LiveResultDiffer.DiffAffected(
                snapshot,
                [changed],
                static row => row.Id,
                nextSequence: nextSequence);
            if (diff.Events.Count != 1 ||
                diff.Events[0].Kind != LiveEventKind.RowUpdated ||
                diff.Events[0].Key != changed.Id)
            {
                throw new InvalidOperationException(
                    "Affected-key Live maintenance diverged from the expected one-row update.");
            }
            snapshot = diff.Snapshot;
            nextSequence = checked(diff.LastSequence + 1);
            liveUpdates++;

            if (cycle % 128 == 0)
            {
                var authoritative = LiveResultDiffer.Diff(
                    snapshot,
                    rows,
                    static row => row.Id,
                    nextSequence: nextSequence);
                if (authoritative.Events.Count != 0 ||
                    !authoritative.Snapshot.Rows.SequenceEqual(rows))
                {
                    throw new InvalidOperationException(
                        "Live affected-key state failed an authoritative drift check.");
                }
                authoritativeChecks++;
            }

            var deploymentIndex = (int)(cycles % DeploymentCount);
            var deploymentId = "deployment-" + deploymentIndex.ToString(
                "D4",
                CultureInfo.InvariantCulture);
            var current = await store.GetAsync(deploymentId, cancellationToken) ??
                throw new InvalidOperationException("A managed deployment disappeared during endurance.");
            await store.PutAsync(
                current.Spec with
                {
                    Generation = checked(current.Spec.Generation + 1),
                    Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["endurance-cycle"] = cycle.ToString(CultureInfo.InvariantCulture),
                    },
                },
                current.Spec.Generation,
                cancellationToken);

            var overview = await fleet.GetFleetOverviewAsync(cancellationToken);
            if (overview.Deployments.Count != DeploymentCount ||
                overview.Deployments[0].DeploymentId != "deployment-0000" ||
                overview.Deployments[^1].DeploymentId != "deployment-0255")
            {
                throw new InvalidOperationException(
                    "Control Plane fleet inventory lost membership or stable ordering.");
            }
            inventoryReads++;

            var operation = new ControlPlaneOperationRequest(
                Guid.NewGuid(),
                ControlPlaneOperationKind.ReconcileDeployment,
                deploymentId,
                $"{ControlPlaneOperationKind.ReconcileDeployment}:{deploymentId}",
                "release-endurance");
            await executor.ExecuteAsync(actor, operation, cancellationToken);
            operationExecutions++;

            cycles = cycle;
            cycleWatch.Stop();
            maximumCycleMilliseconds = Math.Max(
                maximumCycleMilliseconds,
                (long)cycleWatch.Elapsed.TotalMilliseconds);
            process.Refresh();
            maximumWorkingSetBytes = Math.Max(
                maximumWorkingSetBytes,
                process.WorkingSet64);

            if (settings.IntervalMilliseconds > 0)
            {
                await Task.Delay(settings.IntervalMilliseconds, cancellationToken);
            }
        }
        stopwatch.Stop();

        if (cycles < settings.MinimumCycles)
        {
            throw new InvalidOperationException(
                $"Live/Control Plane endurance completed {cycles} cycle(s), below {settings.MinimumCycles}.");
        }
        if (audit.Count != checked(operationExecutions * 2) ||
            audit.InvalidTransitions != 0 ||
            audit.HasPendingOperation ||
            operations.Count != operationExecutions)
        {
            throw new InvalidOperationException(
                "Control Plane operations did not retain requested/succeeded audit ordering.");
        }

        var report = new HarnessReport(
            settings.Duration.ToString("c", CultureInfo.InvariantCulture),
            stopwatch.Elapsed.ToString("c", CultureInfo.InvariantCulture),
            cycles,
            settings.MinimumCycles,
            LiveRowCount,
            DeploymentCount,
            liveUpdates,
            authoritativeChecks,
            inventoryReads,
            operationExecutions,
            audit.Count,
            maximumCycleMilliseconds,
            maximumWorkingSetBytes,
            checked(GC.GetTotalAllocatedBytes(precise: false) - startAllocatedBytes),
            GC.CollectionCount(0) - startGen0,
            GC.CollectionCount(1) - startGen1,
            GC.CollectionCount(2) - startGen2,
            startedAt,
            DateTimeOffset.UtcNow);
        var reportDirectory = Path.GetDirectoryName(settings.ReportPath);
        if (!string.IsNullOrWhiteSpace(reportDirectory))
        {
            Directory.CreateDirectory(reportDirectory);
        }
        await File.WriteAllTextAsync(
            settings.ReportPath,
            JsonSerializer.Serialize(report, SourceGenerationContext.Default.HarnessReport),
            cancellationToken);
    }

    private static ManagedDeploymentSpec CreateDeployment(
        int index,
        long generation,
        long revision) =>
        new(
            "deployment-" + index.ToString("D4", CultureInfo.InvariantCulture),
            "tenant-" + (index % 16).ToString("D2", CultureInfo.InvariantCulture),
            "kubernetes",
            "lon1",
            generation,
            Paused: false,
            DeleteProtection: true,
            [
                new ManagedWorkloadSpec(
                    index % 2 == 0 ? ManagedWorkloadKind.Live : ManagedWorkloadKind.ControlPlane,
                    "1.2.0-rc.1",
                    new ManagedResourceRequest(2, 250, 256L * 1024 * 1024, 1024L * 1024 * 1024),
                    [],
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["revision"] = revision.ToString(CultureInfo.InvariantCulture),
                    }),
            ],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["environment"] = "endurance",
            });

    private static Settings ReadSettings()
    {
        var durationText = Environment.GetEnvironmentVariable(
            "BLUETUSK_LIVE_CONTROL_PLANE_ENDURANCE_DURATION");
        if (string.IsNullOrWhiteSpace(durationText))
        {
            throw SkipException.ForSkip(
                "Live/Control Plane endurance is disabled unless its duration is explicit.");
        }
        if (!TimeSpan.TryParse(
                durationText,
                CultureInfo.InvariantCulture,
                out var duration) ||
            duration < TimeSpan.FromSeconds(1))
        {
            throw new InvalidOperationException("The Live/Control Plane endurance duration is invalid.");
        }

        var minimumText = Environment.GetEnvironmentVariable(
            "BLUETUSK_LIVE_CONTROL_PLANE_ENDURANCE_MIN_CYCLES");
        if (!long.TryParse(
                minimumText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var minimumCycles) ||
            minimumCycles <= 0)
        {
            throw new InvalidOperationException("The Live/Control Plane minimum cycle count is invalid.");
        }

        var intervalText = Environment.GetEnvironmentVariable(
            "BLUETUSK_LIVE_CONTROL_PLANE_ENDURANCE_INTERVAL_MS");
        if (!int.TryParse(
                intervalText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var intervalMilliseconds) ||
            intervalMilliseconds is < 0 or > 60_000)
        {
            throw new InvalidOperationException("The Live/Control Plane endurance interval is invalid.");
        }

        var reportPath = Environment.GetEnvironmentVariable(
            "BLUETUSK_LIVE_CONTROL_PLANE_ENDURANCE_REPORT");
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            throw new InvalidOperationException("The Live/Control Plane harness report path is required.");
        }
        return new Settings(duration, minimumCycles, intervalMilliseconds, reportPath);
    }

    private sealed record LiveRow(int Id, long Revision);

    private sealed record Settings(
        TimeSpan Duration,
        long MinimumCycles,
        int IntervalMilliseconds,
        string ReportPath);

    public sealed record HarnessReport(
        string RequestedDuration,
        string ActualDuration,
        long Cycles,
        long MinimumCycles,
        int LiveRowCount,
        int DeploymentCount,
        long LiveUpdates,
        long AuthoritativeChecks,
        long InventoryReads,
        long OperationExecutions,
        long AuditRecords,
        long MaximumCycleMilliseconds,
        long MaximumWorkingSetBytes,
        long AllocatedBytes,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections,
        DateTimeOffset StartedAt,
        DateTimeOffset CompletedAt);

    private sealed class CountingAuditStore : IControlPlaneAuditStore
    {
        private readonly object _sync = new();
        private long _count;
        private long _invalidTransitions;
        private Guid? _pendingOperation;

        public long Count => Interlocked.Read(ref _count);

        public long InvalidTransitions => Interlocked.Read(ref _invalidTransitions);

        public bool HasPendingOperation
        {
            get
            {
                lock (_sync)
                {
                    return _pendingOperation.HasValue;
                }
            }
        }

        public ValueTask AppendAsync(
            ControlPlaneAuditRecord record,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(record);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (record.Status == ControlPlaneAuditStatus.Requested)
                {
                    if (_pendingOperation.HasValue)
                    {
                        Interlocked.Increment(ref _invalidTransitions);
                    }
                    _pendingOperation = record.OperationId;
                }
                else if (record.Status == ControlPlaneAuditStatus.Succeeded)
                {
                    if (_pendingOperation != record.OperationId)
                    {
                        Interlocked.Increment(ref _invalidTransitions);
                    }
                    _pendingOperation = null;
                }
                else
                {
                    Interlocked.Increment(ref _invalidTransitions);
                }
            }
            Interlocked.Increment(ref _count);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingOperationHandler : IControlPlaneOperationHandler
    {
        private long _count;

        public long Count => Interlocked.Read(ref _count);

        public ValueTask ExecuteAsync(
            ControlPlaneOperationRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _count);
            return ValueTask.CompletedTask;
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(
    typeof(LiveControlPlaneEnduranceTests.HarnessReport))]
internal sealed partial class SourceGenerationContext : JsonSerializerContext;
