# Expansion failover qualification

Each expansion family has a manual exact-candidate failover workflow named in
[`eng/expansion-release-policy.json`](../../eng/expansion-release-policy.json).
This page covers the shared gate used by Projections, Documents and Workflows.
Jobs keeps its own [physical promotion gate](../jobs/failover.md). Adding a
workflow records no passing run. Only a successful `workflow_dispatch` run at
the candidate SHA, with its retained `expansion-<family>-failover-<sha>`
artifact, can count as release evidence.

## Gate shape

Every workflow runs on `[self-hosted, windows, x64, bluetusk-benchmark]` and
shares the `bluetusk-reference-host` concurrency group. It takes the full
candidate SHA and an exact typed confirmation, checks out that commit, and runs
[`eng/verify-expansion-release-failover.ps1`](../../eng/verify-expansion-release-failover.ps1)
in three modes:

- `Preflight` requires the clean exact candidate and a fresh evidence directory.
- `Run` executes [`eng/run-expansion-failover.ps1`](../../eng/run-expansion-failover.ps1).
  That script provisions three fresh PostgreSQL 18 synchronous primary/standby
  pairs, one per repetition, from the digest-pinned image. Each pair has its own
  labelled network and volumes, `fsync=on`, `full_page_writes=on`,
  `synchronous_commit=remote_apply` and a verified streaming synchronous
  standby. The script runs the family's steps in policy order, captures the
  candidate source before and after, and hashes every step binary before and
  after each step. Run mode then archives the binaries and writes a file-hash
  manifest.
- `Verify` re-checks the archived artifact offline. Readiness uses the same
  mode through `Get-ExpansionRoleVerifier`.

[`eng/verify-expansion-failover-report.ps1`](../../eng/verify-expansion-failover-report.ps1)
judges the evidence against
[`eng/expansion-failover-policy.json`](../../eng/expansion-failover-policy.json).
It requires the exact clean candidate, three distinct fresh fixtures and
PostgreSQL systems, the pinned image, synchronous settings, and unchanged
binaries. Every scenario must run with its exact acknowledged work and without
lost or duplicated effects. The verifier also checks atomic in-flight work,
stale-owner rejection with a newer fence where the product has an owner, tenant
isolation, the expected server timeline, and recovery inside the
pre-registered ceilings. Each harness scenario lists the assertions it ran;
the verifier requires exactly the policy's assertion set, so a scenario cannot
silently drop one.

## Disturbances

The cross-family harness `benchmarks/BlueTusk.Ecosystem.FailoverHarness`
blocks a real product operation inside its open transaction at a PostgreSQL
advisory-lock barrier. It then applies one of four faults:

| Scenario | Fault | Recovery path |
| --- | --- | --- |
| `backend-termination` | `pg_terminate_backend` of the blocked session | New connection on the same server |
| `host-process-kill` | Operating-system kill of a child process running product code | Replacement owner, after lease expiry where the product leases work |
| `primary-crash-restart` | `SIGKILL` of the primary, then restart of the same server | WAL crash recovery, standby reattachment |
| `synchronous-standby-promotion` | `SIGKILL` of the primary, then `pg_ctl promote` of the standby | Public provider multihost route to the promoted server |

The product sources use the public multihost route: both endpoints, a
read-write target and a one-second connect timeout. They are never re-pointed.
Promotion runs last because it permanently removes the original primary.

## Families

**Projections** applies source WAL exactly once, using the durable checkpoint
in the same transaction as the derived model. Three harness scenarios
interrupt a worker inside an apply that has already staged an additive
aggregate delta. Each checks that:

- the partial apply rolled back;
- the old lease can no longer renew;
- a newer fence resumes from the checkpoint;
- every tenant's count and total equal the source exactly.

A lost apply would lower the count and a repeated one would raise it.
Promotion reuses the dedicated
[`BlueTusk.Projections.PhysicalRecoveryTests`](../projections/RECOVERY.md)
rehearsal. A promotion changes the timeline, so recovery there is an explicit
operator rebuild rather than transparent resumption.

**Documents** interrupts a two-document save after its first insert. Each
scenario checks that:

- every acknowledged document survives with its exact value;
- the interrupted save is all-or-nothing;
- no document is lost, duplicated or partial;
- tenants stay isolated;
- a stale pre-fault revision cannot overwrite the compare-and-swap winner.

**Workflows** runs two existing harnesses:

- the Workflows load harness `faults` profile: real worker process death, a
  dropped commit acknowledgement, and network partitions;
- the Jobs/Workflows physical promotion rehearsal.

The verifier judges only the Workflows evidence of the promotion: twelve
singular workflow effects, four fenced activity leases with stale completion
and effect rejection, six matching replays, timers, signals, compensation and
tenant isolation.

## Recovery ceilings

No expansion family documents a recovery-time objective. All ceilings below
were fixed from documented constants before any failover run, and none was
adjusted to an observed result:

- **Fault to fully verified recovery: 180 s.** This is the Jobs failover
  policy's ceiling, which is the 180-second work deadline of the Jobs/Workflows
  promotion rehearsal on the same image, pair and multihost route.
- **First success after a backend termination or host-process kill: 45 s.**
  This is the Jobs rehearsal's drain-phase deadline. Neither fault makes the
  database unavailable; leased products wait out a five-second lease.
- **First success after a crash/restart or promotion: 60 s.** Readiness takes
  30 s (the runner startup deadline, or `pg_ctl promote -t 30`), the standby
  reconnect retry 5 s, the provider failed-host recheck 10 s and the connect
  timeout 1 s. That totals 46 s, rounded up to the next whole minute.
- **Projections promotion: 120 s.** The rehearsal keeps its own tighter work
  deadline.

The core-family production RTOs in `eng/v1-production-slos.json` (15 to 60
minutes) are far looser and are not used.

## Not qualified

Each family lists its untested disturbances in the policy. These include
asynchronous-replication loss, split brain, old-primary rejoin, persistent
storage loss and independent-host fleets. One local host proves none of those.
`ProductionQualified` stays false in every report.

## Local diagnostic runs

Run from a clean committed checkout, with no host measurement lock present:

```powershell
$sha = git rev-parse HEAD
./eng/verify-expansion-release-failover.ps1 -Family Projections -Mode Run -ExpectedCommit $sha `
    -EvidenceRoot artifacts/projections-release-failover/diagnostic -Owner <local-owner-label>
./eng/verify-expansion-release-failover.ps1 -Family Projections -Mode Verify -ExpectedCommit $sha `
    -EvidenceRoot artifacts/projections-release-failover/diagnostic
```

A local pass is diagnostic only. It is not a workflow artifact and does not
qualify a release.
