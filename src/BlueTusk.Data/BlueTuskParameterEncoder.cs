using System.Buffers;
using System.Buffers.Binary;
using System.Data;
using System.Globalization;
using System.Text;
using BlueTusk.Client;
using BlueTusk.TypeSystem;

namespace BlueTusk.Data;

internal static class BlueTuskParameterEncoder
{
    private const uint BooleanOid = 16;
    private const uint ByteaOid = 17;
    private const uint Int8Oid = 20;
    private const uint Int2Oid = 21;
    private const uint Int4Oid = 23;
    private const uint TextOid = 25;
    private const uint OidOid = 26;
    private const uint TidOid = 27;
    private const uint PointOid = 600;
    private const uint LineSegmentOid = 601;
    private const uint PathOid = 602;
    private const uint BoxOid = 603;
    private const uint PolygonOid = 604;
    private const uint LineOid = 628;
    private const uint Float4Oid = 700;
    private const uint Float8Oid = 701;
    private const uint CircleOid = 718;
    private const uint CidrOid = 650;
    private const uint Macaddr8Oid = 774;
    private const uint MoneyOid = 790;
    private const uint MacaddrOid = 829;
    private const uint InetOid = 869;
    private const uint DateOid = 1082;
    private const uint TimeOid = 1083;
    private const uint TimestampOid = 1114;
    private const uint TimestampWithTimeZoneOid = 1184;
    private const uint IntervalOid = 1186;
    private const uint TimeWithTimeZoneOid = 1266;
    private const uint BitOid = 1560;
    private const uint VarbitOid = 1562;
    private const uint NumericOid = 1700;
    private const uint UuidOid = 2950;
    private const uint PgLsnOid = 3220;
    private const uint TextSearchVectorOid = 3614;
    private const uint TextSearchQueryOid = 3615;

    public static IReadOnlyList<BlueTuskExtendedQueryParameter> Encode(
        BlueTuskParameterCollection parameters,
        BlueTuskTypeRegistry? types = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return Encode(parameters.Items, types);
    }

    public static IReadOnlyList<BlueTuskExtendedQueryParameter> Encode(
        IReadOnlyList<BlueTuskParameter> parameters,
        BlueTuskTypeRegistry? types = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.Count == 0)
        {
            return Array.Empty<BlueTuskExtendedQueryParameter>();
        }

        var encoded = new BlueTuskExtendedQueryParameter[parameters.Count];
        for (var index = 0; index < parameters.Count; index++)
        {
            encoded[index] = Encode(parameters[index], types);
        }

