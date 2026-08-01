using BlueTusk.Data;
using BlueTusk.Extensions.Testing;
using BlueTusk.TypeSystem;
using Xunit.Sdk;

namespace BlueTusk.Extensions.Sample.Tests;

public sealed class SampleExtensionTests
{
    private static readonly BlueTuskTypeDescriptor Type = new()
    {
        Id = new BlueTuskTypeId(90_000),
        Schema = "public",
        Name = "sample_type",
        Kind = BlueTuskTypeKind.Base,
    };

    [Theory]
    [InlineData(BlueTuskDataFormat.Text)]
    [InlineData(BlueTuskDataFormat.Binary)]
    public void Codec_round_trips(BlueTuskDataFormat format)
    {
        var codec = new SampleCodec();
        var storage = new byte[128];
        var writer = new BlueTuskWriter(storage);
        var expected = new SampleValue("replace-with-a-representative-value");

        codec.WriteTyped(ref writer, expected, format, Type);
        var reader = new BlueTuskReader(storage.AsSpan(0, writer.WrittenCount));

        Assert.Equal(expected, codec.ReadTyped(ref reader, format, Type));
    }

    [Fact]
    public async Task Plugin_satisfies_the_live_extension_contract()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip(
                "BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        var builder = new BlueTuskDataSourceBuilder(connectionString).UseSample();
        await using var dataSource = builder.Build();
        _ = await BlueTuskExtensionCompatibility.VerifyAsync(
            dataSource,
            new BlueTuskExtensionContract
            {
                FeatureName = SampleFeature.RegistryName,
                FeatureType = typeof(SampleFeature),
                PostgreSqlType = new BlueTuskTypeName("public", "sample_type"),
                ClrType = typeof(SampleValue),
                CodecType = typeof(SampleCodec),
            });
    }
}
