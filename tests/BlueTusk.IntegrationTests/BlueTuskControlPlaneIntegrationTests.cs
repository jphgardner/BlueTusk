using System.Data.Common;
using BlueTusk.ControlPlane;
using BlueTusk.Data;
using BlueTusk.Streams;
using BlueTusk.Streams.Storage.PostgreSql;
using BlueTusk.TypeSystem;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

public sealed class BlueTuskControlPlaneIntegrationTests
{
    [Fact]
    public async Task Control_plane_reads_operational_state_and_audit_rows_are_immutable()
    {
        var connectionString = GetConnectionString();
        var relaySchema = "bluetusk_control_inventory_" + Guid.NewGuid().ToString("N");
        var auditSchema = "bluetusk_control_audit_" + Guid.NewGuid().ToString("N");
        var freshAuditSchema = "bluetusk_control_fresh_" + Guid.NewGuid().ToString("N");
        var slotName = "control_" + Guid.NewGuid().ToString("N")[..20];
        await using var dataSource = BlueTuskDataSource.Create(connectionString);
        try
        {
            await CreateLogicalSlotAsync(dataSource, slotName);
            var storageOptions = new PostgreSqlStreamsStorageOptions
            {
                ControlDataSource = dataSource,
                ControlSchema = relaySchema,
            };
            var relay = new PostgreSqlDurableChangeRelay(storageOptions);
            await relay.InitializeAsync();
            var identity = new ChangeSourceIdentity(
                "control-system",
                "bluetusk_tests",
                slotName,
                "public:orders");
            var source = await relay.RegisterSourceAsync(identity);
            var group = await relay.CreateConsumerGroupAsync(source, "relay-consumer");
            await SeedSnapshotAsync(dataSource, relaySchema, source);

            var stateStore = new PostgreSqlChangeStreamStateStore(storageOptions);
            var key = ChangeStreamStateKey.Create(identity, "direct-consumer");
            var lease = Assert.IsType<ChangeStreamLease>(
                (await stateStore.AcquireAsync(
                    key,
                    "direct-worker",
                    TimeSpan.FromMinutes(1))).Lease);
            var checkpoint = ChangeStreamCheckpoint.CreateInitial(
                    identity,
                    "control-database",
                    "pgoutput",
                    new string('a', 64))
                .MoveTo(new BlueTuskLogSequenceNumber(5), storeGeneration: 0);
            var stored = await stateStore.CompareExchangeAsync(key, -1, checkpoint, lease);
            Assert.Equal(ChangeCheckpointWriteStatus.Stored, stored.Status);

            var queries = new PostgreSqlControlPlaneQueryService(
                [new ControlPlanePostgreSqlSource(
                    "primary",
                    dataSource,
                    dataSource,
                    relaySchema)]);
            var overview = await queries.GetOverviewAsync();
            var observed = Assert.Single(overview.Sources);
            Assert.Equal("primary:" + identity.Fingerprint, observed.SourceKey);
            Assert.True(observed.Slot.SourceReachable);
            Assert.True(observed.Slot.Exists);
            Assert.False(observed.Slot.Active);
            Assert.Equal("pgoutput", observed.Slot.OutputPlugin);
            Assert.True(observed.Slot.WalLagBytes >= 0);
            Assert.Equal(group.Name, Assert.Single(observed.ConsumerGroups).Name);
            Assert.Equal("snapshot-1", Assert.Single(observed.SnapshotRuns).SnapshotEpoch);
            var direct = Assert.Single(observed.Checkpoints);
            Assert.Equal("direct-consumer", direct.ConsumerGroup);
            Assert.Equal("0/5", direct.AcknowledgedPosition);
            Assert.True(direct.IsLeased);

            await CreateLegacyAuditSchemaAsync(dataSource, auditSchema);
            var auditStore = new PostgreSqlControlPlaneAuditStore(dataSource, auditSchema);
            await auditStore.InitializeAsync();
            await auditStore.InitializeAsync();
            Assert.Equal(
                PostgreSqlControlPlaneAuditStore.CurrentSchemaVersion,
                await auditStore.GetSchemaVersionAsync());
            await auditStore.AppendAsync(
                new ControlPlaneAuditRecord(
                    Guid.NewGuid(),
                    DateTimeOffset.UtcNow,
                    "integration-operator",
                    ControlPlaneOperationKind.PauseSource,
                    observed.SourceKey,
                    ControlPlaneAuditStatus.Succeeded,
                    "Acceptance test",
                    null));
            await Assert.ThrowsAnyAsync<Exception>(
                () => ExecuteAsync(
                    dataSource,
                    $"UPDATE \"{auditSchema}\".audit_log SET status = 'Changed'").AsTask());
            await Assert.ThrowsAnyAsync<Exception>(
                () => ExecuteAsync(
                    dataSource,
                    $"DELETE FROM \"{auditSchema}\".audit_log").AsTask());
            Assert.Equal(
                2L,
                await ExecuteInt64Async(
                    dataSource,
                    $"SELECT COUNT(*) FROM \"{auditSchema}\".audit_log"));
            Assert.Equal(
                2L,
                await ExecuteInt64Async(
                    dataSource,
                    $"SELECT COUNT(*) FROM \"{auditSchema}\".audit_log " +
                    "WHERE record_format = " +
                    PostgreSqlControlPlaneAuditStore.CurrentRecordFormatVersion));

            var freshAuditStore = new PostgreSqlControlPlaneAuditStore(
                dataSource,
                freshAuditSchema);
            await freshAuditStore.InitializeAsync();
            Assert.Equal(
                PostgreSqlControlPlaneAuditStore.CurrentSchemaVersion,
                await freshAuditStore.GetSchemaVersionAsync());

            await ExecuteAsync(
                dataSource,
                $"""
                 UPDATE "{auditSchema}".storage_metadata
                 SET schema_version = {PostgreSqlControlPlaneAuditStore.CurrentSchemaVersion + 1}
                 WHERE singleton
                 """);
            var futureVersion = await Assert.ThrowsAsync<InvalidOperationException>(
                () => auditStore.InitializeAsync().AsTask());
            Assert.Contains("newer", futureVersion.Message, StringComparison.Ordinal);
        }
        finally
        {
            await DropLogicalSlotAsync(dataSource, slotName);
            await DropSchemaAsync(dataSource, relaySchema);
            await DropSchemaAsync(dataSource, auditSchema);
            await DropSchemaAsync(dataSource, freshAuditSchema);
        }
    }

