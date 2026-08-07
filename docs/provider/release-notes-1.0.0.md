# BlueTusk Provider 1.0.0 release record

Status: release-prepared, not published.

Provider 1.0.0 is the stable contract for the ADO.NET provider, protocol and
transport stack, replication, EF Core provider, authentication integrations,
extension packages, templates, and tooling registered to the Provider family.
Its public API is frozen by
[`eng/provider-api-freeze.json`](../../eng/provider-api-freeze.json).

The supported PostgreSQL baseline is 15 through 19. PostgreSQL 19 support and
publication remain blocked until the repository records the official GA
milestone, a digest-pinned GA image, and exact-candidate matrix evidence.

Support starts only after `provider-v1.0.0` is created from the immutable
reviewed `main` candidate and the package registry, hashes, provenance,
installation smoke test, and dependency resolution are verified. Until then,
candidate artifacts are test-only and receive no published-version support
commitment.

Breaking public API or wire-contract changes require a new major version.
Post-publication defects use rollback or pinning followed by a new fixed
version; the 1.0.0 artifacts are never replaced.
