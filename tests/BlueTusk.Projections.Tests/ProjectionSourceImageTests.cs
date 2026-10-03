using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using BlueTusk.Streams;
using BlueTusk.TypeSystem;

namespace BlueTusk.Projections.Tests;

public sealed class ProjectionSourceImageTests
{
    private static readonly ProjectionDependency Key = new("public.image", "k1");

    [Fact]
    public async Task SourceReadsMatchTheTransactionWithoutRepeatedRoundTripsUntilRawAccess()
    {
        await using var fixture = await ProjectionDatabase.CreateAsync();
        var (lease, _) = await fixture.ReadyAsync();
        await using var delivery = fixture.Delivery(200, id => new InsertChange(id, ProjectionDatabase.Order("2", "first", "customer", 20m)));
        using var commands = new CommandCounter();
        var definition = new ScriptDefinition(lease.Identity, async (context, token) =>
        {
            // A miss reads the database once; the known absence is then remembered.
            Assert.Null(await commands.ExpectAsync(1, () => context.ReadSourceAsync("first", Key, token)));
            Assert.Null(await commands.ExpectAsync(0, () => context.ReadSourceAsync("first", Key, token)));
            await commands.ExpectAsync(1, () => context.UpsertSourcesAsync([new ProjectionSourceWrite("first", Key, Bytes("v1"))], token));
            var first = await commands.ExpectAsync(0, () => context.ReadSourceAsync("first", Key, token));
            Assert.Equal("v1", Text(first));
            // Callers receive private arrays, exactly like repeated database reads.
            Assert.True(MemoryMarshal.TryGetArray(first!.Value, out var segment));
            segment.Array![segment.Offset] = (byte)'X';
            Assert.Equal("v1", Text(await commands.ExpectAsync(0, () => context.ReadSourceAsync("first", Key, token))));
            await commands.ExpectAsync(1, () => context.UpsertSourceAsync("first", Key, Bytes("v2"), token));
            Assert.Equal("v2", Text(await commands.ExpectAsync(0, () => context.ReadSourceAsync("first", Key, token))));
            // Tenant and table are part of the identity.
            Assert.Null(await commands.ExpectAsync(1, () => context.ReadSourceAsync("second", Key, token)));
            Assert.Null(await commands.ExpectAsync(1, () => context.ReadSourceAsync("first", new ProjectionDependency("public.other", "k1"), token)));
            await commands.ExpectAsync(1, () => context.DeleteSourcesAsync([new ProjectionSourceDelete("first", Key)], token));
            Assert.Null(await commands.ExpectAsync(0, () => context.ReadSourceAsync("first", Key, token)));
            await commands.ExpectAsync(1, () => context.UpsertSourcesAsync([new ProjectionSourceWrite("first", Key, Bytes("v3"))], token));
            // The three bulk-document statements share one round trip.
            await commands.ExpectAsync(1, () => context.UpsertManyAsync(
                [new ProjectionDocumentWrite("first", "image", Bytes("{}"), [Key])], token));
            // Raw access can change source rows behind the context, so later reads use the database again.
            var connection = context.Connection;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = context.Transaction;
                command.CommandText = $"UPDATE \"{fixture.Schema}\".source_rows SET payload = @payload WHERE tenant_id = 'first' AND table_id = 'public.image' AND key_id = 'k1'";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "payload";
                parameter.Value = Bytes("v4");
                command.Parameters.Add(parameter);
                Assert.Equal(1, await command.ExecuteNonQueryAsync(token));
            }

            Assert.Equal("v4", Text(await commands.ExpectAsync(1, () => context.ReadSourceAsync("first", Key, token))));
            Assert.Equal("v4", Text(await commands.ExpectAsync(1, () => context.ReadSourceAsync("first", Key, token))));
        });

        var applied = await fixture.Store.ApplyAsync(lease, definition, delivery.Transaction);

