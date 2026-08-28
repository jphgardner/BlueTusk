# ContinuousGraph public API compatibility

The immutable 1.0 surface and additive 1.1 candidate surface are locked by two
independent gates:

- Roslyn PublicApiAnalyzers reject undeclared additions and incompatible
  removals in both ContinuousGraph packages while allowing reviewed additive
  1.1 APIs; and
- [`eng/continuous-graph-api-freeze.json`](../../eng/continuous-graph-api-freeze.json)
  records a platform-independent SHA-256 digest for every public API baseline.

To change the candidate, prove that every 1.0 member remains source and binary
compatible, update the implementation and API baseline, run the complete
ContinuousGraph and PostgreSQL 19 suite, and update the freeze manifest in the
same reviewed commit. The reviewed 1.1 additions cover automatic incremental
sessions, immutable impact/capability metadata, maintenance tiers, the trusted
CDC projector contract, and per-tier status. Any signature change after
candidate freeze invalidates every exact-SHA result.

The 1.0 packages remain unchanged. The 1.1 candidate becomes publishable only
after PostgreSQL 19 GA, the 24-hour ContinuousGraph endurance gate, dependency
publication, performance evidence, and final protected release verification
pass. Until then, the 1.1 freeze is an engineering contract, not a release
claim.