        return encoded;
    }

    internal static void Encode(
        IReadOnlyList<BlueTuskParameter> parameters,
        BlueTuskTypeRegistry? types,
        BlueTuskExtendedQueryParameter[] destination,
        byte[]?[] reusableBuffers)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(reusableBuffers);
        if (destination.Length < parameters.Count || reusableBuffers.Length < parameters.Count)
        {
            throw new ArgumentException("Reusable parameter storage must match the parameter count.");
        }

        for (var index = 0; index < parameters.Count; index++)
        {
            destination[index] = Encode(
                parameters[index],
                types,
                ref reusableBuffers[index],
                rentBuffer: true);
        }
    }

    public static BlueTuskExtendedQueryParameter Encode(
        BlueTuskParameter parameter,
        BlueTuskTypeRegistry? types = null)
    {
        byte[]? reusableBuffer = null;
        return Encode(parameter, types, ref reusableBuffer);
    }

    internal static BlueTuskExtendedQueryParameter Encode(
        BlueTuskParameter parameter,
        BlueTuskTypeRegistry? types,
        ref byte[]? reusableBuffer,
        bool rentBuffer = false)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        var value = parameter.Value is DBNull ? null : parameter.Value;
        var typeOid = parameter.PostgreSqlTypeOid
            ?? ResolveTypeOid(parameter.PostgreSqlTypeName, parameter.DbType, value, types);
        if (typeOid == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(parameter),
                "PostgreSqlTypeOid must be a non-zero PostgreSQL type OID.");
        }

        return value is null
            ? new BlueTuskExtendedQueryParameter(typeOid, 0, null)
            : EncodeValue(typeOid, value, types, ref reusableBuffer, rentBuffer);
    }

    private static uint ResolveTypeOid(
        string? postgreSqlTypeName,
        DbType dbType,
        object? value,
        BlueTuskTypeRegistry? types)
    {
        if (!string.IsNullOrWhiteSpace(postgreSqlTypeName))
        {
            return ResolveTypeName(postgreSqlTypeName, types);
        }

        if (dbType != DbType.Object)
        {
            return dbType switch
            {
                DbType.Boolean => BooleanOid,
                DbType.SByte or DbType.Byte or DbType.Int16 or DbType.UInt16 => Int2Oid,
                DbType.Int32 => Int4Oid,
                DbType.UInt32 => OidOid,
                DbType.Int64 or DbType.UInt64 => Int8Oid,
                DbType.Single => Float4Oid,
                DbType.Double => Float8Oid,
                DbType.Decimal or DbType.Currency or DbType.VarNumeric => NumericOid,
                DbType.Guid => UuidOid,
                DbType.Binary => ByteaOid,
                DbType.Date => DateOid,
                DbType.Time => TimeOid,
                DbType.DateTime or DbType.DateTime2 => TimestampOid,
                DbType.DateTimeOffset => TimestampWithTimeZoneOid,
                DbType.AnsiString or DbType.AnsiStringFixedLength or DbType.String or DbType.StringFixedLength
                    or DbType.Xml => TextOid,
                _ => throw new NotSupportedException($"DbType {dbType} does not have a BlueTusk parameter encoder yet."),
            };
        }

        return value switch
        {
            null => throw new InvalidOperationException(
                "A null parameter requires DbType, PostgreSqlTypeOid, or PostgreSqlTypeName so PostgreSQL can determine its type."),
            bool => BooleanOid,
            sbyte or byte or short or ushort => Int2Oid,
            int => Int4Oid,
            uint => OidOid,
            long or ulong => Int8Oid,
            float => Float4Oid,
            double => Float8Oid,
            decimal => NumericOid,
            BlueTuskNumeric => NumericOid,
            BlueTuskTupleId => TidOid,
            BlueTuskInterval => IntervalOid,
            BlueTuskTimeWithTimeZone => TimeWithTimeZoneOid,
            BlueTuskBitString => VarbitOid,
            BlueTuskLogSequenceNumber => PgLsnOid,
            BlueTuskPoint => PointOid,
            BlueTuskLineSegment => LineSegmentOid,
            BlueTuskPath => PathOid,
            BlueTuskBox => BoxOid,
            BlueTuskPolygon => PolygonOid,
            BlueTuskLine => LineOid,
            BlueTuskCircle => CircleOid,
            BlueTuskMoney => MoneyOid,
            BlueTuskTextSearchVector => TextSearchVectorOid,
            BlueTuskTextSearchQuery => TextSearchQueryOid,
            BlueTuskNetworkAddress network => network.IsCidr ? CidrOid : InetOid,
            BlueTuskMacAddress8 => Macaddr8Oid,
            BlueTuskMacAddress => MacaddrOid,
            Guid => UuidOid,
            byte[] or ReadOnlyMemory<byte> or Memory<byte> => ByteaOid,
            DateOnly => DateOid,
            TimeOnly or TimeSpan => TimeOid,
            DateTime => TimestampOid,
            DateTimeOffset => TimestampWithTimeZoneOid,
            string or char => TextOid,
            _ when types?.TryGetType(value.GetType(), out var type, out _) == true => type!.Id.Oid,
            _ => throw new NotSupportedException(
                $"CLR type {value.GetType().FullName} does not have a BlueTusk parameter encoder yet. " +
                "Register a unique runtime codec or set PostgreSqlTypeOid and supply a string or byte payload."),
        };
    }

    private static uint ResolveTypeName(string postgreSqlTypeName, BlueTuskTypeRegistry? types)
    {
        if (types is null)
        {
            throw new InvalidOperationException(
                "PostgreSqlTypeName requires an open BlueTusk connection with a loaded PostgreSQL type catalogue.");
        }

        var typeName = postgreSqlTypeName.AsSpan().Trim().ToString();
        var isArray = false;
        while (typeName.EndsWith("[]", StringComparison.Ordinal))
        {
            isArray = true;
            typeName = typeName[..^2].TrimEnd();
        }

        var parsedName = BlueTuskTypeName.Parse(typeName);
        if (!types.TryGetType(parsedName, out var type, out _))
        {
            throw new InvalidOperationException(
                $"PostgreSQL type {parsedName} is not present in the loaded type catalogue.");
        }

        if (!isArray)
        {
            return type!.Id.Oid;
        }

        if (type!.ArrayType is not { } arrayType)
        {
            throw new InvalidOperationException($"PostgreSQL type {parsedName} does not have an array type.");
        }

        return arrayType.Oid;
    }

    private static BlueTuskExtendedQueryParameter EncodeValue(
        uint typeOid,
        object value,
        BlueTuskTypeRegistry? types,
        ref byte[]? reusableBuffer,
        bool rentBuffer) => typeOid switch
        {
            BooleanOid => BinaryBoolean(
                typeOid,
                value is bool typed ? typed : Convert.ToBoolean(value, CultureInfo.InvariantCulture),
                ref reusableBuffer,
                rentBuffer),
            Int2Oid => BinaryInt16(
                typeOid,
                value is short typed ? typed : Convert.ToInt16(value, CultureInfo.InvariantCulture),
                ref reusableBuffer,
                rentBuffer),
            Int4Oid => BinaryInt32(
                typeOid,
                value is int typed ? typed : Convert.ToInt32(value, CultureInfo.InvariantCulture),
                ref reusableBuffer,
                rentBuffer),
            OidOid => BinaryUInt32(
                typeOid,
                value is uint typed ? typed : Convert.ToUInt32(value, CultureInfo.InvariantCulture),
                ref reusableBuffer,
                rentBuffer),
            Int8Oid => BinaryInt64(
                typeOid,
                value is long typed ? typed : Convert.ToInt64(value, CultureInfo.InvariantCulture),
                ref reusableBuffer,
                rentBuffer),
            Float4Oid => BinarySingle(
                typeOid,
                value is float typed ? typed : Convert.ToSingle(value, CultureInfo.InvariantCulture),
                ref reusableBuffer,
                rentBuffer),
            Float8Oid => BinaryDouble(
                typeOid,
                value is double typed ? typed : Convert.ToDouble(value, CultureInfo.InvariantCulture),
                ref reusableBuffer,
                rentBuffer),
            PointOid => EncodeBinary(
                typeOid,
                new BlueTuskPointCodec(),
                BlueTuskBuiltInTypes.Point,
                GetValue<BlueTuskPoint>(value),
                16,
                ref reusableBuffer,
                rentBuffer),
            LineSegmentOid => EncodeBinary(
                typeOid,
                new BlueTuskLineSegmentCodec(),
                BlueTuskBuiltInTypes.LineSegment,
                GetValue<BlueTuskLineSegment>(value),
                32,
                ref reusableBuffer,
                rentBuffer),
            PathOid => EncodePath(typeOid, GetValue<BlueTuskPath>(value)),
            BoxOid => EncodeBinary(
                typeOid,
                new BlueTuskBoxCodec(),
                BlueTuskBuiltInTypes.Box,
                GetValue<BlueTuskBox>(value),
                32,
                ref reusableBuffer,
                rentBuffer),
            PolygonOid => EncodePolygon(typeOid, GetValue<BlueTuskPolygon>(value)),
            LineOid => EncodeBinary(
                typeOid,
                new BlueTuskLineCodec(),
                BlueTuskBuiltInTypes.Line,
                GetValue<BlueTuskLine>(value),
                24,
                ref reusableBuffer,
                rentBuffer),
            CircleOid => EncodeBinary(
                typeOid,
                new BlueTuskCircleCodec(),
                BlueTuskBuiltInTypes.Circle,
                GetValue<BlueTuskCircle>(value),
                24,
                ref reusableBuffer,
                rentBuffer),
            MoneyOid => EncodeMoney(typeOid, GetValue<BlueTuskMoney>(value), types),
            CidrOid or InetOid => EncodeNetworkAddress(typeOid, GetValue<BlueTuskNetworkAddress>(value)),
            Macaddr8Oid => EncodeBinary(
                typeOid,
                new BlueTuskMacAddress8Codec(),
                BlueTuskBuiltInTypes.Macaddr8,
                GetValue<BlueTuskMacAddress8>(value),
                sizeof(ulong),
                ref reusableBuffer,
                rentBuffer),
            MacaddrOid => EncodeBinary(
                typeOid,
                new BlueTuskMacAddressCodec(),
                BlueTuskBuiltInTypes.Macaddr,
                GetValue<BlueTuskMacAddress>(value),
                6,
                ref reusableBuffer,
                rentBuffer),
            ByteaOid => Binary(typeOid, GetBytes(value)),
            NumericOid => EncodeNumeric(typeOid, value, ref reusableBuffer, rentBuffer),
            UuidOid => EncodeBinary(
                typeOid,
                new BlueTuskGuidCodec(),
                BlueTuskBuiltInTypes.Uuid,
                GetGuid(value),
                16,
                ref reusableBuffer,
                rentBuffer),
            DateOid => EncodeBinary(
                typeOid,
                new BlueTuskDateCodec(),
                BlueTuskBuiltInTypes.Date,
                GetDate(value),
                sizeof(int),
                ref reusableBuffer,
                rentBuffer),
            TimeOid => EncodeBinary(
                typeOid,
                new BlueTuskTimeCodec(),
                BlueTuskBuiltInTypes.Time,
                GetTime(value),
                sizeof(long),
                ref reusableBuffer,
                rentBuffer),
            TimestampOid => EncodeBinary(
                typeOid,
                new BlueTuskTimestampCodec(),
                BlueTuskBuiltInTypes.Timestamp,
                GetDateTime(value),
                sizeof(long),
                ref reusableBuffer,
                rentBuffer),
            TimestampWithTimeZoneOid => EncodeBinary(
                typeOid,
                new BlueTuskTimestampWithTimeZoneCodec(),
                BlueTuskBuiltInTypes.TimestampWithTimeZone,
                GetDateTimeOffset(value),
                sizeof(long),
                ref reusableBuffer,
                rentBuffer),
            TidOid => EncodeBinary(
                typeOid,
                new BlueTuskTupleIdCodec(),
                BlueTuskBuiltInTypes.Tid,
                GetValue<BlueTuskTupleId>(value),
                6,
                ref reusableBuffer,
                rentBuffer),
            IntervalOid => EncodeBinary(
                typeOid,
                new BlueTuskIntervalCodec(),
                BlueTuskBuiltInTypes.Interval,
                GetValue<BlueTuskInterval>(value),
                16,
                ref reusableBuffer,
                rentBuffer),
            TimeWithTimeZoneOid => EncodeBinary(
                typeOid,
                new BlueTuskTimeWithTimeZoneCodec(),
                BlueTuskBuiltInTypes.TimeWithTimeZone,
                GetValue<BlueTuskTimeWithTimeZone>(value),
                12,
                ref reusableBuffer,
                rentBuffer),
            BitOid or VarbitOid => EncodeBitString(typeOid, GetValue<BlueTuskBitString>(value)),
            PgLsnOid => EncodeBinary(
                typeOid,
                new BlueTuskLogSequenceNumberCodec(),
                BlueTuskBuiltInTypes.PgLsn,
                GetValue<BlueTuskLogSequenceNumber>(value),
                sizeof(ulong),
                ref reusableBuffer,
                rentBuffer),
            TextSearchVectorOid => EncodeTextSearchVector(typeOid, GetValue<BlueTuskTextSearchVector>(value)),
            TextSearchQueryOid => EncodeTextSearchQuery(typeOid, GetValue<BlueTuskTextSearchQuery>(value)),
            TextOid => Text(
                typeOid,
                Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
                ref reusableBuffer,
                rentBuffer),
            _ => EncodeFallback(
                typeOid,
                value,
                types,
                ref reusableBuffer,
                rentBuffer),
        };

    private static BlueTuskExtendedQueryParameter BinaryBoolean(
        uint typeOid,
        bool value,
        ref byte[]? reusableBuffer,
        bool rentBuffer)
    {
        var bytes = GetReusableBuffer(ref reusableBuffer, sizeof(byte), rentBuffer);
        bytes[0] = value ? (byte)1 : (byte)0;
        return Binary(typeOid, bytes.AsMemory(0, sizeof(byte)));
    }

    private static BlueTuskExtendedQueryParameter BinaryInt16(
        uint typeOid,
        short value,
        ref byte[]? reusableBuffer,
        bool rentBuffer)
    {
        var bytes = GetReusableBuffer(ref reusableBuffer, sizeof(short), rentBuffer);
        BinaryPrimitives.WriteInt16BigEndian(bytes, value);
        return Binary(typeOid, bytes.AsMemory(0, sizeof(short)));
    }

    private static BlueTuskExtendedQueryParameter BinaryInt32(
        uint typeOid,
        int value,
        ref byte[]? reusableBuffer,
        bool rentBuffer)
    {
        var bytes = GetReusableBuffer(ref reusableBuffer, sizeof(int), rentBuffer);
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return Binary(typeOid, bytes.AsMemory(0, sizeof(int)));
    }

    private static BlueTuskExtendedQueryParameter BinaryUInt32(
        uint typeOid,
        uint value,
        ref byte[]? reusableBuffer,
        bool rentBuffer)
    {
        var bytes = GetReusableBuffer(ref reusableBuffer, sizeof(uint), rentBuffer);
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return Binary(typeOid, bytes.AsMemory(0, sizeof(uint)));
    }

    private static BlueTuskExtendedQueryParameter BinaryInt64(
        uint typeOid,
        long value,
        ref byte[]? reusableBuffer,
        bool rentBuffer)
    {
        var bytes = GetReusableBuffer(ref reusableBuffer, sizeof(long), rentBuffer);
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        return Binary(typeOid, bytes.AsMemory(0, sizeof(long)));
    }

    private static BlueTuskExtendedQueryParameter BinaryInt64(uint typeOid, long value)
    {
        byte[]? reusableBuffer = null;
        return BinaryInt64(typeOid, value, ref reusableBuffer, rentBuffer: false);
    }

    private static BlueTuskExtendedQueryParameter BinarySingle(
        uint typeOid,
        float value,
        ref byte[]? reusableBuffer,
        bool rentBuffer) =>
        BinaryInt32(
            typeOid,
            BitConverter.SingleToInt32Bits(value),
            ref reusableBuffer,
            rentBuffer);

    private static BlueTuskExtendedQueryParameter BinaryDouble(
        uint typeOid,
        double value,
        ref byte[]? reusableBuffer,
        bool rentBuffer) =>
        BinaryInt64(
            typeOid,
            BitConverter.DoubleToInt64Bits(value),
            ref reusableBuffer,
            rentBuffer);

    private static byte[] GetReusableBuffer(
        ref byte[]? reusableBuffer,
        int length,
        bool rentBuffer)
    {
        if (reusableBuffer is null || reusableBuffer.Length < length)
        {
            if (rentBuffer && reusableBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(reusableBuffer, clearArray: true);
            }

            reusableBuffer = rentBuffer
                ? ArrayPool<byte>.Shared.Rent(length)
                : new byte[length];
        }

        return reusableBuffer;
    }

    private static BlueTuskExtendedQueryParameter Binary(uint typeOid, ReadOnlyMemory<byte> value) =>
        new(typeOid, 1, value);

    private static BlueTuskExtendedQueryParameter Text(uint typeOid, string value) =>
        new(typeOid, 0, Encoding.UTF8.GetBytes(value));

    private static BlueTuskExtendedQueryParameter Text(
        uint typeOid,
        string value,
        ref byte[]? reusableBuffer,
        bool rentBuffer)
    {
        var length = Encoding.UTF8.GetByteCount(value);
        var bytes = GetReusableBuffer(ref reusableBuffer, length, rentBuffer);
        var written = Encoding.UTF8.GetBytes(value, bytes);
        return new BlueTuskExtendedQueryParameter(
            typeOid,
            0,
            bytes.AsMemory(0, written));
    }

    private static BlueTuskExtendedQueryParameter EncodeFallback(
        uint typeOid,
        object value,
        BlueTuskTypeRegistry? types,
        ref byte[]? reusableBuffer,
        bool rentBuffer)
    {
        var typeId = new BlueTuskTypeId(typeOid);
        if (types?.TryGetType(typeId, out var type) == true &&
            type is not null &&
            types.TryGetCodec(typeId, out var codec) &&
            codec is not null &&
            (codec.ClrType.IsInstanceOfType(value) ||
             type.Kind == BlueTuskTypeKind.Array && value is Array))
        {
            return EncodeRegistered(
                typeOid,
                type,
                codec,
                value,
                ref reusableBuffer,
                rentBuffer);
        }

        return value switch
        {
            string text => Text(typeOid, text, ref reusableBuffer, rentBuffer),
            byte[] bytes => Binary(typeOid, bytes),
            ReadOnlyMemory<byte> bytes => Binary(typeOid, bytes),
            Memory<byte> bytes => Binary(typeOid, bytes),
            _ => throw new NotSupportedException(
                $"PostgreSQL type OID {typeOid} requires a registered codec or string/byte payload."),
        };
    }

    private static BlueTuskExtendedQueryParameter EncodeRegistered(
        uint typeOid,
        BlueTuskTypeDescriptor type,
        IBlueTuskCodec codec,
        object value,
        ref byte[]? reusableBuffer,
        bool rentBuffer)
    {
        var format = codec is IBlueTuskWriteFormatSelector selector
            ? selector.GetPreferredWriteFormat(value, type)
            : BlueTuskDataFormat.Binary;
        if (format is not (BlueTuskDataFormat.Text or BlueTuskDataFormat.Binary))
        {
            throw new InvalidOperationException(
                $"The {type.QualifiedName} codec selected unsupported wire format {format}.");
        }

        if (!rentBuffer)
        {
            return EncodeRegisteredOwned(typeOid, type, codec, value, format);
        }

        var length = Math.Max(256, reusableBuffer?.Length ?? 0);
        while (true)
        {
            var buffer = GetReusableBuffer(ref reusableBuffer, length, rentBuffer);
            try
            {
                var writer = new BlueTuskWriter(buffer);
                codec.Write(ref writer, value, format, type);
                return new BlueTuskExtendedQueryParameter(
                    typeOid,
                    (short)format,
                    buffer.AsMemory(0, writer.WrittenCount));
            }
            catch (BlueTuskWriteBufferTooSmallException) when (length < Array.MaxLength)
            {
                length = length > Array.MaxLength / 2 ? Array.MaxLength : length * 2;
            }
        }
    }

    private static BlueTuskExtendedQueryParameter EncodeRegisteredOwned(
        uint typeOid,
        BlueTuskTypeDescriptor type,
        IBlueTuskCodec codec,
        object value,
        BlueTuskDataFormat format)
    {
        var length = 256;
        byte[]? temporary = null;
        try
        {
            while (true)
            {
                temporary = ArrayPool<byte>.Shared.Rent(length);
                try
                {
                    var writer = new BlueTuskWriter(temporary);
                    codec.Write(ref writer, value, format, type);
                    var owned = temporary.AsSpan(0, writer.WrittenCount).ToArray();
                    return new BlueTuskExtendedQueryParameter(
                        typeOid,
                        (short)format,
                        owned);
                }
                catch (BlueTuskWriteBufferTooSmallException) when (length < Array.MaxLength)
                {
                    ArrayPool<byte>.Shared.Return(temporary, clearArray: true);
                    temporary = null;
                    length = length > Array.MaxLength / 2 ? Array.MaxLength : length * 2;
                }
            }
        }
        finally
        {
            if (temporary is not null)
            {
                ArrayPool<byte>.Shared.Return(temporary, clearArray: true);
            }
        }
    }

    private static BlueTuskExtendedQueryParameter EncodeBinary<T>(
        uint typeOid,
        BlueTuskCodec<T> codec,
        BlueTuskTypeDescriptor type,
        T value,
        int length)
    {
        var bytes = new byte[length];
        var writer = new BlueTuskWriter(bytes);
        codec.WriteTyped(ref writer, value, BlueTuskDataFormat.Binary, type);
        if (writer.WrittenCount != length)
        {
            throw new InvalidOperationException(
                $"The {type.QualifiedName} parameter codec wrote {writer.WrittenCount} bytes; {length} were expected.");
        }

        return Binary(typeOid, bytes);
    }

    private static BlueTuskExtendedQueryParameter EncodeBinary<T>(
        uint typeOid,
        BlueTuskCodec<T> codec,
        BlueTuskTypeDescriptor type,
        T value,
        int length,
        ref byte[]? reusableBuffer,
        bool rentBuffer)
    {
        var bytes = GetReusableBuffer(ref reusableBuffer, length, rentBuffer);
        var writer = new BlueTuskWriter(bytes.AsSpan(0, length));
        codec.WriteTyped(ref writer, value, BlueTuskDataFormat.Binary, type);
        if (writer.WrittenCount != length)
        {
            throw new InvalidOperationException(
                $"The {type.QualifiedName} parameter codec wrote {writer.WrittenCount} bytes; {length} were expected.");
        }

        return Binary(typeOid, bytes.AsMemory(0, length));
    }

    private static BlueTuskExtendedQueryParameter EncodeNumeric(
        uint typeOid,
        object value,
        ref byte[]? reusableBuffer,
        bool rentBuffer)
    {
        var numeric = value is BlueTuskNumeric typed
            ? typed
            : (BlueTuskNumeric)Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        var codec = new BlueTuskNumericCodec();
        var maximumLength = BlueTuskNumericCodec.GetMaximumBinarySize(numeric);
        var buffer = GetReusableBuffer(ref reusableBuffer, maximumLength, rentBuffer);
        var writer = new BlueTuskWriter(buffer.AsSpan(0, maximumLength));
        codec.WriteTyped(
            ref writer,
            numeric,
            BlueTuskDataFormat.Binary,
            BlueTuskBuiltInTypes.Numeric);
        return Binary(typeOid, buffer.AsMemory(0, writer.WrittenCount));
    }

    private static BlueTuskExtendedQueryParameter EncodeBitString(uint typeOid, BlueTuskBitString value)
    {
        var bytes = new byte[sizeof(int) + ((value.Length + 7) / 8)];
        var writer = new BlueTuskWriter(bytes);
        new BlueTuskBitStringCodec().WriteTyped(
            ref writer,
            value,
            BlueTuskDataFormat.Binary,
            typeOid == BitOid ? BlueTuskBuiltInTypes.Bit : BlueTuskBuiltInTypes.Varbit);
        return Binary(typeOid, bytes);
    }

    private static BlueTuskExtendedQueryParameter EncodeNetworkAddress(
        uint typeOid,
        BlueTuskNetworkAddress value)
    {
        var bytes = new byte[4 + value.Address.GetAddressBytes().Length];
        var writer = new BlueTuskWriter(bytes);
        if (typeOid == CidrOid)
        {
            new BlueTuskCidrCodec().WriteTyped(
                ref writer,
                value,
                BlueTuskDataFormat.Binary,
                BlueTuskBuiltInTypes.Cidr);
        }
        else
        {
            new BlueTuskInetCodec().WriteTyped(
                ref writer,
                value,
                BlueTuskDataFormat.Binary,
                BlueTuskBuiltInTypes.Inet);
        }

        return Binary(typeOid, bytes);
    }

    private static BlueTuskExtendedQueryParameter EncodePath(uint typeOid, BlueTuskPath value) =>
        EncodeBinary(
            typeOid,
            new BlueTuskPathCodec(),
            BlueTuskBuiltInTypes.Path,
            value,
            checked(5 + (value.Count * 16)));

    private static BlueTuskExtendedQueryParameter EncodePolygon(uint typeOid, BlueTuskPolygon value) =>
        EncodeBinary(
            typeOid,
            new BlueTuskPolygonCodec(),
            BlueTuskBuiltInTypes.Polygon,
            value,
            checked(4 + (value.Count * 16)));

    private static BlueTuskExtendedQueryParameter EncodeTextSearchVector(
        uint typeOid,
        BlueTuskTextSearchVector value) =>
        EncodeBinary(
            typeOid,
            new BlueTuskTextSearchVectorCodec(),
            BlueTuskBuiltInTypes.TextSearchVector,
            value,
            BlueTuskTextSearchVectorCodec.GetBinarySize(value));

    private static BlueTuskExtendedQueryParameter EncodeTextSearchQuery(
        uint typeOid,
        BlueTuskTextSearchQuery value) =>
        EncodeBinary(
            typeOid,
            new BlueTuskTextSearchQueryCodec(),
            BlueTuskBuiltInTypes.TextSearchQuery,
            value,
            BlueTuskTextSearchQueryCodec.GetBinarySize(value));

    private static BlueTuskExtendedQueryParameter EncodeMoney(
        uint typeOid,
        BlueTuskMoney value,
        BlueTuskTypeRegistry? types)
    {
        if (types is not null &&
            types.TryGetCodec(BlueTuskBuiltInTypes.Money.Id, out var registered) &&
            registered is BlueTuskMoneyCodec codec &&
            value.FractionalDigits != codec.FractionalDigits)
        {
            throw new InvalidOperationException(
                $"The money parameter has {value.FractionalDigits} fractional digits, but PostgreSQL locale " +
                $"'{codec.Locale}' uses {codec.FractionalDigits}.");
        }

        return BinaryInt64(typeOid, value.UnscaledValue);
    }

    private static ReadOnlyMemory<byte> GetBytes(object value) => value switch
    {
        byte[] bytes => bytes,
        ReadOnlyMemory<byte> bytes => bytes,
        Memory<byte> bytes => bytes,
        _ => throw new InvalidCastException($"Value of type {value.GetType().FullName} cannot be encoded as bytea."),
    };

    private static Guid GetGuid(object value) => value is Guid guid ? guid : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!);

    private static DateOnly GetDate(object value) => value switch
    {
        DateOnly date => date,
        DateTime dateTime => DateOnly.FromDateTime(dateTime),
        _ => DateOnly.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture),
    };

    private static TimeSpan GetTime(object value) => value switch
    {
        TimeOnly time => time.ToTimeSpan(),
        TimeSpan timeSpan => timeSpan,
        _ => TimeOnly.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture).ToTimeSpan(),
    };

    private static DateTime GetDateTime(object value) => value is DateTime dateTime
        ? dateTime
        : Convert.ToDateTime(value, CultureInfo.InvariantCulture);

    private static DateTimeOffset GetDateTimeOffset(object value) => value is DateTimeOffset dateTimeOffset
        ? dateTimeOffset
        : DateTimeOffset.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);

    private static T GetValue<T>(object value) => value is T typed
        ? typed
        : throw new InvalidCastException($"Value of type {value.GetType().FullName} cannot be encoded as {typeof(T).FullName}.");
}
