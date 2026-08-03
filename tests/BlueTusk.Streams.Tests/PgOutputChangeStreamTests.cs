using System.Runtime.CompilerServices;
using System.Text;
using BlueTusk.Replication;
using BlueTusk.Replication.PgOutput;
using BlueTusk.TypeSystem;

namespace BlueTusk.Streams.Tests;

public sealed class PgOutputChangeStreamTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Ordinary_transaction_preserves_order_identity_and_explicit_row_states()
    {
        var sourceIdentity = SourceIdentity();
        var messages = new[]
        {
            Envelope(Relation(
                new BlueTuskPgOutputRelationColumn(BlueTuskPgOutputRelationColumnOptions.None, "name", 25, -1),
                new BlueTuskPgOutputRelationColumn(BlueTuskPgOutputRelationColumnOptions.Key, "id", 23, -1),
                new BlueTuskPgOutputRelationColumn(BlueTuskPgOutputRelationColumnOptions.None, "description", 25, -1),
                new BlueTuskPgOutputRelationColumn(BlueTuskPgOutputRelationColumnOptions.None, "optional", 25, -1))),
            Envelope(new BlueTuskPgOutputBegin(Lsn(150), Timestamp, 42)),
            Envelope(new BlueTuskPgOutputInsert(
                null,
                7,
                Tuple(Text("alpha"), Text("1"), Toast()))),
            Envelope(new BlueTuskPgOutputUpdate(
                null,
                7,
                null,
                null,
                Tuple(Text("beta"), Text("1"), Toast(), Null()))),
            Envelope(new BlueTuskPgOutputDelete(
                null,
                7,
                BlueTuskPgOutputOldRowKind.Key,
                Tuple(Null(), Text("1"), Null(), Null()))),
            Envelope(new BlueTuskPgOutputLogicalMessage(
                null,
                true,
                Lsn(175),
                "audit",
                Encoding.UTF8.GetBytes("changed"))),
            Envelope(new BlueTuskPgOutputCommit(Lsn(190), Lsn(200), Timestamp.AddSeconds(1))),
        };
        var stream = new PgOutputChangeStream(Messages(messages), sourceIdentity);

        await using var enumerator = stream.ReadTransactionsAsync().GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        var delivery = enumerator.Current;
        var transaction = delivery.Transaction;
        var changes = await transaction.Changes.MaterializeAsync();

        Assert.Equal(42U, transaction.TransactionId);
        Assert.Equal(Lsn(200), transaction.CommitEndPosition);
        Assert.Equal(4, changes.Count);
        Assert.All(changes, change =>
        {
            Assert.Equal(sourceIdentity, change.Id.Source);
            Assert.Equal(Lsn(200), change.Id.CommitEndPosition);
            Assert.Equal(42U, change.Id.TransactionId);
        });
        Assert.Equal([0, 1, 2, 3], changes.Select(change => change.Id.Ordinal));

        var insert = Assert.IsType<InsertChange>(changes[0]);
        Assert.Equal(ChangeColumnState.Value, insert.NewRow["name"].State);
        Assert.Equal(ChangeColumnState.Value, insert.NewRow["id"].State);
        Assert.Equal(ChangeColumnState.UnchangedToast, insert.NewRow["description"].State);
        Assert.Equal(ChangeColumnState.NotPublished, insert.NewRow["optional"].State);

        var update = Assert.IsType<UpdateChange>(changes[1]);
        Assert.All(update.OldRow.Values, value => Assert.Equal(ChangeColumnState.OldValueUnavailable, value.State));
        Assert.False(update.ChangedColumns.IsExact);
        Assert.Empty(update.ChangedColumns.Ordinals);
        Assert.Equal(ChangeColumnState.DatabaseNull, update.NewRow["optional"].State);

        var delete = Assert.IsType<DeleteChange>(changes[2]);
        Assert.Equal(ChangeColumnState.OldValueUnavailable, delete.OldRow["name"].State);
        Assert.Equal("1", Encoding.UTF8.GetString(delete.OldRow["id"].Data.Span));

        var logical = Assert.IsType<LogicalMessageChange>(changes[3]);
        Assert.True(logical.IsTransactional);
        Assert.Equal("audit", logical.Prefix);

        await delivery.AcknowledgeAsync();
        Assert.Equal(ChangeDeliveryState.Acknowledged, delivery.State);
        Assert.False(await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task Full_old_row_produces_an_exact_changed_column_set()
    {
        var messages = new[]
        {
            Envelope(Relation(
                new BlueTuskPgOutputRelationColumn(BlueTuskPgOutputRelationColumnOptions.Key, "id", 23, -1),
                new BlueTuskPgOutputRelationColumn(BlueTuskPgOutputRelationColumnOptions.None, "name", 25, -1),
                new BlueTuskPgOutputRelationColumn(BlueTuskPgOutputRelationColumnOptions.None, "payload", 25, -1))),
            Envelope(new BlueTuskPgOutputBegin(Lsn(100), Timestamp, 12)),
            Envelope(new BlueTuskPgOutputUpdate(
                null,
                7,
                BlueTuskPgOutputOldRowKind.Full,
                Tuple(Text("1"), Text("before"), Text("large")),
                Tuple(Text("1"), Text("after"), Toast()))),
            Envelope(new BlueTuskPgOutputCommit(Lsn(120), Lsn(125), Timestamp)),
        };
        var stream = new PgOutputChangeStream(Messages(messages), SourceIdentity());

        await foreach (var delivery in stream.ReadTransactionsAsync())
        {
            var changes = await delivery.Transaction.Changes.MaterializeAsync();
            var update = Assert.IsType<UpdateChange>(Assert.Single(changes));
            Assert.True(update.ChangedColumns.IsExact);
            Assert.Equal([1], update.ChangedColumns.Ordinals);
            await delivery.AcknowledgeAsync();
        }
    }

    [Fact]
    public async Task Streamed_transaction_spills_and_is_removed_after_acknowledgement()
    {
        var spoolDirectory = CreateTemporaryDirectory();
        try
        {
            var messages = new[]
            {
                Envelope(Relation(
                    new BlueTuskPgOutputRelationColumn(BlueTuskPgOutputRelationColumnOptions.Key, "id", 23, -1))),
                Envelope(new BlueTuskPgOutputStreamStart(91, true)),
                Envelope(new BlueTuskPgOutputInsert(91, 7, Tuple(Binary(new byte[4096])))),
                Envelope(new BlueTuskPgOutputStreamStop()),
                Envelope(new BlueTuskPgOutputStreamCommit(91, Lsn(400), Lsn(420), Timestamp)),
            };
            var options = new TransactionAssemblyOptions
            {
                MaxInMemoryTransactionBytes = 64,
                MaxTransactionBytes = 16 * 1024,
                MaxSpoolBytes = 32 * 1024,
                SpoolDirectory = spoolDirectory,
            };
            var stream = new PgOutputChangeStream(Messages(messages), SourceIdentity(), options);

            await using var enumerator = stream.ReadTransactionsAsync().GetAsyncEnumerator();
            Assert.True(await enumerator.MoveNextAsync());
            var delivery = enumerator.Current;
            Assert.True(delivery.Transaction.Changes.IsSpooled);
            Assert.Single(Directory.GetFiles(spoolDirectory, "*.ready"));
            var change = Assert.IsType<InsertChange>(
                Assert.Single(await delivery.Transaction.Changes.MaterializeAsync()));
            Assert.Equal(4096, change.NewRow[0].Data.Length);

            await delivery.AcknowledgeAsync();
            Assert.Empty(Directory.GetFiles(spoolDirectory));
            Assert.False(await enumerator.MoveNextAsync());
        }
        finally
        {
            Directory.Delete(spoolDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Stream_abort_discards_spooled_changes_without_delivery()
    {
        var spoolDirectory = CreateTemporaryDirectory();
        try
        {
            var messages = new[]
            {
                Envelope(Relation(
                    new BlueTuskPgOutputRelationColumn(BlueTuskPgOutputRelationColumnOptions.Key, "id", 23, -1))),
                Envelope(new BlueTuskPgOutputStreamStart(92, true)),
                Envelope(new BlueTuskPgOutputInsert(92, 7, Tuple(Binary(new byte[4096])))),
                Envelope(new BlueTuskPgOutputStreamStop()),
                Envelope(new BlueTuskPgOutputStreamAbort(92, 92, null, null)),
            };
            var stream = new PgOutputChangeStream(
                Messages(messages),
                SourceIdentity(),
                new TransactionAssemblyOptions
                {
                    MaxInMemoryTransactionBytes = 1,
                    MaxTransactionBytes = 16 * 1024,
                    MaxSpoolBytes = 32 * 1024,
                    SpoolDirectory = spoolDirectory,
                });

            await using var enumerator = stream.ReadTransactionsAsync().GetAsyncEnumerator();
            Assert.False(await enumerator.MoveNextAsync());
            Assert.Empty(Directory.GetFiles(spoolDirectory));
        }
        finally
        {
            Directory.Delete(spoolDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Source_disconnect_discards_the_incomplete_epoch_for_safe_redelivery()
    {
        var spoolDirectory = CreateTemporaryDirectory();
        try
        {
            var stream = new PgOutputChangeStream(
                Messages(
                    Envelope(Relation(
                        new BlueTuskPgOutputRelationColumn(BlueTuskPgOutputRelationColumnOptions.Key, "id", 23, -1))),
                    Envelope(new BlueTuskPgOutputStreamStart(93, true)),
                    Envelope(new BlueTuskPgOutputInsert(93, 7, Tuple(Binary(new byte[4096])))),
                    Envelope(new BlueTuskPgOutputStreamStop())),
                SourceIdentity(),
                new TransactionAssemblyOptions
                {
                    MaxInMemoryTransactionBytes = 1,
                    MaxTransactionBytes = 16 * 1024,
                    MaxSpoolBytes = 32 * 1024,
                    SpoolDirectory = spoolDirectory,
                });

            await using var enumerator = stream.ReadTransactionsAsync().GetAsyncEnumerator();
            Assert.False(await enumerator.MoveNextAsync());
            Assert.Empty(Directory.GetFiles(spoolDirectory));
        }
        finally
        {
            Directory.Delete(spoolDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Nontransactional_logical_message_uses_an_explicit_synthetic_transaction()
    {
        var message = new BlueTuskPgOutputLogicalMessage(
            null,
            false,
            Lsn(300),
            "signal",
            Encoding.UTF8.GetBytes("ready"));
        var stream = new PgOutputChangeStream(Messages(Envelope(message)), SourceIdentity());

        await using var enumerator = stream.ReadTransactionsAsync().GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        var delivery = enumerator.Current;
        Assert.True(delivery.Transaction.IsSynthetic);
        Assert.Equal(0U, delivery.Transaction.TransactionId);
        var logical = Assert.IsType<LogicalMessageChange>(
            Assert.Single(await delivery.Transaction.Changes.MaterializeAsync()));
        Assert.False(logical.IsTransactional);
        Assert.Equal(Lsn(300), logical.Id.CommitEndPosition);
        await delivery.AcknowledgeAsync();
        Assert.False(await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task Reading_past_an_unacknowledged_delivery_stops_the_stream()
    {
        var stream = new PgOutputChangeStream(
            Messages(
                Envelope(Relation(
                    new BlueTuskPgOutputRelationColumn(BlueTuskPgOutputRelationColumnOptions.Key, "id", 23, -1))),
                Envelope(new BlueTuskPgOutputBegin(Lsn(10), Timestamp, 1)),
                Envelope(new BlueTuskPgOutputInsert(null, 7, Tuple(Text("1")))),
                Envelope(new BlueTuskPgOutputCommit(Lsn(20), Lsn(21), Timestamp))),
            SourceIdentity());

        await using var enumerator = stream.ReadTransactionsAsync().GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        var delivery = enumerator.Current;
        var exception = await Assert.ThrowsAsync<ChangeDeliveryNotAcknowledgedException>(
            async () => await enumerator.MoveNextAsync().AsTask());
        Assert.Equal(ChangeDeliveryState.Active, exception.State);
        Assert.Equal(ChangeDeliveryState.Disposed, delivery.State);
    }

    [Fact]
    public async Task File_spool_detects_record_tampering()
    {
        var spoolDirectory = CreateTemporaryDirectory();
        try
        {
            var spool = new FileTransactionSpool(
                new FileTransactionSpoolOptions
                {
                    DirectoryPath = spoolDirectory,
                    MaxStorageBytes = 4096,
                    MaxRecordBytes = 1024,
                });
            await using var writer = await spool.CreateAsync(new TransactionSpoolKey("source", 1));
            await writer.AppendAsync(Encoding.UTF8.GetBytes("integrity"));
            await using var reader = await writer.CompleteAsync();
            var path = Assert.Single(Directory.GetFiles(spoolDirectory, "*.ready"));
            var bytes = await File.ReadAllBytesAsync(path);
            bytes[^9] ^= 0xFF;
            await File.WriteAllBytesAsync(path, bytes);

            await Assert.ThrowsAsync<TransactionSpoolIntegrityException>(async () =>
            {
                await foreach (var _ in reader.ReadRecordsAsync())
                {
                }
            });
        }
        finally
        {
            Directory.Delete(spoolDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Transaction_size_limit_fails_before_unbounded_growth()
    {
        var spoolDirectory = CreateTemporaryDirectory();
        try
        {
            var stream = new PgOutputChangeStream(
                Messages(
                    Envelope(Relation(
                        new BlueTuskPgOutputRelationColumn(BlueTuskPgOutputRelationColumnOptions.Key, "id", 23, -1))),
                    Envelope(new BlueTuskPgOutputBegin(Lsn(10), Timestamp, 1)),
                    Envelope(new BlueTuskPgOutputInsert(null, 7, Tuple(Binary(new byte[2048]))))),
                SourceIdentity(),
                new TransactionAssemblyOptions
                {
                    MaxInMemoryTransactionBytes = 128,
                    MaxTransactionBytes = 1024,
                    MaxSpoolBytes = 4096,
                    SpoolDirectory = spoolDirectory,
                });

            await using var enumerator = stream.ReadTransactionsAsync().GetAsyncEnumerator();
            await Assert.ThrowsAsync<TransactionAssemblyLimitExceededException>(
                async () => await enumerator.MoveNextAsync().AsTask());
            Assert.Empty(Directory.GetFiles(spoolDirectory));
        }
        finally
        {
            Directory.Delete(spoolDirectory, recursive: true);
        }
    }

    private static ChangeSourceIdentity SourceIdentity() =>
        new("739463", "app", "bluetusk_test", "public:orders");

    private static BlueTuskPgOutputRelation Relation(params BlueTuskPgOutputRelationColumn[] columns) =>
        new(null, 7, "public", "orders", 'd', columns);

    private static BlueTuskPgOutputEnvelope Envelope(BlueTuskPgOutputMessage message) =>
        new(new BlueTuskXLogData(Lsn(1), Lsn(500), Timestamp, ReadOnlyMemory<byte>.Empty), message);

    private static BlueTuskLogSequenceNumber Lsn(ulong value) => new(value);

    private static BlueTuskPgOutputTuple Tuple(params BlueTuskPgOutputTupleValue[] values) => new(values);

    private static BlueTuskPgOutputTupleValue Text(string value) =>
        new(BlueTuskPgOutputTupleValueKind.Text, Encoding.UTF8.GetBytes(value));

    private static BlueTuskPgOutputTupleValue Binary(byte[] value) =>
        new(BlueTuskPgOutputTupleValueKind.Binary, value);

    private static BlueTuskPgOutputTupleValue Null() =>
        new(BlueTuskPgOutputTupleValueKind.Null, ReadOnlyMemory<byte>.Empty);

    private static BlueTuskPgOutputTupleValue Toast() =>
        new(BlueTuskPgOutputTupleValueKind.UnchangedToast, ReadOnlyMemory<byte>.Empty);

    private static async IAsyncEnumerable<BlueTuskPgOutputEnvelope> Messages(
        IEnumerable<BlueTuskPgOutputEnvelope> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return message;
        }
    }

    private static IAsyncEnumerable<BlueTuskPgOutputEnvelope> Messages(
        params BlueTuskPgOutputEnvelope[] messages) => Messages((IEnumerable<BlueTuskPgOutputEnvelope>)messages);

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "bluetusk-streams-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
