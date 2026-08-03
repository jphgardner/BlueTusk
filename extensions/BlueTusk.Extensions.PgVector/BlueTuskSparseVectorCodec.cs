using BlueTusk.TypeSystem;

namespace BlueTusk.Extensions.PgVector;

/// <summary>Encodes pgvector sparse vectors in their native binary and text formats.</summary>
public sealed class BlueTuskSparseVectorCodec : BlueTuskCodec<BlueTuskSparseVector>
{
    private const int HeaderSize = sizeof(int) * 3;

    public static int GetBinarySize(BlueTuskSparseVector value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return checked(HeaderSize + (value.Count * (sizeof(int) + sizeof(float))));
    }

    public override BlueTuskSparseVector ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Text)
        {
            return BlueTuskSparseVector.Parse(reader.ReadRemainingUtf8());
        }

        if (format != BlueTuskDataFormat.Binary)
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        if (reader.Remaining < HeaderSize)
        {
            throw InvalidBinary(type, "the twelve-byte header is incomplete");
        }

        var dimensions = reader.ReadInt32BigEndian();
        var count = reader.ReadInt32BigEndian();
        var unused = reader.ReadInt32BigEndian();
        if (dimensions is < 1 or > BlueTuskSparseVector.MaxDimensions)
        {
            throw InvalidBinary(type, $"the dimension count {dimensions} is outside the supported range");
        }

        if (count < 0 || count > BlueTuskSparseVector.MaxNonZeroElements || count > dimensions)
        {
            throw InvalidBinary(type, $"the non-zero count {count} is outside the supported range");
        }

        if (unused != 0)
        {
            throw InvalidBinary(type, $"the reserved header value is {unused} instead of zero");
        }

        var expectedPayloadSize = count * (sizeof(int) + sizeof(float));
        if (reader.Remaining != expectedPayloadSize)
        {
            throw InvalidBinary(
                type,
                $"the payload contains {reader.Remaining} bytes instead of {expectedPayloadSize}");
        }

        var indices = new int[count];
        for (var index = 0; index < count; index++)
        {
            indices[index] = reader.ReadInt32BigEndian();
            if (indices[index] < 0 || indices[index] >= dimensions ||
                index > 0 && indices[index] <= indices[index - 1])
            {
                throw InvalidBinary(type, $"index {index} is out of bounds, duplicated, or unordered");
            }
        }

        var elements = new BlueTuskSparseVectorElement[count];
        for (var index = 0; index < count; index++)
        {
            var value = reader.ReadSingleBigEndian();
            if (!float.IsFinite(value) || value == 0)
            {
                throw InvalidBinary(type, $"element {index} is zero or non-finite");
            }

            elements[index] = new BlueTuskSparseVectorElement(indices[index], value);
        }

        return new BlueTuskSparseVector(dimensions, elements);
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskSparseVector value,
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

        writer.WriteInt32BigEndian(value.Dimensions);
        writer.WriteInt32BigEndian(value.Count);
        writer.WriteInt32BigEndian(0);
        foreach (var element in value.AsSpan())
        {
            writer.WriteInt32BigEndian(element.Index);
        }

        foreach (var element in value.AsSpan())
        {
            writer.WriteSingleBigEndian(element.Value);
        }
    }

    private static InvalidOperationException InvalidBinary(
        BlueTuskTypeDescriptor type,
        string reason) =>
        new($"PostgreSQL {type.QualifiedName} contains an invalid pgvector sparsevec binary value: {reason}.");
}
