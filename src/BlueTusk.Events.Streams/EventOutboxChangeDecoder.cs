using System.Buffers.Binary;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using BlueTusk.Streams;

namespace BlueTusk.Events.Streams;

public sealed class EventOutboxDecodingException : InvalidOperationException
{
    public EventOutboxDecodingException(string column) : base($"The event outbox column '{column}' does not satisfy its published wire contract.") { }
}

/// <summary>Decode immutable outbox inserts from raw Streams CDC without reflection or provider interception.</summary>
public sealed class EventOutboxChangeDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _schema;
    private readonly int _maximumEventBytes;

    public EventOutboxChangeDecoder(string schema = "bluetusk_events", int maximumEventBytes = 1_048_576)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        if (schema.Length > 63 || schema.Any(static character => !char.IsAsciiLetterOrDigit(character) && character != '_') ||
            !char.IsAsciiLetter(schema[0]) && schema[0] != '_')
        {
            throw new ArgumentException("The outbox schema must be a 1 to 63 character ASCII PostgreSQL identifier.", nameof(schema));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEventBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumEventBytes, 16_777_216);
        _schema = schema;
        _maximumEventBytes = maximumEventBytes;
    }

    public bool TryDecode(Change change, [NotNullWhen(true)] out StoredEvent? value)
    {
        ArgumentNullException.ThrowIfNull(change);
        value = null;
        var row = change switch
        {
            InsertChange insert => insert.NewRow,
            UpdateChange update when Matches(update.NewRow.Table) || Matches(update.OldRow.Table) =>
                throw new InvalidOperationException("The published event outbox is immutable; CDC updates require operator remediation."),
            DeleteChange delete when Matches(delete.OldRow.Table) =>
                throw new InvalidOperationException("The published event outbox was deleted; durable retention/replay must be coordinated explicitly."),
            TruncateChange truncate when truncate.Tables.Any(Matches) =>
                throw new InvalidOperationException("The published event outbox was truncated."),
            _ => null
        };
        if (row is null || !Matches(row.Table))
        {
            return false;
        }

        return TryDecodeRow(row, out value);
    }

    public bool TryDecodeRow(ChangeRow row, [NotNullWhen(true)] out StoredEvent? value)
    {
        ArgumentNullException.ThrowIfNull(row);
        value = null;
        if (!Matches(row.Table))
        {
            return false;
        }

        try
        {
            var tenant = Text(row, "tenant_id");
            var stream = Text(row, "stream_id");
            var sequence = Integer(row, "sequence", sizeof(long));
            var version = Integer(row, "version", sizeof(int));
            var identity = Identity(row);
            if (sequence <= 0 || version <= 0 || version > int.MaxValue || identity == Guid.Empty)
            {
                throw new EventOutboxDecodingException("event identity, sequence or version");
            }

            var eventType = Text(row, "event_type");
            if (string.IsNullOrWhiteSpace(eventType) || eventType.Length > 200 || eventType.Contains('\0', StringComparison.Ordinal))
            {
                throw new EventOutboxDecodingException("event_type");
            }
            var payload = Payload(row);
            if (payload.Length == 0 || payload.Length > _maximumEventBytes)
            {
                throw new EventOutboxDecodingException("payload");
            }

            value = new StoredEvent(new EventStreamKey(tenant, stream), sequence, identity, eventType,
                (int)version, Timestamp(row), payload);
            return true;
        }
        catch (KeyNotFoundException)
        {
            throw new EventOutboxDecodingException("required column missing");
        }
    }

    private bool Matches(ChangeTable table) => string.Equals(table.Schema, _schema, StringComparison.Ordinal) &&
        string.Equals(table.Name, "outbox", StringComparison.Ordinal);

    private static ChangeColumnValue Column(ChangeRow row, string name)
    {
        var value = row[name];
        if (value.State != ChangeColumnState.Value || value.Encoding is not ChangeValueEncoding.Text and not ChangeValueEncoding.Binary)
        {
            throw new EventOutboxDecodingException(name);
        }

        return value;
    }

    private static string Text(ChangeRow row, string name)
    {
        var bytes = Column(row, name).Data;
        if (bytes.Length > 800)
        {
            throw new EventOutboxDecodingException(name);
        }

        try
        {
            return StrictUtf8.GetString(bytes.Span);
        }
        catch (DecoderFallbackException)
        {
            throw new EventOutboxDecodingException(name);
        }
    }

    private static long Integer(ChangeRow row, string name, int binarySize)
    {
        var column = Column(row, name);
        if (column.Encoding == ChangeValueEncoding.Binary)
        {
            if (column.Data.Length != binarySize)
            {
                throw new EventOutboxDecodingException(name);
            }

            return binarySize == sizeof(long) ? BinaryPrimitives.ReadInt64BigEndian(column.Data.Span) : BinaryPrimitives.ReadInt32BigEndian(column.Data.Span);
        }

        if (column.Data.Length > 20 || !Utf8Parser.TryParse(column.Data.Span, out long value, out var consumed) || consumed != column.Data.Length)
        {
            throw new EventOutboxDecodingException(name);
        }

        return value;
    }

    private static Guid Identity(ChangeRow row)
    {
        var column = Column(row, "event_id");
        if (column.Encoding == ChangeValueEncoding.Binary)
        {
            return column.Data.Length == 16 ? new Guid(column.Data.Span, bigEndian: true) :
                throw new EventOutboxDecodingException("event_id");
        }

        return column.Data.Length == 36 && Utf8Parser.TryParse(column.Data.Span, out Guid identity, out var consumed) && consumed == 36
            ? identity : throw new EventOutboxDecodingException("event_id");
    }

    private static DateTimeOffset Timestamp(ChangeRow row)
    {
        var column = Column(row, "occurred_at");
        if (column.Encoding == ChangeValueEncoding.Binary)
        {
            if (column.Data.Length != 8)
            {
                throw new EventOutboxDecodingException("occurred_at");
            }

            try
            {
                var microseconds = BinaryPrimitives.ReadInt64BigEndian(column.Data.Span);
                return new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(checked(microseconds * 10));
            }
            catch (Exception exception) when (exception is OverflowException or ArgumentOutOfRangeException)
            {
                throw new EventOutboxDecodingException("occurred_at");
            }
        }

        if (column.Data.Length > 64 || !DateTimeOffset.TryParse(Text(row, "occurred_at"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var occurred))
        {
            throw new EventOutboxDecodingException("occurred_at");
        }

        return occurred;
    }

    private byte[] Payload(ChangeRow row)
    {
        var column = Column(row, "payload");
        if (column.Encoding == ChangeValueEncoding.Binary)
        {
            if (column.Data.Length > _maximumEventBytes)
            {
                throw new EventOutboxDecodingException("payload");
            }

            return column.Data.ToArray();
        }

        var bytes = column.Data.Span;
        if (bytes.StartsWith("\\x"u8))
        {
            var length = (bytes.Length - 2) / 2;
            if ((bytes.Length & 1) != 0 || length > _maximumEventBytes)
            {
                throw new EventOutboxDecodingException("payload");
            }

            var result = new byte[length];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = (byte)((Hex(bytes[i * 2 + 2]) << 4) | Hex(bytes[i * 2 + 3]));
            }

            return result;
        }

        // PostgreSQL's bytea_output=escape is also valid. Count/validate before allocating the output.
        var escapedLength = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            _ = EscapedByte(bytes, ref i);
            if (++escapedLength > _maximumEventBytes)
            {
                throw new EventOutboxDecodingException("payload");
            }
        }

        var escapedResult = new byte[escapedLength];
        var index = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            escapedResult[index++] = EscapedByte(bytes, ref i);
        }

        return escapedResult;
    }

    private static int Hex(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - '0',
        >= (byte)'a' and <= (byte)'f' => value - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => value - 'A' + 10,
        _ => throw new EventOutboxDecodingException("payload")
    };

    private static byte EscapedByte(ReadOnlySpan<byte> bytes, ref int index)
    {
        var value = bytes[index];
        if (value != '\\')
        {
            return value is >= 32 and <= 126 ? value : throw new EventOutboxDecodingException("payload");
        }

        if (++index < bytes.Length && bytes[index] == '\\')
        {
            return (byte)'\\';
        }

        if (index + 2 >= bytes.Length || bytes[index] is < (byte)'0' or > (byte)'3' ||
            bytes[index + 1] is < (byte)'0' or > (byte)'7' || bytes[index + 2] is < (byte)'0' or > (byte)'7')
        {
            throw new EventOutboxDecodingException("payload");
        }

        var decoded = (byte)(((bytes[index] - '0') << 6) | ((bytes[index + 1] - '0') << 3) | (bytes[index + 2] - '0'));
        index += 2;
        return decoded;
    }
}
