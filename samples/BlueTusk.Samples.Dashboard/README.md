# BlueTusk Dashboard preview host

This sample hosts the real `BlueTusk.Dashboard` endpoint renderer. Most
control-plane inventories remain representative and redacted so the public UI
does not expose a production database. Continuous Graph is different: when
`BLUETUSK_GRAPH_CONNECTION_STRING` is configured, the host compiles and
executes a registered, read-only PostgreSQL 19 `GRAPH_TABLE` query and renders
its complete bounded result over a continuously discovered Kubernetes
topology.

The preview has two independent mutation barriers:

- its authenticated preview identity has only the viewer role; and
- the `ControlPlaneOperationExecutor` uses an authorizer that denies every
  operation even if an endpoint policy is changed accidentally.

Run it locally:

```powershell
dotnet run --project samples/BlueTusk.Samples.Dashboard `
  --urls http://127.0.0.1:5217
```

Open `http://127.0.0.1:5217/bluetusk/overview`. The preview contains several
representative sources, pipelines, Live subscriptions, and deployments in
healthy, catching-up, and degraded states. Every inventory
row is linked to its complete redacted detail view, including nested consumer
groups, snapshots, and checkpoints. Search and health filters make the larger
inventories easy to inspect, and the layout adapts down to phone widths.

The public Kubernetes deployment requires `BLUETUSK_GRAPH_CONNECTION_STRING`
and fails startup when the PostgreSQL registration cannot be compiled. A
separate collector uses a namespace-scoped, read-only Kubernetes service
account to list Deployments, ReplicaSets, StatefulSets, Pods, Services,
Ingresses, Certificates, Jobs, EndpointSlices, container images, and observed
load-balancer addresses. It writes only the bounded topology tables through a
dedicated PostgreSQL role. The public dashboard pod has neither Kubernetes API
credentials nor database write permission.

Every successful scan atomically replaces the previous complete snapshot. An
API, validation, limit, or database failure leaves the last complete snapshot
untouched. Each rendered node carries its Kubernetes API version, namespace,
UID, resource version, observation time, freshness, status, and provenance.
Edges are derived from live owner references, label selectors, ingress
backends, certificate secrets, EndpointSlice targets, image references, and
load-balancer status. The default refresh interval is 30 seconds.

The digest-pinned PostgreSQL 19 Beta 3 preview stores this inventory in
`bluetusk_dashboard.cluster_topology`. A dedicated reader login has only schema
usage, table `SELECT`, and property-graph `SELECT`; no browser-supplied SQL or
security scope is accepted. The graph database initializer, collector RBAC,
workload isolation, and network policies are in
`deploy/kubernetes/dashboard-preview/graph-database.yaml` and
`deploy/kubernetes/dashboard-preview/kubernetes.yaml`. This remains explicit
non-gating preview evidence until PostgreSQL 19 GA is available.

The `/preview` endpoint reports the graph execution mode, database identity,
data classification, and registered fingerprint. Both health
endpoints are unauthenticated for container probes. Public search indexing and
response caching are disabled. The preview identity remains viewer-only and its
operation authorizer denies every mutation independently of the UI.
The public ingress enforces HSTS at the TLS boundary. The host also emits HSTS
when it observes a validated HTTPS request, alongside its same-origin Content
Security Policy and anti-framing, MIME-sniffing, referrer, permissions, cache,
and indexing protections.
