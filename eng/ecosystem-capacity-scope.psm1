Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Exact evidence shape of each ecosystem capacity scope. A single-family scope is the only
# shape that may carry expansion release capacity evidence. 'All' is the combined diagnostic
# campaign: its manifest, layout and artifact name can never satisfy a family capacity role.
$script:EcosystemCapacityScopes = [ordered]@{
    All = @{
        Campaigns = @('jobs-workflows', 'projections-capacity', 'projections-overload', 'documents')
        Harnesses = @('jobs', 'projections', 'documents')
    }
    Jobs = @{ Campaigns = @('jobs'); Harnesses = @('jobs') }
    Workflows = @{ Campaigns = @('jobs-workflows'); Harnesses = @('jobs') }
    Projections = @{ Campaigns = @('projections-capacity', 'projections-overload'); Harnesses = @('projections') }
    Documents = @{ Campaigns = @('documents'); Harnesses = @('documents') }
}

function Assert-EcosystemCapacityCondition([bool] $Condition, [string] $Message)
{
    if (-not $Condition) { throw $Message }
}

function Test-EcosystemCapacityNameSet([string[]] $Expected, [string[]] $Actual)
{
    $expectedSorted = [string[]]@($Expected | Sort-Object -CaseSensitive)
    $actualSorted = [string[]]@($Actual | Sort-Object -CaseSensitive)
    if ($expectedSorted.Count -ne $actualSorted.Count) { return $false }
    for ($index = 0; $index -lt $expectedSorted.Count; $index++)
    {
        if ($expectedSorted[$index] -cne $actualSorted[$index]) { return $false }
    }
    return $true
}

function Get-EcosystemCapacityScope
{
    param([Parameter(Mandatory)][string] $Product)
    $known = @($script:EcosystemCapacityScopes.Keys | Where-Object { $_ -ceq $Product })
    Assert-EcosystemCapacityCondition ($known.Count -eq 1) "Unknown ecosystem capacity scope '$Product'."
    $definition = $script:EcosystemCapacityScopes[$Product]
    return [pscustomobject][ordered]@{
        Product = $Product
        Campaigns = @($definition.Campaigns)
        Harnesses = @($definition.Harnesses)
        ReleaseFamily = $(if ($Product -ceq 'All') { $null } else { $Product })
    }
}

function Get-EcosystemCapacityArtifactName
{
    param([Parameter(Mandatory)][string] $Product,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $Commit)
    $scope = Get-EcosystemCapacityScope -Product $Product
    Assert-EcosystemCapacityCondition ($null -ne $scope.ReleaseFamily) (
        "The combined '$Product' campaign is diagnostic only and cannot produce family capacity evidence.")
    return "expansion-$($scope.ReleaseFamily.ToLowerInvariant())-capacity-$Commit"
}

function Assert-EcosystemCapacityEvidenceScope
{
    # Checks only the declared family and exact layout of an evidence directory. It does not
    # verify hashes, budgets or measurements; verify-ecosystem-performance.ps1 does that.
    param([Parameter(Mandatory)][string] $EvidenceRoot,
        [Parameter(Mandatory)][string] $Product,
        [Parameter(Mandatory)][ValidateRange(1, 100)][int] $Repetitions)
    $scope = Get-EcosystemCapacityScope -Product $Product
    $root = (Resolve-Path -LiteralPath $EvidenceRoot).Path
    $manifestPath = Join-Path $root 'manifest.json'
    Assert-EcosystemCapacityCondition (Test-Path -LiteralPath $manifestPath -PathType Leaf) 'Capacity evidence has no manifest.'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 100
    $familyProperty = $manifest.PSObject.Properties['Family']
    $family = if ($null -eq $familyProperty) { '' } else { [string]$familyProperty.Value }
    if ($null -eq $scope.ReleaseFamily)
    {
        Assert-EcosystemCapacityCondition ($family -cin @('', 'All')) 'Capacity manifest has the wrong product scope.'
    }
    else
    {
        Assert-EcosystemCapacityCondition ($null -ne $familyProperty -and $familyProperty.Value -is [string] -and
            $family -ceq $scope.ReleaseFamily) 'Capacity manifest has the wrong product scope.'
    }

    $files = @('budgets.json', 'manifest.json', 'runner.json', 'source-after.json', 'source-before.json')
    $directories = @('binary-snapshot') + @(1..$Repetitions | ForEach-Object { "run-$_" })
    $entries = @(Get-ChildItem -LiteralPath $root -Force)
    Assert-EcosystemCapacityCondition (@($entries | Where-Object {
        ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }).Count -eq 0) 'Capacity evidence must not contain links or junctions.'
    Assert-EcosystemCapacityCondition ((Test-EcosystemCapacityNameSet $files @($entries | Where-Object { -not $_.PSIsContainer } | ForEach-Object Name)) -and
        (Test-EcosystemCapacityNameSet $directories @($entries | Where-Object { $_.PSIsContainer } | ForEach-Object Name))) (
        "Capacity evidence for '$Product' contains top-level entries outside its scope.")
    for ($run = 1; $run -le $Repetitions; $run++)
    {
        $runEntries = @(Get-ChildItem -LiteralPath (Join-Path $root "run-$run") -Force)
        Assert-EcosystemCapacityCondition (@($runEntries | Where-Object {
            -not $_.PSIsContainer -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }).Count -eq 0 -and
            (Test-EcosystemCapacityNameSet $scope.Campaigns @($runEntries | ForEach-Object Name))) (
            "Capacity evidence run $run does not contain exactly the '$Product' campaigns.")
    }

    $snapshot = Join-Path $root 'binary-snapshot'
    $snapshotEntries = @(Get-ChildItem -LiteralPath $snapshot -Force)
    Assert-EcosystemCapacityCondition (@($snapshotEntries | Where-Object {
        ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }).Count -eq 0 -and
        (Test-EcosystemCapacityNameSet @('binaries.json') @($snapshotEntries | Where-Object { -not $_.PSIsContainer } | ForEach-Object Name)) -and
        (Test-EcosystemCapacityNameSet $scope.Harnesses @($snapshotEntries | Where-Object { $_.PSIsContainer } | ForEach-Object Name))) (
        "Capacity binary snapshot does not contain exactly the '$Product' harnesses.")
    $records = @((Get-Content -LiteralPath (Join-Path $snapshot 'binaries.json') -Raw | ConvertFrom-Json -Depth 20).Files)
    Assert-EcosystemCapacityCondition ($records.Count -gt 0 -and (Test-EcosystemCapacityNameSet $scope.Harnesses @(
        $records | ForEach-Object { [string]$_.Product } | Sort-Object -Unique -CaseSensitive))) (
        "Capacity binary manifest does not describe exactly the '$Product' harnesses.")
}

Export-ModuleMember -Function Get-EcosystemCapacityScope, Get-EcosystemCapacityArtifactName,
    Assert-EcosystemCapacityEvidenceScope
