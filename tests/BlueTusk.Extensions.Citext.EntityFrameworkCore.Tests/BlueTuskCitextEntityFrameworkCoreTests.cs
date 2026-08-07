using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.Extensions.Citext;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit.Sdk;

namespace BlueTusk.Extensions.Citext.EntityFrameworkCore.Tests;

public sealed class BlueTuskCitextEntityFrameworkCoreTests
{
    private const string OfflineConnectionString =
        "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=bluetusk_tests";

    [Fact]
    public void Plugin_maps_scalar_and_array_properties_and_translates_equality()
    {
        using var dataSource = new BlueTuskDataSourceBuilder(OfflineConnectionString)
            .UseCitext()
            .Build();
        using var context = CreateContext(dataSource);

        var entityType = context.Model.FindEntityType(typeof(CitextValue))!;
        Assert.Equal(
            "\"public\".\"citext\"",
            entityType.FindProperty(nameof(CitextValue.Name))!.GetRelationalTypeMapping().StoreType);
        Assert.Equal(
            "\"public\".\"citext\"[]",
            entityType.FindProperty(nameof(CitextValue.Aliases))!.GetRelationalTypeMapping().StoreType);

        var probe = new BlueTuskCitext("bluetusk");
        var sql = context.Values
            .Where(value => value.Name == probe)
            .Select(value => value.Name)
            .ToQueryString();

        Assert.Contains("WHERE \"b\".\"name\" = @probe", sql, StringComparison.Ordinal);
        Assert.Contains("\"public\".\"citext\"", context.Database.GenerateCreateScript(), StringComparison.Ordinal);
    }

    [Fact]
    public void Plugin_options_participate_in_service_provider_caching_and_debug_metadata()
    {
        using var dataSource = new BlueTuskDataSourceBuilder(OfflineConnectionString)
            .UseCitext()
            .Build();
        var publicOptions = CreateOptions(dataSource, "public");
        var matchingOptions = CreateOptions(dataSource, "public");
        var customOptions = CreateOptions(dataSource, "Application Types");
        var publicExtension = GetCitextExtension(publicOptions);
        var matchingExtension = GetCitextExtension(matchingOptions);
        var customExtension = GetCitextExtension(customOptions);
        var debugInfo = new Dictionary<string, string>();

        publicExtension.Info.PopulateDebugInfo(debugInfo);

        Assert.True(publicExtension.Info.ShouldUseSameServiceProvider(matchingExtension.Info));
        Assert.False(publicExtension.Info.ShouldUseSameServiceProvider(customExtension.Info));
        Assert.NotEqual(
            publicExtension.Info.GetServiceProviderHashCode(),
            customExtension.Info.GetServiceProviderHashCode());
        Assert.Equal("public", debugInfo["BlueTusk:Citext"]);
    }

    [Fact]
    public void Migration_helpers_quote_schema_and_keep_citext_SQL_out_of_the_core_provider()
    {
        var migrationBuilder = new MigrationBuilder("BlueTusk.EntityFrameworkCore");

        migrationBuilder.EnsureCitext("Application \"Types");
        migrationBuilder.DropCitext(cascade: true);

        var operations = migrationBuilder.Operations.Cast<SqlOperation>().ToArray();
        Assert.Equal(
            "CREATE EXTENSION IF NOT EXISTS \"citext\" WITH SCHEMA \"Application \"\"Types\"",
            operations[0].Sql);
        Assert.Equal("DROP EXTENSION IF EXISTS \"citext\" CASCADE", operations[1].Sql);
    }

    [Fact]
    public async Task Plugin_round_trips_scalar_array_and_case_insensitive_query_through_EF()
    {
        var connectionString = GetConnectionString();
        await using (var administration = BlueTuskDataSource.Create(connectionString))
        await using (var create = administration.CreateCommand("CREATE EXTENSION IF NOT EXISTS citext"))
        {
            _ = await create.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
            .UseCitext()
            .Build();
        await using var context = CreateContext(dataSource);
        await context.Database.ExecuteSqlRawAsync(
            """
            DROP TABLE IF EXISTS bluetusk_citext_ef_values;
            CREATE TABLE bluetusk_citext_ef_values (
                id int4 PRIMARY KEY,
                name citext NOT NULL,
                aliases citext[] NOT NULL);
            """);

        try
        {
            var expected = new CitextValue
            {
                Id = 1,
                Name = new BlueTuskCitext("BlueTusk"),
                Aliases = [new("Tusk"), new("PostgreSQL")],
            };
            context.Add(expected);
            Assert.Equal(1, await context.SaveChangesAsync());

            var probe = new BlueTuskCitext("bluetusk");
            var actual = await context.Values
                .AsNoTracking()
                .SingleAsync(value => value.Name == probe);

            Assert.Equal(expected.Name, actual.Name);
            Assert.Equal(expected.Aliases, actual.Aliases);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS bluetusk_citext_ef_values");
        }
    }

    private static CitextContext CreateContext(BlueTuskDataSource dataSource) =>
        new(CreateOptions(dataSource, "public"));

    private static DbContextOptions<CitextContext> CreateOptions(
        BlueTuskDataSource dataSource,
        string schema) =>
        new DbContextOptionsBuilder<CitextContext>()
            .UseBlueTusk(dataSource, provider => provider.UseCitext(schema))
            .Options;

    private static Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsExtension GetCitextExtension(
        DbContextOptions<CitextContext> options) =>
        options.Extensions.Single(extension =>
            extension.Info.LogFragment.Contains("citext", StringComparison.Ordinal));

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

    private sealed class CitextContext(DbContextOptions<CitextContext> options) : DbContext(options)
    {
        public DbSet<CitextValue> Values => Set<CitextValue>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CitextValue>(entity =>
            {
                entity.ToTable("bluetusk_citext_ef_values");
                entity.HasKey(value => value.Id);
                entity.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
                entity.Property(value => value.Name).HasColumnName("name");
                entity.Property(value => value.Aliases).HasColumnName("aliases");
            });
        }
    }

    private sealed class CitextValue
    {
        public int Id { get; set; }

        public BlueTuskCitext Name { get; set; } = new(string.Empty);

        public BlueTuskCitext[] Aliases { get; set; } = [];
    }
}
