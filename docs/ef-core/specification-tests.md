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
- `MigrationsTestBase`: all 134 live schema-evolution and catalogue round-trip
  contracts covering tables, columns, keys, indexes, sequences, comments,
  collations, generated columns, JSON mappings, primitive collections
  (including the converted required-collection cases EF Core skips), seed
  data, migration snapshot compilation, and database-model reverse
  engineering. PostgreSQL's rejection of implicit arbitrary text-to-JSONB casts
  is asserted explicitly in the three applicable cases rather than skipped;
- `RelationalModelBuilderTest`: all 748 offline generic model-building
  contracts covering non-relationship mappings, primitive-collection element
  facets, complex types and collections, inheritance, one-to-many,
  many-to-one, one-to-one, many-to-many, and owned types. This includes the
  cases EF Core skips: shadow properties on reference complex types (`#35613`)
  and value-type (tuple) complex collections (`#31411`). Two of those run as
  ported copies because they cannot pass as written: EF Core's tuple-collection
  test omits the `ToJson()` configuration that every sibling relational
  complex-collection test applies, and its discriminator test predates
  `#38119`, after which EF Core 10 saves complex-type discriminators so that an
  optional complex property can change between null and non-null. Each port
  changes only that line. This gate found and closed BlueTusk's missing
  `decimal`/`numeric` array-element mapping;
- `DataAnnotationRelationalTestBase`: 97 live model, validation, concurrency,
  transaction, and data-annotation cases;
- `CompositeKeyEndToEndTestBase`: all three live composite-key cases;
- `FieldMappingTestBase`: 167 live field, property, relationship, and
  change-tracking cases;
- `WithConstructorsTestBase`: 41 live materialization and constructor-binding
  cases; and
- `PropertyValuesRelationalTestBase`: all 202 live current, original, store,
  inheritance, complex-type, and structural-JSON value cases, plus seven
  BlueTusk store-value regressions. This includes the four cases EF Core skips
  (`#31411`), which read complex-collection store values. In EF Core 10, and on
  EF Core's main branch, `EntityEntry.GetDatabaseValues()` builds the store
  values from scalar columns only, inside `EntityEntry`, so no provider service
  can add complex-collection values. BlueTusk therefore provides
  `GetCompleteDatabaseValues()` and `GetCompleteDatabaseValuesAsync()`, which
  read the same scalar values and every complex collection in one statement.
  The four skipped cases and the two store-value `ToObject` cases run as ported
  copies that call these methods. The ports also seed the School that EF Core's
  shared fixture leaves commented out pending `#31411`, and read `Departments`
  through `ComplexCollectionProperties` instead of the scalar-only
  `Properties`. A BlueTusk regression pins EF Core's own `GetDatabaseValues()`
  behaviour, so the ports are revisited when EF Core fixes it;
- `UpdatesRelationalTestBase`: all 36 live insert, update, delete, concurrency,
  generated-value, batching, filtered-index, and identifier-length contracts;
- `StoreGeneratedFixupRelationalTestBase`: all 119 live temporary-key,
  generated-key, relationship-fixup, and composite-key contracts; and
- `ComplexTypesTrackingRelationalTestBase`: all 398 live tracking, mutation,
  JSON persistence, and JSON-query contracts, including the cases EF Core
  skips: array-typed complex collections, readonly structs whose constructors
  take nested complex values (`#31621`), and complex collections of structs and
  readonly structs, through properties and fields and with nested struct
  collections (`#31411`). EF Core has never executed its struct-collection
  tests and two of its helpers are defective: the struct-collection factories
  insert a default element that the shared assertions do not expect, and two
  change-detection tests read a collection through the reference-only
  `ComplexProperty` API. The affected methods run as ports in
  `BlueTuskComplexTypesTrackingTest.StructCollections.cs` that correct only
  those defects. One provider regression recursively verifies that every
  nested JSON scalar has an EF JSON reader/writer;
- `ComplexTypeQueryRelationalTestBase`: all 148 live filtering, projection,
  ordering, grouping, equality, set-operation, optional-navigation,
  constructor-binding, bulk update, and class/struct complex-type query
  contracts, including EF Core's skipped duplicate complex-projection pushdown
  case (`#31376`);
