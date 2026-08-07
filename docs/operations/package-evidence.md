# Canonical V1 package evidence

The manual `build.yml` candidate run produces one canonical, evidence-only
package set for all six BlueTusk product families. The set is the package input
to release review. It is not a publication path and receives no NuGet or npm
credentials.

This closes an important integrity boundary: a successful build alone does not
prove which archives were reviewed, and separate family artifacts do not prove
that the complete V1 product set came from the same source commit. The canonical
artifact binds the complete archive inventory, package metadata, source commit,
CycloneDX and SPDX documents, and build provenance into one retained unit.

## Build the package set

Run from a clean checkout:

```powershell
./eng/build-v1-candidate-packages.ps1 `
  -Commit (git rev-parse HEAD)
```

The command:

1. requires a full commit SHA, the matching checkout and a clean tracked tree;
2. restores the complete solution;
3. packs Provider, Streams, Sync, Live, Control Plane and Continuous Graph into
   isolated family directories with publication still disabled;
4. verifies each exact family archive set, version, repository commit,
   dependency version, license, README, symbol package and safe archive path;
5. verifies the three Live npm archives, compiled distributions, dependency
   versions, public metadata and absence of install lifecycle scripts;
6. copies the verified family outputs into one collision-checked canonical
   package directory;
7. creates CycloneDX 1.6 and SPDX 2.3 SBOMs plus build provenance;
8. verifies every package and SBOM hash; and
9. writes the package manifest and removes intermediate family copies.

Output is restricted to `artifacts/` and is replaced on each run:

```text
artifacts/v1-candidate-packages/
├── package-manifest.json
├── packages/
│   ├── *.nupkg
│   ├── *.snupkg
│   └── *.tgz
└── sbom/
    ├── bluetusk.cdx.json
    ├── bluetusk.spdx.json
    └── build-provenance.json
```

`package-manifest.json` records the exact source commit, every family, every
archive path, byte length and SHA-256, per-family counts and bytes, aggregate
counts and bytes, and integrity records for all three supply-chain documents.
The build provenance independently records every package hash and hashes both
SBOMs.

## Verify downloaded evidence

After downloading the artifact, run:

```powershell
./eng/verify-v1-package-evidence.ps1 `
  -EvidenceRoot artifacts/v1-candidate-packages `
  -ExpectedCommit '<40-character-candidate-sha>'
```

The verifier is intentionally independent of the build step. It rejects:

- missing, additional, nested, duplicated or renamed package archives;
- missing or duplicated product families;
- altered byte lengths, SHA-256 values, family summaries or aggregate totals;
- package files assigned to the wrong family;
- a package repository commit or internal BlueTusk dependency version that
  differs from the candidate;
- unsafe archive paths or incomplete NuGet/npm metadata;
- altered or incomplete CycloneDX, SPDX or provenance documents;
- a dirty provenance record; and
- an evidence directory outside the repository during automated verification.

It reconstructs each family set in a temporary directory and reruns the exact
family package-content verifier. Temporary copies are deleted whether
verification passes or fails.

## Exact-candidate binding

For a manual `build.yml` run, GitHub retains
`v1-candidate-packages-<candidate-sha>` for 90 days. The protected
`v1-candidate-readiness.yml` workflow:

1. requires that build run to be a successful `workflow_dispatch` run at the
   exact candidate SHA;
2. downloads exactly one matching package artifact and exactly one website
   artifact from that same run;
3. records the package-manifest and build-provenance hashes in
   `candidate.json`;
4. reruns the complete package-evidence verifier; and
5. archives the verified package set with the endurance, performance, website
   and approval evidence.

Changing a package, version, dependency, SBOM, provenance file, manifest,
workflow or source commit invalidates the candidate bundle.

## Publication separation

The canonical package job never authenticates to a registry and cannot
publish. Stable publication remains disabled in
`eng/product-families.json`. After the protected V1 candidate gate, each family
still uses its dedicated tag-triggered release path, dependency ordering,
protected `package-production` environment, live governance verification and
registry credentials.

The canonical artifact proves what was built and accepted. It does not prove
that a registry received those exact bytes; registry-side verification remains
part of the publication and post-publication release procedure.
