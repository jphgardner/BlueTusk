<#
.SYNOPSIS
Resolve the deterministic old Jobs commit for the exact-candidate upgrade rehearsal.
.DESCRIPTION
The baseline is the first parent of the most recent commit that changed the Jobs-owned
sources in the candidate's own history, walked by `git rev-list --topo-order` with Git's
default history simplification (a merge is followed through the parent whose Jobs sources
it kept). The Jobs-owned sources are src/BlueTusk.Jobs, which also contains the durable
schema, format and migration DDL (PostgreSqlJobStore.cs). The two Public API analyzer
baselines are excluded: shipping an API surface rewrites them without changing the
assembly, so they cannot make a release upgrade rehearsal cross-version.

The result depends only on the candidate commit and its ancestry, never on the current
branch tips. Resolution fails closed when history is shallow, when the candidate has no
Jobs project, or when the most recent Jobs-owned change introduced the project, because no
earlier Jobs implementation then exists to upgrade from. It never substitutes another
commit.
.EXAMPLE
./eng/resolve-jobs-upgrade-baseline.ps1 -CandidateCommit <full-candidate-sha>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string] $CandidateCommit,
    [string] $RepositoryRoot = (Split-Path $PSScriptRoot -Parent)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$jobsRoot = 'src/BlueTusk.Jobs'
$jobsProject = "$jobsRoot/BlueTusk.Jobs.csproj"
$ownedPathspec = @(
    $jobsRoot,
    ":(exclude)$jobsRoot/PublicAPI.Shipped.txt",
    ":(exclude)$jobsRoot/PublicAPI.Unshipped.txt")
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path

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

$shallow = [string](Invoke-Git @('rev-parse', '--is-shallow-repository') 'Shallow history check')[0]
Require ($shallow.Trim() -ceq 'false') (
    'Jobs upgrade baseline resolution requires the complete candidate history; check out with fetch-depth 0.')
$candidate = ([string](Invoke-Git @('rev-parse', '--verify', "$CandidateCommit^{commit}") 'Candidate commit lookup')[0]).Trim()
Require ($candidate -ceq $CandidateCommit.ToLowerInvariant()) 'Candidate commit lookup did not return the exact candidate.'
Require (Test-GitObject "${candidate}:$jobsProject") 'The candidate has no Jobs project to upgrade to.'

$changes = Invoke-Git (@('rev-list', '--topo-order', '--max-count=1', $candidate, '--') + $ownedPathspec) 'Jobs-owned source history walk'
Require ($changes.Count -eq 1 -and ([string]$changes[0]).Trim() -cmatch '^[0-9a-f]{40}$') (
    'No commit in the candidate history changed the Jobs-owned sources.')
$change = ([string]$changes[0]).Trim()
$lineage = ([string](Invoke-Git @('rev-list', '--parents', '--max-count=1', $change) 'Jobs change parent lookup')[0]).Trim() -split ' '
Require ($lineage.Count -ge 2 -and $lineage[0] -ceq $change) (
    "Jobs-owned change $change is a root commit; no earlier Jobs implementation exists to upgrade from.")
$baseline = $lineage[1]
Require (Test-GitObject "${baseline}:$jobsProject") (
    "The most recent Jobs-owned change $change introduced the Jobs project; no earlier Jobs implementation exists to upgrade from.")
& git -C $root merge-base --is-ancestor $baseline $candidate
Require ($LASTEXITCODE -eq 0) 'The resolved Jobs baseline is not an ancestor of the candidate.'
& git -C $root diff --quiet $baseline $candidate -- @ownedPathspec
Require ($LASTEXITCODE -eq 1) 'The resolved Jobs baseline does not differ from the candidate in its Jobs-owned sources.'
$baselineJobsTree = Get-Tree "${baseline}:$jobsRoot"
$candidateJobsTree = Get-Tree "${candidate}:$jobsRoot"
Require ($baselineJobsTree -cne $candidateJobsTree) 'The resolved Jobs baseline has the candidate Jobs source tree.'

[pscustomobject][ordered]@{
    Rule = 'first-parent-of-most-recent-jobs-owned-source-change'
    OwnedPathspec = $ownedPathspec
    CandidateCommit = $candidate
    ChangeCommit = $change
    BaselineCommit = $baseline
    CandidateSourceTree = Get-Tree "$candidate^{tree}"
    BaselineSourceTree = Get-Tree "$baseline^{tree}"
    CandidateJobsTree = $candidateJobsTree
    BaselineJobsTree = $baselineJobsTree
}
