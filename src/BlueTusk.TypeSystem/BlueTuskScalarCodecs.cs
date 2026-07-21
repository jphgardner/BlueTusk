using System.Globalization;

namespace BlueTusk.TypeSystem;

public sealed class BlueTuskBooleanCodec : BlueTuskCodec<bool>
{
    public override bool ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) => format switch
        {
            BlueTuskDataFormat.Binary when reader.Remaining == 1 => reader.ReadByte() switch
            {
                0 => false,
                1 => true,
                var value => throw new InvalidOperationException(
                    $"PostgreSQL {type.QualifiedName} contains invalid binary boolean value {value}."),
            },
            BlueTuskDataFormat.Binary => throw BinaryLength(type, 1),
            BlueTuskDataFormat.Text => reader.ReadRemainingUtf8() switch
            {
                "t" => true,
                "f" => false,
                var value => throw new FormatException(
                    $"PostgreSQL {type.QualifiedName} contains invalid text boolean value '{value}'."),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        bool value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        switch (format)
        {
            case BlueTuskDataFormat.Binary:
                writer.WriteByte(value ? (byte)1 : (byte)0);
                break;
            case BlueTuskDataFormat.Text:
                writer.WriteByte(value ? (byte)'t' : (byte)'f');
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    private static InvalidOperationException BinaryLength(BlueTuskTypeDescriptor type, int expected) =>
        new($"PostgreSQL {type.QualifiedName} binary values must contain exactly {expected} byte(s).");
}

public sealed class BlueTuskInt16Codec : BlueTuskCodec<short>
{
    public override short ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) => format switch
        {
            BlueTuskDataFormat.Binary when reader.Remaining == sizeof(short) => reader.ReadInt16BigEndian(),
            BlueTuskDataFormat.Binary => throw FixedWidth(type, sizeof(short)),
            BlueTuskDataFormat.Text => short.Parse(
                reader.ReadRemainingUtf8(),
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        short value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Binary)
        {
            writer.WriteInt16BigEndian(value);
        }
        else if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    private static InvalidOperationException FixedWidth(BlueTuskTypeDescriptor type, int width) =>
        new($"PostgreSQL {type.QualifiedName} binary values must contain exactly {width} bytes.");
}

public sealed class BlueTuskInt64Codec : BlueTuskCodec<long>
{
    public override long ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) => format switch
        {
            BlueTuskDataFormat.Binary when reader.Remaining == sizeof(long) => reader.ReadInt64BigEndian(),
            BlueTuskDataFormat.Binary => throw FixedWidth(type, sizeof(long)),
            BlueTuskDataFormat.Text => long.Parse(
                reader.ReadRemainingUtf8(),
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        long value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Binary)
        {
            writer.WriteInt64BigEndian(value);
        }
        else if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    private static InvalidOperationException FixedWidth(BlueTuskTypeDescriptor type, int width) =>
        new($"PostgreSQL {type.QualifiedName} binary values must contain exactly {width} bytes.");
}

public sealed class BlueTuskUInt32Codec : BlueTuskCodec<uint>
{
    public override uint ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) => format switch
        {
            BlueTuskDataFormat.Binary when reader.Remaining == sizeof(uint) => reader.ReadUInt32BigEndian(),
            BlueTuskDataFormat.Binary => throw FixedWidth(type, sizeof(uint)),
            BlueTuskDataFormat.Text => uint.Parse(
                reader.ReadRemainingUtf8(),
                NumberStyles.None,
                CultureInfo.InvariantCulture),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        uint value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Binary)
        {
            writer.WriteUInt32BigEndian(value);
        }
        else if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    private static InvalidOperationException FixedWidth(BlueTuskTypeDescriptor type, int width) =>
        new($"PostgreSQL {type.QualifiedName} binary values must contain exactly {width} bytes.");
}

public sealed class BlueTuskSingleCodec : BlueTuskCodec<float>
{
    public override float ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) => format switch
        {
            BlueTuskDataFormat.Binary when reader.Remaining == sizeof(float) => reader.ReadSingleBigEndian(),
            BlueTuskDataFormat.Binary => throw FixedWidth(type, sizeof(float)),
            BlueTuskDataFormat.Text => float.Parse(reader.ReadRemainingUtf8(), CultureInfo.InvariantCulture),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        float value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Binary)
        {
            writer.WriteSingleBigEndian(value);
        }
        else if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value.ToString("R", CultureInfo.InvariantCulture));
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    private static InvalidOperationException FixedWidth(BlueTuskTypeDescriptor type, int width) =>
        new($"PostgreSQL {type.QualifiedName} binary values must contain exactly {width} bytes.");
}

