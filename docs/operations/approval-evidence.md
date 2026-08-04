# V1 operational approval evidence

BlueTusk treats operational acceptance as measured release evidence, not a
collection of unchecked signatures. The protected candidate workflow requires
ten JSON records for one immutable commit. Every record is SHA-256-bound by the
candidate manifest and validated against a gate-specific schema before stable
publication can be authorised.

The authoritative assets are:

- `eng/v1-approval-evidence-contract.json`, which declares the exact fields,
  types, minimums and pass values for all ten gates;
- `eng/v1-approval-evidence.examples.json`, which contains one complete
  structural example per gate;
- `eng/verify-v1-approval-evidence.ps1`, which validates one record; and
- `eng/verify-v1-approval-evidence-set.ps1`, which validates the canonical
  ten-file set, pilot independence and website hash binding; and
- `eng/test-v1-approval-evidence-verifier.ps1`, which proves that the examples
  pass and representative weak or inconsistent records fail.

Examples are schemas, not release evidence. Replace every identity, value,
timestamp, candidate commit and reference with an observed result from the
actual candidate.

## Common envelope

Every approval file uses schema 2 and contains exactly these top-level fields:

| Field | Requirement |
| --- | --- |
| `schemaVersion` | `2` |
| `gateId` | Exact required gate identifier and file stem |
| `candidateCommit` | Full 40-character immutable candidate SHA |
| `outcome` | `approved` |
| `approvedBy` | Named accountable person or durable organisational identity |
| `approvedUtc` | UTC timestamp at or after the candidate commit and not in the future |
| `summary` | At least 40 non-whitespace characters describing what was accepted |
| `blockingFindings` | `0` |
| `references` | One or more absolute HTTPS URLs for retained evidence |
| `details` | Exact gate-specific measured fields; missing and unknown fields fail |

Candidate mode rejects ancestor-commit approvals, future-dated approvals,
non-HTTPS references, unexpected fields and narrative-only records. A reference
should resolve to a retained workflow, change, test, dashboard snapshot,
incident record or evidence archive that the protected reviewer can access.
Do not put credentials, access tokens or personal data in approval JSON.

Validate an individual record before packaging it:

```powershell
./eng/verify-v1-approval-evidence.ps1 `
  -EvidencePath 'D:/release-evidence/approvals/application-pilot-a.json' `
  -ExpectedGateId 'application-pilot-a' `
  -ExpectedCommit '<40-character-candidate-sha>'
