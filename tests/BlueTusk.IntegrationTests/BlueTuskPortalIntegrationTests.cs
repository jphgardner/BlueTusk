using System.Buffers.Binary;
using BlueTusk.Client;
using BlueTusk.Data;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

public sealed class BlueTuskPortalIntegrationTests
{
    [Fact]
    public void Named_portal_suspends_fetches_and_streams_fields_synchronously()
    {
        using var session = BlueTuskSession.Open(CreateOptions());
        using (var portal = session.BeginPortal(
                   "SELECT value, repeat(value::text, 10000)::text " +
                   "FROM generate_series(1, 7) AS value ORDER BY value",
                   [],
                   fetchSize: 2))
        {
            Assert.StartsWith("bluetusk_portal_", portal.Name, StringComparison.Ordinal);
            Assert.Equal(2, portal.Fields.Count);
            var values = new List<int>();
            var prefix = new byte[16];
            BlueTuskPortalRow? row;
            while ((row = portal.Read()) is not null)
            {
                var value = row.ReadField(0);
                values.Add(BinaryPrimitives.ReadInt32BigEndian(value!.Value.Span));
                using var stream = row.OpenFieldStream(1);
                Assert.Equal(prefix.Length, stream.Read(prefix));
                Assert.All(prefix, digit => Assert.Equal((byte)('0' + values[^1]), digit));
            }

            Assert.Equal([1, 2, 3, 4, 5, 6, 7], values);
            Assert.Equal(7, portal.RowsRead);
            Assert.Equal("SELECT 1", portal.CommandTag);
            Assert.True(portal.IsCompleted);
        }

        Assert.Equal("1", ReadText(session.ExecuteSimpleQuery("SELECT 1")));
    }

    [Fact]
    public async Task Named_portal_streams_fields_asynchronously_and_recovers_after_early_disposal()
    {
        await using var session = await BlueTuskSession.OpenAsync(CreateOptions(), CancellationToken.None);
        await using (var portal = await session.BeginPortalAsync(
                         "SELECT value, repeat('x', 250000)::text " +
                         "FROM generate_series(1, 20) AS value ORDER BY value",
                         [],
                         fetchSize: 3,
                         cancellationToken: CancellationToken.None))
        {
            var row = await portal.ReadAsync(CancellationToken.None);
            Assert.NotNull(row);
            Assert.Equal(
                1,
                BinaryPrimitives.ReadInt32BigEndian(
                    (await row!.ReadFieldAsync(0, CancellationToken.None))!.Value.Span));
            await using var stream = row.OpenFieldStream(1);
            var buffer = new byte[8192];
            var read = await stream.ReadAsync(buffer, CancellationToken.None);
            Assert.InRange(read, 1, buffer.Length);
            Assert.All(buffer.AsSpan(0, read).ToArray(), value => Assert.Equal((byte)'x', value));
        }

        var result = await session.ExecuteSimpleQueryAsync("SELECT 42", CancellationToken.None);
        Assert.Equal("42", ReadText(result));
    }

    [Fact]
    public async Task Repeated_unlimited_portals_survive_unnamed_statement_invalidation()
    {
        await using var session = await BlueTuskSession.OpenAsync(
            CreateOptions(),
            CancellationToken.None);
        const string repeatedSql =
            "SELECT value FROM generate_series(1, 3) AS value ORDER BY value";

        var values = await ReadValuesAsync(repeatedSql);
        Assert.Equal([1, 2, 3], values);
        values = await ReadValuesAsync(repeatedSql);
        Assert.Equal([1, 2, 3], values);

        values = await ReadValuesAsync("SELECT 9");
        Assert.Equal([9], values);
        values = await ReadValuesAsync(repeatedSql);
        Assert.Equal([1, 2, 3], values);

        var simpleResult = await session.ExecuteSimpleQueryAsync(
            "SELECT 10",
            CancellationToken.None);
        Assert.Equal("10", ReadText(simpleResult));
        values = await ReadValuesAsync(repeatedSql);
        Assert.Equal([1, 2, 3], values);

        _ = await session.ExecuteExtendedQueryAsync(
            "SELECT 11",
            [],
            CancellationToken.None);
        values = await ReadValuesAsync(repeatedSql);
        Assert.Equal([1, 2, 3], values);

        _ = await session.ExecuteBatchAsync(
            [new BlueTuskBatchQuery("SELECT 12", [], UseBinaryResults: true)],
            CancellationToken.None);
        values = await ReadValuesAsync(repeatedSql);
        Assert.Equal([1, 2, 3], values);

        _ = await session.ExecutePipelineAsync(
            [
                new BlueTuskPipelineGroup(
                    [new BlueTuskBatchQuery("SELECT 13", [], UseBinaryResults: true)]),
            ],
            CancellationToken.None);
        values = await ReadValuesAsync(repeatedSql);
        Assert.Equal([1, 2, 3], values);

        async Task<int[]> ReadValuesAsync(string sql)
        {
            var values = new List<int>();
            await using var portal = await session.BeginPortalAsync(
                sql,
                [],
                cancellationToken: CancellationToken.None);
            BlueTuskPortalRow? row;
            while ((row = await portal.ReadAsync(CancellationToken.None)) is not null)
            {
                values.Add(BinaryPrimitives.ReadInt32BigEndian(
                    (await row.ReadFieldAsync(0, CancellationToken.None))!.Value.Span));
            }

            return [.. values];
        }
    }

    private static string ReadText(BlueTuskQueryResult result) =>
        System.Text.Encoding.UTF8.GetString(
            Assert.Single(Assert.Single(result.ResultSets).Rows).Values[0]!.Value.Span);

    private static BlueTuskClientOptions CreateOptions()
    {
        var settings = new BlueTuskConnectionStringBuilder(GetConnectionString());
        return new BlueTuskClientOptions
        {
            Host = settings.Host,
            Port = settings.Port,
            Database = settings.Database,
            Username = settings.Username,
            Password = settings.Password,
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        };
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        return connectionString;
    }
}
