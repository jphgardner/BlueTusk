# BlueTusk Sql

New preview typed SQL tooling consists of `BlueTusk.Sql` and the incremental
`BlueTusk.Sql.SourceGeneration` analyzer. Reference the generator as an analyzer
with `ReferenceOutputAssembly="false"` and add `.sql` files as `AdditionalFiles`.

```sql
-- bluetusk-query: Contoso.Queries.FindOrders
-- bluetusk-param: tenant text required
-- bluetusk-result: Id int8 required
-- bluetusk-result: Label text nullable
-- bluetusk-max-rows: 100
SELECT id AS "Id", label AS "Label"
FROM app.orders WHERE tenant_id = $1 ORDER BY id LIMIT 100;
```

Generated code contains a typed `Arguments` record, a typed `Row` record and one
immutable `Definition`. Positional parameter OIDs, result getters and nullable
handling are emitted directly; there is no reflection-driven construction.
Invalid/duplicate identifiers, types, bounds or directives fail compilation.
Directives precede the SQL body, preserving application SQL literals.

```csharp
await FindOrders.Definition.ValidateAsync(dataSource, new("tenant-a"), token);
await foreach (var row in FindOrders.Definition.ReadAsync(
    connection, new("tenant-a"), transaction, token))
{
    // The connection and optional transaction remain application-owned.
}
```

PostgreSQL validates read query grammar and actual result name/type contracts
through a zero-row wrapper in a read-only transaction. Runtime execution checks
the result contract and required nulls before constructing rows. Runtime
execution wraps each admitted single read query in an outer limit of
`MaximumRows + 1`, so PostgreSQL sends at most the declared rows plus one
overflow sentinel even for a query without parameters. The original `ORDER BY`,
`LIMIT`, and `OFFSET` remain inside the wrapper. BlueTusk SQL requests a non-sequential
portal reader and rejects a connection configured to buffer entire readers
before executing the query. SQL forms that PostgreSQL cannot use inside a
derived table fail rather than running without the cap. Command time is also
bounded. Each field defaults to one MiB and each result to
64 MiB of encoded PostgreSQL field data plus four length bytes per field. The
BlueTusk reader exposes encoded field length without value decoding/copying, so
limits are checked before the generated projector allocates strings/byte arrays.
The non-sequential portal reader buffers one row before field admission, so a
single oversized row can still allocate memory beyond these limits. Binary
prefixes and text encodings count toward this budget; it is not a bound on
every transport/CLR allocation. Execution requires BlueTuskDataReader for
this admission contract. Early disposal releases commands/readers
without committing or disposing caller-owned transactions. Overflow is explicit,
and result-shape validation does not establish application authorization.

The initial generator supports bool, int2/int4/int8, text/varchar, UUID,
float4/float8, decimal numeric, timestamp/timestamptz, bytea and JSON/JSONB.
It also supports `numeric-precise` as lossless `BlueTuskNumeric`, date/time,
`interval-pg` as the PostgreSQL months/days/microseconds representation, scalar
arrays, nullable int2/int4/int8 array elements (`int4?[]`), nullable text/JSON
array elements, and a two-dimensional integer matrix (`int4[,]`). Nullable
containers and nullable elements are separate contracts. Result array metadata
uses the provider's catalogue names such as `_int4`. Nullable integer element
reading uses a typed bounded decoder and never substitutes zero for NULL.
Unsupported dimensions/lower bounds fail explicitly; standard lower bounds
and one/two-dimensional arrays are statically constructed for NativeAOT.
PostgreSQL remains authoritative; annotations declare a contract rather than
attempting to implement the complete PostgreSQL SQL grammar in the compiler.

The shared type roundtrip now runs in both the regular tests and the actual
Windows native executable. It covers nullable short/long elements, GUIDs,
nonfinite floats, decimal and lossless numeric arrays, timestamp/time-zone/date,
JSON/varchar arrays, and date/time/month-bearing intervals, alongside earlier
integer matrices and nullable containers. This evidence covers the listed built-in
shapes; extension contracts remain additional product work.

Catalogue-backed enum and domain bindings use qualified, lower-case PostgreSQL
identifiers and an explicit base type for domains:

```sql
-- bluetusk-validation: required
-- bluetusk-param: state enum:app.order_state required
-- bluetusk-param: amounts domain:app.positive:int4[] nullable
-- bluetusk-result: State enum:app.order_state required
-- bluetusk-result: Amounts domain:app.positive:int4[] nullable
```

Use `enum:app.order_state[]` for an enum array, and `nullable` for a nullable
container. The generated enum CLR type is `BlueTuskEnumValue`, which preserves
the exact catalogue label; this is distinct from registering an application CLR
enum with the provider. Domain CLR types use the declared supported built-in
base codec. Arrays are arrays of the domain, with PostgreSQL's distinct array
type identity. An explicit `?[]` element marker, quoted/mixed-case identifiers,
and arbitrary extension codecs are not declared by this annotation grammar
and fail at compilation. Array element null handling follows the supported
base codec. Nested domains and domains whose base is itself an array fail
live validation because the declared direct base OID does not match. Only base
types already supported by this generator can be named;
an array requires that base's supported one-dimensional array shape.

