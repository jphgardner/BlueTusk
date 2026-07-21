using System.Text;

namespace BlueTusk.TypeSystem.Tests;

public sealed class BlueTuskCompositeCodecTests
{
    private static readonly BlueTuskTypeId AddressId = new(90_400);
    private static readonly BlueTuskTypeId AddressArrayId = new(90_401);

    [Fact]
    public void Constructor_mapped_composite_round_trips_binary_and_text()
    {
        var registry = CreateRegistry<Address>();
        var type = Assert.Single(registry.Types, candidate => candidate.Id == AddressId);
        Assert.True(registry.TryGetCodec(type.Id, out var registered));
        var codec = Assert.IsType<BlueTuskCompositeCodec<Address>>(registered);
        var expected = new Address(42, "Main, \"Road\" 🐘", null);

        Assert.Equal(expected, RoundTrip(codec, type, expected, BlueTuskDataFormat.Binary));
        Assert.Equal(expected, RoundTrip(codec, type, expected, BlueTuskDataFormat.Text));

        Assert.True(registry.TryGetCodec(AddressArrayId, out var arrayCodec));
        Assert.Equal(typeof(Address[]), arrayCodec!.ClrType);
        Assert.True(registry.TryGetType(typeof(Address[]), out var inferredType, out var inferredCodec));
        Assert.Equal(AddressArrayId, inferredType!.Id);
        Assert.Same(arrayCodec, inferredCodec);
    }

    [Fact]
    public void Attribute_named_writable_members_use_parameterless_construction()
    {
        var registry = CreateRegistry<MutableAddress>();
        var type = Assert.Single(registry.Types, candidate => candidate.Id == AddressId);
        Assert.True(registry.TryGetCodec(type.Id, out var registered));
        var codec = Assert.IsType<BlueTuskCompositeCodec<MutableAddress>>(registered);
        var expected = new MutableAddress
        {
            Number = 7,
            Street = "High Street",
            Note = "rear entrance",
        };

        var actual = RoundTrip(codec, type, expected, BlueTuskDataFormat.Binary);

        Assert.Equal(expected.Number, actual.Number);
        Assert.Equal(expected.Street, actual.Street);
        Assert.Equal(expected.Note, actual.Note);
    }

    [Fact]
    public void Null_database_field_is_rejected_for_non_nullable_member()
    {
        var registry = CreateRegistry<Address>();
        var type = Assert.Single(registry.Types, candidate => candidate.Id == AddressId);
        Assert.True(registry.TryGetCodec(type.Id, out var registered));
        var codec = Assert.IsType<BlueTuskCompositeCodec<Address>>(registered);

        var error = Assert.Throws<InvalidOperationException>(
            () => ReadText(codec, type, "(,street,note)"));

        Assert.Contains("house_number", error.Message, StringComparison.Ordinal);
        Assert.Contains("not nullable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_postgresql_field_must_have_a_compatible_clr_member()
    {
        var configured = new BlueTuskTypeRegistryBuilder()
            .Register("app", "address", new BlueTuskCompositeCodec<IncompleteAddress>())
            .Build();

        var error = Assert.Throws<InvalidOperationException>(
            () => BlueTuskTypeCatalogue.BuildRegistry(CreateCatalogue(), configured));

        Assert.Contains("note", error.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(IncompleteAddress).FullName!, error.Message, StringComparison.Ordinal);
    }

    private static BlueTuskTypeRegistry CreateRegistry<T>()
    {
        var configured = new BlueTuskTypeRegistryBuilder()
            .Register("app", "address", new BlueTuskCompositeCodec<T>())
            .Build();
        return BlueTuskTypeCatalogue.BuildRegistry(CreateCatalogue(), configured);
    }

    private static BlueTuskCatalogueType[] CreateCatalogue() =>
    [
        new BlueTuskCatalogueType
        {
            Id = AddressId,
            Schema = "app",
            Name = "address",
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
            Name = "_address",
            PostgreSqlKind = 'b',
            PostgreSqlCategory = 'A',
            ElementType = AddressId,
        },
    ];

    private static T RoundTrip<T>(
        BlueTuskCompositeCodec<T> codec,
        BlueTuskTypeDescriptor type,
        T value,
        BlueTuskDataFormat format)
    {
        var destination = new byte[4096];
        var writer = new BlueTuskWriter(destination);
        codec.WriteTyped(ref writer, value, format, type);
        var reader = new BlueTuskReader(destination.AsSpan(0, writer.WrittenCount));
        return codec.ReadTyped(ref reader, format, type);
    }

    private static Address ReadText(
        BlueTuskCompositeCodec<Address> codec,
        BlueTuskTypeDescriptor type,
        string text)
    {
        var reader = new BlueTuskReader(Encoding.UTF8.GetBytes(text));
        return codec.ReadTyped(ref reader, BlueTuskDataFormat.Text, type);
    }

    public sealed record Address(int HouseNumber, string Street, string? Note);

    public sealed class MutableAddress
    {
        [BlueTuskName("house_number")]
        public int Number { get; set; }

        public string Street { get; set; } = string.Empty;

        public string? Note { get; set; }
    }

    public sealed record IncompleteAddress(int HouseNumber, string Street);
}
