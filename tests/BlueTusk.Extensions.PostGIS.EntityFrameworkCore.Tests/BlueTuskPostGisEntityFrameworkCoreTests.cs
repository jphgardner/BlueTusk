using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.Extensions.PostGIS;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NetTopologySuite.Geometries;
using Xunit;
using Xunit.Sdk;

namespace BlueTusk.Extensions.PostGIS.EntityFrameworkCore.Tests;

public sealed class BlueTuskPostGisEntityFrameworkCoreTests
{
    private const string OfflineConnectionString =
        "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=bluetusk_tests";

    [Fact]
    public void Conversion_bridge_preserves_rich_geometry_shape_ordinates_and_srid()
    {
        var point = new Point(1.25, -2.5, 9.75) { SRID = 4326 };

        var geometry = point.ToBlueTuskGeometry().ToNetTopologySuite();
        var geography = point.ToBlueTuskGeography().ToNetTopologySuite();

        Assert.IsType<Point>(geometry);
        Assert.Equal(4326, geometry.SRID);
        Assert.Equal(9.75, ((Point)geometry).Z);
        Assert.True(point.EqualsExact(geometry));
        Assert.Equal(4326, geography.SRID);
        Assert.True(point.EqualsExact(geography));
    }

    [Fact]
    public void Plugin_maps_rich_geometry_geography_typmods_and_arrays()
    {
        using var dataSource = new BlueTuskDataSourceBuilder(OfflineConnectionString)
            .UsePostGis("Spatial Types")
            .Build();
        using var context = new SpatialContext(CreateOptions(dataSource, "Spatial Types"));
        var entityType = context.Model.FindEntityType(typeof(SpatialValue))!;

        Assert.Equal(
            "geometry(Point,4326)",
            entityType.FindProperty(nameof(SpatialValue.Location))!.GetRelationalTypeMapping().StoreType);
        Assert.Equal(
            "\"Spatial Types\".\"geometry\"",
            entityType.FindProperty(nameof(SpatialValue.Shape))!.GetRelationalTypeMapping().StoreType);
        Assert.Equal(
            "geometry(Polygon,4326)",
            entityType.FindProperty(nameof(SpatialValue.Coverage))!.GetRelationalTypeMapping().StoreType);
        Assert.Equal(
            "geography(Point,4326)",
            entityType.FindProperty(nameof(SpatialValue.ServiceArea))!.GetRelationalTypeMapping().StoreType);
        Assert.Equal(
            "geometry(Point,4326)[]",
            entityType.FindProperty(nameof(SpatialValue.Waypoints))!.GetRelationalTypeMapping().StoreType);

        var createScript = context.Database.GenerateCreateScript();
        Assert.Contains("geometry(Point,4326)", createScript, StringComparison.Ordinal);
        Assert.Contains("geography(Point,4326)", createScript, StringComparison.Ordinal);
        Assert.Contains("geometry(Point,4326)[]", createScript, StringComparison.Ordinal);
    }

