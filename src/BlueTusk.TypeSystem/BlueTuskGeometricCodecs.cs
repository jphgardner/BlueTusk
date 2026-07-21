namespace BlueTusk.TypeSystem;

public sealed class BlueTuskPointCodec : BlueTuskCodec<BlueTuskPoint>
{
    public override BlueTuskPoint ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) => format switch
        {
            BlueTuskDataFormat.Text => BlueTuskPoint.Parse(reader.ReadRemainingUtf8()),
            BlueTuskDataFormat.Binary when reader.Remaining == 16 => BlueTuskGeometricBinary.ReadPoint(ref reader),
            BlueTuskDataFormat.Binary => throw BlueTuskGeometricBinary.InvalidWidth(type, 16),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskPoint value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type) =>
        BlueTuskGeometricBinary.WriteValue(ref writer, value, format, type, BlueTuskGeometricBinary.WritePoint);
}

public sealed class BlueTuskLineCodec : BlueTuskCodec<BlueTuskLine>
{
    public override BlueTuskLine ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        if (format == BlueTuskDataFormat.Text)
        {
            return BlueTuskLine.Parse(reader.ReadRemainingUtf8());
        }

        BlueTuskGeometricBinary.RequireWidth(ref reader, format, type, 24);
        return new BlueTuskLine(
            reader.ReadDoubleBigEndian(),
            reader.ReadDoubleBigEndian(),
            reader.ReadDoubleBigEndian());
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskLine value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value.ToString());
        }
        else if (format == BlueTuskDataFormat.Binary)
        {
            writer.WriteDoubleBigEndian(value.A);
            writer.WriteDoubleBigEndian(value.B);
            writer.WriteDoubleBigEndian(value.C);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }
}

public sealed class BlueTuskLineSegmentCodec : BlueTuskCodec<BlueTuskLineSegment>
{
    public override BlueTuskLineSegment ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        if (format == BlueTuskDataFormat.Text)
        {
            return BlueTuskLineSegment.Parse(reader.ReadRemainingUtf8());
        }

        BlueTuskGeometricBinary.RequireWidth(ref reader, format, type, 32);
        return new BlueTuskLineSegment(
            BlueTuskGeometricBinary.ReadPoint(ref reader),
            BlueTuskGeometricBinary.ReadPoint(ref reader));
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskLineSegment value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value.ToString());
        }
        else if (format == BlueTuskDataFormat.Binary)
        {
            BlueTuskGeometricBinary.WritePoint(ref writer, value.Start);
            BlueTuskGeometricBinary.WritePoint(ref writer, value.End);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }
}

public sealed class BlueTuskBoxCodec : BlueTuskCodec<BlueTuskBox>
{
    public override BlueTuskBox ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        if (format == BlueTuskDataFormat.Text)
        {
            return BlueTuskBox.Parse(reader.ReadRemainingUtf8());
        }

        BlueTuskGeometricBinary.RequireWidth(ref reader, format, type, 32);
        return new BlueTuskBox(
            BlueTuskGeometricBinary.ReadPoint(ref reader),
            BlueTuskGeometricBinary.ReadPoint(ref reader));
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskBox value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value.ToString());
        }
        else if (format == BlueTuskDataFormat.Binary)
        {
            BlueTuskGeometricBinary.WritePoint(ref writer, value.High);
            BlueTuskGeometricBinary.WritePoint(ref writer, value.Low);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }
}

public sealed class BlueTuskPathCodec : BlueTuskCodec<BlueTuskPath>
{
    public override BlueTuskPath ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Text)
        {
            return BlueTuskPath.Parse(reader.ReadRemainingUtf8());
        }

        if (format != BlueTuskDataFormat.Binary || reader.Remaining < 5)
        {
            throw new InvalidOperationException(
                $"PostgreSQL {type.QualifiedName} binary values require a closed marker, point count, and points.");
        }

        var closed = reader.ReadByte();
        var count = reader.ReadInt32BigEndian();
        if (closed > 1 || count <= 0 || reader.Remaining != (long)count * 16)
        {
            throw new InvalidOperationException($"PostgreSQL {type.QualifiedName} binary value has an invalid layout.");
        }

        return new BlueTuskPath(BlueTuskGeometricBinary.ReadPoints(ref reader, count), closed == 1);
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskPath value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value.ToString());
        }
        else if (format == BlueTuskDataFormat.Binary)
        {
            writer.WriteByte(value.IsClosed ? (byte)1 : (byte)0);
            writer.WriteInt32BigEndian(value.Count);
            BlueTuskGeometricBinary.WritePoints(ref writer, value);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }
}

