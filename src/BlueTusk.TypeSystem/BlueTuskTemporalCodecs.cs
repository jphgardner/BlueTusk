using System.Globalization;

namespace BlueTusk.TypeSystem;

public sealed class BlueTuskDateCodec :
    BlueTuskCodec<DateOnly>,
    IBlueTuskRangeCodecFactory
{
    private static readonly DateOnly Epoch = new(2000, 1, 1);

    IBlueTuskCodec? IBlueTuskRangeCodecFactory.CreateRangeCodec(
        BlueTuskTypeDescriptor subtype,
        IBlueTuskCodec subtypeCodec) =>
        BlueTuskBuiltInRangeCodecFactory<DateOnly>.Create(subtype, subtypeCodec);

    public override DateOnly ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Text)
        {
            return reader.ReadRemainingUtf8() switch
            {
                "infinity" => DateOnly.MaxValue,
                "-infinity" => DateOnly.MinValue,
                var text => DateOnly.Parse(text, CultureInfo.InvariantCulture),
            };
        }

        if (format != BlueTuskDataFormat.Binary || reader.Remaining != sizeof(int))
        {
            throw InvalidBinary(type, sizeof(int));
        }

        return reader.ReadInt32BigEndian() switch
        {
            int.MaxValue => DateOnly.MaxValue,
            int.MinValue => DateOnly.MinValue,
            var days => Epoch.AddDays(days),
        };
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        DateOnly value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value == DateOnly.MaxValue
                ? "infinity"
                : value == DateOnly.MinValue
                    ? "-infinity"
                    : value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        else if (format == BlueTuskDataFormat.Binary)
        {
            writer.WriteInt32BigEndian(value == DateOnly.MaxValue
                ? int.MaxValue
                : value == DateOnly.MinValue
                    ? int.MinValue
                    : value.DayNumber - Epoch.DayNumber);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    private static InvalidOperationException InvalidBinary(BlueTuskTypeDescriptor type, int width) =>
        new($"PostgreSQL {type.QualifiedName} binary values must contain exactly {width} bytes.");
}

public sealed class BlueTuskTimeCodec : BlueTuskCodec<TimeSpan>
{
    private const long MicrosecondsPerSecond = 1_000_000;
    private const long MicrosecondsPerDay = 86_400 * MicrosecondsPerSecond;

