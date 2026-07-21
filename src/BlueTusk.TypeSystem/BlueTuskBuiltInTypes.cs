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

    public static BlueTuskTypeDescriptor Int4 { get; } = Create(23, "int4", 1007);

    public static BlueTuskTypeDescriptor Text { get; } = Create(25, "text", 1009);

    public static BlueTuskTypeDescriptor Oid { get; } = Create(26, "oid", 1028);

    public static BlueTuskTypeDescriptor Json { get; } = Create(114, "json", 199);

    public static BlueTuskTypeDescriptor Xml { get; } = Create(142, "xml", 143);

    public static BlueTuskTypeDescriptor Float4 { get; } = Create(700, "float4", 1021);

    public static BlueTuskTypeDescriptor Float8 { get; } = Create(701, "float8", 1022);

    public static BlueTuskTypeDescriptor Bpchar { get; } = Create(1042, "bpchar", 1014);

    public static BlueTuskTypeDescriptor Varchar { get; } = Create(1043, "varchar", 1015);

    public static BlueTuskTypeDescriptor Date { get; } = Create(1082, "date", 1182);

    public static BlueTuskTypeDescriptor Time { get; } = Create(1083, "time", 1183);

    public static BlueTuskTypeDescriptor Timestamp { get; } = Create(1114, "timestamp", 1115);

    public static BlueTuskTypeDescriptor TimestampWithTimeZone { get; } = Create(1184, "timestamptz", 1185);

    public static BlueTuskTypeDescriptor Numeric { get; } = Create(1700, "numeric", 1231);

    public static BlueTuskTypeDescriptor Uuid { get; } = Create(2950, "uuid", 2951);

    public static BlueTuskTypeDescriptor Jsonb { get; } = Create(3802, "jsonb", 3807);

    public static BlueTuskTypeRegistry CreateInitialRegistry() => CreateRegistry();

    public static BlueTuskTypeRegistry CreateRegistry()
    {
        var textCodec = new BlueTuskStringCodec();
        return new BlueTuskTypeRegistryBuilder()
            .Register(Boolean, new BlueTuskBooleanCodec())
            .Register(Bytea, new BlueTuskByteArrayCodec())
            .Register(Char, textCodec)
            .Register(Name, textCodec)
            .Register(Int8, new BlueTuskInt64Codec())
            .Register(Int2, new BlueTuskInt16Codec())
            .Register(Int4, new BlueTuskInt32Codec())
            .Register(Text, textCodec)
            .Register(Oid, new BlueTuskUInt32Codec())
            .Register(Json, textCodec)
            .Register(Xml, textCodec)
            .Register(Float4, new BlueTuskSingleCodec())
            .Register(Float8, new BlueTuskDoubleCodec())
            .Register(Bpchar, textCodec)
            .Register(Varchar, textCodec)
            .Register(Date)
            .Register(Time)
            .Register(Timestamp)
            .Register(TimestampWithTimeZone)
            .Register(Numeric)
            .Register(Uuid, new BlueTuskGuidCodec())
            .Register(Jsonb, new BlueTuskJsonbCodec())
            .Build();
    }

    private static BlueTuskTypeDescriptor Create(uint oid, string name, uint arrayOid) => new()
    {
        Id = new BlueTuskTypeId(oid),
        Schema = "pg_catalog",
        Name = name,
        Kind = BlueTuskTypeKind.Base,
        ArrayType = new BlueTuskTypeId(arrayOid),
    };
}
