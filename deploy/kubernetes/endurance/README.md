# Kubernetes release endurance

This lane runs the exact BlueTusk 1.2 release candidate in the isolated
`bluetusk-endurance` namespace. It distributes destination services across the
cluster instead of nesting them inside one Docker-in-Docker runner. All images
are digest pinned, service-account tokens are disabled, traffic is isolated by
default, and reports are written continuously to a retained block volume.

The launcher refuses to start a gate unless the exact candidate commit is an
ancestor of `origin/main`. Streams must complete and verify before Sync can
start, and Sync must complete and verify before the combined Live/Control Plane
gate can start. Continuous Graph is deliberately absent until PostgreSQL 19 GA
has an official digest-pinned image; Beta 3 must never satisfy that stable gate.

```powershell
./eng/deploy-kubernetes-endurance.ps1 -Action Prepare
./eng/deploy-kubernetes-endurance.ps1 -Action Status

# Optional preliminary run from any exact commit available on origin.
# This is PostgreSQL 19 Beta 3 evidence and never satisfies a release gate.
./eng/deploy-kubernetes-endurance.ps1 `
  -Action StartContinuousGraphPreview `
  -CandidateSha <full-remote-sha> `
  -CandidateVersion 1.2.0-rc.1

# After the reviewed 1.2 RC commit is merged to main:
./eng/deploy-kubernetes-endurance.ps1 `
  -Action StartStreams `
  -CandidateSha <full-main-sha> `
  -CandidateVersion 1.2.0-rc.1

# Only after streams-72h reports Complete:
./eng/deploy-kubernetes-endurance.ps1 `
  -Action StartSync `
  -CandidateSha <same-full-main-sha> `
  -CandidateVersion 1.2.0-rc.1

# Only after sync-24h reports Complete:
./eng/deploy-kubernetes-endurance.ps1 `
  -Action StartLiveControlPlane `
  -CandidateSha <same-full-main-sha> `
  -CandidateVersion 1.2.0-rc.1

./eng/deploy-kubernetes-endurance.ps1 `
  -Action DownloadEvidence `
  -Output artifacts/kubernetes-endurance-evidence
```

`Cleanup` deletes the namespace and requires `-ConfirmCleanup`. Because the two
PVCs use the retain storage class, deleting the namespace does not silently
destroy the underlying evidence volumes; an operator must explicitly archive
or remove retained volumes afterward.

The final pre-GA sequence is therefore 72 hours of Streams, 24 hours of Sync,
then 24 hours of Live/Control Plane against the same immutable main commit.
The Live/Control Plane harness churns 10,000 Live rows, checks authoritative
drift, scans a 256-deployment fleet, and verifies every operation's
Requested/Succeeded audit transition while recording allocation, GC, latency,
and working-set evidence.
