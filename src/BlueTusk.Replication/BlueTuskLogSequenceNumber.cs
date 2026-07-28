using System.Globalization;

namespace BlueTusk.Replication;

/// <summary>A PostgreSQL write-ahead log position.</summary>
public readonly record struct BlueTuskLogSequenceNumber(ulong Value) :
    IComparable<BlueTuskLogSequenceNumber>,
    ISpanFormattable
{
    public static BlueTuskLogSequenceNumber Zero => default;

    public static BlueTuskLogSequenceNumber Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var separator = value.IndexOf('/');
        if (separator <= 0 || separator == value.Length - 1)
        {
            throw new FormatException("A PostgreSQL LSN must have the form XXXXXXXX/XXXXXXXX.");
        }

        var high = uint.Parse(value.AsSpan(0, separator), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        var low = uint.Parse(value.AsSpan(separator + 1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        return new BlueTuskLogSequenceNumber(((ulong)high << 32) | low);
    }

    public static bool TryParse(string? value, out BlueTuskLogSequenceNumber result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = default;
            return false;
        }

        var separator = value.IndexOf('/');
        if (separator <= 0 ||
            separator == value.Length - 1 ||
            !uint.TryParse(
                value.AsSpan(0, separator),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out var high) ||
            !uint.TryParse(
                value.AsSpan(separator + 1),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out var low))
        {
            result = default;
            return false;
        }

        result = new BlueTuskLogSequenceNumber(((ulong)high << 32) | low);
        return true;
    }

    public int CompareTo(BlueTuskLogSequenceNumber other) => Value.CompareTo(other.Value);

    public static bool operator <(
        BlueTuskLogSequenceNumber left,
        BlueTuskLogSequenceNumber right) =>
        left.Value < right.Value;

    public static bool operator >(
        BlueTuskLogSequenceNumber left,
        BlueTuskLogSequenceNumber right) =>
        left.Value > right.Value;

    public static bool operator <=(
        BlueTuskLogSequenceNumber left,
        BlueTuskLogSequenceNumber right) =>
        left.Value <= right.Value;

    public static bool operator >=(
        BlueTuskLogSequenceNumber left,
        BlueTuskLogSequenceNumber right) =>
        left.Value >= right.Value;

    public static BlueTuskLogSequenceNumber operator +(
        BlueTuskLogSequenceNumber position,
        ulong byteCount) =>
        new(checked(position.Value + byteCount));

    public override string ToString() => $"{Value >> 32:X}/{Value & uint.MaxValue:X}";

    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    public bool TryFormat(
        Span<char> destination,
        out int charsWritten,
        ReadOnlySpan<char> format,
        IFormatProvider? provider)
    {
        var text = ToString();
        if (text.AsSpan().TryCopyTo(destination))
        {
            charsWritten = text.Length;
            return true;
        }

        charsWritten = 0;
        return false;
    }
}
