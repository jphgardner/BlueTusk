# EF Core relational specification tests

BlueTusk consumes Microsoft's provider-facing EF Core relational specification
package directly. The package is pinned to the same `10.0.10` version as the
provider's runtime and design dependencies, so an EF upgrade cannot silently
move the contract suite independently of the provider.

The executable harness lives in
`tests/BlueTusk.EntityFrameworkCore.SpecificationTests`. It currently adopts
these official suites:

- `RelationalServiceCollectionExtensionsTestBase`: all three provider-service
  registration, idempotency, isolation, and lifetime contracts;
- `MigrationsSqlGeneratorTestBase`: all inherited generator cases, including
  provider-specific golden SQL for PostgreSQL column facets, foreign keys,
  renames, seed insert/update/delete operations, multiline defaults, sequence
  restart operations, unsupported store-type diagnostics, and PostGIS spatial
  literals.

The current offline gate is 55 tests with no skips. Every virtual migration
test is overridden because EF Core's own compliance test fails when a provider
inherits a generator case without asserting its generated SQL.

Run the gate directly with:

```powershell
dotnet test tests/BlueTusk.EntityFrameworkCore.SpecificationTests -c Release
```

The specification package uses xUnit v2 while BlueTusk's native tests use xUnit
v3. The official suites therefore have a separate test assembly; this prevents
duplicate framework types while keeping both assemblies in `BlueTusk.slnx`.
The Visual Studio test adapter discovers and runs both.

This is an adopted official-suite slice, not a claim that the whole EF
relational suite is complete. Live relational fixtures and the broader official
query/update/model/scaffolding bases remain part of the explicit 1.0 gate in
the [roadmap](../roadmap.md). BlueTusk's provider-specific EF tests continue to
cover those implemented surfaces while official-suite adoption expands.

References: [Writing an EF Core database provider](https://learn.microsoft.com/ef/core/providers/writing-a-provider),
[Microsoft.EntityFrameworkCore.Relational.Specification.Tests](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore.Relational.Specification.Tests/10.0.10),
and the [EF Core source](https://github.com/dotnet/efcore).
