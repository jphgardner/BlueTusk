[CmdletBinding()]
param()

# Self-test for the pure expansion readiness functions. Synthetic GitHub-shaped inputs only exercise
# the guards; they never certify a live run, artifact or release.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'core-candidate-evidence.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'expansion-candidate-evidence.psm1') -Force

$commit = 'a' * 40
$otherCommit = 'b' * 40
$rejected = 0

function Assert-Rejected([string] $Name, [scriptblock] $Action, [string] $Expected)
{
    $failure = $null
    try { & $Action | Out-Null } catch { $failure = $_.Exception.Message }
    if ($null -eq $failure -or -not $failure.Contains($Expected))
    { throw "Case '$Name' expected '$Expected'; received '$failure'." }
    $script:rejected++
}

# Contract derivation follows the policy for every family.
foreach ($family in @('Events', 'Jobs', 'Documents', 'Schema', 'Projections', 'Search', 'Sql', 'Studio', 'Edge', 'Workflows'))
{
    $contract = Get-ExpansionReadinessContract -Family $family -Commit $commit
    $slug = $family.ToLowerInvariant()
    if ($contract.Roles.Count -ne 3 -or $contract.ReadinessArtifactName -cne "expansion-readiness-$slug-$commit" -or
        @($contract.Roles | Where-Object { $_.ArtifactName -cne "expansion-$slug-$($_.Role)-$commit" }).Count -ne 0)
    { throw "Contract for '$family' does not use the exact per-family artifact names." }
    foreach ($role in $contract.Roles)
    {
        if ((Get-ExpansionWorkflowArtifactName -Contract $contract -WorkflowFile $role.WorkflowFile) -cne $role.ArtifactName)
        { throw "Release selection does not bind '$family' $($role.Role) to its exact artifact." }
    }
    if ((Get-ExpansionWorkflowArtifactName -Contract $contract -WorkflowFile $contract.ReadinessWorkflow) -cne $contract.ReadinessArtifactName -or
        $null -ne (Get-ExpansionWorkflowArtifactName -Contract $contract -WorkflowFile 'build.yml'))
    { throw "Release selection does not distinguish '$family' readiness from ordinary CI." }
}
Assert-Rejected 'unknown-family' { Get-ExpansionReadinessContract -Family 'Graph' -Commit $commit } 'Unknown expansion family'
Assert-Rejected 'lowercase-family' { Get-ExpansionReadinessContract -Family 'jobs' -Commit $commit } 'Unknown expansion family'

# Run selection: newest qualifying run that retains the exact unexpired artifact.
$jobs = Get-ExpansionReadinessContract -Family 'Jobs' -Commit $commit
$capacity = $jobs.Roles | Where-Object Role -ceq 'capacity'
function Run([long] $Id, [string] $Sha = $commit, [string] $Event = 'workflow_dispatch', [string] $Conclusion = 'success', [string] $Workflow = $capacity.WorkflowFile)
{ [pscustomobject]@{ workflowFile = $Workflow; headSha = $Sha; event = $Event; conclusion = $Conclusion; runId = $Id; runAttempt = 1; url = "u$Id" } }
$artifacts = @{
    10 = @([pscustomobject]@{ name = $capacity.ArtifactName; expired = $false })
    20 = @([pscustomobject]@{ name = $capacity.ArtifactName; expired = $true })
    30 = @([pscustomobject]@{ name = 'expansion-documents-capacity-' + $commit; expired = $false })
}
$lookup = { param($runId, $name) if ($artifacts.ContainsKey([int]$runId)) { $artifacts[[int]$runId] } else { @() } }
$selected = Select-ExpansionQualificationRun -Runs @((Run 10), (Run 20), (Run 30), (Run 40 -Sha $otherCommit), (Run 50 -Event 'push'), (Run 60 -Conclusion 'failure')) `
    -WorkflowFile $capacity.WorkflowFile -Commit $commit -ArtifactName $capacity.ArtifactName -GetRunArtifacts $lookup
if ($selected.Run.runId -ne 10) { throw "Selection chose run $($selected.Run.runId); expected 10 (20 expired, 30 holds another family's artifact)." }
Assert-Rejected 'no-qualifying-run' {
    Select-ExpansionQualificationRun -Runs @((Run 40 -Sha $otherCommit), (Run 50 -Event 'push'), (Run 60 -Conclusion 'failure')) `
        -WorkflowFile $capacity.WorkflowFile -Commit $commit -ArtifactName $capacity.ArtifactName -GetRunArtifacts $lookup } 'No successful workflow_dispatch'
