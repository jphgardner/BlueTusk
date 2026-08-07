# Compatibility and versioning

## Current support matrix

| BlueTusk package line | Target framework | EF Core | PostgreSQL |
| --- | --- | --- | --- |
| `1.0.0` (release-prepared, unpublished) | .NET 10 (`net10.0`) | 10.0.10 | 15, 16, 17, 18, and PostgreSQL 19 after its GA gate |

PostgreSQL 15–18 are the released-server compatibility baseline. PostgreSQL 19
is currently exercised with the pinned `postgres:19beta2-alpine` image and is
explicitly beta-sensitive: SQL/PGQ syntax, catalogues, and capability thresholds
may need to change before GA. Stable V1 publication requires a digest-pinned
PostgreSQL 19 GA image and exact-candidate 15–19 evidence. The live CI matrix runs the complete
solution against every listed major version; feature-specific documentation
records narrower extension-server combinations where an extension image sets
the server version.

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
