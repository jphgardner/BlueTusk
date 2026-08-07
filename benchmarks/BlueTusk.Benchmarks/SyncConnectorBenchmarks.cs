using System.Text;
using BenchmarkDotNet.Attributes;
using BlueTusk.Streams;
using BlueTusk.Streams.Testing;
using BlueTusk.Sync;
using BlueTusk.Sync.Nats;
using BlueTusk.Sync.OpenSearch;
using BlueTusk.Sync.PostgreSql;
using BlueTusk.TypeSystem;

namespace BlueTusk.Benchmarks;

[MemoryDiagnoser]
[JsonExporterAttribute.Brief]
public class SyncConnectorBenchmarks
{
    private readonly ChangeSourceIdentity _source =
        new("benchmark-system", "benchmark", "benchmark-slot", "public:orders");
    private ChangeTransactionDelivery _delivery = null!;
    private SyncTransformVersion _transform = null!;
    private SyncMutation[] _mutations = null!;
    private SyncTransactionBatch _batch = null!;
    private byte[] _json = null!;

    [GlobalSetup]
    public void Setup()
    {
        _delivery = ChangeDeliveryTestFactory.CreateCommitted(
            _source,
            transactionId: 42,
            new BlueTuskLogSequenceNumber(105),
            commitTimestamp: DateTimeOffset.UnixEpoch);
        _transform = SyncTransformVersion.Create("orders", "benchmark-v1");
        _json = Encoding.UTF8.GetBytes(
            "{\"id\":\"42\",\"payload\":\"" + new string('x', 32 * 1024) + "\"}");
        _mutations =
        [
            new SyncMutation(
                new ChangeId(
                    _source,
                    new BlueTuskLogSequenceNumber(105),
                    42,
                    0),
                SyncMutationKind.Upsert,
                "orders",
                "42",
                _json,
                "application/json"),
        ];
        _batch = new SyncTransactionBatch(
            "orders",
            _transform,
            _delivery.Transaction,
            _mutations);
    }

    [Benchmark(Baseline = true)]
    public SyncTransactionBatch ConstructCoreTransactionBatch() =>
        new("orders", _transform, _delivery.Transaction, _mutations);

    [Benchmark]
    public byte[] EncodeNatsTransactionEnvelope() =>
        NatsSyncEnvelopeCodec.EncodeTransaction(_batch);

    [Benchmark]
    public int ValidateOpenSearchDocument()
    {
        OpenSearchSyncDestination.ValidateJsonDocument(_json, "application/json");
        return _json.Length;
    }

    [Benchmark]
    public byte[] CopyPostgreSqlParameterPayload() =>
        PostgreSqlDocumentMutationWriter.CopyParameterPayload(_json);

    [GlobalCleanup]
    public void Cleanup() =>
        _delivery.DisposeAsync().AsTask().GetAwaiter().GetResult();
}
