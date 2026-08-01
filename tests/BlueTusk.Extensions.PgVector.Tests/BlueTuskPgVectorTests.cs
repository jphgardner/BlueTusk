using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.Extensions.Testing;
using BlueTusk.TypeSystem;
using Xunit.Sdk;

namespace BlueTusk.Extensions.PgVector.Tests;

public sealed class BlueTuskPgVectorTests
{
    private static readonly BlueTuskTypeDescriptor VectorType = new()
    {
        Id = new BlueTuskTypeId(16_384),
        Schema = "public",
        Name = "vector",
        Kind = BlueTuskTypeKind.Base,
    };

    [Fact]
    public void Value_is_immutable_structurally_equal_and_uses_pgvector_text()
    {
        float[] source = [1.5f, -2f, 0.25f];
        var value = new BlueTuskVector(source);
        source[0] = 99;

        Assert.Equal(1.5f, value[0]);
        Assert.Equal(new BlueTuskVector(1.5f, -2f, 0.25f), value);
        Assert.Equal("[1.5,-2,0.25]", value.ToString());
        Assert.Equal(value, BlueTuskVector.Parse(" [ 1.5, -2, 2.5e-1 ] "));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("1,2")]
    [InlineData("[1,]")]
    [InlineData("[NaN]")]
    [InlineData("[Infinity]")]
    public void Value_rejects_invalid_text(string value)
    {
        Assert.ThrowsAny<Exception>(() => BlueTuskVector.Parse(value));
    }

    [Fact]
    public void Binary_codec_matches_pgvector_wire_layout()
    {
        var value = new BlueTuskVector(1.5f, -2f, 0.25f);
        var codec = new BlueTuskVectorCodec();
        var bytes = new byte[BlueTuskVectorCodec.GetBinarySize(value)];
        var writer = new BlueTuskWriter(bytes);

        codec.WriteTyped(ref writer, value, BlueTuskDataFormat.Binary, VectorType);

        Assert.Equal(
            [
                0x00, 0x03, 0x00, 0x00,
                0x3f, 0xc0, 0x00, 0x00,
                0xc0, 0x00, 0x00, 0x00,
                0x3e, 0x80, 0x00, 0x00,
            ],
            bytes);
        var reader = new BlueTuskReader(bytes);
        Assert.Equal(value, codec.ReadTyped(ref reader, BlueTuskDataFormat.Binary, VectorType));
        Assert.Equal(0, reader.Remaining);
    }

    [Theory]
    [InlineData(new byte[] { 0, 1, 0, 1, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 0, 2, 0, 0, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 0, 1, 0, 0, 0x7f, 0xc0, 0, 0 })]
    public void Binary_codec_rejects_invalid_payload(byte[] bytes)
    {
        Assert.Throws<InvalidOperationException>(() => ReadBinary(bytes));
    }

    [Fact]
    public void Build_carries_pgvector_type_and_immutable_feature_snapshot()
    {
        var builder = new BlueTuskDataSourceBuilder(
            "Host=localhost;Username=test;Password=test");
        Assert.Same(builder, builder.UsePgVector());

        using var dataSource = builder.Build();
        builder.Features.Register("test.late-feature", 42);

        var feature = dataSource.Features.GetRequired<BlueTuskPgVectorFeature>(
            BlueTuskPgVectorFeature.RegistryName);
        Assert.Equal(new BlueTuskTypeName("public", "vector"), feature.TypeName);
        Assert.False(dataSource.Features.Contains("test.late-feature"));
    }

    [Fact]
    public async Task Pgvector_plugin_executes_binary_array_and_distance_live_paths()
    {
        var connectionString = GetConnectionString();
        await using (var administration = BlueTuskDataSource.Create(connectionString))
        await using (var create = administration.CreateCommand("CREATE EXTENSION IF NOT EXISTS vector"))
        {
            _ = await create.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var builder = new BlueTuskDataSourceBuilder(connectionString).UsePgVector();
        await using var dataSource = builder.Build();
        var compatibility = await BlueTuskExtensionCompatibility.VerifyAsync(
            dataSource,
            new BlueTuskExtensionContract
            {
                FeatureName = BlueTuskPgVectorFeature.RegistryName,
                FeatureType = typeof(BlueTuskPgVectorFeature),
                PostgreSqlType = new BlueTuskTypeName("public", "vector"),
                ClrType = typeof(BlueTuskVector),
                CodecType = typeof(BlueTuskVectorCodec),
            });
        Assert.Equal(typeof(BlueTuskVectorCodec), compatibility.CodecType);

        var value = new BlueTuskVector(1, 2, 3);
        BlueTuskVector[] values = [value, new(3, 2, 1)];
        await using var command = dataSource.CreateCommand(
            "SELECT $1::vector, $2::vector[], $1 <-> '[1,2,4]'::vector");
        command.Parameters.Add(new BlueTuskParameter<BlueTuskVector>(value));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskVector[]>(values));
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);

        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(value, reader.GetFieldValue<BlueTuskVector>(0));
        Assert.Equal(values, reader.GetFieldValue<BlueTuskVector[]>(1));
        Assert.Equal(1d, reader.GetDouble(2), 12);
        Assert.Equal("vector", reader.GetDataTypeName(0));
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

    private static BlueTuskVector ReadBinary(byte[] bytes)
    {
        var reader = new BlueTuskReader(bytes);
        return new BlueTuskVectorCodec().ReadTyped(
            ref reader,
            BlueTuskDataFormat.Binary,
            VectorType);
    }
}
