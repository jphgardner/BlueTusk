using System.Globalization;
using System.Text;
using BlueTusk.Projections;
using BlueTusk.Streams;
using BlueTusk.Streams.Testing;
using BlueTusk.TypeSystem;
using static BlueTusk.UpgradeProbe.ProbeContext;

namespace BlueTusk.UpgradeProbe;

/// <summary>
/// Projections rehearsal. The baseline builds and promotes version 1 of a projection from a snapshot
/// plus three committed transactions, then stops while still holding the build lease (a crashed
/// builder). The candidate must open the same schema, keep the checkpoint, generation and published
/// documents, honour the persisted lease until it expires, take over with a newer fence, reject the
/// stale owner and a duplicate delivery, and apply the next transactions exactly once. The baseline
/// then reopens the candidate's state and continues from its checkpoint.
/// </summary>
internal static class FamilyProbe
{
    public const string Family = "Projections";
    private const string Name = "items";
    private const string Tenant = "upgrade";
    private static readonly ChangeSourceIdentity Source = new("upgrade-system", "upgrade-database", "upgrade-slot", "upgrade-publication");
    private static readonly ChangeTable Items = new(1, "public", "items", 'f',
        [new ChangeColumn(0, "id", 25, -1, true), new ChangeColumn(1, "value", 25, -1, false)]);
    private static readonly ProjectionIdentity Identity = new(Name, 1, "items-definition-1", Source);
    private static readonly ChangedColumnSet ValueChanged = new(true, [1]);

