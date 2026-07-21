# Protocol notes

PostgreSQL backend frames begin with a one-byte identifier followed by a big-endian 32-bit length. The length includes itself and excludes the identifier. Startup messages are exceptional: they have no identifier byte.

`BlueTuskBackendMessageParser` deliberately accepts unknown identifiers and returns a zero-copy payload view. It rejects lengths below four and lengths above its configured maximum before waiting for or allocating the payload.

