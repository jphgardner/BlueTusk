using BlueTusk.Client;
using BlueTusk.Data;
using Xunit.Sdk;

namespace BlueTusk.Extensions.PgDurable.Tests;

public sealed class BlueTuskPgDurableTests
{
    [Fact]
    public void Build_carries_feature_only_registration_in_an_immutable_snapshot()
    {
        var builder = new BlueTuskDataSourceBuilder(
            "Host=localhost;Username=test;Password=test");
        Assert.Same(builder, builder.UsePgDurable());

        using var dataSource = builder.Build();
        builder.Features.Register("test.late-feature", 42);

        _ = dataSource.Features.GetRequired<BlueTuskPgDurableFeature>(
            BlueTuskPgDurableFeature.RegistryName);
        Assert.False(dataSource.Features.Contains("test.late-feature"));
        Assert.DoesNotContain(
            dataSource.TypeRegistry.Types,
            type => type.Schema == BlueTuskPgDurableFeature.Schema);
    }

    [Fact]
    public async Task Operations_require_explicit_pg_durable_registration()
    {
        await using var dataSource = new BlueTuskDataSourceBuilder(
                "Host=localhost;Username=test;Password=test")
            .Build();

        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
            await dataSource.GetPgDurableVersionAsync());
    }

    [Fact]
    public async Task Helpers_validate_inputs_before_connecting()
    {
        await using var dataSource = new BlueTuskDataSourceBuilder(
                "Host=localhost;Port=1;Username=test;Password=test")
            .UsePgDurable()
            .Build();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await dataSource.StartPgDurableAsync(" "));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await dataSource.StartPgDurableAsync("SELECT 1", label: " "));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await dataSource.StartPgDurableAsync(
                "SELECT 1",
                transactionMode: (BlueTuskPgDurableTransactionMode)42));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await dataSource.GetPgDurableStatusAsync(" "));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await dataSource.AwaitPgDurableAsync("deadbeef", timeoutSeconds: 0));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await dataSource.CancelPgDurableAsync("deadbeef", " "));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await dataSource.SignalPgDurableAsync("deadbeef", " "));
    }

    [Fact]
    public async Task Pg_durable_plugin_executes_a_parameterized_workflow_lifecycle_live()
    {
        var connectionString = GetConnectionString();
        await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
            .UsePgDurable()
            .Build();
        var version = await dataSource.GetPgDurableVersionAsync();
        Assert.Equal("0.2.5", version);

        const string adversarialLabel = "BlueTusk'); DROP TABLE df.instances; --";
        var instanceId = await dataSource.StartPgDurableAsync(
            "SELECT 42 AS answer",
            adversarialLabel);
        Assert.Matches("^[0-9a-f]{8}$", instanceId);

        var status = await dataSource.AwaitPgDurableAsync(instanceId, timeoutSeconds: 30);
        Assert.Equal(BlueTuskPgDurableStatus.Completed, status);
        Assert.Equal(
            BlueTuskPgDurableStatus.Completed,
            await dataSource.GetPgDurableStatusAsync(instanceId));

        var result = await dataSource.GetPgDurableResultAsync(instanceId);
        Assert.Contains("42", result, StringComparison.Ordinal);

        var metrics = await dataSource.GetPgDurableMetricsAsync();
        Assert.True(metrics.TotalInstances > 0);
        Assert.True(metrics.CompletedInstances > 0);

        await using var labelCommand = dataSource.CreateCommand(
            "SELECT label FROM df.instances WHERE id = $1");
        labelCommand.Parameters.Add(new BlueTuskParameter<string>(instanceId));
        Assert.Equal(
            adversarialLabel,
            await labelCommand.ExecuteScalarAsync<string>(CancellationToken.None));

        await using var signalWorkflow = dataSource.CreateCommand(
            "SELECT df.start(" +
            "df.seq(df.wait_for_signal($1::text, $2::int4), $3::text), " +
            "$4::text)");
        signalWorkflow.Parameters.Add(new BlueTuskParameter<string>("approved"));
        signalWorkflow.Parameters.Add(new BlueTuskParameter<int>(30));
        signalWorkflow.Parameters.Add(new BlueTuskParameter<string>("SELECT 7 AS signaled"));
        signalWorkflow.Parameters.Add(new BlueTuskParameter<string>("bluetusk-signal"));
        var signalInstanceId = await signalWorkflow.ExecuteScalarAsync<string>(CancellationToken.None);
        Assert.NotNull(signalInstanceId);

        await WaitForSignalNodeAsync(dataSource, signalInstanceId);
        var signalAcknowledgement = await dataSource.SignalPgDurableAsync(
            signalInstanceId,
            "approved",
            "{\"approved\":true}");
        Assert.NotEmpty(signalAcknowledgement);
        Assert.Equal(
            BlueTuskPgDurableStatus.Completed,
            await dataSource.AwaitPgDurableAsync(signalInstanceId, timeoutSeconds: 30));

        await using var cancelWorkflow = dataSource.CreateCommand(
            "SELECT df.start(df.seq(df.sleep($1::int8), $2::text), $3::text)");
        cancelWorkflow.Parameters.Add(new BlueTuskParameter<long>(30));
        cancelWorkflow.Parameters.Add(new BlueTuskParameter<string>("SELECT 1"));
        cancelWorkflow.Parameters.Add(new BlueTuskParameter<string>("bluetusk-cancel"));
        var cancelInstanceId = await cancelWorkflow.ExecuteScalarAsync<string>(CancellationToken.None);
        Assert.NotNull(cancelInstanceId);

        var cancellationAcknowledgement = await dataSource.CancelPgDurableAsync(
            cancelInstanceId,
            "BlueTusk acceptance cleanup");
        Assert.NotEmpty(cancellationAcknowledgement);
        Assert.Equal(
            BlueTuskPgDurableStatus.Cancelled,
            await dataSource.AwaitPgDurableAsync(cancelInstanceId, timeoutSeconds: 30));
    }

    private static async Task WaitForSignalNodeAsync(
        BlueTuskDataSource dataSource,
        string instanceId)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT EXISTS (" +
            "SELECT 1 FROM df.nodes " +
            "WHERE instance_id = $1 AND node_type = 'SIGNAL' AND status = 'running')");
        command.Parameters.Add(new BlueTuskParameter<string>(instanceId));
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await command.ExecuteScalarAsync<bool>(CancellationToken.None))
            {
                // pg_durable marks the node running immediately before the runtime
                // installs its signal subscription. Let that handoff complete.
                await Task.Delay(TimeSpan.FromMilliseconds(500));
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        Assert.Fail("pg_durable did not enter the expected signal wait within ten seconds.");
    }

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
}
