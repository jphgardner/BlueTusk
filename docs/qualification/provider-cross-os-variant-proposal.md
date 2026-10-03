# Proposal: meaning of the cross-OS Provider variants

**Status:** proposal for the contract owner. Nothing here is adopted. The committed
[variant map](../../eng/performance-variant-map.json) keeps `"crossOsProfile": "unresolved"`, so the
two cross-OS capture legs fail closed and those workloads are reported as missing.

## The problem

[`performance-leadership-contract.json`](../../eng/performance-leadership-contract.json) lists the
Provider variants `windows`, `linux`, `tls` and `constrained-network`.
[`verify-performance-leadership-evidence.ps1`](../../eng/verify-performance-leadership-evidence.ps1)
crosses **every** environment with **every** variant. That produces four workload keys per feature,
concurrency and environment:

| Environment | `variant=windows` | `variant=linux` | `variant=tls` | `variant=constrained-network` |
|---|---|---|---|---|
| windows | same-OS baseline | **undefined** | defined | defined |
| linux | **undefined** | same-OS baseline | defined | defined |

The 96 cross-OS keys (16 features x 3 concurrency levels x 2 environments) have no unambiguous
meaning on one host. They are part of the 384 Provider comparisons that the verifier requires.

**Rule kept by the pipeline:** these keys are never filled with copies of same-OS data. The
generator rejects them while the map says `unresolved`. The generator and the independent checker
also require each raw capture's own `environment.os` to equal the profile's client OS, and they
reject two trials with identical bytes. A relabelled copy of a same-OS capture therefore fails,
even with a changed map.

## Options considered

1. **The variant names the PostgreSQL server OS.** This would need PostgreSQL running natively on
   Windows next to the Linux container image. That is a different server build and configuration,
   so it measures the server, not the Provider. It would also need a Windows server on the Linux
   runner. Rejected.
2. **The variant names the client runtime OS** (proposed). The server is always the same
   digest-pinned Linux PostgreSQL container. The variant says which OS the measured .NET client
   process runs on.
3. **Remove the cross-OS keys** (contract change). The four variants become three per environment.

## Proposal

Adopt option 2 for the Windows environment now. Make a contract change for the Linux environment,
because a Windows client cannot run on a Linux host.

| Key | Meaning under this proposal | Producible on the single Windows PC? |
|---|---|---|
| `windows\|…\|variant=windows` | Windows-native client, Linux PostgreSQL container | Yes (`native` profile) |
| `windows\|…\|variant=linux` | The same Release build runs in a digest-pinned `mcr.microsoft.com/dotnet/aspnet:10.0` Linux container on the Windows host's Docker (WSL2), on the fixture network, against the same server | Yes (`linux-container-client` profile, already implemented) |
| `linux\|…\|variant=linux` | Native Linux client in the Linux runner, Linux PostgreSQL container | Yes (`native` profile) |
| `linux\|…\|variant=windows` | A Windows client against the Linux runner's server | **No.** Windows containers need a Windows kernel. It would need a second, Windows, machine that drives the Linux leg's server. |

### Adopting the Windows half (one-line change)

In [`eng/performance-variant-map.json`](../../eng/performance-variant-map.json), change:

```diff
-  "crossOsProfile": "unresolved",
+  "crossOsProfile": "linux-container-client",
```

The plan, capture, generator, checker and assembler all read this table. The
`linux-container-client` profile declares `hostOs: ["windows"]`. The `windows-provider-linux` leg
therefore becomes runnable. The `linux-provider-windows` leg is planned as `unavailable-on-host`
and still fails closed.

### Contract change needed for the Linux half (proposal only; not applied)

The contract and verifier are deliberately unchanged in this PR. One minimal change that keeps
every other gate intact is to make variants per environment:

```diff
 "Provider": {
   ...
-  "variants": ["windows", "linux", "tls", "constrained-network"]
+  "variants": ["windows", "linux", "tls", "constrained-network"],
+  "variantsByEnvironment": {
+    "windows": ["windows", "linux", "tls", "constrained-network"],
+    "linux": ["linux", "tls", "constrained-network"]
+  }
 }
```

Then `verify-performance-leadership-evidence.ps1` would iterate
`variantsByEnvironment.<os>` instead of `variants`. The Core Provider count would fall from 384 to
336 (Core total from 536 to 488). The contract verifier would pin the new table, and the verifier
self-test would expand it the same way. The owner may prefer the simpler symmetric alternative,
`["native", "tls", "constrained-network"]` for both environments (288 Provider comparisons). That
drops the container-client measurement completely.

Either change must update the contract, the contract verifier, the evidence verifier, its
self-test and `PerformanceEvidenceGenerator.ExpectedCoreWorkloads` together. The assembler and
checker derive their expectations from the same expansion.

## Fairness notes for the container client

- Candidate and reference run the same harness build, the same options and the same process
  restarts inside the same container image. Ratios therefore compare like with like.
- The container client reaches PostgreSQL over the Docker bridge network. The Windows-native client
  reaches it through a published loopback port. Absolute latencies are not comparable between those
  two variants, and the evidence never compares them with each other.
- The WSL2 VM's CPU and memory allocation is recorded by the environment manifest
  (`docker info` CPU and memory). It must not change during a capture.
