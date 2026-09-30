using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlueTusk.Data;
using BlueTusk.Documents.Live;
using BlueTusk.Documents.Streams;
using BlueTusk.Live;
using BlueTusk.Live.DependencyInjection;
using BlueTusk.Replication;
using BlueTusk.Replication.PgOutput;
using BlueTusk.Streams;
using BlueTusk.Streams.Testing;
using BlueTusk.TypeSystem;

namespace BlueTusk.Documents.Tests;

public sealed class DocumentIntegrationTests
{
    private static readonly ChangeSourceIdentity Source = new("system", "documents", "slot", "publication");
    private static readonly DocumentCollectionDefinition<IntegrationDocument> Collection = new("orders", IntegrationJsonContext.Default.IntegrationDocument);
    private static readonly ChangeTable Table = CreateTable("bluetusk_documents");

    [Fact]
    public async Task Typed_transactions_preserve_scope_causality_partial_old_images_and_unchanged_toast()
    {
        var id = new ChangeId(Source, new BlueTuskLogSequenceNumber(50), 3, 0);
        var old = Row(Table, "tenant", "orders", "1", "{\"Name\":\"old\",\"Count\":1}", 4);
        var newer = Row(Table, "tenant", "orders", "1", "{\"Name\":\"new\",\"Count\":2}", 5);
        var values = newer.Values.ToArray();
        values[5] = ChangeColumnValue.UnchangedToast;
        var toast = new ChangeRow(Table, values);
        var unavailable = new ChangeRow(Table, Enumerable.Repeat(ChangeColumnValue.OldValueUnavailable, Table.Columns.Count));
        await using var delivery = Delivery([
            new UpdateChange(id, old, toast, new ChangedColumnSet(false, [3])),
            new UpdateChange(id with { Ordinal = 1 }, unavailable, newer, new ChangedColumnSet(false, [3, 5])),
            new InsertChange(id with { Ordinal = 2 }, Row(Table, "private", "orders", "1", "{}", 6)),
            new DeleteChange(id with { Ordinal = 3 }, new ChangeRow(Table, old.Values.Select((value, ordinal) => ordinal < 3 ? value : ChangeColumnValue.OldValueUnavailable))),
        ], position: 50, transactionId: 3);
        var mapped = await new DocumentChangeMapper<IntegrationDocument>("tenant", Collection).MapTransactionAsync(delivery.Transaction);
        Assert.Same(delivery.Transaction, mapped.SourceTransaction);
        Assert.Equal(3, mapped.Changes.Count);
        Assert.Equal(id, mapped.Changes[0].Id);
        Assert.Equal(ChangeColumnState.UnchangedToast, mapped.Changes[0].NewImage!.BodyState);
        Assert.True(mapped.Changes[0].NewImage!.HasValue);
        Assert.Equal("old", mapped.Changes[0].NewImage!.Value!.Name);
        Assert.Equal(5, mapped.Changes[0].NewImage!.Revision);
        Assert.Equal(ChangeKind.Update, mapped.Changes[1].Kind);
        Assert.False(mapped.Changes[1].OldImage!.HasValue);
        Assert.Null(mapped.Changes[1].OldImage!.Revision);
        Assert.Equal("new", mapped.Changes[1].NewImage!.Value!.Name);
        Assert.Equal(ChangeKind.Delete, mapped.Changes[2].Kind);
        Assert.Null(mapped.Changes[2].OldImage!.Revision);
        Assert.Equal(50UL, mapped.Changes[2].Id.CommitEndPosition.Value);
    }

    [Fact]
    public async Task Binary_jsonb_and_numbers_decode_without_reflection_and_unknown_schema_stays_explicit()
    {
        var row = Row(Table, "tenant", "orders", "1", "{\"Name\":\"binary\",\"Count\":2}", 4);
        var values = row.Values.ToArray();
        var revision = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(revision, 4);
        var schema = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(schema, 1);
        values[3] = ChangeColumnValue.FromValue(revision, ChangeValueEncoding.Binary);
        values[4] = ChangeColumnValue.FromValue(schema, ChangeValueEncoding.Binary);
        values[5] = ChangeColumnValue.FromValue(new byte[] { 1 }.Concat(values[5].Data.ToArray()).ToArray(), ChangeValueEncoding.Binary);
        var id = new ChangeId(Source, new BlueTuskLogSequenceNumber(1), 1, 0);
        await using var delivery = Delivery([new InsertChange(id, new ChangeRow(Table, values))]);
        var mapper = new DocumentChangeMapper<IntegrationDocument>("tenant", Collection);
        Assert.Equal("binary", Assert.Single((await mapper.MapTransactionAsync(delivery.Transaction)).Changes).NewImage!.Value!.Name);
        values[4] = Text("2");
        await using var future = Delivery([new InsertChange(id, new ChangeRow(Table, values))]);
        var image = Assert.Single((await mapper.MapTransactionAsync(future.Transaction)).Changes).NewImage!;
        Assert.Equal(2, image.SchemaVersion);
        Assert.False(image.HasValue);
        var strict = new DocumentChangeMapper<IntegrationDocument>("tenant", Collection, new DocumentStreamOptions { RequireCompleteNewDocuments = true });
        await Assert.ThrowsAsync<DocumentStreamMappingException>(async () => await strict.MapTransactionAsync(future.Transaction));
    }

