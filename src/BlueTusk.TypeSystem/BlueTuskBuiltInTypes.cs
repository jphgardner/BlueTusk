using System.Diagnostics.CodeAnalysis;

namespace BlueTusk.TypeSystem;

[SuppressMessage(
    "Naming",
    "CA1720:Identifier contains type name",
    Justification = "Public descriptors intentionally use PostgreSQL's canonical built-in type names.")]
public static class BlueTuskBuiltInTypes
{
    public static BlueTuskTypeDescriptor Boolean { get; } = Create(16, "bool", 1000);

    public static BlueTuskTypeDescriptor Bytea { get; } = Create(17, "bytea", 1001);

    public static BlueTuskTypeDescriptor Char { get; } = Create(18, "char", 1002);

    public static BlueTuskTypeDescriptor Name { get; } = Create(19, "name", 1003);

    public static BlueTuskTypeDescriptor Int8 { get; } = Create(20, "int8", 1016);

    public static BlueTuskTypeDescriptor Int2 { get; } = Create(21, "int2", 1005);

    public static BlueTuskTypeDescriptor Int2Vector { get; } =
        Create(22, "int2vector", 1006, elementTypeOid: 21);

    public static BlueTuskTypeDescriptor Int4 { get; } = Create(23, "int4", 1007);

    public static BlueTuskTypeDescriptor RegProc { get; } = Create(24, "regproc", 1008);

    public static BlueTuskTypeDescriptor Text { get; } = Create(25, "text", 1009);

    public static BlueTuskTypeDescriptor Oid { get; } = Create(26, "oid", 1028);

    public static BlueTuskTypeDescriptor Tid { get; } = Create(27, "tid", 1010);

    public static BlueTuskTypeDescriptor Xid { get; } = Create(28, "xid", 1011);

    public static BlueTuskTypeDescriptor Cid { get; } = Create(29, "cid", 1012);

    public static BlueTuskTypeDescriptor OidVector { get; } =
        Create(30, "oidvector", 1013, elementTypeOid: 26);

    public static BlueTuskTypeDescriptor Json { get; } = Create(114, "json", 199);

    public static BlueTuskTypeDescriptor Xml { get; } = Create(142, "xml", 143);

    public static BlueTuskTypeDescriptor NodeTree { get; } = CreateWithoutArray(194, "pg_node_tree");

    public static BlueTuskTypeDescriptor Point { get; } = Create(600, "point", 1017);

    public static BlueTuskTypeDescriptor LineSegment { get; } = Create(601, "lseg", 1018);

    public static BlueTuskTypeDescriptor Path { get; } = Create(602, "path", 1019);

    public static BlueTuskTypeDescriptor Box { get; } = Create(603, "box", 1020, ';');

    public static BlueTuskTypeDescriptor Polygon { get; } = Create(604, "polygon", 1027);

    public static BlueTuskTypeDescriptor Line { get; } = Create(628, "line", 629);

    public static BlueTuskTypeDescriptor Cidr { get; } = Create(650, "cidr", 651);

    public static BlueTuskTypeDescriptor Float4 { get; } = Create(700, "float4", 1021);

    public static BlueTuskTypeDescriptor Float8 { get; } = Create(701, "float8", 1022);

    public static BlueTuskTypeDescriptor Circle { get; } = Create(718, "circle", 719);

    public static BlueTuskTypeDescriptor Macaddr8 { get; } = Create(774, "macaddr8", 775);

    public static BlueTuskTypeDescriptor Money { get; } = Create(790, "money", 791);

    public static BlueTuskTypeDescriptor Macaddr { get; } = Create(829, "macaddr", 1040);

    public static BlueTuskTypeDescriptor Inet { get; } = Create(869, "inet", 1041);

    public static BlueTuskTypeDescriptor AclItem { get; } = Create(1033, "aclitem", 1034);

    public static BlueTuskTypeDescriptor Bpchar { get; } = Create(1042, "bpchar", 1014);

    public static BlueTuskTypeDescriptor Varchar { get; } = Create(1043, "varchar", 1015);

    public static BlueTuskTypeDescriptor Date { get; } = Create(1082, "date", 1182);

    public static BlueTuskTypeDescriptor Time { get; } = Create(1083, "time", 1183);

    public static BlueTuskTypeDescriptor Timestamp { get; } = Create(1114, "timestamp", 1115);

    public static BlueTuskTypeDescriptor TimestampWithTimeZone { get; } = Create(1184, "timestamptz", 1185);

    public static BlueTuskTypeDescriptor Interval { get; } = Create(1186, "interval", 1187);

    public static BlueTuskTypeDescriptor TimeWithTimeZone { get; } = Create(1266, "timetz", 1270);

    public static BlueTuskTypeDescriptor Numeric { get; } = Create(1700, "numeric", 1231);

    public static BlueTuskTypeDescriptor RefCursor { get; } = Create(1790, "refcursor", 2201);

