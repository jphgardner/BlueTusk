# Core performance-leadership evidence pipeline

This pipeline produces `performance/performance-leadership-evidence.json` (schema 3). Core
readiness binds it as `performanceManifest`, from producer `core-performance-evidence.yml`. It
measures the contract in
[`eng/performance-leadership-contract.json`](../../eng/performance-leadership-contract.json) **as
written**. The contract is not relaxed anywhere in the pipeline.

**Status:** pipeline implemented; evidence not yet producible. Provider capture works for the
same-OS, TLS and constrained-network variants. The cross-OS Provider variants are unresolved (see
the [proposal](../../eng/performance-cross-os-proposal.md)). The Streams, Sync, Live,
Control Plane and primary hot-path harnesses are planned legs that fail closed. Until all of these
exist, a qualification run fails and the verifier reports the missing workloads. That is the
intended outcome.
Qualification planning reports every missing producer and fails before starting measurement
jobs. Provider jobs are split by concurrency level, each with a separate raw input directory;
assembly still requires every original workload key exactly once.

## Data flow

```text
capture legs (one per OS x Provider variant, one per OS x family)
   raw/<input-id>/capture-index.json | trial-index.json + one raw file per process trial
        |
        v  BlueTusk.Benchmarks --performance-evidence-generate      (per OS)
   summary.json + raw-samples.json      per-trial metrics, seeded bootstrap bounds, file hashes
        |
        v  write-performance-environment-manifest.ps1               (on the measured OS)
   environment-manifest.json            host, Docker, topology, images, raw-samples hash
        |
        v  PerformanceEvidenceChecker check                         (independent; BCL only)
   check-report.json                    every trial value and bound recomputed from raw
        |
        v  assemble-performance-leadership-evidence.ps1             (both OS)
   performance-leadership-evidence.json + consolidated-report.md + verifier-self-tests.log
        |
        v  verify-performance-leadership-evidence.ps1               (unchanged gate)
```

Each OS directory is laid out as `performance/<os>/{summary,raw-samples,check-report,environment-manifest}.json`.
The raw files stay under `<evidence store>/<run>/<os>/raw/`. `raw-samples.json` binds every raw
file by path, size and SHA-256. The checker rejects any file that is unbound, missing or changed.

## Family-pluggable inputs

Each `raw/<input-id>/` directory holds exactly one index:

| Format | Index | Raw file per trial | Used by |
|---|---|---|---|
| `provider-request-capture-index/1` | `capture-index.json` | Provider request capture | Provider variants |
| `performance-trial-index/1` | `trial-index.json` | `bluetusk-performance-trial` | Streams, Sync, Live, Control Plane, primary hot paths |

To add a family, write a harness that emits the family-neutral format. The generator, checker and
assembler need no change. A `trial-index.json` has `schemaVersion: 1`,
`evidenceKind: "bluetusk-performance-trial-index"`, and these fields:

- `family`, `sourceCommit`, `os`, `diagnostic`, `synthetic` and `trials`;
- `candidateImplementation` and `referenceImplementation`;
- `containerImageDigests`, which must all be `@sha256:` references;
- `leadershipGatePassed: false`;
- `records`, each with `workloadKey`, `trial`, `role` (`candidate` or `reference`), `path` and
  `sha256`.

Each raw trial file repeats that identity. It adds `implementation` and `environment`
(`os`, `architecture`), `frequency` (ticks per second) and `measurement`. `measurement` holds
`elapsedTicks`, `completedOperations`, `allocatedBytes`, `cpuMilliseconds`,
`peakWorkingSetBytes`, `gcCollections[3]` and `workers[]`, each with `requestTicks[]` and `count`.
For a JVM or other non-.NET reference, the harness must say in `environment` how it measured
allocation, CPU and GC. One trial is one independently restarted process. Within a workload,
candidate and reference processes alternate order and never overlap.

## Statistics

The generator implements this method, and the checker re-implements it separately, from this text:

- **Per-trial metrics.** These are computed from individual operations:
  - throughput = operations / elapsed seconds;
  - mean latency;
  - nearest-rank P95 and P99, using the sample at index `ceil(p * N) - 1` of the sorted samples;
  - allocated bytes, CPU microseconds per operation, peak RSS;
  - `gcCounters` = gen0 collections (every GC) per 1,000 operations. This is reported, not gated.
- **Point estimate.** The arithmetic mean of the per-trial values, separately for the candidate
  and the reference.
