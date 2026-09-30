# Compatibility and versioning

## Current support matrix

| BlueTusk package line | Target framework | EF Core | PostgreSQL |
| --- | --- | --- | --- |
| `1.0.0` stable (published) | .NET 10 (`net10.0`) | 10.0.11 | 15, 16, 17, and 18; PostgreSQL 19 features are capability guarded |
| `1.1.0-rc.1` prerelease (published) | .NET 10 (`net10.0`) | 10.0.11 | 15, 16, 17, and 18; PostgreSQL 19 SQL/PGQ evaluation requires a negotiated prerelease capability |

PostgreSQL 15–18 are the released-server compatibility baseline. PostgreSQL 19
is currently exercised with the pinned `postgres:19beta3-alpine` image and is
explicitly beta-sensitive: SQL/PGQ syntax, catalogues, and capability thresholds
may need to change before GA. Stable `1.1.0` requires a digest-pinned PostgreSQL
19 GA image and exact-candidate 15–19 evidence. The live CI matrix runs the complete
solution against every listed major version; feature-specific documentation
records narrower extension-server combinations where an extension image sets
the server version.

The `1.1.0-rc.1` train is immutable and was published from commit
`2e735ed46aec11d5009158a00ca7b862f9ec12af`. All coordinated BlueTusk NuGet and
npm dependencies must use that exact version. A corrected prerelease receives
a new suffix such as `rc.2`; an existing package is never overwritten.

As of 2026-08-29, the official PostgreSQL project lists PostgreSQL 19 Beta 3
and plans the major release for September 2026. Its
[beta guidance](https://www.postgresql.org/developer/beta/) explicitly advises
against production use of beta releases. BlueTusk keeps SQL/PGQ capability
guards and the stable 1.1 GA gate for that reason.

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
