# Jobs and Workflows physical promotion rehearsal

`eng/jobs-physical-recovery.ps1` creates fresh labelled PostgreSQL 18 primary and
physical standby containers, private network and separate data volumes. Default
loopback ports are 55625/55626, independent of the shared 55415–55418 fixtures and
the Projections recovery lab. The image is pinned by digest. It takes a physical
base backup and verifies streaming synchronous replication before running the
dedicated `BlueTusk.Workflows.PhysicalRecoveryTests` project. Three repetitions
each receive a new pair and durable state. Cleanup verifies both owner and
fixture labels before removing any container, network or volume.

```powershell
./eng/jobs-physical-recovery.ps1
# When the exact candidate is already built:
./eng/jobs-physical-recovery.ps1 -NoBuild
```

Each C# rehearsal has a 180-second work deadline and finite admission: two
tenant scopes, 66 Jobs and six workflows. It creates the real fixture explicitly;
missing configuration fails. The project is outside the ordinary `*.Tests.csproj`
selector so a normal PostgreSQL test run cannot silently treat an absent
failover lab as a passing scenario. The wrapper rejects skipped tests, preserves
per-run TRX and PostgreSQL logs under `artifacts/jobs-physical-recovery`, and
retains compact raw JSON plus fixture CPU/memory/I/O samples next to the selected
report. Worker/probe shutdown and schema cleanup have separate bounded deadlines.
Failed runs retain partial logs; they are not passing evidence.

The wrapper captures the global candidate tree with
`eng/capture-ecosystem-source.py` before and after all repetitions and rejects a
changed tree. This covers provider source, shared build/central dependency inputs
and the rest of the candidate, beyond the listed Jobs/Workflows source hashes.
Each trial also captures and compares all executable, DLL, dependency and runtime
configuration files in the dedicated test output before/after execution,
including Data and the lower provider libraries. Generated reports/samples remain
in ignored artifacts until the source comparison passes, then publish to the
selected output. The installed SDK/runtime is identified separately; it is not
hashed by the DLL manifest. An unchanged manifest does not independently prove
that a pre-existing `-NoBuild` output was compiled from those sources. Rebuild the
candidate before freezing it. Use an ignored `-OutputReport` path when this
campaign shares a larger source-freeze gate, then retain the evidence after that gate.

