using System.Text;
using BlueTusk.TypeSystem;

namespace BlueTusk.SourceGeneration.Tests;

public sealed class BlueTuskGeneratedCompositeCodecTests
{
    private static readonly BlueTuskTypeId AddressId = new(91_400);
    private static readonly BlueTuskTypeId AddressArrayId = new(91_401);

    [Theory]
    [InlineData(BlueTuskDataFormat.Binary)]
    [InlineData(BlueTuskDataFormat.Text)]
    public void Generated_private_construction_and_member_access_round_trip(
        BlueTuskDataFormat format)
    {
        var configuredTypes = GeneratedAddress.RegisterBlueTuskCodec(
            new BlueTuskTypeRegistryBuilder()).Build();
        var registry = BlueTuskTypeCatalogue.BuildRegistry(CreateCatalogue(), configuredTypes);
        var type = Assert.Single(registry.Types, candidate => candidate.Id == AddressId);
        Assert.True(registry.TryGetCodec(type.Id, out var registered));
        var codec = Assert.IsType<BlueTuskCompositeCodec<GeneratedAddress>>(registered);
        var expected = GeneratedAddress.Create(42, "Main Street", "rear entrance");

        var destination = new byte[4096];
        var writer = new BlueTuskWriter(destination);
        codec.WriteTyped(ref writer, expected, format, type);
        var reader = new BlueTuskReader(destination.AsSpan(0, writer.WrittenCount));
        var actual = codec.ReadTyped(ref reader, format, type);

        Assert.Equal(expected.HouseNumber, actual.HouseNumber);
        Assert.Equal(expected.Street, actual.Street);
        Assert.Equal(expected.Note, actual.Note);
        Assert.True(registry.TryGetCodec(AddressArrayId, out var arrayCodec));
        Assert.Equal(typeof(GeneratedAddress[]), arrayCodec!.ClrType);
    }

    [Fact]
    public void Generated_mapping_supports_a_shape_the_reflection_fallback_rejects()
    {
        var configuredTypes = new BlueTuskTypeRegistryBuilder()
            .Register("app", "generated_address", new BlueTuskCompositeCodec<GeneratedAddress>())
            .Build();

        var error = Assert.Throws<InvalidOperationException>(
            () => BlueTuskTypeCatalogue.BuildRegistry(CreateCatalogue(), configuredTypes));

        Assert.Contains("public parameterless constructor", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_codec_preserves_null_validation()
    {
        var configuredTypes = GeneratedAddress.RegisterBlueTuskCodec(
            new BlueTuskTypeRegistryBuilder()).Build();
        var registry = BlueTuskTypeCatalogue.BuildRegistry(CreateCatalogue(), configuredTypes);
        var type = Assert.Single(registry.Types, candidate => candidate.Id == AddressId);
        Assert.True(registry.TryGetCodec(type.Id, out var registered));
        var codec = Assert.IsType<BlueTuskCompositeCodec<GeneratedAddress>>(registered);

        var error = Assert.Throws<InvalidOperationException>(() =>
            ReadInvalid(codec, type));

        Assert.Contains("house_number", error.Message, StringComparison.Ordinal);
        Assert.Contains("not nullable", error.Message, StringComparison.Ordinal);
    }

    private static GeneratedAddress ReadInvalid(
        BlueTuskCompositeCodec<GeneratedAddress> codec,
        BlueTuskTypeDescriptor type)
    {
        var reader = new BlueTuskReader(Encoding.UTF8.GetBytes("(,street,note)"));
        return codec.ReadTyped(ref reader, BlueTuskDataFormat.Text, type);
    }

    private static BlueTuskCatalogueType[] CreateCatalogue() =>
    [
        new BlueTuskCatalogueType
        {
            Id = AddressId,
            Schema = "app",
            Name = "generated_address",
            PostgreSqlKind = 'c',
            PostgreSqlCategory = 'C',
            ArrayType = AddressArrayId,
            CompositeFields =
            [
                new BlueTuskCompositeField
                {
                    Position = 1,
                    Name = "house_number",
                    Type = BlueTuskBuiltInTypes.Int4.Id,
                },
                new BlueTuskCompositeField
                {
                    Position = 2,
                    Name = "street",
                    Type = BlueTuskBuiltInTypes.Text.Id,
                },
                new BlueTuskCompositeField
                {
                    Position = 3,
                    Name = "note",
                    Type = BlueTuskBuiltInTypes.Text.Id,
                },
            ],
        },
        new BlueTuskCatalogueType
        {
            Id = AddressArrayId,
            Schema = "app",
            Name = "_generated_address",
            PostgreSqlKind = 'b',
            PostgreSqlCategory = 'A',
            ElementType = AddressId,
        },
    ];
}

[BlueTuskComposite("app", "generated_address")]
public sealed partial class GeneratedAddress
{
    private GeneratedAddress()
    {
    }

    [BlueTuskName("house_number")]
    public int HouseNumber { get; private set; }

    public string Street { get; private set; } = string.Empty;

    public string? Note { get; private set; }

    public static GeneratedAddress Create(int houseNumber, string street, string? note) =>
        new()
        {
            HouseNumber = houseNumber,
            Street = street,
            Note = note,
        };
}
