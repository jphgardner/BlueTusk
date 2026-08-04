[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $DistributionPath,

    [Parameter(Mandatory)]
    [string] $MetricsPath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string] $ExpectedCommit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$distribution = (Resolve-Path -LiteralPath $DistributionPath).Path
$metricsFile = (Resolve-Path -LiteralPath $MetricsPath).Path
if (-not (Test-Path -LiteralPath $distribution -PathType Container) -or
    -not (Test-Path -LiteralPath $metricsFile -PathType Leaf))
{
    throw 'Website evidence requires an existing distribution and production metrics file.'
}

$distributionPrefix = $distribution.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $metricsFile.StartsWith(
        $distributionPrefix,
        [StringComparison]::OrdinalIgnoreCase))
{
    throw 'The website production metrics file must be inside the distribution.'
}

$metrics = Get-Content -LiteralPath $metricsFile -Raw | ConvertFrom-Json
if ([int]$metrics.schemaVersion -ne 1 -or
    -not [string]::Equals(
        [string]$metrics.sourceCommit,
        $ExpectedCommit,
        [StringComparison]::OrdinalIgnoreCase))
{
    throw 'Website production metrics are not schema 1 evidence for the expected commit.'
}

$contract = Get-Content -LiteralPath (
    Join-Path $repositoryRoot 'website/production-contract.json') -Raw | ConvertFrom-Json
foreach ($limitName in @(
        'initialRawBytes',
        'initialBrotliBytes',
        'largestLazyBrotliBytes',
        'totalDistributionBytes'))
{
    $expectedLimit = [long]$contract.limits.$limitName
    $reportedLimit = [long]$metrics.limits.$limitName
    $measured = [long]$metrics.metrics.$limitName
    if ($reportedLimit -ne $expectedLimit -or
        $measured -lt 0 -or
        $measured -gt $expectedLimit)
    {
        throw (
            "Website metric '$limitName' does not satisfy the checked-in production contract: " +
            "measured $measured, reported limit $reportedLimit, expected limit $expectedLimit.")
    }
}

$reportedFiles = @($metrics.files)
$reportedPaths = @($reportedFiles | ForEach-Object { [string]$_.path })
if ($reportedFiles.Count -ne [int]$metrics.assetCount -or
    $reportedFiles.Count -eq 0 -or
    @($reportedPaths | Sort-Object -Unique).Count -ne $reportedFiles.Count)
{
    throw 'Website production metrics contain an empty, duplicate or inconsistent file manifest.'
}

$actualFiles = @(
    Get-ChildItem -LiteralPath $distribution -Recurse -File |
        Where-Object {
            -not [string]::Equals(
                $_.FullName,
                $metricsFile,
                [StringComparison]::OrdinalIgnoreCase)
        }
)
if ($actualFiles.Count -ne $reportedFiles.Count)
{
    throw (
        'Website distribution file count does not match production metrics: ' +
        "$($actualFiles.Count) actual, $($reportedFiles.Count) reported.")
}

foreach ($reportedFile in $reportedFiles)
{
    $candidate = Join-Path $distribution ([string]$reportedFile.path)
    $resolved = (Resolve-Path -LiteralPath $candidate).Path
    if (-not $resolved.StartsWith(
            $distributionPrefix,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $resolved -PathType Leaf))
    {
        throw "Website distribution path '$($reportedFile.path)' is invalid or escapes its root."
    }

    $actualHash = (
        Get-FileHash -LiteralPath $resolved -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    $actualLength = (Get-Item -LiteralPath $resolved).Length
    if ([string]$reportedFile.sha256 -notmatch '^[0-9a-f]{64}$' -or
        $actualHash -ne [string]$reportedFile.sha256 -or
        $actualLength -ne [long]$reportedFile.bytes)
    {
        throw "Website distribution asset '$($reportedFile.path)' failed integrity verification."
    }
}

Write-Output (
    "Verified $($reportedFiles.Count) website assets for commit " +
    "$($ExpectedCommit.ToLowerInvariant()): " +
    "$([long]$metrics.metrics.initialRawBytes) initial raw bytes, " +
    "$([long]$metrics.metrics.initialBrotliBytes) initial Brotli bytes, " +
    "$([long]$metrics.metrics.largestLazyBrotliBytes) largest lazy Brotli bytes and " +
    "$([long]$metrics.metrics.totalDistributionBytes) total bytes.")
