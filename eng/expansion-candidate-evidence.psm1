Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'core-candidate-evidence.psm1')

# Pure contract, selection and mapping functions for expansion candidate readiness. Synthetic
# API-shaped inputs can exercise these guards, but they never certify a live run or artifact.
$script:ExpansionFamilies = @('Events', 'Jobs', 'Documents', 'Schema', 'Projections',
    'Search', 'Sql', 'Studio', 'Edge', 'Workflows')
$script:ExpansionRoles = @('capacity', 'failover', 'upgrade')

function Assert-ExpansionCondition([bool] $Condition, [string] $Message)
{
    if (-not $Condition) { throw $Message }
}

function Get-ExpansionReadinessContract
{
    param([Parameter(Mandatory)][string] $Family,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $Commit,
        [string] $PolicyPath = (Join-Path $PSScriptRoot 'expansion-release-policy.json'))
    Assert-ExpansionCondition ($Family -cin $script:ExpansionFamilies) "Unknown expansion family '$Family'."
    $policy = Read-CoreEvidenceJson $PolicyPath
    Assert-CoreEvidenceProperties $policy @('schemaVersion', 'readinessWorkflow', 'families') 'Expansion release policy'
    Assert-ExpansionCondition (($policy.schemaVersion -is [int] -or $policy.schemaVersion -is [long]) -and
        $policy.schemaVersion -eq 1 -and $policy.readinessWorkflow -is [string] -and
        $policy.readinessWorkflow -ceq 'expansion-candidate-readiness.yml') (
        'Expansion release policy schema or readiness workflow changed.')
    Assert-CoreEvidenceProperties $policy.families $script:ExpansionFamilies 'Expansion release policy families'
    $qualification = $policy.families.PSObject.Properties[$Family].Value
    Assert-CoreEvidenceProperties $qualification @('capacityWorkflow', 'failoverWorkflow', 'upgradeWorkflow') (
        "Expansion '$Family' qualification policy")
    $slug = $Family.ToLowerInvariant()
    $roles = foreach ($role in $script:ExpansionRoles)
    {
        $workflowFile = $qualification.PSObject.Properties["${role}Workflow"].Value
        Assert-ExpansionCondition ($workflowFile -is [string] -and
            $workflowFile -cmatch '^[a-z0-9][a-z0-9-]*\.yml$' -and
            $workflowFile -cne $policy.readinessWorkflow) "Expansion '$Family' $role workflow is invalid."
        [pscustomobject][ordered]@{
            Role = $role
            WorkflowFile = $workflowFile
            ArtifactName = "expansion-$slug-$role-$Commit"
        }
    }
    Assert-ExpansionCondition (@($roles.WorkflowFile | Sort-Object -Unique).Count -eq 3) (
        "Expansion '$Family' roles must use three distinct workflows.")
    return [pscustomobject][ordered]@{
        Family = $Family
        Commit = $Commit
        ReadinessWorkflow = [string]$policy.readinessWorkflow
        ReadinessArtifactName = "expansion-readiness-$slug-$Commit"
        Roles = @($roles)
    }
}

function Get-ExpansionReleaseIdentity
{
    # The tag and version the release gate will require for this family at this checkout.
    param([Parameter(Mandatory)][string] $Family,
        [string] $RepositoryRoot = (Split-Path $PSScriptRoot -Parent))
    Assert-ExpansionCondition ($Family -cin $script:ExpansionFamilies) "Unknown expansion family '$Family'."
    $manifest = Read-CoreEvidenceJson (Join-Path $RepositoryRoot 'eng/product-families.json')
    $definition = $manifest.families.PSObject.Properties[$Family].Value
    Assert-ExpansionCondition ($null -ne $definition -and $definition.versionFile -is [string] -and
        $definition.publication.tagPrefix -is [string] -and
        $definition.publication.tagPrefix -cmatch '^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$') (
        "Expansion family '$Family' has no valid release identity.")
    [xml] $document = Get-Content -LiteralPath (Join-Path $RepositoryRoot $definition.versionFile) -Raw
    $prefix = [string]$document.Project.PropertyGroup.VersionPrefix
    $suffix = [string]$document.Project.PropertyGroup.VersionSuffix
    $parsed = $null
    Assert-ExpansionCondition ([Version]::TryParse($prefix, [ref]$parsed)) "Expansion family '$Family' has an invalid version prefix."
    $version = if ([string]::IsNullOrWhiteSpace($suffix)) { $prefix } else { "$prefix-$suffix" }
    return [pscustomobject][ordered]@{
        Tag = "$($definition.publication.tagPrefix)-v$version"
        Version = $version
    }
}

