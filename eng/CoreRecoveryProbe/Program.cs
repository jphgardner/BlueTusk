using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlueTusk.ControlPlane;
using BlueTusk.Data;
using BlueTusk.Live;
using BlueTusk.Live.DependencyInjection;
using BlueTusk.Streams;
using BlueTusk.Streams.Storage.PostgreSql;
using BlueTusk.Streams.Testing;
using BlueTusk.Sync;
using BlueTusk.Sync.PostgreSql;
using BlueTusk.TypeSystem;

// Core recovery-rehearsal probe. Each invocation is one phase of an application that uses the
// Provider, Streams (durable checkpoint and fenced lease), Live (PostgreSQL replay store, shared
// subscription and signed resume tokens), Control Plane (managed desired state and fenced leases)
// and Sync (PostgreSQL destination checkpoint) against one PostgreSQL database.
//
//   seed     creates the durable state with the binary under test;
//   advance  takes over from the previous binary, reconciles every prior record, proves stale
//            owners are fenced, then writes a further acknowledged workload;
//   verify   the same takeover and reconciliation, used after a rollback or a restore.
//
// The phase report records every check and observation; the process exits non-zero when any check
// fails. Nothing is assumed: every value in the report was read back from PostgreSQL.
if (args.Length != 3 || args[0] is not ("seed" or "advance" or "verify"))
{
    Console.Error.WriteLine("Usage: CoreRecoveryProbe <seed|advance|verify> <state.json> <report.json>");
    return 2;
}

var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_REHEARSAL_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("BLUETUSK_REHEARSAL_CONNECTION_STRING must name the owned rehearsal database.");
    return 2;
}

var probe = new RecoveryProbe(args[0], Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), connectionString);
return await probe.RunAsync();

internal sealed record OrderRow(long Id, string Phase);

internal sealed class LeaseRecord
{
    public string Owner { get; set; } = "";

    public long FencingToken { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}

internal sealed class OrderRange
{
    public string Phase { get; set; } = "";

    public long FirstId { get; set; }

    public long LastId { get; set; }
}

/// <summary>Durable expectations handed from one phase (and binary) to the next.</summary>
internal sealed class ProbeState
{
    public int CompletedPhases { get; set; }

    public List<string> PhaseVersions { get; set; } = [];

    public List<OrderRange> Orders { get; set; } = [];

    public DateTimeOffset? LastAcknowledgedUtc { get; set; }

    public ulong StreamsPosition { get; set; }

    public long StreamsGeneration { get; set; }

    public LeaseRecord? StreamsLease { get; set; }

    public string LiveKey { get; set; } = "";

    public string? LiveResumeToken { get; set; }

    public long LiveSequence { get; set; }

    public string? LiveReplayHash { get; set; }

    public long DeploymentGeneration { get; set; }

    public long DeploymentRevision { get; set; }

    public LeaseRecord? DeploymentLease { get; set; }

    public ulong SyncPosition { get; set; }

    public uint SyncTransactionId { get; set; }

    public long SyncDocuments { get; set; }
}

internal sealed class RecoveryProbe(string phase, string statePath, string reportPath, string connectionString)
{
    private const string ConsumerGroup = "rehearsal-consumer";
    private const string PipelineId = "rehearsal-orders";
    private const string DeploymentId = "rehearsal";
    private const int OrdersPerPhase = 100;
    private const int SyncDocumentsPerPhase = 10;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly ChangeSourceIdentity StreamsSource =
        new("rehearsal-system", "rehearsal", "rehearsal_slot", "rehearsal:orders");
    private static readonly ChangeSourceIdentity SyncSource =
        new("rehearsal-system", "rehearsal", "rehearsal_sync_slot", "rehearsal:orders");
    private static readonly SyncTransformVersion Transform = SyncTransformVersion.Create("orders", "v1");

    private readonly SortedDictionary<string, bool> _checks = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, object?> _observations = new(StringComparer.Ordinal);
    private readonly List<string> _failures = [];
    private readonly string _owner = phase + "-" + Guid.NewGuid().ToString("N")[..8];

