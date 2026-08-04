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
| Package artifact SHA-256 values | See `build-provenance.json` |
| CycloneDX 1.6 SHA-256 | |
| SPDX 2.3 SHA-256 | |
| GitHub attestation URL | |

## Required evidence

- [ ] Release dependencies are published at the exact versions declared by the
      candidate.
- [ ] The full manual `build.yml` run passed at the candidate commit.
- [ ] The manual `security.yml` CodeQL run passed at the candidate commit.
- [ ] Every target in the manual `fuzzing.yml` run completed for at least one
      hour without a crash or hang finding at the candidate commit.
- [ ] The [fuzz-finding review handoff](operations/fuzz-finding-handoff.md) is
      closed by an independent security reviewer.
- [ ] All workflow actions are pinned to full commits.
- [ ] CodeQL, dependency review and the complete NuGet vulnerability audit
      passed.
- [ ] Public API budgets and the applicable API freeze passed.
- [ ] Package-content verification, CycloneDX, SPDX, provenance hashes and
      GitHub build attestation agree.
- [ ] The PostgreSQL 15–19 matrix and the required PostgreSQL 19 milestone
      evidence passed.
- [ ] Streams has an exact 72-hour report when it is in the dependency chain.
- [ ] Sync has an exact 24-hour report when it is in the dependency chain.
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

Publication remains disabled until this record and every applicable evidence
item are complete for the same candidate commit.
