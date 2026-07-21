using System.Globalization;

namespace BlueTusk.Replication;

/// <summary>A PostgreSQL write-ahead log position.</summary>
public readonly record struct BlueTuskLogSequenceNumber(ulong Value) : ISpanFormattable
{
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
