# BlueTusk Continuous Graph 0.1.0-preview.1

This is the first package-verified Continuous Graph preview candidate. It
builds trusted, registered PostgreSQL 19 SQL/PGQ plans on the existing BlueTusk
property-graph, Live, and Control Plane foundations without exposing
replication internals to applications.

## Package

- `BlueTusk.ContinuousGraph`

The package uses the repository's MIT licence and the independent Continuous
Graph `0.1.0-preview.1` version property.

## Capabilities in this preview

- trusted typed graph-query registration against EF property-graph metadata;
- a production capability probe that requires negotiated PostgreSQL 19
  SQL/PGQ support;
- explicit graph-element aliases resolved to exact relational invalidation
  dependencies;
- fail-closed query-shape validation with deterministic key ordering and one
  bounded `Take`;
- stable registration fingerprints and result-free operational descriptors;
- gap-free Live cursor reservation, authoritative `GRAPH_TABLE` requery, and
  keyed add/update/remove/reorder/reset events;
- security-scoped subscription identities and normal Live cancellation;
- authorised, HTML-safe Control Plane dashboard and API inventory;
- executable fraud-transfer and network-health PostgreSQL 19 samples; and
- checked-in live registration, 999-path requery, invalidation/diff, and
  allocation budgets.

The candidate verification gate includes a zero-warning repository Release
build, the complete offline solution suite, all seven Continuous Graph tests
against the pinned PostgreSQL 19 Beta 2 service, public API and dependency
conformance, documentation validation, allocation budgets, and an inspected
NuGet pack.
The gated pack is reproducible with
`./eng/pack-product-family.ps1 -Family ContinuousGraph -Candidate -NoRestore`;
candidate mode emits verification artifacts but does not open the publication
gate.

## Preview boundaries

- PostgreSQL 15–18 and PostgreSQL 19 servers without negotiated SQL/PGQ
  capability fail during registration.
- Queries are registered by trusted server code. Arbitrary client SQL, LINQ,
  graph patterns, and unbounded paths are not accepted.
- CDC is an invalidation signal. Client-visible rows always come from a fresh
  authorised EF/`GRAPH_TABLE` query.
- Incremental graph maintenance is deferred; affected subscriptions use
  authoritative requery and keyed diff.
- Explicit element aliases are required. Dependencies are not inferred from
  caller-authored raw SQL.
- PostgreSQL 19 remains beta-sensitive, so server syntax and capability
  compatibility can change before PostgreSQL 19 is final.
- The reference ShortRun figures are regression evidence, not latency service
  levels or universal production performance claims.
- The family remains non-publishable until the Live production-audit and
  release dependency pass. The release script machine-enforces that ordering.
- Package verification does not complete the separate Streams 72-hour, Sync
  24-hour, or Control Plane release gates.

After every declared release dependency passes, the independent release
workflow still publishes only after an explicit publish-enabled workflow
dispatch or matching Continuous Graph version tag. Passing this repository gate
does not silently publish the package.
