using BenchmarkDotNet.Attributes;
using BlueTusk.TypeSystem;

namespace BlueTusk.Benchmarks;

[MemoryDiagnoser]
[JsonExporterAttribute.Brief]
public class Int32CodecBenchmarks
{
    private readonly BlueTuskInt32Codec _codec = new();
    private readonly byte[] _binary = [0, 0, 0, 42];
    private readonly byte[] _text = "42"u8.ToArray();

    [Benchmark(Baseline = true)]
    public int ReadBinary()
    {
        var reader = new BlueTuskReader(_binary);
        return _codec.ReadTyped(ref reader, BlueTuskDataFormat.Binary, BlueTuskBuiltInTypes.Int4);
    }

    [Benchmark]
    public int ReadText()
    {
        var reader = new BlueTuskReader(_text);
        return _codec.ReadTyped(ref reader, BlueTuskDataFormat.Text, BlueTuskBuiltInTypes.Int4);
    }

    [Benchmark]
    public int WriteBinary()
    {
        Span<byte> destination = stackalloc byte[sizeof(int)];
        var writer = new BlueTuskWriter(destination);
        _codec.WriteTyped(ref writer, 42, BlueTuskDataFormat.Binary, BlueTuskBuiltInTypes.Int4);
        return writer.WrittenCount;
    }

    [Benchmark]
    public int WriteText()
    {
        Span<byte> destination = stackalloc byte[16];
        var writer = new BlueTuskWriter(destination);
        _codec.WriteTyped(ref writer, 42, BlueTuskDataFormat.Text, BlueTuskBuiltInTypes.Int4);
        return writer.WrittenCount;
    }
}
