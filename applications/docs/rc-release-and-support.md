# BlueTusk applications 1.0.0-rc.1

The three applications exercise all six BlueTusk product families from exact package versions.
This release is staging-only because PostgreSQL 19 is Beta 3. It is not production evidence,
does not count as a formal V1 pilot, and cannot substitute for the seven stable-candidate
workflows.

Support covers defects reproducible with the recorded commit, package hashes, image digests,
PostgreSQL image digest, and deployment evidence. Report incidents with application/tenant,
UTC interval, correlation IDs, package/image evidence, replay/checkpoint positions, and whether
cancellation, repair, restart, failover, restore, or rollback was attempted. Never include
cookies, tokens, connection strings, or Kubernetes Secret values.

RC package and image versions are immutable. A correction uses `1.0.0-rc.2` and new image
digests. Stable promotion requires PostgreSQL 19 GA and the existing exact-SHA programme; no RC
observation is promoted automatically.
