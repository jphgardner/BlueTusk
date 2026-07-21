using BlueTusk.TypeSystem;

namespace BlueTusk.Data.Tests;

public sealed class BlueTuskDataSourceBuilderTests
{
    [Fact]
    public void MapEnum_registers_codec_by_qualified_catalogue_name()
    {
        var builder = new BlueTuskDataSourceBuilder("Host=localhost;Username=test;Password=test");
        Assert.Same(builder, builder.MapEnum<OrderStatus>("app.order_status"));
        var registry = BlueTuskTypeCatalogue.BuildRegistry(
        [
            new BlueTuskCatalogueType
            {
                Id = new BlueTuskTypeId(90_300),
                Schema = "app",
                Name = "order_status",
                PostgreSqlKind = 'e',
                PostgreSqlCategory = 'E',
                EnumLabels = ["Pending", "Complete"],
            },
        ], builder.Types.Build());

        Assert.True(registry.TryGetCodec(new BlueTuskTypeId(90_300), out var codec));
        Assert.IsType<BlueTuskEnumCodec<OrderStatus>>(codec);
    }

    [Theory]
    [InlineData("order_status")]
    [InlineData("app.")]
    [InlineData(".order_status")]
    public void MapEnum_requires_schema_qualified_type_name(string name)
    {
        var builder = new BlueTuskDataSourceBuilder("Host=localhost;Username=test;Password=test");

        Assert.Throws<FormatException>(() => builder.MapEnum<OrderStatus>(name));
    }

    private enum OrderStatus
    {
        Pending,
        Complete,
    }
}
