# Contributing to BlueTusk

BlueTusk treats PostgreSQL documentation, protocol specifications, catalogues, and observed server behaviour as the source of truth. Compatibility with another provider is useful evidence, not a specification.

## Development workflow

1. Create or reference an issue that states the PostgreSQL behaviour being implemented.
2. Keep dependencies flowing in the direction documented in `docs/architecture/overview.md`.
3. Keep every project in the product-oriented solution hierarchy documented in
   [repository and solution layout](docs/contributing/repository-layout.md).
4. Add unit or protocol-conformance tests. Network fragmentation cases should include every meaningful frame boundary.
5. Run the solution-layout, formatting, build, and test gates.
6. Never include passwords, tokens, authentication payloads, or unredacted connection strings in tests, logs, or exceptions.

EF provider changes must also preserve the official relational specification
gate in `tests/BlueTusk.EntityFrameworkCore.SpecificationTests`. New inherited
migration-generator cases require a BlueTusk override with an exact PostgreSQL
SQL baseline; do not satisfy EF Core's override check without asserting the
result. Live official fixtures require `BLUETUSK_TEST_CONNECTION_STRING` and a
test role allowed to create and drop isolated databases. The current adopted
suites and the broader coverage backlog are documented in
[EF Core relational specification tests](docs/ef-core/specification-tests.md).

Public API proposals should explain lifetime/ownership, synchronous and asynchronous behaviour, cancellation, and how unknown future PostgreSQL values degrade. The shipped ADO.NET, replication, and extension-authoring assemblies have `PublicAPI.Shipped.txt` contracts. Additive preview APIs belong in `PublicAPI.Unshipped.txt`; removing or changing a shipped signature requires an explicit compatibility/versioning decision and documentation update. See [API compatibility](docs/api-compatibility.md).