public sealed class BlueTuskDoubleCodec : BlueTuskCodec<double>
{
    public override double ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) => format switch
        {
            BlueTuskDataFormat.Binary when reader.Remaining == sizeof(double) => reader.ReadDoubleBigEndian(),
            BlueTuskDataFormat.Binary => throw FixedWidth(type, sizeof(double)),
            BlueTuskDataFormat.Text => double.Parse(reader.ReadRemainingUtf8(), CultureInfo.InvariantCulture),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        double value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Binary)
        {
            writer.WriteDoubleBigEndian(value);
        }
        else if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value.ToString("R", CultureInfo.InvariantCulture));
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    private static InvalidOperationException FixedWidth(BlueTuskTypeDescriptor type, int width) =>
        new($"PostgreSQL {type.QualifiedName} binary values must contain exactly {width} bytes.");
}

public sealed class BlueTuskStringCodec : BlueTuskCodec<string>
{
    public override string ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return format is BlueTuskDataFormat.Text or BlueTuskDataFormat.Binary
            ? reader.ReadRemainingUtf8()
            : throw new ArgumentOutOfRangeException(nameof(format));
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        string value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format is not (BlueTuskDataFormat.Text or BlueTuskDataFormat.Binary))
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        writer.WriteUtf8(value);
    }
}

public sealed class BlueTuskByteArrayCodec : BlueTuskCodec<byte[]>
{
    public override byte[] ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return format switch
        {
            BlueTuskDataFormat.Binary => reader.ReadRemainingBytes().ToArray(),
            BlueTuskDataFormat.Text => DecodeText(reader.ReadRemainingBytes()),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        byte[] value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Binary)
        {
            writer.WriteBytes(value);
            return;
        }

        if (format != BlueTuskDataFormat.Text)
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        writer.WriteBytes("\\x"u8);
        foreach (var item in value)
        {
            writer.WriteByte(ToHex(item >> 4));
            writer.WriteByte(ToHex(item & 0x0F));
        }
    }

    private static byte[] DecodeText(ReadOnlySpan<byte> value)
    {
        if (value.StartsWith("\\x"u8))
        {
            return Convert.FromHexString(System.Text.Encoding.ASCII.GetString(value[2..]));
        }

        var result = new byte[value.Length];
        var written = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != (byte)'\\')
            {
                result[written++] = value[index];
                continue;
            }

            if (++index >= value.Length)
            {
                throw new FormatException("PostgreSQL bytea escape text ends with an incomplete escape.");
            }

            if (value[index] == (byte)'\\')
            {
                result[written++] = (byte)'\\';
                continue;
            }

            if (index + 2 >= value.Length ||
                !IsOctal(value[index]) ||
                !IsOctal(value[index + 1]) ||
                !IsOctal(value[index + 2]))
            {
                throw new FormatException("PostgreSQL bytea text contains an invalid escape.");
            }

            result[written++] = checked((byte)(
                ((value[index] - (byte)'0') << 6) |
                ((value[index + 1] - (byte)'0') << 3) |
                (value[index + 2] - (byte)'0')));
            index += 2;
        }

        return result.AsSpan(0, written).ToArray();
    }

    private static bool IsOctal(byte value) => value is >= (byte)'0' and <= (byte)'7';

    private static byte ToHex(int value) =>
        checked((byte)(value < 10 ? (byte)'0' + value : (byte)'a' + value - 10));
}

public sealed class BlueTuskGuidCodec : BlueTuskCodec<Guid>
{
    public override Guid ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) => format switch
        {
            BlueTuskDataFormat.Binary when reader.Remaining == 16 => new Guid(
                reader.ReadBytes(16),
                bigEndian: true),
            BlueTuskDataFormat.Binary => throw new InvalidOperationException(
                $"PostgreSQL {type.QualifiedName} binary values must contain exactly 16 bytes."),
            BlueTuskDataFormat.Text => Guid.Parse(reader.ReadRemainingUtf8()),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        Guid value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Binary)
        {
            Span<byte> bytes = stackalloc byte[16];
            if (!value.TryWriteBytes(bytes, bigEndian: true, out var bytesWritten) || bytesWritten != bytes.Length)
            {
                throw new InvalidOperationException("Could not encode a UUID value.");
            }

            writer.WriteBytes(bytes);
        }
        else if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value.ToString("D", CultureInfo.InvariantCulture));
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }
}

public sealed class BlueTuskJsonbCodec : BlueTuskCodec<string>
{
    public override string ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Text)
        {
            return reader.ReadRemainingUtf8();
        }

        if (format != BlueTuskDataFormat.Binary)
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        if (reader.Remaining == 0 || reader.ReadByte() != 1)
        {
            throw new InvalidOperationException("PostgreSQL jsonb binary values require version 1.");
        }

        return reader.ReadRemainingUtf8();
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        string value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Binary)
        {
            writer.WriteByte(1);
            writer.WriteUtf8(value);
        }
        else if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }
}
