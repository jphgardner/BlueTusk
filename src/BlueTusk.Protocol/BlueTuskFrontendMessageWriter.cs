using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace BlueTusk.Protocol;

/// <summary>Writes PostgreSQL frontend messages into caller-owned buffers.</summary>
public static class BlueTuskFrontendMessageWriter
{
    public const int ProtocolVersion30 = 3 << 16;
    public const int SslRequestCode = 80877103;

    public static void WriteSslRequest(IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        WriteInt32(output, 8);
        WriteInt32(output, SslRequestCode);
    }

    public static void WriteStartupMessage(
        IBufferWriter<byte> output,
        IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(parameters);

        var payloadLength = sizeof(int);
        foreach (var pair in parameters)
        {
            ValidateCString(pair.Key, nameof(parameters));
            ValidateCString(pair.Value, nameof(parameters));
            payloadLength = checked(payloadLength + Encoding.UTF8.GetByteCount(pair.Key) + 1);
            payloadLength = checked(payloadLength + Encoding.UTF8.GetByteCount(pair.Value) + 1);
        }

        var messageLength = checked(sizeof(int) + payloadLength + 1);
        WriteInt32(output, messageLength);
        WriteInt32(output, ProtocolVersion30);

        foreach (var pair in parameters)
        {
            WriteCString(output, pair.Key);
            WriteCString(output, pair.Value);
        }

        WriteByte(output, 0);
    }

    public static void WriteSimpleQuery(IBufferWriter<byte> output, string sql)
    {
        ArgumentNullException.ThrowIfNull(output);
        ValidateCString(sql, nameof(sql));

        var sqlLength = Encoding.UTF8.GetByteCount(sql);
        WriteByte(output, (byte)'Q');
        WriteInt32(output, checked(sizeof(int) + sqlLength + 1));
        WriteUtf8(output, sql, sqlLength);
        WriteByte(output, 0);
    }

    private static void WriteCString(IBufferWriter<byte> output, string value)
    {
        WriteUtf8(output, value, Encoding.UTF8.GetByteCount(value));
        WriteByte(output, 0);
    }

    private static void WriteUtf8(IBufferWriter<byte> output, string value, int byteCount)
    {
        var destination = output.GetSpan(byteCount);
        var written = Encoding.UTF8.GetBytes(value, destination);
        output.Advance(written);
    }

    private static void WriteInt32(IBufferWriter<byte> output, int value)
    {
        var destination = output.GetSpan(sizeof(int));
        BinaryPrimitives.WriteInt32BigEndian(destination, value);
        output.Advance(sizeof(int));
    }

    private static void WriteByte(IBufferWriter<byte> output, byte value)
    {
        var destination = output.GetSpan(1);
        destination[0] = value;
        output.Advance(1);
    }

    private static void ValidateCString(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("PostgreSQL C strings cannot contain a null character.", parameterName);
        }
    }
}
