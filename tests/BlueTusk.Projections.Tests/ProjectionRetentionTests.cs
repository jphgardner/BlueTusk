using BlueTusk.Streams;

namespace BlueTusk.Projections.Tests;

public sealed class ProjectionRetentionTests
{
    [Fact]
    public async Task RetiredUnpublishedVersionIsPermanentlyFencedAndPrunedInBoundedBatchesWithoutDeletingIdentity()
    {
        await using var db = await ProjectionDatabase.CreateAsync();
        var (active, _) = await db.ReadyAsync();
        await db.Store.PromoteAsync(active, new(100), null);
        var (old, definition) = await db.ReadyAsync(2, 3);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await db.Store.RetireAsync(active, 1));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await db.Store.RetireAsync(old, 3));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await db.Store.PruneRetiredAsync(old.Identity));
        var publication = await db.Store.ReadPublicationAsync("orders");
        await db.Store.RetireAsync(old, 1);
        Assert.Equal(publication, await db.Store.ReadPublicationAsync("orders"));
        Assert.Null(await db.Store.AcquireAsync(old.Identity, "replacement", TimeSpan.FromMinutes(1)));
        await Assert.ThrowsAsync<ProjectionRetiredException>(async () => await db.Store.RegisterAsync(old.Identity));
        await using var delivery = db.Delivery(101, id => new InsertChange(id, ProjectionDatabase.Order("4", "first", "customer", 1m)));
        await Assert.ThrowsAsync<ProjectionRetiredException>(async () => await db.Store.ApplyAsync(old, definition, delivery.Transaction));
        await Assert.ThrowsAsync<ProjectionRetiredException>(async () => await db.Store.PromoteAsync(old, new(100), 1));
        ProjectionRetentionResult result;
        var total = 0;
        do
        {
            result = await db.Store.PruneRetiredAsync(old.Identity, 2);
            Assert.InRange(result.DeletedRows, 0, 2);
            total += result.DeletedRows;
            Assert.True(total < 100);
        } while (result.HasRemainingRows);
        Assert.True(total > 10);
        Assert.Equal(new ProjectionRetentionResult(0, false), await db.Store.PruneRetiredAsync(old.Identity, 2));
        Assert.Equal("Alice", (await db.ReadAsync())!.CustomerName);
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM \"{db.Schema}\".state WHERE projection='orders' AND version=2";
        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task PublicationRevisionMigrationFromSchemaOneIsIdempotent()
    {
        await using var db = await ProjectionDatabase.CreateAsync();
        await using (var connection = await db.DataSource.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"ALTER TABLE \"{db.Schema}\".heads DROP COLUMN publication_revision; DROP TABLE \"{db.Schema}\".retired_versions; UPDATE \"{db.Schema}\".schema_version SET version=1";
            await command.ExecuteNonQueryAsync();
        }
        await db.Store.InitializeAsync();
        await db.Store.InitializeAsync();
        var (lease, _) = await db.ReadyAsync();
        await db.Store.PromoteAsync(lease, new(100), null);
        Assert.Equal(new ProjectionPublication(1, 1), await db.Store.ReadPublicationAsync("orders"));
    }

    [Fact]
    public async Task ThrowingMetricsCallbacksCannotChangeCommittedProjectionOrRevision()
    {
        using var listener = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = static (instrument, l) => { if (instrument.Meter.Name == "BlueTusk.Projections") { l.EnableMeasurementEvents(instrument); } }
        };
        listener.SetMeasurementEventCallback<long>(static (_, _, _, _) => throw new InvalidOperationException("Faulty telemetry observer."));
        listener.SetMeasurementEventCallback<double>(static (_, _, _, _) => throw new InvalidOperationException("Faulty telemetry observer."));
        listener.Start();
        await using var db = await ProjectionDatabase.CreateAsync();
        var (lease, definition) = await db.ReadyAsync();
        await db.Store.PromoteAsync(lease, new(100), null);
        await using var delivery = db.Delivery(101, id => new InsertChange(id, ProjectionDatabase.Order("2", "first", "customer", 20m)));
        await db.Store.ApplyAsync(lease, definition, delivery.Transaction);
        Assert.Equal(30m, (await db.Store.ReadActiveAggregateAsync("orders", "first", "all", "total")).Value);
        Assert.Equal(new ProjectionPublication(1, 2), await db.Store.ReadPublicationAsync("orders"));
    }
}
