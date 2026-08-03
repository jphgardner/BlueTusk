using BlueTusk.EntityFrameworkCore;
using BlueTusk.Live.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace BlueTusk.Live.Tests;

public sealed class LiveEfQueryCompilerTests
{
    [Fact]
    public async Task Compiler_accepts_keyed_tenant_bound_ordered_bounded_query()
    {
        var factory = Factory();
        var definition = Definition((context, arguments) =>
        {
            var tenant = arguments.Get<string>("tenant")!;
            var minimum = arguments.Get<decimal>("minimum");
            return context.Orders
                .Where(order => order.TenantId == tenant && order.Total >= minimum)
                .OrderByDescending(order => order.CreatedAt)
                .ThenBy(order => order.Id)
                .Take(25);
        });

        var plan = await LiveEfQueryCompiler.CompileAsync(
            factory,
            definition,
            TestContext.Current.CancellationToken);

        Assert.Equal("orders", plan.Name);
        Assert.Equal("sales.orders", Assert.Single(plan.Dependencies).ToString());
        Assert.True(plan.Capabilities.HasFlag(LiveQueryCapabilities.TenantFilter));
        Assert.True(plan.Capabilities.HasFlag(LiveQueryCapabilities.DeterministicOrdering));
    }

    [Fact]
    public async Task Compiler_rejects_unbounded_nondeterministic_and_unbound_tenant_queries()
    {
        var factory = Factory();
        await Assert.ThrowsAsync<LiveEfQueryRegistrationException>(async () =>
            await LiveEfQueryCompiler.CompileAsync(
                factory,
                Definition((context, arguments) =>
                {
                    var tenant = arguments.Get<string>("tenant")!;
                    return context.Orders.Where(order => order.TenantId == tenant);
                }),
                TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<LiveEfQueryRegistrationException>(async () =>
            await LiveEfQueryCompiler.CompileAsync(
                factory,
                Definition((context, arguments) =>
                {
                    var tenant = arguments.Get<string>("tenant")!;
                    return context.Orders
                        .Where(order => order.TenantId == tenant)
                        .OrderBy(order => order.CreatedAt)
                        .Take(25);
                }),
                TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<LiveEfQueryRegistrationException>(async () =>
            await LiveEfQueryCompiler.CompileAsync(
                factory,
                Definition((context, _) => context.Orders
                    .OrderBy(order => order.Id)
                    .Take(25)),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Compiler_rejects_unsupported_query_shapes_and_non_primary_keys()
    {
        var factory = Factory();
        await Assert.ThrowsAsync<LiveEfQueryRegistrationException>(async () =>
            await LiveEfQueryCompiler.CompileAsync(
                factory,
                Definition((context, arguments) =>
                {
                    var tenant = arguments.Get<string>("tenant")!;
                    return context.Orders
                        .Where(order => order.TenantId == tenant)
                        .OrderBy(order => order.Id)
                        .Skip(2)
                        .Take(25);
                }),
                TestContext.Current.CancellationToken));

        var wrongKey = new LiveEfQueryDefinition<OrdersContext, Order, string>(
            "orders-wrong-key",
            "database",
            "v1",
            Parameters,
            ValidationArguments,
            50,
            (context, arguments) =>
            {
                var tenant = arguments.Get<string>("tenant")!;
                return context.Orders
                    .Where(order => order.TenantId == tenant)
                    .OrderBy(order => order.Id)
                    .Take(25);
            },
            order => order.TenantId,
            EqualityComparer<Order>.Default,
            LiveEfTenantIsolationMode.RegisteredPredicate,
            new LiveEfTenantBinding(nameof(Order.TenantId), "tenant"));
        await Assert.ThrowsAsync<LiveEfQueryRegistrationException>(async () =>
            await LiveEfQueryCompiler.CompileAsync(
                factory,
                wrongKey,
                TestContext.Current.CancellationToken));
    }

    private static readonly LiveQueryParameter[] Parameters =
    [
        new("tenant", typeof(string)),
        new("minimum", typeof(decimal)),
    ];

    private static readonly IReadOnlyDictionary<string, object?> ValidationArguments =
        new Dictionary<string, object?>
        {
            ["tenant"] = "tenant-a",
            ["minimum"] = 10m,
        };

    private static LiveEfQueryDefinition<OrdersContext, Order, int> Definition(
        Func<OrdersContext, LiveQueryArguments, IQueryable<Order>> queryFactory) =>
        new(
            "orders",
            "database",
            "v1",
            Parameters,
            ValidationArguments,
            50,
            queryFactory,
            order => order.Id,
            EqualityComparer<Order>.Default,
            LiveEfTenantIsolationMode.RegisteredPredicate,
            new LiveEfTenantBinding(nameof(Order.TenantId), "tenant"));

    private static ContextFactory Factory()
    {
        var options = new DbContextOptionsBuilder<OrdersContext>()
            .UseBlueTusk(
                "Host=localhost;Database=unused;Username=unused;Password=unused;SSL Mode=Disable")
            .Options;
        return new ContextFactory(options);
    }

    private sealed class ContextFactory(DbContextOptions<OrdersContext> options)
        : IDbContextFactory<OrdersContext>
    {
        public OrdersContext CreateDbContext() => new(options);
    }

    private sealed class OrdersContext(DbContextOptions<OrdersContext> options) : DbContext(options)
    {
        public DbSet<Order> Orders => Set<Order>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>(entity =>
            {
                entity.ToTable("orders", "sales");
                entity.HasKey(order => order.Id);
                entity.Property(order => order.TenantId).IsRequired();
            });
        }
    }

    private sealed class Order
    {
        public int Id { get; set; }

        public string TenantId { get; set; } = string.Empty;

        public decimal Total { get; set; }

        public DateTimeOffset CreatedAt { get; set; }
    }
}
