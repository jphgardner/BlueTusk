using System.Buffers.Text;
using System.Text;

namespace BlueTusk.Protocol;

/// <summary>Decodes complete backend message payloads into validated protocol values.</summary>
public static class BlueTuskBackendMessageDecoder
{
    private const int MaximumCollectionCount = 4096;
    private const int MaximumSaslMechanisms = 128;
    private const int MinimumRowDescriptionFieldBytes = 19;
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

        ValidateCollectionCount(
            count,
            reader.Remaining,
            MinimumRowDescriptionFieldBytes,
            "RowDescription field");
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
        var payload = DecodeDataRowPayload(message, expectedFieldCount, out var count);
        return new BlueTuskDataRow(new BlueTuskDataRowValues(payload, count));
    }

    internal static byte[] DecodeDataRowPayload(
        BlueTuskBackendMessage message,
        int? expectedFieldCount,
        out int count)
    {
        RequireCode(message, 'D');
        var payload = message.ToPayloadArray();
        var reader = new BlueTuskBackendPayloadReader(payload);
        count = ValidateDataRowPayload(ref reader, expectedFieldCount);
        return payload;
    }

    internal static int ValidateDataRowPayload(
        BlueTuskBackendMessage message,
        int? expectedFieldCount)
    {
        RequireCode(message, 'D');
        var reader = CreateReader(message, out _);
        return ValidateDataRowPayload(ref reader, expectedFieldCount);
    }

    private static int ValidateDataRowPayload(
        ref BlueTuskBackendPayloadReader reader,
        int? expectedFieldCount)
    {
        var count = reader.ReadInt16();
        if (count < 0 || expectedFieldCount is { } expected && count != expected)
        {
            throw new BlueTuskProtocolException("DataRow field count does not match its row description.");
        }

        ValidateCollectionCount(count, reader.Remaining, sizeof(int), "DataRow field");
        for (var index = 0; index < count; index++)
        {
            var length = reader.ReadInt32();
            if (length < -1)
            {
                throw new BlueTuskProtocolException("DataRow declared an invalid negative field length.");
            }

            if (length >= 0)
            {
                _ = reader.ReadBytes(length);
            }
        }

        reader.EnsureConsumed();
        return count;
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

        ValidateCollectionCount(count, reader.Remaining, sizeof(int), "DataRow field");
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
        if (message.Payload.IsSingleSegment)
        {
            var payload = message.Payload.FirstSpan;
            if (payload.SequenceEqual("SELECT 1\0"u8))
            {
                return "SELECT 1";
            }

            if (payload.SequenceEqual("INSERT 0 1\0"u8))
            {
                return "INSERT 0 1";
            }

            if (payload.SequenceEqual("UPDATE 1\0"u8))
            {
                return "UPDATE 1";
            }

            if (payload.SequenceEqual("DELETE 1\0"u8))
            {
                return "DELETE 1";
            }
        }

        var reader = CreateReader(message, out _);
        var tag = reader.ReadCString();
        reader.EnsureConsumed();
        return tag;
    }

    /// <summary>
    /// Reads the affected-row count from a command-complete message without
    /// materializing its command tag.
    /// </summary>
    internal static bool TryDecodeRecordsAffected(
        BlueTuskBackendMessage message,
        out int count)
    {
        RequireCode(message, 'C');
        count = 0;
        if (!message.Payload.IsSingleSegment)
        {
            return TryDecodeRecordsAffected(DecodeCommandComplete(message), out count);
        }

        var payload = message.Payload.FirstSpan;
        if (payload.Length == 0 || payload[^1] != 0)
        {
            throw new BlueTuskProtocolException(
                "CommandComplete did not contain a terminated command tag.");
        }

        var tag = payload[..^1];
        if (!IsCountedCommand(tag))
        {
            return false;
        }

        var separator = tag.LastIndexOf((byte)' ');
        if (separator < 0)
        {
            return false;
        }

        var value = tag[(separator + 1)..];
        return Utf8Parser.TryParse(value, out count, out var consumed) &&
            consumed == value.Length;
    }

    private static bool TryDecodeRecordsAffected(string commandTag, out int count)
    {
        count = 0;
        var tag = commandTag.AsSpan();
        if (!IsCountedCommand(tag))
        {
            return false;
        }

        var separator = tag.LastIndexOf(' ');
        return separator >= 0 &&
            int.TryParse(
                tag[(separator + 1)..],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out count);
    }

    private static bool IsCountedCommand(ReadOnlySpan<byte> tag) =>
        tag.StartsWith("INSERT "u8) ||
        tag.StartsWith("UPDATE "u8) ||
        tag.StartsWith("DELETE "u8) ||
        tag.StartsWith("MERGE "u8) ||
        tag.StartsWith("MOVE "u8) ||
        tag.StartsWith("FETCH "u8) ||
        tag.StartsWith("COPY "u8);

    private static bool IsCountedCommand(ReadOnlySpan<char> tag) =>
        tag.StartsWith("INSERT ", StringComparison.Ordinal) ||
        tag.StartsWith("UPDATE ", StringComparison.Ordinal) ||
        tag.StartsWith("DELETE ", StringComparison.Ordinal) ||
        tag.StartsWith("MERGE ", StringComparison.Ordinal) ||
        tag.StartsWith("MOVE ", StringComparison.Ordinal) ||
        tag.StartsWith("FETCH ", StringComparison.Ordinal) ||
        tag.StartsWith("COPY ", StringComparison.Ordinal);

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

        ValidateCollectionCount(
            columnCount,
            reader.Remaining,
            sizeof(short),
            "COPY response column");
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

            if (mechanisms.Count == MaximumSaslMechanisms)
            {
                throw new BlueTuskProtocolException(
                    $"AuthenticationSASL advertised more than {MaximumSaslMechanisms} mechanisms.");
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

    private static void ValidateCollectionCount(
        int count,
        int remainingBytes,
        int minimumBytesPerItem,
        string description)
    {
        if (count > MaximumCollectionCount ||
            count > remainingBytes / minimumBytesPerItem)
        {
            throw new BlueTuskProtocolException(
                $"{description} count {count} exceeds the bounded payload capacity.");
        }
    }

    private static BlueTuskBackendPayloadReader CreateReader(
        BlueTuskBackendMessage message,
        out byte[]? bytes)
    {
        if (message.Payload.IsSingleSegment)
        {
            bytes = null;
            return new BlueTuskBackendPayloadReader(message.Payload.FirstSpan);
        }

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
