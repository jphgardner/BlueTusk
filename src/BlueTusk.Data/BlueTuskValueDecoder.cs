using BlueTusk.Protocol;
using BlueTusk.TypeSystem;

namespace BlueTusk.Data;

internal readonly record struct BlueTuskResolvedField(
    BlueTuskFieldDescription Field,
    BlueTuskDataFormat? Format,
    BlueTuskTypeDescriptor? Type,
    IBlueTuskCodec? Codec);

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
        var resolved = Resolve(types, field);
        return Decode(resolved, value);
    }

    internal static BlueTuskResolvedField Resolve(
        BlueTuskTypeRegistry types,
        BlueTuskFieldDescription field)
    {
        ArgumentNullException.ThrowIfNull(types);
        var format = field.FormatCode switch
        {
            0 => BlueTuskDataFormat.Text,
            1 => BlueTuskDataFormat.Binary,
            _ => (BlueTuskDataFormat?)null,
        };
        var id = new BlueTuskTypeId(field.TypeOid);
        _ = types.TryGetType(id, out var type);
        _ = types.TryGetCodec(id, out var codec);
        return new BlueTuskResolvedField(field, format, type, codec);
    }

    internal static object Decode(
        in BlueTuskResolvedField resolved,
        ReadOnlyMemory<byte>? value)
    {
        if (value is null)
        {
            return DBNull.Value;
        }

        var format = GetFormat(resolved);
        if (resolved.Type is not { } type)
        {
            return Unknown(resolved.Field, format, value.Value, type: null);
        }

        if (resolved.Codec is not { } codec)
        {
            return Unknown(resolved.Field, format, value.Value, type);
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

    internal static T DecodeTyped<T>(
        in BlueTuskResolvedField resolved,
        ReadOnlyMemory<byte>? value)
    {
        if (value is null)
        {
            throw new InvalidCastException("A database NULL cannot be read as a non-null value.");
        }

        if (resolved.Type is not { } type ||
            resolved.Codec is not IBlueTuskCodec<T> codec)
        {
            throw new InvalidCastException(
                $"PostgreSQL field '{resolved.Field.Name}' does not have a codec for {typeof(T).FullName}.");
        }

        var reader = new BlueTuskReader(value.Value.Span);
        var decoded = codec.ReadTyped(ref reader, GetFormat(resolved), type);
        EnsureFullyRead(type, reader.Remaining);
        return decoded;
    }

    public static Type GetFieldType(BlueTuskFieldDescription field)
        => GetFieldType(BuiltInTypes, field);

    public static Type GetFieldType(BlueTuskTypeRegistry types, BlueTuskFieldDescription field)
    {
        ArgumentNullException.ThrowIfNull(types);
        return GetFieldType(Resolve(types, field));
    }

    internal static Type GetFieldType(in BlueTuskResolvedField resolved) =>
        resolved.Codec?.ClrType ?? typeof(BlueTuskUnknownValue);

    public static string GetDataTypeName(uint oid) =>
        GetDataTypeName(BuiltInTypes, oid);

    public static string GetDataTypeName(BlueTuskTypeRegistry types, uint oid)
    {
        ArgumentNullException.ThrowIfNull(types);
        return types.TryGetType(new BlueTuskTypeId(oid), out var type) && type is not null
            ? type.Name
            : $"oid_{oid}";
    }

    internal static string GetDataTypeName(in BlueTuskResolvedField resolved) =>
        resolved.Type?.Name ?? $"oid_{resolved.Field.TypeOid}";

    private static BlueTuskDataFormat GetFormat(in BlueTuskResolvedField resolved) =>
        resolved.Format ?? throw new InvalidOperationException(
            $"PostgreSQL field '{resolved.Field.Name}' has unknown format code {resolved.Field.FormatCode}.");

    private static void EnsureFullyRead(BlueTuskTypeDescriptor type, int remaining)
    {
        if (remaining != 0)
        {
            throw new InvalidOperationException(
                $"The {type.QualifiedName} codec left {remaining} unread field bytes.");
        }
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
