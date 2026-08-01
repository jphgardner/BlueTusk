using BlueTusk.TypeSystem;

namespace BlueTusk.Extensions.PostGIS;

/// <summary>Encodes PostGIS geometry as EWKB binary or WKT/EWKT text.</summary>
public sealed class BlueTuskGeometryCodec :
    BlueTuskCodec<BlueTuskGeometry>,
    IBlueTuskWriteFormatSelector
{
    public override BlueTuskGeometry ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return format switch
        {
            BlueTuskDataFormat.Binary => ReadBinary(ref reader, type),
            BlueTuskDataFormat.Text => BlueTuskGeometry.FromText(reader.ReadRemainingUtf8()),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskGeometry value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Binary)
        {
            writer.WriteBytes(value.GetWellKnownBinary());
        }
        else if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value.GetTextOrHex());
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
        ArgumentNullException.ThrowIfNull(type);
        return value is BlueTuskGeometry geometry && geometry.HasWellKnownBinary
            ? BlueTuskDataFormat.Binary
            : BlueTuskDataFormat.Text;
    }

    private static BlueTuskGeometry ReadBinary(
        ref BlueTuskReader reader,
        BlueTuskTypeDescriptor type)
    {
        try
        {
            return BlueTuskGeometry.FromWellKnownBinary(reader.ReadRemainingBytes());
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                $"PostgreSQL {type.QualifiedName} contains invalid PostGIS EWKB.",
                exception);
        }
    }
}

/// <summary>Encodes PostGIS geography as EWKB binary or WKT/EWKT text.</summary>
public sealed class BlueTuskGeographyCodec :
    BlueTuskCodec<BlueTuskGeography>,
    IBlueTuskWriteFormatSelector
{
    public override BlueTuskGeography ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return format switch
        {
            BlueTuskDataFormat.Binary => ReadBinary(ref reader, type),
            BlueTuskDataFormat.Text => BlueTuskGeography.FromText(reader.ReadRemainingUtf8()),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskGeography value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Binary)
        {
            writer.WriteBytes(value.GetWellKnownBinary());
        }
        else if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value.GetTextOrHex());
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
        ArgumentNullException.ThrowIfNull(type);
        return value is BlueTuskGeography geography && geography.HasWellKnownBinary
            ? BlueTuskDataFormat.Binary
            : BlueTuskDataFormat.Text;
    }

    private static BlueTuskGeography ReadBinary(
        ref BlueTuskReader reader,
        BlueTuskTypeDescriptor type)
    {
        try
        {
            return BlueTuskGeography.FromWellKnownBinary(reader.ReadRemainingBytes());
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                $"PostgreSQL {type.QualifiedName} contains invalid PostGIS EWKB.",
                exception);
        }
    }
}
