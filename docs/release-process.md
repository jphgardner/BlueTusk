# Release process

BlueTusk release publication is fail closed. A successful build or candidate
package is evidence, not permission to publish.

`eng/product-families.json` schema 2 records, for every independently versioned
family:

- whether publication is armed in an immutable candidate;
- whether the release channel is stable or preview;
- the one exact tag prefix;
- cross-family release dependencies; and
- the GitHub Actions workflows that must have succeeded for the exact release
  commit from an approved event.

All six families are release-prepared at stable `1.0.0` without prerelease
suffixes. During preparation every publication policy remains disabled. In the
final immutable candidate, `enabled=true` means the family is armed; it does
not publish a package. Exact release tags and protected `package-production`
approval remain the publication boundary.

## RC application train

`eng/prerelease-train.json` defines the separate immutable `1.0.0-rc.1` train
for all six families in stable dependency order. RC packing uses version
overrides and temporary npm artifact copies; it does not rewrite the stable
family manifests. Every internal NuGet/npm dependency must resolve to the same
exact RC version. Exact `*-v1.0.0-rc.1` tags publish through the protected
`package-prerelease` environment, and npm packages use the `rc` dist-tag,
never `latest`. A correction is `rc.2`; an RC is never overwritten.

Before registry publication, the package-consumer solution restores through
`eng/nuget/applications-candidate.config` into an isolated cache. Its source
mapping resolves `BlueTusk.*` only from `artifacts/prerelease/feed`, which is
rebuilt from the current candidate; it cannot silently consume an older
same-version package from a machine cache or registry. Deployable application
images reverse that trust boundary: their workflow first verifies the complete
public RC inventory, then restores from the public registries and records image
digests, scans and attestations.

RC tags, packages, images, deployments, and observations cannot satisfy a
stable exact-SHA gate. The three package-consumer applications and the
staging/production boundary are documented in the
[V1 application suite](v1-applications.md).

## Candidate sequence

1. Finish code, documentation, API/format freezes, upgrades, package inspection,
   security audit, live matrices, and performance gates.
2. After PostgreSQL 19 GA, merge a reviewed final arming PR to `main`. Its
   resulting SHA must contain exactly six stable `1.0.0` families, all armed
   in dependency order, with no V1 release tags and no candidate packages
   published. That reviewed `main` SHA is the immutable candidate.
3. Dispatch `build.yml` explicitly at that exact commit. A normal pull-request
   or branch-push run is not release evidence because the manual run includes
   the elevated PostgreSQL, connector, authentication, stress, and endurance
   jobs.
4. Dispatch `security.yml` at that exact commit and require the manual CodeQL
   run to succeed. Dispatch `fuzzing.yml` at the same commit for its enforced
   minimum of one hour per target; every target must finish without a crash or
   hang finding.
5. Dispatch `performance.yml` at that exact commit on the labelled reference
   runner and retain its integrity-bound complete benchmark evidence.
6. Complete `streams-release-endurance.yml`,
   `sync-release-endurance.yml`, and
   `continuous-graph-release-endurance.yml` at that commit. The three windows
   are 72, 24, and 24 hours respectively. ContinuousGraph additionally requires
   at least 100,000 evaluations, 99.9% committed outcomes, P95 lifecycle at or
   below one second, repair/restart/cancellation/disconnect evidence, and no
   ordering or reconciliation errors.
7. Complete the external acceptance records and run
   the protected `v1-candidate-readiness.yml` aggregation workflow as described
   in [V1 production readiness](operations/production-readiness.md). Every
   family release requires this successful workflow at the exact commit.
8. Do not change the candidate commit after evidence succeeds. Any source,
   project, dependency, version, workflow, or release-policy change creates a
   new commit and invalidates the evidence.
9. Create the exact tags sequentially on the verified commit:
   `provider-v1.0.0`, `streams-v1.0.0`, `sync-v1.0.0`,
   `live-v1.0.0`, `control-plane-v1.0.0`, then
   `continuous-graph-v1.0.0`. After every tag, verify registry availability,
   hashes, provenance, installation, and dependency resolution before creating
   the next tag.

The release workflow rejects a tag that differs from the version property,
rejects a dirty or different checkout, and queries GitHub Actions for every
required successful workflow run whose `head_sha` is exactly the tagged commit.
It does not accept a run for an ancestor, a rebuilt binary from another tree, a
pull-request event, or a merely uploaded report. Before accepting a dependent
family, it also verifies that every exact dependency package version is already
available from NuGet, including all npm clients owned by a dependency family.
Arming all families in the candidate therefore cannot bypass the tag and
protected-environment publication order.

## Publication boundary

A manual dispatch of `release-product-family.yml` can only produce gated
candidate artifacts. It cannot publish. Publication occurs only from an exact
matching tag after `verify-release-gates.ps1` succeeds.

The publish job downloads the artifact created by the verified job, records a
GitHub build-provenance attestation, and runs in the `package-production`
environment. That environment is configured with prevent-self-review and the
six allowed release-tag patterns. Before the first candidate, add another
eligible human reviewer; the repository currently has only its owner, so an
owner-triggered deployment cannot self-approve. Scope `NUGET_API_KEY` and
`NPM_TOKEN` only to `package-production`; they must not be available to
pull-request or candidate jobs. Store a fine-grained
`V1_GOVERNANCE_TOKEN` with Administration read, Actions read, Contents read
and Environments read in both protected environments. It is used only for the
fail-closed live settings and required-secret-name check; no workflow uses it
to read secret values or change repository settings.

The exact live settings are not informal setup advice. They are declared in
`eng/v1-github-governance.json` and verified through the GitHub API by
`verify-github-governance.ps1 -Mode Remote`. The same contract requires the
`main` ruleset, all 35 V1 status checks, fresh independent review after the
last push, resolved review threads, the protected
`v1-candidate-readiness` environment, and the six allowed production tag
patterns. It also requires the dependency graph, vulnerability alerts,
automated security fixes and private vulnerability reporting. A missing or
unprotected environment, or a disabled repository security feature, makes both
candidate acceptance and tagged publication fail.

The workflow packages only the selected family, and each package project is
listed explicitly in the manifest. Candidate package creation uses
`pack-product-family.ps1 -Candidate`; normal packaging refuses every disabled
family. Before upload, `verify-product-family-packages.ps1` requires the exact
NuGet, symbol, and npm archive set; safe archive paths; MIT metadata; repository
commit provenance; correct internal dependency versions; portable PDBs; and
compiled npm distributions without install lifecycle scripts. Duplicate NuGet
publication is a release failure, and npm publication uses registry provenance.
Dependency order is Provider, Streams, Sync, Live, Control Plane, then
ContinuousGraph.

Every external GitHub Action reference is pinned to a full commit.
`security.yml` runs CodeQL and pull-request dependency review, while
`verify-supply-chain.ps1` enforces the pins and exact per-family API budgets.
The release job generates CycloneDX 1.6 and SPDX 2.3 SBOMs, plus a provenance
manifest containing the source commit and SHA-256 of every NuGet, symbols and
npm artifact. It verifies those records before upload and includes the package
set and SBOMs in the GitHub build-provenance attestation.

An independent reviewer completes the
[release review handoff](release-review-handoff.md) for the exact candidate
before an administrator enables publication. The
[V1 release-readiness record](v1-release-readiness.md) separates implemented
hardening from the remaining exact-candidate and external evidence.
