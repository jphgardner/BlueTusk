using BlueTusk.TypeSystem;

namespace BlueTusk.Data.Copy;

internal static class BlueTuskBinaryCopyCodec
{
    public static byte[] Encode(
        object value,
        uint? postgreSqlTypeOid,
        BlueTuskTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(registry);
        var parameter = new BlueTuskParameter(value)
        {
            PostgreSqlTypeOid = postgreSqlTypeOid,
        };
        var encoded = BlueTuskParameterEncoder.Encode(parameter, registry);
        if (encoded.FormatCode == (short)BlueTuskDataFormat.Binary)
        {
            return encoded.Value?.ToArray() ??
                throw new InvalidOperationException("A non-null COPY value encoded as null.");
        }

        var typeId = new BlueTuskTypeId(encoded.TypeOid);
        if (!registry.TryGetType(typeId, out var type) ||
            type is null ||
            !registry.TryGetCodec(typeId, out var codec) ||
            codec is null)
        {
            throw new NotSupportedException(
                $"PostgreSQL type OID {encoded.TypeOid} has no binary COPY codec.");
        }

        var length = 256;
        while (true)
        {
            var bytes = new byte[length];
            var writer = new BlueTuskWriter(bytes);
            try
            {
                codec.Write(
                    ref writer,
                    value,
                    BlueTuskDataFormat.Binary,
                    type);
                Array.Resize(ref bytes, writer.WrittenCount);
                return bytes;
            }
            catch (BlueTuskWriteBufferTooSmallException) when (length < Array.MaxLength)
            {
                length = length > Array.MaxLength / 2 ? Array.MaxLength : length * 2;
            }
        }
    }

    public static T Decode<T>(
        ReadOnlyMemory<byte> data,
        uint? postgreSqlTypeOid,
        BlueTuskTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var typeId = new BlueTuskTypeId(
            postgreSqlTypeOid ?? ResolveTypeOid(typeof(T), registry));
        if (!registry.TryGetType(typeId, out var type) ||
            type is null ||
            !registry.TryGetCodec(typeId, out var codec) ||
            codec is null)
        {
            throw new NotSupportedException(
                $"PostgreSQL type OID {typeId} has no binary COPY codec.");
        }

        var reader = new BlueTuskReader(data.Span);
        var value = codec.Read(
            ref reader,
            BlueTuskDataFormat.Binary,
            type);
        if (reader.Remaining != 0)
        {
            throw new InvalidOperationException(
                $"The {type.QualifiedName} codec left {reader.Remaining} unread COPY field bytes.");
        }

        return value is T typed
            ? typed
            : throw new InvalidCastException(
                $"The {type.QualifiedName} codec returned {value?.GetType().FullName ?? "null"}, not {typeof(T).FullName}.");
    }

    private static uint ResolveTypeOid(
        Type clrType,
        BlueTuskTypeRegistry registry)
    {
        clrType = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (clrType == typeof(bool))
        {
            return BlueTuskBuiltInTypes.Boolean.Id.Oid;
        }

        if (clrType == typeof(byte[]))
        {
            return BlueTuskBuiltInTypes.Bytea.Id.Oid;
        }

        if (clrType == typeof(short))
        {
            return BlueTuskBuiltInTypes.Int2.Id.Oid;
        }

        if (clrType == typeof(int))
        {
            return BlueTuskBuiltInTypes.Int4.Id.Oid;
        }

        if (clrType == typeof(uint))
        {
            return BlueTuskBuiltInTypes.Oid.Id.Oid;
        }

        if (clrType == typeof(long))
        {
            return BlueTuskBuiltInTypes.Int8.Id.Oid;
        }

        if (clrType == typeof(float))
        {
            return BlueTuskBuiltInTypes.Float4.Id.Oid;
        }

        if (clrType == typeof(double))
        {
            return BlueTuskBuiltInTypes.Float8.Id.Oid;
        }

        if (clrType == typeof(string))
        {
            return BlueTuskBuiltInTypes.Text.Id.Oid;
        }

        if (clrType == typeof(Guid))
        {
            return BlueTuskBuiltInTypes.Uuid.Id.Oid;
        }

        if (clrType == typeof(DateOnly))
        {
            return BlueTuskBuiltInTypes.Date.Id.Oid;
        }

        if (clrType == typeof(TimeSpan))
        {
            return BlueTuskBuiltInTypes.Time.Id.Oid;
        }

        if (clrType == typeof(DateTime))
        {
            return BlueTuskBuiltInTypes.Timestamp.Id.Oid;
        }

        if (clrType == typeof(DateTimeOffset))
        {
            return BlueTuskBuiltInTypes.TimestampWithTimeZone.Id.Oid;
        }

        if (registry.TryGetType(clrType, out var type, out _))
        {
            return type!.Id.Oid;
        }

        throw new NotSupportedException(
            $"CLR type {clrType.FullName} does not have an unambiguous binary COPY mapping. Supply PostgreSqlTypeOid explicitly.");
    }
}
