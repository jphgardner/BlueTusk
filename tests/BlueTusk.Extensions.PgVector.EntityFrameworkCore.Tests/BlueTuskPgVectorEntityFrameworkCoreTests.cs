using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Query;
using BlueTusk.Extensions.PgVector;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;
using Xunit.Sdk;

namespace BlueTusk.Extensions.PgVector.EntityFrameworkCore.Tests;

public sealed class BlueTuskPgVectorEntityFrameworkCoreTests
{
    private const string OfflineConnectionString =
        "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=bluetusk_tests";

    [Fact]
    public void Plugin_maps_scalar_array_and_dimension_qualified_properties()
    {
        using var dataSource = new BlueTuskDataSourceBuilder(OfflineConnectionString)
            .UsePgVector()
            .Build();
        using var context = CreateContext(dataSource);
        var entityType = context.Model.FindEntityType(typeof(VectorValue))!;

        Assert.Equal(
            "vector(3)",
            entityType.FindProperty(nameof(VectorValue.Embedding))!.GetRelationalTypeMapping().StoreType);
        Assert.Equal(
            "\"public\".\"vector\"[]",
            entityType.FindProperty(nameof(VectorValue.History))!.GetRelationalTypeMapping().StoreType);
        Assert.Contains("vector(3)", context.Database.GenerateCreateScript(), StringComparison.Ordinal);
    }

