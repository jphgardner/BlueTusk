using System.Text.Json;
using BlueTusk.Data;
using BlueTusk.Schema;
using Xunit.Sdk;

namespace BlueTusk.Schema.Tool.Tests;

public sealed class SchemaCliTests
{
    [Fact]
    public async Task Comparison_writes_safe_structured_changes_and_classifies_incompatibility()
    {
        using var files = new ScratchFiles();
        var before = files.Path("before.json");
        var after = files.Path("after.json");
        var report = files.Path("report.json");
        await File.WriteAllBytesAsync(before, SchemaSnapshotSerializer.Serialize(Snapshot("bigint")));
        await File.WriteAllBytesAsync(after, SchemaSnapshotSerializer.Serialize(Snapshot("text")));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await SchemaCli.RunAsync(["compare", "--before", before, "--after", after, "--output", report], output, error,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, exit);
        using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(report));
        Assert.True(json.RootElement.GetProperty("hasIncompatibleChanges").GetBoolean());
        Assert.Equal("ColumnTypeChanged", json.RootElement.GetProperty("changes")[0].GetProperty("kind").GetString());
        Assert.Empty(error.ToString());
        Assert.DoesNotContain("DefaultSql", await File.ReadAllTextAsync(report), StringComparison.Ordinal);
        Assert.DoesNotContain(Directory.EnumerateFiles(files.Directory), path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tampering_fails_without_overwriting_an_existing_report_or_exposing_input()
    {
        using var files = new ScratchFiles();
        var before = files.Path("before.json");
        var after = files.Path("after.json");
        var report = files.Path("report.json");
        await File.WriteAllBytesAsync(before, SchemaSnapshotSerializer.Serialize(Snapshot("bigint")));
        var invalid = System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(before)).Replace("bigint", "private-secret", StringComparison.Ordinal);
        await File.WriteAllTextAsync(after, invalid);
        await File.WriteAllTextAsync(report, "preserved");
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, await SchemaCli.RunAsync(["compare", "--before", before, "--after", after, "--output", report], output, error));
        Assert.Equal("preserved", await File.ReadAllTextAsync(report));
        Assert.DoesNotContain("private-secret", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task Duplicate_unknown_and_incomplete_arguments_fail_without_database_access()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, await SchemaCli.RunAsync(["capture", "--schemas", "app", "--schemas", "other"], output, error));
        Assert.Equal(1, await SchemaCli.RunAsync(["compare", "--unknown", "secret"], output, error));
        Assert.Equal(1, await SchemaCli.RunAsync(["capture", "--schemas"], output, error));
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Live_capture_and_strict_drift_check_use_a_consistent_read_only_snapshot()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING") is { Length: > 0 } value
            ? value : throw SkipException.ForSkip("A disposable PostgreSQL fixture is required.");
        await using var dataSource = BlueTuskDataSource.Create(connectionString);
        using var files = new ScratchFiles();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var schema = "schema_cli_" + Guid.NewGuid().ToString("N");
        var snapshot = files.Path("live.json");
        var report = files.Path("drift.json");
        try
        {
            await using (var setup = dataSource.CreateCommand($"CREATE SCHEMA \"{schema}\"; CREATE TABLE \"{schema}\".items(id bigint)"))
            {
                _ = await setup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            Assert.Equal(0, await SchemaCli.RunAsync(["capture", "--schemas", schema, "--output", snapshot], output, error, dataSource,
                TestContext.Current.CancellationToken));
            Assert.Single(SchemaSnapshotSerializer.Deserialize(await File.ReadAllBytesAsync(snapshot)).Relations);
            Assert.Equal(0, await SchemaCli.RunAsync(["check", "--schemas", schema, "--baseline", snapshot], output, error, dataSource,
                TestContext.Current.CancellationToken));
            await using (var change = dataSource.CreateCommand($"ALTER TABLE \"{schema}\".items ADD COLUMN label text"))
            {
                _ = await change.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            Assert.Equal(2, await SchemaCli.RunAsync(["check", "--schemas", schema, "--baseline", snapshot, "--output", report], output, error,
                dataSource, TestContext.Current.CancellationToken));
            using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(report));
            Assert.True(json.RootElement.GetProperty("hasDrift").GetBoolean());
            Assert.False(json.RootElement.GetProperty("hasIncompatibleChanges").GetBoolean());
            Assert.Empty(error.ToString());
        }
        finally
        {
            await using var cleanup = dataSource.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            _ = await cleanup.ExecuteNonQueryAsync();
        }
    }

    private static SchemaSnapshot Snapshot(string type) => new([new(new("app", "items"), "r", false, false, "d", [new("id", 1, type, false)])]);

    private sealed class ScratchFiles : IDisposable
    {
        public string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bluetusk-schema-tool-" + Guid.NewGuid().ToString("N"));
        public ScratchFiles() => System.IO.Directory.CreateDirectory(Directory);
        public string Path(string name) => System.IO.Path.Combine(Directory, name);
        public void Dispose()
        {
            var resolved = System.IO.Path.GetFullPath(Directory);
            var allowed = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
            if (!resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || !System.IO.Path.GetFileName(resolved).StartsWith("bluetusk-schema-tool-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing cleanup outside the test-owned temporary directory.");
            }
            System.IO.Directory.Delete(resolved, recursive: true);
        }
    }
}