    public async Task<int> RunAsync()
    {
        var started = DateTimeOffset.UtcNow;
        if (File.Exists(reportPath))
        {
            Console.Error.WriteLine("Every phase needs a fresh report path.");
            return 2;
        }

        var state = phase == "seed" ? null : await ReadStateAsync();
        if (phase == "seed" && File.Exists(statePath))
        {
            Console.Error.WriteLine("A seed phase requires a fresh state path.");
            return 2;
        }

        if (phase != "seed" && state is null)
        {
            Console.Error.WriteLine("This phase requires the state written by an earlier phase.");
            return 2;
        }

        state ??= new ProbeState { LiveKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
        var version = PackageVersion(typeof(BlueTuskDataSource));
        _observations["packageVersion"] = version;
        _observations["assemblyVersions"] = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["BlueTusk.Data"] = InformationalVersion(typeof(BlueTuskDataSource)),
            ["BlueTusk.Streams"] = InformationalVersion(typeof(ChangeStreamCheckpoint)),
            ["BlueTusk.Streams.Storage.PostgreSql"] = InformationalVersion(typeof(PostgreSqlChangeStreamStateStore)),
            ["BlueTusk.Live"] = InformationalVersion(typeof(LiveResumeTokenProtector)),
            ["BlueTusk.Live.DependencyInjection"] = InformationalVersion(typeof(PostgreSqlLiveInvalidationStore)),
            ["BlueTusk.ControlPlane"] = InformationalVersion(typeof(PostgreSqlManagedDeploymentStore)),
            ["BlueTusk.Sync"] = InformationalVersion(typeof(SyncTransformVersion)),
            ["BlueTusk.Sync.PostgreSql"] = InformationalVersion(typeof(PostgreSqlSyncDestination)),
        };

        await using var dataSource = BlueTuskDataSource.Create(connectionString);
        var streams = new PostgreSqlChangeStreamStateStore(new PostgreSqlStreamsStorageOptions { ControlDataSource = dataSource });
        var live = new PostgreSqlLiveInvalidationStore(new PostgreSqlLiveStoreOptions
        {
            ControlDataSource = dataSource,
            ReplayRetentionWindow = TimeSpan.FromDays(7),
        });
        var control = new PostgreSqlManagedDeploymentStore(dataSource);
        var sync = new PostgreSqlSyncDestination(new PostgreSqlSyncOptions { DestinationDataSource = dataSource });

        // Opening every store against an existing database is itself the version-compatibility
        // check: each store refuses a schema or durable format newer than it understands.
        await CheckAsync("schemaInitialized", async () =>
        {
            await ExecuteAsync(dataSource, "CREATE SCHEMA IF NOT EXISTS rehearsal");
            await ExecuteAsync(
                dataSource,
                "CREATE TABLE IF NOT EXISTS rehearsal.orders (id bigint PRIMARY KEY, phase text NOT NULL, payload text NOT NULL, created_utc timestamptz NOT NULL DEFAULT clock_timestamp())");
            await streams.InitializeAsync();
            await live.InitializeAsync();
            await control.InitializeAsync();
            var provisioned = await sync.ProvisionAsync(new SyncProvisionRequest(PipelineId, SyncSource, Transform));
            Require(provisioned.Status == SyncProvisionStatus.Ready, $"Sync provisioning returned {provisioned.Status}.");
            return true;
        });

        if (phase != "seed")
        {
            await CheckAsync("checkpointReadableBefore", async () => { _observations["checkpointBefore"] = await ReadCheckpointAsync(dataSource, streams, control); return true; });
            await ReconcileOrdersAsync(dataSource, state);
            await ReconcileStreamsAsync(streams, state);
            await ReconcileControlPlaneAsync(control, state);
            await ReconcileSyncAsync(dataSource, sync, state);
        }

        await WriteOrdersAsync(dataSource, state);
        await AdvanceStreamsAsync(streams, state);
        await AdvanceLiveAsync(dataSource, live, state);
        await AdvanceControlPlaneAsync(control, state, version);
        await AdvanceSyncAsync(dataSource, sync, state);
        await CheckAsync("checkpointReadableAfter", async () => { _observations["checkpointAfter"] = await ReadCheckpointAsync(dataSource, streams, control); return true; });

        state.CompletedPhases++;
        state.PhaseVersions.Add(phase + "@" + version);
        var passed = _failures.Count == 0 && _checks.Values.All(static value => value);
        if (passed)
        {
            await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(state, Json));
        }

