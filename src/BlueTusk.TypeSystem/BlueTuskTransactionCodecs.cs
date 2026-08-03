using System.Globalization;

namespace BlueTusk.TypeSystem;

public sealed class BlueTuskTransactionIdCodec : BlueTuskCodec<BlueTuskTransactionId>
{
    public override BlueTuskTransactionId ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) =>
        new(format switch
        {
            BlueTuskDataFormat.Binary => reader.ReadUInt32BigEndian(),
            BlueTuskDataFormat.Text => ParseUInt32(reader.ReadRemainingUtf8(), type),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        });

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskTransactionId value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        if (format == BlueTuskDataFormat.Binary)
        {
            writer.WriteUInt32BigEndian(value.Value);
        }
        else if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value.Value.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    internal static uint ParseUInt32(string text, BlueTuskTypeDescriptor type) =>
        uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidOperationException(
                $"The {type.QualifiedName} text value '{text}' is not a valid UInt32.");
}

public sealed class BlueTuskCommandIdCodec : BlueTuskCodec<BlueTuskCommandId>
{
    public override BlueTuskCommandId ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) =>
        new(format switch
        {
            BlueTuskDataFormat.Binary => reader.ReadUInt32BigEndian(),
            BlueTuskDataFormat.Text => BlueTuskTransactionIdCodec.ParseUInt32(
                reader.ReadRemainingUtf8(),
                type),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        });

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskCommandId value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        if (format == BlueTuskDataFormat.Binary)
        {
            writer.WriteUInt32BigEndian(value.Value);
        }
        else if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value.Value.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }
}

public sealed class BlueTuskFullTransactionIdCodec : BlueTuskCodec<BlueTuskFullTransactionId>
{
    public override BlueTuskFullTransactionId ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) =>
        new(format switch
        {
            BlueTuskDataFormat.Binary => reader.ReadUInt64BigEndian(),
            BlueTuskDataFormat.Text => ParseUInt64(reader.ReadRemainingUtf8(), type),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        });

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskFullTransactionId value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        if (format == BlueTuskDataFormat.Binary)
        {
            writer.WriteUInt64BigEndian(value.Value);
        }
        else if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value.Value.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    internal static ulong ParseUInt64(string text, BlueTuskTypeDescriptor type) =>
        ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidOperationException(
                $"The {type.QualifiedName} text value '{text}' is not a valid UInt64.");
}

/// <summary>Encodes PostgreSQL 19's unsigned 64-bit <c>oid8</c> values.</summary>
public sealed class BlueTuskObjectIdentifier64Codec : BlueTuskCodec<BlueTuskObjectIdentifier64>
{
    public override BlueTuskObjectIdentifier64 ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) =>
        new(format switch
        {
            BlueTuskDataFormat.Binary => reader.ReadUInt64BigEndian(),
            BlueTuskDataFormat.Text => BlueTuskFullTransactionIdCodec.ParseUInt64(
                reader.ReadRemainingUtf8(),
                type),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        });

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskObjectIdentifier64 value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        if (format == BlueTuskDataFormat.Binary)
        {
            writer.WriteUInt64BigEndian(value.Value);
        }
        else if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value.Value.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }
}

public sealed class BlueTuskTransactionSnapshotCodec :
    BlueTuskCodec<BlueTuskTransactionSnapshot>
{
    public override BlueTuskTransactionSnapshot ReadTyped(
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
        BlueTuskTransactionSnapshot value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (format == BlueTuskDataFormat.Binary)
        {
            writer.WriteInt32BigEndian(value.InProgressTransactionIds.Count);
            writer.WriteUInt64BigEndian(value.MinimumTransactionId);
            writer.WriteUInt64BigEndian(value.MaximumTransactionId);
            foreach (var transactionId in value.InProgressTransactionIds)
            {
                writer.WriteUInt64BigEndian(transactionId);
            }
        }
        else if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value.MinimumTransactionId.ToString(CultureInfo.InvariantCulture));
            writer.WriteByte((byte)':');
            writer.WriteUtf8(value.MaximumTransactionId.ToString(CultureInfo.InvariantCulture));
            writer.WriteByte((byte)':');
            for (var index = 0; index < value.InProgressTransactionIds.Count; index++)
            {
                if (index != 0)
                {
                    writer.WriteByte((byte)',');
                }

                writer.WriteUtf8(
                    value.InProgressTransactionIds[index].ToString(CultureInfo.InvariantCulture));
            }
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    private static BlueTuskTransactionSnapshot ReadBinary(
        ref BlueTuskReader reader,
        BlueTuskTypeDescriptor type)
    {
        var count = reader.ReadInt32BigEndian();
        if (count < 0 ||
            reader.Remaining < sizeof(ulong) * 2 ||
            count > (reader.Remaining - sizeof(ulong) * 2) / sizeof(ulong))
        {
            throw new InvalidOperationException(
                $"The {type.QualifiedName} binary snapshot has invalid in-progress count {count}.");
        }

        var minimum = reader.ReadUInt64BigEndian();
        var maximum = reader.ReadUInt64BigEndian();
        var inProgress = new ulong[count];
        for (var index = 0; index < inProgress.Length; index++)
        {
            inProgress[index] = reader.ReadUInt64BigEndian();
        }

        if (reader.Remaining != 0)
        {
            throw new InvalidOperationException(
                $"The {type.QualifiedName} binary snapshot contains trailing bytes.");
        }

        return CreateSnapshot(minimum, maximum, inProgress, type);
    }

    private static BlueTuskTransactionSnapshot ReadText(
        string text,
        BlueTuskTypeDescriptor type)
    {
        var fields = text.Split(':');
        if (fields.Length != 3)
        {
            throw new InvalidOperationException(
                $"The {type.QualifiedName} text snapshot must contain xmin:xmax:xip_list.");
        }

        var minimum = BlueTuskFullTransactionIdCodec.ParseUInt64(fields[0], type);
        var maximum = BlueTuskFullTransactionIdCodec.ParseUInt64(fields[1], type);
        var inProgress = fields[2].Length == 0
            ? []
            : fields[2]
                .Split(',')
                .Select(value => BlueTuskFullTransactionIdCodec.ParseUInt64(value, type))
                .ToArray();
        return CreateSnapshot(minimum, maximum, inProgress, type);
    }

    private static BlueTuskTransactionSnapshot CreateSnapshot(
        ulong minimum,
        ulong maximum,
        ulong[] inProgress,
        BlueTuskTypeDescriptor type)
    {
        try
        {
            return new BlueTuskTransactionSnapshot(minimum, maximum, inProgress);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                $"The {type.QualifiedName} snapshot is not valid.",
                exception);
        }
    }
}
