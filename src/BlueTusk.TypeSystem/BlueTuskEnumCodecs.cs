using System.Reflection;
using System.Runtime.Serialization;

namespace BlueTusk.TypeSystem;

/// <summary>An unmapped PostgreSQL enum label with its exact case and whitespace preserved.</summary>
public readonly record struct BlueTuskEnumValue(string Label)
{
    public override string ToString() => Label;
}

/// <summary>Reads and writes an unmapped PostgreSQL enum as its exact catalogue label.</summary>
public sealed class BlueTuskEnumValueCodec : BlueTuskCodec<BlueTuskEnumValue>
{
    public override BlueTuskEnumValue ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        var label = reader.ReadRemainingUtf8();
        ValidateCatalogueLabel(type, label);
        return new BlueTuskEnumValue(label);
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        BlueTuskEnumValue value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(value.Label);
        ValidateCatalogueLabel(type, value.Label);
        writer.WriteUtf8(value.Label);
    }

    internal static void ValidateCatalogueLabel(BlueTuskTypeDescriptor type, string label)
    {
        if (type.EnumLabels.Count != 0 && !type.EnumLabels.Contains(label, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{label}' is not a catalogue label for PostgreSQL enum {type.QualifiedName}.");
        }
    }
}

/// <summary>Maps a CLR enum to exact PostgreSQL enum labels.</summary>
public sealed class BlueTuskEnumCodec<TEnum> : BlueTuskCodec<TEnum>
    where TEnum : struct, Enum
{
    private readonly Dictionary<TEnum, string> _labelsByValue;
    private readonly Dictionary<string, TEnum> _valuesByLabel;

    public BlueTuskEnumCodec(IReadOnlyDictionary<TEnum, string>? labels = null)
    {
        if (typeof(TEnum).IsDefined(typeof(FlagsAttribute), inherit: false))
        {
            throw new ArgumentException(
                $"PostgreSQL enum mappings do not support CLR flags enum {typeof(TEnum).FullName}.");
        }

        var labelsByValue = new Dictionary<TEnum, string>();
        var valuesByLabel = new Dictionary<string, TEnum>(StringComparer.Ordinal);
        var names = Enum.GetNames<TEnum>();
        for (var index = 0; index < names.Length; index++)
        {
            var name = names[index];
            var member = typeof(TEnum).GetField(name, BindingFlags.Public | BindingFlags.Static)!;
            var value = (TEnum)member.GetValue(null)!;
            var label = labels is not null && labels.TryGetValue(value, out var configuredLabel)
                ? configuredLabel
                : member.GetCustomAttribute<BlueTuskNameAttribute>()?.Name ??
                    member.GetCustomAttribute<EnumMemberAttribute>()?.Value ??
                    name;
            ValidateLabel(label);
            if (!labelsByValue.TryAdd(value, label))
            {
                throw new ArgumentException(
                    $"CLR enum {typeof(TEnum).FullName} contains aliases for value {value}.");
            }

            if (!valuesByLabel.TryAdd(label, value))
            {
                throw new ArgumentException(
                    $"PostgreSQL enum label '{label}' is mapped more than once.",
                    nameof(labels));
            }
        }

        if (labels is not null)
        {
            foreach (var mapping in labels)
            {
                if (!labelsByValue.ContainsKey(mapping.Key))
                {
                    throw new ArgumentException(
                        $"A label was configured for undefined CLR enum value {mapping.Key}.",
                        nameof(labels));
                }
            }
        }

        _labelsByValue = labelsByValue;
        _valuesByLabel = valuesByLabel;
    }

    public override TEnum ReadTyped(
        ref BlueTuskReader reader,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        var label = reader.ReadRemainingUtf8();
        BlueTuskEnumValueCodec.ValidateCatalogueLabel(type, label);
        return _valuesByLabel.TryGetValue(label, out var value)
            ? value
            : throw new InvalidOperationException(
                $"PostgreSQL enum label '{label}' has no {typeof(TEnum).FullName} mapping.");
    }

    public override void WriteTyped(
        ref BlueTuskWriter writer,
        TEnum value,
        BlueTuskDataFormat format,
        BlueTuskTypeDescriptor type)
    {
        if (!_labelsByValue.TryGetValue(value, out var label))
        {
            throw new InvalidOperationException(
                $"CLR enum value {value} has no {type.QualifiedName} mapping.");
        }

        BlueTuskEnumValueCodec.ValidateCatalogueLabel(type, label);
        writer.WriteUtf8(label);
    }

    private static void ValidateLabel(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        if (label.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("PostgreSQL enum labels cannot contain a null character.", nameof(label));
        }
    }
}
