using System.Text;

namespace BlueTusk.Protocol;

/// <summary>Decodes complete backend message payloads into validated protocol values.</summary>
public static class BlueTuskBackendMessageDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static BlueTuskAuthenticationRequest DecodeAuthentication(BlueTuskBackendMessage message)
    {
        RequireCode(message, 'R');
        var bytes = message.ToPayloadArray();
        var reader = new BlueTuskBackendPayloadReader(bytes);
        var requestCode = reader.ReadInt32();
        BlueTuskAuthenticationRequest result = requestCode switch
        {
            0 => new BlueTuskAuthenticationRequest.Ok(),
            3 => new BlueTuskAuthenticationRequest.CleartextPassword(),
            5 => new BlueTuskAuthenticationRequest.Md5Password(reader.ReadBytes(4).ToArray()),
            7 => new BlueTuskAuthenticationRequest.Gss(),
            8 => new BlueTuskAuthenticationRequest.GssContinue(reader.ReadRemainingBytes().ToArray()),
            9 => new BlueTuskAuthenticationRequest.Sspi(),
            10 => DecodeSaslMechanisms(ref reader),
            11 => new BlueTuskAuthenticationRequest.SaslContinue(DecodeUtf8(reader.ReadRemainingBytes())),
            12 => new BlueTuskAuthenticationRequest.SaslFinal(DecodeUtf8(reader.ReadRemainingBytes())),
            _ => throw new BlueTuskProtocolException($"PostgreSQL requested unsupported authentication type {requestCode}."),
        };
        reader.EnsureConsumed();
        return result;
    }

    public static BlueTuskParameterStatus DecodeParameterStatus(BlueTuskBackendMessage message)
    {
        RequireCode(message, 'S');
        var reader = CreateReader(message, out _);
        var result = new BlueTuskParameterStatus(reader.ReadCString(), reader.ReadCString());
        reader.EnsureConsumed();
        return result;
    }

    public static BlueTuskBackendKeyData DecodeBackendKeyData(BlueTuskBackendMessage message)
    {
        RequireCode(message, 'K');
        var reader = CreateReader(message, out _);
        var result = new BlueTuskBackendKeyData(reader.ReadInt32(), reader.ReadInt32());
        reader.EnsureConsumed();
        return result;
    }

    public static BlueTuskNotificationResponse DecodeNotificationResponse(BlueTuskBackendMessage message)
    {
        RequireCode(message, 'A');
        var reader = CreateReader(message, out _);
        var result = new BlueTuskNotificationResponse(
            reader.ReadInt32(),
            reader.ReadCString(),
            reader.ReadCString());
        reader.EnsureConsumed();
        return result;
    }

    public static BlueTuskTransactionStatus DecodeReadyForQuery(BlueTuskBackendMessage message)
    {
        RequireCode(message, 'Z');
        var reader = CreateReader(message, out _);
        var status = reader.ReadByte() switch
        {
            (byte)'I' => BlueTuskTransactionStatus.Idle,
            (byte)'T' => BlueTuskTransactionStatus.InTransaction,
            (byte)'E' => BlueTuskTransactionStatus.FailedTransaction,
            var value => throw new BlueTuskProtocolException($"ReadyForQuery contained unknown status byte {value}."),
        };
        reader.EnsureConsumed();
        return status;
    }

    public static IReadOnlyList<BlueTuskFieldDescription> DecodeRowDescription(BlueTuskBackendMessage message)
    {
        RequireCode(message, 'T');
        var reader = CreateReader(message, out _);
        var count = reader.ReadInt16();
        if (count < 0)
        {
            throw new BlueTuskProtocolException("RowDescription declared a negative field count.");
        }

        var fields = new BlueTuskFieldDescription[count];
        for (var index = 0; index < fields.Length; index++)
        {
            fields[index] = new BlueTuskFieldDescription(
                reader.ReadCString(),
                reader.ReadUInt32(),
                reader.ReadInt16(),
                reader.ReadUInt32(),
                reader.ReadInt16(),
                reader.ReadInt32(),
                reader.ReadInt16());
        }

        reader.EnsureConsumed();
        return fields;
    }

    public static BlueTuskDataRow DecodeDataRow(BlueTuskBackendMessage message, int? expectedFieldCount = null)
    {
        RequireCode(message, 'D');
        var reader = CreateReader(message, out _);
        var count = reader.ReadInt16();
        if (count < 0 || expectedFieldCount is { } expected && count != expected)
        {
            throw new BlueTuskProtocolException("DataRow field count does not match its row description.");
        }

        var values = new ReadOnlyMemory<byte>?[count];
        for (var index = 0; index < values.Length; index++)
        {
            var length = reader.ReadInt32();
            if (length == -1)
            {
                values[index] = null;
            }
            else if (length < -1)
            {
                throw new BlueTuskProtocolException("DataRow declared an invalid negative field length.");
            }
            else
            {
                values[index] = reader.ReadBytes(length).ToArray();
            }
        }

        reader.EnsureConsumed();
        return new BlueTuskDataRow(values);
    }

    internal static ReadOnlyMemory<byte>? DecodeFirstDataRowValue(
        BlueTuskBackendMessage message,
        int expectedFieldCount)
    {
        RequireCode(message, 'D');
        var reader = CreateReader(message, out _);
        var count = reader.ReadInt16();
        if (count < 1 || count != expectedFieldCount)
        {
            throw new BlueTuskProtocolException(
                "DataRow field count does not match its row description.");
        }

        ReadOnlyMemory<byte>? firstValue = null;
        for (var index = 0; index < count; index++)
        {
            var length = reader.ReadInt32();
            if (length < -1)
            {
                throw new BlueTuskProtocolException(
                    "DataRow declared an invalid negative field length.");
            }

            if (length >= 0)
            {
                var bytes = reader.ReadBytes(length);
                if (index == 0)
                {
                    firstValue = bytes.ToArray();
                }
            }
        }

        reader.EnsureConsumed();
        return firstValue;
    }

    public static string DecodeCommandComplete(BlueTuskBackendMessage message)
    {
        RequireCode(message, 'C');
        var reader = CreateReader(message, out _);
        var tag = reader.ReadCString();
        reader.EnsureConsumed();
        return tag;
    }

    public static BlueTuskCopyResponse DecodeCopyResponse(BlueTuskBackendMessage message)
    {
        if (message.Identifier is not ('G' or 'H' or 'W'))
        {
            throw new ArgumentException(
                "The message is not a CopyInResponse, CopyOutResponse, or CopyBothResponse.",
                nameof(message));
        }

        var reader = CreateReader(message, out _);
        var format = DecodeCopyFormat(reader.ReadByte());
        var columnCount = reader.ReadInt16();
        if (columnCount < 0)
        {
            throw new BlueTuskProtocolException("COPY response declared a negative column count.");
        }

        var columnFormats = new BlueTuskCopyFormat[columnCount];
        for (var index = 0; index < columnFormats.Length; index++)
        {
            columnFormats[index] = DecodeCopyFormat(reader.ReadInt16());
        }

        reader.EnsureConsumed();
        return new BlueTuskCopyResponse(format, columnFormats);
    }

    public static byte[] DecodeCopyData(BlueTuskBackendMessage message)
    {
        RequireCode(message, 'd');
        return message.ToPayloadArray();
    }

    public static BlueTuskError DecodeErrorOrNotice(BlueTuskBackendMessage message)
    {
        if (message.Identifier is not ('E' or 'N'))
        {
            throw new ArgumentException("The message is not an ErrorResponse or NoticeResponse.", nameof(message));
        }

        var reader = CreateReader(message, out _);
        var fields = new Dictionary<char, string>();
        while (true)
        {
            var code = reader.ReadByte();
            if (code == 0)
            {
                break;
            }

            if (!fields.TryAdd((char)code, reader.ReadCString()))
            {
                throw new BlueTuskProtocolException($"ErrorResponse contained duplicate field '{(char)code}'.");
            }
        }

        reader.EnsureConsumed();
        return new BlueTuskError(fields);
    }

    private static BlueTuskAuthenticationRequest.Sasl DecodeSaslMechanisms(ref BlueTuskBackendPayloadReader reader)
    {
        var mechanisms = new List<string>();
        while (reader.Remaining > 0)
        {
            var mechanism = reader.ReadCString();
            if (mechanism.Length == 0)
            {
                if (mechanisms.Count == 0)
                {
                    throw new BlueTuskProtocolException("AuthenticationSASL did not advertise a mechanism.");
                }

                return new BlueTuskAuthenticationRequest.Sasl(mechanisms);
            }

            mechanisms.Add(mechanism);
        }

        throw new BlueTuskProtocolException("AuthenticationSASL was missing its final null terminator.");
    }

    private static BlueTuskCopyFormat DecodeCopyFormat(int value) => value switch
    {
        0 => BlueTuskCopyFormat.Text,
        1 => BlueTuskCopyFormat.Binary,
        _ => throw new BlueTuskProtocolException($"COPY response contained unknown format code {value}."),
    };

    private static BlueTuskBackendPayloadReader CreateReader(BlueTuskBackendMessage message, out byte[] bytes)
    {
        bytes = message.ToPayloadArray();
        return new BlueTuskBackendPayloadReader(bytes);
    }

    private static string DecodeUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new BlueTuskProtocolException("An authentication message was not valid UTF-8.", exception);
        }
    }

    private static void RequireCode(BlueTuskBackendMessage message, char expected)
    {
        if (message.Identifier != expected)
        {
            throw new ArgumentException($"Expected backend message '{expected}', received '{message.Identifier}'.", nameof(message));
        }
    }
}
