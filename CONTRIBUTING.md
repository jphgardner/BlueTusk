# Contributing to BlueTusk

BlueTusk treats PostgreSQL documentation, protocol specifications, catalogues, and observed server behaviour as the source of truth. Compatibility with another provider is useful evidence, not a specification.

## Development workflow

1. Create or reference an issue that states the PostgreSQL behaviour being implemented.
2. Keep dependencies flowing in the direction documented in `docs/architecture/overview.md`.
3. Add unit or protocol-conformance tests. Network fragmentation cases should include every meaningful frame boundary.
4. Run `dotnet format --verify-no-changes`, `dotnet build`, and `dotnet test`.
5. Never include passwords, tokens, authentication payloads, or unredacted connection strings in tests, logs, or exceptions.

Public API proposals should explain lifetime/ownership, synchronous and asynchronous behaviour, cancellation, and how unknown future PostgreSQL values degrade. The shipped ADO.NET, replication, and extension-authoring assemblies have `PublicAPI.Shipped.txt` contracts. Additive preview APIs belong in `PublicAPI.Unshipped.txt`; removing or changing a shipped signature requires an explicit compatibility/versioning decision and documentation update. See [API compatibility](docs/api-compatibility.md).
