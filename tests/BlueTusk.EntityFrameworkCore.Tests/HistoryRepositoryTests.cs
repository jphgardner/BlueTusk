using BlueTusk.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Tests;

public sealed class HistoryRepositoryTests
{
    private const string ConnectionString =
        "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=bluetusk_tests";

    [Fact]
    public void Generates_PostgreSQL_history_and_idempotency_scripts()
    {
        using var context = CreateContext();
        var repository = context.GetService<IHistoryRepository>();

        var createScript = repository.GetCreateIfNotExistsScript();
        var insertScript = repository.GetInsertScript(new HistoryRow("202607310001_Initial", "10.0.10"));
        var deleteScript = repository.GetDeleteScript("202607310001_Initial");
        var beginUpScript = repository.GetBeginIfNotExistsScript("202607310001_Initial");
        var beginDownScript = repository.GetBeginIfExistsScript("202607310001_Initial");

        Assert.Contains("CREATE TABLE IF NOT EXISTS \"__EFMigrationsHistory\"", createScript, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO \"__EFMigrationsHistory\"", insertScript, StringComparison.Ordinal);
        Assert.Contains("DELETE FROM \"__EFMigrationsHistory\"", deleteScript, StringComparison.Ordinal);
        Assert.Contains("IF NOT EXISTS (SELECT 1 FROM \"__EFMigrationsHistory\"", beginUpScript, StringComparison.Ordinal);
        Assert.Contains("IF EXISTS (SELECT 1 FROM \"__EFMigrationsHistory\"", beginDownScript, StringComparison.Ordinal);
        Assert.Contains("END IF;", repository.GetEndIfScript(), StringComparison.Ordinal);
    }

    [Fact]
    public void Custom_history_table_is_delimited_in_every_script()
    {
        using var context = CreateContext(
            options => options.MigrationsHistoryTable("History \"Table", "History Schema"));
        var repository = context.GetService<IHistoryRepository>();
        const string delimitedTable = "\"History Schema\".\"History \"\"Table\"";

        Assert.Contains($"CREATE TABLE IF NOT EXISTS {delimitedTable}", repository.GetCreateIfNotExistsScript(), StringComparison.Ordinal);
        Assert.Contains($"INSERT INTO {delimitedTable}", repository.GetInsertScript(new HistoryRow("Migration'One", "10.0.10")), StringComparison.Ordinal);
        Assert.Contains($"DELETE FROM {delimitedTable}", repository.GetDeleteScript("Migration'One"), StringComparison.Ordinal);
        Assert.Contains($"SELECT 1 FROM {delimitedTable}", repository.GetBeginIfNotExistsScript("Migration'One"), StringComparison.Ordinal);
        Assert.Contains("'Migration''One'", repository.GetBeginIfNotExistsScript("Migration'One"), StringComparison.Ordinal);
    }

    [Fact]
    public void Idempotent_generation_rejects_transaction_suppressed_DDL()
    {
        using var context = CreateContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var operation = new SqlOperation
        {
            Sql = "VACUUM",
            SuppressTransaction = true,
        };

        var exception = Assert.Throws<NotSupportedException>(() => generator.Generate(
            [operation],
            model: null,
            MigrationsSqlGenerationOptions.Idempotent));

        Assert.Contains("transaction-suppressed DDL", exception.Message, StringComparison.Ordinal);
        Assert.True(Assert.Single(generator.Generate([operation])).TransactionSuppressed);
    }

    [Fact]
    public void Migrator_fails_fast_instead_of_emitting_an_invalid_idempotent_DO_block()
    {
        var options = new DbContextOptionsBuilder<SuppressedCommandContext>()
            .UseBlueTusk(ConnectionString)
            .Options;
        using var context = new SuppressedCommandContext(options);
        var migrator = context.GetService<IMigrator>();

        var exception = Assert.Throws<NotSupportedException>(() => migrator.GenerateScript(
            fromMigration: null,
            toMigration: null,
            MigrationsSqlGenerationOptions.Idempotent));

        Assert.Contains("transaction-suppressed DDL", exception.Message, StringComparison.Ordinal);
        Assert.Contains("VACUUM", migrator.GenerateScript(), StringComparison.Ordinal);
    }

    private static HistoryContext CreateContext(
        Action<BlueTuskDbContextOptionsBuilder>? configureProvider = null)
    {
        var options = new DbContextOptionsBuilder<HistoryContext>()
            .UseBlueTusk(ConnectionString, configureProvider)
            .Options;
        return new HistoryContext(options);
    }

    private sealed class HistoryContext(DbContextOptions<HistoryContext> options) : DbContext(options);
}

[DbContext(typeof(SuppressedCommandContext))]
[Migration("20260801000100_SuppressedCommand")]
internal sealed class SuppressedCommandMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("VACUUM", suppressTransaction: true);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}

internal sealed class SuppressedCommandContext(DbContextOptions<SuppressedCommandContext> options)
    : DbContext(options);
