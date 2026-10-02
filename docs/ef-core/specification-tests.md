# EF Core relational specification tests

BlueTusk consumes Microsoft's provider-facing EF Core relational specification
package directly. The package is pinned to the same `10.0.11` version as the
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
- `MigrationsTestBase`: 132 passing live schema-evolution and catalogue
  round-trip contracts covering tables, columns, keys, indexes, sequences,
  comments, collations, generated columns, JSON mappings, primitive
  collections, seed data, migration snapshot compilation, and database-model
  reverse engineering, plus two primitive-collection converter skips declared
  by EF Core itself. PostgreSQL's rejection of implicit arbitrary text-to-JSONB
  casts is asserted explicitly in the three applicable cases rather than
  skipped;
- `RelationalModelBuilderTest`: 746 passing offline generic model-building
  contracts covering non-relationship mappings, primitive-collection element
  facets, complex types and collections (including shadow properties on
  reference complex types, which EF Core skips for `#35613`), inheritance,
  one-to-many, many-to-one, one-to-one, many-to-many, and owned types. Two
  cases retain EF Core's own skip declarations: a value-type (tuple) complex
  collection (`#31411`) and a complex discriminator without an explicit value.
  This gate found and closed BlueTusk's missing `decimal`/`numeric`
  array-element mapping;
- `DataAnnotationRelationalTestBase`: 97 live model, validation, concurrency,
  transaction, and data-annotation cases;
- `CompositeKeyEndToEndTestBase`: all three live composite-key cases;
- `FieldMappingTestBase`: 167 live field, property, relationship, and
  change-tracking cases;
- `WithConstructorsTestBase`: 41 live materialization and constructor-binding
  cases; and
- `PropertyValuesRelationalTestBase`: 198 passing live current, original,
  store, inheritance, complex-type, and structural-JSON value cases, plus four
  skips declared by EF Core itself for its open complex-collection query issue;
- `UpdatesRelationalTestBase`: all 36 live insert, update, delete, concurrency,
  generated-value, batching, filtered-index, and identifier-length contracts;
- `StoreGeneratedFixupRelationalTestBase`: all 119 live temporary-key,
  generated-key, relationship-fixup, and composite-key contracts; and
- `ComplexTypesTrackingRelationalTestBase`: 282 passing inherited live
  tracking, mutation, JSON persistence, and JSON-query contracts, including
  array-typed complex collections and readonly structs whose constructors take
  nested complex values, which EF Core skips; 36 skips declared by EF Core
  itself for open complex-struct collection scenarios; and one provider
  regression that recursively verifies every nested JSON scalar has an EF JSON
  reader/writer;
- `ComplexTypeQueryRelationalTestBase`: 146 passing live filtering,
  projection, ordering, grouping, equality, set-operation, optional-navigation,
  constructor-binding, bulk update, and class/struct complex-type query
  contracts, plus one skip declared by EF Core itself for its open duplicate
  complex-projection pushdown issue;
- `AdHocComplexTypeQueryRelationalTestBase`: all 14 discovered complex-type
  model and query regressions. Thirteen execute the portable relational
  contract; the remaining case is an upstream SQL Server-only mapping test
  scheduled for removal by EF Core and is represented by the same documented
  PostgreSQL provider no-op as the reference provider; and
- `AdHocJsonQueryRelationalTestBase`: 61 passing live structural-JSON query,
  missing/null member, malformed-shape, primitive-array, custom-property-name,
  entity-splitting, and materialization contracts, plus one skip declared by EF
  Core for its open JSON primitive-array projection issue. PostgreSQL-specific
  seed data exercises valid `jsonb` documents with deliberately missing, null,
  or structurally incompatible members rather than bypassing those cases.

Without live credentials, the executable gate is 737 passing tests. Discovery
also reports 117 static skip declarations owned by EF Core: 65 model-building
cases for issue `#35613`, one model-building case for issue `#31411`, 50 from
its complex-struct collection backlog, and one from its duplicate
complex-projection pushdown backlog. No BlueTusk test is skipped to hide a
provider failure.
Every virtual migration test is overridden because EF Core's own compliance
test fails when a provider inherits a generator case without asserting its
generated SQL.

