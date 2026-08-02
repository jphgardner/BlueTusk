using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace BlueTusk.Protocol;

/// <summary>Writes PostgreSQL frontend messages into caller-owned buffers.</summary>
public static class BlueTuskFrontendMessageWriter
{
    public const int ProtocolVersion30 = 3 << 16;
    public const int SslRequestCode = 80877103;
    public const int CancelRequestCode = 80877102;

    public static void WriteSslRequest(IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        WriteInt32(output, 8);
        WriteInt32(output, SslRequestCode);
    }

    public static void WriteCancelRequest(IBufferWriter<byte> output, BlueTuskBackendKeyData backendKeyData)
    {
        ArgumentNullException.ThrowIfNull(output);
        WriteInt32(output, sizeof(int) * 4);
        WriteInt32(output, CancelRequestCode);
        WriteInt32(output, backendKeyData.ProcessId);
        WriteInt32(output, backendKeyData.SecretKey);
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

    public static void WriteSaslInitialResponse(IBufferWriter<byte> output, string mechanism, string response)
    {
        ArgumentNullException.ThrowIfNull(output);
        ValidateCString(mechanism, nameof(mechanism));
        ArgumentNullException.ThrowIfNull(response);

        var mechanismLength = Encoding.UTF8.GetByteCount(mechanism);
        var responseLength = Encoding.UTF8.GetByteCount(response);
        WriteSaslInitialResponseHeader(output, mechanism, mechanismLength, responseLength);
        WriteUtf8(output, response, responseLength);
    }

    /// <summary>Writes a SASL initial response from caller-owned sensitive bytes.</summary>
    public static void WriteSaslInitialResponse(
        IBufferWriter<byte> output,
        string mechanism,
        ReadOnlySpan<byte> response)
    {
        ArgumentNullException.ThrowIfNull(output);
        ValidateCString(mechanism, nameof(mechanism));

        var mechanismLength = Encoding.UTF8.GetByteCount(mechanism);
        WriteSaslInitialResponseHeader(output, mechanism, mechanismLength, response.Length);
        WriteBytes(output, response);
    }

    private static void WriteSaslInitialResponseHeader(
        IBufferWriter<byte> output,
        string mechanism,
        int mechanismLength,
        int responseLength)
    {
        WriteByte(output, (byte)'p');
        WriteInt32(output, checked(sizeof(int) + mechanismLength + 1 + sizeof(int) + responseLength));
        WriteUtf8(output, mechanism, mechanismLength);
        WriteByte(output, 0);
        WriteInt32(output, responseLength);
    }

    public static void WriteSaslResponse(IBufferWriter<byte> output, string response)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(response);

        var responseLength = Encoding.UTF8.GetByteCount(response);
        WriteByte(output, (byte)'p');
        WriteInt32(output, checked(sizeof(int) + responseLength));
        WriteUtf8(output, response, responseLength);
    }

    /// <summary>Writes a SASL response from caller-owned sensitive bytes.</summary>
    public static void WriteSaslResponse(IBufferWriter<byte> output, ReadOnlySpan<byte> response)
    {
        ArgumentNullException.ThrowIfNull(output);

        WriteByte(output, (byte)'p');
        WriteInt32(output, checked(sizeof(int) + response.Length));
        WriteBytes(output, response);
    }

    /// <summary>Writes an opaque GSSAPI or SSPI negotiation token.</summary>
    public static void WriteGssResponse(IBufferWriter<byte> output, ReadOnlySpan<byte> response)
    {
        ArgumentNullException.ThrowIfNull(output);

        WriteByte(output, (byte)'p');
        WriteInt32(output, checked(sizeof(int) + response.Length));
        WriteBytes(output, response);
    }

    /// <summary>Writes a PostgreSQL password response as a null-terminated byte string.</summary>
    /// <remarks>
    /// The caller owns and must clear <paramref name="response"/> when it contains sensitive
    /// material. Use <see cref="BlueTuskProtocolConnection.WriteSensitive"/> or
    /// <see cref="BlueTuskProtocolConnection.WriteSensitiveAsync"/> to clear the protocol write
    /// buffer after the message has been flushed.
    /// </remarks>
    public static void WritePasswordMessage(IBufferWriter<byte> output, ReadOnlySpan<byte> response)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (response.Contains((byte)0))
        {
            throw new ArgumentException("A PostgreSQL password response cannot contain an embedded null byte.", nameof(response));
        }

