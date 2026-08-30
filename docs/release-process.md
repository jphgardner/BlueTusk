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

All six families are published at stable `1.0.0` without prerelease suffixes;
the [V1 publication record](releases/1.0.0-publication-record.md) documents the
owner exception used for that release. During preparation a publication policy
may be disabled. In a final immutable candidate, `enabled=true` means the family is armed; it does
not publish a package. Exact release tags and protected `package-production`
approval remain the publication boundary.

## Current package RC train

`eng/package-prerelease-train.json` defines the prepared immutable
`1.2.0-rc.1` package train for all six families in stable dependency order.
This train is not public until the reviewed commit reaches `main` and the six
exact RC tags publish successfully. RC packing uses version
overrides and temporary npm artifact copies; it does not rewrite the stable
family manifests. Every internal NuGet/npm dependency must resolve to the same
exact RC version. Exact `*-v1.2.0-rc.1` tags publish through the protected
`package-prerelease` environment, and npm packages use the `rc` dist-tag,
never `latest`. A correction is `rc.2`; an RC is never overwritten.

The complete `1.1.0-rc.1` train was published on 2026-08-29 from commit
`2e735ed46aec11d5009158a00ca7b862f9ec12af`. All 62 NuGet and three npm
packages passed public-availability and clean-consumer verification. The
[release record](releases/1.1.0-rc.1.md) is the human-readable authority. This
does not arm or authorize stable `1.1.0`.

The separate `eng/prerelease-train.json` manifest places the production
application source and image workflow on the prepared exact `1.2.0-rc.1`
train. New image evidence must be generated after that train is public and from
the reviewed commit; it cannot reuse the historical 1.0 or 1.1 image manifest.
Earlier application observations remain historical evidence and are not
rewritten.

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
   resulting SHA must contain exactly six stable `1.2.0` families, all armed
   in dependency order, with no stable 1.2 release tags or stable 1.2 packages
   published. The manifest-bound public RC remains separate evidence and
   cannot satisfy the stable gate. That reviewed `main` SHA is the immutable
   candidate.
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
   `sync-release-endurance.yml`,
   `live-control-plane-release-endurance.yml`, and
   `continuous-graph-release-endurance.yml` at that commit. The four windows
   are 72, 24, 24, and 24 hours respectively. ContinuousGraph additionally requires
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
   `provider-v1.2.0`, `streams-v1.2.0`, `sync-v1.2.0`,
   `live-v1.2.0`, `control-plane-v1.2.0`, then
   `continuous-graph-v1.2.0`. After every tag, verify registry availability,
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
owner-triggered deployment cannot self-approve. Configure NuGet trusted-
publishing policies for `release-product-family.yml`, restricted to the
`package-production` and `package-prerelease` environments and the
`BlueTusk.*` package-ID glob. The publish jobs exchange their GitHub OIDC tokens
for one-hour NuGet API keys immediately before publishing; no long-lived NuGet
credential is stored in GitHub. Scope `NPM_TOKEN` only to the matching protected
environment; it must not be available to pull-request or candidate jobs. Store
a fine-grained `V1_GOVERNANCE_TOKEN` with Administration read, Actions read,
Contents read and Environments read in both protected environments. It is used
only for the fail-closed live settings and required-secret-name check; no
workflow uses it to read secret values or change repository settings.

The exact live settings are not informal setup advice. They are declared in
`eng/v1-github-governance.json` and verified through the GitHub API by
`verify-github-governance.ps1 -Mode Remote`. The same contract requires the
`main` ruleset, all 35 V1 status checks, fresh independent review after the
last push, resolved review threads, the protected
`v1-candidate-readiness` environment with administrator bypass disabled, and
the six allowed production tag patterns. It also requires the dependency
graph, vulnerability alerts, automated security fixes and private vulnerability
reporting. A missing or
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
