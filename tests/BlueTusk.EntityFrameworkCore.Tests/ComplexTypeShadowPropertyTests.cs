using BlueTusk.Client;
using BlueTusk.Data;
using Microsoft.EntityFrameworkCore;
using Xunit.Sdk;

namespace BlueTusk.EntityFrameworkCore.Tests;

public sealed class ComplexTypeShadowPropertyTests
{
    [Fact]
    public void Shadow_properties_on_value_type_complex_types_remain_rejected()
    {
        var options = new DbContextOptionsBuilder<ValueTypeContext>()
            .UseBlueTusk("Host=localhost;Database=unused")
            .Options;
        using var context = new ValueTypeContext(options);

        var exception = Assert.Throws<InvalidOperationException>(() => context.Model);
        Assert.Contains("Shadow", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Table_split_complex_type_shadow_property_round_trips_through_PostgreSQL()
        => await RoundTripAsync(StorageShape.TableSplit);

    [Fact]
    public async Task Json_complex_type_shadow_property_round_trips_through_PostgreSQL()
        => await RoundTripAsync(StorageShape.Json);

    [Fact]
    public async Task Json_complex_collection_element_shadow_property_round_trips_through_PostgreSQL()
    {
        var connectionString = GetConnectionString();
        var table = "ef_shadow_lines_" + Guid.NewGuid().ToString("N")[..12];
        await using (var context = CreateContext(connectionString, table, StorageShape.Json))
        {
            await context.Database.ExecuteSqlRawAsync(CreateTableSql(context, table));
        }

        try
        {
            int id;
            await using (var context = CreateContext(connectionString, table, StorageShape.Json))
            {
                var order = NewOrder();
                context.Orders.Add(order);
                var lines = context.Entry(order).ComplexCollection(o => o.Lines);
                lines[0].Property("Note").CurrentValue = "first";
                lines[1].Property("Note").CurrentValue = "second";
                Assert.Equal(1, await context.SaveChangesAsync());
                id = order.Id;
            }

            await using (var context = CreateContext(connectionString, table, StorageShape.Json))
            {
                var order = await context.Orders.SingleAsync(o => o.Id == id);
                var lines = context.Entry(order).ComplexCollection(o => o.Lines);
                Assert.Equal("first", lines[0].Property("Note").CurrentValue);
                Assert.Equal("second", lines[1].Property("Note").CurrentValue);

                lines[1].Property("Note").CurrentValue = "changed";
                Assert.Equal(EntityState.Modified, context.Entry(order).State);
                Assert.Equal(1, await context.SaveChangesAsync());
            }

            await using (var context = CreateContext(connectionString, table, StorageShape.Json))
            {
                var order = await context.Orders.SingleAsync(o => o.Id == id);
                var lines = context.Entry(order).ComplexCollection(o => o.Lines);
                Assert.Equal("first", lines[0].Property("Note").CurrentValue);
                Assert.Equal("changed", lines[1].Property("Note").CurrentValue);
                Assert.Equal(["A", "B"], order.Lines.Select(line => line.Sku));
            }
        }
        finally
        {
            await using var context = CreateContext(connectionString, table, StorageShape.Json);
            var drop = "DROP TABLE IF EXISTS \"" + table + "\"";
            await context.Database.ExecuteSqlRawAsync(drop);
        }
    }

    private static async Task RoundTripAsync(StorageShape shape)
    {
        var connectionString = GetConnectionString();
        var table = "ef_shadow_orders_" + Guid.NewGuid().ToString("N")[..12];
        await using (var context = CreateContext(connectionString, table, shape))
        {
            await context.Database.ExecuteSqlRawAsync(CreateTableSql(context, table));
        }

        try
        {
            int id;
            await using (var context = CreateContext(connectionString, table, shape))
            {
                var order = NewOrder();
                context.Orders.Add(order);
                context.Entry(order).ComplexProperty(o => o.Address).Property("Label").CurrentValue = "home";
                Assert.Equal(1, await context.SaveChangesAsync());
                id = order.Id;
            }

            await using (var context = CreateContext(connectionString, table, shape))
            {
                var order = await context.Orders.SingleAsync(o => o.Id == id);
                var label = context.Entry(order).ComplexProperty(o => o.Address).Property("Label");
                Assert.Equal("home", label.CurrentValue);
                Assert.Equal("home", label.OriginalValue);
                Assert.Equal("Main Street", order.Address.Street);

                var projected = await context.Orders
                    .Where(o => o.Id == id)
                    .Select(o => EF.Property<string>(o.Address, "Label"))
                    .SingleAsync();
                Assert.Equal("home", projected);
                Assert.Equal(1, await context.Orders.CountAsync(o => EF.Property<string>(o.Address, "Label") == "home"));

                label.CurrentValue = "work";
                Assert.True(label.IsModified);
                Assert.Equal(EntityState.Modified, context.Entry(order).State);
                Assert.Equal(1, await context.SaveChangesAsync());
            }

            await using (var context = CreateContext(connectionString, table, shape))
            {
                var order = await context.Orders.SingleAsync(o => o.Id == id);
                Assert.Equal("work", context.Entry(order).ComplexProperty(o => o.Address).Property("Label").CurrentValue);
                Assert.Equal("Main Street", order.Address.Street);

                context.Orders.Remove(order);
                Assert.Equal(1, await context.SaveChangesAsync());
                Assert.Equal(0, await context.Orders.CountAsync());
            }
        }
        finally
        {
            await using var context = CreateContext(connectionString, table, shape);
            var drop = "DROP TABLE IF EXISTS \"" + table + "\"";
            await context.Database.ExecuteSqlRawAsync(drop);
        }
    }

    private static Order NewOrder() => new()
    {
        Address = new Address { Street = "Main Street" },
        Lines = [new Line { Sku = "A" }, new Line { Sku = "B" }],
    };

    private static string CreateTableSql(ShadowContext context, string table)
    {
        var script = context.Database.GenerateCreateScript();
        Assert.Contains(table, script, StringComparison.Ordinal);
        return script;
    }

    private static ShadowContext CreateContext(string connectionString, string table, StorageShape shape)
    {
        var options = new DbContextOptionsBuilder<ShadowContext>()
            .UseBlueTusk(connectionString)
            .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, ShadowModelCacheKeyFactory>()
            .Options;
        return new ShadowContext(options, table, shape);
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

    private enum StorageShape
    {
        TableSplit,
        Json,
    }

    private sealed class ShadowModelCacheKeyFactory : Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime)
            => context is ShadowContext shadow ? (shadow.Table, shadow.Shape, designTime) : (object)(context.GetType(), designTime);
    }

    private sealed class ShadowContext(DbContextOptions<ShadowContext> options, string table, StorageShape shape)
        : DbContext(options)
    {
        public string Table { get; } = table;

        public StorageShape Shape { get; } = shape;

        public DbSet<Order> Orders => Set<Order>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Order>(order =>
            {
                order.ToTable(Table);
                order.HasKey(o => o.Id);
                order.ComplexProperty(o => o.Address, address =>
                {
                    address.Property<string>("Label");
                    if (Shape == StorageShape.Json)
                    {
                        address.ToJson();
                    }
                });
                order.ComplexCollection(o => o.Lines, line =>
                {
                    line.Property<string>("Note");
                    line.ToJson();
                });
            });
    }

    private sealed class ValueTypeContext(DbContextOptions<ValueTypeContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<ValueTypeOwner>(owner =>
            {
                owner.HasKey(o => o.Id);
                owner.ComplexProperty(o => o.Point, point => point.Property<int>("Shadow"));
            });
    }

    private sealed class Order
    {
        public int Id { get; set; }

        public Address Address { get; set; } = new();

        public List<Line> Lines { get; set; } = [];
    }

    private sealed class Address
    {
        public string Street { get; set; } = string.Empty;
    }

    private sealed class Line
    {
        public string Sku { get; set; } = string.Empty;
    }

    private sealed class ValueTypeOwner
    {
        public int Id { get; set; }

        public Coordinate Point { get; set; }
    }

    private struct Coordinate
    {
        public int X { get; set; }
    }
}
