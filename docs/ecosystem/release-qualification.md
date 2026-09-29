# Expansion release qualification

Events, Jobs, Documents, Schema, Projections, Search, Sql, Studio, Edge and
Workflows are implemented preview products at `0.1.0-preview.1`. Their
publication flags are disabled. The tagged release workflow recognizes their
names so a future qualified candidate can use the same package, SBOM,
provenance and protected-publication machinery as the established families.
Recognizing a tag is not authority to publish it.

`eng/verify-expansion-release-policy.ps1` is the fail-closed source contract.
It checks all ten preview entries while disabled. If a future candidate arms a
family, it requires a stable version of at least `1.0.0`, the complete exact
manual workflow set in its family manifest, a protected independent
`expansion-candidate-readiness` environment, and the exact versioned tag
pattern in the `package-production` governance contract. The tagged workflow
also runs `verify-release-gates.ps1`, which requires successful manual runs
whose `head_sha` equals the tag commit and verifies that exact-version release
dependencies are already public. The protected publish job checks the live
repository and environment settings before any registry write. A source-only
governance declaration cannot substitute for those live settings. Source policy
checks neither run outcomes nor report contents. The current generic release
verifier checks workflow identity, event, conclusion and commit; the future
qualification workflows and protected candidate aggregator must validate
their own raw evidence before a successful run can count.

Each family must have its own capacity, failover and durable-format upgrade
qualification. The required workflow identities are fixed in
`eng/expansion-release-policy.json`. Jobs, Workflows, Projections and Documents
share the exact-candidate `ecosystem-performance.yml` capacity campaign because
that workflow measures each of those four explicitly. Its pending result is
not a pass. The other six need dedicated capacity workflows. Every family
needs a dedicated failover and upgrade workflow, plus the protected expansion
candidate aggregator. Those workflows and their complete evidence readers are
not implemented yet. An arming edit is therefore rejected even if generic
`build.yml`, `security.yml`, `performance.yml` and `ecosystem-build.yml` pass.

The eventual workflows must bind reports, binary/source hashes, fixture
versions and actual workload outcomes to the same full candidate SHA. Capacity
must cover sustained throughput, P50/P95/P99 latency, allocation, memory,
connections, WAL, physical storage, overload and retention under documented
payload, tenant and concurrency distributions. Failover must check
acknowledged-effect survival, exact recovery, stale-owner fences, tenant
isolation and repeated disturbance. Upgrade must rehearse forward and rollback
paths for durable formats and supported PostgreSQL/client versions, including
in-flight state. Independent review must inspect those retained reports and
operator runbooks. A workflow that exits successfully without these checks
must not be added to the family manifest as qualification evidence.

Source arming necessarily changes the commit SHA. The reviewed arming commit
is therefore the immutable candidate; its manual runs and approvals occur
after that commit, and a later source change invalidates them. Until the
product-specific readers, protected environment, exact tag policies and
candidate evidence exist, leave `publication.enabled=false` for all ten
families. This document does not declare any expansion family stable or
production qualified.
