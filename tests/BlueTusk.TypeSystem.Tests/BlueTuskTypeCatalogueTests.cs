namespace BlueTusk.TypeSystem.Tests;

public sealed class BlueTuskTypeCatalogueTests
{
    public static TheoryData<char, char, BlueTuskTypeKind> TypeKinds => new()
    {
        { 'b', 'N', BlueTuskTypeKind.Base },
        { 'b', 'A', BlueTuskTypeKind.Array },
        { 'c', 'C', BlueTuskTypeKind.Composite },
        { 'd', 'N', BlueTuskTypeKind.Domain },
        { 'e', 'E', BlueTuskTypeKind.Enum },
        { 'm', 'R', BlueTuskTypeKind.Multirange },
        { 'p', 'P', BlueTuskTypeKind.Pseudo },
        { 'r', 'R', BlueTuskTypeKind.Range },
        { 'x', 'U', BlueTuskTypeKind.Unknown },
    };

    [Theory]
    [MemberData(nameof(TypeKinds))]
    public void Catalogue_maps_postgresql_type_kinds(
        char postgreSqlKind,
        char postgreSqlCategory,
        BlueTuskTypeKind expected)
    {
        var descriptor = BlueTuskTypeCatalogue.CreateDescriptor(new BlueTuskCatalogueType
        {
            Id = new BlueTuskTypeId(90_000),
            Schema = "app",
            Name = "sample",
            PostgreSqlKind = postgreSqlKind,
            PostgreSqlCategory = postgreSqlCategory,
            ElementType = postgreSqlCategory == 'A' ? new BlueTuskTypeId(23) : null,
        });

        Assert.Equal(expected, descriptor.Kind);
    }

    [Fact]
    public void Catalogue_preserves_discovered_relationships_and_bootstrap_codecs()
    {
        var registry = BlueTuskTypeCatalogue.BuildRegistry(
        [
            new BlueTuskCatalogueType
            {
                Id = new BlueTuskTypeId(23),
                Schema = "pg_catalog",
                Name = "int4",
                PostgreSqlKind = 'b',
                PostgreSqlCategory = 'N',
                ArrayType = new BlueTuskTypeId(1007),
            },
            new BlueTuskCatalogueType
            {
                Id = new BlueTuskTypeId(1007),
                Schema = "pg_catalog",
                Name = "_int4",
                PostgreSqlKind = 'b',
                PostgreSqlCategory = 'A',
                ElementType = new BlueTuskTypeId(23),
            },
            new BlueTuskCatalogueType
            {
                Id = new BlueTuskTypeId(90_001),
                Schema = "app",
                Name = "positive_int",
                PostgreSqlKind = 'd',
                PostgreSqlCategory = 'N',
                BaseType = new BlueTuskTypeId(23),
                ArrayType = new BlueTuskTypeId(90_002),
            },
            new BlueTuskCatalogueType
            {
                Id = new BlueTuskTypeId(90_003),
                Schema = "app",
                Name = "int_span",
                PostgreSqlKind = 'r',
                PostgreSqlCategory = 'R',
                RangeSubtype = new BlueTuskTypeId(23),
            },
        ]);

        Assert.True(registry.TryGetType(new BlueTuskTypeId(1007), out var array));
        Assert.Equal(BlueTuskTypeKind.Array, array!.Kind);
        Assert.Equal(new BlueTuskTypeId(23), array.ElementType);
        Assert.True(registry.TryGetType(new BlueTuskTypeId(90_001), out var domain));
        Assert.Equal(new BlueTuskTypeId(23), domain!.BaseType);
        Assert.True(registry.TryGetType(new BlueTuskTypeId(90_003), out var range));
        Assert.Equal(new BlueTuskTypeId(23), range!.RangeSubtype);
        Assert.True(registry.TryGetCodec(new BlueTuskTypeId(23), out var codec));
        Assert.IsType<BlueTuskInt32Codec>(codec);
    }

    [Fact]
    public void Configured_type_codec_is_bound_to_discovered_descriptor()
    {
        var type = new BlueTuskTypeDescriptor
        {
            Id = new BlueTuskTypeId(90_100),
            Schema = "custom",
            Name = "special_value",
            Kind = BlueTuskTypeKind.Base,
        };
        var configured = new BlueTuskTypeRegistryBuilder()
            .Register(type, new BlueTuskStringCodec())
            .Build();

        var registry = BlueTuskTypeCatalogue.BuildRegistry(
        [
            new BlueTuskCatalogueType
            {
                Id = type.Id,
                Schema = type.Schema,
                Name = type.Name,
                PostgreSqlKind = 'b',
                PostgreSqlCategory = 'U',
            },
        ], configured);

        Assert.True(registry.TryGetCodec(type.Id, out var codec));
        Assert.IsType<BlueTuskStringCodec>(codec);
    }
}
