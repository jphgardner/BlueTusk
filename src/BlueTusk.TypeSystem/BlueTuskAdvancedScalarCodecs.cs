namespace BlueTusk.TypeSystem;

public sealed class BlueTuskTimeWithTimeZoneCodec : BlueTuskCodec<BlueTuskTimeWithTimeZone>
{
    private const long MicrosecondsPerDay = 86_400_000_000L;

    public override BlueTuskTimeWithTimeZone ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Text)
        {
            return BlueTuskTimeWithTimeZone.Parse(reader.ReadRemainingUtf8());
        }

        if (format != BlueTuskDataFormat.Binary || reader.Remaining != 12)
        {
            throw InvalidBinary(type, 12);
        }

        var microseconds = reader.ReadInt64BigEndian();
        var secondsWestOfUtc = reader.ReadInt32BigEndian();
        if (microseconds is < 0 or > MicrosecondsPerDay)
        {
            throw new InvalidOperationException(
                "PostgreSQL time with time zone is outside 00:00:00 through 24:00:00.");
        }

        return new BlueTuskTimeWithTimeZone(
            TimeSpan.FromTicks(checked(microseconds * TimeSpan.TicksPerMicrosecond)),
            TimeSpan.FromSeconds(checked(-secondsWestOfUtc)));
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskTimeWithTimeZone value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value.ToString());
        }
        else if (format == BlueTuskDataFormat.Binary)
        {
            writer.WriteInt64BigEndian(value.TimeOfDay.Ticks / TimeSpan.TicksPerMicrosecond);
            writer.WriteInt32BigEndian(checked(-(int)value.UtcOffset.TotalSeconds));
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    private static InvalidOperationException InvalidBinary(BlueTuskTypeDescriptor type, int width) =>
        new($"PostgreSQL {type.QualifiedName} binary values must contain exactly {width} bytes.");
}

public sealed class BlueTuskIntervalCodec : BlueTuskCodec<BlueTuskInterval>
{
    public override BlueTuskInterval ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Text)
        {
            return BlueTuskInterval.Parse(reader.ReadRemainingUtf8());
        }

        if (format != BlueTuskDataFormat.Binary || reader.Remaining != 16)
        {
            throw InvalidBinary(type, 16);
        }

        var microseconds = reader.ReadInt64BigEndian();
        var days = reader.ReadInt32BigEndian();
        var months = reader.ReadInt32BigEndian();
        if (microseconds == long.MaxValue && days == int.MaxValue && months == int.MaxValue)
        {
            return BlueTuskInterval.PositiveInfinity;
        }

        if (microseconds == long.MinValue && days == int.MinValue && months == int.MinValue)
        {
            return BlueTuskInterval.NegativeInfinity;
        }

        return new BlueTuskInterval(months, days, microseconds);
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskInterval value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value.ToString());
            return;
        }

        if (format != BlueTuskDataFormat.Binary)
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        switch (value.Kind)
        {
            case BlueTuskIntervalKind.Finite:
                writer.WriteInt64BigEndian(value.Microseconds);
                writer.WriteInt32BigEndian(value.Days);
                writer.WriteInt32BigEndian(value.Months);
                break;
            case BlueTuskIntervalKind.PositiveInfinity:
                writer.WriteInt64BigEndian(long.MaxValue);
                writer.WriteInt32BigEndian(int.MaxValue);
                writer.WriteInt32BigEndian(int.MaxValue);
                break;
            case BlueTuskIntervalKind.NegativeInfinity:
                writer.WriteInt64BigEndian(long.MinValue);
                writer.WriteInt32BigEndian(int.MinValue);
                writer.WriteInt32BigEndian(int.MinValue);
                break;
            default:
                throw new InvalidOperationException("Unknown PostgreSQL interval kind.");
        }
    }

    private static InvalidOperationException InvalidBinary(BlueTuskTypeDescriptor type, int width) =>
        new($"PostgreSQL {type.QualifiedName} binary values must contain exactly {width} bytes.");
}

