# Endurance disturbance evidence

V1 requires more than a long-running green test process. The exact 72-hour
Streams run and exact 24-hour Sync run must each survive the seven operational
disturbances in `eng/v1-endurance-disturbance-contract.json`. That produces 14
separately observed recoveries for one immutable candidate.

The native endurance reports remain responsible for duration, workload,
package provenance, runtime, operating system, container images, source
cleanliness and test-artifact integrity. The operational-disturbance report
adds contemporaneous injection, detection, recovery and continuity evidence
inside those exact report windows. Neither layer can substitute for the other.

## Required matrix

| Scenario | Required observation |
| --- | --- |
| Process death | Ungraceful termination, different before/after process identities, durable restart, duplicate-safe progress and no loss |
| Network interruption | Measured non-zero outage, detection, bounded reconnection, at least one recovered connection and resumed progress |
| Storage exhaustion | Controlled hard limit at 95% or greater utilisation, explicit exhaustion signal, fail-closed behaviour, recovered free capacity and resumed progress |
| Credential rotation | Non-secret old/new credential versions, retired credential rejection, replacement credential acceptance and resumed progress |
| Primary failover | Different old/new primary identities, role-compatible routing, source-identity continuity, checkpoint continuity and resumed progress |
| Clock movement | Both backward and forward movement through an injected `TimeProvider` or isolated runtime clock, followed by expiry, lease, retention, ordering and UTC-report checks |
| PostgreSQL minor upgrade | Two distinct digest-pinned images, a higher minor release of the same supported major, state and checkpoint continuity, capability rediscovery and lag recovery |

Run every row once during Streams and once during Sync. A unit test, smoke run,
earlier commit, different package set, event outside the recorded endurance
window, or scenario performed against an idle component is not release
evidence.

Clock movement must not mutate the self-hosted runner's global clock. Use the
product's injected time boundary or an isolated runtime/container clock.
Storage exhaustion must use a bounded disposable volume, quota or product hard
limit; do not fill the runner's host filesystem. A PostgreSQL minor-upgrade
scenario may use any supported major for which two real minor releases exist.
It must not pretend that beta-to-RC or cross-major migration is a minor upgrade.

## Evidence directory

The protected evidence ZIP contains the ten approval files plus the
operational records:

```text
approvals/
  independent-release-review.json
  ...
  maintainer-signoff.json
disturbances/
  operational-disturbance-evidence.json
  streams/
    process-death-injection.json
    process-death-recovery.json
    ...
    postgresql-minor-upgrade-recovery.json
  sync/
    process-death-injection.json
    process-death-recovery.json
    ...
    postgresql-minor-upgrade-recovery.json
```

Start from `eng/v1-operational-disturbance-evidence.example.json`. Keep the
actual compact evidence below the protected-secret size limit: store the
content-addressed observation summaries in the ZIP and link their `references`
to the retained full runner logs, dashboards, incident/change records and
database evidence.

Each scenario has exactly two content-addressed summaries:

- `injection` records target identity, UTC timestamp, technique, before state,
  expected failure signal and the retained raw-log reference;
- `recovery` records after identity, UTC timestamp, recovery action, progress
  probe, continuity/data-loss result, measurements and the retained raw-log
  reference.

The summaries may use JSON, plain text or another declared media type. They
must be non-empty, unique to the scenario and match the SHA-256 recorded in
the disturbance report. Never include passwords, access tokens, connection
strings with credentials, private keys or raw customer data. Credential
evidence uses version identifiers or secret-manager revision IDs only.

## Timing and candidate binding

For each run:

1. Freeze the candidate commit and build the canonical package artifact.
2. Start the confirmed endurance workflow.
3. Record the native endurance `startedAt` value.
4. Inject each disturbance against an active production-like path.
5. Capture the detection signal before recovery.
6. Restore service and prove progress, identity/checkpoint continuity,
   duplicate handling and zero observed loss.
7. Keep each scenario's `startedAt` and `completedAt` inside that workflow's
   native endurance report window.
8. After both workflows finish, set the disturbance report's exact package
   manifest/provenance hashes and exact Streams/Sync report hashes.
9. Have the named accountable operator review all 14 scenarios after the final
   recovery, with zero blocking findings.

The protected candidate workflow hashes
`disturbances/operational-disturbance-evidence.json` into `candidate.json`.
The report in turn hashes all 28 injection/recovery summaries and the exact
package plus endurance reports. This produces a closed content-addressed
chain; editing any observation invalidates candidate readiness.

## Verification

The protected workflow invokes the strict reader automatically. For an
offline reconstruction, run:

```powershell
./eng/verify-endurance-disturbance-evidence.ps1 `
  -EvidencePath 'D:/release-evidence/v1-evidence/disturbances/operational-disturbance-evidence.json' `
  -EvidenceRoot 'D:/release-evidence/v1-evidence' `
  -ExpectedCommit '<40-character-candidate-sha>' `
  -ExpectedPackageManifestSha256 '<64-character-sha256>' `
  -ExpectedPackageProvenanceSha256 '<64-character-sha256>' `
  -StreamsReportPath 'D:/release-evidence/v1-evidence/streams/report.json' `
  -ExpectedStreamsReportSha256 '<64-character-sha256>' `
  -SyncReportPath 'D:/release-evidence/v1-evidence/sync/report.json' `
  -ExpectedSyncReportSha256 '<64-character-sha256>'
```

The verifier rejects missing or duplicate scenarios, failed assertions,
future/non-UTC timestamps, observations outside a run window, reused or
modified artifacts, unpinned upgrade images, cross-major “minor” upgrades,
unsafe/unsupported clock sources, inadequate storage pressure, data loss,
blocking findings, stale package/report hashes and incomplete review.

Passing the engineering contract check only proves that this fail-closed
mechanism is present and synchronized. It does not claim that any disturbance
has been performed. Only candidate mode with the real, exact files can do that.
