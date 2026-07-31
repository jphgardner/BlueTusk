using System.Data;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Storage.Internal;

internal sealed class BlueTuskTypeMappingSource : RelationalTypeMappingSource
{
    private static readonly RelationalTypeMapping Bool = new BoolTypeMapping("boolean", DbType.Boolean);
    private static readonly RelationalTypeMapping Byte = new ByteTypeMapping("smallint", DbType.Byte);
    private static readonly RelationalTypeMapping Short = new ShortTypeMapping("smallint", DbType.Int16);
    private static readonly RelationalTypeMapping Int = new IntTypeMapping("integer", DbType.Int32);
    private static readonly RelationalTypeMapping Long = new LongTypeMapping("bigint", DbType.Int64);
    private static readonly RelationalTypeMapping Float = new FloatTypeMapping("real", DbType.Single);
    private static readonly RelationalTypeMapping Double = new DoubleTypeMapping("double precision", DbType.Double);
    private static readonly RelationalTypeMapping Decimal = new DecimalTypeMapping("numeric", DbType.Decimal);
    private static readonly RelationalTypeMapping String = new StringTypeMapping("text", DbType.String);
    private static readonly RelationalTypeMapping Char = new CharTypeMapping("character(1)", DbType.StringFixedLength);
    private static readonly RelationalTypeMapping Bytes = new ByteArrayTypeMapping("bytea", DbType.Binary);
    private static readonly RelationalTypeMapping Guid = new GuidTypeMapping("uuid", DbType.Guid);
    private static readonly RelationalTypeMapping DateTime =
        new DateTimeTypeMapping("timestamp with time zone", DbType.DateTime);
    private static readonly RelationalTypeMapping DateTimeOffset =
        new DateTimeOffsetTypeMapping("timestamp with time zone", DbType.DateTimeOffset);
    private static readonly RelationalTypeMapping DateOnly = new DateOnlyTypeMapping("date", DbType.Date);
    private static readonly RelationalTypeMapping TimeOnly = new TimeOnlyTypeMapping("time without time zone", DbType.Time);
    private static readonly RelationalTypeMapping TimeSpan = new TimeSpanTypeMapping("interval", DbType.Time);

    private static readonly Dictionary<Type, RelationalTypeMapping> ClrMappings =
        new Dictionary<Type, RelationalTypeMapping>
        {
            [typeof(bool)] = Bool,
            [typeof(byte)] = Byte,
            [typeof(short)] = Short,
            [typeof(int)] = Int,
            [typeof(long)] = Long,
            [typeof(float)] = Float,
            [typeof(double)] = Double,
            [typeof(decimal)] = Decimal,
            [typeof(string)] = String,
            [typeof(char)] = Char,
            [typeof(byte[])] = Bytes,
            [typeof(Guid)] = Guid,
            [typeof(DateTime)] = DateTime,
            [typeof(DateTimeOffset)] = DateTimeOffset,
            [typeof(DateOnly)] = DateOnly,
            [typeof(TimeOnly)] = TimeOnly,
            [typeof(TimeSpan)] = TimeSpan,
        };

    private static readonly Dictionary<string, RelationalTypeMapping> StoreMappings =
        new Dictionary<string, RelationalTypeMapping>(StringComparer.OrdinalIgnoreCase)
        {
            ["bool"] = Bool,
            ["boolean"] = Bool,
            ["int2"] = Short,
            ["smallint"] = Short,
            ["int4"] = Int,
            ["integer"] = Int,
            ["int8"] = Long,
            ["bigint"] = Long,
            ["float4"] = Float,
            ["real"] = Float,
            ["float8"] = Double,
            ["double precision"] = Double,
            ["decimal"] = Decimal,
            ["numeric"] = Decimal,
            ["text"] = String,
            ["varchar"] = String,
            ["character varying"] = String,
            ["char"] = String,
            ["character"] = String,
            ["bytea"] = Bytes,
            ["uuid"] = Guid,
            ["timestamp"] = DateTime,
            ["timestamp without time zone"] = DateTime,
            ["timestamptz"] = DateTimeOffset,
            ["timestamp with time zone"] = DateTimeOffset,
            ["date"] = DateOnly,
            ["time"] = TimeOnly,
            ["time without time zone"] = TimeOnly,
            ["interval"] = TimeSpan,
        };

    public BlueTuskTypeMappingSource(
        TypeMappingSourceDependencies dependencies,
        RelationalTypeMappingSourceDependencies relationalDependencies)
        : base(dependencies, relationalDependencies)
    {
    }

    protected override RelationalTypeMapping? FindMapping(in RelationalTypeMappingInfo mappingInfo)
    {
        if (mappingInfo.StoreTypeNameBase is { } storeTypeName
            && StoreMappings.TryGetValue(storeTypeName, out var storeMapping))
        {
            return storeMapping;
        }

        if (mappingInfo.ClrType is { } clrType
            && ClrMappings.TryGetValue(Nullable.GetUnderlyingType(clrType) ?? clrType, out var clrMapping))
        {
            return clrMapping;
        }

        return base.FindMapping(mappingInfo);
    }
}
