using BlueTusk.Client;
using BlueTusk.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit.Sdk;

#pragma warning disable EF1001 // Internal EF Core API usage: complex collection entry ordinals are asserted directly.

namespace BlueTusk.EntityFrameworkCore.Tests;

public sealed class ValueTypeComplexCollectionTests
{
    [Fact]
    public async Task Value_type_complex_collections_round_trip_through_PostgreSQL()
    {
        await using var table = await RouteTable.CreateAsync();
        var id = await table.InsertAsync(NewRoute());

        await using (var context = table.CreateContext())
        {
            AssertRoute(await context.Routes.SingleAsync(r => r.Id == id));
            Assert.Equal(EntityState.Unchanged, context.Entry(await context.Routes.SingleAsync(r => r.Id == id)).State);
        }

        await using (var context = table.CreateContext())
        {
            AssertRoute(await context.Routes.AsNoTracking().SingleAsync(r => r.Id == id));
            Assert.Empty(context.ChangeTracker.Entries());
        }

        await using (var context = table.CreateContext())
        {
            AssertRoute(await context.Routes.AsNoTrackingWithIdentityResolution().SingleAsync(r => r.Id == id));
        }

        static void AssertRoute(Route route)
        {
            Assert.Equal(["Depot", "Harbour"], route.Stops.Select(s => s.Name));
            Assert.Equal([5, 12], route.Stops.Select(s => s.Minutes));
            Assert.Equal([1, 2], route.Stops[0].Bays.Select(b => b.Number));
            Assert.Equal([7], route.Stops[1].Bays.Select(b => b.Number));
            Assert.Equal(["Depot->Harbour", "Harbour->Depot"], route.Legs.Select(l => l.From + "->" + l.To));
            Assert.Equal([("fast", 1), ("scenic", 2)], route.Tags);
            Assert.Equal("summary", route.Summary.Label);
            Assert.Equal([3, 4], route.Summary.Bays.Select(b => b.Number));
        }
    }

    [Fact]
    public async Task Removing_a_value_type_element_is_detected_by_value_and_saved()
    {
        await using var table = await RouteTable.CreateAsync();
        var id = await table.InsertAsync(NewRoute());

        await using (var context = table.CreateContext())
        {
            var route = await context.Routes.SingleAsync(r => r.Id == id);
            route.Stops.RemoveAt(0);
            context.ChangeTracker.DetectChanges();

            var entry = context.Entry(route);
            var stops = entry.ComplexCollection(r => r.Stops);
            Assert.Equal(EntityState.Modified, entry.State);
            Assert.True(stops.IsModified);
            var internalEntry = entry.GetInfrastructure();
            Assert.Equal([-1, 0], internalEntry.GetComplexCollectionOriginalEntries(stops.Metadata).Select(e => e?.Ordinal));
            Assert.Equal([1], internalEntry.GetComplexCollectionEntries(stops.Metadata).Select(e => e?.OriginalOrdinal));
            Assert.Equal(1, await context.SaveChangesAsync());
        }

        await using (var context = table.CreateContext())
        {
            var route = await context.Routes.SingleAsync(r => r.Id == id);
            Assert.Equal(["Harbour"], route.Stops.Select(s => s.Name));
            Assert.Equal([7], route.Stops[0].Bays.Select(b => b.Number));
        }
    }

    [Fact]
    public async Task Changing_and_inserting_value_type_elements_is_detected_and_saved()
    {
        await using var table = await RouteTable.CreateAsync();
        var id = await table.InsertAsync(NewRoute());

        await using (var context = table.CreateContext())
        {
            var route = await context.Routes.SingleAsync(r => r.Id == id);
            var harbour = route.Stops[1];
            harbour.Minutes = 15;
            route.Stops[1] = harbour;
            route.Stops.Insert(0, new Stop { Name = "Yard", Minutes = 1, Bays = [] });
            route.Tags[1] = ("scenic", 3);
            context.ChangeTracker.DetectChanges();

            var entry = context.Entry(route);
            var stops = entry.ComplexCollection(r => r.Stops);
            Assert.True(stops.IsModified);
            Assert.Equal(EntityState.Added, stops[0].State);
            Assert.Equal(EntityState.Unchanged, stops[1].State);
            Assert.Equal(EntityState.Modified, stops[2].State);
            Assert.True(stops[2].Property(s => s.Minutes).IsModified);
            Assert.Equal(12, stops[2].Property(s => s.Minutes).OriginalValue);
            Assert.True(entry.ComplexCollection(r => r.Tags).IsModified);
            Assert.Equal(1, await context.SaveChangesAsync());
        }

        await using (var context = table.CreateContext())
        {
            var route = await context.Routes.SingleAsync(r => r.Id == id);
            Assert.Equal(["Yard", "Depot", "Harbour"], route.Stops.Select(s => s.Name));
            Assert.Equal([1, 5, 15], route.Stops.Select(s => s.Minutes));
            Assert.Equal([("fast", 1), ("scenic", 3)], route.Tags);
        }
    }

