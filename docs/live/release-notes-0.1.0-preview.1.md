# BlueTusk Live 0.1.0-preview.1

This is the first packaging-ready BlueTusk Live preview. It provides trusted,
authorised, resumable real-time queries over BlueTusk Streams while preserving
PostgreSQL/EF as the source of every client-visible row.

## NuGet packages

- `BlueTusk.Live`
- `BlueTusk.Live.AspNetCore`
- `BlueTusk.Live.Aspire`
- `BlueTusk.Live.DependencyInjection`
- `BlueTusk.Live.EntityFrameworkCore`
- `BlueTusk.Live.Grpc`
- `BlueTusk.Live.ServerSentEvents`
- `BlueTusk.Live.SignalR`
- `BlueTusk.Live.Testing`

## npm packages

- `@bluetusk/live`
- `@bluetusk/live-angular`
- `@bluetusk/live-react`

Every package uses the repository's MIT licence and the independent Live
`0.1.0-preview.1` version property.

## Capabilities in this preview

- trusted startup registration of bounded, deterministic EF query plans;
- typed scalar parameters with no client SQL, LINQ, or expression trees;
- security-scoped subscription identities that include tenant/user scope and
  authorisation-policy version;
- gap-free initial result delivery across cursor reservation, query, and
  invalidation replay;
- authoritative EF requery with keyed add/update/remove/reorder/reset events;
- signed, expiring, rotating, subscription-bound resume tokens;
- integrity-checked, sequence-fenced PostgreSQL replay with bounded retention;
- security-partitioned shared queries, quotas, bounded client buffers,
  slow-client policy, and load shedding;
- authenticated SSE, SignalR, and gRPC server streaming;
- framework-neutral TypeScript state, Angular signals, and React concurrent
  store adapters;
- vetted one-to-many includes/projections, grouping, aggregates, and
  PostgreSQL full-text query shapes; and
- Aspire configuration, deterministic testing stores, OpenTelemetry, and
  redacted Control Plane visibility.

The release gate covers the complete offline solution suite, all 45 Live tests
against PostgreSQL 19, and a PostgreSQL 15–19 acceptance matrix that persists
an initial result and affected update in the production replay store, then
drives signed disconnect/resume delivery through real SSE, SignalR/WebSockets,
and HTTP/2 gRPC endpoints. It also builds all three TypeScript packages, passes
the browser-client tests, audits NuGet and npm dependencies, and inspects all
nine NuGet and three npm package artifacts.

## Preview boundaries

- Delivery and invalidation processing are at least once. Stable IDs,
  transaction deduplication, and sequence fencing support idempotency; exactly
  once is not claimed.
- CDC is only an invalidation signal. PostgreSQL/EF authorisation, RLS, tenant
  filters, and the registered query are reapplied before rows reach a client.
- Only vetted bounded query shapes are accepted. Unsupported queries fail at
  startup with diagnostics.
- Replay is intentionally bounded. An expired cursor produces an explicit
  reset or reconnect requirement, never a silent gap.
- The checked-in load figures are regression budgets, not universal production
  latency or capacity claims.
- This preview gate does not represent Live 1.0 and does not complete the
  separate Streams 72-hour, Sync 24-hour, or Control Plane release gates.

The independent workflow creates candidate NuGet and npm artifacts on manual
dispatch but cannot publish them. Live publication remains disabled until its
V1 dependencies and exact-commit release gates pass; only the exact matching
tag may enter the protected production environment.
