# Sync quick start: copy a table to another database

In this quick start you keep a PostgreSQL table in one database identical to a
table in another database. You copy the existing rows, then watch inserts,
updates and deletes arrive within a second. It takes about 10 minutes.

## Before you start

You need:

- the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0);
- a PostgreSQL 15, 16, 17 or 18 test server started with `wal_level=logical`.
  Step 1 of the [first-app quick start](../getting-started/quickstart.md#1-start-postgresql)
  starts one in Docker as `bluetusk-postgres`.

## 1. Create the source database

The source has an `orders` table, a publication that tells PostgreSQL which
tables to stream, and a role allowed to replicate and read them.

```powershell
docker exec bluetusk-postgres psql -U postgres -c "CREATE DATABASE sync_source"
@'
CREATE TABLE public.orders (
    id bigint PRIMARY KEY,
    customer text NOT NULL,
    status text NOT NULL
);
INSERT INTO public.orders VALUES (1, 'Ada', 'new'), (2, 'Grace', 'paid');

CREATE PUBLICATION orders_pub FOR TABLE public.orders;

CREATE ROLE sync_replicator LOGIN REPLICATION PASSWORD 'local-dev-only';
GRANT SELECT ON public.orders TO sync_replicator;
'@ | docker exec -i bluetusk-postgres psql -U postgres -d sync_source
```

In bash, pass the SQL with a `<<'SQL'` heredoc instead of `@'...'@ |`.

Do not create the replication slot yourself. Sync creates it so that the
initial copy and the change stream start at exactly the same point.

## 2. Create the target database

The target gets the same table and a role that can write to it. Sync also
creates a small `bluetusk_sync` schema here for its checkpoint, so the role
needs `CREATE` on the database.

```powershell
docker exec bluetusk-postgres psql -U postgres -c "CREATE DATABASE sync_target"
@'
CREATE TABLE public.orders (
    id bigint PRIMARY KEY,
    customer text NOT NULL,
    status text NOT NULL
);

CREATE ROLE sync_writer LOGIN PASSWORD 'local-dev-only';
GRANT CREATE ON DATABASE sync_target TO sync_writer;
GRANT SELECT, INSERT, UPDATE, DELETE ON public.orders TO sync_writer;
'@ | docker exec -i bluetusk-postgres psql -U postgres -d sync_target
```

## 3. Create the project

```powershell
dotnet new console --framework net10.0 --name OrdersSync
cd OrdersSync
dotnet add package BlueTusk.Sync.PostgreSql
dotnet add package BlueTusk.Sync.DependencyInjection
dotnet add package Microsoft.Extensions.Hosting
```

See [Install BlueTusk](../getting-started/install.md) to choose and pin a
version.

## 4. Set the connection strings

```powershell
$env:SYNC_SOURCE = "Host=localhost;Port=5432;Database=sync_source;Username=sync_replicator;Password=local-dev-only;SSL Mode=Disable;Channel Binding=Disable"
$env:SYNC_TARGET = "Host=localhost;Port=5432;Database=sync_target;Username=sync_writer;Password=local-dev-only;SSL Mode=Disable;Channel Binding=Disable"
```

In bash, use `export SYNC_SOURCE="..."`.

> **Warning:** `SSL Mode=Disable` is only for a local test container. Keep the
> default `SSL Mode=VerifyFull` everywhere else.

## 5. Write the code

A pipeline has a Streams **source**, a **transform** that turns rows into
keyed documents, and a **destination** that writes them
([concepts](concepts.md)). Replace `Program.cs`:

```csharp
using BlueTusk.Data;
using BlueTusk.Replication;
using BlueTusk.Streams;
using BlueTusk.Sync;
using BlueTusk.Sync.DependencyInjection;
using BlueTusk.Sync.PostgreSql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var source = new BlueTuskDataSourceBuilder(
    Environment.GetEnvironmentVariable("SYNC_SOURCE")!).Build();
var target = new BlueTuskDataSourceBuilder(
    Environment.GetEnvironmentVariable("SYNC_TARGET")!).Build();

// Identify the source database. Sync stores this identity with its checkpoint.
ChangeSourceIdentity sourceIdentity;
await using (var replication = await BlueTuskLogicalReplicationConnection.OpenAsync(
                 source.CreateDedicatedSessionOptions()))
{
    var server = await replication.IdentifySystemAsync();
    sourceIdentity = new ChangeSourceIdentity(
        server.SystemIdentifier,
        server.DatabaseName!,
        slotName: "orders_sync",
        publicationFingerprint: "orders_pub:public.orders");
}

// Describe the table to copy: column order and PostgreSQL type OIDs.
var orders = new ChangeTable(
    relationId: 0,
    "public",
    "orders",
    replicaIdentity: 'd',
    [
        new ChangeColumn(0, "id", 20, -1, IsKey: true),        // bigint
        new ChangeColumn(1, "customer", 25, -1, IsKey: false), // text
        new ChangeColumn(2, "status", 25, -1, IsKey: false),   // text
    ]);
var snapshotOptions = new PostgreSqlConsistentSnapshotOptions
{
    Source = sourceIdentity,
    PublicationNames = ["orders_pub"],
    Tables = [new PostgreSqlSnapshotTable(orders, [0])],
    ExistingSlotMode = PostgreSqlExistingSnapshotSlotMode.RestartSnapshot,
};

builder.Services.AddSingleton(new PostgreSqlSyncDestination(new PostgreSqlSyncOptions
{
    DestinationDataSource = target,
    MutationWriter = new OrdersTableWriter(),
}));
builder.Services.AddBlueTuskSync()
    .AddHostedPipeline<OrdersTransform, PostgreSqlSyncDestination>(
        new SyncPipelineOptions { PipelineId = "orders-replica" },
        sourceIdentity,
        _ => new PostgreSqlConsistentSnapshotSource(source, snapshotOptions));

await builder.Build().RunAsync();
```

Add `OrdersTransform.cs`. It maps every row to a JSON document keyed by `id`:

```csharp
using System.Globalization;
using System.Text;
using System.Text.Json;
using BlueTusk.Streams;
using BlueTusk.Sync;

// Maps each source row to one keyed JSON document.
public sealed class OrdersTransform : ISyncTransform
{
    // Stored in the target. Changing it later requires a rebuild.
    public SyncTransformVersion Version { get; } = SyncTransformVersion.Create("orders", "v1");

    public async ValueTask<IReadOnlyList<SyncMutation>> TransformTransactionAsync(
        ChangeTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        var mutations = new List<SyncMutation>();
        await foreach (var change in transaction.Changes.WithCancellation(cancellationToken))
        {
            switch (change)
            {
                case InsertChange insert:
                    mutations.Add(Upsert(change.Id, insert.NewRow));
                    break;
                case UpdateChange update:
                    mutations.Add(Upsert(change.Id, update.NewRow));
                    break;
                case DeleteChange delete:
                    mutations.Add(new SyncMutation(
                        change.Id, SyncMutationKind.Delete, "orders", Key(delete.OldRow), default));
                    break;
                case TruncateChange:
                    mutations.Add(new SyncMutation(
                        change.Id, SyncMutationKind.DeleteCollection, "orders", null, default));
                    break;
            }
        }

        return mutations;
    }

    public ValueTask<IReadOnlyList<SyncSnapshotMutation>> TransformSnapshotBatchAsync(
        ChangeSnapshotBatch batch,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SyncSnapshotMutation> mutations = batch.Rows
            .Select(row => new SyncSnapshotMutation(
                row.Id, "orders", Key(row.Row), ToJson(row.Row), "application/json"))
            .ToArray();
        return ValueTask.FromResult(mutations);
    }

    private static SyncMutation Upsert(ChangeId id, ChangeRow row) =>
        new(id, SyncMutationKind.Upsert, "orders", Key(row), ToJson(row), "application/json");

    private static string Key(ChangeRow row) =>
        Id(row).ToString(CultureInfo.InvariantCulture);

    private static long Id(ChangeRow row)
    {
        var column = row.Table.Columns.First(c => c.Name == "id");
        return ChangeValueDecoders.Decode<long>(column, row[column.Ordinal]);
    }

    // PostgreSQL text values are UTF-8 in both the snapshot and the change stream.
    private static string Text(ChangeRow row, string name) =>
        Encoding.UTF8.GetString(row[name].Data.Span);

    private static byte[] ToJson(ChangeRow row) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = Id(row),
            customer = Text(row, "customer"),
            status = Text(row, "status"),
        });
}
```

Add `OrdersTableWriter.cs`. It writes the documents into `public.orders`.
Sync runs it inside the same database transaction that saves the checkpoint:

```csharp
using System.Data.Common;
using System.Text;
using BlueTusk.Streams;
using BlueTusk.Sync;
using BlueTusk.Sync.PostgreSql;

// Writes mutations into public.orders. Sync owns the transaction and commits
// these writes together with its checkpoint.
public sealed class OrdersTableWriter : IPostgreSqlSyncMutationWriter
{
    private const string UpsertSql = """
        INSERT INTO public.orders (id, customer, status)
        SELECT id, customer, status
        FROM jsonb_populate_record(NULL::public.orders, @value::jsonb)
        ON CONFLICT (id) DO UPDATE
        SET customer = EXCLUDED.customer, status = EXCLUDED.status
        """;

    public async ValueTask ResetSnapshotAsync(
        DbConnection connection, DbTransaction transaction, string pipelineId,
        SnapshotReset reset, CancellationToken cancellationToken = default)
    {
        Console.WriteLine("Snapshot started: clearing public.orders");
        await ExecuteAsync(connection, transaction, "DELETE FROM public.orders", null, cancellationToken);
    }

    public async ValueTask ApplySnapshotBatchAsync(
        DbConnection connection, DbTransaction transaction,
        SyncSnapshotBatch batch, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"Copying {batch.Mutations.Count} snapshot row(s)");
        foreach (var mutation in batch.Mutations)
        {
            await ExecuteAsync(connection, transaction, UpsertSql, Json(mutation.Content), cancellationToken);
        }
    }

    public async ValueTask ApplyTransactionAsync(
        DbConnection connection, DbTransaction transaction,
        SyncTransactionBatch batch, CancellationToken cancellationToken = default)
    {
        Console.WriteLine(
            $"Applying transaction {batch.Transaction.TransactionId}: {batch.Mutations.Count} change(s)");
        foreach (var mutation in batch.Mutations)
        {
            var (sql, value) = mutation.Kind switch
            {
                SyncMutationKind.Upsert => (UpsertSql, Json(mutation.Content)),
                SyncMutationKind.Delete => ("DELETE FROM public.orders WHERE id = @value::bigint", mutation.Key),
                _ => ("DELETE FROM public.orders", null),
            };
            await ExecuteAsync(connection, transaction, sql, value, cancellationToken);
        }
    }

    private static string Json(ReadOnlyMemory<byte> content) => Encoding.UTF8.GetString(content.Span);

    private static async ValueTask ExecuteAsync(
        DbConnection connection, DbTransaction transaction, string sql,
        string? value, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (value is not null)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "value";
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
```

## 6. Run it

```powershell
dotnet run
```

After the host start-up lines you see the initial copy:

```text
Snapshot started: clearing public.orders
Copying 2 snapshot row(s)
```

Leave it running.

## 7. Change the source and watch the target

In a second terminal, change the source:

```powershell
docker exec bluetusk-postgres psql -U postgres -d sync_source -c "INSERT INTO public.orders VALUES (3, 'Linus', 'new');" -c "UPDATE public.orders SET status = 'shipped' WHERE id = 1;" -c "DELETE FROM public.orders WHERE id = 2;"
```

Each statement is its own transaction, so the worker prints three lines (your
transaction numbers will differ):

```text
Applying transaction 980: 1 change(s)
Applying transaction 981: 1 change(s)
Applying transaction 982: 1 change(s)
```

Check the target:

```powershell
docker exec bluetusk-postgres psql -U postgres -d sync_target -c "SELECT * FROM public.orders ORDER BY id"
```

```text
 id | customer | status
----+----------+---------
  1 | Ada      | shipped
  3 | Linus    | new
```

A multi-statement transaction arrives as one batch and commits in the target
all at once.

## 8. Restart the worker

Press Ctrl+C. While the worker is stopped, change the source:

```powershell
docker exec bluetusk-postgres psql -U postgres -d sync_source -c "UPDATE public.orders SET status = 'cancelled' WHERE id = 3;"
```

Run `dotnet run` again. The worker replaces its own inactive slot and copies
the table again, so the target picks up the change made while it was down:

```text
Snapshot started: clearing public.orders
Copying 2 snapshot row(s)
```

This direct setup re-copies the table on every start. To resume from the saved
checkpoint without re-copying, read through the
[durable relay](../streams/durable-relay.md) instead; see
[where checkpoints live](concepts.md#where-is-the-checkpoint-stored).

## 9. Clean up

Stop the worker, then drop the replication slot so PostgreSQL stops keeping
WAL for it, and remove the test databases and roles:

```powershell
docker exec bluetusk-postgres psql -U postgres -c "SELECT pg_drop_replication_slot('orders_sync')"
docker exec bluetusk-postgres psql -U postgres -c "DROP DATABASE sync_source" -c "DROP DATABASE sync_target" -c "DROP ROLE sync_replicator" -c "DROP ROLE sync_writer"
```

## Next steps

- [Sync concepts](concepts.md): transactions, idempotency, ordering, rebuilds
  and failure handling.
- [Configuration](configuration.md): every option, and how to set up the
  other destinations.
- [Troubleshooting](troubleshooting.md): what to do when a pipeline stops.
- [Streams quick start](../streams/quickstart.md): the change feed Sync reads.
