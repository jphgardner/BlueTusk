using System.Buffers;

namespace BlueTusk.Protocol.Tests;

internal sealed class SegmentedBuffer : ReadOnlySequenceSegment<byte>
{
    private SegmentedBuffer(ReadOnlyMemory<byte> memory)
    {
        Memory = memory;
    }

    public static ReadOnlySequence<byte> Create(params ReadOnlyMemory<byte>[] segments)
    {
        if (segments.Length == 0)
        {
            return ReadOnlySequence<byte>.Empty;
        }

        var first = new SegmentedBuffer(segments[0]);
        var last = first;
        foreach (var segment in segments.Skip(1))
        {
            var next = new SegmentedBuffer(segment)
            {
                RunningIndex = last.RunningIndex + last.Memory.Length,
            };
            last.Next = next;
            last = next;
        }

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }
}

