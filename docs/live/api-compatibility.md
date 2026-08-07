# Live public API compatibility

The current Live 1.0 candidate surface is locked by two independent gates:

- Roslyn PublicApiAnalyzers reject undeclared additions and incompatible
  removals in every Live package; and
- `eng/live-api-freeze.json` records a platform-independent SHA-256 digest for
  every Live public API baseline.

This makes an API edit deliberate and reviewable even when it is technically
additive. To change the candidate, update the implementation and its API
baseline, document source and binary compatibility, run the complete Live
NuGet/npm and PostgreSQL transport matrices, and update the freeze manifest in
the same commit. The compatibility test scans the source tree so a new Live
package cannot omit a frozen baseline.

The candidate becomes the Live 1.0 shipped baseline only after its dependency
release gates and final format/transport verification pass. Until then, this is
an engineering freeze rather than a claim that 1.0 was published.
