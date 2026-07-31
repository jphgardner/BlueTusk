# Protocol notes

PostgreSQL backend frames begin with a one-byte identifier followed by a big-endian 32-bit length. The length includes itself and excludes the identifier. Startup messages are exceptional: they have no identifier byte.

`BlueTuskBackendMessageParser` deliberately accepts unknown identifiers and returns a zero-copy payload view. It rejects lengths below four and lengths above its configured maximum before waiting for or allocating the payload.

## Incremental messages and portals

`BlueTuskProtocolConnection.ReadMessageHeader` and `ReadMessageHeaderAsync` separate frame validation from payload consumption. Callers can then drain the active payload in bounded chunks with `ReadMessagePayload` or `ReadMessagePayloadAsync`; another frame cannot be read until that payload is complete. This is the primitive used for large `DataRow` fields, so sequential consumers do not need to allocate a complete backend message.

`BlueTuskSession.BeginPortal` and `BeginPreparedPortal` expose bounded named portals. Their asynchronous counterparts provide the same behavior. A portal sends `Execute` with the requested fetch size followed by `Flush`, resumes after `PortalSuspended`, and sends `Sync` only when the command completes or the portal is disposed. `BlueTuskPortalRow` enforces forward-only field access and can return a transport-backed field stream.
