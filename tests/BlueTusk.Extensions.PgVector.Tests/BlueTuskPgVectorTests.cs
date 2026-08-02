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
    private static readonly BlueTuskTypeDescriptor HalfVectorType = new()
    {
        Id = new BlueTuskTypeId(16_385),
        Schema = "public",
        Name = "halfvec",
        Kind = BlueTuskTypeKind.Base,
    };
    private static readonly BlueTuskTypeDescriptor SparseVectorType = new()
    {
        Id = new BlueTuskTypeId(16_386),
        Schema = "public",
        Name = "sparsevec",
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

    [Fact]
    public void Half_vector_value_and_codec_match_pgvector_wire_layout()
    {
        var value = BlueTuskHalfVector.FromSinglePrecision(1.5f, -2f, 0.25f);
        var codec = new BlueTuskHalfVectorCodec();
        var bytes = new byte[BlueTuskHalfVectorCodec.GetBinarySize(value)];
        var writer = new BlueTuskWriter(bytes);

        codec.WriteTyped(ref writer, value, BlueTuskDataFormat.Binary, HalfVectorType);

        Assert.Equal([0x00, 0x03, 0x00, 0x00, 0x3e, 0x00, 0xc0, 0x00, 0x34, 0x00], bytes);
        var reader = new BlueTuskReader(bytes);
        Assert.Equal(value, codec.ReadTyped(ref reader, BlueTuskDataFormat.Binary, HalfVectorType));
        Assert.Equal(value, BlueTuskHalfVector.Parse("[1.5,-2,0.25]"));
        Assert.Equal("[1.5,-2,0.25]", value.ToString());
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void Sparse_vector_value_and_codec_match_pgvector_wire_layout()
    {
        var value = new BlueTuskSparseVector(
            5,
            new(3, -2f),
            new(0, 1.5f));
        var codec = new BlueTuskSparseVectorCodec();
        var bytes = new byte[BlueTuskSparseVectorCodec.GetBinarySize(value)];
        var writer = new BlueTuskWriter(bytes);

        codec.WriteTyped(ref writer, value, BlueTuskDataFormat.Binary, SparseVectorType);

        Assert.Equal(
            [
                0x00, 0x00, 0x00, 0x05,
                0x00, 0x00, 0x00, 0x02,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x03,
                0x3f, 0xc0, 0x00, 0x00,
                0xc0, 0x00, 0x00, 0x00,
            ],
            bytes);
        var reader = new BlueTuskReader(bytes);
        Assert.Equal(value, codec.ReadTyped(ref reader, BlueTuskDataFormat.Binary, SparseVectorType));
        Assert.Equal(value, BlueTuskSparseVector.Parse("{4:-2,1:1.5}/5"));
        Assert.Equal("{1:1.5,4:-2}/5", value.ToString());
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void Half_and_sparse_values_reject_invalid_elements()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BlueTuskHalfVector.FromSinglePrecision(float.PositiveInfinity));
        Assert.Throws<ArgumentException>(() => new BlueTuskHalfVector([]));
        Assert.Throws<ArgumentException>(() =>
            new BlueTuskSparseVector(
                3,
                new BlueTuskSparseVectorElement(1, 1),
                new BlueTuskSparseVectorElement(1, 2)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BlueTuskSparseVector(3, new BlueTuskSparseVectorElement(3, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BlueTuskSparseVector(3, new BlueTuskSparseVectorElement(1, 0)));
    }

    [Fact]
    public void Half_and_sparse_codecs_reject_invalid_binary_payloads()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ReadHalfBinary([0x00, 0x01, 0x00, 0x00, 0x7c, 0x00]));
        Assert.Throws<InvalidOperationException>(() =>
            ReadSparseBinary(
            [
                0x00, 0x00, 0x00, 0x02,
                0x00, 0x00, 0x00, 0x01,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x02,
                0x3f, 0x80, 0x00, 0x00,
            ]));
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
        Assert.Equal(new BlueTuskTypeName("public", "halfvec"), feature.HalfVectorTypeName);
        Assert.Equal(new BlueTuskTypeName("public", "sparsevec"), feature.SparseVectorTypeName);
        Assert.False(dataSource.Features.Contains("test.late-feature"));
    }

    [Fact]
    public async Task Pgvector_plugin_executes_binary_array_and_distance_live_paths()
    {
        var connectionString = GetConnectionString();
        await RequireExtensionAvailableAsync(connectionString, "vector");
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
        var halfCompatibility = await BlueTuskExtensionCompatibility.VerifyAsync(
            dataSource,
            new BlueTuskExtensionContract
            {
                FeatureName = BlueTuskPgVectorFeature.RegistryName,
                FeatureType = typeof(BlueTuskPgVectorFeature),
                PostgreSqlType = new BlueTuskTypeName("public", "halfvec"),
                ClrType = typeof(BlueTuskHalfVector),
                CodecType = typeof(BlueTuskHalfVectorCodec),
            });
        Assert.Equal(typeof(BlueTuskHalfVectorCodec), halfCompatibility.CodecType);
        var sparseCompatibility = await BlueTuskExtensionCompatibility.VerifyAsync(
            dataSource,
            new BlueTuskExtensionContract
            {
                FeatureName = BlueTuskPgVectorFeature.RegistryName,
                FeatureType = typeof(BlueTuskPgVectorFeature),
                PostgreSqlType = new BlueTuskTypeName("public", "sparsevec"),
                ClrType = typeof(BlueTuskSparseVector),
                CodecType = typeof(BlueTuskSparseVectorCodec),
            });
        Assert.Equal(typeof(BlueTuskSparseVectorCodec), sparseCompatibility.CodecType);

        var value = new BlueTuskVector(1, 2, 3);
        BlueTuskVector[] values = [value, new(3, 2, 1)];
        var half = BlueTuskHalfVector.FromSinglePrecision(1, 2, 3);
        BlueTuskHalfVector[] halves = [half, BlueTuskHalfVector.FromSinglePrecision(3, 2, 1)];
        var sparse = new BlueTuskSparseVector(
            5,
            new BlueTuskSparseVectorElement(0, 1),
            new BlueTuskSparseVectorElement(3, 2));
        BlueTuskSparseVector[] sparseValues =
        [
            sparse,
            new BlueTuskSparseVector(5, new BlueTuskSparseVectorElement(1, 3)),
        ];
        await using var command = dataSource.CreateCommand(
            "SELECT $1::vector, $2::vector[], $1 <-> '[1,2,4]'::vector, " +
            "$3::halfvec, $4::halfvec[], $3 <-> '[1,2,4]'::halfvec, " +
            "$5::sparsevec, $6::sparsevec[], $5 <-> '{1:1,5:2}/5'::sparsevec, " +
            "B'101' <~> B'001', B'101' <%> B'001'");
        command.Parameters.Add(new BlueTuskParameter<BlueTuskVector>(value));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskVector[]>(values));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskHalfVector>(half));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskHalfVector[]>(halves));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskSparseVector>(sparse));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskSparseVector[]>(sparseValues));
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);

        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(value, reader.GetFieldValue<BlueTuskVector>(0));
        Assert.Equal(values, reader.GetFieldValue<BlueTuskVector[]>(1));
        Assert.Equal(1d, reader.GetDouble(2), 12);
        Assert.Equal("vector", reader.GetDataTypeName(0));
        Assert.Equal(half, reader.GetFieldValue<BlueTuskHalfVector>(3));
        Assert.Equal(halves, reader.GetFieldValue<BlueTuskHalfVector[]>(4));
        Assert.Equal(1d, reader.GetDouble(5), 12);
        Assert.Equal(sparse, reader.GetFieldValue<BlueTuskSparseVector>(6));
        Assert.Equal(sparseValues, reader.GetFieldValue<BlueTuskSparseVector[]>(7));
        Assert.Equal(Math.Sqrt(8), reader.GetDouble(8), 12);
        Assert.Equal(1d, reader.GetDouble(9), 12);
        Assert.Equal(0.5d, reader.GetDouble(10), 12);
    }

    private static async Task RequireExtensionAvailableAsync(
        string connectionString,
        string extensionName)
    {
        await using var dataSource = BlueTuskDataSource.Create(connectionString);
        await using var command = dataSource.CreateCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = $1)");
        command.Parameters.Add(new BlueTuskParameter<string>(extensionName));
        if (!await command.ExecuteScalarAsync<bool>(CancellationToken.None))
        {
            throw SkipException.ForSkip(
                $"PostgreSQL extension '{extensionName}' is not available on the configured server.");
        }
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

    private static BlueTuskHalfVector ReadHalfBinary(byte[] bytes)
    {
        var reader = new BlueTuskReader(bytes);
        return new BlueTuskHalfVectorCodec().ReadTyped(
            ref reader,
            BlueTuskDataFormat.Binary,
            HalfVectorType);
    }

    private static BlueTuskSparseVector ReadSparseBinary(byte[] bytes)
    {
        var reader = new BlueTuskReader(bytes);
        return new BlueTuskSparseVectorCodec().ReadTyped(
            ref reader,
            BlueTuskDataFormat.Binary,
            SparseVectorType);
    }
}
