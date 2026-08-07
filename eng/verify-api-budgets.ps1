[CmdletBinding()]
param(
    [string] $RepositoryRoot = (Split-Path $PSScriptRoot -Parent)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$manifest = Get-Content -LiteralPath (
    Join-Path $RepositoryRoot 'eng/product-families.json') -Raw | ConvertFrom-Json
$budgets = Get-Content -LiteralPath (
    Join-Path $RepositoryRoot 'eng/api-budgets.json') -Raw | ConvertFrom-Json
if ($budgets.schemaVersion -ne 1)
{
    throw "Expected API-budget schema 1; found '$($budgets.schemaVersion)'."
}

$manifestFamilies = @($manifest.families.PSObject.Properties.Name | Sort-Object)
$budgetFamilies = @($budgets.families.PSObject.Properties.Name | Sort-Object)
if (Compare-Object $manifestFamilies $budgetFamilies)
{
    throw 'API-budget families do not exactly match the product-family manifest.'
}

$total = 0
foreach ($familyName in $manifestFamilies)
{
    $definition = $manifest.families.$familyName
    $budget = $budgets.families.$familyName
    $exempt = @($budget.apiExemptProjects)
    $usedExemptions = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    $familyCount = 0
    $baselineProjects = 0

    foreach ($project in @($definition.packages))
    {
        $projectPath = Join-Path $RepositoryRoot $project
        if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf))
        {
            throw "Product-family project '$project' does not exist."
        }

        $projectDirectory = Split-Path $projectPath -Parent
        $shipped = Join-Path $projectDirectory 'PublicAPI.Shipped.txt'
        $unshipped = Join-Path $projectDirectory 'PublicAPI.Unshipped.txt'
        if ((Test-Path -LiteralPath $shipped) -and (Test-Path -LiteralPath $unshipped))
        {
            $baselineProjects++
            foreach ($path in @($shipped, $unshipped))
            {
                $familyCount += @(
                    Get-Content -LiteralPath $path |
                        Where-Object {
                            -not [string]::IsNullOrWhiteSpace($_) -and
                            -not $_.TrimStart().StartsWith('#')
                        }).Count
            }

            continue
        }

        if ($project -notin $exempt)
        {
            throw (
                "Publishable project '$project' has no complete public API baseline " +
                "and is not explicitly exempt.")
        }

        [void]$usedExemptions.Add([string]$project)
    }

    $unusedExemptions = @($exempt | Where-Object { -not $usedExemptions.Contains($_) })
    if ($unusedExemptions.Count -gt 0)
    {
        throw "API-budget family '$familyName' has unused exemptions: $($unusedExemptions -join ', ')."
    }

    if ($baselineProjects -ne [int]$budget.baselineProjects)
    {
        throw (
            "API-budget family '$familyName' expected $($budget.baselineProjects) " +
            "baseline projects but found $baselineProjects.")
    }

    if ($familyCount -gt [int]$budget.maximumSignatures)
    {
        throw (
            "API-budget family '$familyName' exposes $familyCount signatures; " +
            "the reviewed maximum is $($budget.maximumSignatures).")
    }

    $total += $familyCount
    Write-Host (
        "$familyName API budget: $familyCount / $($budget.maximumSignatures) " +
        "signatures across $baselineProjects projects.")
}

Write-Host "API budgets verified: $total signatures across $($manifestFamilies.Count) families."
