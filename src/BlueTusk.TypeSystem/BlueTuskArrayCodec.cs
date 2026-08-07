using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;

namespace BlueTusk.TypeSystem;

/// <summary>Encodes PostgreSQL arrays by composing the catalogue-discovered element codec.</summary>
public sealed class BlueTuskArrayCodec :
    IBlueTuskCodec,
    IBlueTuskRangeCodecFactory,
    IBlueTuskWriteFormatSelector
{
    private const int MaximumDimensions = 6;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly BlueTuskTypeDescriptor _elementType;
    private readonly IBlueTuskCodec _elementCodec;
    private readonly IBlueTuskArrayFactory? _arrayFactory;

    public BlueTuskArrayCodec(
        BlueTuskTypeDescriptor elementType,
        IBlueTuskCodec elementCodec)
    {
        _elementType = elementType ?? throw new ArgumentNullException(nameof(elementType));
        _elementCodec = elementCodec ?? throw new ArgumentNullException(nameof(elementCodec));
        _arrayFactory = elementCodec as IBlueTuskArrayFactory;
        ClrType = ResolveArrayClrType(elementCodec, _arrayFactory);
    }

    public Type ClrType { get; }

    public object Read(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) => format switch
        {
            BlueTuskDataFormat.Binary => ReadBinary(ref reader, type),
            BlueTuskDataFormat.Text => ReadText(ref reader, type),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

    public void Write(
        ref BlueTuskWriter writer,
        object? value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        if (value is not Array array)
        {
            throw new InvalidCastException($"The {type.QualifiedName} codec requires a CLR array value.");
        }

        switch (format)
        {
            case BlueTuskDataFormat.Binary:
                WriteBinary(ref writer, array, type);
                break;
            case BlueTuskDataFormat.Text:
                WriteText(ref writer, array);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    IBlueTuskCodec? IBlueTuskRangeCodecFactory.CreateRangeCodec(
        BlueTuskTypeDescriptor subtype,
        IBlueTuskCodec subtypeCodec) =>
        _elementCodec is IBlueTuskArrayRangeCodecFactory factory
            ? factory.CreateArrayRangeCodec(subtype, subtypeCodec)
            : null;

    public BlueTuskDataFormat GetPreferredWriteFormat(
        object value,
        BlueTuskTypeDescriptor type)
    {
        if (value is not Array array)
        {
            throw new InvalidCastException(
                $"The {type.QualifiedName} codec requires a CLR array value.");
        }

        if (_elementCodec is not IBlueTuskWriteFormatSelector selector)
        {
            return BlueTuskDataFormat.Binary;
        }

        var preferredFormat = selector.DefaultWriteFormat;
        foreach (var item in array)
        {
            if (item is not null &&
                selector.GetPreferredWriteFormat(
                    ConvertElementForWrite(item),
                    _elementType) == BlueTuskDataFormat.Text)
            {
                return BlueTuskDataFormat.Text;
            }
        }

        return preferredFormat;
    }

    private Array ReadBinary(ref BlueTuskReader reader, BlueTuskTypeDescriptor type)
    {
        var rank = reader.ReadInt32BigEndian();
        ValidateRank(rank);
        var flags = reader.ReadInt32BigEndian();
        if (flags is not 0 and not 1)
        {
            throw new InvalidOperationException($"The {type.QualifiedName} binary array has invalid flags {flags}.");
        }

        var elementTypeId = new BlueTuskTypeId(reader.ReadUInt32BigEndian());
        if (elementTypeId != _elementType.Id)
        {
            throw new InvalidOperationException(
                $"The {type.QualifiedName} binary array contains element OID {elementTypeId}; " +
                $"OID {_elementType.Id} was expected.");
        }

        if (rank == 0)
        {
            return CreateArray([0], [0]);
        }

        var lengths = new int[rank];
        var lowerBounds = new int[rank];
        var elementCount = 1L;
        for (var dimension = 0; dimension < rank; dimension++)
        {
            var length = reader.ReadInt32BigEndian();
            if (length < 0)
            {
                throw new InvalidOperationException(
                    $"The {type.QualifiedName} binary array has a negative dimension length.");
            }

            lengths[dimension] = length;
            lowerBounds[dimension] = TranslateLowerBound(
                reader.ReadInt32BigEndian(),
                length,
                type);
            elementCount = checked(elementCount * length);
            if (elementCount > Array.MaxLength)
            {
                throw new InvalidOperationException($"The {type.QualifiedName} array exceeds the CLR array limit.");
            }
        }

        if (elementCount > reader.Remaining / sizeof(int))
        {
            throw new InvalidOperationException(
                $"The {type.QualifiedName} binary array element count exceeds the remaining field size.");
        }

        var result = CreateArray(lengths, lowerBounds);
        PopulateBinary(ref reader, result, checked((int)elementCount), flags != 0, type);
        return result;
    }

    private void PopulateBinary(
        ref BlueTuskReader reader,
        Array result,
        int elementCount,
        bool headerAllowsNulls,
        BlueTuskTypeDescriptor arrayType)
    {
        var indexes = GetLowerBounds(result);
        for (var itemIndex = 0; itemIndex < elementCount; itemIndex++)
        {
            var length = reader.ReadInt32BigEndian();
            object? value;
            if (length == -1)
            {
                if (!headerAllowsNulls)
                {
                    throw new InvalidOperationException(
                        $"The {arrayType.QualifiedName} binary array contains a null element without the null flag.");
                }

                EnsureElementCanBeNull(arrayType);
                value = null;
            }
            else
            {
                if (length < 0)
                {
                    throw new InvalidOperationException(
                        $"The {arrayType.QualifiedName} binary array has invalid element length {length}.");
                }

                if (length > reader.Remaining)
                {
                    throw new InvalidOperationException(
                        $"The {arrayType.QualifiedName} binary array element length {length} " +
                        $"exceeds the {reader.Remaining} remaining field bytes.");
                }

                var elementReader = new BlueTuskReader(reader.ReadBytes(length));
                value = _elementCodec.Read(ref elementReader, BlueTuskDataFormat.Binary, _elementType);
                EnsureElementConsumed(elementReader.Remaining);
            }

            result.SetValue(value, indexes);
            MoveNext(result, indexes);
        }
    }

    private Array ReadText(ref BlueTuskReader reader, BlueTuskTypeDescriptor arrayType)
    {
        var parsed = BlueTuskArrayTextParser.Parse(
            reader.ReadRemainingUtf8(),
            _elementType.Delimiter,
            MaximumDimensions);
        if (parsed.Lengths.Length == 0)
        {
            return CreateArray([0], [0]);
        }

        var lowerBounds = new int[parsed.LowerBounds.Length];
        for (var dimension = 0; dimension < lowerBounds.Length; dimension++)
        {
            lowerBounds[dimension] = TranslateLowerBound(
                parsed.LowerBounds[dimension],
                parsed.Lengths[dimension],
                arrayType);
        }

        var result = CreateArray(parsed.Lengths, lowerBounds);
        var indexes = GetLowerBounds(result);
        foreach (var item in parsed.Elements)
        {
            object? value;
            if (item is null)
            {
                EnsureElementCanBeNull(arrayType);
                value = null;
            }
            else
            {
                var bytes = StrictUtf8.GetBytes(item);
                var elementReader = new BlueTuskReader(bytes);
                value = _elementCodec.Read(ref elementReader, BlueTuskDataFormat.Text, _elementType);
                EnsureElementConsumed(elementReader.Remaining);
            }

            result.SetValue(value, indexes);
            MoveNext(result, indexes);
        }

        return result;
    }

    private void WriteBinary(
        ref BlueTuskWriter writer,
        Array value,
        BlueTuskTypeDescriptor arrayType)
    {
        ValidateRank(value.Rank);
        var rank = value.Length == 0 ? 0 : value.Rank;
        var hasNulls = value.Cast<object?>().Any(item => item is null);
        writer.WriteInt32BigEndian(rank);
        writer.WriteInt32BigEndian(hasNulls ? 1 : 0);
        writer.WriteUInt32BigEndian(_elementType.Id.Oid);
        for (var dimension = 0; dimension < rank; dimension++)
        {
            writer.WriteInt32BigEndian(value.GetLength(dimension));
            writer.WriteInt32BigEndian(checked(value.GetLowerBound(dimension) + 1));
        }

        foreach (var item in value)
        {
            if (item is null)
            {
                writer.WriteInt32BigEndian(-1);
                continue;
            }

            var lengthOffset = writer.WrittenCount;
            writer.WriteInt32BigEndian(0);
            var valueOffset = writer.WrittenCount;
            _elementCodec.Write(
                ref writer,
                ConvertElementForWrite(item),
                BlueTuskDataFormat.Binary,
                _elementType);
            writer.WriteInt32BigEndianAt(lengthOffset, writer.WrittenCount - valueOffset);
        }
    }

    private void WriteText(ref BlueTuskWriter writer, Array value)
    {
        ValidateRank(value.Rank);
        if (value.Length == 0)
        {
            writer.WriteUtf8("{}");
            return;
        }

        if (Enumerable.Range(0, value.Rank).Any(dimension => value.GetLowerBound(dimension) != 0))
        {
            for (var dimension = 0; dimension < value.Rank; dimension++)
            {
                var lowerBound = checked(value.GetLowerBound(dimension) + 1);
                var upperBound = checked(lowerBound + value.GetLength(dimension) - 1);
                writer.WriteByte((byte)'[');
                writer.WriteUtf8(lowerBound.ToString(System.Globalization.CultureInfo.InvariantCulture));
                writer.WriteByte((byte)':');
                writer.WriteUtf8(upperBound.ToString(System.Globalization.CultureInfo.InvariantCulture));
                writer.WriteByte((byte)']');
            }

            writer.WriteByte((byte)'=');
        }

        var indexes = GetLowerBounds(value);
        WriteTextDimension(ref writer, value, indexes, dimension: 0);
    }

    private void WriteTextDimension(
        ref BlueTuskWriter writer,
        Array value,
        int[] indexes,
        int dimension)
    {
        writer.WriteByte((byte)'{');
        var lower = value.GetLowerBound(dimension);
        var upper = value.GetUpperBound(dimension);
        for (var index = lower; index <= upper; index++)
        {
            if (index != lower)
            {
                writer.WriteUtf8(_elementType.Delimiter.ToString());
            }

            indexes[dimension] = index;
            if (dimension == value.Rank - 1)
            {
                WriteTextElement(ref writer, value.GetValue(indexes));
            }
            else
            {
                WriteTextDimension(ref writer, value, indexes, dimension + 1);
            }
        }

        writer.WriteByte((byte)'}');
    }

    private void WriteTextElement(ref BlueTuskWriter writer, object? value)
    {
        if (value is null)
        {
            writer.WriteUtf8("NULL");
            return;
        }

        var text = EncodeTextElement(value);
        var requiresQuotes = text.Length == 0 ||
            string.Equals(text, "NULL", StringComparison.OrdinalIgnoreCase) ||
            text.Any(character =>
                character == '{' ||
                character == '}' ||
                character == _elementType.Delimiter ||
                character == '"' ||
                character == '\\' ||
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
                escaped.Append('\\');
            }

            escaped.Append(character);
        }

        escaped.Append('"');
        writer.WriteUtf8(escaped.ToString());
    }

    private string EncodeTextElement(object value)
    {
        value = ConvertElementForWrite(value);
        var length = 64;
        while (true)
        {
            var bytes = new byte[length];
            var writer = new BlueTuskWriter(bytes);
            try
            {
                _elementCodec.Write(ref writer, value, BlueTuskDataFormat.Text, _elementType);
                return StrictUtf8.GetString(bytes, 0, writer.WrittenCount);
            }
            catch (BlueTuskWriteBufferTooSmallException) when (length < Array.MaxLength)
            {
                length = length > Array.MaxLength / 2 ? Array.MaxLength : length * 2;
            }
        }
    }

    private object ConvertElementForWrite(object value)
    {
        if (_elementCodec.ClrType.IsInstanceOfType(value))
        {
            return value;
        }

        if (_elementCodec.ClrType == typeof(BlueTuskNumeric) && value is decimal decimalValue)
        {
            return (BlueTuskNumeric)decimalValue;
        }

        throw new InvalidCastException(
            $"The {_elementType.QualifiedName} array element codec requires a " +
            $"{_elementCodec.ClrType.FullName} value, but received {value.GetType().FullName}.");
    }

    private void EnsureElementCanBeNull(BlueTuskTypeDescriptor arrayType)
    {
        if (_elementCodec.ClrType.IsValueType && Nullable.GetUnderlyingType(_elementCodec.ClrType) is null)
        {
            throw new InvalidOperationException(
                $"The {arrayType.QualifiedName} array contains a null {_elementType.QualifiedName} element, " +
                $"which cannot be stored in {_elementCodec.ClrType.FullName}[].");
        }
    }

    private void EnsureElementConsumed(int remaining)
    {
        if (remaining != 0)
        {
            throw new InvalidOperationException(
                $"The {_elementType.QualifiedName} codec left {remaining} unread array-element bytes.");
        }
    }

    private static int[] GetLowerBounds(Array value) =>
        Enumerable.Range(0, value.Rank).Select(value.GetLowerBound).ToArray();

    private static void MoveNext(Array value, int[] indexes)
    {
        for (var dimension = value.Rank - 1; dimension >= 0; dimension--)
        {
            if (indexes[dimension] < value.GetUpperBound(dimension))
            {
                indexes[dimension]++;
                return;
            }

            indexes[dimension] = value.GetLowerBound(dimension);
        }
    }

    private static void ValidateRank(int rank)
    {
        if (rank is < 0 or > MaximumDimensions)
        {
            throw new InvalidOperationException(
                $"PostgreSQL arrays support between 0 and {MaximumDimensions} dimensions; {rank} were supplied.");
        }
    }

    private static int TranslateLowerBound(
        int postgreSqlLowerBound,
        int length,
        BlueTuskTypeDescriptor arrayType)
    {
        var lowerBound = (long)postgreSqlLowerBound - 1;
        var upperBound = lowerBound + length - 1L;
        if (lowerBound < int.MinValue ||
            lowerBound > int.MaxValue ||
            (length > 0 && upperBound > int.MaxValue))
        {
            throw new InvalidOperationException(
                $"The {arrayType.QualifiedName} array bounds cannot be represented by a CLR array.");
        }

        return (int)lowerBound;
    }

    [UnconditionalSuppressMessage(
        "Aot",
        "IL3050",
        Justification =
            "The dynamic fallback is guarded by RuntimeFeature.IsDynamicCodeSupported; " +
            "NativeAOT requires the statically typed BlueTuskCodec<T> array factory.")]
    private static Type ResolveArrayClrType(
        IBlueTuskCodec elementCodec,
        IBlueTuskArrayFactory? arrayFactory)
    {
        if (arrayFactory is not null)
        {
            return arrayFactory.ArrayClrType;
        }

        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            throw UnsupportedCustomCodec(elementCodec);
        }

        return elementCodec.ClrType.MakeArrayType();
    }

    [UnconditionalSuppressMessage(
        "Aot",
        "IL3050",
        Justification =
            "The dynamic fallback is guarded by RuntimeFeature.IsDynamicCodeSupported; " +
            "NativeAOT requires the statically typed BlueTuskCodec<T> array factory.")]
    private Array CreateArray(
        ReadOnlySpan<int> lengths,
        ReadOnlySpan<int> lowerBounds)
    {
        if (_arrayFactory is not null)
        {
            return _arrayFactory.CreateArray(lengths, lowerBounds);
        }

        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            throw UnsupportedCustomCodec(_elementCodec);
        }

        return Array.CreateInstance(
            _elementCodec.ClrType,
            lengths.ToArray(),
            lowerBounds.ToArray());
    }

    private static NotSupportedException UnsupportedCustomCodec(IBlueTuskCodec elementCodec) =>
        new(
            $"NativeAOT array support requires element codec " +
            $"'{elementCodec.GetType().FullName}' to derive from BlueTuskCodec<T>. " +
            "Direct IBlueTuskCodec implementations require a JIT deployment.");
}