    public static BlueTuskTypeDescriptor Bit { get; } = Create(1560, "bit", 1561);

    public static BlueTuskTypeDescriptor Varbit { get; } = Create(1562, "varbit", 1563);

    public static BlueTuskTypeDescriptor Uuid { get; } = Create(2950, "uuid", 2951);

    public static BlueTuskTypeDescriptor RegProcedure { get; } = Create(2202, "regprocedure", 2207);

    public static BlueTuskTypeDescriptor RegOper { get; } = Create(2203, "regoper", 2208);

    public static BlueTuskTypeDescriptor RegOperator { get; } = Create(2204, "regoperator", 2209);

    public static BlueTuskTypeDescriptor RegClass { get; } = Create(2205, "regclass", 2210);

    public static BlueTuskTypeDescriptor RegType { get; } = Create(2206, "regtype", 2211);

    public static BlueTuskTypeDescriptor TxidSnapshot { get; } = Create(2970, "txid_snapshot", 2949);

    public static BlueTuskTypeDescriptor PgLsn { get; } = Create(3220, "pg_lsn", 3221);

    public static BlueTuskTypeDescriptor PgNDistinct { get; } =
        CreateWithoutArray(3361, "pg_ndistinct");

    public static BlueTuskTypeDescriptor PgDependencies { get; } =
        CreateWithoutArray(3402, "pg_dependencies");

    public static BlueTuskTypeDescriptor TextSearchVector { get; } = Create(3614, "tsvector", 3643);

    public static BlueTuskTypeDescriptor TextSearchQuery { get; } = Create(3615, "tsquery", 3645);

    public static BlueTuskTypeDescriptor GistTextSearchVector { get; } =
        Create(3642, "gtsvector", 3644);

    public static BlueTuskTypeDescriptor RegConfig { get; } = Create(3734, "regconfig", 3735);

    public static BlueTuskTypeDescriptor RegDictionary { get; } =
        Create(3769, "regdictionary", 3770);

    public static BlueTuskTypeDescriptor Jsonb { get; } = Create(3802, "jsonb", 3807);

    public static BlueTuskTypeDescriptor JsonPath { get; } = Create(4072, "jsonpath", 4073);

    public static BlueTuskTypeDescriptor RegNamespace { get; } =
        Create(4089, "regnamespace", 4090);

    public static BlueTuskTypeDescriptor RegRole { get; } = Create(4096, "regrole", 4097);

    public static BlueTuskTypeDescriptor RegCollation { get; } =
        Create(4191, "regcollation", 4192);

    public static BlueTuskTypeDescriptor PgBrinBloomSummary { get; } =
        CreateWithoutArray(4600, "pg_brin_bloom_summary");

    public static BlueTuskTypeDescriptor PgBrinMinMaxMultiSummary { get; } =
        CreateWithoutArray(4601, "pg_brin_minmax_multi_summary");

    public static BlueTuskTypeDescriptor PgMcvList { get; } =
        CreateWithoutArray(5017, "pg_mcv_list");

    public static BlueTuskTypeDescriptor PgSnapshot { get; } = Create(5038, "pg_snapshot", 5039);

    public static BlueTuskTypeDescriptor Xid8 { get; } = Create(5069, "xid8", 271);

    public static BlueTuskTypeDescriptor Oid8 { get; } = Create(6437, "oid8", 6442);

    public static BlueTuskTypeDescriptor RegDatabase { get; } =
        Create(6490, "regdatabase", 6491);

    public static BlueTuskTypeRegistry CreateInitialRegistry() => CreateRegistry();

