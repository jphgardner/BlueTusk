using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.Extensions.Testing;
using BlueTusk.TypeSystem;
using Xunit.Sdk;

namespace BlueTusk.Extensions.PostGIS.Tests;

public sealed class BlueTuskPostGisTests
{
    private static readonly BlueTuskTypeDescriptor GeometryType = Type("geometry", 16_389);
    private static readonly BlueTuskTypeDescriptor GeographyType = Type("geography", 16_390);

    [Fact]
    public void Spatial_values_are_immutable_and_reject_invalid_representations()
    {
        byte[] source = [1, 1, 0, 0, 0];
        var geometry = BlueTuskGeometry.FromWellKnownBinary(source);
        source[1] = 99;

        Assert.Equal(1, geometry.GetWellKnownBinary()[1]);
        Assert.Equal(geometry, BlueTuskGeometry.FromWellKnownBinary([1, 1, 0, 0, 0]));
        Assert.Equal(
            BlueTuskGeography.FromText("SRID=4326;POINT(0 0)"),
            BlueTuskGeography.FromText("SRID=4326;POINT(0 0)"));
        Assert.Throws<ArgumentException>(() => BlueTuskGeometry.FromWellKnownBinary([1, 1]));
        Assert.Throws<ArgumentException>(() => BlueTuskGeography.FromWellKnownBinary([2, 1, 0, 0, 0]));
        Assert.Throws<ArgumentException>(() => BlueTuskGeometry.FromText(" "));
    }

    [Fact]
    public void Codecs_select_text_or_binary_and_preserve_exact_ewkb()
    {
        var ewkb = new byte[] { 1, 1, 0, 0, 0 };
        var binary = BlueTuskGeometry.FromWellKnownBinary(ewkb);
        var text = BlueTuskGeometry.FromText("POINT(0 0)");
        var codec = new BlueTuskGeometryCodec();

        Assert.Equal(BlueTuskDataFormat.Binary, codec.GetPreferredWriteFormat(binary, GeometryType));
        Assert.Equal(BlueTuskDataFormat.Text, codec.GetPreferredWriteFormat(text, GeometryType));

        var bytes = new byte[ewkb.Length];
        var writer = new BlueTuskWriter(bytes);
        codec.WriteTyped(ref writer, binary, BlueTuskDataFormat.Binary, GeometryType);
        Assert.Equal(ewkb, bytes);
        var reader = new BlueTuskReader(bytes);
        Assert.Equal(binary, codec.ReadTyped(ref reader, BlueTuskDataFormat.Binary, GeometryType));

        var textBytes = new byte[32];
        var textWriter = new BlueTuskWriter(textBytes);
        codec.WriteTyped(ref textWriter, binary, BlueTuskDataFormat.Text, GeometryType);
        Assert.Equal("0101000000", System.Text.Encoding.UTF8.GetString(textBytes[..textWriter.WrittenCount]));
    }

    [Fact]
    public void Build_carries_both_spatial_types_and_an_immutable_feature_snapshot()
    {
        var builder = new BlueTuskDataSourceBuilder(
            "Host=localhost;Username=test;Password=test");
        Assert.Same(builder, builder.UsePostGis());

        using var dataSource = builder.Build();
        builder.Features.Register("test.late-feature", 42);

        var feature = dataSource.Features.GetRequired<BlueTuskPostGisFeature>(
            BlueTuskPostGisFeature.RegistryName);
        Assert.Equal(new BlueTuskTypeName("public", "geometry"), feature.GeometryTypeName);
        Assert.Equal(new BlueTuskTypeName("public", "geography"), feature.GeographyTypeName);
        Assert.False(dataSource.Features.Contains("test.late-feature"));
    }

    [Fact]
    public async Task Postgis_plugin_executes_text_binary_array_and_spatial_live_paths()
    {
        var connectionString = GetConnectionString();
        await RequireExtensionAvailableAsync(connectionString, "postgis");
        await using (var administration = BlueTuskDataSource.Create(connectionString))
        await using (var create = administration.CreateCommand("CREATE EXTENSION IF NOT EXISTS postgis"))
        {
            _ = await create.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var builder = new BlueTuskDataSourceBuilder(connectionString).UsePostGis();
        await using var dataSource = builder.Build();
        await VerifyTypeAsync<BlueTuskGeometry, BlueTuskGeometryCodec>(
            dataSource,
            new BlueTuskTypeName("public", "geometry"));
        await VerifyTypeAsync<BlueTuskGeography, BlueTuskGeographyCodec>(
            dataSource,
            new BlueTuskTypeName("public", "geography"));

        var geometry = BlueTuskGeometry.FromText("SRID=4326;POINT(-0.1276 51.5072)");
        var geography = BlueTuskGeography.FromText("SRID=4326;POINT(-0.1276 51.5072)");
        BlueTuskGeometry[] geometries = [geometry, BlueTuskGeometry.FromText("POINT(0 0)")];
        BlueTuskGeometry binaryGeometry;
        await using (var command = dataSource.CreateCommand(
                         "SELECT $1::geometry, $2::geography, $3::geometry[], " +
                         "ST_SRID($1), ST_AsText($1), " +
                         "ST_Distance($2, 'SRID=4326;POINT(-0.1276 51.5072)'::geography)"))
        {
            command.Parameters.Add(new BlueTuskParameter<BlueTuskGeometry>(geometry));
            command.Parameters.Add(new BlueTuskParameter<BlueTuskGeography>(geography));
            command.Parameters.Add(new BlueTuskParameter<BlueTuskGeometry[]>(geometries));
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);

            Assert.True(await reader.ReadAsync(CancellationToken.None));
            binaryGeometry = reader.GetFieldValue<BlueTuskGeometry>(0);
            Assert.True(binaryGeometry.HasWellKnownBinary);
            Assert.True(reader.GetFieldValue<BlueTuskGeography>(1).HasWellKnownBinary);
            Assert.All(reader.GetFieldValue<BlueTuskGeometry[]>(2), value => Assert.True(value.HasWellKnownBinary));
            Assert.Equal(4326, reader.GetInt32(3));
            Assert.Equal("POINT(-0.1276 51.5072)", reader.GetString(4));
            Assert.Equal(0d, reader.GetDouble(5), 8);
        }

        await using var reuse = dataSource.CreateCommand(
            "SELECT ST_Equals($1::geometry, 'SRID=4326;POINT(-0.1276 51.5072)'::geometry)");
        reuse.Parameters.Add(new BlueTuskParameter<BlueTuskGeometry>(binaryGeometry));
        Assert.True(await reuse.ExecuteScalarAsync<bool>(CancellationToken.None));
    }

    private static async ValueTask VerifyTypeAsync<TValue, TCodec>(
        BlueTuskDataSource dataSource,
        BlueTuskTypeName typeName)
        where TCodec : IBlueTuskCodec
    {
        var compatibility = await BlueTuskExtensionCompatibility.VerifyAsync(
            dataSource,
            new BlueTuskExtensionContract
            {
                FeatureName = BlueTuskPostGisFeature.RegistryName,
                FeatureType = typeof(BlueTuskPostGisFeature),
                PostgreSqlType = typeName,
                ClrType = typeof(TValue),
                CodecType = typeof(TCodec),
            });
        Assert.Equal(typeof(TCodec), compatibility.CodecType);
    }

    private static BlueTuskTypeDescriptor Type(string name, uint oid) => new()
    {
        Id = new BlueTuskTypeId(oid),
        Schema = "public",
        Name = name,
        Kind = BlueTuskTypeKind.Base,
    };

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
}
