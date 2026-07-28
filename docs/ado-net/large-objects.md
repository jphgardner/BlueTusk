# Large objects

PostgreSQL large objects are addressed by an unsigned object identifier and accessed through a transactional descriptor. BlueTusk exposes that descriptor as an asynchronous stream:

```csharp
var objectId = await connection.CreateLargeObjectAsync();

await using (var stream =
    await connection.OpenLargeObjectAsync(
        objectId,
        FileAccess.ReadWrite))
{
    await stream.WriteAsync(payload);
    await stream.SeekAsync(0, SeekOrigin.Begin);
    _ = await stream.ReadAsync(buffer);
}

await connection.DeleteLargeObjectAsync(objectId);
```

`CreateLargeObjectAsync(uint preferredObjectId, ...)` requests a particular OID; passing zero, or using the overload without an OID, lets PostgreSQL assign one. `DeleteLargeObjectAsync` calls `lo_unlink` and permanently removes the object when its transaction commits.

## Transaction ownership

PostgreSQL requires every large-object descriptor to remain inside a transaction:

- If the connection has no active transaction, `OpenLargeObjectAsync` starts an implicit transaction. Successful asynchronous disposal closes the descriptor and commits. A failed read, write, seek, or truncate rolls it back.
- Only one implicitly transactional stream can be open on a connection. Begin an explicit transaction when multiple descriptors must overlap.
- If the caller already owns a `BlueTuskTransaction`, large-object creation, deletion, and streams join it. The caller remains responsible for commit or rollback, and multiple streams may be open.
- Creating or deleting outside an explicit transaction uses a short implicit transaction.

Always use `await using` for an implicitly transactional stream. BlueTusk does not implement synchronous database I/O by blocking the asynchronous path. Synchronous stream disposal therefore closes the connection and rolls back the implicit transaction; synchronous `Read`, `Write`, `Seek`, and `SetLength` throw `NotSupportedException`.

## Stream behavior

`BlueTuskLargeObjectStream` exposes:

- asynchronous `ReadAsync` and `WriteAsync`;
- 64-bit `SeekAsync` and `SetLengthAsync`;
- cached `Length` and `Position`;
- the backing `ObjectId`;
- `FileAccess.Read`, `Write`, and `ReadWrite` enforcement.

Transfers are split into chunks of at most 1 MiB. Each write reaches PostgreSQL before its task completes, so `FlushAsync` has no additional server work. Opening with `FileAccess.Write` does not truncate an existing object; call `SetLengthAsync(0)` when replacement semantics are required.

Large objects live in the database independently of table rows. PostgreSQL does not automatically delete an object when an OID column is deleted, so applications should call `DeleteLargeObjectAsync` or arrange database-side ownership cleanup.
