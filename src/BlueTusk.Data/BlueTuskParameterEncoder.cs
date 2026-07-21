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
        var encoded = new BlueTuskExtendedQueryParameter[parameters.Count];
        for (var index = 0; index < parameters.Count; index++)
        {
            encoded[index] = Encode(parameters.Items[index], types);
        }

        return encoded;
    }

    public static BlueTuskExtendedQueryParameter Encode(
        BlueTuskParameter parameter,
        BlueTuskTypeRegistry? types = null)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        var value = parameter.Value is DBNull ? null : parameter.Value;
        var typeOid = parameter.PostgreSqlTypeOid ?? ResolveTypeOid(parameter.DbType, value);
        if (typeOid == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(parameter),
                "PostgreSqlTypeOid must be a non-zero PostgreSQL type OID.");
        }

        return value is null
            ? new BlueTuskExtendedQueryParameter(typeOid, 0, null)
            : EncodeValue(typeOid, value, types);
    }

    private static uint ResolveTypeOid(DbType dbType, object? value)
    {
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
                "A null parameter requires DbType or PostgreSqlTypeOid so PostgreSQL can determine its type."),
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
            _ => throw new NotSupportedException(
                $"CLR type {value.GetType().FullName} does not have a BlueTusk parameter encoder yet. " +
                "Set PostgreSqlTypeOid and supply a string or byte payload for a custom type."),
        };
    }

    private static BlueTuskExtendedQueryParameter EncodeValue(
        uint typeOid,
        object value,
        BlueTuskTypeRegistry? types) => typeOid switch
        {
            BooleanOid => Binary(
                typeOid,
                new byte[] { (byte)(Convert.ToBoolean(value, CultureInfo.InvariantCulture) ? 1 : 0) }),
            Int2Oid => BinaryInt16(typeOid, Convert.ToInt16(value, CultureInfo.InvariantCulture)),
            Int4Oid => BinaryInt32(typeOid, Convert.ToInt32(value, CultureInfo.InvariantCulture)),
            OidOid => BinaryUInt32(typeOid, Convert.ToUInt32(value, CultureInfo.InvariantCulture)),
            Int8Oid => BinaryInt64(typeOid, Convert.ToInt64(value, CultureInfo.InvariantCulture)),
            Float4Oid => BinarySingle(typeOid, Convert.ToSingle(value, CultureInfo.InvariantCulture)),
            Float8Oid => BinaryDouble(typeOid, Convert.ToDouble(value, CultureInfo.InvariantCulture)),
            PointOid => EncodeBinary(
                typeOid,
                new BlueTuskPointCodec(),
                BlueTuskBuiltInTypes.Point,
                GetValue<BlueTuskPoint>(value),
                16),
            LineSegmentOid => EncodeBinary(
                typeOid,
                new BlueTuskLineSegmentCodec(),
                BlueTuskBuiltInTypes.LineSegment,
                GetValue<BlueTuskLineSegment>(value),
                32),
            PathOid => EncodePath(typeOid, GetValue<BlueTuskPath>(value)),
            BoxOid => EncodeBinary(
                typeOid,
                new BlueTuskBoxCodec(),
                BlueTuskBuiltInTypes.Box,
                GetValue<BlueTuskBox>(value),
                32),
            PolygonOid => EncodePolygon(typeOid, GetValue<BlueTuskPolygon>(value)),
            LineOid => EncodeBinary(
                typeOid,
                new BlueTuskLineCodec(),
                BlueTuskBuiltInTypes.Line,
                GetValue<BlueTuskLine>(value),
                24),
            CircleOid => EncodeBinary(
                typeOid,
                new BlueTuskCircleCodec(),
                BlueTuskBuiltInTypes.Circle,
                GetValue<BlueTuskCircle>(value),
                24),
            MoneyOid => EncodeMoney(typeOid, GetValue<BlueTuskMoney>(value), types),
            CidrOid or InetOid => EncodeNetworkAddress(typeOid, GetValue<BlueTuskNetworkAddress>(value)),
            Macaddr8Oid => EncodeBinary(
                typeOid,
                new BlueTuskMacAddress8Codec(),
                BlueTuskBuiltInTypes.Macaddr8,
                GetValue<BlueTuskMacAddress8>(value),
                sizeof(ulong)),
            MacaddrOid => EncodeBinary(
                typeOid,
                new BlueTuskMacAddressCodec(),
                BlueTuskBuiltInTypes.Macaddr,
                GetValue<BlueTuskMacAddress>(value),
                6),
            ByteaOid => Binary(typeOid, GetBytes(value)),
            NumericOid => EncodeNumeric(typeOid, value),
            UuidOid => EncodeBinary(
                typeOid,
                new BlueTuskGuidCodec(),
                BlueTuskBuiltInTypes.Uuid,
                GetGuid(value),
                16),
            DateOid => EncodeBinary(
                typeOid,
                new BlueTuskDateCodec(),
                BlueTuskBuiltInTypes.Date,
                GetDate(value),
                sizeof(int)),
            TimeOid => EncodeBinary(
                typeOid,
                new BlueTuskTimeCodec(),
                BlueTuskBuiltInTypes.Time,
                GetTime(value),
                sizeof(long)),
            TimestampOid => EncodeBinary(
                typeOid,
                new BlueTuskTimestampCodec(),
                BlueTuskBuiltInTypes.Timestamp,
                GetDateTime(value),
                sizeof(long)),
            TimestampWithTimeZoneOid => EncodeBinary(
                typeOid,
                new BlueTuskTimestampWithTimeZoneCodec(),
                BlueTuskBuiltInTypes.TimestampWithTimeZone,
                GetDateTimeOffset(value),
                sizeof(long)),
            TidOid => EncodeBinary(
                typeOid,
                new BlueTuskTupleIdCodec(),
                BlueTuskBuiltInTypes.Tid,
                GetValue<BlueTuskTupleId>(value),
                6),
            IntervalOid => EncodeBinary(
                typeOid,
                new BlueTuskIntervalCodec(),
                BlueTuskBuiltInTypes.Interval,
                GetValue<BlueTuskInterval>(value),
                16),
            TimeWithTimeZoneOid => EncodeBinary(
                typeOid,
                new BlueTuskTimeWithTimeZoneCodec(),
                BlueTuskBuiltInTypes.TimeWithTimeZone,
                GetValue<BlueTuskTimeWithTimeZone>(value),
                12),
            BitOid or VarbitOid => EncodeBitString(typeOid, GetValue<BlueTuskBitString>(value)),
            PgLsnOid => EncodeBinary(
                typeOid,
                new BlueTuskLogSequenceNumberCodec(),
                BlueTuskBuiltInTypes.PgLsn,
                GetValue<BlueTuskLogSequenceNumber>(value),
                sizeof(ulong)),
            TextSearchVectorOid => EncodeTextSearchVector(typeOid, GetValue<BlueTuskTextSearchVector>(value)),
            TextSearchQueryOid => EncodeTextSearchQuery(typeOid, GetValue<BlueTuskTextSearchQuery>(value)),
            TextOid => Text(typeOid, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
            _ when value is string text => Text(typeOid, text),
            _ when value is byte[] bytes => Binary(typeOid, bytes),
            _ when value is ReadOnlyMemory<byte> bytes => Binary(typeOid, bytes),
            _ when value is Memory<byte> bytes => Binary(typeOid, bytes),
            _ => throw new NotSupportedException(
                $"PostgreSQL type OID {typeOid} requires a string or byte payload when no built-in encoder is available."),
        };

    private static BlueTuskExtendedQueryParameter BinaryInt16(uint typeOid, short value)
    {
        var bytes = new byte[sizeof(short)];
        BinaryPrimitives.WriteInt16BigEndian(bytes, value);
        return Binary(typeOid, bytes);
    }

    private static BlueTuskExtendedQueryParameter BinaryInt32(uint typeOid, int value)
    {
        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return Binary(typeOid, bytes);
    }

    private static BlueTuskExtendedQueryParameter BinaryUInt32(uint typeOid, uint value)
    {
        var bytes = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return Binary(typeOid, bytes);
    }

    private static BlueTuskExtendedQueryParameter BinaryInt64(uint typeOid, long value)
    {
        var bytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        return Binary(typeOid, bytes);
    }

    private static BlueTuskExtendedQueryParameter BinarySingle(uint typeOid, float value) =>
        BinaryInt32(typeOid, BitConverter.SingleToInt32Bits(value));

    private static BlueTuskExtendedQueryParameter BinaryDouble(uint typeOid, double value) =>
        BinaryInt64(typeOid, BitConverter.DoubleToInt64Bits(value));

    private static BlueTuskExtendedQueryParameter Binary(uint typeOid, ReadOnlyMemory<byte> value) =>
        new(typeOid, 1, value);

    private static BlueTuskExtendedQueryParameter Text(uint typeOid, string value) =>
        new(typeOid, 0, Encoding.UTF8.GetBytes(value));

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

    private static BlueTuskExtendedQueryParameter EncodeNumeric(uint typeOid, object value)
    {
        var numeric = value is BlueTuskNumeric typed
            ? typed
            : (BlueTuskNumeric)Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        var codec = new BlueTuskNumericCodec();
        var bytes = new byte[BlueTuskNumericCodec.GetMaximumBinarySize(numeric)];
        var writer = new BlueTuskWriter(bytes);
        codec.WriteTyped(
            ref writer,
            numeric,
            BlueTuskDataFormat.Binary,
            BlueTuskBuiltInTypes.Numeric);
        if (writer.WrittenCount != bytes.Length)
        {
            Array.Resize(ref bytes, writer.WrittenCount);
        }

        return Binary(typeOid, bytes);
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