    [Fact]
    public void Plugin_translates_all_dense_distance_operators()
    {
        using var dataSource = new BlueTuskDataSourceBuilder(OfflineConnectionString)
            .UsePgVector()
            .Build();
        using var context = CreateContext(dataSource);
        var probe = new BlueTuskVector(1, 2, 3);

        var sql = context.Values.Select(value => new
        {
            L2 = EF.Functions.L2Distance(value.Embedding, probe),
            InnerProduct = EF.Functions.MaxInnerProduct(value.Embedding, probe),
            Cosine = EF.Functions.CosineDistance(value.Embedding, probe),
            L1 = EF.Functions.L1Distance(value.Embedding, probe),
        }).ToQueryString();

        Assert.Contains("<->", sql, StringComparison.Ordinal);
        Assert.Contains("<#>", sql, StringComparison.Ordinal);
        Assert.Contains("<=>", sql, StringComparison.Ordinal);
        Assert.Contains("<+>", sql, StringComparison.Ordinal);
        Assert.Contains("@probe", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Plugin_options_participate_in_service_provider_caching_and_debug_metadata()
    {
        using var dataSource = new BlueTuskDataSourceBuilder(OfflineConnectionString)
            .UsePgVector()
            .Build();
        var publicExtension = GetPgVectorExtension(CreateOptions(dataSource, "public"));
        var matchingExtension = GetPgVectorExtension(CreateOptions(dataSource, "public"));
        var customExtension = GetPgVectorExtension(CreateOptions(dataSource, "Application Types"));
        var debugInfo = new Dictionary<string, string>();

        publicExtension.Info.PopulateDebugInfo(debugInfo);

        Assert.True(publicExtension.Info.ShouldUseSameServiceProvider(matchingExtension.Info));
        Assert.False(publicExtension.Info.ShouldUseSameServiceProvider(customExtension.Info));
        Assert.NotEqual(
            publicExtension.Info.GetServiceProviderHashCode(),
            customExtension.Info.GetServiceProviderHashCode());
        Assert.Equal("public", debugInfo["BlueTusk:PgVector"]);
    }

    [Fact]
    public void Provider_operator_factory_rejects_non_operator_SQL()
    {
        using var dataSource = new BlueTuskDataSourceBuilder(OfflineConnectionString)
            .UsePgVector()
            .Build();
        using var context = CreateContext(dataSource);
        var factory = context.GetService<ISqlExpressionFactory>();
        var mapping = context.GetService<IRelationalTypeMappingSource>().FindMapping(typeof(int))!;

        Assert.Throws<ArgumentException>(() => BlueTuskSqlExpressionFactory.BinaryOperator(
            factory.Constant(1, mapping),
            factory.Constant(2, mapping),
            "; DROP TABLE app_data; --",
            typeof(int),
            mapping));
    }

    [Fact]
    public void Migration_helpers_quote_schema_and_keep_pgvector_SQL_out_of_core()
    {
        var migrationBuilder = new MigrationBuilder("BlueTusk.EntityFrameworkCore");

        migrationBuilder.EnsureBlueTuskPgVector("Application \"Types");
        migrationBuilder.DropBlueTuskPgVector(cascade: true);

        var operations = migrationBuilder.Operations.Cast<SqlOperation>().ToArray();
        Assert.Equal(
            "CREATE EXTENSION IF NOT EXISTS \"vector\" WITH SCHEMA \"Application \"\"Types\"",
            operations[0].Sql);
        Assert.Equal("DROP EXTENSION IF EXISTS \"vector\" CASCADE", operations[1].Sql);
    }

    [Fact]
    public async Task Plugin_round_trips_values_and_executes_distance_queries_live()
    {
        var connectionString = GetConnectionString();
        await using (var administration = BlueTuskDataSource.Create(connectionString))
        await using (var setup = administration.CreateCommand(
                         "CREATE EXTENSION IF NOT EXISTS vector; " +
                         "DROP TABLE IF EXISTS bluetusk_pgvector_ef_values; " +
                         "CREATE TABLE bluetusk_pgvector_ef_values (" +
                         "id int4 PRIMARY KEY, embedding vector(3) NOT NULL, history vector[] NOT NULL)"))
        {
            _ = await setup.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
            .UsePgVector()
            .Build();
        await using var context = CreateContext(dataSource);
        try
        {
            var near = new VectorValue
            {
                Id = 1,
                Embedding = new BlueTuskVector(1, 2, 3),
                History = [new(3, 2, 1)],
            };
            var far = new VectorValue
            {
                Id = 2,
                Embedding = new BlueTuskVector(10, 10, 10),
                History = [new(1, 1, 1), new(2, 2, 2)],
            };
            context.AddRange(near, far);
            Assert.Equal(2, await context.SaveChangesAsync());

            var probe = new BlueTuskVector(1, 2, 4);
            var nearest = await context.Values
                .AsNoTracking()
                .OrderBy(value => EF.Functions.L2Distance(value.Embedding, probe))
                .FirstAsync();

            Assert.Equal(near.Id, nearest.Id);
            Assert.Equal(near.Embedding, nearest.Embedding);
            Assert.Equal(near.History, nearest.History);
            Assert.Equal(
                1d,
                await context.Values
                    .Where(value => value.Id == near.Id)
                    .Select(value => EF.Functions.L2Distance(value.Embedding, probe))
                    .SingleAsync(),
                12);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync(
                "DROP TABLE IF EXISTS bluetusk_pgvector_ef_values");
        }
    }

    private static VectorContext CreateContext(BlueTuskDataSource dataSource) =>
        new(CreateOptions(dataSource, "public"));

    private static DbContextOptions<VectorContext> CreateOptions(
        BlueTuskDataSource dataSource,
        string schema) =>
        new DbContextOptionsBuilder<VectorContext>()
            .UseBlueTusk(dataSource, provider => provider.UsePgVector(schema))
            .Options;

    private static Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsExtension GetPgVectorExtension(
        DbContextOptions<VectorContext> options) =>
        options.Extensions.Single(extension =>
            extension.Info.LogFragment.Contains("pgvector", StringComparison.Ordinal));

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

    private sealed class VectorContext(DbContextOptions<VectorContext> options) : DbContext(options)
    {
        public DbSet<VectorValue> Values => Set<VectorValue>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<VectorValue>(entity =>
            {
                entity.ToTable("bluetusk_pgvector_ef_values");
                entity.HasKey(value => value.Id);
                entity.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
                entity.Property(value => value.Embedding).HasColumnName("embedding").HasColumnType("vector(3)");
                entity.Property(value => value.History).HasColumnName("history");
            });
        }
    }

    private sealed class VectorValue
    {
        public int Id { get; set; }

        public BlueTuskVector Embedding { get; set; } = new(0);

        public BlueTuskVector[] History { get; set; } = [];
    }
}
