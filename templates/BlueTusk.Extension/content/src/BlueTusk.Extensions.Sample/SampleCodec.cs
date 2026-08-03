using System.Text;
using BlueTusk.TypeSystem;

namespace BlueTusk.Extensions.Sample;

/// <summary>Encodes the extension value as PostgreSQL UTF-8 text in either wire format.</summary>
public sealed class SampleCodec : BlueTuskCodec<SampleValue>
{
    public override SampleValue ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) =>
        new(Encoding.UTF8.GetString(reader.ReadBytes(reader.Remaining)));

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        SampleValue value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteUtf8(value.Value);
    }
}
