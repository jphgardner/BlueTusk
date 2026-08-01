using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.Extensions.Sample;
using BlueTusk.Extensions.Testing;
using BlueTusk.TypeSystem;
using Xunit.Sdk;

namespace BlueTusk.ExtensionTemplate.Tests;

public sealed class ExtensionTemplateContractTests
{
    private static readonly BlueTuskTypeDescriptor SampleType = new()
    {
        Id = new BlueTuskTypeId(90_000),
        Schema = "public",
        Name = "sample_type",
        Kind = BlueTuskTypeKind.Base,
    };

    [Theory]
    [InlineData(BlueTuskDataFormat.Text)]
    [InlineData(BlueTuskDataFormat.Binary)]
    public void Generated_codec_round_trips(BlueTuskDataFormat format)
    {
        var codec = new SampleCodec();
        var storage = new byte[128];
        var writer = new BlueTuskWriter(storage);
        var expected = new SampleValue("template-contract");

        codec.WriteTyped(ref writer, expected, format, SampleType);
        var reader = new BlueTuskReader(storage.AsSpan(0, writer.WrittenCount));

        Assert.Equal(expected, codec.ReadTyped(ref reader, format, SampleType));
    }

    [Fact]
    public void Generated_plugin_survives_the_immutable_data_source_snapshot()
    {
        var builder = new BlueTuskDataSourceBuilder(
            "Host=localhost;Database=test;Username=test;Password=test").UseSample();
        using var dataSource = builder.Build();

        Assert.IsType<SampleFeature>(
            dataSource.Features.GetRequired<SampleFeature>(SampleFeature.RegistryName));
    }

    [Fact]
    public async Task Generated_plugin_satisfies_the_live_compatibility_contract()
    {
        var connectionString = GetConnectionString();
        await using var administration = BlueTuskDataSource.Create(connectionString);
        await using (var reset = administration.CreateCommand(
                         "DROP DOMAIN IF EXISTS public.sample_type; " +
                         "CREATE DOMAIN public.sample_type AS text"))
        {
            _ = await reset.ExecuteNonQueryAsync(CancellationToken.None);
        }

        try
        {
            var builder = new BlueTuskDataSourceBuilder(connectionString).UseSample();
            await using var dataSource = builder.Build();
            var report = await BlueTuskExtensionCompatibility.VerifyAsync(
                dataSource,
                new BlueTuskExtensionContract
                {
                    FeatureName = SampleFeature.RegistryName,
                    FeatureType = typeof(SampleFeature),
                    PostgreSqlType = new BlueTuskTypeName("public", "sample_type"),
                    ClrType = typeof(SampleValue),
                    CodecType = typeof(SampleCodec),
                });

            Assert.Equal("sample_type", report.PostgreSqlType.Name);
        }
        finally
        {
            await using var drop = administration.CreateCommand(
                "DROP DOMAIN IF EXISTS public.sample_type");
            _ = await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip(
                "BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        var settings = new BlueTuskConnectionStringBuilder(connectionString)
        {
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        };
        return settings.ConnectionString;
    }
}
