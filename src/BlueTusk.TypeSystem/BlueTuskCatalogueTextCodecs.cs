namespace BlueTusk.TypeSystem;

public sealed class BlueTuskRefCursorCodec : BlueTuskCodec<BlueTuskRefCursor>
{
    public override BlueTuskRefCursor ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        EnsureTextLikeFormat(format);
        return new BlueTuskRefCursor(reader.ReadRemainingUtf8());
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskRefCursor value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        EnsureTextLikeFormat(format);
        writer.WriteUtf8(value.Value);
    }

    private static void EnsureTextLikeFormat(BlueTuskDataFormat format)
    {
        if (format is not (BlueTuskDataFormat.Text or BlueTuskDataFormat.Binary))
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }
}

public sealed class BlueTuskNodeTreeCodec : BlueTuskCodec<BlueTuskNodeTree>
{
    public override BlueTuskNodeTree ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        if (format is not (BlueTuskDataFormat.Text or BlueTuskDataFormat.Binary))
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        return new BlueTuskNodeTree(reader.ReadRemainingUtf8());
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskNodeTree value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) =>
        throw new NotSupportedException(
            $"PostgreSQL does not accept input values for {type.QualifiedName}.");
}

public sealed class BlueTuskJsonPathCodec : BlueTuskCodec<BlueTuskJsonPath>
{
    private const byte BinaryVersion = 1;

    public override BlueTuskJsonPath ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        if (format == BlueTuskDataFormat.Binary)
        {
            if (reader.Remaining == 0 || reader.ReadByte() != BinaryVersion)
            {
                throw new InvalidOperationException(
                    $"The {type.QualifiedName} binary version is not supported.");
            }
        }
        else if (format != BlueTuskDataFormat.Text)
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        return new BlueTuskJsonPath(reader.ReadRemainingUtf8());
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskJsonPath value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        if (format == BlueTuskDataFormat.Binary)
        {
            writer.WriteByte(BinaryVersion);
        }
        else if (format != BlueTuskDataFormat.Text)
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        writer.WriteUtf8(value.Value);
    }
}

public sealed class BlueTuskInternalCharCodec : BlueTuskCodec<BlueTuskInternalChar>
{
    public override BlueTuskInternalChar ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        if (format == BlueTuskDataFormat.Binary)
        {
            if (reader.Remaining != sizeof(byte))
            {
                throw InvalidValue(type);
            }

            return new BlueTuskInternalChar(reader.ReadByte());
        }

        if (format != BlueTuskDataFormat.Text)
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        var bytes = reader.ReadRemainingBytes();
        if (bytes.Length == 0)
        {
            return new BlueTuskInternalChar(0);
        }

        if (bytes.Length == 1 && bytes[0] <= 0x7F)
        {
            return new BlueTuskInternalChar(bytes[0]);
        }

        if (bytes.Length == 4 &&
            bytes[0] == (byte)'\\' &&
            IsOctal(bytes[1]) &&
            IsOctal(bytes[2]) &&
            IsOctal(bytes[3]))
        {
            return new BlueTuskInternalChar(
                (byte)(((bytes[1] - '0') << 6) |
                    ((bytes[2] - '0') << 3) |
                    (bytes[3] - '0')));
        }

        throw InvalidValue(type);
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskInternalChar value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        if (format == BlueTuskDataFormat.Binary)
        {
            writer.WriteByte(value.Value);
        }
        else if (format == BlueTuskDataFormat.Text)
        {
            WriteText(ref writer, value.Value);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    private static void WriteText(ref BlueTuskWriter writer, byte value)
    {
        if (value == 0)
        {
            return;
        }

        if (value <= 0x7F)
        {
            writer.WriteByte(value);
            return;
        }

        writer.WriteByte((byte)'\\');
        writer.WriteByte((byte)('0' + (value >> 6)));
        writer.WriteByte((byte)('0' + ((value >> 3) & 0x07)));
        writer.WriteByte((byte)('0' + (value & 0x07)));
    }

    private static bool IsOctal(byte value) =>
        value is >= (byte)'0' and <= (byte)'7';

    private static InvalidOperationException InvalidValue(BlueTuskTypeDescriptor type) =>
        new($"The {type.QualifiedName} value is not a canonical PostgreSQL internal character.");
}
