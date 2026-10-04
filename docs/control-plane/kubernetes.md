# Manage deployments with Kubernetes

This guide shows you how to declare BlueTusk deployments as Kubernetes
`BlueTuskDeployment` resources and have the Control Plane reconcile them:
install the custom resource definition (CRD) and RBAC, run a reconciler host,
apply a resource and read its status.

**New in 1.1.0.** The `BlueTusk.ControlPlane.Kubernetes` package is not in
`1.0.0` or `1.1.0-rc.1`.

## What the package does, and what you write

```text
 kubectl apply ─► BlueTuskDeployment (namespace/name)
                        │  list, add finalizer, patch status
                        ▼
                 your reconciler host ──► KubernetesManagedDeploymentOperator
                                               │
                         PostgreSQL ◄── ManagedDeploymentController ──► your provider
                  (desired state, leases)                              (creates workloads)
```

The package gives you:

- the `controlplane.bluetusk.io/v1alpha1` `BlueTuskDeployment` CRD;
- a ServiceAccount, ClusterRole and ClusterRoleBinding;
- `KubernetesManagedDeploymentOperator`, which turns each resource into a
  managed deployment and writes the result to the resource's status;
- `KubernetesApiManagedDeploymentClient`, a small Kubernetes REST client.

You write:

- a host process that calls the reconciler on a timer;
- an `IManagedInfrastructureProvider` that creates, updates and deletes the
  actual workloads. BlueTusk does not ship one.

The reconciler never reads Kubernetes Secrets. A resource holds secret
*references* only; your provider resolves them with its own identity.