```

Run the verifier self-test after changing any schema or validator:

```powershell
./eng/test-v1-approval-evidence-verifier.ps1
```

## Independent release review

`independent-release-review.json` records a reviewer from outside the candidate
implementation path. The record requires:

- the reviewer organisation and an explicit independence attestation;
- all 17 handoff checklist items passed;
- all six exact workflow records reviewed;
- all six product-family package inventories reviewed;
- independent reproduction of the candidate evidence; and
- zero unresolved review findings.

The evidence URL should identify the signed review or protected pull-request
approval. Repository environment protection remains the identity boundary; the
JSON record does not replace GitHub's prevent-self-review control.

## Security review

`security-review.json` requires positive results for threat-model review,
CodeQL, dependency review, the complete NuGet advisory audit, both SBOM
formats and provenance verification. It separately records the number of
external secret-scanner records dispositioned and requires zero unresolved
secret findings, vulnerabilities and blocking security findings.

An intentional local test credential is not self-dispositioning. Its checked-in
inventory proves its constrained repository use; the external scanner record
must still be independently resolved or accepted and referenced here.

## Application pilots

`application-pilot-a.json` and `application-pilot-b.json` each require:

- a named application, operator organisation and acceptance owner;
- an independent-operator attestation;
- at least 24 hours, 1,000 application operations and 100 transactions;
- PostgreSQL 15 through 19, the tested topology and at least one enabled
  product family;
- successful candidate upgrade and a validated rollback path;
- observed peak CPU percentage and peak memory bytes;
- at least one declared SLO, zero SLO violations and zero blocking defects.

Candidate mode additionally requires different application names, operator
organisations and accountable approvers between pilot A and pilot B. A sample
application run twice by the maintainer is not two independent pilots. Retain
the workload definition, topology, version inventory, time-series metrics,
defect log and acceptance decision behind each evidence URL.

## Website deployment acceptance

`website-deployment-acceptance.json` binds the public acceptance result to the
archived `production-metrics.json` SHA-256. It requires:

- an HTTPS public origin, valid certificate and minimum TLS 1.2;
- working SPA fallback;
- immutable caching for hashed assets and revalidation/no-cache behavior for
  `index.html`;
- successful compression and security-header checks;
- zero broken links;
- at least two desktop browsers and one mobile browser; and
- at least 100 field samples over 28 days.

The 75th-percentile field limits are LCP at most 2,500 ms, INP at most 200 ms
and CLS at most 0.1. These are the current "good" Core Web Vitals thresholds;
BlueTusk's 100-sample minimum is an additional V1 evidence rule. Preserve the
segmented raw field report so a reviewer can distinguish desktop, mobile,
routes and low-sample populations. Laboratory Lighthouse output is diagnostic
evidence but cannot replace field measurements.

The thresholds and 75th-percentile interpretation follow the
[Web Vitals definition](https://web.dev/articles/vitals); the 28-day field
window matches the window documented for
[PageSpeed Insights and CrUX](https://web.dev/articles/vitals-tools).

## Backup and restore rehearsal

`backup-restore-rehearsal.json` proves restore, not merely backup creation. It
requires an encrypted backup restored into an empty isolated target, plus:

- backup identity and operator;
- equal source/restored object counts and row counts;
- equal source/restored checkpoint positions;
- observed recovery-point gap no greater than declared RPO;
- observed restore duration no greater than declared RTO;
- zero integrity mismatches; and
- successful post-restore reconciliation.

Retain the backup inventory, encryption/control record, timed operator log,
source and restored hashes, checkpoint output and reconciliation report. Never
restore over the source environment to manufacture this evidence.

## Rollback rehearsal

`rollback-rehearsal.json` names the candidate version, rollback version,
representative trigger, decision authority and measured duration. It requires
successful version compatibility, connection drain, durable-format
compatibility, relay/checkpoint ownership, Live client reset, Control Plane
fencing and final reconciliation, with zero data-loss events.

Rollback evidence must use packages reconstructed from the immutable candidate
and the declared previous release. Replacing an archive in place or manually
advancing a checkpoint is not a rollback.

## Incident-response game day

`incident-response-game-day.json` records one representative failure or
saturation scenario and at least one affected V1 SLO. It captures measured
detection, triage and mitigation seconds and requires:

- detection through shipped BlueTusk telemetry;
- use of the documented runbook;
- preservation of durable state;
- a completed postmortem;
- named follow-up owners; and
- zero blocking follow-ups.

Keep the incident timeline, alert notification, relevant metrics/traces/logs,
operator actions, mitigation evidence and postmortem behind the reference.
Non-blocking improvements may remain open when owned; a blocker cannot.

## SLO owner approval

`slo-owner-approval.json` requires named service owners to accept at least 14
V1 SLOs, five recovery objectives and 20 alert rules. Alert routing, the
error-budget policy and on-call coverage must be confirmed, with zero unowned
SLOs. The reference should include the exact SLO contract, alert configuration,
route test and ownership record used for the candidate.

## Maintainer sign-off

`maintainer-signoff.json` is the final accountable decision, not a substitute
for earlier gates. It lists at least six frozen product-family versions and
requires:

- an immutable candidate;
- accepted dependency-ordered publication;
- confirmation that publication switches are still disabled before approval;
- accepted stop conditions;
- a named rollback authority; and
- a UTC release window.

The sign-off is valid only after PostgreSQL 19 GA, every protected workflow,
endurance and disturbance evidence, both pilots, all rehearsals and every other
approval pass for the same commit.

## Protected archive

Place the ten files directly in an `approvals/` directory. Place the reviewed
operational-disturbance report and its 28 injection/recovery records in the
sibling `disturbances/` directory. Create a ZIP containing those two top-level
directories, base64-encode it outside CI logs, and store the result as the
protected `V1_APPROVAL_EVIDENCE_BASE64` environment secret.

The candidate workflow restores the archive, rejects duplicate or missing
approval filenames, downloads exact workflow artifacts, hashes every approval
into `candidate.json`, and runs the complete candidate verifier. Keep the
original evidence outside the repository in access-controlled, retention-bound
storage. The uploaded readiness artifact is retained for 90 days; organisational
release records may require longer retention.

Any candidate code, version, dependency, workflow, publication policy or
package-content change invalidates the approval set. Regenerate the affected
measurements and approvals for the new commit rather than editing the prior
archive in place.
