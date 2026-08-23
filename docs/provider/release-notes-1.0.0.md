# BlueTusk Provider 1.0.0 release record

Status: published on 2026-08-23 from `provider-v1.0.0` at release commit
`7380d7b028c72b2aae348b778711d104d022a3f8`.

The complete release evidence, registry inventory, and explicit owner-accepted
exceptions are recorded in the
[V1 publication record](../releases/1.0.0-publication-record.md).

Provider 1.0.0 is the stable contract for the ADO.NET provider, protocol and
transport stack, replication, EF Core provider, authentication integrations,
extension packages, templates, and tooling registered to the Provider family.
Its public API is frozen by
[`eng/provider-api-freeze.json`](../../eng/provider-api-freeze.json).

The supported PostgreSQL baseline is 15 through 19. The planned PostgreSQL 19
GA publication prerequisite was explicitly deferred for `1.0.0`; the official
GA milestone, digest-pinned GA image, and exact-candidate matrix evidence remain
open before PostgreSQL 19 receives GA-grade validation.

Support starts with the immutable `provider-v1.0.0` packages. Registry
availability, package contents, hashes, SBOMs, provenance, tests, and dependency
resolution passed in the recorded release workflow.

Breaking public API or wire-contract changes require a new major version.
Post-publication defects use rollback or pinning followed by a new fixed
version; the 1.0.0 artifacts are never replaced.

The V1 provider also generates readable fluent property-graph migrations. The
builder validates vertex/edge aliases, labels, key cardinality, properties and
endpoints; generated migrations no longer expose serialized graph metadata as
a long string. The existing definition overload remains compatible.

The three-application RC suite restores Provider only from exact
`1.0.0-rc.1` packages and covers migrations, tenant isolation, idempotency,
optimistic concurrency, PostgreSQL 19 graphs, and package-only architecture.
This is staging verification, not stable support or pilot evidence.
