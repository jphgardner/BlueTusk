using System.Globalization;
using BlueTusk.Streams;
using BlueTusk.Streams.Testing;
using BlueTusk.TypeSystem;

namespace BlueTusk.Projections.Tests;

public sealed class PostgreSqlProjectionStoreTests
{
    [Fact]
    public async Task SnapshotFromBothTablesBuildsJoinAndAggregateThenPublishesAtomically()
    {
        await using var fixture = await ProjectionDatabase.CreateAsync();
        var (lease, definition) = await fixture.ReadyAsync(orderCount: 3);
        Assert.Null(await fixture.ReadAsync());
        Assert.Equal(60m, await fixture.AggregateAsync());
        Assert.Equal(6, await fixture.CountAsync("dependencies"));
        await fixture.Store.PromoteAsync(lease, new BlueTuskLogSequenceNumber(100), null);
        Assert.Equal(new OrderView("1", "Alice", 10m, 1), await fixture.ReadAsync());
        Assert.Equal(new OrderView("3", "Alice", 30m, 1), await fixture.ReadAsync(id: "3"));
        Assert.Null(await fixture.ReadAsync("other"));
        Assert.Throws<InvalidOperationException>(() => _ = definition.LastContext!.Connection);
    }

    [Fact]
    public async Task SnapshotCompletenessSequenceAndContentAreEnforcedAndRetriesDoNotDoubleAggregate()
    {
        await using var fixture = await ProjectionDatabase.CreateAsync();
        var (lease, definition, epoch) = await fixture.BeginAsync();
        var batch = ProjectionDatabase.Batch(epoch, ProjectionDatabase.Orders, 0,
            [ProjectionDatabase.Order("1", "first", "customer", 10m)], true);
        Assert.True(await fixture.Store.ApplySnapshotAsync(lease, definition, batch));
        Assert.False(await fixture.Store.ApplySnapshotAsync(lease, definition, batch));
        Assert.Equal(10m, await fixture.AggregateAsync());
        var changed = ProjectionDatabase.Batch(epoch, ProjectionDatabase.Orders, 0,
            [ProjectionDatabase.Order("1", "first", "customer", 20m)], true);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Store.ApplySnapshotAsync(lease, definition, changed));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Store.CompleteSnapshotAsync(lease, new SnapshotComplete(epoch, 1, 2)));
        await using var early = fixture.Delivery(101);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Store.ApplyAsync(lease, definition, early.Transaction));
        var outOfOrder = ProjectionDatabase.Batch(epoch, ProjectionDatabase.Customers, 1, [], true);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Store.ApplySnapshotAsync(lease, definition, outOfOrder));
        await fixture.Store.ApplySnapshotAsync(lease, definition, ProjectionDatabase.Batch(epoch, ProjectionDatabase.Customers, 0, [], true));
        await fixture.Store.CompleteSnapshotAsync(lease, new SnapshotComplete(epoch, 1, 2));
        Assert.False(await fixture.Store.ApplySnapshotAsync(lease, definition, batch));
        var state = await fixture.Store.ReadStateAsync(lease.Identity);
        Assert.Equal(ProjectionBuildPhase.CatchingUp, state.Phase);
        Assert.Equal(100UL, state.Checkpoint.Value);
    }

    [Fact]
    public async Task SnapshotFailureRollsBackOutputAggregateAndBatchLedger()
    {
        await using var fixture = await ProjectionDatabase.CreateAsync();
        var (lease, definition, epoch) = await fixture.BeginAsync();
        var batch = ProjectionDatabase.Batch(epoch, ProjectionDatabase.Orders, 0,
            [ProjectionDatabase.Order("1", "first", "customer", 10m)], true);
        definition.FailAfterWrites = true;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Store.ApplySnapshotAsync(lease, definition, batch));
        Assert.Equal(0m, await fixture.AggregateAsync());
        Assert.Equal(0, await fixture.CountAsync("documents"));
        Assert.Equal(0, await fixture.CountAsync("source_rows"));
        Assert.Equal(0, await fixture.CountAsync("snapshot_batches"));
        Assert.Equal(0, (await fixture.Store.ReadStateAsync(lease.Identity)).SnapshotRows);
        definition.FailAfterWrites = false;
        Assert.True(await fixture.Store.ApplySnapshotAsync(lease, definition, batch));
        Assert.Equal(10m, await fixture.AggregateAsync());
    }

    [Fact]
    public async Task RepeatedSourceRowInDifferentSnapshotBatchCannotDoubleAggregate()
    {
        await using var fixture = await ProjectionDatabase.CreateAsync();
        var (lease, definition, epoch) = await fixture.BeginAsync();
        var row = ProjectionDatabase.Order("1", "first", "customer", 10m);
        await fixture.Store.ApplySnapshotAsync(lease, definition, ProjectionDatabase.Batch(epoch, ProjectionDatabase.Orders, 0, [row], false));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Store.ApplySnapshotAsync(lease, definition, ProjectionDatabase.Batch(epoch, ProjectionDatabase.Orders, 1, [row], true)));
        Assert.Equal(10m, await fixture.AggregateAsync());
        Assert.Equal(1, (await fixture.Store.ReadStateAsync(lease.Identity)).SnapshotRows);
    }

    [Fact]
    public async Task CustomerUpdateInvalidatesEveryPageOfDependentJoinedDocuments()
    {
        await using var fixture = await ProjectionDatabase.CreateAsync();
        var (lease, definition) = await fixture.ReadyAsync(orderCount: 4);
        await fixture.Store.PromoteAsync(lease, new BlueTuskLogSequenceNumber(100), null);
        await using var delivery = fixture.Delivery(200,
            id => new UpdateChange(id, ProjectionDatabase.Customer("customer", "first", "Alice"),
                ProjectionDatabase.Customer("customer", "first", "Bob"), new ChangedColumnSet(true, [2])));
        var consumer = new StreamsProjectionConsumer(fixture.Store, lease, definition);
        await consumer.ConsumeTransactionAsync(delivery);
        Assert.Equal(ChangeDeliveryState.Acknowledged, delivery.State);
        for (var i = 1; i <= 4; i++)
        {
            Assert.Equal("Bob", (await fixture.ReadAsync(id: i.ToString(CultureInfo.InvariantCulture)))!.CustomerName);
        }

        Assert.Equal(100m, await fixture.AggregateAsync());
        Assert.Equal(200UL, (await fixture.Store.ReadStateAsync(lease.Identity)).Checkpoint.Value);
    }

    [Fact]
    public async Task ChangingOrderCustomerReconcilesFanOutToTheNewCustomer()
    {
        await using var fixture = await ProjectionDatabase.CreateAsync();
        var (lease, definition) = await fixture.ReadyAsync();
        await fixture.Store.PromoteAsync(lease, new BlueTuskLogSequenceNumber(100), null);
        await using (var added = fixture.Delivery(200,
            id => new InsertChange(id, ProjectionDatabase.Customer("other", "first", "Bob"))))
        {
            await fixture.Store.ApplyAsync(lease, definition, added.Transaction);
        }

        await using (var moved = fixture.Delivery(300,
            id => new UpdateChange(id, ProjectionDatabase.Order("1", "first", "customer", 10m),
                ProjectionDatabase.Order("1", "first", "other", 10m), new ChangedColumnSet(true, [2]))))
        {
            await fixture.Store.ApplyAsync(lease, definition, moved.Transaction);
        }

        Assert.Equal("Bob", (await fixture.ReadAsync())!.CustomerName);
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT string_agg(key_id, ',' ORDER BY key_id)
                FROM "{fixture.Schema}".dependencies
                WHERE projection='orders' AND version=1 AND tenant_id='first'
                    AND document_key='1' AND table_id='public.customers'
                """;
            Assert.Equal("other", await command.ExecuteScalarAsync());
        }

        await using (var oldCustomer = fixture.Delivery(400,
            id => new UpdateChange(id, ProjectionDatabase.Customer("customer", "first", "Alice"),
                ProjectionDatabase.Customer("customer", "first", "Alicia"), new ChangedColumnSet(true, [2]))))
        {
            await fixture.Store.ApplyAsync(lease, definition, oldCustomer.Transaction);
        }

        Assert.Equal("Bob", (await fixture.ReadAsync())!.CustomerName);
        await using (var newCustomer = fixture.Delivery(500,
            id => new UpdateChange(id, ProjectionDatabase.Customer("other", "first", "Bob"),
                ProjectionDatabase.Customer("other", "first", "Bobby"), new ChangedColumnSet(true, [2]))))
        {
            await fixture.Store.ApplyAsync(lease, definition, newCustomer.Transaction);
        }

        Assert.Equal("Bobby", (await fixture.ReadAsync())!.CustomerName);
        Assert.Equal(10m, await fixture.AggregateAsync());
    }

    [Fact]
    public async Task CdcBatchStagesAllSourceTablesBeforeRenderingJoinAndCommitsAggregateExactlyOnce()
    {
        await using var fixture = await ProjectionDatabase.CreateAsync();
        var (lease, definition) = await fixture.ReadyAsync();
        await fixture.Store.PromoteAsync(lease, new BlueTuskLogSequenceNumber(100), null);
        await using var delivery = fixture.Delivery(200,
            id => new InsertChange(id, ProjectionDatabase.Order("2", "first", "new-customer", 12.34m)),
            id => new InsertChange(id, ProjectionDatabase.Customer("new-customer", "first", "Charlie")));
        var applied = await fixture.Store.ApplyAsync(lease, definition, delivery.Transaction);
        Assert.True(applied.WasApplied);
        Assert.Equal(22.34m, await fixture.AggregateAsync());
        Assert.Equal(new OrderView("2", "Charlie", 12.34m, 1), await fixture.ReadAsync(id: "2"));
        var duplicate = await fixture.Store.ApplyAsync(lease, definition, delivery.Transaction);
        Assert.False(duplicate.WasApplied);
        Assert.Equal(applied.Generation, duplicate.Generation);
        Assert.Equal(22.34m, await fixture.AggregateAsync());
    }

    [Fact]
    public async Task CdcFailureDoesNotAdvanceCheckpointOrAcknowledgeAndCanRetryAfterRestart()
    {
        await using var fixture = await ProjectionDatabase.CreateAsync();
        var (lease, definition) = await fixture.ReadyAsync();
        await fixture.Store.PromoteAsync(lease, new BlueTuskLogSequenceNumber(100), null);
        await using var delivery = fixture.Delivery(200, id => new InsertChange(id, ProjectionDatabase.Order("2", "first", "customer", 20m)));
        definition.FailAfterWrites = true;
        var consumer = new StreamsProjectionConsumer(fixture.Store, lease, definition);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await consumer.ConsumeTransactionAsync(delivery));
        Assert.Equal(ChangeDeliveryState.Active, delivery.State);
        Assert.Equal(100UL, (await fixture.Store.ReadStateAsync(lease.Identity)).Checkpoint.Value);
        Assert.Null(await fixture.ReadAsync(id: "2"));
        Assert.Equal(10m, await fixture.AggregateAsync());
        Assert.True(await fixture.Store.ReleaseAsync(lease));
        var replacement = Assert.IsType<ProjectionLease>(await fixture.Store.AcquireAsync(lease.Identity, "replacement", TimeSpan.FromMinutes(5)));
        Assert.True(replacement.FencingToken > lease.FencingToken);
        var replacementDefinition = new OrdersProjection(replacement.Identity);
        await new StreamsProjectionConsumer(fixture.Store, replacement, replacementDefinition).ConsumeTransactionAsync(delivery);
        Assert.Equal(ChangeDeliveryState.Acknowledged, delivery.State);
        Assert.Equal(30m, await fixture.AggregateAsync());
    }

    [Fact]
    public async Task IncompleteDependencyPaginationRollsBackAllWritesAndCheckpoint()
    {
        await using var fixture = await ProjectionDatabase.CreateAsync();
        var (lease, definition) = await fixture.ReadyAsync(orderCount: 3);
        await fixture.Store.PromoteAsync(lease, new BlueTuskLogSequenceNumber(100), null);
        definition.IncompletePagination = true;
        await using var delivery = fixture.Delivery(200,
            id => new UpdateChange(id, ProjectionDatabase.Customer("customer", "first", "Alice"),
                ProjectionDatabase.Customer("customer", "first", "Bob"), new ChangedColumnSet(true, [2])));
        await Assert.ThrowsAsync<ProjectionBoundExceededException>(async () => await fixture.Store.ApplyAsync(lease, definition, delivery.Transaction));
        Assert.Equal("Alice", (await fixture.ReadAsync())!.CustomerName);
        Assert.Equal("Alice", (await fixture.ReadAsync(id: "3"))!.CustomerName);
        Assert.Equal(100UL, (await fixture.Store.ReadStateAsync(lease.Identity)).Checkpoint.Value);
    }

    [Fact]
    public async Task FanOutBoundFailureCannotBeSwallowedToCheckpointPartialWork()
    {
        await using var fixture = await ProjectionDatabase.CreateAsync(maximumInvalidations: 2);
        // Seed customers first so snapshot initialization does not invalidate a preexisting fan-out.
        var (lease, definition, epoch) = await fixture.BeginAsync();
        await fixture.Store.ApplySnapshotAsync(lease, definition, ProjectionDatabase.Batch(epoch, ProjectionDatabase.Customers, 0,
            [ProjectionDatabase.Customer("customer", "first", "Alice")], true));
        await fixture.Store.ApplySnapshotAsync(lease, definition, ProjectionDatabase.Batch(epoch, ProjectionDatabase.Orders, 0,
            Enumerable.Range(1, 3).Select(i => ProjectionDatabase.Order(i.ToString(CultureInfo.InvariantCulture), "first", "customer", i * 10m)), true));
        await fixture.Store.CompleteSnapshotAsync(lease, new SnapshotComplete(epoch, 4, 2));
        await fixture.Store.PromoteAsync(lease, new BlueTuskLogSequenceNumber(100), null);
        definition.SwallowBoundFailure = true;
        await using var delivery = fixture.Delivery(200,
            id => new UpdateChange(id, ProjectionDatabase.Customer("customer", "first", "Alice"),
                ProjectionDatabase.Customer("customer", "first", "Bob"), new ChangedColumnSet(true, [2])));
        await Assert.ThrowsAsync<ProjectionBoundExceededException>(async () => await fixture.Store.ApplyAsync(lease, definition, delivery.Transaction));
        Assert.Equal("Alice", (await fixture.ReadAsync())!.CustomerName);
        Assert.Equal(100UL, (await fixture.Store.ReadStateAsync(lease.Identity)).Checkpoint.Value);
    }

    [Fact]
    public async Task VersionedRebuildKeepsOldReadsAndRequiresCatchUpBeforeAtomicCutover()
    {
        await using var fixture = await ProjectionDatabase.CreateAsync();
        var (firstLease, firstDefinition) = await fixture.ReadyAsync();
        await fixture.Store.PromoteAsync(firstLease, new BlueTuskLogSequenceNumber(100), null);
        await using var firstDelivery = fixture.Delivery(200,
            id => new InsertChange(id, ProjectionDatabase.Order("2", "first", "customer", 20m)));
        await fixture.Store.ApplyAsync(firstLease, firstDefinition, firstDelivery.Transaction);
        var (secondLease, secondDefinition) = await fixture.ReadyAsync(2);
        Assert.Equal(1, (await fixture.ReadAsync())!.Version);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Store.PromoteAsync(secondLease, new BlueTuskLogSequenceNumber(100), 1));
        await fixture.Store.ApplyAsync(secondLease, secondDefinition, firstDelivery.Transaction);
        await fixture.Store.PromoteAsync(secondLease, new BlueTuskLogSequenceNumber(200), 1);
        Assert.Equal(2, await fixture.Store.ReadActiveVersionAsync("orders"));
        Assert.Equal(2, (await fixture.ReadAsync())!.Version);
        Assert.Equal(2, (await fixture.ReadAsync(id: "2"))!.Version);
        Assert.Equal(30m, await fixture.AggregateAsync(2));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Store.PromoteAsync(firstLease, new BlueTuskLogSequenceNumber(200), 1));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Store.StartSnapshotAsync(secondLease, new SnapshotStart(SnapshotEpoch.Create(fixture.Source, new BlueTuskLogSequenceNumber(300)), 2)));
    }

    [Fact]
    public async Task AbandonedSnapshotEpochResetsOnlyUnpublishedVersionAndRejectsOldBatches()
    {
        await using var fixture = await ProjectionDatabase.CreateAsync();
        var (lease, definition, epoch) = await fixture.BeginAsync();
        var oldBatch = ProjectionDatabase.Batch(epoch, ProjectionDatabase.Orders, 0,
            [ProjectionDatabase.Order("1", "first", "customer", 10m)], true);
        await fixture.Store.ApplySnapshotAsync(lease, definition, oldBatch);
        var replacement = SnapshotEpoch.Create(fixture.Source, new BlueTuskLogSequenceNumber(150));
        await fixture.Store.StartSnapshotAsync(lease, new SnapshotStart(replacement, 2));
        Assert.Equal(0m, await fixture.AggregateAsync());
        Assert.Equal(0, await fixture.CountAsync("documents"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Store.ApplySnapshotAsync(lease, definition, oldBatch));
        Assert.Equal(replacement.Value, (await fixture.Store.ReadStateAsync(lease.Identity)).SnapshotEpoch);
    }

    [Fact]
    public async Task TenantJoinsDependenciesAndAggregatesRemainIsolated()
    {
        await using var fixture = await ProjectionDatabase.CreateAsync();
        var (lease, definition, epoch) = await fixture.BeginAsync();
        await fixture.Store.ApplySnapshotAsync(lease, definition, ProjectionDatabase.Batch(epoch, ProjectionDatabase.Customers, 0,
            [ProjectionDatabase.Customer("shared", "first", "Alice"), ProjectionDatabase.Customer("other", "second", "Bob")], true));
        await fixture.Store.ApplySnapshotAsync(lease, definition, ProjectionDatabase.Batch(epoch, ProjectionDatabase.Orders, 0,
            [ProjectionDatabase.Order("1", "first", "shared", 10m), ProjectionDatabase.Order("2", "second", "other", 20m)], true));
        await fixture.Store.CompleteSnapshotAsync(lease, new SnapshotComplete(epoch, 4, 2));
        await fixture.Store.PromoteAsync(lease, new BlueTuskLogSequenceNumber(100), null);
        Assert.Equal("Alice", (await fixture.ReadAsync("first", "1"))!.CustomerName);
        Assert.Equal("Bob", (await fixture.ReadAsync("second", "2"))!.CustomerName);
        Assert.Null(await fixture.ReadAsync("second", "1"));
        Assert.Equal(10m, await fixture.AggregateAsync(tenant: "first"));
        Assert.Equal(20m, await fixture.AggregateAsync(tenant: "second"));
    }

    [Fact]
    public async Task DeletesRemoveDependenciesAndAggregateWhileOrphanJoinCanRecover()
    {
        await using var fixture = await ProjectionDatabase.CreateAsync();
        var (lease, definition) = await fixture.ReadyAsync();
        await fixture.Store.PromoteAsync(lease, new BlueTuskLogSequenceNumber(100), null);
        await using (var deletedCustomer = fixture.Delivery(200,
            id => new DeleteChange(id, ProjectionDatabase.Customer("customer", "first", "Alice"))))
        {
            await fixture.Store.ApplyAsync(lease, definition, deletedCustomer.Transaction);
        }

        Assert.Null((await fixture.ReadAsync())!.CustomerName);
        Assert.Equal(2, await fixture.CountAsync("dependencies"));
        await using (var insertedCustomer = fixture.Delivery(300,
            id => new InsertChange(id, ProjectionDatabase.Customer("customer", "first", "Returned"))))
        {
            await fixture.Store.ApplyAsync(lease, definition, insertedCustomer.Transaction);
        }

        Assert.Equal("Returned", (await fixture.ReadAsync())!.CustomerName);
        await using (var deletedOrder = fixture.Delivery(400,
            id => new DeleteChange(id, ProjectionDatabase.Order("1", "first", "customer", 10m))))
        {
            await fixture.Store.ApplyAsync(lease, definition, deletedOrder.Transaction);
        }

        Assert.Null(await fixture.ReadAsync());
        Assert.Equal(0, await fixture.CountAsync("dependencies"));
        Assert.Equal(0m, await fixture.AggregateAsync());
    }

    [Fact]
    public async Task ConcurrentDuplicateCdcDeliveriesChangeModelsOnce()
    {
        await using var fixture = await ProjectionDatabase.CreateAsync();
        var (lease, definition) = await fixture.ReadyAsync();
        await using var delivery = fixture.Delivery(200,
            id => new InsertChange(id, ProjectionDatabase.Order("2", "first", "customer", 20m)));
        var tasks = Enumerable.Range(0, 8).Select(_ => fixture.Store.ApplyAsync(lease, definition, delivery.Transaction).AsTask()).ToArray();
        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(results, static value => value.WasApplied);
        Assert.Equal(30m, await fixture.AggregateAsync());
        Assert.Equal(200UL, (await fixture.Store.ReadStateAsync(lease.Identity)).Checkpoint.Value);
    }

    [Fact]
    public async Task OldOwnerAndExpiryCannotCheckpointEvenAfterHandlerWrites()
    {
        await using var fixture = await ProjectionDatabase.CreateAsync();
        var (lease, definition) = await fixture.ReadyAsync();
        Assert.Null(await fixture.Store.AcquireAsync(lease.Identity, "other", TimeSpan.FromMinutes(1)));
        Assert.True(await fixture.Store.ReleaseAsync(lease));
        var replacement = Assert.IsType<ProjectionLease>(await fixture.Store.AcquireAsync(lease.Identity, "other", TimeSpan.FromMinutes(1)));
        Assert.False(await fixture.Store.RenewAsync(lease, TimeSpan.FromMinutes(1)));
        Assert.False(await fixture.Store.ReleaseAsync(lease));
        await using var delivery = fixture.Delivery(200,
            id => new InsertChange(id, ProjectionDatabase.Order("2", "first", "customer", 20m)));
        await Assert.ThrowsAsync<ProjectionFencedException>(async () => await fixture.Store.ApplyAsync(lease, definition, delivery.Transaction));
        var expiring = new ExpiringDefinition(new OrdersProjection(replacement.Identity), fixture.Schema);
        await Assert.ThrowsAsync<ProjectionFencedException>(async () => await fixture.Store.ApplyAsync(replacement, expiring, delivery.Transaction));
        Assert.Equal(10m, await fixture.AggregateAsync());
        Assert.Equal(100UL, (await fixture.Store.ReadStateAsync(lease.Identity)).Checkpoint.Value);
    }

    [Fact]
    public async Task SameVersionCannotRebindDefinitionSourceAndRejectsTwoPhase()
    {
        await using var fixture = await ProjectionDatabase.CreateAsync();
        var (lease, definition) = await fixture.ReadyAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Store.RegisterAsync(
            new ProjectionIdentity("orders", 1, "changed-definition", fixture.Source)));
        var changedSource = new ChangeSourceIdentity("other-system", "test-database", "test-slot", "test-publication");
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Store.RegisterAsync(
            new ProjectionIdentity("orders", 1, lease.Identity.DefinitionFingerprint, changedSource)));
        await using var wrongSource = ChangeDeliveryTestFactory.CreateCommitted(changedSource, 200, new BlueTuskLogSequenceNumber(200));
        await Assert.ThrowsAsync<ArgumentException>(async () => await fixture.Store.ApplyAsync(lease, definition, wrongSource.Transaction));
        await using var prepared = ChangeDeliveryTestFactory.CreateTwoPhase(fixture.Source, 200, new BlueTuskLogSequenceNumber(200),
            ChangeTransactionOutcome.Prepared, "prepared");
        await Assert.ThrowsAsync<ArgumentException>(async () => await fixture.Store.ApplyAsync(lease, definition, prepared.Transaction));
    }

    private sealed class ExpiringDefinition(OrdersProjection inner, string schema) : IProjectionDefinition
    {
        public ProjectionIdentity Identity => inner.Identity;
        public ValueTask ApplySnapshotAsync(ChangeSnapshotBatch batch, ProjectionWriteContext context, CancellationToken cancellationToken) =>
            inner.ApplySnapshotAsync(batch, context, cancellationToken);

        public async ValueTask ApplyTransactionAsync(ChangeTransaction transaction, ProjectionWriteContext context, CancellationToken cancellationToken)
        {
            await inner.ApplyTransactionAsync(transaction, context, cancellationToken);
            await using var command = context.Connection.CreateCommand();
            command.Transaction = context.Transaction;
            command.CommandText = $"UPDATE \"{schema}\".state SET expires_at = clock_timestamp() - interval '1 second'";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
