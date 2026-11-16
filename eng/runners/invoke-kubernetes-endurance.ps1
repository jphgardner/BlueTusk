[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Streams', 'Sync', 'LiveControlPlane', 'ContinuousGraph')]
    [string] $Track,

    [Parameter(Mandatory)] [string] $Ref,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9a-f]{40}$')] [string] $Candidate,
    [Parameter(Mandatory)] [string] $KubernetesContext,
    [ValidateRange(0, [long]::MaxValue)] [long] $PackageRunId = 0,
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string] $Repository = 'jphgardner/BlueTusk'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$namespace = 'bluetusk-endurance'
$api = "repos/$Repository"
$trackSettings = switch ($Track) {
    'Streams' { @('streams-release-endurance.yml', 'RUN-STREAMS-72-HOUR-ENDURANCE', 277200) }
    'Sync' { @('sync-release-endurance.yml', 'RUN-SYNC-24-HOUR-ENDURANCE', 99000) }
    'LiveControlPlane' { @('live-control-plane-release-endurance.yml', 'RUN-LIVE-CONTROL-PLANE-24-HOUR-ENDURANCE', 99000) }
    'ContinuousGraph' { @('continuous-graph-release-endurance.yml', 'RUN-CONTINUOUS-GRAPH-24-HOUR-ENDURANCE', 99000) }
}
$workflow, $confirmation, $timeLimit = $trackSettings
$encodedRef = [Uri]::EscapeDataString($Ref)
$refCommit = & gh api "$api/commits/$encodedRef" --jq .sha
if ($LASTEXITCODE -ne 0 -or $refCommit -cne $Candidate) { throw 'The requested ref does not resolve to the candidate SHA.' }
if ($Track -ne 'ContinuousGraph') {
    if ($PackageRunId -eq 0) { throw 'Core endurance requires the canonical package build run ID.' }
    $build = & gh api "$api/actions/runs/$PackageRunId" | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $build.head_sha -cne $Candidate -or $build.event -cne 'workflow_dispatch' -or
        $build.path.Split('@')[0] -cne '.github/workflows/build.yml' -or $build.conclusion -cne 'success') {
        throw 'The package run must be a successful manual build at the exact candidate SHA.'
    }
}
$null = & kubectl --context $KubernetesContext get namespace $namespace -o name
if ($LASTEXITCODE -ne 0) { throw 'Apply kubernetes-endurance-namespace.yaml on the selected cluster first.' }

$name = 'bluetusk-kube-' + [Guid]::NewGuid().ToString('N').Substring(0, 20)
$dispatch = @('-f', "confirmation=$confirmation", '-f', "runner_label=$name")
if ($Track -ne 'ContinuousGraph') { $dispatch += @('-f', "package_run_id=$PackageRunId") }
& gh workflow run $workflow --repo $Repository --ref $Ref @dispatch
if ($LASTEXITCODE -ne 0) { throw 'GitHub did not accept the endurance dispatch.' }

$queuedRun = $null
$queuedJob = $null
$deadline = [DateTimeOffset]::UtcNow.AddMinutes(3)
do {
    $runs = @(& gh run list --repo $Repository --workflow $workflow --commit $Candidate --event workflow_dispatch `
        --limit 8 --json databaseId --jq '.[].databaseId')
    if ($LASTEXITCODE -ne 0) { throw 'Unable to list the dispatched endurance runs.' }
    foreach ($runId in $runs) {
        $jobs = & gh api "$api/actions/runs/$runId/jobs" | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0) { throw "Unable to read jobs for run $runId." }
        $match = @($jobs.jobs | Where-Object { $_.head_sha -ceq $Candidate -and $name -cin $_.labels })
        if ($match.Count -eq 1) {
            $queuedRun = [long] $runId
            $queuedJob = $match[0]
            break
        }
    }
    if ($null -eq $queuedJob -or $queuedJob.status -cne 'queued') { Start-Sleep -Seconds 3 }
} until (($null -ne $queuedJob -and $queuedJob.status -ceq 'queued') -or [DateTimeOffset]::UtcNow -ge $deadline)
if ($null -eq $queuedJob -or $queuedJob.status -cne 'queued') {
    throw "GitHub did not queue the job for $name. Check '$workflow' and its concurrency before retrying."
}
if (@('self-hosted','linux','x64') | Where-Object { $_ -cnotin $queuedJob.labels }) {
    throw 'The exact queued job must target a self-hosted Linux x64 runner.'
}

$runnerId = $null
$jobCreated = $false
try {
    $jitRequest = @{ name = $name; runner_group_id = 1; labels = @('self-hosted','Linux','X64',$name); work_folder = '_work' }
    $registration = $jitRequest | ConvertTo-Json -Compress |
        & gh api --method POST "$api/actions/runners/generate-jitconfig" --input - | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $registration.encoded_jit_config) { throw 'GitHub JIT registration failed.' }
    $runnerId = $registration.runner.id
    $manifest = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'kubernetes-endurance-job.yaml') -Raw).
        Replace('__RUNNER_NAME__', $name).Replace('__TIME_LIMIT__', $timeLimit.ToString([Globalization.CultureInfo]::InvariantCulture))
    $job = $manifest | & kubectl --context $KubernetesContext create -f - -o json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Kubernetes did not create the runner job.' }
    $jobCreated = $true
    $secret = @{
        apiVersion = 'v1'; kind = 'Secret'; immutable = $true; type = 'Opaque'
        metadata = @{
            name = $name; namespace = $namespace
            ownerReferences = @(@{ apiVersion = 'batch/v1'; kind = 'Job'; name = $name; uid = $job.metadata.uid })
        }
        stringData = @{ config = $registration.encoded_jit_config }
    }
    $secret | ConvertTo-Json -Depth 6 -Compress | & kubectl --context $KubernetesContext create -f - | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Kubernetes did not accept the one-job runner configuration.' }
}
catch {
    if ($jobCreated) { & kubectl --context $KubernetesContext -n $namespace delete job $name --ignore-not-found --wait=false | Out-Null }
    if ($null -ne $runnerId) { & gh api --method DELETE "$api/actions/runners/$runnerId" | Out-Null }
    & gh run cancel $queuedRun --repo $Repository | Out-Null
    throw
}

[pscustomobject]@{
    Run = "https://github.com/$Repository/actions/runs/$queuedRun"
    GitHubJob = $queuedJob.id
    KubernetesNamespace = $namespace
    KubernetesJob = $name
}
