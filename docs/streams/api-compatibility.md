# Streams public API compatibility

The current Streams 1.0 candidate surface is locked by two independent gates:

- Roslyn PublicApiAnalyzers reject undeclared additions and incompatible
  removals in every Streams package; and
- `eng/streams-api-freeze.json` records a platform-independent SHA-256 digest
  for every Streams public API baseline.

This makes an API edit deliberate and reviewable even when it is technically
additive. To change the candidate, first update the implementation and its API
baseline, document source and binary compatibility, run the full Streams test
and package matrices, and then update the freeze manifest in the same commit.
The compatibility test also scans the source tree so a new Streams package
cannot omit a frozen baseline.

The candidate becomes the Streams 1.0 shipped baseline only after the format
upgrade suites and the successful 72-hour release-endurance evidence pass. Until
then, this is an engineering freeze rather than a claim that 1.0 was published.

The published-source lineage proof adds `ChangeDeliveryTestFactory.CreateCommittedWithLineage`
to the optional `BlueTusk.Streams.Testing` package. It creates a synthetic
delivery carrying the database and publication OIDs used by protected consumers.
Existing factory methods and their signatures remain unchanged, so this is a
source- and binary-compatible addition. The Streams.Testing API baseline and
freeze digest advance together; the runtime Streams public API is unchanged.