- **Interval.** A studentized bootstrap of the arithmetic trial means with B = 10,000 resamples.
  The contract requires 95% comparisons; the producer uses nominal 99% intervals and at least
  30 trials to provide additional margin for finite-sample error. The separately reported
  `intervalConfidenceLevel` is 0.99; the contract `confidenceLevel` stays 0.95.
  - `SE = sample standard deviation / sqrt(n)`, using the `n - 1` variance divisor;
  - each resample produces `t* = (mean* - mean) / SE*`;
  - quantiles are the sorted pivots at `floor(B * 0.005)` and `ceil(B * 0.995) - 1`;
  - the interval is `(mean - upperQuantile * SE, mean - lowerQuantile * SE)`;
  - singular resamples remain in the appropriate infinite tail; they are never discarded or
    assigned a substitute standard error. Unbounded intervals fail generation and checking;
  - all-constant trials have zero observed variation and a point interval;
  - bounds are intersected with the nonnegative parameter space and widened, if needed, to
    contain the point estimate. No observations are trimmed or transformed.
  See [Hesterberg's bootstrap-t derivation, section 4.3](https://pmc.ncbi.nlm.nih.gov/articles/PMC4784504/).
- **Deterministic plan.** The seed is `SHA-256("bluetusk-performance-bootstrap/v2|" + sourceCommit)`.
  Each draw is `UInt64LE(SHA-256(seed || Int32LE(n) || Int32LE(b) || Int32LE(j))[0..8]) mod n`.
  The seed depends only on the commit, so it cannot be chosen after the data is seen.
- **Trials.** Qualification needs 30–50 independent trials per role and workload. Diagnostics
  need at least 3, but sparse or very small captures may have unbounded intervals and remain
  raw diagnostic captures. They cannot qualify a release.
- **Calibration.** The self-test checks 2,000 samples from each of normal, lognormal (sigma 0.5
  and 1) and exponential populations at n = 30. It checks total coverage and each tail against
  the contract's 95% level, using a predetermined three-standard-error Monte Carlo tolerance.
  These checks exercise finite-sample behavior on these populations; they are not a proof for
  arbitrary latency distributions. The method assumes independent process trials, a stable
  workload and finite variance. The verifier combines a candidate bound with a reference bound;
  their underlying nominal intervals use 0.5% tails. Original undercoverage failures are retained
  separately from subsequent calibration results.
- **Multiplicity.** These are separate bounds per provider and metric. The pipeline makes no
  simultaneous confidence claim across the full matrix.

The Provider analyzer's paired log-t interval (`--provider-request-analyze`) is a different
quantity. It is never relabelled as these bounds.

## Guards

- **Diagnostic and synthetic output.** Such output is labelled `diagnostic` or `synthetic` at
  every level: index, summary, check report, environment manifest and evidence. The verifier
  rejects any label other than `false`. A diagnostic assembly writes
  `diagnostic-performance-leadership-evidence.json`, never the readiness file name. Synthetic
  fixtures can only go through `--performance-evidence-generate-synthetic`. The production
  generator and the workflow reject them.
- **No substitution.** These are all rejected:
  - workloads outside the contract, or supplied by two inputs;
  - partial trial pairs and too few trials;
  - identical raw content in two trials, which catches copied captures;
  - a raw capture whose client OS, TLS state or network profile differs from its variant profile;
  - an unbound file in a raw directory.
- **Cross-OS variants.** These resolve only through
  [`eng/performance-variant-map.json`](../../eng/performance-variant-map.json). While
  `crossOsProfile` is `unresolved`, the legs fail closed and the assembler reports the keys as
  missing.
- **Qualification assembly.** It requires both OS directories, exact contract coverage, a
  consistent independent check, environment manifests bound to the raw samples and the measured
  images, and the committed variant map.

## Capture profiles and fixtures

| Variant on host OS | Profile | Fixture |
|---|---|---|
| same OS (`windows` on Windows, `linux` on Linux) | `native` | PostgreSQL `postgres:18-alpine@sha256:77f5…` with `max_connections=600` and 1 GiB `/dev/shm` |
| `tls` | `tls` | Same, with `ssl=on` and a disposable CA; the client uses `VerifyFull` |
| `constrained-network` | `constrained-network` | Same, behind `ghcr.io/shopify/toxiproxy:2.12.0@sha256:9378…`, profile `toxiproxy-constrained-v1` |
| cross-OS | `unresolved` | Fails closed; see the proposal |

`eng/start-performance-fixture.ps1` creates containers labelled `bluetusk.owner=<owner>` and
`bluetusk.run=<run>`. `eng/stop-performance-fixture.ps1` removes only containers with both labels.
The fixture uses `docker create`, `docker cp` and `docker start`, not host bind mounts, so it also
works from a containerized Linux runner. Connection strings stay in the fixture directory as
`*.secret` files. They are inherited by name into capture processes and are never printed.

## Runners

The only measurement host is one Windows 11 PC, so both contract environments run there:

- **Windows x64.** An ephemeral self-hosted runner on the host, with labels
  `self-hosted, windows, x64, bluetusk-benchmark`.
- **Linux x64.** A self-hosted runner in a Linux container in Docker Desktop's WSL2 VM, built from
  [`eng/runners/linux-benchmark-runner.Dockerfile`](../../eng/runners/linux-benchmark-runner.Dockerfile)
  on a digest-pinned `actions-runner` image. It has labels
  `self-hosted, linux, x64, bluetusk-benchmark`. It reaches Docker through the mounted socket, and
  fixtures attach it to their network.

[`eng/runners/start-benchmark-runner.ps1`](../../eng/runners/start-benchmark-runner.ps1) registers
either runner with `--ephemeral` and re-registers it after each job. Runner registration requires
repository administration permission. For this release programme the owner has authorized
ephemeral runners on this PC, registered only while a dispatched job runs. An authorized
operator supplies a registration token in `BLUETUSK_RUNNER_REGISTRATION_TOKEN` for that process
only. Tokens expire after one hour; obtain a fresh token or just-in-time runner configuration for
each later registration and never retain the token in evidence.

Both runners need:

- `BLUETUSK_EVIDENCE_STORE`: a host-persistent directory. Ephemeral runners lose their workspace,
  so raw samples must survive between the capture and summarize jobs.
- `BLUETUSK_HOST_MEASUREMENT_LOCK` (optional): the host lock that other agents honour. Each leg
  waits for it and holds it while measuring.

Legs never overlap. The workflow uses one matrix with `max-parallel: 1`, and the
`bluetusk-reference-host` concurrency group serializes runs. The Windows leg reaches PostgreSQL
through a loopback port published by Docker Desktop. The Linux leg uses the Docker bridge network.
Absolute numbers are therefore not comparable across OS. Each comparison is
candidate-versus-reference within one OS, variant and topology. The topology and the Docker VM
CPU and memory are recorded in each environment manifest.

## Running it

**Workflow.** Run `core-performance-evidence.yml` manually with:

- `candidate_sha`;
- confirmation `CAPTURE-1.1-PERFORMANCE-EVIDENCE`;
- `evidence_class`. `qualification` plans every leg. `diagnostic` may select families and skips
  legs that cannot run.

The `assemble` job runs the verifier and pipeline self-tests into `verifier-self-tests.log`,
assembles the evidence and runs the verifier. It uploads `artifacts/core-performance/`, which
contains `performance/` with the summaries, raw-sample manifests, check reports and manifests.
Raw samples stay in the evidence store, because a full capture far exceeds the 2 GiB delivery
limit for artifact capture.

**Local producer.** After a qualification run on the host, run
`eng/build-core-local-performance-record.ps1`. It:

- runs the unchanged verifier;
- confirms that the bundled summaries match the evidence store byte for byte;
- re-runs the independent checker from the retained raw samples;
- emits a schema-5 `LocalDocker` record.

The record's single `environment.hostOs` names the physical host (`windows`), with
`dockerOs=linux`. Both measured environments are bound inside the performance evidence through
their environment manifests.

## Host-time estimate (qualification, 30 trials, 5 s warm-up, 10 s window)

The estimate assumes about 20 s per process run, including process start, fixture schema, warm-up,
measurement, in-flight drain and sample serialization. Each OS has 48 Provider workloads per
variant (16 features x 3 concurrency levels) and 2 providers. One variant therefore needs
48 x 2 x 30 = 2,880 process runs, which is about 16 hours. The variant is split into three
concurrency jobs: about 5.3 hours each at 30 trials and 8.9 hours each at 50 trials. This keeps
each Provider job below the workflow's 24-hour limit and the GitHub token lifetime.

| Per OS | Windows | Linux |
|---|---:|---:|
| Provider: same-OS, TLS and constrained network | 48 h | 48 h |
| Provider: cross-OS (after definition and implementation) | +16 h | +16 h; currently unproducible |
| Streams, Sync, Live, Control Plane and primary hot paths, when implemented (76 workloads, assumed 45 s per run) | ~57 h | ~57 h |
| Generation and independent check | < 1 h | < 1 h |
| **Total, if every producer becomes executable** | **~122 h** | **~122 h** |

The two OS legs run one after the other on the same PC, so a full qualification takes about
roughly 10 days of exclusive host time under these assumptions. This is a planning estimate,
not a measured completion time. The missing family producers need bounded job chunks before
they can be enabled, especially at 50 trials. Endurance needs the same host and cannot overlap.
