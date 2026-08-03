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
4. For Streams, also complete
   `streams-release-endurance.yml` at that commit. For Sync, also complete
   `sync-release-endurance.yml` at that commit.
5. Do not change the candidate commit after evidence succeeds. Any source,
   project, dependency, version, workflow, or release-policy change creates a
   new commit and invalidates the evidence.
6. Create the exact version tag declared by the manifest, such as
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
environment. Repository administrators must configure that environment with
required reviewers, restrict deployment branches/tags, and scope
`NUGET_API_KEY` and `NPM_TOKEN` to it. NuGet and npm credentials must not be
available to pull-request or candidate jobs.

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
