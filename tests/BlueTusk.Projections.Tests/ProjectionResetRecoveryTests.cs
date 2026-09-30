using System.Globalization;
using BlueTusk.Streams;

namespace BlueTusk.Projections.Tests;

public sealed class ProjectionResetRecoveryTests
{
    [Fact]
    public async Task LargeUnpublishedResetHasDurablePhaseExactEpochBoundedDeletesAndFencedCrossInstanceRecovery()
    {
        await using var db = await ProjectionDatabase.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        var (published, _) = await db.ReadyAsync();
        await db.Store.PromoteAsync(published, new(100), null, token);
        var (oldLease, definition) = await db.ReadyAsync(2, 2000);
        definition.DependencyPageSize = 37;
        var publication = await db.Store.ReadPublicationAsync("orders", token);
        var originalRows = await CountDerivedAsync(db, token);
        Assert.True(originalRows > 10_000);
        var replacement = new SnapshotStart(SnapshotEpoch.Create(db.Source, new(300)), 2);
        Assert.False((await db.Store.BeginSnapshotResetAsync(oldLease, replacement, token)).IsComplete);
        Assert.Equal(originalRows, await CountDerivedAsync(db, token));
        Assert.Equal(ProjectionBuildPhase.Resetting, (await db.Store.ReadStateAsync(oldLease.Identity, token)).Phase);
        var snapshotBatch = ProjectionDatabase.Batch(replacement.Epoch, ProjectionDatabase.Orders, 0,
            [ProjectionDatabase.Order("replacement", "first", "customer", 7m)], true);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await db.Store.ApplySnapshotAsync(oldLease, definition, snapshotBatch, token));
        await using var cdc = db.Delivery(400, id => new InsertChange(id, ProjectionDatabase.Order("late", "first", "customer", 9m)));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await db.Store.ApplyAsync(oldLease, definition, cdc.Transaction, token));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await db.Store.CompleteSnapshotAsync(oldLease, new(replacement.Epoch, 0, 2), token));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await db.Store.PromoteAsync(oldLease, new(0), 1, token));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await db.Store.BeginSnapshotResetAsync(oldLease,
            new(SnapshotEpoch.Create(db.Source, new(301)), 2), token));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await db.Store.ContinueSnapshotResetAsync(oldLease,
            replacement with { TableCount = 1 }, 37, token));
        var removed = 0;
        for (var iteration = 0; iteration < 4; iteration++)
        {
            var before = await CountDerivedAsync(db, token);
            var parallel = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
                await db.Store.ContinueSnapshotResetAsync(oldLease, replacement, 37, token)));
            Assert.All(parallel, progress => Assert.InRange(progress.DeletedRows, 1, 37));
            var sum = parallel.Sum(static progress => progress.DeletedRows);
            Assert.Equal(before - sum, await CountDerivedAsync(db, token));
            removed += sum;
        }
        Assert.True(await db.Store.ReleaseAsync(oldLease, token));
        var otherProcessStore = new PostgreSqlProjectionStore(db.DataSource, new PostgreSqlProjectionsOptions { Schema = db.Schema, MaximumResetBatchRows = 37 });
        var recovered = Assert.IsType<ProjectionLease>(await otherProcessStore.AcquireAsync(oldLease.Identity, "recovered", TimeSpan.FromMinutes(2), token));
        Assert.True(recovered.FencingToken > oldLease.FencingToken);
        var persisted = Assert.IsType<SnapshotStart>(await otherProcessStore.ReadSnapshotResetAsync(recovered.Identity, token));
        Assert.Equal(replacement.Epoch.Value, persisted.Epoch.Value);
        Assert.Equal(replacement.Epoch.ConsistentPosition, persisted.Epoch.ConsistentPosition);
        Assert.Equal(replacement.TableCount, persisted.TableCount);
        replacement = persisted;
        var beforeFailure = await CountDerivedAsync(db, token);
        await Assert.ThrowsAsync<ProjectionFencedException>(async () => await db.Store.ContinueSnapshotResetAsync(oldLease, replacement, 37, token));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await otherProcessStore.ContinueSnapshotResetAsync(recovered, replacement, 37, canceled.Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await otherProcessStore.ContinueSnapshotResetAsync(recovered, replacement, 38, token));
        Assert.Equal(beforeFailure, await CountDerivedAsync(db, token));
        Assert.False((await otherProcessStore.BeginSnapshotResetAsync(recovered, replacement, token)).IsComplete);
        ProjectionSnapshotResetProgress progress;
        do
        {
            var before = await CountDerivedAsync(db, token);
            progress = await otherProcessStore.ContinueSnapshotResetAsync(recovered, replacement, 37, token);
            Assert.InRange(progress.DeletedRows, 0, 37);
            Assert.Equal(before - progress.DeletedRows, await CountDerivedAsync(db, token));
            removed += progress.DeletedRows;
        } while (!progress.IsComplete);
        Assert.Equal(originalRows, removed);
        Assert.Equal(0, await CountDerivedAsync(db, token));
        Assert.Equal(ProjectionBuildPhase.Snapshot, (await otherProcessStore.ReadStateAsync(recovered.Identity, token)).Phase);
        Assert.Null(await otherProcessStore.ReadSnapshotResetAsync(recovered.Identity, token));
        Assert.Equal(new ProjectionSnapshotResetProgress(replacement.Epoch.Value, 0, true), await otherProcessStore.ContinueSnapshotResetAsync(recovered, replacement, 37, token));
        await otherProcessStore.StartSnapshotAsync(recovered, replacement, token);
        await otherProcessStore.ApplySnapshotAsync(recovered, definition, snapshotBatch, token);
        await otherProcessStore.ApplySnapshotAsync(recovered, definition,
            ProjectionDatabase.Batch(replacement.Epoch, ProjectionDatabase.Customers, 0, [ProjectionDatabase.Customer("customer", "first", "New")], true), token);
        await otherProcessStore.CompleteSnapshotAsync(recovered, new(replacement.Epoch, 2, 2), token);
        Assert.Equal(7m, await db.AggregateAsync(2));
        Assert.Equal(publication, await db.Store.ReadPublicationAsync("orders", token));
        Assert.Equal("Alice", (await db.ReadAsync())!.CustomerName);
        await otherProcessStore.PromoteAsync(recovered, replacement.Epoch.ConsistentPosition, 1, token);
        Assert.Null(await db.ReadAsync());
        Assert.Equal("New", (await db.ReadAsync(id: "replacement"))!.CustomerName);
    }

    private static async Task<int> CountDerivedAsync(ProjectionDatabase db, CancellationToken token)
    {
        var tables = new[] { "dependencies", "documents", "source_rows", "aggregates", "snapshot_row_keys", "snapshot_batches", "snapshot_tables" };
        await using var connection = await db.DataSource.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + string.Join('+', tables.Select(table => $"(SELECT count(*) FROM \"{db.Schema}\".{table} WHERE projection='orders' AND version=2)"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
    }
}
