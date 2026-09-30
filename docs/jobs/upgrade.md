# Jobs binary upgrade and rollback gate

The manual [Jobs upgrade workflow](../../.github/workflows/jobs-release-upgrade.yml)
requires two immutable full commit SHAs: an earlier Jobs preview source commit
reachable from the candidate and the exact candidate commit. It builds and archives
both Jobs packages and separate probe executables from those source trees. The
executed `BlueTusk.Jobs.dll` must match its archived package and **differ** between
old and candidate. A same-binary configuration reopen does not satisfy this gate.

The old process initializes a fresh format-one schema, persists a running lease,
a failed attempt ready for retry, and pending work. While the old process remains
alive and holds its lease, the candidate process opens the same store, confirms
deduplication identities, completes the retry and pending work, and signals the
old process to complete its held lease. The candidate verifies that completion.
After both processes exit, the old binary reopens the store, checks all states and
attempt histories, then admits and completes a new job. Each phase rejects a
duplicate completion. The store header must remain format 1 with unchanged
payload and history admission limits throughout. This is the only supported
rollback boundary; there is no format migration or downgrade procedure.

The successful artifact includes the old Git source archive, both package files,
both executable snapshots, phase records and process logs, and a manifest of
SHA-256 hashes. Its verifier ties the source trees, probe, packages, executable
Jobs DLLs and exact effect counts to the candidate and old commits. A partial or
failed run uses a separate artifact name and never satisfies release readiness.
This workflow has not run and creates no qualification merely by existing.

An earlier preview commit such as `14b212e` is reachable from this branch.
The candidate's claim-ordering correction changes the Jobs assembly while
preserving format one, so it provides a real cross-binary boundary to rehearse.
The verifier still checks the selected old commit and both packaged DLLs; no
passing rehearsal has yet been retained.
There is no previously published Jobs package or released durable format. A
future format change requires a separate migration and rollback policy.
