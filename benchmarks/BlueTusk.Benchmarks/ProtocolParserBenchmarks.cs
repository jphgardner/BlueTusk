using System.Buffers;
using BenchmarkDotNet.Attributes;
using BlueTusk.Protocol;

namespace BlueTusk.Benchmarks;

[MemoryDiagnoser]
[JsonExporterAttribute.Brief]
public class ProtocolParserBenchmarks
{
    private readonly BlueTuskBackendMessageParser _parser = new();
    private readonly byte[] _frame = [(byte)'D', 0, 0, 0, 10, 0, 1, 0, 0, 0, 0];
    private ReadOnlySequence<byte> _fragmented;

    [GlobalSetup]
    public void Setup()
    {
        var first = new BufferSegment(_frame.AsMemory(0, 3));
        var second = first.Append(_frame.AsMemory(3, 4));
        var third = second.Append(_frame.AsMemory(7));
        _fragmented = new ReadOnlySequence<byte>(first, 0, third, third.Memory.Length);
    }

    [Benchmark(Baseline = true)]
    public long ParseContiguous()
    {
        var input = new ReadOnlySequence<byte>(_frame);
        return _parser.TryParse(ref input, out var message) ? message.Length : -1;
    }

    [Benchmark]
    public long ParseThreeSegments()
    {
        var input = _fragmented;
        return _parser.TryParse(ref input, out var message) ? message.Length : -1;
    }

    private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
    {
        public BufferSegment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        public BufferSegment Append(ReadOnlyMemory<byte> memory)
        {
            var segment = new BufferSegment(memory)
            {
                RunningIndex = RunningIndex + Memory.Length,
            };
            Next = segment;
            return segment;
        }
    }
}
