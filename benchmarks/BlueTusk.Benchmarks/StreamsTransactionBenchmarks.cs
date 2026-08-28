using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BlueTusk.Replication;
using BlueTusk.Replication.PgOutput;
using BlueTusk.Streams;
using BlueTusk.TypeSystem;

namespace BlueTusk.Benchmarks;

[MemoryDiagnoser]
[JsonExporterAttribute.Brief]
public class StreamsTransactionBenchmarks
{
    private readonly ChangeSourceIdentity _sourceIdentity =
        new("benchmark-system", "benchmark", "benchmark_slot", "public:benchmark");
    private BlueTuskPgOutputEnvelope[] _ordinaryTransaction = null!;
    private BlueTuskPgOutputEnvelope[] _largeStreamedTransaction = null!;
    private string _spoolDirectory = null!;

    [Params(1, 1_000)]
    public int TransactionChangeCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _spoolDirectory = Path.Combine(
            Path.GetTempPath(),
            "bluetusk-streams-benchmarks",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_spoolDirectory);
        _ordinaryTransaction = CreateOrdinaryTransaction();
        _largeStreamedTransaction = CreateLargeStreamedTransaction();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_spoolDirectory))
        {
            Directory.Delete(_spoolDirectory, recursive: true);
        }
    }

    [Benchmark]
    public async Task<int> AssembleAndMaterializeOneThousandInserts()
    {
        var stream = new PgOutputChangeStream(Messages(_ordinaryTransaction), _sourceIdentity);
        await using var enumerator = stream.ReadTransactionsAsync().GetAsyncEnumerator();
        _ = await enumerator.MoveNextAsync();
        var delivery = enumerator.Current;
        var changes = await delivery.Transaction.Changes.MaterializeAsync();
        var checksum = 0;
        foreach (var change in changes)
        {
            checksum += BitConverter.ToInt32(((InsertChange)change).NewRow["id"].Data.Span);
        }

        await delivery.AcknowledgeAsync();
        return checksum;
    }

    [Benchmark]
    public async Task<int> SpillAndStreamFourMiBTransaction()
    {
        var stream = new PgOutputChangeStream(
            Messages(_largeStreamedTransaction),
            _sourceIdentity,
            new TransactionAssemblyOptions
            {
                MaxInMemoryTransactionBytes = 64 * 1024,
                MaxTransactionBytes = 8 * 1024 * 1024,
                MaxSpoolBytes = 16 * 1024 * 1024,
                SpoolDirectory = _spoolDirectory,
            });
        await using var enumerator = stream.ReadTransactionsAsync().GetAsyncEnumerator();
        _ = await enumerator.MoveNextAsync();
        var delivery = enumerator.Current;
        if (!delivery.Transaction.Changes.IsSpooled)
        {
            throw new InvalidOperationException("The benchmark transaction did not cross the spool threshold.");
        }

        var changes = await delivery.Transaction.Changes.MaterializeAsync();
        var data = ((InsertChange)changes[0]).NewRow[0].Data.Span;
        var checksum = data.Length ^ data[0] ^ data[^1] ^ data[data.Length / 2];
        await delivery.AcknowledgeAsync();
        return checksum;
    }

    private BlueTuskPgOutputEnvelope[] CreateOrdinaryTransaction()
    {
        var messages = new BlueTuskPgOutputEnvelope[TransactionChangeCount + 3];
        messages[0] = Envelope(Relation(streamingTransactionId: null));
        messages[1] = Envelope(new BlueTuskPgOutputBegin(Lsn(10), DateTimeOffset.UnixEpoch, 1));
        for (var index = 0; index < TransactionChangeCount; index++)
        {
            messages[index + 2] = Envelope(
                new BlueTuskPgOutputInsert(
                    null,
                    1,
                    new BlueTuskPgOutputTuple(
                        [
                            new BlueTuskPgOutputTupleValue(
                                BlueTuskPgOutputTupleValueKind.Binary,
                                BitConverter.GetBytes(index)),
                        ])));
        }

        messages[^1] = Envelope(new BlueTuskPgOutputCommit(Lsn(20), Lsn(21), DateTimeOffset.UnixEpoch));
        return messages;
    }

    private static BlueTuskPgOutputEnvelope[] CreateLargeStreamedTransaction()
    {
        var payload = new byte[4 * 1024 * 1024];
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = unchecked((byte)index);
        }

        return
        [
            Envelope(Relation(2)),
            Envelope(new BlueTuskPgOutputStreamStart(2, true)),
            Envelope(
                new BlueTuskPgOutputInsert(
                    2,
                    1,
                    new BlueTuskPgOutputTuple(
                        [
                            new BlueTuskPgOutputTupleValue(
                                BlueTuskPgOutputTupleValueKind.Binary,
                                payload),
                        ]))),
            Envelope(new BlueTuskPgOutputStreamStop()),
            Envelope(new BlueTuskPgOutputStreamCommit(2, Lsn(30), Lsn(31), DateTimeOffset.UnixEpoch)),
        ];
    }

    private static BlueTuskPgOutputRelation Relation(uint? streamingTransactionId) =>
        new(
            streamingTransactionId,
            1,
            "public",
            "benchmark",
            'd',
            [
                new BlueTuskPgOutputRelationColumn(
                    BlueTuskPgOutputRelationColumnOptions.Key,
                    "id",
                    23,
                    -1),
            ]);

    private static BlueTuskPgOutputEnvelope Envelope(BlueTuskPgOutputMessage message) =>
        BlueTuskPgOutputEnvelope.CreateOwned(
            new BlueTuskXLogData(Lsn(1), Lsn(100), DateTimeOffset.UnixEpoch, ReadOnlyMemory<byte>.Empty),
            message);

    private static BlueTuskLogSequenceNumber Lsn(ulong value) => new(value);

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
}
