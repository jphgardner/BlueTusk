using System.Text;
using BlueTusk.TypeSystem;

namespace BlueTusk.Extensions.HStore;

/// <summary>Encodes PostgreSQL hstore values in their native binary and text formats.</summary>
public sealed class BlueTuskHStoreCodec : BlueTuskCodec<BlueTuskHStore>
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static int GetBinarySize(BlueTuskHStore value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var size = sizeof(int);
        foreach (var pair in value)
        {
            size = checked(size + sizeof(int) + StrictUtf8.GetByteCount(pair.Key) + sizeof(int));
            if (pair.Value is not null)
            {
                size = checked(size + StrictUtf8.GetByteCount(pair.Value));
            }
        }

        return size;
    }

    public override BlueTuskHStore ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Text)
        {
            return BlueTuskHStore.Parse(reader.ReadRemainingUtf8());
        }

        if (format != BlueTuskDataFormat.Binary)
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        if (reader.Remaining < sizeof(int))
        {
            throw InvalidBinary(type, "the pair count is missing");
        }

        var count = reader.ReadInt32BigEndian();
        if (count < 0 || count > reader.Remaining / (sizeof(int) * 2))
        {
            throw InvalidBinary(type, $"the pair count {count} cannot fit in the payload");
        }

        var pairs = new KeyValuePair<string, string?>[count];
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var key = ReadString(ref reader, type, allowNull: false)!;
            var value = ReadString(ref reader, type, allowNull: true);
            if (!keys.Add(key))
            {
                throw InvalidBinary(type, $"the key '{key}' is duplicated");
            }

            pairs[index] = new KeyValuePair<string, string?>(key, value);
        }

        if (reader.Remaining != 0)
        {
            throw InvalidBinary(type, $"{reader.Remaining} trailing bytes remain");
        }

        return new BlueTuskHStore(pairs);
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskHStore value,
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

        writer.WriteInt32BigEndian(value.Count);
        foreach (var pair in value)
        {
            WriteString(ref writer, pair.Key);
            if (pair.Value is null)
            {
                writer.WriteInt32BigEndian(-1);
            }
            else
            {
                WriteString(ref writer, pair.Value);
            }
        }
    }

    private static string? ReadString(
        ref BlueTuskReader reader,
        BlueTuskTypeDescriptor type,
        bool allowNull)
    {
        if (reader.Remaining < sizeof(int))
        {
            throw InvalidBinary(type, "a string length is missing");
        }

        var length = reader.ReadInt32BigEndian();
        if (length == -1 && allowNull)
        {
            return null;
        }

        if (length < 0 || length > reader.Remaining)
        {
            throw InvalidBinary(type, $"the string length {length} is invalid");
        }

        try
        {
            return StrictUtf8.GetString(reader.ReadBytes(length));
        }
        catch (DecoderFallbackException exception)
        {
            throw InvalidBinary(type, "a string is not valid UTF-8", exception);
        }
    }

    private static void WriteString(ref BlueTuskWriter writer, string value)
    {
        var length = StrictUtf8.GetByteCount(value);
        writer.WriteInt32BigEndian(length);
        writer.WriteUtf8(value);
    }

    private static InvalidOperationException InvalidBinary(
        BlueTuskTypeDescriptor type,
        string reason,
        Exception? innerException = null) =>
        new(
            $"PostgreSQL {type.QualifiedName} contains an invalid hstore binary value: {reason}.",
            innerException);
}
