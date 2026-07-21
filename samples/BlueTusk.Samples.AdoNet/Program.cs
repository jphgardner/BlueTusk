using BlueTusk.Data;

var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("Set BLUETUSK_CONNECTION_STRING to run this sample.");
    return 2;
}

await using var connection = new BlueTuskConnection(connectionString);
await connection.OpenAsync();
await using var command = new BlueTuskCommand("SELECT 42::int4 AS answer, 'hello'::text AS greeting", connection);
await using var reader = await command.ExecuteReaderAsync();
while (await reader.ReadAsync())
{
    Console.WriteLine($"{reader.GetInt32(0)} — {reader.GetString(1)}");
}

return 0;
