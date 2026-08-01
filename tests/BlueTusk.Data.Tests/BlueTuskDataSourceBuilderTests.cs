using BlueTusk.Client;
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

    [Fact]
    public void MapComposite_registers_codec_by_qualified_catalogue_name()
    {
        var builder = new BlueTuskDataSourceBuilder("Host=localhost;Username=test;Password=test");
        Assert.Same(builder, builder.MapComposite<Address>("app.address"));
        var registry = BlueTuskTypeCatalogue.BuildRegistry(
        [
            new BlueTuskCatalogueType
            {
                Id = new BlueTuskTypeId(90_400),
                Schema = "app",
                Name = "address",
                PostgreSqlKind = 'c',
                PostgreSqlCategory = 'C',
                CompositeFields =
                [
                    new BlueTuskCompositeField
                    {
                        Position = 1,
                        Name = "house_number",
                        Type = BlueTuskBuiltInTypes.Int4.Id,
                    },
                ],
            },
        ], builder.Types.Build());

        Assert.True(registry.TryGetCodec(new BlueTuskTypeId(90_400), out var codec));
        Assert.IsType<BlueTuskCompositeCodec<Address>>(codec);
    }

    [Theory]
    [InlineData("address")]
    [InlineData("app.")]
    [InlineData(".address")]
    public void MapComposite_requires_schema_qualified_type_name(string name)
    {
        var builder = new BlueTuskDataSourceBuilder("Host=localhost;Username=test;Password=test");

        Assert.Throws<FormatException>(() => builder.MapComposite<Address>(name));
    }

    [Fact]
    public void Dedicated_session_options_preserve_connection_security_without_using_the_pool()
    {
        var settings = new BlueTuskConnectionStringBuilder
        {
            Host = "db.example.test",
            Port = 5544,
            Database = "app",
            Username = "replicator",
            Password = "secret",
            ApplicationName = "wal-reader",
            Timeout = TimeSpan.FromSeconds(7),
            Pooling = true,
            SslMode = BlueTuskSslMode.Require,
            ChannelBinding = BlueTuskChannelBindingMode.Require,
        };
        using var dataSource = BlueTuskDataSource.Create(settings.ConnectionString);

        var options = dataSource.CreateDedicatedSessionOptions();

        Assert.Equal("db.example.test", options.Host);
        Assert.Equal(5544, options.Port);
        Assert.Equal("app", options.Database);
        Assert.Equal("replicator", options.Username);
        Assert.Equal("secret", options.Password);
        Assert.Equal("wal-reader", options.ApplicationName);
        Assert.Equal(TimeSpan.FromSeconds(7), options.ConnectTimeout);
        Assert.Equal(BlueTuskSslMode.Require, options.SslMode);
        Assert.Equal(BlueTuskChannelBindingMode.Require, options.ChannelBinding);
        Assert.Equal(BlueTuskReplicationMode.None, options.ReplicationMode);
        Assert.Equal(0, dataSource.GetPoolStatistics().Total);
    }

    [Fact]
    public void Multi_host_dedicated_sessions_require_a_configured_endpoint()
    {
        using var dataSource = BlueTuskDataSource.Create(
            "Host=primary,standby;Port=5432,5433;Database=app;Username=test;Password=test");

        Assert.Throws<InvalidOperationException>(dataSource.CreateDedicatedSessionOptions);

        var options = dataSource.CreateDedicatedSessionOptions(
            new BlueTuskHostEndpoint("standby", 5433));
        Assert.Equal("standby", options.Host);
        Assert.Equal(5433, options.Port);
        Assert.Throws<ArgumentException>(
            () => dataSource.CreateDedicatedSessionOptions(
                new BlueTuskHostEndpoint("other", 5432)));
    }

    private enum OrderStatus
    {
        Pending,
        Complete,
    }

    private sealed record Address(int HouseNumber);
}
