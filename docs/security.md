# Security review

Review date: 2026-08-02

Reviewed version: `0.3.0-preview.1`

Scope: BlueTusk provider, EF/design tooling, first-party extensions and identity
adapters, build dependencies, and checked-in test infrastructure.

This is the repository's 1.0 security-gate review record. It is a maintainer
code/configuration review backed by deterministic and live tests; it is not a
claim of independent penetration testing or a production support SLA.

## Threat model and controls

| Boundary | Primary threats | Reviewed controls and evidence |
| --- | --- | --- |
| Connection configuration | Password, token, passfile, or client-key disclosure | Telemetry redaction has an allowlist-shaped payload; callback exceptions discard original messages; client options redact `ToString()`; public connection/data-source strings default to `Persist Security Info=false`; secrets are absent from diagnostic tests and the live parameter-redaction gate. |
| Server identity and transport | Downgrade, hostname/certificate bypass, credential exposure | `VerifyFull` is the default; `SslStream` platform validation and online revocation are used unless an application explicitly supplies the validation callback; required TLS fails if PostgreSQL rejects encryption; channel binding supports require/prefer/disable; cleartext passwords and access-token adapters fail closed on insecure transport by default. |
| Authentication exchange | Offline cracking, replay, token leakage, malformed negotiation | SCRAM-SHA-256/PLUS, OAUTHBEARER, GSSAPI/Kerberos/SSPI, client certificates, MD5 compatibility, and explicitly gated cleartext are covered by unit/conformance tests. OAuth has a real PostgreSQL validator gate and GSSAPI has a real MIT KDC gate. Writable authentication payloads are overwritten after flush and temporary buffers use cryptographic zeroing. |
| Wire protocol | Oversized/negative lengths, truncation, unknown frames, desynchronisation | Frame/message lengths and state transitions are bounded and validated before payload use; fake-server and parser tests cover fragmentation, truncation, unknown messages/OIDs, delayed readiness, protocol errors, cancellation races, and recovery. Broken or undrainable sessions are discarded. |
| Pool boundary | Cross-tenant/session state leakage, poisoned reuse, waiter starvation | A data source owns one immutable configuration; returns roll back transactions and reset session state; health/lifetime checks discard unsafe sessions; cancellation and clear/drain have stress coverage; multi-host pools remain endpoint-partitioned. Applications must not share one data source between different security principals. |
| Commands and schema tooling | SQL injection, unsafe identifier/literal handling, accidental trusted SQL execution | Runtime values use protocol parameters. Provider-generated identifiers and literals use central quoting. APIs that accept SQL expressions, routine bodies, predicates, or migration fragments are documented trusted-code boundaries and retain explicit validation/diagnostics; they do not reinterpret user input as parameters. |
| Diagnostics and captures | SQL, parameters, exception messages, credentials, or tokens in telemetry | Provider activities/metrics expose bounded stable attributes without SQL or parameter values. Slow-command events are opt-in and redacted. Protocol capture requires explicit payload capture, uses bounded records, and supplies a redaction-aware inspector. |
| Dependencies and release | Known vulnerable direct/transitive package, compromised review trail | Restore explicitly enables `NuGetAuditMode=all` at `NuGetAuditLevel=low`; warnings are errors and there are no advisory suppressions. The 2026-08-02 machine-readable audit reported no vulnerable direct or transitive packages. CI has read-only default permissions and pinned major action versions. |
| Test credentials and automation | A disposable credential is mistaken for production material, copied into a release path, or silently replaced by a real secret | `eng/test-credential-inventory.json` records SHA-256 fingerprints rather than repeating values. Its verifier scans workflow and Compose sources, fixes the accepted file/context/count for every literal, requires localhost or named disposable containers, and forbids literal credentials in candidate and publication workflows. Unknown fingerprints and external-host use fail closed. |

## Intentional test credentials and external scanner triage

The repository contains three low-entropy values used only by disposable local
PostgreSQL and MIT Kerberos test infrastructure. They are not credentials for
GitHub, package registries, cloud services, deployed databases or any
maintainer account. The current inventory covers 22 literal occurrences across
workflow and Compose inputs:

- the PostgreSQL credential used by localhost Compose services, PgBouncer and
  their test clients;
- the disposable Kerberos test-principal credential; and
- the disposable KDC database bootstrap value.

`eng/verify-test-credential-inventory.ps1` discovers literal password forms
instead of trusting a hand-maintained occurrence list. Every discovery must
match one registered fingerprint, exact relative file, expected count and
local-only line context. The fingerprint is computed in memory and the
temporary plaintext byte buffer is zeroed. Candidate-readiness and package
publication workflows are explicitly forbidden inventory locations.

The verifier self-test proves that a registered localhost fixture passes while
the same value on an external host and an unregistered literal both fail. It
runs as a V1 engineering gate and inside the manual `security.yml` CodeQL job.

An external scanner finding is not closed merely because this local gate
passes. A security reviewer must inspect the scanner record, confirm that its
fingerprint and locations match this inventory, and explicitly mark it as a
test-credential false positive. A value with any production reuse, unknown
origin, different location or external endpoint must instead be revoked,
removed and handled as a security incident.

## Findings closed by this review

`SEC-001` — public connection-string credential persistence (medium). Before
this review, `BlueTuskConnection.ConnectionString` and
`BlueTuskDataSource.ConnectionString` returned the supplied password for their
full lifetime. They now follow ADO.NET `Persist Security Info=false` semantics:
an immutable data source omits `Password` and `Passfile` immediately, while a
connection omits them after its first successful open and after subsequent
close/reopen cycles. `Persist Security Info=true` is an explicit opt-in.
Deterministic configuration tests and live PostgreSQL acceptance protect the
public/private split.

No unresolved critical, high, or moderate finding was identified in this
review. Future findings are handled under [SECURITY.md](../SECURITY.md), not in
public issues.

## Accepted application boundaries

- A custom server-certificate callback replaces the platform validation
  boundary. The application must perform complete certificate and identity
  validation; a callback that always returns `true` is insecure.
- `Persist Security Info=true`, `Allow Unencrypted Password=true`, and TLS
  disablement are explicit compatibility choices. Their risk belongs to the
  application and deployment that enables them.
- Passwords and ready tokens supplied as .NET strings cannot be zeroed. Prefer
  short-lived callbacks, OS/Kerberos identity, client certificates, or managed
  cloud identity and never log callback results.
- Raw SQL/expression inputs in migration and schema-program APIs are trusted
  developer inputs. Untrusted application data must use parameters.
- Database roles, grants, row-level-security policy correctness, OAuth validator
  policy, KDC/keytab security, certificate issuance, secret storage, network
  isolation, PostgreSQL patching, backups, and server auditing are deployment
  responsibilities.
- BlueTusk is still a preview. Re-run this review before each preview and 1.0
  release, after a new authentication mechanism, or after a material transport,
  pooling, parser, dependency, or diagnostic change.

## Repeatable release checks

```powershell
dotnet restore BlueTusk.slnx
dotnet package list --project BlueTusk.slnx --vulnerable --include-transitive --format json
dotnet test tests/BlueTusk.Security.Tests -c Release --no-restore
dotnet test tests/BlueTusk.Transport.Tests -c Release --no-restore
dotnet test tests/BlueTusk.ConformanceTests -c Release --no-restore
./eng/verify-test-credential-inventory.ps1
```

The PostgreSQL 15–19 matrix, OAuth-validator job, Kerberos/KDC job, provider
stress job, and replication endurance job add the live and long-running gates.
The exact commands and test infrastructure are documented in
[Testing](contributing/testing.md) and [Authentication](ado-net/authentication.md).
