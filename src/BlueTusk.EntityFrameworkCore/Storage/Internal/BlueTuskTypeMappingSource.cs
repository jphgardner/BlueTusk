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

    private static readonly BlueTuskTypeDescriptor Int4RangeDescriptor =
        BuiltInCollectionDescriptor(3904, "int4range", 3905, BlueTuskTypeKind.Range);
    private static readonly BlueTuskTypeDescriptor NumericRangeDescriptor =
        BuiltInCollectionDescriptor(3906, "numrange", 3907, BlueTuskTypeKind.Range);
    private static readonly BlueTuskTypeDescriptor TimestampRangeDescriptor =
        BuiltInCollectionDescriptor(3908, "tsrange", 3909, BlueTuskTypeKind.Range);
    private static readonly BlueTuskTypeDescriptor TimestampWithTimeZoneRangeDescriptor =
        BuiltInCollectionDescriptor(3910, "tstzrange", 3911, BlueTuskTypeKind.Range);
    private static readonly BlueTuskTypeDescriptor DateRangeDescriptor =
        BuiltInCollectionDescriptor(3912, "daterange", 3913, BlueTuskTypeKind.Range);
    private static readonly BlueTuskTypeDescriptor Int8RangeDescriptor =
        BuiltInCollectionDescriptor(3926, "int8range", 3927, BlueTuskTypeKind.Range);
    private static readonly BlueTuskTypeDescriptor Int4MultirangeDescriptor =
        BuiltInCollectionDescriptor(4451, "int4multirange", 6150, BlueTuskTypeKind.Multirange);
    private static readonly BlueTuskTypeDescriptor NumericMultirangeDescriptor =
        BuiltInCollectionDescriptor(4532, "nummultirange", 6151, BlueTuskTypeKind.Multirange);
    private static readonly BlueTuskTypeDescriptor TimestampMultirangeDescriptor =
        BuiltInCollectionDescriptor(4533, "tsmultirange", 6152, BlueTuskTypeKind.Multirange);
    private static readonly BlueTuskTypeDescriptor TimestampWithTimeZoneMultirangeDescriptor =
        BuiltInCollectionDescriptor(4534, "tstzmultirange", 6153, BlueTuskTypeKind.Multirange);
    private static readonly BlueTuskTypeDescriptor DateMultirangeDescriptor =
        BuiltInCollectionDescriptor(4535, "datemultirange", 6155, BlueTuskTypeKind.Multirange);
    private static readonly BlueTuskTypeDescriptor Int8MultirangeDescriptor =
        BuiltInCollectionDescriptor(4536, "int8multirange", 6157, BlueTuskTypeKind.Multirange);

    private static readonly RelationalTypeMapping Int4Range = new BlueTuskRangeTypeMapping<int>("int4range", 3904);
    private static readonly RelationalTypeMapping NumericRange =
        new BlueTuskRangeTypeMapping<BlueTuskNumeric>("numrange", 3906);
    private static readonly RelationalTypeMapping TimestampRange =
        new BlueTuskRangeTypeMapping<DateTime>("tsrange", 3908);
    private static readonly RelationalTypeMapping TimestampWithTimeZoneRange =
        new BlueTuskRangeTypeMapping<DateTimeOffset>("tstzrange", 3910);
    private static readonly RelationalTypeMapping DateRange = new BlueTuskRangeTypeMapping<DateOnly>("daterange", 3912);
    private static readonly RelationalTypeMapping Int8Range = new BlueTuskRangeTypeMapping<long>("int8range", 3926);
    private static readonly RelationalTypeMapping Int4Multirange =
        new BlueTuskMultirangeTypeMapping<int>("int4multirange", 4451);
    private static readonly RelationalTypeMapping NumericMultirange =
        new BlueTuskMultirangeTypeMapping<BlueTuskNumeric>("nummultirange", 4532);
    private static readonly RelationalTypeMapping TimestampMultirange =
        new BlueTuskMultirangeTypeMapping<DateTime>("tsmultirange", 4533);
    private static readonly RelationalTypeMapping TimestampWithTimeZoneMultirange =
        new BlueTuskMultirangeTypeMapping<DateTimeOffset>("tstzmultirange", 4534);
    private static readonly RelationalTypeMapping DateMultirange =
        new BlueTuskMultirangeTypeMapping<DateOnly>("datemultirange", 4535);
    private static readonly RelationalTypeMapping Int8Multirange =
        new BlueTuskMultirangeTypeMapping<long>("int8multirange", 4536);

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
            [typeof(BlueTuskRange<int>)] = Int4Range,
            [typeof(BlueTuskRange<BlueTuskNumeric>)] = NumericRange,
            [typeof(BlueTuskRange<DateTime>)] = TimestampRange,
            [typeof(BlueTuskRange<DateTimeOffset>)] = TimestampWithTimeZoneRange,
            [typeof(BlueTuskRange<DateOnly>)] = DateRange,
            [typeof(BlueTuskRange<long>)] = Int8Range,
            [typeof(BlueTuskMultirange<int>)] = Int4Multirange,
            [typeof(BlueTuskMultirange<BlueTuskNumeric>)] = NumericMultirange,
            [typeof(BlueTuskMultirange<DateTime>)] = TimestampMultirange,
            [typeof(BlueTuskMultirange<DateTimeOffset>)] = TimestampWithTimeZoneMultirange,
            [typeof(BlueTuskMultirange<DateOnly>)] = DateMultirange,
            [typeof(BlueTuskMultirange<long>)] = Int8Multirange,
        };

    private static readonly Dictionary<Type, BlueTuskTypeDescriptor> ArrayElementDescriptors =
        new Dictionary<Type, BlueTuskTypeDescriptor>
        {
            [typeof(bool)] = BlueTuskBuiltInTypes.Boolean,
            [typeof(short)] = BlueTuskBuiltInTypes.Int2,
            [typeof(int)] = BlueTuskBuiltInTypes.Int4,
            [typeof(long)] = BlueTuskBuiltInTypes.Int8,
            [typeof(float)] = BlueTuskBuiltInTypes.Float4,
            [typeof(double)] = BlueTuskBuiltInTypes.Float8,
            [typeof(string)] = BlueTuskBuiltInTypes.Text,
            [typeof(byte[])] = BlueTuskBuiltInTypes.Bytea,
            [typeof(Guid)] = BlueTuskBuiltInTypes.Uuid,
            [typeof(DateTime)] = BlueTuskBuiltInTypes.Timestamp,
            [typeof(DateTimeOffset)] = BlueTuskBuiltInTypes.TimestampWithTimeZone,
            [typeof(DateOnly)] = BlueTuskBuiltInTypes.Date,
            [typeof(uint)] = BlueTuskBuiltInTypes.Oid,
            [typeof(BlueTuskInterval)] = BlueTuskBuiltInTypes.Interval,
            [typeof(BlueTuskTimeWithTimeZone)] = BlueTuskBuiltInTypes.TimeWithTimeZone,
            [typeof(BlueTuskBitString)] = BlueTuskBuiltInTypes.Varbit,
            [typeof(BlueTuskNumeric)] = BlueTuskBuiltInTypes.Numeric,
            [typeof(BlueTuskTupleId)] = BlueTuskBuiltInTypes.Tid,
            [typeof(BlueTuskLogSequenceNumber)] = BlueTuskBuiltInTypes.PgLsn,
            [typeof(BlueTuskNetworkAddress)] = BlueTuskBuiltInTypes.Inet,
            [typeof(BlueTuskMacAddress)] = BlueTuskBuiltInTypes.Macaddr,
            [typeof(BlueTuskMacAddress8)] = BlueTuskBuiltInTypes.Macaddr8,
            [typeof(BlueTuskPoint)] = BlueTuskBuiltInTypes.Point,
            [typeof(BlueTuskLine)] = BlueTuskBuiltInTypes.Line,
            [typeof(BlueTuskLineSegment)] = BlueTuskBuiltInTypes.LineSegment,
            [typeof(BlueTuskBox)] = BlueTuskBuiltInTypes.Box,
            [typeof(BlueTuskPath)] = BlueTuskBuiltInTypes.Path,
            [typeof(BlueTuskPolygon)] = BlueTuskBuiltInTypes.Polygon,
            [typeof(BlueTuskCircle)] = BlueTuskBuiltInTypes.Circle,
            [typeof(BlueTuskMoney)] = BlueTuskBuiltInTypes.Money,
            [typeof(BlueTuskTextSearchVector)] = BlueTuskBuiltInTypes.TextSearchVector,
            [typeof(BlueTuskTextSearchQuery)] = BlueTuskBuiltInTypes.TextSearchQuery,
            [typeof(BlueTuskJsonPath)] = BlueTuskBuiltInTypes.JsonPath,
            [typeof(BlueTuskRegProc)] = BlueTuskBuiltInTypes.RegProc,
            [typeof(BlueTuskRegProcedure)] = BlueTuskBuiltInTypes.RegProcedure,
            [typeof(BlueTuskRegOper)] = BlueTuskBuiltInTypes.RegOper,
            [typeof(BlueTuskRegOperator)] = BlueTuskBuiltInTypes.RegOperator,
            [typeof(BlueTuskRegClass)] = BlueTuskBuiltInTypes.RegClass,
            [typeof(BlueTuskRegType)] = BlueTuskBuiltInTypes.RegType,
            [typeof(BlueTuskRegConfig)] = BlueTuskBuiltInTypes.RegConfig,
            [typeof(BlueTuskRegDictionary)] = BlueTuskBuiltInTypes.RegDictionary,
            [typeof(BlueTuskRegNamespace)] = BlueTuskBuiltInTypes.RegNamespace,
            [typeof(BlueTuskRegRole)] = BlueTuskBuiltInTypes.RegRole,
            [typeof(BlueTuskRegCollation)] = BlueTuskBuiltInTypes.RegCollation,
            [typeof(BlueTuskTransactionId)] = BlueTuskBuiltInTypes.Xid,
            [typeof(BlueTuskCommandId)] = BlueTuskBuiltInTypes.Cid,
            [typeof(BlueTuskFullTransactionId)] = BlueTuskBuiltInTypes.Xid8,
            [typeof(BlueTuskTransactionSnapshot)] = BlueTuskBuiltInTypes.PgSnapshot,
            [typeof(BlueTuskRefCursor)] = BlueTuskBuiltInTypes.RefCursor,
            [typeof(BlueTuskInternalChar)] = BlueTuskBuiltInTypes.Char,
            [typeof(BlueTuskAccessControlItem)] = BlueTuskBuiltInTypes.AclItem,
            [typeof(BlueTuskGistTextSearchVector)] = BlueTuskBuiltInTypes.GistTextSearchVector,
            [typeof(BlueTuskInt16Vector)] = BlueTuskBuiltInTypes.Int2Vector,
            [typeof(BlueTuskObjectIdentifierVector)] = BlueTuskBuiltInTypes.OidVector,
            [typeof(BlueTuskRange<int>)] = Int4RangeDescriptor,
            [typeof(BlueTuskRange<BlueTuskNumeric>)] = NumericRangeDescriptor,
            [typeof(BlueTuskRange<DateTime>)] = TimestampRangeDescriptor,
            [typeof(BlueTuskRange<DateTimeOffset>)] = TimestampWithTimeZoneRangeDescriptor,
            [typeof(BlueTuskRange<DateOnly>)] = DateRangeDescriptor,
            [typeof(BlueTuskRange<long>)] = Int8RangeDescriptor,
            [typeof(BlueTuskMultirange<int>)] = Int4MultirangeDescriptor,
            [typeof(BlueTuskMultirange<BlueTuskNumeric>)] = NumericMultirangeDescriptor,
            [typeof(BlueTuskMultirange<DateTime>)] = TimestampMultirangeDescriptor,
            [typeof(BlueTuskMultirange<DateTimeOffset>)] = TimestampWithTimeZoneMultirangeDescriptor,
            [typeof(BlueTuskMultirange<DateOnly>)] = DateMultirangeDescriptor,
            [typeof(BlueTuskMultirange<long>)] = Int8MultirangeDescriptor,
        };

    private static readonly Dictionary<string, BlueTuskTypeDescriptor> ArrayStoreDescriptors =
        new Dictionary<string, BlueTuskTypeDescriptor>(StringComparer.OrdinalIgnoreCase)
        {
            ["bool"] = BlueTuskBuiltInTypes.Boolean,
            ["boolean"] = BlueTuskBuiltInTypes.Boolean,
            ["int2"] = BlueTuskBuiltInTypes.Int2,
            ["smallint"] = BlueTuskBuiltInTypes.Int2,
            ["int4"] = BlueTuskBuiltInTypes.Int4,
            ["integer"] = BlueTuskBuiltInTypes.Int4,
            ["int8"] = BlueTuskBuiltInTypes.Int8,
            ["bigint"] = BlueTuskBuiltInTypes.Int8,
            ["float4"] = BlueTuskBuiltInTypes.Float4,
            ["real"] = BlueTuskBuiltInTypes.Float4,
            ["float8"] = BlueTuskBuiltInTypes.Float8,
            ["double precision"] = BlueTuskBuiltInTypes.Float8,
            ["text"] = BlueTuskBuiltInTypes.Text,
            ["name"] = BlueTuskBuiltInTypes.Name,
            ["varchar"] = BlueTuskBuiltInTypes.Varchar,
            ["character varying"] = BlueTuskBuiltInTypes.Varchar,
            ["bytea"] = BlueTuskBuiltInTypes.Bytea,
            ["uuid"] = BlueTuskBuiltInTypes.Uuid,
            ["timestamp"] = BlueTuskBuiltInTypes.Timestamp,
            ["timestamp without time zone"] = BlueTuskBuiltInTypes.Timestamp,
            ["timestamptz"] = BlueTuskBuiltInTypes.TimestampWithTimeZone,
            ["timestamp with time zone"] = BlueTuskBuiltInTypes.TimestampWithTimeZone,
            ["date"] = BlueTuskBuiltInTypes.Date,
            ["interval"] = BlueTuskBuiltInTypes.Interval,
            ["timetz"] = BlueTuskBuiltInTypes.TimeWithTimeZone,
            ["time with time zone"] = BlueTuskBuiltInTypes.TimeWithTimeZone,
            ["numeric"] = BlueTuskBuiltInTypes.Numeric,
            ["decimal"] = BlueTuskBuiltInTypes.Numeric,
            ["bit"] = BlueTuskBuiltInTypes.Bit,
            ["varbit"] = BlueTuskBuiltInTypes.Varbit,
            ["bit varying"] = BlueTuskBuiltInTypes.Varbit,
            ["json"] = BlueTuskBuiltInTypes.Json,
            ["jsonb"] = BlueTuskBuiltInTypes.Jsonb,
            ["xml"] = BlueTuskBuiltInTypes.Xml,
            ["tid"] = BlueTuskBuiltInTypes.Tid,
            ["pg_lsn"] = BlueTuskBuiltInTypes.PgLsn,
            ["inet"] = BlueTuskBuiltInTypes.Inet,
            ["cidr"] = BlueTuskBuiltInTypes.Cidr,
            ["macaddr"] = BlueTuskBuiltInTypes.Macaddr,
            ["macaddr8"] = BlueTuskBuiltInTypes.Macaddr8,
            ["point"] = BlueTuskBuiltInTypes.Point,
            ["line"] = BlueTuskBuiltInTypes.Line,
            ["lseg"] = BlueTuskBuiltInTypes.LineSegment,
            ["box"] = BlueTuskBuiltInTypes.Box,
            ["path"] = BlueTuskBuiltInTypes.Path,
            ["polygon"] = BlueTuskBuiltInTypes.Polygon,
            ["circle"] = BlueTuskBuiltInTypes.Circle,
            ["money"] = BlueTuskBuiltInTypes.Money,
            ["tsvector"] = BlueTuskBuiltInTypes.TextSearchVector,
            ["tsquery"] = BlueTuskBuiltInTypes.TextSearchQuery,
            ["jsonpath"] = BlueTuskBuiltInTypes.JsonPath,
            ["oid"] = BlueTuskBuiltInTypes.Oid,
            ["regproc"] = BlueTuskBuiltInTypes.RegProc,
            ["regprocedure"] = BlueTuskBuiltInTypes.RegProcedure,
            ["regoper"] = BlueTuskBuiltInTypes.RegOper,
            ["regoperator"] = BlueTuskBuiltInTypes.RegOperator,
            ["regclass"] = BlueTuskBuiltInTypes.RegClass,
            ["regtype"] = BlueTuskBuiltInTypes.RegType,
            ["regconfig"] = BlueTuskBuiltInTypes.RegConfig,
            ["regdictionary"] = BlueTuskBuiltInTypes.RegDictionary,
            ["regnamespace"] = BlueTuskBuiltInTypes.RegNamespace,
            ["regrole"] = BlueTuskBuiltInTypes.RegRole,
            ["regcollation"] = BlueTuskBuiltInTypes.RegCollation,
            ["xid"] = BlueTuskBuiltInTypes.Xid,
            ["cid"] = BlueTuskBuiltInTypes.Cid,
            ["xid8"] = BlueTuskBuiltInTypes.Xid8,
            ["pg_snapshot"] = BlueTuskBuiltInTypes.PgSnapshot,
            ["txid_snapshot"] = BlueTuskBuiltInTypes.TxidSnapshot,
            ["refcursor"] = BlueTuskBuiltInTypes.RefCursor,
            ["aclitem"] = BlueTuskBuiltInTypes.AclItem,
            ["gtsvector"] = BlueTuskBuiltInTypes.GistTextSearchVector,
            ["int2vector"] = BlueTuskBuiltInTypes.Int2Vector,
            ["oidvector"] = BlueTuskBuiltInTypes.OidVector,
            ["int4range"] = Int4RangeDescriptor,
            ["numrange"] = NumericRangeDescriptor,
            ["tsrange"] = TimestampRangeDescriptor,
            ["tstzrange"] = TimestampWithTimeZoneRangeDescriptor,
            ["daterange"] = DateRangeDescriptor,
            ["int8range"] = Int8RangeDescriptor,
            ["int4multirange"] = Int4MultirangeDescriptor,
            ["nummultirange"] = NumericMultirangeDescriptor,
            ["tsmultirange"] = TimestampMultirangeDescriptor,
            ["tstzmultirange"] = TimestampWithTimeZoneMultirangeDescriptor,
            ["datemultirange"] = DateMultirangeDescriptor,
            ["int8multirange"] = Int8MultirangeDescriptor,
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
            ["int4range"] = Int4Range,
            ["numrange"] = NumericRange,
            ["tsrange"] = TimestampRange,
            ["tstzrange"] = TimestampWithTimeZoneRange,
            ["daterange"] = DateRange,
            ["int8range"] = Int8Range,
            ["int4multirange"] = Int4Multirange,
            ["nummultirange"] = NumericMultirange,
            ["tsmultirange"] = TimestampMultirange,
            ["tstzmultirange"] = TimestampWithTimeZoneMultirange,
            ["datemultirange"] = DateMultirange,
            ["int8multirange"] = Int8Multirange,
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

        if (clrType is { IsArray: true } && clrType != typeof(byte[]))
        {
            return FindArrayMapping(mappingInfo, clrType, elementTypeMapping: null);
        }

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

    protected override RelationalTypeMapping? FindCollectionMapping(
        RelationalTypeMappingInfo mappingInfo,
        Type modelClrType,
        Type? providerClrType,
        CoreTypeMapping? elementMapping)
    {
        if (modelClrType.IsArray
            && providerClrType?.IsArray == true
            && modelClrType != typeof(byte[])
            && elementMapping is RelationalTypeMapping relationalElementMapping)
        {
            return FindArrayMapping(mappingInfo, modelClrType, relationalElementMapping);
        }

        return null;
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

    private static BlueTuskTypeDescriptor BuiltInCollectionDescriptor(
        uint oid,
        string name,
        uint arrayOid,
        BlueTuskTypeKind kind) => new()
        {
            Id = new BlueTuskTypeId(oid),
            Schema = "pg_catalog",
            Name = name,
            Kind = kind,
            ArrayType = new BlueTuskTypeId(arrayOid),
        };

    private static BlueTuskArrayTypeMapping? FindArrayMapping(
        in RelationalTypeMappingInfo mappingInfo,
        Type arrayType,
        RelationalTypeMapping? elementTypeMapping)
    {
        var elementClrType = arrayType.GetElementType()!;
        BlueTuskTypeDescriptor? elementDescriptor = null;
        string? requestedElementStoreType = null;
        if (mappingInfo.StoreTypeName is { } requestedStoreType)
        {
            var storeType = requestedStoreType.Trim();
            if (!storeType.EndsWith("[]", StringComparison.Ordinal))
            {
                return null;
            }

            requestedElementStoreType = storeType[..^2].Trim();
            var facetIndex = requestedElementStoreType.IndexOf('(');
            var storeTypeBase = facetIndex < 0
                ? requestedElementStoreType
                : requestedElementStoreType[..facetIndex].TrimEnd();
            if (!ArrayStoreDescriptors.TryGetValue(storeTypeBase.Trim('"'), out elementDescriptor))
            {
                return null;
            }

            if (!StoreMappings.TryGetValue(storeTypeBase.Trim('"'), out elementTypeMapping))
            {
                return null;
            }
        }

        if (elementDescriptor is null
            && !ArrayElementDescriptors.TryGetValue(elementClrType, out elementDescriptor))
        {
            return null;
        }

        if (elementTypeMapping is null
            && !ClrMappings.TryGetValue(elementClrType, out elementTypeMapping))
        {
            return null;
        }

        if (elementDescriptor.ArrayType is not { } arrayTypeId)
        {
            return null;
        }

        var storeTypeName = mappingInfo.StoreTypeName
            ?? $"{requestedElementStoreType ?? elementTypeMapping.StoreType}[]";
        return new BlueTuskArrayTypeMapping(
            storeTypeName,
            arrayType,
            arrayTypeId.Oid,
            elementTypeMapping);
    }
}
