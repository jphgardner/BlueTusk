using BlueTusk.Data;

var connectionString = Environment.GetEnvironmentVariable(
    "BLUETUSK_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine(
        "Set BLUETUSK_CONNECTION_STRING to a PostgreSQL 19 database.");
    return 1;
}

await using var dataSource = new BlueTuskDataSourceBuilder(connectionString).Build();
await using var connection = await dataSource.OpenConnectionAsync();
if (connection.ServerCapabilities is not { SupportsSqlPgq: true } capabilities)
{
    Console.Error.WriteLine(
        $"SQL/PGQ is unavailable on PostgreSQL {connection.ServerVersion}.");
    return 2;
}

Console.WriteLine($"PostgreSQL {capabilities.ServerVersion}: SQL/PGQ detected.");
await ExecuteAsync(
    "CREATE TEMP TABLE graph_people (id int4 PRIMARY KEY, name text NOT NULL)");
await ExecuteAsync(
    "CREATE TEMP TABLE graph_knows (" +
    "id int4 PRIMARY KEY, " +
    "source_id int4 REFERENCES graph_people(id), " +
    "destination_id int4 REFERENCES graph_people(id))");
await ExecuteAsync("INSERT INTO graph_people VALUES (1, 'Ada'), (2, 'Grace')");
await ExecuteAsync("INSERT INTO graph_knows VALUES (1, 1, 2)");

try
{
    await ExecuteAsync(
        """
        CREATE TEMP PROPERTY GRAPH people_graph
            VERTEX TABLES (graph_people LABEL person)
            EDGE TABLES (
                graph_knows
                    SOURCE KEY (source_id) REFERENCES graph_people (id)
                    DESTINATION KEY (destination_id) REFERENCES graph_people (id)
                    LABEL knows)
        """);

    await using var command = new BlueTuskCommand(
        """
        SELECT source_name, destination_name
        FROM GRAPH_TABLE (
            people_graph
            MATCH (source IS person)-[IS knows]->(destination IS person)
            COLUMNS (
                source.name AS source_name,
                destination.name AS destination_name))
        """,
        connection);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        Console.WriteLine($"{reader.GetString(0)} knows {reader.GetString(1)}");
    }
}
finally
{
    await ExecuteAsync("DROP PROPERTY GRAPH IF EXISTS people_graph");
}

return 0;

async Task ExecuteAsync(string sql)
{
    await using var command = new BlueTuskCommand(sql, connection);
    _ = await command.ExecuteNonQueryAsync();
}
