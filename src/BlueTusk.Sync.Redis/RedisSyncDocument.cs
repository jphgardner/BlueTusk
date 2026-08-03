using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BlueTusk.Sync.Redis;

public sealed record RedisSyncDocument(
    string StableSourceId,
    ReadOnlyMemory<byte> Content,
    string ContentType,
    string? PartitionKey);

public static class RedisSyncDocumentReader
{
    public static RedisSyncDocument Decode(ReadOnlySpan<byte> value) =>
        RedisSyncDocumentCodec.Decode(value);
}

internal static class RedisSyncDocumentCodec
{
    internal const int CurrentFormatVersion = 1;

    private const int IntegrityLength = 32;
    private static ReadOnlySpan<byte> Magic => "BTSD"u8;

    internal static byte[] Encode(
        string stableSourceId,
        ReadOnlyMemory<byte> content,
        string contentType,
        string? partitionKey)
    {
        var writer = new ArrayBufferWriter<byte>();
        Write(writer, Magic);
        WriteByte(writer, CurrentFormatVersion);
        WriteString(writer, stableSourceId);
        WriteString(writer, contentType);
        WriteString(writer, partitionKey);
        WriteInt32(writer, content.Length);
        Write(writer, content.Span);
        Span<byte> integrity = stackalloc byte[IntegrityLength];
        _ = SHA256.HashData(writer.WrittenSpan, integrity);
        Write(writer, integrity);
        return writer.WrittenSpan.ToArray();
    }

    internal static RedisSyncDocument Decode(ReadOnlySpan<byte> value)
    {
        if (value.Length < Magic.Length + 1 + IntegrityLength)
        {
            throw new RedisSyncDocumentException("The Redis Sync document is truncated.");
        }

        var content = value[..^IntegrityLength];
        Span<byte> integrity = stackalloc byte[IntegrityLength];
        _ = SHA256.HashData(content, integrity);
        if (!CryptographicOperations.FixedTimeEquals(integrity, value[^IntegrityLength..]))
        {
            throw new RedisSyncDocumentException("The Redis Sync document integrity check failed.");
        }

        try
        {
            var reader = new DocumentReader(content);
            if (!reader.Read(Magic.Length).SequenceEqual(Magic))
            {
                throw new RedisSyncDocumentException("The Redis Sync document magic is invalid.");
            }

            var version = reader.ReadByte();
            if (version != CurrentFormatVersion)
            {
                throw new RedisSyncDocumentException(
                    $"Redis Sync document format {version} is unsupported; this build requires format {CurrentFormatVersion}.");
            }

            var stableSourceId = reader.ReadRequiredString();
            var contentType = reader.ReadRequiredString();
            var partitionKey = reader.ReadString();
            var payload = reader.ReadMemory();
            reader.EnsureComplete();
            return new RedisSyncDocument(stableSourceId, payload, contentType, partitionKey);
        }
        catch (RedisSyncDocumentException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            throw new RedisSyncDocumentException("The Redis Sync document contains invalid data.", exception);
        }
    }

    private static void WriteString(ArrayBufferWriter<byte> writer, string? value)
    {
        if (value is null)
        {
            WriteInt32(writer, -1);
            return;
        }

        var length = Encoding.UTF8.GetByteCount(value);
        WriteInt32(writer, length);
        var span = writer.GetSpan(length);
        _ = Encoding.UTF8.GetBytes(value, span);
        writer.Advance(length);
    }

    private static void WriteByte(ArrayBufferWriter<byte> writer, int value)
    {
        writer.GetSpan(1)[0] = checked((byte)value);
        writer.Advance(1);
    }

    private static void WriteInt32(ArrayBufferWriter<byte> writer, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(writer.GetSpan(sizeof(int)), value);
        writer.Advance(sizeof(int));
    }

    private static void Write(ArrayBufferWriter<byte> writer, ReadOnlySpan<byte> value)
    {
        value.CopyTo(writer.GetSpan(value.Length));
        writer.Advance(value.Length);
    }

    private ref struct DocumentReader
    {
        private readonly ReadOnlySpan<byte> _value;
        private int _offset;

        internal DocumentReader(ReadOnlySpan<byte> value)
        {
            _value = value;
            _offset = 0;
        }

        internal byte ReadByte() => Read(1)[0];

        internal string ReadRequiredString() =>
            ReadString() ?? throw new RedisSyncDocumentException("A required string is null.");

        internal string? ReadString()
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(Read(sizeof(int)));
            if (length == -1)
            {
                return null;
            }

            if (length < 0)
            {
                throw new RedisSyncDocumentException($"String length {length} is invalid.");
            }

            return Encoding.UTF8.GetString(Read(length));
        }

        internal ReadOnlyMemory<byte> ReadMemory()
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(Read(sizeof(int)));
            if (length < 0)
            {
                throw new RedisSyncDocumentException($"Content length {length} is invalid.");
            }

            return Read(length).ToArray();
        }

        internal ReadOnlySpan<byte> Read(int length)
        {
            if (length < 0 || length > _value.Length - _offset)
            {
                throw new RedisSyncDocumentException("The Redis Sync document is truncated.");
            }

            var result = _value.Slice(_offset, length);
            _offset += length;
            return result;
        }

        internal void EnsureComplete()
        {
            if (_offset != _value.Length)
            {
                throw new RedisSyncDocumentException("The Redis Sync document contains trailing bytes.");
            }
        }
    }
}
