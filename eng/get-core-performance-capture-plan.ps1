[CmdletBinding()]
param(
    [ValidateSet('qualification', 'diagnostic')][string] $EvidenceClass = 'qualification',
    # Diagnostic runs may select a subset; qualification always plans every leg.
    [string] $Families = 'Provider,Streams,Sync,Live,ControlPlane,primary-hot-path',
    [string] $VariantMapPath = (Join-Path $PSScriptRoot 'performance-variant-map.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Emits the GitHub Actions matrix for core-performance-evidence.yml from the data-driven tables.
$contract = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'performance-leadership-contract.json') -Raw | ConvertFrom-Json
$plan = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'performance-evidence-plan.json') -Raw | ConvertFrom-Json
$map = Get-Content -LiteralPath $VariantMapPath -Raw | ConvertFrom-Json
$known = @('Provider', 'Streams', 'Sync', 'Live', 'ControlPlane', 'primary-hot-path')
$selected = @($Families.Split(',', ([StringSplitOptions]::RemoveEmptyEntries -bor [StringSplitOptions]::TrimEntries)))
if ($selected.Count -eq 0 -or @($selected | Where-Object { $_ -cnotin $known }).Count -ne 0 -or
    @($selected | Select-Object -Unique).Count -ne $selected.Count)
{ throw "Families must be a comma-separated subset of: $($known -join ', ')." }
if ($EvidenceClass -eq 'qualification') { $selected = $known }

$legs = [Collections.Generic.List[object]]::new()
foreach ($environment in @($contract.environments | Sort-Object { if ($_.os -eq 'windows') { 0 } else { 1 } }))
{
    $os = [string]$environment.os
    $runner = (@($plan.runners.$os) | ConvertTo-Json -Compress)
    if ((($environment.runnerLabels | ConvertTo-Json -Compress)) -cne $runner) { throw "Runner labels for $os differ from the contract." }
    if ('Provider' -cin $selected)
    {
        foreach ($variant in $contract.workloads.Provider.variants)
        {
            $profileName = [string]$map.variants.$os.$variant
            if ($profileName -ceq 'crossOsProfile') { $profileName = [string]$map.crossOsProfile }
            $status = if ($profileName -ceq 'unresolved') { 'unresolved' }
                elseif ($os -cnotin @($map.profiles.$profileName.hostOs)) { 'unavailable-on-host' }
                else { 'ready' }
            # Diagnostic runs skip legs that cannot run; qualification keeps them so the run fails closed.
            if ($EvidenceClass -eq 'diagnostic' -and $status -ne 'ready') { continue }
            $legs.Add([ordered]@{ leg = "$os-provider-$variant"; os = $os; runner = $runner; kind = 'provider'
                variant = $variant; family = 'Provider'; profile = $profileName; status = $status })
        }
    }
    foreach ($family in @($selected | Where-Object { $_ -cne 'Provider' }))
    {
        $status = if ($family -eq 'primary-hot-path') { [string]$plan.primaryHotPaths.status } else { [string]$plan.families.$family.status }
        $legs.Add([ordered]@{ leg = "$os-$($family.ToLowerInvariant())"; os = $os; runner = $runner; kind = 'family'
            variant = ''; family = $family; profile = ''; status = $status })
    }
}
if ($legs.Count -eq 0) { throw 'The capture plan is empty.' }
$oses = @($legs | ForEach-Object { $_.os } | Select-Object -Unique)
[ordered]@{
    capture = [ordered]@{ include = $legs.ToArray() }
    environments = [ordered]@{ include = @($oses | ForEach-Object { [ordered]@{ os = $_; runner = (@($plan.runners.$_) | ConvertTo-Json -Compress) } }) }
} | ConvertTo-Json -Depth 6 -Compress
