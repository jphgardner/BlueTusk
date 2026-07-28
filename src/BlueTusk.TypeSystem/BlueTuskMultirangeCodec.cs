namespace BlueTusk.TypeSystem;

/// <summary>Composes a PostgreSQL multirange codec from its corresponding range codec.</summary>
public sealed class BlueTuskMultirangeCodec<T> : BlueTuskCodec<BlueTuskMultirange<T>>
{
    private readonly BlueTuskTypeDescriptor _rangeType;
    private readonly BlueTuskRangeCodec<T> _rangeCodec;

    internal BlueTuskMultirangeCodec(
        BlueTuskTypeDescriptor rangeType,
        BlueTuskRangeCodec<T> rangeCodec)
    {
        _rangeType = rangeType ?? throw new ArgumentNullException(nameof(rangeType));
        _rangeCodec = rangeCodec ?? throw new ArgumentNullException(nameof(rangeCodec));
    }

    public override BlueTuskMultirange<T> ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) => format switch
        {
            BlueTuskDataFormat.Binary => ReadBinary(ref reader, type),
            BlueTuskDataFormat.Text => ReadText(ref reader),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskMultirange<T> value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(value);
        switch (format)
        {
            case BlueTuskDataFormat.Binary:
                WriteBinary(ref writer, value);
                break;
            case BlueTuskDataFormat.Text:
                WriteText(ref writer, value);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    private BlueTuskMultirange<T> ReadBinary(
        ref BlueTuskReader reader,
        BlueTuskTypeDescriptor multirangeType)
    {
        var count = reader.ReadInt32BigEndian();
        if (count < 0 || count > reader.Remaining / (sizeof(int) + sizeof(byte)))
        {
            throw new InvalidOperationException(
                $"The {multirangeType.QualifiedName} binary multirange has invalid range count {count}.");
        }

        var ranges = new BlueTuskRange<T>[count];
        for (var index = 0; index < ranges.Length; index++)
        {
            var length = reader.ReadInt32BigEndian();
            if (length <= 0 || length > reader.Remaining)
            {
                throw new InvalidOperationException(
                    $"The {multirangeType.QualifiedName} binary multirange has invalid range length {length}.");
            }

            var rangeReader = new BlueTuskReader(reader.ReadBytes(length));
            ranges[index] = _rangeCodec.ReadTyped(
                ref rangeReader,
                BlueTuskDataFormat.Binary,
                _rangeType);
            if (rangeReader.Remaining != 0)
            {
                throw new InvalidOperationException(
                    $"The {_rangeType.QualifiedName} codec left {rangeReader.Remaining} unread multirange bytes.");
            }
        }

        if (reader.Remaining != 0)
        {
            throw new InvalidOperationException(
                $"The {multirangeType.QualifiedName} codec left {reader.Remaining} unread multirange bytes.");
        }

        return new BlueTuskMultirange<T>(ranges);
    }

    private BlueTuskMultirange<T> ReadText(ref BlueTuskReader reader)
    {
        var parsed = BlueTuskMultirangeTextParser.Parse(reader.ReadRemainingUtf8());
        var ranges = new BlueTuskRange<T>[parsed.Length];
        for (var index = 0; index < ranges.Length; index++)
        {
            var rangeReader = new BlueTuskReader(System.Text.Encoding.UTF8.GetBytes(parsed[index]));
            ranges[index] = _rangeCodec.ReadTyped(
                ref rangeReader,
                BlueTuskDataFormat.Text,
                _rangeType);
        }

        return new BlueTuskMultirange<T>(ranges);
    }

    private void WriteBinary(
        ref BlueTuskWriter writer,
        BlueTuskMultirange<T> value)
    {
        writer.WriteInt32BigEndian(value.Count);
        foreach (var range in value)
        {
            var lengthOffset = writer.WrittenCount;
            writer.WriteInt32BigEndian(0);
            var rangeOffset = writer.WrittenCount;
            _rangeCodec.WriteTyped(
                ref writer,
                range,
                BlueTuskDataFormat.Binary,
                _rangeType);
            writer.WriteInt32BigEndianAt(lengthOffset, writer.WrittenCount - rangeOffset);
        }
    }

    private void WriteText(
        ref BlueTuskWriter writer,
        BlueTuskMultirange<T> value)
    {
        writer.WriteByte((byte)'{');
        for (var index = 0; index < value.Count; index++)
        {
            if (index != 0)
            {
                writer.WriteByte((byte)',');
            }

            _rangeCodec.WriteTyped(
                ref writer,
                value[index],
                BlueTuskDataFormat.Text,
                _rangeType);
        }

        writer.WriteByte((byte)'}');
    }
}
