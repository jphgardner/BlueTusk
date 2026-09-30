# Documents fixed-cardinality maintenance comparison, 28 September 2026

Two fresh, owned PostgreSQL 18.6 fixtures ran the same 15-cell payload/concurrency
sweep, followed by 600 seconds of 64 KiB mixed document churn, 120 seconds idle,
and a hard-killed writer recovery check. The second fixture changed only the
experimental table and TOAST autovacuum reloptions (`ToastVacuumFast`). Each
fixture had four Docker CPUs, 2 GiB memory, an eight-connection application
pool, a 24 GiB database stop guard, and an 8 GiB filesystem-free guard. The
payloads contain seeded random base64 rather than repeated compressible text.

| Sustained result | Package defaults | Fast TOAST vacuum |
| --- | ---: | ---: |
| Verified logical transitions | 271,228 | 230,476 |
| Measured write window | 608.81 s | 600.16 s |
| Transitions per second | 445.5 | 384.0 |
| Documents relation before writes | 17.65 MB | 17.65 MB |
| Documents relation after writes | 16.204 GB | 9.060 GB |
| Documents relation after 120 s idle | 16.204 GB | 9.060 GB |
| TOAST relation after writes | 16.204 GB | 9.060 GB |
| TOAST autovacuums after writes / idle | 1 / 1 | 3 / 4 |
| WAL bytes during writes | 22.701 GB | 24.821 GB |
| Save p99, upper histogram bound | 1,056.8 ms | 1,286.1 ms |
| Whole-operation p99, upper histogram bound | 2,768.9 ms | 4,358.1 ms |
| Database observation timeouts | 0 | 0 |
| Maximum observation gap | 11.91 s | 11.95 s |

All 16 scenarios in each fixture passed logical state checks. The 32-tenant,
32-writer, 1 MiB cell is a notable counterexample to treating the tuning as a
free capacity gain: its save p99 was 6.13 s with package defaults and 28.31 s
with fast TOAST vacuum. In sustained churn, each of eight tenants continued
to make progress. Both fixtures verified acknowledged two-document batches
after hard-killing a blocked writer and found no partial batch. Each left zero
owned schemas and removed its label-checked container and volume.

The tuning reduced observed relation size in this run, but neither profile
demonstrated a stable physical bound. Both relations grew after temporary flat
intervals, and neither shrank during the 120-second idle drain. The tuned run
also had lower throughput, higher sustained p99, and more WAL per successful
transition. A fixed logical record count therefore does **not** imply bounded
TOAST or WAL storage. Operators need explicit capacity headroom, maintenance
policy, alerts, and longer representative workload evidence before setting a
production limit. The sequential local runs do not isolate all host variance
or prove a causal performance effect of the reloptions.

The two ignored raw evidence directories are
`artifacts/documents-load/final-immutable-default-20260928` and
`artifacts/documents-load/final-immutable-tuned-20260928`. The raw report
SHA-256 values are, respectively,
`AFBC2F13AC2ADDACACFBB1114C5A7DD9672F8FB997D3D3669C51A37BDC680DA4`
and `CC4D52EF430C9E6A14EA61C1DEEA3E2674A5282F67A0EA0C63242F3EF306E994`.
Both runners recorded unchanged candidate inputs with fingerprint
`A12EEEFE78C877FEA4E641E04323FEBD1ADCFAF40D6728B7982F1EF0E4831796`
and matched all ten measured DLLs to their pre-run snapshots, including
load-harness DLL SHA-256
`957F7321718F36F974579DFF74442D043AEE431AA0F95D71FC39A7FEB09C05E7`.
The full `bindings.json` SHA-256 values are
`0D753785982A1D6AEE47BB5368B50B3802B024088194B3D141E17CECB6F55FC5`
and `49DEBEB920161A85D2EF6F561AE48CD7879466BFA7CF806642FB94B9E161F3A1`.
These are source-frozen local measurements on a dirty working tree, not an
immutable release candidate or production qualification.
