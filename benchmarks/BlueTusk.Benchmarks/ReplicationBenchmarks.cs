using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using BlueTusk.Replication;

namespace BlueTusk.Benchmarks;

[MemoryDiagnoser]
[JsonExporterAttribute.Brief]
public class ReplicationBenchmarks
{
    private const int FrameCount = 1024;
    private readonly byte[][] _frames = CreateFrames();

    [Benchmark]
    public BlueTuskReplicationMessage DecodeOneKilobyteXLogData() =>
        BlueTuskReplicationWireProtocol.Decode(_frames[0]);

    [Benchmark(OperationsPerInvoke = FrameCount)]
    public ulong PullOneThousandBoundedXLogFrames()
    {
        ulong checksum = 0;
        foreach (var frame in _frames)
        {
            var message = (BlueTuskXLogData)BlueTuskReplicationWireProtocol.Decode(frame);
            checksum += message.WalEnd.Value;
            checksum += message.Data.Span[0];
        }

        return checksum;
    }

    private static byte[][] CreateFrames()
    {
        var frames = new byte[FrameCount][];
        for (var index = 0; index < frames.Length; index++)
        {
            var frame = new byte[1024 + 25];
            frame[0] = (byte)'w';
            BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(1), (ulong)index);
            BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(9), (ulong)(index + 1024));
            frame[25] = (byte)index;
            frames[index] = frame;
        }

        return frames;
    }
}
