# Edge ordered offline capacity campaign

`eng/run-edge-capacity.ps1` prepares a clean exact-commit local measurement. It builds the .NET
harness and locked browser client, snapshots every executable dependency and the JavaScript
modules, then runs two fresh digest-pinned PostgreSQL 18 fixtures. Each fixture has four Docker
CPUs, 2 GiB of memory and its own database volume. A real Kestrel endpoint serves seven SQLite
clients and one persistent-profile Chromium-family IndexedDB client. The runner labels and removes
only its own PostgreSQL container and volume and generates a fresh random database credential per
fixture. It retains raw JSON, workload logs, local SQLite
files, browser profile, source captures, binary snapshots and SHA-256 manifest under `artifacts/`.

The eight isolated tenant scopes each begin with 256 distinct 4 KiB records and an empty ordered
client stream. Each client schedules one 4 KiB high-entropy update every 200 ms for 30 minutes:
40 planned writes/s across the fixture. The monotonic scheduler counts every slot as offered or
skipped, including slots remaining after a slow operation crosses the deadline, and never creates
an unbounded client request queue. The offline verifier requires exact planned-slot accounting for
each client. The same key cannot have two pending writes. Each
client holds at most 512 local mutations and receipts; the server caps each scope at 512 receipts
and 1024 feed entries. Successful reconnect passes durably acknowledge results, confirm original
mutations, advance gapless ordered horizons and prune only a feed prefix behind the active
client's durable checkpoint. An intentionally lagging read verifies HTTP 410 and a fresh snapshot
after the floor advances.

Each run includes two 30-second offline windows, one response dropped **after** the server's
business transaction commits for every client, a local SQLite reopen or real browser-profile
restart, and a Kestrel restart. The response drop is injected by a client-side transport wrapper
after a real successful HTTP response; it proves durable retry behavior but does not model every
socket or proxy failure. The .NET SQLite handles are reopened, not forcibly killed as OS
processes. The browser closes and relaunches its persistent profile. A failed run retains its
partial evidence without asserting capacity.

The browser publishes its durable checkpoint through a same-directory atomic rename. The
.NET observer shares deletion while reading the previous snapshot, so publication neither
truncates an observed checkpoint nor writes into its open handle. Replacement retries only
Windows lock errors for a bounded interval, then surfaces any persistent failure. The frozen `fa18630`
full campaign failed during its first run when the original in-place writer encountered
`EBUSY` on `browser.checkpoint`; its partial capture remains failed. The publication fix
requires a new exact-source campaign with the same durations and budgets.

Use an installed Playwright browser channel and an otherwise idle reference host. A short
diagnostic must precede interpreting the provisional budget:

```powershell
$env:BLUETUSK_EDGE_BROWSER_CHANNEL = 'msedge' # or chromium when installed
./eng/run-edge-capacity.ps1 -ExpectedCommit '<full 40-character SHA>' -Seconds 60 -Repetitions 1
```

The default is two independent 30-minute runs, roughly 70–90 minutes total including seed,
faults, drain and verification on a ready host:

```powershell
./eng/run-edge-capacity.ps1 -ExpectedCommit '<same full SHA>'
./eng/verify-edge-capacity.ps1 -ExpectedCommit '<same full SHA>' -EvidenceDirectory 'artifacts/edge-capacity/<campaign>'
```

The offline verifier checks source/binary/asset hashes, all retained artifacts, nonoverlapping
runs, offered-slot accounting, all eight stores, four fault recoveries per client, exact business
effect counts, contiguous retained feed, final local checkpoints, server horizons, empty durable
confirmation outboxes, and a 410 retry fence on a reclaimed UUID. It requires at least 30
durable applications/s; at most 5% missed slots; successful-call p99 limits of 250 ms enqueue,
2 s HTTP apply and 1 s horizon advance; a 5 s p99 for acknowledgments outside fault windows;
and at most 120 s to the first successful reconnect or lost-write recovery and final drain.
It also checks five-second physical samples, 2 GiB owned
relations, 4 GiB whole database, 32 MiB/min late owned growth, 128 KiB inserted WAL per
application, and bounded SQLite/browser profile footprints. The numeric thresholds in
`eng/edge-capacity-budgets.json` are **proposed**, not measured results. Diagnostic runs cannot
set `QualifiedLocalCapacity=true`; even a full local pass sets `ProductionQualified=false`.

The campaign measures one client stream per scope, one loopback host, fixed 256-key churn and
the ordered receipt path. It does not qualify multi-device scope contention, long offline
periods beyond the bounded queue, clock rollback/suspend, mobile browsers, operator-selected
retention floors with unknown clients, real process kills for the SQLite clients, external
authentication, fleet failover, or production disk and network behavior. Those require
separate release evidence.
