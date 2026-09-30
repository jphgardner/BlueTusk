# BlueTusk Control Plane for Kubernetes

`BlueTusk.ControlPlane.Kubernetes` turns namespaced `BlueTuskDeployment`
resources into the existing fenced managed-hosting state machine. It does not
read Kubernetes Secret values. Custom resources contain secret references only;
the selected infrastructure provider resolves them inside its own identity and
audit boundary.

The reconciler adds a finalizer before any provider mutation, maps arbitrary
Kubernetes generation jumps onto consecutive durable managed generations,
uses the Control Plane's lease and fencing token for provider calls, and writes
only non-sensitive state and diagnostic codes to the status subresource. A
protected deployment keeps its finalizer during deletion until an independently
authorised operator removes protection.

Install the packaged CRD and minimum RBAC before starting a host that uses
`KubernetesManagedDeploymentOperator`. Configure the supplied `HttpClient` with
the Kubernetes API base address, service-account bearer token, and cluster CA;
the package deliberately does not weaken certificate validation or take
ownership of credentials.
