# Compatibility and versioning

## Current support matrix

| BlueTusk package line | Target framework | EF Core | PostgreSQL |
| --- | --- | --- | --- |
| `1.0.0` stable (published) | .NET 10 (`net10.0`) | 10.0.11 | 15, 16, 17, and 18; PostgreSQL 19 features are capability guarded |
| `1.1.0-rc.1` prerelease (published) | .NET 10 (`net10.0`) | 10.0.11 | 15, 16, 17, and 18; PostgreSQL 19 SQL/PGQ evaluation requires a negotiated prerelease capability |

PostgreSQL 15–18 are the stable Core compatibility baseline. Core 1.1.0
does not require a PostgreSQL 19 GA result. PostgreSQL 19 coverage remains
a separate preview. The pinned PostgreSQL 19 Beta 3 image is a historical
SQL/PGQ fixture; native Graph requires the connected server to report the
actual SQL/PGQ capability.

Portable Graph has been verified locally on PostgreSQL 15–18 without SQL/PGQ.
It has its own release qualification and is absent from the published
1.1.0-rc.1 packages. See the [release tracks](docs/releases/release-tracks.md)
for the current evidence and the boundaries of each track.

The `1.1.0-rc.1` train is immutable and was published from commit
`2e735ed46aec11d5009158a00ca7b862f9ec12af`. All coordinated BlueTusk NuGet and
npm dependencies must use that exact version. A corrected prerelease receives
a new suffix such as `rc.2`; an existing package is never overwritten.

## Compatibility policy

Before `1.0.0`, public APIs may change between minor releases. Patch releases
should remain source- and binary-compatible unless a security or correctness
defect makes that unsafe. The shipped API/nullability baselines make accidental
changes fail compilation in covered core, replication, and extension-authoring
assemblies.

Beginning with `1.0.0`, removals and incompatible signature or behavioral
changes require a new major version. Additive APIs may ship in minor releases;
compatible fixes ship in patches. Security corrections may override this rule
only when retaining the old behavior would keep users exposed, and must be
called out in release notes.

Protocol and SQL behavior is negotiated from the server's reported version and
catalogue-derived capabilities. Feature implementations use the centralized
capability model rather than scattering version-number checks. A newly released
PostgreSQL major is supported only after it has an executable CI matrix entry
and its beta-sensitive assumptions have been re-audited.
