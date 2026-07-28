using System.Globalization;

namespace BlueTusk.TypeSystem;

/// <summary>Encodes one of PostgreSQL's symbolic <c>reg*</c> object identifier aliases.</summary>
public sealed class BlueTuskObjectIdentifierCodec<T> :
    BlueTuskCodec<T>,
    IBlueTuskWriteFormatSelector
    where T : struct, IBlueTuskObjectIdentifierValue<T>
{
    public override T ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        BlueTuskObjectIdentifier identifier;
        if (format == BlueTuskDataFormat.Binary)
        {
            if (reader.Remaining != sizeof(uint))
            {
                throw new InvalidOperationException(
                    $"The {type.QualifiedName} binary value must contain exactly four bytes.");
            }

            identifier = new BlueTuskObjectIdentifier(reader.ReadUInt32BigEndian());
        }
        else if (format == BlueTuskDataFormat.Text)
        {
            var text = reader.ReadRemainingUtf8();
            identifier = uint.TryParse(
                text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var oid)
                ? new BlueTuskObjectIdentifier(oid)
                : new BlueTuskObjectIdentifier(text);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        return T.FromIdentifier(identifier);
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        T value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        if (format == BlueTuskDataFormat.Binary)
        {
            if (value.Identifier.Oid is not { } oid)
            {
                throw new InvalidOperationException(
                    $"The symbolic {type.QualifiedName} value '{value.Identifier}' must be sent in text format.");
            }

            writer.WriteUInt32BigEndian(oid);
        }
        else if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value.Identifier.ToString());
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    public BlueTuskDataFormat GetPreferredWriteFormat(
        object value,
        BlueTuskTypeDescriptor type)
    {
        if (value is not T typedValue)
        {
            throw new InvalidCastException(
                $"The {type.QualifiedName} codec requires a {typeof(T).FullName} value.");
        }

        return typedValue.Identifier.IsNumeric
            ? BlueTuskDataFormat.Binary
            : BlueTuskDataFormat.Text;
    }
}
