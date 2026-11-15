[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$head = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $head -cne $ExpectedCommit)
{ throw 'The active Core performance contract requires the exact candidate checkout.' }

$paths = @(
    'eng/release-tracks.json',
    'eng/performance-leadership-contract.json',
    'eng/performance-evidence-plan.json'
)
& git -C $root diff --quiet $ExpectedCommit -- @paths
if ($LASTEXITCODE -ne 0) { throw 'The candidate release or performance contract has local changes.' }

$tracks = Get-Content -LiteralPath (Join-Path $root $paths[0]) -Raw | ConvertFrom-Json
$release = [string]$tracks.releaseVersion
$confirmation = switch ($release)
{
    '1.1.0' { 'CAPTURE-1.1-PERFORMANCE-EVIDENCE' }
    '1.2.0' { 'CAPTURE-1.2-CORE-PERFORMANCE-EVIDENCE' }
    default { throw "Core performance release '$release' is not supported." }
}
$output = Join-Path $root "artifacts/core-performance-contract/$ExpectedCommit"
$null = New-Item -ItemType Directory -Path $output -Force

# Keep the approved workload and statistical definitions byte-identical. The
# evidence version and dispatch token are the only release-specific fields.
$historicalRelease = '"release": "1.1.0"'
$historicalConfirmation = '"confirmationToken": "CAPTURE-1.1-PERFORMANCE-EVIDENCE"'
$contract = Get-Content -LiteralPath (Join-Path $root $paths[1]) -Raw
$plan = Get-Content -LiteralPath (Join-Path $root $paths[2]) -Raw
if ([regex]::Matches($contract, [regex]::Escape($historicalRelease)).Count -ne 1 -or
    [regex]::Matches($plan, [regex]::Escape($historicalRelease)).Count -ne 1 -or
    [regex]::Matches($plan, [regex]::Escape($historicalConfirmation)).Count -ne 1)
{ throw 'Could not identify the historical Core contract and plan release fields.' }

$releaseField = '"release": "' + $release + '"'
$contract = $contract.Replace($historicalRelease, $releaseField)
$plan = $plan.Replace($historicalRelease, $releaseField).Replace(
    $historicalConfirmation, ('"confirmationToken": "' + $confirmation + '"'))
$utf8 = [Text.UTF8Encoding]::new($false)
$contractPath = Join-Path $output 'performance-leadership-contract.json'
$planPath = Join-Path $output 'performance-evidence-plan.json'
[IO.File]::WriteAllText($contractPath, $contract, $utf8)
[IO.File]::WriteAllText($planPath, $plan, $utf8)
[pscustomobject]@{ Release = $release; Confirmation = $confirmation; ContractPath = $contractPath; PlanPath = $planPath }
