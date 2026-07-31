using System.Data;
using BlueTusk.TypeSystem;
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
        new DateTimeTypeMapping("timestamp without time zone", DbType.DateTime);
    private static readonly RelationalTypeMapping DateTimeOffset =
        new DateTimeOffsetTypeMapping("timestamp with time zone", DbType.DateTimeOffset);
    private static readonly RelationalTypeMapping DateOnly = new DateOnlyTypeMapping("date", DbType.Date);
    private static readonly RelationalTypeMapping TimeOnly = new TimeOnlyTypeMapping("time without time zone", DbType.Time);
    private static readonly RelationalTypeMapping TimeSpan = new BlueTuskIntervalTypeMapping();

    private static readonly RelationalTypeMapping Json = Native("json", typeof(string), BlueTuskBuiltInTypes.Json);
    private static readonly RelationalTypeMapping Jsonb = Native("jsonb", typeof(string), BlueTuskBuiltInTypes.Jsonb);
    private static readonly RelationalTypeMapping Xml = Native("xml", typeof(string), BlueTuskBuiltInTypes.Xml);
    private static readonly RelationalTypeMapping Name = Native("name", typeof(string), BlueTuskBuiltInTypes.Name);

    private static readonly RelationalTypeMapping NativeInterval =
        Native("interval", typeof(BlueTuskInterval), BlueTuskBuiltInTypes.Interval);
    private static readonly RelationalTypeMapping TimeWithTimeZone =
        Native("time with time zone", typeof(BlueTuskTimeWithTimeZone), BlueTuskBuiltInTypes.TimeWithTimeZone);
    private static readonly RelationalTypeMapping Bit =
        Native("bit", typeof(BlueTuskBitString), BlueTuskBuiltInTypes.Bit);
    private static readonly RelationalTypeMapping Varbit =
        Native("bit varying", typeof(BlueTuskBitString), BlueTuskBuiltInTypes.Varbit);
    private static readonly RelationalTypeMapping Numeric =
        Native("numeric", typeof(BlueTuskNumeric), BlueTuskBuiltInTypes.Numeric);
    private static readonly RelationalTypeMapping Tid =
        Native("tid", typeof(BlueTuskTupleId), BlueTuskBuiltInTypes.Tid);
    private static readonly RelationalTypeMapping PgLsn =
        Native("pg_lsn", typeof(BlueTuskLogSequenceNumber), BlueTuskBuiltInTypes.PgLsn);

    private static readonly RelationalTypeMapping Inet =
        Native("inet", typeof(BlueTuskNetworkAddress), BlueTuskBuiltInTypes.Inet);
    private static readonly RelationalTypeMapping Cidr =
        Native("cidr", typeof(BlueTuskNetworkAddress), BlueTuskBuiltInTypes.Cidr);
    private static readonly RelationalTypeMapping Macaddr =
        Native("macaddr", typeof(BlueTuskMacAddress), BlueTuskBuiltInTypes.Macaddr);
    private static readonly RelationalTypeMapping Macaddr8 =
        Native("macaddr8", typeof(BlueTuskMacAddress8), BlueTuskBuiltInTypes.Macaddr8);

    private static readonly RelationalTypeMapping Point =
        Native("point", typeof(BlueTuskPoint), BlueTuskBuiltInTypes.Point);
    private static readonly RelationalTypeMapping Line =
        Native("line", typeof(BlueTuskLine), BlueTuskBuiltInTypes.Line);
    private static readonly RelationalTypeMapping LineSegment =
        Native("lseg", typeof(BlueTuskLineSegment), BlueTuskBuiltInTypes.LineSegment);
    private static readonly RelationalTypeMapping Box =
        Native("box", typeof(BlueTuskBox), BlueTuskBuiltInTypes.Box);
    private static readonly RelationalTypeMapping Path =
        Native("path", typeof(BlueTuskPath), BlueTuskBuiltInTypes.Path);
    private static readonly RelationalTypeMapping Polygon =
        Native("polygon", typeof(BlueTuskPolygon), BlueTuskBuiltInTypes.Polygon);
    private static readonly RelationalTypeMapping Circle =
        Native("circle", typeof(BlueTuskCircle), BlueTuskBuiltInTypes.Circle);

    private static readonly RelationalTypeMapping Money =
        Native("money", typeof(BlueTuskMoney), BlueTuskBuiltInTypes.Money);
    private static readonly RelationalTypeMapping TextSearchVector =
        Native("tsvector", typeof(BlueTuskTextSearchVector), BlueTuskBuiltInTypes.TextSearchVector);
    private static readonly RelationalTypeMapping TextSearchQuery =
        Native("tsquery", typeof(BlueTuskTextSearchQuery), BlueTuskBuiltInTypes.TextSearchQuery);
    private static readonly RelationalTypeMapping JsonPath =
        Native("jsonpath", typeof(BlueTuskJsonPath), BlueTuskBuiltInTypes.JsonPath);

    private static readonly RelationalTypeMapping Oid = Native("oid", typeof(uint), BlueTuskBuiltInTypes.Oid);
    private static readonly RelationalTypeMapping RegProc =
        Native("regproc", typeof(BlueTuskRegProc), BlueTuskBuiltInTypes.RegProc);
    private static readonly RelationalTypeMapping RegProcedure =
        Native("regprocedure", typeof(BlueTuskRegProcedure), BlueTuskBuiltInTypes.RegProcedure);
    private static readonly RelationalTypeMapping RegOper =
        Native("regoper", typeof(BlueTuskRegOper), BlueTuskBuiltInTypes.RegOper);
    private static readonly RelationalTypeMapping RegOperator =
        Native("regoperator", typeof(BlueTuskRegOperator), BlueTuskBuiltInTypes.RegOperator);
    private static readonly RelationalTypeMapping RegClass =
        Native("regclass", typeof(BlueTuskRegClass), BlueTuskBuiltInTypes.RegClass);
    private static readonly RelationalTypeMapping RegType =
        Native("regtype", typeof(BlueTuskRegType), BlueTuskBuiltInTypes.RegType);
    private static readonly RelationalTypeMapping RegConfig =
        Native("regconfig", typeof(BlueTuskRegConfig), BlueTuskBuiltInTypes.RegConfig);
    private static readonly RelationalTypeMapping RegDictionary =
        Native("regdictionary", typeof(BlueTuskRegDictionary), BlueTuskBuiltInTypes.RegDictionary);
    private static readonly RelationalTypeMapping RegNamespace =
        Native("regnamespace", typeof(BlueTuskRegNamespace), BlueTuskBuiltInTypes.RegNamespace);
    private static readonly RelationalTypeMapping RegRole =
        Native("regrole", typeof(BlueTuskRegRole), BlueTuskBuiltInTypes.RegRole);
    private static readonly RelationalTypeMapping RegCollation =
        Native("regcollation", typeof(BlueTuskRegCollation), BlueTuskBuiltInTypes.RegCollation);

    private static readonly RelationalTypeMapping Xid =
        Native("xid", typeof(BlueTuskTransactionId), BlueTuskBuiltInTypes.Xid);
    private static readonly RelationalTypeMapping Cid =
        Native("cid", typeof(BlueTuskCommandId), BlueTuskBuiltInTypes.Cid);
    private static readonly RelationalTypeMapping Xid8 =
        Native("xid8", typeof(BlueTuskFullTransactionId), BlueTuskBuiltInTypes.Xid8);
    private static readonly RelationalTypeMapping PgSnapshot =
        Native("pg_snapshot", typeof(BlueTuskTransactionSnapshot), BlueTuskBuiltInTypes.PgSnapshot);
    private static readonly RelationalTypeMapping TxidSnapshot =
        Native("txid_snapshot", typeof(BlueTuskTransactionSnapshot), BlueTuskBuiltInTypes.TxidSnapshot);

    private static readonly RelationalTypeMapping RefCursor =
        Native("refcursor", typeof(BlueTuskRefCursor), BlueTuskBuiltInTypes.RefCursor);
    private static readonly RelationalTypeMapping NodeTree =
        Native("pg_node_tree", typeof(BlueTuskNodeTree), BlueTuskBuiltInTypes.NodeTree);
    private static readonly RelationalTypeMapping InternalChar =
        Native("\"char\"", typeof(BlueTuskInternalChar), BlueTuskBuiltInTypes.Char);
    private static readonly RelationalTypeMapping AclItem =
        Native("aclitem", typeof(BlueTuskAccessControlItem), BlueTuskBuiltInTypes.AclItem);
    private static readonly RelationalTypeMapping GistTextSearchVector =
        Native("gtsvector", typeof(BlueTuskGistTextSearchVector), BlueTuskBuiltInTypes.GistTextSearchVector);
    private static readonly RelationalTypeMapping Int2Vector =
        Native("int2vector", typeof(BlueTuskInt16Vector), BlueTuskBuiltInTypes.Int2Vector);
    private static readonly RelationalTypeMapping OidVector =
        Native("oidvector", typeof(BlueTuskObjectIdentifierVector), BlueTuskBuiltInTypes.OidVector);
    private static readonly RelationalTypeMapping NDistinctStatistics =
        Native("pg_ndistinct", typeof(BlueTuskNDistinctStatistics), BlueTuskBuiltInTypes.PgNDistinct);
    private static readonly RelationalTypeMapping DependencyStatistics =
        Native("pg_dependencies", typeof(BlueTuskDependencyStatistics), BlueTuskBuiltInTypes.PgDependencies);
    private static readonly RelationalTypeMapping MostCommonValueStatistics =
        Native("pg_mcv_list", typeof(BlueTuskMostCommonValueStatistics), BlueTuskBuiltInTypes.PgMcvList);
    private static readonly RelationalTypeMapping BrinBloomSummary =
        Native("pg_brin_bloom_summary", typeof(BlueTuskBrinBloomSummary), BlueTuskBuiltInTypes.PgBrinBloomSummary);
    private static readonly RelationalTypeMapping BrinMinMaxMultiSummary =
        Native(
            "pg_brin_minmax_multi_summary",
            typeof(BlueTuskBrinMinMaxMultiSummary),
            BlueTuskBuiltInTypes.PgBrinMinMaxMultiSummary);

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
            [typeof(uint)] = Oid,
            [typeof(BlueTuskInterval)] = NativeInterval,
            [typeof(BlueTuskTimeWithTimeZone)] = TimeWithTimeZone,
            [typeof(BlueTuskBitString)] = Varbit,
            [typeof(BlueTuskNumeric)] = Numeric,
            [typeof(BlueTuskTupleId)] = Tid,
            [typeof(BlueTuskLogSequenceNumber)] = PgLsn,
            [typeof(BlueTuskNetworkAddress)] = Inet,
            [typeof(BlueTuskMacAddress)] = Macaddr,
            [typeof(BlueTuskMacAddress8)] = Macaddr8,
            [typeof(BlueTuskPoint)] = Point,
            [typeof(BlueTuskLine)] = Line,
            [typeof(BlueTuskLineSegment)] = LineSegment,
            [typeof(BlueTuskBox)] = Box,
            [typeof(BlueTuskPath)] = Path,
            [typeof(BlueTuskPolygon)] = Polygon,
            [typeof(BlueTuskCircle)] = Circle,
            [typeof(BlueTuskMoney)] = Money,
            [typeof(BlueTuskTextSearchVector)] = TextSearchVector,
            [typeof(BlueTuskTextSearchQuery)] = TextSearchQuery,
            [typeof(BlueTuskJsonPath)] = JsonPath,
            [typeof(BlueTuskRegProc)] = RegProc,
            [typeof(BlueTuskRegProcedure)] = RegProcedure,
            [typeof(BlueTuskRegOper)] = RegOper,
            [typeof(BlueTuskRegOperator)] = RegOperator,
            [typeof(BlueTuskRegClass)] = RegClass,
            [typeof(BlueTuskRegType)] = RegType,
            [typeof(BlueTuskRegConfig)] = RegConfig,
            [typeof(BlueTuskRegDictionary)] = RegDictionary,
            [typeof(BlueTuskRegNamespace)] = RegNamespace,
            [typeof(BlueTuskRegRole)] = RegRole,
            [typeof(BlueTuskRegCollation)] = RegCollation,
            [typeof(BlueTuskTransactionId)] = Xid,
            [typeof(BlueTuskCommandId)] = Cid,
            [typeof(BlueTuskFullTransactionId)] = Xid8,
            [typeof(BlueTuskTransactionSnapshot)] = PgSnapshot,
            [typeof(BlueTuskRefCursor)] = RefCursor,
            [typeof(BlueTuskNodeTree)] = NodeTree,
            [typeof(BlueTuskInternalChar)] = InternalChar,
            [typeof(BlueTuskAccessControlItem)] = AclItem,
            [typeof(BlueTuskGistTextSearchVector)] = GistTextSearchVector,
            [typeof(BlueTuskInt16Vector)] = Int2Vector,
            [typeof(BlueTuskObjectIdentifierVector)] = OidVector,
            [typeof(BlueTuskNDistinctStatistics)] = NDistinctStatistics,
            [typeof(BlueTuskDependencyStatistics)] = DependencyStatistics,
            [typeof(BlueTuskMostCommonValueStatistics)] = MostCommonValueStatistics,
            [typeof(BlueTuskBrinBloomSummary)] = BrinBloomSummary,
            [typeof(BlueTuskBrinMinMaxMultiSummary)] = BrinMinMaxMultiSummary,
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
            ["name"] = Name,
            ["varchar"] = String,
            ["character varying"] = String,
            ["char"] = String,
            ["character"] = String,
            ["json"] = Json,
            ["jsonb"] = Jsonb,
            ["xml"] = Xml,
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
            ["timetz"] = TimeWithTimeZone,
            ["time with time zone"] = TimeWithTimeZone,
            ["bit"] = Bit,
            ["varbit"] = Varbit,
            ["bit varying"] = Varbit,
            ["tid"] = Tid,
            ["pg_lsn"] = PgLsn,
            ["inet"] = Inet,
            ["cidr"] = Cidr,
            ["macaddr"] = Macaddr,
            ["macaddr8"] = Macaddr8,
            ["point"] = Point,
            ["line"] = Line,
            ["lseg"] = LineSegment,
            ["box"] = Box,
            ["path"] = Path,
            ["polygon"] = Polygon,
            ["circle"] = Circle,
            ["money"] = Money,
            ["tsvector"] = TextSearchVector,
            ["tsquery"] = TextSearchQuery,
            ["jsonpath"] = JsonPath,
            ["oid"] = Oid,
            ["regproc"] = RegProc,
            ["regprocedure"] = RegProcedure,
            ["regoper"] = RegOper,
            ["regoperator"] = RegOperator,
            ["regclass"] = RegClass,
            ["regtype"] = RegType,
            ["regconfig"] = RegConfig,
            ["regdictionary"] = RegDictionary,
            ["regnamespace"] = RegNamespace,
            ["regrole"] = RegRole,
            ["regcollation"] = RegCollation,
            ["xid"] = Xid,
            ["cid"] = Cid,
            ["xid8"] = Xid8,
            ["pg_snapshot"] = PgSnapshot,
            ["txid_snapshot"] = TxidSnapshot,
            ["refcursor"] = RefCursor,
            ["pg_node_tree"] = NodeTree,
            ["aclitem"] = AclItem,
            ["gtsvector"] = GistTextSearchVector,
            ["int2vector"] = Int2Vector,
            ["oidvector"] = OidVector,
            ["pg_ndistinct"] = NDistinctStatistics,
            ["pg_dependencies"] = DependencyStatistics,
            ["pg_mcv_list"] = MostCommonValueStatistics,
            ["pg_brin_bloom_summary"] = BrinBloomSummary,
            ["pg_brin_minmax_multi_summary"] = BrinMinMaxMultiSummary,
        };

    public BlueTuskTypeMappingSource(
        TypeMappingSourceDependencies dependencies,
        RelationalTypeMappingSourceDependencies relationalDependencies)
        : base(dependencies, relationalDependencies)
    {
    }

    protected override RelationalTypeMapping? FindMapping(in RelationalTypeMappingInfo mappingInfo)
    {
        var clrType = mappingInfo.ClrType is { } requestedClrType
            ? Nullable.GetUnderlyingType(requestedClrType) ?? requestedClrType
            : null;

        if (mappingInfo.StoreTypeNameBase is { } storeTypeName
            && StoreMappings.TryGetValue(storeTypeName, out var storeMapping))
        {
            if (clrType is not null
                && storeMapping.ClrType != clrType
                && ClrMappings.TryGetValue(clrType, out var compatibleClrMapping)
                && HasCompatibleStoreType(compatibleClrMapping, storeTypeName))
            {
                return ApplyFacets(compatibleClrMapping, mappingInfo);
            }

            return ApplyFacets(storeMapping, mappingInfo);
        }

        if (clrType is not null
            && ClrMappings.TryGetValue(clrType, out var clrMapping))
        {
            return ApplyFacets(clrMapping, mappingInfo);
        }

        return base.FindMapping(mappingInfo);
    }

    private static RelationalTypeMapping ApplyFacets(
        RelationalTypeMapping mapping,
        in RelationalTypeMappingInfo mappingInfo)
    {
        if (mapping.ClrType == typeof(string))
        {
            if (!ReferenceEquals(mapping, String)
                || mappingInfo.IsFixedLength is null && mappingInfo.Size is null)
            {
                return mapping;
            }

            return mappingInfo.IsFixedLength == true
                ? new StringTypeMapping(
                    "character",
                    DbType.StringFixedLength,
                    unicode: false,
                    mappingInfo.Size).Clone(
                        mappingInfo,
                        storeTypePostfix: StoreTypePostfix.Size)
                : mappingInfo.Size is { } size
                    ? new StringTypeMapping("character varying", DbType.String, unicode: false, size).Clone(
                        mappingInfo,
                        storeTypePostfix: StoreTypePostfix.Size)
                    : String;
        }

        if (mapping.ClrType == typeof(decimal)
            && (mappingInfo.Precision is not null || mappingInfo.Scale is not null))
        {
            return new DecimalTypeMapping(
                "numeric",
                DbType.Decimal,
                mappingInfo.Precision,
                mappingInfo.Scale).Clone(
                    mappingInfo,
                    storeTypePostfix: StoreTypePostfix.PrecisionAndScale);
        }

        return mapping;
    }

    private static bool HasCompatibleStoreType(RelationalTypeMapping mapping, string requestedStoreType)
    {
        var mappingStoreType = mapping.StoreType;
        var facetIndex = mappingStoreType.IndexOf('(');
        if (facetIndex >= 0)
        {
            mappingStoreType = mappingStoreType[..facetIndex];
        }

        return string.Equals(mappingStoreType.Trim(' ', '"'), requestedStoreType.Trim(' ', '"'), StringComparison.OrdinalIgnoreCase);
    }

    private static BlueTuskNativeTypeMapping Native(
        string storeType,
        Type clrType,
        BlueTuskTypeDescriptor descriptor) =>
        new BlueTuskNativeTypeMapping(storeType, clrType, descriptor.Id.Oid);
}
