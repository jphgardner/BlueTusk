using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.Extensions.Testing;
using BlueTusk.TypeSystem;
using Xunit.Sdk;

namespace BlueTusk.Extensions.HStore.Tests;

public sealed class BlueTuskHStoreTests
{
    private static readonly BlueTuskTypeDescriptor HStoreType = new()
    {
        Id = new BlueTuskTypeId(16_385),
        Schema = "public",
        Name = "hstore",
        Kind = BlueTuskTypeKind.Base,
    };

    [Fact]
    public void Value_is_immutable_order_independent_and_round_trips_text()
    {
        var source = new Dictionary<string, string?>
        {
            ["owner"] = "Blue\\\"Tusk",
            ["missing"] = null,
            ["empty"] = string.Empty,
        };
        var value = new BlueTuskHStore(source);
        source["owner"] = "changed";
        var reordered = new BlueTuskHStore(
            new("empty", string.Empty),
            new("owner", "Blue\\\"Tusk"),
            new("missing", null));

        Assert.Equal("Blue\\\"Tusk", value["owner"]);
        Assert.Equal(reordered, value);
        Assert.Equal(value.GetHashCode(), reordered.GetHashCode());
        Assert.Equal(value, BlueTuskHStore.Parse(value.ToString()));
        Assert.Equal(
            new BlueTuskHStore(new("", "empty key"), new("a", null), new("b", "NULL")),
            BlueTuskHStore.Parse("\"\" => \"empty key\", a => null, b => \"NULL\""));
    }

    [Fact]
    public void Binary_codec_matches_hstore_wire_layout()
    {
        var value = new BlueTuskHStore(new KeyValuePair<string, string?>("a", "é"));
        var codec = new BlueTuskHStoreCodec();
        var bytes = new byte[BlueTuskHStoreCodec.GetBinarySize(value)];
        var writer = new BlueTuskWriter(bytes);

        codec.WriteTyped(ref writer, value, BlueTuskDataFormat.Binary, HStoreType);

        Assert.Equal(
            [
                0x00, 0x00, 0x00, 0x01,
                0x00, 0x00, 0x00, 0x01, 0x61,
                0x00, 0x00, 0x00, 0x02, 0xc3, 0xa9,
            ],
            bytes);
        Assert.Equal(value, ReadBinary(bytes));
    }

    [Theory]
    [InlineData(new byte[] { 0, 0, 0, 1 })]
    [InlineData(new byte[] { 0, 0, 0, 255 })]
    [InlineData(new byte[] { 0, 0, 0, 1, 255, 255, 255, 255 })]
    [InlineData(new byte[] { 0, 0, 0, 1, 0, 0, 0, 1, 0xff, 255, 255, 255, 255 })]
    public void Binary_codec_rejects_invalid_payload(byte[] bytes)
    {
        Assert.Throws<InvalidOperationException>(() => ReadBinary(bytes));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("a=>")]
    [InlineData("a=>b,")]
    [InlineData("\"unterminated=>b")]
    [InlineData("a=>b,a=>c")]
    public void Value_rejects_invalid_text(string value)
    {
        Assert.Throws<FormatException>(() => BlueTuskHStore.Parse(value));
    }

    [Fact]
    public void Build_carries_hstore_type_and_immutable_feature_snapshot()
    {
        var builder = new BlueTuskDataSourceBuilder(
            "Host=localhost;Username=test;Password=test");
        Assert.Same(builder, builder.UseHStore());

        using var dataSource = builder.Build();
        builder.Features.Register("test.late-feature", 42);

        var feature = dataSource.Features.GetRequired<BlueTuskHStoreFeature>(
            BlueTuskHStoreFeature.RegistryName);
        Assert.Equal(new BlueTuskTypeName("public", "hstore"), feature.TypeName);
        Assert.False(dataSource.Features.Contains("test.late-feature"));
    }

    [Fact]
    public async Task Hstore_plugin_executes_binary_array_and_operator_live_paths()
    {
        var connectionString = GetConnectionString();
        await using (var administration = BlueTuskDataSource.Create(connectionString))
        await using (var create = administration.CreateCommand("CREATE EXTENSION IF NOT EXISTS hstore"))
        {
            _ = await create.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var builder = new BlueTuskDataSourceBuilder(connectionString).UseHStore();
        await using var dataSource = builder.Build();
        var compatibility = await BlueTuskExtensionCompatibility.VerifyAsync(
            dataSource,
            new BlueTuskExtensionContract
            {
                FeatureName = BlueTuskHStoreFeature.RegistryName,
                FeatureType = typeof(BlueTuskHStoreFeature),
                PostgreSqlType = new BlueTuskTypeName("public", "hstore"),
                ClrType = typeof(BlueTuskHStore),
                CodecType = typeof(BlueTuskHStoreCodec),
            });
        Assert.Equal(typeof(BlueTuskHStoreCodec), compatibility.CodecType);

        var value = new BlueTuskHStore(
            new("owner", "Chloé \"BlueTusk\""),
            new("missing", null));
        BlueTuskHStore[] values =
        [
            value,
            new(new KeyValuePair<string, string?>("owner", "PostgreSQL")),
        ];
        await using var command = dataSource.CreateCommand(
            "SELECT $1::hstore, $2::hstore[], $1 ? 'owner', $1 -> 'owner', defined($1, 'missing')");
        command.Parameters.Add(new BlueTuskParameter<BlueTuskHStore>(value));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskHStore[]>(values));
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);

        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(value, reader.GetFieldValue<BlueTuskHStore>(0));
        Assert.Equal(values, reader.GetFieldValue<BlueTuskHStore[]>(1));
        Assert.True(reader.GetBoolean(2));
        Assert.Equal("Chloé \"BlueTusk\"", reader.GetString(3));
        Assert.False(reader.GetBoolean(4));
        Assert.Equal("hstore", reader.GetDataTypeName(0));
    }

    private static BlueTuskHStore ReadBinary(byte[] bytes)
    {
        var reader = new BlueTuskReader(bytes);
        return new BlueTuskHStoreCodec().ReadTyped(
            ref reader,
            BlueTuskDataFormat.Binary,
            HStoreType);
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        return new BlueTuskConnectionStringBuilder(connectionString)
        {
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        }.ConnectionString;
    }
}
