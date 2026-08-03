using System.Text;
using Microsoft.EntityFrameworkCore;

namespace BlueTusk.Streams.EntityFrameworkCore.Tests;

public sealed class EfChangeMappingTests
{
    [Fact]
    public void Ef_model_derives_table_key_and_column_overrides()
    {
        using var context = new OrdersContext();
        var relation = OrdersRelation();

        var mapping = BlueTuskEfChangeMappingFactory.Create<Order>(context.Model, relation);
        var row = mapping.MapRow(new ChangeRow(relation, [Text("42"), Text("Ada")]));

        Assert.Equal(["order_id"], mapping.KeyColumns);
        Assert.Equal(["display_name", "order_id"], mapping.Properties.Select(property => property.ColumnName));
        Assert.True(row.HasValue);
        Assert.Equal(42, row.Value!.Id);
        Assert.Equal("Ada", row.Value.Name);
    }

    [Fact]
    public void Missing_publication_column_fails_at_startup_with_actionable_diagnostic()
    {
        using var context = new OrdersContext();
        var partial = new ChangeTable(
            10,
            "sales",
            "orders",
            'd',
            [new ChangeColumn(0, "order_id", 23, -1, true)]);

        var error = Assert.Throws<EfChangeMappingValidationException>(
            () => BlueTuskEfChangeMappingFactory.Create<Order>(context.Model, partial));

        Assert.Contains(error.Diagnostics, diagnostic => diagnostic.Code == "BTSEF005");
    }

    [Fact]
    public void Mismatched_table_fails_before_streaming()
    {
        using var context = new OrdersContext();
        var relation = new ChangeTable(
            10,
            "public",
            "orders",
            'd',
            [new ChangeColumn(0, "order_id", 23, -1, true)]);

        var error = Assert.Throws<EfChangeMappingValidationException>(
            () => BlueTuskEfChangeMappingFactory.Create<Order>(context.Model, relation));

        Assert.Equal("BTSEF003", Assert.Single(error.Diagnostics).Code);
    }

    private static ChangeTable OrdersRelation() =>
        new(
            10,
            "sales",
            "orders",
            'd',
            [
                new ChangeColumn(0, "order_id", 23, -1, true),
                new ChangeColumn(1, "display_name", 25, -1, false),
            ]);

    private static ChangeColumnValue Text(string value) =>
        ChangeColumnValue.FromValue(Encoding.UTF8.GetBytes(value), ChangeValueEncoding.Text);

    private sealed class OrdersContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            optionsBuilder.UseBlueTusk(
                "Host=localhost;Database=unused;Username=unused;Password=unused;Pooling=false");

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>(entity =>
            {
                entity.ToTable("orders", "sales");
                entity.HasKey(order => order.Id);
                entity.Property(order => order.Id).HasColumnName("order_id");
                entity.Property(order => order.Name).HasColumnName("display_name");
            });
        }
    }

    private sealed class Order
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}
