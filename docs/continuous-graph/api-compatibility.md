# ContinuousGraph public API compatibility

The ContinuousGraph 1.0 candidate surface is locked by two independent gates:

- Roslyn PublicApiAnalyzers reject undeclared additions and incompatible
  removals in both ContinuousGraph packages; and
- [`eng/continuous-graph-api-freeze.json`](../../eng/continuous-graph-api-freeze.json)
  records a platform-independent SHA-256 digest for every public API baseline.

To change the candidate, update the implementation and API baseline, document
source and binary compatibility, run the complete ContinuousGraph and
PostgreSQL 19 suite, and update the freeze manifest in the same reviewed
commit. Any signature change after candidate freeze invalidates every
exact-SHA result.

The candidate becomes the shipped 1.0 baseline only after PostgreSQL 19 GA,
the 24-hour ContinuousGraph endurance gate, dependency publication, and final
protected release verification pass. Until then, the freeze is an engineering
contract, not a claim that 1.0.0 has been published.
