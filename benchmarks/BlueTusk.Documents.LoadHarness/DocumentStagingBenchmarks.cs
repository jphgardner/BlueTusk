using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BlueTusk.Data;

namespace BlueTusk.Documents.LoadHarness;

[MemoryDiagnoser]
[JsonExporterAttribute.Brief]
public class DocumentStagingBenchmarks : IDisposable
{
    private DocumentStore _store = null!;
    private BlueTuskDataSource _source = null!;
    private PayloadDocument _document = null!;
    private byte[] _serialized = null!;
    [Params(1024, 64 * 1024, 1024 * 1024)]
    public int PayloadBytes { get; set; }
    [GlobalSetup]
    public void Setup()
    {
        _source = BlueTuskDataSource.Create("Host=127.0.0.1;Username=unused;Database=unused;SSL Mode=Disable;Channel Binding=Disable");
        _store = new(_source); _document = new("synthetic", Program.Payload(PayloadBytes), 0);
        _serialized = JsonSerializer.SerializeToUtf8Bytes(_document, HarnessJson.Default.PayloadDocument);
    }
    [Benchmark] public byte[] SourceGeneratedSerialize() => JsonSerializer.SerializeToUtf8Bytes(_document, HarnessJson.Default.PayloadDocument);
    [Benchmark] public PayloadDocument SourceGeneratedDeserialize() => JsonSerializer.Deserialize(_serialized, HarnessJson.Default.PayloadDocument)!;
    [Benchmark]
    public int StageEightDocuments()
    {
        using var session = _store.OpenSession("synthetic");
        for (var row = 0; row < 8; row++) { session.Insert(Program.Collection, row.ToString(System.Globalization.CultureInfo.InvariantCulture), _document); }
        return session.PendingCount;
    }
    [GlobalCleanup] public void Cleanup() => Dispose();
    public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
    protected virtual void Dispose(bool disposing)
    { if (disposing) { _store?.DisposeAsync().AsTask().GetAwaiter().GetResult(); _source?.Dispose(); } }
}