    [Fact]
    public async Task Mapping_limits_truncation_and_two_phase_delivery_fail_before_acknowledgement()
    {
        var id = new ChangeId(Source, new BlueTuskLogSequenceNumber(1), 1, 0);
        await using var tooLarge = Delivery([new InsertChange(id, Row(Table, "tenant", "orders", "1", "{\"Name\":\"long\",\"Count\":1}", 1))]);
        var mapper = new DocumentChangeMapper<IntegrationDocument>("tenant", Collection, new DocumentStreamOptions { MaxDocumentBytes = 5, MaxTransactionBytes = 100 });
        await Assert.ThrowsAsync<DocumentStreamMappingException>(async () => await mapper.MapTransactionAsync(tooLarge.Transaction));
        await using var truncate = Delivery([new TruncateChange(id, [Table], false, false)]);
        await Assert.ThrowsAsync<DocumentStreamResetRequiredException>(async () => await mapper.MapTransactionAsync(truncate.Transaction));
        await using var prepared = ChangeDeliveryTestFactory.CreateTwoPhase(Source, 1, new BlueTuskLogSequenceNumber(1), ChangeTransactionOutcome.Prepared, "prepared");
        await Assert.ThrowsAsync<DocumentStreamMappingException>(async () => await mapper.MapTransactionAsync(prepared.Transaction));
        Assert.Equal(ChangeDeliveryState.Active, prepared.State);
    }

