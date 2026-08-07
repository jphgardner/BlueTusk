[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$verifier = Join-Path $PSScriptRoot 'verify-v1-workflow-evidence.ps1'
$examplePath = Join-Path $PSScriptRoot 'v1-candidate-evidence.example.json'
$example = Get-Content -LiteralPath $examplePath -Raw | ConvertFrom-Json
$zeroCommit = '0' * 40
$candidateUtc = [DateTimeOffset]'2025-12-31T23:59:59Z'
$temporaryRoot = Join-Path (
    [IO.Path]::GetTempPath()
) "bluetusk-v1-workflow-self-test-$([Guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $temporaryRoot

function Copy-Example
{
    return (
        $example | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    )
}

function Write-Fixture
{
    param(
        [Parameter(Mandatory)]
        [object] $Evidence,

        [Parameter(Mandatory)]
        [string] $Name
    )

    $path = Join-Path $temporaryRoot "$Name.json"
    $Evidence | ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath $path -Encoding utf8NoBOM
    return $path
}

function Assert-Rejected
{
    param(
        [Parameter(Mandatory)]
        [scriptblock] $Mutate,

        [Parameter(Mandatory)]
        [string] $Name
    )

    $fixture = Copy-Example
    & $Mutate $fixture
    $path = Write-Fixture $fixture $Name
    try
    {
        & $verifier `
            -EvidencePath $path `
            -ExpectedCommit $zeroCommit `
            -CandidateCommitUtc $candidateUtc *> $null
        throw "Negative workflow fixture '$Name' was accepted."
    }
    catch
    {
        if ($_.Exception.Message -eq "Negative workflow fixture '$Name' was accepted.")
        {
            throw
        }
    }
}

try
{
    $positivePath = Write-Fixture (Copy-Example) 'positive'
    $result = & $verifier `
        -EvidencePath $positivePath `
        -ExpectedCommit $zeroCommit `
        -CandidateCommitUtc $candidateUtc
    if ([int]$result.RunCount -ne 7 -or
        [DateTimeOffset]$result.LatestCompletedUtc -ne
            [DateTimeOffset]'2026-01-01T00:00:07Z')
    {
        throw 'Positive workflow evidence returned an unexpected summary.'
    }

    Assert-Rejected -Name 'schema-one' -Mutate {
        param($fixture)
        $fixture.schemaVersion = 1
    }
    Assert-Rejected -Name 'duplicate-run-id' -Mutate {
        param($fixture)
        $fixture.workflowRuns[1].runId = $fixture.workflowRuns[0].runId
        $fixture.workflowRuns[1].url = $fixture.workflowRuns[0].url
    }
    Assert-Rejected -Name 'zero-attempt' -Mutate {
        param($fixture)
        $fixture.workflowRuns[0].runAttempt = 0
    }
    Assert-Rejected -Name 'precommit-completion' -Mutate {
        param($fixture)
        $fixture.workflowRuns[0].completedUtc = '2025-12-31T23:59:58Z'
    }
    Assert-Rejected -Name 'future-completion' -Mutate {
        param($fixture)
        $fixture.workflowRuns[0].completedUtc = (
            [DateTimeOffset]::UtcNow.AddDays(1).ToString('O')
        )
    }
    Assert-Rejected -Name 'wrong-url-host' -Mutate {
        param($fixture)
        $fixture.workflowRuns[0].url = 'https://evidence.example/actions/runs/1'
    }
    Assert-Rejected -Name 'unexpected-field' -Mutate {
        param($fixture)
        $fixture.workflowRuns[0] |
            Add-Member -NotePropertyName trusted -NotePropertyValue $true
    }
}
finally
{
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
}

Write-Output (
    'V1 workflow-evidence verifier self-test passed: one complete seven-run set ' +
    'and seven fail-closed mutations.')
