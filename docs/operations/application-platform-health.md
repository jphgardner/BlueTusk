# Application platform health and rollout acceptance

BlueTusk's three Clean Architecture reference applications are production-shaped
workloads, but Kubernetes desired state is not proof that a workload is actually
running. A stale Pod object can continue to show `Running` and a stale EndpointSlice
can continue to show `ready: true` after a kubelet or datastore has stopped
reconciling that object. Application rollout acceptance therefore uses independent
control-plane, node-runtime, storage, database and workload checks.

This gate is required before either reference application can begin a formal V1
pilot. It is read-only: it does not restart nodes, delete Pods, repair volumes,
change Secrets, or deploy images.

## Commands

Run the infrastructure preflight before rendering or applying an RC release:

```powershell
./eng/verify-application-platform-health.ps1 -MinimumReadyNodes 2
```

After the digest-pinned Helm rollout, require the complete application state:

```powershell
./eng/verify-application-platform-health.ps1 `
  -MinimumReadyNodes 2 `
  -RequireApplications
```

`deploy-applications-rc.ps1` runs the first form before its server-side dry run
and the second form after every `-Apply` rollout. Running the deployment command
without `-Apply` still exercises the live infrastructure preflight and cannot
produce a misleading render-only success against an unreconciled cluster.

## What the verifier proves

| Surface | Fail-closed requirement | Why Kubernetes status alone is insufficient |
| --- | --- | --- |
| Context | Current context is exactly `proxmox-homelab` | Prevents validating or changing the wrong cluster |
| Nodes | At least the requested number are `Ready` | Capacity claims need an explicit minimum |
| Node pressure | No Ready node reports disk, memory, PID or network pressure | A Ready heartbeat does not mean the node can safely admit or retain workloads |
| API/kubelet parity | Every non-terminal API Pod assigned to a Ready node appears in that kubelet's runtime Pod list by UID | Detects stale API objects, split datastore state and broken kubelet reconciliation |
| Longhorn | Every volume reports `healthy` robustness | Attached or cached PVC status does not prove replica integrity |
| CloudNativePG | Every cluster reports a healthy phase and all declared instances Ready | A Service or Pod object does not prove database quorum/readiness |
| Deployments | API, worker and UI for all three applications have desired, ready and available replicas, with the current generation observed | Prevents old ReplicaSets satisfying a new rollout superficially |
| Active Pods | Every non-terminal application container is Ready | Catches crash loops, image/runtime failures and pending containers |
| Migration Jobs | No application namespace contains a failed Job | A healthy API must not hide an incomplete schema transition |

The protected `applications-images.yml` workflow supplies the preceding image
trust boundary. It builds all nine components, then executes every API and worker
image with `dotnet --list-runtimes`. Both `Microsoft.NETCore.App 10.0.11` and
`Microsoft.AspNetCore.App 10.0.11` must exist before vulnerability scanning,
provenance attestation or digest evidence. Static Dockerfile review is not a
substitute for this executable closure check. Each worker then runs for 20
seconds in a read-only, no-network container with an intentionally unreachable
database. The process must remain alive and preserve its retry loop; an image
that starts but terminates during a dependency outage is rejected.

## Failure interpretation

### API Pods are absent from the kubelet

Treat `Kubelet '<node>' is not reconciling ...` as a control-plane incident, not
an application restart request. Do not delete database Pods, PVCs or Longhorn
replicas while the API and kubelet disagree. Preserve:

- `kubectl get nodes -o wide` and Ready-condition timestamps;
- the API Pod list grouped by `spec.nodeName`;
- `/api/v1/nodes/<node>/proxy/pods/` from each Ready kubelet;
- current MicroK8s/Kubelet and datastore logs; and
- Longhorn volume/replica plus CloudNativePG cluster state.

Repair datastore quorum and node reconciliation through the authorised cluster
console or host access. The gate must then observe the same non-terminal Pod UIDs
from the API and each Ready kubelet. Cached API readiness, a recent Node lease, or
a healthy-looking EndpointSlice cannot waive this check.

### A backend image lacks a shared framework

An immediate .NET process exit (commonly host exit code 150) can mean the image
does not contain a framework recorded in the application's runtime configuration.
Inspect the image with `dotnet --list-runtimes`; do not add roll-forward settings
or install a framework into a running container. Correct the digest-pinned final
base, rebuild from the reviewed commit, rerun the protected closure/scan/attest
workflow, and deploy the new immutable digest.

BlueTusk workers share production hosting code that references
`Microsoft.AspNetCore.App`; consequently API and worker final stages both use the
reviewed ASP.NET Core chiseled runtime. The application verifier locks that base
and the executable workflow assertions together.

### Storage or database health fails

Stop the rollout and preserve recovery evidence. Do not force-delete a volume,
replica or database primary merely to make Kubernetes reschedule it. Confirm the
last usable backup, replica placement, current primary and recovery objective;
repair the storage/database layer first; then require the platform gate to pass
before restarting migration or application rollout.

### A deployment, Pod or migration fails

Use the application runbook in this order: database readiness, migration Job,
worker, API, UI and Live resume. Roll back application images by recorded digest.
Do not reverse or delete durable schema state unless the checked-in rollback was
rehearsed against a restored copy.

## Recovery and acceptance sequence

1. Restore MicroK8s datastore health and kubelet reconciliation on the declared
   Ready nodes.
2. Require API/kubelet Pod UID parity and zero node pressure.
3. Require healthy Longhorn volumes and CloudNativePG instances before changing
   any application object.
4. Build, scan, attest and record all nine application images from the reviewed
   immutable commit; verify backend runtime closure.
5. Deploy each application with `helm upgrade --install --wait --atomic` through
   `deploy-applications-rc.ps1 -Apply`.
6. Require all migrations and nine deployments to pass the post-rollout health
   gate.
7. Exercise readiness endpoints, authentication, the documented browser journey,
   deterministic seed preflight and each application's recovery scenario.
8. Retain the verifier output, image digest manifest, Helm revision, Kubernetes
   events and observation timestamps with the candidate evidence.

Only then may the deployment start its 24-hour pilot window. This platform gate
does not replace pilot traffic, field website measurements, backup/restore,
rollback, incident game day, PostgreSQL 19 GA evidence, independent review or
protected publication approval.

## 2026-08-18 RC audit finding

The live `proxmox-homelab` audit found API objects for application and platform
Pods while every Ready kubelet returned an empty runtime Pod list. MicroK8s logs
repeated node-not-found failures and Kine/Dqlite socket errors. The existing
Order Operations migration Job was failed, and all three worker Deployments had
previously terminated immediately because their final image lacked
`Microsoft.AspNetCore.App`.

Those observations invalidate the deployment as V1 evidence even though several
API objects still reported Ready. Candidate `96b33c3` remains immutable for audit
but is superseded. The corrected source, replacement image evidence, repaired
cluster, passing platform gate and a newly frozen exact candidate are all
required before release acceptance resumes.
