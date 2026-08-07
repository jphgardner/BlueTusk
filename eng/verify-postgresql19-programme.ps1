[CmdletBinding()]
param(
    [string] $RepositoryRoot = (Split-Path $PSScriptRoot -Parent),
    [switch] $VerifyOfficialCurrent,
    [switch] $RequireGeneralAvailability
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$manifestPath = Join-Path $RepositoryRoot 'eng/postgresql19-programme.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.majorVersion -ne 19)
{
    throw 'Expected PostgreSQL 19 programme schema 1.'
}
if (@($manifest.requiredFutureCadence).Count -ne 3)
{
    throw 'PostgreSQL 19 must require later beta, release-candidate and GA cadence.'
}

$current = @($manifest.milestones | Where-Object {
    $_.version -eq $manifest.currentOfficialMilestone
})
if ($current.Count -ne 1 -or $current[0].status -ne 'verified')
{
    throw (
        "Current PostgreSQL milestone '$($manifest.currentOfficialMilestone)' " +
        'must have one verified record.')
}
if ([string]$current[0].image -notmatch
    '^postgres:19[^@\s]+@sha256:[0-9a-f]{64}$')
{
    throw 'The current PostgreSQL 19 candidate image must be pinned by digest.'
}
foreach ($path in @($current[0].evidence, $manifest.typedSubsetRecord))
{
    if (-not (Test-Path -LiteralPath (Join-Path $RepositoryRoot $path) -PathType Leaf))
    {
        throw "PostgreSQL 19 evidence '$path' is missing."
    }
}

$compose = Get-Content -LiteralPath (
    Join-Path $RepositoryRoot 'eng/compose/postgres.yml') -Raw
if (-not $compose.Contains([string]$current[0].image, [StringComparison]::Ordinal))
{
    throw 'The PostgreSQL 19 compose service does not use the recorded image digest.'
}

if ($VerifyOfficialCurrent)
{
    $response = Invoke-WebRequest `
        -Uri ([string]$manifest.officialDocumentationUrl) `
        -MaximumRedirection 5
    if (-not $response.Content.Contains(
        [string]$manifest.currentDocumentationMarker,
        [StringComparison]::OrdinalIgnoreCase))
    {
        throw (
            "Official PostgreSQL 19 documentation no longer identifies " +
            "'$($manifest.currentOfficialMilestone)'. Record and verify the new " +
            'beta, RC or GA milestone before the gate can pass.')
    }
}

if ($RequireGeneralAvailability)
{
    $ga = $manifest.generalAvailability
    if ($ga.status -ne 'verified' -or
        [string]$ga.version -notmatch '^19(?:\.0)?$' -or
        [string]$ga.image -notmatch '^postgres:19(?:\.0)?[^@\s]*@sha256:[0-9a-f]{64}$' -or
        [string]$ga.sourceCommit -notmatch '^[0-9a-f]{40}$' -or
        [string]::IsNullOrWhiteSpace([string]$ga.evidence) -or
        -not (Test-Path -LiteralPath (
            Join-Path $RepositoryRoot ([string]$ga.evidence)) -PathType Leaf))
    {
        throw (
            'Stable publication is blocked until PostgreSQL 19 GA has a ' +
            'digest-pinned image and exact-commit compatibility evidence.')
    }
    if (-not [string]::Equals(
            [string]$manifest.currentOfficialMilestone,
            [string]$ga.version,
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string]$current[0].image,
            [string]$ga.image,
            [StringComparison]::Ordinal))
    {
        throw (
            'Stable publication requires PostgreSQL 19 GA to be the current ' +
            'official milestone and the compose-pinned milestone image.')
    }
}

Write-Host (
    "PostgreSQL 19 programme verified at $($manifest.currentOfficialMilestone); " +
    "GA status is $($manifest.generalAvailability.status).")
