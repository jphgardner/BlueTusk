# Core recovery rehearsals

The 1.1.0 Core release contract keeps two operational rehearsals as required
gates: backup/restore and rollback (`backupRestoreRehearsal` and
`rollbackRehearsal` in `eng/v1.1-release-contract.json`). Independent pilots are
not a 1.1.0 gate; see [approval evidence](approval-evidence.md). This page shows
how to run both rehearsals for real against the exact candidate, what they
produce, and how the result is checked.

A rehearsal is evidence for the approval record, not the approval. The
accountable approver still signs `backup-restore-rehearsal.json` and
`rollback-rehearsal.json`, citing the retained evidence.

## What runs

`eng/run-core-recovery-rehearsal.ps1` drives a probe application,
`eng/CoreRecoveryProbe`, that uses Core durable state in one PostgreSQL
database:

- Provider: an `orders` table written one acknowledged row at a time;
- Streams: a PostgreSQL checkpoint store with a fenced lease;
- Live: the PostgreSQL replay store, a shared subscription and signed resume
  tokens;
- Control Plane: managed desired state with fenced reconciliation leases;
- Sync: a PostgreSQL destination checkpoint and idempotent redelivery.

Each probe phase is a separate process. It reads back everything the previous
phase acknowledged, proves the previous owner is fenced, then writes more. Every
value in a phase report is read from PostgreSQL.

The probe is restored from exact packages only. The candidate build uses the
1.1.0 packages from `build-v1-candidate-packages.ps1`; the runner checks that
every resolved `BlueTusk.*` package has the same SHA-512 as the verified
candidate nupkg. The rollback build uses the published 1.0.0 packages from
nuget.org. Each build has its own package folder, so a cached package cannot
stand in.

PostgreSQL is the digest-pinned Core image from
`eng/v1.1-candidate-readiness.json` (`endurancePostgreSqlImage`). Containers
are labelled `bluetusk.owner` and `bluetusk.run`. The runner removes only its
own containers.

### Backup and restore

1. The candidate seeds a fresh server and runs a second workload.
2. The runner records an inventory of every user table (row count and an MD5
   of the ordered row text), plus the count of user objects.
3. `pg_dump -Fc` takes a consistent backup. It is encrypted with AES-256-GCM
   using a key that exists only in memory for the run, and the plaintext is
   deleted.
4. A second, fresh server is checked empty. The timer starts, the backup is
   decrypted and restored with `pg_restore --exit-on-error`, and the restored
   inventory is taken.
5. The candidate reconciles the restored database: orders, checkpoints, Live
   replay integrity, a client resume or explicit reset, Control Plane fencing,
   and Sync idempotency. Then it writes again. The timer stops.

The declared objectives come from the `streams-relay` recovery objective in
`eng/v1-production-slos.json`: an RPO of zero acknowledged transactions and an
RTO of 30 minutes. The recovery-point gap is measured on the source clock: it
is zero when the restore contains the last acknowledged write.

### Rollback

1. The published 1.0.0 release seeds the database (production before the
   upgrade).
2. The 1.1.0 candidate takes over, reconciles and writes, then drains: it
   releases its leases and stops.
3. Trigger: the candidate is withdrawn. The runner confirms that no candidate
   session remains in `pg_stat_activity`.
4. The 1.0.0 release takes over the same database. It must open every store,
   read everything 1.1.0 wrote, take checkpoint and reconciliation ownership
   with a higher fencing token, reject the candidate's stale leases, restart
   the Live shared subscription, serve a client holding a 1.1.0 resume token,
   and write again.

The rollback duration is measured from the trigger to the end of that takeover.

## Run it

Use a clean checkout of the exact candidate commit, Docker, and the .NET SDK
from `global.json`. Do not run a rehearsal while the reference host is taking
measurements.

```powershell
$commit = (git rev-parse HEAD)
./eng/build-v1-candidate-packages.ps1 -ReleaseTrack Core `
    -OutputRoot "artifacts/core-candidate-packages-$commit" -Commit $commit

./eng/run-core-recovery-rehearsal.ps1 -Rehearsal BackupRestore `
    -ExpectedCommit $commit `
    -CandidatePackageRoot "artifacts/core-candidate-packages-$commit" `
    -EvidenceRoot "artifacts/core-rehearsal-backup-restore-$commit" `
    -Operator 'Name of the person running it'

./eng/run-core-recovery-rehearsal.ps1 -Rehearsal Rollback `
    -ExpectedCommit $commit `
    -CandidatePackageRoot "artifacts/core-candidate-packages-$commit" `
    -EvidenceRoot "artifacts/core-rehearsal-rollback-$commit" `
    -Operator 'Name of the person running it' `
    -DecisionAuthority 'Name of the person who may order a rollback'
```

Each run needs network access to nuget.org for the 1.0.0 packages and
third-party dependencies. Every evidence directory must be new and below
`artifacts/`.

## Evidence

Each evidence directory contains:

| File | Content |
| --- | --- |
| `rehearsal-report.json` | Commit, operator, image, package identities and SHA-512 hashes, probe source hashes, phase list, measurements and the result |
| `phases/*.json` | One report per probe phase: version, checks, observations and failures |
| `backup/rehearsal.dump.enc` | Backup/restore only: the encrypted backup that was restored |
| `approval-details.json` | Only when the rehearsal passed: the measured `details` object for the approval record |

A failed rehearsal keeps its report and phase reports, and writes no
`approval-details.json`.

## Verify it

The runner verifies its own output on success. Anyone can check it again from
the candidate checkout:

```powershell
./eng/verify-core-recovery-rehearsal.ps1 -Rehearsal BackupRestore `
    -EvidenceRoot "artifacts/core-rehearsal-backup-restore-$commit" -ExpectedCommit $commit
./eng/verify-core-recovery-rehearsal.ps1 -Rehearsal Rollback `
    -EvidenceRoot "artifacts/core-rehearsal-rollback-$commit" -ExpectedCommit $commit
```

The verifier rejects the evidence if any of these is not true:

- the commit and image are the expected ones;
- the package versions are exact and not mixed;
- the probe source matches the checkout;
- every phase report is unaltered and passed;
- each approval detail matches what the probe observed;
- the RPO and RTO were met;
- the details pass the `backup-restore-rehearsal` or `rollback-rehearsal`
  schema in `eng/v1-approval-evidence-contract.json`.

`eng/test-core-recovery-rehearsal.ps1` exercises the verifier and the runner's
preflight with synthetic fixtures in CI. Synthetic fixtures are never evidence.

## From evidence to approval

Archive the evidence directory where the protected reviewer can reach it over
HTTPS. Then create the approval record from `approval-details.json`, using the
common envelope in [approval evidence](approval-evidence.md): the candidate
commit, `approved` outcome, accountable approver, UTC time, summary, zero
blocking findings, and the archive URL as a reference. Validate it:

```powershell
./eng/verify-v1-approval-evidence.ps1 -ReleaseTrack Core `
    -EvidencePath approvals/backup-restore-rehearsal.json `
    -ExpectedGateId backup-restore-rehearsal -ExpectedCommit $commit
```

Run the rehearsals after the exact-candidate workflows complete. Approval
records must not predate the latest workflow completion.
