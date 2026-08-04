[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $EvidenceDirectory,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string] $ExpectedCommit,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string] $ExpectedWebsiteProductionMetricsSha256,

    [DateTimeOffset] $NotBeforeUtc = [DateTimeOffset]::MinValue
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$directory = (Resolve-Path -LiteralPath $EvidenceDirectory).Path
if (-not (Test-Path -LiteralPath $directory -PathType Container))
{
    throw "Approval-evidence directory '$directory' does not exist."
}

$contract = Get-Content -LiteralPath (
    Join-Path $PSScriptRoot 'v1-approval-evidence-contract.json') -Raw |
    ConvertFrom-Json
$gateIds = @($contract.gates | ForEach-Object { [string]$_.id })
if ($gateIds.Count -ne 10 -or
    @($gateIds | Select-Object -Unique).Count -ne $gateIds.Count)
{
    throw 'The V1 approval contract must define exactly ten unique gates.'
}

$files = @(Get-ChildItem -LiteralPath $directory -Recurse -File)
$subdirectories = @(Get-ChildItem -LiteralPath $directory -Recurse -Directory)
$expectedNames = @($gateIds | ForEach-Object { "$_.json" })
$unexpectedFiles = @($files | Where-Object {
    $_.Name -notin $expectedNames -or $_.DirectoryName -ne $directory
})
$missingNames = @($expectedNames | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $directory $_) -PathType Leaf)
})
if ($files.Count -ne $expectedNames.Count -or
    $subdirectories.Count -ne 0 -or
    $unexpectedFiles.Count -ne 0 -or
    $missingNames.Count -ne 0)
{
    throw (
        'The approval directory must contain exactly the ten canonical JSON files. ' +
        "Missing: $(if ($missingNames.Count) { $missingNames -join ', ' } else { '<none>' }); " +
        "unexpected: $(if ($unexpectedFiles.Count) { $unexpectedFiles.FullName -join ', ' } else { '<none>' }); " +
        "subdirectories: $(if ($subdirectories.Count) { $subdirectories.FullName -join ', ' } else { '<none>' }).")
}

$verifier = Join-Path $PSScriptRoot 'verify-v1-approval-evidence.ps1'
$approvals = @{}
foreach ($gateId in $gateIds)
{
    $path = Join-Path $directory "$gateId.json"
    & $verifier `
        -EvidencePath $path `
        -ExpectedGateId $gateId `
        -ExpectedCommit $ExpectedCommit `
        -NotBeforeUtc $NotBeforeUtc | Out-Null
    $approvals[$gateId] = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}

$pilotA = $approvals['application-pilot-a']
$pilotB = $approvals['application-pilot-b']
foreach ($identityField in @('applicationName', 'operatorOrganisation'))
{
    if ([string]::Equals(
            [string]$pilotA.details.$identityField,
            [string]$pilotB.details.$identityField,
            [StringComparison]::OrdinalIgnoreCase))
    {
        throw (
            "Application pilots A and B must have distinct '$identityField' values.")
    }
}
if ([string]::Equals(
        [string]$pilotA.approvedBy,
        [string]$pilotB.approvedBy,
        [StringComparison]::OrdinalIgnoreCase))
{
    throw 'Application pilots A and B must have distinct accountable approvers.'
}

$websiteApproval = $approvals['website-deployment-acceptance']
if (-not [string]::Equals(
        [string]$websiteApproval.details.productionMetricsSha256,
        $ExpectedWebsiteProductionMetricsSha256,
        [StringComparison]::OrdinalIgnoreCase))
{
    throw (
        'Website deployment acceptance does not identify the exact archived ' +
        'production-metrics record.')
}

$operationalGateIds = @(
    $gateIds |
        Where-Object { $_ -notin @('independent-release-review', 'maintainer-signoff') }
)
$latestOperationalApprovalUtc = [DateTimeOffset]::MinValue
foreach ($gateId in $operationalGateIds)
{
    $approvedUtc = [DateTimeOffset]$approvals[$gateId].approvedUtc
    if ($approvedUtc -gt $latestOperationalApprovalUtc)
    {
        $latestOperationalApprovalUtc = $approvedUtc
    }
}
$independentReviewUtc = [DateTimeOffset]$approvals[
    'independent-release-review'
].approvedUtc
if ($independentReviewUtc -lt $latestOperationalApprovalUtc)
{
    throw (
        'Independent release review must not predate any operational, security, ' +
        'pilot, recovery, game-day, or SLO approval.')
}
$maintainerSignoffUtc = [DateTimeOffset]$approvals['maintainer-signoff'].approvedUtc
$latestPreSignoffApprovalUtc = $independentReviewUtc
foreach ($gateId in $operationalGateIds)
{
    $approvedUtc = [DateTimeOffset]$approvals[$gateId].approvedUtc
    if ($approvedUtc -gt $latestPreSignoffApprovalUtc)
    {
        $latestPreSignoffApprovalUtc = $approvedUtc
    }
}
if ($maintainerSignoffUtc -lt $latestPreSignoffApprovalUtc)
{
    throw 'Maintainer sign-off must be the final V1 approval decision.'
}

Write-Output (
    "V1 approval-evidence set passed: $($gateIds.Count) gate-specific records, " +
    'two independent pilots, exact website production-metrics binding, and ' +
    'ordered independent review and maintainer sign-off.')
