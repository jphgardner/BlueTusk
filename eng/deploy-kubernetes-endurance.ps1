[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet(
        'Prepare',
        'StartStreams',
        'StartSync',
        'StartLiveControlPlane',
        'StartContinuousGraphPreview',
        'Status',
        'DownloadEvidence',
        'Cleanup')]
    [string] $Action,

    [string] $CandidateSha,

    [string] $CandidateVersion = '1.2.0-rc.1',

    [string] $Output = 'artifacts/kubernetes-endurance-evidence',

    [switch] $ConfirmCleanup
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$manifestRoot = Join-Path $repositoryRoot 'deploy/kubernetes/endurance'
$namespace = 'bluetusk-endurance'

function Invoke-Kubectl
{
    param(
        [Parameter(Mandatory)]
        [string[]] $Arguments,

        [string] $InputText
    )

    if ($null -eq $InputText)
    {
        & kubectl @Arguments
    }
    else
    {
        $InputText | & kubectl @Arguments
    }
    if ($LASTEXITCODE -ne 0)
    {
        throw "kubectl $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function New-RandomSecret
{
    $bytes = [byte[]]::new(32)
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    return [Convert]::ToHexString($bytes).ToLowerInvariant()
}

function Assert-Candidate
{
    if ($CandidateSha -notmatch '^[0-9a-f]{40}$')
    {
        throw 'Start actions require a lowercase full CandidateSha.'
    }
    if ($CandidateVersion -notmatch '^1\.2\.0-rc\.[1-9][0-9]*$')
    {
        throw 'Start actions require an exact 1.2.0 release-candidate version.'
    }

    & git -C $repositoryRoot fetch origin main --no-tags
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Could not refresh origin/main before validating the candidate.'
    }
    & git -C $repositoryRoot merge-base --is-ancestor $CandidateSha origin/main
    if ($LASTEXITCODE -ne 0)
    {
        throw "Candidate '$CandidateSha' is not included in origin/main."
    }
}

function Assert-PreviewCandidate
{
    if ($CandidateSha -notmatch '^[0-9a-f]{40}$')
    {
        throw 'Preview start requires a lowercase full CandidateSha.'
    }
    if ($CandidateVersion -notmatch '^1\.2\.0-rc\.[1-9][0-9]*$')
    {
        throw 'Preview start requires an exact 1.2.0 release-candidate version.'
    }

    & git -C $repositoryRoot fetch origin $CandidateSha --no-tags --depth=1
    if ($LASTEXITCODE -ne 0)
    {
        throw "Preview candidate '$CandidateSha' is not available from origin."
    }
    & git -C $repositoryRoot cat-file -e "$CandidateSha`^{commit}"
    if ($LASTEXITCODE -ne 0)
    {
        throw "Preview candidate '$CandidateSha' is not a commit."
    }
}

function Set-CandidateConfig
{
    $config = [ordered]@{
        apiVersion = 'v1'
        kind = 'ConfigMap'
        metadata = [ordered]@{
            name = 'endurance-candidate'
            namespace = $namespace
            labels = [ordered]@{
                'app.kubernetes.io/part-of' = 'bluetusk-endurance'
                'bluetusk.io/source-commit' = $CandidateSha
            }
        }
        data = [ordered]@{
            CANDIDATE_SHA = $CandidateSha
            CANDIDATE_VERSION = $CandidateVersion
        }
    }
    Invoke-Kubectl -Arguments @('apply', '-f', '-') `
        -InputText ($config | ConvertTo-Json -Depth 8)
}

switch ($Action)
{
    'Prepare'
    {
        Invoke-Kubectl -Arguments @(
            'apply', '-f', (Join-Path $manifestRoot 'namespace.yaml'))

        $secretExists = $true
        & kubectl get secret endurance-runtime -n $namespace *> $null
        if ($LASTEXITCODE -ne 0)
        {
            $secretExists = $false
        }
        if (-not $secretExists)
        {
            $secret = [ordered]@{
                apiVersion = 'v1'
                kind = 'Secret'
                metadata = [ordered]@{
                    name = 'endurance-runtime'
                    namespace = $namespace
                    labels = [ordered]@{
                        'app.kubernetes.io/part-of' = 'bluetusk-endurance'
                    }
                }
                type = 'Opaque'
                stringData = [ordered]@{
                    'postgres-password' = New-RandomSecret
                    's3-access-key' = 'bluetusk-endurance'
                    's3-secret-key' = New-RandomSecret
                }
            }
            Invoke-Kubectl -Arguments @('apply', '-f', '-') `
                -InputText ($secret | ConvertTo-Json -Depth 8)
        }

        Invoke-Kubectl -Arguments @(
            'apply', '-f', (Join-Path $manifestRoot 'postgresql.yaml'))
        Invoke-Kubectl -Arguments @(
            'rollout', 'status', 'statefulset/postgresql', '-n', $namespace,
            '--timeout=10m')
        Write-Output 'Prepared the isolated Kubernetes endurance namespace and PostgreSQL 19 Beta 3 service.'
    }
    'StartStreams'
    {
        Assert-Candidate
        Set-CandidateConfig
        & kubectl delete job streams-72h -n $namespace --ignore-not-found *> $null
        Invoke-Kubectl -Arguments @(
            'apply', '-f', (Join-Path $manifestRoot 'streams-job.yaml'))
        Write-Output "Started Streams 72-hour endurance for $CandidateSha ($CandidateVersion)."
    }
    'StartSync'
    {
        Assert-Candidate
        $streamsSucceeded = (& kubectl get job streams-72h -n $namespace `
            -o jsonpath='{.status.succeeded}' 2>$null) -eq '1'
        if (-not $streamsSucceeded)
        {
            throw 'Sync cannot start until the exact Streams 72-hour Job has completed successfully.'
        }

        $currentSha = & kubectl get configmap endurance-candidate -n $namespace `
            -o jsonpath='{.data.CANDIDATE_SHA}'
        if ($LASTEXITCODE -ne 0 -or $currentSha -ne $CandidateSha)
        {
            throw 'The completed Streams Job is not bound to this exact candidate SHA.'
        }

        Invoke-Kubectl -Arguments @(
            'apply', '-f', (Join-Path $manifestRoot 'sync-services.yaml'))
        foreach ($deployment in @('redis', 'nats', 'kafka', 'minio', 'opensearch'))
        {
            Invoke-Kubectl -Arguments @(
                'rollout', 'status', "deployment/$deployment", '-n', $namespace,
                '--timeout=15m')
        }

        & kubectl delete job sync-24h -n $namespace --ignore-not-found *> $null
        Invoke-Kubectl -Arguments @(
            'apply', '-f', (Join-Path $manifestRoot 'sync-job.yaml'))
        Write-Output "Started Sync 24-hour endurance for $CandidateSha ($CandidateVersion)."
    }
    'StartLiveControlPlane'
    {
        Assert-Candidate
        $syncSucceeded = (& kubectl get job sync-24h -n $namespace `
            -o jsonpath='{.status.succeeded}' 2>$null) -eq '1'
        if (-not $syncSucceeded)
        {
            throw 'Live/Control Plane cannot start until the exact Sync 24-hour Job has completed successfully.'
        }

        $currentSha = & kubectl get configmap endurance-candidate -n $namespace `
            -o jsonpath='{.data.CANDIDATE_SHA}'
        if ($LASTEXITCODE -ne 0 -or $currentSha -ne $CandidateSha)
        {
            throw 'The completed Sync Job is not bound to this exact candidate SHA.'
        }

        & kubectl delete job live-control-plane-24h -n $namespace --ignore-not-found *> $null
        Invoke-Kubectl -Arguments @(
            'apply', '-f', (Join-Path $manifestRoot 'live-control-plane-job.yaml'))
        Write-Output (
            "Started Live/Control Plane 24-hour endurance for " +
            "$CandidateSha ($CandidateVersion).")
    }
    'StartContinuousGraphPreview'
    {
        Assert-PreviewCandidate
        Set-CandidateConfig
        & kubectl delete job continuous-graph-preview-1h -n $namespace `
            --ignore-not-found *> $null
        Invoke-Kubectl -Arguments @(
            'apply', '-f',
            (Join-Path $manifestRoot 'continuous-graph-preview-job.yaml'))
        Write-Output (
            "Started the non-gating PostgreSQL 19 Beta 3 Continuous Graph " +
            "preview for $CandidateSha ($CandidateVersion).")
    }
    'Status'
    {
        Invoke-Kubectl -Arguments @(
            'get', 'pods,jobs,pvc', '-n', $namespace, '-o', 'wide')
    }
    'DownloadEvidence'
    {
        $fullOutput = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $Output))
        $artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
        if (-not $fullOutput.StartsWith(
                $artifactsRoot + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase))
        {
            throw "Evidence output '$fullOutput' must be beneath '$artifactsRoot'."
        }
        [IO.Directory]::CreateDirectory($fullOutput) | Out-Null

        $helper = @'
apiVersion: v1
kind: Pod
metadata:
  name: evidence-reader
  namespace: bluetusk-endurance
spec:
  automountServiceAccountToken: false
  restartPolicy: Never
  containers:
    - name: reader
      image: mcr.microsoft.com/dotnet/sdk:10.0.400-noble@sha256:0e53453ccfc8ff2d51319fe80c678971c6d0f8008dff3565fa88e15840b69854
      command: [sh, -c, 'while true; do sleep 3600; done']
      resources:
        requests: {cpu: 10m, memory: 16Mi}
        limits: {cpu: 100m, memory: 64Mi}
      securityContext:
        allowPrivilegeEscalation: false
        capabilities:
          drop: [ALL]
      volumeMounts:
        - {name: evidence, mountPath: /evidence, readOnly: true}
  volumes:
    - name: evidence
      persistentVolumeClaim:
        claimName: endurance-evidence
'@
        & kubectl delete pod evidence-reader -n $namespace --ignore-not-found *> $null
        Invoke-Kubectl -Arguments @('apply', '-f', '-') -InputText $helper
        Invoke-Kubectl -Arguments @(
            'wait', '--for=condition=Ready', 'pod/evidence-reader', '-n', $namespace,
            '--timeout=5m')
        & kubectl cp "$namespace/evidence-reader:/evidence/endurance/." $fullOutput
        if ($LASTEXITCODE -ne 0)
        {
            throw 'Could not copy endurance evidence from the retained volume.'
        }
        & kubectl delete pod evidence-reader -n $namespace --wait=true *> $null
        Write-Output "Downloaded Kubernetes endurance evidence to '$fullOutput'."
    }
    'Cleanup'
    {
        if (-not $ConfirmCleanup)
        {
            throw 'Cleanup deletes the endurance namespace; pass -ConfirmCleanup explicitly.'
        }
        Invoke-Kubectl -Arguments @('delete', 'namespace', $namespace)
        Write-Output 'Deleted the Kubernetes endurance namespace; retained PVs require separate explicit disposal.'
    }
}
