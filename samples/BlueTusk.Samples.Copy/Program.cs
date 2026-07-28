using BlueTusk.Data;

var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("Set BLUETUSK_CONNECTION_STRING to run this sample.");
    return 2;
}

await using var connection = new BlueTuskConnection(connectionString);
await connection.OpenAsync();
await using (var create = new BlueTuskCommand(
                 "CREATE TEMP TABLE bluetusk_copy_sample (id int4, name text)",
                 connection))
{
    _ = await create.ExecuteNonQueryAsync();
}

await using (var source = new MemoryStream("1,Alice\n2,\"Bob, Jr.\"\n"u8.ToArray()))
{
    var imported = await connection.CopyFromAsync(
        "COPY bluetusk_copy_sample FROM STDIN WITH (FORMAT CSV)",
        source);
    Console.Error.WriteLine($"Imported {imported.RowsAffected} rows.");
}

await using var destination = Console.OpenStandardOutput();
var exported = await connection.CopyToAsync(
    """
    COPY (
        SELECT id, name
        FROM bluetusk_copy_sample
        ORDER BY id
    ) TO STDOUT WITH (FORMAT CSV, HEADER true)
    """,
    destination);
await destination.FlushAsync();
Console.Error.WriteLine($"Exported {exported.RowsAffected} rows.");

return 0;
