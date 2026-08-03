using BlueTusk.Data;

var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("Set BLUETUSK_CONNECTION_STRING to run this sample.");
    return 2;
}

await using var dataSource = new BlueTuskDataSourceBuilder(connectionString).Build();
await using var connection = await dataSource.OpenConnectionAsync();
await using var command = new BlueTuskCommand("SELECT 42::int4 AS answer, 'hello'::text AS greeting", connection);
await using var reader = await command.ExecuteReaderAsync();
while (await reader.ReadAsync())
{
    Console.WriteLine($"{reader.GetInt32(0)} — {reader.GetString(1)}");
}

await using var batch = connection.CreateBatch();
var first = batch.BatchCommands.Add("SELECT @value::int4 AS value");
first.Parameters.Add(new BlueTuskParameter<int>(42) { ParameterName = "value" });
batch.BatchCommands.Add("SELECT 'one protocol cycle'::text AS description");

await using var batchReader = await batch.ExecuteReaderAsync();
if (await batchReader.ReadAsync())
{
    Console.WriteLine($"Batch value: {batchReader.GetInt32(0)}");
}

await batchReader.NextResultAsync();
if (await batchReader.ReadAsync())
{
    Console.WriteLine($"Batch description: {batchReader.GetString(0)}");
}

return 0;
