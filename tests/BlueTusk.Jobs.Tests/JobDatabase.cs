using BlueTusk.Data;
using Xunit.Sdk;

namespace BlueTusk.Jobs.Tests;

internal sealed class JobDatabase : IAsyncDisposable
{
    private JobDatabase(BlueTuskDataSource dataSource, JobStoreOptions options)
    {
        DataSource = dataSource;
        Options = options;
        Store = new PostgreSqlJobStore(dataSource, options);
    }

    internal BlueTuskDataSource DataSource { get; }
    internal JobStoreOptions Options { get; }
    internal PostgreSqlJobStore Store { get; }
    internal JobScope Scope { get; } = new("tenant-a", "default");

    internal static async Task<JobDatabase> CreateAsync(JobStoreOptions? options = null)
    {
        string? connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured; live PostgreSQL Jobs tests require a disposable database.");
        }

        var source = BlueTuskDataSource.Create(connectionString);
        var resolved = (options ?? new JobStoreOptions()) with { Schema = "jobs_test_" + Guid.NewGuid().ToString("N") };
        var database = new JobDatabase(source, resolved);
        try
        {
            await database.Store.InitializeAsync();
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    internal JobRequest Request(string? key = null, int attempts = 5, JobScope? scope = null) =>
        JobRequest.FromJson(scope ?? Scope, "test.v1", new TestJob(42), JobJsonContext.Default.TestJob) with
        {
            DeduplicationKey = key,
            MaximumAttempts = attempts,
        };

    internal async Task ExecuteAsync(string sql)
    {
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = new BlueTuskCommand(sql.Replace("{schema}", "\"" + Options.Schema + "\"", StringComparison.Ordinal), connection);
        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await ExecuteAsync("DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
        finally
        {
            await DataSource.DisposeAsync();
        }
    }
}