    public static BlueTuskTypeRegistry CreateRegistry()
    {
        var textCodec = new BlueTuskStringCodec();
        return new BlueTuskTypeRegistryBuilder()
            .Register(Boolean, new BlueTuskBooleanCodec())
            .Register(Bytea, new BlueTuskByteArrayCodec())
            .Register(Char, new BlueTuskInternalCharCodec())
            .Register(Name, textCodec)
            .Register(Int8, new BlueTuskInt64Codec())
            .Register(Int2, new BlueTuskInt16Codec())
            .Register(Int2Vector, new BlueTuskInt16VectorCodec())
            .Register(Int4, new BlueTuskInt32Codec())
            .Register(RegProc, new BlueTuskObjectIdentifierCodec<BlueTuskRegProc>())
            .Register(Text, textCodec)
            .Register(Oid, new BlueTuskUInt32Codec())
            .Register(Tid, new BlueTuskTupleIdCodec())
            .Register(Xid, new BlueTuskTransactionIdCodec())
            .Register(Cid, new BlueTuskCommandIdCodec())
            .Register(OidVector, new BlueTuskObjectIdentifierVectorCodec())
            .Register(Json, textCodec)
            .Register(Xml, textCodec)
            .Register(NodeTree, new BlueTuskNodeTreeCodec())
            .Register(Point, new BlueTuskPointCodec())
            .Register(LineSegment, new BlueTuskLineSegmentCodec())
            .Register(Path, new BlueTuskPathCodec())
            .Register(Box, new BlueTuskBoxCodec())
            .Register(Polygon, new BlueTuskPolygonCodec())
            .Register(Line, new BlueTuskLineCodec())
            .Register(Cidr, new BlueTuskCidrCodec())
            .Register(Float4, new BlueTuskSingleCodec())
            .Register(Float8, new BlueTuskDoubleCodec())
            .Register(Circle, new BlueTuskCircleCodec())
            .Register(Macaddr8, new BlueTuskMacAddress8Codec())
            .Register(Money)
            .Register(Macaddr, new BlueTuskMacAddressCodec())
            .Register(Inet, new BlueTuskInetCodec())
            .Register(AclItem, new BlueTuskAccessControlItemCodec())
            .Register(Bpchar, textCodec)
            .Register(Varchar, textCodec)
            .Register(Date, new BlueTuskDateCodec())
            .Register(Time, new BlueTuskTimeCodec())
            .Register(Timestamp, new BlueTuskTimestampCodec())
            .Register(TimestampWithTimeZone, new BlueTuskTimestampWithTimeZoneCodec())
            .Register(Interval, new BlueTuskIntervalCodec())
            .Register(TimeWithTimeZone, new BlueTuskTimeWithTimeZoneCodec())
            .Register(Numeric, new BlueTuskNumericCodec())
            .Register(RefCursor, new BlueTuskRefCursorCodec())
            .Register(Bit, new BlueTuskBitStringCodec())
            .Register(Varbit, new BlueTuskBitStringCodec())
            .Register(Uuid, new BlueTuskGuidCodec())
            .Register(RegProcedure, new BlueTuskObjectIdentifierCodec<BlueTuskRegProcedure>())
            .Register(RegOper, new BlueTuskObjectIdentifierCodec<BlueTuskRegOper>())
            .Register(RegOperator, new BlueTuskObjectIdentifierCodec<BlueTuskRegOperator>())
            .Register(RegClass, new BlueTuskObjectIdentifierCodec<BlueTuskRegClass>())
            .Register(RegType, new BlueTuskObjectIdentifierCodec<BlueTuskRegType>())
            .Register(TxidSnapshot, new BlueTuskTransactionSnapshotCodec())
            .Register(PgLsn, new BlueTuskLogSequenceNumberCodec())
            .Register(PgNDistinct, new BlueTuskNDistinctStatisticsCodec())
            .Register(PgDependencies, new BlueTuskDependencyStatisticsCodec())
            .Register(TextSearchVector, new BlueTuskTextSearchVectorCodec())
            .Register(TextSearchQuery, new BlueTuskTextSearchQueryCodec())
            .Register(GistTextSearchVector, new BlueTuskGistTextSearchVectorCodec())
            .Register(RegConfig, new BlueTuskObjectIdentifierCodec<BlueTuskRegConfig>())
            .Register(RegDictionary, new BlueTuskObjectIdentifierCodec<BlueTuskRegDictionary>())
            .Register(Jsonb, new BlueTuskJsonbCodec())
            .Register(JsonPath, new BlueTuskJsonPathCodec())
            .Register(RegNamespace, new BlueTuskObjectIdentifierCodec<BlueTuskRegNamespace>())
            .Register(RegRole, new BlueTuskObjectIdentifierCodec<BlueTuskRegRole>())
            .Register(RegCollation, new BlueTuskObjectIdentifierCodec<BlueTuskRegCollation>())
            .Register(PgBrinBloomSummary, new BlueTuskBrinBloomSummaryCodec())
            .Register(PgBrinMinMaxMultiSummary, new BlueTuskBrinMinMaxMultiSummaryCodec())
            .Register(PgMcvList, new BlueTuskMostCommonValueStatisticsCodec())
            .Register(PgSnapshot, new BlueTuskTransactionSnapshotCodec())
            .Register(Xid8, new BlueTuskFullTransactionIdCodec())
            .Register(Oid8, new BlueTuskObjectIdentifier64Codec())
            .Register(RegDatabase, new BlueTuskObjectIdentifierCodec<BlueTuskRegDatabase>())
            .Build();
    }

    private static BlueTuskTypeDescriptor Create(
        uint oid,
        string name,
        uint arrayOid,
        char delimiter = ',',
        uint? elementTypeOid = null) => new()
        {
            Id = new BlueTuskTypeId(oid),
            Schema = "pg_catalog",
            Name = name,
            Kind = BlueTuskTypeKind.Base,
            ElementType = elementTypeOid is null ? null : new BlueTuskTypeId(elementTypeOid.Value),
            ArrayType = new BlueTuskTypeId(arrayOid),
            Delimiter = delimiter,
        };

    private static BlueTuskTypeDescriptor CreateWithoutArray(uint oid, string name) => new()
    {
        Id = new BlueTuskTypeId(oid),
        Schema = "pg_catalog",
        Name = name,
        Kind = BlueTuskTypeKind.Base,
    };
}
