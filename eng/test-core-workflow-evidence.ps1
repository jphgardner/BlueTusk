[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$configuration = Join-Path $PSScriptRoot 'v1.1-candidate-readiness.json'
$contract = Get-Content -LiteralPath $configuration -Raw | ConvertFrom-Json
$scratch = Join-Path ([IO.Path]::GetTempPath()) "bluetusk-core-workflow-$([Guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $scratch
$commit = '0' * 40
$example = [ordered]@{
    schemaVersion = 4; candidateCommit = $commit
    workflowRuns = @(for ($i = 0; $i -lt $contract.requiredWorkflows.Count; $i++) {
        @{
            workflowFile = $contract.requiredWorkflows[$i]; headSha = $commit
            event = 'workflow_dispatch'; conclusion = 'success'; runId = ($i + 1)
            runAttempt = 1; completedUtc = "2026-01-01T00:00:0$($i + 1)Z"
            url = "https://github.com/jphgardner/BlueTusk/actions/runs/$($i + 1)"
        }
    })
}
function Verify-Fixture
{
    param($Fixture)
    $path = Join-Path $scratch 'fixture.json'
    $Fixture | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8NoBOM
    & (Join-Path $PSScriptRoot 'verify-v1-workflow-evidence.ps1') `
        -EvidencePath $path -ExpectedCommit $commit -CandidateCommitUtc '2025-12-31T23:59:59Z' `
        -ConfigurationPath $configuration -ExpectedRepository 'jphgardner/BlueTusk'
}
try
{
    $result = Verify-Fixture $example
    if ($result.RunCount -ne 7) { throw 'Expected seven exact core workflow records.' }
    $mutations = @(
        {param($r) $r.schemaVersion = 3},
        {param($r) $r.workflowRuns[6].workflowFile = 'continuous-graph-release-endurance.yml'},
        {param($r) $r.workflowRuns[3].workflowFile = 'performance.yml'},
        {param($r) $r.workflowRuns[0].headSha = ('1' * 40)},
        {param($r) $r.workflowRuns[0].url = 'https://github.com/another/repository/actions/runs/1'},
        {param($r) $r.workflowRuns[0].event = 'push'},
        {param($r) $r.workflowRuns[0].conclusion = 'failure'},
        {param($r) $r.workflowRuns[0].runAttempt = 0},
        {param($r) $r.workflowRuns = @($r.workflowRuns | Select-Object -First 6)}
    )
    foreach ($mutation in $mutations)
    {
        $copy = $example | ConvertTo-Json -Depth 8 | ConvertFrom-Json
        & $mutation $copy
        $rejected = $false
        try { Verify-Fixture $copy | Out-Null } catch { $rejected = $true }
        if (-not $rejected) { throw 'Invalid core workflow fixture was accepted.' }
    }
    Write-Output 'Synthetic core workflow self-tests passed: one seven-run set and nine rejected mutations. No workflows were triggered or certified.'
}
finally
{
    if ((Split-Path $scratch -Parent) -ne [IO.Path]::GetTempPath().TrimEnd([IO.Path]::DirectorySeparatorChar))
    { throw 'Refusing unexpected self-test cleanup target.' }
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