        WriteByte(output, (byte)'p');
        WriteInt32(output, checked(sizeof(int) + response.Length + 1));
        WriteBytes(output, response);
        WriteByte(output, 0);
    }

    public static void WriteTerminate(IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        WriteByte(output, (byte)'X');
        WriteInt32(output, sizeof(int));
    }

    public static void WriteParse(
        IBufferWriter<byte> output,
        string statementName,
        string sql,
        IReadOnlyList<uint> parameterTypeOids)
    {
        ArgumentNullException.ThrowIfNull(parameterTypeOids);
        WriteParse(
            output,
            statementName,
            sql,
            new TypeOidListSource(parameterTypeOids));
    }

    internal static void WriteParse<TTypeOids>(
        IBufferWriter<byte> output,
        string statementName,
        string sql,
        TTypeOids parameterTypeOids)
        where TTypeOids : struct, IBlueTuskTypeOidSource
    {
        ArgumentNullException.ThrowIfNull(output);
        ValidateCString(statementName, nameof(statementName));
        ValidateCString(sql, nameof(sql));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(parameterTypeOids.Count, short.MaxValue);

        var statementLength = Encoding.UTF8.GetByteCount(statementName);
        var sqlLength = Encoding.UTF8.GetByteCount(sql);
        var length = checked(
            sizeof(int) + statementLength + 1 + sqlLength + 1 + sizeof(short) + (sizeof(int) * parameterTypeOids.Count));
        WriteByte(output, (byte)'P');
        WriteInt32(output, length);
        WriteUtf8(output, statementName, statementLength);
        WriteByte(output, 0);
        WriteUtf8(output, sql, sqlLength);
        WriteByte(output, 0);
        WriteInt16(output, checked((short)parameterTypeOids.Count));
        for (var index = 0; index < parameterTypeOids.Count; index++)
        {
            WriteInt32(output, unchecked((int)parameterTypeOids.GetTypeOid(index)));
        }
    }

    private readonly struct TypeOidListSource(
        IReadOnlyList<uint> typeOids) : IBlueTuskTypeOidSource
    {
        public int Count => typeOids.Count;

        public uint GetTypeOid(int index) => typeOids[index];
    }

    public static void WriteBind(
        IBufferWriter<byte> output,
        string portalName,
        string statementName,
        IReadOnlyList<BlueTuskBindParameter> parameters,
        IReadOnlyList<short>? resultFormatCodes = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        WriteBind(
            output,
            portalName,
            statementName,
            new BindParameterListSource(parameters),
            resultFormatCodes);
    }

    internal static void WriteBind<TParameters>(
        IBufferWriter<byte> output,
        string portalName,
        string statementName,
        TParameters parameters,
        IReadOnlyList<short>? resultFormatCodes = null)
        where TParameters : struct, IBlueTuskBindParameterSource
    {
        ArgumentNullException.ThrowIfNull(output);
        ValidateCString(portalName, nameof(portalName));
        ValidateCString(statementName, nameof(statementName));
        resultFormatCodes ??= Array.Empty<short>();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(parameters.Count, short.MaxValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(resultFormatCodes.Count, short.MaxValue);

        var portalLength = Encoding.UTF8.GetByteCount(portalName);
        var statementLength = Encoding.UTF8.GetByteCount(statementName);
        var length = checked(
            sizeof(int) + portalLength + 1 + statementLength + 1 + sizeof(short) +
            (sizeof(short) * parameters.Count) + sizeof(short) + sizeof(short) +
            (sizeof(short) * resultFormatCodes.Count));
        for (var index = 0; index < parameters.Count; index++)
        {
            length = checked(length + sizeof(int) + (parameters.GetValue(index)?.Length ?? 0));
        }

        WriteByte(output, (byte)'B');
        WriteInt32(output, length);
        WriteUtf8(output, portalName, portalLength);
        WriteByte(output, 0);
        WriteUtf8(output, statementName, statementLength);
        WriteByte(output, 0);
        WriteInt16(output, checked((short)parameters.Count));
        for (var index = 0; index < parameters.Count; index++)
        {
            var formatCode = parameters.GetFormatCode(index);
            if (formatCode is not (0 or 1))
            {
                throw new ArgumentOutOfRangeException(nameof(parameters), "Parameter format codes must be text (0) or binary (1).");
            }

            WriteInt16(output, formatCode);
        }

        WriteInt16(output, checked((short)parameters.Count));
        for (var index = 0; index < parameters.Count; index++)
        {
            if (parameters.GetValue(index) is not { } value)
            {
                WriteInt32(output, -1);
                continue;
            }

            WriteInt32(output, value.Length);
            WriteBytes(output, value.Span);
        }

        WriteInt16(output, checked((short)resultFormatCodes.Count));
        for (var index = 0; index < resultFormatCodes.Count; index++)
        {
            var formatCode = resultFormatCodes[index];
            if (formatCode is not (0 or 1))
            {
                throw new ArgumentOutOfRangeException(nameof(resultFormatCodes), "Result format codes must be text (0) or binary (1).");
            }

            WriteInt16(output, formatCode);
        }
    }

    private readonly struct BindParameterListSource(
        IReadOnlyList<BlueTuskBindParameter> parameters) : IBlueTuskBindParameterSource
    {
        public int Count => parameters.Count;

        public short GetFormatCode(int index) => parameters[index].FormatCode;

        public ReadOnlyMemory<byte>? GetValue(int index) => parameters[index].Value;
    }

    public static void WriteDescribePortal(IBufferWriter<byte> output, string portalName)
        => WriteDescribe(output, (byte)'P', portalName, nameof(portalName));

    public static void WriteDescribeStatement(IBufferWriter<byte> output, string statementName)
        => WriteDescribe(output, (byte)'S', statementName, nameof(statementName));

    public static void WriteCloseStatement(IBufferWriter<byte> output, string statementName)
        => WriteClose(output, (byte)'S', statementName, nameof(statementName));

    public static void WriteClosePortal(IBufferWriter<byte> output, string portalName)
        => WriteClose(output, (byte)'P', portalName, nameof(portalName));

    private static void WriteDescribe(
        IBufferWriter<byte> output,
        byte targetType,
        string name,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(output);
        ValidateCString(name, parameterName);
        var nameLength = Encoding.UTF8.GetByteCount(name);
        WriteByte(output, (byte)'D');
        WriteInt32(output, checked(sizeof(int) + 1 + nameLength + 1));
        WriteByte(output, targetType);
        WriteUtf8(output, name, nameLength);
        WriteByte(output, 0);
    }

    private static void WriteClose(
        IBufferWriter<byte> output,
        byte targetType,
        string name,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(output);
        ValidateCString(name, parameterName);
        var nameLength = Encoding.UTF8.GetByteCount(name);
        WriteByte(output, (byte)'C');
        WriteInt32(output, checked(sizeof(int) + 1 + nameLength + 1));
        WriteByte(output, targetType);
        WriteUtf8(output, name, nameLength);
        WriteByte(output, 0);
    }

    public static void WriteExecute(IBufferWriter<byte> output, string portalName, int maximumRows = 0)
    {
        ArgumentNullException.ThrowIfNull(output);
        ValidateCString(portalName, nameof(portalName));
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRows);
        var portalLength = Encoding.UTF8.GetByteCount(portalName);
        WriteByte(output, (byte)'E');
        WriteInt32(output, checked(sizeof(int) + portalLength + 1 + sizeof(int)));
        WriteUtf8(output, portalName, portalLength);
        WriteByte(output, 0);
        WriteInt32(output, maximumRows);
    }

    public static void WriteSync(IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        WriteByte(output, (byte)'S');
        WriteInt32(output, sizeof(int));
    }

    public static void WriteFlush(IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        WriteByte(output, (byte)'H');
        WriteInt32(output, sizeof(int));
    }

    public static void WriteCopyData(
        IBufferWriter<byte> output,
        ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(output);
        WriteByte(output, (byte)'d');
        WriteInt32(output, checked(sizeof(int) + data.Length));
        WriteBytes(output, data);
    }

    public static void WriteCopyDone(IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        WriteByte(output, (byte)'c');
        WriteInt32(output, sizeof(int));
    }

    public static void WriteCopyFail(
        IBufferWriter<byte> output,
        string message)
    {
        ArgumentNullException.ThrowIfNull(output);
        ValidateCString(message, nameof(message));
        var messageLength = Encoding.UTF8.GetByteCount(message);
        WriteByte(output, (byte)'f');
        WriteInt32(output, checked(sizeof(int) + messageLength + 1));
        WriteUtf8(output, message, messageLength);
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

    private static void WriteInt16(IBufferWriter<byte> output, short value)
    {
        var destination = output.GetSpan(sizeof(short));
        BinaryPrimitives.WriteInt16BigEndian(destination, value);
        output.Advance(sizeof(short));
    }

    private static void WriteBytes(IBufferWriter<byte> output, ReadOnlySpan<byte> value)
    {
        value.CopyTo(output.GetSpan(value.Length));
        output.Advance(value.Length);
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

internal interface IBlueTuskBindParameterSource
{
    int Count { get; }

    short GetFormatCode(int index);

    ReadOnlyMemory<byte>? GetValue(int index);
}

internal interface IBlueTuskTypeOidSource
{
    int Count { get; }

    uint GetTypeOid(int index);
}
