using System.Collections.Concurrent;
using System.Diagnostics;
using BlueTusk.Data;
using BlueTusk.Diagnostics;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

public sealed class BlueTuskDiagnosticsIntegrationTests
{
    [Fact]
    public async Task Command_activity_uses_the_selected_endpoint_and_redacts_parameter_values()
    {
        var queryTag = $"diagnostics-{Guid.NewGuid():N}";
        const string parameterSecret = "diagnostic-parameter-must-not-escape";
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == BlueTuskDiagnostics.InstrumentationName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        await using var dataSource = new BlueTuskDataSourceBuilder(GetConnectionString()).Build();
        await using var command = dataSource.CreateCommand(
            $"-- {queryTag}\nSELECT @value::text");
        command.Parameters.Add(new BlueTuskParameter<string>(parameterSecret)
        {
            ParameterName = "value",
        });

        Assert.Equal(parameterSecret, await command.ExecuteScalarAsync<string>());

        var activity = Assert.Single(
            stopped,
            candidate => candidate.GetTagItem("bluetusk.query.tags") is string[] tags &&
                tags.Contains(queryTag, StringComparer.Ordinal));
        var settings = new BlueTuskConnectionStringBuilder(GetConnectionString());
        Assert.Equal("SELECT", activity.GetTagItem("db.operation.name"));
        Assert.Equal(settings.Database, activity.GetTagItem("db.namespace"));
        Assert.Equal("postgresql", activity.GetTagItem("db.system.name"));
        Assert.NotNull(activity.GetTagItem("server.address"));
        Assert.NotNull(activity.GetTagItem("server.port"));
        Assert.Null(activity.GetTagItem("db.query.text"));
        Assert.DoesNotContain(
            activity.TagObjects,
            tag => tag.Value?.ToString()?.Contains(parameterSecret, StringComparison.Ordinal) == true);
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        return string.IsNullOrWhiteSpace(connectionString)
            ? throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.")
            : connectionString;
    }
}
