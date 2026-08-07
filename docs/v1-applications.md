# V1 application suite and RC deployment

BlueTusk V1 is exercised by three independently deployable applications in
[`applications/BlueTusk.Applications.slnx`](../applications/BlueTusk.Applications.slnx):

- **Order Fulfilment Operations** uses Provider/EF for its write model,
  Streams for its durable relay, Sync for the operational projection, Live
  SignalR delivery, and the Control Plane dashboard.
- **Service Topology Centre** uses PostgreSQL 19 SQL/PGQ, ContinuousGraph,
  Streams replay/invalidation, Angular Live delivery, and graph status in the
  Control Plane.
- **Fraud Graph Investigator** uses PostgreSQL 19 property graphs,
  ContinuousGraph evaluation, Streams, the framework-neutral Live client, and
  immutable case evidence.

The applications consume exact `1.0.0-rc.1` NuGet and npm packages. They have
no project reference to BlueTusk production source. Each backend is split into
Domain, Application, Infrastructure, API, and Worker projects, and each has a
browser client, Dockerfiles, a Helm deployment, seed data, SLOs, and an
operator runbook. The shared BFF implements OIDC sessions, secure cookies,
CSRF protection, CSP, rate limiting, tenant/role enforcement, RFC 9457 errors,
health endpoints, and OpenTelemetry.

## Local RC verification

The 2026-08-07 working-tree verification restored the application solution
from locally packed exact `1.0.0-rc.1` artifacts and produced:

| Gate | Result |
| --- | --- |
| Package-only architecture | 20 projects; 44 exact BlueTusk package references; no BlueTusk source project references |
| Release build | Zero warnings and zero errors |
| Application tests | 15 passed, including a disposable digest-pinned PostgreSQL 19 Beta 2 migration/integration test |
| Browser journeys | Three passed in Chromium through Playwright |
| Order UI | 203.83 kB JavaScript; 64.44 kB gzip |
| Topology UI | 127.92 kB JavaScript; 38.58 kB estimated transfer |
| Fraud UI | 206.02 kB JavaScript; 65.01 kB gzip |

The integration test applies checked-in migrations and covers tenant
isolation, command idempotency, topology dependencies and paths, blast-radius
analysis, incidents, fraud accounts/transfers, multi-hop analysis, alert
rules, optimistic case assignment/decision, and the immutable evidence audit.
This is reproducible engineering evidence for a mutable working tree, not
candidate or pilot evidence.

## Property-graph migrations

Generated migrations use a typed fluent builder. Application migrations do
not contain serialized graph definitions or caller-authored DDL:

```csharp
migrationBuilder.CreatePropertyGraph(
    "fraud_graph",
    graph => graph
        .Vertex(
            "accounts",
            "accounts",
            "fraud",
            vertex => vertex
                .HasKey("Id")
                .HasLabel(
                    "account",
                    label => label.Property("Id").Property("DisplayName")))
        .Edge(
            "transfers",
            "transfers",
            "fraud",
            edge => edge
                .HasKey("Id")
                .HasLabel(
                    "transfer",
                    label => label.Property("Id").Property("Amount"))
                .HasSource("accounts", ["SourceId"], ["Id"])
                .HasDestination("accounts", ["DestinationId"], ["Id"])),
    "fraud");
```

The public definition overload remains compatible. The former serialized
metadata overload remains hidden only for binary/source compatibility; new and
generated migrations use the fluent form. The builder validates aliases,
labels, endpoints, key cardinality, and property names before adding the
migration operation.

This intentionally adds 16 Provider signatures before candidate freeze. The
Provider API budget and shipped hash manifest are updated in the same change.
It is not valid to reuse candidate workflow evidence recorded before this API
contract change.

## RC staging boundary

The checked-in deployment targets the Kubernetes `proxmox-homelab` context
through digest-pinned images, Traefik Gateway API, `homelab-ca`, Longhorn,
CloudNativePG 1.29.1, Keycloak 26.7, and pinned observability charts. RC values
use reduced replica counts while one worker is unavailable; production values
retain the full availability topology. Secrets are pre-created and are never
stored in Helm values or evidence artifacts.

Publication and deployment remain external operations. They require a
reviewed immutable `main` SHA, protected `package-prerelease` approval,
NuGet/npm/GHCR credentials, the declared Kubernetes Secrets, installed
operators, registry hash/provenance verification, and digest-pinned rollout
evidence. PostgreSQL 19 Beta 2 is staging-only and carries no production claim.

RC observations do not count as either formal V1 pilot. After PostgreSQL 19
GA and sequential stable publication, Orders is intended as pilot A and Fraud
as pilot B; together they exercise all six families and ContinuousGraph.
