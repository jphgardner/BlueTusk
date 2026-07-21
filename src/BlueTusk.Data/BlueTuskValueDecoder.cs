using BlueTusk.Protocol;
using BlueTusk.TypeSystem;

namespace BlueTusk.Data;

internal static class BlueTuskValueDecoder
{
    private static readonly BlueTuskTypeRegistry BuiltInTypes = BlueTuskBuiltInTypes.CreateRegistry();

    public static object Decode(BlueTuskFieldDescription field, ReadOnlyMemory<byte>? value)
        => Decode(BuiltInTypes, field, value);

    public static object Decode(
        BlueTuskTypeRegistry types,
        BlueTuskFieldDescription field,
        ReadOnlyMemory<byte>? value)
    {
        ArgumentNullException.ThrowIfNull(types);
        if (value is null)
        {
            return DBNull.Value;
        }

        var format = field.FormatCode switch
        {
            0 => BlueTuskDataFormat.Text,
            1 => BlueTuskDataFormat.Binary,
            _ => throw new InvalidOperationException(
                $"PostgreSQL field '{field.Name}' has unknown format code {field.FormatCode}."),
        };
        var id = new BlueTuskTypeId(field.TypeOid);
        if (!types.TryGetType(id, out var type) || type is null)
        {
            return Unknown(field, format, value.Value, type: null);
        }

        if (!types.TryGetCodec(id, out var codec) || codec is null)
        {
            return Unknown(field, format, value.Value, type);
        }

        var reader = new BlueTuskReader(value.Value.Span);
        var decoded = codec.Read(ref reader, format, type);
        if (reader.Remaining != 0)
        {
            throw new InvalidOperationException(
                $"The {type.QualifiedName} codec left {reader.Remaining} unread field bytes.");
        }

        return decoded ?? DBNull.Value;
    }

    public static Type GetFieldType(BlueTuskFieldDescription field)
        => GetFieldType(BuiltInTypes, field);

    public static Type GetFieldType(BlueTuskTypeRegistry types, BlueTuskFieldDescription field)
    {
        ArgumentNullException.ThrowIfNull(types);
        var id = new BlueTuskTypeId(field.TypeOid);
        return types.TryGetCodec(id, out var codec) && codec is not null
            ? codec.ClrType
            : typeof(BlueTuskUnknownValue);
    }

    public static string GetDataTypeName(uint oid) =>
        GetDataTypeName(BuiltInTypes, oid);

    public static string GetDataTypeName(BlueTuskTypeRegistry types, uint oid)
    {
        ArgumentNullException.ThrowIfNull(types);
        return types.TryGetType(new BlueTuskTypeId(oid), out var type) && type is not null
            ? type.Name
            : $"oid_{oid}";
    }

    private static BlueTuskUnknownValue Unknown(
        BlueTuskFieldDescription field,
        BlueTuskDataFormat format,
        ReadOnlyMemory<byte> value,
        BlueTuskTypeDescriptor? type) =>
        new(
            type ?? new BlueTuskTypeDescriptor
            {
                Id = new BlueTuskTypeId(field.TypeOid),
                Schema = string.Empty,
                Name = $"oid_{field.TypeOid}",
                Kind = BlueTuskTypeKind.Unknown,
            },
            format,
            value);
}