        var report = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schemaVersion"] = 1,
            ["phase"] = phase,
            ["owner"] = _owner,
            ["startedUtc"] = started,
            ["completedUtc"] = DateTimeOffset.UtcNow,
            ["passed"] = passed,
            ["checks"] = _checks,
            ["observations"] = _observations,
            ["failures"] = _failures,
        };
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, Json));
        Console.WriteLine($"{phase} with BlueTusk {version}: {(passed ? "passed" : "FAILED")} ({_checks.Count} checks).");
        foreach (var failure in _failures)
        {
            Console.Error.WriteLine(failure);
        }

        return passed ? 0 : 3;
    }

    private async Task ReconcileOrdersAsync(BlueTuskDataSource dataSource, ProbeState state)
    {
        long present = 0;
        long mismatches = 0;
        long lastId = state.Orders.Count == 0 ? 0 : state.Orders[^1].LastId;
        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var command = new BlueTuskCommand("SELECT id, phase, payload FROM rehearsal.orders ORDER BY id", connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var id = reader.GetInt64(0);
                var range = state.Orders.FirstOrDefault(candidate => id >= candidate.FirstId && id <= candidate.LastId);
                if (range is null)
                {
                    mismatches++;
                    continue;
                }

                present++;
                if (!string.Equals(reader.GetString(1), range.Phase, StringComparison.Ordinal) ||
                    !string.Equals(reader.GetString(2), Payload(id, range.Phase), StringComparison.Ordinal))
                {
                    mismatches++;
                }
            }
        }

        var missing = lastId - present;
        _observations["ordersAcknowledgedBefore"] = lastId;
        _observations["ordersPresentBefore"] = present;
        _observations["missingAcknowledgedOrders"] = missing;
        _observations["orderIntegrityMismatches"] = mismatches;
        Record("ordersReconciled", missing == 0 && mismatches == 0,
            $"{missing} acknowledged orders are missing and {mismatches} rows differ from what was acknowledged.");
    }

    private async Task ReconcileStreamsAsync(PostgreSqlChangeStreamStateStore streams, ProbeState state)
    {
        await CheckAsync("streamsCheckpointReconciled", async () =>
        {
            var key = ChangeStreamStateKey.Create(StreamsSource, ConsumerGroup);
            var current = await streams.ReadAsync(key) ?? throw new InvalidOperationException("The Streams checkpoint is missing.");
            _observations["streamsPositionBefore"] = current.AcknowledgedCommitPosition.Value;
            Require(current.FormatVersion == ChangeStreamCheckpoint.CurrentFormatVersion, "The Streams checkpoint format changed.");
            Require(current.AcknowledgedCommitPosition.Value == state.StreamsPosition &&
                current.StoreGeneration == state.StreamsGeneration,
                $"The Streams checkpoint is {current.AcknowledgedCommitPosition.Value}/{current.StoreGeneration}, expected {state.StreamsPosition}/{state.StreamsGeneration}.");
            return true;
        });
    }

    private async Task ReconcileControlPlaneAsync(PostgreSqlManagedDeploymentStore control, ProbeState state)
    {
        await CheckAsync("controlPlaneReconciled", async () =>
        {
            var deployment = await control.GetAsync(DeploymentId) ?? throw new InvalidOperationException("The managed deployment is missing.");
            _observations["deploymentGenerationBefore"] = deployment.Spec.Generation;
            _observations["deploymentRevisionBefore"] = deployment.Status.Revision;
            Require(deployment.Spec.Generation == state.DeploymentGeneration &&
                deployment.Status.Revision == state.DeploymentRevision &&
                deployment.Status.State == ManagedDeploymentState.Ready,
                "The managed deployment desired or observed state differs from the last acknowledged state.");
            return true;
        });
    }

    private async Task ReconcileSyncAsync(BlueTuskDataSource dataSource, PostgreSqlSyncDestination sync, ProbeState state)
    {
        await CheckAsync("syncReconciled", async () =>
        {
            var position = await ReadSyncPositionAsync(dataSource);
            var documents = await sync.CountAsync(PipelineId, "orders");
            _observations["syncPositionBefore"] = position;
            _observations["syncDocumentsBefore"] = documents;
            Require(position == state.SyncPosition && documents == state.SyncDocuments,
                $"Sync is at {position} with {documents} documents, expected {state.SyncPosition} with {state.SyncDocuments}.");
            return true;
        });
        await CheckAsync("syncIdempotent", async () =>
        {
            // Redelivering the last acknowledged transaction must not apply it twice.
            await using var delivery = ChangeDeliveryTestFactory.CreateCommitted(
                SyncSource,
                state.SyncTransactionId,
                new BlueTuskLogSequenceNumber(state.SyncPosition));
            var result = await sync.ApplyTransactionAsync(SyncBatch(delivery, state.CompletedPhases - 1, state.SyncTransactionId, state.SyncPosition));
            Require(result.Status == SyncApplyStatus.AlreadyApplied, $"Redelivery returned {result.Status}.");
            return true;
        });
    }

    private async Task WriteOrdersAsync(BlueTuskDataSource dataSource, ProbeState state)
    {
        await CheckAsync("ordersWritable", async () =>
        {
            var first = (state.Orders.Count == 0 ? 0 : state.Orders[^1].LastId) + 1;
            var range = new OrderRange { Phase = phase + "-" + state.CompletedPhases.ToString(CultureInfo.InvariantCulture), FirstId = first, LastId = first - 1 };
            state.Orders.Add(range);
            await using var connection = await dataSource.OpenConnectionAsync();
            for (var id = first; id < first + OrdersPerPhase; id++)
            {
                await using var command = new BlueTuskCommand(
                    "INSERT INTO rehearsal.orders (id, phase, payload) VALUES (@id, @phase, @payload)",
                    connection);
                command.Parameters.Add(new BlueTuskParameter(id) { ParameterName = "id" });
                command.Parameters.Add(new BlueTuskParameter(range.Phase) { ParameterName = "phase" });
                command.Parameters.Add(new BlueTuskParameter(Payload(id, range.Phase)) { ParameterName = "payload" });
                Require(await command.ExecuteNonQueryAsync() == 1, "An order insert was not acknowledged.");
                range.LastId = id;
                state.LastAcknowledgedUtc = DateTimeOffset.UtcNow;
            }

            _observations["ordersAcknowledgedAfter"] = range.LastId;
            _observations["lastAcknowledgedUtc"] = state.LastAcknowledgedUtc;
            return true;
        });
    }

    private async Task AdvanceStreamsAsync(PostgreSqlChangeStreamStateStore streams, ProbeState state)
    {
        await CheckAsync(phase == "seed" ? "streamsCheckpointWritable" : "streamsOwnershipFenced", async () =>
        {
            var key = ChangeStreamStateKey.Create(StreamsSource, ConsumerGroup);
            var acquired = await streams.AcquireAsync(key, _owner, TimeSpan.FromMinutes(10));
            Require(acquired.Status == ChangeLeaseAcquireStatus.Acquired && acquired.Lease is not null,
                $"The Streams checkpoint lease was {acquired.Status}; the previous owner did not drain.");
            var lease = acquired.Lease!;
            var current = await streams.ReadAsync(key);
            var generation = current?.StoreGeneration ?? -1;
            var target = PhasePosition(state.CompletedPhases);
            if (state.StreamsLease is { } stale)
            {
                Require(lease.FencingToken > stale.FencingToken, "The new Streams owner did not receive a higher fencing token.");
                var staleLease = new ChangeStreamLease(key, stale.Owner, stale.FencingToken, stale.ExpiresAt);
                var staleWrite = await streams.CompareExchangeAsync(
                    key,
                    generation,
                    current!.MoveTo(new BlueTuskLogSequenceNumber(target), generation + 1),
                    staleLease);
                _observations["staleStreamsOwnerWrite"] = staleWrite.Status.ToString();
                Require(staleWrite.Status == ChangeCheckpointWriteStatus.Fenced, $"A stale Streams owner write returned {staleWrite.Status}.");
            }

            var next = (current ?? ChangeStreamCheckpoint.CreateInitial(StreamsSource, "rehearsal-db", "pgoutput", "rehearsal-mapping-v1"))
                .MoveTo(new BlueTuskLogSequenceNumber(target), generation + 1);
            var write = await streams.CompareExchangeAsync(key, generation, next, lease);
            Require(write.Status == ChangeCheckpointWriteStatus.Stored, $"The Streams checkpoint write returned {write.Status}.");
            var stored = await streams.ReadAsync(key) ?? throw new InvalidOperationException("The stored checkpoint vanished.");
            state.StreamsPosition = stored.AcknowledgedCommitPosition.Value;
            state.StreamsGeneration = stored.StoreGeneration;
            state.StreamsLease = new LeaseRecord { Owner = lease.OwnerId, FencingToken = lease.FencingToken, ExpiresAt = lease.ExpiresAt };
            _observations["streamsFencingToken"] = lease.FencingToken;
            _observations["streamsPositionAfter"] = state.StreamsPosition;

            // Graceful drain: the owner releases its lease before the process stops.
            Require(await streams.ReleaseAsync(lease), "The Streams lease could not be released during drain.");
            return true;
        });
    }

    private async Task AdvanceLiveAsync(BlueTuskDataSource dataSource, PostgreSqlLiveInvalidationStore live, ProbeState state)
    {
        var plan = new LiveQueryPlan<OrderRow, long>(
            "rehearsal-orders",
            "rehearsal",
            Convert.ToHexStringLower(SHA256.HashData("rehearsal.orders:latest:v1"u8)),
            LiveQueryCapabilities.SingleTable |
                LiveQueryCapabilities.TenantFilter |
                LiveQueryCapabilities.DeterministicOrdering |
                LiveQueryCapabilities.BoundedTake,
            [new LiveTableDependency("rehearsal", "orders")],
            [],
            50,
            async (_, cancellationToken) => await ReadLatestOrdersAsync(dataSource, cancellationToken),
            static row => row.Id);
        await using var session = new LiveQuerySession<OrderRow, long>(
            plan,
            LiveQueryArguments.Create([], new Dictionary<string, object?>()),
            new LiveSecurityScope("tenant:rehearsal", "policy:v1"),
            live);
        await using var shared = new LiveSharedSubscription<OrderRow, long>(session, live);
        var protector = new LiveResumeTokenProtector([new LiveResumeTokenKey("rehearsal", Convert.FromBase64String(state.LiveKey), isPrimary: true)]);

        if (phase != "seed")
        {
            await CheckAsync("liveReplayIntegrity", async () =>
            {
                var replay = await live.ReadAsync(shared.Identity, 0, 10_000);
                Require(replay.Status is LiveReplayReadStatus.Available or LiveReplayReadStatus.Current,
                    $"The persisted Live replay is {replay.Status}.");
                var prefix = replay.Events.Where(item => item.Sequence <= state.LiveSequence).ToArray();
                Require(prefix.Length == state.LiveSequence && prefix.All(LiveReplayJsonSerializer.VerifyIntegrity) &&
                    string.Equals(ReplayHash(prefix), state.LiveReplayHash, StringComparison.Ordinal),
                    "The persisted Live replay differs from the acknowledged replay.");
                _observations["liveSequenceBefore"] = replay.LastSequence;
                return true;
            });
        }

        var started = await CheckAsync("liveStarted", async () =>
        {
            await shared.StartAsync();
            return true;
        });
        if (!started)
        {
            // Nothing else can be served to clients when the shared subscription cannot start.
            Record("liveClientResumedOrReset", false, "Live clients could not reconnect because the shared subscription did not start.");
            return;
        }

        if (state.LiveResumeToken is { } token)
        {
            await CheckAsync("liveClientResumedOrReset", async () =>
            {
                // A client that was connected to the previous binary reconnects with its signed token.
                var result = await shared.ConnectWithTokenAsync(token, protector);
                _observations["liveReconnectStatus"] = result.Status.ToString();
                _observations["liveReconnectTokenStatus"] = result.TokenStatus?.ToString();
                Require(result.Status == LiveSubscriptionConnectStatus.Connected && result.Connection is not null,
                    $"A client holding the previous binary's resume token was refused: {result.Status}.");
                await using var connection = result.Connection!;
                var replay = connection.Replay;
                var contiguous = replay.Select((item, index) => item.Sequence == state.LiveSequence + 1 + index).All(static value => value);
                Require(contiguous && replay.All(LiveReplayJsonSerializer.VerifyIntegrity),
                    "The resumed client did not receive a contiguous, integrity-checked replay.");
                var reset = replay.Any(static item => item.Kind.ToString().Contains("Reset", StringComparison.Ordinal));
                _observations["liveClientOutcome"] = reset ? "explicit-reset" : "resumed";
                _observations["liveReplayedEvents"] = replay.Count;
                return true;
            });
        }

        await CheckAsync("liveFreshClientConnected", async () =>
        {
            var fresh = await shared.ConnectAsync(0);
            Require(fresh.Status == LiveSubscriptionConnectStatus.Connected && fresh.Connection is not null,
                $"A fresh Live client was refused: {fresh.Status}.");
            await using var connection = fresh.Connection!;
            var sequence = shared.Status.PersistedSequence;
            var replay = await live.ReadAsync(shared.Identity, 0, 10_000);
            var events = replay.Events.Where(item => item.Sequence <= sequence).ToArray();
            state.LiveSequence = sequence;
            state.LiveReplayHash = ReplayHash(events);
            state.LiveResumeToken = protector.Protect(shared.Identity, sequence, TimeSpan.FromHours(2));
            _observations["liveSequenceAfter"] = sequence;
            return true;
        });
    }

    private async Task AdvanceControlPlaneAsync(PostgreSqlManagedDeploymentStore control, ProbeState state, string version)
    {
        await CheckAsync(phase == "seed" ? "controlPlaneWritable" : "controlPlaneFencingEnforced", async () =>
        {
            var existing = await control.GetAsync(DeploymentId);
            var generation = (existing?.Spec.Generation ?? 0) + 1;
            var spec = new ManagedDeploymentSpec(
                DeploymentId,
                "tenant-rehearsal",
                "kubernetes",
                "local",
                generation,
                Paused: false,
                DeleteProtection: true,
                [
                    new ManagedWorkloadSpec(
                        ManagedWorkloadKind.Streams,
                        version,
                        new ManagedResourceRequest(1, 250, 256L * 1024 * 1024, 1024L * 1024 * 1024),
                        [new ManagedSecretReference("rehearsal", "postgres/rehearsal", "1")],
                        new Dictionary<string, string> { ["mode"] = "relay" }),
                ],
                new Dictionary<string, string> { ["environment"] = "rehearsal" });
            if (existing is null)
            {
                // A lease can only be taken on an existing deployment.
                await control.PutAsync(spec, 0);
            }

            var lease = await control.TryAcquireAsync(DeploymentId, _owner, TimeSpan.FromMinutes(10));
            Require(lease is not null, "The Control Plane reconciliation lease is still held; the previous owner did not drain.");
            if (state.DeploymentLease is { } stale && existing is not null)
            {
                Require(lease!.FencingToken > stale.FencingToken, "The new Control Plane owner did not receive a higher fencing token.");
                var staleLease = new ManagedDeploymentLease(DeploymentId, stale.Owner, stale.FencingToken, stale.ExpiresAt);
                var renewRejected = false;
                try
                {
                    await control.RenewAsync(staleLease, TimeSpan.FromMinutes(1));
                }
                catch (ManagedDeploymentLeaseException)
                {
                    renewRejected = true;
                }

                var statusRejected = false;
                try
                {
                    await control.UpdateStatusAsync(
                        DeploymentId,
                        Status(existing.Spec, existing.Status.Revision + 1, stale.FencingToken),
                        existing.Status.Revision);
                }
                catch (ManagedDeploymentConcurrencyException)
                {
                    statusRejected = true;
                }

                _observations["staleControlPlaneRenewRejected"] = renewRejected;
                _observations["staleControlPlaneStatusRejected"] = statusRejected;
                Require(renewRejected && statusRejected, "A stale Control Plane owner was not fenced.");
            }

            if (existing is not null)
            {
                await control.PutAsync(spec, generation - 1);
            }

            var stored = await control.GetAsync(DeploymentId) ?? throw new InvalidOperationException("The managed deployment vanished.");
            var updated = await control.UpdateStatusAsync(
                DeploymentId,
                Status(spec, stored.Status.Revision + 1, lease!.FencingToken),
                stored.Status.Revision);
            Require(updated.Status.State == ManagedDeploymentState.Ready && updated.Spec.Generation == generation,
                "The managed deployment did not converge.");
            state.DeploymentGeneration = updated.Spec.Generation;
            state.DeploymentRevision = updated.Status.Revision;
            state.DeploymentLease = new LeaseRecord { Owner = lease.Owner, FencingToken = lease.FencingToken, ExpiresAt = lease.ExpiresAt };
            _observations["deploymentGenerationAfter"] = state.DeploymentGeneration;
            _observations["controlPlaneFencingToken"] = lease.FencingToken;
            await control.ReleaseAsync(lease);
            return true;
        });
    }

    private async Task AdvanceSyncAsync(BlueTuskDataSource dataSource, PostgreSqlSyncDestination sync, ProbeState state)
    {
        await CheckAsync("syncApplied", async () =>
        {
            var transactionId = (uint)(100 + state.CompletedPhases);
            var position = PhasePosition(state.CompletedPhases) + 500;
            await using var delivery = ChangeDeliveryTestFactory.CreateCommitted(
                SyncSource,
                transactionId,
                new BlueTuskLogSequenceNumber(position));
            var result = await sync.ApplyTransactionAsync(SyncBatch(delivery, state.CompletedPhases, transactionId, position));
            Require(result.Status == SyncApplyStatus.Applied && result.DurablePosition?.Value == position,
                $"Sync apply returned {result.Status} at {result.DurablePosition?.Value}.");
            state.SyncPosition = await ReadSyncPositionAsync(dataSource);
            state.SyncTransactionId = transactionId;
            state.SyncDocuments = await sync.CountAsync(PipelineId, "orders");
            Require(state.SyncPosition == position, "The durable Sync checkpoint did not advance.");
            _observations["syncPositionAfter"] = state.SyncPosition;
            _observations["syncDocumentsAfter"] = state.SyncDocuments;
            return true;
        });
    }

    private static SyncTransactionBatch SyncBatch(ChangeTransactionDelivery delivery, int phaseIndex, uint transactionId, ulong position)
    {
        var lsn = new BlueTuskLogSequenceNumber(position);
        var mutations = Enumerable.Range(0, SyncDocumentsPerPhase).Select(index => new SyncMutation(
            new ChangeId(SyncSource, lsn, transactionId, index),
            SyncMutationKind.Upsert,
            "orders",
            string.Create(CultureInfo.InvariantCulture, $"p{phaseIndex}-{index}"),
            Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{{\"phase\":{phaseIndex},\"index\":{index}}}")),
            "application/json"));
        return new SyncTransactionBatch(PipelineId, Transform, delivery.Transaction, mutations);
    }

    private static ManagedDeploymentStatus Status(ManagedDeploymentSpec spec, long revision, long fencingToken) =>
        new(
            ManagedDeploymentState.Ready,
            spec.Generation,
            revision,
            fencingToken,
            ManagedDeploymentValidation.GetFingerprint(spec),
            "rehearsal-plan",
            "resource/rehearsal",
            null,
            DateTimeOffset.UtcNow);

    private static async Task<string> ReadCheckpointAsync(
        BlueTuskDataSource dataSource,
        PostgreSqlChangeStreamStateStore streams,
        PostgreSqlManagedDeploymentStore control)
    {
        var checkpoint = await streams.ReadAsync(ChangeStreamStateKey.Create(StreamsSource, ConsumerGroup));
        var deployment = await control.GetAsync(DeploymentId);
        var sync = await ReadSyncPositionAsync(dataSource);
        long orders;
        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var command = new BlueTuskCommand("SELECT coalesce(max(id), 0) FROM rehearsal.orders", connection))
        {
            orders = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"streams={checkpoint?.AcknowledgedCommitPosition.Value ?? 0}/{checkpoint?.StoreGeneration ?? -1};sync={sync};deployment={deployment?.Spec.Generation ?? 0}/{deployment?.Status.Revision ?? 0};orders={orders}");
    }

    private static async Task<ulong> ReadSyncPositionAsync(BlueTuskDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new BlueTuskCommand(
            "SELECT coalesce(checkpoint_position, 0) FROM bluetusk_sync.pipelines WHERE pipeline_id = @pipeline",
            connection);
        command.Parameters.Add(new BlueTuskParameter(PipelineId) { ParameterName = "pipeline" });
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? 0 : Convert.ToUInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<OrderRow>> ReadLatestOrdersAsync(BlueTuskDataSource dataSource, CancellationToken cancellationToken)
    {
        var rows = new List<OrderRow>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new BlueTuskCommand("SELECT id, phase FROM rehearsal.orders ORDER BY id DESC LIMIT 50", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new OrderRow(reader.GetInt64(0), reader.GetString(1)));
        }

        return rows;
    }

    private static async Task ExecuteAsync(BlueTuskDataSource dataSource, string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new BlueTuskCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<ProbeState?> ReadStateAsync() =>
        File.Exists(statePath)
            ? JsonSerializer.Deserialize<ProbeState>(await File.ReadAllTextAsync(statePath), Json)
            : null;

    private async Task<bool> CheckAsync(string name, Func<Task<bool>> check)
    {
        try
        {
            var passed = await check();
            Record(name, passed, $"{name} failed.");
            return passed;
        }
        catch (Exception exception)
        {
            Record(name, false, $"{name}: {exception.GetType().FullName}: {exception.Message}");
            return false;
        }
    }

    private void Record(string name, bool passed, string failure)
    {
        _checks[name] = passed && (!_checks.TryGetValue(name, out var previous) || previous);
        if (!passed)
        {
            _failures.Add(failure);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static ulong PhasePosition(int phaseIndex) => (ulong)(phaseIndex + 1) * 1_000;

    private static string Payload(long id, string phaseName) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"order:{id}:{phaseName}"))))[..32];

    private static string ReplayHash(IEnumerable<LiveReplayEvent> events)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var item in events.OrderBy(static item => item.Sequence))
        {
            hash.AppendData(BitConverter.GetBytes(item.Sequence));
            hash.AppendData(item.IntegrityHash.Span);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string InformationalVersion(Type type) =>
        type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? type.Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private static string PackageVersion(Type type)
    {
        var informational = InformationalVersion(type);
        var separator = informational.IndexOf('+', StringComparison.Ordinal);
        return separator < 0 ? informational : informational[..separator];
    }
}
