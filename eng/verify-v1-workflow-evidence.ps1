[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $EvidencePath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string] $ExpectedCommit,

    [Parameter(Mandatory)]
    [DateTimeOffset] $CandidateCommitUtc
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertTo-VerifiedUtcDateTime
{
    param(
        [Parameter(Mandatory)]
        [object] $Value,

        [Parameter(Mandatory)]
        [string] $Context
    )

    if ($Value -is [DateTimeOffset])
    {
        $parsed = [DateTimeOffset]$Value
        if ($parsed.Offset -ne [TimeSpan]::Zero)
        {
            throw "$Context must be an ISO 8601 UTC timestamp with a Z offset."
        }

        return $parsed
    }
    if ($Value -is [DateTime])
    {
        $dateTime = [DateTime]$Value
        if ($dateTime.Kind -ne [DateTimeKind]::Utc)
        {
            throw "$Context must be an ISO 8601 UTC timestamp with a Z offset."
        }

        return [DateTimeOffset]::new($dateTime)
    }
    if ($Value -isnot [string])
    {
        throw "$Context must be an ISO 8601 UTC timestamp."
    }

    $parsed = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse(
            [string]$Value,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeUniversal,
            [ref]$parsed) -or
        $parsed.Offset -ne [TimeSpan]::Zero)
    {
        throw "$Context must be an ISO 8601 UTC timestamp with a Z offset."
    }

    return $parsed
}

$configuration = Get-Content -LiteralPath (
    Join-Path $PSScriptRoot 'v1-production-readiness.json') -Raw |
    ConvertFrom-Json
$evidence = Get-Content -LiteralPath $EvidencePath -Raw | ConvertFrom-Json
if ([int]$evidence.schemaVersion -ne 3)
{
    throw "Expected candidate-evidence schema 3; found '$($evidence.schemaVersion)'."
}
if (-not [string]::Equals(
        [string]$evidence.candidateCommit,
        $ExpectedCommit,
        [StringComparison]::OrdinalIgnoreCase))
{
    throw (
        "Workflow evidence candidate '$($evidence.candidateCommit)' does not match " +
        "'$ExpectedCommit'.")
}

$requiredWorkflows = @($configuration.requiredWorkflows | ForEach-Object { [string]$_ })
$workflowRuns = @($evidence.workflowRuns)
if ($workflowRuns.Count -ne $requiredWorkflows.Count)
{
    throw (
        'Candidate evidence must contain exactly one run record for each required workflow; ' +
        "expected $($requiredWorkflows.Count), found $($workflowRuns.Count).")
}

$expectedProperties = @(
    'workflowFile',
    'headSha',
    'event',
    'conclusion',
    'runId',
    'runAttempt',
    'completedUtc',
    'url'
)
$runIds = [Collections.Generic.HashSet[long]]::new()
$latestCompletedUtc = $CandidateCommitUtc.ToUniversalTime()
foreach ($requiredWorkflow in $requiredWorkflows)
{
    $matches = @($workflowRuns | Where-Object {
        [string]::Equals(
            [string]$_.workflowFile,
            $requiredWorkflow,
            [StringComparison]::Ordinal)
    })
    if ($matches.Count -ne 1)
    {
        throw (
            "Candidate evidence must contain exactly one '$requiredWorkflow' run; " +
            "found $($matches.Count).")
    }

    $run = $matches[0]
    $actualProperties = @($run.PSObject.Properties.Name)
    $missingProperties = @($expectedProperties | Where-Object {
        $_ -notin $actualProperties
    })
    $unexpectedProperties = @($actualProperties | Where-Object {
        $_ -notin $expectedProperties
    })
    if ($missingProperties.Count -ne 0 -or $unexpectedProperties.Count -ne 0)
    {
        throw (
            "Workflow '$requiredWorkflow' record schema mismatch. Missing: " +
            "$(if ($missingProperties.Count) { $missingProperties -join ', ' } else { '<none>' }); " +
            "unexpected: " +
            "$(if ($unexpectedProperties.Count) { $unexpectedProperties -join ', ' } else { '<none>' }).")
    }

    if (-not [string]::Equals(
            [string]$run.headSha,
            $ExpectedCommit,
            [StringComparison]::OrdinalIgnoreCase) -or
        [string]$run.event -ne 'workflow_dispatch' -or
        [string]$run.conclusion -ne 'success' -or
        [long]$run.runId -le 0 -or
        [int]$run.runAttempt -le 0)
    {
        throw (
            "Workflow '$requiredWorkflow' must be a successful positive-attempt manual " +
            "run for '$ExpectedCommit'.")
    }

    $runId = [long]$run.runId
    if (-not $runIds.Add($runId))
    {
        throw "Candidate workflow run ID '$runId' is duplicated."
    }

    $runUri = [Uri]::new('https://invalid.example')
    if (-not [Uri]::TryCreate(
            [string]$run.url,
            [UriKind]::Absolute,
            [ref]$runUri) -or
        $runUri.Scheme -ne [Uri]::UriSchemeHttps -or
        $runUri.Host -ne 'github.com' -or
        -not $runUri.AbsolutePath.EndsWith(
            "/actions/runs/$runId",
            [StringComparison]::Ordinal))
    {
        throw (
            "Workflow '$requiredWorkflow' URL must be the matching absolute GitHub " +
            "Actions run URL for '$runId'.")
    }

    $completedUtc = ConvertTo-VerifiedUtcDateTime `
        $run.completedUtc `
        "Workflow '$requiredWorkflow' completedUtc"
    if ($completedUtc -lt $CandidateCommitUtc.ToUniversalTime() -or
        $completedUtc -gt [DateTimeOffset]::UtcNow)
    {
        throw (
            "Workflow '$requiredWorkflow' completion time is before the candidate " +
            'commit or in the future.')
    }
    if ($completedUtc -gt $latestCompletedUtc)
    {
        $latestCompletedUtc = $completedUtc
    }
}

Write-Output ([pscustomobject]@{
    RunCount = $workflowRuns.Count
    LatestCompletedUtc = $latestCompletedUtc
})
