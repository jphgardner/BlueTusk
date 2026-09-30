using System.Data.Common;
using System.Diagnostics;
using BlueTusk.Data;
using BlueTusk.Events;
using BlueTusk.Projections.Live;
using BlueTusk.Replication;

namespace BlueTusk.Projections.LoadHarness;

internal sealed partial class Scenario
{
    private async Task<RecoveryObservation> PromoteUnderBacklogAsync(long pending, CancellationToken token)
    {
        var standbyConnection = Environment.GetEnvironmentVariable("BLUETUSK_PROJECTIONS_LOAD_STANDBY")
            ?? throw new InvalidOperationException("A labelled synchronous owned standby is required for promotion.");
        await using (var connection = await _source.OpenConnectionAsync(token))
        await using (var command = Sql.Command(connection, null, "SELECT current_setting('synchronous_commit')='remote_apply' AND EXISTS(SELECT 1 FROM pg_stat_replication WHERE application_name='bluetusk_load_standby' AND sync_state='sync' AND state='streaming')"))
        { Program.Check((bool)(await command.ExecuteScalarAsync(token))!, "remote_apply and actual synchronous standby before promotion"); }
        var beforeSystem = await IdentifyAsync(_source, token);
        var oldLease = _lease!;
        var oldCheckpoint = (await _store.ReadStateAsync(oldLease.Identity, token)).Checkpoint;
        Program.Check(pending > 0 && _operations.Values.All(static row => row.Committed != 0 && row.Projected == 0 && row.Inbox == 0), "actual acknowledged unapplied backlog before hard failover");
        await _snapshot!.DisposeAsync(); _snapshot = null;
        var promotion = Stopwatch.GetTimestamp();
        await FixtureDockerAsync("BLUETUSK_PROJECTIONS_LOAD_PRIMARY_CONTAINER", ["kill", "--signal", "KILL"], [], token);
        await FixtureDockerAsync("BLUETUSK_PROJECTIONS_LOAD_STANDBY_CONTAINER", ["exec"],
            ["gosu", "postgres", "pg_ctl", "-D", "/var/lib/postgresql/data/pgdata", "promote", "-w", "-t", "30"], token);
        var promotedSettings = new BlueTuskConnectionStringBuilder(standbyConnection)
        { MaximumPoolSize = _configuration.PoolSize, ApplicationName = "BlueTuskProjectionsLoadHarness" };
        _originalSource = _source; _source = BlueTuskDataSource.Create(promotedSettings.ConnectionString);
        await Sql.ExecuteAsync(_source, "ALTER SYSTEM SET synchronous_standby_names=''", token);
        await Sql.ExecuteAsync(_source, "ALTER SYSTEM SET synchronous_commit='on'", token);
        await Sql.ExecuteAsync(_source, "SELECT pg_reload_conf(); CHECKPOINT", token); // Exclusive fresh fixture only; updates timeline control evidence.
        _routing.Target = _source;
        var afterSystem = await IdentifyAsync(_source, token);
        Program.Check(beforeSystem.SystemIdentifier == afterSystem.SystemIdentifier && afterSystem.Timeline > beforeSystem.Timeline, "actual same-system physical timeline promotion");
        Program.Check((await _store.ReadStateAsync(oldLease.Identity, token)).Checkpoint == oldCheckpoint, "acknowledged target checkpoint preserved synchronously");
        await using (var connection = await _source.OpenConnectionAsync(token))
        await using (var command = Sql.Command(connection, null, $"SELECT (SELECT count(*) FROM \"{_schema}\".operations),(SELECT count(*) FROM \"{_eventsSchema}\".outbox),(SELECT count(*) FROM \"{_eventsSchema}\".effects)"))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            Program.Check(await reader.ReadAsync(token) && reader.GetInt64(0) == pending && reader.GetInt64(1) == pending && reader.GetInt64(2) == 0, "all acknowledged source commits preserved, backlog still pending");
        }
        var promotionMilliseconds = Stopwatch.GetElapsedTime(promotion).TotalMilliseconds;
        var rebuild = Stopwatch.GetTimestamp();
        _identity = new(afterSystem.SystemIdentifier, afterSystem.DatabaseName!, _slot + "_rebuild", _publication);
        var lineage = await PostgreSqlProjectionLineage.CaptureAsync(_source, _identity, [_publication], token);
        var changedTimeline = await PostgreSqlProjectionLineage.CaptureAsync(_source, oldLease.Identity.Source, [_publication], token);
        var oldLineageRejected = false;
        try { await _store.RegisterWithLineageAsync(oldLease.Identity, changedTimeline, token); }
        catch (InvalidOperationException) { oldLineageRejected = true; }
        Program.Check(oldLineageRejected, "old source/catalogue binding cannot silently change");
        var candidate = new ProjectionIdentity("orders", 2, "load-orders-v2", _identity);
        await _store.RegisterWithLineageAsync(candidate, lineage, token);
        _lease = await _store.AcquireAsync(candidate, "physical-rebuild", TimeSpan.FromHours(2), token)
            ?? throw new InvalidOperationException("Recovery candidate lease unavailable.");
        _definition = new(candidate) { DependencyPageSize = 17 };
        var evidence = await PostgreSqlProjectionLineage.CaptureForCutoverAsync(_source, _identity, [_publication], token);
        var recoveryId = Guid.NewGuid();
        await _store.BeginRecoveryAsync(candidate, 1, evidence, recoveryId, "Synchronous hard physical promotion with acknowledged retained backlog", token);
        Program.Check(!await _store.RenewAsync(oldLease, TimeSpan.FromMinutes(1), token)
            && await _store.AcquireAsync(oldLease.Identity, "stale-owner", TimeSpan.FromMinutes(1), token) is null, "old projection writer cannot renew/reacquire after operator fence");
        _snapshot = await SnapshotSource(_source, _identity, lineage).BeginAttemptAsync(null, token);
        var consumer = new StreamsProjectionConsumer(_store, _lease, _definition);
        await PopulateSnapshotAsync(consumer, _snapshot, token);
        var projected = Stopwatch.GetTimestamp();
        foreach (var row in _operations.Values)
        { Interlocked.CompareExchange(ref row.Projected, projected, 0); _livePending[row.Tenant].Enqueue(row); }
        // Existing outbox rows are covered by the fresh snapshot, but their inbox effects must be
        // repaired separately from durable ordered Events replay. Preserve all identity tombstones.
        for (var index = 0; index < _configuration.Tenants; index++)
        {
            var stream = new EventStreamKey(Tenant(index), "load");
            var replay = await _events.AcquireReplayAsync("load-wal", stream, "physical-repair", TimeSpan.FromMinutes(10), token)
                ?? throw new InvalidOperationException("Bounded outbox repair lease unavailable.");
            EventReplayResult result;
            do
            {
                var checkpoint = await _events.ReadCheckpointAsync("load-wal", stream, token);
                var page = await _events.ReadAsync(stream, checkpoint, 64, 1_048_576, token);
                result = await _events.ReplayAsync(replay, HandleAsync, 64, 1_048_576, token);
                Interlocked.Add(ref _inboxEffects, result.HandledCount);
                var inbox = Stopwatch.GetTimestamp();
                foreach (var value in page)
                {
                    Program.Check(_operations.TryGetValue(value.EventId, out var row), "repair retains accepted operation identity");
                    if (Interlocked.CompareExchange(ref row!.Inbox, inbox, 0) == 0) { Interlocked.Increment(ref _deliveredByTenant[row.Tenant]); }
                }
            } while (!result.ReachedEnd);
            Program.Check(result.Checkpoint == _operations.Values.LongCount(row => row.Tenant == index), "repair checkpoint covers each tenant backlog without gaps");
            Program.Check(await _events.ReleaseReplayAsync(replay, token), "release durable repair owner");
            var replacement = await _events.AcquireReplayAsync("load-wal", stream, "repair-recovered", TimeSpan.FromMinutes(10), token)
                ?? throw new InvalidOperationException("Repair replacement lease unavailable.");
            var fenced = false;
            try { await _events.ReplayAsync(replay, HandleAsync, 64, 1_048_576, token); }
            catch (EventReplayFencedException) { fenced = true; }
            Program.Check(fenced && (await _events.ReplayAsync(replacement, HandleAsync, 64, 1_048_576, token)).HandledCount == 0, "stale repair owner rejected and exact replay is idempotent");
        }
        var finalEvidence = await PostgreSqlProjectionLineage.CaptureForCutoverAsync(_source, _identity, [_publication], token);
        _walReader = _snapshot.CreateChangeStream().ReadTransactionsAsync(_walCancellation!.Token).GetAsyncEnumerator(_walCancellation.Token);
        {
            var covered = false;
            for (var count = 0; count < 1024; count++)
            {
                Program.Check(await _walReader.MoveNextAsync(), "recovery WAL retained through verified barrier");
                await using var delivery = _walReader.Current;
                await consumer.ConsumeTransactionAsync(delivery, token);
                Interlocked.Increment(ref _walTransactions);
                if (delivery.Transaction.CommitEndPosition >= finalEvidence.BarrierPosition) { covered = true; break; }
            }
            Program.Check(covered, "bounded verified cutover coverage");
        }
        var strictRejected = false;
        try { await _store.PromoteWithLineageAsync(_lease, finalEvidence.BarrierPosition, 1, finalEvidence, token); }
        catch (InvalidOperationException) { strictRejected = true; }
        Program.Check(strictRejected, "ordinary cutover cannot bypass explicit changed-timeline operator recovery");
        await _store.CompleteRecoveryAsync(_lease, recoveryId, finalEvidence, token);
        await _store.CompleteRecoveryAsync(_lease, recoveryId, finalEvidence, token);
        Program.Check((await _store.ReadRecoveryAsync("orders", recoveryId, token))!.IsComplete, "durable operator cutover exact retry");
        // Route the existing old publisher objects to the promoted database, then replace their
        // expired leases. A failed socket is insufficient evidence of durable stale-owner fencing.
        var replayStore = new PostgreSqlProjectionLiveReplayStore(_routing, new() { Schema = _schema });
        var oldWriterRejected = true;
        foreach (var old in _live)
        {
            var owner = await replayStore.AcquireAsync(old.Identity, "promotion-owner-probe", TimeSpan.FromMinutes(1), token);
            Program.Check(owner is not null, "expired owned Live lease replaceable on promoted target");
            var rejected = false;
            try { await old.RefreshAsync(token); } catch (ProjectionLivePublisherFencedException) { rejected = true; }
            oldWriterRejected &= rejected;
            await owner!.DisposeAsync();
        }
        Program.Check(oldWriterRejected, "stale Live owner rejected on actually routed promoted database");
        // Durable writer fencing intentionally closes old connections; their already-counted
        // deliveries must still be decoded, without requiring the fenced connection to stay open.
        await DrainSubscriberFramesAsync(token, requireConnected: false);
        _retiredFanOutFrames += _live.Sum(static subscription => subscription.Status.FanOutDeliveries);
        await _readers.CancelAsync();
        foreach (var client in _clients) { await client.DisposeAsync(); }
        await Task.WhenAll(_clientReaders);
        foreach (var old in _live) { await old.DisposeAsync(); }
        _clients.Clear(); _clientReaders.Clear(); _live.Clear(); _readers.Dispose(); _readers = new(); _recovered = true;
        await StartLiveAsync(token);
        var reset = _clients.All(static client => client.Replay.Any(static entry => entry.Kind == BlueTusk.Live.LiveEventKind.ResultReset));
        Program.Check(reset, "replacement Live publisher reconnect provides authoritative reset");
        var retentionCalls = 0;
        long pruned = 0;
        ProjectionRetentionResult retention;
        do
        {
            Program.Check(retentionCalls++ < 512, "bounded retired-version cleanup calls");
            retention = await _store.PruneRetiredAsync(oldLease.Identity, 37, token);
            Program.Check(retention.DeletedRows <= 37, "retired derived rows obey total per-transaction deletion bound");
            pruned += retention.DeletedRows;
        } while (retention.HasRemainingRows);
        Program.Check(!await _store.RenewAsync(oldLease, TimeSpan.FromMinutes(1), token), "retired identity fence survives derived-state pruning");
        await using (var connection = await _source.OpenConnectionAsync(token))
        await using (var command = Sql.Command(connection, null, $"SELECT (SELECT count(*) FROM \"{_eventsSchema}\".outbox),(SELECT count(*) FROM \"{_eventsSchema}\".inbox),(SELECT count(*) FROM \"{_eventsSchema}\".effects)"))
        await using (var reader = await command.ExecuteReaderAsync(token))
        { Program.Check(await reader.ReadAsync(token) && reader.GetInt64(0) == pending && reader.GetInt64(1) == pending && reader.GetInt64(2) == pending, "permanent event/inbox identities survive safe derived retention"); }
        _processor = new(_routing, _events, new(_eventsSchema, _eventLimit), "load-wal", _identity,
            new() { MaximumEvents = 64, MaximumPayloadBytes = 1_048_576, MaximumSourceChanges = 256 });
        return new("Hard-fenced synchronous standby promotion; explicit fresh-snapshot/retained-WAL operator rebuild; preserved outbox repaired with bounded durable Events replay; recovery is not transparent.",
            beforeSystem.Timeline, afterSystem.Timeline, pending, pending, promotionMilliseconds, Stopwatch.GetElapsedTime(rebuild).TotalMilliseconds,
            oldWriterRejected, oldLineageRejected && strictRejected, reset, true, pruned, retentionCalls);
    }

    private static async Task<BlueTuskReplicationSystemIdentity> IdentifyAsync(BlueTuskDataSource source, CancellationToken token)
    { await using var replication = await BlueTuskLogicalReplicationConnection.OpenAsync(source.CreateDedicatedSessionOptions(), token); return await replication.IdentifySystemAsync(token); }

    private static async Task FixtureDockerAsync(string variable, string[] before, string[] after, CancellationToken token)
    {
        var name = Environment.GetEnvironmentVariable(variable) ?? throw new InvalidOperationException("Owned physical fixture container missing.");
        var fixture = Environment.GetEnvironmentVariable("BLUETUSK_PROJECTIONS_LOAD_FIXTURE") ?? throw new InvalidOperationException("Owned fixture label missing.");
        var labels = await DockerAsync(["inspect", name, "--format", "{{index .Config.Labels \"bluetusk.owner\"}}|{{index .Config.Labels \"bluetusk.fixture\"}}"], token);
        Program.Check(labels.Trim() == "bluetusk.projections.load|" + fixture, "physical action targets only this labelled disposable fixture");
        await DockerAsync([.. before, name, .. after], token);
    }

    private static async Task<string> DockerAsync(string[] arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("BLUETUSK_PROJECTIONS_LOAD_DOCKER") ?? "docker")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Owned Docker operation did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(token); var stderr = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token); var result = await stdout; await stderr;
        if (process.ExitCode != 0) { throw new InvalidOperationException("Owned Docker operation failed; sensitive stderr omitted."); }
        return result;
    }

    private sealed class RoutedDataSource(BlueTuskDataSource initial) : DbDataSource
    {
        private BlueTuskDataSource _target = initial;
        internal BlueTuskDataSource Target { set => Volatile.Write(ref _target, value); }
        public override string ConnectionString => "Owned harness endpoint; credentials omitted";
        protected override DbConnection CreateDbConnection() => Volatile.Read(ref _target).CreateConnection();
    }
}