Assert-Rejected 'only-expired-or-foreign' {
    Select-ExpansionQualificationRun -Runs @((Run 20), (Run 30)) -WorkflowFile $capacity.WorkflowFile -Commit $commit `
        -ArtifactName $capacity.ArtifactName -GetRunArtifacts $lookup } 'retains an unexpired'
Assert-Rejected 'ambiguous-artifact' {
    Select-ExpansionQualificationRun -Runs @((Run 70)) -WorkflowFile $capacity.WorkflowFile -Commit $commit -ArtifactName $capacity.ArtifactName `
        -GetRunArtifacts { param($r, $n) @([pscustomobject]@{ name = $n; expired = $false }, [pscustomobject]@{ name = $n; expired = $false }) } } 'ambiguous'
Assert-Rejected 'duplicate-listing' {
    Select-ExpansionQualificationRun -Runs @((Run 10), (Run 10)) -WorkflowFile $capacity.WorkflowFile -Commit $commit `
        -ArtifactName $capacity.ArtifactName -GetRunArtifacts $lookup } 'more than once'

# A later successful shared workflow for another family must not displace this family's
# capacity or readiness run. Combined diagnostics must not displace it either.
$documents = Get-ExpansionReadinessContract -Family 'Documents' -Commit $commit
$workflows = Get-ExpansionReadinessContract -Family 'Workflows' -Commit $commit
$sharedWorkflow = ($documents.Roles | Where-Object Role -ceq 'capacity').WorkflowFile
$documentsCapacity = Get-ExpansionWorkflowArtifactName -Contract $documents -WorkflowFile $sharedWorkflow
$workflowsCapacity = Get-ExpansionWorkflowArtifactName -Contract $workflows -WorkflowFile $sharedWorkflow
$sharedArtifacts = @{
    61 = @([pscustomobject]@{ name = $documentsCapacity; expired = $false })
    62 = @([pscustomobject]@{ name = $workflowsCapacity; expired = $false })
    63 = @([pscustomobject]@{ name = "ecosystem-performance-$commit-63-1"; expired = $false })
    71 = @([pscustomobject]@{ name = $documents.ReadinessArtifactName; expired = $false })
    72 = @([pscustomobject]@{ name = $workflows.ReadinessArtifactName; expired = $false })
}
$sharedLookup = { param($runId, $name) $sharedArtifacts[[int]$runId] }
$selected = Select-ExpansionQualificationRun -Runs @((Run 61 -Workflow $sharedWorkflow), (Run 62 -Workflow $sharedWorkflow), (Run 63 -Workflow $sharedWorkflow)) `
    -WorkflowFile $sharedWorkflow -Commit $commit -ArtifactName $documentsCapacity -GetRunArtifacts $sharedLookup
if ($selected.Run.runId -ne 61) { throw 'A later foreign family or combined capacity run displaced Documents.' }
$selected = Select-ExpansionQualificationRun -Runs @((Run 71 -Workflow $documents.ReadinessWorkflow), (Run 72 -Workflow $documents.ReadinessWorkflow)) `
    -WorkflowFile $documents.ReadinessWorkflow -Commit $commit `
    -ArtifactName (Get-ExpansionWorkflowArtifactName -Contract $documents -WorkflowFile $documents.ReadinessWorkflow) `
    -GetRunArtifacts $sharedLookup
if ($selected.Run.runId -ne 71) { throw 'A later foreign family readiness run displaced Documents.' }
Assert-Rejected 'shared-workflow-only-foreign-or-combined' {
    Select-ExpansionQualificationRun -Runs @((Run 62 -Workflow $sharedWorkflow), (Run 63 -Workflow $sharedWorkflow)) `
        -WorkflowFile $sharedWorkflow -Commit $commit -ArtifactName $documentsCapacity -GetRunArtifacts $sharedLookup
} 'retains an unexpired'

