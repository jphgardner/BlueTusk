using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BlueTusk.Events;

namespace BlueTusk.Projections.LoadHarness;

/// <summary>CPU/allocation measurements of the exercised immutable event/document admission paths.</summary>
[MemoryDiagnoser]
[JsonExporterAttribute.Brief]
public class EventAdmissionBenchmarks
{
    private readonly Guid _id = Guid.Parse("55555555-1111-2222-3333-444444444444");
    private LoadEvent _event = null!;
    private byte[] _payload = null!;
    private byte[] _document = null!;

    [Params(128, 4096, 65536)]
    public int PayloadBytes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _event = new(_id, "tenant_000", "0000", false, new string('p', Math.Max(0, PayloadBytes - 180)));
        _payload = JsonSerializer.SerializeToUtf8Bytes(_event, ReportJson.Default.LoadEvent);
        _document = JsonSerializer.SerializeToUtf8Bytes(new OrderView("0000", "tenant_000", new string('c', PayloadBytes), 42, 1), ReportJson.Default.OrderView);
    }

    [Benchmark]
    public byte[] SourceGeneratedEventSerialization() => JsonSerializer.SerializeToUtf8Bytes(_event, ReportJson.Default.LoadEvent);

    [Benchmark]
    public EventWrite ImmutableOutboxAdmission() => new(_id, "load.order", 1, DateTimeOffset.UnixEpoch, _payload);

    [Benchmark]
    public object SourceGeneratedPublishedDocumentRead() => JsonSerializer.Deserialize(_document, ReportJson.Default.OrderView)!;
}
