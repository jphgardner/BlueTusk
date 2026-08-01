using BlueTusk.Client;
using BlueTusk.Data;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

public sealed class BlueTuskSqlPgqIntegrationTests
{
    [Fact]
    public async Task PostgreSQL_19_property_graphs_cover_DDL_metadata_preparation_and_batches()
    {
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
        var capabilities = Assert.IsType<BlueTuskServerCapabilities>(
            connection.ServerCapabilities);
        if (capabilities.ServerVersion.Major < 19)
        {
            Assert.False(capabilities.SupportsSqlPgq);
            return;
        }

        Assert.True(capabilities.SupportsSqlPgq);
        await ExecuteAsync(
            connection,
            "CREATE TEMP TABLE bluetusk_graph_people " +
            "(id int4 PRIMARY KEY, name text NOT NULL)");
        await ExecuteAsync(
            connection,
            "CREATE TEMP TABLE bluetusk_graph_knows (" +
            "id int4 PRIMARY KEY, " +
            "source_id int4 NOT NULL REFERENCES bluetusk_graph_people(id), " +
            "destination_id int4 NOT NULL REFERENCES bluetusk_graph_people(id))");
        await ExecuteAsync(
            connection,
            "INSERT INTO bluetusk_graph_people VALUES (1, 'Ada'), (2, 'Grace')");
        await ExecuteAsync(
            connection,
            "INSERT INTO bluetusk_graph_knows VALUES (1, 1, 2)");

        try
        {
            await ExecuteAsync(
                connection,
                """
                CREATE TEMP PROPERTY GRAPH bluetusk_graph
                    VERTEX TABLES (
                        bluetusk_graph_people LABEL person)
                    EDGE TABLES (
                        bluetusk_graph_knows
                            SOURCE KEY (source_id)
                                REFERENCES bluetusk_graph_people (id)
                            DESTINATION KEY (destination_id)
                                REFERENCES bluetusk_graph_people (id)
                            LABEL knows)
                """);

            await using (var query = new BlueTuskCommand(
                             """
                             SELECT source_name, destination_name
                             FROM GRAPH_TABLE (
                                 bluetusk_graph
                                 MATCH (source IS person)-[IS knows]->(destination IS person)
                                 COLUMNS (
                                     source.name AS source_name,
                                     destination.name AS destination_name))
                             WHERE source_name = $1::text
                             """,
                             connection))
            {
                query.Parameters.Add(new BlueTuskParameter<string>("Ada"));
                await query.PrepareAsync(CancellationToken.None);
                await using var reader = await query.ExecuteReaderAsync(CancellationToken.None);
                Assert.True(await reader.ReadAsync(CancellationToken.None));
                Assert.Equal("source_name", reader.GetName(0));
                Assert.Equal("text", reader.GetDataTypeName(0));
                Assert.Equal("Ada", reader.GetString(0));
                Assert.Equal("Grace", reader.GetString(1));
                Assert.False(await reader.ReadAsync(CancellationToken.None));
            }

            await using (var batch = connection.CreateBatch())
            {
                batch.BatchCommands.Add(
                    """
                    SELECT count(*)::int4
                    FROM GRAPH_TABLE (
                        bluetusk_graph
                        MATCH (person IS person)
                        COLUMNS (person.id AS id))
                    """);
                batch.BatchCommands.Add(
                    "SELECT count(*)::int4 FROM bluetusk_graph_people");
                await using var reader = await batch.ExecuteReaderAsync(CancellationToken.None);
                Assert.True(await reader.ReadAsync(CancellationToken.None));
                Assert.Equal(2, reader.GetInt32(0));
                Assert.True(await reader.NextResultAsync(CancellationToken.None));
                Assert.True(await reader.ReadAsync(CancellationToken.None));
                Assert.Equal(2, reader.GetInt32(0));
            }

            await ExecuteAsync(
                connection,
                "ALTER PROPERTY GRAPH bluetusk_graph RENAME TO bluetusk_graph_renamed");
            await using var metadata = new BlueTuskCommand(
                """
                SELECT property_graph_name
                FROM information_schema.property_graphs
                WHERE property_graph_name = 'bluetusk_graph_renamed'
                """,
                connection);
            Assert.Equal(
                "bluetusk_graph_renamed",
                await metadata.ExecuteScalarAsync<string>(CancellationToken.None));

            await ExecuteAsync(
                connection,
                "DROP PROPERTY GRAPH bluetusk_graph_renamed");
        }
        finally
        {
            await ExecuteAsync(
                connection,
                "DROP PROPERTY GRAPH IF EXISTS bluetusk_graph");
            await ExecuteAsync(
                connection,
                "DROP PROPERTY GRAPH IF EXISTS bluetusk_graph_renamed");
        }

        Assert.Equal(1, dataSource.GetPoolStatistics().Busy);
    }

    private static async Task ExecuteAsync(
        BlueTuskConnection connection,
        string sql)
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
                "BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        var settings = new BlueTuskConnectionStringBuilder(connectionString)
        {
            Pooling = true,
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        };
        return settings.ConnectionString;
    }
}