    public static async Task RunAsync(ProbeContext context)
    {
        var schema = context.SchemaBase + "_projections";
        var store = new PostgreSqlProjectionStore(context.DataSource, new PostgreSqlProjectionsOptions { Schema = schema });
        var definition = new ItemsProjection(Identity);
        await context.FingerprintAsync("before", schema);
        await store.InitializeAsync();
        await context.FingerprintAsync("after", schema);
        context.Observe("SchemaVersion", await context.CountAsync($"SELECT version FROM \"{schema}\".schema_version"));
        var leaseExpiry = $"SELECT expires_at FROM \"{schema}\".state WHERE projection = '{Name}' AND version = 1";

        if (context.IsSeed)
        {
            await store.RegisterAsync(Identity);
            var lease = await store.AcquireAsync(Identity, "baseline-builder", InFlightLease)
                ?? throw new InvalidOperationException("The baseline could not acquire the build lease.");
            var epoch = SnapshotEpoch.Create(Source, new BlueTuskLogSequenceNumber(100));
            await store.StartSnapshotAsync(lease, new SnapshotStart(epoch, 1));
            var rows = new[] { Row("i1", "one"), Row("i2", "two"), Row("i3", "three") };
            Require(await store.ApplySnapshotAsync(lease, definition, new ChangeSnapshotBatch(epoch, Items, 0,
                rows.Select(row => new ChangeSnapshotRow(SnapshotRowId.Create(epoch, Items, [row[0]]), row)), true)),
                "The baseline snapshot batch was not applied.");
            await store.CompleteSnapshotAsync(lease, new SnapshotComplete(epoch, rows.Length, 1));
            context.Observe("SnapshotRows", (await store.ReadStateAsync(Identity)).SnapshotRows);
            var applied = 0;
            applied += await ApplyAsync(store, lease, definition, 101, id => new InsertChange(id, Row("i4", "four")));
            applied += await ApplyAsync(store, lease, definition, 102, id => new UpdateChange(id, Row("i1", "one"), Row("i1", "one-v2"), ValueChanged));
            applied += await ApplyAsync(store, lease, definition, 103, id => new DeleteChange(id, Row("i2", "two")));
            context.Observe("AppliedTransactions", applied);
            await store.PromoteAsync(lease, new BlueTuskLogSequenceNumber(103), null);
            context.Observe("ActiveVersion", await store.ReadActiveVersionAsync(Name) ?? 0);
            context.Observe("ActiveDocuments", await RequireDocumentsAsync(store, ("i1", "one-v2"), ("i3", "three"), ("i4", "four")));
            var state = await store.ReadStateAsync(Identity);
            // The baseline stops without releasing its lease: the candidate inherits an in-flight builder.
            context.Observe("HeldLeases", await context.CountAsync(
                $"SELECT count(*) FROM \"{schema}\".state WHERE owner_id = 'baseline-builder' AND expires_at > clock_timestamp()"));
            context.WriteState(new()
            {
                ["fence"] = lease.FencingToken.ToString(CultureInfo.InvariantCulture),
                ["checkpoint"] = state.Checkpoint.Value.ToString(CultureInfo.InvariantCulture),
                ["generation"] = state.Generation.ToString(CultureInfo.InvariantCulture),
            });
            return;
        }

        var handoff = context.ReadState();
        var preserved = await store.ReadStateAsync(Identity);
        Require(preserved.Phase == ProjectionBuildPhase.CatchingUp &&
            preserved.Checkpoint.Value.ToString(CultureInfo.InvariantCulture) == handoff["checkpoint"] &&
            preserved.Generation.ToString(CultureInfo.InvariantCulture) == handoff["generation"],
            "The projection phase, checkpoint or generation changed across the binary boundary.");
        context.Observe("CheckpointPreserved", 1);
        Require(await store.ReadActiveVersionAsync(Name) == 1, "The published projection version changed across the binary boundary.");

        if (context.IsUpgrade)
        {
            context.Observe("DocumentsRead", await RequireDocumentsAsync(store, ("i1", "one-v2"), ("i3", "three"), ("i4", "four")));
            await context.RequireUnexpiredAsync(leaseExpiry, "baseline build lease");
            Require(await store.AcquireAsync(Identity, "candidate-builder", InFlightLease) is null,
                "The candidate acquired a build lease the baseline still held.");
            context.Observe("HeldLeaseHonoured", 1);
            await context.WaitForExpiryAsync(leaseExpiry, "baseline build lease");
            var lease = await store.AcquireAsync(Identity, "candidate-builder", InFlightLease)
                ?? throw new InvalidOperationException("The candidate could not take over the expired build lease.");
            var staleFence = long.Parse(handoff["fence"], CultureInfo.InvariantCulture);
            Require(lease.FencingToken > staleFence, "The candidate takeover did not advance the fencing token.");
            var stale = new ProjectionLease(Identity, "baseline-builder", staleFence);
            await RequireRejectedAsync<ProjectionFencedException>(
                async () =>
                {
                    await using var delivery = Delivery(104, id => new InsertChange(id, Row("stale", "stale")));
                    await store.ApplyAsync(stale, definition, delivery.Transaction);
                },
                "The stale baseline lease could still checkpoint after the candidate takeover.");
            context.Observe("StaleLeaseRejected", 1);
            context.Observe("DuplicateDeliveriesIgnored", await ApplyAsync(store, lease, definition, 103,
                id => new DeleteChange(id, Row("i2", "two"))) == 0 ? 1 : 0);
            var applied = 0;
            applied += await ApplyAsync(store, lease, definition, 104, id => new InsertChange(id, Row("i5", "five")));
            applied += await ApplyAsync(store, lease, definition, 105, id => new UpdateChange(id, Row("i3", "three"), Row("i3", "three-v2"), ValueChanged));
            context.Observe("AppliedTransactions", applied);
            context.Observe("ActiveDocuments", await RequireDocumentsAsync(store, ("i1", "one-v2"), ("i3", "three-v2"), ("i4", "four"), ("i5", "five")));
            Require(await store.ReleaseAsync(lease), "The candidate could not release its build lease.");
            await RequireNewerFormatRejectedAsync(context);
            var state = await store.ReadStateAsync(Identity);
            context.WriteState(new()
            {
                ["fence"] = lease.FencingToken.ToString(CultureInfo.InvariantCulture),
                ["checkpoint"] = state.Checkpoint.Value.ToString(CultureInfo.InvariantCulture),
                ["generation"] = state.Generation.ToString(CultureInfo.InvariantCulture),
            });
            return;
        }

        context.Observe("DocumentsRead", await RequireDocumentsAsync(store, ("i1", "one-v2"), ("i3", "three-v2"), ("i4", "four"), ("i5", "five")));
        var rollbackLease = await store.AcquireAsync(Identity, "rollback-builder", InFlightLease)
            ?? throw new InvalidOperationException("The rolled-back binary could not acquire the released build lease.");
        Require(rollbackLease.FencingToken > long.Parse(handoff["fence"], CultureInfo.InvariantCulture),
            "The rolled-back binary did not advance the candidate's fencing token.");
        context.Observe("DuplicateDeliveriesIgnored", await ApplyAsync(store, rollbackLease, definition, 105,
            id => new UpdateChange(id, Row("i3", "three"), Row("i3", "three-v2"), ValueChanged)) == 0 ? 1 : 0);
        context.Observe("AppliedTransactions", await ApplyAsync(store, rollbackLease, definition, 106,
            id => new DeleteChange(id, Row("i4", "four"))));
        context.Observe("ActiveDocuments", await RequireDocumentsAsync(store, ("i1", "one-v2"), ("i3", "three-v2"), ("i5", "five")));
        Require(await store.ReleaseAsync(rollbackLease), "The rolled-back binary could not release its build lease.");
    }

