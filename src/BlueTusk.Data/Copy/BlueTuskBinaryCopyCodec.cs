using BlueTusk.TypeSystem;

namespace BlueTusk.Data.Copy;

internal static class BlueTuskBinaryCopyCodec
{
    public static byte[] Encode(
        object value,
        uint? postgreSqlTypeOid,
        BlueTuskTypeRegistry registry)
    {
        byte[]? reusableBuffer = null;
        return Encode(value, postgreSqlTypeOid, registry, ref reusableBuffer).ToArray();
    }

    public static ReadOnlyMemory<byte> Encode(
        object value,
        uint? postgreSqlTypeOid,
        BlueTuskTypeRegistry registry,
        ref byte[]? reusableBuffer)
    {
        var parameter = new BlueTuskParameter();
        return Encode(
            value,
            postgreSqlTypeOid,
            registry,
            parameter,
            ref reusableBuffer);
    }

    public static ReadOnlyMemory<byte> Encode(
        object value,
        uint? postgreSqlTypeOid,
        BlueTuskTypeRegistry registry,
        BlueTuskParameter parameter,
        ref byte[]? reusableBuffer)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(parameter);
        parameter.Value = value;
        parameter.PostgreSqlTypeOid = postgreSqlTypeOid;
        var encoded = BlueTuskParameterEncoder.Encode(parameter, registry, ref reusableBuffer);
        if (encoded.FormatCode == (short)BlueTuskDataFormat.Binary)
        {
            return encoded.Value ??
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
                reusableBuffer = bytes;
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
        var decoder = default(BlueTuskBinaryCopyDecoder);
        return Decode<T>(data, postgreSqlTypeOid, registry, ref decoder);
    }

    public static T Decode<T>(
        ReadOnlyMemory<byte> data,
        uint? postgreSqlTypeOid,
        BlueTuskTypeRegistry registry,
        ref BlueTuskBinaryCopyDecoder decoder)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var clrType = typeof(T);
        if (!ReferenceEquals(decoder.ClrType, clrType) ||
            decoder.RequestedTypeOid != postgreSqlTypeOid ||
            decoder.Type is null ||
            decoder.Codec is null)
        {
            var typeId = new BlueTuskTypeId(
                postgreSqlTypeOid ?? ResolveTypeOid(clrType, registry));
            if (!registry.TryGetType(typeId, out var resolvedType) ||
                resolvedType is null ||
                !registry.TryGetCodec(typeId, out var resolvedCodec) ||
                resolvedCodec is null)
            {
                throw new NotSupportedException(
                    $"PostgreSQL type OID {typeId} has no binary COPY codec.");
            }

            decoder.ClrType = clrType;
            decoder.RequestedTypeOid = postgreSqlTypeOid;
            decoder.Type = resolvedType;
            decoder.Codec = resolvedCodec;
        }

        var type = decoder.Type;
        var codec = decoder.Codec;
        var reader = new BlueTuskReader(data.Span);
        T? typedValue;
        if (codec is BlueTuskCodec<T> typedCodec)
        {
            typedValue = typedCodec.ReadTyped(
                ref reader,
                BlueTuskDataFormat.Binary,
                type);
        }
        else
        {
            var value = codec.Read(
                ref reader,
                BlueTuskDataFormat.Binary,
                type);
            typedValue = value is T typed
                ? typed
                : throw new InvalidCastException(
                    $"The {type.QualifiedName} codec returned " +
                    $"{value?.GetType().FullName ?? "null"}, not {typeof(T).FullName}.");
        }

        if (reader.Remaining != 0)
        {
            throw new InvalidOperationException(
                $"The {type.QualifiedName} codec left {reader.Remaining} unread COPY field bytes.");
        }

        return typedValue;
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

internal struct BlueTuskBinaryCopyFieldState
{
    public byte[]? Buffer;

    public BlueTuskBinaryCopyDecoder Decoder;
}

internal struct BlueTuskBinaryCopyDecoder
{
    public Type? ClrType;

    public uint? RequestedTypeOid;

    public BlueTuskTypeDescriptor? Type;

    public IBlueTuskCodec? Codec;
}
