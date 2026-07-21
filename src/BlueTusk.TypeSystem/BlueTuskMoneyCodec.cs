using System.Globalization;
using System.Numerics;
using System.Text;

namespace BlueTusk.TypeSystem;

public readonly record struct BlueTuskMoney
{
    public BlueTuskMoney(long unscaledValue, int fractionalDigits)
    {
        if (fractionalDigits is < 0 or > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(fractionalDigits));
        }

        UnscaledValue = unscaledValue;
        FractionalDigits = fractionalDigits;
    }

    public long UnscaledValue { get; }

    public int FractionalDigits { get; }

    public BlueTuskNumeric ToNumeric() => new(new BigInteger(UnscaledValue), FractionalDigits);

    public override string ToString() => ToNumeric().ToString();
}

public sealed record BlueTuskMoneyFormat
{
    public BlueTuskMoneyFormat(string locale, int fractionalDigits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        if (fractionalDigits is < 0 or > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(fractionalDigits));
        }

        Locale = locale;
        FractionalDigits = fractionalDigits;
        NumberFormat = ResolveNumberFormat(locale);
    }

    public string Locale { get; }

    public int FractionalDigits { get; }

    internal NumberFormatInfo NumberFormat { get; }

    private static NumberFormatInfo ResolveNumberFormat(string locale)
    {
        var normalized = locale.AsSpan().Trim().ToString();
        if (normalized.Equals("C", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("POSIX", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("C.", StringComparison.OrdinalIgnoreCase))
        {
            return CultureInfo.GetCultureInfo("en-US").NumberFormat;
        }

        var suffix = normalized.IndexOfAny('.', '@');
        if (suffix >= 0)
        {
            normalized = normalized[..suffix];
        }

        normalized = normalized.Replace('_', '-');
        try
        {
            return CultureInfo.GetCultureInfo(normalized).NumberFormat;
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture.NumberFormat;
        }
    }
}

public sealed class BlueTuskMoneyCodec : BlueTuskCodec<BlueTuskMoney>
{
    private readonly BlueTuskMoneyFormat _format;

    public BlueTuskMoneyCodec(BlueTuskMoneyFormat format) =>
        _format = format ?? throw new ArgumentNullException(nameof(format));

    public int FractionalDigits => _format.FractionalDigits;

    public string Locale => _format.Locale;

    public override BlueTuskMoney ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return format switch
        {
            BlueTuskDataFormat.Text => ParseText(reader.ReadRemainingUtf8()),
            BlueTuskDataFormat.Binary when reader.Remaining == sizeof(long) =>
                new BlueTuskMoney(reader.ReadInt64BigEndian(), FractionalDigits),
            BlueTuskDataFormat.Binary => throw new InvalidOperationException(
                $"PostgreSQL {type.QualifiedName} binary values must contain exactly eight bytes."),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskMoney value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        EnsureScale(value);
        if (format == BlueTuskDataFormat.Binary)
        {
            writer.WriteInt64BigEndian(value.UnscaledValue);
        }
        else if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(FormatText(value));
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    private BlueTuskMoney ParseText(string value)
    {
        var text = value.AsSpan().Trim();
        var digits = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (char.IsAsciiDigit(character))
            {
                digits.Append(character);
            }
        }

        if (digits.Length == 0 ||
            !BigInteger.TryParse(digits.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var magnitude) ||
            magnitude > long.MaxValue + BigInteger.One)
        {
            throw new FormatException("The PostgreSQL money value contains an invalid or out-of-range amount.");
        }

        var negativeSign = _format.NumberFormat.NegativeSign;
        var negative = (negativeSign.Length > 0 && text.Contains(negativeSign, StringComparison.Ordinal)) ||
            text.Contains('-') ||
            text.Contains('\u2212') ||
            (text.Length >= 2 && text[0] == '(' && text[^1] == ')');
        var signed = negative ? -magnitude : magnitude;
        if (signed < long.MinValue || signed > long.MaxValue)
        {
            throw new FormatException("The PostgreSQL money value is outside its signed 64-bit range.");
        }

        return new BlueTuskMoney((long)signed, FractionalDigits);
    }

    private string FormatText(BlueTuskMoney value)
    {
        var negative = value.UnscaledValue < 0;
        var digits = BigInteger.Abs(new BigInteger(value.UnscaledValue)).ToString(CultureInfo.InvariantCulture);
        if (FractionalDigits == 0)
        {
            return negative ? $"{GetNegativeSign()}{digits}" : digits;
        }

        if (digits.Length <= FractionalDigits)
        {
            digits = $"{new string('0', FractionalDigits - digits.Length + 1)}{digits}";
        }

        var separator = _format.NumberFormat.CurrencyDecimalSeparator;
        var amount = string.Concat(
            digits.AsSpan(0, digits.Length - FractionalDigits),
            separator,
            digits.AsSpan(digits.Length - FractionalDigits));
        return negative ? $"{GetNegativeSign()}{amount}" : amount;
    }

    private string GetNegativeSign() =>
        string.IsNullOrEmpty(_format.NumberFormat.NegativeSign) ? "-" : _format.NumberFormat.NegativeSign;

    private void EnsureScale(BlueTuskMoney value)
    {
        if (value.FractionalDigits != FractionalDigits)
        {
            throw new InvalidOperationException(
                $"The money value has {value.FractionalDigits} fractional digits, but PostgreSQL locale " +
                $"'{Locale}' uses {FractionalDigits}.");
        }
    }
}
