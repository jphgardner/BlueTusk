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
whose `head_sha` equals the tag commit, a readiness artifact bound to the exact
family, commit, tag, version and qualification artifact digests, and verifies
that exact-version release dependencies are already public. The protected
publish job checks the live repository and environment settings before any
registry write. A source-only governance declaration cannot substitute for
those live settings. Source policy
checks neither run outcomes nor report contents. The release verifier checks
workflow identity, event, conclusion, commit and the readiness artifact's
references to the exact qualification runs and their retained artifact digests.
The future qualification workflows and protected candidate aggregator must
validate their own raw measurements before a successful run can count.

Each family must have its own capacity, failover and durable-format upgrade
qualification. The required workflow identities are fixed in
`eng/expansion-release-policy.json`. Jobs needs a dedicated
`jobs-release-capacity.yml` workflow; the combined `ecosystem-performance.yml`
campaign cannot qualify it alone. The Jobs-only workflow is implemented but
has no passing release evidence. Workflows, Projections and Documents share
that exact-candidate capacity campaign because it measures each explicitly.
It must emit a separate retained capacity artifact for each family before
any can use it as release evidence. Its result alone is not a product
release pass. The other six need dedicated capacity workflows. Every family
needs a dedicated failover and upgrade workflow, plus the protected expansion
candidate aggregator. Jobs now has a source-bound synchronous-promotion
failover workflow, but no passing exact-candidate release run. Its upgrade
workflow, the protected aggregator, and the other families' required readers
are not implemented yet. An arming edit is therefore rejected even if
generic `build.yml`, `security.yml`, `performance.yml` and
`ecosystem-build.yml` pass.

The Jobs capacity workflow runs only the Jobs storage profile on a dedicated,
digest-pinned PostgreSQL 15 fixture. It requires two separate full 1,800-second
high-entropy campaigns on the reference runner. The Jobs-only verifier checks
durable completions, hot and cold tenant p99 latency, exact accepted effects,
explicit overload rejection, pruning, physical storage samples, late growth
and cluster WAL per accepted job against
[`eng/jobs-release-capacity-budgets.json`](../../eng/jobs-release-capacity-budgets.json).
It retains the raw reports, fixture observations, source captures, binary
snapshot and a file-hash manifest in the
`expansion-jobs-capacity-<full-sha>` artifact. These are local reference-runner
limits, not claims about independent hosts or multi-day retention. No
capacity pass is recorded by adding the workflow.

The Jobs failover workflow repeats three fresh PostgreSQL 18 synchronous
primary/standby promotions. Its verifier requires the exact candidate source,
unchanged test binaries, a passing unskipped test for each pair, 66 preserved
acknowledged Jobs/admissions and 66 singular Jobs effects per pair, newer
attempt-two fences with stale completion/effect rejection, tenant isolation,
and outage-to-recovery health. Raw reports, database logs, fixture samples,
TRX results and binary snapshots are retained in
`expansion-jobs-failover-<full-sha>`. The local hard-stop proves neither
asynchronous-loss tolerance nor split-brain fencing, old-primary rejoin,
credential rotation during promotion, persistent storage failure or fleet
availability. No failover pass is recorded by adding the workflow.

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

The candidate-readiness run must upload one
`expansion-readiness-<lowercase-family>-<full-sha>` artifact containing
`readiness.json`. Its schema 1 record must state the exact `family`,
`candidateCommit`, `tag`, `version`, `readinessRun.id` and
`readinessRun.attempt`. Its `qualificationEvidence` must contain exactly one
`capacity`, `failover` and `upgrade` entry. Each names the policy workflow,
successful exact-candidate run ID and attempt, and an
`expansion-<lowercase-family>-<role>-<full-sha>` artifact with its SHA-256
digest. The tagged release verifier compares these entries with GitHub's
retained, unexpired run artifacts and verifies the downloaded readiness ZIP
digest. This binds the reviewed evidence set to one candidate; the readiness
workflow must still inspect the reports' substance and get independent
approval. No workflow currently produces this release artifact.

Source arming necessarily changes the commit SHA. The reviewed arming commit
is therefore the immutable candidate; its manual runs and approvals occur
after that commit, and a later source change invalidates them. Until the
product-specific readers, protected environment, exact tag policies and
candidate evidence exist, leave `publication.enabled=false` for all ten
families. This document does not declare any expansion family stable or
production qualified.