    [Fact]
    public async Task Value_type_complex_collections_can_be_projected()
    {
        await using var table = await RouteTable.CreateAsync();
        var id = await table.InsertAsync(NewRoute());

        await using var context = table.CreateContext();
        var stops = await context.Routes.Where(r => r.Id == id).Select(r => r.Stops).SingleAsync();
        Assert.Equal(["Depot", "Harbour"], stops.Select(s => s.Name));
        Assert.Equal([7], stops[1].Bays.Select(b => b.Number));

        var projected = await context.Routes
            .Where(r => r.Id == id)
            .Select(r => new { r.Id, r.Tags, r.Summary, Legs = r.Legs })
            .SingleAsync();
        Assert.Equal([("fast", 1), ("scenic", 2)], projected.Tags);
        Assert.Equal([3, 4], projected.Summary.Bays.Select(b => b.Number));
        Assert.Equal(["Depot", "Harbour"], projected.Legs.Select(l => l.From));

        var harbour = await context.Routes.Where(r => r.Id == id).Select(r => r.Stops[1]).SingleAsync();
        Assert.Equal("Harbour", harbour.Name);
        Assert.Equal([7], harbour.Bays.Select(b => b.Number));

        var summaryBays = await context.Routes.Where(r => r.Id == id).Select(r => r.Summary.Bays).SingleAsync();
        Assert.Equal([3, 4], summaryBays.Select(b => b.Number));

        var tracked = await context.Routes.Where(r => r.Id == id).Select(r => new { Route = r, r.Stops }).SingleAsync();
        Assert.Equal(EntityState.Unchanged, context.Entry(tracked.Route).State);
        Assert.Equal(tracked.Route.Stops.Select(s => s.Name), tracked.Stops.Select(s => s.Name));
    }

    [Fact]
    public async Task Unchanged_value_type_elements_are_not_reported_as_changes()
    {
        await using var table = await RouteTable.CreateAsync();
        var id = await table.InsertAsync(NewRoute());

        await using var context = table.CreateContext();
        var route = await context.Routes.SingleAsync(r => r.Id == id);
        route.Stops[0] = route.Stops[0];
        context.ChangeTracker.DetectChanges();

        Assert.Equal(EntityState.Unchanged, context.Entry(route).State);
        Assert.False(context.ChangeTracker.HasChanges());
        Assert.Equal(0, await context.SaveChangesAsync());
    }

    private static Route NewRoute() => new()
    {
        Stops =
        [
            new Stop { Name = "Depot", Minutes = 5, Bays = [new Bay { Number = 1 }, new Bay { Number = 2 }] },
            new Stop { Name = "Harbour", Minutes = 12, Bays = [new Bay { Number = 7 }] },
        ],
        Legs = [new Leg("Depot", "Harbour"), new Leg("Harbour", "Depot")],
        Tags = [("fast", 1), ("scenic", 2)],
        Summary = new Segment { Label = "summary", Bays = [new Bay { Number = 3 }, new Bay { Number = 4 }] },
    };

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

    private sealed class RouteTable : IAsyncDisposable
    {
        private readonly string _connectionString;

        private RouteTable(string connectionString, string name)
        {
            _connectionString = connectionString;
            Name = name;
        }

        public string Name { get; }

        public static async Task<RouteTable> CreateAsync()
        {
            var table = new RouteTable(GetConnectionString(), "ef_value_routes_" + Guid.NewGuid().ToString("N")[..12]);
            await using var context = table.CreateContext();
            var script = context.Database.GenerateCreateScript();
            Assert.Contains(table.Name, script, StringComparison.Ordinal);
            await context.Database.ExecuteSqlRawAsync(script);
            return table;
        }

        public RouteContext CreateContext()
            => new(
                new DbContextOptionsBuilder<RouteContext>()
                    .UseBlueTusk(_connectionString)
                    .ReplaceService<IModelCacheKeyFactory, RouteModelCacheKeyFactory>()
                    .Options,
                Name);

        public async Task<int> InsertAsync(Route route)
        {
            await using var context = CreateContext();
            context.Routes.Add(route);
            Assert.Equal(1, await context.SaveChangesAsync());
            return route.Id;
        }

        public async ValueTask DisposeAsync()
        {
            await using var context = CreateContext();
            var drop = "DROP TABLE IF EXISTS \"" + Name + "\"";
            await context.Database.ExecuteSqlRawAsync(drop);
        }
    }

    private sealed class RouteModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime)
            => context is RouteContext route ? (route.Table, designTime) : (object)(context.GetType(), designTime);
    }

    private sealed class RouteContext(DbContextOptions<RouteContext> options, string table) : DbContext(options)
    {
        public string Table { get; } = table;

        public DbSet<Route> Routes => Set<Route>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Route>(route =>
            {
                route.ToTable(Table);
                route.HasKey(r => r.Id);
                route.ComplexCollection(r => r.Stops, stop =>
                {
                    stop.ComplexCollection(s => s.Bays);
                    stop.ToJson();
                });
                route.ComplexCollection(r => r.Legs, leg => leg.ToJson());
                route.ComplexCollection(r => r.Tags, tag => tag.ToJson());
                route.ComplexProperty(r => r.Summary, summary =>
                {
                    summary.ComplexCollection(s => s.Bays);
                    summary.ToJson();
                });
            });
    }

    private sealed class Route
    {
        public int Id { get; set; }

        public List<Stop> Stops { get; set; } = [];

        public Leg[] Legs { get; set; } = [];

        public List<(string, int)> Tags { get; set; } = [];

        public Segment Summary { get; set; } = new();
    }

    private struct Stop
    {
        public string Name { get; set; }

        public int Minutes { get; set; }

        public List<Bay> Bays { get; set; }
    }

    private struct Bay
    {
        public int Number { get; set; }
    }

    // Materialized through its constructor binding.
    private readonly struct Leg(string from, string to)
    {
        public string From { get; init; } = from;

        public string To { get; init; } = to;
    }

    private sealed class Segment
    {
        public string Label { get; set; } = string.Empty;

        public List<Bay> Bays { get; set; } = [];
    }
}
