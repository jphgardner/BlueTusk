using System.Buffers;

namespace BlueTusk.Protocol;

/// <summary>A zero-copy view over a complete backend message payload.</summary>
/// <remarks>The payload remains valid only while its source buffer is retained.</remarks>
public readonly record struct BlueTuskBackendMessage(byte Code, ReadOnlySequence<byte> Payload)
{
    public long Length => Payload.Length;

    public char Identifier => (char)Code;

    public byte[] ToPayloadArray() => Payload.ToArray();
}