        Assert.True(applied.WasApplied);
        Assert.True(definition.Completed);
        Assert.Equal("v4", await ReadSourceRowAsync(fixture));
        Assert.Equal(200UL, (await fixture.Store.ReadStateAsync(lease.Identity)).Checkpoint.Value);
    }

    [Fact]
    public async Task FailedStatementDiscardsTheImageSoAbortedTransactionsStillFailReads()
    {
        await using var fixture = await ProjectionDatabase.CreateAsync();
        var (lease, _) = await fixture.ReadyAsync();
        await using var delivery = fixture.Delivery(200, id => new InsertChange(id, ProjectionDatabase.Order("2", "first", "customer", 20m)));
        var definition = new ScriptDefinition(lease.Identity, async (context, token) =>
        {
            await context.UpsertSourcesAsync([new ProjectionSourceWrite("first", Key, Bytes("v1"))], token);
            Assert.Equal("v1", Text(await context.ReadSourceAsync("first", Key, token)));
            await context.AddAggregateAsync("first", "image", "overflow", decimal.MaxValue, token);
            // The aggregate CHECK rejects the sum and PostgreSQL aborts the transaction.
            await Assert.ThrowsAnyAsync<DbException>(async () => await context.AddAggregateAsync("first", "image", "overflow", decimal.MaxValue, token));
            await Assert.ThrowsAnyAsync<DbException>(async () => await context.ReadSourceAsync("first", Key, token));
        });

        await Assert.ThrowsAnyAsync<DbException>(async () => await fixture.Store.ApplyAsync(lease, definition, delivery.Transaction));

        Assert.True(definition.Completed);
        Assert.Null(await ReadSourceRowAsync(fixture));
        Assert.Equal(100UL, (await fixture.Store.ReadStateAsync(lease.Identity)).Checkpoint.Value);
    }

    [Fact]
    public async Task FencedCheckpointRollsBackPipelinedPublicationAndProjectionWrites()
    {
        await using var fixture = await ProjectionDatabase.CreateAsync();
        var (lease, _) = await fixture.ReadyAsync();
        await fixture.Store.PromoteAsync(lease, new BlueTuskLogSequenceNumber(100), null);
        var revision = await RevisionAsync(fixture);
        await using var delivery = fixture.Delivery(200, id => new InsertChange(id, ProjectionDatabase.Order("2", "first", "customer", 20m)));
        var definition = new ScriptDefinition(lease.Identity, async (context, token) =>
        {
            await context.UpsertSourcesAsync([new ProjectionSourceWrite("first", Key, Bytes("v1"))], token);
            // Expire this owner's lease inside the transaction; the checkpoint must then be fenced.
            await using var command = context.Connection.CreateCommand();
            command.Transaction = context.Transaction;
            command.CommandText = $"UPDATE \"{fixture.Schema}\".state SET expires_at = clock_timestamp() - interval '1 second'";
            await command.ExecuteNonQueryAsync(token);
        });

        await Assert.ThrowsAsync<ProjectionFencedException>(async () => await fixture.Store.ApplyAsync(lease, definition, delivery.Transaction));

        Assert.Null(await ReadSourceRowAsync(fixture));
        Assert.Equal(revision, await RevisionAsync(fixture));
        Assert.Equal(100UL, (await fixture.Store.ReadStateAsync(lease.Identity)).Checkpoint.Value);
    }

    private static ReadOnlyMemory<byte> Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static string? Text(ReadOnlyMemory<byte>? value) => value is null ? null : Encoding.UTF8.GetString(value.Value.Span);

    private static async Task<string?> ReadSourceRowAsync(ProjectionDatabase fixture)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT payload FROM \"{fixture.Schema}\".source_rows WHERE tenant_id = 'first' AND table_id = 'public.image' AND key_id = 'k1'";
        return await command.ExecuteScalarAsync() is byte[] bytes ? Encoding.UTF8.GetString(bytes) : null;
    }

    private static async Task<long> RevisionAsync(ProjectionDatabase fixture)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT publication_revision FROM \"{fixture.Schema}\".heads WHERE projection = 'orders'";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private sealed class ScriptDefinition(ProjectionIdentity identity, Func<ProjectionWriteContext, CancellationToken, Task> script)
        : IProjectionDefinition
    {
        public ProjectionIdentity Identity { get; } = identity;
        public bool Completed { get; private set; }

        public ValueTask ApplySnapshotAsync(ChangeSnapshotBatch batch, ProjectionWriteContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This definition only applies CDC transactions.");

        public async ValueTask ApplyTransactionAsync(ChangeTransaction transaction, ProjectionWriteContext context, CancellationToken cancellationToken)
        {
            await script(context, cancellationToken);
            Completed = true;
        }
    }

    /// <summary>Counts BlueTusk command/batch operations on this test's trace only.</summary>
    private sealed class CommandCounter : IDisposable
    {
        private readonly Activity _root;
        private readonly ActivityListener _listener;
        private int _count;

        public CommandCounter()
        {
            _root = new Activity("projection-source-image-test").SetIdFormat(ActivityIdFormat.W3C).Start();
            var trace = _root.TraceId;
            _listener = new ActivityListener
            {
                ShouldListenTo = static source => source.Name == "BlueTusk.Diagnostics",
                Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                    options.Parent.TraceId == trace ? ActivitySamplingResult.AllData : ActivitySamplingResult.None,
                ActivityStopped = activity =>
                {
                    if (activity.TraceId == trace)
                    {
                        Interlocked.Increment(ref _count);
                    }
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public async Task ExpectAsync(int operations, Func<ValueTask> action)
        {
            var before = Volatile.Read(ref _count);
            await action();
            Assert.Equal(before + operations, Volatile.Read(ref _count));
        }

        public async Task<T> ExpectAsync<T>(int operations, Func<ValueTask<T>> action)
        {
            var before = Volatile.Read(ref _count);
            var result = await action();
            Assert.Equal(before + operations, Volatile.Read(ref _count));
            return result;
        }

        public void Dispose()
        {
            _listener.Dispose();
            _root.Stop();
        }
    }
}
