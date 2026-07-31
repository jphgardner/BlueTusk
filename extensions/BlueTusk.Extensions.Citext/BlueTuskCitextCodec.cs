using BlueTusk.TypeSystem;

namespace BlueTusk.Extensions.Citext;

/// <summary>Encodes PostgreSQL citext using its UTF-8 text and binary wire representation.</summary>
public sealed class BlueTuskCitextCodec : BlueTuskCodec<BlueTuskCitext>
{
    public override BlueTuskCitext ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return format is BlueTuskDataFormat.Text or BlueTuskDataFormat.Binary
            ? new BlueTuskCitext(reader.ReadRemainingUtf8())
            : throw new ArgumentOutOfRangeException(nameof(format));
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskCitext value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format is not (BlueTuskDataFormat.Text or BlueTuskDataFormat.Binary))
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        writer.WriteUtf8(value.Value);
    }
}