function ConvertTo-ExpansionRunSummary
{
    # Maps one GitHub workflow_runs item onto the release gate's run summary shape.
    param([Parameter(Mandatory)][object] $Run, [Parameter(Mandatory)][string] $WorkflowFile)
    foreach ($name in @('id', 'run_attempt', 'head_sha', 'event', 'status', 'conclusion', 'path', 'html_url'))
    {
        Assert-ExpansionCondition ($Run -is [pscustomobject] -and $null -ne $Run.PSObject.Properties[$name]) (
            "GitHub workflow run listing is missing '$name'.")
    }
    Assert-CoreEvidenceInteger $Run.id 'Listed GitHub run ID'
    Assert-CoreEvidenceInteger $Run.run_attempt 'Listed GitHub run attempt'
    return [pscustomobject][ordered]@{
        workflowFile = $(if ([string]$Run.path -ceq ".github/workflows/$WorkflowFile") { $WorkflowFile } else { [string]$Run.path })
        headSha = [string]$Run.head_sha
        event = [string]$Run.event
        conclusion = $(if ([string]$Run.status -ceq 'completed') { [string]$Run.conclusion } else { [string]$Run.status })
        runId = [long]$Run.id
        runAttempt = [int]$Run.run_attempt
        url = [string]$Run.html_url
    }
}

function Get-ExpansionWorkflowArtifactName
{
    # Shared workflow files can execute for several families at one candidate SHA. Select
    # their run by the exact family artifact, using the same contract as readiness.
    param([Parameter(Mandatory)][object] $Contract,
        [Parameter(Mandatory)][string] $WorkflowFile)
    if ($WorkflowFile -ceq $Contract.ReadinessWorkflow)
    {
        return [string]$Contract.ReadinessArtifactName
    }
    $roles = @($Contract.Roles | Where-Object { $_.WorkflowFile -ceq $WorkflowFile })
    Assert-ExpansionCondition ($roles.Count -le 1) 'Expansion workflow has ambiguous qualification roles.'
    if ($roles.Count -eq 1) { return [string]$roles[0].ArtifactName }
    return $null
}

function Select-ExpansionQualificationRun
{
    # The single run-selection rule shared by readiness and the release gate: the latest
    # successful workflow_dispatch run whose head_sha is the candidate and which retains exactly
    # one unexpired artifact with this family's exact name. Shared workflows (the ecosystem
    # capacity campaign and readiness itself) run once per family at one head_sha, so a run
    # that carries another family's artifact, or only a combined diagnostic artifact, never
    # qualifies. Any other run state fails closed.
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]] $Runs,
        [Parameter(Mandatory)][string] $WorkflowFile,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $Commit,
        [Parameter(Mandatory)][string] $ArtifactName,
        [Parameter(Mandatory)][scriptblock] $GetRunArtifacts)
    $eligible = @($Runs | Where-Object {
        $_ -is [pscustomobject] -and
        [string]$_.workflowFile -ceq $WorkflowFile -and
        [string]$_.headSha -ceq $Commit -and
        [string]$_.event -ceq 'workflow_dispatch' -and
        [string]$_.conclusion -ceq 'success'
    } | Sort-Object -Property @{ Expression = { [long]$_.runId } } -Descending)
    Assert-ExpansionCondition ($eligible.Count -gt 0) (
        "No successful workflow_dispatch '$WorkflowFile' run exists at exact candidate head_sha '$Commit'.")
    Assert-ExpansionCondition (@($eligible | ForEach-Object { [long]$_.runId } | Sort-Object -Unique).Count -eq $eligible.Count) (
        "GitHub listed a '$WorkflowFile' run more than once.")
    foreach ($run in $eligible)
    {
        $named = @(& $GetRunArtifacts ([long]$run.runId) $ArtifactName | Where-Object {
            [string]$_.name -ceq $ArtifactName })
        Assert-ExpansionCondition ($named.Count -le 1) (
            "Run $($run.runId) has $($named.Count) artifacts named '$ArtifactName'; the binding is ambiguous.")
        if ($named.Count -eq 1 -and $named[0].expired -is [bool] -and -not $named[0].expired)
        {
            return [pscustomobject][ordered]@{ Run = $run; Artifact = $named[0] }
        }
    }
    throw "No successful workflow_dispatch '$WorkflowFile' run at '$Commit' retains an unexpired '$ArtifactName' artifact."
}

