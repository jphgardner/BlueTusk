using System.Text;

namespace BlueTusk.TypeSystem;

/// <summary>Composes a PostgreSQL range codec from its catalogue-discovered subtype codec.</summary>
public sealed class BlueTuskRangeCodec<T> :
    BlueTuskCodec<BlueTuskRange<T>>,
    IBlueTuskRangeCodecFactory,
    IBlueTuskArrayRangeCodecFactory,
    IBlueTuskMultirangeCodecFactory
{
    private const byte EmptyFlag = 0x01;
    private const byte LowerInclusiveFlag = 0x02;
    private const byte UpperInclusiveFlag = 0x04;
    private const byte LowerInfiniteFlag = 0x08;
    private const byte UpperInfiniteFlag = 0x10;
    private const byte SupportedFlags =
        EmptyFlag |
        LowerInclusiveFlag |
        UpperInclusiveFlag |
        LowerInfiniteFlag |
        UpperInfiniteFlag;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly BlueTuskTypeDescriptor _subtype;
    private readonly IBlueTuskCodec _subtypeCodec;

    internal BlueTuskRangeCodec(
        BlueTuskTypeDescriptor subtype,
        IBlueTuskCodec subtypeCodec)
    {
        _subtype = subtype ?? throw new ArgumentNullException(nameof(subtype));
        _subtypeCodec = subtypeCodec ?? throw new ArgumentNullException(nameof(subtypeCodec));
        if (_subtypeCodec.ClrType != typeof(T))
        {
            throw new ArgumentException(
                $"The {_subtype.QualifiedName} codec maps to {_subtypeCodec.ClrType.FullName}, not {typeof(T).FullName}.",
                nameof(subtypeCodec));
        }
    }

    public override BlueTuskRange<T> ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) => format switch
        {
            BlueTuskDataFormat.Binary => ReadBinary(ref reader, type),
            BlueTuskDataFormat.Text => ReadText(ref reader, type),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskRange<T> value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
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

    IBlueTuskCodec IBlueTuskMultirangeCodecFactory.CreateMultirangeCodec(
        BlueTuskTypeDescriptor rangeType) =>
        new BlueTuskMultirangeCodec<T>(rangeType, this);

    IBlueTuskCodec? IBlueTuskRangeCodecFactory.CreateRangeCodec(
        BlueTuskTypeDescriptor subtype,
        IBlueTuskCodec subtypeCodec) =>
        BlueTuskDynamicRangeCodecFactory.Create(
            typeof(BlueTuskRange<T>),
            subtype,
            subtypeCodec);

    IBlueTuskCodec IBlueTuskArrayRangeCodecFactory.CreateArrayRangeCodec(
        BlueTuskTypeDescriptor subtype,
        IBlueTuskCodec subtypeCodec) =>
        BlueTuskDynamicRangeCodecFactory.Create(
            typeof(BlueTuskRange<T>[]),
            subtype,
            subtypeCodec);

    private BlueTuskRange<T> ReadBinary(
        ref BlueTuskReader reader,
        BlueTuskTypeDescriptor rangeType)
    {
        var flags = reader.ReadByte();
        if ((flags & ~SupportedFlags) != 0)
        {
            throw new InvalidOperationException(
                $"The {rangeType.QualifiedName} binary range contains unsupported flags 0x{flags:X2}.");
        }

        if ((flags & EmptyFlag) != 0)
        {
            if (flags != EmptyFlag || reader.Remaining != 0)
            {
                throw new InvalidOperationException(
                    $"The {rangeType.QualifiedName} binary empty range contains bounds or incompatible flags.");
            }

            return BlueTuskRange.Empty<T>();
        }

        var lowerInfinite = (flags & LowerInfiniteFlag) != 0;
        var upperInfinite = (flags & UpperInfiniteFlag) != 0;
        if (lowerInfinite && (flags & LowerInclusiveFlag) != 0 ||
            upperInfinite && (flags & UpperInclusiveFlag) != 0)
        {
            throw new InvalidOperationException(
                $"The {rangeType.QualifiedName} binary range marks an infinite boundary as inclusive.");
        }

        var lower = lowerInfinite
            ? BlueTuskRangeBound.Unbounded<T>()
            : ReadBound(ref reader, (flags & LowerInclusiveFlag) != 0, rangeType);
        var upper = upperInfinite
            ? BlueTuskRangeBound.Unbounded<T>()
            : ReadBound(ref reader, (flags & UpperInclusiveFlag) != 0, rangeType);
        EnsureFullyConsumed(reader.Remaining, rangeType);
        return new BlueTuskRange<T>(lower, upper);
    }

    private BlueTuskRangeBound<T> ReadBound(
        ref BlueTuskReader reader,
        bool inclusive,
        BlueTuskTypeDescriptor rangeType)
    {
        var length = reader.ReadInt32BigEndian();
        if (length < 0 || length > reader.Remaining)
        {
            throw new InvalidOperationException(
                $"The {rangeType.QualifiedName} binary range contains invalid boundary length {length}.");
        }

        var boundReader = new BlueTuskReader(reader.ReadBytes(length));
        var value = _subtypeCodec.Read(ref boundReader, BlueTuskDataFormat.Binary, _subtype);
        EnsureBoundConsumed(boundReader.Remaining, rangeType);
        if (value is not T typedValue)
        {
            throw new InvalidOperationException(
                $"The {_subtype.QualifiedName} codec returned {value?.GetType().FullName ?? "null"}; " +
                $"{typeof(T).FullName} was expected.");
        }

        return inclusive
            ? BlueTuskRangeBound.Inclusive(typedValue)
            : BlueTuskRangeBound.Exclusive(typedValue);
    }

    private BlueTuskRange<T> ReadText(
        ref BlueTuskReader reader,
        BlueTuskTypeDescriptor rangeType)
    {
        var parsed = BlueTuskRangeTextParser.Parse(reader.ReadRemainingUtf8());
        if (parsed.IsEmpty)
        {
            return BlueTuskRange.Empty<T>();
        }

        var lower = parsed.LowerBound is null
            ? BlueTuskRangeBound.Unbounded<T>()
            : DecodeTextBound(parsed.LowerBound, parsed.LowerInclusive, rangeType);
        var upper = parsed.UpperBound is null
            ? BlueTuskRangeBound.Unbounded<T>()
            : DecodeTextBound(parsed.UpperBound, parsed.UpperInclusive, rangeType);
        return new BlueTuskRange<T>(lower, upper);
    }

    private BlueTuskRangeBound<T> DecodeTextBound(
        string text,
        bool inclusive,
        BlueTuskTypeDescriptor rangeType)
    {
        var bytes = StrictUtf8.GetBytes(text);
        var boundReader = new BlueTuskReader(bytes);
        var value = _subtypeCodec.Read(ref boundReader, BlueTuskDataFormat.Text, _subtype);
        EnsureBoundConsumed(boundReader.Remaining, rangeType);
        if (value is not T typedValue)
        {
            throw new InvalidOperationException(
                $"The {_subtype.QualifiedName} codec returned {value?.GetType().FullName ?? "null"}; " +
                $"{typeof(T).FullName} was expected.");
        }

        return inclusive
            ? BlueTuskRangeBound.Inclusive(typedValue)
            : BlueTuskRangeBound.Exclusive(typedValue);
    }

    private void WriteBinary(
        ref BlueTuskWriter writer,
        BlueTuskRange<T> value)
    {
        if (value.IsEmpty)
        {
            writer.WriteByte(EmptyFlag);
            return;
        }

        byte flags = 0;
        flags |= value.LowerBound.IsInfinite
            ? LowerInfiniteFlag
            : value.LowerBound.IsInclusive ? LowerInclusiveFlag : (byte)0;
        flags |= value.UpperBound.IsInfinite
            ? UpperInfiniteFlag
            : value.UpperBound.IsInclusive ? UpperInclusiveFlag : (byte)0;
        writer.WriteByte(flags);
        if (value.LowerBound.HasValue)
        {
            WriteBinaryBound(ref writer, value.LowerBound.Value);
        }

        if (value.UpperBound.HasValue)
        {
            WriteBinaryBound(ref writer, value.UpperBound.Value);
        }
    }

    private void WriteBinaryBound(ref BlueTuskWriter writer, T value)
    {
        var lengthOffset = writer.WrittenCount;
        writer.WriteInt32BigEndian(0);
        var valueOffset = writer.WrittenCount;
        _subtypeCodec.Write(ref writer, value, BlueTuskDataFormat.Binary, _subtype);
        writer.WriteInt32BigEndianAt(lengthOffset, writer.WrittenCount - valueOffset);
    }

    private void WriteText(
        ref BlueTuskWriter writer,
        BlueTuskRange<T> value)
    {
        if (value.IsEmpty)
        {
            writer.WriteUtf8("empty");
            return;
        }

        writer.WriteByte(value.LowerBound.IsInclusive ? (byte)'[' : (byte)'(');
        if (value.LowerBound.HasValue)
        {
            WriteTextBound(ref writer, EncodeTextBound(value.LowerBound.Value));
        }

        writer.WriteByte((byte)',');
        if (value.UpperBound.HasValue)
        {
            WriteTextBound(ref writer, EncodeTextBound(value.UpperBound.Value));
        }

        writer.WriteByte(value.UpperBound.IsInclusive ? (byte)']' : (byte)')');
    }

    private string EncodeTextBound(T value)
    {
        var length = 64;
        while (true)
        {
            var bytes = new byte[length];
            var writer = new BlueTuskWriter(bytes);
            try
            {
                _subtypeCodec.Write(ref writer, value, BlueTuskDataFormat.Text, _subtype);
                return StrictUtf8.GetString(bytes, 0, writer.WrittenCount);
            }
            catch (BlueTuskWriteBufferTooSmallException) when (length < Array.MaxLength)
            {
                length = length > Array.MaxLength / 2 ? Array.MaxLength : length * 2;
            }
        }
    }

    private static void WriteTextBound(ref BlueTuskWriter writer, string text)
    {
        var requiresQuotes = text.Length == 0 || text.Any(character =>
            character is '(' or ')' or '[' or ']' or ',' or '"' or '\\' ||
            char.IsWhiteSpace(character));
        if (!requiresQuotes)
        {
            writer.WriteUtf8(text);
            return;
        }

        var escaped = new StringBuilder(text.Length + 2);
        escaped.Append('"');
        foreach (var character in text)
        {
            if (character is '"' or '\\')
            {
                escaped.Append(character);
            }

            escaped.Append(character);
        }

        escaped.Append('"');
        writer.WriteUtf8(escaped.ToString());
    }

    private void EnsureBoundConsumed(int remaining, BlueTuskTypeDescriptor rangeType)
    {
        if (remaining != 0)
        {
            throw new InvalidOperationException(
                $"The {_subtype.QualifiedName} codec left {remaining} unread " +
                $"{rangeType.QualifiedName} boundary bytes.");
        }
    }

    private static void EnsureFullyConsumed(int remaining, BlueTuskTypeDescriptor rangeType)
    {
        if (remaining != 0)
        {
            throw new InvalidOperationException(
                $"The {rangeType.QualifiedName} codec left {remaining} unread range bytes.");
        }
    }
}
