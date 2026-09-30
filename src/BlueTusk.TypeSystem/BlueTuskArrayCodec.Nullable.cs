namespace BlueTusk.TypeSystem;

public sealed partial class BlueTuskArrayCodec
{
    TElement?[] IBlueTuskNullableArrayCodec.ReadNullableElements<TElement>(ref BlueTuskReader reader,
        BlueTuskDataFormat format, BlueTuskTypeDescriptor type) => ReadNullableElements<TElement>(ref reader, format, type);

    internal TElement?[] ReadNullableElements<TElement>(ref BlueTuskReader reader, BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) where TElement : struct
    {
        if (_elementCodec is not IBlueTuskCodec<TElement> codec)
        { throw new InvalidCastException("The requested nullable array element does not match its PostgreSQL codec."); }
        if (format == BlueTuskDataFormat.Text)
        {
            var parsed = BlueTuskArrayTextParser.Parse(reader.ReadRemainingUtf8(), _elementType.Delimiter, 1);
            if (parsed.Lengths.Length == 0) { return []; }
            if (parsed.Lengths.Length != 1 || parsed.LowerBounds[0] != 1)
            { throw new InvalidOperationException("Nullable typed arrays require a single dimension with PostgreSQL lower bound one."); }
            var result = new TElement?[parsed.Lengths[0]];
            var index = 0;
            foreach (var element in parsed.Elements)
            {
                if (element is not null)
                {
                    var bytes = StrictUtf8.GetBytes(element);
                    var elementReader = new BlueTuskReader(bytes);
                    result[index] = codec.ReadTyped(ref elementReader, format, _elementType);
                    EnsureElementConsumed(elementReader.Remaining);
                }
                index++;
            }
            return result;
        }
        if (format != BlueTuskDataFormat.Binary) { throw new ArgumentOutOfRangeException(nameof(format)); }
        var rank = reader.ReadInt32BigEndian();
        var flags = reader.ReadInt32BigEndian();
        var elementId = new BlueTuskTypeId(reader.ReadUInt32BigEndian());
        if (rank is < 0 or > 1 || flags is not (0 or 1) || elementId != _elementType.Id)
        { throw new InvalidOperationException("The nullable typed array header does not match its registered shape and element type."); }
        if (rank == 0) { return []; }
        var length = reader.ReadInt32BigEndian();
        var lowerBound = reader.ReadInt32BigEndian();
        if (lowerBound != 1 || length < 0 || length > reader.Remaining / sizeof(int))
        { throw new InvalidOperationException("The nullable typed array exceeds its encoded shape or byte bounds."); }
        var values = new TElement?[length];
        for (var index = 0; index < length; index++)
        {
            var elementLength = reader.ReadInt32BigEndian();
            if (elementLength == -1)
            {
                if (flags == 0) { throw new InvalidOperationException("A nullable array element requires the PostgreSQL null flag."); }
                continue;
            }
            if (elementLength < 0 || elementLength > reader.Remaining)
            { throw new InvalidOperationException("The nullable array element length exceeds its encoded byte bound."); }
            var elementReader = new BlueTuskReader(reader.ReadBytes(elementLength));
            values[index] = codec.ReadTyped(ref elementReader, format, _elementType);
            EnsureElementConsumed(elementReader.Remaining);
        }
        return values;
    }
}