Before failure the primary uses `synchronous_commit=remote_apply` and a verified
synchronous standby. A successful commit then waits for the standby to apply the
transaction, making it visible there; the rehearsal checks acknowledged Jobs,
business admissions and already committed effects by reading that standby.
See [PostgreSQL's synchronous commit contract](https://www.postgresql.org/docs/18/runtime-config-wal.html)
and [physical standby operation](https://www.postgresql.org/docs/18/warm-standby.html).
This configuration is required for this acknowledgement-preservation proof;
asynchronous replication can lose transactions that were acknowledged only by
the failed primary.

The work uses one `BlueTuskDataSource` with the two public host endpoints and
`TargetSessionAttributes=ReadWrite`. Jobs and Workflows continue using that
source, their original store instances and their original workers. The campaign
does not replace the source, clear its pools or route through a custom shim.
`MaximumPoolSize=8` applies **per host**, yielding an aggregate maximum of 16.
The report samples total/busy/waiting connections and checks that aggregate
boundary. Budget all configured hosts and all application sources in a real
deployment, including maintenance and probes.

The fixture stages these recovery boundaries:

- Every Job is enqueued in the same acknowledged transaction as its business
  admission. Both tenants have an active Job whose idempotent fenced ledger
  effect commits before acknowledgement, plus pending and eight-second delayed
  work. Four handlers per tenant are bounded by a pre-promotion gate for ordinary
  work; they cannot commit those effects before promotion. Already claimed
  gated attempts also recover by lease expiry.
- A workflow commits a transactional activity and consumes a signal before
  waiting on an eight-second timer. The activity result and timer dispatch must
  survive without repeating the completed activity.
- A workflow commits an idempotent database effect before its activity result.
  Recovery must use the same idempotency identity, repeat the unacknowledged
  activity, retain one effect row and complete its downstream timer/activity.
- A workflow is canceled after its committed activity and begins compensation.
  Its compensation effect commits before its acknowledgement. Recovery must
  finish compensation with one effect, reach `Canceled`, and reject a late signal.

The wrapper hard-stops only the labelled primary. The test verifies it is no
longer running, waits until the persisted timer/delayed deadlines pass while the
standby remains read-only, then promotes that standby. Promotion never occurs
while the old primary is running. No attempt is made to restart or rejoin the
old primary; that requires separate cluster recovery and fencing work. The
promoted node uses local synchronous commit with no replica. Production
operators must restore their required replication protection separately.

Every interrupted lease must recover on attempt two with a newer fence. The
actual old lease rows are captured from the owned format-one schema, not
invented tokens. Replacement attempt-two handlers remain behind an acknowledgement
barrier while all six old completion and fenced-effect capabilities are checked.
Each corresponding current Job must still be `Running` with a newer fence, and
the stale callback must not be invoked. Every admitted Job must succeed and retain its deduplication
identity; every workflow must retain its start identity, expected final state,
singular completed transitions and matching history replay. Foreign-tenant
reads must return no state. An outage probe must be unhealthy and all scoped
readiness reports must be healthy after dispatch drains.

## Lease and connection-selection budget

The initial one-second lease/100 ms heartbeat policy failed two fresh-pair
exploratory trials; neither is passing evidence. In the diagnostic trial,
post-promotion `job.read` P50/P99 were 1,003/1,214 ms (91 observations), and fenced
effect P50/P99 were 1,015/2,020 ms (32 observations), versus 7/31 ms before failure
(two fenced effects). All six workflow states recovered, but ordinary Jobs
expired repeatedly, four had exhausted attempt five and 58 remained pending at
the snapshot. The first trial overlapped native compilation; the diagnostic
trial ran after that window was released. The retained
[failed-trial record](performance-reports/physical-promotion-short-lease-failure.json),
[state](performance-reports/physical-promotion-short-lease-failure.state.json),
[operation timings](performance-reports/physical-promotion-short-lease-failure.operations.json)
and configuration preserve that failure.

The subsequent exploratory policy uses a **five-second lease**, **500 ms heartbeat**, **one-second
connect timeout**, 100 ms store-failure backoff, four Jobs handlers and three
Workflows handlers per tenant. The eight-second outage outlasts those leases.
The provider in these baseline trials tried ordered endpoints and refreshed host
role on each pool checkout; it had no unavailable-host backoff or configured host-recheck interval.
Repeated attempts against the dead first host therefore add roughly one second
to an acquisition in this lab. Several acquisitions can fall between claim,
effect, heartbeat and acknowledgement. Set leases and heartbeat budgets to cover
that path plus pool waiting and expected database latency; do not select a lease
from the callback time alone. The five-second policy also failed one of its three
strengthened exploratory repetitions below. It is not a supported minimum or an
availability guarantee. Provider changes must repeat this same workload and
retain both the baseline failure and the new binary/source identity.

The drain observer uses bounded scoped counts rather than enumerating the whole
backlog on every poll. Exact Job status, deduplication and foreign-tenant checks
still run for every item afterward, with at most four concurrent verifications.
Per-operation timing samples are capped at 10,000 for each fixed operation/phase
name, and do not retain identifiers, SQL parameters or payloads. The drain phase
has a 45-second observation deadline; the scenario work remains capped at 180
seconds, followed by separately bounded cleanup. A passing condition observed
after its deadline still fails.

Effects use a primary key containing tenant, product, item identity and phase,
and verify the stable idempotency value on retry. The repeated activity and
compensation are intentional at-least-once callbacks. Their one-row database
ledger demonstrates an explicit deduplication contract. It does not establish
exactly-once calls to an external system.

Reports include kill-to-promotion, first durable effect replay, first acknowledged
recovery and drain durations; actual
database system identifier and timeline change; acknowledgements, exact effects,
attempts/fences, callback counts and replay results; sampled pool/process
CPU/RSS/managed/allocation/GC values; WAL positions and database bytes; pinned
image/settings, listed source hashes and all DLL hashes from the dedicated test
output. Process counters are cumulative snapshots and PostgreSQL statistics can
lag. Fixture samples include Docker's raw CPU, memory, network and block-I/O
fields; container-VM block-I/O reporting may be unavailable. These observations
support recovery diagnosis, not a throughput or availability guarantee.

## Retained baseline: two passes, one failure

The [three-pair exploratory record](performance-reports/physical-promotion-pg18-exploratory.json)
retains each passing report, the third failure's scoped state/operation
observations, fixture metadata, raw Docker samples and TRX paths. These fresh
PostgreSQL 18.6 pairs used the same unchanged test/provider binaries. The campaign
overlapped the Documents storage campaign on the shared Windows/Docker host.
Global source and DLL capture began during repetition one, before repetitions
two/three; this is **not a frozen-global-candidate passing campaign**. The full
runnable baseline output and matching manifest are preserved under
`artifacts/jobs-physical-draft/baseline-test-output`.

| Pair | Result | Kill command start → promotion | First replay effect | First acknowledged recovery | Drained | All invariants |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| 1 | Passed | 32.91 s | 33.20 s | 45.43 s | 55.55 s | 157.64 s |
| 2 | Passed | 11.37 s | 11.94 s | 26.94 s | 43.69 s | 104.71 s |
| 3 | Failed | Not retained as a passing duration | — | — | — | 45 s replacement-barrier timeout |

The first two pairs each preserved 66 acknowledged admissions/Jobs, 66 Job ledger
effects and 12 workflow effects. All six captured leases recovered at attempt
two with fence one advancing to two; all old completions/effects were rejected
while replacements were running. Both database system identifiers remained
unchanged through physical promotion, and both timelines advanced from one to
two. Replay, signal/timer deadlines, compensation, foreign-tenant reads and scoped
health checks passed.

In pair three, the two held Jobs and four replay/compensation dispatches lost
replacement leases again. The harness deliberately requires attempt two and
rejects later attempts, yielding retained `handler_failed`,
`workflow_activity_failed` and `compensation_failed` codes at attempt five.
The other 64 Jobs and the two signal/timer workflows completed. The 78 promoted
fenced-effect observations had P50/P95/P99 of 140/4,787/5,054 ms, compared with the
five-second lease. This is a failed policy/workload boundary, not evidence that
ordinary application handlers must reject attempt three. It also shows why
extending the observation deadline would not fix the persisted failed state.

Post-promotion reads in pairs one/two had P50/P99 of 1,003/1,042 ms and 2/2,012 ms.
Deduplication rechecks had P50/P99 of 1,008/30,198 ms and 203/4,674 ms.
These are per-run nearest-rank percentiles with 66 rechecks each, not pooled
percentiles or a service-level objective. The first acknowledged-recovery times
include the deliberate active-owner rejection barrier. Kill-to-promotion also
includes Docker stop/inspection, the timer outage and promotion; the baseline
does not separate those command costs.

From initialized to all-invariants-verified, pairs one/two respectively added
9.31/6.83 seconds of process CPU and 56.06/38.80 MiB of allocated bytes. Sampled
peak RSS was 120.87/117.60 MiB, final managed bytes 17.32/30.48 MiB, and GC deltas
were 4/2 Gen0, 1/1 Gen1 and 0/0 Gen2 collections. Pool total high-water was eight
in both (configured aggregate maximum sixteen); busy/waiting high-water was
7/2 and 8/10. Database bytes grew 488/464 KiB between those phase snapshots.
Raw WAL positions and container samples remain in the report. These include
verification/probing and finite fixture state, and do not establish an allocation
per production Job, retention plateau, fleet pool budget or disk-throughput limit.

## Frozen optimized-provider campaign: three passes

The [full three-pair record](performance-reports/optimized-physical-promotion-pg18.json)
and [compact per-pair summary](performance-reports/optimized-physical-promotion-summary.json)
preserve three fresh PostgreSQL 18.6 synchronous primary/standby fixtures. The
same unchanged C# workload passed one test per pair with no skips. All three
started from the same globally frozen 2,397-file source tree and unchanged test
binary/dependency manifest. The provider DLL SHA-256 was
`4F4C906F0B55086E9A4DCE3050F55E0702A02715F9A2165498BAFD25AEA0DAF1`;
the global source SHA-256 was
`f70f0ce491b552ea03beb3c19eb6d03cde6841c91b088c914c4a3495c2a69602`.
The wrapper verified both tree and binary hashes after execution. Each fixture
had a distinct system identifier and advanced timeline 1 to 2. The three owned
sets of containers, network and volumes were removed and verified absent after
the campaign. Per-run TRX, PostgreSQL logs, Docker samples and manifests remain
under `artifacts/jobs-physical-recovery` on the measuring host; this checked-in
record retains the exact invariant counts and paths to those raw files.

| Pair | Kill start to promotion | First replay effect | First acknowledgement | Drained | All invariants | Promoted Job read P99 | Dedup recheck P99 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 9.06 s | 8.96 s | 9.31 s | 9.52 s | 11.00 s | 8.11 ms | 5.85 ms |
| 2 | 8.26 s | 8.09 s | 8.34 s | 8.54 s | 10.02 s | 6.77 ms | 5.47 ms |
| 3 | 9.02 s | 8.91 s | 9.10 s | 9.31 s | 10.82 s | 8.35 ms | 5.44 ms |

Each pair retained 66 acknowledged transactional admissions and 66 Job effects,
plus 12 workflow effects. Six actual persisted old lease capabilities were
rejected while replacement attempt-two/fence-two work was running; all Jobs
then succeeded and all workflow replay, signal, timer, compensation, dedup and
tenant/health assertions passed. Pool total high-water stayed at eight of the
configured sixteen. CPU from initialized to final verification was
2.39/1.95/2.08 seconds, process allocations 17.20/16.62/16.65 MiB, sampled
peak RSS 105.64/108.14/105.80 MiB, and database size grew 472/488/488 KiB.
These include fixture verification and are local observations, not steady-state
resource budgets. The old-provider exploratory runs had one failure, different
shared-host load and no equally frozen global provenance from their start;
these rows do not establish a causal performance improvement. Promotion and
drain finished before the provider's ten-second failed-host recheck interval,
so this campaign did **not** exercise a post-cooldown recheck.

## Separate real-wire post-cooldown boundary

`BlueTusk.Workflows.PostCooldownTests` exercises that later boundary without
changing the promotion workload. It binds a private loopback port that initially
refuses connections and uses a configured disposable healthy PostgreSQL endpoint
as the second host. After the first fallback, the owned socket starts accepting
TCP but never responds to PostgreSQL startup. Eight reads succeed on the healthy
host before the ten-second monotonic cooldown expires. After 10.25 seconds, one
request starts a real TCP recheck and holds it open while 32 concurrent reads
complete on the healthy host. A reset makes the probe fall back; eight subsequent
reads must also succeed. The test asserts exactly one dead-endpoint TCP accept,
provider retry/failover counters of 34/50, and a drained aggregate pool. The
different counters are intentional: a cooling host is ordered last, so healthy
service records failover but no retry attempt at index zero.

The test requires a live fixture and report path; absence fails rather than
skipping. It remains outside the ordinary `*.Tests.csproj` selector:

```powershell
$env:BLUETUSK_POST_COOLDOWN_HEALTHY_CONNECTION_STRING = 'Host=127.0.0.1;Port=55418;Username=postgres;Password=postgres;Database=bluetusk_ecosystem;SSL Mode=Disable;Channel Binding=Disable'
$env:BLUETUSK_POST_COOLDOWN_REPORT = "$PWD/artifacts/jobs-physical-recovery/post-cooldown.json"
dotnet test tests/BlueTusk.Workflows.PostCooldownTests/BlueTusk.Workflows.PostCooldownTests.csproj -c Release --no-build -nr:false
```

Three independent test processes passed one test each, zero failed or skipped.
The checked-in [run 1](performance-reports/post-cooldown-run1.json),
[run 2](performance-reports/post-cooldown-run2.json) and
[run 3](performance-reports/post-cooldown-run3.json) reports preserve the raw
measurements. First recheck ages were 10,271.9/10,284.1/10,286.1 ms;
32 healthy reads while the sole TCP probe was held completed in
22.27/22.59/22.18 ms, with per-read P99 of 7.55/7.68/7.66 ms. Each run accepted
one dead-endpoint connection, recorded exactly 34 retries and 50 failovers,
and ended with four total pool connections, zero busy and zero waiting.
This is a short local wire test of one recheck interval, not a network-partition
endurance or availability guarantee.

This campaign does not qualify asynchronous-replication loss, simultaneous
primary/standby failure, split brain, old-primary rejoin, silent network loss,
power-loss durability, major server upgrades, Linux client/AOT operation or
production availability. Deployment automation must establish one writable
primary, endpoint selection, storage/replication protection and finite retry
budgets; the product's lease fence cannot reconcile independently writable
database histories. The shared release gates still require a frozen candidate
and independent approval.
