using BlueTusk.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.CompatibilityTests;

public sealed class ProviderCompatibilityTests
{
    private static readonly int[] ExpectedNumbers = [1, 2, 3];
    private const string ConnectionString = "Host=localhost;Username=postgres;Password=secret;Database=compatibility_tests";

    [Fact]
    public void Configures_the_native_provider_and_external_data_source()
    {
        using var dataSource = BlueTuskDataSource.Create(ConnectionString);
        var options = new DbContextOptionsBuilder<CompatibilityContext>().UseBlueTusk(dataSource).Options;
        using var context = new CompatibilityContext(options);

        Assert.Equal("BlueTusk.EntityFrameworkCore", context.Database.ProviderName);
        Assert.IsType<BlueTuskConnection>(context.Database.GetDbConnection());
        Assert.Equal("compatibility_tests", context.Database.GetDbConnection().Database);
    }

    [Fact]
    public void Translates_parameters_postgresql_operators_and_ordering()
    {
        using var context = CreateContext();
        var pattern = "blue%";
        var sql = context.Rows.Where(row => row.Id > 3 && EF.Functions.ILike(row.Name, pattern))
            .OrderBy(row => row.Name).Take(10).ToQueryString();

        Assert.Contains("ILIKE", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT", sql, StringComparison.Ordinal);
        Assert.Contains("@", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Maps_native_dates_arrays_and_array_snapshots()
    {
        using var context = CreateContext();
        var mappings = context.GetService<IRelationalTypeMappingSource>();
        Assert.Equal("date", mappings.FindMapping(typeof(DateOnly))!.StoreType);
        var arrayMapping = mappings.FindMapping(typeof(int[]))!;
        Assert.Equal("integer[]", arrayMapping.StoreType);
        var value = new[] { 1, 2, 3 };
        var snapshot = Assert.IsType<int[]>(arrayMapping.Comparer.Snapshot(value));
        value[0] = 99;
        Assert.Equal(ExpectedNumbers, snapshot);
        Assert.Contains("ARRAY[1,2,3]", arrayMapping.GenerateSqlLiteral(snapshot), StringComparison.Ordinal);
    }

    [Fact]
    public void Translates_delete_and_multi_property_update_returning()
    {
        using var context = CreateContext();
        var deleted = context.Rows.Where(row => row.Id > 7).DeleteReturning().Select(row => row.Name).ToQueryString();
        var updated = context.Rows.Where(row => row.Id > 7)
            .UpdateReturning(setters => setters.SetProperty(row => row.Name, row => row.Name + "!")
                .SetProperty(row => row.Score, 42))
            .Select(row => new { row.Id, row.Name }).ToQueryString();

        Assert.Contains("DELETE FROM", deleted, StringComparison.Ordinal);
        Assert.Contains("RETURNING", deleted, StringComparison.Ordinal);
        Assert.Contains("UPDATE", updated, StringComparison.Ordinal);
        Assert.Contains("SET", updated, StringComparison.Ordinal);
        Assert.Contains("RETURNING", updated, StringComparison.Ordinal);
        Assert.Contains("\"Score\"", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void Translates_scalar_set_returning_queries()
    {
        using var context = CreateContext();
        var sql = context.Database.GenerateSeries(1, 5).Where(number => number > 2).ToQueryString();

        Assert.Contains("generate_series(", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Translates_typed_json_recordsets()
    {
        using var context = CreateContext();
        var sql = context.Rows.SelectMany(
                row => EF.Functions.JsonToRecordset<JsonRow>("[{\"Id\":1,\"Name\":\"one\"}]"),
                (row, jsonRow) => jsonRow)
            .Where(row => row.Id > 0).ToQueryString();

        Assert.Contains("jsonb_to_recordset(", sql, StringComparison.Ordinal);
        Assert.Contains("integer", sql, StringComparison.Ordinal);
        Assert.Contains("text", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generates_transactional_migration_commands()
    {
        using var context = CreateContext();
        var create = new CreateTableOperation { Name = "compatibility_created" };
        create.Columns.Add(new AddColumnOperation
        {
            Name = "Id",
            Table = create.Name,
            ClrType = typeof(int),
            ColumnType = "integer",
            IsNullable = false,
        });
        create.Columns.Add(new AddColumnOperation
        {
            Name = "Name",
            Table = create.Name,
            ClrType = typeof(string),
            ColumnType = "text",
            IsNullable = false,
        });
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate([create]);

        var command = Assert.Single(commands);
        Assert.Contains("CREATE TABLE \"compatibility_created\"", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("\"Id\" integer NOT NULL", command.CommandText, StringComparison.Ordinal);
        Assert.False(command.TransactionSuppressed);
    }

#if BLUETUSK_EF8_OR_GREATER
    [Fact]
    public void Translates_owned_json_members_and_primitive_collections()
    {
        using var context = CreateContext();
        var scalarSql = context.Rows.Where(row => row.Payload.Name == "one").Select(row => row.Id).ToQueryString();
        var property = context.Model.FindEntityType(typeof(Row))!.FindNavigation(nameof(Row.Payload))!
            .TargetEntityType.FindProperty(nameof(Payload.Numbers))!;
        Assert.True(property.IsPrimitiveCollection,
            $"JSON numbers must retain primitive collection metadata; mapping: {property.GetTypeMapping().GetType().Name}.");
        var collectionSql = context.Rows.SelectMany(row => row.Payload.Numbers).ToQueryString();

        Assert.Contains("->>", scalarSql, StringComparison.Ordinal);
        Assert.Contains("jsonb_array_elements_text(", collectionSql, StringComparison.Ordinal);
    }
#endif

    private static CompatibilityContext CreateContext() => new(new DbContextOptionsBuilder<CompatibilityContext>()
        .UseBlueTusk(ConnectionString).Options);

    private sealed class CompatibilityContext(DbContextOptions<CompatibilityContext> options) : DbContext(options)
    {
        public DbSet<Row> Rows => Set<Row>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Row>(entity =>
            {
                entity.ToTable("compatibility_rows");
                entity.HasKey(row => row.Id);
#if BLUETUSK_EF8_OR_GREATER
                entity.OwnsOne(row => row.Payload, owned => owned.ToJson());
#else
                entity.Ignore(row => row.Payload);
#endif
            });
            modelBuilder.Entity<JsonRow>().HasNoKey().ToTable("compatibility_json_rows");
        }
    }

    private sealed class Row
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Score { get; set; }
        public DateOnly Date { get; set; }
        public int[] Numbers { get; set; } = [];
        public Payload Payload { get; set; } = new();
    }

    private sealed class Payload
    {
        public string Name { get; set; } = string.Empty;
        public int[] Numbers { get; set; } = [];
    }

    private sealed class JsonRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}
