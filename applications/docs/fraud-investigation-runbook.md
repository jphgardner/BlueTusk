# Fraud Graph Investigator RC runbook

Owner: fraud operations. Data: `bluetusk-fraud-rc/application`. Entry point:
`https://fraud.192.168.1.230.nip.io`.

During a suspected integrity incident, disable transfer ingestion but retain read-only case
access, capture the current graph evaluation/checkpoint, and preserve all decision evidence.
Cancel an in-flight evaluation, force authoritative repair from accounts/transfers, and require
zero stale or out-of-order paths before ingestion resumes.

After worker or PostgreSQL restart, verify replay resumes after the last committed result and
that case assignment/decision versions remain monotonic. A disconnect test must demonstrate
automatic connection recovery without duplicate transfers. An expired Keycloak session must
return to authentication and must not expose cached tenant data.

Restore to a new CloudNativePG cluster, run migrations, validate `fraud_graph`, compare account,
transfer, case, and decision counts, then rebuild graph state. Roll back API, worker, and UI by
their recorded digests; keep the immutable evidence audit and restored database available for
incident review.