Read [Fleet operations](concepts.md#fleet-operations) for the model behind
generations, states and leases.

## Before you start

You need:

- a Kubernetes cluster and `kubectl` with rights to create CRDs and cluster
  roles;
- a PostgreSQL database the reconciler can reach, for desired state and
  leases;
- a container registry for your reconciler image.

## 1. Install the CRD and RBAC

The manifests are in the repository under
[`deploy/kubernetes/operator`](../../deploy/kubernetes/operator/). The NuGet
package carries the same files under
`contentFiles/any/any/kubernetes/` in its package folder (for example
`~/.nuget/packages/bluetusk.controlplane.kubernetes/<version>/`).

```powershell
kubectl create namespace bluetusk-system
kubectl apply -f deploy/kubernetes/operator/bluetuskdeployments.controlplane.bluetusk.io.yaml
kubectl apply -f deploy/kubernetes/operator/rbac.yaml
kubectl get crd bluetuskdeployments.controlplane.bluetusk.io
```

`rbac.yaml` creates the `bluetusk-control-plane-operator` ServiceAccount in
`bluetusk-system`. Its ClusterRole can `get`, `list`, `watch` and `patch`
`bluetuskdeployments`, and `get`, `patch` and `update` their status. It has no
access to Secrets or any other resource. Grant your provider's own permissions
to a separate identity.

## 2. Write the reconciler host

Create a console app and add the packages:

```powershell
dotnet new console --framework net10.0 --name BlueTuskReconciler
cd BlueTuskReconciler
dotnet add package BlueTusk.ControlPlane.Kubernetes
dotnet add package BlueTusk.Data
```

Replace `Program.cs`:

```csharp
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using BlueTusk.ControlPlane;
using BlueTusk.ControlPlane.Kubernetes;
using BlueTusk.Data;

var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_CONNECTION_STRING")
    ?? throw new InvalidOperationException("Set BLUETUSK_CONNECTION_STRING first.");
await using var dataSource = new BlueTuskDataSourceBuilder(connectionString).Build();

// Durable desired state, status and leases (schema "bluetusk_control").
var store = new PostgreSqlManagedDeploymentStore(dataSource);
await store.InitializeAsync();

var controller = new ManagedDeploymentController(
    store,
    store,
    new ManagedDeploymentQuotaSource(
        store,
        new Dictionary<string, ManagedTenantQuota>
        {
            ["commerce"] = new(
                MaximumDeployments: 10,
                MaximumReplicas: 50,
                MaximumCpuMillicores: 50_000,
                MaximumMemoryBytes: 64L * 1024 * 1024 * 1024,
                MaximumStorageBytes: 1024L * 1024 * 1024 * 1024),
        }),
    new ManagedInfrastructureProviderResolver([new MyKubernetesProvider()]),
    owner: Environment.GetEnvironmentVariable("HOSTNAME") ?? Environment.MachineName);

var reconciler = new KubernetesManagedDeploymentOperator(
    store,
    controller,
    new KubernetesApiManagedDeploymentClient(CreateInClusterClient()),
    maximumConcurrency: 4);

using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
do
{
    try
    {
        foreach (var result in await reconciler.ReconcileAllAsync())
        {
            Console.WriteLine(
                $"{result.DeploymentId}: succeeded={result.Succeeded} " +
                $"changed={result.Changed} diagnostic={result.DiagnosticCode ?? "none"}");
        }
    }
    catch (Exception exception)
    {
        // Listing the resources failed, so this pass did nothing. Try again on the next tick.
        Console.Error.WriteLine($"Reconcile pass failed: {exception.GetType().Name}");
    }
}
while (await timer.WaitForNextTickAsync());

// Calls the Kubernetes API with the pod's service account and the cluster CA.
static HttpClient CreateInClusterClient()
{
    const string account = "/var/run/secrets/kubernetes.io/serviceaccount";
    var clusterCa = X509Certificate2.CreateFromPemFile(Path.Combine(account, "ca.crt"));
    var tls = new SocketsHttpHandler();
    tls.SslOptions.CertificateChainPolicy = new X509ChainPolicy
    {
        TrustMode = X509ChainTrustMode.CustomRootTrust,
        CustomTrustStore = { clusterCa },
    };

    return new HttpClient(new ServiceAccountToken(Path.Combine(account, "token"), tls))
    {
        BaseAddress = new Uri("https://kubernetes.default.svc"),
    };
}

// Reads the token on every request, because Kubernetes rotates it.
sealed class ServiceAccountToken(string path, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = (await File.ReadAllTextAsync(path, cancellationToken)).Trim();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken);
    }
}

// The provider named in each resource's spec.provider. BlueTusk does not ship one:
// this is where you create or update the Deployments, StatefulSets and Services.
sealed class MyKubernetesProvider : IManagedInfrastructureProvider
{
    public string Name => "kubernetes";

    public ValueTask<ManagedDeploymentPlan> PlanAsync(
        ManagedDeploymentSpec desired,
        ManagedDeploymentStatus current,
        CancellationToken cancellationToken = default)
    {
        var fingerprint = ManagedDeploymentValidation.GetFingerprint(desired);
        return ValueTask.FromResult(new ManagedDeploymentPlan(
            desired.DeploymentId,
            desired.Generation,
            DesiredFingerprint: fingerprint,
            PlanFingerprint: fingerprint,
            RequiresChange: current.AppliedPlanFingerprint != fingerprint,
            Actions: [new ManagedDeploymentAction("apply", desired.DeploymentId, "Apply workloads")]));
    }

    public ValueTask<ManagedProviderResult> ApplyAsync(
        ManagedDeploymentSpec desired,
        ManagedDeploymentPlan plan,
        long fencingToken,
        CancellationToken cancellationToken = default)
    {
        // Create or update workloads here. Reject a fencingToken older than the last one you saw.
        return ValueTask.FromResult(new ManagedProviderResult(desired.DeploymentId, plan.PlanFingerprint));
    }

    public ValueTask DeleteAsync(
        ManagedDeploymentSpec desired,
        long fencingToken,
        CancellationToken cancellationToken = default)
    {
        // Delete workloads here. It must be safe to call more than once.
        return ValueTask.CompletedTask;
    }
}
```

Points to check:

- **Provider name.** `Name` must equal `spec.provider` in your resources.
  Otherwise the status shows `provider-not-registered`.
- **Quotas.** Every `spec.tenantId` needs a quota (or pass a `defaultQuota`).
  Otherwise the status shows `tenant-quota-missing`.
- **Owner.** The lease owner names this process. The pod's `HOSTNAME` is
  unique per pod. Run one replica: leases stop two replicas from applying the
  same deployment at once, but the one that loses reports `lease-unavailable`
  in the resource status.
- **Provider rules.** `ApplyAsync` and `DeleteAsync` must be safe to repeat
  for the same deployment, generation and fencing token, and must reject a
  fencing token older than the newest one they have accepted.
- **TLS.** The client trusts only the cluster CA and keeps normal certificate
  and host-name checks. Do not turn certificate validation off.

## 3. Run the host in the cluster

Build a container image of the host and run it as a one-replica Deployment in
`bluetusk-system` with:

- `serviceAccountName: bluetusk-control-plane-operator`;
- `BLUETUSK_CONNECTION_STRING` from a Kubernetes Secret, using a login that
  owns, or has been granted rights on, the `bluetusk_control` schema;
- the default service-account token mount (the code reads
  `/var/run/secrets/kubernetes.io/serviceaccount`).

## 4. Write a BlueTuskDeployment

This is the repository's `example.yaml`:

```yaml
apiVersion: controlplane.bluetusk.io/v1alpha1
kind: BlueTuskDeployment
metadata:
  name: orders
  namespace: production
spec:
  tenantId: commerce
  provider: kubernetes
  region: uk-south
  deleteProtection: true
  labels:
    environment: production
    owner: commerce-platform
  workloads:
    - kind: Streams
      version: 1.1.0
      resources:
        replicas: 2
        cpuMillicoresPerReplica: 500
        memoryBytesPerReplica: 536870912
        storageBytes: 10737418240
      secretReferences:
        - store: kubernetes
          name: orders-database
      settings:
        durability: relay
    - kind: Sync
      version: 1.1.0
      resources:
        replicas: 2
        cpuMillicoresPerReplica: 500
        memoryBytesPerReplica: 536870912
        storageBytes: 0
      secretReferences:
        - store: kubernetes
          name: orders-destinations
      settings:
        pipeline: orders
```

| Field | Required | Default | Rules |
| --- | --- | --- | --- |
| `spec.tenantId` | Yes | | 1 to 128 characters |
| `spec.provider` | Yes | | Must match a registered provider's `Name` |
| `spec.region` | Yes | | 1 to 128 characters |
| `spec.paused` | No | `false` | `true` stops changes; state becomes `Paused` |
| `spec.deleteProtection` | No | `true` | While `true`, deleting the resource does not delete the workloads |
| `spec.labels` | No | | Up to 128 entries |
| `spec.workloads[]` | Yes | | 1 to 32, at most one per `kind` |
| `workloads[].kind` | Yes | | `Streams`, `Sync`, `Live`, `ControlPlane`, `Dashboard` or `ContinuousGraph` |
| `workloads[].version` | Yes | | Starts with a numeric version, such as `1.1.0`. The CRD does not check this; a value such as `latest` makes the resource `Failed` with `workload-version-invalid` |
| `workloads[].resources` | Yes | | `replicas` 1 to 256, `cpuMillicoresPerReplica` 10 to 1,000,000, `memoryBytesPerReplica` 16 MiB to 16 TiB, `storageBytes` 0 to 16 TiB |
| `workloads[].secretReferences[]` | No | | `store`, `name`, optional `version`; up to 128 |
| `workloads[].settings` | No | | Up to 256 string entries |

The managed deployment ID is `<namespace>/<name>`, for example
`production/orders`. Keep it to 128 characters or fewer; a longer ID is
reported as `resource-invalid`.

## 5. Apply it and read the status

```powershell
kubectl create namespace production
kubectl apply -f deploy/kubernetes/operator/example.yaml
kubectl get bluetuskdeployments -A
```

The short name is `btd`. The list shows the **State**, **Desired** (Kubernetes
generation), **Observed**, **Tenant** and **Age** columns. On the first pass
the reconciler adds the `controlplane.bluetusk.io/finalizer` finalizer, stores
the desired state, reconciles it and writes the status:

```yaml
status:
  observedGeneration: 1
  managedGeneration: 1
  state: Ready
  updatedAt: "2026-10-03T09:46:13.18+00:00"
```

| Status field | Meaning |
| --- | --- |
| `observedGeneration` | The resource's `metadata.generation` the reconciler last processed |
| `managedGeneration` | The Control Plane's desired generation. It rises by one per real change, however many edits Kubernetes coalesced |
| `state` | `Pending`, `Planning`, `Applying`, `Ready`, `Degraded`, `Paused`, `Deleting`, `Deleted` or `Failed` |
| `diagnosticCode` | Present when something stopped reconciliation, for example `tenant-quota-missing` |

Each resource is reconciled on its own. A resource that is invalid or fails
gets `state: Failed` and a `diagnosticCode`; the others in the same pass are
still reconciled. [Troubleshooting](troubleshooting.md#kubernetes-reconciler)
lists every code.

The deployment also appears on the dashboard's **Deployments** page when the
dashboard host registers `ManagedDeploymentFleetQueryService` over the same
store (see [Enable operator actions](operations.md#6-enable-fleet-operations)).

## 6. Change, pause and delete

- **Change.** Edit the resource. If the desired state really changed, the next
  pass raises `managedGeneration` by one and reconciles.
- **Pause.** Set `spec.paused: true`. The resource is the source of truth: a
  pause or resume made from the dashboard is undone on the next reconciler
  pass, so change the resource instead.
- **Delete.** With `deleteProtection: false`, `kubectl delete` makes the
  reconciler call your provider's `DeleteAsync`, then remove the finalizer.
  With `deleteProtection: true`, the resource stays in `Terminating`, its
  status shows `state: Failed` and `diagnosticCode: delete-protection-enabled`,
  and nothing is deleted.

To delete a protected deployment, turn protection off first. Set
`deleteProtection: false` in `example.yaml`, apply it, and wait until
`managedGeneration` goes up, which shows the reconciler stored the change:

```powershell
kubectl apply -f deploy/kubernetes/operator/example.yaml
kubectl get btd orders -n production -o jsonpath='{.status.managedGeneration}'
kubectl delete btd orders -n production
```

> **Warning:** Once a protected resource is already `Terminating`, editing its
> spec has no effect; the reconciler only retries the deletion with the stored
> desired state. Removing the finalizer by hand deletes the resource but
> leaves your workloads and the stored record in place.

## Next steps

- [Troubleshooting](troubleshooting.md#kubernetes-reconciler): status codes
  and stuck resources.
- [Configuration](configuration.md#kubernetes-reconciler): reconciler and
  client settings.
- [ADR 0014: managed hosting reconciliation](../architecture/decisions/0014-managed-hosting-reconciliation.md)
