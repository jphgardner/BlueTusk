# Batches

This page helps you send several SQL statements to PostgreSQL in one round
trip, either as a `BlueTuskBatch` or as one multi-statement command.

`BlueTuskBatch` implements the .NET `DbBatch` abstraction and sends every command through one PostgreSQL extended-query protocol cycle. Each command has its own text and parameter collection; positional and named placeholders use the same safe rewriting and encoding rules as `BlueTuskCommand`.

```csharp
await using var connection = await dataSource.OpenConnectionAsync();
await using var batch = connection.CreateBatch();

var insert = batch.BatchCommands.Add(
    "INSERT INTO app.people (id, name) VALUES (@id, @name)");
insert.Parameters.Add(new BlueTuskParameter<Guid>(id) { ParameterName = "id" });
insert.Parameters.Add(new BlueTuskParameter<string>(name) { ParameterName = "name" });

var select = batch.BatchCommands.Add(
    "SELECT id, name FROM app.people WHERE id = @id");
select.Parameters.Add(new BlueTuskParameter<Guid>(id) { ParameterName = "id" });

await using var reader = await batch.ExecuteReaderAsync();
await reader.NextResultAsync();
while (await reader.ReadAsync())
{
    Console.WriteLine($"{reader.GetGuid(0)}: {reader.GetString(1)}");
}

Console.WriteLine(insert.RecordsAffected);
```

`ExecuteReaderAsync` exposes one result in command order, including empty non-query results. `ExecuteNonQueryAsync` returns the sum of affected rows, while every `BlueTuskBatchCommand.RecordsAffected` reports its own command tag. `ExecuteScalarAsync` returns the first field of the first row.

Set `Transaction` to enlist the complete protocol cycle in the connection's active transaction. `Timeout`, cancellation tokens, `Cancel()`, and `CancelAsync()` use PostgreSQL's cancellation channel and drain through `ReadyForQuery` before the connection can be reused.

`PrepareAsync` creates one named server statement per batch command. Later executions bind all of those statements in one cycle, and changing command text or PostgreSQL parameter OIDs rebuilds the prepared set. Batches created by a data source own a temporary pooled connection for each execution and therefore cannot be explicitly prepared.

## Several statements in one command

> **Note:** New in 1.1.0. Not available in 1.0.0 or 1.1.0-rc.1.

A buffered `BlueTuskCommand` also accepts ordinary parameterized,
semicolon-separated statements. EF Core uses this path for its write batches:

```csharp
await using var command = new BlueTuskCommand(
    "SELECT @id::int4; SELECT @id::int4 + 1;", connection);
command.Parameters.Add(new BlueTuskParameter<int>(41) { ParameterName = "id" });

await using var reader = await command.ExecuteReaderAsync(cancellationToken);
await reader.ReadAsync(cancellationToken);
Console.WriteLine(reader.GetInt32(0)); // 41
await reader.NextResultAsync(cancellationToken);
await reader.ReadAsync(cancellationToken);
Console.WriteLine(reader.GetInt32(0)); // 42
```

Named parameters are rebound separately for each statement. Positional `$1`,
`$2`, etc. retain their ordinals in the parent command's parameter collection.
Do not mix named and positional placeholders. Quoted strings, quoted
identifiers, dollar-quoted bodies and SQL comments can contain semicolons
without creating a new statement. Supply data through parameters, not string
interpolation.

The driver uses separate Parse/Bind/Execute messages and one final Sync, with
results in statement order. Text-format results avoid replaying a write batch
to retry an unsupported binary output type. Asynchronous buffered execution
reuses row storage, released when the reader is disposed; scalar and non-query
executions release their buffers after consuming their result. Dispose readers
promptly, including when reading only the first result.

Without an explicit transaction, a PostgreSQL statement error rolls back the
implicit transaction containing the batch. Inside an explicit transaction,
handle the failure and roll back to a savepoint or roll back the transaction
before continuing. Cancellation drains the response when synchronization can
be recovered; transport failures can instead require discarding the session.
Do not blindly retry writes with an unknown commit outcome.

Use `BlueTuskBatch` for explicit multi-statement preparation. Do not use
`CommandBehavior.SequentialAccess` with a multi-statement `BlueTuskCommand`;
use separate commands for streaming readers. Multiplexed data-source commands
with several statements use buffered dispatch rather than the single-statement
pipeline.
