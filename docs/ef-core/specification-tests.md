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
  literals;
- `DataAnnotationRelationalTestBase`: 97 live model, validation, concurrency,
  transaction, and data-annotation cases;
- `CompositeKeyEndToEndTestBase`: all three live composite-key cases;
- `FieldMappingTestBase`: 167 live field, property, relationship, and
  change-tracking cases;
- `WithConstructorsTestBase`: 41 live materialization and constructor-binding
  cases; and
- `PropertyValuesRelationalTestBase`: 198 passing live current, original,
  store, inheritance, complex-type, and structural-JSON value cases, plus four
  skips declared by EF Core itself for its open complex-collection query issue.

The current offline gate is 55 tests with no skips. Every virtual migration
test is overridden because EF Core's own compliance test fails when a provider
inherits a generator case without asserting its generated SQL.

The adopted live gate discovers 510 cases: 506 pass and the four inherited EF
Core complex-collection cases are skipped. The data-annotation fixture follows
PostgreSQL provider semantics by overriding the three relational expectations
that require a length exception or SQL Server-style rowversion behavior; these
are provider-specific no-op assertions, matching the reference PostgreSQL
provider's contract rather than hidden skips.

Run the gate directly with:

```powershell
dotnet test tests/BlueTusk.EntityFrameworkCore.SpecificationTests -c Release
```

Set `BLUETUSK_TEST_CONNECTION_STRING` to run the live fixtures. The configured
role must be able to create and drop databases: each fixture force-drops and
recreates its isolated database before seeding it.

```powershell
$env:BLUETUSK_TEST_CONNECTION_STRING = "Host=localhost;Port=5419;Database=bluetusk_tests;Username=postgres;Password=postgres;SSL Mode=Disable;Channel Binding=Disable"
dotnet test tests/BlueTusk.EntityFrameworkCore.SpecificationTests -c Release
```

The specification package uses xUnit v2 while BlueTusk's native tests use xUnit
v3. The official suites therefore have a separate test assembly; this prevents
duplicate framework types while keeping both assemblies in `BlueTusk.slnx`.
The Visual Studio test adapter discovers and runs both.

This is an adopted official-suite slice, not a claim that the whole EF
relational suite is complete. Broader official query, update, model,
migrations, and scaffolding bases remain part of the explicit 1.0 gate in the
[roadmap](../roadmap.md). BlueTusk's provider-specific EF tests continue to
cover those implemented surfaces while official-suite adoption expands.

References: [Writing an EF Core database provider](https://learn.microsoft.com/ef/core/providers/writing-a-provider),
[Microsoft.EntityFrameworkCore.Relational.Specification.Tests](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore.Relational.Specification.Tests/10.0.10),
and the [EF Core source](https://github.com/dotnet/efcore).
