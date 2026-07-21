using System.Text;

namespace BlueTusk.TypeSystem.Tests;

public sealed class BlueTuskTypeRegistryTests
{
    [Fact]
    public void Initial_registry_resolves_int4_by_catalogue_oid()
    {
        var registry = BlueTuskBuiltInTypes.CreateInitialRegistry();

        Assert.True(registry.TryGetType(new BlueTuskTypeId(23), out var type));
        Assert.Equal("pg_catalog.int4", type!.QualifiedName);
        Assert.True(registry.TryGetCodec(type.Id, out var codec));
        Assert.IsType<BlueTuskInt32Codec>(codec);
    }

    [Theory]
    [InlineData(16, "bool", typeof(bool), typeof(BlueTuskBooleanCodec))]
    [InlineData(17, "bytea", typeof(byte[]), typeof(BlueTuskByteArrayCodec))]
    [InlineData(18, "char", typeof(string), typeof(BlueTuskStringCodec))]
    [InlineData(19, "name", typeof(string), typeof(BlueTuskStringCodec))]
    [InlineData(20, "int8", typeof(long), typeof(BlueTuskInt64Codec))]
    [InlineData(21, "int2", typeof(short), typeof(BlueTuskInt16Codec))]
    [InlineData(23, "int4", typeof(int), typeof(BlueTuskInt32Codec))]
    [InlineData(25, "text", typeof(string), typeof(BlueTuskStringCodec))]
    [InlineData(26, "oid", typeof(uint), typeof(BlueTuskUInt32Codec))]
    [InlineData(27, "tid", typeof(BlueTuskTupleId), typeof(BlueTuskTupleIdCodec))]
    [InlineData(114, "json", typeof(string), typeof(BlueTuskStringCodec))]
    [InlineData(142, "xml", typeof(string), typeof(BlueTuskStringCodec))]
    [InlineData(700, "float4", typeof(float), typeof(BlueTuskSingleCodec))]
    [InlineData(701, "float8", typeof(double), typeof(BlueTuskDoubleCodec))]
    [InlineData(1042, "bpchar", typeof(string), typeof(BlueTuskStringCodec))]
    [InlineData(1043, "varchar", typeof(string), typeof(BlueTuskStringCodec))]
    [InlineData(1082, "date", typeof(DateOnly), typeof(BlueTuskDateCodec))]
    [InlineData(1083, "time", typeof(TimeSpan), typeof(BlueTuskTimeCodec))]
    [InlineData(1114, "timestamp", typeof(DateTime), typeof(BlueTuskTimestampCodec))]
    [InlineData(1184, "timestamptz", typeof(DateTimeOffset), typeof(BlueTuskTimestampWithTimeZoneCodec))]
    [InlineData(1186, "interval", typeof(BlueTuskInterval), typeof(BlueTuskIntervalCodec))]
    [InlineData(1266, "timetz", typeof(BlueTuskTimeWithTimeZone), typeof(BlueTuskTimeWithTimeZoneCodec))]
    [InlineData(1560, "bit", typeof(BlueTuskBitString), typeof(BlueTuskBitStringCodec))]
    [InlineData(1562, "varbit", typeof(BlueTuskBitString), typeof(BlueTuskBitStringCodec))]
    [InlineData(1700, "numeric", typeof(BlueTuskNumeric), typeof(BlueTuskNumericCodec))]
    [InlineData(2950, "uuid", typeof(Guid), typeof(BlueTuskGuidCodec))]
    [InlineData(3220, "pg_lsn", typeof(BlueTuskLogSequenceNumber), typeof(BlueTuskLogSequenceNumberCodec))]
    [InlineData(3802, "jsonb", typeof(string), typeof(BlueTuskJsonbCodec))]
    public void Registry_resolves_core_scalar_codecs(
        uint oid,
        string name,
        Type clrType,
        Type codecType)
    {
        var registry = BlueTuskBuiltInTypes.CreateRegistry();

        Assert.True(registry.TryGetType(new BlueTuskTypeId(oid), out var type));
        Assert.Equal(name, type!.Name);
        Assert.True(registry.TryGetCodec(type.Id, out var codec));
        Assert.Equal(clrType, codec!.ClrType);
        Assert.IsType(codecType, codec);
    }

    [Fact]
    public void Unknown_text_values_remain_accessible()
    {
        var type = new BlueTuskTypeDescriptor
        {
            Id = new BlueTuskTypeId(90001),
            Schema = "future",
            Name = "new_type",
            Kind = BlueTuskTypeKind.Unknown,
        };
        var value = new BlueTuskUnknownValue(type, BlueTuskDataFormat.Text, Encoding.UTF8.GetBytes("future-value"));

        Assert.Equal("future-value", value.GetText());
    }
}