public sealed class BlueTuskPolygonCodec : BlueTuskCodec<BlueTuskPolygon>
{
    public override BlueTuskPolygon ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Text)
        {
            return BlueTuskPolygon.Parse(reader.ReadRemainingUtf8());
        }

        if (format != BlueTuskDataFormat.Binary || reader.Remaining < 4)
        {
            throw new InvalidOperationException(
                $"PostgreSQL {type.QualifiedName} binary values require a point count and points.");
        }

        var count = reader.ReadInt32BigEndian();
        if (count <= 0 || reader.Remaining != (long)count * 16)
        {
            throw new InvalidOperationException($"PostgreSQL {type.QualifiedName} binary value has an invalid layout.");
        }

        return new BlueTuskPolygon(BlueTuskGeometricBinary.ReadPoints(ref reader, count));
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskPolygon value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value.ToString());
        }
        else if (format == BlueTuskDataFormat.Binary)
        {
            writer.WriteInt32BigEndian(value.Count);
            BlueTuskGeometricBinary.WritePoints(ref writer, value);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }
}

public sealed class BlueTuskCircleCodec : BlueTuskCodec<BlueTuskCircle>
{
    public override BlueTuskCircle ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        if (format == BlueTuskDataFormat.Text)
        {
            return BlueTuskCircle.Parse(reader.ReadRemainingUtf8());
        }

        BlueTuskGeometricBinary.RequireWidth(ref reader, format, type, 24);
        return new BlueTuskCircle(
            BlueTuskGeometricBinary.ReadPoint(ref reader),
            reader.ReadDoubleBigEndian());
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskCircle value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value.ToString());
        }
        else if (format == BlueTuskDataFormat.Binary)
        {
            BlueTuskGeometricBinary.WritePoint(ref writer, value.Center);
            writer.WriteDoubleBigEndian(value.Radius);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }
}

internal static class BlueTuskGeometricBinary
{
    public delegate void BinaryWriter<T>(ref BlueTuskWriter writer, T value);

    public static BlueTuskPoint ReadPoint(ref BlueTuskReader reader) =>
        new(reader.ReadDoubleBigEndian(), reader.ReadDoubleBigEndian());

    public static BlueTuskPoint[] ReadPoints(ref BlueTuskReader reader, int count)
    {
        var points = new BlueTuskPoint[count];
        for (var index = 0; index < points.Length; index++)
        {
            points[index] = ReadPoint(ref reader);
        }

        return points;
    }

    public static void WritePoint(ref BlueTuskWriter writer, BlueTuskPoint value)
    {
        writer.WriteDoubleBigEndian(value.X);
        writer.WriteDoubleBigEndian(value.Y);
    }

    public static void WritePoints(ref BlueTuskWriter writer, IReadOnlyList<BlueTuskPoint> points)
    {
        for (var index = 0; index < points.Count; index++)
        {
            WritePoint(ref writer, points[index]);
        }
    }

    public static void WriteValue<T>(
        ref BlueTuskWriter writer,
        T value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type,
        BinaryWriter<T> binaryWriter)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value?.ToString() ?? string.Empty);
        }
        else if (format == BlueTuskDataFormat.Binary)
        {
            binaryWriter(ref writer, value);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    public static void RequireWidth(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type,
        int width)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format != BlueTuskDataFormat.Binary)
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        if (reader.Remaining != width)
        {
            throw InvalidWidth(type, width);
        }
    }

    public static InvalidOperationException InvalidWidth(BlueTuskTypeDescriptor type, int width)
    {
        ArgumentNullException.ThrowIfNull(type);
        return new InvalidOperationException(
            $"PostgreSQL {type.QualifiedName} binary values must contain exactly {width} bytes.");
    }
}
