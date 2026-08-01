using BlueTusk.TypeSystem;

namespace BlueTusk.Extensions.PgVector;

/// <summary>Encodes pgvector half vectors in their native binary and text formats.</summary>
public sealed class BlueTuskHalfVectorCodec : BlueTuskCodec<BlueTuskHalfVector>
{
    private const int HeaderSize = sizeof(short) * 2;

    public static int GetBinarySize(BlueTuskHalfVector value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return checked(HeaderSize + (value.Count * sizeof(ushort)));
    }

    public override BlueTuskHalfVector ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Text)
        {
            return BlueTuskHalfVector.Parse(reader.ReadRemainingUtf8());
        }

        if (format != BlueTuskDataFormat.Binary)
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        if (reader.Remaining < HeaderSize)
        {
            throw InvalidBinary(type, "the four-byte header is incomplete");
        }

        var dimensions = reader.ReadInt16BigEndian();
        var unused = reader.ReadInt16BigEndian();
        if (dimensions is < 1 or > BlueTuskHalfVector.MaxDimensions)
        {
            throw InvalidBinary(type, $"the dimension count {dimensions} is outside the supported range");
        }

        if (unused != 0)
        {
            throw InvalidBinary(type, $"the reserved header value is {unused} instead of zero");
        }

        var expectedPayloadSize = dimensions * sizeof(ushort);
        if (reader.Remaining != expectedPayloadSize)
        {
            throw InvalidBinary(
                type,
                $"the payload contains {reader.Remaining} bytes instead of {expectedPayloadSize}");
        }

        var values = new Half[dimensions];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = BitConverter.UInt16BitsToHalf(reader.ReadUInt16BigEndian());
            if (!Half.IsFinite(values[index]))
            {
                throw InvalidBinary(type, $"element {index} is not finite");
            }
        }

        return new BlueTuskHalfVector(values);
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskHalfVector value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(value);
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

        writer.WriteInt16BigEndian(checked((short)value.Count));
        writer.WriteInt16BigEndian(0);
        foreach (var element in value.AsSpan())
        {
            writer.WriteUInt16BigEndian(BitConverter.HalfToUInt16Bits(element));
        }
    }

    private static InvalidOperationException InvalidBinary(
        BlueTuskTypeDescriptor type,
        string reason) =>
        new($"PostgreSQL {type.QualifiedName} contains an invalid pgvector halfvec binary value: {reason}.");
}
