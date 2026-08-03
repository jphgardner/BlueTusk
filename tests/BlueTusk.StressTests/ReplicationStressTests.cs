using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.Replication;
using BlueTusk.TypeSystem;
using Xunit.Sdk;

namespace BlueTusk.StressTests;

public sealed class ReplicationStressTests
{
    [Fact]
    public async Task Repeated_stream_cancellation_drains_the_dedicated_session_for_reuse()
    {
        await using var dataSource = CreateDataSource();
        var workers = 4 * StressScale;
        const int iterations = 3;

        await Task.WhenAll(
            Enumerable.Range(0, workers).Select(
                _ => Task.Run(
                    async () =>
                    {
                        for (var iteration = 0; iteration < iterations; iteration++)
                        {
                            await using var replication =
                                await BlueTuskPhysicalReplicationConnection.OpenAsync(
                                    dataSource.CreateDedicatedSessionOptions());
                            var identity = await replication.IdentifySystemAsync();
                            using var cancellation = new CancellationTokenSource(
                                TimeSpan.FromMilliseconds(75));

                            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                                async () =>
                                {
                                    await foreach (var _ in replication.StartReplicationAsync(
                                        identity.WalPosition,
                                        cancellationToken: cancellation.Token))
                                    {
                                    }
                                });

                            Assert.False(replication.IsStreaming);
                            Assert.True(replication.IsOpen);
                            Assert.Equal(
                                identity.SystemIdentifier,
                                (await replication.IdentifySystemAsync()).SystemIdentifier);
                        }
                    })));

        Assert.Equal(0, dataSource.GetPoolStatistics().Total);
    }

    [Fact]
    public async Task Disposing_connections_unblocks_pending_replication_reads()
    {
        await using var dataSource = CreateDataSource();
        var workers = 4 * StressScale;

        await Task.WhenAll(
            Enumerable.Range(0, workers).Select(
                _ => Task.Run(
                    async () =>
                    {
                        var replication = await BlueTuskPhysicalReplicationConnection.OpenAsync(
                            dataSource.CreateDedicatedSessionOptions());
                        var identity = await replication.IdentifySystemAsync();
                        var read = ConsumeAsync(replication, identity.WalPosition);

                        await Task.Delay(TimeSpan.FromMilliseconds(50));
                        await replication.DisposeAsync();

                        var exception = await Record.ExceptionAsync(
                            () => read.WaitAsync(TimeSpan.FromSeconds(5)));
                        if (exception is not null)
                        {
                            Assert.True(
                                exception is OperationCanceledException or
                                    ObjectDisposedException or
                                    IOException,
                                $"Unexpected disposal exception: {exception}");
                        }

                        Assert.False(replication.IsOpen);
                    })));

        Assert.Equal(0, dataSource.GetPoolStatistics().Total);
    }

    private static async Task ConsumeAsync(
        BlueTuskPhysicalReplicationConnection replication,
        BlueTuskLogSequenceNumber startPosition)
    {
        await foreach (var _ in replication.StartReplicationAsync(startPosition))
        {
        }
    }

    private static int StressScale
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("BLUETUSK_STRESS_SCALE");
            return int.TryParse(raw, out var scale) && scale > 0 ? scale : 1;
        }
    }

    private static BlueTuskDataSource CreateDataSource()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip(
                "BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        var settings = new BlueTuskConnectionStringBuilder(connectionString)
        {
            Pooling = true,
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        };
        return BlueTuskDataSource.Create(settings.ConnectionString);
    }
}
