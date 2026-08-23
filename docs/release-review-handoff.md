# Independent V1 release review handoff

Use one copy of this record for each exact candidate commit. Reviewers must be
independent of the person who prepared the release. Blank, inherited or
ancestor-commit evidence does not pass.

## Candidate identity

| Field | Value |
| --- | --- |
| Product family | |
| Version and tag | |
| Full source commit | |
| Candidate workflow URL and run ID | |
| Package artifact SHA-256 values | See canonical `package-manifest.json` and `build-provenance.json` |
| CycloneDX 1.6 SHA-256 | |
| SPDX 2.3 SHA-256 | |
| GitHub attestation URL | |

## Required evidence

- [ ] Release dependencies are published at the exact versions declared by the
      candidate.
- [ ] The full manual `build.yml` run passed at the candidate commit.
- [ ] The build retained exactly one `v1-candidate-packages-<sha>` artifact;
      its six family inventories, package contents, SBOMs, provenance and
      manifest pass `verify-v1-package-evidence.ps1`.
- [ ] The archived Angular `production-metrics.json` passes the checked-in
      startup, lazy-chunk and complete-distribution budgets, and pilot evidence
      records field LCP, INP and CLS for the selected production host.
- [ ] `website-deployment-acceptance.json` records TLS, SPA fallback, cache and
      security-header policy, broken-link crawl, supported browsers and field
      Core Web Vitals for the exact archived website artifact.
- [ ] All ten schema-4 operational approval files pass
      `verify-v1-approval-evidence.ps1`; generic narrative approvals, unknown
      fields, measurements outside budget and approvals older than the
      latest exact workflow are rejected. Independent review follows all
      operational approvals, and maintainer sign-off is last.
- [ ] The manual `security.yml` CodeQL run passed at the candidate commit.
- [ ] Every target in the manual `fuzzing.yml` run completed for at least one
      hour without a crash or hang finding at the candidate commit.
- [ ] The [fuzz-finding review handoff](operations/fuzz-finding-handoff.md) is
      closed by an independent security reviewer.
- [ ] All workflow actions are pinned to full commits.
- [ ] CodeQL, dependency review and the complete NuGet vulnerability audit
      passed.
- [ ] Public API budgets and the applicable API freeze passed.
- [ ] Package-content verification, exact byte lengths and SHA-256 values,
      CycloneDX, SPDX, provenance hashes and dependency versions are complete
      for all six families from the same build run.
- [ ] The exact application-image workflow proves both .NET 10.0.11 shared
      frameworks are present in every API and worker image before scanning and
      attestation; all nine deployable image digests are bound to this commit.
- [ ] `verify-application-platform-health.ps1 -RequireApplications` passes on
      the selected deployment: API-assigned pods match the Ready kubelets,
      nodes report no pressure, Longhorn and CloudNativePG are healthy, all nine
      deployments are converged, every active container is ready, and no
      migration job is failed.
- [ ] The PostgreSQL 15–19 matrix and the required PostgreSQL 19 milestone
      evidence passed.
- [ ] Streams has an exact 72-hour report when it is in the dependency chain.
- [ ] Sync has an exact 24-hour report when it is in the dependency chain.
- [ ] Both exact endurance windows contain process death, network interruption,
      controlled storage exhaustion, credential rotation, primary failover,
      backward/forward clock movement and a same-major PostgreSQL minor
      upgrade: 14 passed scenarios, 28 matching observation hashes, no blocker
      and no observed data loss.
- [ ] Known limitations, migrations, operational rollback and security
      assumptions are documented.

## Review decision

| Field | Value |
| --- | --- |
| Reviewer | |
| Organisation/team | |
| Review completed (UTC) | |
| Decision (`approve` or `reject`) | |
| Findings and accepted risk references | |
| Reviewer signature or approval URL | |

Under the standard policy, publication remains blocked until this record and
every applicable evidence item are complete for the same candidate commit.
Version `1.0.0` was published under the separate, explicit repository-owner
exception recorded in the
[V1 publication record](releases/1.0.0-publication-record.md); this handoff was
not retroactively completed.
