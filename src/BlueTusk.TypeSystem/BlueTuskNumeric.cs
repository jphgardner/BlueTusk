using System.Globalization;
using System.Numerics;

namespace BlueTusk.TypeSystem;

public enum BlueTuskNumericKind
{
    Finite,
    NaN,
    PositiveInfinity,
    NegativeInfinity,
}

/// <summary>Represents PostgreSQL arbitrary-precision <c>numeric</c>, including special values.</summary>
public readonly record struct BlueTuskNumeric
{
    public const int MaximumScale = 16_383;

    public BlueTuskNumeric(BigInteger unscaledValue, int scale)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(scale);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(scale, MaximumScale);
        UnscaledValue = unscaledValue;
        Scale = scale;
        Kind = BlueTuskNumericKind.Finite;
    }

    private BlueTuskNumeric(BlueTuskNumericKind kind)
    {
        if (kind == BlueTuskNumericKind.Finite)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
        UnscaledValue = BigInteger.Zero;
        Scale = 0;
    }

    public BlueTuskNumericKind Kind { get; }

    public BigInteger UnscaledValue { get; }

    public int Scale { get; }

    public bool IsFinite => Kind == BlueTuskNumericKind.Finite;

    public static BlueTuskNumeric NaN { get; } = new(BlueTuskNumericKind.NaN);

    public static BlueTuskNumeric PositiveInfinity { get; } = new(BlueTuskNumericKind.PositiveInfinity);

    public static BlueTuskNumeric NegativeInfinity { get; } = new(BlueTuskNumericKind.NegativeInfinity);

    public static BlueTuskNumeric Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = value.AsSpan().Trim();
        if (text.Equals("NaN", StringComparison.OrdinalIgnoreCase))
        {
            return NaN;
        }

        if (text.Equals("Infinity", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("+Infinity", StringComparison.OrdinalIgnoreCase))
        {
            return PositiveInfinity;
        }

        if (text.Equals("-Infinity", StringComparison.OrdinalIgnoreCase))
        {
            return NegativeInfinity;
        }

        var exponentIndex = text.IndexOfAny('e', 'E');
        var exponent = 0;
        if (exponentIndex >= 0)
        {
            if (!int.TryParse(
                    text[(exponentIndex + 1)..],
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out exponent))
            {
                throw new FormatException("The PostgreSQL numeric exponent is invalid.");
            }

            text = text[..exponentIndex];
        }

        var negative = false;
        if (!text.IsEmpty && text[0] is '+' or '-')
        {
            negative = text[0] == '-';
            text = text[1..];
        }

        var decimalIndex = text.IndexOf('.');
        if (decimalIndex >= 0 && text[(decimalIndex + 1)..].IndexOf('.') >= 0)
        {
            throw new FormatException("The PostgreSQL numeric value contains more than one decimal point.");
        }

        var fractionalDigits = decimalIndex < 0 ? 0 : text.Length - decimalIndex - 1;
        var digitCount = text.Length - (decimalIndex < 0 ? 0 : 1);
        if (digitCount == 0)
        {
            throw new FormatException("The PostgreSQL numeric value does not contain digits.");
        }

        Span<char> stackDigits = digitCount <= 256 ? stackalloc char[digitCount] : default;
        var digits = digitCount <= 256 ? stackDigits : new char[digitCount];
        var written = 0;
        foreach (var character in text)
        {
            if (character == '.')
            {
                continue;
            }

            if (!char.IsAsciiDigit(character))
            {
                throw new FormatException($"The PostgreSQL numeric value contains invalid character '{character}'.");
            }

            digits[written++] = character;
        }

        var scale = checked(fractionalDigits - exponent);
        var unscaled = BigInteger.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        if (scale < 0)
        {
            unscaled *= BigInteger.Pow(10, checked(-scale));
            scale = 0;
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(scale, MaximumScale);
        return new BlueTuskNumeric(negative ? -unscaled : unscaled, scale);
    }

    public static bool TryParse(string? value, out BlueTuskNumeric result)
    {
        try
        {
            result = Parse(value!);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            result = default;
            return false;
        }
    }

    public decimal ToDecimal()
    {
        if (!IsFinite)
        {
            throw new InvalidCastException($"PostgreSQL numeric {Kind} cannot be represented as System.Decimal.");
        }

        return decimal.Parse(ToString(), NumberStyles.Number, CultureInfo.InvariantCulture);
    }

    public override string ToString()
    {
        if (!IsFinite)
        {
            return Kind switch
            {
                BlueTuskNumericKind.NaN => "NaN",
                BlueTuskNumericKind.PositiveInfinity => "Infinity",
                BlueTuskNumericKind.NegativeInfinity => "-Infinity",
                _ => throw new InvalidOperationException("Unknown PostgreSQL numeric kind."),
            };
        }

        var negative = UnscaledValue.Sign < 0;
        var digits = BigInteger.Abs(UnscaledValue).ToString(CultureInfo.InvariantCulture);
        string magnitude;
        if (Scale == 0)
        {
            magnitude = digits;
        }
        else if (digits.Length <= Scale)
        {
            magnitude = $"0.{new string('0', Scale - digits.Length)}{digits}";
        }
        else
        {
            magnitude = string.Concat(digits.AsSpan(0, digits.Length - Scale), ".", digits.AsSpan(digits.Length - Scale));
        }

        return negative && UnscaledValue != BigInteger.Zero ? $"-{magnitude}" : magnitude;
    }

    public static implicit operator BlueTuskNumeric(decimal value) =>
        Parse(value.ToString(CultureInfo.InvariantCulture));

    public static explicit operator decimal(BlueTuskNumeric value) => value.ToDecimal();
}
