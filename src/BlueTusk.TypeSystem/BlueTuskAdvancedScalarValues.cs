using System.Globalization;
using System.Text;

namespace BlueTusk.TypeSystem;

public readonly record struct BlueTuskTimeWithTimeZone
{
    private static readonly TimeSpan MaximumTime = TimeSpan.FromDays(1);
    private static readonly TimeSpan MaximumOffset = TimeSpan.FromHours(16);

    public BlueTuskTimeWithTimeZone(TimeSpan timeOfDay, TimeSpan utcOffset)
    {
        if (timeOfDay < TimeSpan.Zero || timeOfDay > MaximumTime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeOfDay),
                "PostgreSQL time with time zone must be between 00:00:00 and 24:00:00.");
        }

        if (utcOffset <= -MaximumOffset || utcOffset >= MaximumOffset ||
            utcOffset.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(utcOffset),
                "PostgreSQL time-zone offsets must be an integral number of seconds below 16 hours.");
        }

        TimeOfDay = timeOfDay;
        UtcOffset = utcOffset;
    }

    public TimeSpan TimeOfDay { get; }

    public TimeSpan UtcOffset { get; }

    public static BlueTuskTimeWithTimeZone Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = value.AsSpan().Trim();
        var offsetIndex = -1;
        for (var index = 1; index < text.Length; index++)
        {
            if (text[index] is '+' or '-')
            {
                offsetIndex = index;
            }
        }

        if (offsetIndex < 0)
        {
            throw new FormatException("PostgreSQL time with time zone requires a numeric UTC offset.");
        }

        return new BlueTuskTimeWithTimeZone(
            ParseClock(text[..offsetIndex]),
            ParseOffset(text[offsetIndex..]));
    }

    public override string ToString() => string.Concat(FormatClock(TimeOfDay), FormatOffset(UtcOffset));

    internal static TimeSpan ParseClock(ReadOnlySpan<char> value)
    {
        var sign = 1;
        if (!value.IsEmpty && value[0] is '+' or '-')
        {
            sign = value[0] == '-' ? -1 : 1;
            value = value[1..];
        }

        var components = value.ToString().Split(':');
        if (components.Length != 3 ||
            !long.TryParse(components[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(components[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            !decimal.TryParse(components[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) ||
            minutes is < 0 or > 59 ||
            seconds is < 0 or >= 60)
        {
            throw new FormatException("The PostgreSQL time value is invalid.");
        }

        var microseconds = checked(
            (hours * 3_600_000_000L) +
            (minutes * 60_000_000L) +
            decimal.ToInt64(seconds * 1_000_000m));
        return TimeSpan.FromTicks(checked(sign * microseconds * TimeSpan.TicksPerMicrosecond));
    }

    internal static string FormatClock(TimeSpan value)
    {
        var microseconds = value.Ticks / TimeSpan.TicksPerMicrosecond;
        var negative = microseconds < 0;
        var magnitude = Math.Abs(microseconds);
        var hours = magnitude / 3_600_000_000L;
        var minutes = (magnitude / 60_000_000L) % 60;
        var seconds = (magnitude / 1_000_000L) % 60;
        var fraction = magnitude % 1_000_000L;
        var result = string.Create(
            CultureInfo.InvariantCulture,
            $"{(negative ? "-" : string.Empty)}{hours:00}:{minutes:00}:{seconds:00}");
        return fraction == 0
            ? result
            : string.Create(CultureInfo.InvariantCulture, $"{result}.{fraction:000000}").TrimEnd('0');
    }

    private static TimeSpan ParseOffset(ReadOnlySpan<char> value)
    {
        var negative = value[0] == '-';
        var components = value[1..].ToString().Split(':');
        var minutes = 0;
        var seconds = 0;
        if (components.Length is < 1 or > 3 ||
            !int.TryParse(components[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            (components.Length > 1 &&
             !int.TryParse(components[1], NumberStyles.None, CultureInfo.InvariantCulture, out minutes)) ||
            (components.Length > 2 &&
             !int.TryParse(components[2], NumberStyles.None, CultureInfo.InvariantCulture, out seconds)) ||
            hours > 15 || minutes is < 0 or > 59 || seconds is < 0 or > 59)
        {
            throw new FormatException("The PostgreSQL time-zone offset is invalid.");
        }

        var offset = new TimeSpan(hours, minutes, seconds);
        return negative ? -offset : offset;
    }

    private static string FormatOffset(TimeSpan value)
    {
        var totalSeconds = checked((int)Math.Abs(value.TotalSeconds));
        var hours = totalSeconds / 3_600;
        var minutes = (totalSeconds / 60) % 60;
        var seconds = totalSeconds % 60;
        return seconds == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{(value < TimeSpan.Zero ? '-' : '+')}{hours:00}:{minutes:00}")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{(value < TimeSpan.Zero ? '-' : '+')}{hours:00}:{minutes:00}:{seconds:00}");
    }
}

public enum BlueTuskIntervalKind
{
    Finite,
    PositiveInfinity,
    NegativeInfinity,
}

public readonly record struct BlueTuskInterval
{
    public BlueTuskInterval(int months, int days, long microseconds)
    {
        Kind = BlueTuskIntervalKind.Finite;
        Months = months;
        Days = days;
        Microseconds = microseconds;
    }

    private BlueTuskInterval(BlueTuskIntervalKind kind)
    {
        if (kind == BlueTuskIntervalKind.Finite)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
        Months = 0;
        Days = 0;
        Microseconds = 0;
    }

    public BlueTuskIntervalKind Kind { get; }

    public int Months { get; }

    public int Days { get; }

    public long Microseconds { get; }

    public bool IsFinite => Kind == BlueTuskIntervalKind.Finite;

    public static BlueTuskInterval PositiveInfinity { get; } = new(BlueTuskIntervalKind.PositiveInfinity);

    public static BlueTuskInterval NegativeInfinity { get; } = new(BlueTuskIntervalKind.NegativeInfinity);

    public static BlueTuskInterval Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = value.Trim();
        if (string.Equals(text, "infinity", StringComparison.OrdinalIgnoreCase))
        {
            return PositiveInfinity;
        }

        if (string.Equals(text, "-infinity", StringComparison.OrdinalIgnoreCase))
        {
            return NegativeInfinity;
        }

        return text.StartsWith('P')
            ? ParseIso(text.AsSpan(1))
            : text.StartsWith('@') || text.Contains("year", StringComparison.Ordinal) ||
              text.Contains("mon", StringComparison.Ordinal) || text.Contains("day", StringComparison.Ordinal) ||
              text.Contains("hour", StringComparison.Ordinal) || text.Contains("min", StringComparison.Ordinal) ||
              text.Contains("sec", StringComparison.Ordinal)
                ? ParsePostgres(text)
                : ParseSqlStandard(text);
    }

    public override string ToString()
    {
        if (!IsFinite)
        {
            return Kind == BlueTuskIntervalKind.PositiveInfinity ? "infinity" : "-infinity";
        }

        if (Months == 0 && Days == 0 && Microseconds == 0)
        {
            return "PT0S";
        }

        var result = new StringBuilder("P");
        if (Months != 0)
        {
            result.Append(Months.ToString(CultureInfo.InvariantCulture)).Append('M');
        }

        if (Days != 0)
        {
            result.Append(Days.ToString(CultureInfo.InvariantCulture)).Append('D');
        }

        if (Microseconds != 0)
        {
            result.Append('T').Append(FormatSeconds(Microseconds)).Append('S');
        }

        return result.ToString();
    }

    private static BlueTuskInterval ParseIso(ReadOnlySpan<char> value)
    {
        var months = 0;
        var days = 0;
        long microseconds = 0;
        var timePart = false;
        var start = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == 'T')
            {
                timePart = true;
                start = index + 1;
                continue;
            }

            if (value[index] is not ('Y' or 'M' or 'W' or 'D' or 'H' or 'S'))
            {
                continue;
            }

            var number = value[start..index];
            if (number.IsEmpty)
            {
                throw new FormatException("The ISO 8601 interval contains an empty component.");
            }

            switch (value[index])
            {
                case 'Y':
                    months = checked(months + (ParseInt32(number) * 12));
                    break;
                case 'M' when !timePart:
                    months = checked(months + ParseInt32(number));
                    break;
                case 'W':
                    days = checked(days + (ParseInt32(number) * 7));
                    break;
                case 'D':
                    days = checked(days + ParseInt32(number));
                    break;
                case 'H':
                    microseconds = checked(microseconds + (ParseInt64(number) * 3_600_000_000L));
                    break;
                case 'M':
                    microseconds = checked(microseconds + (ParseInt64(number) * 60_000_000L));
                    break;
                case 'S':
                    microseconds = checked(microseconds + ParseSeconds(number));
                    break;
            }

            start = index + 1;
        }

        if (start != value.Length)
        {
            throw new FormatException("The ISO 8601 interval ends with an incomplete component.");
        }

        return new BlueTuskInterval(months, days, microseconds);
    }

    private static BlueTuskInterval ParsePostgres(string value)
    {
        var text = value.AsSpan().Trim();
        if (!text.IsEmpty && text[0] == '@')
        {
            text = text[1..].Trim();
        }

        var negate = text.EndsWith("ago", StringComparison.OrdinalIgnoreCase);
        if (negate)
        {
            text = text[..^3].Trim();
        }

        var tokens = text.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var months = 0;
        var days = 0;
        long microseconds = 0;
        for (var index = 0; index < tokens.Length; index++)
        {
            if (tokens[index].Contains(':', StringComparison.Ordinal))
            {
                microseconds = checked(microseconds +
                    (BlueTuskTimeWithTimeZone.ParseClock(tokens[index]).Ticks / TimeSpan.TicksPerMicrosecond));
                continue;
            }

            if (index + 1 >= tokens.Length)
            {
                throw new FormatException("The PostgreSQL interval contains an incomplete component.");
            }

            var number = tokens[index];
            var unit = tokens[++index];
            if (unit.StartsWith("year", StringComparison.Ordinal))
            {
                months = checked(months + (int.Parse(number, CultureInfo.InvariantCulture) * 12));
            }
            else if (unit.StartsWith("mon", StringComparison.Ordinal))
            {
                months = checked(months + int.Parse(number, CultureInfo.InvariantCulture));
            }
            else if (unit.StartsWith("day", StringComparison.Ordinal))
            {
                days = checked(days + int.Parse(number, CultureInfo.InvariantCulture));
            }
            else if (unit.StartsWith("hour", StringComparison.Ordinal))
            {
                microseconds = checked(microseconds +
                    (long.Parse(number, CultureInfo.InvariantCulture) * 3_600_000_000L));
            }
            else if (unit.StartsWith("min", StringComparison.Ordinal))
            {
                microseconds = checked(microseconds +
                    (long.Parse(number, CultureInfo.InvariantCulture) * 60_000_000L));
            }
            else if (unit.StartsWith("sec", StringComparison.Ordinal))
            {
                microseconds = checked(microseconds + ParseSeconds(number));
            }
            else
            {
                throw new FormatException($"Unknown PostgreSQL interval unit '{unit}'.");
            }
        }

        return negate
            ? new BlueTuskInterval(checked(-months), checked(-days), checked(-microseconds))
            : new BlueTuskInterval(months, days, microseconds);
    }

    private static BlueTuskInterval ParseSqlStandard(string value)
    {
        var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var months = 0;
        var days = 0;
        long microseconds = 0;
        foreach (var token in tokens)
        {
            if (token.Contains(':', StringComparison.Ordinal))
            {
                microseconds = checked(microseconds +
                    (BlueTuskTimeWithTimeZone.ParseClock(token).Ticks / TimeSpan.TicksPerMicrosecond));
                continue;
            }

            var separator = token.IndexOf('-', 1);
            if (separator >= 0)
            {
                var sign = token[0] == '-' ? -1 : 1;
                var start = token[0] is '+' or '-' ? 1 : 0;
                var years = int.Parse(token.AsSpan(start, separator - start), CultureInfo.InvariantCulture);
                var remainingMonths = int.Parse(token.AsSpan(separator + 1), CultureInfo.InvariantCulture);
                months = checked(months + (sign * checked((years * 12) + remainingMonths)));
            }
            else
            {
                days = checked(days + int.Parse(token, CultureInfo.InvariantCulture));
            }
        }

        return new BlueTuskInterval(months, days, microseconds);
    }

    private static int ParseInt32(ReadOnlySpan<char> value) =>
        int.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

    private static long ParseInt64(ReadOnlySpan<char> value) =>
        long.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

    private static long ParseSeconds(ReadOnlySpan<char> value) => decimal.ToInt64(
        decimal.Parse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture) *
        1_000_000m);

    private static string FormatSeconds(long microseconds)
    {
        var whole = microseconds / 1_000_000;
        var fraction = Math.Abs(microseconds % 1_000_000);
        if (fraction == 0)
        {
            return whole.ToString(CultureInfo.InvariantCulture);
        }

        var prefix = microseconds is > -1_000_000 and < 0 ? "-0" : whole.ToString(CultureInfo.InvariantCulture);
        return string.Create(CultureInfo.InvariantCulture, $"{prefix}.{fraction:000000}").TrimEnd('0');
    }
}

public readonly record struct BlueTuskBitString
{
    private readonly string? _bits;

    public BlueTuskBitString(string bits)
    {
        ArgumentNullException.ThrowIfNull(bits);
        foreach (var bit in bits)
        {
            if (bit is not ('0' or '1'))
            {
                throw new FormatException("A PostgreSQL bit string can contain only '0' and '1'.");
            }
        }

        _bits = bits;
    }

    public int Length => _bits?.Length ?? 0;

    public char this[int index] => (_bits ?? string.Empty)[index];

    public override string ToString() => _bits ?? string.Empty;
}

public readonly record struct BlueTuskLogSequenceNumber(ulong Value)
{
    public static BlueTuskLogSequenceNumber Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var separator = value.IndexOf('/');
        if (separator <= 0 || separator == value.Length - 1 ||
            !uint.TryParse(value.AsSpan(0, separator), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var high) ||
            !uint.TryParse(value.AsSpan(separator + 1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var low))
        {
            throw new FormatException("The PostgreSQL log sequence number must use hexadecimal X/Y notation.");
        }

        return new BlueTuskLogSequenceNumber(((ulong)high << 32) | low);
    }

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Value >> 32:X}/{Value & uint.MaxValue:X}");
}

public readonly record struct BlueTuskTupleId(uint BlockNumber, ushort OffsetNumber)
{
    public static BlueTuskTupleId Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = value.AsSpan().Trim();
        var separator = text.IndexOf(',');
        if (text.Length < 5 || text[0] != '(' || text[^1] != ')' || separator <= 1 ||
            !uint.TryParse(text[1..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var block) ||
            !ushort.TryParse(text[(separator + 1)..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
        {
            throw new FormatException("The PostgreSQL tuple ID must use '(block,offset)' notation.");
        }

        return new BlueTuskTupleId(block, offset);
    }

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"({BlockNumber},{OffsetNumber})");
}
