Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'core-candidate-evidence.psm1')

function ConvertTo-CoreGithubRunRecord
{
    param([Parameter(Mandatory)][object] $Run, [Parameter(Mandatory)][object] $Workflow,
        [Parameter(Mandatory)][object[]] $JobPages,
        [Parameter(Mandatory)][string] $WorkflowFile,
        [Parameter(Mandatory)][long] $RunId, [Parameter(Mandatory)][int] $RunAttempt,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
        [Parameter(Mandatory)][DateTimeOffset] $CandidateCommitUtc,
        [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string] $ExpectedRepository = 'jphgardner/BlueTusk')

    # Pure validation/mapping of API-shaped data. Calling this function with
    # synthetic snapshots does not certify a live GitHub identity or execution.
    function Require([bool] $Condition, [string] $Message)
    { if (-not $Condition) { throw $Message } }
    function Value([object] $Object, [string] $Name)
    {
        Require ($Object -is [pscustomobject]) 'GitHub metadata must contain JSON objects.'
        $property = $Object.PSObject.Properties[$Name]
        Require ($null -ne $property) "GitHub metadata is missing '$Name'."
        return ,$property.Value
    }
    function Utc([object] $Value, [string] $Context)
    {
        Require ($Value -is [string] -and $Value -cmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$') "$Context requires an explicit UTC timestamp."
        $instant = [DateTimeOffset]::MinValue
        Require ([DateTimeOffset]::TryParse($Value, [ref]$instant)) "$Context timestamp is invalid."
        Require ($instant -ge $CandidateCommitUtc.ToUniversalTime() -and $instant -le [DateTimeOffset]::UtcNow) "$Context predates the candidate or is in the future."
        return $instant
    }

    $contract = Get-CoreCandidateContract
    Require ($WorkflowFile -cin $contract.requiredWorkflows) 'Unknown Core producer workflow.'
    Assert-CoreEvidenceInteger $RunId 'Requested GitHub run ID'
    Assert-CoreEvidenceInteger $RunAttempt 'Requested GitHub run attempt'
    $actualRunId = Value $Run 'id'
    $actualAttempt = Value $Run 'run_attempt'
    Assert-CoreEvidenceInteger $actualRunId 'GitHub API run ID'
    Assert-CoreEvidenceInteger $actualAttempt 'GitHub API run attempt'
    Require ($actualRunId -eq $RunId -and $actualAttempt -eq $RunAttempt) 'GitHub run ID or attempt differs from the requested execution.'
    Require ((Value (Value $Run 'repository') 'full_name') -ceq $ExpectedRepository -and
        (Value (Value $Run 'head_repository') 'full_name') -ceq $ExpectedRepository) 'GitHub run belongs to a different repository or fork.'
    Require ((Value $Run 'head_sha') -ceq $ExpectedCommit) 'GitHub run head differs from the exact candidate.'
    Require ((Value $Run 'event') -ceq 'workflow_dispatch') 'Core GitHub producers require an actual manual workflow_dispatch run.'
    Require ((Value $Run 'status') -ceq 'completed' -and (Value $Run 'conclusion') -ceq 'success') 'GitHub run must have completed successfully.'
    $workflowPath = ".github/workflows/$WorkflowFile"
    $workflowId = Value $Workflow 'id'
    $runWorkflowId = Value $Run 'workflow_id'
    Assert-CoreEvidenceInteger $workflowId 'GitHub workflow ID'
    Assert-CoreEvidenceInteger $runWorkflowId 'GitHub run workflow ID'
    Require ($workflowId -eq $runWorkflowId -and (Value $Workflow 'path') -ceq $workflowPath -and
        (Value $Run 'path') -ceq $workflowPath) 'GitHub workflow ID or path differs from the producer.'
    $runUrl = "https://github.com/$ExpectedRepository/actions/runs/$RunId"
    Require ((Value $Run 'html_url') -ceq $runUrl) 'GitHub run URL differs from its repository/run identity.'
    $created = Utc (Value $Run 'created_at') 'GitHub run creation'
    $started = Utc (Value $Run 'run_started_at') 'GitHub run start'
    $updated = Utc (Value $Run 'updated_at') 'GitHub run update'
    Require ($started -ge $created -and $updated -ge $started) 'GitHub run lifecycle timestamps are inconsistent.'

    Require ($JobPages.Count -gt 0) 'GitHub jobs pagination is empty.'
    $jobs = @()
    $expectedTotal = Value $JobPages[0] 'total_count'
    Assert-CoreEvidenceInteger $expectedTotal 'GitHub job total'
    foreach ($page in $JobPages)
    {
        $total = Value $page 'total_count'
        Assert-CoreEvidenceInteger $total 'GitHub page job total'
        $items = Value $page 'jobs'
        Require ($total -eq $expectedTotal -and $items -is [array] -and $items.Count -gt 0) 'GitHub job pages disagree or contain an empty/malformed page.'
        $jobs += $items
    }
    Require ($jobs.Count -eq $expectedTotal) 'GitHub job pagination does not contain the complete declared job set.'
    $ids = [Collections.Generic.HashSet[long]]::new()
    $latest = $started
    $completedUtc = $null
    $successful = 0
    foreach ($job in $jobs)
    {
        $id = Value $job 'id'
        Assert-CoreEvidenceInteger $id 'GitHub job ID'
        $jobRunId = Value $job 'run_id'
        $jobAttempt = Value $job 'run_attempt'
        Assert-CoreEvidenceInteger $jobRunId 'GitHub job run ID'
        Assert-CoreEvidenceInteger $jobAttempt 'GitHub job attempt'
        Require ($ids.Add($id)) 'GitHub job ID is duplicated across pages.'
        Require ($jobRunId -eq $RunId -and $jobAttempt -eq $RunAttempt -and
            (Value $job 'head_sha') -ceq $ExpectedCommit) 'GitHub job belongs to another run, attempt or candidate.'
        Require ((Value $job 'status') -ceq 'completed' -and
            (Value $job 'conclusion') -cin @('success', 'skipped')) 'GitHub attempt contains an unfinished or unsuccessful job.'
        if ((Value $job 'conclusion') -ceq 'success') { $successful++ }
        Require ((Value $job 'html_url') -ceq "$runUrl/job/$id") 'GitHub job URL differs from its run identity.'
        $jobStarted = Utc (Value $job 'started_at') 'GitHub job start'
        $jobCompletedText = Value $job 'completed_at'
        $jobCompleted = Utc $jobCompletedText 'GitHub job completion'
        Require ($jobStarted -ge $started -and $jobCompleted -ge $jobStarted -and
            $jobCompleted -le $updated) 'GitHub job lifecycle timestamps are inconsistent with the run.'
        if ($null -eq $completedUtc -or $jobCompleted -gt $latest)
        { $latest = $jobCompleted; $completedUtc = $jobCompletedText }
    }
    Require ($successful -gt 0) 'GitHub attempt must contain an actually successful job, not only skipped jobs.'

    return [pscustomobject][ordered]@{
        workflowFile = $WorkflowFile; headSha = $ExpectedCommit; event = 'workflow_dispatch'
        conclusion = 'success'; runId = $RunId; runAttempt = $RunAttempt
        completedUtc = $completedUtc; url = $runUrl
    }
}

Export-ModuleMember -Function ConvertTo-CoreGithubRunRecord
