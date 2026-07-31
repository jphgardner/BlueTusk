using System.Data;
using BlueTusk.Client;
using BlueTusk.Data;
using Xunit.Sdk;

namespace BlueTusk.StressTests;

public sealed class ConcurrentProviderStressTests
{
    [Fact]
    public async Task Concurrent_connection_churn_preserves_pool_bounds_and_results()
    {
        await using var dataSource = CreateDataSource(maximumPoolSize: 8);
        var workers = 16 * StressScale;
        const int iterations = 20;

        await Task.WhenAll(
            Enumerable.Range(0, workers).Select(
                worker => Task.Run(
                    async () =>
                    {
                        for (var iteration = 0; iteration < iterations; iteration++)
                        {
                            await using var connection = await dataSource.OpenConnectionAsync(
                                CancellationToken.None);
                            await using var command = new BlueTuskCommand(
                                "SELECT @value::int4 + 1",
                                connection);
                            var expected = worker * iterations + iteration + 1;
                            command.Parameters.Add(
                                new BlueTuskParameter<int>(expected - 1)
                                {
                                    ParameterName = "value",
                                });
                            Assert.Equal(
                                expected,
                                await command.ExecuteScalarAsync<int>(CancellationToken.None));
                        }
                    })));

        var statistics = dataSource.GetPoolStatistics();
        Assert.Equal(0, statistics.Busy);
        Assert.InRange(statistics.Total, 1, 8);
    }

    [Fact]
    public async Task Concurrent_cancellation_storm_recovers_every_physical_session()
    {
        await using var dataSource = CreateDataSource(maximumPoolSize: 6);
        var workers = 12 * StressScale;

        await Task.WhenAll(
            Enumerable.Range(0, workers).Select(
                _ => Task.Run(
                    async () =>
                    {
                        await using var connection = await dataSource.OpenConnectionAsync(
                            CancellationToken.None);
                        await using var command = new BlueTuskCommand("SELECT pg_sleep(5)", connection);
                        using var cancellation = new CancellationTokenSource(
                            TimeSpan.FromMilliseconds(Random.Shared.Next(40, 160)));
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(
                            () => command.ExecuteNonQueryAsync(cancellation.Token));

                        command.CommandText = "SELECT 1";
                        Assert.Equal(1, await command.ExecuteScalarAsync<int>(CancellationToken.None));
                    })));

        Assert.Equal(0, dataSource.GetPoolStatistics().Busy);
    }

    [Fact]
    public async Task Concurrent_preparation_batches_and_streaming_readers_remain_isolated()
    {
        await using var dataSource = CreateDataSource(maximumPoolSize: 8);
        var workers = 12 * StressScale;

        await Task.WhenAll(
            Enumerable.Range(1, workers).Select(
                worker => Task.Run(
                    async () =>
                    {
                        await using var connection = await dataSource.OpenConnectionAsync(
                            CancellationToken.None);

                        await using (var prepared = new BlueTuskCommand(
                                         "SELECT @value::int4 * 2",
                                         connection))
                        {
                            prepared.Parameters.Add(
                                new BlueTuskParameter<int>(worker)
                                {
                                    ParameterName = "value",
                                });
                            await prepared.PrepareAsync(CancellationToken.None);
                            Assert.Equal(
                                worker * 2,
                                await prepared.ExecuteScalarAsync<int>(CancellationToken.None));
                        }

                        await using (var batch = (BlueTuskBatch)connection.CreateBatch())
                        {
                            batch.BatchCommands.Add("SELECT 1");
                            batch.BatchCommands.Add("SELECT 2");
                            await using var reader = await batch.ExecuteReaderAsync(CancellationToken.None);
                            Assert.True(await reader.ReadAsync(CancellationToken.None));
                            Assert.Equal(1, reader.GetInt32(0));
                            Assert.True(await reader.NextResultAsync(CancellationToken.None));
                            Assert.True(await reader.ReadAsync(CancellationToken.None));
                            Assert.Equal(2, reader.GetInt32(0));
                        }

                        await using (var streamCommand = new BlueTuskCommand(
                                         "SELECT value, decode(repeat('ab', 20000), 'hex') " +
                                         "FROM generate_series(1, 25) AS value",
                                         connection))
                        await using (var reader = await streamCommand.ExecuteReaderAsync(
                                         CommandBehavior.SequentialAccess,
                                         CancellationToken.None))
                        {
                            Assert.True(await reader.ReadAsync(CancellationToken.None));
                            Assert.Equal(1, reader.GetInt32(0));
                            await using var stream = reader.GetStream(1);
                            var buffer = new byte[1024];
                            Assert.Equal(
                                buffer.Length,
                                await stream.ReadAsync(buffer, CancellationToken.None));
                        }
                    })));

        Assert.Equal(0, dataSource.GetPoolStatistics().Busy);
    }

    private static int StressScale
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("BLUETUSK_STRESS_SCALE");
            return int.TryParse(raw, out var scale) && scale > 0 ? scale : 1;
        }
    }

    private static BlueTuskDataSource CreateDataSource(int maximumPoolSize)
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        var settings = new BlueTuskConnectionStringBuilder(connectionString)
        {
            Pooling = true,
            MaximumPoolSize = maximumPoolSize,
            MaxAutoPrepare = 16,
            AutoPrepareMinUsages = 2,
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        };
        return BlueTuskDataSource.Create(settings.ConnectionString);
    }
}