The adopted live gate discovers 1,308 cases: 1,250 pass and 58 cases explicitly
skipped by EF Core are reported as skips. Combined with the offline gate, the
assembly discovers 2,111 cases: 1,987 pass and 124 retain their upstream skip
declarations. The data-annotation fixture follows PostgreSQL provider semantics
by overriding the three relational expectations that require a length exception
or SQL Server-style rowversion behavior; these are provider-specific no-op
assertions, matching the reference PostgreSQL provider's contract rather than
hidden skips.

The 223 complex-type/JSON query cases also run as a focused PostgreSQL 15–19
matrix: each server reports 221 passes and the same two upstream EF skips. The
complete official-assembly results from the earlier capture are:

| PostgreSQL | Passed | Upstream skips | Version-excluded rows | Discovered |
| --- | ---: | ---: | ---: | ---: |
| 15 | 1,973 | 124 | 14 | 2,097 |
| 16 | 1,973 | 124 | 14 | 2,097 |
| 17 | 1,975 | 124 | 12 | 2,099 |
| 18 | 1,987 | 124 | 0 | 2,111 |
| 19 | 1,987 | 124 | 0 | 2,111 |

Generated-column migration cases now execute on every supported server. Stored
columns and the provider's default stored form run the upstream assertions on
PostgreSQL 15–18. Virtual columns require PostgreSQL 18; generated-expression
changes require PostgreSQL 17. On older servers the same migration is executed
and must fail with SQLSTATE `0A000` and the exact provider capability diagnostic.
This replaces the whole-method discovery conditions that also removed supported
stored-column rows. Fresh captures must retain an execution result for every
discovered row; the historical table above does not qualify the current source.

BlueTusk extends EF Core 10 so shadow properties on reference complex types
work end to end, which EF Core rejects (`#35613`): the model validator accepts
them, query materialization reads them into the tracked snapshot from columns
or from JSON, snapshot factories keep their original values, and fixed-size
(array) complex collections are snapshotted by index. Shadow properties on
value-type complex types remain rejected. EF Core binds complex-type
constructor parameters only to scalar properties (`#31621`); a value-type
complex type whose constructor parameters all name mapped members, including
nested complex values, is materialized with `default(T)` followed by member
assignment, as EF Core already does for value types without constructors.
Reference types with such constructors are still rejected. `ComplexTypeShadowPropertyTests`
round-trips table-split, JSON and JSON-collection-element shadow values through
PostgreSQL. A single PostgreSQL 18 run of the full assembly with these
extensions discovered 2,145 cases: 2,103 passed, no failures, and 42 retain EF
Core's own skip declarations (36 complex-struct collection, four
complex-collection property-values and the two model-building cases above).

The strict Core release collector still rejects every inherited upstream skip
declaration. Passing the ordinary specification suite with those skips is not
zero-skip Core compatibility evidence and does not authorize stable publication.

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

This is the adopted official-suite coverage required by BlueTusk's product
specification, not a claim that every test base published in Microsoft's entire
relational specification assembly is inherited. The official 2,111-case gate
on PostgreSQL 18 and 19, and the capability-adjusted official gate on 15–17,
are paired with BlueTusk's native 301-case provider project on each PostgreSQL
15–19 server. The latter covers PostgreSQL-specific translations, migrations,
catalogue discovery, scaffolding, database lifecycle, and SQL/PGQ. Future EF
upgrades must re-run both gates and explicitly review newly published official
test bases rather than silently broadening or weakening this boundary.

References: [Writing an EF Core database provider](https://learn.microsoft.com/ef/core/providers/writing-a-provider),
the [EF Core 10.0.11 relational specification-test source](https://github.com/dotnet/efcore/tree/v10.0.11/test/EFCore.Relational.Specification.Tests),
and the [EF Core repository](https://github.com/dotnet/efcore).
