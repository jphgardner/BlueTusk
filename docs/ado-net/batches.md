# Batches

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
    Console.WriteLine($"{reader.GetGuid(0)} — {reader.GetString(1)}");
}

Console.WriteLine(insert.RecordsAffected);
```

`ExecuteReaderAsync` exposes one result in command order, including empty non-query results. `ExecuteNonQueryAsync` returns the sum of affected rows, while every `BlueTuskBatchCommand.RecordsAffected` reports its own command tag. `ExecuteScalarAsync` returns the first field of the first row.

Set `Transaction` to enlist the complete protocol cycle in the connection's active transaction. `Timeout`, cancellation tokens, `Cancel()`, and `CancelAsync()` use PostgreSQL's cancellation channel and drain through `ReadyForQuery` before the connection can be reused.

`PrepareAsync` creates one named server statement per batch command. Later executions bind all of those statements in one cycle, and changing command text or PostgreSQL parameter OIDs rebuilds the prepared set. Batches created by a data source own a temporary pooled connection for each execution and therefore cannot be explicitly prepared.