# Run record validation against one complete, successful attempt.
$candidateUtc = [DateTimeOffset]::Parse('2026-10-01T00:00:00Z')
function New-Snapshot
{
    $url = 'https://github.com/jphgardner/BlueTusk/actions/runs/99'
    $run = [pscustomobject]@{
        id = 99; run_attempt = 1; head_sha = $commit; event = 'workflow_dispatch'; status = 'completed'; conclusion = 'success'
        workflow_id = 7; path = ".github/workflows/$($capacity.WorkflowFile)"; html_url = $url
        repository = [pscustomobject]@{ full_name = 'jphgardner/BlueTusk' }; head_repository = [pscustomobject]@{ full_name = 'jphgardner/BlueTusk' }
        created_at = '2026-10-02T10:00:00Z'; run_started_at = '2026-10-02T10:00:05Z'; updated_at = '2026-10-02T11:00:00Z'
    }
    $workflow = [pscustomobject]@{ id = 7; path = ".github/workflows/$($capacity.WorkflowFile)" }
    $job = [pscustomobject]@{ id = 501; run_id = 99; run_attempt = 1; head_sha = $commit; status = 'completed'; conclusion = 'success'
        html_url = "$url/job/501"; started_at = '2026-10-02T10:00:10Z'; completed_at = '2026-10-02T10:59:00Z' }
    return @{ Run = $run; Workflow = $workflow; Pages = @([pscustomobject]@{ total_count = 1; jobs = @($job) }) }
}
function Convert-Snapshot($Snapshot)
{
    ConvertTo-ExpansionGithubRunRecord -Run $Snapshot.Run -Workflow $Snapshot.Workflow -JobPages $Snapshot.Pages `
        -WorkflowFile $capacity.WorkflowFile -RunId 99 -RunAttempt 1 -ExpectedCommit $commit -CandidateCommitUtc $candidateUtc
}
$record = Convert-Snapshot (New-Snapshot)
if ($record.runId -ne 99 -or $record.completedUtc -cne '2026-10-02T10:59:00Z') { throw 'A valid run snapshot was not mapped exactly.' }
$mutations = [ordered]@{
    'foreign-sha'       = @{ Change = { param($s) $s.Run.head_sha = $otherCommit }; Error = 'exact candidate' }
    'push-event'        = @{ Change = { param($s) $s.Run.event = 'push' }; Error = 'workflow_dispatch' }
    'failed-run'        = @{ Change = { param($s) $s.Run.conclusion = 'failure' }; Error = 'completed successfully' }
    'fork'              = @{ Change = { param($s) $s.Run.head_repository.full_name = 'someone/BlueTusk' }; Error = 'fork' }
    'other-workflow'    = @{ Change = { param($s) $s.Workflow.path = '.github/workflows/build.yml' }; Error = 'role workflow' }
    'failed-job'        = @{ Change = { param($s) $s.Pages[0].jobs[0].conclusion = 'failure' }; Error = 'unsuccessful job' }
    'skipped-only'      = @{ Change = { param($s) $s.Pages[0].jobs[0].conclusion = 'skipped' }; Error = 'actually successful job' }
    'incomplete-pages'  = @{ Change = { param($s) $s.Pages[0].total_count = 2 }; Error = 'complete declared job set' }
    'pages-disagree'    = @{ Change = { param($s) $s.Pages += [pscustomobject]@{ total_count = 3; jobs = @($s.Pages[0].jobs[0]) } }; Error = 'disagree' }
    'duplicate-job'     = @{ Change = { param($s) $s.Pages[0].total_count = 2; $s.Pages += [pscustomobject]@{ total_count = 2; jobs = @($s.Pages[0].jobs[0]) } }; Error = 'duplicated' }
    'job-other-attempt' = @{ Change = { param($s) $s.Pages[0].jobs[0].run_attempt = 2 }; Error = 'another run' }
    'predates-commit'   = @{ Change = { param($s) $s.Run.created_at = '2026-09-01T00:00:00Z' }; Error = 'predates' }
}
foreach ($case in $mutations.GetEnumerator())
{
    $snapshot = New-Snapshot
    & $case.Value.Change $snapshot
    Assert-Rejected $case.Key { Convert-Snapshot $snapshot } $case.Value.Error
}

# Verifier mapping: implemented roles resolve to existing scripts; every other role fails closed.
foreach ($pair in @('Jobs/capacity', 'Jobs/failover', 'Jobs/upgrade', 'Search/capacity', 'Edge/capacity',
    'Documents/capacity', 'Projections/capacity', 'Workflows/capacity', 'Documents/failover', 'Projections/failover',
    'Workflows/failover'))
{
    $family, $role = $pair.Split('/')
    $verifier = Get-ExpansionRoleVerifier -Family $family -Role $role
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $verifier.Script)))
    { throw "Verifier '$($verifier.Script)' for $pair does not exist." }
}
Assert-Rejected 'unimplemented-role' { Get-ExpansionRoleVerifier -Family 'Events' -Role 'capacity' } 'fails closed'
Assert-Rejected 'unimplemented-upgrade' { Get-ExpansionRoleVerifier -Family 'Documents' -Role 'upgrade' } 'fails closed'

# Readiness record binds exactly the three roles by workflow, artifact, run and digest.
function Bindings { foreach ($role in $jobs.Roles) { [pscustomobject]@{ Role = $role.Role; WorkflowFile = $role.WorkflowFile
    ArtifactName = $role.ArtifactName; RunId = 100; RunAttempt = 1; ArtifactSha256 = 'c' * 64 } } }
$readiness = New-ExpansionReadinessRecord -Contract $jobs -Tag 'jobs-v1.1.0' -Version '1.1.0' -ReadinessRunId 7 -ReadinessRunAttempt 1 -Bindings @(Bindings)
if ($readiness.qualificationEvidence.Count -ne 3 -or $readiness.candidateCommit -cne $commit -or
    @($readiness.qualificationEvidence | Where-Object { $_.artifactDigest -cne ('sha256:' + 'c' * 64) }).Count -ne 0)
{ throw 'Readiness record does not bind the three roles exactly.' }
Assert-Rejected 'missing-role' {
    New-ExpansionReadinessRecord -Contract $jobs -Tag 't' -Version 'v' -ReadinessRunId 7 -ReadinessRunAttempt 1 -Bindings @(Bindings | Select-Object -First 2) } 'exactly the three'
Assert-Rejected 'renamed-artifact' {
    $b = @(Bindings); $b[0].ArtifactName = 'expansion-documents-capacity-' + $commit
    New-ExpansionReadinessRecord -Contract $jobs -Tag 't' -Version 'v' -ReadinessRunId 7 -ReadinessRunAttempt 1 -Bindings $b } 'exact artifact'
Assert-Rejected 'bad-digest' {
    $b = @(Bindings); $b[1].ArtifactSha256 = 'XYZ'
    New-ExpansionReadinessRecord -Contract $jobs -Tag 't' -Version 'v' -ReadinessRunId 7 -ReadinessRunAttempt 1 -Bindings $b } 'SHA-256'

Write-Output "Expansion candidate readiness self-test passed: 10 family contracts, run selection, run-record mapping, verifier mapping and readiness binding with $rejected rejected cases. NOT release evidence."
