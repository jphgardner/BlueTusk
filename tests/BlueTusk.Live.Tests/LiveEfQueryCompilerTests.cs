using BlueTusk.Live.EntityFrameworkCore;
using BlueTusk.TypeSystem;
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

    [Fact]
    public async Task Compiler_accepts_one_to_many_include_and_tracks_both_tables()
    {
        var plan = await LiveEfQueryCompiler.CompileAsync(
            Factory(),
            Definition((context, arguments) =>
            {
                var tenant = arguments.Get<string>("tenant")!;
                return context.Orders
                    .Include(order => order.Lines)
                    .Where(order => order.TenantId == tenant)
                    .OrderByDescending(order => order.CreatedAt)
                    .ThenBy(order => order.Id)
                    .Take(25);
            }),
            TestContext.Current.CancellationToken);

        Assert.True(plan.Capabilities.HasFlag(LiveQueryCapabilities.Include));
        Assert.False(plan.Capabilities.HasFlag(LiveQueryCapabilities.SingleTable));
        Assert.Equal(
            ["sales.order_lines", "sales.orders"],
            plan.Dependencies.Select(static dependency => dependency.ToString()));
    }

    [Fact]
    public async Task Compiler_accepts_translatable_PostgreSQL_full_text_predicate()
    {
        LiveQueryParameter[] parameters =
        [
            new("tenant", typeof(string)),
            new("search", typeof(string)),
        ];
        IReadOnlyDictionary<string, object?> validationArguments =
            new Dictionary<string, object?>
            {
                ["tenant"] = "tenant-a",
                ["search"] = "durable relay",
            };
        var definition = new LiveEfQueryDefinition<OrdersContext, Order, int>(
            "order-search",
            "database",
            "v1",
            parameters,
            validationArguments,
            50,
            (context, arguments) =>
            {
                var tenant = arguments.Get<string>("tenant")!;
                var search = arguments.Get<string>("search")!;
                return context.Orders
                    .Where(order =>
                        order.TenantId == tenant &&
                        EF.Functions.FullTextMatches(
                            order.SearchVector,
                            EF.Functions.WebSearchToTextSearchQuery(search)))
                    .OrderByDescending(order => order.CreatedAt)
                    .ThenBy(order => order.Id)
                    .Take(25);
            },
            order => order.Id,
            EqualityComparer<Order>.Default,
            LiveEfTenantIsolationMode.RegisteredPredicate,
            new LiveEfTenantBinding(nameof(Order.TenantId), "tenant"));

        var plan = await LiveEfQueryCompiler.CompileAsync(
            Factory(),
            definition,
            TestContext.Current.CancellationToken);

        Assert.True(plan.Capabilities.HasFlag(LiveQueryCapabilities.FullText));
        Assert.True(plan.Capabilities.HasFlag(LiveQueryCapabilities.SingleTable));
    }

    [Fact]
    public async Task Projection_compiler_accepts_model_proven_one_to_many_join()
    {
        var definition =
            new LiveEfProjectionQueryDefinition<OrdersContext, Order, OrderLineProjection, int>(
                "order-lines",
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
                        .SelectMany(order => order.Lines)
                        .OrderBy(line => line.Id)
                        .Take(25)
                        .Select(line => new OrderLineProjection(line.Id, line.Total));
                },
                line => line.Id,
                EqualityComparer<OrderLineProjection>.Default,
                LiveEfTenantIsolationMode.RegisteredPredicate,
                new LiveEfTenantBinding(nameof(Order.TenantId), "tenant"));

        var plan = await LiveEfQueryCompiler.CompileProjectionAsync(
            Factory(),
            definition,
            TestContext.Current.CancellationToken);

        Assert.True(plan.Capabilities.HasFlag(LiveQueryCapabilities.OneToManyJoin));
        Assert.False(plan.Capabilities.HasFlag(LiveQueryCapabilities.SingleTable));
        Assert.Equal(
            ["sales.order_lines", "sales.orders"],
            plan.Dependencies.Select(static dependency => dependency.ToString()));
    }

    [Fact]
    public async Task Projection_compiler_accepts_bounded_grouped_aggregates()
    {
        var definition =
            new LiveEfProjectionQueryDefinition<OrdersContext, Order, TenantSummary, string>(
                "tenant-order-summary",
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
                        .GroupBy(order => order.TenantId)
                        .OrderBy(group => group.Key)
                        .Take(25)
                        .Select(group => new TenantSummary(
                            group.Key,
                            group.Count(),
                            group.Sum(order => order.Total)));
                },
                summary => summary.Key,
                EqualityComparer<TenantSummary>.Default,
                LiveEfTenantIsolationMode.RegisteredPredicate,
                new LiveEfTenantBinding(nameof(Order.TenantId), "tenant"));

        var plan = await LiveEfQueryCompiler.CompileProjectionAsync(
            Factory(),
            definition,
            TestContext.Current.CancellationToken);

        Assert.True(plan.Capabilities.HasFlag(LiveQueryCapabilities.Grouping));
        Assert.True(plan.Capabilities.HasFlag(LiveQueryCapabilities.Aggregate));
        Assert.True(plan.Capabilities.HasFlag(LiveQueryCapabilities.SingleTable));
    }

    [Fact]
    public async Task Projection_compiler_rejects_unproven_join_and_missing_root_tenant_predicate()
    {
        var unprovenJoin =
            new LiveEfProjectionQueryDefinition<OrdersContext, Order, OrderLineProjection, int>(
                "unproven-join",
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
                        .Join(
                            context.OrderLines,
                            order => order.Id,
                            line => line.OrderId,
                            (_, line) => new OrderLineProjection(line.Id, line.Total))
                        .OrderBy(line => line.Id)
                        .Take(25);
                },
                line => line.Id,
                EqualityComparer<OrderLineProjection>.Default,
                LiveEfTenantIsolationMode.RegisteredPredicate,
                new LiveEfTenantBinding(nameof(Order.TenantId), "tenant"));
        await Assert.ThrowsAsync<LiveEfQueryRegistrationException>(async () =>
            await LiveEfQueryCompiler.CompileProjectionAsync(
                Factory(),
                unprovenJoin,
                TestContext.Current.CancellationToken));

        var missingTenant =
            new LiveEfProjectionQueryDefinition<OrdersContext, Order, TenantSummary, string>(
                "missing-tenant",
                "database",
                "v1",
                Parameters,
                ValidationArguments,
                50,
                (context, _) => context.Orders
                    .GroupBy(order => order.TenantId)
                    .OrderBy(group => group.Key)
                    .Take(25)
                    .Select(group => new TenantSummary(
                        group.Key,
                        group.Count(),
                        group.Sum(order => order.Total))),
                summary => summary.Key,
                EqualityComparer<TenantSummary>.Default,
                LiveEfTenantIsolationMode.RegisteredPredicate,
                new LiveEfTenantBinding(nameof(Order.TenantId), "tenant"));
        await Assert.ThrowsAsync<LiveEfQueryRegistrationException>(async () =>
            await LiveEfQueryCompiler.CompileProjectionAsync(
                Factory(),
                missingTenant,
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

        public DbSet<OrderLine> OrderLines => Set<OrderLine>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>(entity =>
            {
                entity.ToTable("orders", "sales");
                entity.HasKey(order => order.Id);
                entity.Property(order => order.TenantId).IsRequired();
                entity.HasMany(order => order.Lines)
                    .WithOne(line => line.Order)
                    .HasForeignKey(line => line.OrderId);
            });
            modelBuilder.Entity<OrderLine>(entity =>
            {
                entity.ToTable("order_lines", "sales");
                entity.HasKey(line => line.Id);
            });
        }
    }

    private sealed class Order
    {
        public int Id { get; set; }

        public string TenantId { get; set; } = string.Empty;

        public decimal Total { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public BlueTuskTextSearchVector SearchVector { get; set; } = new([]);

        public List<OrderLine> Lines { get; } = [];
    }

    private sealed class OrderLine
    {
        public int Id { get; set; }

        public int OrderId { get; set; }

        public decimal Total { get; set; }

        public Order Order { get; set; } = null!;
    }

    private sealed record OrderLineProjection(int Id, decimal Total);

    private sealed record TenantSummary(string Key, int Count, decimal Total);
}
