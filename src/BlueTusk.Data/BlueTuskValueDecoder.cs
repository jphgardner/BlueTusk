using System.Globalization;
using System.Text;
using BlueTusk.Protocol;
using BlueTusk.TypeSystem;

namespace BlueTusk.Data;

internal static class BlueTuskValueDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static object Decode(BlueTuskFieldDescription field, ReadOnlyMemory<byte>? value)
    {
        if (value is null)
        {
            return DBNull.Value;
        }

        if (field.FormatCode != 0)
        {
            return Unknown(field, BlueTuskDataFormat.Binary, value.Value);
        }

        var text = StrictUtf8.GetString(value.Value.Span);
        return field.TypeOid switch
        {
            16 => text == "t",
            20 => long.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
            21 => short.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
            23 => int.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
            26 => uint.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture),
            700 => float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
            701 => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
            1700 => decimal.Parse(text, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture),
            2950 => Guid.Parse(text),
            17 => DecodeBytea(text),
            18 or 19 or 25 or 1042 or 1043 or 114 or 3802 => text,
            1082 => DateOnly.Parse(text, CultureInfo.InvariantCulture),
            1083 => TimeOnly.Parse(text, CultureInfo.InvariantCulture),
            1114 => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces),
            1184 => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces),
            _ => Unknown(field, BlueTuskDataFormat.Text, value.Value),
        };
    }

    public static Type GetFieldType(BlueTuskFieldDescription field) => field.TypeOid switch
    {
        16 => typeof(bool),
        20 => typeof(long),
        21 => typeof(short),
        23 => typeof(int),
        26 => typeof(uint),
        700 => typeof(float),
        701 => typeof(double),
        1700 => typeof(decimal),
        2950 => typeof(Guid),
        17 => typeof(byte[]),
        18 or 19 or 25 or 1042 or 1043 or 114 or 3802 => typeof(string),
        1082 => typeof(DateOnly),
        1083 => typeof(TimeOnly),
        1114 => typeof(DateTime),
        1184 => typeof(DateTimeOffset),
        _ => typeof(BlueTuskUnknownValue),
    };

    public static string GetDataTypeName(uint oid) => oid switch
    {
        16 => "bool",
        17 => "bytea",
        18 => "char",
        19 => "name",
        20 => "int8",
        21 => "int2",
        23 => "int4",
        25 => "text",
        26 => "oid",
        700 => "float4",
        701 => "float8",
        1042 => "bpchar",
        1043 => "varchar",
        1082 => "date",
        1083 => "time",
        1114 => "timestamp",
        1184 => "timestamptz",
        114 => "json",
        1700 => "numeric",
        2950 => "uuid",
        3802 => "jsonb",
        _ => $"oid_{oid}",
    };

    private static BlueTuskUnknownValue Unknown(
        BlueTuskFieldDescription field,
        BlueTuskDataFormat format,
        ReadOnlyMemory<byte> value) =>
        new(
            new BlueTuskTypeDescriptor
            {
                Id = new BlueTuskTypeId(field.TypeOid),
                Schema = string.Empty,
                Name = $"oid_{field.TypeOid}",
                Kind = BlueTuskTypeKind.Unknown,
            },
            format,
            value);

    private static byte[] DecodeBytea(string text) =>
        text.StartsWith("\\x", StringComparison.Ordinal)
            ? Convert.FromHexString(text[2..])
            : throw new NotSupportedException("Legacy escaped bytea text is not implemented.");
}
