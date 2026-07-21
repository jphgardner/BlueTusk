using System.Text;
using BlueTusk.Client;
using BlueTusk.Data;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

public sealed class BlueTuskSessionIntegrationTests
{
    [Fact]
    public async Task Opens_with_scram_and_executes_a_simple_query()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("$XunitDynamicSkip$BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        var settings = new BlueTuskConnectionStringBuilder(connectionString);
        await using var session = await BlueTuskSession.OpenAsync(
            new BlueTuskClientOptions
            {
                Host = settings.Host,
                Port = settings.Port,
                Database = settings.Database,
                Username = settings.Username,
                Password = settings.Password,
                SslMode = BlueTuskSslMode.Disable,
                ChannelBinding = BlueTuskChannelBindingMode.Disable,
            },
            CancellationToken.None);

        var result = await session.ExecuteSimpleQueryAsync(
            "SELECT 42::int4 AS answer, 'hello'::text AS greeting, NULL::text AS missing",
            CancellationToken.None);

        var resultSet = Assert.Single(result.ResultSets);
        var row = Assert.Single(resultSet.Rows);
        Assert.Equal(["answer", "greeting", "missing"], resultSet.Fields.Select(field => field.Name));
        Assert.Equal("42", Encoding.UTF8.GetString(row.Values[0]!.Value.Span));
        Assert.Equal("hello", Encoding.UTF8.GetString(row.Values[1]!.Value.Span));
        Assert.Null(row.Values[2]);
        Assert.Equal("SELECT 1", resultSet.CommandTag);
        Assert.Contains("server_version", session.Parameters);
        Assert.NotNull(session.BackendKeyData);
    }
}

