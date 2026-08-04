[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$contract = Get-Content -LiteralPath (
    Join-Path $PSScriptRoot 'fuzzing-contract.json') -Raw | ConvertFrom-Json
if ([int]$contract.schemaVersion -ne 1)
{
    throw "Expected fuzzing-contract schema 1; found '$($contract.schemaVersion)'."
}

$expectedTargets = @($contract.targets | ForEach-Object { [string]$_ })
if ($expectedTargets.Count -ne 9 -or
    @($expectedTargets | Sort-Object -Unique).Count -ne $expectedTargets.Count)
{
    throw 'The V1 fuzzing contract must declare nine unique targets.'
}

foreach ($relativePath in @($contract.requiredFiles))
{
    $path = Join-Path $repositoryRoot ([string]$relativePath)
    if (-not (Test-Path -LiteralPath $path -PathType Leaf))
    {
        throw "Required fuzzing asset '$relativePath' is missing."
    }
}

function Assert-ExactTargets
{
    param(
        [Parameter(Mandatory)]
        [string] $Description,

        [Parameter(Mandatory)]
        [string[]] $Actual
    )

    $actualTargets = @($Actual | Sort-Object -Unique)
    $expected = @($expectedTargets | Sort-Object -Unique)
    $missing = @($expected | Where-Object { $_ -notin $actualTargets })
    $unexpected = @($actualTargets | Where-Object { $_ -notin $expected })
    if ($missing.Count -ne 0 -or
        $unexpected.Count -ne 0 -or
        $actualTargets.Count -ne $expected.Count)
    {
        throw (
            "$Description does not match the V1 fuzzing target contract. " +
            "Missing: $($missing -join ', '); unexpected: $($unexpected -join ', ').")
    }
}

$targetsSource = Get-Content -LiteralPath (
    Join-Path $repositoryRoot 'tests/BlueTusk.Fuzzing/FuzzTargets.cs') -Raw
$registeredTargets = @(
    [regex]::Matches($targetsSource, '\["(?<target>[a-z0-9-]+)"\]\s*=') |
        ForEach-Object { $_.Groups['target'].Value }
)
Assert-ExactTargets -Description 'FuzzTargets registration' -Actual $registeredTargets

$runFuzzSource = Get-Content -LiteralPath (
    Join-Path $repositoryRoot 'eng/run-fuzz.ps1') -Raw
$validateSet = [regex]::Match(
    $runFuzzSource,
    '(?s)\[ValidateSet\((?<values>.*?)\)\]\s*\[string\]\s+\$Target')
if (-not $validateSet.Success)
{
    throw 'run-fuzz.ps1 has no target ValidateSet.'
}
$scriptTargets = @(
    [regex]::Matches($validateSet.Groups['values'].Value, "'(?<target>[a-z0-9-]+)'") |
        ForEach-Object { $_.Groups['target'].Value }
)
Assert-ExactTargets -Description 'run-fuzz.ps1 ValidateSet' -Actual $scriptTargets

$workflowSource = Get-Content -LiteralPath (
    Join-Path $repositoryRoot '.github/workflows/fuzzing.yml') -Raw
$matrix = [regex]::Match(
    $workflowSource,
    '(?ms)^\s+matrix:\s*\r?\n\s+target:\s*\r?\n' +
    '(?<values>(?:\s+-\s+[a-z0-9-]+\s*\r?\n)+)')
if (-not $matrix.Success)
{
    throw 'fuzzing.yml has no readable target matrix.'
}
$workflowTargets = @(
    [regex]::Matches($matrix.Groups['values'].Value, '(?m)^\s+-\s+(?<target>[a-z0-9-]+)\s*$') |
        ForEach-Object { $_.Groups['target'].Value }
)
Assert-ExactTargets -Description 'fuzzing.yml target matrix' -Actual $workflowTargets

$corpusRoot = Join-Path $repositoryRoot 'tests/fuzz-corpus'
$corpusTargets = @(
    Get-ChildItem -LiteralPath $corpusRoot -Directory |
        ForEach-Object { $_.Name }
)
Assert-ExactTargets -Description 'Encoded corpus directories' -Actual $corpusTargets

$maximumEncodedLength = [int](
    [Math]::Ceiling([int]$contract.limits.maximumInputBytes / 3.0) * 4)
$corpusCaseCount = 0
foreach ($target in $expectedTargets)
{
    $cases = @(
        Get-ChildItem -LiteralPath (Join-Path $corpusRoot $target) -Filter '*.b64' -File
    )
    if ($cases.Count -eq 0)
    {
        throw "Fuzz target '$target' has no replayable encoded corpus case."
    }

    foreach ($case in $cases)
    {
        $encoded = (Get-Content -LiteralPath $case.FullName -Raw).Trim()
        if ([string]::IsNullOrWhiteSpace($encoded) -or
            $encoded.Length -gt $maximumEncodedLength -or
            $encoded -notmatch '^[A-Za-z0-9+/]*={0,2}$')
        {
            throw "Encoded corpus case '$($case.FullName)' is empty, malformed or oversized."
        }
        $corpusCaseCount++
    }
}

$limits = $contract.limits
$maximumInputKib = [int]$limits.maximumInputBytes / 1024
foreach ($requiredSource in @(
        "public const int MaximumInputBytes = $maximumInputKib * 1024;",
        "public const int MaximumMessagesPerInput = $([int]$limits.maximumMessagesPerInput);"))
{
    if (-not $targetsSource.Contains($requiredSource, [StringComparison]::Ordinal))
    {
        throw "FuzzTargets.cs does not enforce '$requiredSource'."
    }
}

foreach ($requiredWorkflowSource in @(
        "-ExecutionTimeoutMilliseconds $([int]$limits.executionTimeoutMilliseconds)",
        "-MemoryLimitMegabytes $([int]$limits.managedHeapMegabytes)",
        "-MaximumInputBytes $([int]$limits.maximumInputBytes)",
        "`$duration -lt $([int]$limits.manualDurationSecondsPerTarget)",
        "retention-days: $([int]$limits.artifactRetentionDays)",
        'artifacts/fuzz-${TARGET}-${GITHUB_RUN_ID}.tar.gz'))
{
    if (-not $workflowSource.Contains(
            $requiredWorkflowSource,
            [StringComparison]::Ordinal))
    {
        throw "fuzzing.yml does not enforce '$requiredWorkflowSource'."
    }
}

if (-not $runFuzzSource.Contains("'-m', 'none'", [StringComparison]::Ordinal) -or
    -not $runFuzzSource.Contains('DOTNET_GCHeapHardLimit', [StringComparison]::Ordinal) -or
    -not $runFuzzSource.Contains("'-g', 1", [StringComparison]::Ordinal) -or
    -not $runFuzzSource.Contains("'-G', `$MaximumInputBytes", [StringComparison]::Ordinal))
{
    throw 'run-fuzz.ps1 does not enforce the managed-heap and AFL input-bound contract.'
}

Write-Output (
    "Verified $($expectedTargets.Count) synchronized fuzz targets, $corpusCaseCount encoded corpus " +
    "cases, $([int]$limits.maximumInputBytes)-byte inputs, " +
    "$([int]$limits.executionTimeoutMilliseconds)ms executions, " +
    "$([int]$limits.managedHeapMegabytes) MiB managed heaps and " +
    "$([int]$limits.manualDurationSecondsPerTarget)-second manual candidate runs.")