public sealed class BlueTuskBitStringCodec : BlueTuskCodec<BlueTuskBitString>
{
    public override BlueTuskBitString ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Text)
        {
            return new BlueTuskBitString(reader.ReadRemainingUtf8());
        }

        if (format != BlueTuskDataFormat.Binary || reader.Remaining < sizeof(int))
        {
            throw new InvalidOperationException(
                $"PostgreSQL {type.QualifiedName} binary values require a four-byte bit length.");
        }

        var bitCount = reader.ReadInt32BigEndian();
        if (bitCount < 0 || reader.Remaining != (bitCount + 7) / 8)
        {
            throw new InvalidOperationException(
                $"PostgreSQL {type.QualifiedName} binary bit length does not match its payload.");
        }

        var bytes = reader.ReadRemainingBytes();
        if (bitCount % 8 != 0 && (bytes[^1] & ((1 << (8 - (bitCount % 8))) - 1)) != 0)
        {
            throw new InvalidOperationException(
                $"PostgreSQL {type.QualifiedName} binary value contains non-zero padding bits.");
        }

        return new BlueTuskBitString(DecodeBits(bytes, bitCount));
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskBitString value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value.ToString());
            return;
        }

        if (format != BlueTuskDataFormat.Binary)
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        writer.WriteInt32BigEndian(value.Length);
        var bits = value.ToString();
        for (var offset = 0; offset < bits.Length; offset += 8)
        {
            byte item = 0;
            var count = Math.Min(8, bits.Length - offset);
            for (var index = 0; index < count; index++)
            {
                if (bits[offset + index] == '1')
                {
                    item |= checked((byte)(1 << (7 - index)));
                }
            }

            writer.WriteByte(item);
        }
    }

    private static string DecodeBits(ReadOnlySpan<byte> bytes, int bitCount) =>
        string.Create(bitCount, (Bytes: bytes.ToArray(), BitCount: bitCount), static (characters, state) =>
        {
            for (var index = 0; index < state.BitCount; index++)
            {
                characters[index] = (state.Bytes[index / 8] & (1 << (7 - (index % 8)))) == 0 ? '0' : '1';
            }
        });
}

public sealed class BlueTuskLogSequenceNumberCodec : BlueTuskCodec<BlueTuskLogSequenceNumber>
{
    public override BlueTuskLogSequenceNumber ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) => format switch
        {
            BlueTuskDataFormat.Text => BlueTuskLogSequenceNumber.Parse(reader.ReadRemainingUtf8()),
            BlueTuskDataFormat.Binary when reader.Remaining == sizeof(ulong) =>
                new BlueTuskLogSequenceNumber(reader.ReadUInt64BigEndian()),
            BlueTuskDataFormat.Binary => throw new InvalidOperationException(
                $"PostgreSQL {type.QualifiedName} binary values must contain exactly eight bytes."),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskLogSequenceNumber value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Binary)
        {
            writer.WriteUInt64BigEndian(value.Value);
        }
        else if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value.ToString());
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }
}

public sealed class BlueTuskTupleIdCodec : BlueTuskCodec<BlueTuskTupleId>
{
    public override BlueTuskTupleId ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) => format switch
        {
            BlueTuskDataFormat.Text => BlueTuskTupleId.Parse(reader.ReadRemainingUtf8()),
            BlueTuskDataFormat.Binary when reader.Remaining == 6 =>
                new BlueTuskTupleId(reader.ReadUInt32BigEndian(), reader.ReadUInt16BigEndian()),
            BlueTuskDataFormat.Binary => throw new InvalidOperationException(
                $"PostgreSQL {type.QualifiedName} binary values must contain exactly six bytes."),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskTupleId value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Binary)
        {
            writer.WriteUInt32BigEndian(value.BlockNumber);
            writer.WriteUInt16BigEndian(value.OffsetNumber);
        }
        else if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value.ToString());
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }
}
