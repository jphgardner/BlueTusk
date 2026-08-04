# Release process

BlueTusk release publication is fail closed. A successful build or candidate
package is evidence, not permission to publish.

`eng/product-families.json` schema 2 records, for every independently versioned
family:

- whether publication is enabled;
- whether the release channel is stable or preview;
- the one exact tag prefix;
- cross-family release dependencies; and
- the GitHub Actions workflows that must have succeeded for the exact release
  commit from an approved event.

Provider, Streams, Sync, Live, and Control Plane are locked to stable
1.0.0-or-newer versions without a prerelease suffix. Continuous Graph is locked
to a prerelease version. All publication switches remain disabled until their
documented V1 gates are complete.

## Candidate sequence

1. Finish code, documentation, API/format freezes, upgrades, package inspection,
   security audit, live matrices, and performance gates.
2. Set the intended family version and enable its publication policy in the
   same clean candidate commit. Every declared release dependency must already
   be enabled and available at the version referenced by the candidate.
3. Dispatch `build.yml` explicitly at that exact commit. A normal pull-request
   or branch-push run is not release evidence because the manual run includes
   the elevated PostgreSQL, connector, authentication, stress, and endurance
   jobs.
4. Dispatch `performance.yml` at that exact commit on the labelled reference
   runner and retain its integrity-bound complete benchmark evidence.
5. For Streams, also complete
   `streams-release-endurance.yml` at that commit. For Sync, also complete
   `sync-release-endurance.yml` at that commit.
6. Complete the external acceptance records and run
   the protected `v1-candidate-readiness.yml` aggregation workflow as described
   in [V1 production readiness](operations/production-readiness.md). Every
   family release requires this successful workflow at the exact commit.
7. Do not change the candidate commit after evidence succeeds. Any source,
   project, dependency, version, workflow, or release-policy change creates a
   new commit and invalidates the evidence.
8. Create the exact version tag declared by the manifest, such as
   `provider-v1.0.0`, on the verified commit.

The release workflow rejects a tag that differs from the version property,
rejects a dirty or different checkout, and queries GitHub Actions for every
required successful workflow run whose `head_sha` is exactly the tagged commit.
It does not accept a run for an ancestor, a rebuilt binary from another tree, a
pull-request event, or a merely uploaded report. Before accepting a dependent
family, it also verifies that every exact dependency package version is already
available from NuGet, including all npm clients owned by a dependency family.
Enabling several family switches in one commit therefore cannot bypass the
publication order.

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
Dependency order is Provider, Streams, Sync/Live, Control Plane, then Continuous
Graph preview.

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
