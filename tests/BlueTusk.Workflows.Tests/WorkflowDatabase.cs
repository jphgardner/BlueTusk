using System.Diagnostics;
using BlueTusk.Data;
using BlueTusk.Jobs;
using Xunit.Sdk;

namespace BlueTusk.Workflows.Tests;

internal sealed class WorkflowDatabase : IAsyncDisposable
{
    private WorkflowDatabase(BlueTuskDataSource source, WorkflowOptions options, JobStoreOptions jobOptions)
    {
        Source = source;
        Options = options;
        JobOptions = jobOptions;
        Store = new PostgreSqlWorkflowStore(source, options, jobOptions);
    }

    internal BlueTuskDataSource Source { get; }
    internal WorkflowOptions Options { get; }
    internal JobStoreOptions JobOptions { get; }
    internal PostgreSqlWorkflowStore Store { get; }
    internal JobScope Scope { get; } = new("tenant-a", "workflow");
    internal static WorkflowWorkerOptions WorkerOptions { get; } = new()
    {
        Jobs = new JobWorkerOptions
        {
            Concurrency = 4,
            PollInterval = TimeSpan.FromMilliseconds(10),
            LeaseDuration = TimeSpan.FromSeconds(10),
            HeartbeatInterval = TimeSpan.FromMilliseconds(100),
            DispatchRecurringSchedules = false,
            RetryPolicy = new JobRetryPolicy { InitialDelay = TimeSpan.FromMilliseconds(10), MaximumDelay = TimeSpan.FromMilliseconds(10), JitterFraction = 0 },
        },
        RecoveryInterval = TimeSpan.FromMilliseconds(20),
    };

    internal static async Task<WorkflowDatabase> CreateAsync(WorkflowOptions? options = null)
    {
        string? connection = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connection))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is required for disposable PostgreSQL workflow tests.");
        }

        string suffix = Guid.NewGuid().ToString("N");
        var database = new WorkflowDatabase(BlueTuskDataSource.Create(connection),
            (options ?? new WorkflowOptions()) with { Schema = "wf_test_" + suffix },
            new JobStoreOptions { Schema = "wf_jobs_" + suffix });
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

    internal async Task<WorkflowKey> StartAsync(IReadOnlyList<WorkflowNode> nodes, string? dedup = null)
    {
        await Store.RegisterDefinitionAsync(Scope, new WorkflowDefinition { Name = "test", Version = 1, Nodes = nodes });
        return await Store.StartAsync(Request(dedup));
    }

    internal WorkflowStartRequest Request(string? dedup = null) => new()
    {
        Scope = Scope,
        Definition = "test",
        Version = 1,
        Input = new byte[] { 42 },
        DeduplicationKey = dedup,
    };

    internal async Task ExecuteAsync(string sql)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var command = new BlueTuskCommand(sql.Replace("{schema}", "\"" + Options.Schema + "\"", StringComparison.Ordinal)
            .Replace("{jobs}", "\"" + JobOptions.Schema + "\"", StringComparison.Ordinal), connection);
        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    internal static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!await condition())
        {
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(15), "The durable workflow did not reach its expected state.");
            await Task.Delay(20);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await ExecuteAsync("DROP SCHEMA IF EXISTS {schema} CASCADE");
            await ExecuteAsync("DROP SCHEMA IF EXISTS {jobs} CASCADE");
        }
        finally
        {
            await Source.DisposeAsync();
        }
    }
}
