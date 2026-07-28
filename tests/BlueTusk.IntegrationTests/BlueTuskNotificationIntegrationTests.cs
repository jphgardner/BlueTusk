using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.Data.Notifications;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

public sealed class BlueTuskNotificationIntegrationTests
{
    [Fact]
    public async Task Connection_delivers_notifications_while_remaining_available_for_commands()
    {
        var channel = $"Order Events \"West\" \U0001F9A3 {Guid.NewGuid():N}";
        const string payload = "created \U0001F9A3";
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);

        await connection.ListenAsync(channel, CancellationToken.None);
        await connection.ListenAsync(channel, CancellationToken.None);
        var pending = ReadOneAsync(connection.Notifications, TimeSpan.FromSeconds(5));

        var processId = await ExecuteScalarAsync<int>(
            connection,
            "SELECT pg_backend_pid()",
            []);
        Assert.Equal(
            42,
            await ExecuteScalarAsync<int>(connection, "SELECT 42::int4", []));
        await ExecuteNonQueryAsync(
            connection,
            "SELECT pg_notify($1, $2)",
            [new BlueTuskParameter<string>(channel), new BlueTuskParameter<string>(payload)]);

        var notification = await pending;
        Assert.Equal(processId, notification.ProcessId);
        Assert.Equal(channel, notification.Channel);
        Assert.Equal(payload, notification.Payload);

        await AssertNoNotificationAsync(connection.Notifications, TimeSpan.FromMilliseconds(150));
    }

    [Fact]
    public async Task Unlisten_and_close_end_their_notification_lifetimes()
    {
        var firstChannel = $"bluetusk_first_{Guid.NewGuid():N}";
        var secondChannel = $"bluetusk_second_{Guid.NewGuid():N}";
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await connection.ListenAsync(firstChannel, CancellationToken.None);
        await connection.ListenAsync(secondChannel, CancellationToken.None);

        var firstPending = ReadOneAsync(connection.Notifications, TimeSpan.FromSeconds(5));
        await NotifyAsync(connection, firstChannel, "first");
        Assert.Equal("first", (await firstPending).Payload);

        await connection.UnlistenAsync(firstChannel, CancellationToken.None);
        await NotifyAsync(connection, firstChannel, "ignored");
        await AssertNoNotificationAsync(connection.Notifications, TimeSpan.FromMilliseconds(150));

        var secondPending = ReadOneAsync(connection.Notifications, TimeSpan.FromSeconds(5));
        await NotifyAsync(connection, secondChannel, "second");
        Assert.Equal("second", (await secondPending).Payload);

        await using var oldEnumerator = connection.Notifications.GetAsyncEnumerator();
        var completion = oldEnumerator.MoveNextAsync().AsTask();
        await connection.CloseAsync();
        Assert.False(await completion.WaitAsync(TimeSpan.FromSeconds(5)));

        await connection.OpenAsync(CancellationToken.None);
        await connection.ListenAsync(firstChannel, CancellationToken.None);
        var reopenedPending = ReadOneAsync(connection.Notifications, TimeSpan.FromSeconds(5));
        await NotifyAsync(connection, firstChannel, "reopened");
        Assert.Equal("reopened", (await reopenedPending).Payload);
    }

    [Fact]
    public async Task Listen_requires_an_open_connection()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => connection.ListenAsync("orders", CancellationToken.None).AsTask());
    }

    private static async Task NotifyAsync(
        BlueTuskConnection connection,
        string channel,
        string payload)
    {
        await ExecuteNonQueryAsync(
            connection,
            "SELECT pg_notify($1, $2)",
            [new BlueTuskParameter<string>(channel), new BlueTuskParameter<string>(payload)]);
    }

    private static async Task ExecuteNonQueryAsync(
        BlueTuskConnection connection,
        string sql,
        IReadOnlyList<BlueTuskParameter> parameters)
    {
        await using var command = new BlueTuskCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<T> ExecuteScalarAsync<T>(
        BlueTuskConnection connection,
        string sql,
        IReadOnlyList<BlueTuskParameter> parameters)
    {
        await using var command = new BlueTuskCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        var result = await command.ExecuteScalarAsync<T>(CancellationToken.None);
        return result is null
            ? throw new InvalidOperationException("The notification test query returned null.")
            : result;
    }

    private static async Task<BlueTuskNotification> ReadOneAsync(
        IAsyncEnumerable<BlueTuskNotification> notifications,
        TimeSpan timeout)
    {
        using var cancellationSource = new CancellationTokenSource(timeout);
        await using var enumerator = notifications.GetAsyncEnumerator(cancellationSource.Token);
        if (await enumerator.MoveNextAsync())
        {
            return enumerator.Current;
        }

        throw new InvalidOperationException("The notification stream completed before a notification arrived.");
    }

    private static async Task AssertNoNotificationAsync(
        IAsyncEnumerable<BlueTuskNotification> notifications,
        TimeSpan timeout)
    {
        using var cancellationSource = new CancellationTokenSource(timeout);
        await using var enumerator = notifications.GetAsyncEnumerator(cancellationSource.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => enumerator.MoveNextAsync().AsTask());
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("$XunitDynamicSkip$BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        var settings = new BlueTuskConnectionStringBuilder(connectionString)
        {
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        };
        return settings.ConnectionString;
    }
}
