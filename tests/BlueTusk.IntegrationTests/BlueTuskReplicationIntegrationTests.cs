using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.Replication;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

public sealed class BlueTuskReplicationIntegrationTests
{
    [Fact]
    public async Task Physical_connection_discovers_slots_streams_wal_and_sends_feedback()
    {
        var connectionString = GetConnectionString();
        var slotName = $"bluetusk_physical_{Guid.NewGuid():N}";
        await using var replication =
            await BlueTuskPhysicalReplicationConnection.OpenAsync(connectionString);

        var identity = await replication.IdentifySystemAsync();
        Assert.False(string.IsNullOrWhiteSpace(identity.SystemIdentifier));
        Assert.True(identity.Timeline > 0);
        Assert.False(string.IsNullOrWhiteSpace(await replication.ShowAsync("server_version")));

        var slot = await replication.CreateReplicationSlotAsync(
            slotName,
            temporary: true,
            reserveWal: true);
        Assert.Equal(slotName, slot.SlotName);
        Assert.Null(slot.OutputPlugin);

        var initialSlotState = await replication.ReadReplicationSlotAsync(slotName);
        var startPosition = initialSlotState.RestartPosition ?? identity.WalPosition;
        await using var enumerator = replication.StartReplicationAsync(
            startPosition,
            slotName).GetAsyncEnumerator();
        await ForceWalSwitchAsync(connectionString);

        var message = await ReadXLogDataAsync(enumerator);
        var status = new BlueTuskStandbyStatus(
            message.WalEnd,
            message.WalEnd,
            message.WalEnd);
        await replication.SendStandbyStatusUpdateAsync(status);
        await replication.SendHotStandbyFeedbackAsync(default);

        Assert.Equal(message.WalEnd, replication.LastReceivedWalPosition);
        Assert.Equal(status, replication.StandbyStatus);

        await enumerator.DisposeAsync();
        var discovered = await replication.ReadReplicationSlotAsync(slotName);
        Assert.Equal("physical", discovered.SlotType);
        Assert.NotNull(discovered.RestartPosition);
        Assert.NotNull(discovered.RestartTimeline);
    }

    [Fact]
    public async Task Logical_connection_uses_the_convenience_pgoutput_stream()
    {
        var connectionString = GetConnectionString();
        var suffix = Guid.NewGuid().ToString("N");
        var tableName = $"bluetusk_replication_{suffix}";
        var publicationName = $"bluetusk_publication_{suffix}";
        var slotName = $"bluetusk_logical_{suffix}";
        var quotedTable = BlueTuskSql.QuoteIdentifier(tableName);
        var quotedPublication = BlueTuskSql.QuoteIdentifier(publicationName);

        await using var administration = new BlueTuskConnection(connectionString);
        await administration.OpenAsync(CancellationToken.None);
        await ExecuteAsync(
            administration,
            $"CREATE TABLE {quotedTable} (id int PRIMARY KEY, value text NOT NULL)");
        try
        {
            await ExecuteAsync(
                administration,
                $"CREATE PUBLICATION {quotedPublication} FOR TABLE {quotedTable}");
            try
            {
                await using var replication =
                    await BlueTuskLogicalReplicationConnection.OpenAsync(connectionString);
                var slot = await replication.CreateReplicationSlotAsync(
                    slotName,
                    temporary: true);
                Assert.Equal("pgoutput", slot.OutputPlugin);

                await using var enumerator = replication.StartReplicationAsync(
                    slotName,
                    publicationName).GetAsyncEnumerator();
                var firstMessage = enumerator.MoveNextAsync().AsTask();
                await ExecuteAsync(
                    administration,
                    $"INSERT INTO {quotedTable} VALUES (1, 'hello')");

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var completed = await Task.WhenAny(
                    firstMessage,
                    Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token));
                Assert.Same(firstMessage, completed);
                Assert.True(await firstMessage);
                var xLogData = enumerator.Current as BlueTuskXLogData ??
                    await ReadXLogDataAsync(enumerator);
                Assert.NotEmpty(xLogData.Data.ToArray());
                Assert.Contains(
                    xLogData.Data.Span[0],
                    new[]
                    {
                        (byte)'B',
                        (byte)'R',
                        (byte)'I',
                        (byte)'C',
                    });

                await replication.SendStandbyStatusUpdateAsync(
                    new BlueTuskStandbyStatus(
                        xLogData.WalEnd,
                        xLogData.WalEnd,
                        xLogData.WalEnd));
            }
            finally
            {
                await ExecuteAsync(
                    administration,
                    $"DROP PUBLICATION IF EXISTS {quotedPublication}");
            }
        }
        finally
        {
            await ExecuteAsync(administration, $"DROP TABLE IF EXISTS {quotedTable}");
        }
    }

    private static async Task<BlueTuskXLogData> ReadXLogDataAsync(
        IAsyncEnumerator<BlueTuskReplicationMessage> enumerator)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await enumerator.MoveNextAsync().AsTask().WaitAsync(timeout.Token))
        {
            if (enumerator.Current is BlueTuskXLogData xLogData)
            {
                return xLogData;
            }
        }

        throw new XunitException("The physical replication stream completed before sending WAL.");
    }

    private static async Task ForceWalSwitchAsync(string connectionString)
    {
        await using var connection = new BlueTuskConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await ExecuteAsync(connection, "SELECT pg_switch_wal()");
    }

    private static async Task ExecuteAsync(BlueTuskConnection connection, string sql)
    {
        await using var command = new BlueTuskCommand(sql, connection);
        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip(
                "$XunitDynamicSkip$BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        var settings = new BlueTuskConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        };
        return settings.ConnectionString;
    }
}
