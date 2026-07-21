using System.Buffers;
using BenchmarkDotNet.Attributes;
using BlueTusk.Protocol;

namespace BlueTusk.Benchmarks;

[MemoryDiagnoser]
[JsonExporterAttribute.Brief]
public class FrontendWriterBenchmarks
{
    private readonly ArrayBufferWriter<byte> _output = new(256);
    private readonly uint[] _parameterTypeOids = [23, 23];
    private readonly BlueTuskBindParameter[] _parameters =
    [
        new(1, new byte[] { 0, 0, 0, 20 }),
        new(1, new byte[] { 0, 0, 0, 22 }),
    ];

    [Benchmark(Baseline = true)]
    public int WriteSimpleQuery()
    {
        _output.Clear();
        BlueTuskFrontendMessageWriter.WriteSimpleQuery(_output, "SELECT 42::int4");
        return _output.WrittenCount;
    }

    [Benchmark]
    public int WriteExtendedQuery()
    {
        _output.Clear();
        BlueTuskFrontendMessageWriter.WriteParse(
            _output,
            string.Empty,
            "SELECT $1::int4 + $2::int4",
            _parameterTypeOids);
        BlueTuskFrontendMessageWriter.WriteBind(_output, string.Empty, string.Empty, _parameters);
        BlueTuskFrontendMessageWriter.WriteDescribePortal(_output, string.Empty);
        BlueTuskFrontendMessageWriter.WriteExecute(_output, string.Empty);
        BlueTuskFrontendMessageWriter.WriteSync(_output);
        return _output.WrittenCount;
    }
}
