using System.Globalization;

namespace BlueTusk.TypeSystem;

/// <summary>Encodes and decodes PostgreSQL <c>int4</c> in text and binary formats.</summary>
public sealed class BlueTuskInt32Codec : IBlueTuskCodec<int>
{
    public Type ClrType => typeof(int);

    public int ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return format switch
        {
            BlueTuskDataFormat.Binary when reader.Remaining == sizeof(int) => reader.ReadInt32BigEndian(),
            BlueTuskDataFormat.Binary => throw new InvalidOperationException(
                $"PostgreSQL {type.QualifiedName} binary values must contain exactly four bytes."),
            BlueTuskDataFormat.Text => int.Parse(
                reader.ReadRemainingUtf8(),
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
    }

    public void WriteTyped(
        ref BlueTuskWriter writer,
        int value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        switch (format)
        {
            case BlueTuskDataFormat.Binary:
                writer.WriteInt32BigEndian(value);
                break;
            case BlueTuskDataFormat.Text:
                writer.WriteUtf8(value.ToString(CultureInfo.InvariantCulture));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    object IBlueTuskCodec.Read(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) => ReadTyped(ref reader, format, type);

    void IBlueTuskCodec.Write(
        ref BlueTuskWriter writer,
        object? value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        if (value is not int typedValue)
        {
            throw new InvalidCastException($"The {type.QualifiedName} codec requires a System.Int32 value.");
        }

        WriteTyped(ref writer, typedValue, format, type);
    }
}

