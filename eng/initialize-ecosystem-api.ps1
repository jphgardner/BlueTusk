[CmdletBinding()]
param(
    [ValidateSet('Events', 'Jobs', 'Documents', 'Projections', 'Search', 'Schema', 'Sql', 'Studio', 'Edge', 'Workflows')]
    [string[]] $Families = @('Events', 'Jobs', 'Documents', 'Projections', 'Search', 'Schema', 'Sql', 'Studio', 'Edge', 'Workflows'),
    [switch] $UpdateUnpublishedBudget
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$lockDirectory = Join-Path $repositoryRoot 'artifacts/ecosystem-locks'
New-Item -ItemType Directory -Path $lockDirectory -Force | Out-Null
try
{
    $candidateApiLock = [IO.File]::Open((Join-Path $lockDirectory 'api-initialization.lock'),
        [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
}
catch [IO.IOException]
{
    throw 'Another candidate API initializer owns the shared budget lock; retry after it finishes.'
}
try
{
$manifestPath = Join-Path $PSScriptRoot 'product-families.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$budgetPath = Join-Path $PSScriptRoot 'api-budgets.json'
$budgets = Get-Content -LiteralPath $budgetPath -Raw | ConvertFrom-Json
foreach ($family in $Families)
{
    $definition = $manifest.families.PSObject.Properties[$family]
    if ($null -eq $definition) { throw "Register $family before initializing its API contract." }
    if ($definition.Value.publication.enabled) { throw "Published $family contracts require the existing API review workflow." }
    $signatures = 0
    $baselineProjects = 0
    $exempt = @()
    foreach ($relativeProject in $definition.Value.packages)
    {
        $project = Join-Path $repositoryRoot $relativeProject
        [xml] $document = Get-Content -LiteralPath $project -Raw
        if ($document.SelectSingleNode('//IncludeBuildOutput')?.InnerText -eq 'false' -or
            $document.SelectSingleNode('//OutputType')?.InnerText -eq 'Exe')
        {
            # Analyzer packages carry tooling rather than a consumer runtime API.
            $exempt += $relativeProject
            continue
        }
        $directory = Split-Path $project -Parent
        foreach ($name in @('PublicAPI.Shipped.txt', 'PublicAPI.Unshipped.txt'))
        {
            $path = Join-Path $directory $name
            if (-not (Test-Path -LiteralPath $path)) { Set-Content -LiteralPath $path -Value '#nullable enable' -Encoding utf8NoBOM }
        }
        & dotnet restore $project --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw "API restore failed for $relativeProject." }
        # Add declarations only. Formatter removal can incorrectly classify synthesized
        # record members as stale; removals need explicit review and a compiler check.
        & dotnet format analyzers $project --diagnostics RS0016 --severity info --no-restore --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw "API baseline generation failed for $relativeProject." }
        foreach ($name in @('PublicAPI.Shipped.txt', 'PublicAPI.Unshipped.txt'))
        {
            $signatures += @(Get-Content -LiteralPath (Join-Path $directory $name) |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and -not $_.TrimStart().StartsWith('#') }).Count
        }
        $baselineProjects++
    }
    if ($null -eq $budgets.families.PSObject.Properties[$family])
    {
        $budgets.families | Add-Member -NotePropertyName $family -NotePropertyValue ([ordered]@{
            maximumSignatures = $signatures; baselineProjects = $baselineProjects; apiExemptProjects = @($exempt)
        })
    }
    else
    {
        $budget = $budgets.families.$family
        if ($UpdateUnpublishedBudget)
        {
            Write-Output "$family unpublished budget update: $($budget.maximumSignatures) -> $signatures signatures; $($budget.baselineProjects) -> $baselineProjects runtime projects."
            $budget.maximumSignatures = $signatures
            $budget.baselineProjects = $baselineProjects
            $budget.apiExemptProjects = @($exempt)
        }
        elseif ($signatures -gt $budget.maximumSignatures -or $baselineProjects -ne $budget.baselineProjects -or
            ((@($exempt) -join '|') -ne (@($budget.apiExemptProjects) -join '|')))
        {
                throw "The $family API exceeded its initial budget; review the changed signatures before updating api-budgets.json."
        }
    }
    Write-Output "$family initial candidate API: $signatures signatures in $baselineProjects runtime packages."
}
$budgets | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $budgetPath -Encoding utf8NoBOM
Write-Output 'Unshipped compiler contracts initialized; this does not approve public API stability.'
}
finally
{
    $candidateApiLock.Dispose()
}
