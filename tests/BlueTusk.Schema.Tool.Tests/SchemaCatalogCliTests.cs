using System.Text.Json;
using BlueTusk.Data;
using BlueTusk.Schema;
using Xunit.Sdk;

namespace BlueTusk.Schema.Tool.Tests;

public sealed class SchemaCatalogCliTests
{
    [Fact]
    public async Task Catalogue_comparison_reports_enum_review_without_body_or_default_sql()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bluetusk-schema-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var before = Path.Combine(directory, "before.json"); var after = Path.Combine(directory, "after.json"); var report = Path.Combine(directory, "report.json");
            await File.WriteAllBytesAsync(before, SchemaCatalogSerializer.Serialize(new(new([]), [new(new("app", "state"), "e", ["a"])])));
            await File.WriteAllBytesAsync(after, SchemaCatalogSerializer.Serialize(new(new([]), [new(new("app", "state"), "e", ["a", "private-enum-label"])])));
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.Equal(3, await SchemaCli.RunAsync(["compare-catalog", "--before", before, "--after", after, "--output", report], output, error));
            Assert.DoesNotContain("private-enum-label", await File.ReadAllTextAsync(report), StringComparison.Ordinal);
            Assert.Empty(error.ToString());
            await File.WriteAllTextAsync(after, "{\"FormatVersion\":999}");
            var preserved = await File.ReadAllBytesAsync(report);
            Assert.Equal(1, await SchemaCli.RunAsync(["compare-catalog", "--before", before, "--after", after, "--output", report], output, error));
            Assert.Equal(preserved, await File.ReadAllBytesAsync(report));
        }
        finally { DeleteOwned(directory); }
    }

    [Fact]
    public async Task Catalogue_capture_and_check_detect_drift_which_relation_format_does_not_observe()
    {
        var connection = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING") is { Length: > 0 } value
            ? value : throw SkipException.ForSkip("A disposable PostgreSQL fixture is required.");
        await using var source = BlueTuskDataSource.Create(connection);
        var schema = "catalog_cli_" + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), "bluetusk-schema-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using (var setup = source.CreateCommand($"CREATE SCHEMA \"{schema}\"; CREATE TYPE \"{schema}\".state AS ENUM ('new')"))
            { await setup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken); }
            var snapshot = Path.Combine(directory, "catalog.json"); var report = Path.Combine(directory, "drift.json");
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.Equal(0, await SchemaCli.RunAsync(["capture-catalog", "--schemas", schema, "--output", snapshot], output, error, source, TestContext.Current.CancellationToken));
            Assert.Single(SchemaCatalogSerializer.Deserialize(await File.ReadAllBytesAsync(snapshot)).Types);
            Assert.Equal(0, await SchemaCli.RunAsync(["check-catalog", "--schemas", schema, "--baseline", snapshot], output, error, source, TestContext.Current.CancellationToken));
            await using (var alter = source.CreateCommand($"ALTER TYPE \"{schema}\".state ADD VALUE 'done'"))
            { await alter.ExecuteNonQueryAsync(TestContext.Current.CancellationToken); }
            Assert.Equal(2, await SchemaCli.RunAsync(["check-catalog", "--schemas", schema, "--baseline", snapshot, "--output", report], output, error, source, TestContext.Current.CancellationToken));
            using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(report));
            Assert.True(json.RootElement.GetProperty("hasDrift").GetBoolean());
            Assert.False(json.RootElement.GetProperty("hasIncompatibleChanges").GetBoolean());
            Assert.True(json.RootElement.GetProperty("requiresReview").GetBoolean());
            Assert.Empty(error.ToString());
        }
        finally
        {
            await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE"); await cleanup.ExecuteNonQueryAsync();
            DeleteOwned(directory);
        }
    }

    private static void DeleteOwned(string directory)
    {
        var path = Path.GetFullPath(directory);
        if (!path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).StartsWith("bluetusk-schema-catalog-", StringComparison.Ordinal)) { throw new InvalidOperationException("Refusing unowned cleanup."); }
        Directory.Delete(path, recursive: true);
    }
}