    [Fact]
    public async Task Consumer_commits_callback_before_acknowledging_and_nacks_failures_including_cancellation()
    {
        var id = new ChangeId(Source, new BlueTuskLogSequenceNumber(1), 1, 0);
        var observer = new Observer();
        var mapper = new DocumentChangeMapper<IntegrationDocument>("tenant", Collection);
        var consumer = new DocumentTransactionConsumer<IntegrationDocument>(mapper, (transaction, token) =>
        {
            Assert.False(observer.Acknowledged);
            Assert.Single(transaction.Changes);
            observer.Applied = true;
            return ValueTask.CompletedTask;
        });
        await using var delivery = Delivery([new InsertChange(id, Row(Table, "tenant", "orders", "1", "{}", 1))], observer);
        await consumer.ConsumeAsync(delivery);
        Assert.True(observer.AppliedBeforeAcknowledged);
        await using var failure = Delivery([new InsertChange(id, Row(Table, "tenant", "orders", "1", "{}", 1))], observer);
        var failing = new DocumentTransactionConsumer<IntegrationDocument>(mapper, static (_, _) => ValueTask.FromException(new IOException("commit failed")));
        await Assert.ThrowsAsync<IOException>(async () => await failing.ConsumeAsync(failure));
        Assert.Equal(ChangeDeliveryState.Nacked, failure.State);
        await using var cancelled = Delivery([], observer);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await consumer.ConsumeAsync(cancelled, cancellation.Token));
        Assert.Equal(ChangeDeliveryState.Nacked, cancelled.State);
    }

    [Fact]
    public void Snapshot_mapping_retains_epoch_scope_and_complete_contract()
    {
        var epoch = SnapshotEpoch.Create(Source, new BlueTuskLogSequenceNumber(12));
        var row = Row(Table, "tenant", "orders", "1", "{\"Name\":\"snapshot\",\"Count\":1}", 4);
        var batch = new ChangeSnapshotBatch(epoch, Table, 3, [new ChangeSnapshotRow(SnapshotRowId.Create(epoch, Table, row.Values.Take(3)), row)], true);
        var mapped = new DocumentChangeMapper<IntegrationDocument>("tenant", Collection).MapSnapshot(batch);
        Assert.Equal(epoch, mapped.Epoch);
        Assert.Equal(3, mapped.Sequence);
        Assert.True(mapped.IsLastForTable);
        Assert.Equal("snapshot", Assert.Single(mapped.Documents).Value!.Name);
    }

    [Fact]
    public async Task Real_document_WAL_drives_typed_transactions_and_durable_Live_authoritative_diffs()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Set BLUETUSK_TEST_CONNECTION_STRING to a disposable logical-replication PostgreSQL database.");
        var schema = "documents_integrated_" + Guid.NewGuid().ToString("N");
        var controlSchema = "documents_live_" + Guid.NewGuid().ToString("N");
        var publication = "documents_pub_" + Guid.NewGuid().ToString("N");
        var slot = "documents_slot_" + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var source = BlueTuskDataSource.Create(connectionString);
        await using var store = new DocumentStore(source, new DocumentStoreOptions { Schema = schema });
        await store.InitializeAsync(token);
        await DocumentStreamDeployment.EnableFullReplicaIdentityAsync(source, schema, token);
        try
        {
            using (var setup = store.OpenSession("tenant"))
            {
                setup.Insert(Collection, "1", new IntegrationDocument("first", 1));
                _ = await setup.SaveChangesAsync(token);
            }

            await using (var create = source.CreateCommand($"CREATE PUBLICATION \"{publication}\" FOR TABLE \"{schema}\".documents"))
            {
                _ = await create.ExecuteNonQueryAsync(token);
            }

            var live = new PostgreSqlLiveInvalidationStore(new PostgreSqlLiveStoreOptions { ControlDataSource = source, ControlSchema = controlSchema });
            var plan = DocumentLiveQuery.Create(store, Collection, "orders", "primary", "1", static scope => scope.Scope, maximumResults: 2);
            var arguments = plan.Bind(new Dictionary<string, object?>());
            await using var session = new LiveQuerySession<StoredDocument<IntegrationDocument>, string>(plan, arguments, new LiveSecurityScope("tenant", "policy-1"), live);
            await using var otherTenant = new LiveQuerySession<StoredDocument<IntegrationDocument>, string>(plan, arguments, new LiveSecurityScope("other", "policy-1"), live);
            Assert.Single(Assert.Single((await session.StartAsync(token)).Events).Rows!);
            Assert.Empty(Assert.Single((await otherTenant.StartAsync(token)).Events).Rows!);
            await using var replication = await BlueTuskLogicalReplicationConnection.OpenAsync(source.CreateDedicatedSessionOptions(), token);
            var system = await replication.IdentifySystemAsync(token);
            _ = await replication.CreateReplicationSlotAsync(slot, temporary: true, cancellationToken: token);
            var stream = new PgOutputChangeStream(replication.StartReplicationAsync(slot, publication, cancellationToken: token).DecodePgOutputAsync(),
                new ChangeSourceIdentity(system.SystemIdentifier, system.DatabaseName!, slot, publication));
            await using var enumerator = stream.ReadTransactionsAsync(token).GetAsyncEnumerator(token);
            var pending = enumerator.MoveNextAsync().AsTask();
            var original = (await store.LoadAsync("tenant", Collection, "1", token))!;
            using (var write = store.OpenSession("tenant"))
            {
                write.Replace(Collection, "1", original.Value with { Count = 2 }, original.Revision);
                write.Insert(Collection, "2", new IntegrationDocument("second", 2));
                _ = await write.SaveChangesAsync(token);
            }

            Assert.True(await pending.WaitAsync(token));
            var delivery = enumerator.Current;
            var mapper = new DocumentChangeMapper<IntegrationDocument>("tenant", Collection, new DocumentStreamOptions { Schema = schema, RequireCompleteNewDocuments = true });
            var mapped = await mapper.MapTransactionAsync(delivery.Transaction, token);
            Assert.Equal(2, mapped.Changes.Count);
            Assert.All(mapped.Changes, static change => Assert.True(change.NewImage!.HasValue));
            Assert.Equal(2, mapped.Changes[0].NewImage!.Value!.Count);
            var consumer = new LiveInvalidationConsumer("primary", live);
            await consumer.ConsumeTransactionAsync(delivery, token);
            Assert.Equal(ChangeDeliveryState.Acknowledged, delivery.State);
            var cursor = await live.GetCurrentCursorAsync("primary", token);
            Assert.Equal(cursor, await live.AppendAsync("primary", delivery.Transaction, token));
            var difference = (await session.RefreshToCurrentAsync(token))!;
            Assert.Contains(difference.Events, static item => item.Kind == LiveEventKind.RowUpdated && item.Key == "1" && item.Row!.Value.Count == 2);
            Assert.Contains(difference.Events, static item => item.Kind == LiveEventKind.RowAdded && item.Key == "2");
            Assert.Empty((await otherTenant.RefreshToCurrentAsync(token))!.Events);

            pending = enumerator.MoveNextAsync().AsTask();
            using (var delete = store.OpenSession("tenant"))
            {
                delete.Delete(Collection, "1", (await store.LoadAsync("tenant", Collection, "1", token))!.Revision);
                _ = await delete.SaveChangesAsync(token);
            }

            Assert.True(await pending.WaitAsync(token));
            delivery = enumerator.Current;
            var removed = Assert.Single((await mapper.MapTransactionAsync(delivery.Transaction, token)).Changes);
            Assert.Equal(ChangeKind.Delete, removed.Kind);
            Assert.NotNull(removed.OldImage!.Revision);
            await consumer.ConsumeTransactionAsync(delivery, token);
            Assert.Contains((await session.RefreshToCurrentAsync(token))!.Events, static item => item.Kind == LiveEventKind.RowRemoved && item.Key == "1");
        }
        finally
        {
            await using var cleanup = source.CreateCommand($"DROP PUBLICATION IF EXISTS \"{publication}\"; DROP SCHEMA IF EXISTS \"{schema}\" CASCADE; DROP SCHEMA IF EXISTS \"{controlSchema}\" CASCADE");
            _ = await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Live_plan_contains_filter_is_snapshotted_and_query_shape_is_fingerprinted()
    {
        await using var source = BlueTuskDataSource.Create("Host=127.0.0.1;Port=55418;Username=postgres;Password=postgres;Database=bluetusk_ecosystem;SSL Mode=Disable;Channel Binding=Disable");
        await using var store = new DocumentStore(source);
        var filter = JsonDocument.Parse("{\"Count\":2}");
        var one = DocumentLiveQuery.Create(store, Collection, "query", "primary", "1", static scope => scope.Scope, contains: filter.RootElement);
        filter.Dispose();
        var different = DocumentLiveQuery.Create(store, Collection, "query", "primary", "1", static scope => scope.Scope, maximumResults: 50);
        Assert.NotEqual(one.Fingerprint, different.Fingerprint);
        Assert.Equal(new LiveTableDependency("bluetusk_documents", "documents"), Assert.Single(one.Dependencies));
        Assert.Empty(one.Parameters);
        Assert.Throws<ArgumentOutOfRangeException>(() => DocumentLiveQuery.Create(store, Collection, "query", "primary", "1", static scope => scope.Scope, maximumResults: 1001));
    }

    private static ChangeTransactionDelivery Delivery(IEnumerable<Change> changes, IChangeDeliveryObserver? observer = null, ulong position = 1, uint transactionId = 1) =>
        ChangeDeliveryTestFactory.CreateCommitted(Source, transactionId, new BlueTuskLogSequenceNumber(position), changes, observer);

    private static ChangeTable CreateTable(string schema) => new(1, schema, "documents", 'f', [
        new ChangeColumn(0, "tenant", 25, -1, true), new ChangeColumn(1, "collection", 25, -1, true), new ChangeColumn(2, "id", 25, -1, true),
        new ChangeColumn(3, "revision", 20, -1, false), new ChangeColumn(4, "schema_version", 23, -1, false), new ChangeColumn(5, "body", 3802, -1, false),
    ]);
    private static ChangeRow Row(ChangeTable table, string tenant, string collection, string id, string json, long revision) =>
        new(table, [Text(tenant), Text(collection), Text(id), Text(revision.ToString(System.Globalization.CultureInfo.InvariantCulture)), Text("1"), Text(json)]);
    private static ChangeColumnValue Text(string value) => ChangeColumnValue.FromValue(Encoding.UTF8.GetBytes(value), ChangeValueEncoding.Text);

    private sealed class Observer : IChangeDeliveryObserver
    {
        internal bool Applied { get; set; }
        internal bool Acknowledged { get; private set; }
        internal bool AppliedBeforeAcknowledged { get; private set; }
        public ValueTask AcknowledgeAsync(ChangeTransaction transaction, CancellationToken cancellationToken = default)
        {
            AppliedBeforeAcknowledged = Applied;
            Acknowledged = true;
            return ValueTask.CompletedTask;
        }
        public ValueTask NackAsync(ChangeTransaction transaction, Exception? failure, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed record IntegrationDocument(string Name, int Count);
[JsonSerializable(typeof(IntegrationDocument))]
internal sealed partial class IntegrationJsonContext : JsonSerializerContext;
