using System.Text;
using BlueTusk.Client;
using BlueTusk.Data;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

public sealed class BlueTuskLargeObjectIntegrationTests
{
    [Fact]
    public async Task Implicit_transaction_stream_round_trips_seeks_truncates_and_commits()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        var objectId = await connection.CreateLargeObjectAsync(CancellationToken.None);
        var initial = Encoding.UTF8.GetBytes("BlueTusk large object \U0001F9A3");
        var suffix = Encoding.UTF8.GetBytes(" provider");

        try
        {
            await using (var stream = await connection.OpenLargeObjectAsync(
                             objectId,
                             FileAccess.ReadWrite,
                             CancellationToken.None))
            {
                Assert.Equal(objectId, stream.ObjectId);
                Assert.True(stream.CanRead);
                Assert.True(stream.CanWrite);
                Assert.Equal(0, stream.Length);

                await stream.WriteAsync(initial, CancellationToken.None);
                _ = await stream.SeekAsync(8, SeekOrigin.Begin, CancellationToken.None);
                await stream.WriteAsync(suffix, CancellationToken.None);
                await stream.SetLengthAsync(17, CancellationToken.None);
                Assert.Equal(17, stream.Length);
            }

            await using (var stream = await connection.OpenLargeObjectAsync(
                             objectId,
                             FileAccess.Read,
                             CancellationToken.None))
            {
                Assert.False(stream.CanWrite);
                Assert.Equal(17, stream.Length);
                await using var destination = new MemoryStream();
                await stream.CopyToAsync(destination, CancellationToken.None);
                Assert.Equal(
                    "BlueTusk provider",
                    Encoding.UTF8.GetString(destination.ToArray()));
            }

            await connection.DeleteLargeObjectAsync(objectId, CancellationToken.None);
            await Assert.ThrowsAsync<BlueTuskException>(
                () => connection.OpenLargeObjectAsync(
                    objectId,
                    FileAccess.Read,
                    CancellationToken.None).AsTask());
            Assert.Equal(
                42,
                await ExecuteScalarAsync<int>(connection, "SELECT 42::int4"));
        }
        finally
        {
            await TryDeleteAsync(connection, objectId);
        }
    }

    [Fact]
    public async Task Explicit_transaction_supports_multiple_streams_and_caller_rollback()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        uint firstObjectId;
        uint secondObjectId;

        await using (var transaction = await connection.BeginTransactionAsync(
                         CancellationToken.None))
        {
            firstObjectId = await connection.CreateLargeObjectAsync(CancellationToken.None);
            secondObjectId = await connection.CreateLargeObjectAsync(CancellationToken.None);
            await using var first = await connection.OpenLargeObjectAsync(
                firstObjectId,
                FileAccess.Write,
                CancellationToken.None);
            await using var second = await connection.OpenLargeObjectAsync(
                secondObjectId,
                FileAccess.Write,
                CancellationToken.None);

            await first.WriteAsync("first"u8.ToArray(), CancellationToken.None);
            await second.WriteAsync("second"u8.ToArray(), CancellationToken.None);
            await transaction.RollbackAsync(CancellationToken.None);
        }

        await Assert.ThrowsAsync<BlueTuskException>(
            () => connection.OpenLargeObjectAsync(
                firstObjectId,
                FileAccess.Read,
                CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<BlueTuskException>(
            () => connection.OpenLargeObjectAsync(
                secondObjectId,
                FileAccess.Read,
                CancellationToken.None).AsTask());
        Assert.Equal(
            42,
            await ExecuteScalarAsync<int>(connection, "SELECT 42::int4"));
    }

    [Fact]
    public async Task Implicit_stream_exclusivity_prevents_transaction_lifetime_ambiguity()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        var firstObjectId = await connection.CreateLargeObjectAsync(CancellationToken.None);
        var secondObjectId = await connection.CreateLargeObjectAsync(CancellationToken.None);

        try
        {
            await using var first = await connection.OpenLargeObjectAsync(
                firstObjectId,
                FileAccess.ReadWrite,
                CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => connection.OpenLargeObjectAsync(
                    secondObjectId,
                    FileAccess.ReadWrite,
                    CancellationToken.None).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => connection.DeleteLargeObjectAsync(
                    secondObjectId,
                    CancellationToken.None).AsTask());
        }
        finally
        {
            await TryDeleteAsync(connection, firstObjectId);
            await TryDeleteAsync(connection, secondObjectId);
        }
    }

    private static async Task<T> ExecuteScalarAsync<T>(
        BlueTuskConnection connection,
        string sql)
    {
        await using var command = new BlueTuskCommand(sql, connection);
        var result = await command.ExecuteScalarAsync<T>(CancellationToken.None);
        return result is null
            ? throw new InvalidOperationException("The large-object test query returned null.")
            : result;
    }

    private static async Task TryDeleteAsync(
        BlueTuskConnection connection,
        uint objectId)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            return;
        }

        try
        {
            await connection.DeleteLargeObjectAsync(objectId, CancellationToken.None);
        }
        catch (BlueTuskException)
        {
            // The object may already have been deleted or rolled back by the test.
        }
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        var settings = new BlueTuskConnectionStringBuilder(connectionString)
        {
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        };
        return settings.ConnectionString;
    }
}
