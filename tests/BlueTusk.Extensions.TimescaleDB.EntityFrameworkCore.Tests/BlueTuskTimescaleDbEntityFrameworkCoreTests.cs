using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.Extensions.TimescaleDB;
using BlueTusk.TypeSystem;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;
using Xunit.Sdk;

namespace BlueTusk.Extensions.TimescaleDB.EntityFrameworkCore.Tests;

public sealed class BlueTuskTimescaleDbEntityFrameworkCoreTests
{
    private const string OfflineConnectionString =
        "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=bluetusk_tests";

    [Fact]
    public void Plugin_translates_time_buckets_hyperfunctions_and_aggregate_modifiers()
    {
        using var dataSource = new BlueTuskDataSourceBuilder(OfflineConnectionString).Build();
        using var context = new TimescaleContext(CreateOptions(dataSource, "Application Extensions"));
        var width = Hours(1);
        var offset = Minutes(15);
        var origin = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var timezone = "Europe/London";
        var minimum = 10d;

        var bucketSql = context.Metrics.Select(metric => new
        {
            Timestamp = EF.Functions.TimeBucket(width, metric.RecordedAt),
            Offset = EF.Functions.TimeBucket(width, metric.RecordedAt, offset),
            Origin = EF.Functions.TimeBucket(width, metric.RecordedAt, origin),
            Zoned = EF.Functions.TimeBucket(width, metric.RecordedAt, timezone),
            ZonedWithOrigin = EF.Functions.TimeBucket(
                width,
                metric.RecordedAt,
                timezone,
                origin,
                offset),
            Local = EF.Functions.TimeBucket(width, metric.ObservedAt),
            Date = EF.Functions.TimeBucket(width, metric.BusinessDate),
            Integer = EF.Functions.TimeBucket(10, metric.Sequence, 2),
            BigInteger = EF.Functions.TimeBucket(10L, metric.BigSequence),
        }).ToQueryString();

        var aggregateSql = context.Metrics
            .GroupBy(metric => metric.SensorId)
            .Select(group => new
            {
                First = EF.Functions.TimescaleFirst(
                    group.OrderBy(metric => metric.RecordedAt)
                        .Select(metric => ValueTuple.Create(metric.Value, metric.RecordedAt))),
                Last = EF.Functions.TimescaleLast(
                    group.Select(metric => ValueTuple.Create(metric.Value, metric.RecordedAt))
                        .Distinct()),
                Histogram = EF.Functions.TimescaleHistogram(
                    group.Where(metric => metric.Value >= minimum).Select(metric => metric.Value),
                    0,
                    100,
                    4),
            })
            .ToQueryString();

        Assert.Contains("\"Application Extensions\".\"time_bucket\"", bucketSql, StringComparison.Ordinal);
        Assert.Contains("@width", bucketSql, StringComparison.Ordinal);
        Assert.Contains("@offset", bucketSql, StringComparison.Ordinal);
        Assert.Contains("@origin", bucketSql, StringComparison.Ordinal);
        Assert.Contains("@timezone", bucketSql, StringComparison.Ordinal);
        Assert.Contains("\"Application Extensions\".\"first\"(", aggregateSql, StringComparison.Ordinal);
        Assert.Contains(" ORDER BY ", aggregateSql, StringComparison.Ordinal);
        Assert.Contains("\"Application Extensions\".\"last\"(DISTINCT ", aggregateSql, StringComparison.Ordinal);
        Assert.Contains("\"Application Extensions\".\"histogram\"(", aggregateSql, StringComparison.Ordinal);
        Assert.Contains(" FILTER (WHERE ", aggregateSql, StringComparison.Ordinal);
        Assert.Contains("@minimum", aggregateSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Plugin_options_participate_in_service_provider_caching_and_debug_metadata()
    {
        using var dataSource = new BlueTuskDataSourceBuilder(OfflineConnectionString).Build();
        var publicExtension = GetTimescaleDbExtension(CreateOptions(dataSource, "public"));
        var matchingExtension = GetTimescaleDbExtension(CreateOptions(dataSource, "public"));
        var customExtension = GetTimescaleDbExtension(CreateOptions(dataSource, "Time Series"));
        var debugInfo = new Dictionary<string, string>();

        publicExtension.Info.PopulateDebugInfo(debugInfo);

        Assert.True(publicExtension.Info.ShouldUseSameServiceProvider(matchingExtension.Info));
        Assert.False(publicExtension.Info.ShouldUseSameServiceProvider(customExtension.Info));
        Assert.NotEqual(
            publicExtension.Info.GetServiceProviderHashCode(),
            customExtension.Info.GetServiceProviderHashCode());
        Assert.Equal("public", debugInfo["BlueTusk:TimescaleDB"]);
    }

    [Fact]
    public void Migration_helpers_quote_identifiers_and_literals()
    {
        var migrationBuilder = new MigrationBuilder("BlueTusk.EntityFrameworkCore");

        migrationBuilder.EnsureBlueTuskTimescaleDb("Extension \"Schema");
        migrationBuilder.ConvertToBlueTuskHypertable(
            "Metric's \"Data",
            "recorded'at",
            "Tenant's \"Space",
            "Extension \"Schema",
            migrateData: true);
        migrationBuilder.DropBlueTuskTimescaleDb(cascade: true);

        var operations = migrationBuilder.Operations.Cast<SqlOperation>().ToArray();
        Assert.Equal(
            "CREATE EXTENSION IF NOT EXISTS \"timescaledb\" WITH SCHEMA \"Extension \"\"Schema\"",
            operations[0].Sql);
        Assert.Contains("\"Extension \"\"Schema\".\"create_hypertable\"", operations[1].Sql, StringComparison.Ordinal);
        Assert.Contains("'\"Tenant''s \"\"Space\".\"Metric''s \"\"Data\"'::regclass", operations[1].Sql, StringComparison.Ordinal);
        Assert.Contains("'recorded''at'::name", operations[1].Sql, StringComparison.Ordinal);
        Assert.Contains("migrate_data => TRUE", operations[1].Sql, StringComparison.Ordinal);
        Assert.Equal("DROP EXTENSION IF EXISTS \"timescaledb\" CASCADE", operations[2].Sql);
    }

    [Fact]
    public void Query_methods_reject_client_evaluation()
    {
        var width = Hours(1);

        Assert.Throws<InvalidOperationException>(() =>
            EF.Functions.TimeBucket(width, DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(() =>
            EF.Functions.TimescaleFirst(new[] { (1d, DateTimeOffset.UtcNow) }));
        Assert.Throws<InvalidOperationException>(() =>
            EF.Functions.TimescaleHistogram([1d], 0, 2, 2));
    }

    [Fact]
    public async Task Plugin_executes_buckets_hyperfunctions_compiled_queries_and_policies_live()
    {
        var connectionString = GetConnectionString();
        await using (var administration = BlueTuskDataSource.Create(connectionString))
        await using (var setup = administration.CreateCommand(
                         "CREATE EXTENSION IF NOT EXISTS timescaledb; " +
                         "DROP MATERIALIZED VIEW IF EXISTS bluetusk_timescale_hourly CASCADE; " +
                         "DROP TABLE IF EXISTS bluetusk_timescale_ef_metrics CASCADE; " +
                         "CREATE TABLE bluetusk_timescale_ef_metrics (" +
                         "id int4 NOT NULL, sensor_id int4 NOT NULL, recorded_at timestamptz NOT NULL, " +
                         "observed_at timestamp NOT NULL, business_date date NOT NULL, " +
                         "sequence int4 NOT NULL, big_sequence int8 NOT NULL, value float8 NOT NULL, " +
                         "PRIMARY KEY (id, recorded_at))"))
        {
            _ = await setup.ExecuteNonQueryAsync(CancellationToken.None);
        }

        const string relation = "\"public\".\"bluetusk_timescale_ef_metrics\"";
        await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
            .UseTimescaleDb()
            .Build();
        await using var context = new TimescaleContext(CreateOptions(dataSource, "public"));
        var start = new DateTimeOffset(2025, 1, 1, 0, 10, 0, TimeSpan.Zero);
        try
        {
            _ = await dataSource.CreateHypertableAsync(relation, "recorded_at");
            context.AddRange(
                CreateMetric(1, start, 10, 1),
                CreateMetric(2, start.AddMinutes(20), 20, 2),
                CreateMetric(3, start.AddMinutes(70), 80, 3));
            Assert.Equal(3, await context.SaveChangesAsync());

            var width = Hours(1);
            var rows = await context.Metrics
                .GroupBy(metric => EF.Functions.TimeBucket(width, metric.RecordedAt))
                .OrderBy(group => group.Key)
                .Select(group => new
                {
                    group.Key,
                    First = EF.Functions.TimescaleFirst(
                        group.Select(metric => ValueTuple.Create(metric.Value, metric.RecordedAt))),
                    Last = EF.Functions.TimescaleLast(
                        group.Select(metric => ValueTuple.Create(metric.Value, metric.RecordedAt))),
                    Histogram = EF.Functions.TimescaleHistogram(
                        group.Select(metric => metric.Value), 0, 100, 2),
                })
                .ToArrayAsync();

            Assert.Equal(2, rows.Length);
            Assert.Equal(10, rows[0].First);
            Assert.Equal(20, rows[0].Last);
            Assert.Equal([0, 2, 0, 0], rows[0].Histogram);
            Assert.Equal(80, rows[1].First);
            Assert.Equal(80, rows[1].Last);
            Assert.Equal([0, 0, 1, 0], rows[1].Histogram);

            var compiled = EF.CompileQuery(
                (TimescaleContext database, BlueTuskInterval bucketWidth) =>
                    database.Metrics
                        .OrderBy(metric => metric.RecordedAt)
                        .Select(metric => EF.Functions.TimeBucket(bucketWidth, metric.RecordedAt))
                        .First());
            Assert.Equal(
                new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
                compiled(context, width));

            Assert.True(await dataSource.GetApproximateRowCountAsync(relation) >= 0);

            await context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE bluetusk_timescale_ef_metrics " +
                "SET (timescaledb.enable_columnstore = true)");
            await dataSource.AddColumnstorePolicyAsync(relation, Hours(1));
            await dataSource.RemoveColumnstorePolicyAsync(relation);

            await context.Database.ExecuteSqlRawAsync(
                "CREATE MATERIALIZED VIEW bluetusk_timescale_hourly " +
                "WITH (timescaledb.continuous) AS " +
                "SELECT time_bucket(INTERVAL '1 hour', recorded_at) AS bucket, avg(value) AS average " +
                "FROM bluetusk_timescale_ef_metrics GROUP BY 1 WITH NO DATA");
            const string continuousAggregate = "\"public\".\"bluetusk_timescale_hourly\"";
            await dataSource.RefreshContinuousAggregateAsync(
                continuousAggregate,
                start.AddHours(-1),
                start.AddHours(3));
            var jobId = await dataSource.AddContinuousAggregatePolicyAsync(
                continuousAggregate,
                Days(7),
                Hours(1),
                Hours(1));
            Assert.True(jobId > 0);
            await dataSource.RemoveContinuousAggregatePolicyAsync(continuousAggregate);

            await using var aggregate = dataSource.CreateCommand(
                "SELECT count(*) FROM bluetusk_timescale_hourly");
            Assert.Equal(2L, await aggregate.ExecuteScalarAsync<long>(CancellationToken.None));
        }
        finally
        {
            await using var cleanup = BlueTuskDataSource.Create(connectionString);
            await using var drop = cleanup.CreateCommand(
                "DROP MATERIALIZED VIEW IF EXISTS bluetusk_timescale_hourly CASCADE; " +
                "DROP TABLE IF EXISTS bluetusk_timescale_ef_metrics CASCADE");
            _ = await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private static Metric CreateMetric(int id, DateTimeOffset recordedAt, double value, int sequence) =>
        new()
        {
            Id = id,
            SensorId = 7,
            RecordedAt = recordedAt,
            ObservedAt = recordedAt.UtcDateTime,
            BusinessDate = DateOnly.FromDateTime(recordedAt.UtcDateTime),
            Sequence = sequence,
            BigSequence = sequence,
            Value = value,
        };

    private static BlueTuskInterval Minutes(long value) =>
        new(months: 0, days: 0, microseconds: value * 60 * 1_000_000);

    private static BlueTuskInterval Hours(long value) => Minutes(value * 60);

    private static BlueTuskInterval Days(int value) =>
        new(months: 0, days: value, microseconds: 0);

    private static DbContextOptions<TimescaleContext> CreateOptions(
        BlueTuskDataSource dataSource,
        string schema) =>
        new DbContextOptionsBuilder<TimescaleContext>()
            .UseBlueTusk(dataSource, provider => provider.UseTimescaleDb(schema))
            .Options;

    private static IDbContextOptionsExtension GetTimescaleDbExtension(
        DbContextOptions<TimescaleContext> options) =>
        options.Extensions.Single(extension =>
            extension.Info.LogFragment.Contains("TimescaleDB", StringComparison.Ordinal));

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

    private sealed class TimescaleContext(DbContextOptions<TimescaleContext> options) : DbContext(options)
    {
        public DbSet<Metric> Metrics => Set<Metric>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Metric>(entity =>
            {
                entity.ToTable("bluetusk_timescale_ef_metrics");
                entity.HasKey(metric => new { metric.Id, metric.RecordedAt });
                entity.Property(metric => metric.Id).HasColumnName("id").ValueGeneratedNever();
                entity.Property(metric => metric.SensorId).HasColumnName("sensor_id");
                entity.Property(metric => metric.RecordedAt).HasColumnName("recorded_at");
                entity.Property(metric => metric.ObservedAt)
                    .HasColumnName("observed_at")
                    .HasColumnType("timestamp without time zone");
                entity.Property(metric => metric.BusinessDate).HasColumnName("business_date");
                entity.Property(metric => metric.Sequence).HasColumnName("sequence");
                entity.Property(metric => metric.BigSequence).HasColumnName("big_sequence");
                entity.Property(metric => metric.Value).HasColumnName("value");
            });
        }
    }

    private sealed class Metric
    {
        public int Id { get; set; }

        public int SensorId { get; set; }

        public DateTimeOffset RecordedAt { get; set; }

        public DateTime ObservedAt { get; set; }

        public DateOnly BusinessDate { get; set; }

        public int Sequence { get; set; }

        public long BigSequence { get; set; }

        public double Value { get; set; }
    }
}
