# ADR 0014: managed hosting uses fenced desired-state reconciliation

- Status: accepted
- Date: 2026-08-03

## Context

Managed hosting must provision and operate Streams, Sync, Live, the Control
Plane, the Dashboard, and Continuous Graph across more than one infrastructure
provider. Embedding one cloud SDK or accepting raw provider credentials in the
core would couple the product lifecycle to a vendor, broaden the secret
exposure boundary, and make failover between reconcilers unsafe.

A production controller can also be interrupted after provider mutation but
before status persistence. At-least-once reconciliation is therefore
unavoidable. An unversioned mutable deployment document, an unfenced leader
lock, or a provider action without a stable plan identity could turn that
redelivery into duplicate infrastructure or allow an expired controller to
overwrite a newer owner.

## Decision

Managed hosting is a Control Plane capability built around these contracts:

- `ManagedDeploymentSpec` is versioned desired state. Its generation advances
  by exactly one through compare-and-swap storage.
- tenant, provider, and region are immutable placement identity. Moving a
  deployment is an explicit new deployment and cutover, not an in-place field
  edit;
- workloads carry bounded replica, CPU, memory, and storage requests. Tenant
  quota is enforced before a plan can reach a provider;
- workloads contain only `ManagedSecretReference` values. The Control Plane
  never accepts, resolves, serializes, logs, or returns secret material;
- canonical desired-state and provider-plan fingerprints make retries and
  drift decisions stable across process restarts and dictionary order;
- one exclusive lease owns reconciliation. Every acquisition advances a
  monotonically increasing fencing token, renews while provider work is in
  flight, and supplies that token to every provider mutation;
- provider adapters plan, apply, and delete through
  `IManagedInfrastructureProvider`. They must make apply/delete idempotent for
  deployment, generation, plan fingerprint, and fencing token;
- observed state advances with a separate compare-and-swap revision and only
  against the desired generation it observed;
- provider exception messages are not durable status. The store receives a
  stable diagnostic code while the original exception remains available to
  the host's protected logs;
- delete protection requires an explicit override, expected desired
  generation, and the normal fenced lease;
- PostgreSQL is the production store. Its schema migration is serialized,
  desired documents have an explicit format version, future formats fail
  closed, and lease expiry is measured by the database clock; and
- the in-memory store is for tests and single-process development only.

Cloud and Kubernetes adapters remain provider-specific packages or host
components. They may resolve secret references inside their own credential
boundary, but their public plan and result must remain non-sensitive.

## Consequences

The same controller can operate multiple hosting environments without making
the Control Plane a credential broker. A crash after apply safely retries the
same plan; the provider adapter must detect that stable identity and converge.
Lease loss cancels in-flight work, while downstream fencing prevents a stale
operation that ignored cancellation from becoming authoritative.

The model deliberately does not provide an arbitrary “run infrastructure
command” escape hatch. Provider adapters need conformance tests for
idempotency, stale-token rejection, bounded plans, cancellation, drift,
partial failure, and deletion before they qualify for a managed service.
