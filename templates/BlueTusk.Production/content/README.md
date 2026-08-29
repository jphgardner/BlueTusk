# BlueTusk production starter

This is a complete Clean Architecture order-operations system generated from
the same package-consumer application used by BlueTusk release validation.
It is deliberately more than a hello-world sample.

## What is included

- Domain, Application, Infrastructure, API, and Worker projects with enforced
  inward dependency direction;
- EF Core migrations, optimistic concurrency, idempotent commands, an immutable
  operational audit, and an independently retrying relay worker;
- a same-origin BFF with OIDC, tenant scope, CSRF protection, rate limiting,
  security headers, health checks, and OpenTelemetry;
- a React or Angular BlueTusk Live client;
- unit tests, non-root container builds, a hardened Helm chart, SLOs, and an
  incident/restore/rollback runbook; and
- PostgreSQL 18, Redis, NATS JetStream, and OpenSearch in one local Compose stack.

## Start locally

```console
docker compose up -d
dotnet tool install --global BlueTusk.Tool --version 1.2.0
bluetusk doctor --connection "Host=localhost;Database=bluetusk_orders;Username=bluetusk;Password=local-development-only" --require-streams
dotnet run --project applications/src/OrderOperations/BlueTusk.OrderOperations.Api -- --migrate
```

Then provide the production-hosting settings described in
`applications/docs/order-operations-runbook.md`, run the API and Worker, and
start the selected web client with `npm ci && npm run dev`.

The Compose credentials are intentionally local-only. Kubernetes deployments
must use external Secrets, immutable image digests, TLS, and the checked-in Helm
security defaults. Do not copy local credentials into a cluster.
