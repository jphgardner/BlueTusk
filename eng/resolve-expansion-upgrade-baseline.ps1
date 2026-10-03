<#
.SYNOPSIS
Resolve the deterministic old commit for one expansion family's exact-candidate upgrade rehearsal.
.DESCRIPTION
This generalises resolve-jobs-upgrade-baseline.ps1 (which Jobs keeps unchanged) to the other
expansion families. No expansion family has ever been published, so the rehearsal upgrades from
the state immediately before the most recent change to the family-owned sources.

The family-owned sources are the directories of the family's package projects, read from
eng/product-families.json *at the candidate commit*, so the result depends only on the candidate
and its ancestry, never on the working tree or branch tips. Each directory's two Public API
analyzer baselines are excluded: shipping an API surface rewrites them without changing an
assembly, so they cannot make a release upgrade rehearsal cross-version. npm packages are not
part of the source set; the rehearsal exercises the family's .NET assemblies.

The baseline is the first parent of the most recent commit that changed those sources, walked by
`git rev-list --topo-order` with Git's default history simplification (a merge is followed through
the parent whose family sources it kept). Resolution fails closed when history is shallow, when
the candidate lacks the family's primary project (src/BlueTusk.<Family>), when the change is a
root commit, or when the change introduced the primary project, because no earlier
implementation then exists to upgrade from. It never substitutes another commit.
.EXAMPLE
./eng/resolve-expansion-upgrade-baseline.ps1 -Family Projections -CandidateCommit <full-candidate-sha>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Events', 'Documents', 'Schema', 'Projections', 'Search', 'Sql',
        'Studio', 'Edge', 'Workflows')][string] $Family,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string] $CandidateCommit,
    [string] $RepositoryRoot = (Split-Path $PSScriptRoot -Parent)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ValidateSet is case-insensitive; the family identity is not.
$families = @('Events', 'Documents', 'Schema', 'Projections', 'Search', 'Sql', 'Studio', 'Edge', 'Workflows')
$Family = @($families | Where-Object { $_ -ieq $Family })[0]
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$primaryRoot = "src/BlueTusk.$Family"
$primaryProject = "$primaryRoot/BlueTusk.$Family.csproj"

