using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

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

    private static HistoryContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<HistoryContext>()
            .UseBlueTusk(ConnectionString)
            .Options;
        return new HistoryContext(options);
    }

    private sealed class HistoryContext(DbContextOptions<HistoryContext> options) : DbContext(options);
}
