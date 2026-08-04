# Control Plane API and format compatibility

The Control Plane V1 candidate has two independently enforced compatibility
surfaces:

- compiler public APIs in `BlueTusk.ControlPlane` and `BlueTusk.Dashboard`; and
- versioned HTTP and PostgreSQL audit formats.

The compiler surface is hash-locked by
[`eng/control-plane-api-freeze.json`](../../eng/control-plane-api-freeze.json).
The normal conformance suite rejects an edited, removed, or silently replaced
shipped or candidate signature. Additive APIs first enter
`PublicAPI.Unshipped.txt`; after review, their candidate hash is updated without
misrepresenting them as shipped. Promotion moves the accepted signatures to
the shipped baseline and updates the manifest in the same release change.

[`eng/control-plane-formats.json`](../../eng/control-plane-formats.json)
registers every persisted or remotely consumed format with its current and
minimum readable version. Tests reject registry drift from the implementation.
The V1 agent API uses an explicit route version and response envelope.
PostgreSQL audit storage uses independently versioned schema and record
formats, performs transactional in-place migrations, and refuses a future
schema.

The original unversioned preview routes remain aliases for the V1 payload
during the `0.1.0-preview.1` line. They are not a separate compatibility
contract. Incompatible changes require a new route/envelope version and a
documented migration path.

Continuous Graph integration is deliberately outside the stable Control Plane
core. Applications that install the graph preview add
`BlueTusk.ContinuousGraph.ControlPlane`, which supplies the optional graph
projection. This prevents a stable Control Plane package from taking a
dependency on a preview feature.