function ConvertTo-ExpansionGithubRunRecord
{
    # Mirrors ConvertTo-CoreGithubRunRecord for an expansion role workflow: pure validation of
    # one attempt's API snapshot and complete job pages, mapped to the canonical run record.
    param([Parameter(Mandatory)][object] $Run, [Parameter(Mandatory)][object] $Workflow,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]] $JobPages,
        [Parameter(Mandatory)][string] $WorkflowFile,
        [Parameter(Mandatory)][long] $RunId, [Parameter(Mandatory)][int] $RunAttempt,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
        [Parameter(Mandatory)][DateTimeOffset] $CandidateCommitUtc,
        [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string] $ExpectedRepository = 'jphgardner/BlueTusk')

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

    Require ($WorkflowFile -cmatch '^[a-z0-9][a-z0-9-]*\.yml$') 'Unknown expansion role workflow.'
    Assert-CoreEvidenceInteger $RunId 'Requested GitHub run ID'
    Assert-CoreEvidenceInteger $RunAttempt 'Requested GitHub run attempt'
    $actualRunId = Value $Run 'id'
    $actualAttempt = Value $Run 'run_attempt'
    Assert-CoreEvidenceInteger $actualRunId 'GitHub API run ID'
    Assert-CoreEvidenceInteger $actualAttempt 'GitHub API run attempt'
    Require ($actualRunId -eq $RunId -and $actualAttempt -eq $RunAttempt) 'GitHub run ID or attempt differs from the selected execution.'
    Require ((Value (Value $Run 'repository') 'full_name') -ceq $ExpectedRepository -and
        (Value (Value $Run 'head_repository') 'full_name') -ceq $ExpectedRepository) 'GitHub run belongs to a different repository or fork.'
    Require ((Value $Run 'head_sha') -ceq $ExpectedCommit) 'GitHub run head differs from the exact candidate.'
    Require ((Value $Run 'event') -ceq 'workflow_dispatch') 'Expansion qualification requires an actual manual workflow_dispatch run.'
    Require ((Value $Run 'status') -ceq 'completed' -and (Value $Run 'conclusion') -ceq 'success') 'GitHub run must have completed successfully.'
    $workflowPath = ".github/workflows/$WorkflowFile"
    $workflowId = Value $Workflow 'id'
    $runWorkflowId = Value $Run 'workflow_id'
    Assert-CoreEvidenceInteger $workflowId 'GitHub workflow ID'
    Assert-CoreEvidenceInteger $runWorkflowId 'GitHub run workflow ID'
    Require ($workflowId -eq $runWorkflowId -and (Value $Workflow 'path') -ceq $workflowPath -and
        (Value $Run 'path') -ceq $workflowPath) 'GitHub workflow ID or path differs from the role workflow.'
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

