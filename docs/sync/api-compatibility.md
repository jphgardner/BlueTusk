# Sync public API compatibility

The current Sync 1.0 candidate surface is locked by two independent gates:

- Roslyn PublicApiAnalyzers reject undeclared additions and incompatible
  removals in every Sync package; and
- `eng/sync-api-freeze.json` records a platform-independent SHA-256 digest for
  every Sync public API baseline.

This makes an API edit deliberate and reviewable even when it is technically
additive. To change the candidate, update the implementation and its API
baseline, document source and binary compatibility, run the complete Sync
connector and packaging matrices, and update the freeze manifest in the same
commit. The compatibility test scans the source tree so a new Sync package
cannot omit a frozen baseline.

The candidate becomes the Sync 1.0 shipped baseline only after the 24-hour
endurance gate and final connector verification pass. Until then, this is an
engineering freeze rather than a claim that 1.0 was published.