function Require([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
function Invoke-Git([string[]] $Arguments, [string] $Action)
{
    $output = @(& git -C $root @Arguments)
    Require ($LASTEXITCODE -eq 0) "$Action failed."
    return ,$output
}
function Test-GitObject([string] $Specification)
{
    & git -C $root cat-file -e $Specification 2> $null
    return $LASTEXITCODE -eq 0
}
function Get-Tree([string] $Specification)
{
    $tree = [string](Invoke-Git @('rev-parse', '--verify', $Specification) "Tree lookup for '$Specification'")[0]
    Require ($tree.Trim() -cmatch '^[0-9a-f]{40}$') "Tree lookup for '$Specification' returned no object."
    return $tree.Trim()
}
function Get-FamilyTrees([string] $Commit, [string[]] $Roots)
{
    # One "root=tree" entry per owned root; a root absent at that commit is recorded as missing.
    return @($Roots | ForEach-Object {
        if (Test-GitObject "${Commit}:$_") { "$_=$(Get-Tree "${Commit}:$_")" } else { "$_=missing" }
    })
}

$shallow = [string](Invoke-Git @('rev-parse', '--is-shallow-repository') 'Shallow history check')[0]
Require ($shallow.Trim() -ceq 'false') (
    "$Family upgrade baseline resolution requires the complete candidate history; check out with fetch-depth 0.")
$candidate = ([string](Invoke-Git @('rev-parse', '--verify', "$CandidateCommit^{commit}") 'Candidate commit lookup')[0]).Trim()
Require ($candidate -ceq $CandidateCommit.ToLowerInvariant()) 'Candidate commit lookup did not return the exact candidate.'
Require (Test-GitObject "${candidate}:$primaryProject") "The candidate has no $Family project to upgrade to."

$manifestSpecification = "${candidate}:eng/product-families.json"
Require (Test-GitObject $manifestSpecification) 'The candidate has no product family manifest.'
$manifestBlob = Get-Tree $manifestSpecification
$manifestText = (Invoke-Git @('cat-file', '-p', $manifestBlob) 'Candidate product family manifest read') -join "`n"
$manifest = $manifestText | ConvertFrom-Json
$definition = $manifest.families.PSObject.Properties[$Family]
Require ($null -ne $definition -and $null -ne $definition.Value.PSObject.Properties['packages']) (
    "The candidate product family manifest does not declare $Family packages.")
$projects = @($definition.Value.packages | ForEach-Object { [string]$_ })
Require ($projects.Count -gt 0 -and $projects -ccontains $primaryProject) (
    "The candidate product family manifest does not declare the $Family primary project.")
foreach ($project in $projects)
{
    Require ($project -cmatch '^(?:src|tooling)/BlueTusk\.[A-Za-z0-9.]+/BlueTusk\.[A-Za-z0-9.]+\.csproj$' -and
        -not $project.Contains('..')) "Unsafe $Family project path '$project'."
}
$ownedRoots = @($projects | ForEach-Object { $_.Substring(0, $_.LastIndexOf('/')) } |
    Sort-Object -Unique -CaseSensitive)
$ownedPathspec = @(foreach ($ownedRoot in $ownedRoots)
{
    $ownedRoot
    ":(exclude)$ownedRoot/PublicAPI.Shipped.txt"
    ":(exclude)$ownedRoot/PublicAPI.Unshipped.txt"
})

$changes = Invoke-Git (@('rev-list', '--topo-order', '--max-count=1', $candidate, '--') + $ownedPathspec) "$Family-owned source history walk"
Require ($changes.Count -eq 1 -and ([string]$changes[0]).Trim() -cmatch '^[0-9a-f]{40}$') (
    "No commit in the candidate history changed the $Family-owned sources.")
$change = ([string]$changes[0]).Trim()
$lineage = ([string](Invoke-Git @('rev-list', '--parents', '--max-count=1', $change) "$Family change parent lookup")[0]).Trim() -split ' '
Require ($lineage.Count -ge 2 -and $lineage[0] -ceq $change) (
    "$Family-owned change $change is a root commit; no earlier $Family implementation exists to upgrade from.")
$baseline = $lineage[1]
Require (Test-GitObject "${baseline}:$primaryProject") (
    "The most recent $Family-owned change $change introduced the $Family project; no earlier $Family implementation exists to upgrade from.")
& git -C $root merge-base --is-ancestor $baseline $candidate
Require ($LASTEXITCODE -eq 0) "The resolved $Family baseline is not an ancestor of the candidate."
& git -C $root diff --quiet $baseline $candidate -- @ownedPathspec
Require ($LASTEXITCODE -eq 1) "The resolved $Family baseline does not differ from the candidate in its $Family-owned sources."
$baselineFamilyTrees = Get-FamilyTrees $baseline $ownedRoots
$candidateFamilyTrees = Get-FamilyTrees $candidate $ownedRoots
Require (($baselineFamilyTrees -join '|') -cne ($candidateFamilyTrees -join '|')) (
    "The resolved $Family baseline has the candidate $Family source trees.")

[pscustomobject][ordered]@{
    Rule = 'first-parent-of-most-recent-family-owned-source-change'
    Family = $Family
    ProductFamiliesBlob = $manifestBlob
    PrimaryProject = $primaryProject
    OwnedRoots = $ownedRoots
    OwnedPathspec = $ownedPathspec
    CandidateCommit = $candidate
    ChangeCommit = $change
    BaselineCommit = $baseline
    CandidateSourceTree = Get-Tree "$candidate^{tree}"
    BaselineSourceTree = Get-Tree "$baseline^{tree}"
    CandidatePrimaryTree = Get-Tree "${candidate}:$primaryRoot"
    BaselinePrimaryTree = Get-Tree "${baseline}:$primaryRoot"
    CandidateFamilyTrees = $candidateFamilyTrees
    BaselineFamilyTrees = $baselineFamilyTrees
}
