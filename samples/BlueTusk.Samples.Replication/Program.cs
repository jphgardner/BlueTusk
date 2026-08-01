using BlueTusk.Data;
using BlueTusk.Replication;
using BlueTusk.Replication.PgOutput;

var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine(
        "Set BLUETUSK_CONNECTION_STRING and pass the slot and publication names.");
    return 1;
}

if (args.Length != 2)
{
    Console.Error.WriteLine(
        "Usage: BlueTusk.Samples.Replication <slot-name> <publication-name>");
    return 1;
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

await using var dataSource = new BlueTuskDataSourceBuilder(connectionString).Build();
await using var replication = await BlueTuskLogicalReplicationConnection.OpenAsync(
    dataSource.CreateDedicatedSessionOptions(),
    shutdown.Token);
var identity = await replication.IdentifySystemAsync(shutdown.Token);
Console.WriteLine(
    $"System {identity.SystemIdentifier}, timeline {identity.Timeline}, WAL {identity.WalPosition}");

try
{
    var stream = replication.StartReplicationAsync(
        slotName: args[0],
        publicationName: args[1],
        cancellationToken: shutdown.Token);
    await foreach (var envelope in stream.DecodePgOutputAsync(
        cancellationToken: shutdown.Token))
    {
        Console.WriteLine(
            $"{envelope.XLogData.WalStart}: {envelope.Message.Code}");

        // pgoutput transaction-end LSNs, not CopyData payload lengths, are safe
        // logical checkpoints. A real consumer must first persist all work for
        // the transaction and the checkpoint atomically.
        if (envelope.TryGetTransactionEndPosition(out var applied))
        {
            await replication.SendStandbyStatusUpdateAsync(
                new BlueTuskStandbyStatus(applied, applied, applied),
                shutdown.Token);
        }
    }
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    Console.WriteLine("Replication stopped.");
}

return 0;