    [Fact]
    public void Plugin_translates_spatial_methods_members_and_postgis_functions()
    {
        using var dataSource = new BlueTuskDataSourceBuilder(OfflineConnectionString)
            .UsePostGis("Spatial Types")
            .Build();
        using var context = new SpatialContext(CreateOptions(dataSource, "Spatial Types"));
        var probe = Point(0, 0);
        var geographyProbe = Point(0.01, 0.01);

        var sql = context.Values.Select(value => new
        {
            Distance = value.Location.Distance(probe),
            Nearby = EF.Functions.IsWithinDistance(value.Location, probe, 0.25),
            BoundingBox = EF.Functions.BoundingBoxIntersects(value.Location, probe),
            Intersects = value.Shape.Intersects(value.Coverage),
            Area = value.Coverage.Area,
            Centroid = value.Coverage.Centroid,
            X = value.Location.X,
            Valid = value.Coverage.IsValid,
            Fixed = EF.Functions.MakeValid(value.Coverage),
            Flat = EF.Functions.Force2D(value.Location),
            Transformed = EF.Functions.Transform(value.Location, 3857),
            GeoJson = EF.Functions.AsGeoJson(value.Coverage),
            GeographyDistance = value.ServiceArea.Distance(geographyProbe),
        }).ToQueryString();

        Assert.Contains("st_distance", sql, StringComparison.Ordinal);
        Assert.Contains("\"Spatial Types\".\"st_distance\"", sql, StringComparison.Ordinal);
        Assert.Contains("st_dwithin", sql, StringComparison.Ordinal);
        Assert.Contains(" && ", sql, StringComparison.Ordinal);
        Assert.Contains("st_intersects", sql, StringComparison.Ordinal);
        Assert.Contains("st_area", sql, StringComparison.Ordinal);
        Assert.Contains("st_centroid", sql, StringComparison.Ordinal);
        Assert.Contains("st_x", sql, StringComparison.Ordinal);
        Assert.Contains("st_isvalid", sql, StringComparison.Ordinal);
        Assert.Contains("st_makevalid", sql, StringComparison.Ordinal);
        Assert.Contains("st_force2d", sql, StringComparison.Ordinal);
        Assert.Contains("st_transform", sql, StringComparison.Ordinal);
        Assert.Contains("st_asgeojson", sql, StringComparison.Ordinal);
        Assert.Contains("@probe", sql, StringComparison.Ordinal);
        Assert.Contains("@geographyProbe", sql, StringComparison.Ordinal);

        var unsupported = Assert.Throws<InvalidOperationException>(() => context.Values
            .Where(value => value.ServiceArea.Contains(geographyProbe))
            .ToQueryString());
        Assert.Contains("only supported for geometry operands", unsupported.Message, StringComparison.Ordinal);

        var mixed = Assert.Throws<InvalidOperationException>(() => context.Values
            .Where(value => value.Location.Intersects(value.ServiceArea))
            .ToQueryString());
        Assert.Contains("cannot mix geometry and geography", mixed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plugin_options_participate_in_service_provider_caching_and_debug_metadata()
    {
        using var dataSource = new BlueTuskDataSourceBuilder(OfflineConnectionString)
            .UsePostGis()
            .Build();
        var publicExtension = GetPostGisExtension(CreateOptions(dataSource, "public"));
        var matchingExtension = GetPostGisExtension(CreateOptions(dataSource, "public"));
        var customExtension = GetPostGisExtension(CreateOptions(dataSource, "Spatial Types"));
        var debugInfo = new Dictionary<string, string>();

        publicExtension.Info.PopulateDebugInfo(debugInfo);

        Assert.True(publicExtension.Info.ShouldUseSameServiceProvider(matchingExtension.Info));
        Assert.False(publicExtension.Info.ShouldUseSameServiceProvider(customExtension.Info));
        Assert.NotEqual(
            publicExtension.Info.GetServiceProviderHashCode(),
            customExtension.Info.GetServiceProviderHashCode());
        Assert.Equal("public", debugInfo["BlueTusk:PostGIS"]);
    }

    [Fact]
    public void Migration_helpers_quote_schema_and_keep_postgis_sql_out_of_core()
    {
        var migrationBuilder = new MigrationBuilder("BlueTusk.EntityFrameworkCore");

        migrationBuilder.EnsurePostGis("Spatial \"Types");
        migrationBuilder.DropPostGis(cascade: true);

        var operations = migrationBuilder.Operations.Cast<SqlOperation>().ToArray();
        Assert.Equal(
            "CREATE EXTENSION IF NOT EXISTS \"postgis\" WITH SCHEMA \"Spatial \"\"Types\"",
            operations[0].Sql);
        Assert.Equal("DROP EXTENSION IF EXISTS \"postgis\" CASCADE", operations[1].Sql);
    }

    [Fact]
    public async Task Plugin_round_trips_rich_spatial_values_and_executes_compiled_queries_live()
    {
        var connectionString = GetConnectionString();
        await RequireExtensionAvailableAsync(connectionString, "postgis");
        await using (var administration = BlueTuskDataSource.Create(connectionString))
        await using (var setup = administration.CreateCommand(
                         "CREATE EXTENSION IF NOT EXISTS postgis; " +
                         "DROP TABLE IF EXISTS bluetusk_postgis_ef_values; " +
                         "CREATE TABLE bluetusk_postgis_ef_values (" +
                         "id int4 PRIMARY KEY, " +
                         "location geometry(Point,4326) NOT NULL, " +
                         "shape geometry NOT NULL, " +
                         "coverage geometry(Polygon,4326) NOT NULL, " +
                         "service_area geography(Point,4326) NOT NULL, " +
                         "waypoints geometry(Point,4326)[] NOT NULL)"))
        {
            _ = await setup.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
            .UsePostGis()
            .Build();
        await using var context = CreateContext(dataSource);
        try
        {
            var location = Point(-0.1276, 51.5072);
            var serviceArea = Point(-0.1276, 51.5072);
            var coverage = Polygon(
                (-0.14, 51.50),
                (-0.11, 51.50),
                (-0.11, 51.52),
                (-0.14, 51.52),
                (-0.14, 51.50));
            var expected = new SpatialValue
            {
                Id = 1,
                Location = location,
                Shape = coverage.Copy(),
                Coverage = coverage,
                ServiceArea = serviceArea,
                Waypoints = [location, null, Point(-0.12, 51.51)],
            };
            context.Add(expected);
            Assert.Equal(1, await context.SaveChangesAsync());

            var roundTrip = await context.Values.AsNoTracking().SingleAsync();
            Assert.True(expected.Location.EqualsExact(roundTrip.Location));
            Assert.True(expected.Shape.EqualsExact(roundTrip.Shape));
            Assert.True(expected.Coverage.EqualsExact(roundTrip.Coverage));
            Assert.True(expected.ServiceArea.EqualsExact(roundTrip.ServiceArea));
            Assert.Equal(expected.Waypoints.Length, roundTrip.Waypoints.Length);
            Assert.All(expected.Waypoints.Zip(roundTrip.Waypoints), pair =>
            {
                if (pair.First is null)
                {
                    Assert.Null(pair.Second);
                }
                else
                {
                    Assert.True(pair.First.EqualsExact(pair.Second));
                }
            });

            var probe = Point(-0.128, 51.507);
            var spatial = await context.Values
                .Where(value => EF.Functions.IsWithinDistance(value.Location, probe, 0.01))
                .Select(value => new
                {
                    value.Location.X,
                    Area = value.Coverage.Area,
                    Distance = value.Location.Distance(probe),
                    Json = EF.Functions.AsGeoJson(value.Coverage),
                })
                .SingleAsync();
            Assert.Equal(location.X, spatial.X, 12);
            Assert.True(spatial.Area > 0);
            Assert.True(spatial.Distance < 0.01);
            Assert.Contains("Polygon", spatial.Json, StringComparison.OrdinalIgnoreCase);

            var compiled = EF.CompileQuery(
                (SpatialContext database, Point requestedPoint, double distance) =>
                    database.Values
                        .AsNoTracking()
                        .Where(value => EF.Functions.IsWithinDistance(
                            value.Location,
                            requestedPoint,
                            distance))
                        .Select(value => value.Id));
            Assert.Equal([expected.Id], compiled(context, probe, 0.01).ToArray());

            var geographyProbe = Point(-0.1277, 51.5073);
            var geographyDistance = await context.Values
                .Select(value => value.ServiceArea.Distance(geographyProbe))
                .SingleAsync();
            Assert.InRange(geographyDistance, 0, 20);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync(
                "DROP TABLE IF EXISTS bluetusk_postgis_ef_values");
        }
    }

    private static SpatialContext CreateContext(BlueTuskDataSource dataSource) =>
        new(CreateOptions(dataSource, "public"));

    private static DbContextOptions<SpatialContext> CreateOptions(
        BlueTuskDataSource dataSource,
        string schema) =>
        new DbContextOptionsBuilder<SpatialContext>()
            .UseBlueTusk(dataSource, provider => provider.UsePostGis(schema))
            .Options;

    private static IDbContextOptionsExtension GetPostGisExtension(
        DbContextOptions<SpatialContext> options) =>
        options.Extensions.Single(extension =>
            extension.Info.LogFragment.Contains("PostGIS", StringComparison.Ordinal));

    private static Point Point(double x, double y) =>
        new(x, y) { SRID = 4326 };

    private static Polygon Polygon(params (double X, double Y)[] coordinates)
    {
        var factory = new GeometryFactory(new PrecisionModel(), 4326);
        return factory.CreatePolygon(
            coordinates.Select(coordinate => new Coordinate(coordinate.X, coordinate.Y)).ToArray());
    }

    private static async Task RequireExtensionAvailableAsync(
        string connectionString,
        string extensionName)
    {
        await using var dataSource = BlueTuskDataSource.Create(connectionString);
        await using var command = dataSource.CreateCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = $1)");
        command.Parameters.Add(new BlueTuskParameter<string>(extensionName));
        if (!await command.ExecuteScalarAsync<bool>(CancellationToken.None))
        {
            throw SkipException.ForSkip(
                $"PostgreSQL extension '{extensionName}' is not available on the configured server.");
        }
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

    private sealed class SpatialContext(DbContextOptions<SpatialContext> options) : DbContext(options)
    {
        public DbSet<SpatialValue> Values => Set<SpatialValue>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SpatialValue>(entity =>
            {
                entity.ToTable("bluetusk_postgis_ef_values");
                entity.HasKey(value => value.Id);
                entity.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
                entity.Property(value => value.Location)
                    .HasColumnName("location")
                    .HasColumnType("geometry(Point,4326)");
                entity.Property(value => value.Shape).HasColumnName("shape");
                entity.Property(value => value.Coverage)
                    .HasColumnName("coverage")
                    .HasColumnType("geometry(Polygon,4326)");
                entity.Property(value => value.ServiceArea)
                    .HasColumnName("service_area")
                    .HasColumnType("geography(Point,4326)");
                entity.Property(value => value.Waypoints)
                    .HasColumnName("waypoints")
                    .HasColumnType("geometry(Point,4326)[]");
            });
        }
    }

    private sealed class SpatialValue
    {
        public int Id { get; set; }

        public Point Location { get; set; } = Point(0, 0);

        public Geometry Shape { get; set; } = Point(0, 0);

        public Polygon Coverage { get; set; } = Polygon(
            (0, 0),
            (1, 0),
            (0, 1),
            (0, 0));

        public Point ServiceArea { get; set; } = Point(0, 0);

        public Point?[] Waypoints { get; set; } = [];
    }
}
