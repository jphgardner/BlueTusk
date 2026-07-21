using System.Buffers.Binary;

namespace BlueTusk.Protocol.Tests;

internal static class FakePostgreSqlMessageStream
{
    public static byte[] BackendMessage(byte code, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[1 + sizeof(int) + payload.Length];
        frame[0] = code;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), sizeof(int) + payload.Length);
        payload.CopyTo(frame.AsSpan(5));
        return frame;
    }

    public static IEnumerable<System.Buffers.ReadOnlySequence<byte>> EveryTwoSegmentSplit(byte[] frame)
    {
        for (var split = 0; split <= frame.Length; split++)
        {
            yield return SegmentedBuffer.Create(
                frame.AsMemory(0, split),
                frame.AsMemory(split));
        }
    }
}