    public override TimeSpan ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Text)
        {
            return ParseText(reader.ReadRemainingUtf8());
        }

        if (format != BlueTuskDataFormat.Binary || reader.Remaining != sizeof(long))
        {
            throw InvalidBinary(type, sizeof(long));
        }

        var microseconds = reader.ReadInt64BigEndian();
        if (microseconds is < 0 or > MicrosecondsPerDay)
        {
            throw new InvalidOperationException("PostgreSQL time is outside the range 00:00:00 through 24:00:00.");
        }

        return TimeSpan.FromTicks(checked(microseconds * TimeSpan.TicksPerMicrosecond));
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        TimeSpan value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (value < TimeSpan.Zero || value > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "PostgreSQL time must be between 00:00:00 and 24:00:00.");
        }

        var microseconds = value.Ticks / TimeSpan.TicksPerMicrosecond;
        if (format == BlueTuskDataFormat.Binary)
        {
            writer.WriteInt64BigEndian(microseconds);
        }
        else if (format == BlueTuskDataFormat.Text)
        {
            var hours = microseconds / (3_600 * MicrosecondsPerSecond);
            var minutes = (microseconds / (60 * MicrosecondsPerSecond)) % 60;
            var seconds = (microseconds / MicrosecondsPerSecond) % 60;
            var fraction = microseconds % MicrosecondsPerSecond;
            writer.WriteUtf8(FormattableString.Invariant($"{hours:00}:{minutes:00}:{seconds:00}.{fraction:000000}"));
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    private static TimeSpan ParseText(string text)
    {
        var segments = text.Split(':');
        if (segments.Length != 3 ||
            !int.TryParse(segments[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(segments[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            !decimal.TryParse(segments[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) ||
            hours is < 0 or > 24 || minutes is < 0 or > 59 || seconds is < 0 or >= 60 ||
            (hours == 24 && (minutes != 0 || seconds != 0)))
        {
            throw new FormatException($"PostgreSQL time value '{text}' is invalid.");
        }

        var ticks = checked(
            (hours * TimeSpan.TicksPerHour) +
            (minutes * TimeSpan.TicksPerMinute) +
            decimal.ToInt64(decimal.Truncate(seconds * TimeSpan.TicksPerSecond)));
        return TimeSpan.FromTicks(ticks);
    }

    private static InvalidOperationException InvalidBinary(BlueTuskTypeDescriptor type, int width) =>
        new($"PostgreSQL {type.QualifiedName} binary values must contain exactly {width} bytes.");
}

public sealed class BlueTuskTimestampCodec :
    BlueTuskCodec<DateTime>,
    IBlueTuskRangeCodecFactory
{
    private static readonly DateTime Epoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
    private static readonly long MinimumMicroseconds =
        (DateTime.MinValue.Ticks - Epoch.Ticks) / TimeSpan.TicksPerMicrosecond;
    private static readonly long MaximumMicroseconds =
        (DateTime.MaxValue.Ticks - Epoch.Ticks) / TimeSpan.TicksPerMicrosecond;

    IBlueTuskCodec? IBlueTuskRangeCodecFactory.CreateRangeCodec(
        BlueTuskTypeDescriptor subtype,
        IBlueTuskCodec subtypeCodec) =>
        BlueTuskBuiltInRangeCodecFactory<DateTime>.Create(subtype, subtypeCodec);

    public override DateTime ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Text)
        {
            return reader.ReadRemainingUtf8() switch
            {
                "infinity" => DateTime.MaxValue,
                "-infinity" => DateTime.MinValue,
                var text => DateTime.SpecifyKind(
                    DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces),
                    DateTimeKind.Unspecified),
            };
        }

        return ReadBinary(ref reader, type);
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        DateTime value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value == DateTime.MaxValue
                ? "infinity"
                : value == DateTime.MinValue
                    ? "-infinity"
                    : value.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture));
        }
        else if (format == BlueTuskDataFormat.Binary)
        {
            WriteBinary(ref writer, value);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    internal static DateTime ReadBinary(ref BlueTuskReader reader, BlueTuskTypeDescriptor type)
    {
        if (reader.Remaining != sizeof(long))
        {
            throw new InvalidOperationException(
                $"PostgreSQL {type.QualifiedName} binary values must contain exactly {sizeof(long)} bytes.");
        }

        var microseconds = reader.ReadInt64BigEndian();
        if (microseconds is long.MaxValue)
        {
            return DateTime.MaxValue;
        }

        if (microseconds is long.MinValue)
        {
            return DateTime.MinValue;
        }

        if (microseconds < MinimumMicroseconds || microseconds > MaximumMicroseconds)
        {
            throw new InvalidOperationException(
                $"PostgreSQL {type.QualifiedName} binary value is outside the supported DateTime range.");
        }

        return Epoch.AddTicks(microseconds * TimeSpan.TicksPerMicrosecond);
    }

    internal static void WriteBinary(ref BlueTuskWriter writer, DateTime value) =>
        writer.WriteInt64BigEndian(value == DateTime.MaxValue
            ? long.MaxValue
            : value == DateTime.MinValue
                ? long.MinValue
                : (value.Ticks - Epoch.Ticks) / TimeSpan.TicksPerMicrosecond);
}

public sealed class BlueTuskTimestampWithTimeZoneCodec :
    BlueTuskCodec<DateTimeOffset>,
    IBlueTuskRangeCodecFactory
{
    IBlueTuskCodec? IBlueTuskRangeCodecFactory.CreateRangeCodec(
        BlueTuskTypeDescriptor subtype,
        IBlueTuskCodec subtypeCodec) =>
        BlueTuskBuiltInRangeCodecFactory<DateTimeOffset>.Create(subtype, subtypeCodec);

    public override DateTimeOffset ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Text)
        {
            return reader.ReadRemainingUtf8() switch
            {
                "infinity" => DateTimeOffset.MaxValue,
                "-infinity" => DateTimeOffset.MinValue,
                var text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces),
            };
        }

        var timestamp = BlueTuskTimestampCodec.ReadBinary(ref reader, type);
        return timestamp == DateTime.MaxValue
            ? DateTimeOffset.MaxValue
            : timestamp == DateTime.MinValue
                ? DateTimeOffset.MinValue
                : new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc));
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        DateTimeOffset value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value == DateTimeOffset.MaxValue
                ? "infinity"
                : value == DateTimeOffset.MinValue
                    ? "-infinity"
                    : value.ToString("yyyy-MM-dd HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture));
        }
        else if (format == BlueTuskDataFormat.Binary)
        {
            BlueTuskTimestampCodec.WriteBinary(
                ref writer,
                value == DateTimeOffset.MaxValue
                    ? DateTime.MaxValue
                    : value == DateTimeOffset.MinValue
                        ? DateTime.MinValue
                        : value.UtcDateTime);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }
}
