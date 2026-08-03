using BlueTusk.Data;
using Microsoft.EntityFrameworkCore;

var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("Set BLUETUSK_CONNECTION_STRING to run this sample.");
    return 2;
}

await using var dataSource = new BlueTuskDataSourceBuilder(connectionString).Build();
var options = new DbContextOptionsBuilder<SampleContext>()
    .UseBlueTusk(dataSource)
    .Options;
await using var context = new SampleContext(options);

var answer = await context.Database
    .SqlQueryRaw<int>("SELECT 42::int4 AS \"Value\"")
    .SingleAsync();
Console.WriteLine($"EF Core answer: {answer}");
return 0;

internal sealed class SampleContext(DbContextOptions<SampleContext> options) : DbContext(options);