    [Fact]
    public async Task Managed_hosting_store_persists_CAS_state_and_fenced_leases()
    {
        var schema = "bluetusk_managed_hosting_" + Guid.NewGuid().ToString("N");
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        try
        {
            var store = new PostgreSqlManagedDeploymentStore(dataSource, schema);
            await store.InitializeAsync();
            await store.InitializeAsync();
            Assert.Equal(
                PostgreSqlManagedDeploymentStore.CurrentSchemaVersion,
                await store.GetSchemaVersionAsync());

            var spec = new ManagedDeploymentSpec(
                "orders",
                "tenant-a",
                "kubernetes",
                "eu-west",
                1,
                Paused: false,
                DeleteProtection: true,
                [
                    new ManagedWorkloadSpec(
                        ManagedWorkloadKind.Streams,
                        "1.0.0",
                        new ManagedResourceRequest(
                            2,
                            500,
                            256 * 1024 * 1024,
                            1024 * 1024 * 1024),
                        [new ManagedSecretReference("vault", "postgres/orders", "7")],
                        new Dictionary<string, string> { ["mode"] = "relay" }),
                ],
                new Dictionary<string, string> { ["environment"] = "acceptance" });
            var created = await store.PutAsync(spec, expectedGeneration: 0);
            Assert.Equal(ManagedDeploymentState.Pending, created.Status.State);
            Assert.Equal("postgres/orders", Assert.Single(
                Assert.Single(created.Spec.Workloads).SecretReferences).Name);
            var listed = new List<ManagedDeployment>();
            await foreach (var deployment in store.ListAsync("tenant-a"))
            {
                listed.Add(deployment);
            }

            Assert.Single(listed);

            var firstLease = Assert.IsType<ManagedDeploymentLease>(
                await store.TryAcquireAsync(
                    spec.DeploymentId,
                    "worker-a",
                    TimeSpan.FromMinutes(1)));
            Assert.Null(
                await store.TryAcquireAsync(
                    spec.DeploymentId,
                    "worker-b",
                    TimeSpan.FromMinutes(1)));
            var status = new ManagedDeploymentStatus(
                ManagedDeploymentState.Ready,
                spec.Generation,
                Revision: 1,
                firstLease.FencingToken,
                ManagedDeploymentValidation.GetFingerprint(spec),
                "plan-1",
                "resource/orders",
                null,
                DateTimeOffset.UtcNow);
            var updated = await store.UpdateStatusAsync(
                spec.DeploymentId,
                status,
                expectedRevision: 0);
            Assert.Equal(ManagedDeploymentState.Ready, updated.Status.State);
            await Assert.ThrowsAsync<ManagedDeploymentConcurrencyException>(
                () => store.UpdateStatusAsync(
                    spec.DeploymentId,
                    status,
                    expectedRevision: 0).AsTask());

            await store.ReleaseAsync(firstLease);
            var secondLease = Assert.IsType<ManagedDeploymentLease>(
                await store.TryAcquireAsync(
                    spec.DeploymentId,
                    "worker-b",
                    TimeSpan.FromMinutes(1)));
            Assert.True(secondLease.FencingToken > firstLease.FencingToken);
            await Assert.ThrowsAsync<ManagedDeploymentLeaseException>(
                () => store.ReleaseAsync(firstLease).AsTask());
            await store.ReleaseAsync(secondLease);

            var next = spec with { Generation = 2, Paused = true };
            var stored = await store.PutAsync(next, expectedGeneration: 1);
            Assert.Equal(2, stored.Spec.Generation);
            Assert.Equal(ManagedDeploymentState.Pending, stored.Status.State);
            await Assert.ThrowsAsync<ManagedDeploymentConcurrencyException>(
                () => store.PutAsync(
                    next with { Generation = 3 },
                    expectedGeneration: 1).AsTask());

            await ExecuteAsync(
                dataSource,
                $"""
                 UPDATE "{schema}".managed_deployments
                 SET document_format =
                     {PostgreSqlManagedDeploymentStore.CurrentDocumentFormatVersion + 1}
                 WHERE deployment_id = 'orders'
                 """);
            var futureFormat = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.GetAsync("orders").AsTask());
            Assert.Contains("format", futureFormat.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await DropSchemaAsync(dataSource, schema);
        }
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "BLUETUSK_TEST_CONNECTION_STRING");
        return string.IsNullOrWhiteSpace(connectionString)
            ? throw SkipException.ForSkip(
                "BLUETUSK_TEST_CONNECTION_STRING is not configured.")
            : connectionString;
    }

    private static async Task CreateLogicalSlotAsync(
        BlueTuskDataSource dataSource,
        string slotName)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT slot_name FROM pg_catalog.pg_create_logical_replication_slot(@slot, 'pgoutput')";
        AddParameter(command, "slot", slotName);
        Assert.Equal(slotName, await command.ExecuteScalarAsync());
    }

    private static async Task DropLogicalSlotAsync(
        BlueTuskDataSource dataSource,
        string slotName)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT pg_catalog.pg_drop_replication_slot(slot_name) " +
            "FROM pg_catalog.pg_replication_slots WHERE slot_name = @slot AND NOT active";
        AddParameter(command, "slot", slotName);
        _ = await command.ExecuteScalarAsync();
    }

    private static async Task SeedSnapshotAsync(
        BlueTuskDataSource dataSource,
        string schema,
        ChangeRelaySourceRegistration source)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             INSERT INTO "{schema}".snapshot_runs (
                 source_fingerprint, source_epoch, snapshot_epoch, state, progress)
             VALUES (@source, @epoch, 'snapshot-1', 'complete', '\x0102')
             """;
        AddParameter(command, "source", source.Source.Fingerprint);
        AddParameter(command, "epoch", source.SourceEpoch);
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask ExecuteAsync(
        BlueTuskDataSource dataSource,
        string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask CreateLegacyAuditSchemaAsync(
        BlueTuskDataSource dataSource,
        string schema)
    {
        await ExecuteAsync(
            dataSource,
            $"""
             CREATE SCHEMA "{schema}";
             CREATE TABLE "{schema}".audit_log (
                 audit_sequence bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                 operation_id uuid NOT NULL,
                 occurred_at timestamptz NOT NULL,
                 actor_id text NOT NULL,
                 operation_kind text NOT NULL,
                 target text NOT NULL,
                 status text NOT NULL,
                 reason text NOT NULL,
                 detail_code text NULL
             );
             INSERT INTO "{schema}".audit_log (
                 operation_id, occurred_at, actor_id, operation_kind, target,
                 status, reason, detail_code)
             VALUES (
                 '00000000-0000-0000-0000-000000000001',
                 '2026-08-03T16:00:00Z',
                 'legacy-operator',
                 'PauseSource',
                 'source:legacy',
                 'Succeeded',
                 'Legacy acceptance record',
                 NULL)
             """);
    }

    private static async Task<long> ExecuteInt64Async(
        BlueTuskDataSource dataSource,
        string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task DropSchemaAsync(
        BlueTuskDataSource dataSource,
        string schema)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
        _ = await command.ExecuteNonQueryAsync();
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        _ = command.Parameters.Add(parameter);
    }
}
