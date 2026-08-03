using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.Extensions.Testing;
using BlueTusk.TypeSystem;
using Xunit.Sdk;

namespace BlueTusk.Extensions.Citext.Tests;

public sealed class BlueTuskCitextTests
{
    private static readonly BlueTuskTypeDescriptor CitextType = new()
    {
        Id = new BlueTuskTypeId(90_500),
        Schema = "public",
        Name = "citext",
        Kind = BlueTuskTypeKind.Base,
    };

    [Theory]
    [InlineData(BlueTuskDataFormat.Text)]
    [InlineData(BlueTuskDataFormat.Binary)]
    public void Codec_round_trips_utf8_without_applying_client_side_case_rules(
        BlueTuskDataFormat format)
    {
        var codec = new BlueTuskCitextCodec();
        var bytes = new byte[128];
        var writer = new BlueTuskWriter(bytes);
        var expected = new BlueTuskCitext("Chloé-BlueTusk");

        codec.WriteTyped(ref writer, expected, format, CitextType);
        var reader = new BlueTuskReader(bytes.AsSpan(0, writer.WrittenCount));

        Assert.Equal(expected, codec.ReadTyped(ref reader, format, CitextType));
    }

    [Fact]
    public void Build_carries_an_immutable_feature_snapshot()
    {
        var builder = new BlueTuskDataSourceBuilder(
            "Host=localhost;Username=test;Password=test");
        Assert.Same(builder, builder.UseCitext());

        using var dataSource = builder.Build();
        builder.Features.Register("test.late-feature", 42);

        var feature = dataSource.Features.GetRequired<BlueTuskCitextFeature>(
            BlueTuskCitextFeature.RegistryName);
        Assert.Equal(new BlueTuskTypeName("public", "citext"), feature.TypeName);
        Assert.False(dataSource.Features.Contains("test.late-feature"));
        Assert.Single(dataSource.Features.Names);
    }

    [Fact]
    public async Task Citext_plugin_executes_binary_text_array_and_case_insensitive_live_paths()
    {
        var connectionString = GetConnectionString();
        await using (var administration = BlueTuskDataSource.Create(connectionString))
        await using (var create = administration.CreateCommand("CREATE EXTENSION IF NOT EXISTS citext"))
        {
            _ = await create.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var builder = new BlueTuskDataSourceBuilder(connectionString).UseCitext();
        await using var dataSource = builder.Build();
        var compatibility = await BlueTuskExtensionCompatibility.VerifyAsync(
            dataSource,
            new BlueTuskExtensionContract
            {
                FeatureName = BlueTuskCitextFeature.RegistryName,
                FeatureType = typeof(BlueTuskCitextFeature),
                PostgreSqlType = new BlueTuskTypeName("public", "citext"),
                ClrType = typeof(BlueTuskCitext),
                CodecType = typeof(BlueTuskCitextCodec),
            });
        Assert.Equal(typeof(BlueTuskCitextCodec), compatibility.CodecType);

        Assert.True(dataSource.TypeRegistry.TryGetType(typeof(BlueTuskCitext), out var type, out var codec));
        Assert.Equal("citext", type!.Name);
        Assert.IsType<BlueTuskCitextCodec>(codec);

        var value = new BlueTuskCitext("BlueTusk");
        BlueTuskCitext[] values = [value, new("PostgreSQL")];
        await using (var command = dataSource.CreateCommand(
            "SELECT $1::citext = 'bluetusk'::citext, $1::citext, $2::citext[]"))
        {
            command.Parameters.Add(new BlueTuskParameter<BlueTuskCitext>(value));
            command.Parameters.Add(new BlueTuskParameter<BlueTuskCitext[]>(values));
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.True(reader.GetBoolean(0));
            Assert.Equal(value, reader.GetFieldValue<BlueTuskCitext>(1));
            Assert.Equal(values, reader.GetFieldValue<BlueTuskCitext[]>(2));
        }

        await using (var literal = dataSource.CreateCommand("SELECT 'MiXeD'::citext"))
        await using (var reader = await literal.ExecuteReaderAsync(CancellationToken.None))
        {
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal(new BlueTuskCitext("MiXeD"), reader.GetFieldValue<BlueTuskCitext>(0));
            Assert.Equal("citext", reader.GetDataTypeName(0));
        }
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        var settings = new BlueTuskConnectionStringBuilder(connectionString)
        {
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        };
        return settings.ConnectionString;
    }
}
