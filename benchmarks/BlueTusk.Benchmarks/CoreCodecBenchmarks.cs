using BenchmarkDotNet.Attributes;
using BlueTusk.TypeSystem;

namespace BlueTusk.Benchmarks;

[MemoryDiagnoser]
[JsonExporterAttribute.Brief]
public class CoreCodecBenchmarks
{
    private readonly BlueTuskTimestampCodec _timestampCodec = new();
    private readonly BlueTuskNumericCodec _numericCodec = new();
    private readonly BlueTuskJsonbCodec _jsonbCodec = new();
    private readonly byte[] _timestamp = new byte[sizeof(long)];
    private readonly byte[] _numeric = Convert.FromHexString("0003000100000004000109291A85");
    private readonly byte[] _jsonb = [1, .. "{\"answer\":42}"u8.ToArray()];

    [Benchmark(Baseline = true)]
    public DateTime ReadTimestampBinary()
    {
        var reader = new BlueTuskReader(_timestamp);
        return _timestampCodec.ReadTyped(
            ref reader,
            BlueTuskDataFormat.Binary,
            BlueTuskBuiltInTypes.Timestamp);
    }

    [Benchmark]
    public BlueTuskNumeric ReadNumericBinary()
    {
        var reader = new BlueTuskReader(_numeric);
        return _numericCodec.ReadTyped(
            ref reader,
            BlueTuskDataFormat.Binary,
            BlueTuskBuiltInTypes.Numeric);
    }

    [Benchmark]
    public string ReadJsonbBinary()
    {
        var reader = new BlueTuskReader(_jsonb);
        return _jsonbCodec.ReadTyped(
            ref reader,
            BlueTuskDataFormat.Binary,
            BlueTuskBuiltInTypes.Jsonb);
    }
}
