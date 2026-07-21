using System.Globalization;
using System.Numerics;

namespace BlueTusk.TypeSystem;

/// <summary>Encodes PostgreSQL arbitrary-precision <c>numeric</c> text and base-10000 binary values.</summary>
public sealed class BlueTuskNumericCodec : BlueTuskCodec<BlueTuskNumeric>
{
    private const ushort PositiveSign = 0x0000;
    private const ushort NegativeSign = 0x4000;
    private const ushort NaNSign = 0xC000;
    private const ushort PositiveInfinitySign = 0xD000;
    private const ushort NegativeInfinitySign = 0xF000;

    public static int GetMaximumBinarySize(BlueTuskNumeric value)
    {
        if (!value.IsFinite)
        {
            return 8;
        }

        var digits = BigInteger.Abs(value.UnscaledValue).ToString(CultureInfo.InvariantCulture).Length;
        var integerDigits = Math.Max(0, digits - value.Scale);
        var integerGroups = (integerDigits + 3) / 4;
        var fractionalGroups = (value.Scale + 3) / 4;
        return checked(8 + (sizeof(short) * (integerGroups + fractionalGroups)));
    }

    public override BlueTuskNumeric ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Text)
        {
            return BlueTuskNumeric.Parse(reader.ReadRemainingUtf8());
        }

        if (format != BlueTuskDataFormat.Binary)
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        if (reader.Remaining < 8)
        {
            throw new InvalidOperationException("PostgreSQL numeric binary values require an eight-byte header.");
        }

        var digitCount = reader.ReadInt16BigEndian();
        var weight = reader.ReadInt16BigEndian();
        var sign = reader.ReadUInt16BigEndian();
        var displayScale = reader.ReadUInt16BigEndian();
        if (digitCount < 0 || reader.Remaining != digitCount * sizeof(short))
        {
            throw new InvalidOperationException("PostgreSQL numeric binary digit count does not match its payload.");
        }

        if (sign is NaNSign or PositiveInfinitySign or NegativeInfinitySign)
        {
            if (digitCount != 0)
            {
                throw new InvalidOperationException("A special PostgreSQL numeric value cannot contain digits.");
            }

            return sign switch
            {
                NaNSign => BlueTuskNumeric.NaN,
                PositiveInfinitySign => BlueTuskNumeric.PositiveInfinity,
                NegativeInfinitySign => BlueTuskNumeric.NegativeInfinity,
                _ => throw new InvalidOperationException(),
            };
        }

        if (sign is not (PositiveSign or NegativeSign))
        {
            throw new InvalidOperationException($"PostgreSQL numeric contains unknown sign 0x{sign:X4}.");
        }

        var aggregate = BigInteger.Zero;
        for (var index = 0; index < digitCount; index++)
        {
            var digit = reader.ReadUInt16BigEndian();
            if (digit >= 10_000)
            {
                throw new InvalidOperationException($"PostgreSQL numeric base-10000 digit {digit} is invalid.");
            }

            aggregate = (aggregate * 10_000) + digit;
        }

        var basePower = weight - digitCount + 1;
        BigInteger unscaled;
        if (basePower >= 0)
        {
            unscaled = aggregate * BigInteger.Pow(10_000, basePower) * BigInteger.Pow(10, displayScale);
        }
        else
        {
            var numerator = aggregate * BigInteger.Pow(10, displayScale);
            unscaled = BigInteger.DivRem(
                numerator,
                BigInteger.Pow(10_000, -basePower),
                out var remainder);
            if (remainder != BigInteger.Zero)
            {
                throw new InvalidOperationException(
                    "PostgreSQL numeric binary digits contain precision beyond the declared display scale.");
            }
        }

        return new BlueTuskNumeric(sign == NegativeSign ? -unscaled : unscaled, displayScale);
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskNumeric value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (format == BlueTuskDataFormat.Text)
        {
            writer.WriteUtf8(value.ToString());
            return;
        }

        if (format != BlueTuskDataFormat.Binary)
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        if (!value.IsFinite)
        {
            writer.WriteInt16BigEndian(0);
            writer.WriteInt16BigEndian(0);
            writer.WriteUInt16BigEndian(value.Kind switch
            {
                BlueTuskNumericKind.NaN => NaNSign,
                BlueTuskNumericKind.PositiveInfinity => PositiveInfinitySign,
                BlueTuskNumericKind.NegativeInfinity => NegativeInfinitySign,
                _ => throw new InvalidOperationException("Unknown PostgreSQL numeric kind."),
            });
            writer.WriteUInt16BigEndian(0);
            return;
        }

        var scale = value.Scale;
        var digits = BigInteger.Abs(value.UnscaledValue).ToString(CultureInfo.InvariantCulture);
        var integerLength = Math.Max(0, digits.Length - scale);
        var integerPart = integerLength == 0 ? string.Empty : digits[..integerLength];
        var fractionalPart = scale == 0
            ? string.Empty
            : digits.Length <= scale
                ? string.Concat(new string('0', scale - digits.Length), digits)
                : digits[integerLength..];
        integerPart = integerPart.TrimStart('0');

        var paddedIntegerLength = integerPart.Length == 0 ? 0 : ((integerPart.Length + 3) / 4) * 4;
        var paddedFractionLength = fractionalPart.Length == 0 ? 0 : ((fractionalPart.Length + 3) / 4) * 4;
        var padded = string.Concat(
            new string('0', paddedIntegerLength - integerPart.Length),
            integerPart,
            fractionalPart,
            new string('0', paddedFractionLength - fractionalPart.Length));
        var groups = new List<ushort>(padded.Length / 4);
        for (var index = 0; index < padded.Length; index += 4)
        {
            groups.Add(ushort.Parse(padded.AsSpan(index, 4), NumberStyles.None, CultureInfo.InvariantCulture));
        }

        var weight = (paddedIntegerLength / 4) - 1;
        while (groups.Count > 0 && groups[0] == 0)
        {
            groups.RemoveAt(0);
            weight--;
        }

        while (groups.Count > 0 && groups[^1] == 0)
        {
            groups.RemoveAt(groups.Count - 1);
        }

        writer.WriteInt16BigEndian(checked((short)groups.Count));
        writer.WriteInt16BigEndian(groups.Count == 0 ? (short)0 : checked((short)weight));
        writer.WriteUInt16BigEndian(value.UnscaledValue.Sign < 0 ? NegativeSign : PositiveSign);
        writer.WriteUInt16BigEndian(checked((ushort)scale));
        foreach (var group in groups)
        {
            writer.WriteUInt16BigEndian(group);
        }
    }
}