internal sealed class BlueTuskArrayCodec<T> :
    IBlueTuskCodec<T[]>,
    IBlueTuskRangeCodecFactory,
    IBlueTuskWriteFormatSelector
{
    private readonly BlueTuskTypeDescriptor _elementType;
    private readonly IBlueTuskCodec<T> _elementCodec;
    private readonly BlueTuskArrayCodec _fallback;

    internal BlueTuskArrayCodec(
        BlueTuskTypeDescriptor elementType,
        IBlueTuskCodec<T> elementCodec)
    {
        _elementType = elementType ?? throw new ArgumentNullException(nameof(elementType));
        _elementCodec = elementCodec ?? throw new ArgumentNullException(nameof(elementCodec));
        _fallback = new BlueTuskArrayCodec(elementType, elementCodec);
    }

    public Type ClrType => typeof(T[]);

    public object Read(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        if (format == BlueTuskDataFormat.Binary && CanReadTypedBinary(ref reader))
        {
            return ReadTyped(ref reader, format, type);
        }

        return _fallback.Read(ref reader, format, type);
    }

    public T[] ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        if (format == BlueTuskDataFormat.Text)
        {
            var value = _fallback.Read(ref reader, format, type);
            return value as T[] ?? throw UnsupportedTypedShape(type);
        }

        if (format != BlueTuskDataFormat.Binary)
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        var probe = reader;
        var rank = probe.ReadInt32BigEndian();
        if (rank > 1)
        {
            throw UnsupportedTypedShape(type);
        }

        if (rank < 0)
        {
            throw new InvalidOperationException(
                $"The {type.QualifiedName} binary array has invalid rank {rank}.");
        }

        var flags = probe.ReadInt32BigEndian();
        if (flags is not 0 and not 1)
        {
            throw new InvalidOperationException(
                $"The {type.QualifiedName} binary array has invalid flags {flags}.");
        }

        var elementTypeId = new BlueTuskTypeId(probe.ReadUInt32BigEndian());
        if (elementTypeId != _elementType.Id)
        {
            throw new InvalidOperationException(
                $"The {type.QualifiedName} binary array contains element OID {elementTypeId}; " +
                $"OID {_elementType.Id} was expected.");
        }

        if (rank == 0)
        {
            reader = probe;
            return [];
        }

        var length = probe.ReadInt32BigEndian();
        if (length < 0)
        {
            throw new InvalidOperationException(
                $"The {type.QualifiedName} binary array has a negative dimension length.");
        }

        var lowerBound = (long)probe.ReadInt32BigEndian() - 1;
        if (lowerBound != 0)
        {
            throw UnsupportedTypedShape(type);
        }

        if (length > probe.Remaining / sizeof(int))
        {
            throw new InvalidOperationException(
                $"The {type.QualifiedName} binary array element count exceeds the remaining field size.");
        }

        var result = new T[length];
        for (var index = 0; index < result.Length; index++)
        {
            var elementLength = probe.ReadInt32BigEndian();
            if (elementLength == -1)
            {
                if (flags == 0)
                {
                    throw new InvalidOperationException(
                        $"The {type.QualifiedName} binary array contains a null element without the null flag.");
                }

                if (typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) is null)
                {
                    throw new InvalidOperationException(
                        $"The {type.QualifiedName} array contains a null {_elementType.QualifiedName} element, " +
                        $"which cannot be stored in {typeof(T).FullName}[].");
                }

                result[index] = default!;
                continue;
            }

            if (elementLength < 0 || elementLength > probe.Remaining)
            {
                throw new InvalidOperationException(
                    $"The {type.QualifiedName} binary array contains invalid element length {elementLength}.");
            }

            var elementReader = new BlueTuskReader(probe.ReadBytes(elementLength));
            result[index] = _elementCodec.ReadTyped(
                ref elementReader,
                BlueTuskDataFormat.Binary,
                _elementType);
            if (elementReader.Remaining != 0)
            {
                throw new InvalidOperationException(
                    $"The {_elementType.QualifiedName} codec left {elementReader.Remaining} unread array-element bytes.");
            }
        }

        reader = probe;
        return result;
    }

    public void Write(
        ref BlueTuskWriter writer,
        object? value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        if (value is T[] typedValue)
        {
            WriteTyped(ref writer, typedValue, format, type);
            return;
        }

        _fallback.Write(ref writer, value, format, type);
    }

    public void WriteTyped(
        ref BlueTuskWriter writer,
        T[] value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (format == BlueTuskDataFormat.Text)
        {
            _fallback.Write(ref writer, value, format, type);
            return;
        }

        if (format != BlueTuskDataFormat.Binary)
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        var hasNulls = false;
        if (!typeof(T).IsValueType || Nullable.GetUnderlyingType(typeof(T)) is not null)
        {
            foreach (var item in value)
            {
                if (item is null)
                {
                    hasNulls = true;
                    break;
                }
            }
        }

        var rank = value.Length == 0 ? 0 : 1;
        writer.WriteInt32BigEndian(rank);
        writer.WriteInt32BigEndian(hasNulls ? 1 : 0);
        writer.WriteUInt32BigEndian(_elementType.Id.Oid);
        if (rank != 0)
        {
            writer.WriteInt32BigEndian(value.Length);
            writer.WriteInt32BigEndian(1);
        }

        foreach (var item in value)
        {
            if (item is null)
            {
                writer.WriteInt32BigEndian(-1);
                continue;
            }

            var lengthOffset = writer.WrittenCount;
            writer.WriteInt32BigEndian(0);
            var valueOffset = writer.WrittenCount;
            _elementCodec.WriteTyped(
                ref writer,
                item,
                BlueTuskDataFormat.Binary,
                _elementType);
            writer.WriteInt32BigEndianAt(lengthOffset, writer.WrittenCount - valueOffset);
        }
    }

    public BlueTuskDataFormat GetPreferredWriteFormat(
        object value,
        BlueTuskTypeDescriptor type) =>
        _fallback.GetPreferredWriteFormat(value, type);

    IBlueTuskCodec? IBlueTuskRangeCodecFactory.CreateRangeCodec(
        BlueTuskTypeDescriptor subtype,
        IBlueTuskCodec subtypeCodec) =>
        ((IBlueTuskRangeCodecFactory)_fallback).CreateRangeCodec(
            subtype,
            subtypeCodec);

    private static bool CanReadTypedBinary(ref BlueTuskReader reader)
    {
        var probe = reader;
        var rank = probe.ReadInt32BigEndian();
        if (rank != 1)
        {
            return rank == 0;
        }

        probe.ReadInt32BigEndian();
        probe.ReadUInt32BigEndian();
        probe.ReadInt32BigEndian();
        return probe.ReadInt32BigEndian() == 1;
    }

    private static InvalidCastException UnsupportedTypedShape(
        BlueTuskTypeDescriptor type) =>
        new(
            $"The typed {type.QualifiedName} codec supports one-dimensional arrays " +
            "with PostgreSQL's standard lower bound of 1. Use the non-generic codec " +
            "to read multidimensional arrays or arrays with non-standard lower bounds.");
}
