# Parser reliability and coverage-guided fuzzing

BlueTusk fuzzes every externally controlled parser boundary used by the V1
product chain:

| Target | Boundary |
| --- | --- |
| `protocol-frames` | Segmented PostgreSQL backend frames and backend-message decoders |
| `authentication` | PostgreSQL authentication messages and SCRAM server exchanges |
| `pgoutput` | pgoutput protocol versions 1–4, streaming and two-phase messages |
| `binary-copy` | Binary COPY field decoding through the built-in type registry |
| `array-codec` | Binary and text PostgreSQL arrays |
| `range-codec` | Binary and text ranges and multiranges |
| `composite-codec` | Binary and text composite/record values |
| `streams-envelope` | Integrity-protected Streams transaction envelopes |
| `live-resume-token` | Raw, signed-malformed and structurally valid Live resume tokens |

The harness accepts at most 64 KiB. A protocol input may yield at most 512
messages. Collection decoders reject counts that exceed either their remaining
payload capacity or the 4,096-item parser ceiling. Streams decoding uses
separate limits of 512 changes, 128 tables, 256 columns per table and 4 KiB per
string. CI gives each execution 2 seconds and the .NET managed heap 1 GiB.
AFL's virtual-address-space limit is disabled because the .NET runtime reserves
more address space than it commits; the GC heap hard limit supplies the bounded
managed-memory control instead.

The machine-readable source contract is
[`eng/fuzzing-contract.json`](../eng/fuzzing-contract.json). It prevents the
target registry, runner, workflow matrix, encoded corpus directories and
resource limits from silently drifting apart:

```powershell
./eng/verify-fuzzing-contract.ps1
```

Structured codecs reject declared array-element and record-field lengths before
slicing the remaining payload. Text arrays enforce the same six-dimension
ceiling as binary arrays, CLR array-bound translation is range checked, and
binary timestamps outside the representable .NET range are rejected as malformed
values.

## Deterministic replay

Every seed and minimized regression is stored as Base64 in
`tests/fuzz-corpus/<target>`. The normal solution test run replays every case:

```powershell
dotnet test tests/BlueTusk.Fuzzing.Tests/BlueTusk.Fuzzing.Tests.csproj `
  --configuration Release
```

Replay one materialized input directly with:

```powershell
dotnet run --project tests/BlueTusk.Fuzzing/BlueTusk.Fuzzing.csproj `
  --configuration Release -- `
  --replay protocol-frames artifacts/fuzz/case.bin
```

## Coverage-guided runs

Install AFL++ and restore the repository-pinned SharpFuzz tool, then run:

```powershell
dotnet tool restore
./eng/run-fuzz.ps1 `
  -Target protocol-frames `
  -DurationSeconds 60 `
  -ExecutionTimeoutMilliseconds 2000 `
  -MemoryLimitMegabytes 1024 `
  -MaximumInputBytes 65536
```

`fuzzing.yml` runs a 45-second smoke for every target on pushes and pull
requests, a one-hour-per-target scheduled run, and an explicitly configurable
manual run. Each job archives its fuzzer state. Crash and hang inputs are also
converted to replayable Base64 with source-commit and SHA-256 metadata by
`archive-fuzz-findings.ps1`, and make the job fail.

Manual runs enforce at least 3,600 seconds per target. A successful manual run
ID for the exact candidate commit is mandatory input to the protected V1
readiness workflow; a push, pull-request, scheduled, ancestor-commit or shorter
run is not candidate evidence.

The current release-blocking review record is the
[V1 fuzz-finding handoff](operations/fuzz-finding-handoff.md).

The raw AFL state is compressed before artifact upload because AFL++ queue
filenames contain colons, which are valid on Linux but rejected by GitHub's
cross-platform artifact service.

Minimize a finding against the instrumented harness with:

```powershell
./eng/minimize-fuzz-finding.ps1 `
  -Target protocol-frames `
  -InputPath artifacts/fuzz/protocol-frames/findings/default/crashes/id-000000 `
  -OutputPath artifacts/fuzz/minimized-protocol-frame `
  -InstrumentedDirectory artifacts/fuzz/protocol-frames/bin
```

After review, encode the minimized file as Base64, add it to the matching
checked-in corpus directory, fix the parser defect, and rerun both deterministic
replay and coverage-guided smoke.
