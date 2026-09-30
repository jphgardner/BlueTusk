Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-CoreEvidenceJson
{
    param([Parameter(Mandatory)][string] $Path, [switch] $Array)
    $text = Get-Content -LiteralPath $Path -Raw
    $document = [Text.Json.JsonDocument]::Parse($text)
    try
    {
        function Assert-UniqueJsonNames([Text.Json.JsonElement] $Element)
        {
            if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object)
            {
                $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                foreach ($property in $Element.EnumerateObject())
                {
                    if (-not $names.Add($property.Name))
                    { throw "Evidence JSON has duplicate or case-ambiguous property '$($property.Name)'." }
                    Assert-UniqueJsonNames $property.Value
                }
            }
            elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array)
            {
                foreach ($item in $Element.EnumerateArray()) { Assert-UniqueJsonNames $item }
            }
        }
        Assert-UniqueJsonNames $document.RootElement
        $expectedKind = if ($Array) { [Text.Json.JsonValueKind]::Array } else { [Text.Json.JsonValueKind]::Object }
        if ($document.RootElement.ValueKind -ne $expectedKind)
        { throw "Evidence JSON '$Path' must contain a $expectedKind root." }
    }
    finally { $document.Dispose() }
    return ,($text | ConvertFrom-Json -Depth 64 -DateKind String)
}

function Assert-CoreEvidenceProperties
{
    param([Parameter(Mandatory)][object] $Value, [Parameter(Mandatory)][string[]] $Names,
        [Parameter(Mandatory)][string] $Context)
    if ($Value -isnot [pscustomobject]) { throw "$Context must be a JSON object." }
    $actual = @($Value.PSObject.Properties.Name)
    if ($actual.Count -ne $Names.Count -or
        @(Compare-Object ($actual | Sort-Object -CaseSensitive) ($Names | Sort-Object -CaseSensitive) -CaseSensitive).Count -ne 0)
    { throw "$Context schema mismatch; expected exactly: $($Names -join ', ')." }
}

function Assert-CoreEvidenceInteger
{
    param([AllowNull()][object] $Value, [string] $Context)
    if ($Value -isnot [long] -and $Value -isnot [int])
    { throw "$Context must be a positive JSON integer, not text, boolean or fractional data." }
    if ($Value -le 0) { throw "$Context must be a positive JSON integer." }
}

