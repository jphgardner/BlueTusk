using System.Buffers.Binary;
using System.Text;
using BenchmarkDotNet.Attributes;
using BlueTusk.Protocol;

namespace BlueTusk.Benchmarks;

public enum TransportPipelineWorkload
{
    ByteFragmentedRows,
    LargeField,
    CopyStream,
    CancellationDrain,
}

[MemoryDiagnoser]
[JsonExporterAttribute.Brief]
public class TransportPipelineBenchmarks : IDisposable
{
    private byte[] _batch = null!;
    private ReplayTransport _currentTransport = null!;
    private ReplayTransport _prototypeTransport = null!;
    private BlueTuskProtocolConnection _current = null!;
    private TransportPipelinePrototype _prototype = null!;
    private int _messageCount;

    [ParamsAllValues]
    public TransportPipelineWorkload Workload { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        (_batch, _messageCount, var fragmentSize) = Workload switch
        {
            TransportPipelineWorkload.ByteFragmentedRows =>
                (CreateRepeatedFrames('D', 32, 256), 256, 1),
            TransportPipelineWorkload.LargeField =>
                (CreateRepeatedFrames('D', 1024 * 1024, 1), 1, 4 * 1024),
            TransportPipelineWorkload.CopyStream =>
                (CreateRepeatedFrames('d', 8 * 1024, 128), 128, 1024),
            TransportPipelineWorkload.CancellationDrain =>
                (CreateCancellationDrain(), 2, 2),
            _ => throw new ArgumentOutOfRangeException(nameof(Workload)),
        };
        _currentTransport = new ReplayTransport(_batch, fragmentSize);
        _prototypeTransport = new ReplayTransport(_batch, fragmentSize);
        _current = new BlueTuskProtocolConnection(_currentTransport);
        _prototype = new TransportPipelinePrototype();
    }

    [Benchmark(Baseline = true)]
    public long CurrentArrayPoolSync()
    {
        _currentTransport.Reset();
        return ReadCurrent();
    }

    [Benchmark]
    public long PipelinesPrototypeBlockingSync()
    {
        _prototypeTransport.Reset();
        return _prototype.ReadBatch(_prototypeTransport, _batch.Length, _messageCount);
    }

    [Benchmark]
    public async ValueTask<long> CurrentArrayPoolAsync()
    {
        _currentTransport.Reset();
        var checksum = 0L;
        for (var index = 0; index < _messageCount; index++)
        {
            checksum += TransportPipelinePrototype.Consume(
                await _current.ReadMessageAsync(CancellationToken.None).ConfigureAwait(false));
        }

        return checksum;
    }

    [Benchmark]
    public ValueTask<long> PipelinesPrototypeAsync()
    {
        _prototypeTransport.Reset();
        return _prototype.ReadBatchAsync(
            _prototypeTransport,
            _batch.Length,
            _messageCount);
    }

    public void Dispose()
    {
        _current.Dispose();
        _prototype.Dispose();
        GC.SuppressFinalize(this);
    }

    private long ReadCurrent()
    {
        var checksum = 0L;
        for (var index = 0; index < _messageCount; index++)
        {
            checksum += TransportPipelinePrototype.Consume(_current.ReadMessage());
        }

        return checksum;
    }

    private static byte[] CreateRepeatedFrames(char identifier, int payloadLength, int count)
    {
        var frameLength = payloadLength + 5;
        var result = new byte[checked(frameLength * count)];
        for (var index = 0; index < count; index++)
        {
            var frame = result.AsSpan(index * frameLength, frameLength);
            frame[0] = (byte)identifier;
            BinaryPrimitives.WriteInt32BigEndian(frame[1..], payloadLength + sizeof(int));
            frame[5..].Fill((byte)(index % 251));
        }

        return result;
    }

    private static byte[] CreateCancellationDrain()
    {
        var errorPayload = Encoding.UTF8.GetBytes(
            "SERROR\0C57014\0Mcanceling statement due to user request\0\0");
        var error = CreateFrame('E', errorPayload);
        var ready = CreateFrame('Z', [(byte)'I']);
        return [.. error, .. ready];
    }

    internal static byte[] CreateFrame(char identifier, ReadOnlySpan<byte> payload)
    {
        var result = new byte[payload.Length + 5];
        result[0] = (byte)identifier;
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(1), payload.Length + sizeof(int));
        payload.CopyTo(result.AsSpan(5));
        return result;
    }
}
