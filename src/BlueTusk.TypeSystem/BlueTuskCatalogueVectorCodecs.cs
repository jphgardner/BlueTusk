using System.Globalization;

namespace BlueTusk.TypeSystem;

public sealed class BlueTuskInt16VectorCodec :
    BlueTuskCodec<BlueTuskInt16Vector>,
    IBlueTuskWriteFormatSelector
{
    private const uint ElementTypeOid = 21;

    public override BlueTuskInt16Vector ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) => format switch
        {
            BlueTuskDataFormat.Binary => ReadBinary(ref reader, type),
            BlueTuskDataFormat.Text => ReadText(reader.ReadRemainingUtf8(), type),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskInt16Vector value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (format == BlueTuskDataFormat.Binary)
        {
            BlueTuskCatalogueVectorBinary.WriteHeader(
                ref writer,
                ElementTypeOid,
                value.Count);
            foreach (var item in value)
            {
                writer.WriteInt32BigEndian(sizeof(short));
                writer.WriteInt16BigEndian(item);
            }
        }
        else if (format == BlueTuskDataFormat.Text)
        {
            for (var index = 0; index < value.Count; index++)
            {
                if (index != 0)
                {
                    writer.WriteByte((byte)' ');
                }

                writer.WriteUtf8(value[index].ToString(CultureInfo.InvariantCulture));
            }
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    public BlueTuskDataFormat GetPreferredWriteFormat(
        object value,
        BlueTuskTypeDescriptor type) =>
        GetVector(value, type).Count == 0
            ? BlueTuskDataFormat.Text
            : BlueTuskDataFormat.Binary;

    private static BlueTuskInt16Vector GetVector(
        object value,
        BlueTuskTypeDescriptor type) =>
        value as BlueTuskInt16Vector ??
        throw new InvalidCastException(
            $"The {type.QualifiedName} codec requires a {typeof(BlueTuskInt16Vector).FullName} value.");

    private static BlueTuskInt16Vector ReadBinary(
        ref BlueTuskReader reader,
        BlueTuskTypeDescriptor type)
    {
        var count = BlueTuskCatalogueVectorBinary.ReadHeader(
            ref reader,
            type,
            ElementTypeOid,
            sizeof(short));
        var values = new short[count];
        for (var index = 0; index < values.Length; index++)
        {
            var length = reader.ReadInt32BigEndian();
            if (length != sizeof(short))
            {
                throw BlueTuskCatalogueVectorBinary.InvalidElementLength(
                    type,
                    index,
                    length,
                    sizeof(short));
            }

            values[index] = reader.ReadInt16BigEndian();
        }

        BlueTuskCatalogueVectorBinary.EnsureConsumed(reader.Remaining, type);
        return new BlueTuskInt16Vector(values);
    }

    private static BlueTuskInt16Vector ReadText(
        string text,
        BlueTuskTypeDescriptor type)
    {
        var fields = text.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var values = new short[fields.Length];
        for (var index = 0; index < fields.Length; index++)
        {
            if (!short.TryParse(
                    fields[index],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out values[index]))
            {
                throw new InvalidOperationException(
                    $"The {type.QualifiedName} text value contains invalid Int16 item '{fields[index]}'.");
            }
        }

        return new BlueTuskInt16Vector(values);
    }
}

public sealed class BlueTuskObjectIdentifierVectorCodec :
    BlueTuskCodec<BlueTuskObjectIdentifierVector>,
    IBlueTuskWriteFormatSelector
{
    private const uint ElementTypeOid = 26;

    public override BlueTuskObjectIdentifierVector ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) => format switch
        {
            BlueTuskDataFormat.Binary => ReadBinary(ref reader, type),
            BlueTuskDataFormat.Text => ReadText(reader.ReadRemainingUtf8(), type),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskObjectIdentifierVector value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (format == BlueTuskDataFormat.Binary)
        {
            BlueTuskCatalogueVectorBinary.WriteHeader(
                ref writer,
                ElementTypeOid,
                value.Count);
            foreach (var item in value)
            {
                writer.WriteInt32BigEndian(sizeof(uint));
                writer.WriteUInt32BigEndian(item);
            }
        }
        else if (format == BlueTuskDataFormat.Text)
        {
            for (var index = 0; index < value.Count; index++)
            {
                if (index != 0)
                {
                    writer.WriteByte((byte)' ');
                }

                writer.WriteUtf8(value[index].ToString(CultureInfo.InvariantCulture));
            }
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    public BlueTuskDataFormat GetPreferredWriteFormat(
        object value,
        BlueTuskTypeDescriptor type) =>
        GetVector(value, type).Count == 0
            ? BlueTuskDataFormat.Text
            : BlueTuskDataFormat.Binary;

    private static BlueTuskObjectIdentifierVector GetVector(
        object value,
        BlueTuskTypeDescriptor type) =>
        value as BlueTuskObjectIdentifierVector ??
        throw new InvalidCastException(
            $"The {type.QualifiedName} codec requires a {typeof(BlueTuskObjectIdentifierVector).FullName} value.");

    private static BlueTuskObjectIdentifierVector ReadBinary(
        ref BlueTuskReader reader,
        BlueTuskTypeDescriptor type)
    {
        var count = BlueTuskCatalogueVectorBinary.ReadHeader(
            ref reader,
            type,
            ElementTypeOid,
            sizeof(uint));
        var values = new uint[count];
        for (var index = 0; index < values.Length; index++)
        {
            var length = reader.ReadInt32BigEndian();
            if (length != sizeof(uint))
            {
                throw BlueTuskCatalogueVectorBinary.InvalidElementLength(
                    type,
                    index,
                    length,
                    sizeof(uint));
            }

            values[index] = reader.ReadUInt32BigEndian();
        }

        BlueTuskCatalogueVectorBinary.EnsureConsumed(reader.Remaining, type);
        return new BlueTuskObjectIdentifierVector(values);
    }

    private static BlueTuskObjectIdentifierVector ReadText(
        string text,
        BlueTuskTypeDescriptor type)
    {
        var fields = text.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var values = new uint[fields.Length];
        for (var index = 0; index < fields.Length; index++)
        {
            if (!uint.TryParse(
                    fields[index],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out values[index]))
            {
                throw new InvalidOperationException(
                    $"The {type.QualifiedName} text value contains invalid UInt32 item '{fields[index]}'.");
            }
        }

        return new BlueTuskObjectIdentifierVector(values);
    }
}

internal static class BlueTuskCatalogueVectorBinary
{
    private const int HeaderLength = sizeof(int) * 5;

    public static int ReadHeader(
        ref BlueTuskReader reader,
        BlueTuskTypeDescriptor type,
        uint expectedElementTypeOid,
        int elementWidth)
    {
        if (reader.Remaining < HeaderLength)
        {
            throw new InvalidOperationException(
                $"The {type.QualifiedName} binary value is shorter than its array header.");
        }

        var dimensions = reader.ReadInt32BigEndian();
        var flags = reader.ReadInt32BigEndian();
        var elementTypeOid = reader.ReadUInt32BigEndian();
        var count = reader.ReadInt32BigEndian();
        var lowerBound = reader.ReadInt32BigEndian();
        if (dimensions != 1 ||
            flags != 0 ||
            elementTypeOid != expectedElementTypeOid ||
            count < 0 ||
            lowerBound != 0 ||
            count > reader.Remaining / checked(sizeof(int) + elementWidth))
        {
            throw new InvalidOperationException(
                $"The {type.QualifiedName} binary value must be a one-dimensional, zero-based, null-free vector.");
        }

        return count;
    }

    public static void WriteHeader(
        ref BlueTuskWriter writer,
        uint elementTypeOid,
        int count)
    {
        writer.WriteInt32BigEndian(1);
        writer.WriteInt32BigEndian(0);
        writer.WriteUInt32BigEndian(elementTypeOid);
        writer.WriteInt32BigEndian(count);
        writer.WriteInt32BigEndian(0);
    }

    public static InvalidOperationException InvalidElementLength(
        BlueTuskTypeDescriptor type,
        int index,
        int actual,
        int expected) =>
        new(
            $"The {type.QualifiedName} binary item {index} has length {actual}; {expected} was expected.");

    public static void EnsureConsumed(
        int remaining,
        BlueTuskTypeDescriptor type)
    {
        if (remaining != 0)
        {
            throw new InvalidOperationException(
                $"The {type.QualifiedName} binary value contains trailing bytes.");
        }
    }
}