function Resolve-CoreEvidenceFile
{
    param([Parameter(Mandatory)][string] $Root, [Parameter(Mandatory)][string] $RelativePath)
    if ([IO.Path]::IsPathRooted($RelativePath) -or $RelativePath.Contains('\') -or
        $RelativePath.Contains(':') -or $RelativePath.Contains([char]0) -or
        @($RelativePath.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0)
    { throw 'Evidence paths must be normalized, root-contained relative paths.' }
    $resolved = (Resolve-Path -LiteralPath $Root).Path
    $volume = [IO.Path]::GetPathRoot($resolved)
    $ancestor = $volume
    foreach ($segment in [IO.Path]::GetRelativePath($volume, $resolved).Replace('\', '/').Split('/'))
    {
        if ($segment -eq '.') { continue }
        $ancestor = Join-Path $ancestor $segment
        if (((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
        { throw 'Evidence root ancestry must not traverse a symbolic link or junction.' }
    }
    $item = Get-Item -LiteralPath $resolved -Force
    if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
    { throw 'Evidence root must be a directory, not a symbolic link or junction.' }
    foreach ($segment in $RelativePath.Split('/'))
    {
        $resolved = Join-Path $resolved $segment
        $item = Get-Item -LiteralPath $resolved -Force -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
        { throw 'Evidence paths must not traverse a symbolic link or junction.' }
    }
    if ($item.PSIsContainer -or $item.Length -le 0)
    { throw "Evidence '$RelativePath' must be a nonempty file." }
    return $item.FullName
}

function Get-CoreCandidateContract
{
    param([string] $ConfigurationPath = (Join-Path $PSScriptRoot 'v1.2-candidate-readiness.json'))
    $contract = Read-CoreEvidenceJson $ConfigurationPath
    $expectedRoles = @('websiteMetrics', 'packageManifest', 'packageProvenance',
        'performanceManifest', 'regressionManifest', 'compatibilityManifest',
        'streamsReport', 'streamsProvenance', 'syncReport', 'syncProvenance',
        'liveControlPlaneReport', 'liveControlPlaneProvenance', 'syncConnectorReport', 'disturbanceReport')
    $workflows = @('build.yml', 'security.yml', 'fuzzing.yml', 'core-performance-evidence.yml',
        'streams-release-endurance.yml', 'sync-release-endurance.yml', 'live-control-plane-release-endurance.yml')
    $localProducers = @($workflows | Select-Object -Skip 3)
    if ($contract.schemaVersion -ne 1 -or $contract.candidateEvidenceSchemaVersion -ne 4 -or
        $contract.releaseVersion -isnot [string] -or $contract.releaseVersion -cne '1.2.0' -or
        $contract.scope -isnot [string] -or $contract.scope -cne 'Core' -or
        $contract.publicationEnabled -isnot [bool] -or $contract.publicationEnabled -ne $false -or
        @($contract.requiredArtifactRoles).Count -ne $expectedRoles.Count -or
        @(Compare-Object $expectedRoles @($contract.requiredArtifactRoles) -CaseSensitive).Count -ne 0 -or
        @($contract.requiredWorkflows).Count -ne $workflows.Count -or
        @(Compare-Object $workflows @($contract.requiredWorkflows) -CaseSensitive).Count -ne 0)
    { throw 'Core candidate contract identity, exact role/workflow coverage or disabled-publication boundary changed.' }
    if (($contract.localCandidateEvidenceSchemaVersion -isnot [int] -and $contract.localCandidateEvidenceSchemaVersion -isnot [long]) -or
        $contract.localCandidateEvidenceSchemaVersion -ne 5 -or
        @($contract.localEligibleProducers).Count -ne $localProducers.Count -or
        @(Compare-Object $localProducers @($contract.localEligibleProducers) -CaseSensitive).Count -ne 0)
    { throw 'Core local execution contract must retain schema 5 and exactly the four eligible producers.' }
    Assert-CoreEvidenceProperties $contract.artifactBindings $expectedRoles 'Artifact binding contract'
    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($role in $expectedRoles)
    {
        $binding = $contract.artifactBindings.PSObject.Properties[$role].Value
        Assert-CoreEvidenceProperties $binding @('path', 'workflowFile', 'commitProperty') "$role contract"
        if ($binding.path -isnot [string] -or -not $paths.Add($binding.path) -or
            $binding.path -notmatch '^[a-z][a-z0-9-]*(?:/[a-z][a-z0-9-]*)*/[a-z][a-z0-9-]*\.json$' -or
            $binding.commitProperty -isnot [string] -or
            $binding.commitProperty -cne $(if ($role -eq 'disturbanceReport') { 'candidateCommit' } else { 'sourceCommit' }))
        { throw "Invalid path or commit binding contract for '$role'." }
        $expectedWorkflow = switch -Regex ($role)
        {
            '^(websiteMetrics|packageManifest|packageProvenance|regressionManifest|compatibilityManifest)$' { 'build.yml'; break }
            '^performanceManifest$' { 'core-performance-evidence.yml'; break }
            '^streams' { 'streams-release-endurance.yml'; break }
            '^sync' { 'sync-release-endurance.yml'; break }
            '^liveControlPlane' { 'live-control-plane-release-endurance.yml'; break }
            '^disturbanceReport$' { $null; break }
        }
        if (($null -ne $expectedWorkflow -and $binding.workflowFile -isnot [string]) -or
            $binding.workflowFile -cne $expectedWorkflow)
        { throw "Producer workflow contract for '$role' changed." }
    }
    & (Join-Path $PSScriptRoot 'verify-core-endurance-contract.ps1') `
        -ConfigurationPath $ConfigurationPath | Out-Null
    & (Join-Path $PSScriptRoot 'verify-release-track.ps1') | Out-Null
    return $contract
}

function Get-CoreCandidateBindingReport
{
    param([Parameter(Mandatory)][string] $EvidencePath,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
        [Parameter(Mandatory)][DateTimeOffset] $CandidateCommitUtc,
        [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string] $ExpectedRepository = 'jphgardner/BlueTusk',
        [string] $ConfigurationPath = (Join-Path $PSScriptRoot 'v1.2-candidate-readiness.json'))
    $EvidenceRoot = Split-Path (Resolve-Path -LiteralPath $EvidencePath).Path -Parent
    $EvidencePath = Resolve-CoreEvidenceFile $EvidenceRoot ([IO.Path]::GetFileName($EvidencePath))
    $envelopeHash = (Get-FileHash -LiteralPath $EvidencePath -Algorithm SHA256).Hash
    $Evidence = Read-CoreEvidenceJson $EvidencePath
    $contract = Get-CoreCandidateContract $ConfigurationPath
    $localExecution = $Evidence.schemaVersion -ceq 5
    $executionProperty = if ($localExecution) { 'producerRuns' } else { 'workflowRuns' }
    Assert-CoreEvidenceProperties $Evidence @('schemaVersion', 'candidateCommit', 'scope',
        'releaseVersion', $executionProperty, 'artifacts', 'approvals') 'Core candidate envelope'
    if (($Evidence.schemaVersion -isnot [int] -and $Evidence.schemaVersion -isnot [long]) -or
        $Evidence.schemaVersion -notin @(4, 5) -or $Evidence.scope -isnot [string] -or $Evidence.scope -cne 'Core' -or
        $Evidence.releaseVersion -isnot [string] -or $Evidence.releaseVersion -cne '1.2.0' -or
        $Evidence.candidateCommit -isnot [string] -or $Evidence.candidateCommit -cne $ExpectedCommit)
    { throw 'Core candidate envelope schema, scope, version or exact commit is invalid.' }
    if ($Evidence.PSObject.Properties[$executionProperty].Value -isnot [array] -or
        $Evidence.artifacts -isnot [array] -or $Evidence.approvals -isnot [array])
    { throw 'Workflow, artifact and approval collections must be JSON arrays.' }
    $runs = @{}
    if ($localExecution)
    {
        Import-Module (Join-Path $PSScriptRoot 'core-execution-evidence.psm1')
        $workflowReport = Get-CoreExecutionBindingReport -Records $Evidence.producerRuns -EvidenceRoot $EvidenceRoot `
            -ExpectedCommit $ExpectedCommit -CandidateCommitUtc $CandidateCommitUtc -ExpectedRepository $ExpectedRepository
        foreach ($producer in $contract.requiredWorkflows)
        { $runs[$producer] = $workflowReport.Bindings[$producer].Identifier }
    }
    else
    {
        foreach ($run in $Evidence.workflowRuns)
        {
            Assert-CoreEvidenceInteger $run.runId 'Workflow run ID'
            Assert-CoreEvidenceInteger $run.runAttempt 'Workflow run attempt'
        }
        $workflowReport = & (Join-Path $PSScriptRoot 'verify-v1-workflow-evidence.ps1') `
            -EvidencePath $EvidencePath -ExpectedCommit $ExpectedCommit -CandidateCommitUtc $CandidateCommitUtc `
            -ConfigurationPath $ConfigurationPath -ExpectedRepository $ExpectedRepository
        foreach ($run in $Evidence.workflowRuns) { $runs[$run.workflowFile] = $run.runId }
    }
    if ($Evidence.artifacts.Count -ne $contract.requiredArtifactRoles.Count)
    { throw 'Core candidate must bind exactly all fourteen required artifact roles.' }
    $files = @{}
    $payloads = @{}
    foreach ($role in $contract.requiredArtifactRoles)
    {
        $matches = @($Evidence.artifacts | Where-Object { $_.role -ceq $role })
        if ($matches.Count -ne 1) { throw "Core candidate role '$role' must occur exactly once." }
        $record = $matches[0]
        $producerProperty = if ($localExecution) { 'producerFile' } else { 'workflowFile' }
        $identityProperty = if ($localExecution) { 'executionId' } else { 'runId' }
        Assert-CoreEvidenceProperties $record @('role', 'path', 'sha256', 'bytes', $producerProperty, $identityProperty) "$role record"
        $binding = $contract.artifactBindings.PSObject.Properties[$role].Value
        $producerFile = $record.PSObject.Properties[$producerProperty].Value
        $executionId = $record.PSObject.Properties[$identityProperty].Value
        if ($record.role -isnot [string] -or $record.path -isnot [string] -or
            ($null -ne $binding.workflowFile -and $producerFile -isnot [string]) -or
            $record.path -cne $binding.path -or $producerFile -cne $binding.workflowFile)
        { throw "Role '$role' must use its canonical path and producer workflow." }
        if ($null -eq $binding.workflowFile)
        {
            if ($null -ne $executionId) { throw 'Protected disturbance handoff must not invent an execution identity.' }
        }
        else
        {
            if ($localExecution)
            {
                if ($executionId -isnot [string] -or $executionId -cne $runs[$binding.workflowFile])
                { throw "Role '$role' is bound to a different producer execution identity." }
                $localArtifacts = $workflowReport.Bindings[$binding.workflowFile].Artifacts
                if ($null -ne $localArtifacts)
                {
                    $localArtifact = $localArtifacts[$role]
                    if ($record.path -cne $localArtifact.path -or $record.sha256 -cne $localArtifact.sha256 -or
                        $record.bytes -ne $localArtifact.bytes)
                    { throw "Role '$role' differs from its retained local producer artifact binding." }
                }
            }
            else
            {
                Assert-CoreEvidenceInteger $executionId "$role producer run ID"
                if ($executionId -ne $runs[$binding.workflowFile])
                { throw "Role '$role' is bound to a different producer run ID." }
            }
        }
        Assert-CoreEvidenceInteger $record.bytes "$role bytes"
        if ($record.sha256 -isnot [string] -or $record.sha256 -cnotmatch '^[0-9a-f]{64}$')
        { throw "Role '$role' requires a lowercase SHA-256 digest." }
        $path = Resolve-CoreEvidenceFile $EvidenceRoot $record.path
        if ((Get-Item -LiteralPath $path).Length -ne $record.bytes -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $record.sha256)
        { throw "Role '$role' failed its file hash or byte-count binding." }
        $payload = Read-CoreEvidenceJson $path
        $property = $payload.PSObject.Properties[$binding.commitProperty]
        if ($null -eq $property -or $property.Value -isnot [string] -or $property.Value -cne $ExpectedCommit)
        { throw "Role '$role' payload is not bound to the exact candidate commit." }
        $files[$role] = $path
        $payloads[$role] = $payload
    }
    if ($payloads.packageManifest.releaseTrack -isnot [string] -or $payloads.packageManifest.releaseTrack -cne 'Core' -or
        $payloads.packageManifest.releaseVersion -isnot [string] -or $payloads.packageManifest.releaseVersion -cne '1.2.0' -or
        $payloads.performanceManifest.scope -isnot [string] -or $payloads.performanceManifest.scope -cne 'Core' -or
        $payloads.performanceManifest.release -isnot [string] -or $payloads.performanceManifest.release -cne '1.2.0')
    { throw 'Package or performance payload scope/version cannot substitute a preview or older release.' }
    $approvalContract = Read-CoreEvidenceJson (Join-Path $PSScriptRoot 'v1-approval-evidence-contract.json')
    $ids = @($approvalContract.gates.id)
    if ($ids.Count -ne 10 -or $Evidence.approvals.Count -ne 10)
    { throw 'Core candidate must bind exactly ten canonical approval records.' }
    foreach ($id in $ids)
    {
        $matches = @($Evidence.approvals | Where-Object { $_.id -ceq $id })
        if ($matches.Count -ne 1) { throw "Approval '$id' must occur exactly once." }
        $record = $matches[0]
        Assert-CoreEvidenceProperties $record @('id', 'path', 'sha256', 'bytes') "$id approval record"
        if ($record.id -isnot [string] -or $record.path -isnot [string] -or
            $record.path -cne "approvals/$id.json" -or $record.sha256 -isnot [string] -or
            $record.sha256 -cnotmatch '^[0-9a-f]{64}$')
        { throw "Approval '$id' must use its canonical path and lowercase digest." }
        Assert-CoreEvidenceInteger $record.bytes "$id approval bytes"
        $path = Resolve-CoreEvidenceFile $EvidenceRoot $record.path
        if ((Get-Item -LiteralPath $path).Length -ne $record.bytes -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $record.sha256)
        { throw "Approval '$id' failed its file hash or byte-count binding." }
        $null = Read-CoreEvidenceJson $path
    }
    $metricsRecord = @($Evidence.artifacts | Where-Object role -ceq 'websiteMetrics')[0]
    & (Join-Path $PSScriptRoot 'verify-v1-approval-evidence-set.ps1') `
        -EvidenceDirectory (Join-Path $EvidenceRoot 'approvals') -ExpectedCommit $ExpectedCommit `
        -ExpectedWebsiteProductionMetricsSha256 $metricsRecord.sha256 `
        -NotBeforeUtc $workflowReport.LatestCompletedUtc -ReleaseTrack Core | Out-Null
    if ((Get-FileHash -LiteralPath $EvidencePath -Algorithm SHA256).Hash -cne $envelopeHash)
    { throw 'Candidate envelope changed during verification.' }
    return [pscustomobject]@{
        Stage = 'CoreEvidenceBindings'; CandidateCommit = $ExpectedCommit; ReleaseVersion = '1.2.0'
        ProducerCount = $workflowReport.RunCount
        WorkflowCount = $(if ($localExecution) { $workflowReport.GitHubRunCount } else { $workflowReport.RunCount })
        ArtifactCount = $files.Count; ApprovalCount = $ids.Count
        ApprovalPayloadsValidated = $true; AllPayloadsValidated = $false; RemoteIdentityValidated = $false
        ExecutionAuthenticityValidated = $false
        ReleaseApproved = $false; LatestProducerCompletedUtc = $workflowReport.LatestCompletedUtc
        GitHubRunCount = $(if ($localExecution) { $workflowReport.GitHubRunCount } else { $workflowReport.RunCount })
        LocalRunCount = $(if ($localExecution) { $workflowReport.LocalRunCount } else { 0 })
    }
}

Export-ModuleMember -Function Read-CoreEvidenceJson, Resolve-CoreEvidenceFile,
    Assert-CoreEvidenceProperties, Assert-CoreEvidenceInteger,
    Get-CoreCandidateContract, Get-CoreCandidateBindingReport