- `AdHocComplexTypeQueryRelationalTestBase`: all 14 discovered complex-type
  model and query regressions. Thirteen execute the portable relational
  contract; the remaining case is an upstream SQL Server-only mapping test
  scheduled for removal by EF Core and is represented by the same documented
  PostgreSQL provider no-op as the reference provider; and
- `AdHocJsonQueryRelationalTestBase`: all 63 live structural-JSON query,
  missing/null member, malformed-shape, primitive-array, custom-property-name,
  entity-splitting, and materialization contracts, including EF Core's skipped
  JSON primitive-array projection case. PostgreSQL-specific
  seed data exercises valid `jsonb` documents with deliberately missing, null,
  or structurally incompatible members rather than bypassing those cases.

Without live credentials, the executable gate is 803 passing tests with no
skips. EF Core 10.0.11 declares 117 static skips in the adopted test bases (65
model-building cases for issue `#35613`, one model-building case for issue
`#31411`, 50 from its complex-struct collection backlog, and one from its
duplicate complex-projection pushdown backlog); BlueTusk runs all of them; the
four complex-collection store-value cases run as the ports described above. No
BlueTusk test is skipped to hide a provider failure.
Every virtual migration test is overridden because EF Core's own compliance
test fails when a provider inherits a generator case without asserting its
generated SQL.

The adopted live gate discovers 1,429 cases and all of them pass. Combined with
the offline gate, the assembly discovers 2,232 cases: all 2,232 pass, none
fail, and none are skipped. The data-annotation fixture follows
PostgreSQL provider semantics by overriding the three relational expectations
that require a length exception or SQL Server-style rowversion behavior; these
are provider-specific no-op assertions, matching the reference PostgreSQL
provider's contract rather than hidden skips.

In an earlier capture, the 223 complex-type/JSON query cases also ran as a
focused PostgreSQL 15–19 matrix, each server reporting 221 passes and the two
upstream EF skips that BlueTusk now runs. The complete official-assembly
results from that earlier capture are:

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
PostgreSQL.

EF Core also rejects complex collections of value types (`#31411`): its JSON
shaper only accepts reference element types, and its change detector matches
collection elements by reference, which a struct copied into a new box on
every read never satisfies. BlueTusk accepts them. Members of struct elements
are written through setters that copy each struct level and write it back.
JSON documents that hold value-type collections are read with EF Core's own
JSON reader and value reader/writers, whether they belong to a queried entity
or are projected on their own. Change detection pairs struct elements by
position and value: unchanged elements first, then a changed element with the
unpaired original between the same unchanged neighbours, so an element edited
in place is reported as modified and inserted or removed elements as added or
deleted. Models without value-type complex collections use EF Core's own
detector. Shadow properties on value-type complex types remain rejected.
`ValueTypeComplexCollectionTests` round-trips struct, readonly-struct, tuple and
nested struct collections through PostgreSQL with tracking, no-tracking and
projection queries, and checks removal, insertion and in-place changes.

A single PostgreSQL 18 run of the full assembly at this source discovered 2,232
cases: 2,232 passed, none failed, and none were skipped or left unexecuted.

The strict Core release collector rejects any skipped or unexecuted case. This
local run is diagnostic; release qualification comes only from the collector's
own exact-candidate runs.

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
relational specification assembly is inherited. The official gate (2,232 cases
on PostgreSQL 18 at this source), with its capability-adjusted form on older
servers, is paired with BlueTusk's native provider project (349 cases at this
source) on each PostgreSQL 15–19 server. The latter covers PostgreSQL-specific
translations, migrations, catalogue discovery, scaffolding, database lifecycle,
and SQL/PGQ. Future EF upgrades must re-run both gates and explicitly review
newly published official test bases rather than silently broadening or
weakening this boundary.

References: [Writing an EF Core database provider](https://learn.microsoft.com/ef/core/providers/writing-a-provider),
the [EF Core 10.0.11 relational specification-test source](https://github.com/dotnet/efcore/tree/v10.0.11/test/EFCore.Relational.Specification.Tests),
and the [EF Core repository](https://github.com/dotnet/efcore).
