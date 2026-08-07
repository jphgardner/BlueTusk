# ADR 0011: Treat CDC as Live invalidation, not client-visible truth

- Status: Accepted
- Date: 2026-08-03

## Context

Replication tuples do not carry application authorisation context and may omit columns or old values. Projecting them directly to clients risks bypassing row-level security, tenant filters, and current policy decisions.

## Decision

Live queries are registered by trusted server code. Clients provide typed parameters only and cannot submit SQL or expression trees. CDC invalidates affected subscriptions; the engine coalesces changes, reruns the bounded authorised EF query, and diffs keyed results.

ADR 0015 adds a separate, explicitly enabled client-query capability for V1.
It does not change the authoritative-requery decision: an application-issued
database capability, PostgreSQL RLS/read-only role, bounded execution, and the
same security-scoped subscription identity are required.

ADR 0016 permits bounded incremental Continuous Graph maintenance, but only
from authorised affected-key database queries. Uncertainty, removal, worsening
rank, two-phase commit, and configured repair intervals return to a full
authoritative query. CDC tuples remain invalidation and key-discovery inputs,
not client-visible truth.

Shared-subscription identity includes database, plan fingerprint, parameters, tenant/security scope, authorisation-policy version, and result limit. Resume tokens are signed, expiring, versioned, and bound to that identity. Subscriptions never share results or replay across security scopes.

## Consequences

PostgreSQL and EF remain the source of truth for data visible to a client. Live trades some query work for a much smaller security and correctness surface. Unsupported query shapes fail at startup registration with diagnostics.