function Get-ExpansionRoleVerifier
{
    # The reviewed archived-evidence verifier for each implemented family role. Any role
    # without one fails closed; readiness never accepts evidence it cannot re-verify.
    param([Parameter(Mandatory)][string] $Family,
        [Parameter(Mandatory)][string] $Role)
    Assert-ExpansionCondition ($Family -cin $script:ExpansionFamilies -and $Role -cin $script:ExpansionRoles) (
        "Unknown expansion family role '$Family' $Role.")
    $verifier = switch -CaseSensitive ("$Family/$Role")
    {
        'Jobs/capacity' { @{ Script = 'verify-jobs-release-capacity.ps1'; EvidenceParameter = 'EvidenceRoot'
                Arguments = [ordered]@{ Mode = 'Verify' }; ProducerRunParameter = 'ProducerRunId' } }
        'Jobs/failover' { @{ Script = 'verify-jobs-release-failover.ps1'; EvidenceParameter = 'EvidenceRoot'
                Arguments = [ordered]@{ Mode = 'Verify' }; ProducerRunParameter = $null } }
        'Jobs/upgrade' { @{ Script = 'verify-jobs-release-upgrade.ps1'; EvidenceParameter = 'EvidenceRoot'
                Arguments = [ordered]@{ Mode = 'Verify' }; ProducerRunParameter = $null } }
        'Search/capacity' { @{ Script = 'verify-search-capacity.ps1'; EvidenceParameter = 'EvidenceDirectory'
                Arguments = [ordered]@{}; ProducerRunParameter = $null } }
        'Edge/capacity' { @{ Script = 'verify-edge-capacity.ps1'; EvidenceParameter = 'EvidenceDirectory'
                Arguments = [ordered]@{}; ProducerRunParameter = $null } }
        { $_ -cin @('Documents/capacity', 'Projections/capacity', 'Workflows/capacity') } {
            @{ Script = 'verify-ecosystem-performance.ps1'; EvidenceParameter = 'EvidenceRoot'
                Arguments = [ordered]@{ Mode = 'Verify'; Product = $Family }; ProducerRunParameter = 'ProducerRunId' } }
    }
    Assert-ExpansionCondition ($null -ne $verifier) (
        "No reviewed archived-evidence verifier exists for the '$Family' $Role role; readiness fails closed.")
    return [pscustomobject][ordered]@{
        Script = [string]$verifier.Script
        EvidenceParameter = [string]$verifier.EvidenceParameter
        Arguments = $verifier.Arguments
        ProducerRunParameter = $verifier.ProducerRunParameter
    }
}

function New-ExpansionReadinessRecord
{
    # Builds the schema-1 readiness.json that verify-expansion-readiness-evidence.ps1 checks:
    # each role artifact bound by exact name, run ID, attempt and GitHub SHA-256 digest.
    param([Parameter(Mandatory)][object] $Contract,
        [Parameter(Mandatory)][string] $Tag,
        [Parameter(Mandatory)][string] $Version,
        [Parameter(Mandatory)][long] $ReadinessRunId,
        [Parameter(Mandatory)][int] $ReadinessRunAttempt,
        [Parameter(Mandatory)][object[]] $Bindings)
    Assert-CoreEvidenceInteger $ReadinessRunId 'Readiness run ID'
    Assert-CoreEvidenceInteger $ReadinessRunAttempt 'Readiness run attempt'
    Assert-ExpansionCondition ($Bindings.Count -eq $Contract.Roles.Count) (
        "Readiness must bind exactly the three '$($Contract.Family)' qualification roles.")
    $entries = foreach ($role in $Contract.Roles)
    {
        $candidates = @($Bindings | Where-Object { [string]$_.Role -ceq $role.Role })
        Assert-ExpansionCondition ($candidates.Count -eq 1) "Readiness is missing a unique '$($role.Role)' binding."
        $binding = $candidates[0]
        Assert-CoreEvidenceInteger $binding.RunId "$($role.Role) run ID"
        Assert-CoreEvidenceInteger $binding.RunAttempt "$($role.Role) run attempt"
        Assert-ExpansionCondition ([string]$binding.WorkflowFile -ceq $role.WorkflowFile -and
            [string]$binding.ArtifactName -ceq $role.ArtifactName -and
            [string]$binding.ArtifactSha256 -cmatch '^[0-9a-f]{64}$') (
            "Readiness '$($role.Role)' binding does not name the policy workflow, exact artifact and SHA-256 digest.")
        [ordered]@{
            role = $role.Role
            workflowFile = $role.WorkflowFile
            runId = [long]$binding.RunId
            runAttempt = [int]$binding.RunAttempt
            artifactName = $role.ArtifactName
            artifactDigest = "sha256:$($binding.ArtifactSha256)"
        }
    }
    return [ordered]@{
        schemaVersion = 1
        family = $Contract.Family
        candidateCommit = $Contract.Commit
        tag = $Tag
        version = $Version
        readinessRun = [ordered]@{ id = $ReadinessRunId; attempt = $ReadinessRunAttempt }
        qualificationEvidence = @($entries)
    }
}

Export-ModuleMember -Function Get-ExpansionReadinessContract, Get-ExpansionReleaseIdentity,
    ConvertTo-ExpansionRunSummary, Get-ExpansionWorkflowArtifactName, Select-ExpansionQualificationRun, ConvertTo-ExpansionGithubRunRecord,
    Get-ExpansionRoleVerifier, New-ExpansionReadinessRecord