These annotations require `bluetusk-validation: required`, an attested
`.bluetusk-catalog.json` snapshot and a matching installed-CLI receipt. The CLI
checks type kind, direct base OID, codec availability and actual result OIDs;
generated parameters resolve qualified names from each live database catalogue
instead of embedding database-specific OIDs. Result identity checks read the
unsigned wire OID directly, including values above `Int32.MaxValue`, without
constructing a schema table. Runtime checks also reject a
same-named enum or domain array from a different schema. PostgreSQL describes
a scalar domain result using its built-in base OID, so a scalar result check
proves the base wire shape and the declared domain's catalogue contract, but
cannot prove that an arbitrary SQL expression originated from that domain.
The database enforces domain constraints when a parameter is bound to the
domain type. Refresh the catalogue snapshot and receipt after type DDL.

`eng/test-sql-package-consumer.ps1` freshly packs the transitive BlueTusk closure,
installs the candidate Schema/Sql CLIs into an isolated cache, captures/validates
a fixture schema and compiles a package-only consumer with that receipt. NuGet
source mapping resolves every `BlueTusk.*` dependency from the fresh local feed,
with a different empty consumer cache. The actual consumer then runs all the
shared type shapes, then validates an attested catalogue snapshot and reruns
those shapes with its offline receipt. It then creates an owned enum/domain
catalogue fixture, validates a second annotated query with the installed CLI,
compiles that query from installed packages and executes scalar, nullable and
array roundtrips. The fixture is removed after the run. Package hashes, commit and dirty state
are retained under
`artifacts/ecosystem/sql-package-consumer-*`. Both validation modes passed in the
fresh local package consumer; this is not an immutable release or a production qualification.

For builds that require database validation, add `-- bluetusk-validation: required`
before the SQL body. Capture a schema with `bluetusk-schema capture`, saving it
as `app.bluetusk-schema.json`, then run:

```text
bluetusk-sql validate --schema app.bluetusk-schema.json --schemas app --output app.bluetusk-sql.xml --query orders.sql --query other.sql
```

The tool reads `BLUETUSK_SQL_CONNECTION_STRING`. It checks the captured schema
and describes all queries in one read-only repeatable-read transaction, with
typed NULL parameter bindings and zero-row wrappers. SQL must use qualified
application object names; the validation search path is `pg_catalog`. The
caller must keep publication/application DDL stable during validation. A
receipt records the canonical schema fingerprint, exact snapshot document
digest, and each SQL/parameter/result contract digest. Add both the snapshot
and receipt as `AdditionalFiles`. Compilation needs no database connection:
`BTS003` rejects missing/stale receipts or a changed snapshot file. The emitted
`ValidatedSchemaFingerprint` identifies the validated schema. This is a build
consistency check, not signed provenance or application authorization. Optional
receipts emit validation metadata only when both the query and exact snapshot
digest match; a missing or changed snapshot leaves that metadata unset.

Expanded catalogue validation also covers enum/domain/routine, object authority,
extension and publication drift when the relation fingerprint is unchanged:

```text
bluetusk-schema capture-catalog --schemas app --output app.bluetusk-catalog.json
bluetusk-sql validate --schema app.bluetusk-catalog.json --schemas app --output app.bluetusk-sql.xml --query orders.sql
```

Add that catalogue and its receipt as `AdditionalFiles`. A compilation accepts
one relation snapshot or one catalogue snapshot. Catalogue mode describes every
query between two captures in the same read-only repeatable-read transaction;
independent fresh observations attest the whole description interval. Existing
catalogue-row changes during a blocked description reject the receipt, and every
failure preserves the prior artifact. The pool must admit two simultaneous
connections and the validation role needs the narrow catalogue permissions in
the [Schema attestation contract](../schema/catalogue-attestation.md). Unrelated
catalogue DDL can conservatively reject validation. The attestation is not a
universal DDL serialization barrier, and the documented uncovered PostgreSQL
contracts remain outside its fingerprint. Legacy relation mode still requires
the caller to keep DDL stable.

The generator checks contiguous positional parameter declarations, excluding
quoted identifiers, string/dollar literals, escaped strings and nested comments.
Repeated parameters are allowed. Required string/byte-array arguments reject
null before execution. The CLI validates result names/types; declarations of
required result nullability remain a runtime check and require application
review for expressions/outer joins. Source and validation documents have
aggregate bounds; artifact output is atomically replaced only after successful
validation. Failures retain prior artifacts and never print SQL or credentials.
Exit 2 means schema drift; exit 1 means an invalid operation/contract.

Remaining requirements include extension codecs, nested/custom domain shapes and broader array shapes,
mutation-contract validation,
performance/endurance and release qualification. The native SQL/Schema smoke
has actually published and executed on Windows x64 against PostgreSQL,
covering generated nullable, UUID, JSONB, bytea, timestamp and exact numeric
bindings plus schema capture/interchange/compatibility. Linux native execution
remains a separate qualification gate.
See the full [implementation programme](../ecosystem/implementation-programme.md).