    private static async Task RequireNewerFormatRejectedAsync(ProbeContext context)
    {
        // RECOVERY.md: current clients fail closed on unknown schema versions.
        var future = context.SchemaBase + "_future";
        var store = new PostgreSqlProjectionStore(context.DataSource, new PostgreSqlProjectionsOptions { Schema = future });
        await store.InitializeAsync();
        await context.ExecuteAsync($"UPDATE \"{future}\".schema_version SET version = version + 1");
        await RequireRejectedAsync<InvalidOperationException>(async () => await store.InitializeAsync(),
            "The candidate initialized a projection schema version newer than it supports.");
        await context.DropSchemaAsync(future);
        context.Observe("NewerFormatRejected", 1);
    }

    private static ChangeRow Row(string id, string value) => new(Items,
        [ChangeColumnValue.FromValue(Encoding.UTF8.GetBytes(id), ChangeValueEncoding.Text),
            ChangeColumnValue.FromValue(Encoding.UTF8.GetBytes(value), ChangeValueEncoding.Text)]);

    private static ChangeTransactionDelivery Delivery(ulong position, Func<ChangeId, Change> change)
    {
        var lsn = new BlueTuskLogSequenceNumber(position);
        var id = new ChangeId(Source, lsn, (uint)position, 0);
        return ChangeDeliveryTestFactory.CreateCommitted(Source, id.TransactionId, lsn, [change(id)]);
    }

    private static async Task<int> ApplyAsync(PostgreSqlProjectionStore store, ProjectionLease lease, ItemsProjection definition,
        ulong position, Func<ChangeId, Change> change)
    {
        await using var delivery = Delivery(position, change);
        var result = await store.ApplyAsync(lease, definition, delivery.Transaction);
        Require(result.Checkpoint.Value >= position, "The projection checkpoint fell behind an acknowledged delivery.");
        return result.WasApplied ? 1 : 0;
    }

    private static async Task<int> RequireDocumentsAsync(PostgreSqlProjectionStore store, params (string Key, string Value)[] expected)
    {
        var page = await store.ReadActivePageAsync(Name, Tenant);
        Require(page.ContinueAfter is null, "The probe projection unexpectedly spans more than one page.");
        var actual = page.Documents.Select(document => (document.Key, Encoding.UTF8.GetString(document.Payload.Span)))
            .OrderBy(document => document.Key, StringComparer.Ordinal).ToArray();
        var wanted = expected.Select(item => (item.Key, Payload(item.Key, item.Value)))
            .OrderBy(document => document.Key, StringComparer.Ordinal).ToArray();
        Require(actual.SequenceEqual(wanted), "Published projection documents were lost, duplicated or changed across the binary boundary.");
        return actual.Length;
    }

    private static string Payload(string id, string value) => $"{{\"id\":\"{id}\",\"value\":\"{value}\"}}";

    private static string Text(ChangeRow row, string column)
    {
        var value = row[column];
        Require(value.State == ChangeColumnState.Value, "The probe projection requires full source values.");
        return Encoding.UTF8.GetString(value.Data.Span);
    }

    private sealed class ItemsProjection(ProjectionIdentity identity) : IProjectionDefinition
    {
        public ProjectionIdentity Identity { get; } = identity;

        public async ValueTask ApplySnapshotAsync(ChangeSnapshotBatch batch, ProjectionWriteContext context, CancellationToken cancellationToken)
        {
            foreach (var row in batch.Rows)
            {
                await UpsertAsync(row.Row, context, cancellationToken);
            }
        }

        public async ValueTask ApplyTransactionAsync(ChangeTransaction transaction, ProjectionWriteContext context, CancellationToken cancellationToken)
        {
            await foreach (var change in transaction.Changes.WithCancellation(cancellationToken))
            {
                switch (change)
                {
                    case InsertChange insert: await UpsertAsync(insert.NewRow, context, cancellationToken); break;
                    case UpdateChange update: await UpsertAsync(update.NewRow, context, cancellationToken); break;
                    case DeleteChange delete: await context.DeleteAsync(Tenant, Text(delete.OldRow, "id"), cancellationToken); break;
                    default: throw new InvalidOperationException("The probe projection only accepts row changes.");
                }
            }
        }

        private static ValueTask UpsertAsync(ChangeRow row, ProjectionWriteContext context, CancellationToken cancellationToken)
        {
            var id = Text(row, "id");
            return context.UpsertAsync(Tenant, id, Encoding.UTF8.GetBytes(Payload(id, Text(row, "value"))), [], cancellationToken);
        }
    }
}
